// Deterministic coverage for the strict schema-v1 clean slate: only the
// canonical runtime sidecar is accepted, non-v1 documents are rejected, and
// coordination never creates a persisted runtime.lock.

#load "../ComputationExpressions.fs"
#load "../Workflow.fs"

open System
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
    | Ok _ -> failwithf "%s: expected rejection" name
    | Error error when (renderError error).Contains(fragment, StringComparison.Ordinal) -> ()
    | Error error -> failwithf "%s: expected '%s', got '%s'" name fragment (renderError error)

let request id title =
    { Id = id
      Title = title
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "The task is complete" ]
      WorkItems =
          [ { Id = "W1"
              Title = "Perform the work"
              DependsOn = []
              Children = [] } ] }

let tempRoot = Path.Combine(Path.GetTempPath(), "opencode", $"workflow-schema-v1-{Guid.NewGuid():N}")
Directory.CreateDirectory tempRoot |> ignore

let sidecar root id = Path.Combine(root, ".tasks", id, SidecarFileName)
let lockPath root id = Path.Combine(root, ".tasks", id, "runtime.lock")

try
    let created = expectOk "create canonical task" (createTask tempRoot (request "CUT-1" "Schema v1"))
    let persisted = JsonNode.Parse(File.ReadAllText(sidecar tempRoot "CUT-1")).AsObject()

    assertEqual "created schema version" 1 (persisted["schemaVersion"].GetValue<int>())
    assertEqual "serialized schema version" 1 ((JsonNode.Parse(serialize created)).["schemaVersion"].GetValue<int>())
    assertTrue
        "canonical sidecar round trips"
        (match serialize created |> deserialize with
         | Ok _ -> true
         | Error _ -> false)
    assertTrue "create leaves no persisted lock" (not (File.Exists(lockPath tempRoot "CUT-1")))

    let topLevel = persisted |> Seq.map (fun entry -> entry.Key) |> Set.ofSeq
    assertTrue "canonical sidecar has no legacy aliases" (not (Set.contains "version" topLevel))
    assertTrue "canonical sidecar has no co-located task document" (not (Set.contains "TASK" topLevel))

    let aliased = JsonNode.Parse(persisted.ToJsonString()).AsObject()
    aliased["version"] <- JsonValue.Create 1
    expectRejected "persisted version alias rejected" "unknown property 'version'" (deserialize (aliased.ToJsonString()))

    for version in [ 0; 2; 3 ] do
        let nonV1 = JsonNode.Parse(persisted.ToJsonString()).AsObject()
        nonV1["schemaVersion"] <- JsonValue.Create version
        expectRejected ($"schema version {version} rejected") ($"unsupported schemaVersion {version}") (deserialize (nonV1.ToJsonString()))

    let legacyDirectory = Path.Combine(tempRoot, ".tasks", "LEG-1")
    Directory.CreateDirectory legacyDirectory |> ignore
    File.WriteAllText(Path.Combine(legacyDirectory, "TASK.md"), "legacy task")
    expectRejected "co-located TASK.md rejected" "unsupported entry" (getTask tempRoot "LEG-1")

    let referencesDirectory = Path.Combine(tempRoot, ".tasks", "REF-1", "references")
    Directory.CreateDirectory referencesDirectory |> ignore
    File.WriteAllText(Path.Combine(referencesDirectory, "issue.md"), "legacy evidence")
    expectRejected "references layout rejected" "unsupported entry" (createTask tempRoot (request "REF-1" "No legacy layout"))

    let lockDirectory = Path.Combine(tempRoot, ".tasks", "LCK-1")
    Directory.CreateDirectory lockDirectory |> ignore
    File.WriteAllText(Path.Combine(lockDirectory, "runtime.lock"), "legacy lock")
    expectRejected "persisted lock layout rejected" "unsupported entry" (createTask tempRoot (request "LCK-1" "No persisted lock"))

    let applied = expectOk "apply canonical task" (applyTask tempRoot "CUT-1" 0 (StartWorkItem "W1"))
    assertEqual "apply persists schema v1" 1 ((JsonNode.Parse(File.ReadAllText(sidecar tempRoot "CUT-1"))).["schemaVersion"].GetValue<int>())
    assertTrue "apply leaves no persisted lock" (not (File.Exists(lockPath tempRoot "CUT-1")))
    expectOk "get canonical task" (getTask tempRoot "CUT-1") |> ignore
    expectOk "validate canonical task" (validateTask tempRoot "CUT-1") |> ignore
    assertTrue "read operations leave no persisted lock" (not (File.Exists(lockPath tempRoot "CUT-1")))
    assertEqual "apply state revision" 1 applied.StateRevision

    printfn "OK strict schema-v1 clean slate: canonical round trip, non-v1 rejection, legacy layout rejection, and ephemeral locking"
finally
    if Directory.Exists tempRoot && tempRoot.Contains("workflow-schema-v1-", StringComparison.Ordinal) then
        Directory.Delete(tempRoot, true)
