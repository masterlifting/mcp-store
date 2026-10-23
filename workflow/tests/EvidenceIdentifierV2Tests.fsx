// role: test entrypoint
// Focused regression coverage for domain-neutral evidence identifiers and the
// forward-only task/profile schema-v2 boundary.

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

try
    let created = createTask root (request "EVI-1") |> Async.RunSynchronously |> unwrap "create"
    let baselineFingerprint = created.ProfileFingerprint
    let arbitrary = evidenceKind "source-verification"
    let guard =
        { Id = "G1"; Target = TaskTarget; Checkpoint = BeforeStart; Origin = TaskDesign
          Requirement = EvidenceRequired { Kind = arbitrary; MinimumCount = 1; ProducerRole = None; RequireIndependentProducer = false }
          Applicability = Always; Waiver = NotWaivable; Disposition = GuardDisposition.Applicable }
    let addedGuard = applyTask root "EVI-1" 0 (AddGuard { Id = guard.Id; Target = guard.Target; Checkpoint = guard.Checkpoint; Requirement = guard.Requirement; Applicability = guard.Applicability; Waiver = guard.Waiver }) |> Async.RunSynchronously |> unwrap "add guard"
    let unrelated =
        { Id = "E1"; Kind = evidenceKind "test"; Source = EvidenceSource "test"; Subject = None
          ProducerRole = None; ProducerId = None; Reference = None; Summary = "source checked" }
    let withUnrelated = applyTask root "EVI-1" addedGuard.StateRevision (AddEvidence unrelated) |> Async.RunSynchronously |> unwrap "add unrelated evidence"
    let startBlocked = applyTask root "EVI-1" withUnrelated.StateRevision (StartWorkItem "W1") |> Async.RunSynchronously
    expectError "unrelated evidence does not satisfy before-start guard" startBlocked
    let evidence = { unrelated with Id = "E2"; Kind = arbitrary }
    let added = applyTask root "EVI-1" withUnrelated.StateRevision (AddEvidence evidence) |> Async.RunSynchronously |> unwrap "add matching evidence"
    let raw = JsonNode.Parse(File.ReadAllText(sidecar "EVI-1"))
    assertEqual "task schema v2" 2 (raw["schemaVersion"].GetValue<int>())
    assertEqual "opaque identifier persistence" "source-verification" ((raw["evidence"].[1]["kind"]).GetValue<string>())
    let roundTrip = deserialize (serialize added) |> unwrap "deserialize"
    assertTrue "guard projection round-trip" (roundTrip.Guards |> List.exists (fun value -> value.Id = guard.Id && value.Checkpoint = BeforeStart && value.Requirement = guard.Requirement))
    assertTrue "evidence projection round-trip" (roundTrip.Evidence |> List.exists (fun value -> value.Evidence.Id = "E2" && value.Evidence.Kind = arbitrary))
    assertEqual "profile fingerprint stable across task evidence" baselineFingerprint roundTrip.ProfileFingerprint
    let started = applyTask root "EVI-1" added.StateRevision (StartWorkItem "W1") |> Async.RunSynchronously |> unwrap "start with matching evidence"
    assertTrue "before-start evidence guard accepts exact kind" (started.WorkItems |> List.exists (fun item -> item.Id = "W1" && item.State = ActiveWork))

    let v1 = JsonNode.Parse(serialize added).AsObject()
    v1["schemaVersion"] <- JsonValue.Create 1
    let bytes = v1.ToJsonString()
    File.WriteAllText(sidecar "EVI-1", bytes)
    let before = File.ReadAllBytes(sidecar "EVI-1")
    expectError "schema-v1 task rejected" (deserialize bytes)
    assertTrue "v1 rejection does not rewrite stored sidecar" (Array.forall2 (=) before (File.ReadAllBytes(sidecar "EVI-1")))
    for read in [ getTask root "EVI-1"; validateTask root "EVI-1" ] do
        match read |> Async.RunSynchronously with
        | Error _ -> ()
        | Ok _ -> failwith "schema-v1 stored task was accepted"
    expectError "schema-v1 apply rejected" (applyTask root "EVI-1" started.StateRevision (AddEvidence { evidence with Id = "E3" }) |> Async.RunSynchronously)
    assertTrue "get/validate preserve schema-v1 bytes" (Array.forall2 (=) before (File.ReadAllBytes(sidecar "EVI-1")))
    assertTrue "apply preserves schema-v1 bytes" (Array.forall2 (=) before (File.ReadAllBytes(sidecar "EVI-1")))

    let occupied = Path.Combine(root, ".tasks", "EVI-2")
    Directory.CreateDirectory occupied |> ignore
    File.WriteAllText(Path.Combine(occupied, "keep.txt"), "preserve")
    match createTask root (request "EVI-2") |> Async.RunSynchronously with
    | Error _ -> ()
    | Ok _ -> failwith "occupied create unexpectedly succeeded"
    assertEqual "occupied create preserves unrelated file" "preserve" (File.ReadAllText(Path.Combine(occupied, "keep.txt")))

    printfn "OK evidence identifier/schema-v2 regressions"
finally
    if Directory.Exists root then Directory.Delete(root, true)
