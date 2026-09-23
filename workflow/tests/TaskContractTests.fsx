// Focused deterministic coverage for the strict schema-v1 persistence boundary.
// Legacy readers, aliases, co-located layouts, and persisted locks are not part
// of the Workflow contract.

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
      AcceptanceCriteria = [ "AC1", "Execution completes" ]
      WorkItems =
          [ { Id = "W1"
              Title = "Do the work"
              DependsOn = []
              Children = [] } ] }

let tempRoot = Path.Combine(Path.GetTempPath(), "opencode", $"workflow-contract-v1-{Guid.NewGuid():N}")
Directory.CreateDirectory tempRoot |> ignore

let sidecar root id = Path.Combine(root, ".tasks", id, SidecarFileName)
let runtimeLock root id = Path.Combine(root, ".tasks", id, "runtime.lock")

try
    let task = expectOk "create task" (createTask tempRoot (request "CON-1" "Contract"))
    let json = serialize task
    let raw = JsonNode.Parse(json).AsObject()

    assertEqual "persisted schema" 1 (raw["schemaVersion"].GetValue<int>())
    assertEqual "serializer/parser parity" task (expectOk "deserialize serialized task" (deserialize json))
    assertTrue "serializer emits no schema alias" (not (raw.ContainsKey "version"))
    assertTrue "serializer emits no legacy task document" (not (File.Exists(Path.Combine(Path.GetDirectoryName(sidecar tempRoot "CON-1"), "TASK.md"))))

    for version in [ 2; 3 ] do
        let changed = JsonNode.Parse(raw.ToJsonString()).AsObject()
        changed["schemaVersion"] <- JsonValue.Create version
        expectRejected ($"schema {version} rejected") ($"unsupported schemaVersion {version}") (deserialize (changed.ToJsonString()))

    let missingSidecarDirectory = Path.Combine(tempRoot, ".tasks", "MIS-1")
    Directory.CreateDirectory missingSidecarDirectory |> ignore
    expectRejected "missing sidecar remains absent" "runtime sidecar does not exist" (getTask tempRoot "MIS-1")
    expectRejected "missing sidecar apply remains absent" "runtime sidecar does not exist" (applyTask tempRoot "MIS-1" 0 (StartWorkItem "W1"))
    assertTrue "missing sidecar creates no lock" (not (File.Exists(runtimeLock tempRoot "MIS-1")))

    let taskDocumentDirectory = Path.Combine(tempRoot, ".tasks", "DOC-1")
    Directory.CreateDirectory taskDocumentDirectory |> ignore
    File.WriteAllText(Path.Combine(taskDocumentDirectory, "TASK.md"), "legacy task")
    expectRejected "TASK.md layout rejected" "unsupported entry" (getTask tempRoot "DOC-1")

    let referencesDirectory = Path.Combine(tempRoot, ".tasks", "REF-1", "references")
    Directory.CreateDirectory referencesDirectory |> ignore
    File.WriteAllText(Path.Combine(referencesDirectory, "issue.md"), "legacy evidence")
    expectRejected "references layout rejected" "unsupported entry" (createTask tempRoot (request "REF-1" "References are not runtime state"))

    let lockDirectory = Path.Combine(tempRoot, ".tasks", "LCK-1")
    Directory.CreateDirectory lockDirectory |> ignore
    File.WriteAllText(Path.Combine(lockDirectory, "runtime.lock"), "legacy lock")
    expectRejected "persisted lock layout rejected" "unsupported entry" (createTask tempRoot (request "LCK-1" "Locks are ephemeral"))

    let updated = expectOk "apply task" (applyTask tempRoot "CON-1" 0 (StartWorkItem "W1"))
    assertEqual "updated state revision" 1 updated.StateRevision
    assertEqual "updated persisted schema" 1 ((JsonNode.Parse(File.ReadAllText(sidecar tempRoot "CON-1"))).["schemaVersion"].GetValue<int>())
    assertTrue "apply leaves no lock" (not (File.Exists(runtimeLock tempRoot "CON-1")))

    printfn "OK schema-v1 contract: strict version, serializer/parser parity, legacy-layout rejection, and ephemeral locking"
finally
    if Directory.Exists tempRoot && tempRoot.Contains("workflow-contract-v1-", StringComparison.Ordinal) then
        Directory.Delete(tempRoot, true)
