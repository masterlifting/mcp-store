// role: reusable module/helper
// Bounded read-only GitHub Releases transport for one pinned asset: exact canonical
// HTTPS path, approved hosts only, manual redirects, and no credentials or fallbacks.
module ResolverTransport

open System
open System.IO
open System.Net
open System.Net.Http
open SharedFailure

type Transport = { CreateClient: unit -> HttpClient }

let private repositoryOwner = "masterlifting"
let private repositoryName = "mcp-store"
let private releaseHost = "github.com"

// The asset CDN hosts GitHub redirects a release download to; nothing else is followed.
let private assetHosts =
    [ "github.com"; "release-assets.githubusercontent.com"; "objects.githubusercontent.com" ]

let private requestTimeout = TimeSpan.FromSeconds 30.0
let private maxRedirects = 5

let defaultTransport () : Transport =
    { CreateClient =
        fun () ->
            let handler = new HttpClientHandler()
            handler.AllowAutoRedirect <- false
            let client = new HttpClient(handler)
            client.Timeout <- requestTimeout
            client.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-store-resolver")
            client }

let private isExactHttpsOrigin (host: string) (uri: Uri) : bool =
    uri.Scheme = Uri.UriSchemeHttps
    && uri.IsDefaultPort
    && String.IsNullOrEmpty uri.UserInfo
    && String.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)

let private isApprovedRedirectTarget (uri: Uri) : bool =
    assetHosts |> List.exists (fun host -> isExactHttpsOrigin host uri)

// Diagnostics keep the signed CDN query string out of every error message.
let private location (uri: Uri) : string =
    if isNull uri then "<null>" else $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}"

let assetUri (pin: DistributionPin.Pin) : Result<Uri, ReleaseError> =
    let expectedPath =
        $"/{repositoryOwner}/{repositoryName}/releases/download/{pin.ComponentId}/{pin.ArchiveName}"

    match ResolverPath.portablePath pin.ComponentId, ResolverPath.portablePath pin.ArchiveName with
    | Error error, _ -> Error error
    | _, Error error -> Error error
    | Ok (), Ok () ->
        match Uri.TryCreate(pin.AssetUri, UriKind.Absolute) with
        | false, _ -> Error(TransportRejected(pin.AssetUri, "the pin assetUri is not an absolute URI"))
        | true, uri ->
            if not (isExactHttpsOrigin releaseHost uri) then
                Error(TransportRejected(pin.AssetUri, "the asset URI is not an exact HTTPS github.com origin"))
            elif not (String.IsNullOrEmpty uri.Query) || not (String.IsNullOrEmpty uri.Fragment) then
                Error(TransportRejected(pin.AssetUri, "the asset URI carries a query string or fragment"))
            elif uri.AbsolutePath <> expectedPath then
                Error(TransportRejected(pin.AssetUri, $"the asset URI path is not '{expectedPath}'"))
            else
                Ok uri

let private isRedirect (status: HttpStatusCode) : bool =
    status = HttpStatusCode.MovedPermanently
    || status = HttpStatusCode.Found
    || status = HttpStatusCode.SeeOther
    || status = HttpStatusCode.TemporaryRedirect
    || status = HttpStatusCode.PermanentRedirect

let private readBounded (message: HttpResponseMessage) (target: string) : Async<Result<byte[], ReleaseError>> =
    async {
        try
            use! stream = message.Content.ReadAsStreamAsync() |> awaitOperational
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 81920
            let mutable total = 0L
            let mutable finished = false
            let mutable oversize = false

            while not finished && not oversize do
                let! read = stream.ReadAsync(chunk, 0, chunk.Length) |> awaitOperational

                if read = 0 then
                    finished <- true
                else
                    total <- total + int64 read

                    if total > DistributionArchive.maxArchiveBytes then
                        oversize <- true
                    else
                        buffer.Write(chunk, 0, read)

            if oversize then
                return
                    Error(
                        TransportRejected(
                            target,
                            $"the response exceeds the {DistributionArchive.maxArchiveBytes}-byte archive bound"
                        )
                    )
            else
                return Ok(buffer.ToArray())
        with
        | :? OperationCanceledException as error -> return Error(TransportFailure(target, error.Message))
        | :? HttpRequestException as error -> return Error(TransportFailure(target, error.Message))
        | :? IOException as error -> return Error(TransportFailure(target, error.Message))
    }

let private send (transport: Transport) (uri: Uri) : Async<Result<HttpResponseMessage, ReleaseError>> =
    let rec loop (current: Uri) (remaining: int) =
        async {
            if not (isApprovedRedirectTarget current) then
                return
                    Error(TransportRejected(location current, "the request target is not an approved HTTPS GitHub host"))
            else
                let! cancellationToken = Async.CancellationToken
                let client = transport.CreateClient()
                use request = new HttpRequestMessage(HttpMethod.Get, current)

                try
                    let! response =
                        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        |> awaitOperational

                    if isRedirect response.StatusCode then
                        let target = response.Headers.Location
                        response.Dispose()

                        if isNull target then
                            return
                                Error(
                                    TransportRejected(location current, "the redirect response did not supply a Location header")
                                )
                        elif remaining <= 0 then
                            return Error(TransportRejected(location current, "the redirect chain exceeded the bounded limit"))
                        else
                            let next = if target.IsAbsoluteUri then target else Uri(current, target)

                            // The signed CDN query string survives only when the issued Location is followed verbatim.
                            return! loop next (remaining - 1)
                    else
                        return Ok response
                with
                | :? OperationCanceledException as error -> return Error(TransportFailure(location current, error.Message))
                | :? HttpRequestException as error -> return Error(TransportFailure(location current, error.Message))
        }

    loop uri maxRedirects

let download (transport: Transport) (pin: DistributionPin.Pin) : Async<Result<byte[], ReleaseError>> =
    async {
        match assetUri pin with
        | Error error -> return Error error
        | Ok uri ->
            match! send transport uri with
            | Error error -> return Error error
            | Ok response ->
                use message = response
                let target = location uri

                if not message.IsSuccessStatusCode then
                    return
                        Error(
                            TransportRejected(
                                target,
                                $"the response status was {(int message.StatusCode)} {message.ReasonPhrase}"
                            )
                        )
                elif isNull message.Content then
                    return Error(TransportRejected(target, "the response carried no content"))
                else
                    let declared = message.Content.Headers.ContentLength

                    if declared.HasValue && declared.Value > DistributionArchive.maxArchiveBytes then
                        return
                            Error(
                                TransportRejected(
                                    target,
                                    $"the response declares more than the {DistributionArchive.maxArchiveBytes}-byte archive bound"
                                )
                            )
                    else
                        match! readBounded message target with
                        | Error error -> return Error error
                        | Ok bytes ->
                            // The payload is checked here and again before decompression, so unverified bytes cross neither boundary.
                            let actual = ResolverDigest.sha256 bytes

                            if actual <> pin.ArchiveSha256 then
                                return Error(IdentityMismatch("archiveSha256", pin.ArchiveSha256, actual))
                            else
                                return Ok bytes
    }
