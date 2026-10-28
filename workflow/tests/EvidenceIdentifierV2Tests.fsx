// role: test entrypoint

#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"

open System
open System.IO
open System.Text.Json.Nodes
open Workflow

let assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let expectError name result =
    match result with
    | Error _ -> ()
    | Ok _ -> failwithf "%s: expected invalid input" name

let unwrap name result =
    match result with
    | Ok value -> value
    | Error error -> failwithf "%s: %s" name (renderError error)

let evidenceKind value =
    match parseEvidenceKind value with
    | Ok parsed -> parsed
    | Error error -> failwithf "expected valid evidence kind %s: %s" value (renderError error)

for value in [ "a"; "z9"; "a-b"; "a-0"; "a1-b2"; String('a', 64) ] do
    evidenceKind value |> ignore

for value in [ null; ""; " "; "\n"; "A"; "1a"; "-a"; "a-"; "a--b"; "a_b"; "a.b"; "a:b"; "é"; String('a', 65); "build" ] do
    if value = "build" then evidenceKind value |> ignore else expectError $"invalid {value}" (parseEvidenceKind value)

let root = Path.Combine(Path.GetTempPath(), "opencode", $"workflow-evidence-v2-{Guid.NewGuid():N}")
Directory.CreateDirectory root |> ignore
let request id =
    { Id = id; Title = "Evidence contract"; Kind = Execution
      AcceptanceCriteria = [ "AC1", "Evidence round-trips" ]
      WorkItems = [ { Id = "W1"; Title = "Work"; DependsOn = []; Children = [] } ] }

let sidecar id = Path.Combine(root, ".tasks", id, SidecarFileName)

async {
    try
        let! createdResult = createTask root (request "EVI-1")
        let created = unwrap "create" createdResult
        let baselineFingerprint = created.ProfileFingerprint
        let arbitrary = evidenceKind "source-verification"
        let guard =
            { Id = "G1"; Target = TaskTarget; Checkpoint = BeforeStart; Origin = TaskDesign
              Requirement = EvidenceRequired { Kind = arbitrary; MinimumCount = 1; ProducerRole = None; RequireIndependentProducer = false }
              Applicability = Always; Waiver = NotWaivable; Disposition = GuardDisposition.Applicable }
        let! addedGuardResult = applyTask root "EVI-1" 0 (AddGuard { Id = guard.Id; Target = guard.Target; Checkpoint = guard.Checkpoint; Requirement = guard.Requirement; Applicability = guard.Applicability; Waiver = guard.Waiver })
        let addedGuard = unwrap "add guard" addedGuardResult
        let unrelated =
            { Id = "E1"; Kind = evidenceKind "test"; Source = EvidenceSource "test"; Subject = None
              ProducerRole = None; ProducerId = None; Reference = None; Summary = "source checked" }
        let! withUnrelatedResult = applyTask root "EVI-1" addedGuard.StateRevision (AddEvidence unrelated)
        let withUnrelated = unwrap "add unrelated evidence" withUnrelatedResult
        let! startBlocked = applyTask root "EVI-1" withUnrelated.StateRevision (StartWorkItem "W1")
        expectError "unrelated evidence does not satisfy before-start guard" startBlocked
        let evidence = { unrelated with Id = "E2"; Kind = arbitrary }
        let! addedResult = applyTask root "EVI-1" withUnrelated.StateRevision (AddEvidence evidence)
        let added = unwrap "add matching evidence" addedResult
        let raw = JsonNode.Parse(File.ReadAllText(sidecar "EVI-1"))
        assertEqual "task schema v2" 2 (raw["schemaVersion"].GetValue<int>())
        assertEqual "opaque identifier persistence" "source-verification" ((raw["evidence"].[1]["kind"]).GetValue<string>())
        let roundTrip = deserialize (serialize added) |> unwrap "deserialize"
        assertTrue "guard projection round-trip" (roundTrip.Guards |> List.exists (fun value -> value.Id = guard.Id && value.Checkpoint = BeforeStart && value.Requirement = guard.Requirement))
        assertTrue "evidence projection round-trip" (roundTrip.Evidence |> List.exists (fun value -> value.Evidence.Id = "E2" && value.Evidence.Kind = arbitrary))
        assertEqual "profile fingerprint stable across task evidence" baselineFingerprint roundTrip.ProfileFingerprint
        let! startedResult = applyTask root "EVI-1" added.StateRevision (StartWorkItem "W1")
        let started = unwrap "start with matching evidence" startedResult
        assertTrue "before-start evidence guard accepts exact kind" (started.WorkItems |> List.exists (fun item -> item.Id = "W1" && item.State = ActiveWork))

        let v1 = JsonNode.Parse(serialize added).AsObject()
        v1["schemaVersion"] <- JsonValue.Create 1
        let bytes = v1.ToJsonString()
        File.WriteAllText(sidecar "EVI-1", bytes)
        let before = File.ReadAllBytes(sidecar "EVI-1")
        expectError "schema-v1 task rejected" (deserialize bytes)
        assertTrue "v1 rejection does not rewrite stored sidecar" (Array.forall2 (=) before (File.ReadAllBytes(sidecar "EVI-1")))
        for read in [ getTask root "EVI-1"; validateTask root "EVI-1" ] do
            match! read with
            | Error _ -> ()
            | Ok _ -> return failwith "schema-v1 stored task was accepted"
        let! schemaV1Apply = applyTask root "EVI-1" started.StateRevision (AddEvidence { evidence with Id = "E3" })
        expectError "schema-v1 apply rejected" schemaV1Apply
        assertTrue "get/validate preserve schema-v1 bytes" (Array.forall2 (=) before (File.ReadAllBytes(sidecar "EVI-1")))
        assertTrue "apply preserves schema-v1 bytes" (Array.forall2 (=) before (File.ReadAllBytes(sidecar "EVI-1")))

        let occupied = Path.Combine(root, ".tasks", "EVI-2")
        Directory.CreateDirectory occupied |> ignore
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "preserve")
        let! occupiedCreate = createTask root (request "EVI-2")
        match occupiedCreate with
        | Error _ -> ()
        | Ok _ -> return failwith "occupied create unexpectedly succeeded"
        assertEqual "occupied create preserves unrelated file" "preserve" (File.ReadAllText(Path.Combine(occupied, "keep.txt")))

        printfn "OK evidence identifier/schema-v2 regressions"
    finally
        if Directory.Exists root then Directory.Delete(root, true)
}
// Standalone entry bridge: FSI needs one synchronous script entry.
|> Async.RunSynchronously
