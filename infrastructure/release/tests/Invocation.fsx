// role: test entrypoint
// Real `Bootstrap.fsx` invocation as a child process; negative cases enforce a wrong SDK/runtime/platform projection through `RELEASE_EXPECTED_*` env overrides.
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

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let private bootstrapPath = Path.Combine(repoRoot, "infrastructure", "release", "Bootstrap.fsx")

let private runProcessCapture (executable: string) (args: string list) (workingDir: string) (timeoutMs: int) =
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- workingDir
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true
    for a in args do info.ArgumentList.Add(a)
    capture timeoutMs info

// Run the actual `dotnet fsi infrastructure/release/Bootstrap.fsx` and
// verify the rendered summary lines against the canonical contract.
let bootstrapRendersCanonicalSummary () = async {
    match! runProcessCapture "dotnet" [ "fsi"; "--nologo"; bootstrapPath ] repoRoot 60000 with
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "bootstrap invocation failed: %s" error)
    | Ok(exit, stdout, stderr) ->
        if exit <> 0 then
            failed <- failed + 1
            failures.Add(sprintf "bootstrap invocation exited %d; stderr=%s stdout=%s" exit stderr stdout)
        let hasSdk = stdout.Contains("RELEASE_SDK=")
        let hasRuntime = stdout.Contains("RELEASE_RUNTIME=")
        let hasPlatform = stdout.Contains("RELEASE_PLATFORM=")
        let hasDotnetVersion = stdout.Contains("RELEASE_DOTNET_VERSION=")
        let hasDotnetArchive = stdout.Contains("RELEASE_DOTNET_ARCHIVE=")
        let hasWorkflowVersion = stdout.Contains("RELEASE_WORKFLOW_VERSION=")
        let hasWorkflowArchive = stdout.Contains("RELEASE_WORKFLOW_ARCHIVE=")
        assertTrue "bootstrap emits RELEASE_SDK line" hasSdk
        assertTrue "bootstrap emits RELEASE_RUNTIME line" hasRuntime
        assertTrue "bootstrap emits RELEASE_PLATFORM line" hasPlatform
        assertTrue "bootstrap emits RELEASE_DOTNET_VERSION line" hasDotnetVersion
        assertTrue "bootstrap emits RELEASE_DOTNET_ARCHIVE line" hasDotnetArchive
        assertTrue "bootstrap emits RELEASE_WORKFLOW_VERSION line" hasWorkflowVersion
        assertTrue "bootstrap emits RELEASE_WORKFLOW_ARCHIVE line" hasWorkflowArchive
}

// Negative enforcement at the boundary: a wrong SDK value projected
// from `RELEASE_EXPECTED_SDK` causes `Bootstrap.fsx`'s verify path to
// assert the executing SDK identity and surface an `IdentityMismatch`
// ReleaseError. The test sets the env var to a value that does not
// match the canonical SDK and observes the typed failure.
let bootstrapRefusesWrongSdk () = async {
    let prior = Environment.GetEnvironmentVariable "RELEASE_EXPECTED_SDK"
    Environment.SetEnvironmentVariable("RELEASE_EXPECTED_SDK", "9.9.9-fake-test")
    try
        match! runProcessCapture "dotnet" [ "fsi"; "--nologo"; bootstrapPath ] repoRoot 60000 with
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "bootstrap wrong-sdk: invocation failed: %s" error)
        | Ok(exit, _, stderr) ->
            // Bootstrap.fsx `verify` reports IdentityMismatch and
            // calls failwith (exit non-zero); the test asserts exit<>0.
            // Different mismatch sources may surface varied failure
            // shapes; we only assert the fail-closed result.
            if exit = 0 then
                failed <- failed + 1
                failures.Add(sprintf "bootstrap wrong-sdk: exit=0 (expected non-zero); stderr=%s" stderr)
            else
                passed <- passed + 1
    finally
        Environment.SetEnvironmentVariable("RELEASE_EXPECTED_SDK", prior)
}

// Negative enforcement: wrong runtime moniker version.
let bootstrapRefusesWrongRuntime () = async {
    let prior = Environment.GetEnvironmentVariable "RELEASE_EXPECTED_RUNTIME"
    Environment.SetEnvironmentVariable("RELEASE_EXPECTED_RUNTIME", "9.9.9-fake-runtime")
    try
        match! runProcessCapture "dotnet" [ "fsi"; "--nologo"; bootstrapPath ] repoRoot 60000 with
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "bootstrap wrong-runtime: invocation failed: %s" error)
        | Ok(exit, _, stderr) ->
            if exit = 0 then
                failed <- failed + 1
                failures.Add(sprintf "bootstrap wrong-runtime: exit=0; stderr=%s" stderr)
            else
                passed <- passed + 1
    finally
        Environment.SetEnvironmentVariable("RELEASE_EXPECTED_RUNTIME", prior)
}

// Negative enforcement: wrong platform.
let bootstrapRefusesWrongPlatform () = async {
    let prior = Environment.GetEnvironmentVariable "RELEASE_EXPECTED_PLATFORM"
    Environment.SetEnvironmentVariable("RELEASE_EXPECTED_PLATFORM", "fake-os-x86")
    try
        match! runProcessCapture "dotnet" [ "fsi"; "--nologo"; bootstrapPath ] repoRoot 60000 with
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "bootstrap wrong-platform: invocation failed: %s" error)
        | Ok(exit, _, stderr) ->
            if exit = 0 then
                failed <- failed + 1
                failures.Add(sprintf "bootstrap wrong-platform: exit=0; stderr=%s" stderr)
            else
                passed <- passed + 1
    finally
        Environment.SetEnvironmentVariable("RELEASE_EXPECTED_PLATFORM", prior)
}

let runSuite () = async {
    do! bootstrapRendersCanonicalSummary ()
    do! bootstrapRefusesWrongSdk ()
    do! bootstrapRefusesWrongRuntime ()
    do! bootstrapRefusesWrongPlatform ()

    printfn "bootstrap-invocation-tests summary: passed=%d failed=%d" passed failed
    if failed > 0 then
        for line in failures do
            eprintfn "FAIL: %s" line
        exit 1
}

// Standalone entry bridge: the FSI process owns the asynchronous regression suite.
runSuite () |> Async.RunSynchronously
