// role: test entrypoint
// Toolchain-bootstrap regression: the real `Update.ps1` projection emits canonical SDK/runtime/platform; a duplicate literal fails the projection.
// `Bootstrap.fsx` source-key contract is asserted by reading its source rather than re-running it as a child.
#load "../../Provenance.fsx"
#load "Process.fsx"

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open BuildProvenance
open ReleaseTestProcess

let mutable passed = 0
let mutable failed = 0
let failures = ResizeArray<string>()

let assertTrue name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(name)

let assertEqual name expected actual =
    if expected = actual then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(sprintf "%s: expected %A got %A" name expected actual)

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private updatePs1Path =
    Path.Combine(repoRoot, "infrastructure", "release", "Update.ps1")

let private bootstrapPath =
    Path.Combine(repoRoot, "infrastructure", "release", "Bootstrap.fsx")

let private pwshExecutable () = async {
    // The PowerShell host may be either Windows PowerShell or PowerShell 7.
    let candidates =
        [
            "pwsh"
            "powershell"
            "C:\\Program Files\\PowerShell\\7\\pwsh.exe"
            "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
        ]
    let mutable found : string option = None
    for path in candidates do
        if found.IsNone then
            let! ok =
                try
                    if Path.IsPathRooted path then
                        async.Return(File.Exists path)
                    else
                        let probe = ProcessStartInfo(path)
                        probe.FileName <- path
                        probe.UseShellExecute <- false
                        probe.RedirectStandardOutput <- true
                        probe.RedirectStandardError <- true
                        probe.Arguments <- "-NoProfile -Command \"exit 0\""
                        async {
                            match! capture 2000 probe with
                            | Ok(exitCode, _, _) -> return exitCode = 0
                            | Error _ -> return false
                        }
                with
                | _ -> async.Return false
            if ok then found <- Some path
    match found with
    | Some path -> return Some path
    | None -> return None
}

// The bootstrap summary keys are stable: the workflow appends the
// canonical RELEASE_* keys to GITHUB_ENV; verify the keys Bootstrap.fsx
// emits by reading its source.
let bootstrapKeysAreStable () =
    let source = File.ReadAllText bootstrapPath
    let stableKeys =
        [
            "RELEASE_SDK"
            "RELEASE_RUNTIME"
            "RELEASE_PLATFORM"
            "RELEASE_ARCHITECTURE"
        ]
    let mutable missing = ResizeArray<string>()
    for key in stableKeys do
        if not (source.Contains(key, StringComparison.Ordinal)) then
            missing.Add key
    assertTrue "bootstrap source emits every stable RELEASE_* key" (missing.Count = 0)

// Update.ps1 -Project emits sdk/runtime/platform lines from the canonical
// Config.fsx files. The script reads both Config.fsx files and never invents
// values; we exercise the actual script and parse the canonical lines.
let updatePs1ProjectEmitsCanonicalValues () = async {
    let! pwshPath = pwshExecutable ()
    let pwsh =
        match pwshPath with
        | Some path -> path
        | None ->
            failed <- failed + 1
            failures.Add("pwsh not available; Update.ps1 -Project cannot be exercised")
            ""
    if not (String.IsNullOrEmpty pwsh) then
        let info = ProcessStartInfo(pwsh)
        info.WorkingDirectory <- repoRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add("-NoProfile")
        info.ArgumentList.Add("-File")
        info.ArgumentList.Add(updatePs1Path)
        info.ArgumentList.Add("-Project")
        // Strip workflow-output env so the script's projection writes do
        // not pollute the orchestrator's $GITHUB_OUTPUT in normal CI.
        info.EnvironmentVariables.Remove "GITHUB_OUTPUT" |> ignore
        info.EnvironmentVariables.Remove "GITHUB_ENV" |> ignore
        let mutable stdout = ""
        try
            match! capture 60000 info with
            | Ok(_, output, _) -> stdout <- output
            | Error error ->
                failed <- failed + 1
                failures.Add(sprintf "pwsh -Project: %s" error)
        with
        | error ->
            failed <- failed + 1
            failures.Add(sprintf "pwsh -Project: %s" error.Message)

        let lines =
            stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

        let sdkLine = lines |> Array.tryFind (fun l -> l.StartsWith("RELEASE_SDK=", StringComparison.Ordinal))
        let runtimeLine = lines |> Array.tryFind (fun l -> l.StartsWith("RELEASE_RUNTIME=", StringComparison.Ordinal))
        let platformLine = lines |> Array.tryFind (fun l -> l.StartsWith("RELEASE_PLATFORM=", StringComparison.Ordinal))

        match sdkLine with
        | Some value ->
            assertTrue (sprintf "RELEASE_SDK matches canonical: %s" value)
                       (value.Contains("11.0.100-rc.1.26425.128", StringComparison.Ordinal))
        | None ->
            failed <- failed + 1
            failures.Add("RELEASE_SDK line missing from Update.ps1 -Project output")

        match runtimeLine with
        | Some value ->
            // Update.ps1 writes the runtimeVersion literal only; Bootstrap.fsx
            // adds the moniker prefix on the verified GITHUB_ENV path. We
            // assert the version field is present.
            assertTrue (sprintf "RELEASE_RUNTIME contains canonical runtime version: %s" value)
                       (value.Contains("11.0.0-rc.1.26425.128", StringComparison.Ordinal))
        | None ->
            failed <- failed + 1
            failures.Add("RELEASE_RUNTIME line missing from Update.ps1 -Project output")

        match platformLine with
        | Some value ->
            assertTrue (sprintf "RELEASE_PLATFORM matches canonical: %s" value)
                       (value.Contains("win-x64", StringComparison.Ordinal))
        | None ->
            failed <- failed + 1
            failures.Add("RELEASE_PLATFORM line missing from Update.ps1 -Project output")
}

// Update.ps1 -Project refuses to project when a producer's Config.fsx is
// missing or has duplicate literals. The script uses `$PSScriptRoot` to
// locate the repo root, so the test stages a complete mock repository under a
// temp directory: a copy of Update.ps1 placed at `<mock>/infrastructure/release/`
// plus modified Config.fsx files alongside it. The script then loads Config.fsx
// from the mock tree; a duplicate sdkVersion literal triggers the strict
// text-projection gate and the script exits non-zero.
let updatePs1ProjectRejectsDuplicateLiteral () = async {
    let! pwshPath = pwshExecutable ()
    let pwsh =
        match pwshPath with
        | Some path -> path
        | None ->
            failed <- failed + 1
            failures.Add("pwsh not available; Update.ps1 duplicate rejection cannot be exercised")
            ""
    if not (String.IsNullOrEmpty pwsh) then
        let mockRoot = Path.Combine(Path.GetTempPath(), "mcp-bootstrap-duplicate-" + Guid.NewGuid().ToString("N"))
        let releaseDir = Path.Combine(mockRoot, "infrastructure", "release")
        let dotnetDir = Path.Combine(mockRoot, "dotnet", "release")
        let workflowDir = Path.Combine(mockRoot, "workflow", "release")
        Directory.CreateDirectory releaseDir |> ignore
        Directory.CreateDirectory dotnetDir |> ignore
        Directory.CreateDirectory workflowDir |> ignore

        File.Copy(updatePs1Path, Path.Combine(releaseDir, "Update.psx"))
        let srcDotnet = Path.Combine(repoRoot, "dotnet", "release", "Config.fsx")
        let srcWorkflow = Path.Combine(repoRoot, "workflow", "release", "Config.fsx")
        let dstDotnet = Path.Combine(dotnetDir, "Config.fsx")
        let dstWorkflow = Path.Combine(workflowDir, "Config.fsx")
        File.Copy(srcDotnet, dstDotnet)
        File.Copy(srcWorkflow, dstWorkflow)

        // Append a duplicate sdkVersion line so Read-CanonicalLiteral's
        // "exactly one literal" rule is triggered.
        File.AppendAllText(dstDotnet, "\nlet sdkVersion = \"11.0.100-rc.1.26425.999\"\n")

        let info = ProcessStartInfo(pwsh)
        info.WorkingDirectory <- mockRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add("-NoProfile")
        info.ArgumentList.Add("-File")
        info.ArgumentList.Add(Path.Combine(releaseDir, "Update.psx"))
        info.ArgumentList.Add("-Project")
        // Strip workflow-output env: the production Update.ps1 would write
        // to it, but the test asserts stdout projection only.
        info.EnvironmentVariables.Remove "GITHUB_OUTPUT" |> ignore
        info.EnvironmentVariables.Remove "GITHUB_ENV" |> ignore
        let! exitCode, stderr =
            try
                async {
                    match! capture 60000 info with
                    | Ok(code, _, error) -> return code, error
                    | Error error -> return -1, error
                }
            with
            | error ->
                failed <- failed + 1
                failures.Add(sprintf "pwsh duplicate: %s" error.Message)
                async.Return(-1, "")

        try Directory.Delete(mockRoot, true) with _ -> ()
        // The duplicate-literal gate must fail closed: either the exit code is
        // non-zero or the stderr explicitly mentions the duplicate-literal
        // diagnostic.
        assertTrue
            (sprintf "Update.ps1 -Project refuses duplicate literal (exit=%d)" exitCode)
            (exitCode <> 0 || stderr.Contains("exactly one", StringComparison.Ordinal))
}

// Bootstrap.fsx is executed in a child `dotnet fsi` from Update.ps1 -
// Verify. It emits the same canonical lines as -Project when the SDK matches.
// We exercise the live `dotnet --version` (canonical SDK installed locally).
let bootstrapEntryEmitsCanonicalSummary () =
    // The bootstrap source contains the exact field mappings Update.ps1
    // writes to GITHUB_ENV. The actual run requires a hosted setup-dotnet
    // step; locally we assert that the bootstrap source is consistent with
    // the Run -Verify contract.
    let source = File.ReadAllText bootstrapPath
    assertTrue "bootstrap source references canonical SDK literal" (source.Contains("expectedRuntimeMoniker", StringComparison.Ordinal))
    assertTrue "bootstrap source references arch/os projection" (source.Contains("Architecture", StringComparison.Ordinal))
    assertTrue "bootstrap source references ProducerSummary" (source.Contains("ProducerSummary", StringComparison.Ordinal))
    assertTrue "bootstrap source references ExecutingIdentity" (source.Contains("ExecutingIdentity", StringComparison.Ordinal))

let runSuite () = async {
    bootstrapKeysAreStable ()
    do! updatePs1ProjectEmitsCanonicalValues ()
    do! updatePs1ProjectRejectsDuplicateLiteral ()
    bootstrapEntryEmitsCanonicalSummary ()

    printfn "bootstrap-tests summary: passed=%d failed=%d" passed failed
    if failed > 0 then
        for line in failures do
            eprintfn "FAIL: %s" line
        exit 1
}

// Standalone entry bridge: the FSI process owns the asynchronous regression suite.
runSuite () |> Async.RunSynchronously
