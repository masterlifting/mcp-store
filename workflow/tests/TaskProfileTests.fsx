// Focused deterministic coverage for the generic Profile resolver:
// no-overlay general default, strict project-profile JSON/duplicate/weakening
// rejection, deterministic same-ID overlay merge with mandatory Guard
// materialization, missing-profile mutation blocking with lenient get,
// Draft-vs-Baselined profile drift absorption/reconciliation, and fail-closed
// baselined weakening reclassification. It also covers the built-in `software`
// policy: Execution materializes build/test/review gates, Research materializes
// none, and Draft reclassification syncs the gate set to the target profile/kind.
// Finally it covers the shared built-in `harness` profile: registry/default
// resolution, policy-only capabilities, per-Kind guard materialization/isolation,
// fail-closed review under Coordinator authority, a satisfiable non-waivable
// research investigation gate, and monotonic same-ID overlay merge.
// Plain FSI harness because the solution contract forbids adding a
// project/package system. All fixtures live under one fresh GUID temp root and
// only that root is removed.

#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"

open System
open System.IO
open Workflow

let assertEqual name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let expectOk name (result: Async<Result<'a, RuntimeError>>) : Async<'a> =
    async {
        match! result with
        | Ok value -> return value
        | Error error -> return failwithf "%s: expected Ok, got Error %s" name (renderError error)
    }

let expectRejected (name: string) (fragment: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ -> return failwithf "%s: expected rejection, got Ok" name
        | Error error ->
            let message = renderError error
            if message.Contains(fragment, StringComparison.Ordinal) then
                return ()
            else
                return failwithf "%s: expected '%s', got '%s'" name fragment message
    }

let spec id title =
    { Id = id
      Title = title
      DependsOn = []
      Children = [] }

let createRequest id title =
    { Id = id
      Title = title
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "Execution completes" ]
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

let defaultHandoff: TerminalHandoff =
    { State = "Complete"
      EvidenceSummary = "Evidence recorded"
      Next = "No task work remains." }

let hasRequirement kind minimumCount (guards: Guard list) =
    guards
    |> List.exists (fun guard ->
        guard.Requirement = EvidenceRequired { Kind = kind; MinimumCount = minimumCount; ProducerRole = None; RequireIndependentProducer = false })

let guardOfKind kind (guards: Guard list) =
    guards
    |> List.find (fun guard ->
        match guard.Requirement with
        | EvidenceRequired requirement -> requirement.Kind = kind)

let requirementOf (guard: Guard) =
    match guard.Requirement with
    | EvidenceRequired requirement -> requirement

// Project profile fixtures follow the strict ProfileSource JSON shape.
let guardJsonWith (key: string) (evidenceKind: string) (minimumCount: int) (applicability: string) (waiver: string) =
    sprintf
        """{"key":"%s","target":"task","checkpoint":"beforeComplete","evidenceKind":"%s","minimumCount":%d,"applicability":"%s","waiver":"%s"}"""
        key
        evidenceKind
        minimumCount
        applicability
        waiver

let guardJson (key: string) (evidenceKind: string) (minimumCount: int) =
    guardJsonWith key evidenceKind minimumCount "always" "notWaivable"

let profileJson (id: string) (description: string option) (requiredCaps: string list) (guards: string list) =
    let fields = ResizeArray<string>()
    fields.Add("\"schemaVersion\":1")
    fields.Add(sprintf "\"id\":\"%s\"" id)

    match description with
    | Some value -> fields.Add(sprintf "\"description\":\"%s\"" value)
    | None -> ()

    if not requiredCaps.IsEmpty then
        let caps = requiredCaps |> List.map (sprintf "\"%s\"") |> String.concat ","
        fields.Add(sprintf "\"capabilityEnvelope\":{\"required\":[%s]}" caps)

    if not guards.IsEmpty then
        fields.Add(sprintf "\"policy\":{\"guards\":[%s]}" (String.concat "," guards))

    "{" + String.concat "," fields + "}"

// Same-ID overlay JSON carrying only role defaults, used to prove that an
// overlay may extend role defaults but never rebind an already-resolved purpose.
let roleDefaultOverlayJson (id: string) (purpose: string) (role: string) =
    sprintf
        """{"schemaVersion":1,"id":"%s","policy":{"roleDefaults":[{"purpose":"%s","role":"%s"}]}}"""
        id
        purpose
        role

let parentRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"taskprofile-tests-{Guid.NewGuid():N}")

Directory.CreateDirectory parentRoot |> ignore

let root label =
    let path = Path.Combine(parentRoot, label)
    Directory.CreateDirectory path |> ignore
    path

let writeProfiles root (files: (string * string) list) =
    let directory = Path.Combine(root, ".workflow", "profiles")
    Directory.CreateDirectory directory |> ignore

    for name, content in files do
        File.WriteAllText(Path.Combine(directory, name), content)

let resolveWith label files =
    let profileRoot = root label
    writeProfiles profileRoot files
    resolveProfiles profileRoot

let profilePath profileRoot name =
    Path.Combine(profileRoot, ".workflow", "profiles", name)

// Standalone entry bridge: FSI needs one synchronous script entry.
async {
    try
        // --- No-overlay default: only the built-in general profile is active ---
        let rootA = root "no-overlay"

        let! builtins = expectOk "resolve builtins without profiles directory" (resolveProfiles rootA)
        assertEqual "builtin registry count" 3 (Map.count builtins)
        assertTrue "harness profile is registered by default" (builtins.ContainsKey HarnessProfileId)
        assertTrue "retired opencode profile is absent" (not (builtins.ContainsKey "opencode"))
        assertTrue "profile resolution requires no OpenCode directory" (not (Directory.Exists(Path.Combine(rootA, ".opencode"))))

        let previousDirectory = Environment.CurrentDirectory
        let unrelatedCwd = root "unrelated-cwd"
        try
            Environment.CurrentDirectory <- unrelatedCwd
            let canonicalDirectory = Path.Combine(rootA, ".workflow", "profiles")
            Directory.CreateDirectory canonicalDirectory |> ignore
            File.WriteAllText(Path.Combine(canonicalDirectory, "cwd-profile.json"), profileJson "cwd-profile" (Some "Found from project root") [] [])
            let! fromUnrelatedCwd = expectOk "project profile discovery ignores current directory" (resolveProfiles rootA)
            assertTrue "canonical profile under project root discovered from unrelated CWD" (fromUnrelatedCwd.ContainsKey "cwd-profile")
            assertTrue "unrelated CWD profile directory is ignored" (not (Directory.Exists(Path.Combine(unrelatedCwd, ".workflow", "profiles"))))
        finally
            Environment.CurrentDirectory <- previousDirectory

        let general = builtins.[GeneralProfileId]
        assertEqual "general origin" BuiltIn general.Origin
        // Every effective profile uses the same content-derived fingerprint,
        // including built-in general; no stable label is special-cased.
        assertTrue "general fingerprint is content-derived" (general.Fingerprint <> "general-v1")
        assertTrue "general fingerprint is a 64-hex digest" (general.Fingerprint.Length = 64)
        assertTrue "general has no profile guards" general.Definition.Policy.Guards.IsEmpty

        let generalFingerprint = general.Fingerprint

        let! defaultTask = expectOk "create default-profile task" (createTask rootA (createRequest "TST-901" "Default"))

        assertEqual "default profile id" GeneralProfileId defaultTask.Profile
        assertEqual "default profile fingerprint" generalFingerprint defaultTask.ProfileFingerprint
        assertTrue "default task has no guards" defaultTask.Guards.IsEmpty

        do! expectRejected "unknown create profile" "unknown profile 'missing-profile'" (createTaskWithProfile rootA (Some "missing-profile") (createRequest "TST-902" "Unknown"))

        // --- Strict project-profile JSON: malformed/unknown/schema/duplicate/weakening ---
        do! expectRejected "malformed profile JSON" "is not valid JSON" (resolveWith "b-malformed" [ "bad.json", "{ not json" ])

        do! expectRejected "unknown profile property" "contains unknown property 'bogus'" (resolveWith "b-unknown-property" [ "p.json", """{"schemaVersion":1,"id":"x","description":"d","bogus":true}""" ])

        do! expectRejected "unsupported profile schema version" "schemaVersion must be 1" (resolveWith "b-schema" [ "p.json", """{"schemaVersion":2,"id":"x","description":"d"}""" ])

        do! expectRejected "duplicate project profile id" "duplicate project profile id 'dup'" (resolveWith "b-duplicate" [ "a.json", profileJson "dup" (Some "first") [] []; "b.json", profileJson "dup" (Some "second") [] [] ])

        // A same-ID overlay targets a shared (builtin) profile; project files with
        // duplicate IDs are rejected before merge. Within an overlay, a later
        // same-key guard that weakens an earlier one must fail resolution.
        let weakeningOverlay applicability waiver =
            profileJson
                GeneralProfileId
                None
                []
                [ guardJson "review" "review" 1
                  guardJsonWith "review" "review" 1 applicability waiver ]

        do! expectRejected "same-id overlay weakens applicability" "overlay weakens its applicability" (resolveWith "b-weaken-applicability" [ "overlay.json", weakeningOverlay "explicitDecision:coordinator" "notWaivable" ])

        do! expectRejected "same-id overlay weakens waiver" "overlay weakens its waiver policy" (resolveWith "b-weaken-waiver" [ "overlay.json", weakeningOverlay "always" "waivableBy:coordinator" ])

        // --- Deterministic same-ID overlay merge and mandatory Guard materialization ---
        let rootC = root "overlay-merge"

        writeProfiles
            rootC
            [ "01-general-overlay.json",
              profileJson
                  GeneralProfileId
                  None
                  [ "cap-base" ]
                  [ guardJson "review" "review" 1
                    guardJson "review" "review" 3 ] ]

        let! resolved = expectOk "resolve general overlay" (resolveProfiles rootC)
        let overlaid = resolved.[GeneralProfileId]
        assertEqual "overlay origin" Overlay overlaid.Origin

        assertTrue
            "overlay required capabilities merge"
            (overlaid.Definition.CapabilityEnvelope.Required |> List.contains "cap-base")

        assertEqual "overlay guard count" 1 overlaid.Definition.Policy.Guards.Length
        assertEqual "overlay guard strengthens minimum count" 3 overlaid.Definition.Policy.Guards.Head.Requirement.MinimumCount
        assertTrue "overlay changes fingerprint" (overlaid.Fingerprint <> generalFingerprint)

        let! resolvedAgain = expectOk "resolve general overlay again" (resolveProfiles rootC)
        assertEqual "overlay resolution deterministic" (renderProfile overlaid) (renderProfile resolvedAgain.[GeneralProfileId])

        let! overlaidTask = expectOk "create overlaid-general task" (createTaskWithProfile rootC (Some GeneralProfileId) (createRequest "TST-903" "Overlaid"))

        assertEqual "overlaid task profile" GeneralProfileId overlaidTask.Profile
        assertEqual "overlaid task fingerprint" overlaid.Fingerprint overlaidTask.ProfileFingerprint
        assertEqual "overlaid task materializes mandatory guard" 1 overlaidTask.Guards.Length

        let materialized = overlaidTask.Guards.Head
        assertEqual "materialized guard origin" ProfileMaterialized materialized.Origin
        assertEqual "materialized guard disposition" GuardDisposition.Applicable materialized.Disposition

        assertEqual
            "materialized guard requirement"
            (EvidenceRequired
                { Kind = EvidenceKind.Review
                  MinimumCount = 3
                  ProducerRole = None
                  RequireIndependentProducer = false })
            materialized.Requirement

        do! expectRejected "mandatory profile guard cannot be removed from draft" "profile-materialized and cannot be removed" (applyTask rootC "TST-903" 0 (ApplyContractPatch(ContractPatch.RemoveGuard materialized.Id, None)))

        // A same-ID overlay may add role defaults but cannot rebind a purpose the
        // shared profile already resolved.
        let! addedRole = expectOk "overlay adds a new role default" (resolveWith "b-role-add" [ "overlay.json", roleDefaultOverlayJson SoftwareProfileId "documentation" "writer" ])

        let addedRoleDefaults = addedRole.[SoftwareProfileId].Definition.Policy.RoleDefaults

        assertEqual
            "overlay keeps shared role default"
            "engineer"
            (addedRoleDefaults |> List.find (fun role -> role.Purpose = "implementation") |> _.Role)

        assertEqual
            "overlay appends new role default"
            "writer"
            (addedRoleDefaults |> List.find (fun role -> role.Purpose = "documentation") |> _.Role)

        do! expectRejected "same-id overlay cannot rebind role default" "profile role default 'implementation' overlay changes its role" (resolveWith "b-role-rebind" [ "overlay.json", roleDefaultOverlayJson SoftwareProfileId "implementation" "reviewer" ])

        // --- Missing profile blocks mutation; get stays lenient ---
        let rootD = root "missing-profile"

        writeProfiles
            rootD
            [ "widget.json", profileJson "widget" (Some "Widget") [] [ guardJson "review" "review" 1 ] ]

        let! missingTask = expectOk "create missing-profile task" (createTaskWithProfile rootD (Some "widget") (createRequest "TST-904" "Widget"))

        File.Delete(profilePath rootD "widget.json")

        let! readWhileMissing = expectOk "get remains lenient without profile" (getTask rootD "TST-904")
        assertEqual "lenient read keeps profile" "widget" readWhileMissing.Profile
        assertEqual "lenient read keeps fingerprint" missingTask.ProfileFingerprint readWhileMissing.ProfileFingerprint

        do! expectRejected "missing profile blocks mutation" "PROFILE_DRIFT: profile 'widget' is not available" (applyTask rootD "TST-904" readWhileMissing.StateRevision (AddEvidence(makeEvidence "E1" EvidenceKind.Test "late")))

        // get remains lenient while validate reports the missing profile explicitly.
        do! expectRejected "validate reports missing profile" "PROFILE_DRIFT: profile 'widget' is not available" (validateTask rootD "TST-904")

        // --- Draft drift: absorb current profile by replacing profile-materialized guards ---
        let rootE1 = root "draft-drift"

        writeProfiles
            rootE1
            [ "widget.json", profileJson "widget" (Some "Widget") [] [ guardJson "review" "review" 1 ] ]

        let! draftTask = expectOk "create draft-drift task" (createTaskWithProfile rootE1 (Some "widget") (createRequest "TST-905" "Widget"))

        assertEqual "draft task materializes one guard" 1 draftTask.Guards.Length

        File.WriteAllText(
            profilePath rootE1 "widget.json",
            profileJson "widget" (Some "Widget") [] [ guardJson "audit" "test" 2 ]
        )

        let! e1Profiles = expectOk "resolve changed draft profile" (resolveProfiles rootE1)
        let e1Fingerprint = e1Profiles.["widget"].Fingerprint
        assertTrue "profile change produces drift" (e1Fingerprint <> draftTask.ProfileFingerprint)

        let! absorbed = expectOk "draft absorbs profile drift" (applyTask rootE1 "TST-905" 0 (AddEvidence(makeEvidence "E1" EvidenceKind.Test "absorbed")))

        assertEqual "draft drift absorbed fingerprint" e1Fingerprint absorbed.ProfileFingerprint
        assertEqual "draft drift stays draft" Draft absorbed.ContractState
        assertEqual "draft drift replaces profile guards" 1 absorbed.Guards.Length
        assertEqual "draft drift guard origin" ProfileMaterialized absorbed.Guards.Head.Origin

        assertEqual
            "draft drift materializes replacement guard"
            (EvidenceRequired
                { Kind = EvidenceKind.Test
                  MinimumCount = 2
                  ProducerRole = None
                  RequireIndependentProducer = false })
            absorbed.Guards.Head.Requirement

        // --- Baselined drift: block mutation, reconcile monotonically, then proceed ---
        let rootE2 = root "baselined-drift"

        writeProfiles
            rootE2
            [ "widget.json", profileJson "widget" (Some "Widget") [] [ guardJson "review" "review" 1 ] ]

        let! baselineTask = expectOk "create baselined-drift task" (createTaskWithProfile rootE2 (Some "widget") (createRequest "TST-906" "Widget"))

        let! baselined = expectOk "baseline drift task" (applyTask rootE2 "TST-906" 0 (StartWorkItem "W1"))
        assertEqual "drift task baselined" Baselined baselined.ContractState
        assertEqual "baseline keeps recorded fingerprint" baselineTask.ProfileFingerprint baselined.ProfileFingerprint

        File.WriteAllText(
            profilePath rootE2 "widget.json",
            profileJson "widget" (Some "Widget") [] [ guardJson "audit" "test" 2 ]
        )

        let! e2Profiles = expectOk "resolve changed baselined profile" (resolveProfiles rootE2)
        let e2Fingerprint = e2Profiles.["widget"].Fingerprint

        let! driftRead = expectOk "get remains lenient under drift" (getTask rootE2 "TST-906")
        assertEqual "drift read keeps recorded fingerprint" baselined.ProfileFingerprint driftRead.ProfileFingerprint

        do! expectRejected "baselined drift blocks mutation" "PROFILE_DRIFT: profile 'widget' no longer matches the recorded fingerprint" (applyTask rootE2 "TST-906" driftRead.StateRevision (AddEvidence(makeEvidence "E1" EvidenceKind.Test "blocked")))

        // get remains lenient while validate reports the fingerprint drift explicitly.
        do! expectRejected "validate reports profile fingerprint drift" "PROFILE_DRIFT: profile 'widget' no longer matches the recorded fingerprint" (validateTask rootE2 "TST-906")

        let! reconciled = expectOk "reconcile profile drift" (applyTask rootE2 "TST-906" driftRead.StateRevision ReconcileProfileDrift)

        assertEqual "reconcile updates fingerprint" e2Fingerprint reconciled.ProfileFingerprint
        assertEqual "reconcile is monotonic" 2 reconciled.Guards.Length

        assertTrue
            "reconcile retains prior profile guard"
            (hasRequirement EvidenceKind.Review 1 reconciled.Guards)

        assertTrue
            "reconcile adds current profile guard"
            (hasRequirement EvidenceKind.Test 2 reconciled.Guards)

        let! afterReconcile = expectOk "mutation proceeds after reconcile" (applyTask rootE2 "TST-906" reconciled.StateRevision (AddEvidence(makeEvidence "E1" EvidenceKind.Test "now allowed")))

        assertEqual "post-reconcile evidence recorded" 1 afterReconcile.Evidence.Length

        // --- Baselined weakening reclassification fails closed; non-weakening succeeds ---
        let rootF = root "reclassification"

        writeProfiles
            rootF
            [ "01-base.json", profileJson "base" (Some "Base") [ "cap-core" ] []
              "02-plus.json", profileJson "base-plus" (Some "Base plus") [ "cap-core"; "cap-extra" ] []
              "03-lean.json", profileJson "lean" (Some "Lean") [] [] ]

        let! fProfiles = expectOk "resolve reclassification profiles" (resolveProfiles rootF)

        let! reclassTask = expectOk "create reclassification task" (createTaskWithProfile rootF (Some "base") (createRequest "TST-907" "Reclass"))

        let! reclassBaselined = expectOk "baseline reclassification task" (applyTask rootF "TST-907" 0 (StartWorkItem "W1"))
        assertEqual "reclassification task baselined" Baselined reclassBaselined.ContractState

        do! expectRejected "baselined weakening reclassification fails closed" "operation requires user authority; ordinary Coordinator invocation cannot authorize it" (applyTask rootF "TST-907" reclassBaselined.StateRevision (ReclassifyTask { Kind = None; Profile = Some "lean"; Reason = "drop required capability" }))

        do! expectRejected "baselined kind reclassification fails closed" "operation requires user authority; ordinary Coordinator invocation cannot authorize it" (applyTask rootF "TST-907" reclassBaselined.StateRevision (ReclassifyTask { Kind = Some Research; Profile = None; Reason = "change kind" }))

        let! unchanged = expectOk "get after rejected reclassification" (getTask rootF "TST-907")
        assertEqual "rejected reclassification does not mutate profile" "base" unchanged.Profile
        assertEqual "rejected reclassification does not mutate revision" reclassBaselined.StateRevision unchanged.StateRevision

        let! strengthened = expectOk "non-weakening reclassification succeeds" (applyTask rootF "TST-907" reclassBaselined.StateRevision (ReclassifyTask { Kind = None; Profile = Some "base-plus"; Reason = "add capability" }))

        assertEqual "reclassified profile" "base-plus" strengthened.Profile
        assertEqual "reclassified fingerprint" fProfiles.["base-plus"].Fingerprint strengthened.ProfileFingerprint

        // --- Keyed profile Guard identity: two same-content keys stay distinct and
        //     never exchange identity or disposition across a reorder reclassification ---
        let rootJ = root "keyed-guard-identity"

        let keyedGuardJson key =
            guardJsonWith key "review" 1 "explicitDecision:coordinator" "notWaivable"

        writeProfiles
            rootJ
            [ "01-keyed.json", profileJson "keyed" (Some "Keyed") [] [ keyedGuardJson "alpha"; keyedGuardJson "beta" ]
              "02-keyed-reordered.json",
              profileJson "keyed-reordered" (Some "Keyed reordered") [] [ keyedGuardJson "beta"; keyedGuardJson "alpha" ] ]

        let! keyedTask = expectOk "create keyed guard task" (createTaskWithProfile rootJ (Some "keyed") (createRequest "TST-923" "Keyed"))

        assertEqual "keyed task materializes two guards" 2 keyedTask.Guards.Length

        let guardForKey key (task: TaskModel) =
            let guardId =
                task.ProfileGuardKeys
                |> Map.tryFindKey (fun _ value -> value = key)
                |> Option.defaultWith (fun () -> failwithf "no guard keyed '%s'" key)

            task.Guards |> List.find (fun guard -> guard.Id = guardId)

        let alphaGuardId = (guardForKey "alpha" keyedTask).Id
        let betaGuardId = (guardForKey "beta" keyedTask).Id
        assertTrue "same-content keys materialize distinct guards" (alphaGuardId <> betaGuardId)

        let! disposedKeyed = expectOk "dispose keyed alpha guard" (applyTask rootJ "TST-923" keyedTask.StateRevision (MarkGuardNotApplicable(alphaGuardId, None)))

        assertEqual "alpha disposition recorded" (GuardDisposition.NotApplicable "D1") (guardForKey "alpha" disposedKeyed).Disposition
        assertEqual "beta stays applicable" GuardDisposition.Applicable (guardForKey "beta" disposedKeyed).Disposition

        let! reorderedKeyed = expectOk "reclassify keyed guards reordered" (applyTask rootJ "TST-923" disposedKeyed.StateRevision (ReclassifyTask { Kind = None; Profile = Some "keyed-reordered"; Reason = "reorder same-content guards" }))

        assertEqual "reorder keeps alpha guard identity" alphaGuardId (guardForKey "alpha" reorderedKeyed).Id
        assertEqual "reorder keeps alpha disposition" (GuardDisposition.NotApplicable "D1") (guardForKey "alpha" reorderedKeyed).Disposition
        assertEqual "reorder keeps beta guard identity" betaGuardId (guardForKey "beta" reorderedKeyed).Id
        assertEqual "reorder keeps beta disposition" GuardDisposition.Applicable (guardForKey "beta" reorderedKeyed).Disposition

        // --- Built-in software profile: Execution gates, Research exclusion, Draft sync ---
        let rootG = root "software-profile"

        let! softwareProfiles = expectOk "resolve built-in software profile" (resolveProfiles rootG)
        let software = softwareProfiles.[SoftwareProfileId]
        assertEqual "software origin" BuiltIn software.Origin
        assertEqual "software definition guard count" 3 software.Definition.Policy.Guards.Length

        let! softwareExec = expectOk "create software execution task" (createTaskWithProfile rootG (Some SoftwareProfileId) (createRequest "TST-910" "Software execution"))

        assertEqual "software execution profile" SoftwareProfileId softwareExec.Profile
        assertEqual "software execution fingerprint" software.Fingerprint softwareExec.ProfileFingerprint
        assertEqual "software execution materializes three gates" 3 softwareExec.Guards.Length

        assertTrue
            "software execution gates are materialized task-completion obligations"
            (softwareExec.Guards
             |> List.forall (fun guard ->
                 guard.Origin = ProfileMaterialized
                 && guard.Disposition = GuardDisposition.Applicable
                 && guard.Target = TaskTarget
                 && guard.Checkpoint = BeforeComplete))

        let buildGuard = guardOfKind EvidenceKind.Build softwareExec.Guards
        let testGuard = guardOfKind EvidenceKind.Test softwareExec.Guards
        let reviewGuard = guardOfKind EvidenceKind.Review softwareExec.Guards

        assertEqual "build gate producer role" (Some "engineer") (requirementOf buildGuard).ProducerRole
        assertEqual "test gate producer role" (Some "tester") (requirementOf testGuard).ProducerRole
        assertEqual "review gate producer role" (Some "reviewer") (requirementOf reviewGuard).ProducerRole

        assertEqual
            "build gate applicability"
            (ExplicitDecision MinimumAuthority.CoordinatorAuthority)
            buildGuard.Applicability

        assertEqual
            "test gate applicability"
            (ExplicitDecision MinimumAuthority.CoordinatorAuthority)
            testGuard.Applicability

        assertEqual
            "review gate applicability"
            (ExplicitDecision MinimumAuthority.UserAuthority)
            reviewGuard.Applicability

        assertTrue
            "software gates are user-waivable"
            (softwareExec.Guards |> List.forall (fun guard -> guard.Waiver = WaivableBy MinimumAuthority.UserAuthority))

        let! softwareResearch = expectOk "create software research task" (createTaskWithProfile rootG (Some SoftwareProfileId) { createRequest "TST-911" "Software research" with Kind = Research })

        assertEqual "software research profile" SoftwareProfileId softwareResearch.Profile
        assertTrue "software research materializes no gates" softwareResearch.Guards.IsEmpty

        let! reclassDraft = expectOk "create general draft for reclassification" (createTaskWithProfile rootG None (createRequest "TST-912" "Draft reclass"))

        assertEqual "draft reclass starts general" GeneralProfileId reclassDraft.Profile
        assertTrue "general draft has no gates" reclassDraft.Guards.IsEmpty

        let! adopted = expectOk "draft reclassifies to software execution" (applyTask rootG "TST-912" 0 (ReclassifyTask { Kind = None; Profile = Some SoftwareProfileId; Reason = "adopt software gates" }))

        assertEqual "draft reclass profile" SoftwareProfileId adopted.Profile
        assertEqual "draft reclass fingerprint" software.Fingerprint adopted.ProfileFingerprint
        assertEqual "draft reclass syncs software gates" 3 adopted.Guards.Length
        assertTrue "draft reclass gates are profile-materialized" (adopted.Guards |> List.forall (fun guard -> guard.Origin = ProfileMaterialized))

        let! researchOnly = expectOk "draft reclassifies software execution to research" (applyTask rootG "TST-912" adopted.StateRevision (ReclassifyTask { Kind = Some Research; Profile = None; Reason = "research only" }))

        assertEqual "draft reclass research kind" Research researchOnly.Kind
        assertEqual "draft reclass drops execution gates" 0 researchOnly.Guards.Length

        // Materialized software execution gates are enforced end-to-end, not merely
        // declared. Task completion stays blocked until role-scoped Build/Test/Review
        // Evidence exists, and wrong-role Review Evidence does not satisfy the review
        // gate.
        let! softwareExecStarted = expectOk "start software execution work" (applyTask rootG "TST-910" 0 (StartWorkItem "W1"))

        assertEqual "software execution baselines on first start" Baselined softwareExecStarted.ContractState

        let! softwareExecWorkDone = expectOk "complete software execution work" (applyTask rootG "TST-910" softwareExecStarted.StateRevision (CompleteWorkItem("W1", { Result = "implemented"; EvidenceRefs = [] })))

        let roleEvidence id kind role summary =
            { makeEvidence id kind summary with
                ProducerRole = Some role }

        let! softwareExecWithBuild = expectOk "add software build evidence" (applyTask rootG "TST-910" softwareExecWorkDone.StateRevision (AddEvidence(roleEvidence "E1" EvidenceKind.Build "engineer" "build passed")))

        let! softwareExecWithTest = expectOk "add software test evidence" (applyTask rootG "TST-910" softwareExecWithBuild.StateRevision (AddEvidence(roleEvidence "E2" EvidenceKind.Test "tester" "tests passed")))

        // A Review record produced by the wrong role cannot satisfy the review gate.
        let! softwareExecWithWrongReview = expectOk "add wrong-role review evidence" (applyTask rootG "TST-910" softwareExecWithTest.StateRevision (AddEvidence(roleEvidence "E3" EvidenceKind.Review "engineer" "self review")))

        let! softwareExecVerified = expectOk "verify software acceptance" (applyTask rootG "TST-910" softwareExecWithWrongReview.StateRevision (VerifyAcceptanceCriterion("AC1", [ "E1" ])))

        assertTrue "software execution cannot complete without reviewer evidence" (not (canCompleteTask softwareExecVerified))

        do! expectRejected "software completion blocked by unsatisfied review gate" $"guard '{reviewGuard.Id}' is not satisfied" (applyTask rootG "TST-910" softwareExecVerified.StateRevision (CompleteTask defaultHandoff))

        let! softwareExecReady = expectOk "add reviewer evidence" (applyTask rootG "TST-910" softwareExecVerified.StateRevision (AddEvidence(roleEvidence "E4" EvidenceKind.Review "reviewer" "independent review passed")))

        assertTrue "software execution completes once all gates are satisfied" (canCompleteTask softwareExecReady)

        let! softwareExecCompleted = expectOk "complete software execution task" (applyTask rootG "TST-910" softwareExecReady.StateRevision (CompleteTask defaultHandoff))

        assertEqual "software execution lifecycle complete" "complete" softwareExecCompleted.Lifecycle

        // --- Built-in harness profile: registry, policy-only capabilities, per-Kind
        //     guard materialization/isolation, fail-closed review, non-waivable
        //     research investigation, and monotonic same-ID overlay ---
        let rootH = root "harness-profile"

        let! harnessProfiles = expectOk "resolve built-in harness profile" (resolveProfiles rootH)
        let harness = harnessProfiles.[HarnessProfileId]
        assertEqual "harness origin" BuiltIn harness.Origin

        // Legacy identifiers have no registry alias, so resolution never consults the former consumer directory.
        do! expectRejected "legacy profile id is not registered" "unknown profile 'opencode'" (createTaskWithProfile rootH (Some "opencode") (createRequest "TST-924" "Retired identity"))

        let legacyRoot = root "legacy-opencode-task"
        let! legacyDraft = expectOk "create legacy source profile draft" (createTask legacyRoot (createRequest "TST-925" "Legacy"))
        let rewriteProfileIdentity (profile: string) (json: string) =
            let node = System.Text.Json.Nodes.JsonNode.Parse(json).AsObject()
            node["profile"] <- System.Text.Json.Nodes.JsonValue.Create(profile)
            node.ToJsonString()
        let legacyJson = rewriteProfileIdentity "opencode" (serialize legacyDraft)
        let legacySidecar = Path.Combine(legacyRoot, ".tasks", "TST-925", SidecarFileName)
        File.WriteAllText(legacySidecar, legacyJson)
        let! legacyRead = expectOk "legacy identity remains inspectable" (getTask legacyRoot "TST-925")
        assertEqual "legacy profile identity remains stored" "opencode" legacyRead.Profile
        assertEqual "legacy profile fingerprint remains stored" legacyDraft.ProfileFingerprint legacyRead.ProfileFingerprint
        do! expectRejected "legacy draft mutation fails closed" "PROFILE_DRIFT: profile 'opencode' is not available" (applyTask legacyRoot "TST-925" legacyRead.StateRevision (AddEvidence(makeEvidence "E1" EvidenceKind.Test "blocked")))
        let! reclassifiedLegacyDraft = expectOk "Draft reclassification is explicit and Coordinator-permitted" (applyTask legacyRoot "TST-925" legacyRead.StateRevision (ReclassifyTask { Kind = None; Profile = Some HarnessProfileId; Reason = "operator compared legacy obligations" }))
        assertEqual "explicit Draft reclassification adopts harness" HarnessProfileId reclassifiedLegacyDraft.Profile

        let! baselinedSource = expectOk "create baselined legacy source" (createTask legacyRoot (createRequest "TST-926" "Legacy baseline"))
        let! baselineSource = expectOk "baseline legacy source" (applyTask legacyRoot "TST-926" baselinedSource.StateRevision (StartWorkItem "W1"))
        let baselineJson = rewriteProfileIdentity "opencode" (serialize baselineSource)
        let baselineSidecar = Path.Combine(legacyRoot, ".tasks", "TST-926", SidecarFileName)
        File.WriteAllText(baselineSidecar, baselineJson)
        do! expectRejected "legacy baselined mutation fails closed" "PROFILE_DRIFT: profile 'opencode' is not available" (applyTask legacyRoot "TST-926" baselineSource.StateRevision (AddEvidence(makeEvidence "E1" EvidenceKind.Test "blocked")))
        do! expectRejected "legacy baselined reclassification requires unavailable user authority" "operation requires user authority; ordinary Coordinator invocation cannot authorize it" (applyTask legacyRoot "TST-926" baselineSource.StateRevision (ReclassifyTask { Kind = None; Profile = Some HarnessProfileId; Reason = "source profile unavailable" }))
        let! stillLegacy = expectOk "rejected legacy reclassification preserves identity" (getTask legacyRoot "TST-926")
        assertEqual "baselined legacy identity remains stored" "opencode" stillLegacy.Profile
        assertEqual "baselined legacy fingerprint remains stored" baselineSource.ProfileFingerprint stillLegacy.ProfileFingerprint

        assertTrue
            "harness fingerprint is content-derived, distinct from general"
            (harness.Fingerprint <> generalFingerprint && harness.Fingerprint <> "general-v1")

        let! harnessAgain = expectOk "resolve built-in harness profile again" (resolveProfiles rootH)

        assertEqual
            "harness resolution deterministic"
            (renderProfile harness)
            (renderProfile harnessAgain.[HarnessProfileId])

        // Capabilities are a declarative envelope: the allowed set is selectable
        // policy, never an automatic capability or permission grant.
        let harnessEnvelope = harness.Definition.CapabilityEnvelope
        assertEqual "harness required capabilities" [] harnessEnvelope.Required
        assertEqual "harness default capabilities" [] harnessEnvelope.Default
        assertEqual "harness allowed capabilities" [ "audit"; "dotnet"; "security"; "devops" ] harnessEnvelope.Allowed
        assertEqual "harness allowed capabilities do not auto-activate" [] (effectiveCapabilities harness [])
        assertEqual "harness declared capability is selectable" [ "dotnet"; "security" ] (effectiveCapabilities harness [ "dotnet"; "security" ])
        assertEqual "harness undeclared capability is not granted" [] (effectiveCapabilities harness [ "performance" ])

        // Execution materializes only the validation/review obligations.
        let! harnessExec = expectOk "create harness execution task" (createTaskWithProfile rootH (Some HarnessProfileId) (createRequest "TST-920" "Harness execution"))

        assertEqual "harness execution profile" HarnessProfileId harnessExec.Profile
        assertEqual "harness execution fingerprint" harness.Fingerprint harnessExec.ProfileFingerprint
        assertEqual "harness execution materializes two guards" 2 harnessExec.Guards.Length

        assertTrue
            "harness execution guards are materialized task-completion obligations"
            (harnessExec.Guards
             |> List.forall (fun guard ->
                 guard.Origin = ProfileMaterialized
                 && guard.Disposition = GuardDisposition.Applicable
                 && guard.Target = TaskTarget
                 && guard.Checkpoint = BeforeComplete))

        let harnessValidation = guardOfKind EvidenceKind.Test harnessExec.Guards
        let harnessReview = guardOfKind EvidenceKind.Review harnessExec.Guards

        assertEqual "harness validation producer role" (Some "tester") ((requirementOf harnessValidation).ProducerRole)
        assertEqual "harness review producer role" (Some "reviewer") ((requirementOf harnessReview).ProducerRole)

        assertEqual
            "harness validation applicability"
            (ExplicitDecision MinimumAuthority.CoordinatorAuthority)
            harnessValidation.Applicability

        assertEqual
            "harness review applicability"
            (ExplicitDecision MinimumAuthority.UserAuthority)
            harnessReview.Applicability

        assertTrue
            "harness execution gates are user-waivable"
            (harnessExec.Guards
             |> List.forall (fun guard -> guard.Waiver = WaivableBy MinimumAuthority.UserAuthority))

        assertTrue
            "harness execution does not materialize the research investigation gate"
            (harnessExec.Guards |> List.forall (fun guard -> (requirementOf guard).Kind <> EvidenceKind.Research))

        // Review is fail-closed under ordinary Coordinator invocation: neither the
        // direct path nor a Coordinator-authored Decision can dispose it.
        do! expectRejected "harness review not-applicable fails closed under coordinator" "operation requires user authority; ordinary Coordinator invocation cannot authorize it" (applyTask rootH "TST-920" harnessExec.StateRevision (MarkGuardNotApplicable(harnessReview.Id, None)))

        do! expectRejected "harness review waiver fails closed under coordinator" "operation requires user authority; ordinary Coordinator invocation cannot authorize it" (applyTask rootH "TST-920" harnessExec.StateRevision (WaiveGuard(harnessReview.Id, None)))

        let! withCoordinatorDecision = expectOk "add coordinator decision for review guard" (applyTask rootH "TST-920" harnessExec.StateRevision (AddDecision { Kind = ApplicabilityDecision; Targets = [ GuardDispositionTarget harnessReview.Id ]; Rationale = "coordinator attempts to dispose a user-required guard" }))

        let coordinatorDecision =
            withCoordinatorDecision.Decisions
            |> List.find (fun decision -> decision.Targets |> List.contains (GuardDispositionTarget harnessReview.Id))

        assertEqual "coordinator decision authority" Coordinator coordinatorDecision.Authority

        do! expectRejected "coordinator-authored decision cannot dispose user-required review guard" "does not authorize the exact operation" (applyTask rootH "TST-920" withCoordinatorDecision.StateRevision (MarkGuardNotApplicable(harnessReview.Id, Some(DecisionRef coordinatorDecision.Id))))

        // Research materializes only the non-waivable investigation obligation.
        let! harnessResearch = expectOk "create harness research task" (createTaskWithProfile rootH (Some HarnessProfileId) { createRequest "TST-921" "Harness research" with Kind = Research })

        assertEqual "harness research profile" HarnessProfileId harnessResearch.Profile
        assertEqual "harness research materializes one guard" 1 harnessResearch.Guards.Length

        let investigation = harnessResearch.Guards.Head
        assertEqual "harness investigation origin" ProfileMaterialized investigation.Origin
        assertEqual "harness investigation kind" EvidenceKind.Research ((requirementOf investigation).Kind)
        assertEqual "harness investigation minimum count" 1 ((requirementOf investigation).MinimumCount)
        assertEqual "harness investigation producer role" None ((requirementOf investigation).ProducerRole)
        assertEqual "harness investigation applicability" Always investigation.Applicability
        assertEqual "harness investigation waiver" NotWaivable investigation.Waiver

        assertTrue
            "harness research does not materialize execution validation/review gates"
            (harnessResearch.Guards
             |> List.forall (fun guard ->
                 let kind = (requirementOf guard).Kind
                 kind <> EvidenceKind.Test && kind <> EvidenceKind.Review))

        do! expectRejected "harness investigation cannot be waived" "is not waivable" (applyTask rootH "TST-921" harnessResearch.StateRevision (WaiveGuard(investigation.Id, None)))

        do! expectRejected "harness investigation is always applicable" "is always applicable and cannot be marked NotApplicable" (applyTask rootH "TST-921" harnessResearch.StateRevision (MarkGuardNotApplicable(investigation.Id, None)))

        // Investigation is satisfiable by task-level Research evidence and blocks
        // completion until that evidence exists.
        let! researchStarted = expectOk "start harness research work" (applyTask rootH "TST-921" harnessResearch.StateRevision (StartWorkItem "W1"))

        let! researchWorkDone = expectOk "complete opencode research work" (applyTask rootH "TST-921" researchStarted.StateRevision (CompleteWorkItem("W1", { Result = "harness investigated"; EvidenceRefs = [] })))

        let! researchSeedEvidence = expectOk "add non-research evidence for acceptance" (applyTask rootH "TST-921" researchWorkDone.StateRevision (AddEvidence(makeEvidence "E1" EvidenceKind.Test "non-research evidence")))

        let! researchVerified = expectOk "verify harness research acceptance" (applyTask rootH "TST-921" researchSeedEvidence.StateRevision (VerifyAcceptanceCriterion("AC1", [ "E1" ])))

        do! expectRejected "harness research completion blocked until investigation evidence exists" "is not satisfied" (applyTask rootH "TST-921" researchVerified.StateRevision (CompleteTask defaultHandoff))

        let! researchEvidenceAdded = expectOk "add harness research investigation evidence" (applyTask rootH "TST-921" researchVerified.StateRevision (AddEvidence(makeEvidence "E2" EvidenceKind.Research "harness investigation recorded")))

        let! researchCompleted = expectOk "complete harness research task" (applyTask rootH "TST-921" researchEvidenceAdded.StateRevision (CompleteTask defaultHandoff))

        assertEqual "opencode research completed lifecycle" "complete" researchCompleted.Lifecycle

        // Same-ID overlay over the shared harness profile merges monotonically:
        // same-key guards strengthen without loss of the research investigation policy.
        let rootI = root "harness-overlay"

        writeProfiles rootI [ "harness-overlay.json", profileJson HarnessProfileId None [] [ guardJson "validation" "test" 2 ] ]

        let! overlaidProfiles = expectOk "resolve harness overlay" (resolveProfiles rootI)
        let overlaidHarness = overlaidProfiles.[HarnessProfileId]
        assertEqual "harness overlay origin" Overlay overlaidHarness.Origin
        assertEqual "harness overlay retains three guards" 3 overlaidHarness.Definition.Policy.Guards.Length

        let overlaidValidation =
            overlaidHarness.Definition.Policy.Guards |> List.find (fun guard -> guard.Key = "validation")

        assertEqual "harness overlay strengthens validation minimum count" 2 overlaidValidation.Requirement.MinimumCount

        let overlaidInvestigation =
            overlaidHarness.Definition.Policy.Guards |> List.find (fun guard -> guard.Key = "investigation")

        assertEqual "harness overlay retains investigation applicability" Always overlaidInvestigation.Applicability
        assertEqual "harness overlay retains investigation waiver" NotWaivable overlaidInvestigation.Waiver

        let! overlaidExec = expectOk "create overlaid harness execution task" (createTaskWithProfile rootI (Some HarnessProfileId) (createRequest "TST-922" "Overlaid harness"))

        assertEqual "harness overlay execution materializes two guards" 2 overlaidExec.Guards.Length

        assertEqual
            "harness overlay materializes strengthened validation"
            2
            ((requirementOf (guardOfKind EvidenceKind.Test overlaidExec.Guards)).MinimumCount)

        do! expectRejected "harness overlay cannot weaken investigation applicability" "overlay weakens its applicability" (resolveWith "b-harness-weaken-applicability" [ "overlay.json", profileJson HarnessProfileId None [] [ guardJsonWith "investigation" "research" 1 "explicitDecision:coordinator" "notWaivable" ] ])

        do! expectRejected "harness overlay cannot weaken investigation waiver" "overlay weakens its waiver policy" (resolveWith "b-harness-weaken-waiver" [ "overlay.json", profileJson HarnessProfileId None [] [ guardJsonWith "investigation" "research" 1 "always" "waivableBy:coordinator" ] ])

        printfn
            "OK profile resolver: harness registry without OpenCode files or identity, explicit Draft reclassification vs fail-closed Baselined legacy profile, no automatic migration, strict profiles, monotonic overlays/guards, drift lifecycle, and built-in software and harness policies"
    finally
     if Directory.Exists parentRoot && parentRoot.Contains("taskprofile-tests-", StringComparison.Ordinal) then
         Directory.Delete(parentRoot, true)
}
|> Async.RunSynchronously
