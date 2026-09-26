// Source-level ephemeral-lock invariant coverage (AC11/AC21). Every normal
// Workflow operation must leave .tasks/<id>/ containing only runtime.json. A
// background sampler watches for any sub-second runtime.lock appearance while
// task_create, task_get, task_apply, and task_validate run against one real
// task directory.

#load "../ComputationExpressions.fs"
#load "../Workflow.fs"

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Workflow

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertEqual name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual

let expectOk name result =
    match result with
    | Ok value -> value
    | Error error -> failwithf "%s: expected Ok, got Error %s" name (renderError error)

let request id =
    { Id = id
      Title = "Ephemeral lock invariant"
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "The task is complete" ]
      WorkItems =
          [ { Id = "W1"
              Title = "Perform the work"
              DependsOn = []
              Children = [] } ] }

let taskDirectory root id = Path.Combine(root, ".tasks", id)
let lockPath root id = Path.Combine(taskDirectory root id, "runtime.lock")

// Recursive listing relative to the task directory. Tolerant of a directory
// that does not exist yet or is being created concurrently.
let enumerateEntries directory =
    try
        if Directory.Exists directory then
            Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path -> Path.GetRelativePath(directory, path))
            |> Seq.sort
            |> Seq.toList
        else
            []
    with
    | :? IOException -> []
    | :? UnauthorizedAccessException -> []

let assertOnlyRuntimeJson label root id =
    assertTrue $"{label}: runtime.lock must not exist" (not (File.Exists(lockPath root id)))
    assertEqual $"{label}: task directory contents" [ "runtime.json" ] (enumerateEntries (taskDirectory root id))

// Polls the task directory at one-millisecond intervals on a background thread
// so a transient runtime.lock that appears and is deleted inside a normal
// operation is still observed and reported.
type DirectorySampler(directory: string) =
    let snapshots = ConcurrentQueue<string list>()
    let cancellation = new CancellationTokenSource()
    let mutable thread = Unchecked.defaultof<Thread>

    member _.Start() =
        let loop () =
            while not cancellation.IsCancellationRequested do
                snapshots.Enqueue(enumerateEntries directory)
                Thread.Sleep 1

        thread <- Thread(ThreadStart loop)
        thread.IsBackground <- true
        thread.Start()

    member _.Stop() =
        cancellation.Cancel()
        if not (isNull thread) then thread.Join()
        snapshots.ToArray() |> Array.toList

let tempRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"workflow-ephemeral-lock-{Guid.NewGuid():N}")

Directory.CreateDirectory tempRoot |> ignore
let taskId = "EPH-1"
let sampler = DirectorySampler(taskDirectory tempRoot taskId)

try
    sampler.Start()

    expectOk "create" (createTask tempRoot (request taskId)) |> ignore
    assertOnlyRuntimeJson "after task_create" tempRoot taskId

    expectOk "get" (getTask tempRoot taskId) |> ignore
    assertOnlyRuntimeJson "after task_get" tempRoot taskId

    let applied = expectOk "apply" (applyTask tempRoot taskId 0 (StartWorkItem "W1"))
    assertEqual "apply revision" 1 applied.StateRevision
    assertOnlyRuntimeJson "after task_apply" tempRoot taskId

    expectOk "validate" (validateTask tempRoot taskId) |> ignore
    assertOnlyRuntimeJson "after task_validate" tempRoot taskId

    let observed = sampler.Stop()

    let observedEntries =
        observed |> List.collect id |> List.distinct |> List.sort

    let observedLockFiles =
        observedEntries
        |> List.filter (fun entry ->
            Path.GetFileName(entry).Equals("runtime.lock", StringComparison.OrdinalIgnoreCase))

    assertTrue
        (sprintf "sampler never observed runtime.lock (observed: %A)" observedEntries)
        observedLockFiles.IsEmpty

    assertOnlyRuntimeJson "final" tempRoot taskId

    printfn "OK ephemeral lock invariant: create/get/apply/validate leave only runtime.json"
    printfn "observed task-directory snapshots (%d samples, %d distinct): %A" observed.Length observedEntries.Length observedEntries
finally
    try sampler.Stop() |> ignore with _ -> ()

    if Directory.Exists tempRoot
       && tempRoot.Contains("workflow-ephemeral-lock-", StringComparison.Ordinal) then
        Directory.Delete(tempRoot, true)
