// Focused deterministic coverage for the strict schema-v1 persistence boundary.
// No alternative readers, aliases, co-located layouts, or persisted locks are
// part of the Workflow contract.

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

let expectOk name (result: Async<Result<'a, RuntimeError>>) : Async<'a> =
    async {
        match! result with
        | Ok value -> return value
        | Error error -> return failwithf "%s: expected Ok, got Error %s" name (renderError error)
    }

let expectRejected (name: string) (fragment: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ -> return failwithf "%s: expected rejection" name
        | Error error when (renderError error).Contains(fragment, StringComparison.Ordinal) -> ()
        | Error error -> return failwithf "%s: expected '%s', got '%s'" name fragment (renderError error)
    }

// `deserialize` is sync; lift it into the async helpers via a thin wrapper.
let deserializeAsync json : Async<Result<TaskModel, RuntimeError>> = async { return deserialize json }

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

// Standalone entry bridge: the only synchronous wait in this script's flow;
// the entire suite composes asynchronously above.
async {
    try
        let! task = expectOk "create task" (createTask tempRoot (request "CON-1" "Contract"))
        let json = serialize task
        let raw = JsonNode.Parse(json).AsObject()

        assertEqual "persisted schema" 1 (raw["schemaVersion"].GetValue<int>())
        let! roundTrip = expectOk "deserialize serialized task" (deserializeAsync json)
        assertEqual "serializer/parser parity" task roundTrip
        assertTrue "serializer emits no schema alias" (not (raw.ContainsKey "version"))
        assertTrue "serializer emits no legacy task document" (not (File.Exists(Path.Combine(Path.GetDirectoryName(sidecar tempRoot "CON-1"), "TASK.md"))))

        for version in [ 0; 2; 3 ] do
            let changed = JsonNode.Parse(raw.ToJsonString()).AsObject()
            changed["schemaVersion"] <- JsonValue.Create version
            do!
                expectRejected
                    ($"schema {version} rejected")
                    ($"unsupported schemaVersion {version}")
                    (deserializeAsync (changed.ToJsonString()))

        let aliased = JsonNode.Parse(raw.ToJsonString()).AsObject()
        aliased["version"] <- JsonValue.Create 1
        do! expectRejected "persisted version alias rejected" "unknown property 'version'" (deserializeAsync (aliased.ToJsonString()))

        let! canonicalRoundTrip = expectOk "canonical sidecar round trips" (async { return task |> serialize |> deserialize })
        assertEqual "canonical round trip kind" task.Kind canonicalRoundTrip.Kind
        assertEqual "canonical round trip state revision" task.StateRevision canonicalRoundTrip.StateRevision

        let missingSidecarDirectory = Path.Combine(tempRoot, ".tasks", "MIS-1")
        Directory.CreateDirectory missingSidecarDirectory |> ignore
        do! expectRejected "missing sidecar remains absent" "runtime sidecar does not exist" (getTask tempRoot "MIS-1")
        do! expectRejected "missing sidecar apply remains absent" "runtime sidecar does not exist" (applyTask tempRoot "MIS-1" 0 (StartWorkItem "W1"))
        assertTrue "missing sidecar creates no lock" (not (File.Exists(runtimeLock tempRoot "MIS-1")))

        let taskDocumentDirectory = Path.Combine(tempRoot, ".tasks", "DOC-1")
        Directory.CreateDirectory taskDocumentDirectory |> ignore
        File.WriteAllText(Path.Combine(taskDocumentDirectory, "TASK.md"), "legacy task")
        do! expectRejected "TASK.md layout rejected" "unsupported entry" (getTask tempRoot "DOC-1")

        let referencesDirectory = Path.Combine(tempRoot, ".tasks", "REF-1", "references")
        Directory.CreateDirectory referencesDirectory |> ignore
        File.WriteAllText(Path.Combine(referencesDirectory, "issue.md"), "legacy evidence")
        do! expectRejected "references layout rejected" "unsupported entry" (createTask tempRoot (request "REF-1" "References are not runtime state"))

        let lockDirectory = Path.Combine(tempRoot, ".tasks", "LCK-1")
        Directory.CreateDirectory lockDirectory |> ignore
        File.WriteAllText(Path.Combine(lockDirectory, "runtime.lock"), "legacy lock")
        do! expectRejected "persisted lock layout rejected" "unsupported entry" (createTask tempRoot (request "LCK-1" "Locks are ephemeral"))

        let! _ = expectOk "apply task" (applyTask tempRoot "CON-1" 0 (StartWorkItem "W1"))
        assertEqual "updated persisted schema" 1 ((JsonNode.Parse(File.ReadAllText(sidecar tempRoot "CON-1"))).["schemaVersion"].GetValue<int>())
        assertTrue "apply leaves no lock" (not (File.Exists(runtimeLock tempRoot "CON-1")))

        let! _ = expectOk "get leaves persisted state" (getTask tempRoot "CON-1")
        let! _ = expectOk "validate leaves persisted state" (validateTask tempRoot "CON-1")
        assertTrue "get leaves no lock" (not (File.Exists(runtimeLock tempRoot "CON-1")))
        assertTrue "validate leaves no lock" (not (File.Exists(runtimeLock tempRoot "CON-1")))

        printfn "OK schema-v1 contract: strict version, serializer/parser parity, legacy-layout rejection, and ephemeral locking"
    finally
        if Directory.Exists tempRoot && tempRoot.Contains("workflow-contract-v1-", StringComparison.Ordinal) then
            Directory.Delete(tempRoot, true)
}
// Standalone entry bridge: FSI needs one synchronous script entry.
|> Async.RunSynchronously
