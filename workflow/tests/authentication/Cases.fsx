// role: test entrypoint
// Isolated-child deterministic coverage for the authenticated task boundary.
// The child refuses to start unless the resolved per-account application-data directory is exactly the isolated root, so the real per-account integrity key is never read, created, or rotated here.
// The supported entry point is authentication/Run.fsx, which owns the redirection and aggregates the reported cases.

#load "../../domain/Computation.fs"
#load "../../domain/Integrity.fs"
#load "../../domain/Workflow.fs"

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Workflow

let fail message = raise (Exception message)


let phase =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_PHASE" with
    | value when not (String.IsNullOrWhiteSpace value) -> value
    | _ ->
        fail
            "MCP_WORKFLOW_TEST_PHASE is required; run this suite through authentication/Run.fsx so the per-account application data directory is redirected"

let isolatedRoot =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_ISOLATED_ROOT" with
    | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
    | _ ->
        fail
            "MCP_WORKFLOW_TEST_ISOLATED_ROOT is required; run this suite through authentication/Run.fsx so the per-account application data directory is redirected"

let samePath (left: string) (right: string) =
    String.Equals(
        (Path.GetFullPath left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        (Path.GetFullPath right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase
    )

let resolvedAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

if String.IsNullOrWhiteSpace resolvedAppData then
    fail "per-account application data directory is unavailable"

let expectedAppData = Path.Combine(isolatedRoot, "appdata")

// The key API resolves this directory, so it must be the redirected root exactly, never the real per-account directory.
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

if phase = "main" && File.Exists keyPath then
    failwithf "the isolated key path already exists; every run needs a fresh isolated root: %s" keyPath

let projectRoot = Path.Combine(isolatedRoot, "project")
let otherProjectRoot = Path.Combine(isolatedRoot, "project-other")
Directory.CreateDirectory projectRoot |> ignore
Directory.CreateDirectory otherProjectRoot |> ignore

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

let describeException (error: exn) =
    sprintf "%s: %s" (error.GetType().FullName) error.Message

// An unhandled exception is reported as a case failure with its type, never as a pass.
let checkRejected name (fragments: string list) (work: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        try
            let! outcome = work

            match outcome with
            | Ok _ -> check name false "expected a rejection, got Ok"
            | Error error ->
                let message = renderError error

                if fragments |> List.exists (fun fragment -> message.Contains(fragment, StringComparison.Ordinal)) then
                    check name true ""
                else
                    check name false (sprintf "expected one of %A, got '%s'" fragments message)
        with error ->
            check name false (sprintf "the operation raised an unhandled %s" (describeException error))
    }

let checkAccepted
    name
    (work: Async<Result<'a, RuntimeError>>)
    (predicate: 'a -> bool)
    (detail: 'a -> string)
    : Async<unit> =
    async {
        try
            let! outcome = work

            match outcome with
            | Ok value ->
                if predicate value then
                    check name true ""
                else
                    check name false (detail value)
            | Error error -> check name false (sprintf "expected success, got '%s'" (renderError error))
        with error ->
            check name false (sprintf "the operation raised an unhandled %s" (describeException error))
    }


let jsonOptions = JsonSerializerOptions(WriteIndented = true)

let receiptPattern = Regex("^hmac-sha256-v1:[0-9a-f]{64}$", RegexOptions.CultureInvariant)

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

let bytesOf path = File.ReadAllBytes path

let sameBytes (left: byte array) (right: byte array) =
    left.AsSpan().SequenceEqual(right.AsSpan())

let textOf path = File.ReadAllText path

// The producer parses persisted state as raw UTF-8 bytes, so a fixture write must not emit the byte-order mark that `Encoding.UTF8` writes.
let utf8NoBom = UTF8Encoding(false)

let writeText (path: string) (text: string) = File.WriteAllText(path, text, utf8NoBom)

let nodeOf path = JsonNode.Parse(File.ReadAllText path).AsObject()

let writeNode path (node: JsonObject) = writeText path (node.ToJsonString(jsonOptions))

let receiptOf path =
    let node = nodeOf path

    match node["integrityReceipt"] with
    | null -> ""
    | value -> value.GetValue<string>()

let guardIdForKey key (task: TaskModel) =
    task.ProfileGuardKeys
    |> Map.tryFindKey (fun _ value -> value = key)
    |> Option.defaultWith (fun () -> failwithf "no guard is keyed '%s'" key)

let createFixture (profileId: string option) (id: string) (title: string) : Async<TaskModel> =
    async {
        let! outcome = createTaskWithProfile projectRoot profileId (createRequest id title)

        return
            match outcome with
            | Ok task -> task
            | Error error ->
                raise (Exception(sprintf "could not create the fixture task '%s': %s" id (renderError error)))
    }

// Two User-only guards give the suite two independent User-authority targets on one task.
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

let dualProfilePath = Path.Combine(projectRoot, ".workflow", "profiles", "dual.json")

let ensureDualProfile () =
    let directory = Path.Combine(projectRoot, ".workflow", "profiles")
    Directory.CreateDirectory directory |> ignore

    if not (File.Exists dualProfilePath) then
        writeText dualProfilePath (dualProfileJson "dual user-authority profile")

// A confirmation double stands in for the native host form and is never operator authorization; the trusted request type is internal, so every double is written inline.
let runMainPhase () : Async<unit> =
    async {
        ensureDualProfile ()


        let! routine = createFixture None "SCHEMA-100" "Routine apply fixture"
        let routinePath = sidecar projectRoot "SCHEMA-100"
        let routineNode = nodeOf routinePath

        check
            "create.schemaVersionIsThree"
            (routineNode["schemaVersion"].GetValue<int>() = 3)
            (sprintf "unexpected schemaVersion %A" (routineNode["schemaVersion"].GetValue<int>()))

        let creationReceipt = receiptOf routinePath

        check
            "create.framedReceiptEmitted"
            (receiptPattern.IsMatch creationReceipt)
            (sprintf "unexpected integrity receipt '%s'" creationReceipt)

        do!
            checkAccepted
                "create.getReadsSignedTask"
                (getTask projectRoot "SCHEMA-100")
                (fun task -> task.Id = "SCHEMA-100" && task.StateRevision = 0 && task.Decisions.IsEmpty)
                (fun task -> sprintf "unexpected task state: revision %d, %d decisions" task.StateRevision task.Decisions.Length)

        do!
            checkAccepted
                "create.statusProjectionReadsSignedTask"
                (projectTask projectRoot "SCHEMA-100" false 0 25)
                (fun node -> node["taskId"].GetValue<string>() = "SCHEMA-100")
                (fun node -> sprintf "unexpected status projection: %s" (node.ToJsonString()))

        do!
            checkAccepted
                "create.readyProjectionReadsSignedTask"
                (projectTask projectRoot "SCHEMA-100" true 0 25)
                (fun node -> node["taskId"].GetValue<string>() = "SCHEMA-100")
                (fun node -> sprintf "unexpected ready projection: %s" (node.ToJsonString()))

        do!
            checkAccepted
                "create.validateReadsSignedTask"
                (validateTask projectRoot "SCHEMA-100")
                (fun task -> task.Id = "SCHEMA-100")
                (fun task -> sprintf "unexpected task state for %s" task.Id)

        check
            "create.keyBootstrappedInIsolatedRoot"
            (File.Exists keyPath)
            "the isolated key file was not created by the canonical creation"

        do!
            checkAccepted
                "apply.ordinaryIsPromptFree"
                (applyTask projectRoot "SCHEMA-100" 0 (StartWorkItem "W1"))
                (fun task -> task.StateRevision = 1 && task.Decisions.IsEmpty)
                (fun task ->
                    sprintf "unexpected task state: revision %d, %d decisions" task.StateRevision task.Decisions.Length)

        let appliedReceipt = receiptOf routinePath

        check
            "apply.continuationIsResigned"
            (receiptPattern.IsMatch appliedReceipt && appliedReceipt <> creationReceipt)
            "the ordinary apply did not produce a new, well-formed receipt"

        do!
            checkAccepted
                "apply.authenticatedRestart"
                (getTask projectRoot "SCHEMA-100")
                (fun task -> task.StateRevision = 1 && task.WorkItems.Head.State = ActiveWork)
                (fun task -> sprintf "unexpected task state after restart: revision %d" task.StateRevision)

        // A changed title invalidates the receipt that signed the old payload.
        let payloadOriginal = textOf routinePath
        let payloadNode = nodeOf routinePath
        payloadNode["title"] <- JsonValue.Create("altered payload")
        writeNode routinePath payloadNode
        let payloadTamperedBytes = bytesOf routinePath

        do!
            checkRejected
                "apply.receiptBindsPayload"
                [ "integrity receipt" ]
                (getTask projectRoot "SCHEMA-100")

        check
            "apply.receiptBindsPayload.noRewrite"
            (sameBytes payloadTamperedBytes (bytesOf routinePath))
            "the rejected read rewrote the tampered task state"

        writeText routinePath payloadOriginal

        do!
            checkAccepted
                "apply.payloadRestoredStateIsReadable"
                (getTask projectRoot "SCHEMA-100")
                (fun task -> task.StateRevision = 1)
                (fun task -> sprintf "unexpected task state after restore: revision %d" task.StateRevision)


        let! waiverFixture = createFixture (Some "dual") "SCHEMA-102" "Repeated waiver fixture"
        let waiverPath = sidecar projectRoot "SCHEMA-102"
        let alphaGuard = guardIdForKey "alpha" waiverFixture
        let betaGuard = guardIdForKey "beta" waiverFixture

        // The attempt must happen while the guard is still applicable: a disposed guard is refused as an invalid transition rather than a missing User authority, and the refused apply leaves the state at revision 0.
        let! ordinaryWaiver =
            applyTask projectRoot "SCHEMA-102" 0 (WaiveGuard(betaGuard, None))

        let ordinaryWaiverDenied =
            match ordinaryWaiver with
            | Error error ->
                match tryAuthorityMetadata error with
                | Some metadata ->
                    metadata.RequiredAuthority = MinimumAuthority.UserAuthority
                    && metadata.Operation = "task_apply.waive-guard"
                    && metadata.DecisionKind = Some WaiverDecision
                    && metadata.Target = Some(GuardDispositionTarget betaGuard)
                | None -> false
            | Ok _ -> false

        check
            "authority.ordinaryWaiverDenied"
            ordinaryWaiverDenied
            "an ordinary caller could waive a User-only guard"

        let! firstWaiver =
            applyTrustedTask
                projectRoot
                "SCHEMA-102"
                0
                (WaiveGuard(alphaGuard, None))
                (fun (_: TrustedDecisionRequest) -> async { return Ok(Some "confirmation-alpha") })

        check
            "waiver.firstConfirmedSucceeds"
            (match firstWaiver with
             | Ok task ->
                 task.StateRevision = 1
                 && task.Decisions.Length = 1
                 && task.Decisions.Head.Authority = User
             | Error _ -> false)
            "the first confirmed User waiver did not add exactly one User decision and one revision"

        let! secondWaiver =
            applyTrustedTask
                projectRoot
                "SCHEMA-102"
                1
                (WaiveGuard(betaGuard, None))
                (fun (_: TrustedDecisionRequest) -> async { return Ok(Some "confirmation-beta") })

        check
            "waiver.secondConfirmedSucceeds"
            (match secondWaiver with
             | Ok task ->
                 task.StateRevision = 2
                 && task.Decisions.Length = 2
                 && task.Decisions |> List.forall (fun decision -> decision.Authority = User)
             | Error _ -> false)
            "the second confirmed User waiver did not add exactly one more User decision and one revision"

        do!
            checkAccepted
                "waiver.stateReloadsFromDisk"
                (getTask projectRoot "SCHEMA-102")
                (fun task ->
                    task.StateRevision = 2
                    && task.Decisions.Length = 2
                    && task.Decisions |> List.forall (fun decision -> decision.Authority = User))
                (fun task ->
                    sprintf "unexpected reloaded state: revision %d, %d decisions" task.StateRevision task.Decisions.Length)

        do!
            checkAccepted
                "waiver.validationAcceptsConfirmedState"
                (validateTask projectRoot "SCHEMA-102")
                (fun task -> task.StateRevision = 2)
                (fun task -> sprintf "unexpected validated state: revision %d" task.StateRevision)

        // The ordinary caller still cannot mint User authority.
        let! ordinaryDecision =
            applyTask
                projectRoot
                "SCHEMA-102"
                2
                (AddDecision
                    { Kind = UserDecision
                      Targets = [ GuardDispositionTarget alphaGuard ]
                      Rationale = "the model claims to be the user" })

        check
            "authority.ordinaryAddDecisionIsCoordinator"
            (match ordinaryDecision with
             | Ok task ->
                 task.Decisions.Length = 3
                 && (List.last task.Decisions).Authority = Coordinator
                 && (List.last task.Decisions).ConfirmationRef = None
             | Error _ -> false)
            "an ordinary add-decision minted User authority"


        let tamperTask = "SCHEMA-101"
        let! _ = createFixture None tamperTask "Tamper fixture"
        let tamperPath = sidecar projectRoot tamperTask
        let tamperOriginal = textOf tamperPath

        let runTamperCase name (mutate: JsonObject -> unit) (fragments: string list) : Async<unit> =
            async {
                let node = nodeOf tamperPath
                mutate node
                writeNode tamperPath node
                let tamperedBytes = bytesOf tamperPath

                do! checkRejected name fragments (getTask projectRoot tamperTask)

                check
                    (name + ".noRewrite")
                    (sameBytes tamperedBytes (bytesOf tamperPath))
                    "the rejected read rewrote the tampered task state"

                writeText tamperPath tamperOriginal
            }

        // The strict required-property validator rejects the removed receipt before the receipt-specific diagnostic, so either wording is accepted.
        do!
            runTamperCase
                "tamper.missingReceiptRejected"
                (fun node -> node.Remove("integrityReceipt") |> ignore)
                [ "integrity receipt"; "missing property 'integrityReceipt'" ]

        do!
            runTamperCase
                "tamper.nullReceiptRejected"
                (fun node -> node["integrityReceipt"] <- JsonNode.Parse("null"))
                [ "integrity receipt"; "property 'integrityReceipt' must be a string" ]

        do!
            runTamperCase
                "tamper.emptyReceiptRejected"
                (fun node -> node["integrityReceipt"] <- JsonValue.Create(""))
                [ "integrity receipt" ]

        do!
            runTamperCase
                "tamper.unknownReceiptFormatRejected"
                (fun node -> node["integrityReceipt"] <- JsonValue.Create("hmac-sha256-v2:" + String('0', 64)))
                [ "integrity receipt" ]

        do!
            runTamperCase
                "tamper.alteredReceiptRejected"
                (fun node ->
                    let receipt = node["integrityReceipt"].GetValue<string>()
                    let tag = receipt.Substring("hmac-sha256-v1:".Length)
                    let last = tag.Substring(tag.Length - 1)
                    let replacement = if last = "0" then "1" else "0"

                    node["integrityReceipt"] <-
                        JsonValue.Create("hmac-sha256-v1:" + tag.Substring(0, tag.Length - 1) + replacement))
                [ "integrity receipt" ]

        do!
            runTamperCase
                "tamper.fabricatedUnsignedSchema3Rejected"
                (fun node ->
                    node.Remove("integrityReceipt") |> ignore
                    node["title"] <- JsonValue.Create("fabricated unsigned state")
                    node["stateRevision"] <- JsonValue.Create(7))
                [ "integrity receipt"; "property 'integrityReceipt' must be a string"; "missing property 'integrityReceipt'" ]

        // Stripping every User provenance claim from an authenticated task is rejected without a rewrite.
        let waiverOriginal = textOf waiverPath
        let waiverNode = nodeOf waiverPath
        waiverNode["decisions"] <- JsonArray()
        writeNode waiverPath waiverNode
        let strippedBytes = bytesOf waiverPath

        do!
            checkRejected
                "tamper.strippedUserProvenanceRejected"
                [ "integrity receipt" ]
                (getTask projectRoot "SCHEMA-102")

        check
            "tamper.strippedUserProvenanceRejected.noRewrite"
            (sameBytes strippedBytes (bytesOf waiverPath))
            "the rejected read rewrote the stripped task state"

        writeText waiverPath waiverOriginal

        // An unsupported task schema version is never rewritten or adopted.
        let schema2Node = nodeOf tamperPath
        schema2Node["schemaVersion"] <- JsonValue.Create(2)
        schema2Node.Remove("integrityReceipt") |> ignore
        writeNode tamperPath schema2Node
        let schema2Bytes = bytesOf tamperPath

        do!
            checkRejected
                "tamper.schema2Rejected"
                [ "unsupported schemaVersion 2" ]
                (getTask projectRoot tamperTask)

        check
            "tamper.schema2Rejected.noRewrite"
            (sameBytes schema2Bytes (bytesOf tamperPath))
            "the rejected read rewrote the schema2 task state"

        writeText tamperPath tamperOriginal

        do!
            checkAccepted
                "tamper.oldDataUntouched"
                (getTask projectRoot tamperTask)
                (fun task -> task.Id = tamperTask && task.StateRevision = 0)
                (fun task -> sprintf "unexpected task state after the tamper sequence: revision %d" task.StateRevision)

        do!
            checkAccepted
                "tamper.confirmedStateUntouched"
                (getTask projectRoot "SCHEMA-102")
                (fun task -> task.StateRevision = 3 && task.Decisions.Length = 3)
                (fun task ->
                    sprintf "unexpected task state after the tamper sequence: revision %d, %d decisions" task.StateRevision task.Decisions.Length)


        let identityTask = "SCHEMA-104"
        let! _ = createFixture None identityTask "Identity fixture"
        let identityPath = sidecar projectRoot identityTask

        // A signed task copied to a different task id in the same root.
        let copiedId = "SCHEMA-105"
        let copiedDirectory = Path.Combine(projectRoot, ".tasks", copiedId)
        Directory.CreateDirectory copiedDirectory |> ignore
        let copiedPath = Path.Combine(copiedDirectory, "runtime.json")
        File.Copy(identityPath, copiedPath, true)

        do!
            checkRejected
                "identity.differentIdSameRootRejected"
                [ "runtime task identity does not match requested task"; "integrity receipt" ]
                (getTask projectRoot copiedId)

        check
            "identity.differentIdSameRootRejected.noRewrite"
            (sameBytes (bytesOf identityPath) (bytesOf copiedPath))
            "the rejected read rewrote the copied task state"

        do!
            checkRejected
                "identity.differentIdSameRootApplyRejected"
                [ "runtime task identity does not match requested task"; "integrity receipt" ]
                (applyTask projectRoot copiedId 0 (StartWorkItem "W1"))

        do!
            checkRejected
                "identity.differentIdSameRootCleanRejected"
                [ "runtime task identity does not match requested task"; "integrity receipt" ]
                (cleanTask projectRoot copiedId CleanOperation.Cleanup)

        // The same task id copied into a different project root.
        let otherDirectory = Path.Combine(otherProjectRoot, ".tasks", identityTask)
        Directory.CreateDirectory otherDirectory |> ignore
        let otherPath = Path.Combine(otherDirectory, "runtime.json")
        File.Copy(identityPath, otherPath, true)
        let otherBytes = bytesOf otherPath

        do!
            checkRejected
                "identity.sameIdDifferentRootRejected"
                [ "integrity receipt" ]
                (getTask otherProjectRoot identityTask)

        check
            "identity.sameIdDifferentRootRejected.noRewrite"
            (sameBytes otherBytes (bytesOf otherPath))
            "the rejected read rewrote the transplanted task state"

        do!
            checkRejected
                "identity.sameIdDifferentRootApplyRejected"
                [ "integrity receipt" ]
                (applyTask otherProjectRoot identityTask 0 (StartWorkItem "W1"))

        do!
            checkRejected
                "identity.sameIdDifferentRootCleanRejected"
                [ "integrity receipt" ]
                (cleanTask otherProjectRoot identityTask CleanOperation.Cleanup)

        check
            "identity.sameIdDifferentRootCleanRejected.stateKept"
            (File.Exists otherPath)
            "the rejected clean removed the transplanted task state"

        // The canonical root is case-insensitive on Windows, so the same task still verifies through the case-folded spelling of the same root.
        if OperatingSystem.IsWindows() then
            let variantRoot = projectRoot.ToUpperInvariant()

            do!
                checkAccepted
                    "identity.canonicalRootNormalizationAccepted"
                    (getTask variantRoot "SCHEMA-104")
                    (fun task -> task.Id = identityTask)
                    (fun task -> sprintf "unexpected task state for %s" task.Id)
        else
            check
                "identity.canonicalRootNormalizationAccepted"
                true
                "skipped: the canonical root is ordinal-preserved on this platform"

        // The receipt payload normalizes a trailing separator away, and the Windows directory-boundary guard compares the opened handle's final path with the requested root, so the requested spelling has to be canonicalized the same way before the two are compared.
        if OperatingSystem.IsWindows() then
            let trailingRoot = projectRoot + string Path.DirectorySeparatorChar
            let trailingBytes = bytesOf identityPath

            do!
                checkAccepted
                    "identity.canonicalRootTrailingSeparatorAccepted"
                    (getTask trailingRoot identityTask)
                    (fun task -> task.Id = identityTask)
                    (fun task -> sprintf "unexpected task state for %s" task.Id)

            check
                "identity.canonicalRootTrailingSeparatorAccepted.noRewrite"
                (sameBytes trailingBytes (bytesOf identityPath))
                "the accepted trailing-separator read rewrote the task state"
        else
            check
                "identity.canonicalRootTrailingSeparatorAccepted"
                true
                "skipped: the directory-handle boundary comparison is Windows-only"


        let! snapshotFixture = createFixture (Some "dual") "SCHEMA-106" "Snapshot fixture"
        let snapshotPath = sidecar projectRoot "SCHEMA-106"
        let snapshotGuard = guardIdForKey "alpha" snapshotFixture
        let snapshotBefore = bytesOf snapshotPath

        // A denied confirmation must leave the bytes, the revision, and the Decision count untouched.
        do!
            checkRejected
                "snapshot.deniedConfirmationRejected"
                [ "trusted User confirmation was rejected or cancelled" ]
                (applyTrustedTask
                    projectRoot
                    "SCHEMA-106"
                    0
                    (WaiveGuard(snapshotGuard, None))
                    (fun (_: TrustedDecisionRequest) -> async { return Ok None }))

        check
            "snapshot.deniedConfirmationRejected.preservesBytes"
            (sameBytes snapshotBefore (bytesOf snapshotPath))
            "the denied trusted apply changed the persisted bytes"

        let! snapshotState = getTask projectRoot "SCHEMA-106"

        check
            "snapshot.deniedConfirmationRejected.preservesRevisionAndDecisions"
            (match snapshotState with
             | Ok task -> task.StateRevision = 0 && task.Decisions.IsEmpty
             | Error _ -> false)
            "the denied trusted apply changed the revision or the Decision count"

        // The original bytes changed while the confirmation was pending.
        let mutable alteredWrittenBytes : byte array option = None

        let alteredCallback (_: TrustedDecisionRequest) =
            async {
                let node = nodeOf snapshotPath
                node["title"] <- JsonValue.Create("altered during confirmation")
                writeNode snapshotPath node
                alteredWrittenBytes <- Some (bytesOf snapshotPath)
                return Ok(Some "confirmation-ref")
            }

        do!
            checkRejected
                "snapshot.changedOriginalBytesRejected"
                [ "task state changed during trusted confirmation" ]
                (applyTrustedTask projectRoot "SCHEMA-106" 0 (WaiveGuard(snapshotGuard, None)) alteredCallback)

        // The altered state is unauthentic, so the rejection is asserted on the persisted content rather than on a successful authenticated read: an authentication failure here is the expected outcome of the tamper, not a test-infrastructure failure.
        let alteredBytesAfterRejection = bytesOf snapshotPath

        let alteredContentSummary =
            try
                let node = nodeOf snapshotPath

                let decisionCount =
                    match node["decisions"] with
                    | null -> -1
                    | value -> value.AsArray().Count

                let revision =
                    match node["stateRevision"] with
                    | null -> -1
                    | value -> value.GetValue<int>()

                Some(decisionCount, revision)
            with _ ->
                None

        check
            "snapshot.changedOriginalBytesRejected.noUserDecision"
            (match alteredWrittenBytes, alteredContentSummary with
             | Some written, Some(decisionCount, revision) ->
                 sameBytes written alteredBytesAfterRejection && decisionCount = 0 && revision = 0
             | _ -> false)
            "the rejected trusted apply persisted a User decision or advanced the revision"

        // The authenticated bytes are restored so the remaining snapshot cases still read a valid state.
        File.WriteAllBytes(snapshotPath, snapshotBefore)

        // The effective profile context changed while the confirmation was pending.
        let profileBefore = textOf dualProfilePath

        let profileCallback (_: TrustedDecisionRequest) =
            async {
                writeText dualProfilePath (dualProfileJson "dual user-authority profile (altered)")
                return Ok(Some "confirmation-ref")
            }

        do!
            checkRejected
                "snapshot.changedProfileContextRejected"
                [ "effective profile context changed during trusted confirmation" ]
                (applyTrustedTask projectRoot "SCHEMA-106" 0 (WaiveGuard(snapshotGuard, None)) profileCallback)

        writeText dualProfilePath profileBefore

        let! profileState = getTask projectRoot "SCHEMA-106"

        check
            "snapshot.changedProfileContextRejected.preservesState"
            (match profileState with
             | Ok task -> task.StateRevision = 0 && task.Decisions.IsEmpty
             | Error _ -> false)
            "the rejected trusted apply changed the revision or the Decision count"

        // A routine stale apply is rejected and persists nothing.
        let routineBefore = bytesOf routinePath

        do!
            checkRejected
                "snapshot.staleRoutineApplyRejected"
                [ "state revision conflict" ]
                (applyTask projectRoot "SCHEMA-100" 99 (StartWorkItem "W1"))

        check
            "snapshot.staleRoutineApplyRejected.preservesBytes"
            (sameBytes routineBefore (bytesOf routinePath))
            "the rejected routine apply changed the persisted bytes"
    }


let runRestartPhase () : Async<unit> =
    async {
        // The confirmed User waivers survive the process boundary: the state is still authenticated by the same key and still carries both User decisions.
        do!
            checkAccepted
                "restart.confirmedStateSurvived"
                (getTask projectRoot "SCHEMA-102")
                (fun task ->
                    task.StateRevision = 3
                    && task.Decisions.Length = 3
                    && task.Decisions |> List.filter (fun decision -> decision.Authority = User) |> List.length = 2)
                (fun task ->
                    sprintf "unexpected reloaded state: revision %d, %d decisions" task.StateRevision task.Decisions.Length)

        check
            "restart.keyStillPresent"
            (File.Exists keyPath)
            "the isolated key file disappeared across the restart"

        // A rotated key would have invalidated the older receipt, so this proves key continuity without reading key material.
        let! _ = createFixture None "SCHEMA-107" "Post restart fixture"

        do!
            checkAccepted
                "restart.newTaskUsesTheSameKey"
                (getTask projectRoot "SCHEMA-107")
                (fun task -> task.Id = "SCHEMA-107" && task.StateRevision = 0)
                (fun task -> sprintf "unexpected task state: revision %d" task.StateRevision)

        do!
            checkAccepted
                "restart.olderTaskStillVerifies"
                (getTask projectRoot "SCHEMA-102")
                (fun task -> task.StateRevision = 3)
                (fun task -> sprintf "unexpected task state: revision %d" task.StateRevision)
    }


let runKeyPhase () : Async<unit> =
    async {
        ensureDualProfile ()

        let keyTask = "SCHEMA-200"
        let! _ = createFixture None keyTask "Key lifecycle fixture"
        let keyTaskPath = sidecar projectRoot keyTask
        let keyTaskBytes = bytesOf keyTaskPath

        check
            "key.createBootstrapsTheKey"
            (File.Exists keyPath)
            "the canonical creation did not bootstrap the isolated key"

        do!
            checkRejected
                "key.createCollisionRejected"
                [ "task directory contains runtime state"; "task directory is already occupied" ]
                (createTask projectRoot (createRequest keyTask "Key lifecycle fixture"))

        check
            "key.createCollisionRejected.noOverwrite"
            (sameBytes keyTaskBytes (bytesOf keyTaskPath))
            "the rejected creation overwrote the existing task state"

        // A missing key never bootstraps a directory or a key, and never turns a persisted receipt into authority.
        if Directory.Exists keyDirectory then
            Directory.Delete(keyDirectory, true)

        do!
            checkRejected
                "key.missingKeyDoesNotBootstrap"
                [ "integrity key" ]
                (getTask projectRoot keyTask)

        check
            "key.missingKeyDoesNotBootstrap.noKeyDirectory"
            (not (Directory.Exists keyDirectory))
            "the rejected read bootstrapped the key directory"

        check
            "key.missingKeyDoesNotBootstrap.noKeyFile"
            (not (File.Exists keyPath))
            "the rejected read bootstrapped the key file"

        check
            "key.missingKeyDoesNotBootstrap.noRewrite"
            (sameBytes keyTaskBytes (bytesOf keyTaskPath))
            "the rejected read rewrote the task state"

        do!
            checkRejected
                "key.missingKeyRejectsApply"
                [ "integrity key" ]
                (applyTask projectRoot keyTask 0 (StartWorkItem "W1"))

        // A malformed existing key fails closed and is never rotated.
        Directory.CreateDirectory keyDirectory |> ignore
        let malformed = Array.init 17 (fun index -> byte (index + 1))
        File.WriteAllBytes(keyPath, malformed)

        do!
            checkRejected
                "key.malformedKeyRejected"
                [ "integrity key" ]
                (getTask projectRoot keyTask)

        check
            "key.malformedKeyRejected.notRotated"
            (sameBytes malformed (File.ReadAllBytes keyPath))
            "the rejected read rotated the malformed key"

        do!
            checkRejected
                "key.malformedKeyRejectsCreate"
                [ "integrity key" ]
                (createTask projectRoot (createRequest "SCHEMA-201" "Malformed key fixture"))

        check
            "key.malformedKeyRejectsCreate.notRotated"
            (sameBytes malformed (File.ReadAllBytes keyPath))
            "the rejected creation rotated the malformed key"
    }


let run () : Async<unit> =
    async {
        match phase with
        | "main" -> do! runMainPhase ()
        | "restart" -> do! runRestartPhase ()
        | "key" -> do! runKeyPhase ()
        | other -> failwithf "unknown phase '%s'" other
    }

// Standalone entry bridge: FSI needs one synchronous script entry.
try
    run () |> Async.RunSynchronously
with error ->
    printfn "FIXTURE-ERROR %s" (describeException error)
    printfn "SUMMARY passed=%d failed=%d" passed failed
    raise error

printfn "SUMMARY passed=%d failed=%d" passed failed

if failed > 0 then
    raise (Exception(sprintf "%d case(s) failed: %s" failed (String.concat "; " (failures |> Seq.toList))))
