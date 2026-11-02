// role: test entrypoint
// The receipt-bearing persistence cases, run only inside an isolated child process.
// The parent suite owns the redirection and aggregates the reported cases.

#load "../Receipt.fsx"
#load "../../domain/Computation.fs"
#load "../../domain/Integrity.fs"
#load "../../domain/Workflow.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Workflow

let fail message = raise (Exception message)

let isolatedRoot =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_ISOLATED_ROOT" with
    | value when not (String.IsNullOrWhiteSpace value) ->
        Path.GetFullPath value
    | _ ->
        fail
            "MCP_WORKFLOW_TEST_ISOLATED_ROOT is required; run this suite through integrity/Run.fsx so the per-account application data directory is redirected"

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

// A project profile with one User-waivable guard gives the suite a canonical
// trusted ingress path, so a User-authority decision is minted by the producer
// instead of being hand-written into the persisted state.
let profileDirectory = Path.Combine(projectRoot, ".workflow", "profiles")
Directory.CreateDirectory profileDirectory |> ignore

let userWaiverProfileJson =
    """{"schemaVersion":2,"id":"userwaiver","description":"user waiver profile","policy":{"guards":[{"key":"alpha","target":"task","checkpoint":"beforeComplete","evidenceKind":"test","minimumCount":1,"applicability":"explicitDecision:user","waiver":"waivableBy:user"}]}}"""

File.WriteAllText(Path.Combine(profileDirectory, "userwaiver.json"), userWaiverProfileJson)

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

// A rejected state can surface the missing receipt through either the strict
// property check or the receipt shape check, so both are accepted.
let checkErrorAny name (fragments: string list) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ ->
            check name false (sprintf "expected an error containing one of %A, got Ok" fragments)
        | Error error ->
            let message = renderError error

            if fragments |> List.exists (fun fragment -> message.Contains(fragment, StringComparison.Ordinal)) then
                check name true ""
            else
                check name false (sprintf "expected one of %A, got '%s'" fragments message)
    }

// A receipt-verified state that claims no User authority must stay readable:
// the read must not be rejected on User-authority grounds.
let checkNotRejectedAsUserAuthority name (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ ->
            check name true ""
        | Error error ->
            let message = renderError error

            if message.Contains("claims User authority", StringComparison.Ordinal) then
                check name false (sprintf "the read was rejected on User-authority grounds: %s" message)
            else
                check name false (sprintf "expected a successful read, got '%s'" message)
    }

let jsonOptions = JsonSerializerOptions(WriteIndented = true)

let canonical (text: string) : string =
    (JsonNode.Parse text).ToJsonString(jsonOptions)

// The receipt binds the canonical state without its receipt, so the payload is
// the persisted record with the receipt property removed.
let payloadOfSigned (text: string) =
    let node = JsonNode.Parse(text).AsObject()
    node.Remove("integrityReceipt") |> ignore
    node.ToJsonString(jsonOptions)

let receiptPrefix = "hmac-sha256-v1:"

let receiptPattern = Regex("^hmac-sha256-v1:[0-9a-f]{64}$", RegexOptions.CultureInvariant)

let receiptOfSigned (text: string) =
    let node = JsonNode.Parse(text).AsObject()
    (node["integrityReceipt"]).GetValue<string>()

// The expected receipt comes from the shared framing oracle, so this suite
// checks the producer's framing against an independent implementation.
let expectedReceipt key requestedId (canonicalPayloadJson: string) =
    WorkflowReceiptOracle.expectedReceipt
        key
        WorkflowReceiptOracle.platformName
        (WorkflowReceiptOracle.canonicalProjectRoot projectRoot)
        requestedId
        canonicalPayloadJson

let tamper (text: string) (mutate: JsonObject -> unit) =
    let node = JsonNode.Parse(text).AsObject()
    mutate node
    node.ToJsonString(jsonOptions)

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

let evidenceKind value =
    match parseEvidenceKind value with
    | Ok kind -> kind
    | Error error -> failwithf "invalid test evidence kind %A: %s" value (renderError error)

let makeEvidence id kind summary =
    { Id = id
      Kind = kind
      Source = EvidenceSource "tester"
      Subject = None
      ProducerRole = None
      ProducerId = None
      Reference = None
      Summary = summary }

let defaultHandoff: TerminalHandoff =
    { State = "Complete"
      EvidenceSummary = "Evidence recorded"
      Next = "No task work remains." }

let decisionDraft rationale =
    { Kind = DesignDecision
      Targets = [ SkipWorkItemTarget "W1" ]
      Rationale = rationale }

// F# parses an indexer applied to a parenthesized expression as a list
// application, so every indexed access below goes through a simple
// local binding.
let firstObject (parent: JsonObject) (name: string) =
    let child = parent[name]
    let array = child.AsArray()
    let element = array[0]
    element.AsObject()

let mutable fatal = ""
let mutable keyBuffer: byte array option = None
let mutable keyFileBytes: byte array option = None

let suite =
    async {
        let! ordinary = createTask projectRoot (createRequest "ORD-1" "Ordinary task")

        match ordinary with
        | Error error -> check "canonical.create" false (renderError error)
        | Ok _ -> check "canonical.create" true ""

        let creationText = File.ReadAllText(sidecar projectRoot "ORD-1")

        check
            "canonical.formFidelity"
            (canonical creationText = creationText)
            "canonical re-serialization differs from the persisted bytes"

        check
            "canonical.creationCarriesFramedReceipt"
            (receiptPattern.IsMatch(receiptOfSigned creationText))
            "the created task carries no framed receipt"

        check
            "canonical.creationBootstrappedKey"
            (File.Exists keyPath)
            "the canonical creation did not bootstrap the per-account integrity key"

        check
            "canonical.keyFileLength"
            (File.Exists keyPath && FileInfo(keyPath).Length = 32L)
            "the integrity key file is not 32 bytes"

        let key = WorkflowIntegrity.loadExistingKey ()
        keyBuffer <- Some key

        check
            "canonical.creationReceiptMatchesFrame"
            (expectedReceipt key "ORD-1" (payloadOfSigned creationText) = receiptOfSigned creationText)
            "the creation receipt does not match the canonical frame"

        check
            "canonical.receiptPayloadIsCanonical"
            (canonical (payloadOfSigned creationText) = payloadOfSigned creationText)
            "the receipt payload is not the canonical receipt-excluded state"

        let keyAgain = WorkflowIntegrity.loadOrCreateKey ()

        try
            check
                "key.stable"
                (CryptographicOperations.FixedTimeEquals(key, keyAgain))
                "loadOrCreateKey rotated the established key"
        finally
            CryptographicOperations.ZeroMemory keyAgain

        let keyLoaded = WorkflowIntegrity.loadExistingKey ()

        try
            check
                "key.loadExisting"
                (CryptographicOperations.FixedTimeEquals(key, keyLoaded))
                "loadExistingKey returned different key material"
        finally
            CryptographicOperations.ZeroMemory keyLoaded

        // An ordinary authenticated write continues the created state and
        // re-signs it with the same key that verified the prior state.
        let! applied = applyTask projectRoot "ORD-1" 0 (StartWorkItem "W1")

        check "ordinary.applyAccepted" (applied.IsOk) "an ordinary authenticated write failed"

        let revisionAfterStart =
            match applied with
            | Ok task -> task.StateRevision
            | Error _ -> 0

        let startedText = File.ReadAllText(sidecar projectRoot "ORD-1")

        check
            "ordinary.receiptRotated"
            (receiptOfSigned startedText <> receiptOfSigned creationText)
            "the receipt was not rotated on the authenticated write"

        check
            "ordinary.writeReceiptMatchesFrame"
            (expectedReceipt key "ORD-1" (payloadOfSigned startedText) = receiptOfSigned startedText)
            "the written receipt does not match the canonical frame"

        do!
            checkOk
                "ordinary.readable"
                (getTask projectRoot "ORD-1")
                (fun task -> task.StateRevision = revisionAfterStart)
                (fun task -> sprintf "expected revision %d, got %d" revisionAfterStart task.StateRevision)

        do! checkNotRejectedAsUserAuthority "ordinary.noUserRejection" (getTask projectRoot "ORD-1")

        do!
            checkOk
                "ordinary.validates"
                (validateTask projectRoot "ORD-1")
                (fun _ -> true)
                (fun task -> sprintf "unexpected validation failure for %s" task.Id)

        let! decided =
            applyTask projectRoot "ORD-1" revisionAfterStart (AddDecision (decisionDraft "record the design choice"))

        check "ordinary.continuedWriteAccepted" (decided.IsOk) "the continued authenticated write failed"

        let revisionAfterDecision =
            match decided with
            | Ok task -> task.StateRevision
            | Error _ -> revisionAfterStart

        let decisionText = File.ReadAllText(sidecar projectRoot "ORD-1")

        check
            "ordinary.continuedWriteRotatedReceipt"
            (receiptOfSigned decisionText <> receiptOfSigned startedText)
            "the continued write did not rotate the receipt"

        check
            "ordinary.continuedWriteReceiptMatchesFrame"
            (expectedReceipt key "ORD-1" (payloadOfSigned decisionText) = receiptOfSigned decisionText)
            "the continued write receipt does not match the canonical frame"

        // A later read verifies the persisted receipt and leaves the state alone.
        let beforeRestartRead = File.ReadAllText(sidecar projectRoot "ORD-1")

        do!
            checkOk
                "restart.readVerifiesReceipt"
                (getTask projectRoot "ORD-1")
                (fun task -> task.StateRevision = revisionAfterDecision)
                (fun task -> sprintf "expected revision %d, got %d" revisionAfterDecision task.StateRevision)

        check
            "restart.readDoesNotRewrite"
            (File.ReadAllText(sidecar projectRoot "ORD-1") = beforeRestartRead)
            "a verified read rewrote the persisted state"

        // A rejected write leaves the persisted receipt untouched.
        do!
            checkError
                "ordinary.rejectedWriteRejected"
                "state revision conflict"
                (applyTask projectRoot "ORD-1" (revisionAfterDecision + 5) (StartWorkItem "W1"))

        check
            "ordinary.rejectedWritePreservesReceipt"
            (File.ReadAllText(sidecar projectRoot "ORD-1") = beforeRestartRead)
            "a rejected write changed the persisted bytes"

        let tamperCase name fragment (mutate: JsonObject -> unit) =
            async {
                let tamperedText = tamper decisionText mutate
                File.WriteAllText(sidecar projectRoot "ORD-1", tamperedText)

                do! checkError name fragment (getTask projectRoot "ORD-1")
                File.WriteAllText(sidecar projectRoot "ORD-1", decisionText)
            }

        do!
            tamperCase
                "tamper.decisionRationale"
                "task state integrity receipt is invalid"
                (fun node ->
                    let decision = firstObject node "decisions"
                    decision["rationale"] <- JsonValue.Create("tampered rationale"))

        do!
            tamperCase
                "tamper.decisionTargets"
                "task state integrity receipt is invalid"
                (fun node ->
                    let decision = firstObject node "decisions"
                    decision["targets"] <- JsonNode.Parse("[\"skipWorkItem:W2\"]"))

        do!
            tamperCase
                "tamper.decisionAuthority"
                "task state integrity receipt is invalid"
                (fun node ->
                    let decision = firstObject node "decisions"
                    decision["authority"] <- JsonValue.Create("user"))

        do!
            tamperCase
                "tamper.acceptanceText"
                "task state integrity receipt is invalid"
                (fun node ->
                    let criterion = firstObject node "acceptanceCriteria"
                    criterion["text"] <- JsonValue.Create("tampered obligation"))

        do!
            tamperCase
                "tamper.workItemTitle"
                "task state integrity receipt is invalid"
                (fun node ->
                    let workItem = firstObject node "workItems"
                    workItem["title"] <- JsonValue.Create("tampered work item"))

        do!
            tamperCase
                "tamper.stateRevision"
                "task state integrity receipt is invalid"
                (fun node -> node["stateRevision"] <- JsonValue.Create(99))

        do!
            tamperCase
                "tamper.lifecycle"
                "task state integrity receipt is invalid"
                (fun node -> node["lifecycle"] <- JsonValue.Create("complete"))

        // A well-formed receipt with a wrong tag is invalid, not malformed.
        let invalidTagText =
            let node = JsonNode.Parse(decisionText).AsObject()
            let receipt = receiptOfSigned decisionText
            let tagStart = receiptPrefix.Length

            let flipped =
                receipt.Substring(0, tagStart)
                + (if receipt.[tagStart] = 'a' then "b" else "a")
                + receipt.Substring(tagStart + 1)

            node["integrityReceipt"] <- JsonValue.Create(flipped)
            node.ToJsonString(jsonOptions)

        File.WriteAllText(sidecar projectRoot "ORD-1", invalidTagText)

        do! checkError "receipt.invalidTag" "task state integrity receipt is invalid" (getTask projectRoot "ORD-1")

        // A receipt with an unsupported prefix is malformed.
        let brokenPrefixText =
            let node = JsonNode.Parse(decisionText).AsObject()
            node["integrityReceipt"] <- JsonValue.Create("hmac-sha256-v2:" + String('0', 64))
            node.ToJsonString(jsonOptions)

        File.WriteAllText(sidecar projectRoot "ORD-1", brokenPrefixText)

        do!
            checkError
                "receipt.brokenPrefix"
                "task state integrity receipt is missing or malformed"
                (getTask projectRoot "ORD-1")

        // A receipt whose tag is not hexadecimal is malformed.
        let brokenHexText =
            let node = JsonNode.Parse(decisionText).AsObject()
            node["integrityReceipt"] <- JsonValue.Create(receiptPrefix + String('z', 64))
            node.ToJsonString(jsonOptions)

        File.WriteAllText(sidecar projectRoot "ORD-1", brokenHexText)

        do!
            checkError
                "receipt.brokenHex"
                "task state integrity receipt is missing or malformed"
                (getTask projectRoot "ORD-1")

        // The receipt is mandatory, so a state without it is rejected.
        let missingPropertyText =
            let node = JsonNode.Parse(decisionText).AsObject()
            node.Remove("integrityReceipt") |> ignore
            node.ToJsonString(jsonOptions)

        File.WriteAllText(sidecar projectRoot "ORD-1", missingPropertyText)

        do!
            checkErrorAny
                "receipt.missingProperty"
                [ "task is missing property 'integrityReceipt'"
                  "task state integrity receipt is missing or malformed" ]
                (getTask projectRoot "ORD-1")

        File.WriteAllText(sidecar projectRoot "ORD-1", decisionText)

        // The trusted ingress path mints the User decision; the ordinary
        // caller in this suite never does.
        let! userTask =
            createTaskWithProfile projectRoot (Some "userwaiver") (createRequest "USR-1" "User provenance task")

        let userGuardId =
            match userTask with
            | Error error ->
                check "user.created" false (renderError error)
                None
            | Ok task ->
                check "user.created" true ""

                task.ProfileGuardKeys
                |> Map.tryFindKey (fun _ value -> value = "alpha")
                |> Option.orElseWith (fun () -> fail "the user-waiver profile has no guard keyed 'alpha'")

        match userGuardId with
        | None -> ()
        | Some guardId ->
            // The confirmation callback is a deterministic test double, so no
            // case here is evidence that a human approved the operation.
            let confirmation (_: TrustedDecisionRequest) =
                async { return Ok(Some "confirmation-ref-integrity") }

            let! trusted =
                applyTrustedTask projectRoot "USR-1" 0 (WaiveGuard(guardId, None)) confirmation

            check "user.trustedApplyAccepted" (trusted.IsOk) "the trusted apply did not mint a User decision"

            let userRevision =
                match trusted with
                | Ok task -> task.StateRevision
                | Error _ -> 0

            let userText = File.ReadAllText(sidecar projectRoot "USR-1")

            check
                "user.receiptMatchesFrame"
                (expectedReceipt key "USR-1" (payloadOfSigned userText) = receiptOfSigned userText)
                "the trusted receipt does not match the canonical frame"

            do!
                checkOk
                    "user.readAccepted"
                    (getTask projectRoot "USR-1")
                    (fun task ->
                        task.Decisions.Length = 1
                        && (task.Decisions |> List.exists (fun decision -> decision.Authority = User)))
                    (fun task -> sprintf "expected one User-authority decision, got %d decision(s)" task.Decisions.Length)

            // A User-claiming state without its receipt is rejected.
            let strippedUserText =
                let node = JsonNode.Parse(userText).AsObject()
                node.Remove("integrityReceipt") |> ignore
                node.ToJsonString(jsonOptions)

            File.WriteAllText(sidecar projectRoot "USR-1", strippedUserText)

            do!
                checkErrorAny
                    "user.strippedReceiptRejected"
                    [ "task is missing property 'integrityReceipt'"
                      "task state integrity receipt is missing or malformed" ]
                    (getTask projectRoot "USR-1")

            // A tampered User state is rejected.
            let tamperedUserText =
                tamper
                    userText
                    (fun node ->
                        let decision = firstObject node "decisions"
                        decision["rationale"] <- JsonValue.Create("tampered user rationale"))

            File.WriteAllText(sidecar projectRoot "USR-1", tamperedUserText)

            do! checkError "user.tamperRejected" "task state integrity receipt is invalid" (getTask projectRoot "USR-1")
            File.WriteAllText(sidecar projectRoot "USR-1", userText)

            // An ordinary write after the trusted apply re-signs the state.
            let! userApply =
                applyTask projectRoot "USR-1" userRevision (AddDecision (decisionDraft "record the follow-up choice"))

            check "user.continuedWriteAccepted" (userApply.IsOk) "the ordinary write after the trusted apply failed"

            let userResignedText = File.ReadAllText(sidecar projectRoot "USR-1")

            check
                "user.continuedWriteRotatedReceipt"
                (receiptOfSigned userResignedText <> receiptOfSigned userText)
                "the ordinary write did not rotate the receipt"

            check
                "user.continuedWriteReceiptMatchesFrame"
                (expectedReceipt key "USR-1" (payloadOfSigned userResignedText) = receiptOfSigned userResignedText)
                "the re-signed receipt does not match the canonical frame"

            do!
                checkOk
                    "user.resignedReadable"
                    (getTask projectRoot "USR-1")
                    (fun task ->
                        task.Decisions.Length = 2
                        && (task.Decisions |> List.exists (fun decision -> decision.Authority = User)))
                    (fun task -> sprintf "expected two decisions, got %d" task.Decisions.Length)

        let! _ = createTask projectRoot (createRequest "CLN-1" "Cleanup task")
        let! cleanupStarted = applyTask projectRoot "CLN-1" 0 (StartWorkItem "W1")

        let cleanupRevision =
            match cleanupStarted with
            | Ok task -> task.StateRevision
            | Error _ -> 0

        let! cleanupWork = applyTask projectRoot "CLN-1" cleanupRevision (CompleteWorkItem("W1", { Result = "work result"; EvidenceRefs = [] }))

        let cleanupRevision2 =
            match cleanupWork with
            | Ok task -> task.StateRevision
            | Error _ -> cleanupRevision

        let! cleanupEvidence = applyTask projectRoot "CLN-1" cleanupRevision2 (AddEvidence(makeEvidence "E1" (evidenceKind "build") "build passed"))

        let cleanupRevision3 =
            match cleanupEvidence with
            | Ok task -> task.StateRevision
            | Error _ -> cleanupRevision2

        let! cleanupVerified = applyTask projectRoot "CLN-1" cleanupRevision3 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))

        let cleanupRevision4 =
            match cleanupVerified with
            | Ok task -> task.StateRevision
            | Error _ -> cleanupRevision3

        let! cleanupCompleted = applyTask projectRoot "CLN-1" cleanupRevision4 (CompleteTask defaultHandoff)
        check "cleanup.completed" (cleanupCompleted.IsOk) "the cleanup fixture task did not reach the complete lifecycle"

        let cleanupText = File.ReadAllText(sidecar projectRoot "CLN-1")

        check
            "cleanup.receiptMatchesFrame"
            (expectedReceipt key "CLN-1" (payloadOfSigned cleanupText) = receiptOfSigned cleanupText)
            "the cleanup receipt does not match the canonical frame"

        do!
            checkOk
                "cleanup.accepted"
                (cleanTask projectRoot "CLN-1" CleanOperation.Cleanup)
                (fun result -> result.Operation = CleanOperation.Cleanup)
                (fun result -> sprintf "unexpected cleanup result %A" result.Operation)

        // A tampered receipt blocks cleanup before the task content is touched.
        let! _ = createTask projectRoot (createRequest "CLN-2" "Tampered cleanup task")
        let! cleanup2Started = applyTask projectRoot "CLN-2" 0 (StartWorkItem "W1")

        let cleanup2Revision =
            match cleanup2Started with
            | Ok task -> task.StateRevision
            | Error _ -> 0

        let! cleanup2Work = applyTask projectRoot "CLN-2" cleanup2Revision (CompleteWorkItem("W1", { Result = "work result"; EvidenceRefs = [] }))

        let cleanup2Revision2 =
            match cleanup2Work with
            | Ok task -> task.StateRevision
            | Error _ -> cleanup2Revision

        let! cleanup2Evidence = applyTask projectRoot "CLN-2" cleanup2Revision2 (AddEvidence(makeEvidence "E1" (evidenceKind "build") "build passed"))

        let cleanup2Revision3 =
            match cleanup2Evidence with
            | Ok task -> task.StateRevision
            | Error _ -> cleanup2Revision2

        let! cleanup2Verified = applyTask projectRoot "CLN-2" cleanup2Revision3 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))

        let cleanup2Revision4 =
            match cleanup2Verified with
            | Ok task -> task.StateRevision
            | Error _ -> cleanup2Revision3

        let! cleanup2Completed = applyTask projectRoot "CLN-2" cleanup2Revision4 (CompleteTask defaultHandoff)
        check "cleanup.tamperedCompleted" (cleanup2Completed.IsOk) "the tampered-cleanup fixture task did not reach the complete lifecycle"

        let cleanup2Text = File.ReadAllText(sidecar projectRoot "CLN-2")

        let cleanup2TamperedText =
            tamper
                cleanup2Text
                (fun node -> node["lifecycle"] <- JsonValue.Create("open"))

        File.WriteAllText(sidecar projectRoot "CLN-2", cleanup2TamperedText)

        let cleanup2Before = File.ReadAllBytes(sidecar projectRoot "CLN-2")

        do!
            checkError
                "cleanup.tamperRejected"
                "task state integrity receipt is invalid"
                (cleanTask projectRoot "CLN-2" CleanOperation.Cleanup)

        check "cleanup.taskPreserved" (File.Exists(sidecar projectRoot "CLN-2")) "a rejected cleanup removed the task sidecar"

        check
            "cleanup.tamperPreservesBytes"
            (File.ReadAllBytes(sidecar projectRoot "CLN-2").AsSpan().SequenceEqual(cleanup2Before.AsSpan()))
            "a rejected cleanup rewrote the persisted bytes"

        // Every fixture exists before the key is disturbed, because a canonical
        // creation needs the key itself.
        let establishedKeyBytes = File.ReadAllBytes keyPath
        keyFileBytes <- Some establishedKeyBytes

        File.Delete keyPath
        check "key.removed" (not (File.Exists keyPath)) "the isolated key file was not removed"

        do!
            checkError
                "key.missingFailsClosed"
                "could not verify task state integrity receipt"
                (getTask projectRoot "ORD-1")

        check "key.notBootstrappedByVerify" (not (File.Exists keyPath)) "a failed verification created the per-account integrity key"

        do!
            checkError
                "key.missingApplyFailsClosed"
                "could not verify task state integrity receipt"
                (applyTask projectRoot "ORD-1" revisionAfterDecision (AddDecision (decisionDraft "record another choice")))

        check "key.notBootstrappedByApply" (not (File.Exists keyPath)) "a failed continuation created the per-account integrity key"

        let mutable missingRaised = false

        try
            WorkflowIntegrity.loadExistingKey () |> ignore
        with
        | :? FileNotFoundException -> missingRaised <- true
        | _ -> ()

        check "key.missingNotCreated" (missingRaised && not (File.Exists keyPath)) "loadExistingKey created or returned a missing key"

        // A malformed key fails closed and is never rotated.
        File.WriteAllBytes(keyPath, Array.init 5 (fun index -> byte index))

        do!
            checkError
                "key.malformedFailsClosed"
                "could not verify task state integrity receipt"
                (getTask projectRoot "ORD-1")

        check "key.malformedNotRotated" (FileInfo(keyPath).Length = 5L) "the malformed per-account integrity key was rotated"

        File.WriteAllBytes(keyPath, establishedKeyBytes)

        do!
            checkOk
                "key.restored"
                (getTask projectRoot "ORD-1")
                (fun _ -> true)
                (fun task -> sprintf "unexpected read failure for %s" task.Id)
    }

try
    try
        // Standalone entry bridge: FSI owns the process entry for this isolated
        // child script; the parent suite owns the redirection and the aggregation.
        suite |> Async.RunSynchronously
    with error ->
        fatal <- error.Message
        printfn "FATAL %s" error.Message
finally
    // Key material never leaves this child and is zeroed before the process ends.
    match keyBuffer with
    | Some buffer -> CryptographicOperations.ZeroMemory buffer
    | None -> ()

    match keyFileBytes with
    | Some bytes -> CryptographicOperations.ZeroMemory bytes
    | None -> ()

printfn "SUMMARY passed=%d failed=%d" passed failed

if failures.Count > 0 then
    for failure in failures do
        printfn "FAILURE %s" failure

if failed > 0 || fatal <> "" then
    raise (Exception(sprintf "integrity receipt suite failed: %d failed case(s), fatal=%s" failed fatal))
