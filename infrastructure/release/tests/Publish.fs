module Release.Tests.Publish

open Expecto
open Release.Tests

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open BuildProvenance
open ReleaseCompare
open ReleaseMetadata
open ReleasePublish

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

let private withEnv name (value: string option) (action: unit -> Async<'a>) : Async<'a> =
    async {
        let prior = Environment.GetEnvironmentVariable name
        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)
        try return! action ()
        finally Environment.SetEnvironmentVariable(name, prior)
    }

let private withUploadFlag (value: string option) (action: unit -> Async<'a>) : Async<'a> =
    withEnv "MCP_RELEASE_DO_UPLOAD" value action

let private classify (name: string) (action: unit -> Async<Result<'a, ReleaseError>>) : Async<Result<Result<'a, ReleaseError>, string>> =
    async {
        try
            let! outcome = action ()
            return Ok outcome
        with
        | :? InvalidOperationException as ex when ex.Message.Contains "This operation is not supported for a relative URI" ->
            failed <- failed + 1
            failures.Add(sprintf "%s: production-bug-relative-uri-host" name)
            return Error "production-bug-relative-uri-host"
        | :? InvalidOperationException as ex ->
            failed <- failed + 1
            failures.Add(sprintf "%s: production-bug-InvalidOperationException: %s" name ex.Message)
            return Error "production-bug"
        | :? HttpRequestException ->
            return Ok (Error (FileFailure("host", "evil.test", "guard rejected")))
        | :? OperationCanceledException ->
            return Ok (Error (FileFailure("timeout", "scripted", "HANG")))
        | ex ->
            failed <- failed + 1
            failures.Add(sprintf "%s: unhandled exception: %s :: %s" name (ex.GetType().Name) ex.Message)
            return Error "unhandled"
    }

type Scripted =
    | Reply of status: int * headers: (string * string) list * body: string
    | ReplyBytes of status: int * headers: (string * string) list * body: byte[]
    | Hang

type Captured =
    { Method: string
      Url: string
      Host: string
      Authorization: string
      Accept: string
      ContentType: string
      ContentLength: int64
      Body: byte[] }

let private applyHeaders (response: HttpResponseMessage) (headers: (string * string) list) =
    for k, v in headers do
        if String.Equals(k, "Location", StringComparison.OrdinalIgnoreCase) then
            response.Headers.Location <- Uri(v)
        else
            response.Headers.TryAddWithoutValidation(k, v) |> ignore

type RecordingHandler(allowHost: string) =
    inherit HttpMessageHandler()
    let queue = Queue<Scripted>()
    let captured = ResizeArray<Captured>()

    member _.Queue response = queue.Enqueue response

    member _.Captured = captured.ToArray()

    override _.SendAsync (request, cancellationToken) = task {
        let allowed = [ "api.github.com"; "uploads.github.com"; "release-assets.githubusercontent.com" ]
        let requestHost = request.RequestUri.Host
        if not (allowed |> List.exists (fun h -> requestHost.EndsWith(h, StringComparison.OrdinalIgnoreCase))) then
            raise (new HttpRequestException(sprintf "RecordingHandler host guard: '%s' is not in allowlist" requestHost))

        let body : byte[] =
            if isNull request.Content then [||]
            else
                let! bytes = request.Content.ReadAsByteArrayAsync(cancellationToken)
                bytes
        let contentType =
            if isNull request.Content || isNull request.Content.Headers then ""
            else
                match request.Content.Headers.ContentType with
                | null -> ""
                | v -> v.ToString()
        let auth =
            match request.Headers.Authorization with
            | null -> ""
            | v -> v.ToString()
        let accept =
            if request.Headers.Accept.Count = 0 then ""
            else String.concat ", " [ for m in request.Headers.Accept -> m.ToString() ]
        captured.Add(
            { Method = request.Method.Method
              Url = request.RequestUri.ToString()
              Host = requestHost
              Authorization = auth
              Accept = accept
              ContentType = contentType
              ContentLength = int64 body.Length
              Body = body })

        if queue.Count = 0 then
            raise (new InvalidOperationException("RecordingHandler: queue empty"))
        let next = queue.Dequeue()
        let mkResponse (status: int) (headers: (string * string) list) (setContent: HttpResponseMessage -> unit) =
            let r = new HttpResponseMessage()
            r.StatusCode <- enum<HttpStatusCode> status
            setContent r
            r.RequestMessage <- request
            for k, v in headers do
                if String.Equals(k, "Location", StringComparison.OrdinalIgnoreCase) then
                    r.Headers.Location <- Uri(v)
                else
                    r.Headers.TryAddWithoutValidation(k, v) |> ignore
            r
        let reply : HttpResponseMessage =
            match next with
            | Reply (status, headers, bodyText) ->
                mkResponse status headers (fun r -> r.Content <- new StringContent(bodyText))
            | ReplyBytes (status, headers, bodyBytes) ->
                mkResponse status headers (fun r -> r.Content <- new ByteArrayContent(bodyBytes))
            | Hang -> raise (new TaskCanceledException("RecordingHandler: scripted HANG"))
        return reply
    }

let private setApiHeaders (client: HttpClient) =
    client.DefaultRequestHeaders.Accept.Clear()
    client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
    client.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-store-release")
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")

let private transportFromHandler (handler: HttpMessageHandler) : HttpTransport =
    let createApi () =
        let c = new HttpClient(handler, disposeHandler = false)
        c.BaseAddress <- Uri "https://api.github.com/"
        c.Timeout <- TimeSpan.FromSeconds 10.0
        setApiHeaders c
        c
    let createUpload () =
        let c = new HttpClient(handler, disposeHandler = false)
        c.Timeout <- TimeSpan.FromSeconds 10.0
        setApiHeaders c
        c
    let createRedirect () = new HttpClient(handler, disposeHandler = false)
    { CreateApiClient = createApi
      CreateUploadClient = createUpload
      CreateRedirectClient = createRedirect }

let private tempRoot = Path.Combine(Path.GetTempPath(), "mcp-release-publish-tests-" + Guid.NewGuid().ToString("N"))
let private writeCanonicalCandidate (label: string) : string * string =
    let archivePath = Path.Combine(tempRoot, label + ".zip")
    let manifestPath = Path.Combine(tempRoot, label + ".json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let sha = Fixture.sha256Hex (File.ReadAllBytes archivePath)
    archivePath, sha

let private canonicalPayload (manifestBytes: byte[]) =
    [
        "FSharp.Core.dll", Encoding.UTF8.GetBytes "fsharp-core-fixture"
        "Mcp.Dotnet.deps.json", Encoding.UTF8.GetBytes "{\"runtimeOptions\":{\"tfm\":\"net11.0\"}}"
        "Mcp.Dotnet.dll", Encoding.UTF8.GetBytes "dotnet-dll-fixture"
        "Mcp.Dotnet.runtimeconfig.json", Encoding.UTF8.GetBytes "{\"runtimeOptions\":{\"tfm\":\"net11.0\"}}"
        "NOTICE.txt", Encoding.UTF8.GetBytes "mcp-store notice"
        "distribution.json", manifestBytes
    ]

let private manifestJson (revision: string) =
    let manifest = JsonObject()
    manifest.["schemaVersion"] <- JsonValue.Create 1
    manifest.["id"] <- JsonValue.Create "dotnet"
    manifest.["version"] <- JsonValue.Create "1.0.4"
    manifest.["revision"] <- JsonValue.Create revision
    manifest.["sdk"] <- JsonValue.Create "11.0.100-rc.1.26425.128"
    manifest.["entryDll"] <- JsonValue.Create "Mcp.Dotnet.dll"
    manifest.["archive"] <- JsonValue.Create "v1.0.4.zip"
    manifest.["files"] <- JsonArray()
    manifest.["archiveFiles"] <- JsonArray()
    manifest.ToJsonString(JsonSerializerOptions())

let private sha256Hex (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

let private releaseJson (releaseId: int64 option) (immutable: bool) (assets: (string * string * int64) list) =
    let doc = JsonObject()
    match releaseId with
    | Some id -> doc.["id"] <- JsonValue.Create id
    | None -> ()
    doc.["immutable"] <- JsonValue.Create immutable
    let assetsNode = JsonArray()
    for name, digest, id in assets do
        let asset = JsonObject()
        asset.["name"] <- JsonValue.Create name
        asset.["digest"] <- JsonValue.Create digest
        asset.["id"] <- JsonValue.Create id
        assetsNode.Add(asset)
    doc.["assets"] <- assetsNode
    doc.ToJsonString(JsonSerializerOptions())

let private projectionFor (archiveSha: string) (manifestSha: string) : ProducerMetadataProjection =
    makeProjection
        "dotnet" "1.0.4"
        "https://example.test/releases/download/dotnet/v1.0.4.zip"
        "dotnet-1.0.4-metadata.json"
        archiveSha manifestSha "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
        (Some "11.0.100-rc.1.26425.128")
        (Some "11.0.0-rc.1.26425.128")
        (Some "win-x64")

let private uploadMetadataUsesProducerOwnedToolchains () = async {
    let producers =
        [ "dotnet", "1.2.3", "dotnet-sdk", "dotnet-runtime", "win-x64"
          "workflow", "4.5.6", "workflow-sdk", "workflow-runtime", "linux-x64" ]

    for componentId, version, sdk, runtime, platform in producers do
        use handler = new RecordingHandler("api.github.com")
        let transport = transportFromHandler handler
        let archiveName = $"v{version}.zip"
        let assetUri = $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/{archiveName}"
        let projection =
            makeProjection
                componentId version assetUri archiveName
                (String.replicate 64 "1") (String.replicate 64 "2") (String.replicate 40 "3")
                (Some sdk) (Some runtime) (Some platform)
        let metadataBytes = Encoding.UTF8.GetBytes(render projection)
        let metadataDigest = sha256Hex metadataBytes
        let assetName = ReleaseMetadata.metadataAssetName componentId version
        let locator = { Owner = "owner"; Repository = "repo"; Tag = componentId }
        let auth = { Token = "sentinel" }

        handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
        handler.Queue(Reply(201, [], "{}"))
        handler.Queue(Reply(200, [], releaseJson (Some 99L) false [ assetName, "sha256:" + metadataDigest, 1L ]))
        handler.Queue(ReplyBytes(200, [], metadataBytes))

        let uploadWithoutAmbientIdentity () =
            withEnv "RELEASE_EXPECTED_SDK" None (fun _ ->
                withEnv "RELEASE_EXPECTED_RUNTIME" None (fun _ ->
                    withEnv "RELEASE_EXPECTED_PLATFORM" None (fun _ ->
                        uploadMetadata auth locator projection transport)))

        let! outcome = withUploadFlag (Some "true") (fun _ -> uploadWithoutAmbientIdentity ())

        match outcome with
        | Ok digest -> assertEqual $"{componentId} metadata upload returns verified bytes" metadataDigest digest
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "%s metadata upload with producer-owned toolchain failed: %s" componentId (ReleaseError.message error))

        let posts = handler.Captured |> Array.filter (fun request -> request.Method = "POST")
        assertEqual $"{componentId} metadata transport issues one POST" 1 posts.Length
        match posts |> Array.tryHead with
        | None -> ()
        | Some request ->
            assertTrue $"{componentId} metadata request uses its versioned asset" (request.Url.Contains(assetName, StringComparison.Ordinal))
            match ReleaseMetadata.parse (Encoding.UTF8.GetString request.Body) with
            | Error error ->
                failed <- failed + 1
                failures.Add(sprintf "%s posted metadata could not be parsed: %s" componentId (ReleaseError.message error))
            | Ok parsed ->
                assertEqual $"{componentId} metadata body preserves its SDK" (Some sdk) parsed.ProducerSdk
                assertEqual $"{componentId} metadata body preserves its runtime" (Some runtime) parsed.ProducerRuntime
                assertEqual $"{componentId} metadata body preserves its platform" (Some platform) parsed.ProducerPlatform
}
let observerUnsafeRedirectRejected () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    handler.Queue(Reply(302, ["Location", "https://evil.test/redirected/path"], ""))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerUnsafeRedirectRejected" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Error error) ->
            let m = ReleaseError.message error
            assertTrue "unsafe redirect error mentions redirect/host"
                (m.Contains("redirect", StringComparison.Ordinal) || m.Contains("not a github.com", StringComparison.Ordinal))
        | Ok (Ok state) -> failed <- failed + 1; failures.Add(sprintf "observerUnsafeRedirect: expected Error got %A" state)
        | _ -> ()
    })
    assertEqual "unsafe redirect issues exactly one request" 1 (Array.length handler.Captured)
}
let observerSafeRedirectObservedAsset () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    handler.Queue(Reply(302, ["Location", "https://api.github.com/repos/x/y/releases/tags/redirected"], ""))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerSafeRedirectObservedAsset" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Ok Absent) -> passed <- passed + 1
        | Ok (Ok other) -> failed <- failed + 1; failures.Add(sprintf "observerSafeRedirect: expected Absent got %A" other)
        | Ok (Error error) -> failed <- failed + 1; failures.Add(sprintf "observerSafeRedirect: %s" (ReleaseError.message error))
        | _ -> ()
    })
    assertEqual "safe redirect issues lookup + follow-up request" 2 (Array.length handler.Captured)
}
let observerSafe301ChainFollowedToRelease () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    handler.Queue(Reply(301, ["Location", "https://api.github.com/repos/x/y/releases/tags/redirected-301"], ""))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerSafe301ChainFollowedToRelease" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Ok Absent) -> passed <- passed + 1
        | Ok (Ok other) -> failed <- failed + 1; failures.Add(sprintf "301-chain: expected Absent got %A" other)
        | Ok (Error error) -> failed <- failed + 1; failures.Add(sprintf "301-chain: %s" (ReleaseError.message error))
        | _ -> ()
    })
    let captured = handler.Captured
    assertEqual "301 safe chain issues 2 requests" 2 (Array.length captured)
    let apiRequests = captured |> Array.filter (fun c -> c.Host = "api.github.com")
    assertEqual "every api.github.com request carried the token" 2 (Array.length apiRequests)
    assertTrue "first request carried Authorization"
        (apiRequests.[0].Authorization.Contains("sentinel", StringComparison.Ordinal))
    assertTrue "follow-up request carried Authorization"
        (apiRequests.[1].Authorization.Contains("sentinel", StringComparison.Ordinal))
}
let uploadCandidateIdempotentNoPost () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "idem"
    let archiveBytes = File.ReadAllBytes archivePath
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false [("v1.0.4.zip", "sha256:" + candidateSha, 1L)]))
    handler.Queue(ReplyBytes(200, [], archiveBytes))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        match! classify "uploadCandidateIdempotentNoPost" (fun _ ->
            uploadCandidate auth locator "v1.0.4.zip" archivePath candidateSha transport) with
        | Ok (Ok digest) -> assertEqual "idempotent returns the existing digest" candidateSha digest
        | Ok (Error error) -> failed <- failed + 1; failures.Add(sprintf "idempotent upload: %s" (ReleaseError.message error))
        | _ -> ()
    })
    let posts = handler.Captured |> Array.filter (fun c -> c.Method = "POST") |> Array.length
    assertEqual "idempotent issues zero POSTs" 0 posts
    let gets = handler.Captured |> Array.filter (fun c -> c.Method = "GET") |> Array.length
    assertEqual "idempotent issues GET release + GET asset" 2 gets
}
let uploadCandidateRefusesOverwrite () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "refuse"
    let wrongBytes = Encoding.UTF8.GetBytes (String('z', 1024))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false [("v1.0.4.zip", "sha256:" + Fixture.sha256Hex wrongBytes, 1L)]))
    handler.Queue(ReplyBytes(200, [], wrongBytes))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        match! classify "uploadCandidateRefusesOverwrite" (fun _ ->
            uploadCandidate auth locator "v1.0.4.zip" archivePath candidateSha transport) with
        | Ok (Error error) -> assertTrue "overwrite refusal is typed" ((ReleaseError.message error).Contains("refusing to overwrite", StringComparison.Ordinal))
        | Ok (Ok value) -> failed <- failed + 1; failures.Add(sprintf "overwrite: expected Error got %s" value)
        | _ -> ()
    })
    let posts = handler.Captured |> Array.filter (fun c -> c.Method = "POST") |> Array.length
    assertEqual "refuse-overwrite issues zero POSTs" 0 posts
}
let uploadCandidateSuccessPath () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "success"
    let archiveBytes = File.ReadAllBytes archivePath
    let uploadResponse =
        let doc = JsonObject()
        doc.["digest"] <- JsonValue.Create ("sha256:" + candidateSha)
        doc.ToJsonString(JsonSerializerOptions())
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    handler.Queue(Reply(201, [], uploadResponse))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false [("v1.0.4.zip", "sha256:" + candidateSha, 1L)]))
    handler.Queue(ReplyBytes(200, [], archiveBytes))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        match! classify "uploadCandidateSuccessPath" (fun _ ->
            uploadCandidate auth locator "v1.0.4.zip" archivePath candidateSha transport) with
        | Ok (Ok digest) -> assertEqual "success returns the verified digest" candidateSha digest
        | Ok (Error error) -> failed <- failed + 1; failures.Add(sprintf "success upload: %s" (ReleaseError.message error))
        | _ -> ()
        let captures = handler.Captured
        let posts = captures |> Array.filter (fun c -> c.Method = "POST") |> Array.length
        let gets = captures |> Array.filter (fun c -> c.Method = "GET") |> Array.length
        assertEqual "success issues exactly one POST" 1 posts
        assertTrue "success issues at least 3 GETs (lookup + post-POST lookup + post-POST asset)" (gets >= 3)
        let post = captures |> Array.find (fun c -> c.Method = "POST")
        assertTrue "POST URL targets uploads.github.com" (post.Host.EndsWith("uploads.github.com", StringComparison.OrdinalIgnoreCase))
        let payloadSha = sha256Hex post.Body
        assertEqual "POST body equals candidate archive" candidateSha payloadSha
    })
}
let uploadCandidatePostServerErrorNoRetry () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "server-error"
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    handler.Queue(Reply(503, [], "service unavailable"))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        match! classify "uploadCandidatePostServerErrorNoRetry" (fun _ ->
            uploadCandidate auth locator "v1.0.4.zip" archivePath candidateSha transport) with
        | Ok (Error error) -> assertTrue "503 error references 503" ((ReleaseError.message error).Contains("503", StringComparison.Ordinal))
        | Ok (Ok value) -> failed <- failed + 1; failures.Add(sprintf "503 upload: expected Error got %s" value)
        | _ -> ()
    })
    let postCount = handler.Captured |> Array.filter (fun c -> c.Method = "POST") |> Array.length
    assertTrue "503 upload issued at most one POST (no blind retry)" (postCount <= 1)
    ()
}
let observerCdnRedirectDropsAuthorization () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "cdn-redirect"
    let archiveBytes = File.ReadAllBytes archivePath
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false [("v1.0.4.zip", "sha256:" + candidateSha, 1L)]))
    handler.Queue(Reply(302, ["Location", "https://release-assets.githubusercontent.com/asset"], ""))
    handler.Queue(ReplyBytes(200, [], archiveBytes))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        let! _ = observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip"
        return ()
    })
    let captured = handler.Captured
    let tokenOriginRequests = captured |> Array.filter (fun c -> c.Host = "api.github.com")
    assertTrue "at least one api.github.com request was issued"
        (tokenOriginRequests.Length >= 1)
    assertTrue "token origin sent the Authorization header"
        (tokenOriginRequests |> Array.exists (fun c -> c.Authorization.Contains("sentinel", StringComparison.Ordinal)))
    let cdnRequest = captured |> Array.tryFind (fun c -> c.Host = "release-assets.githubusercontent.com")
    match cdnRequest with
    | Some req ->
        assertTrue "CDN redirect MUST NOT carry the Authorization header" (req.Authorization = "")
    | None ->
        failed <- failed + 1
        failures.Add("CDN redirect: redirect not followed as expected")
}
let observerHttpRedirectRejected () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    handler.Queue(Reply(302, ["Location", "http://api.github.com/repos/x/y/releases/tags/dotnet"], ""))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerHttpRedirectRejected" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Error error) ->
            let m = ReleaseError.message error
            assertTrue "http redirect fail-closes with host/origin diagnostic"
                (m.Contains("host", StringComparison.Ordinal) || m.Contains("not a permitted", StringComparison.Ordinal))
        | Ok (Ok state) -> failed <- failed + 1; failures.Add(sprintf "http redirect: expected Error got %A" state)
        | _ -> ()
    })
    assertEqual "http redirect issues exactly one request" 1 (Array.length handler.Captured)
}
let observerLookalikeSuffixHostRejected () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    handler.Queue(Reply(302, ["Location", "https://api.github.com.attacker.test/repos/x/y/releases/tags/dotnet"], ""))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerLookalikeSuffixHostRejected" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Error error) ->
            let m = ReleaseError.message error
            assertTrue "lookalike suffix host rejected" (m.Length > 0)
        | Ok (Ok state) -> failed <- failed + 1; failures.Add(sprintf "lookalike: expected Error got %A" state)
        | _ -> ()
    })
    assertEqual "lookalike redirect issues exactly one request" 1 (Array.length handler.Captured)
}
let observerNonDefaultPortRejected () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    handler.Queue(Reply(302, ["Location", "https://api.github.com:8443/repos/x/y/releases/tags/dotnet"], ""))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerNonDefaultPortRejected" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Error error) ->
            let m = ReleaseError.message error
            assertTrue "non-default port rejected" (m.Length > 0)
        | Ok (Ok state) -> failed <- failed + 1; failures.Add(sprintf "non-default port: expected Error got %A" state)
        | _ -> ()
    })
    assertEqual "non-default port redirect issues exactly one request" 1 (Array.length handler.Captured)
}
let observerUserInfoRejected () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    handler.Queue(Reply(302, ["Location", "https://attacker@api.github.com/repos/x/y/releases/tags/dotnet"], ""))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerUserInfoRejected" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Error error) ->
            let m = ReleaseError.message error
            assertTrue "userinfo rejected" (m.Length > 0)
        | Ok (Ok state) -> failed <- failed + 1; failures.Add(sprintf "userinfo: expected Error got %A" state)
        | _ -> ()
    })
    assertEqual "userinfo redirect issues exactly one request" 1 (Array.length handler.Captured)
}
let observerFollows303Then307Then308ThenRelease () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    handler.Queue(Reply(303, ["Location", "https://api.github.com/repos/x/y/releases/tags/redirected-303"], ""))
    handler.Queue(Reply(307, ["Location", "https://api.github.com/repos/x/y/releases/tags/redirected-307"], ""))
    handler.Queue(Reply(308, ["Location", "https://api.github.com/repos/x/y/releases/tags/redirected-308"], ""))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerFollows303Then307Then308ThenRelease" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Ok Absent) -> passed <- passed + 1
        | Ok (Ok other) -> failed <- failed + 1; failures.Add(sprintf "redirect chain: expected Absent got %A" other)
        | Ok (Error error) -> failed <- failed + 1; failures.Add(sprintf "redirect chain: %s" (ReleaseError.message error))
        | _ -> ()
    })
    assertEqual "redirect chain of 4 issues 4 requests" 4 (Array.length handler.Captured)
}
let observerChainBoundRejected () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    for i in 1 .. 6 do
        handler.Queue(Reply(302, ["Location", sprintf "https://api.github.com/repos/x/y/releases/tags/redirect-%d" i], ""))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let observer = gitHubObserver auth tempRoot transport
        match! classify "observerChainBoundRejected" (fun _ ->
            observer.Observe { Owner = "x"; Repository = "y"; Tag = "dotnet" } "v1.0.4.zip") with
        | Ok (Error error) ->
            let m = ReleaseError.message error
            assertTrue "chain bound failure references redirect/exceeded"
                (m.Contains("redirect", StringComparison.Ordinal) || m.Contains("exceeded", StringComparison.Ordinal))
        | Ok (Ok state) -> failed <- failed + 1; failures.Add(sprintf "chain bound: expected Error got %A" state)
        | _ -> ()
    })
    assertEqual "chain bound issues exactly 6 requests (initial + 5 redirects)" 6 (Array.length handler.Captured)
}
let uploadCandidatePostTimeoutReadsBackVerifiedBytes () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "post-timeout"
    let archiveBytes = File.ReadAllBytes archivePath
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    handler.Queue(Hang)
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false [("v1.0.4.zip", "sha256:" + candidateSha, 1L)]))
    handler.Queue(ReplyBytes(200, [], archiveBytes))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        match! classify "uploadCandidatePostTimeoutReadsBackVerifiedBytes" (fun _ ->
            uploadCandidate auth locator "v1.0.4.zip" archivePath candidateSha transport) with
        | Ok (Ok digest) -> assertEqual "post-timeout verified digest matches candidate" candidateSha digest
        | Ok (Error error) -> failed <- failed + 1; failures.Add(sprintf "post-timeout readback: %s" (ReleaseError.message error))
        | _ -> ()
    })
    let captured = handler.Captured
    let gets = captured |> Array.filter (fun c -> c.Method = "GET") |> Array.length
    assertTrue "post-timeout issues at least 3 GETs (lookup + post-timeout lookup + asset)" (gets >= 3)
    let posts = captured |> Array.filter (fun c -> c.Method = "POST") |> Array.length
    assertEqual "post-timeout issues exactly one POST" 1 posts
}
let uploadCandidateAmbiguousPostMustObserveRemote () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "ambiguous"
    let archiveBytes = File.ReadAllBytes archivePath
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    handler.Queue(Hang)
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false [("v1.0.4.zip", "sha256:" + candidateSha, 1L)]))
    handler.Queue(ReplyBytes(200, [], archiveBytes))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        let! _ = uploadCandidate auth locator "v1.0.4.zip" archivePath candidateSha transport
        return ()
    })
    let captured = handler.Captured
    let posts = captured |> Array.filter (fun c -> c.Method = "POST") |> Array.length
    let gets = captured |> Array.filter (fun c -> c.Method = "GET") |> Array.length
    assertTrue "ambiguous POST issues exactly one POST" (posts = 1)
    assertTrue "ambiguous POST issues at least 3 GETs (lookup + post-POST lookup + post-POST asset)" (gets >= 3)
}
let uploadCandidateSnapshotMismatchZeroRequests () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "snap"
    let wrongDigest = String('a', 64)
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        match! classify "uploadCandidateSnapshotMismatch" (fun _ ->
            uploadCandidate auth locator "v1.0.4.zip" archivePath wrongDigest transport) with
        | Ok (Error error) -> assertTrue "snapshot mismatch is fail-closed" ((ReleaseError.message error).Contains("digest", StringComparison.Ordinal))
        | Ok (Ok value) -> failed <- failed + 1; failures.Add(sprintf "snapshot mismatch: expected Error got %s" value)
        | _ -> ()
    })
    assertEqual "snapshot mismatch issues zero requests" 0 (Array.length handler.Captured)
}
let uploadMetadataTargetsMetadataAssetName () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let projection = projectionFor (String('1', 64)) (String('2', 64))
    let renderedBytes = Encoding.UTF8.GetBytes (render projection)
    let renderedSha = sha256Hex renderedBytes
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    handler.Queue(Reply(201, [], "{\"digest\":\"sha256:" + renderedSha + "\"}"))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false [("dotnet-1.0.4-metadata.json", "sha256:" + renderedSha, 1L)]))
    handler.Queue(ReplyBytes(200, [], renderedBytes))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        match! classify "uploadMetadataTargetsMetadataAssetName" (fun _ ->
            uploadMetadata auth locator projection transport) with
        | Ok (Ok digest) -> assertEqual "metadata upload returns verified digest" renderedSha digest
        | Ok (Error error) -> failed <- failed + 1; failures.Add(sprintf "metadata success: %s" (ReleaseError.message error))
        | _ -> ()
    })
    let captures = handler.Captured
    let posts = captures |> Array.filter (fun c -> c.Method = "POST")
    assertEqual "metadata success issues exactly one POST" 1 (Array.length posts)
    assertTrue "POST URL targets the metadata asset name"
        (posts.[0].Url.Contains("dotnet-1.0.4-metadata.json", StringComparison.OrdinalIgnoreCase))
    assertTrue "POST URL does not target the archive name"
        (not ((posts.[0].Url.Contains("v1.0.4.zip", StringComparison.OrdinalIgnoreCase))
              && (not (posts.[0].Url.Contains("dotnet-1.0.4-metadata.json", StringComparison.OrdinalIgnoreCase)))))
    let postedSha = sha256Hex posts.[0].Body
    assertEqual "POST body equals rendered projection" renderedSha postedSha
}
let archiveSuccessThenMetadataFailure () = async {
    use handler = new RecordingHandler("api.github.com")
    let transport = transportFromHandler handler
    let archivePath, candidateSha = writeCanonicalCandidate "twostep"
    let archiveBytes = File.ReadAllBytes archivePath
    let projection = projectionFor (String('1', 64)) (String('2', 64))
    let archiveOkResponse =
        let doc = JsonObject()
        doc.["digest"] <- JsonValue.Create ("sha256:" + candidateSha)
        doc.ToJsonString(JsonSerializerOptions())
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    handler.Queue(Reply(201, [], archiveOkResponse))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false [("v1.0.4.zip", "sha256:" + candidateSha, 1L)]))
    handler.Queue(ReplyBytes(200, [], archiveBytes))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    handler.Queue(Reply(404, [], "metadata-missing"))
    handler.Queue(Reply(200, [], releaseJson (Some 99L) false []))
    do! withUploadFlag (Some "true") (fun _ -> async {
        let auth = { Token = "sentinel" }
        let locator = { Owner = "x"; Repository = "y"; Tag = "dotnet" }
        let! archiveClassified = classify "twostep/archive" (fun _ -> uploadCandidate auth locator "v1.0.4.zip" archivePath candidateSha transport)
        let! metadataClassified = classify "twostep/metadata" (fun _ -> uploadMetadata auth locator projection transport)
        match archiveClassified, metadataClassified with
        | Ok (Ok aDigest), Ok (Error mError) ->
            assertEqual "archive step returns verified digest" candidateSha aDigest
            assertTrue "metadata step fails closed (404 surfaces as typed failure)"
                ((ReleaseError.message mError).Contains("404", StringComparison.Ordinal) || (ReleaseError.message mError).Contains("status=", StringComparison.Ordinal))
        | _ -> ()
    })
}

let runSuite () = async {
    do! observerUnsafeRedirectRejected ()
    do! observerSafeRedirectObservedAsset ()
    do! observerSafe301ChainFollowedToRelease ()
    do! observerCdnRedirectDropsAuthorization ()
    do! observerHttpRedirectRejected ()
    do! observerLookalikeSuffixHostRejected ()
    do! observerNonDefaultPortRejected ()
    do! observerUserInfoRejected ()
    do! observerFollows303Then307Then308ThenRelease ()
    do! observerChainBoundRejected ()
    do! uploadCandidateIdempotentNoPost ()
    do! uploadCandidateRefusesOverwrite ()
    do! uploadCandidateSuccessPath ()
    do! uploadCandidatePostServerErrorNoRetry ()
    do! uploadCandidateAmbiguousPostMustObserveRemote ()
    do! uploadCandidateSnapshotMismatchZeroRequests ()
    do! uploadCandidatePostTimeoutReadsBackVerifiedBytes ()
    do! uploadMetadataTargetsMetadataAssetName ()
    do! uploadMetadataUsesProducerOwnedToolchains ()
    do! archiveSuccessThenMetadataFailure ()

    printfn "publish summary: passed=%d failed=%d" passed failed
    Fixture.safeDeleteDir tempRoot
}


let tests =
    testCaseAsync "Publish legacy regression assertions" <| async {
        Directory.CreateDirectory tempRoot |> ignore
        try
            do! runSuite ()
            Expect.equal 0 failed (String.concat Environment.NewLine failures)
        finally
            Fixture.safeDeleteDir tempRoot
    }
