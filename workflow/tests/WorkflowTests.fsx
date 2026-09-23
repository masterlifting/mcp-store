// Deterministic coverage for the strict schema-v1 Workflow: general + execution,
// general + research, typed Evidence, evidence-backed Acceptance-Criterion
// verification, supersession invalidation, and the recursive Work Tree
// (dotted IDs, dependencies/readiness, ancestor activation, child-gated parent
// completion, and Wait/Block/Resume). Uses the repository's plain FSI test
// harness rather than Expecto because the solution contract forbids adding a
// project/package system. All fixtures live under one fresh GUID temp root and
// only that root is removed.

#load "../ComputationExpressions.fs"
#load "../Workflow.fs"

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Workflow

let assertEqual name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let expectOk name result =
    match result with
    | Ok value -> value
    | Error error -> failwithf "%s: expected Ok, got Error %s" name (renderError error)

let expectRejected (name: string) (fragment: string) result =
    match result with
    | Ok _ -> failwithf "%s: expected rejection, got Ok" name
    | Error error ->
        let message = renderError error

        if not (message.Contains(fragment, StringComparison.Ordinal)) then
            failwithf "%s: expected '%s', got '%s'" name fragment message

let expectRejectedDeserialize (name: string) (fragment: string) json =
    expectRejected name fragment (deserialize json)

let expectDecideRejected (name: string) (fragment: string) (result: Result<'a, RuntimeError list>) =
    match result with
    | Ok _ -> failwithf "%s: expected decide rejection, got Ok" name
    | Error errors ->
        let message = errors |> List.map renderError |> String.concat "; "

        if not (message.Contains(fragment, StringComparison.Ordinal)) then
            failwithf "%s: expected '%s', got '%s'" name fragment message

let spec id title =
    { Id = id
      Title = title
      DependsOn = []
      Children = [] }

let specWith id title dependsOn children =
    { Id = id
      Title = title
      DependsOn = dependsOn
      Children = children }

let createRequest id title =
    { Id = id
      Title = title
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "Execution completes" ]
      WorkItems = [ spec "W1" "Do the work" ] }

let existingCreateRejectedMessage = SidecarFileName

let makeEvidence id kind summary =
    { Id = id
      Kind = kind
      Source = EvidenceSource "tester"
      Subject = None
      ProducerRole = None
      ProducerId = None
      Reference = None
      Summary = summary }

let handoff state evidenceSummary next : TerminalHandoff =
    { State = state
      EvidenceSummary = evidenceSummary
      Next = next }

let defaultHandoff =
    handoff "Complete" "Evidence recorded" "No task work remains."

let owner role agentId : Owner =
    { Role = role
      AgentId = agentId }

let sidecarPath root id =
    Path.Combine(root, ".tasks", id, "runtime.json")

let readPersisted root id =
    File.ReadAllText(sidecarPath root id) |> deserialize

let tempRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"taskruntime-tests-{Guid.NewGuid():N}")

Directory.CreateDirectory tempRoot |> ignore

// decide requires the resolved profile registry; tempRoot carries no project
// profiles, so this is the compiled-in builtin set.
let profiles = expectOk "resolve builtin profiles" (resolveProfiles tempRoot)

try
    // --- Scenario 1: end-to-end Create -> Get -> Start -> Validate -----------
    let created = expectOk "create TST-1" (createTask tempRoot (createRequest "TST-1" "Skeleton task"))
    assertEqual "created state revision" 0 created.StateRevision
    assertEqual "created contract revision" 1 created.ContractRevision
    assertEqual "created lifecycle" "open" created.Lifecycle
    assertEqual "created kind" Execution created.Kind
    assertEqual "created profile" "general" created.Profile
    assertEqual "created fingerprint" profiles.[GeneralProfileId].Fingerprint created.ProfileFingerprint
    assertTrue "created fingerprint is content-derived" (created.ProfileFingerprint <> "general-v1")
    assertEqual "created evidence empty" [] created.Evidence
    assertEqual "created AC count" 1 created.AcceptanceCriteria.Length
    assertEqual "created AC pending" Pending created.AcceptanceCriteria.Head.State
    assertEqual "created work item pending" PendingWork created.WorkItems.Head.State
    assertEqual "created work item links AC" [ "AC1" ] created.WorkItems.Head.AcceptanceRefs

    let fetched = expectOk "get TST-1" (getTask tempRoot "TST-1")
    assertEqual "get returns revision" 0 fetched.StateRevision
    assertEqual "get returns work state" PendingWork fetched.WorkItems.Head.State

    let started = expectOk "start W1" (applyTask tempRoot "TST-1" 0 (StartWorkItem "W1"))
    assertEqual "start increments revision" 1 started.StateRevision
    assertEqual "start activates work" ActiveWork started.WorkItems.Head.State

    let validated = expectOk "validate TST-1" (validateTask tempRoot "TST-1")
    assertEqual "validate sees revision" 1 validated.StateRevision
    assertEqual "validate sees active work" ActiveWork validated.WorkItems.Head.State

    // Completion is mechanically gated: work first, then evidence, then task.
    expectRejected
        "complete before work done"
        "all WorkItems must be done before task completion"
        (applyTask tempRoot "TST-1" 1 (CompleteTask defaultHandoff))

    let workDone =
        expectOk "complete W1" (applyTask tempRoot "TST-1" 1 (CompleteWorkItem("W1", { Result = "work result"; EvidenceRefs = [] })))

    assertEqual "complete work increments revision" 2 workDone.StateRevision
    assertEqual "complete work marks done" DoneWork workDone.WorkItems.Head.State
    assertEqual "complete work records result" (Some "work result") workDone.WorkItems.Head.Result

    expectRejected
        "complete before acceptance verified"
        "all Acceptance Criteria must be verified before task completion"
        (applyTask tempRoot "TST-1" 2 (CompleteTask defaultHandoff))

    expectRejected
        "verify requires evidence"
        "evidenceRefs must contain at least one non-empty value"
        (applyTask tempRoot "TST-1" 2 (VerifyAcceptanceCriterion("AC1", [])))

    let skeletonEvidence = makeEvidence "E1" EvidenceKind.Build "skeleton build passed"
    let evidenceAdded = expectOk "add E1" (applyTask tempRoot "TST-1" 2 (AddEvidence skeletonEvidence))
    assertEqual "add evidence increments revision" 3 evidenceAdded.StateRevision
    assertEqual "add evidence stores valid record" Valid evidenceAdded.Evidence.Head.Validity

    let verified =
        expectOk
            "verify AC1"
            (applyTask tempRoot "TST-1" 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))

    assertEqual "verify increments revision" 4 verified.StateRevision
    assertEqual "verify stores evidence" (Verified [ "E1" ]) verified.AcceptanceCriteria.Head.State

    let completed = expectOk "complete task" (applyTask tempRoot "TST-1" 4 (CompleteTask defaultHandoff))
    assertEqual "complete task increments revision" 5 completed.StateRevision
    assertEqual "complete task sets lifecycle" "complete" completed.Lifecycle

    let validatedComplete = expectOk "validate complete" (validateTask tempRoot "TST-1")
    assertEqual "validate complete revision" 5 validatedComplete.StateRevision
    assertEqual "validate complete lifecycle" "complete" validatedComplete.Lifecycle

    expectRejected
        "complete task twice"
        "only an open task can complete"
        (applyTask tempRoot "TST-1" 5 (CompleteTask defaultHandoff))

    expectRejected
        "start work after completion"
        "a complete task cannot start work"
        (applyTask tempRoot "TST-1" 5 (StartWorkItem "W1"))

    assertEqual "rejected post-completion apply did not bump revision" 5 (expectOk "get after post-complete" (getTask tempRoot "TST-1")).StateRevision

    // --- Scenario 2: stale expected-revision CAS / no lost update -----------
    expectOk "create TST-2" (createTask tempRoot (createRequest "TST-2" "CAS task")) |> ignore
    expectOk "start TST-2 W1" (applyTask tempRoot "TST-2" 0 (StartWorkItem "W1")) |> ignore

    // A second writer still holding revision 0 must lose to the committed rev 1.
    match applyTask tempRoot "TST-2" 0 (CompleteWorkItem("W1", { Result = "late"; EvidenceRefs = [] })) with
    | Error (Conflict (expected, actual)) ->
        assertEqual "stale conflict expected" 0 expected
        assertEqual "stale conflict actual" 1 actual
    | other -> failwithf "stale apply should conflict, got %A" other

    let afterStale = expectOk "get after stale" (getTask tempRoot "TST-2")
    assertEqual "stale apply did not bump revision" 1 afterStale.StateRevision
    assertEqual "stale apply did not change work state" ActiveWork afterStale.WorkItems.Head.State
    assertEqual "stale apply did not write result" None afterStale.WorkItems.Head.Result

    let committed =
        expectOk "commit TST-2 W1" (applyTask tempRoot "TST-2" 1 (CompleteWorkItem("W1", { Result = "winner"; EvidenceRefs = [] })))

    assertEqual "committed revision" 2 committed.StateRevision
    assertEqual "committed result" (Some "winner") committed.WorkItems.Head.Result

    // A writer still holding the superseded revision must not overwrite the winner.
    match applyTask tempRoot "TST-2" 1 (StartWorkItem "W1") with
    | Error (Conflict (expected, actual)) ->
        assertEqual "superseded conflict expected" 1 expected
        assertEqual "superseded conflict actual" 2 actual
    | other -> failwithf "superseded apply should conflict, got %A" other

    let afterSuperseded = expectOk "get after superseded" (getTask tempRoot "TST-2")
    assertEqual "superseded apply did not bump revision" 2 afterSuperseded.StateRevision
    assertEqual "superseded apply preserved winner" (Some "winner") afterSuperseded.WorkItems.Head.Result

    // A future revision is also a conflict, never a silent write.
    expectRejected "future revision conflicts" "state revision conflict: expected 99, actual 2" (applyTask tempRoot "TST-2" 99 (CompleteTask defaultHandoff))

    // A rejected transition is a no-op even when the CAS revision is current.
    expectRejected
        "rejected transition is a no-op"
        "must be pending before it starts"
        (applyTask tempRoot "TST-2" 2 (StartWorkItem "W1"))

    assertEqual "rejected transition did not bump revision" 2 (expectOk "get after rejected transition" (getTask tempRoot "TST-2")).StateRevision

    // --- Scenario 3: malformed / unknown wire input rejection ---------------
    // Schema 1 is the sole canonical persisted shape: contract fields, guards,
    // key provenance, decisions, questions, work-item dependsOn/evidenceRefs, and
    // completion history are all present even when empty.
    let generalFingerprint = profiles.[GeneralProfileId].Fingerprint

    let baselineJson =
        sprintf
             """{"schemaVersion":1,"id":"TST-9","title":"Wire fixture","created":"2026-09-10T00:00:00.0000000+00:00","kind":"execution","profile":"general","profileFingerprint":"%s","objective":"","scope":"","nonGoals":"","contractState":"draft","contractFingerprint":"","contractRevision":1,"stateRevision":0,"lifecycle":"open","evidence":[],"acceptanceCriteria":[{"id":"AC1","text":"Execution completes","state":"pending","evidenceRefs":[]}],"guards":[],"profileGuardKeys":{},"decisions":[],"questions":[],"workItems":[{"id":"W1","title":"Do the work","state":"pending","result":"","acceptanceRefs":["AC1"],"dependsOn":[],"evidenceRefs":[],"children":[]}],"completionHistory":[]}"""
            generalFingerprint

    let mutateJson (mutate: JsonObject -> unit) =
        match JsonNode.Parse baselineJson with
        | :? JsonObject as node ->
            mutate node
            node.ToJsonString()
        | _ -> failwith "wire fixture is not a JSON object"

    // The fixture itself must be accepted so rejections below are meaningful.
    expectOk "wire fixture parses" (deserialize baselineJson) |> ignore

    let unknownTopLevel = mutateJson (fun node -> node.["extra"] <- JsonValue.Create 1)
    let duplicateProperty = baselineJson.Replace("\"id\":\"TST-9\"", "\"id\":\"TST-9\",\"id\":\"TST-9\"")
    let missingProperty = mutateJson (fun node -> node.Remove "lifecycle" |> ignore)
    let wrongIntegerType = mutateJson (fun node -> node.["stateRevision"] <- JsonValue.Create "zero")
    let unsupportedSchema = mutateJson (fun node -> node.["schemaVersion"] <- JsonValue.Create 3)
    let unknownKind = mutateJson (fun node -> node.["kind"] <- JsonValue.Create "hybrid")
    let unknownProfile = mutateJson (fun node -> node.["profile"] <- JsonValue.Create "unregistered")
    let fingerprintMismatch = mutateJson (fun node -> node.["profileFingerprint"] <- JsonValue.Create "general-v2")
    let badTimestamp = mutateJson (fun node -> node.["created"] <- JsonValue.Create "not-a-timestamp")
    let badContractRevision = mutateJson (fun node -> node.["contractRevision"] <- JsonValue.Create 0)
    let badLifecycle = mutateJson (fun node -> node.["lifecycle"] <- JsonValue.Create "paused")
    let noAcceptance = mutateJson (fun node -> node.["acceptanceCriteria"] <- JsonNode.Parse "[]")
    let noWorkItems = mutateJson (fun node -> node.["workItems"] <- JsonNode.Parse "[]")
    let duplicateAcIds =
        mutateJson (fun node ->
            node.["acceptanceCriteria"].AsArray().Add(JsonNode.Parse """{"id":"AC1","text":"Duplicate","state":"pending","evidenceRefs":[]}"""))

    let pendingWithEvidence =
        mutateJson (fun node ->
            node.["acceptanceCriteria"].AsArray().[0].AsObject().["evidenceRefs"] <- JsonNode.Parse """["E1"]""")

    let verifiedWithoutEvidence =
        mutateJson (fun node ->
            let criterion = node.["acceptanceCriteria"].AsArray().[0].AsObject()
            criterion.["state"] <- JsonValue.Create "verified"
            criterion.["evidenceRefs"] <- JsonNode.Parse "[]")

    let doneWithoutResult =
        mutateJson (fun node -> node.["workItems"].AsArray().[0].AsObject().["state"] <- JsonValue.Create "done")

    let nestedUnknownProperty =
        mutateJson (fun node -> node.["acceptanceCriteria"].AsArray().[0].AsObject().["extra"] <- JsonValue.Create 1)

    let unknownAcceptanceRef =
        mutateJson (fun node ->
            node.["workItems"].AsArray().[0].AsObject().["acceptanceRefs"] <- JsonNode.Parse """["AC9"]""")

    let nestedChildren =
        mutateJson (fun node ->
            node.["workItems"].AsArray().[0].AsObject().["children"].AsArray().Add(JsonNode.Parse """{"id":"W2","title":"Child","state":"pending","result":"","acceptanceRefs":[],"dependsOn":[],"evidenceRefs":[],"children":[]}"""))

    // Strict schema-v1 rejections: non-v1 and legacy fingerprints are no longer
    // accepted, and every required v3 field must be present.
    let schemaV2 = mutateJson (fun node -> node.["schemaVersion"] <- JsonValue.Create 2)

    let legacyGeneralFingerprint =
        mutateJson (fun node -> node.["profileFingerprint"] <- JsonValue.Create "general-v1")

    let missingContractState = mutateJson (fun node -> node.Remove "contractState" |> ignore)
    let missingGuards = mutateJson (fun node -> node.Remove "guards" |> ignore)
    let missingCompletionHistory = mutateJson (fun node -> node.Remove "completionHistory" |> ignore)

    let missingWorkItemDependsOn =
        mutateJson (fun node -> node.["workItems"].AsArray().[0].AsObject().Remove "dependsOn" |> ignore)

    // Guard key provenance is mandatory and exact for profile-materialized
    // guards: missing, stale, and blank provenance all fail closed.
    let profileGuardJson =
        """{"id":"G1","target":"task","checkpoint":"beforeComplete","origin":"profileMaterialized","requirement":"evidenceRequired","evidenceKind":"test","minimumCount":1,"producerRole":"","requireIndependentProducer":false,"applicability":"always","waiver":"notWaivable","disposition":"applicable"}"""

    let profileGuardMissingKey =
        mutateJson (fun node -> node.["guards"] <- JsonNode.Parse(sprintf "[%s]" profileGuardJson))

    let profileGuardStaleKey =
        mutateJson (fun node -> node.["profileGuardKeys"] <- JsonNode.Parse """{"G1":"alpha"}""")

    let profileGuardBlankKey =
        mutateJson (fun node ->
            node.["guards"] <- JsonNode.Parse(sprintf "[%s]" profileGuardJson)
            node.["profileGuardKeys"] <- JsonNode.Parse """{"G1":""}""")

    let profileGuardKeyed =
        mutateJson (fun node ->
            node.["guards"] <- JsonNode.Parse(sprintf "[%s]" profileGuardJson)
            node.["profileGuardKeys"] <- JsonNode.Parse """{"G1":"alpha"}""")

    expectRejectedDeserialize "unknown top-level property" "task contains unknown property 'extra'" unknownTopLevel
    expectRejectedDeserialize "duplicate JSON property" "duplicate JSON property 'id'" duplicateProperty
    expectRejectedDeserialize "missing property" "task is missing property 'lifecycle'" missingProperty
    expectRejectedDeserialize "wrong integer type" "property 'stateRevision' must be an integer" wrongIntegerType
    expectRejectedDeserialize "unsupported schema version" "unsupported schemaVersion 3" unsupportedSchema
    expectRejectedDeserialize "unknown kind" "kind must be 'execution' or 'research'" unknownKind
    expectRejectedDeserialize "unknown profile" "unknown profile 'unregistered'" unknownProfile
    expectRejectedDeserialize "fingerprint mismatch" "general profile fingerprint does not match" fingerprintMismatch
    expectRejectedDeserialize "invalid timestamp" "created must be an ISO-8601 timestamp" badTimestamp
    expectRejectedDeserialize "contract revision out of range" "revision values are out of range" badContractRevision
    expectRejectedDeserialize "invalid lifecycle" "lifecycle must be 'open', 'complete', or 'aborted'" badLifecycle
    expectRejectedDeserialize "no acceptance criteria" "a task requires at least one Acceptance Criterion" noAcceptance
    expectRejectedDeserialize "no work items" "a task requires at least one WorkItem" noWorkItems
    expectRejectedDeserialize "duplicate acceptance ids" "Acceptance Criterion IDs must be unique" duplicateAcIds
    expectRejectedDeserialize "pending acceptance with evidence" "cannot contain evidence" pendingWithEvidence
    expectRejectedDeserialize "verified acceptance without evidence" "evidenceRefs must contain at least one non-empty value" verifiedWithoutEvidence
    expectRejectedDeserialize "done work item without result" "invalid state/payload combination" doneWithoutResult
    expectRejectedDeserialize "unknown nested property" "acceptance criterion contains unknown property 'extra'" nestedUnknownProperty
    expectRejectedDeserialize "unknown acceptance reference" "WorkItem references unknown Acceptance Criterion 'AC9'" unknownAcceptanceRef
    expectRejectedDeserialize "invalid JSON syntax" "invalid JSON" "{ not json"
    expectRejectedDeserialize "schema v2 rejected" "unsupported schemaVersion 2" schemaV2
    expectRejectedDeserialize "legacy general-v1 fingerprint rejected" "general profile fingerprint does not match" legacyGeneralFingerprint
    expectRejectedDeserialize "missing contractState" "task is missing property 'contractState'" missingContractState
    expectRejectedDeserialize "missing guards" "task is missing property 'guards'" missingGuards
    expectRejectedDeserialize "missing completionHistory" "task is missing property 'completionHistory'" missingCompletionHistory
    expectRejectedDeserialize "missing work item dependsOn" "work item is missing property 'dependsOn'" missingWorkItemDependsOn
    expectRejectedDeserialize "profile guard missing key provenance" "profile guard key provenance is missing Guard 'G1'" profileGuardMissingKey
    expectRejectedDeserialize "profile guard stale key provenance" "profile guard key provenance contains stale Guard 'G1'" profileGuardStaleKey
    expectRejectedDeserialize "profile guard blank key provenance" "profile guard key provenance values must be non-empty" profileGuardBlankKey

    // Explicit key provenance for a profile-materialized guard is accepted.
    let keyedGuardParsed = expectOk "keyed profile guard parses" (deserialize profileGuardKeyed)
    assertEqual "keyed profile guard provenance" (Map.ofList [ "G1", "alpha" ]) keyedGuardParsed.ProfileGuardKeys

    // Recursion is now supported: a nested child is accepted and preserved.
    let nestedParsed = expectOk "nested children parse" (deserialize nestedChildren)
    assertEqual "nested child id" "W2" nestedParsed.WorkItems.Head.Children.Head.Id
    assertEqual "nested child state" PendingWork nestedParsed.WorkItems.Head.Children.Head.State

    // Create-request domain validation rejects malformed input before persistence.
    let baseRequest = createRequest "TST-4" "Domain fixture"

    expectRejected "invalid task id" "task id has an invalid format" (createTask tempRoot { baseRequest with Id = "../TST-4" })
    expectRejected "blank title" "task title must be a non-empty single line" (createTask tempRoot { baseRequest with Title = "   " })
    expectRejected "multiline title" "task title must be a non-empty single line" (createTask tempRoot { baseRequest with Title = "a\nb" })
    expectRejected "empty acceptance" "create requires at least one Acceptance Criterion" (createTask tempRoot { baseRequest with AcceptanceCriteria = [] })
    expectRejected "empty work items" "create requires at least one WorkItem" (createTask tempRoot { baseRequest with WorkItems = [] })
    expectRejected "invalid acceptance id" "acceptance id has an invalid format" (createTask tempRoot { baseRequest with AcceptanceCriteria = [ "A1", "x" ] })
    expectRejected "duplicate acceptance ids" "Acceptance Criterion IDs must be unique" (createTask tempRoot { baseRequest with AcceptanceCriteria = [ "AC1", "a"; "AC1", "b" ] })
    expectRejected "invalid work item id" "work item id has an invalid format" (createTask tempRoot { baseRequest with WorkItems = [ spec "X1" "x" ] })
    expectRejected "duplicate work item ids" "WorkItem IDs must be unique" (createTask tempRoot { baseRequest with WorkItems = [ spec "W1" "a"; spec "W1" "b" ] })

    // A persisted sidecar is validated on read, not trusted blindly.
    let malformedId = "TST-3"
    let malformedDirectory = Path.Combine(tempRoot, ".tasks", malformedId)
    Directory.CreateDirectory malformedDirectory |> ignore
    File.WriteAllText(Path.Combine(malformedDirectory, "runtime.json"), unknownTopLevel)
    expectRejected "malformed persisted sidecar rejected on get" "task contains unknown property 'extra'" (getTask tempRoot malformedId)

    // --- Scenario 4: sidecar persistence and stateRevision ------------------
    let persistedId = "TST-5"
    expectOk "create TST-5" (createTask tempRoot (createRequest persistedId "Persistence task")) |> ignore

    assertTrue "sidecar exists after create" (File.Exists(sidecarPath tempRoot persistedId))

    let onDisk0 = expectOk "read TST-5 at revision 0" (readPersisted tempRoot persistedId)
    assertEqual "on-disk revision after create" 0 onDisk0.StateRevision
    assertEqual "on-disk work state after create" PendingWork onDisk0.WorkItems.Head.State

    expectOk "start TST-5" (applyTask tempRoot persistedId 0 (StartWorkItem "W1")) |> ignore
    assertEqual "on-disk revision after start" 1 (expectOk "read TST-5 at revision 1" (readPersisted tempRoot persistedId)).StateRevision

    expectOk "complete TST-5" (applyTask tempRoot persistedId 1 (CompleteWorkItem("W1", { Result = "persisted"; EvidenceRefs = [] }))) |> ignore
    let onDisk2 = expectOk "read TST-5 at revision 2" (readPersisted tempRoot persistedId)
    assertEqual "on-disk revision after complete" 2 onDisk2.StateRevision
    assertEqual "on-disk work result" (Some "persisted") onDisk2.WorkItems.Head.Result

    // The persisted wire document keeps the frozen schema and profile identity.
    let raw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot persistedId)).AsObject()
    assertEqual "persisted schema version" 1 (raw.["schemaVersion"].GetValue<int>())
    assertEqual "persisted kind" "execution" (raw.["kind"].GetValue<string>())
    assertEqual "persisted profile" "general" (raw.["profile"].GetValue<string>())
    assertEqual "persisted fingerprint" generalFingerprint (raw.["profileFingerprint"].GetValue<string>())
    assertEqual "persisted lifecycle" "open" (raw.["lifecycle"].GetValue<string>())
    assertEqual "persisted evidence array" 0 (raw.["evidence"].AsArray().Count)

    // Atomic replace must not leave staging files behind.
    let leftovers =
        Directory.GetFiles(Path.Combine(tempRoot, ".tasks", persistedId), "*.tmp")

    assertEqual "no temporary persistence leftovers" 0 leftovers.Length

    // Re-creating an existing task fails closed and never overwrites the sidecar.
    expectRejected
        "re-create existing task"
        existingCreateRejectedMessage
        (createTask tempRoot (createRequest persistedId "Overwrite attempt"))
    assertEqual "re-create did not reset revision" 2 (expectOk "read TST-5 after re-create" (readPersisted tempRoot persistedId)).StateRevision
    assertEqual "re-create did not erase result" (Some "persisted") (expectOk "read TST-5 result after re-create" (readPersisted tempRoot persistedId)).WorkItems.Head.Result

    // --- Scenario 4b: concurrent create cannot overwrite runtime.json -------
    // INFRA-015-R2 regression: creators racing one new id must serialize so
    // exactly one commits and the winning runtime.json survives byte-for-byte.
    // A barrier releases every racer into createTask together so the pre-lock
    // existence check and the post-lock re-check window are exercised.
    let raceRounds = 12
    let racersPerRound = 6

    for round in 0 .. raceRounds - 1 do
        let raceId = $"TST-9{round:D2}"
        use gate = new System.Threading.Barrier(racersPerRound)
        let results: Result<TaskModel, RuntimeError> array = Array.zeroCreate racersPerRound

        let racers =
            [ for racer in 0 .. racersPerRound - 1 ->
                  let thread =
                      System.Threading.Thread(fun () ->
                          gate.SignalAndWait()
                          // Each racer keeps its own distinct title so the committed
                          // document can be attributed to the single winner.
                          let result =
                              try
                                  createTask tempRoot (createRequest raceId $"Concurrent racer {racer}")
                              with error ->
                                  Error(PersistenceFailure $"concurrent racer {racer} threw: {error.Message}")

                          results.[racer] <- result)

                  thread.IsBackground <- true
                  thread.Start()
                  thread ]

        racers |> List.iter (fun thread -> thread.Join())

        let winners =
            results
            |> Array.indexed
            |> Array.choose (fun (racer, result) ->
                match result with
                | Ok task -> Some(racer, task)
                | Error _ -> None)

        assertEqual (sprintf "concurrent create %s has exactly one winner" raceId) 1 winners.Length
        let winnerRacer, winnerTask = winners.[0]

        for racer, result in Array.indexed results do
            match result with
            | Ok _ when racer = winnerRacer -> ()
            | Ok _ -> failwithf "concurrent create %s: racer %d also committed" raceId racer
            | Error error ->
                let message = renderError error

                if not (message.Contains(existingCreateRejectedMessage, StringComparison.Ordinal)) then
                    failwithf "concurrent create %s: racer %d unexpected error %s" raceId racer message

        let committed = expectOk (sprintf "get raced task %s" raceId) (getTask tempRoot raceId)
        assertEqual (sprintf "concurrent create %s revision" raceId) 0 committed.StateRevision
        assertEqual (sprintf "concurrent create %s committed winner" raceId) winnerTask.Title committed.Title

        let beforeBytes = File.ReadAllBytes(sidecarPath tempRoot raceId)

        expectRejected
            (sprintf "post-race re-create %s" raceId)
            existingCreateRejectedMessage
            (createTask tempRoot (createRequest raceId "Late overwrite"))

        assertEqual
            (sprintf "post-race re-create %s left runtime.json bytes unchanged" raceId)
            beforeBytes
            (File.ReadAllBytes(sidecarPath tempRoot raceId))

    // Missing sidecars and missing roots fail closed without creating state.
    let absentId = "TST-405"
    let absentDirectory = Path.Combine(tempRoot, ".tasks", absentId)
    Directory.CreateDirectory absentDirectory |> ignore
    expectRejected "missing sidecar on get" "runtime sidecar does not exist" (getTask tempRoot absentId)
    expectRejected "missing sidecar on validate" "runtime sidecar does not exist" (validateTask tempRoot absentId)
    expectRejected "missing sidecar on apply" "runtime sidecar does not exist" (applyTask tempRoot absentId 0 (StartWorkItem "W1"))
    assertTrue "missing sidecar does not create a lock" (not (File.Exists(Path.Combine(absentDirectory, "runtime.lock"))))
    assertTrue "missing sidecar does not create a sidecar" (not (File.Exists(sidecarPath tempRoot absentId)))

    let noDirectoryId = "TST-404"
    expectRejected "absent task directory has no runtime input" "runtime sidecar does not exist" (getTask tempRoot noDirectoryId)

    expectRejected "invalid task id on get" "task id has an invalid format" (getTask tempRoot "../TST-1")

    let missingRoot = Path.Combine(tempRoot, "missing-root")
    expectRejected "missing project root on get" "project root does not exist" (getTask missingRoot "TST-1")
    expectRejected "missing project root on create" "project root does not exist" (createTask missingRoot (createRequest "TST-6" "No root"))

    // --- Scenario 5: general + research creation and persistence -------------
    let researchId = "TST-6"
    let researchCreated =
        expectOk "create research TST-6" (createTask tempRoot { createRequest researchId "Research task" with Kind = Research })

    assertEqual "research created kind" Research researchCreated.Kind
    assertEqual "research created AC pending" Pending researchCreated.AcceptanceCriteria.Head.State
    assertEqual "research created evidence empty" [] researchCreated.Evidence

    let researchFetched = expectOk "get research TST-6" (getTask tempRoot researchId)
    assertEqual "research get kind" Research researchFetched.Kind

    let researchRoundTrip = expectOk "research serialize round trip" (researchCreated |> serialize |> deserialize)
    assertEqual "research round trip kind" Research researchRoundTrip.Kind
    assertEqual "research round trip evidence" [] researchRoundTrip.Evidence

    let researchRaw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot researchId)).AsObject()
    assertEqual "research persisted schema" 1 (researchRaw.["schemaVersion"].GetValue<int>())
    assertEqual "research persisted kind" "research" (researchRaw.["kind"].GetValue<string>())
    assertEqual "research persisted profile" "general" (researchRaw.["profile"].GetValue<string>())

    // Evidence-backed AC verification works identically for research.
    let researchEvidence = makeEvidence "E1" EvidenceKind.Research "research finding"
    let researchWithEvidence = expectOk "research add evidence" (applyTask tempRoot researchId 0 (AddEvidence researchEvidence))
    assertEqual "research add evidence revision" 1 researchWithEvidence.StateRevision
    assertEqual "research evidence kind" EvidenceKind.Research researchWithEvidence.Evidence.Head.Evidence.Kind

    let researchVerified =
        expectOk "research verify AC" (applyTask tempRoot researchId 1 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))

    assertEqual "research verified AC" (Verified [ "E1" ]) researchVerified.AcceptanceCriteria.Head.State

    // --- Scenario 6: strict Evidence DTO parsing ----------------------------
    let makeEvidenceNode (id: string) (kind: string) (validity: string) (reason: string) (summary: string) =
        let node = JsonObject()
        node.["id"] <- JsonValue.Create id
        node.["kind"] <- JsonValue.Create kind
        node.["source"] <- JsonValue.Create "test-source"
        node.["subject"] <- JsonValue.Create ""
        node.["producerRole"] <- JsonValue.Create ""
        node.["producerId"] <- JsonValue.Create ""
        node.["reference"] <- JsonValue.Create ""
        node.["summary"] <- JsonValue.Create summary
        node.["validity"] <- JsonValue.Create validity
        node.["supersededReason"] <- JsonValue.Create reason
        node

    let taskWithEvidence (evidenceNodes: JsonNode list) (acState: string) (acRefs: string list) =
        let task = JsonNode.Parse(baselineJson).AsObject()
        let evidenceArray = JsonArray()
        evidenceNodes |> List.iter (fun node -> evidenceArray.Add node)
        task.["evidence"] <- evidenceArray
        let criterion = task.["acceptanceCriteria"].AsArray().[0].AsObject()
        criterion.["state"] <- JsonValue.Create acState
        let refArray = JsonArray()
        acRefs |> List.iter (fun reference -> refArray.Add(JsonValue.Create reference))
        criterion.["evidenceRefs"] <- refArray
        task.ToJsonString()

    // Known kinds parse to their canonical domain values.
    let knownKinds =
        [ "build", EvidenceKind.Build
          "test", EvidenceKind.Test
          "review", EvidenceKind.Review
          "observation", EvidenceKind.Observation
          "externalEffect", EvidenceKind.ExternalEffect
          "research", EvidenceKind.Research
          "decisionEvidence", EvidenceKind.DecisionEvidence ]

    for kindText, expectedKind in knownKinds do
        let parsed =
            expectOk
                $"known evidence kind {kindText}"
                (deserialize (taskWithEvidence [ makeEvidenceNode "E1" kindText "valid" "" "summary" ] "pending" []))

        assertEqual $"parsed evidence kind {kindText}" expectedKind parsed.Evidence.Head.Evidence.Kind

    // Other kind carries an open semantic value; empty/unknown values fail closed.
    let otherParsed =
        expectOk
            "other evidence kind"
            (deserialize (taskWithEvidence [ makeEvidenceNode "E1" "other:manual" "valid" "" "summary" ] "pending" []))

    assertEqual "other evidence kind value" (EvidenceKind.Other "manual") otherParsed.Evidence.Head.Evidence.Kind

    expectRejectedDeserialize
        "other evidence kind empty"
        "evidence kind 'other' requires a non-empty value"
        (taskWithEvidence [ makeEvidenceNode "E1" "other:" "valid" "" "summary" ] "pending" [])

    expectRejectedDeserialize
        "other evidence kind whitespace"
        "evidence kind 'other' requires a non-empty value"
        (taskWithEvidence [ makeEvidenceNode "E1" "other:   " "valid" "" "summary" ] "pending" [])

    expectRejectedDeserialize
        "other evidence kind missing value marker"
        "evidence kind is not recognized"
        (taskWithEvidence [ makeEvidenceNode "E1" "other" "valid" "" "summary" ] "pending" [])

    expectRejectedDeserialize
        "unknown evidence kind"
        "evidence kind is not recognized"
        (taskWithEvidence [ makeEvidenceNode "E1" "magic" "valid" "" "summary" ] "pending" [])

    // Optional attributes round-trip: absent means None, present is trimmed.
    let optionalAbsent =
        expectOk
            "evidence optional fields absent"
            (deserialize (taskWithEvidence [ makeEvidenceNode "E1" "build" "valid" "" "summary" ] "pending" []))

    let absentEvidence = optionalAbsent.Evidence.Head.Evidence
    assertEqual "absent subject" None absentEvidence.Subject
    assertEqual "absent producerRole" None absentEvidence.ProducerRole
    assertEqual "absent producerId" None absentEvidence.ProducerId
    assertEqual "absent reference" None absentEvidence.Reference

    let withOptional = makeEvidenceNode "E1" "build" "valid" "" "summary"
    withOptional.["subject"] <- JsonValue.Create "  subject value  "
    withOptional.["producerRole"] <- JsonValue.Create "reviewer"
    withOptional.["producerId"] <- JsonValue.Create "agent-1"
    withOptional.["reference"] <- JsonValue.Create "artifacts/log.txt"

    let optionalPresent =
        expectOk
            "evidence optional fields present"
            (deserialize (taskWithEvidence [ withOptional ] "pending" []))

    let presentEvidence = optionalPresent.Evidence.Head.Evidence
    assertEqual "present subject trimmed" (Some "subject value") presentEvidence.Subject
    assertEqual "present producerRole" (Some "reviewer") presentEvidence.ProducerRole
    assertEqual "present producerId" (Some "agent-1") presentEvidence.ProducerId
    assertEqual "present reference" (Some "artifacts/log.txt") presentEvidence.Reference

    // Duplicate, unknown, and malformed evidence records are rejected.
    expectRejectedDeserialize
        "duplicate evidence ids"
        "Evidence IDs must be unique"
        (taskWithEvidence
            [ makeEvidenceNode "E1" "build" "valid" "" "one"
              makeEvidenceNode "E1" "test" "valid" "" "two" ]
            "pending"
            [])

    let unknownEvidenceProperty =
        let node = makeEvidenceNode "E1" "build" "valid" "" "summary"
        node.["extra"] <- JsonValue.Create 1
        node

    expectRejectedDeserialize
        "unknown evidence property"
        "evidence record contains unknown property 'extra'"
        (taskWithEvidence [ unknownEvidenceProperty ] "pending" [])

    let missingEvidenceProperty =
        let node = makeEvidenceNode "E1" "build" "valid" "" "summary"
        node.Remove "summary" |> ignore
        node

    expectRejectedDeserialize
        "missing evidence property"
        "evidence record is missing property 'summary'"
        (taskWithEvidence [ missingEvidenceProperty ] "pending" [])

    let wrongEvidenceIdType =
        let node = makeEvidenceNode "E1" "build" "valid" "" "summary"
        node.["id"] <- JsonValue.Create 7
        node

    expectRejectedDeserialize
        "wrong evidence id type"
        "property 'id' must be a string"
        (taskWithEvidence [ wrongEvidenceIdType ] "pending" [])

    let blankEvidenceSource =
        let node = makeEvidenceNode "E1" "build" "valid" "" "summary"
        node.["source"] <- JsonValue.Create "   "
        node

    expectRejectedDeserialize
        "blank evidence source"
        "evidence source must be a non-empty single line"
        (taskWithEvidence [ blankEvidenceSource ] "pending" [])

    expectRejectedDeserialize
        "invalid evidence id format"
        "evidence id has an invalid format"
        (taskWithEvidence [ makeEvidenceNode "BAD" "build" "valid" "" "summary" ] "pending" [])

    expectRejectedDeserialize
        "blank evidence summary"
        "evidence summary must be a non-empty single line"
        (taskWithEvidence [ makeEvidenceNode "E1" "build" "valid" "" "   " ] "pending" [])

    expectRejectedDeserialize
        "invalid evidence validity"
        "has an invalid validity"
        (taskWithEvidence [ makeEvidenceNode "E1" "build" "expired" "" "summary" ] "pending" [])

    expectRejectedDeserialize
        "valid evidence with superseded reason"
        "cannot contain a superseded reason"
        (taskWithEvidence [ makeEvidenceNode "E1" "build" "valid" "stale" "summary" ] "pending" [])

    expectRejectedDeserialize
        "superseded evidence without reason"
        "superseded reason must be a non-empty single line"
        (taskWithEvidence [ makeEvidenceNode "E1" "build" "superseded" "" "summary" ] "pending" [])

    let supersededParsed =
        expectOk
            "superseded evidence parses"
            (deserialize (taskWithEvidence [ makeEvidenceNode "E1" "build" "superseded" "outdated" "summary" ] "pending" []))

    assertEqual "superseded validity" (Superseded "outdated") supersededParsed.Evidence.Head.Validity

    // An AC cannot be verified against superseded or unknown evidence on the wire.
    expectRejectedDeserialize
        "verified AC against superseded evidence"
        "references unknown or superseded evidence"
        (taskWithEvidence [ makeEvidenceNode "E1" "build" "superseded" "outdated" "summary" ] "verified" [ "E1" ])

    expectRejectedDeserialize
        "verified AC against unknown evidence"
        "references unknown or superseded evidence"
        (taskWithEvidence [] "verified" [ "E9" ])

    // --- Scenario 7: AddEvidence validation, duplicates, no-op --------------
    let evidenceTask = "TST-7"
    expectOk "create evidence task" (createTask tempRoot (createRequest evidenceTask "Evidence task")) |> ignore

    let baseEvidence = makeEvidence "E1" EvidenceKind.Build "build passed"
    let afterAdd = expectOk "add E1" (applyTask tempRoot evidenceTask 0 (AddEvidence baseEvidence))
    assertEqual "add evidence revision" 1 afterAdd.StateRevision
    assertEqual "add evidence count" 1 afterAdd.Evidence.Length
    assertEqual "add evidence validity" Valid afterAdd.Evidence.Head.Validity
    assertEqual "add evidence content" baseEvidence afterAdd.Evidence.Head.Evidence

    // Optional attributes survive a full serialize/deserialize round trip.
    let richEvidence =
        { baseEvidence with
            Id = "E2"
            Subject = Some "subject"
            ProducerRole = Some "reviewer"
            ProducerId = Some "agent-1"
            Reference = Some "artifacts/run.log" }

    let afterRich = expectOk "add rich evidence" (applyTask tempRoot evidenceTask 1 (AddEvidence richEvidence))
    let richRoundTrip = expectOk "rich evidence round trip" (afterRich |> serialize |> deserialize)
    assertEqual "rich evidence round trip content" richEvidence richRoundTrip.Evidence.[1].Evidence

    // Shape validation rejects malformed evidence before persistence.
    expectRejected
        "add evidence invalid id"
        "evidence id has an invalid format"
        (applyTask tempRoot evidenceTask 2 (AddEvidence { baseEvidence with Id = "X1" }))

    expectRejected
        "add evidence blank source"
        "evidence source must be a non-empty single line"
        (applyTask tempRoot evidenceTask 2 (AddEvidence { baseEvidence with Id = "E3"; Source = EvidenceSource "   " }))

    expectRejected
        "add evidence blank summary"
        "evidence summary must be a non-empty single line"
        (applyTask tempRoot evidenceTask 2 (AddEvidence { baseEvidence with Id = "E3"; Summary = "  " }))

    expectRejected
        "add evidence multiline summary"
        "evidence summary must be a non-empty single line"
        (applyTask tempRoot evidenceTask 2 (AddEvidence { baseEvidence with Id = "E3"; Summary = "a\nb" }))

    expectRejected
        "add evidence other empty kind"
        "evidence kind 'other' requires a non-empty value"
        (applyTask tempRoot evidenceTask 2 (AddEvidence { baseEvidence with Id = "E3"; Kind = EvidenceKind.Other "  " }))

    expectRejected
        "add evidence multiline subject"
        "evidence subject must be a non-empty single line"
        (applyTask tempRoot evidenceTask 2 (AddEvidence { baseEvidence with Id = "E3"; Subject = Some "a\nb" }))

    expectRejected
        "add evidence duplicate id"
        "evidence 'E1' already exists"
        (applyTask tempRoot evidenceTask 2 (AddEvidence baseEvidence))

    assertEqual "add evidence rejections are no-ops" 2 (expectOk "get evidence task" (getTask tempRoot evidenceTask)).StateRevision
    assertEqual "add evidence rejections preserved evidence" 2 (expectOk "get evidence task evidence" (getTask tempRoot evidenceTask)).Evidence.Length

    // Blank optional attributes normalize to None rather than persisting whitespace.
    let normalized =
        expectOk
            "add evidence with blank optional attributes"
            (applyTask
                tempRoot
                evidenceTask
                2
                (AddEvidence
                    { baseEvidence with
                        Id = "E3"
                        Subject = Some "   "
                        ProducerRole = Some ""
                        ProducerId = Some "  "
                        Reference = Some "\t" }))

    let normalizedEvidence = normalized.Evidence |> List.find (fun record -> record.Evidence.Id = "E3")
    assertEqual "blank subject normalized" None normalizedEvidence.Evidence.Subject
    assertEqual "blank producerRole normalized" None normalizedEvidence.Evidence.ProducerRole
    assertEqual "blank producerId normalized" None normalizedEvidence.Evidence.ProducerId
    assertEqual "blank reference normalized" None normalizedEvidence.Evidence.Reference

    // --- Scenario 8: VerifyAcceptanceCriterion evidence scope ---------------
    let verifyTask = "TST-8"
    expectOk "create verify task" (createTask tempRoot (createRequest verifyTask "Verify task")) |> ignore

    expectRejected
        "verify with empty refs"
        "evidenceRefs must contain at least one non-empty value"
        (applyTask tempRoot verifyTask 0 (VerifyAcceptanceCriterion("AC1", [])))

    expectRejected
        "verify with whitespace ref"
        "evidenceRefs must contain at least one non-empty value"
        (applyTask tempRoot verifyTask 0 (VerifyAcceptanceCriterion("AC1", [ "  " ])))

    expectRejected
        "verify unknown acceptance criterion"
        "Acceptance Criterion 'AC9' was not found"
        (applyTask tempRoot verifyTask 0 (VerifyAcceptanceCriterion("AC9", [ "E1" ])))

    expectRejected
        "verify before any evidence exists"
        "references unknown or superseded evidence"
        (applyTask tempRoot verifyTask 0 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))

    expectOk "verify add E1" (applyTask tempRoot verifyTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "test passed"))) |> ignore
    expectRejected
        "verify unknown evidence"
        "references unknown or superseded evidence"
        (applyTask tempRoot verifyTask 1 (VerifyAcceptanceCriterion("AC1", [ "E2" ])))

    expectOk "verify add E2" (applyTask tempRoot verifyTask 1 (AddEvidence (makeEvidence "E2" EvidenceKind.Review "review passed"))) |> ignore

    // A partially invalid reference list rejects the whole command.
    expectRejected
        "verify mixed valid and invalid refs"
        "references unknown or superseded evidence"
        (applyTask tempRoot verifyTask 2 (VerifyAcceptanceCriterion("AC1", [ "E1"; "E9" ])))

    assertEqual "verify rejections are no-ops" 2 (expectOk "get verify task" (getTask tempRoot verifyTask)).StateRevision

    let verifiedTask = expectOk "verify AC1 with E1" (applyTask tempRoot verifyTask 2 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))
    assertEqual "verify increments revision" 3 verifiedTask.StateRevision
    assertEqual "verify stores refs" (Verified [ "E1" ]) verifiedTask.AcceptanceCriteria.Head.State

    expectRejected
        "verify already verified"
        "is already verified"
        (applyTask tempRoot verifyTask 3 (VerifyAcceptanceCriterion("AC1", [ "E2" ])))

    // Evidence is task-scoped: another task's E1 is not visible here.
    let otherEvidenceTask = "TST-9"
    expectOk "create other evidence task" (createTask tempRoot (createRequest otherEvidenceTask "Other evidence task")) |> ignore
    expectOk "add E1 to other task" (applyTask tempRoot otherEvidenceTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Observation "observed"))) |> ignore

    let scopeTask = "TST-10"
    expectOk "create scope task" (createTask tempRoot (createRequest scopeTask "Scope task")) |> ignore
    expectRejected
        "verify cannot consume another task's evidence"
        "references unknown or superseded evidence"
        (applyTask tempRoot scopeTask 0 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))

    // --- Scenario 9: SupersedeEvidence cascade and repetition ---------------
    let supersedeTask = "TST-11"
    expectOk "create supersede task" (createTask tempRoot (createRequest supersedeTask "Supersede task")) |> ignore
    expectOk "supersede start W1" (applyTask tempRoot supersedeTask 0 (StartWorkItem "W1")) |> ignore
    expectOk "supersede complete W1" (applyTask tempRoot supersedeTask 1 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "supersede add E1" (applyTask tempRoot supersedeTask 2 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "first"))) |> ignore
    expectOk "supersede add E2" (applyTask tempRoot supersedeTask 3 (AddEvidence (makeEvidence "E2" EvidenceKind.Test "second"))) |> ignore

    let beforeSupersede =
        expectOk "supersede verify AC1" (applyTask tempRoot supersedeTask 4 (VerifyAcceptanceCriterion("AC1", [ "E1"; "E2" ])))

    assertEqual "verify before supersede" (Verified [ "E1"; "E2" ]) beforeSupersede.AcceptanceCriteria.Head.State

    let originalRecord = beforeSupersede.Evidence |> List.find (fun record -> record.Evidence.Id = "E1")
    let afterSupersedeE1 = expectOk "supersede E1" (applyTask tempRoot supersedeTask 5 (SupersedeEvidence("E1", "outdated")))
    assertEqual "supersede increments revision" 6 afterSupersedeE1.StateRevision

    let supersededRecord = afterSupersedeE1.Evidence |> List.find (fun record -> record.Evidence.Id = "E1")
    assertEqual "supersede preserves evidence content" { originalRecord with Validity = Superseded "outdated" } supersededRecord
    assertEqual "AC keeps remaining valid evidence" (Verified [ "E2" ]) afterSupersedeE1.AcceptanceCriteria.Head.State
    assertEqual "supersede leaves unrelated evidence valid" Valid (afterSupersedeE1.Evidence |> List.find (fun record -> record.Evidence.Id = "E2")).Validity

    // Invalid repetition is rejected without mutating state.
    expectRejected
        "supersede already superseded"
        "is already superseded"
        (applyTask tempRoot supersedeTask 6 (SupersedeEvidence("E1", "again")))

    expectRejected
        "supersede unknown evidence"
        "Evidence 'E9' was not found"
        (applyTask tempRoot supersedeTask 6 (SupersedeEvidence("E9", "missing")))

    expectRejected
        "supersede blank reason"
        "supersede reason must be a non-empty single line"
        (applyTask tempRoot supersedeTask 6 (SupersedeEvidence("E2", "   ")))

    assertEqual "supersede rejections are no-ops" 6 (expectOk "get supersede task" (getTask tempRoot supersedeTask)).StateRevision

    // Superseding the final valid reference invalidates the AC and blocks completion.
    let afterSupersedeE2 = expectOk "supersede E2" (applyTask tempRoot supersedeTask 6 (SupersedeEvidence("E2", "also outdated")))
    assertEqual "supersede E2 increments revision" 7 afterSupersedeE2.StateRevision
    assertEqual "AC pending after final valid ref superseded" Pending afterSupersedeE2.AcceptanceCriteria.Head.State

    expectRejected
        "complete task after AC invalidation"
        "all Acceptance Criteria must be verified"
        (applyTask tempRoot supersedeTask 7 (CompleteTask defaultHandoff))

    assertEqual "failed completion is a no-op" 7 (expectOk "get after failed completion" (getTask tempRoot supersedeTask)).StateRevision

    // --- Scenario 10: post-terminal command rejection -----------------------
    let terminalTask = "TST-12"
    expectOk "create terminal task" (createTask tempRoot (createRequest terminalTask "Terminal task")) |> ignore
    expectOk "terminal start" (applyTask tempRoot terminalTask 0 (StartWorkItem "W1")) |> ignore
    expectOk "terminal complete work" (applyTask tempRoot terminalTask 1 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "terminal add E1" (applyTask tempRoot terminalTask 2 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "done"))) |> ignore
    expectOk "terminal verify" (applyTask tempRoot terminalTask 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore

    let terminal = expectOk "terminal complete" (applyTask tempRoot terminalTask 4 (CompleteTask defaultHandoff))
    assertEqual "terminal lifecycle" "complete" terminal.Lifecycle
    assertEqual "terminal revision" 5 terminal.StateRevision

    expectRejected
        "add evidence after terminal"
        "a complete task cannot add evidence"
        (applyTask tempRoot terminalTask 5 (AddEvidence (makeEvidence "E2" EvidenceKind.Test "late")))

    expectRejected
        "verify after terminal"
        "a complete task cannot verify acceptance"
        (applyTask tempRoot terminalTask 5 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))

    assertEqual "terminal rejections are no-ops" 5 (expectOk "get terminal task" (getTask tempRoot terminalTask)).StateRevision

    // Supersession stays available after terminal so the Section 8.2 cascade can
    // reopen a Complete task that no longer satisfies CanCompleteTask.
    let terminalReopened =
        expectOk "supersede after terminal" (applyTask tempRoot terminalTask 5 (SupersedeEvidence("E1", "late")))

    assertEqual "terminal supersede increments revision" 6 terminalReopened.StateRevision
    assertEqual "terminal supersede reopens AC" Pending terminalReopened.AcceptanceCriteria.Head.State
    assertEqual "terminal supersede reopens lifecycle" "open" terminalReopened.Lifecycle

    // --- Scenario 11: recursive Work Tree create, dotted IDs, round-trip ----
    let treeTask = "TST-20"

    let treeRequest =
        { Id = treeTask
          Title = "Work tree task"
          Kind = Execution
          AcceptanceCriteria = [ "AC1", "Tree complete" ]
          WorkItems =
            [ specWith "W1" "Root" [] [ specWith "W1.1" "First child" [] [ spec "W1.1.1" "Grandchild" ]; spec "W1.2" "Second child" ]
              specWith "W2" "Second root" [ "W1.1" ] [] ] }

    let treeCreated = expectOk "create work tree" (createTask tempRoot treeRequest)
    assertEqual "tree root count" 2 treeCreated.WorkItems.Length
    assertEqual "tree root ids" [ "W1"; "W2" ] (treeCreated.WorkItems |> List.map _.Id)
    assertEqual "tree child ids" [ "W1.1"; "W1.2" ] (treeCreated.WorkItems.Head.Children |> List.map _.Id)
    assertEqual "tree grandchild id" "W1.1.1" treeCreated.WorkItems.Head.Children.Head.Children.Head.Id
    assertEqual "tree all pending" true (treeCreated.WorkItems |> List.forall (fun item -> item.State = PendingWork))
    assertEqual "tree dependsOn preserved" [ "W1.1" ] treeCreated.WorkItems.[1].DependsOn
    assertEqual "tree AC refs propagate to grandchild" [ "AC1" ] treeCreated.WorkItems.Head.Children.Head.Children.Head.AcceptanceRefs

    let treeFetched = expectOk "get work tree" (getTask tempRoot treeTask)
    assertEqual "tree fetched work items" treeCreated.WorkItems treeFetched.WorkItems

    let treeRoundTrip = expectOk "tree round trip" (treeCreated |> serialize |> deserialize)
    assertEqual "tree round trip work items" treeCreated.WorkItems treeRoundTrip.WorkItems
    assertEqual "tree round trip dependsOn" [ "W1.1" ] treeRoundTrip.WorkItems.[1].DependsOn

    let treeRaw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot treeTask)).AsObject()
    let treeRawRoot = treeRaw.["workItems"].AsArray().[0].AsObject()
    assertEqual "tree raw child count" 2 (treeRawRoot.["children"].AsArray().Count)
    assertEqual "tree raw grandchild id" "W1.1.1" (treeRawRoot.["children"].AsArray().[0].AsObject().["children"].AsArray().[0].AsObject().["id"].GetValue<string>())
    assertEqual "tree raw empty dependsOn present" 0 (treeRawRoot.["dependsOn"].AsArray().Count)
    assertEqual "tree raw second root dependsOn" "W1.1" (treeRaw.["workItems"].AsArray().[1].AsObject().["dependsOn"].AsArray().[0].GetValue<string>())

    // --- Scenario 12: WorkItem dependency rejection matrix ------------------
    let depRequest id items =
        { Id = id
          Title = "Dependency task"
          Kind = Execution
          AcceptanceCriteria = [ "AC1", "ok" ]
          WorkItems = items }

    expectRejected
        "self dependency"
        "WorkItem 'W1' cannot depend on itself"
        (createTask tempRoot (depRequest "TST-30" [ specWith "W1" "a" [ "W1" ] [] ]))

    expectRejected
        "unknown dependency"
        "WorkItem 'W1' depends on unknown WorkItem 'W9'"
        (createTask tempRoot (depRequest "TST-31" [ specWith "W1" "a" [ "W9" ] [] ]))

    expectRejected
        "ancestor dependency"
        "WorkItem 'W1.1' cannot depend on ancestor 'W1'"
        (createTask tempRoot (depRequest "TST-32" [ specWith "W1" "a" [] [ specWith "W1.1" "b" [ "W1" ] [] ] ]))

    expectRejected
        "descendant dependency"
        "WorkItem 'W1' cannot depend on descendant 'W1.1'"
        (createTask tempRoot (depRequest "TST-33" [ specWith "W1" "a" [ "W1.1" ] [ spec "W1.1" "b" ] ]))

    expectRejected
        "cyclic dependency"
        "WorkItem dependency cycle detected: W1 -> W2 -> W1"
        (createTask tempRoot (depRequest "TST-34" [ specWith "W1" "a" [ "W2" ] []; specWith "W2" "b" [ "W1" ] [] ]))

    expectRejected
        "nested cyclic dependency"
        "WorkItem dependency cycle detected: W1.1 -> W1.2 -> W1.1"
        (createTask
            tempRoot
            (depRequest
                "TST-35"
                [ specWith "W1" "root" [] [ specWith "W1.1" "a" [ "W1.2" ] []; specWith "W1.2" "b" [ "W1.1" ] [] ] ]))

    expectRejected
        "duplicate nested work item id"
        "WorkItem IDs must be unique"
        (createTask tempRoot (depRequest "TST-36" [ specWith "W1" "a" [] [ spec "W1" "dup" ] ]))

    let invalidWorkItemIds = [ "W"; "W1."; "W1..2"; "w1"; "W1.a"; "1.2" ]

    invalidWorkItemIds
    |> List.iteri (fun index badId ->
        expectRejected
            $"invalid dotted work item id {badId}"
            "work item id has an invalid format"
            (createTask tempRoot (depRequest $"TST-4{index}" [ spec badId "a" ])))

    // Cross-branch (sibling/cousin) dependencies are allowed.
    let crossBranch =
        expectOk
            "cross-branch dependency allowed"
            (createTask
                tempRoot
                (depRequest
                    "TST-38"
                    [ specWith "W1" "root" [] [ spec "W1.1" "child" ]
                      specWith "W2" "other root" [ "W1.1" ] [] ]))

    assertEqual "cross-branch dependency preserved" [ "W1.1" ] crossBranch.WorkItems.[1].DependsOn

    // --- Scenario 13: readiness and Start semantics -------------------------
    let startTask = "TST-50"

    let startRequest =
        { Id = startTask
          Title = "Start task"
          Kind = Execution
          AcceptanceCriteria = [ "AC1", "ok" ]
          WorkItems =
            [ specWith "W1" "root" [] [ spec "W1.1" "first"; spec "W1.2" "second" ]
              specWith "W2" "dependent root" [ "W1" ] [] ] }

    expectOk "create start task" (createTask tempRoot startRequest) |> ignore

    // Starting a nested child activates its still-Pending ancestors.
    let startedChild = expectOk "start W1.1" (applyTask tempRoot startTask 0 (StartWorkItem "W1.1"))
    assertEqual "start nested increments revision" 1 startedChild.StateRevision
    let startedRoot = startedChild.WorkItems |> List.find (fun item -> item.Id = "W1")
    assertEqual "start activates ancestor" ActiveWork startedRoot.State
    assertEqual "start activates child" ActiveWork (startedRoot.Children |> List.find (fun item -> item.Id = "W1.1")).State
    assertEqual "start leaves sibling pending" PendingWork (startedRoot.Children |> List.find (fun item -> item.Id = "W1.2")).State
    assertEqual "start leaves unrelated root pending" PendingWork (startedChild.WorkItems |> List.find (fun item -> item.Id = "W2")).State

    // W2 depends on W1; W1 is Active (not terminal), so W2 is not ready.
    expectRejected
        "start with unmet dependency"
        "WorkItem 'W2' is not ready: dependency 'W1' is not satisfied"
        (applyTask tempRoot startTask 1 (StartWorkItem "W2"))

    assertEqual "unmet dependency start is a no-op" 1 (expectOk "get after unmet start" (getTask tempRoot startTask)).StateRevision

    // Drive W1 to Done, then W2 becomes ready.
    expectOk "complete W1.1" (applyTask tempRoot startTask 1 (CompleteWorkItem("W1.1", { Result = "first done"; EvidenceRefs = [] }))) |> ignore
    expectOk "start W1.2" (applyTask tempRoot startTask 2 (StartWorkItem "W1.2")) |> ignore
    expectOk "complete W1.2" (applyTask tempRoot startTask 3 (CompleteWorkItem("W1.2", { Result = "second done"; EvidenceRefs = [] }))) |> ignore

    let rootDone = expectOk "complete W1" (applyTask tempRoot startTask 4 (CompleteWorkItem("W1", { Result = "root done"; EvidenceRefs = [] })))
    assertEqual "root completion revision" 5 rootDone.StateRevision
    assertEqual "root done" DoneWork (rootDone.WorkItems |> List.find (fun item -> item.Id = "W1")).State

    let dependentStarted = expectOk "start W2 after dependency done" (applyTask tempRoot startTask 5 (StartWorkItem "W2"))
    assertEqual "dependent start revision" 6 dependentStarted.StateRevision
    assertEqual "dependent active" ActiveWork (dependentStarted.WorkItems |> List.find (fun item -> item.Id = "W2")).State

    expectRejected
        "start non-pending work item"
        "WorkItem 'W1.1' must be pending before it starts"
        (applyTask tempRoot startTask 6 (StartWorkItem "W1.1"))

    expectRejected
        "start unknown work item"
        "WorkItem 'W9' was not found"
        (applyTask tempRoot startTask 6 (StartWorkItem "W9"))

    assertEqual "failed starts are no-ops" 6 (expectOk "get after failed starts" (getTask tempRoot startTask)).StateRevision

    // A child cannot start while an ancestor is Waiting. A Pending child under a
    // Done ancestor cannot arise through normal transitions, so that branch is
    // exercised directly on a crafted domain model.
    let ancestorTask = "TST-51"
    let ancestorCreated = expectOk "create ancestor task" (createTask tempRoot { startRequest with Id = ancestorTask; Title = "Ancestor task" })
    expectOk "start ancestor root" (applyTask tempRoot ancestorTask 0 (StartWorkItem "W1")) |> ignore

    let waitingAncestor =
        expectOk "wait ancestor root" (applyTask tempRoot ancestorTask 1 (WaitWorkItem("W1", ResumeCondition "external")))

    expectRejected
        "start under waiting ancestor"
        "WorkItem 'W1.1' cannot start while ancestor 'W1' is waiting"
        (applyTask tempRoot ancestorTask 2 (StartWorkItem "W1.1"))

    let craftedDone =
        { waitingAncestor with
            WorkItems =
                [ { (waitingAncestor.WorkItems |> List.find (fun item -> item.Id = "W1")) with
                      State = DoneWork
                      Result = Some "root done" } ] }

    expectDecideRejected
        "start under done ancestor"
        "cannot start while ancestor 'W1' is done"
        (decide profiles craftedDone (StartWorkItem "W1.1"))

    // --- Scenario 14: parent completion gating and payload validation -------
    let completeTask = "TST-60"

    let completeRequest =
        { Id = completeTask
          Title = "Completion task"
          Kind = Execution
          AcceptanceCriteria = [ "AC1", "ok" ]
          WorkItems = [ specWith "W1" "root" [] [ spec "W1.1" "first"; spec "W1.2" "second" ] ] }

    expectOk "create completion task" (createTask tempRoot completeRequest) |> ignore
    expectOk "start completion child" (applyTask tempRoot completeTask 0 (StartWorkItem "W1.1")) |> ignore

    expectRejected
        "complete with blank result"
        "work item result must be a non-empty single line"
        (applyTask tempRoot completeTask 1 (CompleteWorkItem("W1.1", { Result = "   "; EvidenceRefs = [] })))

    expectRejected
        "complete with multiline result"
        "work item result must be a non-empty single line"
        (applyTask tempRoot completeTask 1 (CompleteWorkItem("W1.1", { Result = "a\nb"; EvidenceRefs = [] })))

    // The first non-terminal direct child blocks the parent, whether Active or Pending.
    expectRejected
        "complete parent with active child"
        "WorkItem 'W1' cannot complete while child 'W1.1' is not terminal"
        (applyTask tempRoot completeTask 1 (CompleteWorkItem("W1", { Result = "premature"; EvidenceRefs = [] })))

    assertEqual "failed completions are no-ops" 1 (expectOk "get after failed completions" (getTask tempRoot completeTask)).StateRevision

    expectOk "complete first child" (applyTask tempRoot completeTask 1 (CompleteWorkItem("W1.1", { Result = "first done"; EvidenceRefs = [] }))) |> ignore

    expectRejected
        "complete parent with pending child"
        "WorkItem 'W1' cannot complete while child 'W1.2' is not terminal"
        (applyTask tempRoot completeTask 2 (CompleteWorkItem("W1", { Result = "premature"; EvidenceRefs = [] })))

    expectOk "start second child" (applyTask tempRoot completeTask 2 (StartWorkItem "W1.2")) |> ignore
    expectOk "complete second child" (applyTask tempRoot completeTask 3 (CompleteWorkItem("W1.2", { Result = "second done"; EvidenceRefs = [] }))) |> ignore

    let parentDone = expectOk "complete parent after children" (applyTask tempRoot completeTask 4 (CompleteWorkItem("W1", { Result = "parent done"; EvidenceRefs = [] })))
    assertEqual "parent completion revision" 5 parentDone.StateRevision
    assertEqual "parent done" DoneWork parentDone.WorkItems.Head.State
    assertEqual "parent result recorded" (Some "parent done") parentDone.WorkItems.Head.Result

    expectRejected
        "complete non-active work item"
        "WorkItem 'W1' must be active before completion"
        (applyTask tempRoot completeTask 5 (CompleteWorkItem("W1", { Result = "again"; EvidenceRefs = [] })))

    expectRejected
        "complete unknown work item"
        "WorkItem 'W9' was not found"
        (applyTask tempRoot completeTask 5 (CompleteWorkItem("W9", { Result = "x"; EvidenceRefs = [] })))

    // --- Scenario 15: Wait/Block/Resume allowed paths and payloads ----------
    let flowTask = "TST-70"
    expectOk "create flow task" (createTask tempRoot (createRequest flowTask "Flow task")) |> ignore

    expectRejected
        "wait from pending"
        "WorkItem 'W1' must be active before it waits"
        (applyTask tempRoot flowTask 0 (WaitWorkItem("W1", ResumeCondition "condition")))

    let f1 = expectOk "start flow W1" (applyTask tempRoot flowTask 0 (StartWorkItem "W1"))

    let f2 = expectOk "wait W1" (applyTask tempRoot flowTask f1.StateRevision (WaitWorkItem("W1", ResumeCondition "external input")))
    assertEqual "wait state" (WaitingWork(ResumeCondition "external input")) f2.WorkItems.Head.State

    let f2Raw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot flowTask)).AsObject()
    let f2Item = f2Raw.["workItems"].AsArray().[0].AsObject()
    assertEqual "persisted wait state" "waiting" (f2Item.["state"].GetValue<string>())
    assertEqual "persisted resume condition" "external input" (f2Item.["resumeCondition"].GetValue<string>())

    expectRejected
        "wait while waiting"
        "WorkItem 'W1' must be active before it waits"
        (applyTask tempRoot flowTask f2.StateRevision (WaitWorkItem("W1", ResumeCondition "again")))

    // Resume from Waiting, then reject a resume from Pending.
    let f3 = expectOk "resume from waiting" (applyTask tempRoot flowTask f2.StateRevision (ResumeWorkItem("W1", None)))
    assertEqual "resume from waiting pending" PendingWork f3.WorkItems.Head.State

    expectRejected
        "resume from pending"
        "WorkItem 'W1' must be waiting or blocked before it resumes"
        (applyTask tempRoot flowTask f3.StateRevision (ResumeWorkItem("W1", Some(ObservationRef "obs"))))

    let f4 = expectOk "restart W1" (applyTask tempRoot flowTask f3.StateRevision (StartWorkItem "W1"))

    expectRejected
        "wait blank condition"
        "resume condition must be a non-empty single line"
        (applyTask tempRoot flowTask f4.StateRevision (WaitWorkItem("W1", ResumeCondition "   ")))

    expectRejected
        "wait multiline condition"
        "resume condition must be a non-empty single line"
        (applyTask tempRoot flowTask f4.StateRevision (WaitWorkItem("W1", ResumeCondition "a\nb")))

    expectRejected
        "block blank blocker"
        "blocker must be a non-empty single line"
        (applyTask tempRoot flowTask f4.StateRevision (BlockWorkItem("W1", Blocker "  ")))

    let f5 = expectOk "block active W1" (applyTask tempRoot flowTask f4.StateRevision (BlockWorkItem("W1", Blocker "reviewer")))
    assertEqual "block active state" (BlockedWork(Blocker "reviewer")) f5.WorkItems.Head.State

    let f5Raw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot flowTask)).AsObject()
    let f5Item = f5Raw.["workItems"].AsArray().[0].AsObject()
    assertEqual "persisted block state" "blocked" (f5Item.["state"].GetValue<string>())
    assertEqual "persisted blocker" "reviewer" (f5Item.["blocker"].GetValue<string>())

    expectRejected
        "block while blocked"
        "WorkItem 'W1' cannot block from state blocked"
        (applyTask tempRoot flowTask f5.StateRevision (BlockWorkItem("W1", Blocker "again")))

    expectRejected
        "resume with blank observation"
        "observation reference must be a non-empty single line"
        (applyTask tempRoot flowTask f5.StateRevision (ResumeWorkItem("W1", Some(ObservationRef "   "))))

    // Resume from Blocked with a valid observation reference.
    let f6 = expectOk "resume from blocked" (applyTask tempRoot flowTask f5.StateRevision (ResumeWorkItem("W1", Some(ObservationRef "obs-1"))))
    assertEqual "resume from blocked pending" PendingWork f6.WorkItems.Head.State

    // Block is allowed from Pending.
    let f7 = expectOk "block pending W1" (applyTask tempRoot flowTask f6.StateRevision (BlockWorkItem("W1", Blocker "blocked while pending")))
    assertEqual "block pending state" (BlockedWork(Blocker "blocked while pending")) f7.WorkItems.Head.State

    let f8 = expectOk "resume blocked pending" (applyTask tempRoot flowTask f7.StateRevision (ResumeWorkItem("W1", None)))

    // Block is allowed from Waiting.
    let f9 = expectOk "restart W1 for wait" (applyTask tempRoot flowTask f8.StateRevision (StartWorkItem "W1"))
    let f10 = expectOk "wait for block-from-waiting" (applyTask tempRoot flowTask f9.StateRevision (WaitWorkItem("W1", ResumeCondition "waiting to be blocked")))
    let f11 = expectOk "block from waiting" (applyTask tempRoot flowTask f10.StateRevision (BlockWorkItem("W1", Blocker "blocked while waiting")))
    assertEqual "block from waiting state" (BlockedWork(Blocker "blocked while waiting")) f11.WorkItems.Head.State
    let f12 = expectOk "resume from waiting-block" (applyTask tempRoot flowTask f11.StateRevision (ResumeWorkItem("W1", None)))

    // Terminal states reject Wait/Block/Resume.
    let f13 = expectOk "restart W1 for completion" (applyTask tempRoot flowTask f12.StateRevision (StartWorkItem "W1"))
    let f14 = expectOk "complete W1" (applyTask tempRoot flowTask f13.StateRevision (CompleteWorkItem("W1", { Result = "flow done"; EvidenceRefs = [] })))
    assertEqual "flow done" DoneWork f14.WorkItems.Head.State

    expectRejected
        "block done work item"
        "WorkItem 'W1' cannot block from state done"
        (applyTask tempRoot flowTask f14.StateRevision (BlockWorkItem("W1", Blocker "late")))

    expectRejected
        "wait done work item"
        "WorkItem 'W1' must be active before it waits"
        (applyTask tempRoot flowTask f14.StateRevision (WaitWorkItem("W1", ResumeCondition "late")))

    expectRejected
        "resume done work item"
        "WorkItem 'W1' must be waiting or blocked before it resumes"
        (applyTask tempRoot flowTask f14.StateRevision (ResumeWorkItem("W1", None)))

    expectRejected "wait unknown work item" "WorkItem 'W9' was not found" (applyTask tempRoot flowTask f14.StateRevision (WaitWorkItem("W9", ResumeCondition "x")))
    expectRejected "block unknown work item" "WorkItem 'W9' was not found" (applyTask tempRoot flowTask f14.StateRevision (BlockWorkItem("W9", Blocker "x")))
    expectRejected "resume unknown work item" "WorkItem 'W9' was not found" (applyTask tempRoot flowTask f14.StateRevision (ResumeWorkItem("W9", None)))

    assertEqual "flow rejections are no-ops" f14.StateRevision (expectOk "get after flow rejections" (getTask tempRoot flowTask)).StateRevision

    // --- Scenario 16: guard command validation, DTO parsing, rejections ------
    let guardTask = "TST-80"
    expectOk "create guard task" (createTask tempRoot (createRequest guardTask "Guard task")) |> ignore

    let requirement kind minimumCount producerRole independent =
        EvidenceRequired
            { Kind = kind
              MinimumCount = minimumCount
              ProducerRole = producerRole
              RequireIndependentProducer = independent }

    let guardSpec id target checkpoint req =
        { Id = id
          Target = target
          Checkpoint = checkpoint
          Requirement = req
          Applicability = Always
          Waiver = NotWaivable }

    let taskStartGuard =
        guardSpec "G1" TaskTarget BeforeStart (requirement EvidenceKind.Test 1 None false)

    expectRejected
        "add guard invalid id"
        "guard id has an invalid format"
        (applyTask tempRoot guardTask 0 (AddGuard { taskStartGuard with Id = "X1" }))

    expectRejected
        "add guard unknown target"
        "guard 'G2' targets unknown WorkItem 'W9'"
        (applyTask tempRoot guardTask 0 (AddGuard { taskStartGuard with Id = "G2"; Target = WorkItemTarget "W9" }))

    expectRejected
        "add guard minimum count"
        "guard minimumCount must be at least 1"
        (applyTask tempRoot guardTask 0 (AddGuard { taskStartGuard with Requirement = requirement EvidenceKind.Test 0 None false }))

    expectRejected
        "add guard other empty kind"
        "evidence kind 'other' requires a non-empty value"
        (applyTask tempRoot guardTask 0 (AddGuard { taskStartGuard with Requirement = requirement (EvidenceKind.Other "  ") 1 None false }))

    // Policy parsers fail closed on unrecognized wire values.
    assertEqual "parse guard target task" (Ok TaskTarget) (parseGuardTarget "task")
    assertEqual "parse guard target work item" (Ok(WorkItemTarget "W1")) (parseGuardTarget "workItem:W1")
    expectRejected "parse guard target invalid work item" "work item id has an invalid format" (parseGuardTarget "workItem:bad")
    expectRejected "parse guard target unknown form" "guard target must be 'task' or 'workItem:<W-id>'" (parseGuardTarget "bogus")
    assertEqual "parse guard checkpoint before start" (Ok BeforeStart) (parseGuardCheckpoint "beforeStart")
    assertEqual "parse guard checkpoint before complete" (Ok BeforeComplete) (parseGuardCheckpoint "beforeComplete")
    expectRejected "parse guard checkpoint invalid" "guard checkpoint is not recognized" (parseGuardCheckpoint "after")
    assertEqual "parse applicability always" (Ok Always) (parseApplicability "always")
    assertEqual "parse applicability coordinator" (Ok(ExplicitDecision MinimumAuthority.CoordinatorAuthority)) (parseApplicability "explicitDecision:coordinator")
    assertEqual "parse applicability user" (Ok(ExplicitDecision MinimumAuthority.UserAuthority)) (parseApplicability "explicitDecision:user")
    expectRejected "parse applicability unknown authority" "minimum authority is not recognized" (parseApplicability "explicitDecision:bogus")
    expectRejected "parse applicability invalid" "guard applicability must be 'always' or 'explicitDecision:<authority>'" (parseApplicability "bogus")
    assertEqual "parse waiver not waivable" (Ok NotWaivable) (parseWaiver "notWaivable")
    assertEqual "parse waiver coordinator" (Ok(WaivableBy MinimumAuthority.CoordinatorAuthority)) (parseWaiver "waivableBy:coordinator")
    assertEqual "parse waiver user" (Ok(WaivableBy MinimumAuthority.UserAuthority)) (parseWaiver "waivableBy:user")
    expectRejected "parse waiver unknown authority" "minimum authority is not recognized" (parseWaiver "waivableBy:bogus")
    expectRejected "parse waiver invalid" "guard waiver must be 'notWaivable' or 'waivableBy:<authority>'" (parseWaiver "bogus")

    // A blank producer role normalizes to None rather than persisting whitespace.
    let blankRoleGuard =
        expectOk
            "add guard with blank producer role"
            (applyTask
                tempRoot
                guardTask
                0
                (AddGuard
                    { taskStartGuard with
                        Requirement = requirement EvidenceKind.Test 1 (Some "  ") false }))

    assertEqual "guard assigned origin" TaskDesign blankRoleGuard.Guards.Head.Origin
    assertEqual "guard assigned disposition" GuardDisposition.Applicable blankRoleGuard.Guards.Head.Disposition

    match blankRoleGuard.Guards.Head.Requirement with
    | EvidenceRequired stored -> assertEqual "blank guard producer role normalized" None stored.ProducerRole

    expectRejected
        "add duplicate guard"
        "guard 'G1' already exists"
        (applyTask tempRoot guardTask 1 (AddGuard taskStartGuard))

    assertEqual "duplicate guard is a no-op" 1 (expectOk "get after duplicate guard" (getTask tempRoot guardTask)).StateRevision

    let concreteGuard =
        { Id = "G3"
          Target = TaskTarget
          Checkpoint = BeforeStart
          Origin = TaskDesign
          Requirement = requirement EvidenceKind.Test 1 None false
          Applicability = Always
          Waiver = NotWaivable
          Disposition = GuardDisposition.Applicable }

    expectRejected
        "guard added event is command-rejected"
        "guard addition events are produced by decide and cannot be applied as commands"
        (applyTask tempRoot guardTask 1 (GuardAdded concreteGuard))

    expectRejected
        "add guard after completion"
        "a complete task cannot add a guard"
        (applyTask tempRoot "TST-1" 5 (AddGuard taskStartGuard))

    // The guard survives a full serialize/deserialize round trip and the sidecar.
    let guardRoundTrip = expectOk "guard round trip" (blankRoleGuard |> serialize |> deserialize)
    assertEqual "guard round trip id" "G1" guardRoundTrip.Guards.Head.Id
    assertEqual "guard round trip target" TaskTarget guardRoundTrip.Guards.Head.Target
    assertEqual "guard round trip checkpoint" BeforeStart guardRoundTrip.Guards.Head.Checkpoint
    assertEqual "guard round trip origin" TaskDesign guardRoundTrip.Guards.Head.Origin

    let guardRaw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot guardTask)).AsObject()
    assertEqual "persisted guard count" 1 (guardRaw.["guards"].AsArray().Count)
    let guardRaw0 = guardRaw.["guards"].AsArray().[0].AsObject()
    assertEqual "persisted guard target" "task" (guardRaw0.["target"].GetValue<string>())
    assertEqual "persisted guard checkpoint" "beforeStart" (guardRaw0.["checkpoint"].GetValue<string>())
    assertEqual "persisted guard requirement" "evidenceRequired" (guardRaw0.["requirement"].GetValue<string>())

    // Strict Guard DTO parsing rejects unknown, missing, and malformed fields.
    let guardNode (mutate: JsonObject -> unit) =
        let guard =
            JsonNode.Parse("""{"id":"G1","target":"task","checkpoint":"beforeComplete","origin":"taskDesign","requirement":"evidenceRequired","evidenceKind":"test","minimumCount":1,"producerRole":"","requireIndependentProducer":false,"applicability":"always","waiver":"notWaivable","disposition":"applicable"}""").AsObject()

        mutate guard
        guard

    let taskWithGuard (guard: JsonObject) =
        let task = JsonNode.Parse(baselineJson).AsObject()
        let guards = JsonArray()
        guards.Add guard
        task.["guards"] <- guards
        task.ToJsonString()

    expectOk "guard fixture parses" (deserialize (taskWithGuard (guardNode ignore))) |> ignore

    let guardUnknownProperty = taskWithGuard (guardNode (fun node -> node.["extra"] <- JsonValue.Create 1))
    let guardMissingProperty = taskWithGuard (guardNode (fun node -> node.Remove "waiver" |> ignore))
    let guardInvalidTarget = taskWithGuard (guardNode (fun node -> node.["target"] <- JsonValue.Create "bogus"))
    let guardUnknownTargetItem = taskWithGuard (guardNode (fun node -> node.["target"] <- JsonValue.Create "workItem:W9"))
    let guardInvalidCheckpoint = taskWithGuard (guardNode (fun node -> node.["checkpoint"] <- JsonValue.Create "after"))
    let guardInvalidOrigin = taskWithGuard (guardNode (fun node -> node.["origin"] <- JsonValue.Create "bogus"))
    let guardUnsupportedRequirement = taskWithGuard (guardNode (fun node -> node.["requirement"] <- JsonValue.Create "decisionRequired"))
    let guardUnknownKind = taskWithGuard (guardNode (fun node -> node.["evidenceKind"] <- JsonValue.Create "magic"))
    let guardBadMinimum = taskWithGuard (guardNode (fun node -> node.["minimumCount"] <- JsonValue.Create 0))
    let guardWrongMinimumType = taskWithGuard (guardNode (fun node -> node.["minimumCount"] <- JsonValue.Create "one"))
    let guardWrongIndependentType = taskWithGuard (guardNode (fun node -> node.["requireIndependentProducer"] <- JsonValue.Create "yes"))
    let guardInvalidApplicability = taskWithGuard (guardNode (fun node -> node.["applicability"] <- JsonValue.Create "bogus"))
    let guardInvalidWaiver = taskWithGuard (guardNode (fun node -> node.["waiver"] <- JsonValue.Create "bogus"))
    let guardInvalidDisposition = taskWithGuard (guardNode (fun node -> node.["disposition"] <- JsonValue.Create "bogus"))
    let guardInvalidId = taskWithGuard (guardNode (fun node -> node.["id"] <- JsonValue.Create "X1"))

    let duplicateGuards =
        let task = JsonNode.Parse(baselineJson).AsObject()
        let guards = JsonArray()
        guards.Add(guardNode ignore)
        guards.Add(guardNode (fun node -> node.["id"] <- JsonValue.Create "G1"))
        task.["guards"] <- guards
        task.ToJsonString()

    expectRejectedDeserialize "guard unknown property" "guard contains unknown property 'extra'" guardUnknownProperty
    expectRejectedDeserialize "guard missing property" "guard is missing property 'waiver'" guardMissingProperty
    expectRejectedDeserialize "guard invalid target" "guard target must be 'task' or 'workItem:<W-id>'" guardInvalidTarget
    expectRejectedDeserialize "guard unknown target work item" "Guard references unknown WorkItem 'W9'" guardUnknownTargetItem
    expectRejectedDeserialize "guard invalid checkpoint" "guard checkpoint is not recognized" guardInvalidCheckpoint
    expectRejectedDeserialize "guard invalid origin" "guard origin is not recognized" guardInvalidOrigin
    expectRejectedDeserialize "guard unsupported requirement" "guard 'G1' has an unsupported requirement" guardUnsupportedRequirement
    expectRejectedDeserialize "guard unknown evidence kind" "evidence kind is not recognized" guardUnknownKind
    expectRejectedDeserialize "guard minimum count zero" "guard 'G1' minimumCount must be at least 1" guardBadMinimum
    expectRejectedDeserialize "guard wrong minimum count type" "property 'minimumCount' must be an integer" guardWrongMinimumType
    expectRejectedDeserialize "guard wrong independent type" "property 'requireIndependentProducer' must be a boolean" guardWrongIndependentType
    expectRejectedDeserialize "guard invalid applicability" "guard applicability must be 'always' or 'explicitDecision:<authority>'" guardInvalidApplicability
    expectRejectedDeserialize "guard invalid waiver" "guard waiver must be 'notWaivable' or 'waivableBy:<authority>'" guardInvalidWaiver
    expectRejectedDeserialize "guard invalid disposition" "guard disposition must be 'applicable', 'notApplicable:<ref>', or 'waived:<ref>'" guardInvalidDisposition
    expectRejectedDeserialize "guard invalid id" "guard id has an invalid format" guardInvalidId
    expectRejectedDeserialize "duplicate guard ids" "Guard IDs must be unique" duplicateGuards

    // --- Scenario 17: BeforeStart guard enforcement -------------------------
    let beforeStartTask = "TST-81"
    expectOk "create before-start task" (createTask tempRoot (createRequest beforeStartTask "Before-start task")) |> ignore
    expectOk
        "add task before-start guard"
        (applyTask tempRoot beforeStartTask 0 (AddGuard (guardSpec "G1" TaskTarget BeforeStart (requirement EvidenceKind.Test 1 None false))))
    |> ignore

    expectRejected
        "start blocked by task before-start guard"
        "WorkItem 'W1' is not ready: guard 'G1' is not satisfied"
        (applyTask tempRoot beforeStartTask 1 (StartWorkItem "W1"))

    assertEqual "blocked start is a no-op" 1 (expectOk "get blocked start" (getTask tempRoot beforeStartTask)).StateRevision

    expectOk "add before-start evidence" (applyTask tempRoot beforeStartTask 1 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "test evidence"))) |> ignore

    let beforeStartStarted =
        expectOk "start after before-start guard satisfied" (applyTask tempRoot beforeStartTask 2 (StartWorkItem "W1"))

    assertEqual "before-start satisfied activates work" ActiveWork beforeStartStarted.WorkItems.Head.State

    // WorkItemTarget BeforeStart guards are target-scoped: task-level Evidence
    // does not satisfy them, and completion-time refs cannot be attached before
    // start, so they fail closed until the item references matching Evidence.
    let itemStartTask = "TST-82"
    expectOk "create item before-start task" (createTask tempRoot (createRequest itemStartTask "Item before-start task")) |> ignore
    expectOk
        "add item before-start guard"
        (applyTask tempRoot itemStartTask 0 (AddGuard (guardSpec "G1" (WorkItemTarget "W1") BeforeStart (requirement EvidenceKind.Test 1 None false))))
    |> ignore
    expectOk "add item before-start evidence" (applyTask tempRoot itemStartTask 1 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "task-level test"))) |> ignore

    expectRejected
        "item before-start guard ignores task-level evidence"
        "WorkItem 'W1' is not ready: guard 'G1' is not satisfied"
        (applyTask tempRoot itemStartTask 2 (StartWorkItem "W1"))

    assertEqual "item before-start block is a no-op" 2 (expectOk "get item before-start" (getTask tempRoot itemStartTask)).StateRevision

    // --- Scenario 18: BeforeComplete WorkItem guard and evidence persistence -
    let completeGuardTask = "TST-83"
    expectOk "create complete-guard task" (createTask tempRoot (createRequest completeGuardTask "Complete guard task")) |> ignore
    expectOk
        "add work before-complete guard"
        (applyTask tempRoot completeGuardTask 0 (AddGuard (guardSpec "G1" (WorkItemTarget "W1") BeforeComplete (requirement EvidenceKind.Test 1 None false))))
    |> ignore
    expectOk "add complete-guard evidence" (applyTask tempRoot completeGuardTask 1 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "completion test"))) |> ignore
    expectOk "start complete-guard work" (applyTask tempRoot completeGuardTask 2 (StartWorkItem "W1")) |> ignore

    expectRejected
        "complete work without guard evidence"
        "WorkItem 'W1' cannot complete while guard 'G1' is not satisfied"
        (applyTask tempRoot completeGuardTask 3 (CompleteWorkItem("W1", { Result = "no evidence"; EvidenceRefs = [] })))

    assertEqual "guard-blocked completion is a no-op" 3 (expectOk "get guard-blocked completion" (getTask tempRoot completeGuardTask)).StateRevision

    let completionWithEvidence =
        expectOk
            "complete work with guard evidence"
            (applyTask tempRoot completeGuardTask 3 (CompleteWorkItem("W1", { Result = "evidence attached"; EvidenceRefs = [ "E1" ] })))

    assertEqual "completion with evidence done" DoneWork completionWithEvidence.WorkItems.Head.State
    assertEqual "completion persists evidence refs" [ "E1" ] completionWithEvidence.WorkItems.Head.EvidenceRefs

    let completionRaw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot completeGuardTask)).AsObject()
    let completionRawItem = completionRaw.["workItems"].AsArray().[0].AsObject()
    assertEqual "persisted completion evidence refs" "E1" (completionRawItem.["evidenceRefs"].AsArray().[0].GetValue<string>())

    let reloadedCompletion = expectOk "reload completion evidence" (getTask tempRoot completeGuardTask)
    assertEqual "reloaded completion evidence refs" [ "E1" ] reloadedCompletion.WorkItems.Head.EvidenceRefs

    // Completion evidence validation fails closed for unknown, malformed, and
    // superseded references before any state change.
    let completionEvidenceTask = "TST-84"
    expectOk "create completion evidence task" (createTask tempRoot (createRequest completionEvidenceTask "Completion evidence task")) |> ignore
    expectOk "start completion evidence work" (applyTask tempRoot completionEvidenceTask 0 (StartWorkItem "W1")) |> ignore

    expectRejected
        "complete with unknown evidence"
        "completion references unknown Evidence 'E9'"
        (applyTask tempRoot completionEvidenceTask 1 (CompleteWorkItem("W1", { Result = "x"; EvidenceRefs = [ "E9" ] })))

    expectRejected
        "complete with malformed evidence id"
        "evidence id has an invalid format"
        (applyTask tempRoot completionEvidenceTask 1 (CompleteWorkItem("W1", { Result = "x"; EvidenceRefs = [ "bad" ] })))

    expectOk "add superseded completion evidence" (applyTask tempRoot completionEvidenceTask 1 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "will be superseded"))) |> ignore
    expectOk "supersede completion evidence" (applyTask tempRoot completionEvidenceTask 2 (SupersedeEvidence("E1", "stale"))) |> ignore

    expectRejected
        "complete with superseded evidence"
        "completion references superseded Evidence 'E1'"
        (applyTask tempRoot completionEvidenceTask 3 (CompleteWorkItem("W1", { Result = "x"; EvidenceRefs = [ "E1" ] })))

    assertEqual "completion evidence rejections are no-ops" 3 (expectOk "get completion evidence task" (getTask tempRoot completionEvidenceTask)).StateRevision

    // --- Scenario 19: Task BeforeComplete guard and completion predicate -----
    let predicateTask = "TST-85"
    expectOk "create predicate task" (createTask tempRoot (createRequest predicateTask "Predicate task")) |> ignore
    expectOk
        "add task before-complete guard"
        (applyTask tempRoot predicateTask 0 (AddGuard (guardSpec "G1" TaskTarget BeforeComplete (requirement EvidenceKind.Review 1 None false))))
    |> ignore
    expectOk "add predicate AC evidence" (applyTask tempRoot predicateTask 1 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "ac evidence"))) |> ignore
    expectOk "start predicate work" (applyTask tempRoot predicateTask 2 (StartWorkItem "W1")) |> ignore
    expectOk "complete predicate work" (applyTask tempRoot predicateTask 3 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "verify predicate AC" (applyTask tempRoot predicateTask 4 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore

    let beforeReview = expectOk "get predicate task" (getTask tempRoot predicateTask)
    assertTrue "CanCompleteTask false while guard unsatisfied" (not (canCompleteTask beforeReview))

    expectRejected
        "complete task with unsatisfied guard"
        "guard 'G1' is not satisfied"
        (applyTask tempRoot predicateTask 5 (CompleteTask defaultHandoff))

    assertEqual "guard-blocked task completion is a no-op" 5 (expectOk "get guard-blocked task" (getTask tempRoot predicateTask)).StateRevision

    expectOk "add predicate review evidence" (applyTask tempRoot predicateTask 5 (AddEvidence (makeEvidence "E2" EvidenceKind.Review "review evidence"))) |> ignore

    let afterReview = expectOk "get predicate task after review" (getTask tempRoot predicateTask)
    assertTrue "CanCompleteTask true once guard satisfied" (canCompleteTask afterReview)

    let predicateComplete = expectOk "complete predicate task" (applyTask tempRoot predicateTask 6 (CompleteTask defaultHandoff))
    assertEqual "predicate task complete" "complete" predicateComplete.Lifecycle

    // The pure predicate tracks terminal work and verified acceptance too.
    let predicateOrderTask = "TST-86"
    expectOk "create predicate order task" (createTask tempRoot (createRequest predicateOrderTask "Predicate order task")) |> ignore
    let freshTask = expectOk "get fresh predicate order" (getTask tempRoot predicateOrderTask)
    assertTrue "fresh task cannot complete" (not (canCompleteTask freshTask))

    expectOk "add predicate order evidence" (applyTask tempRoot predicateOrderTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "done"))) |> ignore
    expectOk "verify predicate order AC" (applyTask tempRoot predicateOrderTask 1 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore

    let acVerified = expectOk "get ac-verified task" (getTask tempRoot predicateOrderTask)
    assertTrue "non-terminal work blocks predicate" (not (canCompleteTask acVerified))

    expectRejected
        "complete with pending work"
        "all WorkItems must be done before task completion"
        (applyTask tempRoot predicateOrderTask 2 (CompleteTask defaultHandoff))

    expectOk "start predicate order work" (applyTask tempRoot predicateOrderTask 2 (StartWorkItem "W1")) |> ignore
    expectOk "complete predicate order work" (applyTask tempRoot predicateOrderTask 3 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore

    let allTerminal = expectOk "get terminal predicate order" (getTask tempRoot predicateOrderTask)
    assertTrue "terminal and verified predicate true" (canCompleteTask allTerminal)
    expectOk "complete predicate order task" (applyTask tempRoot predicateOrderTask 4 (CompleteTask defaultHandoff)) |> ignore

    // NotApplicable / Waived dispositions satisfy the mechanical predicate
    // without the underlying Evidence requirement, but only when backed by an
    // exact target-bound Coordinator Decision the guard policy permits.
    let dispositionTask = "TST-87"
    expectOk "create disposition task" (createTask tempRoot (createRequest dispositionTask "Disposition task")) |> ignore
    expectOk
        "add disposition guard"
        (applyTask tempRoot dispositionTask 0 (AddGuard (guardSpec "G1" TaskTarget BeforeComplete (requirement EvidenceKind.Review 1 None false))))
    |> ignore
    expectOk "add disposition evidence" (applyTask tempRoot dispositionTask 1 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "ac evidence"))) |> ignore
    expectOk "start disposition work" (applyTask tempRoot dispositionTask 2 (StartWorkItem "W1")) |> ignore
    expectOk "complete disposition work" (applyTask tempRoot dispositionTask 3 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "verify disposition AC" (applyTask tempRoot dispositionTask 4 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore

    let dispositionOpen = expectOk "get disposition task" (getTask tempRoot dispositionTask)
    assertTrue "disposition guard unmet predicate false" (not (canCompleteTask dispositionOpen))

    // Section 9.2: a disposition is authorized only by its own Decision kind, so
    // the fixture emits the matching kind for each disposition under test.
    let dispositionJson (dispositionText: string) (applicabilityText: string) (waiverText: string) (decisionKind: string) =
        let node = JsonNode.Parse(serialize dispositionOpen).AsObject()
        let guard = node.["guards"].AsArray().[0].AsObject()
        guard.["disposition"] <- JsonValue.Create dispositionText
        guard.["applicability"] <- JsonValue.Create applicabilityText
        guard.["waiver"] <- JsonValue.Create waiverText
        let decisions = JsonArray()

        decisions.Add(
            JsonNode.Parse(
                $"""{{"id":"D1","authority":"coordinator","kind":"{decisionKind}","targets":["guardDisposition:G1"],"rationale":"disposition","createdAt":"2026-09-10T00:00:00.0000000+00:00","confirmationRef":""}}"""
            )
        )

        node.["decisions"] <- decisions
        node.ToJsonString()

    let notApplicableGuard =
        expectOk
            "parse NotApplicable disposition"
            (deserialize (dispositionJson "notApplicable:D1" "explicitDecision:coordinator" "notWaivable" "applicabilityDecision"))

    assertEqual "NotApplicable disposition parsed" (GuardDisposition.NotApplicable "D1") notApplicableGuard.Guards.Head.Disposition
    assertEqual "NotApplicable decision kind" ApplicabilityDecision notApplicableGuard.Decisions.Head.Kind
    assertTrue "NotApplicable guard satisfies predicate" (canCompleteTask notApplicableGuard)

    let waivedGuard =
        expectOk
            "parse Waived disposition"
            (deserialize (dispositionJson "waived:D1" "always" "waivableBy:coordinator" "waiverDecision"))

    assertEqual "Waived disposition parsed" (GuardDisposition.Waived "D1") waivedGuard.Guards.Head.Disposition
    assertEqual "Waived decision kind" WaiverDecision waivedGuard.Decisions.Head.Kind
    assertTrue "Waived guard satisfies predicate" (canCompleteTask waivedGuard)

    // A same-target Decision of the wrong kind never authorizes the disposition:
    // the exact defect the disposition-specific DecisionKind fix closes.
    expectRejectedDeserialize
        "waiver disposition rejects applicabilityDecision"
        "does not authorize the exact Guard target at coordinator authority"
        (dispositionJson "waived:D1" "always" "waivableBy:coordinator" "applicabilityDecision")

    expectRejectedDeserialize
        "notApplicable disposition rejects waiverDecision"
        "does not authorize the exact Guard target at coordinator authority"
        (dispositionJson "notApplicable:D1" "explicitDecision:coordinator" "notWaivable" "waiverDecision")

    // --- Scenario 20: independent-producer requirement fails closed ----------
    let independentTask = "TST-88"
    expectOk "create independent task" (createTask tempRoot (createRequest independentTask "Independent task")) |> ignore
    expectOk
        "add independent work guard"
        (applyTask
            tempRoot
            independentTask
            0
            (AddGuard
                (guardSpec "G1" (WorkItemTarget "W1") BeforeComplete (requirement EvidenceKind.Review 1 (Some "reviewer") true))))
    |> ignore

    let reviewerEvidence =
        { makeEvidence "E1" EvidenceKind.Review "independent review" with
            ProducerRole = Some "reviewer"
            ProducerId = Some "agent-1" }

    expectOk "add reviewer evidence" (applyTask tempRoot independentTask 1 (AddEvidence reviewerEvidence)) |> ignore
    expectOk "start independent work" (applyTask tempRoot independentTask 2 (StartWorkItem "W1")) |> ignore

    expectRejected
        "independent guard fails closed"
        "WorkItem 'W1' cannot complete while guard 'G1' is not satisfied"
        (applyTask tempRoot independentTask 3 (CompleteWorkItem("W1", { Result = "reviewed"; EvidenceRefs = [ "E1" ] })))

    assertEqual "independent guard rejection is a no-op" 3 (expectOk "get independent task" (getTask tempRoot independentTask)).StateRevision

    // The same requirement without independence is satisfied by matching Evidence.
    let nonIndependentTask = "TST-89"
    expectOk "create non-independent task" (createTask tempRoot (createRequest nonIndependentTask "Non-independent task")) |> ignore
    expectOk
        "add non-independent work guard"
        (applyTask
            tempRoot
            nonIndependentTask
            0
            (AddGuard
                (guardSpec "G1" (WorkItemTarget "W1") BeforeComplete (requirement EvidenceKind.Review 1 (Some "reviewer") false))))
    |> ignore
    expectOk "add non-independent reviewer evidence" (applyTask tempRoot nonIndependentTask 1 (AddEvidence reviewerEvidence)) |> ignore
    expectOk "start non-independent work" (applyTask tempRoot nonIndependentTask 2 (StartWorkItem "W1")) |> ignore

    let nonIndependentComplete =
        expectOk
            "non-independent guard satisfied"
            (applyTask tempRoot nonIndependentTask 3 (CompleteWorkItem("W1", { Result = "reviewed"; EvidenceRefs = [ "E1" ] })))

    assertEqual "non-independent completion done" DoneWork nonIndependentComplete.WorkItems.Head.State

    // Task-target independence also fails closed despite matching task Evidence.
    let independentStartTask = "TST-90"
    expectOk "create independent start task" (createTask tempRoot (createRequest independentStartTask "Independent start task")) |> ignore
    expectOk
        "add independent task before-start guard"
        (applyTask
            tempRoot
            independentStartTask
            0
            (AddGuard (guardSpec "G1" TaskTarget BeforeStart (requirement EvidenceKind.Review 1 (Some "reviewer") true))))
    |> ignore
    expectOk "add independent task evidence" (applyTask tempRoot independentStartTask 1 (AddEvidence reviewerEvidence)) |> ignore

    expectRejected
        "independent task before-start fails closed"
        "WorkItem 'W1' is not ready: guard 'G1' is not satisfied"
        (applyTask tempRoot independentStartTask 2 (StartWorkItem "W1"))

    // --- Scenario 21: guard CAS and no-op revisions --------------------------
    let guardCasTask = "TST-93"
    expectOk "create guard CAS task" (createTask tempRoot (createRequest guardCasTask "Guard CAS task")) |> ignore
    expectOk
        "add guard CAS guard"
        (applyTask tempRoot guardCasTask 0 (AddGuard (guardSpec "G1" TaskTarget BeforeStart (requirement EvidenceKind.Test 1 None false))))
    |> ignore

    match applyTask tempRoot guardCasTask 0 (AddGuard (guardSpec "G2" TaskTarget BeforeStart (requirement EvidenceKind.Test 1 None false))) with
    | Error (Conflict (expected, actual)) ->
        assertEqual "guard CAS expected" 0 expected
        assertEqual "guard CAS actual" 1 actual
    | other -> failwithf "guard CAS should conflict, got %A" other

    let guardCasAfter = expectOk "get guard CAS task" (getTask tempRoot guardCasTask)
    assertEqual "guard CAS did not bump revision" 1 guardCasAfter.StateRevision
    assertEqual "guard CAS did not add guard" 1 guardCasAfter.Guards.Length

    expectRejected
        "rejected guard command is a no-op"
        "guard 'G2' targets unknown WorkItem 'W9'"
        (applyTask tempRoot guardCasTask 1 (AddGuard (guardSpec "G2" (WorkItemTarget "W9") BeforeStart (requirement EvidenceKind.Test 1 None false))))

    assertEqual "rejected guard command did not bump revision" 1 (expectOk "get after rejected guard" (getTask tempRoot guardCasTask)).StateRevision

    // --- Scenario 22: supersession cascade reopens AC/WorkItem/parent/task ---
    let cascadeTask = "TST-94"
    let cascadeRequest =
        { Id = cascadeTask
          Title = "Cascade task"
          Kind = Execution
          AcceptanceCriteria = [ "AC1", "Cascade complete" ]
          WorkItems = [ specWith "W1" "root" [] [ spec "W1.1" "child" ] ] }

    expectOk "create cascade task" (createTask tempRoot cascadeRequest) |> ignore
    expectOk
        "add cascade child guard"
        (applyTask tempRoot cascadeTask 0 (AddGuard (guardSpec "G1" (WorkItemTarget "W1.1") BeforeComplete (requirement EvidenceKind.Test 1 None false))))
    |> ignore
    expectOk
        "add cascade task guard"
        (applyTask tempRoot cascadeTask 1 (AddGuard (guardSpec "G2" TaskTarget BeforeComplete (requirement EvidenceKind.Test 1 None false))))
    |> ignore
    expectOk "add cascade guard evidence" (applyTask tempRoot cascadeTask 2 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "guard test"))) |> ignore
    expectOk "add cascade AC evidence" (applyTask tempRoot cascadeTask 3 (AddEvidence (makeEvidence "E2" EvidenceKind.Build "ac build"))) |> ignore
    expectOk "start cascade child" (applyTask tempRoot cascadeTask 4 (StartWorkItem "W1.1")) |> ignore
    expectOk
        "complete cascade child"
        (applyTask tempRoot cascadeTask 5 (CompleteWorkItem("W1.1", { Result = "child done"; EvidenceRefs = [ "E1" ] })))
    |> ignore
    expectOk "complete cascade root" (applyTask tempRoot cascadeTask 6 (CompleteWorkItem("W1", { Result = "root done"; EvidenceRefs = [] }))) |> ignore
    expectOk "verify cascade AC" (applyTask tempRoot cascadeTask 7 (VerifyAcceptanceCriterion("AC1", [ "E2" ]))) |> ignore

    let cascadeReady = expectOk "get cascade task" (getTask tempRoot cascadeTask)
    assertTrue "cascade task can complete" (canCompleteTask cascadeReady)
    expectOk "complete cascade task" (applyTask tempRoot cascadeTask 8 (CompleteTask defaultHandoff)) |> ignore

    let cascadeComplete = expectOk "get completed cascade" (getTask tempRoot cascadeTask)
    assertEqual "cascade lifecycle complete" "complete" cascadeComplete.Lifecycle
    assertEqual "cascade child done before supersede" DoneWork cascadeComplete.WorkItems.Head.Children.Head.State

    let afterCascade = expectOk "supersede cascade guard evidence" (applyTask tempRoot cascadeTask 9 (SupersedeEvidence("E1", "guard evidence stale")))
    assertEqual "cascade supersede revision" 10 afterCascade.StateRevision
    assertEqual "cascade AC keeps AC evidence" (Verified [ "E2" ]) afterCascade.AcceptanceCriteria.Head.State
    assertEqual "cascade child reopened" PendingWork afterCascade.WorkItems.Head.Children.Head.State
    assertEqual "cascade child result cleared" None afterCascade.WorkItems.Head.Children.Head.Result
    assertEqual "cascade parent reopened" PendingWork afterCascade.WorkItems.Head.State
    assertEqual "cascade lifecycle reopened" "open" afterCascade.Lifecycle

    // Superseding Evidence that no AC or Guard relies on leaves a Complete task intact.
    let stableTask = "TST-95"
    expectOk "create stable task" (createTask tempRoot (createRequest stableTask "Stable task")) |> ignore
    expectOk "add stable AC evidence" (applyTask tempRoot stableTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "ac evidence"))) |> ignore
    expectOk "add stable unrelated evidence" (applyTask tempRoot stableTask 1 (AddEvidence (makeEvidence "E2" EvidenceKind.Observation "unrelated"))) |> ignore
    expectOk "start stable work" (applyTask tempRoot stableTask 2 (StartWorkItem "W1")) |> ignore
    expectOk "complete stable work" (applyTask tempRoot stableTask 3 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "verify stable AC" (applyTask tempRoot stableTask 4 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore
    expectOk "complete stable task" (applyTask tempRoot stableTask 5 (CompleteTask defaultHandoff)) |> ignore

    let afterUnrelatedSupersede = expectOk "supersede unrelated evidence" (applyTask tempRoot stableTask 6 (SupersedeEvidence("E2", "unrelated stale")))
    assertEqual "unrelated supersede keeps lifecycle" "complete" afterUnrelatedSupersede.Lifecycle
    assertEqual "unrelated supersede keeps AC" (Verified [ "E1" ]) afterUnrelatedSupersede.AcceptanceCriteria.Head.State
    assertEqual "unrelated supersede keeps work done" DoneWork afterUnrelatedSupersede.WorkItems.Head.State

    // --- Scenario 19: Decisions and Open Questions (slice 6) -----------------
    // Section 9/10: strict Decision/Question DTOs, decision-graph validation,
    // typed target matching, TaskWide vs WorkItem-scoped blocking, resolution,
    // and revision/no-op/persistence behavior.
    let decisionNode (mutate: JsonObject -> unit) =
        let node =
            JsonNode.Parse(
                """{"id":"D1","authority":"coordinator","kind":"designDecision","targets":["questionResolution:Q1"],"rationale":"resolve Q1","createdAt":"2026-09-10T00:00:00.0000000+00:00","confirmationRef":""}"""
            ).AsObject()

        mutate node
        node

    let questionNode (mutate: JsonObject -> unit) =
        let node =
            JsonNode.Parse("""{"id":"Q1","text":"Which path?","impact":"taskWide","state":"open"}""").AsObject()

        mutate node
        node

    let taskWithDecisionsQuestions (decisions: JsonObject list) (questions: JsonObject list) =
        let task = JsonNode.Parse(baselineJson).AsObject()

        if not decisions.IsEmpty then
            let array = JsonArray()
            decisions |> List.iter (fun node -> array.Add node)
            task.["decisions"] <- array

        if not questions.IsEmpty then
            let array = JsonArray()
            questions |> List.iter (fun node -> array.Add node)
            task.["questions"] <- array

        task.ToJsonString()

    // A self-consistent Decision/Question pair must parse so later rejections mean something.
    expectOk
        "decision/question fixture parses"
        (deserialize (taskWithDecisionsQuestions [ decisionNode ignore ] [ questionNode ignore ]))
    |> ignore

    // Decision DTO strictness: unknown/missing/mistyped fields fail closed.
    let decisionUnknownProperty =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["extra"] <- JsonValue.Create 1) ] [ questionNode ignore ]

    let decisionMissingProperty =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.Remove "confirmationRef" |> ignore) ] [ questionNode ignore ]

    let decisionWrongIdType =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["id"] <- JsonValue.Create 7) ] [ questionNode ignore ]

    let decisionInvalidId =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["id"] <- JsonValue.Create "X1") ] [ questionNode ignore ]

    let decisionInvalidAuthority =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["authority"] <- JsonValue.Create "bogus") ] [ questionNode ignore ]

    let decisionInvalidKind =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["kind"] <- JsonValue.Create "bogus") ] [ questionNode ignore ]

    let decisionEmptyTargets =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse "[]") ] [ questionNode ignore ]

    let decisionTargetsNotArray =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonValue.Create "questionResolution:Q1") ] [ questionNode ignore ]

    let decisionTargetNotString =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse "[1]") ] [ questionNode ignore ]

    let decisionTargetUnknownForm =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["bogus"]""") ] [ questionNode ignore ]

    let decisionTargetUnknownQuestion =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["questionResolution:Q9"]""") ] [ questionNode ignore ]

    let decisionTargetUnknownAcceptance =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["waiveAcceptance:AC9"]""") ] [ questionNode ignore ]

    let decisionTargetUnknownGuard =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["guardDisposition:G9"]""") ] [ questionNode ignore ]

    let decisionTargetUnknownWorkItem =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["skipWorkItem:W9"]""") ] [ questionNode ignore ]

    let decisionBlankRationale =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["rationale"] <- JsonValue.Create "   ") ] [ questionNode ignore ]

    let decisionBadTimestamp =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["createdAt"] <- JsonValue.Create "not-a-time") ] [ questionNode ignore ]

    let decisionBlankOtherTarget =
        taskWithDecisionsQuestions [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["other:"]""") ] [ questionNode ignore ]

    let duplicateDecisions =
        taskWithDecisionsQuestions
            [ decisionNode ignore; decisionNode (fun n -> n.["id"] <- JsonValue.Create "D1") ]
            [ questionNode ignore ]

    expectRejectedDeserialize "decision unknown property" "decision contains unknown property 'extra'" decisionUnknownProperty
    expectRejectedDeserialize "decision missing property" "decision is missing property 'confirmationRef'" decisionMissingProperty
    expectRejectedDeserialize "decision wrong id type" "property 'id' must be a string" decisionWrongIdType
    expectRejectedDeserialize "decision invalid id" "decision id has an invalid format" decisionInvalidId
    expectRejectedDeserialize "decision invalid authority" "decision authority is not recognized" decisionInvalidAuthority
    expectRejectedDeserialize "decision invalid kind" "decision kind is not recognized" decisionInvalidKind
    expectRejectedDeserialize "decision empty targets" "decision 'D1' requires at least one target" decisionEmptyTargets
    expectRejectedDeserialize "decision targets not array" "property 'targets' must be an array" decisionTargetsNotArray
    expectRejectedDeserialize "decision target not string" "array 'targets' must contain only strings" decisionTargetNotString
    expectRejectedDeserialize "decision target unknown form" "decision target is not recognized" decisionTargetUnknownForm
    expectRejectedDeserialize "decision target unknown question" "decision targets unknown Question 'Q9'" decisionTargetUnknownQuestion
    expectRejectedDeserialize "decision target unknown acceptance" "decision targets unknown Acceptance Criterion 'AC9'" decisionTargetUnknownAcceptance
    expectRejectedDeserialize "decision target unknown guard" "decision targets unknown Guard 'G9'" decisionTargetUnknownGuard
    expectRejectedDeserialize "decision target unknown work item" "decision targets unknown WorkItem 'W9'" decisionTargetUnknownWorkItem
    expectRejectedDeserialize "decision blank rationale" "decision rationale must be a non-empty single line" decisionBlankRationale
    expectRejectedDeserialize "decision bad timestamp" "decision 'D1' createdAt must be an ISO-8601 timestamp" decisionBadTimestamp
    expectRejectedDeserialize "decision blank other target" "decision target must be a non-empty single line" decisionBlankOtherTarget
    expectRejectedDeserialize "duplicate decision ids" "Decision IDs must be unique" duplicateDecisions

    // A non-empty Other target parses and preserves its open semantic value.
    let otherTargetParsed =
        expectOk
            "decision other target parses"
            (deserialize (
                taskWithDecisionsQuestions
                    [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["other:manual-note"]""") ]
                    [ questionNode ignore ]
            ))

    assertEqual "decision other target value" [ OtherDecisionTarget "manual-note" ] otherTargetParsed.Decisions.Head.Targets

    // Question DTO strictness and graph consistency.
    let questionUnknownProperty =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["extra"] <- JsonValue.Create 1) ]

    let questionMissingProperty =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.Remove "state" |> ignore) ]

    let questionWrongIdType =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["id"] <- JsonValue.Create 7) ]

    let questionInvalidId =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["id"] <- JsonValue.Create "X1") ]

    let questionBlankText =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["text"] <- JsonValue.Create "   ") ]

    let questionInvalidImpact =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["impact"] <- JsonValue.Create "bogus") ]

    let questionTaskWideWithItems =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["workItems"] <- JsonNode.Parse """["W1"]""") ]

    let questionWorkItemsEmptyArray =
        taskWithDecisionsQuestions
            []
            [ questionNode (fun n ->
                  n.["impact"] <- JsonValue.Create "workItems"
                  n.["workItems"] <- JsonNode.Parse "[]") ]

    let questionWorkItemsMissing =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["impact"] <- JsonValue.Create "workItems") ]

    let questionWorkItemsUnknown =
        taskWithDecisionsQuestions
            []
            [ questionNode (fun n ->
                  n.["impact"] <- JsonValue.Create "workItems"
                  n.["workItems"] <- JsonNode.Parse """["W9"]""") ]

    let questionWorkItemsDuplicate =
        taskWithDecisionsQuestions
            []
            [ questionNode (fun n ->
                  n.["impact"] <- JsonValue.Create "workItems"
                  n.["workItems"] <- JsonNode.Parse """["W1","W1"]""") ]

    let questionOpenWithResolution =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["resolution"] <- JsonValue.Create "D1") ]

    let questionResolvedWithoutResolution =
        taskWithDecisionsQuestions [] [ questionNode (fun n -> n.["state"] <- JsonValue.Create "resolved") ]

    let questionResolvedBadReference =
        taskWithDecisionsQuestions
            []
            [ questionNode (fun n ->
                  n.["state"] <- JsonValue.Create "resolved"
                  n.["resolution"] <- JsonValue.Create "X1") ]

    let questionUnknownDecision =
        taskWithDecisionsQuestions
            []
            [ questionNode (fun n ->
                  n.["state"] <- JsonValue.Create "resolved"
                  n.["resolution"] <- JsonValue.Create "D9") ]

    let questionMismatchedResolution =
        taskWithDecisionsQuestions
            [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["questionResolution:Q2"]""") ]
            [ questionNode (fun n ->
                  n.["state"] <- JsonValue.Create "resolved"
                  n.["resolution"] <- JsonValue.Create "D1")
              questionNode (fun n -> n.["id"] <- JsonValue.Create "Q2") ]

    let duplicateQuestions =
        taskWithDecisionsQuestions
            [ decisionNode ignore ]
            [ questionNode ignore; questionNode (fun n -> n.["id"] <- JsonValue.Create "Q1") ]

    expectRejectedDeserialize "question unknown property" "question contains unknown property 'extra'" questionUnknownProperty
    expectRejectedDeserialize "question missing property" "question is missing property 'state'" questionMissingProperty
    expectRejectedDeserialize "question wrong id type" "property 'id' must be a string" questionWrongIdType
    expectRejectedDeserialize "question invalid id" "question id has an invalid format" questionInvalidId
    expectRejectedDeserialize "question blank text" "question text must be a non-empty single line" questionBlankText
    expectRejectedDeserialize "question invalid impact" "question 'Q1' has an invalid impact" questionInvalidImpact
    expectRejectedDeserialize "question taskWide with work items" "TaskWide question 'Q1' cannot declare WorkItems" questionTaskWideWithItems
    expectRejectedDeserialize "question work items empty array" "question 'Q1' WorkItems impact requires at least one WorkItem" questionWorkItemsEmptyArray
    expectRejectedDeserialize "question work items missing" "question 'Q1' WorkItems impact requires at least one WorkItem" questionWorkItemsMissing
    expectRejectedDeserialize "question work items unknown" "question 'Q1' references unknown WorkItem 'W9'" questionWorkItemsUnknown
    expectRejectedDeserialize "question work items duplicate" "question 'Q1' WorkItems impact must be unique" questionWorkItemsDuplicate
    expectRejectedDeserialize "question open with resolution" "open question 'Q1' cannot declare a resolution" questionOpenWithResolution
    expectRejectedDeserialize "question resolved without resolution" "resolved question 'Q1' requires a decision reference" questionResolvedWithoutResolution
    expectRejectedDeserialize "question resolved bad reference" "decision id has an invalid format" questionResolvedBadReference
    expectRejectedDeserialize "question unknown resolution decision" "question 'Q1' references unknown Decision 'D9'" questionUnknownDecision
    expectRejectedDeserialize "question mismatched resolution" "question 'Q1' resolution Decision 'D1' does not target it" questionMismatchedResolution
    expectRejectedDeserialize "duplicate question ids" "Question IDs must be unique" duplicateQuestions

    // WorkItem-scoped questions parse and preserve their typed impact.
    let scopedQuestionParsed =
        expectOk
            "work item scoped question parses"
            (deserialize (
                taskWithDecisionsQuestions
                    []
                    [ questionNode (fun n ->
                          n.["impact"] <- JsonValue.Create "workItems"
                          n.["workItems"] <- JsonNode.Parse """["W1"]""") ]
            ))

    assertEqual "scoped question impact" (WorkItems [ "W1" ]) scopedQuestionParsed.Questions.Head.Impact
    assertEqual "scoped question state" Open scopedQuestionParsed.Questions.Head.State

    // Command-level Decision/Question draft validation and runtime assignment.
    let dqTask = "TST-200"

    expectOk
        "create decision/question task"
        (createTask tempRoot (createRequest dqTask "Decision and question task"))
    |> ignore

    let draft targets rationale =
        { Kind = DesignDecision
          Targets = targets
          Rationale = rationale }

    let question id text impact =
        { Id = id
          Text = text
          Impact = impact }

    expectRejected
        "decision draft empty targets"
        "a decision requires at least one target"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [] "why")))

    expectRejected
        "decision draft blank rationale"
        "decision rationale must be a non-empty single line"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [ OtherDecisionTarget "note" ] "  ")))

    expectRejected
        "decision draft unknown acceptance"
        "decision targets unknown Acceptance Criterion 'AC9'"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [ WaiveAcceptanceTarget "AC9" ] "why")))

    expectRejected
        "decision draft invalid acceptance id"
        "acceptance id has an invalid format"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [ WaiveAcceptanceTarget "X1" ] "why")))

    expectRejected
        "decision draft unknown guard"
        "decision targets unknown Guard 'G9'"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [ GuardDispositionTarget "G9" ] "why")))

    expectRejected
        "decision draft unknown skip work item"
        "decision targets unknown WorkItem 'W9'"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [ SkipWorkItemTarget "W9" ] "why")))

    expectRejected
        "decision draft unknown requirement work item"
        "decision targets unknown WorkItem 'W9'"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [ RequirementChangeTarget "W9" ] "why")))

    expectRejected
        "decision draft unknown question"
        "decision targets unknown Question 'Q9'"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [ QuestionResolutionTarget "Q9" ] "why")))

    expectRejected
        "decision draft blank other target"
        "decision target must be a non-empty single line"
        (applyTask tempRoot dqTask 0 (AddDecision (draft [ OtherDecisionTarget "  " ] "why")))

    assertEqual
        "rejected decision drafts are no-ops"
        0
        (expectOk "get after rejected decisions" (getTask tempRoot dqTask)).StateRevision

    let firstDecision =
        expectOk
            "add first decision"
            (applyTask tempRoot dqTask 0 (AddDecision (draft [ OtherDecisionTarget "manual-note" ] "record rationale")))

    assertEqual "first decision revision" 1 firstDecision.StateRevision
    assertEqual "first decision id" "D1" firstDecision.Decisions.Head.Id
    assertEqual "first decision authority" Coordinator firstDecision.Decisions.Head.Authority
    assertEqual "first decision confirmation" None firstDecision.Decisions.Head.ConfirmationRef
    assertEqual "first decision targets" [ OtherDecisionTarget "manual-note" ] firstDecision.Decisions.Head.Targets

    let secondDecision =
        expectOk
            "add second decision"
            (applyTask tempRoot dqTask 1 (AddDecision (draft [ OtherDecisionTarget "second" ] "more rationale")))

    assertEqual "second decision id" "D2" secondDecision.Decisions.[1].Id

    let decisionAddedEvent: Decision =
        { Id = "D3"
          Authority = Coordinator
          Kind = DesignDecision
          Targets = [ OtherDecisionTarget "event" ]
          Rationale = "event"
          CreatedAt = DateTimeOffset.UtcNow
          ConfirmationRef = None }

    expectRejected
        "decision added event is command-rejected"
        "decision addition events are produced by decide and cannot be applied as commands"
        (applyTask tempRoot dqTask 2 (DecisionAdded decisionAddedEvent))

    expectRejected
        "question draft invalid id"
        "question id has an invalid format"
        (applyTask tempRoot dqTask 2 (AddQuestion (question "X1" "text" TaskWide)))

    expectRejected
        "question draft blank text"
        "question text must be a non-empty single line"
        (applyTask tempRoot dqTask 2 (AddQuestion (question "Q1" "  " TaskWide)))

    expectRejected
        "question draft empty work items"
        "question 'Q1' WorkItems impact requires at least one WorkItem"
        (applyTask tempRoot dqTask 2 (AddQuestion (question "Q1" "text" (WorkItems []))))

    expectRejected
        "question draft unknown work item"
        "question 'Q1' references unknown WorkItem 'W9'"
        (applyTask tempRoot dqTask 2 (AddQuestion (question "Q1" "text" (WorkItems [ "W9" ]))))

    expectRejected
        "question draft duplicate work item"
        "question 'Q1' WorkItems impact must be unique"
        (applyTask tempRoot dqTask 2 (AddQuestion (question "Q1" "text" (WorkItems [ "W1"; "W1" ]))))

    let openedQuestion =
        expectOk
            "add question"
            (applyTask tempRoot dqTask 2 (AddQuestion (question "Q1" "Which path?" TaskWide)))

    assertEqual "opened question revision" 3 openedQuestion.StateRevision
    assertEqual "opened question state" Open openedQuestion.Questions.Head.State
    assertEqual "opened question impact" TaskWide openedQuestion.Questions.Head.Impact

    expectRejected
        "duplicate question draft"
        "question 'Q1' already exists"
        (applyTask tempRoot dqTask 3 (AddQuestion (question "Q1" "again" TaskWide)))

    let questionOpenedEvent: Question =
        { Id = "Q2"
          Text = "event"
          Impact = TaskWide
          State = Open }

    expectRejected
        "question opened event is command-rejected"
        "question opening events are produced by decide and cannot be applied as commands"
        (applyTask tempRoot dqTask 3 (QuestionOpened questionOpenedEvent))

    // TaskWide question: pending WorkItems cannot start until it is resolved.
    let taskWideTask = "TST-201"

    expectOk
        "create task-wide question task"
        (createTask
            tempRoot
            { createRequest taskWideTask "Task-wide question task" with
                WorkItems = [ spec "W1" "first"; spec "W2" "second" ] })
    |> ignore

    expectOk "add task-wide evidence" (applyTask tempRoot taskWideTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "ac build"))) |> ignore
    expectOk "start task-wide W1" (applyTask tempRoot taskWideTask 1 (StartWorkItem "W1")) |> ignore

    expectOk
        "complete task-wide W1"
        (applyTask tempRoot taskWideTask 2 (CompleteWorkItem("W1", { Result = "first done"; EvidenceRefs = [] })))
    |> ignore

    expectOk "verify task-wide AC" (applyTask tempRoot taskWideTask 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore
    expectOk "open task-wide question" (applyTask tempRoot taskWideTask 4 (AddQuestion (question "Q1" "Block everything?" TaskWide))) |> ignore

    let taskWideOpened = expectOk "get task-wide task" (getTask tempRoot taskWideTask)
    assertEqual "task-wide question open" Open taskWideOpened.Questions.Head.State

    expectRejected
        "task-wide question blocks pending start"
        "WorkItem 'W2' is not ready: question 'Q1' is open"
        (applyTask tempRoot taskWideTask 5 (StartWorkItem "W2"))

    assertEqual
        "blocked task-wide start is a no-op"
        5
        (expectOk "get after blocked task-wide start" (getTask tempRoot taskWideTask)).StateRevision

    expectOk
        "add task-wide resolution decision"
        (applyTask tempRoot taskWideTask 5 (AddDecision (draft [ QuestionResolutionTarget "Q1" ] "resolved by coordinator")))
    |> ignore

    let taskWideResolved =
        expectOk "resolve task-wide question" (applyTask tempRoot taskWideTask 6 (ResolveQuestion("Q1", DecisionRef "D1")))

    assertEqual "task-wide question resolved" (Resolved(DecisionRef "D1")) taskWideResolved.Questions.Head.State
    expectOk "start task-wide W2 after resolution" (applyTask tempRoot taskWideTask 7 (StartWorkItem "W2")) |> ignore

    // TaskWide question blocks CanCompleteTask and CompleteTask even when all work is done.
    let taskWideCompleteTask = "TST-202"
    expectOk "create task-wide completion task" (createTask tempRoot (createRequest taskWideCompleteTask "Task-wide completion task")) |> ignore
    expectOk "add task-wide completion evidence" (applyTask tempRoot taskWideCompleteTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "ac build"))) |> ignore
    expectOk "start task-wide completion W1" (applyTask tempRoot taskWideCompleteTask 1 (StartWorkItem "W1")) |> ignore

    expectOk
        "complete task-wide completion W1"
        (applyTask tempRoot taskWideCompleteTask 2 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] })))
    |> ignore

    expectOk
        "verify task-wide completion AC"
        (applyTask tempRoot taskWideCompleteTask 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))
    |> ignore

    let taskWideCompletable = expectOk "get task-wide completion task" (getTask tempRoot taskWideCompleteTask)
    assertTrue "task-wide completion baseline is completable" (canCompleteTask taskWideCompletable)

    expectOk
        "open task-wide completion question"
        (applyTask tempRoot taskWideCompleteTask 4 (AddQuestion (question "Q1" "Block completion?" TaskWide)))
    |> ignore

    let taskWideBlocked = expectOk "get blocked task-wide completion" (getTask tempRoot taskWideCompleteTask)
    assertTrue "task-wide question blocks CanCompleteTask" (not (canCompleteTask taskWideBlocked))

    expectRejected
        "task-wide question blocks CompleteTask"
        "task cannot complete while a TaskWide question is open"
        (applyTask tempRoot taskWideCompleteTask 5 (CompleteTask defaultHandoff))

    assertEqual
        "blocked task-wide completion is a no-op"
        5
        (expectOk "get after blocked task-wide completion" (getTask tempRoot taskWideCompleteTask)).StateRevision

    expectOk
        "add task-wide completion decision"
        (applyTask tempRoot taskWideCompleteTask 5 (AddDecision (draft [ QuestionResolutionTarget "Q1" ] "unblock completion")))
    |> ignore

    let taskWideUnblocked =
        expectOk "resolve task-wide completion question" (applyTask tempRoot taskWideCompleteTask 6 (ResolveQuestion("Q1", DecisionRef "D1")))

    assertTrue "resolved task-wide question clears CanCompleteTask" (canCompleteTask taskWideUnblocked)

    let taskWideCompleted = expectOk "complete task-wide completion task" (applyTask tempRoot taskWideCompleteTask 7 (CompleteTask defaultHandoff))
    assertEqual "task-wide completion lifecycle" "complete" taskWideCompleted.Lifecycle

    // WorkItem-scoped question: only its referenced WorkItems are blocked.
    let scopedTask = "TST-203"

    expectOk
        "create scoped question task"
        (createTask
            tempRoot
            { createRequest scopedTask "Scoped question task" with
                WorkItems = [ spec "W1" "blocked"; spec "W2" "free" ] })
    |> ignore

    expectOk
        "open scoped question"
        (applyTask tempRoot scopedTask 0 (AddQuestion (question "Q1" "Block W1 only?" (WorkItems [ "W1" ]))))
    |> ignore

    expectRejected
        "scoped question blocks its work item"
        "WorkItem 'W1' is not ready: question 'Q1' is open"
        (applyTask tempRoot scopedTask 1 (StartWorkItem "W1"))

    assertEqual "blocked scoped start is a no-op" 1 (expectOk "get after blocked scoped start" (getTask tempRoot scopedTask)).StateRevision

    let scopedStarted = expectOk "start unblocked work item" (applyTask tempRoot scopedTask 1 (StartWorkItem "W2"))
    assertEqual "unrelated work item starts" ActiveWork scopedStarted.WorkItems.[1].State

    // A WorkItem-scoped question does not block task completion once work is terminal.
    let scopedCompleteTask = "TST-204"
    expectOk "create scoped completion task" (createTask tempRoot (createRequest scopedCompleteTask "Scoped completion task")) |> ignore
    expectOk "add scoped completion evidence" (applyTask tempRoot scopedCompleteTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "ac build"))) |> ignore
    expectOk "start scoped completion W1" (applyTask tempRoot scopedCompleteTask 1 (StartWorkItem "W1")) |> ignore

    expectOk
        "complete scoped completion W1"
        (applyTask tempRoot scopedCompleteTask 2 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] })))
    |> ignore

    expectOk "verify scoped completion AC" (applyTask tempRoot scopedCompleteTask 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore
    expectOk
        "open scoped completion question"
        (applyTask tempRoot scopedCompleteTask 4 (AddQuestion (question "Q1" "Scope W1" (WorkItems [ "W1" ]))))
    |> ignore

    let scopedCompletable = expectOk "get scoped completion task" (getTask tempRoot scopedCompleteTask)
    assertTrue "work-item scoped question does not block CanCompleteTask" (canCompleteTask scopedCompletable)
    expectOk "complete scoped completion task" (applyTask tempRoot scopedCompleteTask 5 (CompleteTask defaultHandoff)) |> ignore

    // Target-matching Decision is required; an exact target cannot resolve another question.
    let matchingTask = "TST-205"
    expectOk "create target matching task" (createTask tempRoot (createRequest matchingTask "Target matching task")) |> ignore
    expectOk "open target matching Q1" (applyTask tempRoot matchingTask 0 (AddQuestion (question "Q1" "First?" TaskWide))) |> ignore
    expectOk "open target matching Q2" (applyTask tempRoot matchingTask 1 (AddQuestion (question "Q2" "Second?" TaskWide))) |> ignore
    expectOk
        "add target matching decision"
        (applyTask tempRoot matchingTask 2 (AddDecision (draft [ QuestionResolutionTarget "Q1" ] "answers Q1")))
    |> ignore

    expectRejected
        "mismatched decision cannot resolve question"
        "Decision 'D1' does not target Question 'Q2'"
        (applyTask tempRoot matchingTask 3 (ResolveQuestion("Q2", DecisionRef "D1")))

    assertEqual
        "mismatched resolution is a no-op"
        3
        (expectOk "get after mismatched resolution" (getTask tempRoot matchingTask)).StateRevision

    expectRejected
        "unknown decision cannot resolve question"
        "Decision 'D9' was not found"
        (applyTask tempRoot matchingTask 3 (ResolveQuestion("Q2", DecisionRef "D9")))

    expectRejected
        "unknown question cannot be resolved"
        "Question 'Q9' was not found"
        (applyTask tempRoot matchingTask 3 (ResolveQuestion("Q9", DecisionRef "D1")))

    let matchingResolved = expectOk "matching decision resolves question" (applyTask tempRoot matchingTask 3 (ResolveQuestion("Q1", DecisionRef "D1")))
    assertEqual "matching question resolved" (Resolved(DecisionRef "D1")) matchingResolved.Questions.Head.State
    assertEqual "unrelated question stays open" Open matchingResolved.Questions.[1].State

    expectRejected
        "already resolved question cannot resolve again"
        "Question 'Q1' is already resolved"
        (applyTask tempRoot matchingTask 4 (ResolveQuestion("Q1", DecisionRef "D1")))

    let questionResolvedEvent: Question =
        { Id = "Q1"
          Text = "event"
          Impact = TaskWide
          State = Open }

    expectRejected
        "question resolved event is command-rejected"
        "question resolution events are produced by decide and cannot be applied as commands"
        (applyTask tempRoot matchingTask 4 (QuestionResolved("Q1", DecisionRef "D1")))

    // Revision accounting, round-trip persistence, and shallow-shape preservation.
    let persistenceTask = "TST-206"
    expectOk "create decision persistence task" (createTask tempRoot (createRequest persistenceTask "Decision persistence task")) |> ignore
    expectOk "open persistence question" (applyTask tempRoot persistenceTask 0 (AddQuestion (question "Q1" "Persist me?" TaskWide))) |> ignore
    expectOk
        "add persistence decision"
        (applyTask tempRoot persistenceTask 1 (AddDecision (draft [ QuestionResolutionTarget "Q1" ] "persisted resolution")))
    |> ignore

    let persistenceResolved =
        expectOk "resolve persistence question" (applyTask tempRoot persistenceTask 2 (ResolveQuestion("Q1", DecisionRef "D1")))

    assertEqual "persistence resolution revision" 3 persistenceResolved.StateRevision

    let reloaded = expectOk "reload decision persistence task" (getTask tempRoot persistenceTask)
    assertEqual "reloaded decision count" 1 reloaded.Decisions.Length
    assertEqual "reloaded decision target" [ QuestionResolutionTarget "Q1" ] reloaded.Decisions.Head.Targets
    assertEqual "reloaded question count" 1 reloaded.Questions.Length
    assertEqual "reloaded question state" (Resolved(DecisionRef "D1")) reloaded.Questions.Head.State

    let roundTripped = expectOk "decision/question round trip" (persistenceResolved |> serialize |> deserialize)
    assertEqual "round trip decision id" "D1" roundTripped.Decisions.Head.Id
    assertEqual "round trip question resolution" (Resolved(DecisionRef "D1")) roundTripped.Questions.Head.State

    let persistedRaw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot persistenceTask)).AsObject()
    assertEqual "persisted decision count" 1 (persistedRaw.["decisions"].AsArray().Count)
    assertEqual "persisted decision authority" "coordinator" (persistedRaw.["decisions"].AsArray().[0].AsObject().["authority"].GetValue<string>())
    assertEqual "persisted decision target" "questionResolution:Q1" (persistedRaw.["decisions"].AsArray().[0].AsObject().["targets"].AsArray().[0].GetValue<string>())
    assertEqual "persisted question count" 1 (persistedRaw.["questions"].AsArray().Count)
    assertEqual "persisted question impact" "taskWide" (persistedRaw.["questions"].AsArray().[0].AsObject().["impact"].GetValue<string>())
    assertEqual "persisted question state" "resolved" (persistedRaw.["questions"].AsArray().[0].AsObject().["state"].GetValue<string>())
    assertEqual "persisted question resolution" "D1" (persistedRaw.["questions"].AsArray().[0].AsObject().["resolution"].GetValue<string>())

    // Schema 3 writes the complete canonical shape: empty Decisions/Questions
    // and guards/provenance are present rather than omitted.
    let shallowTask = "TST-207"
    expectOk "create shallow task" (createTask tempRoot (createRequest shallowTask "Shallow task")) |> ignore
    let shallowRaw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot shallowTask)).AsObject()
    assertEqual "shallow sidecar writes empty decisions" 0 (shallowRaw.["decisions"].AsArray().Count)
    assertEqual "shallow sidecar writes empty questions" 0 (shallowRaw.["questions"].AsArray().Count)
    assertEqual "shallow sidecar writes empty guards" 0 (shallowRaw.["guards"].AsArray().Count)
    assertEqual "shallow sidecar writes empty profileGuardKeys" 0 (shallowRaw.["profileGuardKeys"].AsObject().Count)
    assertEqual "shallow sidecar writes empty completionHistory" 0 (shallowRaw.["completionHistory"].AsArray().Count)

    // Persistence integrity: the reopened parent must round-trip through the
    // sidecar after the cascade reopen clears its stale result.
    let persistedCascade = expectOk "cascade sidecar round trip" (getTask tempRoot cascadeTask)
    assertEqual "persisted cascade parent reopened" PendingWork persistedCascade.WorkItems.Head.State
    assertEqual "persisted cascade parent result cleared" None persistedCascade.WorkItems.Head.Result

    // --- Scenario 23: Coordinator-only invocation authority (slice 6) -------
    // Sections 9.1-9.5/25: authority is derived from a trusted invocation
    // context, never from command input. The #13 User/ProfilePolicy ingress and
    // confirmation receipts are removed, so ordinary applyTask/CLI is
    // Coordinator-only and User-required operations fail closed. Sidecar JSON is
    // untrusted: forged User/ProfilePolicy provenance and confirmationRef claims
    // are rejected on load, and a persisted Guard disposition must be backed by
    // an exact target-bound Decision at the required authority.
    let authorityGuardSpec id target applicability waiver =
        { guardSpec id target BeforeComplete (requirement EvidenceKind.Review 1 None false) with
            Applicability = applicability
            Waiver = waiver }

    let userRequiredGuard id target =
        authorityGuardSpec id target (ExplicitDecision MinimumAuthority.UserAuthority) NotWaivable

    let userWaivableGuard id target =
        authorityGuardSpec id target Always (WaivableBy MinimumAuthority.UserAuthority)

    let coordinatorRequiredGuard id target =
        authorityGuardSpec id target (ExplicitDecision MinimumAuthority.CoordinatorAuthority) NotWaivable

    let coordinatorWaivableGuard id target =
        authorityGuardSpec id target Always (WaivableBy MinimumAuthority.CoordinatorAuthority)

    // Ordinary invocation is Coordinator-only for both User-required policies.
    let authorityTask = "TST-300"
    expectOk "create authority task" (createTask tempRoot (createRequest authorityTask "Authority task")) |> ignore
    expectOk "add user-required guard G1" (applyTask tempRoot authorityTask 0 (AddGuard (userRequiredGuard "G1" TaskTarget))) |> ignore
    expectOk "add user-waivable guard G2" (applyTask tempRoot authorityTask 1 (AddGuard (userWaivableGuard "G2" TaskTarget))) |> ignore

    expectRejected
        "ordinary applyTask cannot mark user-required guard NotApplicable"
        "operation requires user authority; ordinary Coordinator invocation cannot authorize it"
        (applyTask tempRoot authorityTask 2 (MarkGuardNotApplicable("G1", None)))

    expectRejected
        "ordinary applyTask cannot waive user-required guard"
        "operation requires user authority; ordinary Coordinator invocation cannot authorize it"
        (applyTask tempRoot authorityTask 2 (WaiveGuard("G2", None)))

    let afterOrdinaryRejections = expectOk "read after ordinary authority rejections" (readPersisted tempRoot authorityTask)
    assertEqual "ordinary rejection preserves revision" 2 afterOrdinaryRejections.StateRevision
    assertEqual "ordinary rejection creates no decision" 0 afterOrdinaryRejections.Decisions.Length
    assertEqual "ordinary rejection preserves G1" GuardDisposition.Applicable (afterOrdinaryRejections.Guards |> List.find (fun g -> g.Id = "G1")).Disposition
    assertEqual "ordinary rejection preserves G2" GuardDisposition.Applicable (afterOrdinaryRejections.Guards |> List.find (fun g -> g.Id = "G2")).Disposition

    // Valid Coordinator dispositions: ordinary invocation creates a target-bound
    // Coordinator Decision atomically with the disposition.
    let coordinatorTask = "TST-301"
    expectOk "create coordinator authority task" (createTask tempRoot (createRequest coordinatorTask "Coordinator authority task")) |> ignore
    expectOk "add coordinator-required guard G1" (applyTask tempRoot coordinatorTask 0 (AddGuard (coordinatorRequiredGuard "G1" TaskTarget))) |> ignore
    expectOk "add coordinator-waivable guard G2" (applyTask tempRoot coordinatorTask 1 (AddGuard (coordinatorWaivableGuard "G2" TaskTarget))) |> ignore

    let coordinatorDisposition =
        expectOk
            "coordinator marks G1 NotApplicable"
            (applyTask tempRoot coordinatorTask 2 (MarkGuardNotApplicable("G1", None)))

    assertEqual "coordinator disposition revision" 3 coordinatorDisposition.StateRevision
    assertEqual "coordinator disposition decision count" 1 coordinatorDisposition.Decisions.Length
    assertEqual "coordinator disposition decision authority" Coordinator coordinatorDisposition.Decisions.Head.Authority
    assertEqual "coordinator disposition decision kind" ApplicabilityDecision coordinatorDisposition.Decisions.Head.Kind
    assertEqual "coordinator disposition decision target" [ GuardDispositionTarget "G1" ] coordinatorDisposition.Decisions.Head.Targets
    assertEqual "coordinator disposition reference" (GuardDisposition.NotApplicable "D1") (coordinatorDisposition.Guards |> List.find (fun g -> g.Id = "G1")).Disposition

    let coordinatorWaiver =
        expectOk
            "coordinator waives G2"
            (applyTask tempRoot coordinatorTask 3 (WaiveGuard("G2", None)))

    assertEqual "coordinator waiver revision" 4 coordinatorWaiver.StateRevision
    assertEqual "coordinator waiver decision count" 2 coordinatorWaiver.Decisions.Length
    assertEqual "coordinator waiver decision kind" WaiverDecision coordinatorWaiver.Decisions.[1].Kind
    assertEqual "coordinator waiver disposition" (GuardDisposition.Waived "D2") (coordinatorWaiver.Guards |> List.find (fun g -> g.Id = "G2")).Disposition

    let persistedCoordinator = expectOk "read coordinator dispositions" (readPersisted tempRoot coordinatorTask)
    assertEqual "persisted coordinator revision" 4 persistedCoordinator.StateRevision
    assertEqual "persisted coordinator decision authority" Coordinator persistedCoordinator.Decisions.Head.Authority
    assertEqual "persisted coordinator disposition" (GuardDisposition.NotApplicable "D1") (persistedCoordinator.Guards |> List.find (fun g -> g.Id = "G1")).Disposition

    // An existing target-bound Coordinator Decision is reused instead of
    // duplicated, and never authorizes an operation outside its exact target.
    let reuseTask = "TST-302"
    expectOk "create coordinator reuse task" (createTask tempRoot (createRequest reuseTask "Coordinator reuse task")) |> ignore
    expectOk "add reuse guard G1" (applyTask tempRoot reuseTask 0 (AddGuard (coordinatorRequiredGuard "G1" TaskTarget))) |> ignore

    let existingCoordinatorDecision: Decision =
        { Id = "D1"
          Authority = Coordinator
          Kind = ApplicabilityDecision
          Targets = [ GuardDispositionTarget "G1" ]
          Rationale = "pre-existing coordinator decision"
          CreatedAt = DateTimeOffset.UtcNow
          ConfirmationRef = None }

    let reuseTaskModel = expectOk "read reuse task" (getTask tempRoot reuseTask)
    File.WriteAllText(sidecarPath tempRoot reuseTask, serialize { reuseTaskModel with Decisions = [ existingCoordinatorDecision ] })

    let reused =
        expectOk
            "existing Coordinator Decision authorizes ordinary applyTask"
            (applyTask tempRoot reuseTask 1 (MarkGuardNotApplicable("G1", Some(DecisionRef "D1"))))

    assertEqual "reused coordinator decision revision" 2 reused.StateRevision
    assertEqual "reused coordinator decision adds no Decision" 1 reused.Decisions.Length
    assertEqual "reused coordinator decision disposition" (GuardDisposition.NotApplicable "D1") (reused.Guards |> List.find (fun g -> g.Id = "G1")).Disposition

    // Sidecar JSON is untrusted: forged User/ProfilePolicy provenance and
    // confirmationRef claims are rejected on load, before any state is used.
    let forgedBase = expectOk "read forged authority base" (getTask tempRoot authorityTask)

    let forgedTaskJson (decisions: JsonObject list) (guards: JsonObject list) =
        let task = JsonNode.Parse(serialize forgedBase).AsObject()
        let decisionsArray = JsonArray()
        decisions |> List.iter (fun node -> decisionsArray.Add node)
        task.["decisions"] <- decisionsArray
        let guardsArray = JsonArray()
        guards |> List.iter (fun node -> guardsArray.Add node)
        task.["guards"] <- guardsArray
        task.ToJsonString()

    let forgedUserDecision =
        forgedTaskJson [ decisionNode (fun n -> n.["authority"] <- JsonValue.Create "user") ] []

    let forgedPolicyDecision =
        forgedTaskJson [ decisionNode (fun n -> n.["authority"] <- JsonValue.Create "profilePolicy") ] []

    let forgedConfirmationRef =
        forgedTaskJson [ decisionNode (fun n -> n.["confirmationRef"] <- JsonValue.Create "receipt-1") ] []

    expectRejectedDeserialize
        "forged User Decision rejected on load"
        "claims User authority"
        forgedUserDecision

    expectRejectedDeserialize
        "forged ProfilePolicy Decision rejected on load"
        "claims ProfilePolicy provenance"
        forgedPolicyDecision

    expectRejectedDeserialize
        "forged confirmationRef rejected on load"
        "unverifiable confirmationRef"
        forgedConfirmationRef

    // A persisted Guard disposition is untrusted too: it must resolve to an
    // existing Decision that authorizes the exact Guard target at the required
    // authority.
    let danglingDisposition =
        forgedTaskJson
            []
            [ guardNode (fun n ->
                  n.["applicability"] <- JsonValue.Create "explicitDecision:coordinator"
                  n.["disposition"] <- JsonValue.Create "notApplicable:D9") ]

    let unauthorizedDisposition =
        forgedTaskJson
            [ decisionNode (fun n -> n.["targets"] <- JsonNode.Parse """["guardDisposition:G1"]""") ]
            [ guardNode (fun n ->
                  n.["applicability"] <- JsonValue.Create "explicitDecision:user"
                  n.["disposition"] <- JsonValue.Create "notApplicable:D1") ]

    expectRejectedDeserialize
        "dangling guard disposition rejected on load"
        "disposition references unknown Decision 'D9'"
        danglingDisposition

    expectRejectedDeserialize
        "unauthorized guard disposition rejected on load"
        "does not authorize the exact Guard target at user authority"
        unauthorizedDisposition

    // The ordinary CLI is the same Coordinator-only boundary and exposes no
    // authority-injection surface.
    let cliTask = "TST-303"
    expectOk "create CLI authority task" (createTask tempRoot (createRequest cliTask "CLI authority task")) |> ignore
    expectOk "add CLI user-required guard G1" (applyTask tempRoot cliTask 0 (AddGuard (userRequiredGuard "G1" TaskTarget))) |> ignore
    expectOk "add CLI user-waivable guard G2" (applyTask tempRoot cliTask 1 (AddGuard (userWaivableGuard "G2" TaskTarget))) |> ignore

    let runCli (arguments: string list) =
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "dotnet"
        startInfo.ArgumentList.Add "fsi"
        startInfo.ArgumentList.Add "--nologo"
        startInfo.ArgumentList.Add "--exec"
        startInfo.ArgumentList.Add(Path.Combine(__SOURCE_DIRECTORY__, "TaskApply.fsx"))
        arguments |> List.iter startInfo.ArgumentList.Add
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.WorkingDirectory <- Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
        use proc = Process.Start startInfo
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        proc.ExitCode, stdout, stderr

    let cliExit, _, cliStderr = runCli [ tempRoot; cliTask; "2"; "mark-not-applicable"; "G1" ]
    assertEqual "CLI rejects User-required not-applicable" 1 cliExit
    assertTrue "CLI authority error" (cliStderr.Contains("operation requires user authority", StringComparison.Ordinal))

    let cliWaiveExit, _, cliWaiveStderr = runCli [ tempRoot; cliTask; "2"; "waive-guard"; "G2" ]
    assertEqual "CLI rejects User-required waiver" 1 cliWaiveExit
    assertTrue "CLI waiver authority error" (cliWaiveStderr.Contains("operation requires user authority", StringComparison.Ordinal))

    let cliFlagExit, _, _ = runCli [ tempRoot; cliTask; "2"; "mark-not-applicable"; "G1"; "--authority"; "user" ]
    assertEqual "CLI exposes no authority flag" 2 cliFlagExit

    let afterCli = expectOk "read after CLI rejections" (readPersisted tempRoot cliTask)
    assertEqual "CLI rejection preserves revision" 2 afterCli.StateRevision
    assertEqual "CLI rejection creates no decision" 0 afterCli.Decisions.Length
    assertEqual "CLI rejection preserves G1" GuardDisposition.Applicable (afterCli.Guards |> List.find (fun g -> g.Id = "G1")).Disposition
    assertEqual "CLI rejection preserves G2" GuardDisposition.Applicable (afterCli.Guards |> List.find (fun g -> g.Id = "G2")).Disposition

    // --- Scenario 24: WorkItem ownership, rebind, and inheritance ----------
    // Section 30: ownership is a durable design-time responsibility; children
    // inherit the nearest ancestor owner unless explicitly overridden.
    let ownerTask = "TST-400"

    let ownerRequest =
        { Id = ownerTask
          Title = "Owner task"
          Kind = Execution
          AcceptanceCriteria = [ "AC1", "Owner complete" ]
          WorkItems = [ specWith "W1" "root" [] [ spec "W1.1" "child" ] ] }

    expectOk "create owner task" (createTask tempRoot ownerRequest) |> ignore

    let parentOwner = owner "implementer" (Some "agent-1")

    let rebound =
        expectOk "rebind root owner" (applyTask tempRoot ownerTask 0 (RebindOwner("W1", parentOwner, "assign root")))

    assertEqual "rebind increments revision" 1 rebound.StateRevision
    assertEqual "rebind stores root owner" (Some parentOwner) rebound.WorkItems.Head.Owner
    assertEqual "rebind leaves child owner inherited" None rebound.WorkItems.Head.Children.Head.Owner

    // Owner persists as a nested object only on the explicitly owned item; the
    // child omits the key so inheritance stays derived rather than copied.
    let ownerRaw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot ownerTask)).AsObject()
    let ownerRawRoot = ownerRaw.["workItems"].AsArray().[0].AsObject()
    assertEqual "persisted owner role" "implementer" (ownerRawRoot.["owner"].AsObject().["role"].GetValue<string>())
    assertEqual "persisted owner agentId" "agent-1" (ownerRawRoot.["owner"].AsObject().["agentId"].GetValue<string>())

    assertTrue
        "inherited child omits owner"
        (not (ownerRawRoot.["children"].AsArray().[0].AsObject().ContainsKey "owner"))

    let ownerRoundTrip = expectOk "owner round trip" (rebound |> serialize |> deserialize)
    assertEqual "owner round trip root" (Some parentOwner) ownerRoundTrip.WorkItems.Head.Owner

    // Invalid rebinds fail closed without mutating state.
    expectRejected
        "rebind blank role"
        "owner role must be a non-empty single line"
        (applyTask tempRoot ownerTask 1 (RebindOwner("W1", owner "  " None, "reason")))

    expectRejected
        "rebind blank reason"
        "owner rebind reason must be a non-empty single line"
        (applyTask tempRoot ownerTask 1 (RebindOwner("W1", parentOwner, "   ")))

    expectRejected
        "rebind unknown work item"
        "WorkItem 'W9' was not found"
        (applyTask tempRoot ownerTask 1 (RebindOwner("W9", parentOwner, "reason")))

    assertEqual
        "rebind rejections are no-ops"
        1
        (expectOk "get after rebind rejections" (getTask tempRoot ownerTask)).StateRevision

    // An explicit child owner overrides the inherited parent owner.
    let childOwner = owner "reviewer" (Some "agent-2")

    let childRebound =
        expectOk "rebind child owner" (applyTask tempRoot ownerTask 1 (RebindOwner("W1.1", childOwner, "override")))

    assertEqual "child explicit owner overrides" (Some childOwner) childRebound.WorkItems.Head.Children.Head.Owner

    // Effective inheritance is observable through independent-producer Guard
    // satisfaction: a distinct producer satisfies the guard only because the
    // child inherits the ancestor owner.
    let inheritTask = "TST-401"

    let inheritRequest =
        { Id = inheritTask
          Title = "Inheritance task"
          Kind = Execution
          AcceptanceCriteria = [ "AC1", "ok" ]
          WorkItems = [ specWith "W1" "root" [] [ spec "W1.1" "child" ] ] }

    expectOk "create inheritance task" (createTask tempRoot inheritRequest) |> ignore
    expectOk "rebind inheritance root" (applyTask tempRoot inheritTask 0 (RebindOwner("W1", parentOwner, "assign"))) |> ignore

    expectOk
        "add inheritance guard"
        (applyTask
            tempRoot
            inheritTask
            1
            (AddGuard (guardSpec "G1" (WorkItemTarget "W1.1") BeforeComplete (requirement EvidenceKind.Review 1 (Some "reviewer") true))))
    |> ignore

    expectOk "start inheritance child" (applyTask tempRoot inheritTask 2 (StartWorkItem "W1.1")) |> ignore

    let inheritedOwnerEvidence =
        { makeEvidence "E1" EvidenceKind.Review "same producer" with
            ProducerRole = Some "reviewer"
            ProducerId = Some "agent-1" }

    expectOk "add inherited-owner evidence" (applyTask tempRoot inheritTask 3 (AddEvidence inheritedOwnerEvidence)) |> ignore

    expectRejected
        "independent guard rejects evidence from inherited owner"
        "WorkItem 'W1.1' cannot complete while guard 'G1' is not satisfied"
        (applyTask tempRoot inheritTask 4 (CompleteWorkItem("W1.1", { Result = "done"; EvidenceRefs = [ "E1" ] })))

    assertEqual
        "inherited-owner rejection is a no-op"
        4
        (expectOk "get after inherited rejection" (getTask tempRoot inheritTask)).StateRevision

    let distinctProducerEvidence =
        { makeEvidence "E2" EvidenceKind.Review "distinct producer" with
            ProducerRole = Some "reviewer"
            ProducerId = Some "agent-2" }

    expectOk "add distinct producer evidence" (applyTask tempRoot inheritTask 4 (AddEvidence distinctProducerEvidence)) |> ignore

    let inheritedComplete =
        expectOk
            "independent guard accepts distinct producer"
            (applyTask tempRoot inheritTask 5 (CompleteWorkItem("W1.1", { Result = "done"; EvidenceRefs = [ "E2" ] })))

    assertEqual "inherited owner completion done" DoneWork inheritedComplete.WorkItems.Head.Children.Head.State

    // Control: with no owner anywhere, the same distinct producer cannot prove
    // independence, so the acceptance above depended on inherited ownership.
    let noOwnerTask = "TST-402"
    expectOk "create no-owner task" (createTask tempRoot { inheritRequest with Id = noOwnerTask; Title = "No owner task" }) |> ignore

    expectOk
        "add no-owner guard"
        (applyTask tempRoot noOwnerTask 0 (AddGuard (guardSpec "G1" (WorkItemTarget "W1.1") BeforeComplete (requirement EvidenceKind.Review 1 (Some "reviewer") true))))
    |> ignore

    expectOk "start no-owner child" (applyTask tempRoot noOwnerTask 1 (StartWorkItem "W1.1")) |> ignore
    expectOk "add no-owner evidence" (applyTask tempRoot noOwnerTask 2 (AddEvidence distinctProducerEvidence)) |> ignore

    expectRejected
        "no owner cannot prove independence"
        "WorkItem 'W1.1' cannot complete while guard 'G1' is not satisfied"
        (applyTask tempRoot noOwnerTask 3 (CompleteWorkItem("W1.1", { Result = "done"; EvidenceRefs = [ "E2" ] })))

    // --- Scenario 25: terminal handoff persistence, history, validation -----
    // Section 27.1: CompleteTask accepts a structurally validated handoff; the
    // current handoff is retained and superseded handoffs move to history.
    let handoffTask = "TST-410"
    expectOk "create handoff task" (createTask tempRoot (createRequest handoffTask "Handoff task")) |> ignore
    expectOk "handoff add evidence" (applyTask tempRoot handoffTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "built"))) |> ignore
    expectOk "handoff start work" (applyTask tempRoot handoffTask 1 (StartWorkItem "W1")) |> ignore
    expectOk "handoff complete work" (applyTask tempRoot handoffTask 2 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "handoff verify AC" (applyTask tempRoot handoffTask 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore

    let firstHandoff = handoff "Terminal" "E1 recorded" "No task work remains."
    let handoffCompleted = expectOk "handoff complete task" (applyTask tempRoot handoffTask 4 (CompleteTask firstHandoff))

    assertEqual "handoff stored" (Some firstHandoff) handoffCompleted.TerminalHandoff
    assertEqual "handoff history empty" [] handoffCompleted.CompletionHistory

    let handoffRaw = JsonNode.Parse(File.ReadAllText(sidecarPath tempRoot handoffTask)).AsObject()
    assertEqual "persisted handoff state" "Terminal" (handoffRaw.["terminalHandoff"].AsObject().["state"].GetValue<string>())
    assertEqual "persisted handoff evidenceSummary" "E1 recorded" (handoffRaw.["terminalHandoff"].AsObject().["evidenceSummary"].GetValue<string>())
    assertEqual "persisted handoff next" "No task work remains." (handoffRaw.["terminalHandoff"].AsObject().["next"].GetValue<string>())
    assertEqual "persisted empty completionHistory" 0 (handoffRaw.["completionHistory"].AsArray().Count)

    let handoffRoundTrip = expectOk "handoff round trip" (handoffCompleted |> serialize |> deserialize)
    assertEqual "handoff round trip current" (Some firstHandoff) handoffRoundTrip.TerminalHandoff

    // Reopen clears the current handoff into history rather than deleting it.
    let handoffReopened =
        expectOk
            "reopen handoff task"
            (applyTask
                tempRoot
                handoffTask
                5
                (ReopenTask
                    { Reason = "refresh handoff"
                      DecisionRef = None
                      Targets = [ ReopenTarget.AcceptanceCriterionTarget "AC1" ] }))

    assertEqual "reopen clears current handoff" None handoffReopened.TerminalHandoff
    assertEqual "reopen preserves handoff history" [ firstHandoff ] handoffReopened.CompletionHistory
    assertEqual "reopen lifecycle" "open" handoffReopened.Lifecycle
    assertTrue "reopen leaves task incomplete" (not (canCompleteTask handoffReopened))

    expectOk
        "handoff re-verify AC"
        (applyTask tempRoot handoffTask handoffReopened.StateRevision (VerifyAcceptanceCriterion("AC1", [ "E1" ])))
    |> ignore

    let secondHandoff = handoff "Terminal again" "E1 recorded" "No task work remains."

    let reCompleted =
        expectOk
            "handoff complete again"
            (applyTask tempRoot handoffTask (handoffReopened.StateRevision + 1) (CompleteTask secondHandoff))

    assertEqual "re-complete current handoff" (Some secondHandoff) reCompleted.TerminalHandoff
    assertEqual "re-complete history appends prior handoff" [ firstHandoff ] reCompleted.CompletionHistory

    // Structurally invalid handoffs fail closed.
    let handoffValidationTask = "TST-411"
    expectOk "create handoff validation task" (createTask tempRoot (createRequest handoffValidationTask "Handoff validation task")) |> ignore
    expectOk "handoff validation add evidence" (applyTask tempRoot handoffValidationTask 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "built"))) |> ignore
    expectOk "handoff validation start work" (applyTask tempRoot handoffValidationTask 1 (StartWorkItem "W1")) |> ignore
    expectOk "handoff validation complete work" (applyTask tempRoot handoffValidationTask 2 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "handoff validation verify AC" (applyTask tempRoot handoffValidationTask 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore

    expectRejected
        "handoff blank state"
        "terminal handoff state must be a non-empty single line"
        (applyTask tempRoot handoffValidationTask 4 (CompleteTask (handoff "   " "evidence" "next")))

    expectRejected
        "handoff blank evidence summary"
        "terminal handoff evidenceSummary must be a non-empty single line"
        (applyTask tempRoot handoffValidationTask 4 (CompleteTask (handoff "state" "  " "next")))

    expectRejected
        "handoff multiline next"
        "terminal handoff next must be a non-empty single line"
        (applyTask tempRoot handoffValidationTask 4 (CompleteTask (handoff "state" "evidence" "a\nb")))

    assertEqual
        "handoff rejections are no-ops"
        4
        (expectOk "get after handoff rejections" (getTask tempRoot handoffValidationTask)).StateRevision

    // --- Scenario 26: targeted ReopenTask -----------------------------------
    // Section 5.2: reopening must name the state it invalidates and leave
    // CanCompleteTask false; Complete -> Open is Coordinator-authorizable.
    let completeForReopen id =
        expectOk $"create {id}" (createTask tempRoot (createRequest id "Reopen task")) |> ignore
        expectOk $"reopen {id} add evidence" (applyTask tempRoot id 0 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "built"))) |> ignore
        expectOk $"reopen {id} start" (applyTask tempRoot id 1 (StartWorkItem "W1")) |> ignore
        expectOk $"reopen {id} complete work" (applyTask tempRoot id 2 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
        expectOk $"reopen {id} verify" (applyTask tempRoot id 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore
        expectOk $"reopen {id} complete task" (applyTask tempRoot id 4 (CompleteTask (handoff "Terminal" "E1 recorded" "No task work remains.")))

    // AC-targeted reopen returns the AC to Pending and the lifecycle to Open.
    let acReopenTask = "TST-420"
    completeForReopen acReopenTask |> ignore

    let acReopened =
        expectOk
            "reopen by AC target"
            (applyTask
                tempRoot
                acReopenTask
                5
                (ReopenTask
                    { Reason = "AC invalidated"
                      DecisionRef = None
                      Targets = [ ReopenTarget.AcceptanceCriterionTarget "AC1" ] }))

    assertEqual "AC reopen lifecycle" "open" acReopened.Lifecycle
    assertEqual "AC reopen returns pending" Pending acReopened.AcceptanceCriteria.Head.State
    assertTrue "AC reopen cannot complete" (not (canCompleteTask acReopened))
    assertEqual "AC reopen increments revision once" 6 acReopened.StateRevision
    assertEqual "AC reopen creates coordinator decision" 1 acReopened.Decisions.Length
    assertEqual "AC reopen decision target" [ ReopenTaskTarget(acReopenTask, [ ReopenTarget.AcceptanceCriterionTarget "AC1" ]) ] acReopened.Decisions.Head.Targets

    // WorkItem-targeted reopen returns the terminal WorkItem to Pending.
    let workReopenTask = "TST-421"
    completeForReopen workReopenTask |> ignore

    let workReopened =
        expectOk
            "reopen by WorkItem target"
            (applyTask
                tempRoot
                workReopenTask
                5
                (ReopenTask
                    { Reason = "work invalidated"
                      DecisionRef = None
                      Targets = [ ReopenTarget.WorkItemTarget "W1" ] }))

    assertEqual "WorkItem reopen lifecycle" "open" workReopened.Lifecycle
    assertEqual "WorkItem reopen returns pending" PendingWork workReopened.WorkItems.Head.State
    assertEqual "WorkItem reopen clears result" None workReopened.WorkItems.Head.Result
    assertTrue "WorkItem reopen cannot complete" (not (canCompleteTask workReopened))

    // Guard-targeted reopen invalidates a disposed Guard disposition.
    let guardReopenTask = "TST-422"
    expectOk "create guard reopen task" (createTask tempRoot (createRequest guardReopenTask "Guard reopen task")) |> ignore

    expectOk
        "add guard reopen guard"
        (applyTask
            tempRoot
            guardReopenTask
            0
            (AddGuard
                { guardSpec "G1" TaskTarget BeforeComplete (requirement EvidenceKind.Review 1 None false) with
                    Applicability = ExplicitDecision MinimumAuthority.CoordinatorAuthority }))
    |> ignore

    expectOk "dispose guard reopen guard" (applyTask tempRoot guardReopenTask 1 (MarkGuardNotApplicable("G1", None))) |> ignore
    expectOk "guard reopen add AC evidence" (applyTask tempRoot guardReopenTask 2 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "built"))) |> ignore
    expectOk "guard reopen start work" (applyTask tempRoot guardReopenTask 3 (StartWorkItem "W1")) |> ignore
    expectOk "guard reopen complete work" (applyTask tempRoot guardReopenTask 4 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "guard reopen verify AC" (applyTask tempRoot guardReopenTask 5 (VerifyAcceptanceCriterion("AC1", [ "E1" ]))) |> ignore

    let guardCompleted =
        expectOk "guard reopen complete task" (applyTask tempRoot guardReopenTask 6 (CompleteTask (handoff "Terminal" "E1 recorded" "No task work remains.")))

    assertEqual "guard disposed before reopen" (GuardDisposition.NotApplicable "D1") guardCompleted.Guards.Head.Disposition

    let guardReopened =
        expectOk
            "reopen by Guard target"
            (applyTask
                tempRoot
                guardReopenTask
                7
                (ReopenTask
                    { Reason = "guard invalidated"
                      DecisionRef = None
                      Targets = [ ReopenTarget.GuardTarget "G1" ] }))

    assertEqual "Guard reopen lifecycle" "open" guardReopened.Lifecycle
    assertEqual "Guard reopen resets disposition" GuardDisposition.Applicable guardReopened.Guards.Head.Disposition
    assertTrue "Guard reopen cannot complete" (not (canCompleteTask guardReopened))

    // Empty, invalid, and unknown targets fail closed without mutating state.
    let invalidReopenTask = "TST-423"
    completeForReopen invalidReopenTask |> ignore
    let invalidRevision = 5

    expectRejected
        "reopen requires a target"
        "reopening requires at least one invalidation target"
        (applyTask tempRoot invalidReopenTask invalidRevision (ReopenTask { Reason = "no target"; DecisionRef = None; Targets = [] }))

    expectRejected
        "reopen blank reason"
        "reopen reason must be a non-empty single line"
        (applyTask tempRoot invalidReopenTask invalidRevision (ReopenTask { Reason = "  "; DecisionRef = None; Targets = [ ReopenTarget.AcceptanceCriterionTarget "AC1" ] }))

    expectRejected
        "reopen unknown acceptance criterion"
        "reopen targets unknown Acceptance Criterion 'AC9'"
        (applyTask tempRoot invalidReopenTask invalidRevision (ReopenTask { Reason = "unknown AC"; DecisionRef = None; Targets = [ ReopenTarget.AcceptanceCriterionTarget "AC9" ] }))

    expectRejected
        "reopen unknown work item"
        "reopen targets unknown WorkItem 'W9'"
        (applyTask tempRoot invalidReopenTask invalidRevision (ReopenTask { Reason = "unknown work"; DecisionRef = None; Targets = [ ReopenTarget.WorkItemTarget "W9" ] }))

    expectRejected
        "reopen unknown guard"
        "reopen targets unknown Guard 'G9'"
        (applyTask tempRoot invalidReopenTask invalidRevision (ReopenTask { Reason = "unknown guard"; DecisionRef = None; Targets = [ ReopenTarget.GuardTarget "G9" ] }))

    expectRejected
        "reopen duplicate targets"
        "reopen targets must be unique"
        (applyTask
            tempRoot
            invalidReopenTask
            invalidRevision
            (ReopenTask
                { Reason = "duplicate"
                  DecisionRef = None
                  Targets = [ ReopenTarget.AcceptanceCriterionTarget "AC1"; ReopenTarget.AcceptanceCriterionTarget "AC1" ] }))

    expectRejected
        "reopen invalid target id"
        "acceptance id has an invalid format"
        (applyTask tempRoot invalidReopenTask invalidRevision (ReopenTask { Reason = "bad id"; DecisionRef = None; Targets = [ ReopenTarget.AcceptanceCriterionTarget "BAD" ] }))

    assertEqual
        "reopen rejections are no-ops"
        invalidRevision
        (expectOk "get after reopen rejections" (getTask tempRoot invalidReopenTask)).StateRevision

    // A reopen that does not invalidate a completion requirement is rejected.
    let noOpReopenTask = "TST-425"
    expectOk "create no-op reopen task" (createTask tempRoot (createRequest noOpReopenTask "No-op reopen task")) |> ignore

    expectOk
        "add no-op reopen guard"
        (applyTask tempRoot noOpReopenTask 0 (AddGuard (guardSpec "G1" TaskTarget BeforeComplete (requirement EvidenceKind.Test 1 None false))))
    |> ignore

    expectOk "no-op reopen add guard evidence" (applyTask tempRoot noOpReopenTask 1 (AddEvidence (makeEvidence "E1" EvidenceKind.Test "guard test"))) |> ignore
    expectOk "no-op reopen add AC evidence" (applyTask tempRoot noOpReopenTask 2 (AddEvidence (makeEvidence "E2" EvidenceKind.Build "ac build"))) |> ignore
    expectOk "no-op reopen start work" (applyTask tempRoot noOpReopenTask 3 (StartWorkItem "W1")) |> ignore
    expectOk "no-op reopen complete work" (applyTask tempRoot noOpReopenTask 4 (CompleteWorkItem("W1", { Result = "done"; EvidenceRefs = [] }))) |> ignore
    expectOk "no-op reopen verify AC" (applyTask tempRoot noOpReopenTask 5 (VerifyAcceptanceCriterion("AC1", [ "E2" ]))) |> ignore
    expectOk "no-op reopen complete task" (applyTask tempRoot noOpReopenTask 6 (CompleteTask (handoff "Terminal" "recorded" "No task work remains."))) |> ignore

    expectRejected
        "reopen without invalidation"
        "reopening must invalidate at least one completion requirement"
        (applyTask tempRoot noOpReopenTask 7 (ReopenTask { Reason = "no invalidation"; DecisionRef = None; Targets = [ ReopenTarget.GuardTarget "G1" ] }))

    let afterNoOpReopen = expectOk "get after no-op reopen" (getTask tempRoot noOpReopenTask)
    assertEqual "no-op reopen did not bump revision" 7 afterNoOpReopen.StateRevision
    assertEqual "no-op reopen keeps complete lifecycle" "complete" afterNoOpReopen.Lifecycle

    // Non-terminal tasks cannot be reopened.
    let openReopenTask = "TST-424"
    expectOk "create open reopen task" (createTask tempRoot (createRequest openReopenTask "Open reopen task")) |> ignore

    expectRejected
        "reopen open task"
        "only a terminal task can be reopened"
        (applyTask tempRoot openReopenTask 0 (ReopenTask { Reason = "not terminal"; DecisionRef = None; Targets = [ ReopenTarget.AcceptanceCriterionTarget "AC1" ] }))

    assertEqual "open-task reopen is a no-op" 0 (expectOk "get open reopen task" (getTask tempRoot openReopenTask)).StateRevision

    // Aborted reopen is User-only and fails closed under Coordinator invocation.
    let abortedReopenJson = mutateJson (fun node -> node.["lifecycle"] <- JsonValue.Create "aborted")
    let abortedTask = expectOk "parse aborted task" (deserialize abortedReopenJson)
    assertEqual "aborted lifecycle" "aborted" abortedTask.Lifecycle

    expectDecideRejected
        "aborted reopen fails closed"
        "operation requires user authority; ordinary Coordinator invocation cannot authorize it"
        (decide
            profiles
            abortedTask
            (ReopenTask
                { Reason = "aborted reopen"
                  DecisionRef = None
                  Targets = [ ReopenTarget.AcceptanceCriterionTarget "AC1" ] }))

    printfn "OK task runtime recursive Work Tree, dependencies/readiness, ancestor activation, completion gating, Wait/Block/Resume, research, evidence DTO, AddEvidence, verify scope, supersession cascade, Guards (DTO/scope/checkpoints/independence/dispositions), Decisions/Open Questions (strict DTO/graph/targeting, TaskWide and WorkItem blocking, resolution), completion evidence, CanCompleteTask, CAS, persistence, Coordinator-only invocation authority (User/ProfilePolicy/confirmationRef sidecar rejection, exact target-bound Guard dispositions, Coordinator disposition creation/reuse, CLI fail-closed), WorkItem ownership/inheritance, terminal handoff persistence/history, and targeted reopen (AC/WorkItem/Guard, fail-closed targets, aborted User-only)"
finally
    if Directory.Exists tempRoot && tempRoot.Contains("taskruntime-tests-", StringComparison.Ordinal) then
        Directory.Delete(tempRoot, true)
