// Focused deterministic coverage for runtime path containment and the strict
// task-directory layout. Reparse points fail closed and runtime.lock is never
// a supported persistence boundary.

#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"

open System
open System.Diagnostics
open System.IO
open Workflow

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let expectOk name (result: Async<Result<'a, RuntimeError>>) : Async<'a> =
    async {
        match! result with
        | Ok value -> return value
        | Error error -> return failwithf "%s: %s" name (renderError error)
    }

let expectRejected (name: string) (fragment: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ -> return failwithf "%s: expected rejection" name
        | Error error ->
            let message = renderError error
            if message.Contains(fragment, StringComparison.Ordinal) then
                return ()
            else
                return failwithf "%s: expected '%s', got '%s'" name fragment message
    }

let request id =
    { Id = id
      Title = "Path safety"
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "Create" ]
      WorkItems = [ { Id = "W1"; Title = "Create"; DependsOn = []; Children = [] } ] }

let createDirectoryLink (link: string) (target: string) : Async<unit> =
    async {
        if OperatingSystem.IsWindows() then
            let startInfo = ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            use child = Process.Start startInfo
            let stdoutTask = child.StandardOutput.ReadToEndAsync()
            let stderrTask = child.StandardError.ReadToEndAsync()
            let mutable timedOut = false
            try
                do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 60.0) |> Async.AwaitTask
            with :? TimeoutException ->
                timedOut <- true
                if not child.HasExited then child.Kill true
                do! child.WaitForExitAsync() |> Async.AwaitTask
            let! stdout = stdoutTask |> Async.AwaitTask
            let! stderr = stderrTask |> Async.AwaitTask
            if timedOut then failwithf "junction creation timed out: %s %s %s" link stdout stderr
            if child.ExitCode <> 0 then failwithf "could not create junction '%s': %s %s" link stdout stderr
        else
            Directory.CreateSymbolicLink(link, target) |> ignore
    }

let removeLink (path: string) =
    if Directory.Exists path then Directory.Delete(path, false)
    elif File.Exists path then File.Delete path

let tempRoot = Path.Combine(Path.GetTempPath(), "opencode", $"workflow-pathsafety-v1-{Guid.NewGuid():N}")
Directory.CreateDirectory tempRoot |> ignore

let sidecar root id = Path.Combine(root, ".tasks", id, SidecarFileName)

async {
    try
        let realRoot = Path.Combine(tempRoot, "real-root")
        Directory.CreateDirectory realRoot |> ignore
        let rootLink = Path.Combine(tempRoot, "root-link")
        do! createDirectoryLink rootLink realRoot
        do! expectRejected "project root reparse point rejected" "project root must not be a reparse point" (getTask rootLink "PAT-1")

        let traversalRoot = Path.Combine(tempRoot, "traversal-root")
        Directory.CreateDirectory traversalRoot |> ignore
        let traversalTarget = Path.Combine(tempRoot, "traversal-target")
        Directory.CreateDirectory traversalTarget |> ignore
        do! createDirectoryLink (Path.Combine(traversalRoot, ".tasks")) traversalTarget
        do! expectRejected "traversed reparse point rejected" "path must not traverse a reparse point" (getTask traversalRoot "PAT-2")

        let leafRoot = Path.Combine(tempRoot, "leaf-root")
        let leafDirectory = Path.Combine(leafRoot, ".tasks", "PAT-3")
        Directory.CreateDirectory leafDirectory |> ignore
        let leafTarget = Path.Combine(tempRoot, "leaf-target")
        Directory.CreateDirectory leafTarget |> ignore
        do! createDirectoryLink (sidecar leafRoot "PAT-3") leafTarget
        do! expectRejected "sidecar reparse leaf rejected" "file must not be a reparse point" (getTask leafRoot "PAT-3")

        let layoutRoot = Path.Combine(tempRoot, "layout-root")
        let taskDirectory = Path.Combine(layoutRoot, ".tasks", "PAT-4")
        let references = Path.Combine(taskDirectory, "references")
        Directory.CreateDirectory references |> ignore
        File.WriteAllText(Path.Combine(references, "issue.md"), "legacy evidence")
        do! expectRejected "references layout rejected" "unsupported entry" (createTask layoutRoot (request "PAT-4"))

        let taskDocumentRoot = Path.Combine(tempRoot, "task-document-root")
        let taskDocumentDirectory = Path.Combine(taskDocumentRoot, ".tasks", "PAT-5")
        Directory.CreateDirectory taskDocumentDirectory |> ignore
        File.WriteAllText(Path.Combine(taskDocumentDirectory, "TASK.md"), "legacy task")
        do! expectRejected "TASK.md layout rejected" "unsupported entry" (createTask taskDocumentRoot (request "PAT-5"))

        let lockRoot = Path.Combine(tempRoot, "lock-root")
        let lockDirectory = Path.Combine(lockRoot, ".tasks", "PAT-6")
        Directory.CreateDirectory lockDirectory |> ignore
        File.WriteAllText(Path.Combine(lockDirectory, "runtime.lock"), "legacy lock")
        do! expectRejected "persisted lock layout rejected" "unsupported entry" (createTask lockRoot (request "PAT-6"))

        // Existing-record operations must revalidate the task directory after the
        // initial path resolution; a pre-existing junction is the deterministic
        // boundary check for the concurrent swap case.
        let existingRoot = Path.Combine(tempRoot, "existing-root")
        let existingTaskDirectory = Path.Combine(existingRoot, ".tasks", "PAT-7")
        let existingTarget = Path.Combine(tempRoot, "existing-target")
        Directory.CreateDirectory existingRoot |> ignore
        Directory.CreateDirectory existingTarget |> ignore
        let! _ = expectOk "create existing task" (createTask existingRoot (request "PAT-7"))
        Directory.Delete(existingTaskDirectory, true)
        do! createDirectoryLink existingTaskDirectory existingTarget
        do! expectRejected "existing task junction rejected by get" "file must not be a reparse point" (getTask existingRoot "PAT-7")
        do! expectRejected "existing task junction rejected by apply" "file must not be a reparse point" (applyTask existingRoot "PAT-7" 0 (StartWorkItem "W1"))

        printfn "OK path safety: trusted roots, existing-record boundaries, traversed/leaf reparses, and strict v1 task layouts fail closed"
    finally
        [ Path.Combine(tempRoot, "root-link")
          Path.Combine(tempRoot, "traversal-root", ".tasks")
          sidecar (Path.Combine(tempRoot, "leaf-root")) "PAT-3"
          Path.Combine(tempRoot, "existing-root", ".tasks", "PAT-7") ]
        |> List.iter removeLink

        if Directory.Exists tempRoot && tempRoot.Contains("workflow-pathsafety-v1-", StringComparison.Ordinal) then
            Directory.Delete(tempRoot, true)
}
// Standalone entry bridge: FSI requires one synchronous top-level boundary.
|> Async.RunSynchronously
