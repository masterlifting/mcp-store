// W3/AC17 recursive schema/parser parity guard.
//
// This script parses the inline `tools` JSON literal published by
// workflow/WorkflowMcp.fs and proves the published schemas stay
// parser-equivalent for the live-usage gaps:
//
//   1. every object that declares `additionalProperties:false` together with a
//      `required` list names every required property in its `properties`
//      (the resolve-question class of defect that made an instance
//      unconstructible);
//   2. the discriminated command, contract-patch, and reconciliation-plan
//      variant sets are complete and recurse to every nested schema;
//   3. every published finite vocabulary round-trips through the public parser
//      and every parser union case is represented in the published vocabulary.
//
// It uses only System.Text.Json and FSharp.Reflection; there is no external
// package or package-system dependency.

#load "../ComputationExpressions.fs"
#load "../Workflow.fs"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open Workflow

let fail (name: string) (message: string) = failwithf "%s: %s" name message

let assertTrue name condition =
    if not condition then fail name "expected true"

let assertEqual name (expected: 'a) (actual: 'a) =
    if actual <> expected then fail name (sprintf "expected %A, got %A" expected actual)

let expectOk label result =
    match result with
    | Ok _ -> ()
    | Error error -> fail label (renderError error)

let unionCount (unionType: Type) = FSharpType.GetUnionCases unionType |> Array.length

let enumStrings (node: JsonNode) =
    node.["enum"].AsArray()
    |> Seq.map (fun value -> value.GetValue<string>())
    |> List.ofSeq

// --- inline literal extraction ---------------------------------------------

let sourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "WorkflowMcp.fs"))

let extractToolsJson () =
    let text = File.ReadAllText sourcePath
    let marker = "let private tools ="
    let markerIndex = text.IndexOf(marker, StringComparison.Ordinal)

    if markerIndex < 0 then
        fail "tools marker" $"'{marker}' was not found in {sourcePath}"

    let openIndex = text.IndexOf("\"\"\"", markerIndex, StringComparison.Ordinal)

    if openIndex < 0 then
        fail "tools literal" "the opening triple quote was not found"

    let closeIndex = text.IndexOf("\"\"\"", openIndex + 3, StringComparison.Ordinal)

    if closeIndex < 0 then
        fail "tools literal" "the closing triple quote was not found"

    text.Substring(openIndex + 3, closeIndex - openIndex - 3)

let tools =
    let json = extractToolsJson ()

    try
        JsonNode.Parse json
    with error ->
        fail "tools literal JSON" error.Message

let findTool name =
    tools.AsArray()
    |> Seq.map (fun node -> node.AsObject())
    |> Seq.find (fun tool -> tool.["name"].GetValue<string>() = name)

let taskApply = findTool "task_apply"
let commandVariants = taskApply.["inputSchema"].["properties"].["command"].["oneOf"].AsArray()

let requiredNames (node: JsonNode) =
    node.["required"].AsArray()
    |> Seq.map (fun name -> name.GetValue<string>())
    |> Set.ofSeq

let propertyNames (node: JsonNode) =
    if isNull node.["properties"] then
        Set.empty
    else
        node.["properties"].AsObject()
        |> Seq.map (fun property -> property.Key)
        |> Set.ofSeq

let findVariant (variants: JsonArray) name =
    variants
    |> Seq.find (fun variant -> variant.["properties"].["type"].["const"].GetValue<string>() = name)

let variantTypes (variants: JsonArray) =
    variants
    |> Seq.map (fun variant -> variant.["properties"].["type"].["const"].GetValue<string>())
    |> List.ofSeq

// --- 1. recursive required/properties parity --------------------------------

let rec assertRequiredDeclared label (node: JsonNode) =
    if not (isNull node) then
        match node.GetValueKind() with
        | JsonValueKind.Object ->
            let obj = node.AsObject()

            let closed =
                obj.ContainsKey "additionalProperties"
                && obj.["additionalProperties"].GetValue<bool>() = false
                && obj.ContainsKey "required"

            if closed then
                let missing = Set.difference (requiredNames node) (propertyNames node)

                if not missing.IsEmpty then
                    fail label (sprintf "required names missing from properties: %A" missing)

            for property in obj do
                assertRequiredDeclared $"{label}.{property.Key}" property.Value
        | JsonValueKind.Array ->
            node.AsArray()
            |> Seq.iteri (fun index child -> assertRequiredDeclared $"{label}[{index}]" child)
        | _ -> ()

assertRequiredDeclared "tools" tools
commandVariants |> Seq.iteri (fun index variant -> assertRequiredDeclared $"command[{index}]" variant)

// --- 2. discriminated variant coverage --------------------------------------

let expectedCommands =
    [ "start"
      "complete-work"
      "wait"
      "block"
      "resume"
      "rebind-owner"
      "add-evidence"
      "supersede-evidence"
      "verify"
      "add-guard"
      "mark-not-applicable"
      "waive-guard"
      "add-decision"
      "add-question"
      "resolve-question"
      "reopen"
      "apply-contract-patch"
      "reconcile-contract-drift"
      "reclassify-task"
      "reconcile-profile-drift"
      "complete-task" ]

assertEqual "command variant set" expectedCommands (variantTypes commandVariants)

let applyContractPatch = findVariant commandVariants "apply-contract-patch"
let patchVariants = applyContractPatch.["properties"].["patch"].["oneOf"].AsArray()

let expectedPatches =
    [ "addAcceptanceCriterion"
      "updateAcceptanceCriterion"
      "addGuard"
      "removeGuard"
      "removeAcceptanceCriterion"
      "setObjective"
      "setScope"
      "setNonGoals" ]

assertEqual "contract patch variant set" expectedPatches (variantTypes patchVariants)
patchVariants |> Seq.iteri (fun index variant -> assertRequiredDeclared $"patch[{index}]" variant)

let reconcileContractDrift = findVariant commandVariants "reconcile-contract-drift"
let planVariants = reconcileContractDrift.["properties"].["plan"].["oneOf"].AsArray()

assertEqual
    "reconciliation plan variant set"
    [ "acceptExternalContract"; "restoreCanonicalContract" ]
    (variantTypes planVariants)

planVariants |> Seq.iteri (fun index variant -> assertRequiredDeclared $"plan[{index}]" variant)

// --- 3. explicit live-usage gap assertions ----------------------------------

// resolve-question: required/decisionRef must be declared, questionId is a Q-id.
let resolveQuestion = findVariant commandVariants "resolve-question"

assertEqual
    "resolve-question required"
    (Set.ofList [ "type"; "questionId"; "decisionRef" ])
    (requiredNames resolveQuestion)

assertEqual
    "resolve-question properties"
    (Set.ofList [ "type"; "questionId"; "decisionRef" ])
    (propertyNames resolveQuestion)

assertEqual
    "resolve-question questionId pattern"
    "^Q[0-9]+$"
    (resolveQuestion.["properties"].["questionId"].["pattern"].GetValue<string>())

assertEqual
    "resolve-question decisionRef pattern"
    "^D[0-9]+$"
    (resolveQuestion.["properties"].["decisionRef"].["pattern"].GetValue<string>())

assertTrue "resolve-question has no workItemId" (not (Set.contains "workItemId" (propertyNames resolveQuestion)))

// The contract-patch addGuard variant must publish the same closed shape as the
// add-guard command so both parser branches are expressible.
let addGuard = findVariant commandVariants "add-guard"
let addGuardPatch = findVariant patchVariants "addGuard"
assertEqual "addGuard patch required" (requiredNames addGuard) (requiredNames addGuardPatch)
assertEqual "addGuard patch properties" (propertyNames addGuard) (propertyNames addGuardPatch)

// Guard target/checkpoint/kind/applicability/waiver vocabularies.
let guardTargetOneOf = addGuard.["properties"].["target"].["oneOf"].AsArray()
assertEqual "guard target const" "task" (guardTargetOneOf.[0].["const"].GetValue<string>())
assertEqual "GuardTarget variant count" 2 guardTargetOneOf.Count
let workItemTargetPattern = guardTargetOneOf.[1].["pattern"].GetValue<string>()
assertTrue "guard workItem target matches" (Regex.IsMatch("workItem:W1.2", workItemTargetPattern))
assertTrue "guard workItem target rejects" (not (Regex.IsMatch("workItem:1", workItemTargetPattern)))
expectOk "parseGuardTarget task" (parseGuardTarget "task")
expectOk "parseGuardTarget workItem" (parseGuardTarget "workItem:W1.2")

let checkpointEnum = enumStrings addGuard.["properties"].["checkpoint"]
assertEqual "guard checkpoint enum" [ "beforeStart"; "beforeComplete" ] checkpointEnum

for checkpoint in checkpointEnum do
    expectOk $"parseGuardCheckpoint {checkpoint}" (parseGuardCheckpoint checkpoint)

assertEqual "GuardCheckpoint union case count" 2 (unionCount typeof<GuardCheckpoint>)

let applicabilityOneOf = addGuard.["properties"].["applicability"].["oneOf"].AsArray()
assertEqual "guard applicability const" "always" (applicabilityOneOf.[0].["const"].GetValue<string>())
assertEqual "ApplicabilityPolicy variant count" 2 applicabilityOneOf.Count
let applicabilityPattern = applicabilityOneOf.[1].["pattern"].GetValue<string>()

for authority in [ "coordinator"; "user" ] do
    let value = $"explicitDecision:{authority}"
    assertTrue $"guard applicability pattern {authority}" (Regex.IsMatch(value, applicabilityPattern))
    expectOk $"parseApplicability {authority}" (parseApplicability value)

expectOk "parseApplicability always" (parseApplicability "always")

let waiverOneOf = addGuard.["properties"].["waiver"].["oneOf"].AsArray()
assertEqual "guard waiver const" "notWaivable" (waiverOneOf.[0].["const"].GetValue<string>())
assertEqual "WaiverPolicy variant count" 2 waiverOneOf.Count
let waiverPattern = waiverOneOf.[1].["pattern"].GetValue<string>()

for authority in [ "coordinator"; "user" ] do
    let value = $"waivableBy:{authority}"
    assertTrue $"guard waiver pattern {authority}" (Regex.IsMatch(value, waiverPattern))
    expectOk $"parseWaiver {authority}" (parseWaiver value)

expectOk "parseWaiver notWaivable" (parseWaiver "notWaivable")

// Evidence kind: 7 fixed values plus the open `other:<non-empty>` form.
let addEvidence = findVariant commandVariants "add-evidence"
let evidenceKindOneOf = addEvidence.["properties"].["kind"].["oneOf"].AsArray()
let evidenceKindEnum = enumStrings evidenceKindOneOf.[0]

assertEqual
    "evidence kind enum"
    [ "build"; "test"; "review"; "observation"; "externalEffect"; "research"; "decisionEvidence" ]
    evidenceKindEnum

for kind in evidenceKindEnum do
    expectOk $"parseEvidenceKind {kind}" (parseEvidenceKind kind)

expectOk "parseEvidenceKind other" (parseEvidenceKind "other:custom")
assertEqual "EvidenceKind union case count" 8 (unionCount typeof<EvidenceKind>)
assertEqual "EvidenceKind published count" (unionCount typeof<EvidenceKind>) (evidenceKindEnum.Length + 1)
let evidenceOtherPattern = evidenceKindOneOf.[1].["pattern"].GetValue<string>()
assertTrue "evidence other pattern accepts" (Regex.IsMatch("other:custom", evidenceOtherPattern))
assertTrue "evidence other pattern rejects blank" (not (Regex.IsMatch("other:   ", evidenceOtherPattern)))

// Decision kind: the closed 7-case vocabulary.
let addDecision = findVariant commandVariants "add-decision"
let decisionKindEnum = enumStrings addDecision.["properties"].["kind"]

assertEqual
    "decision kind enum"
    [ "userDecision"
      "designDecision"
      "assumption"
      "policyApplication"
      "contractRevision"
      "applicabilityDecision"
      "waiverDecision" ]
    decisionKindEnum

for kind in decisionKindEnum do
    expectOk $"parseDecisionKind {kind}" (parseDecisionKind kind)

assertEqual "DecisionKind union case count" 7 (unionCount typeof<DecisionKind>)

// Question impact: taskWide or workItems:<W-id,...>.
let addQuestion = findVariant commandVariants "add-question"
let impactOneOf = addQuestion.["properties"].["impact"].["oneOf"].AsArray()
assertEqual "impact const" "taskWide" (impactOneOf.[0].["const"].GetValue<string>())
assertEqual "QuestionImpact variant count" 2 impactOneOf.Count
let impactPattern = impactOneOf.[1].["pattern"].GetValue<string>()
assertTrue "impact pattern accepts" (Regex.IsMatch("workItems:W1,W2.3", impactPattern))
assertTrue "impact pattern rejects empty" (not (Regex.IsMatch("workItems:", impactPattern)))
expectOk "parseQuestionImpact taskWide" (parseQuestionImpact "taskWide")
expectOk "parseQuestionImpact workItems" (parseQuestionImpact "workItems:W1,W2.3")

// Reopen targets: acceptance:<AC-id>, workItem:<W-id>, guard:<G-id>.
let reopen = findVariant commandVariants "reopen"
let reopenTargetOneOf = reopen.["properties"].["targets"].["items"].["oneOf"].AsArray()

let reopenSamples = [ "acceptance:AC1"; "workItem:W1.2"; "guard:G1" ]
assertEqual "ReopenTarget variant count" 3 reopenTargetOneOf.Count

for index in 0 .. reopenTargetOneOf.Count - 1 do
    let pattern = reopenTargetOneOf.[index].["pattern"].GetValue<string>()
    let sample = reopenSamples.[index]
    assertTrue $"reopen target pattern {index}" (Regex.IsMatch(sample, pattern))
    expectOk $"parseReopenTarget {sample}" (parseReopenTarget sample)

assertEqual "ReopenTarget union case count" 3 (unionCount typeof<ReopenTarget>)

// Decision targets: the nine structured prefix forms. The parser joins nested
// reopenTask targets with ';' (Workflow.decisionTargetToString/parseReopenTargets).
let decisionTargetOneOf = addDecision.["properties"].["targets"].["items"].["oneOf"].AsArray()

let decisionTargetSamples =
    [ "waiveAcceptance:AC1"
      "guardDisposition:G1"
      "skipWorkItem:W1.2"
      "requirementChange:W3"
      "contractPatch:objective text"
      "reopenTask:T-1|acceptance:AC1;workItem:W2.3;guard:G4"
      "questionResolution:Q2"
      "reclassify:reclassified"
      "other:manual-note" ]

assertEqual "DecisionTarget variant count" 9 decisionTargetOneOf.Count

for index in 0 .. decisionTargetOneOf.Count - 1 do
    let pattern = decisionTargetOneOf.[index].["pattern"].GetValue<string>()
    let sample = decisionTargetSamples.[index]
    assertTrue $"decision target pattern {index}" (Regex.IsMatch(sample, pattern))
    expectOk $"parseDecisionTarget {sample}" (parseDecisionTarget sample)

assertEqual "DecisionTarget union case count" 9 (unionCount typeof<DecisionTarget>)
let reopenTaskPattern = decisionTargetOneOf.[5].["pattern"].GetValue<string>()
assertTrue
    "reopenTask encodes targets with semicolon"
    (Regex.IsMatch("reopenTask:T-1|acceptance:AC1;workItem:W2", reopenTaskPattern))
assertTrue
    "reopenTask rejects comma separation"
    (not (Regex.IsMatch("reopenTask:T-1|acceptance:AC1,workItem:W2", reopenTaskPattern)))

// Reclassification kind is parser-backed (execution|research); parseKind is a
// private WorkflowMcp helper, so the published vocabulary is asserted directly.
let reclassifyTask = findVariant commandVariants "reclassify-task"

assertEqual
    "reclassify kind enum"
    [ "execution"; "research" ]
    (enumStrings reclassifyTask.["properties"].["kind"])

printfn
    "OK recursive Workflow schema/parser parity: required/properties invariant, command/patch/plan variant coverage, and enum round-trip"
