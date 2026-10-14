// role: focused authority-gate regression for the publish pipeline entrypoint
// scope: child-process probes invoked through Expecto CLIArgs so the host's environment is
//   never touched and no real credential values are read; the production pipeline is invoked
//   via reflection on the private `runPublishPipelineWithTransportFactory` whose 8-arg
//   signature is reused without modification.
// child probe contract:
//   * parent strips every publication credential name by NAME on the child ProcessStartInfo
//     (info.EnvironmentVariables.Remove) without reading the value and never prints it;
//   * parent sets ONLY dummy known env vars to opt the child into a specific scenario
//     (sentinel values, no real credentials);
//   * child executes Authority.probeCase through the normal Expecto runner so the Async
//     pipeline is handled without bridges of our own;
//   * parent captures via ReleaseTestProcess.capture (WaitForExitAsync + ReadToEndAsync
//     and a bounded cancellation); no Async.RunSynchronously / WaitForExit / GetAwaiter /
//     .Result / new blocking bridges in our code.

module Release.Tests.Authority

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

open Expecto
open Microsoft.FSharp.Core
open Microsoft.FSharp.Reflection

open BuildProvenance

type private CapturedRequest =
    { Method: string
      Url: string }

type private RejectingHandler () =
    inherit HttpMessageHandler ()
    let requests = ResizeArray<CapturedRequest> ()
    member _.Requests = requests.ToArray ()
    override _.SendAsync (request, _) = task {
        requests.Add ({ Method = request.Method.Method
                        Url = request.RequestUri.ToString () })
        return raise (InvalidOperationException (
            "the authority gate must prevent any HTTP traffic; observed "
            + string request.Method + " " + (request.RequestUri.ToString ())))
    }

type private ScriptedTagHandler () =
    inherit HttpMessageHandler ()
    let requests = ResizeArray<CapturedRequest> ()
    member _.Requests = requests.ToArray ()
    override _.SendAsync (request, _) = task {
        requests.Add ({ Method = request.Method.Method
                        Url = request.RequestUri.ToString () })
        let response = new HttpResponseMessage (HttpStatusCode.InternalServerError)
        response.Content <- new StringContent ("scripted transport refuses traffic", Encoding.UTF8, "text/plain")
        return response
    }

let private runnerAssembly =
    typeof<ReleasePolicy.PolicyProjection>.Assembly

let private publishCliType =
    runnerAssembly.GetType("ReleaseRunner+PublishCli", throwOnError = true)

let private runPublishPipelineMethod =
    let runnerType = runnerAssembly.GetType("ReleaseRunner", throwOnError = true)
    let method =
        runnerType.GetMethod
            ("runPublishPipelineWithTransportFactory",
             BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static)
    if isNull method then
        failwith "ReleaseRunner.runPublishPipelineWithTransportFactory is not reachable via reflection"
    method

let private sha256Hex (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

// WHY: the inner producer archive must use the canonical Deflate (method 8) the strict
//   ReleaseArchive.parse accepts; the outer candidate archive uses plain stored (method 0),
//   the standard GitHub ZIP shape read permissively by ZipArchive.Read. One helper, two shapes.
let private zipBytes (level: CompressionLevel) (entries: (string * byte[]) list) =
    use output = new MemoryStream()
    do
        use archive = new ZipArchive(output, ZipArchiveMode.Create, true)
        for name, payload in entries do
            let entry = archive.CreateEntry(name, level)
            use stream = entry.Open()
            stream.Write(payload, 0, payload.Length)
    output.ToArray()

type private Fixture =
    { Policy: ReleasePolicy.PolicyProjection
      ArchiveName: string
      OuterBytes: byte[]
      OuterSha: string
      Snapshot: ReleaseHandoff.CandidateArchiveSnapshot
      Owner: string
      Name: string
      MetadataLocalPath: string
      RunId: string
      Attempt: string
      CommitSha: string
      OuterSourcePath: string }

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private canonicalPolicy componentId =
    let path = Path.Combine(repositoryRoot, componentId, "release", "Config.fsx")
    match ReleasePolicy.parseBytes path (File.ReadAllBytes path) with
    | Ok policy -> policy
    | Error error -> failwithf "%s canonical policy failed to parse: %s" componentId (ReleaseError.message error)

let private buildOuter (policy: ReleasePolicy.PolicyProjection) (revision: string) =
    let payloads =
        policy.PublishedFiles
        |> List.map (fun name -> name, Encoding.UTF8.GetBytes $"authority-fixture:{policy.ComponentId}:{name}")
        |> fun files -> files @ [ "NOTICE.txt", Encoding.UTF8.GetBytes "authority fixture payload" ]

    let manifest = JsonObject()
    manifest["schemaVersion"] <- JsonValue.Create 1
    manifest["id"] <- JsonValue.Create policy.ComponentId
    manifest["version"] <- JsonValue.Create policy.Version
    // The manifest and verified event context must describe the same committed source.
    manifest["revision"] <- JsonValue.Create revision
    manifest["sdk"] <- JsonValue.Create policy.SdkVersion
    manifest["entryDll"] <- JsonValue.Create policy.EntryDll
    manifest["archive"] <- JsonValue.Create policy.ArchiveName

    let files = JsonArray()
    for name, payload in payloads do
        if name <> "NOTICE.txt" then
            let entry = JsonObject()
            entry["path"] <- JsonValue.Create name
            entry["sha256"] <- JsonValue.Create(sha256Hex payload)
            files.Add entry
    manifest["files"] <- files

    let archiveFiles = JsonArray()
    for name in policy.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] do
        archiveFiles.Add(JsonValue.Create name)
    manifest["archiveFiles"] <- archiveFiles

    let manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString())
    let innerBytes =
        zipBytes CompressionLevel.Optimal
            (payloads
             @ [ "distribution.json", manifestBytes ])
    // Inner archive and standalone manifest are computed first; preparation.json binds the
    // FOUR known inner-archive / manifest hash-and-size fields before the outer bytes exist,
    // so the receipt never references a stale digest of the outer ZIP it sits inside.
    let preparationNode = JsonObject()
    preparationNode["schemaVersion"] <- JsonValue.Create 1
    preparationNode["innerArchiveSha256"] <- JsonValue.Create(sha256Hex innerBytes)
    preparationNode["innerArchiveSize"] <- JsonValue.Create(int64 innerBytes.Length)
    preparationNode["manifestSha256"] <- JsonValue.Create(sha256Hex manifestBytes)
    preparationNode["manifestSize"] <- JsonValue.Create(int64 manifestBytes.Length)
    let preparationBytes = Encoding.UTF8.GetBytes(preparationNode.ToJsonString())
    let outerBytes =
        zipBytes CompressionLevel.NoCompression
            [ policy.ArchiveName, innerBytes
              "distribution.json", manifestBytes
              "preparation.json", preparationBytes ]
    outerBytes, innerBytes, manifestBytes

let private buildFixture (componentId: string) (commitSha: string) : Fixture =
    let policy = canonicalPolicy componentId
    let outerBytes, _, manifestBytes = buildOuter policy commitSha
    let outerSha = sha256Hex outerBytes

    let expected : ReleaseHandoff.ExpectedMetadata =
        { ArtifactId = "controlled-artifact"
          ArtifactName = componentId + "-candidate"
          Role = "candidate"
          Component = componentId
          Repository = "masterlifting/mcp-store"
          RunId = "controlled-run"
          Attempt = "1"
          Sha = commitSha }
    let observed : ReleaseHandoff.ObservedPlatformMetadata =
        { ArtifactId = expected.ArtifactId
          ArtifactName = expected.ArtifactName
          Role = expected.Role
          Component = expected.Component
          Repository = expected.Repository
          RunId = expected.RunId
          Attempt = expected.Attempt
          Sha = expected.Sha }

    let outerDigest =
        match ReleaseHandoff.makeOuterDigest outerSha with
        | Ok value -> value
        | Error error -> failwithf "fixture outer digest rejected: %s" (ReleaseError.message error)

    let evidence : ReleaseHandoff.ArtifactEvidence =
        { Expected = expected
          Observed = observed
          ActualOuterDigest = outerDigest
          TransportSourcePath = "candidate-artifact.zip"
          TransportLength = int64 outerBytes.Length
          RawTransportBytes = outerBytes }

    let snapshot =
        match ReleaseHandoff.readRawTransport "candidate-artifact.zip" outerBytes evidence policy with
        | Ok value -> value
        | Error error -> failwithf "%s readRawTransport rejected the fixture: %s" componentId (ReleaseError.message error)

    let metadataAssetName = ReleaseMetadata.metadataAssetName policy.ComponentId policy.Version
    let metadataLocalPath = Path.Combine(Path.GetTempPath(), $"authority-probe-{componentId}", metadataAssetName)

    { Policy = policy
      ArchiveName = policy.ArchiveName
      OuterBytes = outerBytes
      OuterSha = outerSha
      Snapshot = snapshot
      Owner = "masterlifting"
      Name = "mcp-store"
      MetadataLocalPath = metadataLocalPath
      RunId = "controlled-run"
      Attempt = "1"
      CommitSha = commitSha
      OuterSourcePath = "candidate-artifact.zip" }

let private makePublishCli (f: Fixture) =
    let closurePath = Path.Combine(repositoryRoot, "infrastructure", "release", "Publisher.json")
    let candidateTransportPath = Path.Combine(Path.GetTempPath(), $"authority-probe-{f.Policy.ComponentId}", f.OuterSourcePath)
    let trustedReceiptPath = Path.Combine(Path.GetTempPath(), $"authority-probe-{f.Policy.ComponentId}", "trusted-receipt.json")
    let transportEvidencePath = Path.Combine(Path.GetTempPath(), $"authority-probe-{f.Policy.ComponentId}", "transport-evidence.json")

    let overrideMap : Map<string, string> =
        Map.ofList
            [ "ClosurePath", closurePath
              "CandidateTransportPath", candidateTransportPath
              "Repo", "masterlifting/mcp-store"
              "RunId", f.RunId
              "Attempt", f.Attempt
              "EventSha", f.CommitSha
              "Role", "candidate"
              "ProducerComponent", f.Policy.ComponentId
              "ArtifactId", "controlled-artifact"
              "ArtifactName", f.Policy.ComponentId + "-candidate"
              "TransportSha256", f.OuterSha
              "BuilderSdk", f.Policy.SdkVersion
              "BuilderRuntime", f.Policy.RuntimeVersion
              "BuilderPlatform", f.Policy.Platform
              "WorkspaceRoot", repositoryRoot
              "TrustedReceiptPath", trustedReceiptPath
              "TransportEvidence", transportEvidencePath ]

    let values : obj[] =
        [| box overrideMap.["ClosurePath"]
           box overrideMap.["CandidateTransportPath"]
           box overrideMap.["Repo"]
           box overrideMap.["RunId"]
           box overrideMap.["Attempt"]
           box overrideMap.["EventSha"]
           box overrideMap.["Role"]
           box overrideMap.["ProducerComponent"]
           box overrideMap.["ArtifactId"]
           box overrideMap.["ArtifactName"]
           box overrideMap.["TransportSha256"]
           box overrideMap.["BuilderSdk"]
           box overrideMap.["BuilderRuntime"]
           box overrideMap.["BuilderPlatform"]
           box overrideMap.["WorkspaceRoot"]
           box overrideMap.["TrustedReceiptPath"]
           box overrideMap.["TransportEvidence"]
           box (Option<string>.None) |]

    FSharpValue.MakeRecord(publishCliType, values, BindingFlags.NonPublic)

// The publication credential names listed here are the only entries that are removed BY
// NAME from a child's ProcessStartInfo before it starts. The parent never reads the value,
// so the host's real credentials (if any) never enter our test code or stdout.
let private publicationAuthorityNames =
    [| "MCP_RELEASE_DO_UPLOAD"
       "GITHUB_TOKEN"
       "GH_TOKEN"
       "MCP_RELEASE_GITHUB_OWNER"
       "MCP_RELEASE_GITHUB_REPOSITORY" |]

// Additional host-side names that may carry credentials in CI; removed by NAME only.
let private additionalHostCredentialNames =
    [| "MCP_RELEASE_PROVENANCE_TOKENS"
       "GH_HOST"
       "GITHUB_API_TOKEN"
       "ACTIONS_RUNTIME_TOKEN"
       "MCP_RELEASE_NO_HTTPS"
       "DOTNET_SYSTEM_NET_HTTP_ALLOW_UNENCRYPTED" |]

// Dummy values the parent writes after stripping; none of them are real credentials.
let private dummyDoUpload = "true"
let private dummyTokenValue = "dummy-published-probe"
let private canonicalRepoOwner = "masterlifting"
let private canonicalRepoName = "mcp-store"

// In-process pipeline driver used inside the OWNED CHILD. It never reads the host's
// environment; it relies entirely on the ProcessStartInfo the parent pre-populated, and
// all values it observes come from production code paths.
let private runPipelineWithHandler
    (f: Fixture)
    (mkHandler: unit -> HttpMessageHandler)
    : Async<int * string * CapturedRequest array * int> = async {
    use handler = mkHandler ()
    let factoryCounter = ResizeArray<int> ()
    let factory () : ReleasePublish.HttpTransport =
        factoryCounter.Add (1)
        let configure (client: HttpClient) =
            client.Timeout <- TimeSpan.FromSeconds 10.0
            client.DefaultRequestHeaders.Accept.Add (MediaTypeWithQualityHeaderValue ("application/vnd.github+json"))
            client.DefaultRequestHeaders.UserAgent.ParseAdd ("mcp-store-authority-tests")
            client.DefaultRequestHeaders.Add ("X-GitHub-Api-Version", "2022-11-28")
            client
        { CreateApiClient = fun () -> configure (new HttpClient (handler, disposeHandler = false))
          CreateUploadClient = fun () -> configure (new HttpClient (handler, disposeHandler = false))
          CreateRedirectClient = fun () -> configure (new HttpClient (handler, disposeHandler = false)) }
    let factoryObj : obj = box factory
    let cliObj = makePublishCli f
    // 8-arg production signature: deferred factory, PublishCli, PolicyProjection, Snapshot,
    // archiveName, metadataLocalPath, repositoryOwner, repositoryName. Preparation is no
    // longer a separate pipeline arg; the verified Snapshot carries the parsed receipt.
    let args : obj[] =
        [| factoryObj
           cliObj
           box f.Policy
           box f.Snapshot
           box f.ArchiveName
           box f.MetadataLocalPath
           box f.Owner
           box f.Name |]
    let invoked : Async<int> =
        runPublishPipelineMethod.Invoke (null, args) :?> Async<int>
    let! exitCode = invoked
    let recorded =
        match handler with
        | :? RejectingHandler as h -> h.Requests
        | :? ScriptedTagHandler as h -> h.Requests
        | _ -> [||]
    let firstUrl =
        match recorded |> Array.tryHead with
        | Some first -> first.Url
        | None -> ""
    return (factoryCounter.Count, firstUrl, recorded, exitCode)
}

// We write the probe counters through the underlying stdout stream so Expecto's
// ANSI formatter does not interleave with our key=value lines; the parent reads the
// captured child stdout through ReleaseTestProcess.capture and parses these lines.
let private emitProbeResult
    (mode: string)
    (componentId: string)
    (factoryCalls: int)
    (firstUrl: string)
    (recorded: CapturedRequest array)
    (exitCode: int) =
    let stream = Console.OpenStandardOutput ()
    use writer = new StreamWriter (stream, Encoding.UTF8, 1024, true)
    writer.WriteLine ($"mode={mode}")
    writer.WriteLine ($"component={componentId}")
    writer.WriteLine ($"factory={factoryCalls}")
    writer.WriteLine ($"requests={recorded.Length}")
    writer.WriteLine ($"firstUrl={firstUrl}")
    writer.WriteLine (
        if Array.isEmpty recorded then "method="
        else $"method={recorded.[0].Method}"
    )
    writer.WriteLine ($"exit={exitCode}")
    writer.Flush ()

let private resolveCommitHead () = async {
    match! BuildProvenance.committedHead repositoryRoot with
    | Ok head -> return head
    | Error error -> return failwithf "failed to resolve committed HEAD: %s" (ReleaseError.message error)
}

// internal entry invoked from Main.fs --authority-probe; runs through normal Expecto
// so the Async pipeline is handled by Expecto without our own bridges.
let internal probeCase (mode: string) (componentId: string) : Test =
    testCaseAsync $"authority probe {mode} {componentId}" <| async {
        let! commitHead = resolveCommitHead ()
        let fixture = buildFixture componentId commitHead
        match mode with
        | "reject-unauthorized"
        | "reject-dummy-do-upload"
        | "reject-authorized-missing-token" ->
            let! factoryCalls, firstUrl, recorded, exitCode =
                runPipelineWithHandler fixture (fun () -> new RejectingHandler () :> HttpMessageHandler)
            emitProbeResult mode componentId factoryCalls firstUrl recorded exitCode
        | "authorized-fixed-tag" ->
            let! factoryCalls, firstUrl, recorded, exitCode =
                runPipelineWithHandler fixture (fun () -> new ScriptedTagHandler () :> HttpMessageHandler)
            emitProbeResult mode componentId factoryCalls firstUrl recorded exitCode
        | _ ->
            Expect.isTrue
                false
                $"authority probe mode {mode} is not registered for component {componentId}"
    }

// Compiled test-binary path used as the child probe binary. Release matches the
// configuration the rest of the test suite is built in.
let private testBinaryPath =
    Path.Combine
        ([| __SOURCE_DIRECTORY__
            "bin"
            "Release"
            "net11.0"
            "Release.Tests.exe" |])

// Strip every publication authority name BY NAME from the child ProcessStartInfo without
// reading the value; extra host credential names are likewise removed.
let private stripCredentialNames (info: ProcessStartInfo) : int =
    let mutable removed = 0
    for name in publicationAuthorityNames do
        if info.EnvironmentVariables.ContainsKey name then
            info.EnvironmentVariables.Remove name |> ignore
            removed <- removed + 1
    for name in additionalHostCredentialNames do
        if info.EnvironmentVariables.ContainsKey name then
            info.EnvironmentVariables.Remove name |> ignore
            removed <- removed + 1
    removed

// Sentinel dummy values written after the strip; no real credentials are ever set.
let private dummyOverrides (mode: string) : Map<string, string> =
    match mode with
    | "reject-dummy-do-upload" ->
        Map.ofList [ "MCP_RELEASE_DO_UPLOAD", "dummy" ]
    | "reject-authorized-missing-token" ->
        Map.ofList [ "MCP_RELEASE_DO_UPLOAD", dummyDoUpload ]
    | "authorized-fixed-tag" ->
        Map.ofList
            [ "MCP_RELEASE_DO_UPLOAD", dummyDoUpload
              "GITHUB_TOKEN", dummyTokenValue
              "MCP_RELEASE_GITHUB_OWNER", canonicalRepoOwner
              "MCP_RELEASE_GITHUB_REPOSITORY", canonicalRepoName ]
    | _ -> Map.empty

let private runChildProbe (mode: string) (componentId: string) : Async<Result<string, string>> =
    async {
        Expect.isTrue (File.Exists testBinaryPath) $"child probe binary exists at {testBinaryPath}"
        let info = ProcessStartInfo ()
        info.FileName <- testBinaryPath
        info.WorkingDirectory <- repositoryRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        info.ArgumentList.Add ("--authority-probe")
        info.ArgumentList.Add (mode)
        info.ArgumentList.Add (componentId)
        let _stripped = stripCredentialNames info
        for kv in dummyOverrides mode do
            info.EnvironmentVariables.[kv.Key] <- kv.Value
        match! ReleaseTestProcess.capture 60000 info with
        | Ok (exitCode, stdout, stderr) ->
            if exitCode = 0 then return Ok stdout
            else return Error (sprintf "child exit=%d stderr=%s" exitCode stderr)
        | Error error -> return Error error
    }

type private ProbeResult =
    { Mode: string
      Component: string
      FactoryCount: int
      RequestCount: int
      FirstUrl: string
      FirstMethod: string
      ExitCode: int }

let private parseProbeResult (componentId: string) (stdout: string) : ProbeResult =
    let mutable modeValue = ""
    let mutable componentValue = componentId
    let mutable factoryRef = -1
    let mutable requestsRef = -1
    let mutable urlRef = ""
    let mutable methodRef = ""
    let mutable exitRef = -1
    for rawLine in stdout.Split ('\n') do
        let line = rawLine.Trim ()
        if not (String.IsNullOrEmpty line) then
            let parts = line.Split ('=', 2)
            if parts.Length = 2 then
                match parts.[0] with
                | "mode" -> modeValue <- parts.[1]
                | "component" -> componentValue <- parts.[1]
                | "factory" ->
                    match Int32.TryParse (parts.[1]) with
                    | true, v -> factoryRef <- v
                    | _ -> ()
                | "requests" ->
                    match Int32.TryParse (parts.[1]) with
                    | true, v -> requestsRef <- v
                    | _ -> ()
                | "firstUrl" -> urlRef <- parts.[1]
                | "method" -> methodRef <- parts.[1]
                | "exit" ->
                    match Int32.TryParse (parts.[1]) with
                    | true, v -> exitRef <- v
                    | _ -> ()
                | _ -> ()
    { Mode = modeValue
      Component = componentValue
      FactoryCount = factoryRef
      RequestCount = requestsRef
      FirstUrl = urlRef
      FirstMethod = methodRef
      ExitCode = exitRef }

// 3 reject scenarios per producer × 2 producers = 6 guards.
let private rejectScenarios =
    [ "reject-unauthorized"
      "reject-dummy-do-upload"
      "reject-authorized-missing-token" ]

let private producers = [ "dotnet"; "workflow" ]

// each unresolved child probe increments the matching counter, the assertion expects every
// counter to remain zero so a guard regression is caught even when the inner message differs.
let private unauthorizedGuards =
    testCaseAsync
        "authority gate rejects unauthorized candidates before any HTTP for both producers"
        <| async {
            let factoryFailures = ResizeArray<string> ()
            let requestFailures = ResizeArray<string> ()
            let exitFailures = ResizeArray<string> ()

            for mode in rejectScenarios do
                for componentId in producers do
                    match! runChildProbe mode componentId with
                    | Error error ->
                        factoryFailures.Add ($"{mode}/{componentId}: child probe failed -> {error}")
                    | Ok stdout ->
                        let result = parseProbeResult componentId stdout
                        if result.FactoryCount <> 0 then
                            factoryFailures.Add (
                                $"{mode}/{componentId}: transport factory must not be invoked (factory calls={result.FactoryCount})")
                        if result.RequestCount <> 0 then
                            requestFailures.Add (
                                $"{mode}/{componentId}: zero HTTP requests required (recorded={result.RequestCount})")
                        if result.ExitCode <> 4 then
                            exitFailures.Add (
                                $"{mode}/{componentId}: authority guard exits with code 4 (got {result.ExitCode})")

            Expect.isEmpty
                factoryFailures
                (if Seq.isEmpty factoryFailures then "factory guard"
                 else String.concat "; " factoryFailures)
            Expect.isEmpty
                requestFailures
                (if Seq.isEmpty requestFailures then "request guard"
                 else String.concat "; " requestFailures)
            Expect.isEmpty
                exitFailures
                (if Seq.isEmpty exitFailures then "exit guard"
                 else String.concat "; " exitFailures)
        }

// one authorized controlled observation per producer = 2 tag probes; the production source
// at Publish.fs line apiUrl $"repos/{Owner}/{Repository}/releases/tags/{Tag}" with
// Tag = policy.ComponentId in Runner.fs constructs the canonical URL with no v prefix.
let private authorizedFixedTag =
    testCaseAsync
        "authorized transport invokes the canonical /releases/tags/<producer> URL without a v prefix for both producers"
        <| async {
            let factoryFailures = ResizeArray<string> ()
            let requestFailures = ResizeArray<string> ()
            let urlFailures = ResizeArray<string> ()
            let methodFailures = ResizeArray<string> ()
            let exitFailures = ResizeArray<string> ()

            for componentId in producers do
                match! runChildProbe "authorized-fixed-tag" componentId with
                | Error error ->
                    factoryFailures.Add ($"authorized/{componentId}: child probe failed -> {error}")
                | Ok stdout ->
                    let result = parseProbeResult componentId stdout
                    if result.FactoryCount < 1 then
                        factoryFailures.Add (
                            $"authorized/{componentId}: transport factory must be invoked exactly once (calls={result.FactoryCount})")
                    if result.RequestCount < 1 then
                        requestFailures.Add (
                            $"authorized/{componentId}: at least one HTTP request required (recorded={result.RequestCount})")
                    // Remote observation failures are transaction failures, not pre-contract rejections.
                    if result.ExitCode <> 1 then
                        exitFailures.Add (
                            $"authorized/{componentId}: transaction FAILED on scripted HTTP 500 exits with code 1 (got {result.ExitCode})")
                    let canonical =
                        sprintf "/repos/%s/%s/releases/tags/%s"
                            canonicalRepoOwner canonicalRepoName componentId
                    let urlOk =
                        not (String.IsNullOrEmpty result.FirstUrl)
                        && result.FirstUrl.Contains (canonical, StringComparison.OrdinalIgnoreCase)
                        && not (result.FirstUrl.Contains ("releases/tags/v", StringComparison.OrdinalIgnoreCase))
                        && not (result.FirstUrl.Contains ("/v" + componentId, StringComparison.OrdinalIgnoreCase))
                    if not urlOk then
                        urlFailures.Add (
                            $"{componentId}: canonical URL missing or contains v prefix (url={result.FirstUrl}, expected contains {canonical})")
                    if result.FirstMethod = "POST" then
                        methodFailures.Add (
                            $"{componentId}: scripted transport must stop before any POST (method={result.FirstMethod})")

            Expect.isEmpty
                factoryFailures
                (if Seq.isEmpty factoryFailures then "authorized factory count"
                 else String.concat "; " factoryFailures)
            Expect.isEmpty
                requestFailures
                (if Seq.isEmpty requestFailures then "authorized request count"
                 else String.concat "; " requestFailures)
            Expect.isEmpty
                urlFailures
                (if Seq.isEmpty urlFailures then "canonical URL pattern"
                 else String.concat "; " urlFailures)
            Expect.isEmpty
                methodFailures
                (if Seq.isEmpty methodFailures then "no POST before scripted refusal"
                 else String.concat "; " methodFailures)
            Expect.isEmpty
                exitFailures
                (if Seq.isEmpty exitFailures then "authorized exit code"
                 else String.concat "; " exitFailures)
        }

let tests =
    testList
        "release authority child-probe regressions"
        [ unauthorizedGuards
          authorizedFixedTag ]
