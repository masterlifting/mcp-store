// role: test entrypoint
// Deterministic coverage for the authenticated task boundary.
// The children run with redirected application-data and home directories, so the real per-account integrity key is never read, created, or rotated here.
// Every confirmation in a child is a deterministic test double standing in for the native host form, so no case is evidence that a human approved anything.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions

let failWith message = raise (Exception message)

let assertTrue name condition =
    if not condition then failWith (sprintf "%s: expected true" name)

let assertEqual name expected actual =
    if expected <> actual then
        failWith (sprintf "%s: expected %A, got %A" name expected actual)


let normalizePath (value: string) =
    (Path.GetFullPath value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

let samePath (left: string) (right: string) =
    String.Equals(normalizePath left, normalizePath right, StringComparison.OrdinalIgnoreCase)

// Containment is segment-aware: a plain prefix comparison would accept a sibling directory that merely shares the parent's name prefix.
let isInsideDirectory (parent: string) (candidate: string) =
    let parentPath = normalizePath parent
    let candidatePath = normalizePath candidate
    candidatePath.StartsWith(parentPath + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let childScript = Path.Combine(__SOURCE_DIRECTORY__, "Cases.fsx")
let requiredSdk = "11.0.100-rc.1.26425.128"


let approvedBase =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_TEMP_BASE" with
    | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
    | _ -> Path.Combine(Path.GetTempPath(), "opencode", "GHI-18", "tests")

let systemTemp = Path.GetFullPath(Path.GetTempPath())

assertTrue
    "the approved temporary base must live under the system temporary directory"
    (approvedBase.StartsWith(systemTemp, StringComparison.OrdinalIgnoreCase))

Directory.CreateDirectory approvedBase |> ignore

// The parent process must itself be unredirected: the real per-account application-data directory is only used as a comparison value, never read.
let realAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

assertTrue "the real per-account application data directory must be resolvable" (not (String.IsNullOrWhiteSpace realAppData))

// The real per-account key directory is the exact location the production code would use, so it may only be compared by exact path equality.
let realKeyDirectory = Path.Combine(realAppData, "mcp-store", "workflow")

assertTrue
    "the parent process must not already run with a redirected application data directory"
    (not (realAppData.StartsWith(approvedBase, StringComparison.OrdinalIgnoreCase)))


let resolveDotnetHost () =
    let names = if OperatingSystem.IsWindows() then [ "dotnet.exe" ] else [ "dotnet" ]
    let pathValue = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""

    let candidate =
        pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun directory ->
            names
            |> List.map (fun name -> Path.Combine(directory.Trim(), name))
            |> List.tryFind File.Exists)

    candidate
    |> Option.map Path.GetFullPath
    |> Option.defaultWith (fun () -> failWith (sprintf "the required .NET host '%s' is not on PATH" names.Head))

let dotnetHost = resolveDotnetHost ()

let assertDotnetSdk () =
    async {
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- dotnetHost
        startInfo.ArgumentList.Add "--version"
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.WorkingDirectory <- repoRoot
        use versionProcess = Process.Start startInfo
        // The SDK guard awaits the redirected streams and the process exit instead of blocking on them, so this suite never waits synchronously for the host.
        let stdoutTask = versionProcess.StandardOutput.ReadToEndAsync()
        let stderrTask = versionProcess.StandardError.ReadToEndAsync()
        do! versionProcess.WaitForExitAsync() |> Async.AwaitTask
        let! stdout = stdoutTask |> Async.AwaitTask
        let! stderr = stderrTask |> Async.AwaitTask

        if versionProcess.ExitCode <> 0 then
            failWith (sprintf "could not query the .NET SDK version from '%s': %s" dotnetHost (stderr.Trim()))

        assertEqual "the required .NET SDK version" requiredSdk (stdout.Trim())
    }


// The per-child outcome is reported as data rather than an abort, so every child still runs and the aggregate reports the real per-child counts instead of stopping at the first failure.
type ChildOutcome = {
    Phase: string
    Summary: string
    Passed: int
    Failed: int
    ExitCode: int
}

// `releaseRunRoot` decides whether this invocation may remove the run root it created; a root that a later child reuses is released only after that child has run.
let runChild (runRoot: string) (phase: string) (releaseRunRoot: bool) : Async<ChildOutcome> =
    async {
        let appData = Path.Combine(runRoot, "appdata")
        let home = Path.Combine(runRoot, "home")
        let temp = Path.Combine(runRoot, "temp")

        // The finally below removes the run root after every outcome, including a rejected guard, unless a child case failed and the artifacts are kept for diagnosis.
        let mutable retainRunRoot = false

        try
            for directory in [ runRoot; appData; home; temp ] do
                Directory.CreateDirectory directory |> ignore

            let childInfo = ProcessStartInfo()
            childInfo.FileName <- dotnetHost
            childInfo.ArgumentList.Add "fsi"
            childInfo.ArgumentList.Add "--exec"
            childInfo.ArgumentList.Add "--quiet"
            childInfo.ArgumentList.Add childScript
            childInfo.WorkingDirectory <- repoRoot
            childInfo.RedirectStandardInput <- true
            childInfo.RedirectStandardOutput <- true
            childInfo.RedirectStandardError <- true
            childInfo.UseShellExecute <- false
            childInfo.CreateNoWindow <- true
            // Redirect the per-account application data and home directories so the child can never reach the real per-account key.
            childInfo.Environment["LOCALAPPDATA"] <- appData
            childInfo.Environment["APPDATA"] <- appData
            childInfo.Environment["ProgramData"] <- appData
            childInfo.Environment["USERPROFILE"] <- home
            childInfo.Environment["HOME"] <- home
            childInfo.Environment["TEMP"] <- temp
            childInfo.Environment["TMP"] <- temp
            childInfo.Environment["DOTNET_NOLOGO"] <- "1"
            childInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
            childInfo.Environment["MCP_WORKFLOW_TEST_ISOLATED_ROOT"] <- runRoot
            childInfo.Environment["MCP_WORKFLOW_TEST_REAL_APP_DATA"] <- realAppData
            childInfo.Environment["MCP_WORKFLOW_TEST_PHASE"] <- phase

            use child = Process.Start childInfo
            let stdoutTask = child.StandardOutput.ReadToEndAsync()
            let stderrTask = child.StandardError.ReadToEndAsync()
            do! child.WaitForExitAsync() |> Async.AwaitTask
            let! stdout = stdoutTask |> Async.AwaitTask
            let! stderr = stderrTask |> Async.AwaitTask

            printfn "%s" stdout

            if not (String.IsNullOrWhiteSpace stderr) then
                printfn "CHILD-STDERR %s" (stderr.Trim())

            // The child writes platform line endings, so its output is split on both terminators before any line comparison.
            let childLines = stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

            // A key directory outside the isolated run root would mean the child reached the real per-account key.
            let keyDirectory =
                childLines
                |> Array.tryFind (fun line -> line.StartsWith("KEYDIR ", StringComparison.Ordinal))
                |> Option.map (fun line -> line.Substring("KEYDIR ".Length).Trim())

            match keyDirectory with
            | None -> return failWith "the isolated child did not report its resolved key directory"
            | Some directory ->
                if not (isInsideDirectory runRoot directory) then
                    return failWith (sprintf "the isolated child resolved an unexpected key directory: %s" directory)

                if samePath directory realKeyDirectory then
                    return failWith (sprintf "the isolated child resolved the real per-account key directory: %s" directory)

            let summary =
                childLines
                |> Array.tryFind (fun line -> line.StartsWith("SUMMARY ", StringComparison.Ordinal))
                |> Option.map (fun line -> line.Substring("SUMMARY ".Length).Trim())

            match summary with
            | None -> return failWith "the isolated child did not report a case summary"
            | Some text ->
                let passedMatch = Regex.Match(text, @"passed=(\d+)")
                let failedMatch = Regex.Match(text, @"failed=(\d+)")
                let childPassed = if passedMatch.Success then Int32.Parse(passedMatch.Groups.[1].Value) else -1
                let childFailed = if failedMatch.Success then Int32.Parse(failedMatch.Groups.[1].Value) else -1
                let reportedPass = childLines |> Array.filter (fun line -> Regex.IsMatch(line, @"^CASE \S+ PASS$")) |> Array.length
                let reportedFail = childLines |> Array.filter (fun line -> Regex.IsMatch(line, @"^CASE \S+ FAIL(\s|$)")) |> Array.length

                assertEqual "the isolated child reported case counts consistently (passed)" childPassed reportedPass
                assertEqual "the isolated child reported case counts consistently (failed)" childFailed reportedFail
                assertTrue "the isolated child must report at least one case" (childPassed > 0)

                // Retention is decided before the outcome is reported, so the diagnostic artifacts of a failing child are never deleted.
                if childFailed > 0 then
                    retainRunRoot <- true

                return
                    { Phase = phase
                      Summary = text
                      Passed = childPassed
                      Failed = childFailed
                      ExitCode = child.ExitCode }
        finally
            // Remove only the run root this invocation created, and only when it is inside the approved temporary base.
            if
                releaseRunRoot
                && not retainRunRoot
                && Directory.Exists runRoot
                && isInsideDirectory approvedBase runRoot
            then
                try
                    Directory.Delete(runRoot, true)
                with _ -> ()
    }


// Standalone entry bridge: FSI needs one synchronous script entry.
async {
    do! assertDotnetSdk ()

    let mainRoot = Path.Combine(approvedBase, "schema3-main-" + Guid.NewGuid().ToString("N"))
    let keyRoot = Path.Combine(approvedBase, "schema3-key-" + Guid.NewGuid().ToString("N"))

    // The root reused by the later child is released only after that child has run, and a failing child keeps its run root for diagnosis.
    let! mainOutcome = runChild mainRoot "main" false
    let! restartOutcome = runChild mainRoot "restart" (mainOutcome.Failed = 0)
    let! keyOutcome = runChild keyRoot "key" true

    for outcome in [ mainOutcome; restartOutcome; keyOutcome ] do
        printfn "PHASE %s %s" outcome.Phase outcome.Summary

    let failing =
        [ mainOutcome; restartOutcome; keyOutcome ]
        |> List.filter (fun outcome -> outcome.Failed > 0 || outcome.ExitCode <> 0)

    if not failing.IsEmpty then
        failWith
            ("the isolated boundary suite reported failing cases: "
             + (failing
                |> List.map (fun outcome ->
                    sprintf
                        "%s passed=%d failed=%d exit=%d"
                        outcome.Phase
                        outcome.Passed
                        outcome.Failed
                        outcome.ExitCode)
                |> String.concat "; "))

    printfn
        "OK Workflow schema3 authenticated boundary: main (%s), restart (%s), key (%s)"
        mainOutcome.Summary
        restartOutcome.Summary
        keyOutcome.Summary
}
|> Async.RunSynchronously
