// role: test entrypoint
// Pure HMAC semantics over dummy bytes, with no filesystem and no key access.
// The receipt-bearing persistence cases run in an isolated child process.

#load "../../domain/Integrity.fs"

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

// Normalized path equality ignores case and trailing separators.
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

// The real per-account key directory is the exact location the production code would use, and the approved temporary base may legitimately live below the real application-data directory, so this value may only be compared by exact path equality.
let realKeyDirectory = Path.Combine(realAppData, "mcp-store", "workflow")

assertTrue
    "the parent process must not already run with a redirected application data directory"
    (not (realAppData.StartsWith(approvedBase, StringComparison.OrdinalIgnoreCase)))

let dummyKey = Array.init 32 (fun index -> byte (index + 1))
let otherKey = Array.init 32 (fun index -> byte (255 - index))
let payload = Encoding.UTF8.GetBytes "workflow-integrity-payload"
let otherPayload = Encoding.UTF8.GetBytes "workflow-integrity-payload-mutated"

let mutable hmacPassed = 0
let mutable hmacFailed = 0

let hmacCheck name condition =
    if condition then
        hmacPassed <- hmacPassed + 1
        printfn "CASE %s PASS" name
    else
        hmacFailed <- hmacFailed + 1
        printfn "CASE %s FAIL" name

let tag = WorkflowIntegrity.authenticate dummyKey payload
hmacCheck "hmac.tagLength" (tag.Length = 32)
hmacCheck "hmac.deterministic" (WorkflowIntegrity.authenticate dummyKey payload = tag)
hmacCheck "hmac.verifyAccepted" (WorkflowIntegrity.verify dummyKey payload tag)
hmacCheck "hmac.tamperedPayloadRejected" (not (WorkflowIntegrity.verify dummyKey otherPayload tag))

let flippedTag = Array.copy tag
let lastTagIndex = flippedTag.Length - 1
flippedTag.[lastTagIndex] <- byte (int flippedTag.[lastTagIndex] ^^^ 0x01)
hmacCheck "hmac.tamperedTagRejected" (not (WorkflowIntegrity.verify dummyKey payload flippedTag))
hmacCheck "hmac.wrongKeyRejected" (not (WorkflowIntegrity.verify otherKey payload tag))
hmacCheck "hmac.shortTagRejected" (not (WorkflowIntegrity.verify dummyKey payload (Array.sub tag 0 31)))
hmacCheck "hmac.emptyTagRejected" (not (WorkflowIntegrity.verify dummyKey payload Array.empty))
hmacCheck "hmac.nullTagRejected" (not (WorkflowIntegrity.verify dummyKey payload null))

let mutable shortKeyRaised = false

try
    WorkflowIntegrity.authenticate (Array.sub dummyKey 0 31) payload |> ignore
with
| :? ArgumentException -> shortKeyRaised <- true
| _ -> ()

hmacCheck "hmac.shortKeyRejected" shortKeyRaised

let mutable nullKeyRaised = false

try
    WorkflowIntegrity.authenticate null payload |> ignore
with
| :? ArgumentException -> nullKeyRaised <- true
| _ -> ()

hmacCheck "hmac.nullKeyRejected" nullKeyRaised

let mutable nullPayloadRaised = false

try
    WorkflowIntegrity.authenticate dummyKey null |> ignore
with
| :? ArgumentException -> nullPayloadRaised <- true
| _ -> ()

hmacCheck "hmac.nullPayloadRejected" nullPayloadRaised

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

let runChild () : Async<string> =
    async {
        let runRoot = Path.Combine(approvedBase, "integrity-" + Guid.NewGuid().ToString("N"))
        let appData = Path.Combine(runRoot, "appdata")
        let home = Path.Combine(runRoot, "home")
        let temp = Path.Combine(runRoot, "temp")

        // The run root belongs to this run only, and is removed after every outcome unless a child case failed and its artifacts are kept for diagnosis.
        let mutable retainRunRoot = false

        try
            for directory in [ runRoot; appData; home; temp ] do
                Directory.CreateDirectory directory |> ignore

            let startInfo = ProcessStartInfo()
            startInfo.FileName <- dotnetHost
            startInfo.ArgumentList.Add "--version"
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            startInfo.WorkingDirectory <- repoRoot
            use versionProcess = Process.Start startInfo
            let! versionOut = versionProcess.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
            let! versionErr = versionProcess.StandardError.ReadToEndAsync() |> Async.AwaitTask
            do! versionProcess.WaitForExitAsync() |> Async.AwaitTask

            if versionProcess.ExitCode <> 0 then
                return failWith (sprintf "could not query the .NET SDK version from '%s': %s" dotnetHost (versionErr.Trim()))

            assertEqual "the required .NET SDK version" requiredSdk (versionOut.Trim())

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
            // The per-account application data and home directories are redirected so the child can never reach the real per-account key.
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

            // The child must have resolved the isolated key directory, never the real per-account one.
            let keyDirectory =
                childLines
                |> Array.tryFind (fun line -> line.StartsWith("KEYDIR ", StringComparison.Ordinal))
                |> Option.map (fun line -> line.Substring("KEYDIR ".Length).Trim())

            match keyDirectory with
            | None -> return failWith "the isolated child did not report its resolved key directory"
            | Some directory ->
                // The resolved key directory must live inside the isolated run root as a whole path segment.
                if not (isInsideDirectory runRoot directory) then
                    return failWith (sprintf "the isolated child resolved an unexpected key directory: %s" directory)

                // The comparison is exact because the approved temporary base may live below the real application-data directory, so a prefix comparison would reject a correctly isolated run.
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
                // The child prints "CASE <name> PASS" and "CASE <name> FAIL <detail>", so a failing case carries its detail after the marker and is not detected by a trailing " FAIL" comparison.
                let reportedPass = childLines |> Array.filter (fun line -> Regex.IsMatch(line, @"^CASE \S+ PASS$")) |> Array.length
                let reportedFail = childLines |> Array.filter (fun line -> Regex.IsMatch(line, @"^CASE \S+ FAIL(\s|$)")) |> Array.length

                assertEqual "the isolated child reported case counts consistently (passed)" childPassed reportedPass
                assertEqual "the isolated child reported case counts consistently (failed)" childFailed reportedFail
                assertTrue "the isolated child must not report a failed case" (childFailed = 0)
                assertTrue "the isolated child must report at least one case" (childPassed > 0)

                assertTrue
                    "the isolated child reported no fatal error"
                    (childLines |> Array.exists (fun line -> line.StartsWith("FATAL ", StringComparison.Ordinal)) |> not)

                if child.ExitCode <> 0 then
                    return failWith (sprintf "the isolated child exited with code %d" child.ExitCode)

                // A failed case keeps the isolated run root for diagnosis.
                if childFailed > 0 then
                    retainRunRoot <- true

                return text
        finally
            // Only the run root this invocation created is removed, and only when it is inside the approved temporary base; no key or configuration content is read.
            if not retainRunRoot && Directory.Exists runRoot && isInsideDirectory approvedBase runRoot then
                try
                    Directory.Delete(runRoot, true)
                with _ -> ()
    }

// Standalone entry bridge: FSI needs one synchronous script entry.
async {
    let! childSummary = runChild ()

    printfn "OK Workflow integrity receipt foundation: %d pure HMAC cases and the isolated receipt cases (%s)" hmacPassed childSummary

    if hmacFailed > 0 then
        failWith (sprintf "the pure HMAC layer reported %d failed case(s)" hmacFailed)
}
|> Async.RunSynchronously
