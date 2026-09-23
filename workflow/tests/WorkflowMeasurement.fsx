// Deterministic repeated-operation measurement for the retained Workflow
// library/CLI boundary. The workload is fixed: get, apply, validate, repeated
// over one task. CLI execution is deliberately fixed to the four existing entry
// scripts; no caller-selected executable or command is accepted.

#load "../ComputationExpressions.fs"
#load "../Workflow.fs"
#load "../WorkflowAdapter.fs"

open System
open System.Diagnostics
open System.Globalization
open System.IO
open Workflow
open WorkflowAdapter

let usage () =
    eprintfn "usage: WorkflowMeasurement.fsx [--iterations <n>] [--rounds <n>]"
    exit 2

let parsePositive (name: string) (value: string) =
    match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, number when number > 0 -> number
    | _ ->
        eprintfn "%s must be a positive integer" name
        exit 2

let args = fsi.CommandLineArgs |> Array.skip 1
let mutable iterations = 8
let mutable rounds = 3
let mutable index = 0

while index < args.Length do
    match args.[index] with
    | "--iterations" when index + 1 < args.Length ->
        iterations <- parsePositive "iterations" args.[index + 1]
        index <- index + 2
    | "--rounds" when index + 1 < args.Length ->
        rounds <- parsePositive "rounds" args.[index + 1]
        index <- index + 2
    | _ -> usage ()

let expectOk name result =
    match result with
    | Ok value -> value
    | Error error -> failwithf "%s: %s" name (renderError error)

let taskId = "BENCH-1"
let taskTitle = "Workflow boundary measurement"

let request =
    { Id = taskId
      Title = taskTitle
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "Repeated operation measurement completes" ]
      WorkItems =
          [ { Id = "W1"
              Title = "Measure repeated operations"
              DependsOn = []
              Children = [] } ] }

let scriptDirectory = __SOURCE_DIRECTORY__
let createScript = Path.Combine(scriptDirectory, "TaskCreate.fsx")
let getScript = Path.Combine(scriptDirectory, "TaskGet.fsx")
let applyScript = Path.Combine(scriptDirectory, "TaskApply.fsx")
let validateScript = Path.Combine(scriptDirectory, "TaskValidate.fsx")

let ensureFile path name =
    if not (File.Exists path) then
        failwithf "fixed CLI entry script does not exist (%s): %s" name path

[ createScript, "create"
  getScript, "get"
  applyScript, "apply"
  validateScript, "validate" ]
|> List.iter (fun (path, name) -> ensureFile path name)

let runFixedCli script arguments =
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- "dotnet"
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.ArgumentList.Add("fsi")
    startInfo.ArgumentList.Add("--nologo")
    startInfo.ArgumentList.Add(script)
    arguments |> List.iter startInfo.ArgumentList.Add

    use child = new Process()
    child.StartInfo <- startInfo

    if not (child.Start()) then
        failwithf "could not start fixed CLI entry script: %s" script

    // Drain both redirected streams concurrently so a verbose compiler/runtime
    // cannot block the fixed child process on one full pipe.
    let outputTask = child.StandardOutput.ReadToEndAsync()
    let errorTask = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    let output = outputTask.Result
    let error = errorTask.Result

    if child.ExitCode <> 0 then
        failwithf "fixed CLI entry script failed (%s): %s" (Path.GetFileName script) (error.Trim())

    output.Trim()

let rootFor mode round =
    let root = Path.Combine(Path.GetTempPath(), "opencode", $"taskruntime-measurement-{mode}-{round}-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore
    root

let createLibraryTask root =
    execute (CreateTask { Root = root; ProfileId = None; Request = request }) |> expectOk "library create"

let createCliTask root =
    runFixedCli
        createScript
        [ root
          taskId
          taskTitle
          "--acceptance"
          "Repeated operation measurement completes" ]
    |> deserialize
    |> expectOk "CLI create"

let evidenceFor iteration : Evidence =
    { Id = $"E{iteration}"
      Kind = EvidenceKind.Observation
      Source = EvidenceSource "task-runtime-measurement"
      Subject = None
      ProducerRole = None
      ProducerId = None
      Reference = None
      Summary = $"measurement iteration {iteration}" }

let runLibraryRound round =
    let root = rootFor "library" round

    try
        let mutable state = createLibraryTask root
        let stopwatch = Stopwatch.StartNew()

        for iteration in 1..iterations do
            execute (GetTask { Root = root; TaskId = taskId }) |> expectOk "library get" |> ignore

            state <-
                execute
                    (ApplyTask
                        { Root = root
                          TaskId = taskId
                          ExpectedStateRevision = state.StateRevision
                          Command = AddEvidence(evidenceFor iteration) })
                |> expectOk "library apply"

            execute (ValidateTask { Root = root; TaskId = taskId }) |> expectOk "library validate" |> ignore

        stopwatch.Stop()
        stopwatch.Elapsed.TotalMilliseconds
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let runCliRound round =
    let root = rootFor "cli" round

    try
        createCliTask root |> ignore
        let mutable expectedRevision = 0
        let stopwatch = Stopwatch.StartNew()

        for iteration in 1..iterations do
            runFixedCli getScript [ root; taskId ] |> deserialize |> expectOk "CLI get" |> ignore

            let evidence = evidenceFor iteration

            let next =
                runFixedCli
                    applyScript
                    [ root
                      taskId
                      string expectedRevision
                      "add-evidence"
                      evidence.Id
                      "observation"
                      "task-runtime-measurement"
                      evidence.Summary ]
                |> deserialize
                |> expectOk "CLI apply"

            expectedRevision <- next.StateRevision
            runFixedCli validateScript [ root; taskId ] |> ignore

        stopwatch.Stop()
        stopwatch.Elapsed.TotalMilliseconds
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let median values =
    let ordered = values |> List.sort
    ordered.[ordered.Length / 2]

let libraryMeasurements = [ for round in 1..rounds -> runLibraryRound round ]
let cliMeasurements = [ for round in 1..rounds -> runCliRound round ]
let libraryMedian = median libraryMeasurements
let cliMedian = median cliMeasurements
let saved = cliMedian - libraryMedian
let speedup = if libraryMedian = 0.0 then Double.PositiveInfinity else cliMedian / libraryMedian

// MCP is material only when the measured process-startup saving is both large
// enough to matter to this workload and resistant to a simple ratio artifact.
// The result is a boundary recommendation, not a claim that MCP protocol cost
// has already been measured.
let minimumMaterialSavingMs = 250.0
let minimumMaterialSpeedup = 2.0

let sufficientSample = iterations >= 3 && rounds >= 3

let verdict, recommendation =
    if not sufficientSample then
        "NO-VERDICT",
        "Repeat with at least 3 iterations and 3 rounds; this sample is a smoke measurement only."
    elif saved >= minimumMaterialSavingMs && speedup >= minimumMaterialSpeedup then
        "POSITIVE",
        "MCP candidate justified for subtask 6; retain this workload when measuring protocol overhead."
    else
        "NEGATIVE",
        "Retain the library/scripts/CLI boundary; do not deliver MCP solely for repeated Workflow calls."

printfn "MEASUREMENT task-runtime-boundary"
printfn "workload: get + apply(add-evidence) + validate x %d iterations" iterations
printfn "rounds: %d" rounds
printfn "operations-per-path: %d" (iterations * 3)
printfn "library-ms-raw: %s" (libraryMeasurements |> List.map (sprintf "%.3f") |> String.concat ",")
printfn "cli-ms-raw: %s" (cliMeasurements |> List.map (sprintf "%.3f") |> String.concat ",")
printfn "library-ms-median: %.3f" libraryMedian
printfn "cli-ms-median: %.3f" cliMedian
printfn "saved-ms-median: %.3f" saved
printfn "speedup: %.3f" speedup
printfn "sample-boundary: iterations >= 3 AND rounds >= 3"
printfn "positive-boundary: saved-ms >= %.0f AND speedup >= %.1f" minimumMaterialSavingMs minimumMaterialSpeedup
printfn "VERDICT: %s" verdict
printfn "RECOMMENDATION: %s" recommendation
