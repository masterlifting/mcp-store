#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"
#load "../protocol/Adapter.fs"

open System
open Workflow
open WorkflowAdapter

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 2 then
    eprintfn "usage: TaskGet.fsx <project-root> <TASK-ID>"
    exit 2

// Standalone entry bridge: the only synchronous wait in this CLI's flow.
match execute (GetTask { Root = args.[0]; TaskId = args.[1] }) |> Async.RunSynchronously with
| Ok task -> printfn "%s" (serialize task)
| Error error ->
    eprintfn "%s" (renderError error)
    exit 1
