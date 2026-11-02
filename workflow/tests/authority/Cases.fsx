// role: test entrypoint
// Isolated-child deterministic coverage for the trusted User-ingress boundary.
// This suite may only run inside a fresh child process whose application-data and home directories are redirected to an isolated temporary root, so the real per-account integrity key can never be read, created, or rotated here.
// The supported entry point is authority/Run.fsx, which owns the redirection and aggregates the reported cases.

#load "../Receipt.fsx"
#load "../../domain/Computation.fs"
#load "../../domain/Integrity.fs"
#load "../../domain/Workflow.fs"
#load "../../protocol/Adapter.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Workflow
open WorkflowAdapter

let fail message = raise (Exception message)


let isolatedRoot =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_ISOLATED_ROOT" with
    | value when not (String.IsNullOrWhiteSpace value) ->
        Path.GetFullPath value
    | _ ->
        fail
            "MCP_WORKFLOW_TEST_ISOLATED_ROOT is required; run this suite through authority/Run.fsx so the per-account application data directory is redirected"

let samePath (left: string) (right: string) =
    String.Equals(
        left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase
    )

let resolvedAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

if String.IsNullOrWhiteSpace resolvedAppData then
    fail "per-account application data directory is unavailable"

let expectedAppData = Path.Combine(isolatedRoot, "appdata")

if not (samePath resolvedAppData expectedAppData) then
    failwithf
        "resolved application data directory '%s' is not the isolated test root '%s'"
        resolvedAppData
        expectedAppData

match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_REAL_APP_DATA" with
| value when not (String.IsNullOrWhiteSpace value) ->
    if samePath resolvedAppData value then
        fail "the isolated child resolved the real per-account application data directory"
| _ -> ()

let keyDirectory = Path.Combine(resolvedAppData, "mcp-store", "workflow")
let keyPath = Path.Combine(keyDirectory, "integrity.key")

if File.Exists keyPath then
    failwithf "the isolated key path already exists; every run needs a fresh isolated root: %s" keyPath

let projectRoot = Path.Combine(isolatedRoot, "project")
Directory.CreateDirectory projectRoot |> ignore

printfn "KEYDIR %s" keyDirectory


let mutable passed = 0
let mutable failed = 0
let failures = ResizeArray<string>()

let check name condition detail =
    if condition then
        passed <- passed + 1
        printfn "CASE %s PASS" name
    else
        failed <- failed + 1
        failures.Add(sprintf "%s :: %s" name detail)
        printfn "CASE %s FAIL %s" name detail

let checkOk name (result: Async<Result<'a, RuntimeError>>) (predicate: 'a -> bool) (detail: 'a -> string) : Async<unit> =
    async {
        match! result with
        | Ok value ->
            if predicate value then
                check name true ""
            else
                check name false (detail value)
        | Error error ->
            check name false (renderError error)
    }

let checkError name (fragment: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ ->
            check name false (sprintf "expected an error containing '%s', got Ok" fragment)
        | Error error ->
            let message = renderError error

            if message.Contains(fragment, StringComparison.Ordinal) then
                check name true ""
            else
                check name false (sprintf "expected '%s', got '%s'" fragment message)
    }

// Asserts the exact structured User-authority remediation, not just its text.
let checkAuthorityDenied
    name
    (operation: string)
    (kind: DecisionKind)
    (target: DecisionTarget)
    (status: DecisionRefStatus)
    (result: Async<Result<'a, RuntimeError>>)
    : Async<unit> =
    async {
        match! result with
        | Ok _ ->
            check name false "expected a User-authority denial, got Ok"
        | Error error ->
            match tryAuthorityMetadata error with
            | None ->
                check name false (sprintf "expected structured authority metadata, got '%s'" (renderError error))
            | Some metadata ->
                let problems =
                    [ if metadata.RequiredAuthority <> MinimumAuthority.UserAuthority then
                          "required authority is not user"
                      if metadata.Operation <> operation then
                          sprintf "operation is '%s'" metadata.Operation
                      if metadata.DecisionKind <> Some kind then
                          sprintf "decision kind is '%A'" metadata.DecisionKind
                      if metadata.Target <> Some target then
                          sprintf "target is '%A'" metadata.Target
                      if metadata.DecisionRefStatus <> status then
                          sprintf "decision reference status is '%A'" metadata.DecisionRefStatus ]

                if problems.IsEmpty then
                    check name true ""
                else
                    check name false (String.concat "; " problems)
    }

// The typed outcome an explicit decision reference must produce.
let referenceOutcome (error: RuntimeError) =
    match error with
    | NotFound _ -> "not-found"
    | InvalidInput _ -> "invalid-input"
    | AuthorityDenied (metadata, _) when metadata.DecisionRefStatus = DecisionRefStatus.Present -> "authority-denied-present"
    | _ -> "other"

// Asserts the exact typed rejection, not only its text.
let checkReferenceOutcome name (expected: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ ->
            check name false "expected a rejection, got Ok"
        | Error error ->
            let actual = referenceOutcome error

            if actual = expected then
                check name true ""
            else
                check name false (sprintf "expected '%s', got '%s' (%s)" expected actual (renderError error))
    }

// Asserts the typed rejection of the decision-resolution boundary, which
// reports the reference semantics as error cases.
let checkDomainReferenceOutcome name (expected: string) (result: Result<'a, RuntimeError list>) : unit =
    match result with
    | Ok _ ->
        check name false "expected a rejection, got Ok"
    | Error errors ->
        let actual =
            match errors with
            | [ error ] -> referenceOutcome error
            | _ -> "other"

        if actual = expected then
            check name true ""
        else
            check
                name
                false
                (sprintf
                    "expected '%s', got '%s' (%s)"
                    expected
                    actual
                    (errors |> List.map renderError |> String.concat "; "))


let jsonOptions = JsonSerializerOptions(WriteIndented = true)

let canonical (text: string) : string = (JsonNode.Parse text).ToJsonString(jsonOptions)

let payloadOfSigned (text: string) =
    let node = JsonNode.Parse(text).AsObject()
    node.Remove("integrityReceipt") |> ignore
    node.ToJsonString(jsonOptions)

let receiptPrefix = "hmac-sha256-v1:"

let receiptOfSigned (text: string) =
    let node = JsonNode.Parse(text).AsObject()
    (node["integrityReceipt"]).GetValue<string>()

let revisionOf (text: string) =
    let node = JsonNode.Parse(text).AsObject()
    (node["stateRevision"]).GetValue<int>()

let decisionField (decision: JsonNode) (name: string) =
    match decision[name] with
    | null -> ""
    | value -> value.GetValue<string>()

// A persisted User claim is a user-authority decision or any decision that
// carries a confirmation reference, so both halves are inspected.
let userClaimCount (text: string) =
    let node = JsonNode.Parse(text).AsObject()
    let decisions = (node["decisions"]).AsArray()

    decisions
    |> Seq.toList
    |> List.filter (fun decision ->
        let authority = decisionField decision "authority"
        let confirmationRef = decisionField decision "confirmationRef"
        String.Equals(authority, "user", StringComparison.OrdinalIgnoreCase)
        || not (String.IsNullOrWhiteSpace confirmationRef))
    |> List.length

// The expected receipt comes from the shared framing oracle, so the suite
// checks the producer's framing against an independent implementation.
let expectedReceipt key requestedId (canonicalPayloadJson: string) =
    (WorkflowReceiptOracle.expectedReceipt
        key
        WorkflowReceiptOracle.platformName
        (WorkflowReceiptOracle.canonicalProjectRoot projectRoot)
        requestedId
        canonicalPayloadJson)

let withIsolatedKey (action: byte array -> unit) =
    let key = WorkflowIntegrity.loadExistingKey ()

    try
        action key
    finally
        CryptographicOperations.ZeroMemory key

let sidecar root id = Path.Combine(root, ".tasks", id, "runtime.json")

let createRequest id title =
    { Id = id
      Title = title
      Kind = Execution
      AcceptanceCriteria = [ ("AC1", "Execution completes") ]
      WorkItems =
          [ { Id = "W1"
              Title = "Do the work"
              DependsOn = []
              Children = [] } ] }

let makeEvidence id kind summary =
    { Id = id
      Kind = kind
      Source = EvidenceSource "tester"
      Subject = None
      ProducerRole = None
      ProducerId = None
      Reference = None
      Summary = summary }

let evidenceKind value =
    match parseEvidenceKind value with
    | Ok kind -> kind
    | Error error -> failwithf "invalid test evidence kind %A: %s" value (renderError error)

// A project profile whose two guards both require explicit User authority and
// are waivable only by the User. It gives the suite two independent
// User-authority targets so a User decision for one target can be shown not to
// authorize the other.
let profileDirectory = Path.Combine(projectRoot, ".workflow", "profiles")
Directory.CreateDirectory profileDirectory |> ignore

let dualGuardJson key =
    sprintf
        """{"key":"%s","target":"task","checkpoint":"beforeComplete","evidenceKind":"test","minimumCount":1,"applicability":"explicitDecision:user","waiver":"waivableBy:user"}"""
        key

let dualProfileJson description =
    sprintf
        """{"schemaVersion":2,"id":"dual","description":"%s","policy":{"guards":[%s,%s]}}"""
        description
        (dualGuardJson "alpha")
        (dualGuardJson "beta")

let dualProfilePath = Path.Combine(profileDirectory, "dual.json")
File.WriteAllText(dualProfilePath, dualProfileJson "dual user-authority profile")

// A second profile whose guard is waivable by the Coordinator, so a referenced
// Coordinator decision carries sufficient authority for the waiver and is
// reused instead of a replacement decision being minted.
let coordProfileJson =
    """{"schemaVersion":2,"id":"coord","description":"coordinator waiver profile","policy":{"guards":[{"key":"alpha","target":"task","checkpoint":"beforeComplete","evidenceKind":"test","minimumCount":1,"applicability":"explicitDecision:coordinator","waiver":"waivableBy:coordinator"}]}}"""

let coordProfilePath = Path.Combine(profileDirectory, "coord.json")
File.WriteAllText(coordProfilePath, coordProfileJson)

let guardIdForKey key (task: TaskModel) =
    task.ProfileGuardKeys
    |> Map.tryFindKey (fun _ value -> value = key)
    |> Option.defaultWith (fun () -> failwithf "no guard is keyed '%s'" key)

// Creates one fixture task and fails the case when the domain refuses it.
let createFixtureTask (profileId: string option) (id: string) (title: string) : Async<TaskModel> =
    async {
        let! result = createTaskWithProfile projectRoot profileId (createRequest id title)

        return
            match result with
            | Ok task -> task
            | Error error ->
                raise (Exception(sprintf "could not create the fixture task '%s': %s" id (renderError error)))
    }

let mutable taskCounter = 100

let nextTaskId () =
    taskCounter <- taskCounter + 1
    sprintf "TRU-%d" taskCounter

let createProtectedTask () : Async<TaskModel> =
    createFixtureTask (Some "dual") (nextTaskId ()) "Trusted ingress task"

let bytesOf path = File.ReadAllBytes path

let sameBytes (left: byte array) (right: byte array) =
    left.AsSpan().SequenceEqual(right.AsSpan())

// Runs a nested operation with a bounded wait so a leaked task lock fails the
// case instead of hanging the suite.
let runBounded (work: Async<'a>) (timeoutMs: int) : Async<Result<'a, string>> =
    async {
        let task = Async.StartAsTask work
        let! winner = Task.WhenAny(task, Task.Delay(timeoutMs)) |> Async.AwaitTask

        if obj.ReferenceEquals(winner, task) then
            // The completed task is awaited instead of read synchronously, so
            // this helper never blocks on a task result.
            let! value = task |> Async.AwaitTask
            return Ok value
        else
            return Error(sprintf "the nested operation did not finish within %d ms" timeoutMs)
    }


let mutable fatal = ""

try
    async {
        let routineId = nextTaskId()

        let! routineTask = createTask projectRoot (createRequest routineId "Routine task")

        let mutable routineCallbacks = 0

        let routineCallback (_: TrustedDecisionRequest) =
            async {
                routineCallbacks <- routineCallbacks + 1
                return Ok None
            }

        let! routineApplied =
            executeApplyWithTrustedConfirmation
                { Root = projectRoot
                  TaskId = routineId
                  ExpectedStateRevision = 0
                  Command = StartWorkItem "W1" }
                true
                routineCallback

        check
            "routine.applySucceeds"
            (match routineApplied with
             | Ok task -> task.StateRevision = 1
             | Error _ -> false)
            "the routine apply did not succeed"

        check "routine.noConfirmationRequested" (routineCallbacks = 0) "a routine apply asked for confirmation"

        // The canonical creation bootstraps the producer-owned key, so a routine
        // apply signs with that key instead of running key-free.
        check
            "routine.keyBootstrappedByCreation"
            (File.Exists keyPath)
            "the canonical creation did not bootstrap the per-account integrity key"

        let routineText = File.ReadAllText(sidecar projectRoot routineId)

        withIsolatedKey (fun key ->
            check
                "routine.persistedFrameValid"
                (expectedReceipt key routineId (payloadOfSigned routineText) = receiptOfSigned routineText)
                "the routine receipt does not match the canonical frame")

        check
            "routine.noUserDecision"
            (userClaimCount routineText = 0)
            "a routine apply persisted a User-authority decision"

        let! profilesResult = resolveProfiles projectRoot

        let profiles =
            match profilesResult with
            | Ok value -> value
            | Error error -> failwithf "could not resolve the profile fixtures: %s" (renderError error)

        let! protectedTask = createProtectedTask ()
        let alphaId = guardIdForKey "alpha" protectedTask
        let betaId = guardIdForKey "beta" protectedTask

        let routinePreparation =
            prepareTrustedDecision profiles DateTimeOffset.UtcNow protectedTask (StartWorkItem "W1")

        check
            "prepare.routineCommandRejected"
            (match routinePreparation with
             | Error [ InvalidTransition _ ] -> true
             | _ -> false)
            "a routine command was accepted as a trusted User decision requirement"

        let protectedPreparation =
            prepareTrustedDecision profiles DateTimeOffset.UtcNow protectedTask (WaiveGuard(alphaId, None))

        let preparedRequest =
            match protectedPreparation with
            | Ok request -> Some request
            | Error errors ->
                check
                    "prepare.protectedCommandPrepared"
                    false
                    (errors |> List.map renderError |> String.concat "; ")
                None

        match preparedRequest with
        | Some request ->
            check "prepare.protectedCommandPrepared" true ""
            check "prepare.operation" (request.Operation = "task_apply.waive-guard") (sprintf "operation is '%s'" request.Operation)
            check "prepare.kind" (request.Kind = WaiverDecision) (sprintf "kind is '%A'" request.Kind)
            check
                "prepare.target"
                (request.Target = GuardDispositionTarget alphaId)
                (sprintf "target is '%A'" request.Target)
            check "prepare.taskId" (request.TaskId = protectedTask.Id) (sprintf "task id is '%s'" request.TaskId)
            check
                "prepare.stateRevision"
                (request.StateRevision = protectedTask.StateRevision)
                (sprintf "state revision is %d" request.StateRevision)
            check
                "prepare.command"
                (request.Command = WaiveGuard(alphaId, None))
                (sprintf "command is '%A'" request.Command)

            // A forged request may not bind to a different command.
            let forgedRequest =
                { request with
                    Command = WaiveGuard(betaId, None) }

            check
                "bind.forgedCommandRejected"
                (bindTrustedDecisionRequest forgedRequest protectedTask (WaiveGuard(alphaId, None)) "confirmation-ref" = None)
                "a request bound to another command was accepted"

            check
                "bind.emptyConfirmationRejected"
                (bindTrustedDecisionRequest request protectedTask (WaiveGuard(alphaId, None)) "" = None)
                "an empty confirmation reference was accepted"

            check
                "bind.blankConfirmationRejected"
                (bindTrustedDecisionRequest request protectedTask (WaiveGuard(alphaId, None)) "   " = None)
                "a blank confirmation reference was accepted"

            check
                "bind.exactRequestAccepted"
                (bindTrustedDecisionRequest request protectedTask (WaiveGuard(alphaId, None)) "confirmation-ref" |> Option.isSome)
                "the exact request was not bound"

            // A context bound to one command may not decide another command.
            match bindTrustedDecisionRequest request protectedTask (WaiveGuard(alphaId, None)) "confirmation-ref" with
            | Some context ->
                let mismatchedCommand =
                    decideTrustedAt
                        profiles
                        DateTimeOffset.UtcNow
                        context
                        protectedTask
                        (WaiveGuard(betaId, None))

                check
                    "decide.mismatchedCommandRejected"
                    (match mismatchedCommand with
                     | Error [ InvalidTransition message ] ->
                         message.Contains("does not match this task, revision, and command", StringComparison.Ordinal)
                     | _ -> false)
                    "a trusted context decided a different command"

                // A context whose operation/kind/target were forged must not satisfy the canonical authorization requirement.
                let forgedContext =
                    bindTrustedDecisionRequest
                        { request with
                            Operation = "task_apply.reopen" }
                        protectedTask
                        (WaiveGuard(alphaId, None))
                        "confirmation-ref"

                match forgedContext with
                | Some forged ->
                    let forgedOutcome =
                        decideTrustedAt profiles DateTimeOffset.UtcNow forged protectedTask (WaiveGuard(alphaId, None))

                    check
                        "decide.forgedRequirementRejected"
                        (match forgedOutcome with
                         | Error [ InvalidTransition message ] ->
                             message.Contains("does not match the canonical authorization requirement", StringComparison.Ordinal)
                         | _ -> false)
                        "a forged operation requirement was accepted"

                | None ->
                    check "decide.forgedRequirementRejected" false "the forged request was not bound"
            | None ->
                check "decide.mismatchedCommandRejected" false "the exact request was not bound"
                check "decide.forgedRequirementRejected" false "the forged request was not bound"
        | None -> ()

        let protectedId = nextTaskId()

        let! protectedTask = createFixtureTask (Some "dual") protectedId "Protected task"

        let protectedAlphaId = guardIdForKey "alpha" protectedTask
        let protectedBetaId = guardIdForKey "beta" protectedTask
        let protectedBytesBefore = bytesOf(sidecar projectRoot protectedId)

        do!
            checkAuthorityDenied
                "protected.ordinaryApplyDenied"
                "task_apply.waive-guard"
                WaiverDecision
                (GuardDispositionTarget protectedAlphaId)
                DecisionRefStatus.Absent
                (applyTask projectRoot protectedId 0 (WaiveGuard(protectedAlphaId, None)))

        check
            "protected.ordinaryApplyPreservedBytes"
            (sameBytes protectedBytesBefore (bytesOf(sidecar projectRoot protectedId)))
            "the denied ordinary apply changed the persisted bytes"

        let mutable observedRequest: TrustedDecisionRequest option = None
        let mutable observedCalls = 0

        let approvingCallback (request: TrustedDecisionRequest) =
            async {
                observedCalls <- observedCalls + 1
                observedRequest <- Some request
                return Ok(Some "confirmation-ref-trusted-001")
            }

        let! approved =
            executeApplyWithTrustedConfirmation
                { Root = projectRoot
                  TaskId = protectedId
                  ExpectedStateRevision = 0
                  Command = WaiveGuard(protectedAlphaId, None) }
                true
                approvingCallback

        check
            "trusted.approveSucceeds"
            (match approved with
             | Ok _ -> true
             | Error _ -> false)
            "the approved trusted apply did not succeed"

        check "trusted.callbackInvokedOnce" (observedCalls = 1) (sprintf "the confirmation callback ran %d time(s)" observedCalls)

        match observedRequest with
        | Some request ->
            check "trusted.requestOperation" (request.Operation = "task_apply.waive-guard") (sprintf "operation is '%s'" request.Operation)
            check "trusted.requestKind" (request.Kind = WaiverDecision) (sprintf "kind is '%A'" request.Kind)
            check
                "trusted.requestTarget"
                (request.Target = GuardDispositionTarget protectedAlphaId)
                (sprintf "target is '%A'" request.Target)
            check "trusted.requestTask" (request.TaskId = protectedId) (sprintf "task id is '%s'" request.TaskId)
            check "trusted.requestRevision" (request.StateRevision = 0) (sprintf "state revision is %d" request.StateRevision)
            check
                "trusted.requestCommand"
                (request.Command = WaiveGuard(protectedAlphaId, None))
                (sprintf "command is '%A'" request.Command)
        | None -> check "trusted.requestOperation" false "the confirmation callback never received a request"

        let approvedTask =
            match approved with
            | Ok task -> Some task
            | Error _ -> None

        match approvedTask with
        | Some task ->
            check "trusted.oneRevisionBump" (task.StateRevision = 1) (sprintf "state revision is %d" task.StateRevision)
            check "trusted.oneDecision" (task.Decisions.Length = 1) (sprintf "decision count is %d" task.Decisions.Length)

            match task.Decisions with
            | [ decision ] ->
                check "trusted.decisionAuthority" (decision.Authority = User) (sprintf "authority is '%A'" decision.Authority)
                check "trusted.decisionKind" (decision.Kind = WaiverDecision) (sprintf "kind is '%A'" decision.Kind)
                check
                    "trusted.decisionTarget"
                    (decision.Targets = [ GuardDispositionTarget protectedAlphaId ])
                    (sprintf "targets are '%A'" decision.Targets)
                check
                    "trusted.decisionConfirmationRef"
                    (decision.ConfirmationRef = Some "confirmation-ref-trusted-001")
                    (sprintf "confirmation ref is '%A'" decision.ConfirmationRef)
                check "trusted.decisionId" (decision.Id = "D1") (sprintf "decision id is '%s'" decision.Id)
            | _ -> check "trusted.decisionAuthority" false "the trusted apply did not mint exactly one decision"

            let waivedGuard = task.Guards |> List.find (fun guard -> guard.Id = protectedAlphaId)

            check
                "trusted.guardWaived"
                (waivedGuard.Disposition = GuardDisposition.Waived "D1")
                (sprintf "disposition is '%A'" waivedGuard.Disposition)

            let untouchedGuard = task.Guards |> List.find (fun guard -> guard.Id = protectedBetaId)

            check
                "trusted.otherGuardUntouched"
                (untouchedGuard.Disposition = GuardDisposition.Applicable)
                (sprintf "disposition is '%A'" untouchedGuard.Disposition)
        | None ->
            check "trusted.oneRevisionBump" false "the approved trusted apply did not return a task"
            check "trusted.oneDecision" false "the approved trusted apply did not return a task"

        let approvedText = File.ReadAllText(sidecar projectRoot protectedId)

        check "receipt.persisted" (approvedText.Contains("integrityReceipt")) "the trusted apply persisted no integrity receipt"

        let isolatedKey = WorkflowIntegrity.loadExistingKey ()

        try
            check
                "receipt.matchesCanonicalPayload"
                (expectedReceipt isolatedKey protectedId (payloadOfSigned approvedText) = receiptOfSigned approvedText)
                "the trusted receipt does not match the canonical frame"

            // The receipt must cover the canonical unsigned state: the payload the receipt authenticates is exactly the canonical serialization of the persisted state without its receipt.
            check
                "receipt.payloadIsCanonical"
                (canonical(payloadOfSigned approvedText) = payloadOfSigned approvedText)
                "the receipt payload is not the canonical unsigned state"

            let goldenFrameHex (platform: string) (canonicalRootValue: string) =
                Convert.ToHexString(
                    WorkflowReceiptOracle.expectedFrameBytes
                        platform
                        canonicalRootValue
                        WorkflowReceiptOracle.goldenRequestedId
                        WorkflowReceiptOracle.goldenPayloadJson
                ).ToLowerInvariant()

            check
                "receipt.framingGolden"
                (goldenFrameHex "windows" WorkflowReceiptOracle.goldenWindowsRoot = WorkflowReceiptOracle.goldenWindowsFrameHex
                 && goldenFrameHex "unix" WorkflowReceiptOracle.goldenUnixRoot = WorkflowReceiptOracle.goldenUnixFrameHex)
                "the framing golden does not match the expected frame layout"
        finally
            CryptographicOperations.ZeroMemory isolatedKey

        do!
            checkOk
                "restart.readsTrustedState"
                (getTask projectRoot protectedId)
                (fun task -> task.Decisions.Length = 1 && task.Decisions.Head.Authority = User)
                (fun task -> sprintf "expected one User decision, got %d" task.Decisions.Length)

        do!
            checkOk
                "restart.validatesTrustedState"
                (validateTask projectRoot protectedId)
                (fun _ -> true)
                (fun task -> sprintf "unexpected validation failure for %s" task.Id)

        let rejectionCases =
            [ "rejected", (fun () -> async { return Ok None })
              "cancelled", (fun () -> async { return Ok None })
              "hostError", (fun () -> async { return Error(InternalFailure "synthetic host failure") })
              "malformedReference", (fun () -> async { return Ok(Some "") })
              "paddedReference", (fun () -> async { return Ok(Some " padded ") })
              "multilineReference", (fun () -> async { return Ok(Some "ref\nsecond-line") }) ]

        for label, responder in rejectionCases do
            let id = nextTaskId()

            let! fixture = createFixtureTask (Some "dual") id "Rejection fixture"

            let fixtureGuard = guardIdForKey "alpha" fixture
            let before = bytesOf(sidecar projectRoot id)

            let callback (_: TrustedDecisionRequest) = responder ()

            let outcome =
                applyTrustedTask projectRoot id 0 (WaiveGuard(fixtureGuard, None)) callback

            let expectedFragment =
                match label with
                | "rejected"
                | "cancelled" -> "trusted User confirmation was rejected or cancelled"
                | "hostError" -> "synthetic host failure"
                | _ -> "trusted confirmation reference is malformed"

            do! checkError (sprintf "reject.%s" label) expectedFragment outcome

            check
                (sprintf "reject.%s.preservesBytes" label)
                (sameBytes before (bytesOf(sidecar projectRoot id)))
                "the rejected trusted apply changed the persisted bytes"

            let! reread = getTask projectRoot id

            check
                (sprintf "reject.%s.preservesState" label)
                (match reread with
                 | Ok task -> task.StateRevision = 0 && task.Decisions.IsEmpty
                 | Error _ -> false)
                "the rejected trusted apply changed the persisted state"

        // A routine command is not a trusted User decision requirement.
        let unsupportedId = nextTaskId()

        let! unsupportedFixture = createFixtureTask (Some "dual") unsupportedId "Unsupported fixture"

        let unsupportedGuard = guardIdForKey "alpha" unsupportedFixture
        let unsupportedBefore = bytesOf(sidecar projectRoot unsupportedId)

        do!
            checkError
                "reject.unsupportedCommand"
                "the command does not require a new trusted User decision"
                (applyTrustedTask
                    projectRoot
                    unsupportedId
                    0
                    (StartWorkItem "W1")
                    (fun (_: TrustedDecisionRequest) -> async { return Ok(Some "confirmation-ref") }))

        check
            "reject.unsupportedCommand.preservesBytes"
            (sameBytes unsupportedBefore (bytesOf(sidecar projectRoot unsupportedId)))
            "the unsupported trusted apply changed the persisted bytes"

        // A missing callback is rejected before any state is read.
        do!
            checkError
                "reject.missingCallback"
                "trusted confirmation callback is required"
                (applyTrustedTask
                    projectRoot
                    unsupportedId
                    0
                    (WaiveGuard(unsupportedGuard, None))
                    (Unchecked.defaultof<TrustedConfirmationCallback>))

        check
            "reject.missingCallback.preservesBytes"
            (sameBytes unsupportedBefore (bytesOf(sidecar projectRoot unsupportedId)))
            "the callback-less trusted apply changed the persisted bytes"

        let staleId = nextTaskId()

        let! staleFixture = createFixtureTask (Some "dual") staleId "Stale fixture"

        let staleGuard = guardIdForKey "alpha" staleFixture
        let staleBefore = bytesOf(sidecar projectRoot staleId)

        do!
            checkError
                "binding.staleRevisionRejected"
                "state revision conflict"
                (applyTrustedTask
                    projectRoot
                    staleId
                    7
                    (WaiveGuard(staleGuard, None))
                    (fun (_: TrustedDecisionRequest) -> async { return Ok(Some "confirmation-ref") }))

        check
            "binding.staleRevisionRejected.preservesBytes"
            (sameBytes staleBefore (bytesOf(sidecar projectRoot staleId)))
            "the stale trusted apply changed the persisted bytes"

        // The task bytes changed while the confirmation was pending.
        let alteredId = nextTaskId()

        let! alteredFixture = createFixtureTask (Some "dual") alteredId "Altered fixture"

        let alteredGuard = guardIdForKey "alpha" alteredFixture

        let alteredOriginalReceipt = receiptOfSigned (File.ReadAllText(sidecar projectRoot alteredId))
        let mutable alteredTamperedBytes: byte array option = None

        let alterCallback (_: TrustedDecisionRequest) =
            async {
                let path = sidecar projectRoot alteredId
                let node = JsonNode.Parse(File.ReadAllText path).AsObject()
                node["title"] <- JsonValue.Create("altered during confirmation")
                File.WriteAllText(path, node.ToJsonString(jsonOptions))
                alteredTamperedBytes <- Some (File.ReadAllBytes path)
                return Ok(Some "confirmation-ref")
            }

        do!
            checkError
                "binding.alteredBytesRejected"
                "task state changed during trusted confirmation"
                (applyTrustedTask projectRoot alteredId 0 (WaiveGuard(alteredGuard, None)) alterCallback)

        let alteredText = File.ReadAllText(sidecar projectRoot alteredId)

        check
            "binding.alteredBytesRejected.noUserDecision"
            (userClaimCount alteredText = 0)
            "the rejected trusted apply persisted a User decision"

        // The rejected approval must leave the tampered state exactly as the
        // tamper wrote it, with the receipt the tamper left behind.
        check
            "binding.alteredBytesRejected.preservesTamperedBytes"
            (match alteredTamperedBytes with
             | Some bytes ->
                 sameBytes bytes (bytesOf(sidecar projectRoot alteredId))
                 && receiptOfSigned alteredText = alteredOriginalReceipt
                 && revisionOf alteredText = 0
             | None -> false)
            "the rejected trusted apply rewrote the tampered task bytes"

        // The task advanced while the confirmation was pending.
        let advancedId = nextTaskId()

        let! advancedFixture = createFixtureTask (Some "dual") advancedId "Advanced fixture"

        let advancedGuard = guardIdForKey "alpha" advancedFixture

        let mutable advancedBytes: byte array option = None

        let advanceCallback (_: TrustedDecisionRequest) =
            async {
                let! nested =
                    applyTask
                        projectRoot
                        advancedId
                        0
                        (AddEvidence(makeEvidence "E1" (evidenceKind "test") "added during confirmation"))

                match nested with
                | Ok _ -> advancedBytes <- Some (File.ReadAllBytes(sidecar projectRoot advancedId))
                | Error _ -> ()

                return Ok(Some "confirmation-ref")
            }

        do!
            checkError
                "binding.taskAdvancedRejected"
                "task state changed during trusted confirmation"
                (applyTrustedTask projectRoot advancedId 0 (WaiveGuard(advancedGuard, None)) advanceCallback)

        let advancedText = File.ReadAllText(sidecar projectRoot advancedId)

        check
            "binding.taskAdvancedRejected.noUserDecision"
            (userClaimCount advancedText = 0)
            "the rejected trusted apply persisted a User decision"

        // The rejected approval must leave the canonical concurrent advance
        // intact and signed, without adding a revision of its own.
        withIsolatedKey (fun key ->
            check
                "binding.taskAdvancedRejected.preservesAdvancedBytes"
                (match advancedBytes with
                 | Some bytes ->
                     sameBytes bytes (bytesOf(sidecar projectRoot advancedId))
                     && revisionOf advancedText = 1
                     && expectedReceipt key advancedId (payloadOfSigned advancedText) = receiptOfSigned advancedText
                 | None -> false)
                "the rejected trusted apply rewrote the concurrently advanced task bytes")

        // The effective profile changed while the confirmation was pending.
        let profileId = nextTaskId()

        let! profileFixture = createFixtureTask (Some "dual") profileId "Profile fixture"

        let profileGuard = guardIdForKey "alpha" profileFixture

        let profileCallback (_: TrustedDecisionRequest) =
            async {
                File.WriteAllText(dualProfilePath, dualProfileJson "dual user-authority profile, edited")
                return Ok(Some "confirmation-ref")
            }

        do!
            checkError
                "binding.profileChangedRejected"
                "effective profile context changed during trusted confirmation"
                (applyTrustedTask projectRoot profileId 0 (WaiveGuard(profileGuard, None)) profileCallback)

        File.WriteAllText(dualProfilePath, dualProfileJson "dual user-authority profile")

        let profileText = File.ReadAllText(sidecar projectRoot profileId)

        check
            "binding.profileChangedRejected.noUserDecision"
            (not (profileText.Contains("\"authority\": \"user\"")))
            "the rejected trusted apply persisted a User decision"

        // Replaying the confirmed operation is rejected.
        let replayBefore = bytesOf(sidecar projectRoot protectedId)

        do!
            checkError
                "binding.replayRejected"
                "state revision conflict"
                (applyTrustedTask
                    projectRoot
                    protectedId
                    0
                    (WaiveGuard(protectedAlphaId, None))
                    (fun (_: TrustedDecisionRequest) -> async { return Ok(Some "confirmation-ref-replay") }))

        // Replaying the waiver on the current revision is rejected because the guard is already disposed, so the command requires no new trusted User decision; the trusted seam reports that canonical rejection for every preparation failure, and the ordinary path's more specific "is already disposed" reason is not part of this seam's error vocabulary.
        do!
            checkError
                "binding.replayOnCurrentRevisionRejected"
                "the command does not require a new trusted User decision"
                (applyTrustedTask
                    projectRoot
                    protectedId
                    1
                    (WaiveGuard(protectedAlphaId, None))
                    (fun (_: TrustedDecisionRequest) -> async { return Ok(Some "confirmation-ref-replay") }))

        check
            "binding.replayRejected.preservesBytes"
            (sameBytes replayBefore (bytesOf(sidecar projectRoot protectedId)))
            "the replayed trusted apply changed the persisted bytes"

        let modelId = nextTaskId()

        let! modelFixture = createFixtureTask (Some "dual") modelId "Model authority fixture"

        let modelGuard = guardIdForKey "alpha" modelFixture

        let! modelDecision =
            applyTask
                projectRoot
                modelId
                0
                (AddDecision
                    { Kind = UserDecision
                      Targets = [ GuardDispositionTarget modelGuard ]
                      Rationale = "the model claims to be the user" })

        check
            "authority.modelCannotMintUser"
            (match modelDecision with
             | Ok task ->
                 task.Decisions.Length = 1
                 && task.Decisions.Head.Authority = Coordinator
                 && task.Decisions.Head.ConfirmationRef = None
             | Error _ -> false)
            "an ordinary add-decision minted User authority"

        // The claimed decision carries the wrong kind for a waiver, so the
        // reference is an explicit input error rather than an authority denial.
        do!
            checkReferenceOutcome
                "authority.coordinatorDecisionCannotDispose"
                "invalid-input"
                (applyTask projectRoot modelId 1 (WaiveGuard(modelGuard, Some(DecisionRef "D1"))))

        // An unknown reference is a not-found decision at the resolution
        // boundary; the apply envelope carries every non-authority rejection as
        // invalid input, so the reference is rejected by its canonical reason.
        do!
            checkError
                "authority.forgedDecisionRefRejected"
                "Decision 'D9999' was not found"
                (applyTask projectRoot modelId 1 (WaiveGuard(modelGuard, Some(DecisionRef "D9999"))))

        // An explicit decision reference is resolved against the task before any
        // confirmation is requested, so an unusable reference can neither reach
        // the confirmation callback nor mint a replacement decision.
        let referenceId = nextTaskId()

        let! referenceFixture = createFixtureTask (Some "dual") referenceId "Decision reference fixture"

        let referenceAlpha = guardIdForKey "alpha" referenceFixture
        let referenceBeta = guardIdForKey "beta" referenceFixture

        // D1 carries the wrong kind for a waiver, D2 the wrong target, and D3
        // the right kind and target but only Coordinator authority.
        let! claimedWrongKind =
            applyTask
                projectRoot
                referenceId
                0
                (AddDecision
                    { Kind = UserDecision
                      Targets = [ GuardDispositionTarget referenceAlpha ]
                      Rationale = "the model claims a User decision" })

        let! claimedWrongTarget =
            applyTask
                projectRoot
                referenceId
                1
                (AddDecision
                    { Kind = WaiverDecision
                      Targets = [ GuardDispositionTarget referenceBeta ]
                      Rationale = "the model claims a waiver for another guard" })

        let! claimedInsufficient =
            applyTask
                projectRoot
                referenceId
                2
                (AddDecision
                    { Kind = WaiverDecision
                      Targets = [ GuardDispositionTarget referenceAlpha ]
                      Rationale = "the model claims a waiver" })

        check
            "reference.claimsRecorded"
            (match claimedWrongKind, claimedWrongTarget, claimedInsufficient with
             | Ok first, Ok second, Ok third ->
                 first.StateRevision = 1
                 && second.StateRevision = 2
                 && third.StateRevision = 3
                 && third.Decisions.Length = 3
                 && third.Decisions |> List.forall (fun decision -> decision.Authority = Coordinator)
             | _ -> false)
            "the reference fixture did not record the three claimed decisions"

        let referenceRevision = 3

        // The decision-resolution boundary reports the reference semantics as
        // typed errors: an unknown reference is not found, a wrong kind and a
        // wrong target are invalid input, and a matching reference with
        // insufficient authority is denied with the reference present.
        let referenceCases =
            [ ("unknown", DecisionRef "D9999", "not-found", "Decision 'D9999' was not found")
              ("wrongKind", DecisionRef "D1", "invalid-input", "has kind userDecision; operation requires kind waiverDecision")
              ("wrongTarget", DecisionRef "D2", "invalid-input", "does not authorize the exact operation")
              ("insufficient", DecisionRef "D3", "authority-denied-present", "matches the operation but does not provide") ]

        let! referenceTask = getTask projectRoot referenceId

        match referenceTask with
        | Ok task ->
            for label, decisionRef, expectedOutcome, _ in referenceCases do
                checkDomainReferenceOutcome
                    (sprintf "reference.%s.domain" label)
                    expectedOutcome
                    (decideAt profiles DateTimeOffset.UtcNow task (WaiveGuard(referenceAlpha, Some decisionRef)))
        | Error error ->
            for label, _, _, _ in referenceCases do
                check
                    (sprintf "reference.%s.domain" label)
                    false
                    (sprintf "could not read the reference fixture: %s" (renderError error))

        for label, decisionRef, _, expectedFragment in referenceCases do
            let before = bytesOf (sidecar projectRoot referenceId)

            let! beforeTask = getTask projectRoot referenceId

            let beforeRevision, beforeDecisionCount =
                match beforeTask with
                | Ok task -> task.StateRevision, task.Decisions.Length
                | Error error -> failwithf "could not read the reference fixture: %s" (renderError error)

            do!
                checkError
                    (sprintf "reference.%s.ordinary" label)
                    expectedFragment
                    (applyTask projectRoot referenceId referenceRevision (WaiveGuard(referenceAlpha, Some decisionRef)))

            let mutable callbackCalls = 0

            let countingCallback (_: TrustedDecisionRequest) =
                async {
                    callbackCalls <- callbackCalls + 1
                    return Ok(Some "confirmation-ref-reference")
                }

            do!
                checkError
                    (sprintf "reference.%s.trusted" label)
                    expectedFragment
                    (executeApplyWithTrustedConfirmation
                        { Root = projectRoot
                          TaskId = referenceId
                          ExpectedStateRevision = referenceRevision
                          Command = WaiveGuard(referenceAlpha, Some decisionRef) }
                        true
                        countingCallback)

            check
                (sprintf "reference.%s.noConfirmation" label)
                (callbackCalls = 0)
                (sprintf "the trusted apply requested confirmation %d time(s)" callbackCalls)

            let after = bytesOf (sidecar projectRoot referenceId)

            check
                (sprintf "reference.%s.preservesBytes" label)
                (sameBytes before after)
                "the rejected reference changed the persisted bytes"

            let! afterTask = getTask projectRoot referenceId

            check
                (sprintf "reference.%s.preservesState" label)
                (match afterTask with
                 | Ok task ->
                     task.StateRevision = beforeRevision
                     && task.Decisions.Length = beforeDecisionCount
                 | Error _ -> false)
                "the rejected reference changed the persisted revision or decision count"

        // A matching reference with insufficient authority is denied with the
        // structured remediation, and the reference is reported as present.
        do!
            checkAuthorityDenied
                "reference.insufficient.structured"
                "task_apply.waive-guard"
                WaiverDecision
                (GuardDispositionTarget referenceAlpha)
                DecisionRefStatus.Present
                (applyTask projectRoot referenceId referenceRevision (WaiveGuard(referenceAlpha, Some(DecisionRef "D3"))))

        // A referenced decision with sufficient authority is reused: the
        // disposition points at the existing decision and no replacement is
        // minted, so no confirmation is requested either.
        let createReuseFixture () =
            async {
                let id = nextTaskId()

                let! fixture = createFixtureTask (Some "coord") id "Reuse fixture"

                let guard = guardIdForKey "alpha" fixture

                let! claim =
                    applyTask
                        projectRoot
                        id
                        0
                        (AddDecision
                            { Kind = WaiverDecision
                              Targets = [ GuardDispositionTarget guard ]
                              Rationale = "the model claims a coordinator waiver" })

                return id, guard, claim
            }

        let! reuseId, reuseGuard, reuseClaim = createReuseFixture ()

        check
            "reuse.claimRecorded"
            (match reuseClaim with
             | Ok task ->
                 task.StateRevision = 1
                 && task.Decisions.Length = 1
                 && task.Decisions.Head.Authority = Coordinator
             | Error _ -> false)
            "the reuse fixture did not record the claimed coordinator decision"

        let! reused =
            applyTask projectRoot reuseId 1 (WaiveGuard(reuseGuard, Some(DecisionRef "D1")))

        check
            "reuse.reusesReferencedDecision"
            (match reused with
             | Ok task ->
                 task.StateRevision = 2
                 && task.Decisions.Length = 1
                 && task.Decisions.Head.Id = "D1"
                 && (task.Guards |> List.find (fun guard -> guard.Id = reuseGuard)).Disposition = GuardDisposition.Waived "D1"
             | Error _ -> false)
            "the referenced coordinator decision was not reused"

        let! reuseTrustedId, reuseTrustedGuard, _ = createReuseFixture ()

        let mutable reuseCallbackCalls = 0

        let reuseCallback (_: TrustedDecisionRequest) =
            async {
                reuseCallbackCalls <- reuseCallbackCalls + 1
                return Ok(Some "confirmation-ref-reuse")
            }

        do!
            checkOk
                "reuse.trustedReusesReferencedDecision"
                (executeApplyWithTrustedConfirmation
                    { Root = projectRoot
                      TaskId = reuseTrustedId
                      ExpectedStateRevision = 1
                      Command = WaiveGuard(reuseTrustedGuard, Some(DecisionRef "D1")) }
                    true
                    reuseCallback)
                (fun task ->
                    task.StateRevision = 2
                    && task.Decisions.Length = 1
                    && task.Decisions.Head.Id = "D1"
                    && (task.Guards
                        |> List.find (fun guard -> guard.Id = reuseTrustedGuard)).Disposition = GuardDisposition.Waived "D1")
                (fun task -> sprintf "expected one reused decision, got %d" task.Decisions.Length)

        check
            "reuse.noConfirmation"
            (reuseCallbackCalls = 0)
            (sprintf "the reused decision requested confirmation %d time(s)" reuseCallbackCalls)

        // A historical User decision never authorizes a fresh unrelated target.
        let historyId = nextTaskId()

        let! historyFixture = createFixtureTask (Some "dual") historyId "History fixture"

        let historyAlpha = guardIdForKey "alpha" historyFixture
        let historyBeta = guardIdForKey "beta" historyFixture

        let! historyApproved =
            applyTrustedTask
                projectRoot
                historyId
                0
                (WaiveGuard(historyAlpha, None))
                (fun (_: TrustedDecisionRequest) -> async { return Ok(Some "confirmation-ref-history") })

        check
            "history.userDecisionMinted"
            (match historyApproved with
             | Ok task -> task.Decisions.Length = 1 && task.Decisions.Head.Authority = User
             | Error _ -> false)
            "the trusted apply did not mint a User decision"

        // The denial is measured against the persisted state so the assertion observes the denied operation instead of the earlier trusted result.
        let historyBefore = bytesOf (sidecar projectRoot historyId)

        let! historyBeforeTask = getTask projectRoot historyId

        let historyBeforeRevision, historyBeforeDecisionCount =
            match historyBeforeTask with
            | Ok task -> task.StateRevision, task.Decisions.Length
            | Error error ->
                failwithf "could not read the history fixture before the denied reuse: %s" (renderError error)

        do!
            checkAuthorityDenied
                "history.unrelatedTargetStillDenied"
                "task_apply.mark-not-applicable"
                ApplicabilityDecision
                (GuardDispositionTarget historyBeta)
                DecisionRefStatus.Absent
                (applyTask projectRoot historyId 1 (MarkGuardNotApplicable(historyBeta, None)))

        let historyAfter = bytesOf (sidecar projectRoot historyId)

        check
            "history.unrelatedTargetStillDenied.preservesBytes"
            (sameBytes historyBefore historyAfter)
            "the denied historical reuse changed the persisted bytes"

        let! historyAfterTask = getTask projectRoot historyId

        check
            "history.unrelatedTargetStillDenied.preservesState"
            (match historyAfterTask with
             | Ok task ->
                 task.StateRevision = historyBeforeRevision
                 && task.Decisions.Length = historyBeforeDecisionCount
             | Error _ -> false)
            "the denied historical reuse changed the persisted revision or decision count"

        let concurrentId = nextTaskId()
        let otherId = nextTaskId()

        let! concurrentFixture = createFixtureTask (Some "dual") concurrentId "Concurrent fixture"
        let! otherFixture = createFixtureTask None otherId "Other task"

        let concurrentGuard = guardIdForKey "alpha" concurrentFixture

        let mutable nestedOutcome = "not run"

        let concurrentCallback (_: TrustedDecisionRequest) =
            async {
                let! nested =
                    runBounded
                        (applyTask projectRoot otherId 0 (StartWorkItem "W1"))
                        30000

                nestedOutcome <-
                    match nested with
                    | Ok (Ok task) -> sprintf "applied revision %d" task.StateRevision
                    | Ok (Error error) -> "rejected: " + renderError error
                    | Error message -> message

                return Ok(Some "confirmation-ref-concurrent")
            }

        let! concurrentApplied =
            applyTrustedTask projectRoot concurrentId 0 (WaiveGuard(concurrentGuard, None)) concurrentCallback

        check
            "concurrency.otherTaskAppliedDuringCallback"
            (nestedOutcome.StartsWith("applied revision", StringComparison.Ordinal))
            ("the nested ordinary operation did not succeed: " + nestedOutcome)

        check
            "concurrency.trustedApplySucceeded"
            (match concurrentApplied with
             | Ok task -> task.StateRevision = 1 && task.Decisions.Length = 1
             | Error _ -> false)
            "the trusted apply did not succeed while another task was applied"

        // The same task may also be applied while the confirmation is pending:
        // the task lock must already be released.
        let sameTaskId = nextTaskId()

        let! sameTaskFixture = createFixtureTask (Some "dual") sameTaskId "Same-task fixture"

        let sameTaskGuard = guardIdForKey "alpha" sameTaskFixture

        let mutable sameTaskOutcome = "not run"

        let sameTaskCallback (_: TrustedDecisionRequest) =
            async {
                let! nested = runBounded (applyTask projectRoot sameTaskId 0 (StartWorkItem "W1")) 30000

                sameTaskOutcome <-
                    match nested with
                    | Ok (Ok task) -> sprintf "applied revision %d" task.StateRevision
                    | Ok (Error error) -> "rejected: " + renderError error
                    | Error message -> message

                return Ok(Some "confirmation-ref-same-task")
            }

        do!
            checkError
                "concurrency.sameTaskMutationRejected"
                "task state changed during trusted confirmation"
                (applyTrustedTask projectRoot sameTaskId 0 (WaiveGuard(sameTaskGuard, None)) sameTaskCallback)

        check
            "concurrency.sameTaskLockReleased"
            (sameTaskOutcome.StartsWith("applied revision", StringComparison.Ordinal))
            ("the same-task nested operation did not succeed: " + sameTaskOutcome)

        let trustedText = File.ReadAllText(sidecar projectRoot protectedId)

        let tamperCase name fragment (mutate: JsonObject -> unit) =
            async {
                let node = JsonNode.Parse(trustedText).AsObject()
                mutate node
                File.WriteAllText(sidecar projectRoot protectedId, node.ToJsonString(jsonOptions))

                do! checkError name fragment (getTask projectRoot protectedId)
                File.WriteAllText(sidecar projectRoot protectedId, trustedText)
            }

        let firstDecision (parent: JsonObject) =
            let element = ((parent["decisions"]).AsArray()).[0]
            element.AsObject()

        do!
            tamperCase
                "forgery.decisionRationale"
                "task state integrity receipt is invalid"
                (fun node ->
                    let decision = firstDecision node
                    decision["rationale"] <- JsonValue.Create("forged rationale"))

        do!
            tamperCase
                "forgery.decisionTargets"
                "task state integrity receipt is invalid"
                (fun node ->
                    let decision = firstDecision node
                    decision["targets"] <- JsonNode.Parse("[\"skipWorkItem:W1\"]"))

        do!
            tamperCase
                "forgery.decisionAuthority"
                "task state integrity receipt is invalid"
                (fun node ->
                    let decision = firstDecision node
                    decision["authority"] <- JsonValue.Create("coordinator"))

        do!
            tamperCase
                "forgery.acceptanceText"
                "task state integrity receipt is invalid"
                (fun node ->
                    let criterion = ((node["acceptanceCriteria"]).AsArray()).[0].AsObject()
                    criterion["text"] <- JsonValue.Create("forged obligation"))

        do!
            tamperCase
                "forgery.stateRevision"
                "task state integrity receipt is invalid"
                (fun node -> node["stateRevision"] <- JsonValue.Create(99))

        // A flipped receipt byte is rejected. The digit inside the hexadecimal
        // tag is flipped, so the receipt stays well formed and only its
        // authentication tag is wrong.
        let flippedNode = JsonNode.Parse(trustedText).AsObject()
        let receipt = receiptOfSigned trustedText
        let tagStart = receiptPrefix.Length
        let flipped = receipt.Substring(0, tagStart) + (if receipt.[tagStart] = 'a' then "b" else "a") + receipt.Substring(tagStart + 1)
        flippedNode["integrityReceipt"] <- JsonValue.Create(flipped)
        File.WriteAllText(sidecar projectRoot protectedId, flippedNode.ToJsonString(jsonOptions))

        do! checkError "forgery.invalidReceipt" "task state integrity receipt is invalid" (getTask projectRoot protectedId)

        // A malformed receipt is rejected.
        let malformedNode = JsonNode.Parse(trustedText).AsObject()
        malformedNode["integrityReceipt"] <- JsonValue.Create("zz")
        File.WriteAllText(sidecar projectRoot protectedId, malformedNode.ToJsonString(jsonOptions))

        do!
            checkError
                "forgery.malformedReceipt"
                "task state integrity receipt is missing or malformed"
                (getTask projectRoot protectedId)

        // Removing the receipt from a User-claiming state is rejected.
        let strippedNode = JsonNode.Parse(trustedText).AsObject()
        strippedNode.Remove("integrityReceipt") |> ignore
        File.WriteAllText(sidecar projectRoot protectedId, strippedNode.ToJsonString(jsonOptions))

        do!
            checkError
                "forgery.missingReceipt"
                "task is missing property 'integrityReceipt'"
                (getTask projectRoot protectedId)

        File.WriteAllText(sidecar projectRoot protectedId, trustedText)

        // A forged User decision on a signed state is rejected, because the
        // added claim no longer matches the receipt the creation signed.
        let forgedId = nextTaskId()

        let! forgedFixture = createFixtureTask (Some "dual") forgedId "Forged fixture"

        let forgedText = File.ReadAllText(sidecar projectRoot forgedId)
        let forgedNode = JsonNode.Parse(forgedText).AsObject()

        let forgedDecision = JsonObject()

        forgedDecision["id"] <- JsonValue.Create("D1")
        forgedDecision["authority"] <- JsonValue.Create("user")
        forgedDecision["kind"] <- JsonValue.Create("waiverDecision")
        forgedDecision["targets"] <- JsonNode.Parse("[\"guardDisposition:G1\"]")
        forgedDecision["rationale"] <- JsonValue.Create("forged user confirmation")
        forgedDecision["createdAt"] <- JsonValue.Create("2026-01-01T00:00:00.0000000Z")
        forgedDecision["confirmationRef"] <- JsonValue.Create("")
        (forgedNode["decisions"].AsArray()).Add(forgedDecision)
        File.WriteAllText(sidecar projectRoot forgedId, forgedNode.ToJsonString(jsonOptions))

        do!
            checkError
                "forgery.unsignedUserClaimRejected"
                "task state integrity receipt is invalid"
                (getTask projectRoot forgedId)

        // The keyless fixture is created while the key is still usable, because
        // a canonical creation bootstraps the key and the malformed-key cases
        // below must never create one.
        let keylessId = nextTaskId()

        let! keylessFixture = createFixtureTask (Some "dual") keylessId "Keyless fixture"

        let keylessGuard = guardIdForKey "alpha" keylessFixture
        let keylessBefore = bytesOf(sidecar projectRoot keylessId)

        let keyBytes = File.ReadAllBytes keyPath

        try
            File.Delete keyPath

            do!
                checkError
                    "key.missingFailsClosed"
                    "could not verify task state integrity receipt"
                    (getTask projectRoot protectedId)

            check "key.missingNotBootstrapped" (not (File.Exists keyPath)) "a failed verification recreated the per-account integrity key"

            File.WriteAllBytes(keyPath, keyBytes)
            File.WriteAllBytes(keyPath, Array.init 5 (fun index -> byte index))

            do!
                checkError
                    "key.malformedFailsClosed"
                    "could not verify task state integrity receipt"
                    (getTask projectRoot protectedId)

            check "key.malformedNotRotated" (FileInfo(keyPath).Length = 5L) "the malformed per-account integrity key was rotated"

            // The trusted path refuses to continue while the key is unusable.
            do!
                checkError
                    "key.malformedTrustedApplyRejected"
                    "could not verify task state integrity receipt"
                    (applyTrustedTask
                        projectRoot
                        keylessId
                        0
                        (WaiveGuard(keylessGuard, None))
                        (fun (_: TrustedDecisionRequest) -> async { return Ok(Some "confirmation-ref") }))

            check
                "key.malformedTrustedApplyRejected.preservesBytes"
                (sameBytes keylessBefore (bytesOf(sidecar projectRoot keylessId)))
                "the rejected trusted apply changed the persisted bytes"
        finally
            CryptographicOperations.ZeroMemory keyBytes
    }
    // Standalone entry bridge: FSI owns the process entry for this isolated
    // child script; the parent suite owns the redirection and the aggregation.
    |> Async.RunSynchronously
with error ->
    fatal <- error.Message
    printfn "FATAL %s" error.Message

printfn "SUMMARY passed=%d failed=%d" passed failed

if failures.Count > 0 then
    for failure in failures do
        printfn "FAILURE %s" failure

if failed > 0 || fatal <> "" then
    raise (Exception(sprintf "trusted ingress suite failed: %d failed case(s), fatal=%s" failed fatal))
