// role: test entrypoint
// Runner contract: empty-argv exit 2 with usage line; dirty tree -> structured JSON on stdout, exit 4, all published* keys null.
// Assertions parse the binary's stdout JSON, not just exit codes.
#load "../../Provenance.fsx"
#load "../Compare.fsx"
#load "../Metadata.fsx"
#load "Fixture.fsx"

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance
open ReleaseCompare
open ReleaseMetadata

let mutable passed = 0
let mutable failed = 0
let mutable skipped = 0
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

// runner binary is the workflow matrix entry; presence is the precondition.
let runnerBinaryExists () =
    assertTrue (sprintf "runner binary exists at %s" runnerPath) (File.Exists runnerPath)

// contract JSON shape mirrors the per-component Release.fsx builder.
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

// metadata asset name is deterministic from (componentId, version).
let metadataAssetNameDeterministic () =
    let asset = ReleaseMetadata.metadataAssetName "dotnet" "1.0.4"
    assertEqual "metadata asset name encoding" "dotnet-1.0.4-metadata.json" asset
    let alt = ReleaseMetadata.metadataAssetName "workflow" "1.0.4"
    assertEqual "different component yields different asset name" "workflow-1.0.4-metadata.json" alt

// clean-tree guard surfaces a typed ReleaseError when the working tree is dirty.
let cleanTreeGuardContract () =
    let root = repoRootForRunner
    match BuildProvenance.assertCleanTree root |> Async.RunSynchronously with
    | Ok () ->
        passed <- passed + 1
    | Error error ->
        let message = BuildProvenance.ReleaseError.message error
        assertTrue "clean-tree guard is fail-closed (typed ReleaseError)"
            (message.Contains("dirty", StringComparison.Ordinal) || message.Contains("refusing", StringComparison.Ordinal))

// manifest revision guard contract reads HEAD as 40-hex SHA.
let manifestRevisionGuardContract () =
    let root = repoRootForRunner
    match BuildProvenance.committedHead root |> Async.RunSynchronously with
    | Ok head ->
        assertTrue "HEAD resolves to a 40-hex commit SHA"
            (head.Length = 40 && head |> Seq.forall (fun c -> Uri.IsHexDigit c))
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "committedHead: %s" (BuildProvenance.ReleaseError.message error))

// empty-argv child probe must exit 2 and emit the documented usage line.
let runnerEmptyArgvExitsWithUsage () =
    if not (File.Exists runnerPath) then
        skipped <- skipped + 1
        failures.Add(sprintf "runner-empty-argv SKIP: %s missing" runnerPath)
    else
        let info = ProcessStartInfo(runnerPath)
        info.WorkingDirectory <- repoRootForRunner
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        use proc = Process.Start info
        let stdout = StringBuilder()
        let stderr = StringBuilder()
        proc.OutputDataReceived.Add(fun args ->
            if not (isNull args.Data) then stdout.AppendLine(args.Data) |> ignore)
        proc.ErrorDataReceived.Add(fun args ->
            if not (isNull args.Data) then stderr.AppendLine(args.Data) |> ignore)
        proc.BeginOutputReadLine()
        proc.BeginErrorReadLine()
        let exited = proc.WaitForExit(10000)
        if not exited then
            try proc.Kill true with _ -> ()
            failed <- failed + 1
            failures.Add("runner-empty-argv-times-out: hung >10s on empty argv")
        else
            assertEqual "runner empty-argv exitcode is 2" 2 proc.ExitCode
            let errText = stderr.ToString().TrimEnd()
            assertTrue "runner empty-argv stderr starts with 'usage: ReleaseRunner'"
                (errText.StartsWith("usage: ReleaseRunner", StringComparison.Ordinal))

// dirty-tree: create a uniquely-owned, unignored temp marker inside the repo
// so the clean-tree guard sees a dirty tree; run the binary with the
// canonical manifest/archive/contract args; assert exit=4 plus parsed JSON
// status=failed, required config context fields present, every published*
// key absent, reason names the dirty guard. finally removes ONLY the marker
// the test created, never any other file.
let runnerDirtyTreeEmitsStructuredFailureJson () =
    if not (File.Exists runnerPath) then
        skipped <- skipped + 1
        failures.Add(sprintf "runner-dirty-tree SKIP: %s missing" runnerPath)
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
            use proc = Process.Start info
            let stdout = StringBuilder()
            let stderr = StringBuilder()
            proc.OutputDataReceived.Add(fun args -> if not (isNull args.Data) then stdout.AppendLine args.Data |> ignore)
            proc.ErrorDataReceived.Add(fun args -> if not (isNull args.Data) then stderr.AppendLine args.Data |> ignore)
            proc.BeginOutputReadLine()
            proc.BeginErrorReadLine()
            let exited = proc.WaitForExit(15000)
            if not exited then
                try proc.Kill true with _ -> ()
                failed <- failed + 1
                failures.Add("runner-dirty-tree: invocation timed out >15s")
            else
                assertEqual "runner dirty-tree exitcode is 4" 4 proc.ExitCode
                let outText = stdout.ToString().TrimEnd()
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

runnerBinaryExists ()
contractJsonShape ()
metadataAssetNameDeterministic ()
cleanTreeGuardContract ()
manifestRevisionGuardContract ()
runnerEmptyArgvExitsWithUsage ()
runnerDirtyTreeEmitsStructuredFailureJson ()

printfn "runner-tests summary: passed=%d failed=%d skipped=%d" passed failed skipped
if failed > 0 then
    for line in failures do
        eprintfn "FAIL: %s" line
    exit 1
