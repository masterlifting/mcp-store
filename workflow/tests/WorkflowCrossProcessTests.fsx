// Deterministic process-boundary coverage for Workflow CAS. Two independent
// producer processes must serialize read-decide-write through the ephemeral OS
// mutex; the task directory must still contain only runtime.json.

#load "../ComputationExpressions.fs"
#load "../Workflow.fs"

open System
open System.Diagnostics
open System.IO
open Workflow

let fail name message = failwithf "%s: %s" name message

let expectOk name result =
    match result with
    | Ok value -> value
    | Error error -> fail name (renderError error)

let request id =
    { Id = id
      Title = "Cross-process coordination"
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "The task is serialized" ]
      WorkItems = [ { Id = "W1"; Title = "Coordinate"; DependsOn = []; Children = [] } ] }

let evidence id =
    { Id = id
      Kind = EvidenceKind.Observation
      Source = EvidenceSource "cross-process-test"
      Subject = None
      ProducerRole = None
      ProducerId = None
      Reference = None
      Summary = id }

let sidecar root id = Path.Combine(root, ".tasks", id, SidecarFileName)

let runWorker root taskId evidenceId =
    match applyTask root taskId 0 (AddEvidence(evidence evidenceId)) with
    | Ok task -> printfn "ok:%d" task.StateRevision
    | Error error -> printfn "error:%s" (renderError error)

let startWorker script root taskId evidenceId =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add "fsi"
    startInfo.ArgumentList.Add "--nologo"
    startInfo.ArgumentList.Add script
    startInfo.ArgumentList.Add root
    startInfo.ArgumentList.Add taskId
    startInfo.ArgumentList.Add evidenceId
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    Process.Start startInfo

let arguments = fsi.CommandLineArgs |> Array.skip 1

// FSI defines __SOURCE_FILE__ as the bare file name, so combine it with the
// source directory to give the child process a resolvable script path.
let scriptPath = Path.Combine(__SOURCE_DIRECTORY__, __SOURCE_FILE__)

if arguments.Length = 3 then
    runWorker arguments.[0] arguments.[1] arguments.[2]
else
    let root = Path.Combine(Path.GetTempPath(), "opencode", $"workflow-cross-process-{Guid.NewGuid():N}")
    let taskId = "XPC-1"
    Directory.CreateDirectory root |> ignore

    try
        expectOk "create task" (createTask root (request taskId)) |> ignore

        use first = startWorker scriptPath root taskId "E1"
        use second = startWorker scriptPath root taskId "E2"
        let firstOutput = first.StandardOutput.ReadToEnd()
        let secondOutput = second.StandardOutput.ReadToEnd()
        first.WaitForExit()
        second.WaitForExit()

        if first.ExitCode <> 0 || second.ExitCode <> 0 then
            fail "worker exit" $"workers exited {first.ExitCode} and {second.ExitCode}"

        let outputs = [ firstOutput.Trim(); secondOutput.Trim() ]
        let outputsText = String.concat "; " outputs

        if outputs |> List.filter (fun value -> value.StartsWith("ok:1", StringComparison.Ordinal)) |> List.length <> 1 then
            fail "CAS winner" $"expected one successful worker, got {outputsText}"

        if outputs |> List.filter (fun value -> value.Contains("state revision conflict", StringComparison.Ordinal)) |> List.length <> 1 then
            fail "CAS loser" $"expected one conflict worker, got {outputsText}"

        let committed = expectOk "read committed task" (getTask root taskId)
        if committed.StateRevision <> 1 || committed.Evidence.Length <> 1 then
            fail "committed state" $"expected revision 1 and one evidence item, got {committed.StateRevision} and {committed.Evidence.Length}"

        if File.Exists(Path.Combine(root, ".tasks", taskId, "runtime.lock")) then
            fail "ephemeral lock" "runtime.lock was persisted"

        printfn "OK cross-process Workflow CAS and ephemeral synchronization"
    finally
        if Directory.Exists root then Directory.Delete(root, true)
