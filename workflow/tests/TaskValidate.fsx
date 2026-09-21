#load "../ComputationExpressions.fs"
#load "../TaskRuntime.fs"
#load "../TaskRuntimeAdapter.fs"

open System
open TaskRuntime
open TaskRuntimeAdapter

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length <> 2 then
    eprintfn "usage: TaskValidate.fsx <project-root> <TASK-ID>"
    exit 2

match execute (ValidateTask { Root = args.[0]; TaskId = args.[1] }) with
| Ok task ->
    printfn "OK %s stateRevision=%d" task.Id task.StateRevision
| Error error ->
    eprintfn "%s" (renderError error)
    exit 1
