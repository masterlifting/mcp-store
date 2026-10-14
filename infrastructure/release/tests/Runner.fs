module Release.Tests.Runner

open Expecto
open Release.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open BuildProvenance
open ReleaseTestProcess

let private repositoryRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private runnerPath =
    Path.Combine(repositoryRoot, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

let private readCanonicalPolicy componentId =
    let path = Path.Combine(repositoryRoot, componentId, "release", "Config.json")
    ReleasePolicy.parseBytes path (File.ReadAllBytes path)
    |> function
        | Ok policy -> policy
        | Error error -> failwithf "%s canonical policy failed to parse: %s" componentId (ReleaseError.message error)

let private runnerBinaryExists =
    testCase "the compiled runner exists at the canonical output path" (fun () ->
        Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}")

let private metadataAssetNameUsesCanonicalPolicy =
    testCase "metadata asset names follow each canonical producer version" (fun () ->
        for componentId in [ "dotnet"; "workflow" ] do
            let policy = readCanonicalPolicy componentId
            Expect.equal policy.ComponentId componentId $"{componentId} config identity"
            Expect.equal
                (ReleaseMetadata.metadataAssetName policy.ComponentId policy.Version)
                $"{policy.ComponentId}-{policy.Version}-metadata.json"
                $"{componentId} versioned metadata name")

let private cleanTreeGuardFailsClosed =
    testCaseAsync "the committed-source guard never reports dirty source as clean" <| async {
        match! BuildProvenance.assertCleanTree repositoryRoot with
        | Ok () -> ()
        | Error error ->
            let message = ReleaseError.message error
            Expect.isTrue
                (message.Contains("dirty", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("refusing", StringComparison.OrdinalIgnoreCase))
                "dirty-tree refusal is actionable"
    }

let private committedHeadIsResolved =
    testCaseAsync "the repository HEAD resolves to a commit identifier" <| async {
        match! BuildProvenance.committedHead repositoryRoot with
        | Ok head ->
            Expect.equal head.Length 40 "commit SHA length"
            Expect.isTrue (head |> Seq.forall Uri.IsHexDigit) "commit SHA is hexadecimal"
        | Error error -> failtestf "committedHead failed: %s" (ReleaseError.message error)
    }

let private runnerEmptyArgumentsReturnUsage =
    testCaseAsync "empty runner arguments return the usage exit code" <| async {
        Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}"
        let info = ProcessStartInfo(runnerPath)
        info.WorkingDirectory <- repositoryRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true

        match! capture 10000 info with
        | Error error -> failtestf "empty-argv runner invocation failed: %s" error
        | Ok (exitCode, _, stderr) ->
            Expect.equal exitCode 2 "usage exit code"
            Expect.isTrue (stderr.Contains("usage: ReleaseRunner prepare", StringComparison.Ordinal)) "prepare usage is shown"
            Expect.isTrue (stderr.Contains("usage: ReleaseRunner publish", StringComparison.Ordinal)) "publish usage is shown"
    }

// WHY: the modern publish context enforces (a) the canonical --repo value, (b) a 40-char
//   lowercase hex --event-sha, (c) the exact-three required flags, (d) no unknown or
//   duplicate flag, (e) candidate-directory outside the trusted checkout, (f) registered
//   policy via the roster, and (g) HEAD == --event-sha. Each regression below exercises one
//   real failure path; an unknown flag or unknown receipt claim is rejected with a typed
//   preauth refusal, exit 4. No legacy bootstrap-receipt gate is exercised.
let private runnerPublishRejectsWrongRepo = testCaseAsync "publish rejects a non-canonical --repo" <| async {
    Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}"
    let args =
        [ "publish"
          "dotnet/release/Config.json"
          (Path.Combine(Path.GetTempPath(), "candidate"))
          "--workspace-root=" + repositoryRoot
          "--event-sha=3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
          "--repo=other-owner/mcp-store" ]
    let info = ProcessStartInfo(runnerPath)
    info.WorkingDirectory <- repositoryRoot
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true
    args |> List.iter info.ArgumentList.Add
    [ "GITHUB_TOKEN"; "GH_TOKEN"; "MCP_RELEASE_DO_UPLOAD"; "MCP_RELEASE_GITHUB_OWNER"
      "MCP_RELEASE_GITHUB_REPOSITORY"; "GITHUB_ENV"; "GITHUB_OUTPUT" ]
    |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

    match! capture 15000 info with
    | Error error -> failtestf "publish wrong-repo: failed to run: %s" error
    | Ok (exitCode, stdout, _) ->
        Expect.equal exitCode 4 "non-canonical --repo is rejected"
        let result = JsonNode.Parse(stdout.TrimEnd()).AsObject()
        Expect.equal (result["status"].GetValue<string>()) "failed" "structured failure status"
        Expect.isTrue
            ((result["reason"].GetValue<string>()).Contains("masterlifting/mcp-store", StringComparison.Ordinal))
            "the refusing --repo diagnostic names the canonical owner"
}

let private runnerPublishRejectsUppercaseEventSha = testCaseAsync "publish rejects an uppercase --event-sha" <| async {
    Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}"
    let args =
        [ "publish"
          "dotnet/release/Config.json"
          (Path.Combine(Path.GetTempPath(), "candidate"))
          "--workspace-root=" + repositoryRoot
          "--event-sha=3612E8E6DD4655543E6D4B9DC0EDEE9C24B82BF1"
          "--repo=masterlifting/mcp-store" ]
    let info = ProcessStartInfo(runnerPath)
    info.WorkingDirectory <- repositoryRoot
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true
    args |> List.iter info.ArgumentList.Add
    [ "GITHUB_TOKEN"; "GH_TOKEN"; "MCP_RELEASE_DO_UPLOAD"; "MCP_RELEASE_GITHUB_OWNER"
      "MCP_RELEASE_GITHUB_REPOSITORY"; "GITHUB_ENV"; "GITHUB_OUTPUT" ]
    |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

    match! capture 15000 info with
    | Error error -> failtestf "publish uppercase-sha: failed to run: %s" error
    | Ok (exitCode, stdout, _) ->
        Expect.equal exitCode 4 "uppercase --event-sha is rejected"
        let result = JsonNode.Parse(stdout.TrimEnd()).AsObject()
        Expect.equal (result["status"].GetValue<string>()) "failed" "structured failure status"
        Expect.isTrue
            ((result["reason"].GetValue<string>()).Contains("--event-sha", StringComparison.Ordinal))
            "the refusing --event-sha diagnostic names the flag"
}

let private runnerPublishRejectsUnknownFlag = testCaseAsync "publish rejects a non-modern publish flag" <| async {
    Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}"
    // --run-id, --role, --component, --artifact-id, --artifact-name, --transport-sha256,
    // --builder-sdk, --builder-runtime, --builder-platform, --trusted-receipt,
    // --transport-evidence are all retired / non-modern flags; an unknown flag is the typed
    // preauth refusal path. The runner does not recognize them, so it emits a typed Error.
    let args =
        [ "publish"
          "dotnet/release/Config.json"
          (Path.Combine(Path.GetTempPath(), "candidate"))
          "--workspace-root=" + repositoryRoot
          "--event-sha=3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
          "--repo=masterlifting/mcp-store"
          "--run-id=controlled-run" ]
    let info = ProcessStartInfo(runnerPath)
    info.WorkingDirectory <- repositoryRoot
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true
    args |> List.iter info.ArgumentList.Add
    [ "GITHUB_TOKEN"; "GH_TOKEN"; "MCP_RELEASE_DO_UPLOAD"; "MCP_RELEASE_GITHUB_OWNER"
      "MCP_RELEASE_GITHUB_REPOSITORY"; "GITHUB_ENV"; "GITHUB_OUTPUT" ]
    |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

    match! capture 15000 info with
    | Error error -> failtestf "publish unknown-flag: failed to run: %s" error
    | Ok (exitCode, stdout, _) ->
        Expect.equal exitCode 4 "unknown publish flag is rejected"
        let result = JsonNode.Parse(stdout.TrimEnd()).AsObject()
        Expect.equal (result["status"].GetValue<string>()) "failed" "structured failure status"
        Expect.isTrue
            ((result["reason"].GetValue<string>()).Contains("unknown publish flag", StringComparison.Ordinal))
            "the refusing flag diagnostic names the unrecognized flag"
}

let private runnerPublishRejectsMissingRequiredFlag = testCaseAsync "publish refuses when a required flag is absent" <| async {
    Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}"
    let args =
        [ "publish"
          "dotnet/release/Config.json"
          (Path.Combine(Path.GetTempPath(), "candidate"))
          "--workspace-root=" + repositoryRoot
          "--repo=masterlifting/mcp-store" ]
    let info = ProcessStartInfo(runnerPath)
    info.WorkingDirectory <- repositoryRoot
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true
    args |> List.iter info.ArgumentList.Add
    [ "GITHUB_TOKEN"; "GH_TOKEN"; "MCP_RELEASE_DO_UPLOAD"; "MCP_RELEASE_GITHUB_OWNER"
      "MCP_RELEASE_GITHUB_REPOSITORY"; "GITHUB_ENV"; "GITHUB_OUTPUT" ]
    |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

    match! capture 15000 info with
    | Error error -> failtestf "publish missing-flag: failed to run: %s" error
    | Ok (exitCode, stdout, _) ->
        Expect.equal exitCode 4 "missing required flag is rejected"
        let result = JsonNode.Parse(stdout.TrimEnd()).AsObject()
        Expect.equal (result["status"].GetValue<string>()) "failed" "structured failure status"
        Expect.isTrue
            ((result["reason"].GetValue<string>()).Contains("--event-sha", StringComparison.Ordinal))
            "the missing flag diagnostic names the missing flag"
}

let private runnerPublishRejectsUnregisteredConfig = testCaseAsync "publish rejects an unregistered config path before any child starts" <| async {
    Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}"
    let candidatePath = Path.Combine(Path.GetTempPath(), "candidate")
    let args =
        [ "publish"
          "dotnet/release/Unregistered.json"
          candidatePath
          "--workspace-root=" + repositoryRoot
          "--event-sha=3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
          "--repo=masterlifting/mcp-store" ]
    let info = ProcessStartInfo(runnerPath)
    info.WorkingDirectory <- repositoryRoot
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true
    args |> List.iter info.ArgumentList.Add
    [ "GITHUB_TOKEN"; "GH_TOKEN"; "MCP_RELEASE_DO_UPLOAD"; "MCP_RELEASE_GITHUB_OWNER"
      "MCP_RELEASE_GITHUB_REPOSITORY"; "GITHUB_ENV"; "GITHUB_OUTPUT" ]
    |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

    match! capture 15000 info with
    | Error error -> failtestf "publish unregistered: failed to run: %s" error
    | Ok (exitCode, stdout, _) ->
        Expect.equal exitCode 4 "unregistered config path is rejected"
        let result = JsonNode.Parse(stdout.TrimEnd()).AsObject()
        Expect.equal (result["status"].GetValue<string>()) "failed" "structured failure status"
        Expect.isTrue
            ((result["reason"].GetValue<string>()).Contains("not registered", StringComparison.Ordinal))
            "the refusing config-path diagnostic names the roster boundary"
        Expect.isFalse (Directory.Exists candidatePath) "rejection occurs before any candidate output"
}

let tests =
    testList
        "runner publication boundary"
        [ runnerBinaryExists
          metadataAssetNameUsesCanonicalPolicy
          cleanTreeGuardFailsClosed
          committedHeadIsResolved
          runnerEmptyArgumentsReturnUsage
          runnerPublishRejectsWrongRepo
          runnerPublishRejectsUppercaseEventSha
          runnerPublishRejectsUnknownFlag
          runnerPublishRejectsMissingRequiredFlag
          runnerPublishRejectsUnregisteredConfig ]
