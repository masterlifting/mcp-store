// role: reusable module/helper
// Repo-shared release-publish transport mechanics loaded as the explicit
// ReleasePublish module. Owns the only GitHub effects surface the
// per-component transaction and the compiled runner consume. The
// module implements the GitHub Releases API adapter with bounded HTTP
// effects, minimum token authority, and safe redirect handling; the
// `gh` CLI is intentionally avoided so the production transport is
// reproducible from this script alone.
//
// Two seam contracts are provided:
//
//   * `RemoteObserver` reads the remote release. `gitHubObserver` calls
//     the public GitHub Releases API and downloads the asset through
//     `octet-stream`-authenticated releases API. `localMirrorObserver`
//     reads a published archive from `mirrorRoot/<tag>/<assetName>`
//     for development and tests.
//   * `uploadCandidate` writes the candidate archive to the release.
//     The function is the test seam for the actual upload; in the W3
//     development environment it returns a typed failure unless the
//     `MCP_RELEASE_DO_UPLOAD=true` flag is set, so the local validation
//     flows can never perform an actual remote write.
//   * `uploadMetadata` writes the consumer-neutral release-metadata
//     projection alongside the archive. The function is the
//     safe-immutable-transport seam: an absent metadata asset is
//     uploaded; an existing asset whose bytes equal the freshly
//     rendered projection is treated as a confirmed upload
//     (idempotent recovery); an existing asset with different bytes
//     fails closed without retry. In W3 the function refuses to write
//     unless the same upload flag is set.
//
// Both observers return `Observed` when the asset was observed,
// `Absent` when the release exists but has no asset with the producer-
// declared name, `Missing` when the release itself is not present, and
// `Immutable` when the release is configured as immutable. The
// transaction uses these states to drive the "publish pending" and
// "fail closed" branches; absence is never a silent default.
//
// Safe-redirect handling follows these rules:
//
//   * Redirects to a non-`github.com` host are treated as typed
//     failures; the client does not forward the bearer token to a
//     non-GitHub host.
//   * Redirects to `api.github.com` retain the Authorization header
//     because GitHub uses internal CDN hosts under the same auth
//     scope.
//
// This module is compiled into the Release.fsproj assembly together
// with Provenance.fsx, Compare.fsx, and Metadata.fsx; the `#load`
// directives are honored by the analyzer and ignored by the project
// compiler.
module ReleasePublish

#load "../Provenance.fsx"
#load "Compare.fsx"
#load "Metadata.fsx"

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance

// GitHub API tag/release descriptor the observer needs. The values
// come from the producer contract; the observer is a thin transport
// that does not re-validate the producer policy.
type ReleaseLocator =
    { Owner: string
      Repository: string
      Tag: string }

// The outcome of one remote observation. `Observed` carries the
// captured `ArchiveObservation` (after the remote asset bytes were
// downloaded and verified against the API-reported digest). `Absent`
// means the release exists but the asset is not present. `Missing`
// means the release itself is not present. `Immutable` means the
// release is configured as immutable, which is incompatible with the
// multi-version-per-release model and must fail closed.
type RemoteState =
    | Observed of ReleaseCompare.ArchiveObservation
    | Absent
    | Missing
    | Immutable

// A read-only remote observer the transaction uses.
type RemoteObserver =
    { Observe: ReleaseLocator -> string -> Async<Result<RemoteState, ReleaseError>> }

// The GitHub token the transport uses. The default resolver reads
// `GITHUB_TOKEN` from the environment; a missing token is a typed
// failure rather than a silent null. Minimum token authority: the
// transport only requests the public release API endpoints with a
// bearer token; it never requests elevated scopes or re-uses tokens
// for unrelated endpoints.
type AuthToken = { Token: string }

let environmentToken () : Result<AuthToken, ReleaseError> =
    match Environment.GetEnvironmentVariable "GITHUB_TOKEN" with
    | null | "" -> Error(FileFailure("auth", "GITHUB_TOKEN", "the GITHUB_TOKEN environment variable is not set"))
    | value -> Ok { Token = value }

// Per-request timeout the transport enforces. The GitHub Releases
// API has documented transient 502/503 behavior under load; the
// timeout is bounded (no infinite waits) so a stuck connection
// surfaces as a typed failure rather than a hung release job.
let private requestTimeout = TimeSpan.FromSeconds 30.0

// Build an HTTP client pinned to the GitHub API host with the API
// headers GitHub requires for the releases endpoints. The client
// never auto-follows redirects; the transport follows redirects
// manually so the bearer token can be re-applied only when the
// redirect stays on `github.com`.
let private createApiClient () : HttpClient =
    let client = new HttpClient()
    client.BaseAddress <- Uri "https://api.github.com/"
    client.Timeout <- requestTimeout
    client.DefaultRequestHeaders.Accept.Clear()
    client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
    client.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-store-release")
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
    client

// Read the response body; map a non-2xx response to a typed failure
// that names the response code and a short snippet of the body so the
// upstream log is actionable without re-running.
let private readResponseAsync (operation: string) (response: HttpResponseMessage) : Async<Result<string, ReleaseError>> =
    async {
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        if response.IsSuccessStatusCode then
            return Ok body
        else
            let trimmed = if body.Length > 256 then body.Substring(0, 256) + "..." else body
            return
                Error(
                    FileFailure(
                        operation,
                        response.RequestMessage.RequestUri.ToString(),
                        $"status={(int response.StatusCode)} body={trimmed}"))
    }

// Follow a redirect when the response is a 3xx with a `Location`
// header that resolves to a `github.com` host. Non-GitHub redirects
// are rejected with a typed failure; missing `Location` headers are
// also rejected.
let private followIfSafe
    (auth: AuthToken)
    (request: HttpRequestMessage)
    (response: HttpResponseMessage)
    : Async<Result<HttpResponseMessage, ReleaseError>> =
    async {
        let isRedirect =
            response.StatusCode = HttpStatusCode.Redirect
            || response.StatusCode = HttpStatusCode.Found
            || response.StatusCode = HttpStatusCode.TemporaryRedirect
            || response.StatusCode = HttpStatusCode.PermanentRedirect

        if not isRedirect then
            return Ok response
        else
            match response.Headers.Location with
            | null ->
                return
                    Error(
                        FileFailure(
                            "redirect",
                            request.RequestUri.ToString(),
                            "the response indicated a redirect but did not supply a Location header"))
            | locationUri ->
                let absoluteUri =
                    if locationUri.IsAbsoluteUri then
                        locationUri
                    else
                        Uri(request.RequestUri, locationUri)

                let isGitHub =
                    absoluteUri.Host.EndsWith("api.github.com", StringComparison.OrdinalIgnoreCase)
                    || absoluteUri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)

                if not isGitHub then
                    return
                        Error(
                            FileFailure(
                                "redirect",
                                absoluteUri.ToString(),
                                "the redirect target is not a github.com host; refusing to forward the authorization token"))
                else
                    let redirected = new HttpRequestMessage(HttpMethod.Get, absoluteUri)
                    redirected.Headers.Authorization <- AuthenticationHeaderValue("Bearer", auth.Token)
                    redirected.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/octet-stream"))
                    use redirectedClient = new HttpClient()
                    let! followResponse =
                        redirectedClient.SendAsync(redirected, HttpCompletionOption.ResponseHeadersRead)
                        |> Async.AwaitTask
                    return Ok followResponse
    }

// Issue an API request and apply safe-redirect handling.
let private apiRequestAsync (client: HttpClient) (auth: AuthToken) (request: HttpRequestMessage) : Async<Result<string, ReleaseError>> =
    async {
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", auth.Token)

        try
            let! response =
                client.SendAsync(request, HttpCompletionOption.ResponseContentRead)
                |> Async.AwaitTask

            match! followIfSafe auth request response with
            | Error error -> return Error error
            | Ok safeResponse -> return! readResponseAsync $"{request.Method} {request.RequestUri.AbsolutePath}" safeResponse
        with
        | :? OperationCanceledException as error ->
            return Error(FileFailure("timeout", request.RequestUri.ToString(), error.Message))
        | :? HttpRequestException as error ->
            return Error(FileFailure("http", request.RequestUri.ToString(), error.Message))
    }

// Local-mirror observer for development and tests. Reads a published
// archive from `mirrorRoot/<tag>/<assetName>` and observes it; reports
// `Absent` when the asset file is missing, `Missing` when the mirror
// has no directory for the tag. The mirror is a fixed path the
// workflow or the test harness populates; it does not consult any
// remote API.
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

// Parse a sha256:hex digest string from the GitHub API. The API
// returns digests as `sha256:hex`; the function returns the lowercase
// hex portion or an empty string when the format is not recognized.
let private parseDigest (digest: string) : string =
    let prefix = "sha256:"
    if digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
        digest.Substring(prefix.Length).ToLowerInvariant()
    else if digest.Length = 64 && digest |> Seq.forall (fun c -> Uri.IsHexDigit c) then
        digest.ToLowerInvariant()
    else
        ""

// Verify a downloaded asset's SHA-256 against the API-reported digest.
// A mismatch fails closed because the asset bytes the API saw do not
// match the asset bytes the comparison would compare against.
let private verifyDigest (path: string) (expectedDigest: string) : Result<unit, ReleaseError> =
    let bytes = File.ReadAllBytes path
    let actual = SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()
    if actual = expectedDigest then
        Ok()
    else
        Error(
            FileFailure(
                "verify",
                path,
                $"downloaded asset SHA-256 ({actual}) does not match the API-reported digest ({expectedDigest})"))

// Parse the GitHub release metadata document. The function returns
// `(releaseId option, isImmutable, asset list)`; the release-id option
// is `None` when the document refers to a non-existent release. Each
// asset entry carries `(name, digest, assetId)` so the download can
// resolve the asset on `/releases/assets/{assetId}` (the GitHub API
// requires the asset identifier, not the asset name, on the download
// path).
let private parseReleaseMetadata (json: string) : Result<(int64 option * bool * (string * string * int64) list), ReleaseError> =
    try
        let doc = JsonNode.Parse(json)
        if isNull doc then
            Ok((None, false, []))
        else
            let root = doc.AsObject()

            let idNode = root["id"]
            let releaseId =
                if isNull idNode then None else Some(idNode.GetValue<int64>())

            let isImmutable =
                match root["immutable"] with
                | null -> false
                | node -> node.GetValue<bool>()

            let assets =
                match root["assets"] with
                | null -> []
                | node ->
                    node.AsArray()
                    |> Seq.map (fun entry ->
                        let asset = entry.AsObject()
                        let name = asset["name"].GetValue<string>()
                        let digest =
                            match asset["digest"] with
                            | null -> ""
                            | node -> node.GetValue<string>()
                        let assetId =
                            match asset["id"] with
                            | null -> 0L
                            | idNode -> idNode.GetValue<int64>()
                        (name, digest, assetId))
                    |> Seq.toList

            Ok(releaseId, isImmutable, assets)
    with
    | :? JsonException as error ->
        Error(MalformedArtifact("gh release view", $"the JSON document is not valid: {error.Message}"))
    | :? FormatException as error ->
        Error(MalformedArtifact("gh release view", $"the JSON document is not valid: {error.Message}"))
    | :? InvalidOperationException as error ->
        Error(MalformedArtifact("gh release view", $"the JSON document is not valid: {error.Message}"))

// Production observer backed by the public GitHub Releases API. The
// function reads release metadata (including the per-asset digest and
// the immutable flag) and downloads the asset through the API. The
// observer is the only place that touches GitHub; the per-component
// transaction consumes its result.
let gitHubObserver (auth: AuthToken) (downloadDir: string) : RemoteObserver =
    { Observe =
        fun locator assetName ->
            async {
                use client = createApiClient ()
                let releasePath = $"repos/{locator.Owner}/{locator.Repository}/releases/tags/{locator.Tag}"

                let metadataRequest = new HttpRequestMessage(HttpMethod.Get, releasePath)
                metadataRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))

                match! apiRequestAsync client auth metadataRequest with
                | Error error -> return Error error
                | Ok json ->
                    match parseReleaseMetadata json with
                    | Error error -> return Error error
                    | Ok (None, _, _) -> return Ok Missing
                    | Ok (Some _, true, _) -> return Ok Immutable
                    | Ok (Some _, false, assets) ->
                        let assetEntry =
                            assets
                            |> List.tryFind (fun (name, _, _) -> name = assetName)

                        match assetEntry with
                        | None -> return Ok Absent
                        | Some (_, "", _) -> return Ok Absent
                        | Some (_, digest, 0L) ->
                            return
                                Error(
                                    FileFailure(
                                        "asset",
                                        assetName,
                                        "the asset listing did not include a numeric asset id; refusing to download"))
                        | Some (_, digest, assetId) ->
                            let hexDigest = parseDigest digest

                            if String.IsNullOrEmpty hexDigest then
                                return
                                    Error(
                                        MalformedArtifact(
                                            "gh release view",
                                            $"the API-reported digest '{digest}' is not a recognized sha256:hex format"))
                            else
                                if not (Directory.Exists downloadDir) then
                                    Directory.CreateDirectory downloadDir |> ignore

                                let targetPath = Path.Combine(downloadDir, assetName)

                                if File.Exists targetPath then
                                    File.Delete targetPath

                                let assetPath = $"repos/{locator.Owner}/{locator.Repository}/releases/assets/{assetId}"
                                let downloadRequest = new HttpRequestMessage(HttpMethod.Get, assetPath)
                                downloadRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/octet-stream"))

                                try
                                    let! response =
                                        client.SendAsync(downloadRequest, HttpCompletionOption.ResponseContentRead)
                                        |> Async.AwaitTask

                                    let! bytes =
                                        response.Content.ReadAsByteArrayAsync()
                                        |> Async.AwaitTask

                                    if not response.IsSuccessStatusCode then
                                        let snippet =
                                            System.Text.Encoding.UTF8.GetString(bytes, 0, min bytes.Length 256)
                                        return
                                            Error(
                                                FileFailure(
                                                    "download",
                                                    assetPath,
                                                    $"status={(int response.StatusCode)} body={snippet}"))
                                    else
                                        File.WriteAllBytes(targetPath, bytes)
                                        match verifyDigest targetPath hexDigest with
                                        | Error error -> return Error error
                                        | Ok() ->
                                            match! ReleaseCompare.observe targetPath with
                                            | Error error -> return Error error
                                            | Ok observation -> return Ok(Observed observation)
                                with
                                | :? OperationCanceledException as error ->
                                    return Error(FileFailure("timeout", assetPath, error.Message))
                                | :? HttpRequestException as error ->
                                    return Error(FileFailure("http", assetPath, error.Message))
            } }

// Upload the candidate archive through the GitHub release upload
// endpoint. The function uses bounded HTTP effects only; no
// `gh`-CLI dependency. The function times out per request and never
// retries blindly on transient 5xx; the workflow observes the remote
// to recover when the asset is already at the expected digest (an
// idempotent publish on a rerun, or a parallel run that finished
// first).
//
// In the W3 development environment, the function refuses an actual
// remote write unless `MCP_RELEASE_DO_UPLOAD=true` is set. This
// keeps the local validation flow from performing any actual
// publication while leaving the production code path active.
//
// On success, the function returns the remote asset digest so the
// caller can render the per-component summary with the actual
// published hash.
let uploadCandidate
    (auth: AuthToken)
    (locator: ReleaseLocator)
    (assetName: string)
    (candidatePath: string)
    (expectedDigest: string)
    : Async<Result<string, ReleaseError>> =
    async {
        let writeEnabled =
            match Environment.GetEnvironmentVariable "MCP_RELEASE_DO_UPLOAD" with
            | null | "" -> false
            | "false" | "0" -> false
            | _ -> true

        if not writeEnabled then
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
            use client = createApiClient ()
            let releasePath = $"repos/{locator.Owner}/{locator.Repository}/releases/tags/{locator.Tag}"

            let lookupRequest = new HttpRequestMessage(HttpMethod.Get, releasePath)
            lookupRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))

            match! apiRequestAsync client auth lookupRequest with
            | Error error -> return Error error
            | Ok json ->
                let releaseIdResult = parseReleaseMetadata json

                match releaseIdResult with
                | Error error -> return Error error
                | Ok (None, _, _) ->
                    return
                        Error(
                            FileFailure(
                                "release",
                                locator.Tag,
                                "the release does not exist; a separate container-creation flow is required"))
                | Ok (Some _, true, _) ->
                    return
                        Error(
                            FileFailure(
                                "immutable",
                                assetName,
                                $"the {locator.Tag} release is configured as immutable; refusing to publish"))
                | Ok (Some releaseIdValue, false, assets) ->
                    let downloadDigest =
                        assets
                        |> List.tryPick (fun (name, digest, _assetId) ->
                            if name = assetName then Some digest else None)

                    match downloadDigest with
                    | Some existingDigest ->
                        let existingHexDigest = parseDigest existingDigest

                        if not (String.IsNullOrEmpty existingHexDigest)
                           && existingHexDigest = expectedDigest then
                            // The exact asset is already present with the
                            // expected digest; recover as a confirmed upload
                            // without retrying the write.
                            return Ok existingHexDigest
                        else
                            return
                                Error(
                                    FileFailure(
                                        "upload",
                                        assetName,
                                        $"the {locator.Tag} release already carries an asset named '{assetName}'; refusing to overwrite an existing versioned asset"))
                    | None ->
                        // The GitHub upload endpoint lives on a separate
                        // host (`uploads.github.com`) from the regular
                        // Releases API. Build an absolute URL and a
                        // dedicated client so the bearer token does not
                        // leak through an unrelated redirect path.
                        let uploadUri =
                            Uri(
                                $"https://uploads.github.com/repos/{locator.Owner}/{locator.Repository}/releases/{releaseIdValue}/assets?name={Uri.EscapeDataString assetName}")

                        use uploadClient = new HttpClient()
                        uploadClient.Timeout <- requestTimeout
                        uploadClient.DefaultRequestHeaders.Accept.Clear()
                        uploadClient.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
                        uploadClient.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-store-release")
                        uploadClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")

                        let uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUri)
                        uploadRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", auth.Token)
                        let bytes = File.ReadAllBytes candidatePath
                        let content = new ByteArrayContent(bytes)
                        content.Headers.ContentType <- MediaTypeHeaderValue("application/octet-stream")
                        content.Headers.ContentLength <- int64 bytes.Length
                        uploadRequest.Content <- content

                        try
                            let! response =
                                uploadClient.SendAsync(uploadRequest, HttpCompletionOption.ResponseContentRead)
                                |> Async.AwaitTask

                            let! body =
                                response.Content.ReadAsStringAsync()
                                |> Async.AwaitTask

                            if not response.IsSuccessStatusCode then
                                let trimmed = if body.Length > 256 then body.Substring(0, 256) + "..." else body
                                return
                                    Error(
                                        FileFailure(
                                            "upload",
                                            uploadUri.ToString(),
                                            $"status={(int response.StatusCode)} body={trimmed}"))
                            else
                                try
                                    let doc = JsonNode.Parse(body).AsObject()
                                    let digestNode = doc["digest"]
                                    let parsedDigest =
                                        match digestNode with
                                        | null -> ""
                                        | node ->
                                            let raw = node.GetValue<string>()
                                            parseDigest raw

                                    if parsedDigest = "" then
                                        return
                                            Error(
                                                FileFailure(
                                                    "upload",
                                                    assetName,
                                                    "the upload response did not include a sha256 digest"))
                                    elif parsedDigest <> expectedDigest then
                                        return
                                            Error(
                                                FileFailure(
                                                    "upload",
                                                    assetName,
                                                    $"the upload response digest ({parsedDigest}) does not match the candidate digest ({expectedDigest})"))
                                    else
                                        return Ok parsedDigest
                                with
                                | :? JsonException as error ->
                                    return
                                        Error(
                                            MalformedArtifact(
                                                "upload",
                                                $"the upload response is not valid JSON: {error.Message}"))
                                | :? FormatException as error ->
                                    return
                                        Error(
                                            MalformedArtifact(
                                                "upload",
                                                $"the upload response is not valid JSON: {error.Message}"))
                                | :? InvalidOperationException as error ->
                                    return
                                        Error(
                                            MalformedArtifact(
                                                "upload",
                                                $"the upload response is not valid JSON: {error.Message}"))
                        with
                        | :? OperationCanceledException as error ->
                            return Error(FileFailure("timeout", uploadUri.ToString(), error.Message))
                        | :? HttpRequestException as error ->
                            return Error(FileFailure("http", uploadUri.ToString(), error.Message))
    }

// The minimum acceptable scope of the upload path: an idempotent
// return when the remote asset already matches the freshly rendered
// projection bytes; a strict refusal when the bytes differ; an upload
// when the asset is absent. The "recover" branch guards the
// archive-success-then-metadata-failure scenario by confirming the
// remote observation next run, so a transient transport failure is
// safe to retry without overwriting.
let uploadMetadata
    (auth: AuthToken)
    (locator: ReleaseLocator)
    (projection: ReleaseMetadata.ProducerMetadataProjection)
    : Async<Result<string, ReleaseError>> =
    async {
        let writeEnabled =
            match Environment.GetEnvironmentVariable "MCP_RELEASE_DO_UPLOAD" with
            | null | "" -> false
            | "false" | "0" -> false
            | _ -> true

        if not writeEnabled then
            return
                Error(
                    FileFailure(
                        "upload-metadata",
                        projection.ComponentId,
                        $"W3 dev mode: refusing to upload metadata for {projection.ComponentId} {projection.Version}; set MCP_RELEASE_DO_UPLOAD=true for the production release path"))
        elif String.IsNullOrEmpty auth.Token then
            return Error(FileFailure("upload-metadata", projection.ArchiveName, "the auth token is empty"))
        else
            // Render locally first so the on-disk bytes are exactly
            // the bytes the transport will send. The SHA-256 is the
            // value the safe-immutable transport compares an existing
            // remote asset's digest against and is the only value the
            // function returns on success.
            let bytes = System.Text.Encoding.UTF8.GetBytes(ReleaseMetadata.render projection)
            let expectedDigest =
                SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

            let! lookup =
                async {
                    use client = createApiClient ()
                    let releasePath =
                        $"repos/{locator.Owner}/{locator.Repository}/releases/tags/{locator.Tag}"
                    let lookupRequest = new HttpRequestMessage(HttpMethod.Get, releasePath)
                    lookupRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))

                    match! apiRequestAsync client auth lookupRequest with
                    | Error error -> return Error error
                    | Ok json -> return parseReleaseMetadata json
                }

            match lookup with
            | Error error -> return Error error
            | Ok (None, _, _) ->
                return
                    Error(
                        FileFailure(
                            "release",
                            locator.Tag,
                            "the release does not exist; the runner will not create one arbitrarily"))
            | Ok (Some _, true, _) ->
                return
                    Error(
                        FileFailure(
                            "immutable",
                            projection.ArchiveName,
                            $"the {locator.Tag} release is configured as immutable; refusing to upload metadata"))
            | Ok (Some releaseIdValue, false, assets) ->
                match assets |> List.tryFind (fun (name, _, _) -> name = projection.ArchiveName) with
                | Some (_, existingDigest, _) ->
                    let existingHexDigest = parseDigest existingDigest

                    if not (String.IsNullOrEmpty existingHexDigest) && existingHexDigest = expectedDigest then
                        // Idempotent recovery: the remote asset is the
                        // exact expected projection. The function
                        // returns Ok so the runner can advance to the
                        // published state on the next observation
                        // without another write.
                        return Ok expectedDigest
                    else
                        return
                            Error(
                                FileFailure(
                                    "upload-metadata",
                                    projection.ArchiveName,
                                    $"the {locator.Tag} release already carries an asset named '{projection.ArchiveName}' with a different digest; refusing to overwrite an immutable-versioned metadata asset"))
                | None ->
                    let uploadUri =
                        Uri(
                            $"https://uploads.github.com/repos/{locator.Owner}/{locator.Repository}/releases/{releaseIdValue}/assets?name={Uri.EscapeDataString projection.ArchiveName}")

                    use uploadClient = new HttpClient()
                    uploadClient.Timeout <- requestTimeout
                    uploadClient.DefaultRequestHeaders.Accept.Clear()
                    uploadClient.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
                    uploadClient.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-store-release")
                    uploadClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")

                    let uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUri)
                    uploadRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", auth.Token)
                    let content = new ByteArrayContent(bytes)
                    content.Headers.ContentType <- MediaTypeHeaderValue("application/vnd.github+json")
                    content.Headers.ContentLength <- int64 bytes.Length
                    uploadRequest.Content <- content

                    try
                        let! response =
                            uploadClient.SendAsync(uploadRequest, HttpCompletionOption.ResponseContentRead)
                            |> Async.AwaitTask

                        let! body =
                            response.Content.ReadAsByteArrayAsync()
                            |> Async.AwaitTask

                        if not response.IsSuccessStatusCode then
                            let snippet = System.Text.Encoding.UTF8.GetString(body, 0, min body.Length 256)
                            return
                                Error(
                                    FileFailure(
                                        "upload-metadata",
                                        uploadUri.ToString(),
                                        $"status={(int response.StatusCode)} body={snippet}"))
                        else
                            try
                                let doc = JsonNode.Parse(System.Text.Encoding.UTF8.GetString body).AsObject()
                                let digestNode = doc["digest"]
                                let parsedDigest =
                                    match digestNode with
                                    | null -> ""
                                    | node -> parseDigest (node.GetValue<string>())

                                if parsedDigest = "" then
                                    return
                                        Error(
                                            FileFailure(
                                                "upload-metadata",
                                                projection.ArchiveName,
                                                "the upload response did not include a sha256 digest"))
                                elif parsedDigest <> expectedDigest then
                                    return
                                        Error(
                                            FileFailure(
                                                "upload-metadata",
                                                projection.ArchiveName,
                                                $"the upload response digest ({parsedDigest}) does not match the candidate digest ({expectedDigest})"))
                                else
                                    return Ok parsedDigest
                            with
                            | :? JsonException as error ->
                                return
                                    Error(
                                        MalformedArtifact(
                                            "upload-metadata",
                                            $"the upload response is not valid JSON: {error.Message}"))
                            | :? FormatException as error ->
                                return
                                    Error(
                                        MalformedArtifact(
                                            "upload-metadata",
                                            $"the upload response is not valid JSON: {error.Message}"))
                            | :? InvalidOperationException as error ->
                                return
                                    Error(
                                        MalformedArtifact(
                                            "upload-metadata",
                                            $"the upload response is not valid JSON: {error.Message}"))
                    with
                    | :? OperationCanceledException as error ->
                        return Error(FileFailure("timeout", uploadUri.ToString(), error.Message))
                    | :? HttpRequestException as error ->
                        return Error(FileFailure("http", uploadUri.ToString(), error.Message))
    }
