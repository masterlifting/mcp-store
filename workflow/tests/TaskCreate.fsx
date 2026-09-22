#load "../ComputationExpressions.fs"
#load "../Workflow.fs"
#load "../WorkflowAdapter.fs"

open System
open Workflow
open WorkflowAdapter

let usage () =
    eprintfn "usage: TaskCreate.fsx <project-root> <TASK-ID> <title> [--kind execution|research] [--profile <id>] [--acceptance <text>] [--work <title>]"
    exit 2

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length < 3 then usage ()

let root, id, title = args.[0], args.[1], args.[2]
let mutable kind = Execution
let mutable profile = None
let mutable acceptance = []
let mutable work = []
let mutable index = 3

while index < args.Length do
    match args.[index] with
    | "--kind" when index + 1 < args.Length ->
        kind <-
            match args.[index + 1] with
            | "execution" -> Execution
            | "research" -> Research
            | _ -> usage ()
        index <- index + 2
    | "--profile" when index + 1 < args.Length ->
        profile <- Some args.[index + 1]
        index <- index + 2
    | "--acceptance" when index + 1 < args.Length ->
        acceptance <- ($"AC{acceptance.Length + 1}", args.[index + 1]) :: acceptance
        index <- index + 2
    | "--work" when index + 1 < args.Length ->
        work <-
            { Id = $"W{work.Length + 1}"
              Title = args.[index + 1]
              DependsOn = []
              Children = [] }
            :: work
        index <- index + 2
    | _ -> usage ()

let request =
    { Id = id
      Title = title
      Kind = kind
      AcceptanceCriteria = List.rev acceptance
      WorkItems =
          match List.rev work with
          | [] ->
              [ { Id = "W1"
                  Title = "Execute the task"
                  DependsOn = []
                  Children = [] } ]
          | values -> values }

match execute (CreateTask { Root = root; ProfileId = profile; Request = request }) with
| Ok task ->
    printfn "%s" (serialize task)
| Error error ->
    eprintfn "%s" (renderError error)
    exit 1
