// Deterministic coverage for the frozen INFRA-019 D1 strict schema-v3 cutover.
// It proves the six repository-owned active runtime records were migrated to the
// sole canonical schema (v3 with the complete field set and content-derived
// profile fingerprint), that the retired legacy fingerprint no longer appears,
// and that audit reusable knowledge loads proportionately (one required unit
// eagerly, the remaining units coordinator-gated through applicability).
// Plain FSI harness because the solution contract forbids adding a
// project/package system to the core.

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

// The migrated records belong to the OpenCode consumer repository, which is a
// sibling of mcp-store under the shared private workspace. Resolve that fixture
// root from this file rather than depending on the process working directory.
let root =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "opencode"))

let readText (relative: string) =
    File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))

let migratedIds = [ "INFRA-015"; "INFRA-016"; "INFRA-017"; "INFRA-018"; "INFRA-019"; "INFRA-020" ]

try
    // --- Six migrated records are canonical schema v3 and round-trip ---------
    let migratedFingerprints = ResizeArray<string>()

    for id in migratedIds do
        let path = Path.Combine(root, ".tasks", id, "runtime.json")
        assertTrue $"{id} migrated sidecar exists" (File.Exists path)

        let text = File.ReadAllText path
        let raw = JsonNode.Parse(text).AsObject()

        assertEqual $"{id} migrated schemaVersion" 3 (raw.["schemaVersion"].GetValue<int>())
        assertTrue $"{id} retains no legacy general-v1 fingerprint" (not (text.Contains("\"general-v1\"", StringComparison.Ordinal)))

        // The complete canonical field set must be persisted, including the
        // collections that schema 2 omitted while empty.
        for field in [ "objective"; "scope"; "nonGoals"; "contractState"; "contractFingerprint";
                       "guards"; "profileGuardKeys"; "decisions"; "questions"; "completionHistory" ] do
            assertTrue $"{id} persists '{field}'" (raw.ContainsKey field)

        match deserialize text with
        | Ok task ->
            assertEqual $"{id} deserialized id" id task.Id
            assertEqual $"{id} state revision matches raw" (raw.["stateRevision"].GetValue<int>()) task.StateRevision
            assertEqual $"{id} profile fingerprint matches raw" (raw.["profileFingerprint"].GetValue<string>()) task.ProfileFingerprint
            migratedFingerprints.Add task.ProfileFingerprint
        | Error error -> failwithf "%s did not deserialize as schema v3: %s" id (renderError error)

    // Every migrated record is a refreshed canonical document, not a stale copy.
    assertEqual "six migrated records validated" 6 migratedFingerprints.Count

    // D1 removed the INFRA-013 orphan lock and did not recreate it.
    assertTrue
        "INFRA-013 orphan lock removed"
        (not (File.Exists(Path.Combine(root, ".tasks", "INFRA-013", "runtime.lock"))))

    assertTrue
        "historical TASK.md is never a runtime input"
        (not (File.Exists(Path.Combine(root, ".tasks", "INFRA-019", "TASK.md"))))

    // --- Audit knowledge loads proportionately (AC6) -------------------------
    // Only the always-relevant audit unit is eager; the other audit units are
    // declared as coordinator-selected applicability, never default-loaded.
    let composition = JsonNode.Parse(readText "composition.json").AsObject()
    let knowledge = composition.["routing"].["knowledge"].AsObject()
    let auditRequired =
        knowledge.["audit"].AsArray() |> Seq.map (fun node -> node.GetValue<string>()) |> List.ofSeq

    assertEqual
        "audit eager knowledge is the single instructions-and-evidence unit"
        [ "audit-instructions-and-evidence" ]
        auditRequired

    let applicability = composition.["routing"].["applicability"].AsObject()
    let gatedAuditUnits =
        [ "audit-routing-and-delegation"
          "audit-authority-and-security"
          "audit-composition-and-runtime"
          "audit-model-readiness" ]

    for unit in gatedAuditUnits do
        assertTrue $"{unit} is coordinator-gated applicability" (applicability.ContainsKey unit)

        let entry = applicability.[unit].AsObject()
        assertEqual $"{unit} applicability capability" "audit" (entry.["capability"].GetValue<string>())

        assertEqual
            $"{unit} applicability agent"
            [ "auditor" ]
            (entry.["agents"].AsArray() |> Seq.map (fun node -> node.GetValue<string>()) |> List.ofSeq)

        assertTrue $"{unit} is not eagerly loaded" (not (List.contains unit auditRequired))

    // The auditor prompt consumes only the caller-materialized set and never
    // self-authorizes the remaining units.
    let auditor = readText "agents/auditor.md"

    assertTrue
        "auditor consumes the caller-selected set"
        (auditor.Contains("caller-selected audit knowledge set", StringComparison.Ordinal))

    assertTrue
        "auditor states hints never authorize loading"
        (auditor.Contains("hints never authorize loading", StringComparison.Ordinal))

    assertTrue
        "auditor forbids self-authorization"
        (auditor.Contains("Do not invoke a resolver or otherwise self-authorize knowledge", StringComparison.Ordinal))

    for unit in gatedAuditUnits do
        assertTrue
            $"auditor does not unconditionally load {unit}"
            (not (auditor.Contains($"@capabilities/audit/knowledge/{unit}.md", StringComparison.Ordinal)))

    assertTrue
        "auditor retains the required instructions-and-evidence guidance"
        (auditor.Contains("@capabilities/audit/knowledge/instructions-and-evidence.md", StringComparison.Ordinal))

    let behavioral = readText "skills/audit/references/behavioral-evaluation.md"

    assertTrue
        "proportional audit loading has a behavioral scenario"
        (behavioral.Contains("S14. Proportional audit knowledge loading", StringComparison.Ordinal))

    assertTrue
        "behavioral scenario asserts hints never authorize"
        (behavioral.Contains("hints never authorize loading", StringComparison.Ordinal))

    printfn
        "OK schema-v3 cutover: six migrated records are canonical v3 and round-trip, retired legacy fingerprints are absent, INFRA-013 orphan lock stays removed, and audit knowledge loads proportionately with hints never authorizing"
finally
    ()
