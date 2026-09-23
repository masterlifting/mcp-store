module WorkflowMcp

// Native MCP transport for the Workflow producer. The host owns only the
// JSON-RPC/DTO boundary; all task semantics, CAS, persistence, and path safety
// remain in Workflow.

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Common.CE
open Workflow
open WorkflowAdapter

[<Literal>]
let ProtocolVersion = "2024-11-05"

[<Literal>]
let AcceptedClientProtocolVersion = "2025-11-25"

let private invalid message : Result<'a, string> = Error message

let private objectProperties label (element: JsonElement) allowed required =
    if element.ValueKind <> JsonValueKind.Object then
        invalid $"{label} must be an object"
    else
        let properties = element.EnumerateObject() |> Seq.toList
        let names = properties |> List.map _.Name
        let duplicates = names |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1)

        match duplicates with
        | Some (name, _) -> invalid $"{label} contains duplicate property '{name}'"
        | None ->
            match names |> List.tryFind (fun name -> not (List.contains name allowed)) with
            | Some name -> invalid $"{label} contains unknown property '{name}'"
            | None ->
                match required |> List.tryFind (fun name -> not (List.contains name names)) with
                | Some name -> invalid $"{label} is missing property '{name}'"
                | None -> Ok element

let private property name (element: JsonElement) =
    match element.EnumerateObject() |> Seq.tryFind (fun item -> item.Name = name) with
    | Some item -> Some item.Value
    | None -> None

let private requiredString label name element =
    match property name element with
    | None -> invalid $"{label} is missing property '{name}'"
    | Some value when value.ValueKind <> JsonValueKind.String -> invalid $"property '{name}' must be a string"
    | Some value ->
        let text = value.GetString()
        if isNull text then invalid $"property '{name}' must be a string" else Ok text

let private optionalString label name element =
    match property name element with
    | None -> Ok None
    | Some value when value.ValueKind <> JsonValueKind.String -> invalid $"property '{name}' must be a string"
    | Some value ->
        let text = value.GetString()
        if isNull text then invalid $"property '{name}' must be a string" else Ok(Some text)

let private requiredInteger label name element =
    match property name element with
    | None -> invalid $"{label} is missing property '{name}'"
    | Some value when value.ValueKind <> JsonValueKind.Number -> invalid $"property '{name}' must be an integer"
    | Some value ->
        match value.TryGetInt32() with
        | true, number -> Ok number
        | false, _ -> invalid $"property '{name}' must be a 32-bit integer"

let private optionalInteger name element defaultValue =
    match property name element with
    | None -> Ok defaultValue
    | Some value when value.ValueKind <> JsonValueKind.Number -> invalid $"property '{name}' must be an integer"
    | Some value ->
        match value.TryGetInt32() with
        | true, number -> Ok number
        | false, _ -> invalid $"property '{name}' must be a 32-bit integer"

let private optionalBoolean name element defaultValue =
    match property name element with
    | None -> Ok defaultValue
    | Some value when value.ValueKind = JsonValueKind.True -> Ok true
    | Some value when value.ValueKind = JsonValueKind.False -> Ok false
    | Some _ -> invalid $"property '{name}' must be a boolean"

let private requiredArray label name element =
    match property name element with
    | None -> invalid $"{label} is missing property '{name}'"
    | Some value when value.ValueKind <> JsonValueKind.Array -> invalid $"property '{name}' must be an array"
    | Some value -> Ok(value.EnumerateArray() |> Seq.toList)

let private optionalArray name element =
    match property name element with
    | None -> Ok []
    | Some value when value.ValueKind <> JsonValueKind.Array -> invalid $"property '{name}' must be an array"
    | Some value -> Ok(value.EnumerateArray() |> Seq.toList)

let private pathComparison =
    if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

let private normalizePath (path: string) =
    let fullPath = Path.GetFullPath path
    let root = Path.GetPathRoot fullPath

    if fullPath.Length > root.Length then
        fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    else
        fullPath

let private pathIsWithin (root: string) (candidate: string) =
    let prefix =
        if root.EndsWith(string Path.DirectorySeparatorChar, StringComparison.Ordinal)
           || root.EndsWith(string Path.AltDirectorySeparatorChar, StringComparison.Ordinal) then
            root
        else
            root + string Path.DirectorySeparatorChar

    candidate.Equals(root, pathComparison)
    || candidate.StartsWith(prefix, pathComparison)

let private isReparsePoint (path: string) =
    (File.Exists path || Directory.Exists path)
    && (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint

// runtime.json declares workingDirectory=repository, so the process directory
// is the trusted workspace anchor; projectRoot remains model-controlled input.
let private trustedWorkspaceRoot = normalizePath Environment.CurrentDirectory

let private validateProjectRoot (root: string) : Result<string, string> =
    if String.IsNullOrWhiteSpace root then
        invalid "projectRoot must be a non-empty path"
    else
        try
            let candidate = normalizePath root

            if not (Directory.Exists candidate) then
                invalid $"project root does not exist: {candidate}"
            elif not (pathIsWithin trustedWorkspaceRoot candidate) then
                invalid $"project root is outside the trusted workspace: {candidate}"
            elif isReparsePoint trustedWorkspaceRoot then
                invalid "trusted workspace must not be a reparse point"
            else
                let mutable directory = DirectoryInfo candidate
                let mutable reparse = None

                while not (isNull directory)
                      && not ((normalizePath directory.FullName).Equals(trustedWorkspaceRoot, pathComparison)) do
                    if directory.Exists && directory.Attributes.HasFlag FileAttributes.ReparsePoint then
                        reparse <- Some directory.FullName

                    directory <- directory.Parent

                if isNull directory then
                    invalid $"project root is outside the trusted workspace: {candidate}"
                elif reparse.IsSome then
                    invalid $"project root must not traverse a reparse point: {reparse.Value}"
                else
                    Ok candidate
        with error ->
            invalid $"projectRoot is not a valid path: {error.Message}"

let private strings label (values: JsonElement list) =
    values
    |> List.map (fun (value: JsonElement) ->
        if value.ValueKind <> JsonValueKind.String then
            invalid $"{label} must contain only strings"
        else
            let text = value.GetString()
            if isNull text then invalid $"{label} must contain only strings" else Ok text)
    |> List.fold
        (fun state item ->
            result {
                let! values = state
                let! value = item
                return values @ [ value ]
            })
        (Ok [])

let private collectResults values =
    values
    |> List.fold
        (fun state item ->
            result {
                let! values = state
                let! value = item
                return values @ [ value ]
            })
        (Ok [])

let private parseKind value =
    match value with
    | "execution" -> Ok Execution
    | "research" -> Ok Research
    | _ -> invalid "kind must be 'execution' or 'research'"

let private parseCreateAcceptance element =
    result {
        let! item = objectProperties "acceptance criterion" element [ "id"; "text" ] [ "id"; "text" ]
        let! id = requiredString "acceptance criterion" "id" item
        let! text = requiredString "acceptance criterion" "text" item
        return id, text
    }

let rec private parseWorkSpec element =
    result {
        let! item = objectProperties "work item" element [ "id"; "title"; "children"; "dependsOn" ] [ "id"; "title"; "children" ]
        let! id = requiredString "work item" "id" item
        let! title = requiredString "work item" "title" item
        let! dependenciesJson = optionalArray "dependsOn" item
        let! dependsOn = strings "dependsOn" dependenciesJson
        let! childrenJson = requiredArray "work item" "children" item
        let! children = childrenJson |> List.map parseWorkSpec |> collectResults
        return { Id = id; Title = title; DependsOn = dependsOn; Children = children }
    }

let private parseCreate element =
    result {
        let! args = objectProperties "task_create arguments" element [ "projectRoot"; "taskId"; "title"; "kind"; "profile"; "acceptanceCriteria"; "workItems" ] [ "projectRoot"; "taskId"; "title"; "kind"; "acceptanceCriteria"; "workItems" ]
        let! rootText = requiredString "task_create arguments" "projectRoot" args
        let! root = validateProjectRoot rootText
        let! id = requiredString "task_create arguments" "taskId" args
        let! title = requiredString "task_create arguments" "title" args
        let! kindText = requiredString "task_create arguments" "kind" args
        let! kind = parseKind kindText
        let! profile = optionalString "task_create arguments" "profile" args
        let! acceptanceJson = requiredArray "task_create arguments" "acceptanceCriteria" args
        let! acceptance = acceptanceJson |> List.map parseCreateAcceptance |> collectResults
        let! workJson = requiredArray "task_create arguments" "workItems" args
        let! work = workJson |> List.map parseWorkSpec |> collectResults
        return CreateTask { Root = root; ProfileId = profile; Request = { Id = id; Title = title; Kind = kind; AcceptanceCriteria = acceptance; WorkItems = work } }
    }

let private parseDecisionRef name element =
    optionalString "command" name element |> Result.map (Option.map DecisionRef)

let private commandObject commandType element allowed required =
    objectProperties "task_apply command" element ("type" :: allowed) ("type" :: required)
    |> Result.bind (fun command ->
        requiredString "task_apply command" "type" command
        |> Result.bind (fun actual -> if actual = commandType then Ok command else invalid $"task_apply command type must be '{commandType}'"))

let private parseContractPatch element =
    result {
        let! typeObject =
            objectProperties
                "contract patch"
                element
                [ "type"; "id"; "text"; "target"; "checkpoint"; "kind"; "minimumCount"; "producerRole"; "independent"; "applicability"; "waiver" ]
                [ "type" ]
        let! patchType = requiredString "contract patch" "type" typeObject
        let! patch =
            match patchType with
            | "addAcceptanceCriterion"
            | "updateAcceptanceCriterion" ->
                objectProperties "contract patch" element [ "type"; "id"; "text" ] [ "type"; "id"; "text" ]
            | "addGuard" ->
                objectProperties
                    "contract patch"
                    element
                    [ "type"; "id"; "target"; "checkpoint"; "kind"; "minimumCount"; "producerRole"; "independent"; "applicability"; "waiver" ]
                    [ "type"; "id"; "target"; "checkpoint"; "kind" ]
            | "removeGuard"
            | "removeAcceptanceCriterion" ->
                objectProperties "contract patch" element [ "type"; "id" ] [ "type"; "id" ]
            | "setObjective"
            | "setScope"
            | "setNonGoals" ->
                objectProperties "contract patch" element [ "type"; "text" ] [ "type"; "text" ]
            | _ -> invalid $"contract patch type '{patchType}' is not supported"

        match patchType with
        | "addAcceptanceCriterion" ->
            let! id = requiredString "contract patch" "id" patch
            let! text = requiredString "contract patch" "text" patch
            return ContractPatch.AddAcceptanceCriterion { Id = id; Text = text }
        | "addGuard" ->
            let! id = requiredString "contract patch" "id" patch
            let! targetText = requiredString "contract patch" "target" patch
            let! checkpointText = requiredString "contract patch" "checkpoint" patch
            let! kindText = requiredString "contract patch" "kind" patch
            let! target = parseGuardTarget targetText |> Result.mapError renderError
            let! checkpoint = parseGuardCheckpoint checkpointText |> Result.mapError renderError
            let! kind = parseEvidenceKind kindText |> Result.mapError renderError
            let! minimumCount = optionalInteger "minimumCount" patch 1
            let! producerRole = optionalString "contract patch" "producerRole" patch
            let! independent = optionalBoolean "independent" patch false
            let! applicabilityText = optionalString "contract patch" "applicability" patch
            let! waiverText = optionalString "contract patch" "waiver" patch
            let! applicability = applicabilityText |> Option.map parseApplicability |> Option.defaultValue (Ok Always) |> Result.mapError renderError
            let! waiver = waiverText |> Option.map parseWaiver |> Option.defaultValue (Ok NotWaivable) |> Result.mapError renderError
            return ContractPatch.AddGuard { Id = id; Target = target; Checkpoint = checkpoint; Requirement = EvidenceRequired { Kind = kind; MinimumCount = minimumCount; ProducerRole = producerRole; RequireIndependentProducer = independent }; Applicability = applicability; Waiver = waiver }
        | "removeGuard" ->
            let! id = requiredString "contract patch" "id" patch
            return ContractPatch.RemoveGuard id
        | "updateAcceptanceCriterion" ->
            let! id = requiredString "contract patch" "id" patch
            let! text = requiredString "contract patch" "text" patch
            return ContractPatch.UpdateAcceptanceCriterion(id, text)
        | "removeAcceptanceCriterion" ->
            let! id = requiredString "contract patch" "id" patch
            return ContractPatch.RemoveAcceptanceCriterion id
        | "setObjective" ->
            let! text = requiredString "contract patch" "text" patch
            return ContractPatch.SetObjective text
        | "setScope" ->
            let! text = requiredString "contract patch" "text" patch
            return ContractPatch.SetScope text
        | "setNonGoals" ->
            let! text = requiredString "contract patch" "text" patch
            return ContractPatch.SetNonGoals text
        | _ -> return! invalid $"contract patch type '{patchType}' is not supported"
    }

let private parseCommand element =
    result {
        let! rawType = requiredString "task_apply command" "type" element
        match rawType with
        | "start" ->
            let! command = commandObject "start" element [ "workItemId" ] [ "workItemId" ]
            let! id = requiredString "task_apply command" "workItemId" command
            return StartWorkItem id
        | "complete-work" ->
            let! command = commandObject "complete-work" element [ "workItemId"; "result"; "evidenceRefs" ] [ "workItemId"; "result" ]
            let! id = requiredString "task_apply command" "workItemId" command
            let! resultText = requiredString "task_apply command" "result" command
            let! evidenceJson = optionalArray "evidenceRefs" command
            let! evidenceRefs = strings "evidenceRefs" evidenceJson
            return CompleteWorkItem(id, { Result = resultText; EvidenceRefs = evidenceRefs })
        | "wait" ->
            let! command = commandObject "wait" element [ "workItemId"; "resumeCondition" ] [ "workItemId"; "resumeCondition" ]
            let! id = requiredString "task_apply command" "workItemId" command
            let! condition = requiredString "task_apply command" "resumeCondition" command
            return WaitWorkItem(id, ResumeCondition condition)
        | "block" ->
            let! command = commandObject "block" element [ "workItemId"; "blocker" ] [ "workItemId"; "blocker" ]
            let! id = requiredString "task_apply command" "workItemId" command
            let! blocker = requiredString "task_apply command" "blocker" command
            return BlockWorkItem(id, Blocker blocker)
        | "resume" ->
            let! command = commandObject "resume" element [ "workItemId"; "observationRef" ] [ "workItemId" ]
            let! id = requiredString "task_apply command" "workItemId" command
            let! observation = optionalString "task_apply command" "observationRef" command
            return ResumeWorkItem(id, observation |> Option.map ObservationRef)
        | "rebind-owner" ->
            let! command = commandObject "rebind-owner" element [ "workItemId"; "role"; "agentId"; "reason" ] [ "workItemId"; "role"; "reason" ]
            let! id = requiredString "task_apply command" "workItemId" command
            let! role = requiredString "task_apply command" "role" command
            let! agentId = optionalString "task_apply command" "agentId" command
            let! reason = requiredString "task_apply command" "reason" command
            return RebindOwner(id, { Role = role; AgentId = agentId }, reason)
        | "add-evidence" ->
            let! command = commandObject "add-evidence" element [ "id"; "kind"; "source"; "summary"; "subject"; "producerRole"; "producerId"; "reference" ] [ "id"; "kind"; "source"; "summary" ]
            let! id = requiredString "task_apply command" "id" command
            let! kindText = requiredString "task_apply command" "kind" command
            let! kind = parseEvidenceKind kindText |> Result.mapError renderError
            let! source = requiredString "task_apply command" "source" command
            let! summary = requiredString "task_apply command" "summary" command
            let! subject = optionalString "task_apply command" "subject" command
            let! producerRole = optionalString "task_apply command" "producerRole" command
            let! producerId = optionalString "task_apply command" "producerId" command
            let! reference = optionalString "task_apply command" "reference" command
            return AddEvidence { Id = id; Kind = kind; Source = EvidenceSource source; Subject = subject; ProducerRole = producerRole; ProducerId = producerId; Reference = reference; Summary = summary }
        | "supersede-evidence" ->
            let! command = commandObject "supersede-evidence" element [ "evidenceId"; "reason" ] [ "evidenceId"; "reason" ]
            let! id = requiredString "task_apply command" "evidenceId" command
            let! reason = requiredString "task_apply command" "reason" command
            return SupersedeEvidence(id, reason)
        | "verify" ->
            let! command = commandObject "verify" element [ "acceptanceId"; "evidenceRefs" ] [ "acceptanceId"; "evidenceRefs" ]
            let! id = requiredString "task_apply command" "acceptanceId" command
            let! refsJson = requiredArray "task_apply command" "evidenceRefs" command
            let! refs = strings "evidenceRefs" refsJson
            return VerifyAcceptanceCriterion(id, refs)
        | "add-guard" ->
            let! command = commandObject "add-guard" element [ "id"; "target"; "checkpoint"; "kind"; "minimumCount"; "producerRole"; "independent"; "applicability"; "waiver" ] [ "id"; "target"; "checkpoint"; "kind" ]
            let! id = requiredString "task_apply command" "id" command
            let! targetText = requiredString "task_apply command" "target" command
            let! checkpointText = requiredString "task_apply command" "checkpoint" command
            let! kindText = requiredString "task_apply command" "kind" command
            let! target = parseGuardTarget targetText |> Result.mapError renderError
            let! checkpoint = parseGuardCheckpoint checkpointText |> Result.mapError renderError
            let! kind = parseEvidenceKind kindText |> Result.mapError renderError
            let! minimumCount = optionalInteger "minimumCount" command 1
            let! producerRole = optionalString "task_apply command" "producerRole" command
            let! independent = optionalBoolean "independent" command false
            let! applicabilityText = optionalString "task_apply command" "applicability" command
            let! waiverText = optionalString "task_apply command" "waiver" command
            let! applicability = applicabilityText |> Option.map parseApplicability |> Option.defaultValue (Ok Always) |> Result.mapError renderError
            let! waiver = waiverText |> Option.map parseWaiver |> Option.defaultValue (Ok NotWaivable) |> Result.mapError renderError
            return AddGuard { Id = id; Target = target; Checkpoint = checkpoint; Requirement = EvidenceRequired { Kind = kind; MinimumCount = minimumCount; ProducerRole = producerRole; RequireIndependentProducer = independent }; Applicability = applicability; Waiver = waiver }
        | "mark-not-applicable" ->
            let! command = commandObject "mark-not-applicable" element [ "guardId"; "decisionRef" ] [ "guardId" ]
            let! id = requiredString "task_apply command" "guardId" command
            let! reference = parseDecisionRef "decisionRef" command
            return MarkGuardNotApplicable(id, reference)
        | "waive-guard" ->
            let! command = commandObject "waive-guard" element [ "guardId"; "decisionRef" ] [ "guardId" ]
            let! id = requiredString "task_apply command" "guardId" command
            let! reference = parseDecisionRef "decisionRef" command
            return WaiveGuard(id, reference)
        | "add-decision" ->
            let! command = commandObject "add-decision" element [ "kind"; "targets"; "rationale" ] [ "kind"; "targets"; "rationale" ]
            let! kindText = requiredString "task_apply command" "kind" command
            let! kind = parseDecisionKind kindText |> Result.mapError renderError
            let! targetsJson = requiredArray "task_apply command" "targets" command
            let! targetsText = strings "targets" targetsJson
            let! targets = targetsText |> List.map parseDecisionTarget |> List.map (Result.mapError renderError) |> collectResults
            let! rationale = requiredString "task_apply command" "rationale" command
            return AddDecision { Kind = kind; Targets = targets; Rationale = rationale }
        | "add-question" ->
            let! command = commandObject "add-question" element [ "id"; "text"; "impact" ] [ "id"; "text"; "impact" ]
            let! id = requiredString "task_apply command" "id" command
            let! text = requiredString "task_apply command" "text" command
            let! impactText = requiredString "task_apply command" "impact" command
            let! impact = parseQuestionImpact impactText |> Result.mapError renderError
            return AddQuestion { Id = id; Text = text; Impact = impact }
        | "resolve-question" ->
            let! command = commandObject "resolve-question" element [ "questionId"; "decisionRef" ] [ "questionId"; "decisionRef" ]
            let! id = requiredString "task_apply command" "questionId" command
            let! reference = requiredString "task_apply command" "decisionRef" command
            return ResolveQuestion(id, DecisionRef reference)
        | "reopen" ->
            let! command = commandObject "reopen" element [ "reason"; "targets"; "decisionRef" ] [ "reason"; "targets" ]
            let! reason = requiredString "task_apply command" "reason" command
            let! targetsJson = requiredArray "task_apply command" "targets" command
            let! targetsText = strings "targets" targetsJson
            let! targets = targetsText |> List.map parseReopenTarget |> List.map (Result.mapError renderError) |> collectResults
            let! decisionRef = parseDecisionRef "decisionRef" command
            return ReopenTask { Reason = reason; DecisionRef = decisionRef; Targets = targets }
        | "apply-contract-patch" ->
            let! command = commandObject "apply-contract-patch" element [ "patch"; "decisionRef" ] [ "patch" ]
            let! patchJson = match property "patch" command with Some value -> Ok value | None -> invalid "task_apply command is missing property 'patch'"
            let! patch = parseContractPatch patchJson
            let! decisionRef = parseDecisionRef "decisionRef" command
            return ApplyContractPatch(patch, decisionRef)
        | "reconcile-contract-drift" ->
            let! command = commandObject "reconcile-contract-drift" element [ "plan" ] [ "plan" ]
            let! planJson = match property "plan" command with Some value -> Ok value | None -> invalid "task_apply command is missing property 'plan'"
            let! typeObject =
                objectProperties
                    "reconciliation plan"
                    planJson
                    [ "type"; "objective"; "scope"; "nonGoals"; "acceptanceCriteria" ]
                    [ "type" ]
            let! planType = requiredString "reconciliation plan" "type" typeObject
            let! planObject =
                match planType with
                | "acceptExternalContract" ->
                    objectProperties "reconciliation plan" planJson [ "type" ] [ "type" ]
                | "restoreCanonicalContract" ->
                    objectProperties
                        "reconciliation plan"
                        planJson
                        [ "type"; "objective"; "scope"; "nonGoals"; "acceptanceCriteria" ]
                        [ "type"; "objective"; "scope"; "nonGoals"; "acceptanceCriteria" ]
                | _ -> invalid $"reconciliation plan type '{planType}' is not supported"
            let! plan =
                if planType = "acceptExternalContract" then
                    Ok ReconciliationPlan.AcceptExternalContract
                elif planType = "restoreCanonicalContract" then
                    result {
                        let! objective = requiredString "reconciliation plan" "objective" planObject
                        let! scope = requiredString "reconciliation plan" "scope" planObject
                        let! nonGoals = requiredString "reconciliation plan" "nonGoals" planObject
                        let! criteriaJson = requiredArray "reconciliation plan" "acceptanceCriteria" planObject
                        let! criteria = criteriaJson |> List.map parseCreateAcceptance |> collectResults
                        return ReconciliationPlan.RestoreCanonicalContract { Objective = objective; Scope = scope; NonGoals = nonGoals; AcceptanceCriteria = criteria }
                    }
                else
                    invalid $"reconciliation plan type '{planType}' is not supported"
            return ReconcileContractDrift plan
        | "reclassify-task" ->
            let! command = commandObject "reclassify-task" element [ "kind"; "profile"; "reason" ] [ "reason" ]
            let! kindText = optionalString "task_apply command" "kind" command
            let! kind =
                match kindText with
                | None -> Ok None
                | Some value -> parseKind value |> Result.map Some
            let! profile = optionalString "task_apply command" "profile" command
            let! reason = requiredString "task_apply command" "reason" command
            return ReclassifyTask { Kind = kind; Profile = profile; Reason = reason }
        | "reconcile-profile-drift" ->
            let! _ = commandObject "reconcile-profile-drift" element [] []
            return ReconcileProfileDrift
        | "complete-task" ->
            let! command = commandObject "complete-task" element [ "state"; "evidenceSummary"; "next" ] [ "state"; "evidenceSummary"; "next" ]
            let! state = requiredString "task_apply command" "state" command
            let! evidenceSummary = requiredString "task_apply command" "evidenceSummary" command
            let! next = requiredString "task_apply command" "next" command
            return CompleteTask { State = state; EvidenceSummary = evidenceSummary; Next = next }
        | _ -> return! invalid $"task_apply command type '{rawType}' is not supported"
    }

let private parseOperation name (arguments: JsonElement) =
    match name with
    | "task_create" -> parseCreate arguments
    | "task_get" ->
        result {
            let! args = objectProperties "task_get arguments" arguments [ "projectRoot"; "taskId" ] [ "projectRoot"; "taskId" ]
            let! rootText = requiredString "task_get arguments" "projectRoot" args
            let! root = validateProjectRoot rootText
            let! id = requiredString "task_get arguments" "taskId" args
            return GetTask { Root = root; TaskId = id }
        }
    | "task_validate" ->
        result {
            let! args = objectProperties "task_validate arguments" arguments [ "projectRoot"; "taskId" ] [ "projectRoot"; "taskId" ]
            let! rootText = requiredString "task_validate arguments" "projectRoot" args
            let! root = validateProjectRoot rootText
            let! id = requiredString "task_validate arguments" "taskId" args
            return ValidateTask { Root = root; TaskId = id }
        }
    | "task_apply" ->
        result {
            let! args = objectProperties "task_apply arguments" arguments [ "projectRoot"; "taskId"; "expectedStateRevision"; "command" ] [ "projectRoot"; "taskId"; "expectedStateRevision"; "command" ]
            let! rootText = requiredString "task_apply arguments" "projectRoot" args
            let! root = validateProjectRoot rootText
            let! id = requiredString "task_apply arguments" "taskId" args
            let! revision = requiredInteger "task_apply arguments" "expectedStateRevision" args
            if revision < 0 then
                return! invalid "property 'expectedStateRevision' must be non-negative"
            let! commandJson =
                match property "command" args with
                | Some value -> Ok value
                | None -> invalid "task_apply arguments is missing property 'command'"
            let! command = parseCommand commandJson
            return ApplyTask { Root = root; TaskId = id; ExpectedStateRevision = revision; Command = command }
        }
    | _ -> invalid $"tool '{name}' is not registered"

let private errorCode error =
    match error with
    | InvalidInput _ -> "INVALID_INPUT"
    | NotFound _ -> "NOT_FOUND"
    | Conflict _ -> "CONFLICT"
    | InvalidTransition _ -> "INVALID_TRANSITION"
    | PersistenceFailure _ -> "PERSISTENCE_FAILURE"

let private node (value: 'T) : JsonNode = JsonValue.Create<'T>(value) :> JsonNode

let private resultEnvelope isError (payload: JsonNode) =
    let content = JsonObject()
    content["type"] <- node "text"
    content["text"] <- node (payload.ToJsonString())
    let contentArray = JsonArray()
    contentArray.Add content
    let envelope = JsonObject()
    envelope["content"] <- contentArray
    envelope["isError"] <- node isError
    envelope["structuredContent"] <- payload
    envelope

let private success (taskJson: string) =
    let payload = JsonObject()
    payload["ok"] <- node true
    payload["task"] <- JsonNode.Parse taskJson
    resultEnvelope false payload

let private failure error =
    let payload = JsonObject()
    let detail = JsonObject()
    detail["code"] <- node (errorCode error)
    detail["message"] <- node (renderError error)
    payload["ok"] <- node false
    payload["error"] <- detail
    resultEnvelope true payload

let private response (id: string) result =
    let response = JsonObject()
    response["jsonrpc"] <- node "2.0"
    response["id"] <- JsonNode.Parse id
    response["result"] <- result
    response.ToJsonString()

let private protocolError (id: string) (code: int) (message: string) =
    let error = JsonObject()
    error["code"] <- node code
    error["message"] <- node message
    let response = JsonObject()
    response["jsonrpc"] <- node "2.0"
    response["id"] <- JsonNode.Parse id
    response["error"] <- error
    response.ToJsonString()

let private requestId (request: JsonElement) : Result<string option, string> =
    match property "id" request with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.String
                  || value.ValueKind = JsonValueKind.Number
                  || value.ValueKind = JsonValueKind.Null -> Ok(Some(value.GetRawText()))
    | Some _ -> invalid "JSON-RPC request id must be a string, number, or null"

let private tools =
    """[{"name":"task_create","description":"Create a Workflow task using the strict schema-v1 DTO boundary.","inputSchema":{"type":"object","additionalProperties":false,"required":["projectRoot","taskId","title","kind","acceptanceCriteria","workItems"],"properties":{"projectRoot":{"type":"string","minLength":1},"taskId":{"type":"string","pattern":"^[A-Za-z]+-[0-9]+$"},"title":{"type":"string","minLength":1},"kind":{"type":"string","enum":["execution","research"]},"profile":{"type":"string","minLength":1},"acceptanceCriteria":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,"required":["id","text"],"properties":{"id":{"type":"string","pattern":"^AC[0-9]+$"},"text":{"type":"string","minLength":1}}}},"workItems":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,"required":["id","title","children"],"properties":{"id":{"type":"string","pattern":"^W[0-9]+(\\.[0-9]+)*$"},"title":{"type":"string","minLength":1},"dependsOn":{"type":"array","items":{"type":"string","pattern":"^W[0-9]+(\\.[0-9]+)*$"}},"children":{"type":"array","items":{"$ref":"#/$defs/workItem"}}}}}},"$defs":{"workItem":{"type":"object","additionalProperties":false,"required":["id","title","children"],"properties":{"id":{"type":"string","pattern":"^W[0-9]+(\\.[0-9]+)*$"},"title":{"type":"string","minLength":1},"dependsOn":{"type":"array","items":{"type":"string","pattern":"^W[0-9]+(\\.[0-9]+)*$"}},"children":{"type":"array","items":{"$ref":"#/$defs/workItem"}}}}}}},{"name":"task_get","description":"Read a Workflow task without changing state.","inputSchema":{"type":"object","additionalProperties":false,"required":["projectRoot","taskId"],"properties":{"projectRoot":{"type":"string","minLength":1},"taskId":{"type":"string","pattern":"^[A-Za-z]+-[0-9]+$"}}}},{"name":"task_apply","description":"Apply one existing Workflow command with expectedStateRevision CAS. Authority is always the trusted coordinator invocation; authority or receipt fields are not accepted.","inputSchema":{"type":"object","additionalProperties":false,"required":["projectRoot","taskId","expectedStateRevision","command"],"properties":{"projectRoot":{"type":"string","minLength":1},"taskId":{"type":"string","pattern":"^[A-Za-z]+-[0-9]+$"},"expectedStateRevision":{"type":"integer","minimum":0},"command":{"oneOf":[{"type":"object","additionalProperties":false,"required":["type","workItemId"],"properties":{"type":{"const":"start"},"workItemId":{"type":"string"}}},{"type":"object","additionalProperties":false,"required":["type","workItemId","result"],"properties":{"type":{"const":"complete-work"},"workItemId":{"type":"string"},"result":{"type":"string","minLength":1},"evidenceRefs":{"type":"array","items":{"type":"string"}}}},{"type":"object","additionalProperties":false,"required":["type","workItemId","resumeCondition"],"properties":{"type":{"const":"wait"},"workItemId":{"type":"string"},"resumeCondition":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","workItemId","blocker"],"properties":{"type":{"const":"block"},"workItemId":{"type":"string"},"blocker":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","workItemId"],"properties":{"type":{"const":"resume"},"workItemId":{"type":"string"},"observationRef":{"type":"string"}}},{"type":"object","additionalProperties":false,"required":["type","workItemId","role","reason"],"properties":{"type":{"const":"rebind-owner"},"workItemId":{"type":"string"},"role":{"type":"string","minLength":1},"agentId":{"type":"string"},"reason":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","id","kind","source","summary"],"properties":{"type":{"const":"add-evidence"},"id":{"type":"string","pattern":"^E[0-9]+$"},"kind":{"type":"string"},"source":{"type":"string","minLength":1},"summary":{"type":"string","minLength":1},"subject":{"type":"string"},"producerRole":{"type":"string"},"producerId":{"type":"string"},"reference":{"type":"string"}}},{"type":"object","additionalProperties":false,"required":["type","evidenceId","reason"],"properties":{"type":{"const":"supersede-evidence"},"evidenceId":{"type":"string","pattern":"^E[0-9]+$"},"reason":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","acceptanceId","evidenceRefs"],"properties":{"type":{"const":"verify"},"acceptanceId":{"type":"string","pattern":"^AC[0-9]+$"},"evidenceRefs":{"type":"array","minItems":1,"items":{"type":"string","minLength":1}}}},{"type":"object","additionalProperties":false,"required":["type","id","target","checkpoint","kind"],"properties":{"type":{"const":"add-guard"},"id":{"type":"string","pattern":"^G[0-9]+$"},"target":{"type":"string"},"checkpoint":{"type":"string","enum":["beforeStart","beforeComplete"]},"kind":{"type":"string"},"minimumCount":{"type":"integer","minimum":1},"producerRole":{"type":"string"},"independent":{"type":"boolean"},"applicability":{"type":"string"},"waiver":{"type":"string"}}},{"type":"object","additionalProperties":false,"required":["type","guardId"],"properties":{"type":{"const":"mark-not-applicable"},"guardId":{"type":"string","pattern":"^G[0-9]+$"},"decisionRef":{"type":"string","pattern":"^D[0-9]+$"}}},{"type":"object","additionalProperties":false,"required":["type","guardId"],"properties":{"type":{"const":"waive-guard"},"guardId":{"type":"string","pattern":"^G[0-9]+$"},"decisionRef":{"type":"string","pattern":"^D[0-9]+$"}}},{"type":"object","additionalProperties":false,"required":["type","kind","targets","rationale"],"properties":{"type":{"const":"add-decision"},"kind":{"type":"string"},"targets":{"type":"array","minItems":1,"items":{"type":"string"}},"rationale":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","id","text","impact"],"properties":{"type":{"const":"add-question"},"id":{"type":"string","pattern":"^Q[0-9]+$"},"text":{"type":"string","minLength":1},"impact":{"type":"string"}}},{"type":"object","additionalProperties":false,"required":["type","questionId","decisionRef"],"properties":{"type":{"const":"resolve-question"},"questionId":{"type":"string","pattern":"^D[0-9]+$"}}},{"type":"object","additionalProperties":false,"required":["type","reason","targets"],"properties":{"type":{"const":"reopen"},"reason":{"type":"string","minLength":1},"targets":{"type":"array","minItems":1,"items":{"type":"string"}},"decisionRef":{"type":"string","pattern":"^D[0-9]+$"}}},{"type":"object","additionalProperties":false,"required":["type","patch"],"properties":{"type":{"const":"apply-contract-patch"},"patch":{"oneOf":[{"type":"object","additionalProperties":false,"required":["type","id","text"],"properties":{"type":{"const":"addAcceptanceCriterion"},"id":{"type":"string","pattern":"^AC[0-9]+$"},"text":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","id","text"],"properties":{"type":{"const":"updateAcceptanceCriterion"},"id":{"type":"string","pattern":"^AC[0-9]+$"},"text":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","id"],"properties":{"type":{"const":"removeGuard"},"id":{"type":"string","pattern":"^G[0-9]+$"}}},{"type":"object","additionalProperties":false,"required":["type","id"],"properties":{"type":{"const":"removeAcceptanceCriterion"},"id":{"type":"string","pattern":"^AC[0-9]+$"}}},{"type":"object","additionalProperties":false,"required":["type","text"],"properties":{"type":{"const":"setObjective"},"text":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","text"],"properties":{"type":{"const":"setScope"},"text":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type","text"],"properties":{"type":{"const":"setNonGoals"},"text":{"type":"string","minLength":1}}}]},"decisionRef":{"type":"string","pattern":"^D[0-9]+$"}}},{"type":"object","additionalProperties":false,"required":["type","plan"],"properties":{"type":{"const":"reconcile-contract-drift"},"plan":{"oneOf":[{"type":"object","additionalProperties":false,"required":["type"],"properties":{"type":{"const":"acceptExternalContract"}}},{"type":"object","additionalProperties":false,"required":["type","objective","scope","nonGoals","acceptanceCriteria"],"properties":{"type":{"const":"restoreCanonicalContract"},"objective":{"type":"string"},"scope":{"type":"string"},"nonGoals":{"type":"string"},"acceptanceCriteria":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["id","text"],"properties":{"id":{"type":"string","pattern":"^AC[0-9]+$"},"text":{"type":"string","minLength":1}}}}}}]}}},{"type":"object","additionalProperties":false,"required":["type","reason"],"properties":{"type":{"const":"reclassify-task"},"kind":{"type":"string","enum":["execution","research"]},"profile":{"type":"string","minLength":1},"reason":{"type":"string","minLength":1}}},{"type":"object","additionalProperties":false,"required":["type"],"properties":{"type":{"const":"reconcile-profile-drift"}}},{"type":"object","additionalProperties":false,"required":["type","state","evidenceSummary","next"],"properties":{"type":{"const":"complete-task"},"state":{"type":"string","minLength":1},"evidenceSummary":{"type":"string","minLength":1},"next":{"type":"string","minLength":1}}}]}}}},{"name":"task_validate","description":"Validate a persisted Workflow task and report contract/profile drift.","inputSchema":{"type":"object","additionalProperties":false,"required":["projectRoot","taskId"],"properties":{"projectRoot":{"type":"string","minLength":1},"taskId":{"type":"string","pattern":"^[A-Za-z]+-[0-9]+$"}}}}]"""

let private initializeResult protocolVersion =
    let result = JsonObject()
    result["protocolVersion"] <- node protocolVersion
    let capabilities = JsonObject()
    let toolsCapability = JsonObject()
    toolsCapability["listChanged"] <- node false
    capabilities["tools"] <- toolsCapability
    result["capabilities"] <- capabilities
    let serverInfo = JsonObject()
    serverInfo["name"] <- node "opencode-workflow"
    serverInfo["version"] <- node "1"
    result["serverInfo"] <- serverInfo
    result

let private toolsResult () =
    let result = JsonObject()
    result["tools"] <- JsonNode.Parse tools
    result

let private invokeTool name arguments =
    try
        match parseOperation name arguments with
        | Error message ->
            let detail = InvalidInput message
            failure detail
        | Ok operation ->
            match execute operation with
            | Ok task -> success (serialize task)
            | Error error -> failure error
    with error ->
        failure (PersistenceFailure $"runtime operation failed: {error.Message}")

let private handle id methodName parameters =
    match methodName with
    | "ping" -> response id (JsonObject())
    | "initialize" ->
        match parameters with
        | None -> protocolError id -32602 "initialize requires params"
        | Some values ->
            match objectProperties "initialize params" values [ "protocolVersion"; "capabilities"; "clientInfo" ] [ "protocolVersion"; "capabilities"; "clientInfo" ] with
            | Error message -> protocolError id -32602 message
            | Ok values ->
                match requiredString "initialize params" "protocolVersion" values with
                | Error message -> protocolError id -32602 message
                | Ok version when version <> ProtocolVersion && version <> AcceptedClientProtocolVersion -> protocolError id -32602 $"unsupported protocol version '{version}'"
                | Ok _ -> response id (initializeResult ProtocolVersion)
    | "tools/list" -> response id (toolsResult ())
    | "tools/call" ->
        match parameters with
        | None -> protocolError id -32602 "tools/call requires params"
        | Some values ->
            match objectProperties "tools/call params" values [ "name"; "arguments"; "_meta" ] [ "name" ] with
            | Error message -> protocolError id -32602 message
            | Ok values ->
                match requiredString "tools/call params" "name" values with
                | Error message -> protocolError id -32602 message
                | Ok name ->
                    match property "arguments" values with
                    | Some arguments when arguments.ValueKind <> JsonValueKind.Object -> protocolError id -32602 "tools/call arguments must be an object"
                    | Some arguments -> response id (invokeTool name arguments)
                    | None -> response id (invokeTool name (JsonDocument.Parse("{}").RootElement))
    | _ -> protocolError id -32601 $"method '{methodName}' is not supported"

let private processLine (line: string) =
    try
        use document = JsonDocument.Parse(line, JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false))
        let request = document.RootElement

        match objectProperties "JSON-RPC request" request [ "jsonrpc"; "id"; "method"; "params" ] [ "jsonrpc"; "method" ] with
        | Error message -> protocolError "null" -32600 message
        | Ok request ->
            match requiredString "JSON-RPC request" "jsonrpc" request with
            | Error message -> protocolError "null" -32600 message
            | Ok version when version <> "2.0" -> protocolError "null" -32600 "JSON-RPC request jsonrpc must be exactly '2.0'"
            | Ok _ ->
                match requestId request with
                | Error message -> protocolError "null" -32600 message
                | Ok id ->
                    let methodName = requiredString "JSON-RPC request" "method" request
                    match methodName, id with
                    | Error message, Some requestId -> protocolError requestId -32600 message
                    | Error _, None -> ""
                    | Ok _, None -> ""
                    | Ok methodName, Some requestId ->
                        let parameters = property "params" request
                        if methodName = "notifications/initialized" then ""
                        else handle requestId methodName parameters
    with
    | :? JsonException as error -> protocolError "null" -32700 $"invalid JSON: {error.Message}"
    | :? FormatException as error -> protocolError "null" -32600 $"invalid request: {error.Message}"
    | _ -> protocolError "null" -32603 "internal MCP error"

let private validateCatalog args =
    match args with
    | [| "--profile-catalog"; path; "--profile-catalog-sha256"; expected |]
        when Path.IsPathFullyQualified path
             && Regex.IsMatch(expected, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ->
        try
            if not (File.Exists path) then Error "the canonical profile catalog does not exist"
            elif (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint then Error "the canonical profile catalog must not be a reparse point"
            else
                let actual = SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
                if actual <> expected then Error "the canonical profile catalog hash does not match"
                else Ok()
        with error -> Error $"the canonical profile catalog could not be validated: {error.Message}"
    | _ -> Error "the Workflow requires --profile-catalog <absolute path> --profile-catalog-sha256 <sha256>"

[<EntryPoint>]
let main args =
    match validateCatalog args with
    | Error message ->
        Console.Error.WriteLine($"workflow startup failed: {message}")
        1
    | Ok() ->
        let mutable continueReading = true
        while continueReading do
            let line = Console.ReadLine()
            if isNull line then
                continueReading <- false
            elif not (String.IsNullOrWhiteSpace line) then
                let output = processLine line
                if output <> "" then printfn "%s" output
        0
