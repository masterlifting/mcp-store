module Release.Tests.Runner

open Expecto
open Release.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance
open ReleaseCompare
open ReleaseMetadata
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

let private repoRootForRunner =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private runnerPath =
    Path.Combine(repoRootForRunner, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

let private contractJson () =
    let node = JsonObject()
    node.["componentId"] <- JsonValue.Create "dotnet"
    node.["version"] <- JsonValue.Create "1.0.4"
    node.["archiveName"] <- JsonValue.Create "v1.0.4.zip"
    node.["assetUri"] <- JsonValue.Create "https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.4.zip"
    let allowlist = JsonArray()
    for f in [ "FSharp.Core.dll"; "Mcp.Dotnet.deps.json"; "Mcp.Dotnet.dll"; "Mcp.Dotnet.runtimeconfig.json"; "NOTICE.txt"; "distribution.json" ] do
        allowlist.Add(JsonValue.Create f)
    node.["entryAllowlist"] <- allowlist
    node.ToJsonString(JsonSerializerOptions())

let private contractJsonFor (componentId: string) =
    let node = JsonNode.Parse(contractJson()).AsObject()
    node["componentId"] <- JsonValue.Create componentId
    node["assetUri"] <- JsonValue.Create $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/v1.0.4.zip"
    node.ToJsonString(JsonSerializerOptions())

let runnerBinaryExists () =
    assertTrue (sprintf "runner binary exists at %s" runnerPath) (File.Exists runnerPath)
let contractJsonShape () =
    let json = contractJson ()
    let node = JsonNode.Parse json
    let obj = node.AsObject()
    assertEqual "componentId" "dotnet" (obj.["componentId"].GetValue<string>())
    assertEqual "version" "1.0.4" (obj.["version"].GetValue<string>())
    assertEqual "archiveName" "v1.0.4.zip" (obj.["archiveName"].GetValue<string>())
    let allowlist =
        obj.["entryAllowlist"].AsArray()
        |> Seq.map (fun n -> n.GetValue<string>())
        |> Seq.toList
        |> List.sort
    assertEqual
        "entryAllowlist is the producer allowlist"
        [ "FSharp.Core.dll"; "Mcp.Dotnet.deps.json"; "Mcp.Dotnet.dll"; "Mcp.Dotnet.runtimeconfig.json"; "NOTICE.txt"; "distribution.json" ]
        allowlist
let metadataAssetNameDeterministic () =
    let asset = ReleaseMetadata.metadataAssetName "dotnet" "1.0.4"
    assertEqual "metadata asset name encoding" "dotnet-1.0.4-metadata.json" asset
    let alt = ReleaseMetadata.metadataAssetName "workflow" "1.0.4"
    assertEqual "different component yields different asset name" "workflow-1.0.4-metadata.json" alt
let cleanTreeGuardContract () = async {
    let root = repoRootForRunner
    match! BuildProvenance.assertCleanTree root with
    | Ok () ->
        passed <- passed + 1
    | Error error ->
        let message = BuildProvenance.ReleaseError.message error
        assertTrue "clean-tree guard is fail-closed (typed ReleaseError)"
            (message.Contains("dirty", StringComparison.Ordinal) || message.Contains("refusing", StringComparison.Ordinal))
}
let manifestRevisionGuardContract () = async {
    let root = repoRootForRunner
    match! BuildProvenance.committedHead root with
    | Ok head ->
        assertTrue "HEAD resolves to a 40-hex commit SHA"
            (head.Length = 40 && head |> Seq.forall (fun c -> Uri.IsHexDigit c))
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "committedHead: %s" (BuildProvenance.ReleaseError.message error))
}

let runnerRejectsUnenrolledTransactionComponent () = async {
    if not (File.Exists runnerPath) then
        failed <- failed + 1
        failures.Add(sprintf "runner-enrollment prerequisite missing: %s" runnerPath)
    else
        let fixtureRoot = Fixture.newTempDir "mcp-runner-enrollment-fixtures"
        try
            let info = ProcessStartInfo(runnerPath)
            info.WorkingDirectory <- repoRootForRunner
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.CreateNoWindow <- true
            info.ArgumentList.Add(Path.Combine(fixtureRoot, "missing-manifest.json"))
            info.ArgumentList.Add(Path.Combine(fixtureRoot, "missing-archive.zip"))
            info.ArgumentList.Add(contractJsonFor "telegram")
            [ "GITHUB_TOKEN"; "GITHUB_ENV"; "GITHUB_OUTPUT"; "MCP_RELEASE_DO_UPLOAD"
              "MCP_RELEASE_GITHUB_OWNER"; "MCP_RELEASE_GITHUB_REPOSITORY" ]
            |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

            match! capture 10000 info with
            | Error error ->
                failed <- failed + 1
                failures.Add(sprintf "runner unenrolled component invocation failed: %s" error)
            | Ok(exitCode, stdout, _) ->
                assertEqual "unenrolled CLI component fails before transaction" 1 exitCode
                match JsonNode.Parse(stdout.TrimEnd()).AsObject() with
                | null ->
                    failed <- failed + 1
                    failures.Add("unenrolled CLI component did not return structured JSON")
                | result ->
                    assertEqual "unenrolled CLI status" "failed" (result["status"].GetValue<string>())
                    let reason = result["reason"].GetValue<string>()
                    assertTrue "unenrolled CLI reason names roster rejection" (reason.Contains("unsupported componentId 'telegram'", StringComparison.Ordinal))
                    assertTrue "unenrolled CLI omits published archive evidence" (not (result.ContainsKey "publishedArchiveSha256"))
        finally
            Fixture.safeDeleteDir fixtureRoot
}
let runnerEmptyArgvExitsWithUsage () = async {
    if not (File.Exists runnerPath) then
        failed <- failed + 1
        failures.Add(sprintf "runner-empty-argv prerequisite missing: %s" runnerPath)
    else
        let info = ProcessStartInfo(runnerPath)
        info.WorkingDirectory <- repoRootForRunner
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        match! capture 10000 info with
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "runner-empty-argv-times-out: %s" error)
        | Ok(exitCode, _, stderr) ->
            assertEqual "runner empty-argv exitcode is 2" 2 exitCode
            let errText = stderr.TrimEnd()
            assertTrue "runner empty-argv stderr starts with 'usage: ReleaseRunner'"
                (errText.StartsWith("usage: ReleaseRunner", StringComparison.Ordinal))
}
let runnerDirtyTreeEmitsStructuredFailureJson () = async {
    if not (File.Exists runnerPath) then
        failed <- failed + 1
        failures.Add(sprintf "runner-dirty-tree prerequisite missing: %s" runnerPath)
    else
        let fixtureRoot = Fixture.newTempDir "mcp-runner-dirty-fixtures"
        let manifestPath = Path.Combine(fixtureRoot, "distribution.json")
        let archivePath = Path.Combine(fixtureRoot, "v1.0.4.zip")
        let manifestText =
            "{\"schemaVersion\":1,\"id\":\"dotnet\",\"version\":\"1.0.4\","
            + "\"revision\":\"3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1\","
            + "\"sdk\":\"11.0.100-rc.1.26425.128\",\"entryDll\":\"Mcp.Dotnet.dll\","
            + "\"archive\":\"v1.0.4.zip\",\"files\":[],\"archiveFiles\":[]}"
        File.WriteAllText(manifestPath, manifestText)
        Fixture.writeRevisionVariantArchive archivePath (fixtureRoot + "/manifest-for-archive.json") Fixture.canonicalEvaluatedRevision
        let markerName = sprintf "test-dirty-marker-%s" (Guid.NewGuid().ToString("N"))
        let markerPath = Path.Combine(repoRootForRunner, markerName)
        File.WriteAllText(markerPath, "owned-by-runner-fsx-test")
        try
            let info = ProcessStartInfo(runnerPath)
            info.WorkingDirectory <- repoRootForRunner
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.CreateNoWindow <- true
            info.ArgumentList.Add(manifestPath)
            info.ArgumentList.Add(archivePath)
            info.ArgumentList.Add(contractJson ())
            info.EnvironmentVariables.Remove "GITHUB_TOKEN" |> ignore
            info.EnvironmentVariables.Remove "GITHUB_ENV" |> ignore
            info.EnvironmentVariables.Remove "GITHUB_OUTPUT" |> ignore
            info.EnvironmentVariables.Remove "MCP_RELEASE_DO_UPLOAD" |> ignore
            match! capture 15000 info with
            | Error error ->
                failed <- failed + 1
                failures.Add(sprintf "runner-dirty-tree: invocation failed: %s" error)
            | Ok(exitCode, stdout, _) ->
                assertEqual "runner dirty-tree exitcode is 4" 4 exitCode
                let outText = stdout.TrimEnd()
                match JsonNode.Parse(outText).AsObject() with
                | null ->
                    failed <- failed + 1
                    failures.Add(sprintf "runner-dirty-tree: stdout is not a JSON object (stdout=%s)" outText)
                | obj ->
                    let getStr key = if obj.ContainsKey key then obj.[key].GetValue<string>() else ""
                    assertEqual "dirty-tree status is failed" "failed" (getStr "status")
                    assertEqual "dirty-tree componentId is dotnet" "dotnet" (getStr "componentId")
                    assertEqual "dirty-tree version is 1.0.4" "1.0.4" (getStr "version")
                    assertEqual "dirty-tree archiveName is v1.0.4.zip" "v1.0.4.zip" (getStr "archiveName")
                    let assetUriValue = getStr "assetUri"
                    assertTrue "dirty-tree assetUri is populated" (assetUriValue.Contains("masterlifting/mcp-store", StringComparison.Ordinal))
                    let reasonValue = getStr "reason"
                    assertTrue "dirty-tree reason names the dirty guard"
                        (reasonValue.Contains("dirty", StringComparison.Ordinal) || reasonValue.Contains("refusing", StringComparison.Ordinal) || reasonValue.Contains("clean", StringComparison.Ordinal))
                    let assertNullOrMissing key =
                        if obj.ContainsKey key then
                            match obj.[key] with
                            | null -> ()
                            | node ->
                                let kind = node.GetValueKind()
                                assertTrue (sprintf "dirty-tree %s must be null or missing" key) (kind = JsonValueKind.Null)
                        else ()
                    assertNullOrMissing "publishedArchiveSha256"
                    assertNullOrMissing "publishedManifestSha256"
                    assertNullOrMissing "publishedRevision"
                    assertNullOrMissing "publishedMetadataSha256"
                    assertNullOrMissing "bumpedVersion"
        finally
            try File.Delete markerPath with _ -> ()
            try Directory.Delete(fixtureRoot, true) with _ -> ()

}

let runSuite () = async {
    runnerBinaryExists ()
    contractJsonShape ()
    metadataAssetNameDeterministic ()
    do! cleanTreeGuardContract ()
    do! manifestRevisionGuardContract ()
    do! runnerRejectsUnenrolledTransactionComponent ()
    do! runnerEmptyArgvExitsWithUsage ()
    do! runnerDirtyTreeEmitsStructuredFailureJson ()

    printfn "runner-tests summary: passed=%d failed=%d" passed failed
}


let tests =
    testCaseAsync "Runner legacy regression assertions" <| async {
        do! runSuite ()
        Expect.equal 0 failed (String.concat Environment.NewLine failures)
    }
