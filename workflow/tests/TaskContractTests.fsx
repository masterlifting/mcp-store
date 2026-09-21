// Focused deterministic coverage for the slice-7 contract/persistence integrity
// surface: Draft/Baselined baseline and contractRevision, contract-fingerprint
// canonicalization, drift gating of material mutation, deterministic patch IDs
// and exact-target authorization, Coordinator non-weakening, fail-closed User
// authority, canonical restore reconciliation, and runtime-only sidecar reads.
// Plain FSI harness because the solution contract forbids adding a
// project/package system. All fixtures live under one fresh GUID temp root and
// only that root is removed.

#load "../ComputationExpressions.fs"
#load "../TaskRuntime.fs"

open System
open System.IO
open System.Text.Json.Nodes
open TaskRuntime

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

let spec id title =
    { Id = id
      Title = title
      DependsOn = []
      Children = [] }

let createRequest id title criteria =
    { Id = id
      Title = title
      Kind = Execution
      AcceptanceCriteria = criteria
      WorkItems = [ spec "W1" "Do the work" ] }

let makeEvidence id kind summary =
    { Id = id
      Kind = kind
      Source = EvidenceSource "tester"
      Subject = None
      ProducerRole = None
      ProducerId = None
      Reference = None
      Summary = summary }

let guardSpec =
    { Id = "G1"
      Target = TaskTarget
      Checkpoint = BeforeComplete
      Requirement =
        EvidenceRequired
            { Kind = EvidenceKind.Test
              MinimumCount = 1
              ProducerRole = None
              RequireIndependentProducer = false }
      Applicability = Always
      Waiver = NotWaivable }

let sidecarPath root id =
    Path.Combine(root, ".tasks", id, SidecarFileName)

let readSidecarJson root id =
    JsonNode.Parse(File.ReadAllText(sidecarPath root id)).AsObject()

let writeSidecarJson root id (node: JsonObject) =
    File.WriteAllText(sidecarPath root id, node.ToJsonString())

let tempRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"taskcontract-tests-{Guid.NewGuid():N}")

Directory.CreateDirectory tempRoot |> ignore

try
    // --- Baseline: Draft -> Baselined keeps contractRevision and records the fingerprint ---
    let baselineTask = "TST-100"

    let baselineCreated =
        expectOk
            "create baseline task"
            (createTask tempRoot (createRequest baselineTask "Baseline task" [ "AC1", "Execution completes" ]))

    assertEqual "created contract state" Draft baselineCreated.ContractState
    assertEqual "created contract fingerprint" "" baselineCreated.ContractFingerprint
    assertEqual "created contract revision" 1 baselineCreated.ContractRevision

    // A Draft patch edits the contract without creating revision history.
    let draftPatched =
        expectOk
            "draft objective patch"
            (applyTask tempRoot baselineTask 0 (ApplyContractPatch(ContractPatch.SetObjective "draft objective", None)))

    assertEqual "draft patch keeps state" Draft draftPatched.ContractState
    assertEqual "draft patch keeps empty fingerprint" "" draftPatched.ContractFingerprint
    assertEqual "draft patch keeps contract revision" 1 draftPatched.ContractRevision
    assertEqual "draft patch applied objective" "draft objective" draftPatched.Objective

    // The first material start establishes the baseline; revision stays at 1.
    let baselined =
        expectOk "start work baselines" (applyTask tempRoot baselineTask 1 (StartWorkItem "W1"))

    assertEqual "baseline sets state" Baselined baselined.ContractState
    assertEqual "baseline keeps contract revision" 1 baselined.ContractRevision
    assertEqual "baseline records fingerprint" (contractFingerprintOf (contractContentOf baselined)) baselined.ContractFingerprint
    assertTrue "baseline clears drift" (not (contractDrift baselined))

    // AC verification changes state, not contract content, so it is not drift.
    expectOk "baseline add evidence" (applyTask tempRoot baselineTask 2 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "build passed")))
    |> ignore

    let verified =
        expectOk "baseline verify AC1" (applyTask tempRoot baselineTask 3 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))

    assertTrue "verification is not drift" (not (contractDrift verified))
    assertEqual "verification keeps contract revision" 1 verified.ContractRevision

    // --- Fingerprint is deterministic and sensitive to prose and AC text ---
    let contentA =
        { Objective = "objective"
          Scope = "scope"
          NonGoals = "non-goals"
          AcceptanceCriteria = [ "AC1", "first"; "AC2", "second" ] }

    let contentA' =
        { Objective = "objective"
          Scope = "scope"
          NonGoals = "non-goals"
          AcceptanceCriteria = [ "AC1", "first"; "AC2", "second" ] }

    assertEqual "fingerprint deterministic" (contractFingerprintOf contentA) (contractFingerprintOf contentA')

    assertTrue
        "fingerprint sensitive to AC text"
        (contractFingerprintOf contentA <> contractFingerprintOf { contentA with AcceptanceCriteria = [ "AC1", "changed"; "AC2", "second" ] })

    assertTrue "fingerprint sensitive to prose" (contractFingerprintOf contentA <> contractFingerprintOf { contentA with Objective = "other" })

    // --- Patch ID is deterministic and payload-exact ---
    let patchA = ContractPatch.AddAcceptanceCriterion { Id = "AC2"; Text = "Second criterion" }
    let patchA2 = ContractPatch.AddAcceptanceCriterion { Id = "AC2"; Text = "Second criterion" }
    let patchB = ContractPatch.AddAcceptanceCriterion { Id = "AC2"; Text = "Different text" }
    let patchC = ContractPatch.SetObjective "Second criterion"

    assertEqual "patch id deterministic" (contractPatchIdOf patchA) (contractPatchIdOf patchA2)
    assertTrue "patch id sensitive to payload" (contractPatchIdOf patchA <> contractPatchIdOf patchB)
    assertTrue "patch id sensitive to operation" (contractPatchIdOf patchA <> contractPatchIdOf patchC)

    // --- Post-baseline: Coordinator may strengthen but never weaken ---
    let coordTask = "TST-101"
    expectOk "create coordinator task" (createTask tempRoot (createRequest coordTask "Coordinator task" [ "AC1", "Execution completes" ]))
    |> ignore

    expectOk "draft add guard" (applyTask tempRoot coordTask 0 (AddGuard guardSpec)) |> ignore
    let coordBaselined = expectOk "coordinator baseline" (applyTask tempRoot coordTask 1 (StartWorkItem "W1"))
    assertEqual "coordinator baseline revision" 1 coordBaselined.ContractRevision

    let addPatch = ContractPatch.AddAcceptanceCriterion { Id = "AC2"; Text = "Second criterion" }
    let strengthened = expectOk "coordinator adds AC" (applyTask tempRoot coordTask 2 (ApplyContractPatch(addPatch, None)))

    assertEqual "strengthening bumps contract revision" 2 strengthened.ContractRevision
    assertEqual "strengthening records fingerprint" (contractFingerprintOf (contractContentOf strengthened)) strengthened.ContractFingerprint
    assertTrue "strengthening clears drift" (not (contractDrift strengthened))
    assertEqual "strengthening adds AC" 2 strengthened.AcceptanceCriteria.Length

    // Authorization is bound to the deterministic patch ID, not the new revision.
    let patchDecision = strengthened.Decisions |> List.last
    assertEqual "patch decision kind" ContractRevision patchDecision.Kind
    assertEqual "patch decision exact target" [ ContractPatchTarget(contractPatchIdOf addPatch) ] patchDecision.Targets

    // Weakening prose/AC requires User authority and fails closed under ordinary
    // Coordinator invocation; a baselined guard cannot be physically removed.
    expectRejected
        "coordinator cannot set objective"
        "operation requires user authority; ordinary Coordinator invocation cannot authorize it"
        (applyTask tempRoot coordTask 3 (ApplyContractPatch(ContractPatch.SetObjective "weaker", None)))

    expectRejected
        "coordinator cannot update AC"
        "operation requires user authority; ordinary Coordinator invocation cannot authorize it"
        (applyTask tempRoot coordTask 3 (ApplyContractPatch(ContractPatch.UpdateAcceptanceCriterion("AC1", "weaker"), None)))

    expectRejected
        "coordinator cannot remove AC"
        "operation requires user authority; ordinary Coordinator invocation cannot authorize it"
        (applyTask tempRoot coordTask 3 (ApplyContractPatch(ContractPatch.RemoveAcceptanceCriterion "AC1", None)))

    expectRejected
        "baselined guard cannot be removed"
        "a baselined guard cannot be physically removed"
        (applyTask tempRoot coordTask 3 (ApplyContractPatch(ContractPatch.RemoveGuard "G1", None)))

    assertEqual "weakening rejections are no-ops" 2 (expectOk "get coordinator task" (getTask tempRoot coordTask)).ContractRevision

    // --- Drift blocks material mutation; restore reconciles and preserves state ---
    let driftTask = "TST-102"

    expectOk
        "create drift task"
        (createTask tempRoot (createRequest driftTask "Drift task" [ "AC1", "Execution completes"; "AC2", "Second criterion" ]))
    |> ignore

    expectOk "drift baseline" (applyTask tempRoot driftTask 0 (StartWorkItem "W1")) |> ignore
    expectOk "drift add evidence" (applyTask tempRoot driftTask 1 (AddEvidence (makeEvidence "E1" EvidenceKind.Build "build passed")))
    |> ignore

    let beforeDrift = expectOk "drift verify AC1" (applyTask tempRoot driftTask 2 (VerifyAcceptanceCriterion("AC1", [ "E1" ])))
    let canonicalContent = contractContentOf beforeDrift
    let recordedFingerprint = beforeDrift.ContractFingerprint
    assertEqual "drift task baseline revision" 1 beforeDrift.ContractRevision

    // Simulate an out-of-band rewrite: changed prose plus an added criterion.
    let driftedJson = readSidecarJson tempRoot driftTask
    driftedJson.["objective"] <- JsonValue.Create "out-of-band objective"
    driftedJson.["acceptanceCriteria"].AsArray().Add(JsonNode.Parse """{"id":"AC3","text":"Out of band","state":"pending","evidenceRefs":[]}""")
    writeSidecarJson tempRoot driftTask driftedJson

    let drifted = expectOk "get drifted task" (getTask tempRoot driftTask)
    assertTrue "out-of-band rewrite is drift" (contractDrift drifted)
    assertEqual "drift preserves recorded fingerprint" recordedFingerprint drifted.ContractFingerprint

    // get remains lenient while validate reports the contract drift explicitly.
    expectRejected
        "validate reports contract drift"
        "CONTRACT_DRIFT: the persisted contract no longer matches its recorded fingerprint"
        (validateTask tempRoot driftTask)

    expectRejected
        "drift blocks material mutation"
        "CONTRACT_DRIFT"
        (applyTask tempRoot driftTask drifted.StateRevision (AddEvidence (makeEvidence "E2" EvidenceKind.Test "late")))

    expectRejected
        "drift blocks contract patch"
        "CONTRACT_DRIFT"
        (applyTask tempRoot driftTask drifted.StateRevision (ApplyContractPatch(ContractPatch.AddAcceptanceCriterion { Id = "AC4"; Text = "Late" }, None)))

    // Accepting an out-of-band rewrite is a User-authorized material change.
    expectRejected
        "accept external contract fails closed"
        "operation requires user authority; ordinary Coordinator invocation cannot authorize it"
        (applyTask tempRoot driftTask drifted.StateRevision (ReconcileContractDrift ReconciliationPlan.AcceptExternalContract))

    let restored =
        expectOk
            "restore canonical contract"
            (applyTask
                tempRoot
                driftTask
                drifted.StateRevision
                (ReconcileContractDrift(ReconciliationPlan.RestoreCanonicalContract canonicalContent)))

    assertTrue "restore clears drift" (not (contractDrift restored))
    assertEqual "restore bumps contract revision" 2 restored.ContractRevision
    assertEqual "restore removes absent AC" [ "AC1"; "AC2" ] (restored.AcceptanceCriteria |> List.map _.Id)

    assertEqual
        "restore preserves matching verified state"
        (Verified [ "E1" ])
        (restored.AcceptanceCriteria |> List.find (fun criterion -> criterion.Id = "AC1")).State

    assertEqual
        "restore preserves matching pending state"
        Pending
        (restored.AcceptanceCriteria |> List.find (fun criterion -> criterion.Id = "AC2")).State

    assertEqual "restore resets drifted prose" "" restored.Objective
    assertEqual "restore keeps recorded fingerprint" recordedFingerprint restored.ContractFingerprint

    // --- Schema-v3 cutover: v2 sidecars are rejected, not read -------------
    // D1 eliminated in-runtime v2 compatibility, so a v2 sidecar is an
    // unsupported document and migration is strictly out of band.
    let legacyId = "TST-103"
    let legacyDirectory = Path.Combine(tempRoot, ".tasks", legacyId)
    Directory.CreateDirectory legacyDirectory |> ignore

    // A schema-v2 document with the full field set still fails the schema guard,
    // proving the rejection is the version check rather than a missing field.
    let legacyV2Json =
        """{"schemaVersion":2,"id":"TST-103","title":"Legacy task","created":"2026-09-10T00:00:00.0000000+00:00","kind":"execution","profile":"general","profileFingerprint":"general-v1","objective":"","scope":"","nonGoals":"","contractState":"draft","contractFingerprint":"","contractRevision":1,"stateRevision":0,"lifecycle":"open","evidence":[],"acceptanceCriteria":[{"id":"AC1","text":"Execution completes","state":"pending","evidenceRefs":[]}],"guards":[],"profileGuardKeys":{},"decisions":[],"questions":[],"workItems":[{"id":"W1","title":"Do the work","state":"pending","result":"","acceptanceRefs":["AC1"],"dependsOn":[],"evidenceRefs":[],"children":[]}],"completionHistory":[]}"""

    File.WriteAllText(Path.Combine(legacyDirectory, SidecarFileName), legacyV2Json)

    expectRejected "v2 sidecar rejected on get" "unsupported schemaVersion 2" (getTask tempRoot legacyId)
    expectRejected "v2 sidecar rejected on deserialize" "unsupported schemaVersion 2" (deserialize legacyV2Json)

    // A complete v3 document is the migration target shape.
    let legacyV3Json =
        sprintf
            """{"schemaVersion":3,"id":"TST-103","title":"Legacy task","created":"2026-09-10T00:00:00.0000000+00:00","kind":"execution","profile":"general","profileFingerprint":"%s","objective":"","scope":"","nonGoals":"","contractState":"draft","contractFingerprint":"","contractRevision":1,"stateRevision":0,"lifecycle":"open","evidence":[],"acceptanceCriteria":[{"id":"AC1","text":"Execution completes","state":"pending","evidenceRefs":[]}],"guards":[],"profileGuardKeys":{},"decisions":[],"questions":[],"workItems":[{"id":"W1","title":"Do the work","state":"pending","result":"","acceptanceRefs":["AC1"],"dependsOn":[],"evidenceRefs":[],"children":[]}],"completionHistory":[]}"""
            (expectOk "resolve builtin profiles" (resolveProfiles tempRoot)).[GeneralProfileId].Fingerprint

    expectOk "v3 legacy fixture parses" (deserialize legacyV3Json) |> ignore

    // The retired legacy fingerprint is rejected as profile drift.
    let legacyFingerprintJson =
        let node = JsonNode.Parse(legacyV3Json).AsObject()
        node.["profileFingerprint"] <- JsonValue.Create "general-v1"
        node.ToJsonString()

    expectRejected
        "legacy general-v1 fingerprint rejected"
        "general profile fingerprint does not match"
        (deserialize legacyFingerprintJson)

    // Inconsistent persisted defaults fail closed instead of silently drifting.
    let baselinedNoFingerprint =
        let node = JsonNode.Parse(legacyV3Json).AsObject()
        node.["contractState"] <- JsonValue.Create "baselined"
        node.ToJsonString()

    expectRejected
        "baselined without fingerprint rejected"
        "a baselined contract requires a recorded fingerprint"
        (deserialize baselinedNoFingerprint)

    let draftWithFingerprint =
        let node = JsonNode.Parse(legacyV3Json).AsObject()
        node.["contractFingerprint"] <- JsonValue.Create "deadbeef"
        node.ToJsonString()

    expectRejected
        "draft with fingerprint rejected"
        "a draft contract cannot carry a recorded fingerprint"
        (deserialize draftWithFingerprint)

    // --- Runtime-only missing-sidecar and occupied-directory behavior --------
    let missingSidecarId = "TST-500"
    let missingSidecarDirectory = Path.Combine(tempRoot, ".tasks", missingSidecarId)
    Directory.CreateDirectory missingSidecarDirectory |> ignore
    File.WriteAllText(Path.Combine(missingSidecarDirectory, "TASK.md"), "historical record\n")

    for operation, result in
        [ "get", getTask tempRoot missingSidecarId
          "validate", validateTask tempRoot missingSidecarId
          "apply", applyTask tempRoot missingSidecarId 0 (StartWorkItem "W1") ] do
        expectRejected (operation + " ignores historical TASK.md") "runtime sidecar does not exist" result

    assertTrue "missing-sidecar operations do not create a lock" (not (File.Exists(Path.Combine(missingSidecarDirectory, LockFileName))))
    assertTrue "missing-sidecar operations do not create a sidecar" (not (File.Exists(sidecarPath tempRoot missingSidecarId)))

    // --- Evidence-only task bootstrap ----------------------------------------
    // A user-supplied issue record is immutable evidence, not runtime state. A
    // successful bootstrap must leave both its bytes and nested references intact.
    let bootstrapId = "TST-600"
    let bootstrapDirectory = Path.Combine(tempRoot, ".tasks", bootstrapId)
    let bootstrapReferences = Path.Combine(bootstrapDirectory, "references", "nested")
    Directory.CreateDirectory bootstrapReferences |> ignore
    let issueBytes = [| 0uy; 1uy; 2uy; 239uy; 255uy |]
    let issuePath = Path.Combine(bootstrapDirectory, "references", "issue.md")
    let nestedPath = Path.Combine(bootstrapReferences, "context.txt")
    File.WriteAllBytes(issuePath, issueBytes)
    File.WriteAllText(nestedPath, "preserve nested evidence\n")

    let unsupportedEvidenceBootstrap =
        "evidence-only task bootstrap requires Windows directory-handle boundaries"

    if OperatingSystem.IsWindows() then
        expectOk
            "create from evidence-only directory"
            (createTask tempRoot (createRequest bootstrapId "Evidence bootstrap" [ "AC1", "x" ]))
        |> ignore
    else
        expectRejected
            "Unix evidence bootstrap is rejected before mutation"
            unsupportedEvidenceBootstrap
            (createTask tempRoot (createRequest bootstrapId "Evidence bootstrap" [ "AC1", "x" ]))

    assertEqual "bootstrap preserves issue bytes" issueBytes (File.ReadAllBytes issuePath)
    assertEqual "bootstrap preserves nested evidence" "preserve nested evidence\n" (File.ReadAllText nestedPath)
    assertTrue
        "Unix evidence bootstrap creates no lock or sidecar"
        (OperatingSystem.IsWindows()
         || (not (File.Exists(sidecarPath tempRoot bootstrapId))
             && not (File.Exists(Path.Combine(bootstrapDirectory, LockFileName)))))

    if OperatingSystem.IsWindows() then
        assertTrue "bootstrap writes runtime sidecar" (File.Exists(sidecarPath tempRoot bootstrapId))
        expectRejected
            "duplicate create reports runtime state"
            SidecarFileName
            (createTask tempRoot (createRequest bootstrapId "Duplicate bootstrap" [ "AC1", "x" ]))

    let prepareEvidenceOnly id =
        let directory = Path.Combine(tempRoot, ".tasks", id, "references")
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(Path.Combine(directory, "issue.md"), "immutable issue\n")
        Path.GetDirectoryName directory

    let expectEvidenceRejected name windowsFragment result =
        expectRejected name (if OperatingSystem.IsWindows() then windowsFragment else unsupportedEvidenceBootstrap) result

    let runtimeJsonId = "TST-601"
    let runtimeJsonDirectory = prepareEvidenceOnly runtimeJsonId
    let runtimeJsonEvidencePath = Path.Combine(runtimeJsonDirectory, "references", "issue.md")
    let runtimeJsonEvidenceBytes = File.ReadAllBytes runtimeJsonEvidencePath
    File.WriteAllText(Path.Combine(runtimeJsonDirectory, SidecarFileName), "user runtime state")
    expectEvidenceRejected
        "pre-existing runtime.json is rejected"
        SidecarFileName
        (createTask tempRoot (createRequest runtimeJsonId "Runtime state" [ "AC1", "x" ]))
    assertEqual
        "rejected runtime-state create preserves evidence"
        runtimeJsonEvidenceBytes
        (File.ReadAllBytes runtimeJsonEvidencePath)

    let runtimeLockId = "TST-602"
    let runtimeLockDirectory = prepareEvidenceOnly runtimeLockId
    File.WriteAllText(Path.Combine(runtimeLockDirectory, LockFileName), "user lock state")
    expectEvidenceRejected
        "pre-existing runtime.lock is rejected"
        LockFileName
        (createTask tempRoot (createRequest runtimeLockId "Lock state" [ "AC1", "x" ]))

    let historicalId = "TST-603"
    let historicalDirectory = prepareEvidenceOnly historicalId
    File.WriteAllText(Path.Combine(historicalDirectory, "TASK.md"), "historical record\n")
    expectEvidenceRejected
        "pre-existing TASK.md is rejected"
        "TASK.md"
        (createTask tempRoot (createRequest historicalId "Historical state" [ "AC1", "x" ]))

    let unsupportedId = "TST-604"
    let unsupportedDirectory = prepareEvidenceOnly unsupportedId
    File.WriteAllText(Path.Combine(unsupportedDirectory, "notes.txt"), "unsupported sidecar\n")
    expectEvidenceRejected
        "unsupported pre-existing file is rejected"
        "unsupported pre-existing entry"
        (createTask tempRoot (createRequest unsupportedId "Unsupported state" [ "AC1", "x" ]))

    // A directory at the task root is unsupported evidence and must not be
    // claimed by create.
    let unsupportedRootId = "TST-605"
    let unsupportedRootDirectory = Path.Combine(tempRoot, ".tasks", unsupportedRootId, "notes")
    Directory.CreateDirectory unsupportedRootDirectory |> ignore
    expectEvidenceRejected
        "unsupported pre-existing root directory is rejected"
        "unsupported pre-existing entry"
        (createTask tempRoot (createRequest unsupportedRootId "Unsupported root state" [ "AC1", "x" ]))

    // Existing evidence directories use the same lock/recheck boundary as new
    // task directories: exactly one creator commits and evidence remains intact.
    if OperatingSystem.IsWindows() then
        let raceId = "TST-606"
        let raceDirectory = prepareEvidenceOnly raceId
        let raceIssuePath = Path.Combine(raceDirectory, "references", "issue.md")
        let raceIssueBytes = File.ReadAllBytes raceIssuePath
        let racersPerRound = 6
        use gate = new System.Threading.Barrier(racersPerRound)
        let results: Result<TaskModel, RuntimeError> array = Array.zeroCreate racersPerRound

        let racers =
            [ for racer in 0 .. racersPerRound - 1 ->
                  let thread =
                      System.Threading.Thread(fun () ->
                          gate.SignalAndWait()
                          results.[racer] <- createTask tempRoot (createRequest raceId ($"Evidence racer {racer}") [ "AC1", "x" ]))

                  thread.IsBackground <- true
                  thread.Start()
                  thread ]

        racers |> List.iter (fun thread -> thread.Join())

        let winners = results |> Array.choose (function | Ok task -> Some task | Error _ -> None)
        assertEqual "evidence bootstrap race has one winner" 1 winners.Length
        assertEqual "evidence bootstrap race preserves bytes" raceIssueBytes (File.ReadAllBytes raceIssuePath)

    printfn "OK slice-7 contract/persistence: baseline/revision/fingerprint, patch ID/target, Coordinator non-weakening, drift gating, fail-closed User authority, canonical restore, runtime-only missing-sidecar behavior, evidence-only bootstrap preservation, disallowed pre-existing state rejection, and concurrent evidence bootstrap"
finally
    if Directory.Exists tempRoot && tempRoot.Contains("taskcontract-tests-", StringComparison.Ordinal) then
        Directory.Delete(tempRoot, true)
