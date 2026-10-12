// role: reusable module/helper
// GitHub Releases adapter: exact-origin HTTPS, bounded manual redirects, and one verify-then-publish path.
// `HttpTransport` is the only injection seam; an injected handler runs the same real logic.
// Archive/metadata bytes are snapshotted and re-hashed before any POST, and every POST is confirmed by re-observing the remote.
module ReleasePublish

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open BuildProvenance

type ReleaseLocator =
    { Owner: string
      Repository: string
      Tag: string }

type RemoteState =
    | Observed of ReleaseCompare.ArchiveObservation
    | Absent
    | Missing
    | Immutable

type RemoteObserver =
    { Observe: ReleaseLocator -> string -> Async<Result<RemoteState, ReleaseError>> }

// Minimum token authority is the canonical `GITHUB_TOKEN` environment variable only.
type AuthToken = { Token: string }

let environmentToken () : Result<AuthToken, ReleaseError> =
    match Environment.GetEnvironmentVariable "GITHUB_TOKEN" with
    | null | "" -> Error(FileFailure("auth", "GITHUB_TOKEN", "the GITHUB_TOKEN environment variable is not set"))
    | value -> Ok { Token = value }

let private requestTimeout = TimeSpan.FromSeconds 30.0
let private maxRedirects = 5
let private maxJsonBytes = 4L * 1024L * 1024L
let private maxMetadataAssetBytes = 1L * 1024L * 1024L

// Token authority is exactly these HTTPS authorities; the asset CDN is download-only and never receives the token.
let private tokenOrigins = [| "api.github.com"; "uploads.github.com" |]
let private assetCdnOrigins = [| "release-assets.githubusercontent.com" |]

let private isExactHttpsOrigin (uri: Uri) (hosts: string array) : bool =
    uri.Scheme = Uri.UriSchemeHttps
    && uri.IsDefaultPort
    && String.IsNullOrEmpty uri.UserInfo
    && hosts |> Array.exists (fun host -> String.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))

let private isTokenOrigin (uri: Uri) : bool = isExactHttpsOrigin uri tokenOrigins
let private isAssetCdnOrigin (uri: Uri) : bool = isExactHttpsOrigin uri assetCdnOrigins

// Error text never includes userinfo or the signed query string.
let private safeUrl (uri: Uri) : string =
    if isNull uri then "<null>"
    else $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}"

let private apiBaseUri = Uri "https://api.github.com/"
let private uploadBaseUri = Uri "https://uploads.github.com/"

let private apiUrl (relativePath: string) : Uri = Uri(apiBaseUri, relativePath)
let private uploadUrl (relativePath: string) : Uri = Uri(uploadBaseUri, relativePath)

// Three factories so an injected handler can observe every request; the default disables automatic redirects on all clients.
type HttpTransport =
    { CreateApiClient: unit -> HttpClient
      CreateUploadClient: unit -> HttpClient
      CreateRedirectClient: unit -> HttpClient }

let defaultHttpTransport () : HttpTransport =
    let createHandler () =
        let handler = new HttpClientHandler()
        handler.AllowAutoRedirect <- false
        handler

    let setApiHeaders (client: HttpClient) =
        client.DefaultRequestHeaders.Accept.Clear()
        client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-store-release")
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")

    let createApi () =
        let client = new HttpClient(createHandler ())
        client.BaseAddress <- apiBaseUri
        client.Timeout <- requestTimeout
        setApiHeaders client
        client

    let createUpload () =
        let client = new HttpClient(createHandler ())
        client.Timeout <- requestTimeout
        setApiHeaders client
        client

    let createRedirect () =
        let client = new HttpClient(createHandler ())
        client.Timeout <- requestTimeout
        client

    { CreateApiClient = createApi
      CreateUploadClient = createUpload
      CreateRedirectClient = createRedirect }

let private isRedirectStatus (status: HttpStatusCode) : bool =
    status = HttpStatusCode.MovedPermanently
    || status = HttpStatusCode.Found
    || status = HttpStatusCode.SeeOther
    || status = HttpStatusCode.TemporaryRedirect
    || status = HttpStatusCode.PermanentRedirect

// Reads the body under a hard cap so a hostile or accidental large response cannot exhaust memory.
let private readBoundedAsync (response: HttpResponseMessage) (url: string) (maxBytes: int64) : Async<Result<byte[], ReleaseError>> =
    async {
        try
            let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
            use stream = stream
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 81920
            let mutable total = 0L
            let mutable finished = false
            let mutable oversize = false

            while not finished && not oversize do
                let! read = stream.ReadAsync(chunk, 0, chunk.Length) |> Async.AwaitTask

                if read = 0 then
                    finished <- true
                else
                    total <- total + int64 read

                    if total > maxBytes then
                        oversize <- true
                    else
                        buffer.Write(chunk, 0, read)

            if oversize then
                return Error(FileFailure("read", url, $"the response exceeds the {maxBytes}-byte bound"))
            else
                return Ok(buffer.ToArray())
        with
        | :? OperationCanceledException as error -> return Error(FileFailure("timeout", url, error.Message))
        | :? HttpRequestException as error -> return Error(FileFailure("http", url, error.Message))
        | error -> return Error(FileFailure("read", url, error.Message))
    }

// GET with bounded manual redirects. The token is sent only to a token origin; the asset CDN is followed without it.
let private sendGetWithRedirects
    (transport: HttpTransport)
    (auth: AuthToken)
    (initialUri: Uri)
    (allowAssetCdn: bool)
    : Async<Result<HttpResponseMessage, ReleaseError>> =
    let permitted (uri: Uri) = isTokenOrigin uri || (allowAssetCdn && isAssetCdnOrigin uri)

    let rec loop (uri: Uri) (remaining: int) =
        async {
            if not (permitted uri) then
                return Error(FileFailure("host", safeUrl uri, "the request origin is not an exact permitted HTTPS authority"))
            else
                let useToken = isTokenOrigin uri
                let client = if useToken then transport.CreateApiClient() else transport.CreateRedirectClient()
                let request = new HttpRequestMessage(HttpMethod.Get, uri)
                request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/octet-stream"))

                if useToken then
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", auth.Token)

                try
                    let! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask

                    if isRedirectStatus response.StatusCode then
                        let location = response.Headers.Location
                        response.Dispose()

                        if isNull location then
                            return Error(FileFailure("redirect", safeUrl uri, "the response did not supply a Location header"))
                        elif remaining <= 0 then
                            return Error(FileFailure("redirect", safeUrl uri, "the redirect chain exceeded the bounded limit"))
                        else
                            let next = if location.IsAbsoluteUri then location else Uri(uri, location)
                            return! loop next (remaining - 1)
                    else
                        return Ok response
                with
                | :? OperationCanceledException as error -> return Error(FileFailure("timeout", safeUrl uri, error.Message))
                | :? HttpRequestException as error -> return Error(FileFailure("http", safeUrl uri, error.Message))
                | error -> return Error(FileFailure("transport", safeUrl uri, error.Message))
        }

    loop initialUri maxRedirects

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private parseDigest (digest: string) : string =
    let prefix = "sha256:"

    if digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
        digest.Substring(prefix.Length).ToLowerInvariant()
    elif digest.Length = 64 && digest |> Seq.forall (fun c -> Uri.IsHexDigit c) then
        digest.ToLowerInvariant()
    else
        ""

// Single strict verifier shared by the GitHub observer, the metadata observer, and the upload/readback probe; bytes verify is never optional.
let private verifyAssetDigest
    (assetName: string)
    (claimedDigest: string)
    (remoteBytes: byte[])
    : Result<unit, ReleaseError> =
    let parsed = parseDigest claimedDigest

    if String.IsNullOrEmpty parsed then
        Error(
            MalformedArtifact(
                "release",
                $"the API-reported digest '{claimedDigest}' is not a recognized sha256:hex format"))
    else
        let actual = sha256Hex remoteBytes

        if parsed <> actual then
            Error(
                FileFailure(
                    "verify",
                    assetName,
                    $"downloaded asset SHA-256 ({actual}) does not match the API-reported digest ({parsed})"))
        else
            Ok()

let private tryProperty (element: JsonElement) (name: string) : JsonElement option =
    let mutable found = None

    for property in element.EnumerateObject() do
        if property.Name = name && found.IsNone then
            found <- Some property.Value

    found

type private ReleaseInfo =
    | ReleaseAbsent
    | ReleasePresent of releaseId: int64 * immutable: bool * assets: (string * string * int64) list

// An unsupported or unknown release shape is rejected rather than coerced into a permissive default.
let private parseRelease (json: string) : Result<ReleaseInfo, ReleaseError> =
    try
        use document = JsonDocument.Parse(json)
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error(MalformedArtifact("release", "the release document is not a JSON object"))
        else
            match tryProperty root "id" with
            | None -> Ok ReleaseAbsent
            | Some element when element.ValueKind = JsonValueKind.Null -> Ok ReleaseAbsent
            | Some element when element.ValueKind <> JsonValueKind.Number ->
                Error(MalformedArtifact("release", "the release 'id' is not a number"))
            | Some element ->
                let releaseId = element.GetInt64()

                let immutable =
                    match tryProperty root "immutable" with
                    | Some flag when flag.ValueKind = JsonValueKind.True -> true
                    | Some flag when flag.ValueKind = JsonValueKind.False -> false
                    | _ -> false

                match tryProperty root "assets" with
                | None -> Error(MalformedArtifact("release", "the release document is missing its 'assets' array"))
                | Some assetsElement when assetsElement.ValueKind <> JsonValueKind.Array ->
                    Error(MalformedArtifact("release", "the release 'assets' is not an array"))
                | Some assetsElement ->
                    let parsed = ResizeArray<string * string * int64>()
                    let mutable failure : ReleaseError option = None

                    for asset in assetsElement.EnumerateArray() do
                        if failure.IsNone then
                            if asset.ValueKind <> JsonValueKind.Object then
                                failure <- Some(MalformedArtifact("release", "an asset entry is not a JSON object"))
                            else
                                match tryProperty asset "name", tryProperty asset "id" with
                                | Some nameElement, Some idElement
                                    when nameElement.ValueKind = JsonValueKind.String
                                         && idElement.ValueKind = JsonValueKind.Number ->
                                    let digest =
                                        match tryProperty asset "digest" with
                                        | Some digestElement when digestElement.ValueKind = JsonValueKind.String -> digestElement.GetString()
                                        | _ -> ""

                                    parsed.Add((nameElement.GetString(), digest, idElement.GetInt64()))
                                | _ ->
                                    failure <- Some(MalformedArtifact("release", "each asset entry needs a string 'name' and a numeric 'id'"))

                    match failure with
                    | Some error -> Error error
                    | None ->
                        let duplicate =
                            parsed
                            |> Seq.map (fun (name, _, _) -> name)
                            |> Seq.groupBy id
                            |> Seq.tryFind (fun (_, group) -> Seq.length group > 1)

                        match duplicate with
                        | Some (name, _) -> Error(MalformedArtifact("release", $"the release lists duplicate asset name '{name}'"))
                        | None -> Ok(ReleasePresent(releaseId, immutable, List.ofSeq parsed))
    with
    | :? JsonException as error ->
        Error(MalformedArtifact("release", $"the release document is not valid JSON: {error.Message}"))

let private getRelease (transport: HttpTransport) (auth: AuthToken) (locator: ReleaseLocator) : Async<Result<ReleaseInfo, ReleaseError>> =
    async {
        let uri = apiUrl $"repos/{locator.Owner}/{locator.Repository}/releases/tags/{locator.Tag}"

        match! sendGetWithRedirects transport auth uri false with
        | Error error -> return Error error
        | Ok message ->
            use response = message

            if response.StatusCode = HttpStatusCode.NotFound then
                return Ok ReleaseAbsent
            elif not response.IsSuccessStatusCode then
                return Error(FileFailure("release", safeUrl uri, $"status={(int response.StatusCode)}"))
            else
                match! readBoundedAsync response (safeUrl uri) maxJsonBytes with
                | Error error -> return Error error
                | Ok bytes -> return parseRelease (Encoding.UTF8.GetString bytes)
    }

let private findUniqueAsset (assets: (string * string * int64) list) (name: string) : Result<(string * int64) option, ReleaseError> =
    match assets |> List.filter (fun (assetName, _, _) -> assetName = name) with
    | [] -> Ok None
    | [ (_, digest, assetId) ] -> Ok(Some(digest, assetId))
    | _ -> Error(MalformedArtifact("release", $"the release lists multiple assets named '{name}'"))

let private downloadAssetBytes
    (transport: HttpTransport)
    (auth: AuthToken)
    (locator: ReleaseLocator)
    (assetId: int64)
    (maxBytes: int64)
    : Async<Result<byte[], ReleaseError>> =
    async {
        let uri = apiUrl $"repos/{locator.Owner}/{locator.Repository}/releases/assets/{assetId}"

        match! sendGetWithRedirects transport auth uri true with
        | Error error -> return Error error
        | Ok message ->
            use response = message

            if not response.IsSuccessStatusCode then
                return Error(FileFailure("download", safeUrl uri, $"status={(int response.StatusCode)}"))
            else
                return! readBoundedAsync response (safeUrl uri) maxBytes
    }

// A redirect on the POST is refused so the token-bearing write cannot be replayed to another authority.
let private postAsset
    (auth: AuthToken)
    (locator: ReleaseLocator)
    (releaseId: int64)
    (assetName: string)
    (bytes: byte[])
    (contentType: string)
    (transport: HttpTransport)
    : Async<Result<unit, ReleaseError>> =
    async {
        let uri = uploadUrl $"repos/{locator.Owner}/{locator.Repository}/releases/{releaseId}/assets?name={Uri.EscapeDataString assetName}"

        if not (isTokenOrigin uri) then
            return Error(FileFailure("host", safeUrl uri, "the upload origin is not an exact permitted HTTPS authority"))
        else
            let request = new HttpRequestMessage(HttpMethod.Post, uri)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", auth.Token)
            let content = new ByteArrayContent(bytes)
            content.Headers.ContentType <- MediaTypeHeaderValue(contentType)
            content.Headers.ContentLength <- int64 bytes.Length
            request.Content <- content
            use client = transport.CreateUploadClient()

            try
                let! message = client.SendAsync(request, HttpCompletionOption.ResponseContentRead) |> Async.AwaitTask
                use response = message

                if isRedirectStatus response.StatusCode then
                    return Error(FileFailure("upload", safeUrl uri, "the upload response was a redirect; refusing to follow a redirected POST"))
                elif not response.IsSuccessStatusCode then
                    return Error(FileFailure("upload", safeUrl uri, $"status={(int response.StatusCode)}"))
                else
                    return Ok()
            with
            | :? OperationCanceledException as error -> return Error(FileFailure("timeout", safeUrl uri, error.Message))
            | :? HttpRequestException as error -> return Error(FileFailure("http", safeUrl uri, error.Message))
            | error -> return Error(FileFailure("upload", safeUrl uri, error.Message))
    }

let localMirrorObserver (mirrorRoot: string) : RemoteObserver =
    { Observe =
        fun locator assetName ->
            async {
                let tagDirectory = Path.Combine(mirrorRoot, locator.Tag)
                let assetPath = Path.Combine(tagDirectory, assetName)

                if not (Directory.Exists tagDirectory) then
                    return Ok Missing
                elif not (File.Exists assetPath) then
                    return Ok Absent
                else
                    match! ReleaseCompare.observe assetPath with
                    | Error error -> return Error error
                    | Ok observation -> return Ok(Observed observation)
            } }

let gitHubObserver (auth: AuthToken) (downloadDir: string) (transport: HttpTransport) : RemoteObserver =
    { Observe =
        fun locator assetName ->
            async {
                match! getRelease transport auth locator with
                | Error error -> return Error error
                | Ok ReleaseAbsent -> return Ok Missing
                | Ok (ReleasePresent (_, true, _)) -> return Ok Immutable
                | Ok (ReleasePresent (_, false, assets)) ->
                    match findUniqueAsset assets assetName with
                    | Error error -> return Error error
                    | Ok None -> return Ok Absent
                    | Ok (Some (claimedDigest, assetId)) ->
                        match! downloadAssetBytes transport auth locator assetId ReleaseArchive.maxArchiveBytes with
                        | Error error -> return Error error
                        | Ok bytes ->
                            match verifyAssetDigest assetName claimedDigest bytes with
                            | Error error -> return Error error
                            | Ok () ->
                                if not (Directory.Exists downloadDir) then
                                    Directory.CreateDirectory downloadDir |> ignore

                                let targetPath = Path.Combine(downloadDir, assetName)

                                if File.Exists targetPath then
                                    File.Delete targetPath

                                File.WriteAllBytes(targetPath, bytes)

                                match! ReleaseCompare.observe targetPath with
                                | Error error -> return Error error
                                | Ok observation -> return Ok(Observed observation)
            } }

// The compressed candidate is validated as a real archive before it is ever sent.
let private verifyArchiveBytes (bytes: byte[]) : Async<Result<unit, ReleaseError>> =
    async {
        let path = Path.Combine(Path.GetTempPath(), $"mcp-release-verify-{Guid.NewGuid():N}.zip")

        let! attempt =
            async {
                try
                    do! File.WriteAllBytesAsync(path, bytes) |> Async.AwaitTask

                    match! ReleaseCompare.observe path with
                    | Error error -> return Error error
                    | Ok _ -> return Ok()
                with
                | error -> return Error(FileFailure("verify", path, error.Message))
            }

        try File.Delete path with _ -> ()
        return attempt
    }

let private verifyMetadataBytes (projection: ReleaseMetadata.ProducerMetadataProjection) (bytes: byte[]) : Async<Result<unit, ReleaseError>> =
    async {
        match ReleaseMetadata.parse (Encoding.UTF8.GetString bytes) with
        | Error error -> return Error error
        | Ok existing -> return ReleaseMetadata.compare existing projection
    }

let private writeEnabled () : bool =
    match Environment.GetEnvironmentVariable "MCP_RELEASE_DO_UPLOAD" with
    | null | "" -> false
    | "false" | "0" -> false
    | _ -> true

type private Probe =
    | ReleaseUnavailable
    | AssetAbsent of releaseId: int64
    | AssetVerified of digest: string
    | AssetImmutable

// One remote read of the release, then: absent, immutable, verified-exact, or a typed no-overwrite failure.
let private probe
    (auth: AuthToken)
    (locator: ReleaseLocator)
    (assetName: string)
    (expectedBytes: byte[])
    (expectedDigest: string)
    (maxBytes: int64)
    (verify: byte[] -> Async<Result<unit, ReleaseError>>)
    (transport: HttpTransport)
    : Async<Result<Probe, ReleaseError>> =
    async {
        match! getRelease transport auth locator with
        | Error error -> return Error error
        | Ok ReleaseAbsent -> return Ok ReleaseUnavailable
        | Ok (ReleasePresent (releaseId, true, _)) -> return Ok AssetImmutable
        | Ok (ReleasePresent (releaseId, false, assets)) ->
            match findUniqueAsset assets assetName with
            | Error error -> return Error error
            | Ok None -> return Ok(AssetAbsent releaseId)
            | Ok (Some (claimedDigest, assetId)) ->
                match! downloadAssetBytes transport auth locator assetId maxBytes with
                | Error error -> return Error error
                | Ok remoteBytes ->
                    // The API-reported digest must parse and match the downloaded bytes before any byte-level or verification step runs.
                    match verifyAssetDigest assetName claimedDigest remoteBytes with
                    | Error error -> return Error error
                    | Ok () ->
                        if remoteBytes <> expectedBytes then
                            return
                                Error(
                                    FileFailure(
                                        "upload",
                                        assetName,
                                        $"the {locator.Tag} release already carries '{assetName}'; the remote bytes differ from the candidate bytes, refusing to overwrite"))
                        else
                            match! verify remoteBytes with
                            | Error error -> return Error error
                            | Ok() -> return Ok(AssetVerified (sha256Hex remoteBytes))
    }

// Snapshot bytes, recompute the digest, then publish and re-observe: the exact sent buffer must be observable remotely.
let private uploadBytes
    (auth: AuthToken)
    (locator: ReleaseLocator)
    (assetName: string)
    (contentType: string)
    (bytes: byte[])
    (expectedDigest: string)
    (maxBytes: int64)
    (verify: byte[] -> Async<Result<unit, ReleaseError>>)
    (transport: HttpTransport)
    : Async<Result<string, ReleaseError>> =
    async {
        let snapshotDigest = sha256Hex bytes

        if snapshotDigest <> expectedDigest then
            return
                Error(
                    FileFailure(
                        "upload",
                        assetName,
                        "the bytes changed after validation; refusing to upload bytes whose digest is not the validated digest"))
        else
            match! probe auth locator assetName bytes expectedDigest maxBytes verify transport with
            | Error error -> return Error error
            | Ok AssetImmutable ->
                return Error(FileFailure("immutable", assetName, $"the {locator.Tag} release is configured as immutable; refusing to publish"))
            | Ok ReleaseUnavailable ->
                return Error(FileFailure("release", locator.Tag, "the release does not exist; a container-creation flow is required"))
            | Ok (AssetVerified digest) -> return Ok digest
            | Ok (AssetAbsent releaseId) ->
                let! postOutcome = postAsset auth locator releaseId assetName bytes contentType transport

                // Any POST outcome is reconciled by a bounded remote read and byte verification before success.
                match! probe auth locator assetName bytes expectedDigest maxBytes verify transport with
                | Ok (AssetVerified digest) -> return Ok digest
                | Ok (AssetAbsent _) ->
                    return
                        match postOutcome with
                        | Error error -> Error error
                        | Ok() -> Error(FileFailure("upload", assetName, "the uploaded asset was not observable at the expected bytes"))
                | Ok AssetImmutable -> return Error(FileFailure("immutable", assetName, $"the {locator.Tag} release became immutable after the upload"))
                | Ok ReleaseUnavailable -> return Error(FileFailure("release", locator.Tag, "the release became unobservable after the upload"))
                | Error error -> return Error error
    }

let uploadCandidate
    (auth: AuthToken)
    (locator: ReleaseLocator)
    (assetName: string)
    (candidatePath: string)
    (expectedDigest: string)
    (transport: HttpTransport)
    : Async<Result<string, ReleaseError>> =
    async {
        if not (writeEnabled ()) then
            return
                Error(
                    FileFailure(
                        "upload",
                        assetName,
                        $"W3 dev mode: refusing to upload {candidatePath}; set MCP_RELEASE_DO_UPLOAD=true for the production release path"))
        elif String.IsNullOrEmpty auth.Token then
            return Error(FileFailure("upload", assetName, "the auth token is empty"))
        elif not (File.Exists candidatePath) then
            return Error(MissingArtifact candidatePath)
        else
            let bytes = File.ReadAllBytes candidatePath
            return!
                uploadBytes
                    auth
                    locator
                    assetName
                    "application/octet-stream"
                    bytes
                    expectedDigest
                    ReleaseArchive.maxArchiveBytes
                    verifyArchiveBytes
                    transport
    }

let uploadMetadata
    (auth: AuthToken)
    (locator: ReleaseLocator)
    (projection: ReleaseMetadata.ProducerMetadataProjection)
    (transport: HttpTransport)
    : Async<Result<string, ReleaseError>> =
    async {
        if not (writeEnabled ()) then
            return
                Error(
                    FileFailure(
                        "upload-metadata",
                        projection.ComponentId,
                        $"W3 dev mode: refusing to upload metadata for {projection.ComponentId} {projection.Version}; set MCP_RELEASE_DO_UPLOAD=true for the production release path"))
        elif String.IsNullOrEmpty auth.Token then
            return Error(FileFailure("upload-metadata", projection.ArchiveName, "the auth token is empty"))
        else
            // The metadata asset name is versioned, never the archive (zip) name the projection also carries.
            let assetName = ReleaseMetadata.metadataAssetName projection.ComponentId projection.Version
            let bytes = Encoding.UTF8.GetBytes(ReleaseMetadata.render projection)
            let expectedDigest = sha256Hex bytes

            return!
                uploadBytes
                    auth
                    locator
                    assetName
                    "application/vnd.github+json"
                    bytes
                    expectedDigest
                    maxMetadataAssetBytes
                    (verifyMetadataBytes projection)
                    transport
    }

// Sibling of `gitHubObserver` for the JSON metadata asset; surface verified absence distinctly so the runner never silently overwrites.
type RemoteMetadataState =
    | MetadataObserved of body: string * sha256: string * parsed: ReleaseMetadata.ProducerMetadataProjection
    | MetadataAbsent
    | MetadataMissing
    | MetadataImmutable

type RemoteMetadataObserver =
    { ObserveMetadata: ReleaseLocator -> string -> Async<Result<RemoteMetadataState, ReleaseError>> }

// Reuses `getRelease`/`findUniqueAsset`/`downloadAssetBytes`; never reuses the ZIP validators.
let gitHubMetadataObserver (auth: AuthToken) (downloadDir: string) (transport: HttpTransport) : RemoteMetadataObserver =
    { ObserveMetadata =
        fun locator assetName ->
            async {
                match! getRelease transport auth locator with
                | Error error -> return Error error
                | Ok ReleaseAbsent -> return Ok MetadataMissing
                | Ok (ReleasePresent (_, true, _)) -> return Ok MetadataImmutable
                | Ok (ReleasePresent (_, false, assets)) ->
                    match findUniqueAsset assets assetName with
                    | Error error -> return Error error
                    | Ok None -> return Ok MetadataAbsent
                    | Ok (Some (claimedDigest, assetId)) ->
                        match! downloadAssetBytes transport auth locator assetId maxMetadataAssetBytes with
                        | Error error -> return Error error
                        | Ok bytes ->
                            match verifyAssetDigest assetName claimedDigest bytes with
                            | Error error -> return Error error
                            | Ok () ->
                                let actualDigest = sha256Hex bytes
                                let body = Encoding.UTF8.GetString bytes

                                match ReleaseMetadata.parse body with
                                | Error error -> return Error error
                                | Ok parsed -> return Ok(MetadataObserved(body, actualDigest, parsed))
            } }
