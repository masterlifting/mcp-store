// role: pre-SDK configuration projection regression gate
// WHY: the projected component list is sourced through Update.ps1 -Project and the canonical
//   Config.json parser; the toolchain (SDK, runtime, platform) is no longer carried through an
//   FSI bootstrap. Each regression below exercises one Update.ps1 output channel and one
//   Config.json field so the canonical projection path is observable without a publisher child.
module Release.Tests.Invocation

open Expecto
open Release.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
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
let private updatePs1Path = Path.Combine(repoRoot, "infrastructure", "release", "Update.ps1")

let private runUpdate (parameters: string list) =
    async {
        let outputPath = Path.Combine(Path.GetTempPath(), "mcp-invocation-output-" + Guid.NewGuid().ToString("N") + ".txt")
        let info = ProcessStartInfo("pwsh")
        info.WorkingDirectory <- repoRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add("-NoProfile")
        info.ArgumentList.Add("-File")
        info.ArgumentList.Add(updatePs1Path)
        parameters |> List.iter info.ArgumentList.Add
        [ "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD"; "GITHUB_ENV" ]
        |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)
        info.EnvironmentVariables["GITHUB_OUTPUT"] <- outputPath
        let! result = ReleaseTestProcess.capture 60000 info
        let body = if File.Exists outputPath then File.ReadAllText outputPath else ""
        return result, body
    }

// WHY: Update.ps1 -Project must echo the canonical SDK literal exactly once; the canonical
//   Config.json no longer carries the executing F# bootstrap so we read the SDK literal from
//   the production Update.ps1 output, not from the obsolete FSI bootstrap.
let updateProjectEmitsCanonicalSdkLiteral () = async {
    let! result, output = runUpdate [ "-Project" ]
    match result with
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "Update.ps1 -Project invocation failed: %s" error)
    | Ok (exitCode, stdout, stderr) ->
        if exitCode <> 0 then
            failed <- failed + 1
            failures.Add(sprintf "Update.ps1 -Project exited %d: stdout=%s stderr=%s" exitCode stdout stderr)
        let combined = stdout + stderr + "\n" + output
        let sdkLine =
            combined.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryFind (fun l -> l.StartsWith("RELEASE_SDK=", StringComparison.Ordinal))
        match sdkLine with
        | Some value ->
            assertTrue
                (sprintf "RELEASE_SDK matches canonical SDK literal: %s" value)
                (value.Contains("11.0.100-rc.1.26425.128", StringComparison.Ordinal))
        | None ->
            failed <- failed + 1
            failures.Add(sprintf "RELEASE_SDK line missing from Update.ps1 -Project output: %s" combined)
}

// WHY: the canonical 9-field Config.json supplies the executing version directly; a malformed
//   unknown field rejection confirms the projection is sourced from the strict JSON policy,
//   not from an obsolete F# interpreter.
let policyRejectsUnknownField () =
    let tempPath = Path.Combine(Path.GetTempPath(), "mcp-invocation-unknown-" + Guid.NewGuid().ToString("N") + ".json")
    let source =
        "{\"componentId\":\"dotnet\",\"version\":\"1.0.4\",\"entryDll\":\"Mcp.Dotnet.dll\"," +
        "\"sdkVersion\":\"11.0.100-rc.1.26425.128\",\"runtimeVersion\":\"11.0.0-rc.1.26425.128\"," +
        "\"platform\":\"win-x64\",\"archiveName\":\"v1.0.4.zip\"," +
        "\"assetUri\":\"https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.4.zip\"," +
        "\"publishedFiles\":[\"Mcp.Dotnet.dll\"],\"rogueField\":\"rogue\"}"
    File.WriteAllText(tempPath, source)
    match ReleasePolicy.parseBytes tempPath (File.ReadAllBytes tempPath) with
    | Error (MalformedArtifact (_, detail)) ->
        assertTrue
            (sprintf "unknown Config.json field is rejected: %s" detail)
            (detail.Contains("unknown field", StringComparison.Ordinal))
    | Error other ->
        failtestf "expected malformed rejection, got %A" other
    | Ok projection ->
        failtestf "unknown field accepted: %A" projection

// WHY: a malformed UTF-8 / NUL byte Config.json is rejected by the strict parser; this is
//   the only projection regression that does not require Update.ps1 to be on disk.
let policyRejectsEmbeddedNulByte () =
    let tempPath = Path.Combine(Path.GetTempPath(), "mcp-invocation-nul-" + Guid.NewGuid().ToString("N") + ".json")
    let bytes = Encoding.UTF8.GetBytes("{\"componentId\":\u0000\"dotnet\"}")
    match ReleasePolicy.parseBytes tempPath bytes with
    | Error (MalformedArtifact (_, detail)) ->
        assertTrue
            (sprintf "embedded NUL is rejected: %s" detail)
            (detail.Contains("NUL", StringComparison.Ordinal))
    | Error other ->
        failtestf "expected malformed rejection, got %A" other
    | Ok projection ->
        failtestf "embedded NUL accepted: %A" projection

let runSuite () = async {
    do! updateProjectEmitsCanonicalSdkLiteral ()
    policyRejectsUnknownField ()
    policyRejectsEmbeddedNulByte ()

    printfn "invocation-projections summary: passed=%d failed=%d" passed failed
}

let tests =
    testCaseAsync "Update.ps1 / strict-JSON projection regressions" <| async {
        do! runSuite ()
        Expect.equal 0 failed (String.concat Environment.NewLine failures)
    }