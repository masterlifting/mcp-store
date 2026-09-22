// Self-contained producer contract coverage for Workflow clean-slate schema v1.
// Consumer-owned OpenCode task records are migrated separately by opencode#27;
// this producer test must never read or mutate a sibling OpenCode repository.

#load "../ComputationExpressions.fs"
#load "../Workflow.fs"

open System
open System.IO
open System.Text.Json.Nodes
open Workflow

let assertEqual name expected actual =
    if actual <> expected then
        failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then
        failwithf "%s: expected true" name

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

let tempRoot =
    Path.Combine(
        Path.GetTempPath(),
        "mcp-workflow-schema-v1-contract",
        Guid.NewGuid().ToString("N")
    )

Directory.CreateDirectory tempRoot |> ignore

try
    let request =
        { Id = "SCHEMA-1"
          Title = "Workflow schema v1 producer contract"
          Kind = Execution
          AcceptanceCriteria = [ ("AC1", "Schema v1 round-trips") ]
          WorkItems =
              [ { Id = "W1"
                  Title = "Exercise schema contract"
                  DependsOn = []
                  Children = [] } ] }

    let created =
        createTask tempRoot request
        |> expectOk "create schema-v1 task"

    assertEqual "created state revision" 0 created.StateRevision

    let runtimePath =
        Path.Combine(tempRoot, ".tasks", request.Id, SidecarFileName)

    assertTrue "runtime sidecar exists" (File.Exists runtimePath)

    let text = File.ReadAllText runtimePath
    let raw = JsonNode.Parse(text).AsObject()

    assertEqual
        "canonical schemaVersion"
        1
        (raw.["schemaVersion"].GetValue<int>())

    // Schema v1 is the complete current shape, not the historical minimal v1.
    for field in
        [ "objective"
          "scope"
          "nonGoals"
          "contractState"
          "contractFingerprint"
          "guards"
          "profileGuardKeys"
          "decisions"
          "questions"
          "completionHistory" ] do
        assertTrue $"canonical v1 persists '{field}'" (raw.ContainsKey field)

    let roundTripped =
        deserialize text
        |> expectOk "canonical v1 round-trip"

    assertEqual "round-tripped id" request.Id roundTripped.Id
    assertEqual "round-tripped state revision" 0 roundTripped.StateRevision

    let withVersion (version: int) =
        let clone = JsonNode.Parse(text).AsObject()
        clone.["schemaVersion"] <- JsonValue.Create version
        clone.ToJsonString()

    deserialize (withVersion 2)
    |> expectRejected "schema v2 rejected" "unsupported schemaVersion 2"

    deserialize (withVersion 3)
    |> expectRejected "schema v3 rejected" "unsupported schemaVersion 3"

    // The ephemeral Workflow lock must not be persisted after creation.
    let lockPath =
        Path.Combine(tempRoot, ".tasks", request.Id, LockFileName)

    assertTrue "idle task has no runtime.lock" (not (File.Exists lockPath))

    printfn "OK Workflow producer schema-v1 contract: canonical v1 round-trips, v2/v3 are rejected, and idle lock state is clean."
finally
    if Directory.Exists tempRoot then
        Directory.Delete(tempRoot, true)
