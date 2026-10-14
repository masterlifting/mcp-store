module Release.Tests.Integrity

open Expecto

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

open BuildProvenance
open ReleaseCompare
open ReleaseEvaluate
open ReleaseMetadata
open ReleasePublish
open ReleaseTransaction

type private Producer =
    { ComponentId: string
      Version: string
      ArchiveName: string
      EntryDll: string
      EntryAllowlist: string list
      Sdk: string
      Runtime: string
      Platform: string
      Policy: ReleasePolicy.PolicyProjection }

type private Candidate =
    { Producer: Producer
      ArchiveBytes: byte[]
      ArchiveSha: string
      ManifestSha: string
      Revision: string
      Dto: CandidateDto
      Projection: ProducerMetadataProjection
      Verified: VerifiedCandidate }

type private ScriptedResponse =
    | TextResponse of int * string
    | ByteResponse of int * byte[]

type private CapturedRequest =
    { Method: string
      Url: string
      Authorization: string
      Body: byte[] }

type private IntegrityRecordingHandler() =
    inherit HttpMessageHandler()
    let responses = Queue<ScriptedResponse>()
    let requests = ResizeArray<CapturedRequest>()

    member _.Queue(response) = responses.Enqueue response
    member _.Requests = requests.ToArray()

    override _.SendAsync(request, cancellationToken) = task {
        let bytes =
            if isNull request.Content then [||]
            else
                let! content = request.Content.ReadAsByteArrayAsync(cancellationToken)
                content

        let authorization =
            match request.Headers.Authorization with
            | null -> ""
            | value -> value.ToString()

        requests.Add
            { Method = request.Method.Method
              Url = request.RequestUri.ToString()
              Authorization = authorization
              Body = bytes }

        if responses.Count = 0 then
            raise (InvalidOperationException $"No scripted response for {request.Method} {request.RequestUri}")

        let response = new HttpResponseMessage()
        response.RequestMessage <- request

        match responses.Dequeue() with
        | TextResponse(status, body) ->
            response.StatusCode <- enum<HttpStatusCode> status
            response.Content <- new StringContent(body)
        | ByteResponse(status, body) ->
            response.StatusCode <- enum<HttpStatusCode> status
            response.Content <- new ByteArrayContent(body)

        return response
    }

let private sha256 (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private createTransport (handler: HttpMessageHandler) : HttpTransport =
    let configure (client: HttpClient) =
        client.Timeout <- TimeSpan.FromSeconds 10.0
        client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-store-integrity-tests")
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
        client

    let createApi () =
        let client = new HttpClient(handler, disposeHandler = false)
        client.BaseAddress <- Uri "https://api.github.com/"
        configure client

    let createUpload () = configure (new HttpClient(handler, disposeHandler = false))
    let createRedirect () = configure (new HttpClient(handler, disposeHandler = false))

    { CreateApiClient = createApi
      CreateUploadClient = createUpload
      CreateRedirectClient = createRedirect }

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private loadProducer componentId =
    let configPath = Path.Combine(repositoryRoot, componentId, "release", "Config.fsx")
    let bytes = File.ReadAllBytes configPath
    let policy =
        match ReleasePolicy.parseBytes configPath bytes with
        | Ok projection -> projection
        | Error error -> failwithf "%s canonical policy failed to parse: %s" componentId (ReleaseError.message error)

    { ComponentId = policy.ComponentId
      Version = policy.Version
      ArchiveName = policy.ArchiveName
      EntryDll = policy.EntryDll
      EntryAllowlist = policy.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ]
      Sdk = policy.SdkVersion
      Runtime = policy.RuntimeVersion
      Platform = policy.Platform
      Policy = policy }

let private producers = [ "dotnet"; "workflow" ] |> List.map loadProducer

let private withUploadEnabled action =
    async {
        let previous = Environment.GetEnvironmentVariable "MCP_RELEASE_DO_UPLOAD"
        Environment.SetEnvironmentVariable("MCP_RELEASE_DO_UPLOAD", "true")
        try return! action ()
        finally Environment.SetEnvironmentVariable("MCP_RELEASE_DO_UPLOAD", previous)
    }

let private zipBytes (entries: (string * byte[]) list) =
    use output = new MemoryStream()
    do
        use archive = new ZipArchive(output, ZipArchiveMode.Create, true)
        for name, bytes in entries do
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use entryStream = entry.Open()
            entryStream.Write(bytes, 0, bytes.Length)
    output.ToArray()

let private writeCandidate (root: string) (producer: Producer) (revision: string) : Async<Candidate> =
    async {
        let payloads =
            producer.EntryAllowlist
            |> List.filter (fun name -> name <> "NOTICE.txt" && name <> "distribution.json")
            |> List.map (fun name -> name, Encoding.UTF8.GetBytes($"candidate:{producer.ComponentId}:{name}"))
            |> fun files -> files @ [ "NOTICE.txt", Encoding.UTF8.GetBytes "release integrity fixture" ]

        let manifest = JsonObject()
        manifest["schemaVersion"] <- JsonValue.Create 1
        manifest["id"] <- JsonValue.Create producer.ComponentId
        manifest["version"] <- JsonValue.Create producer.Version
        manifest["revision"] <- JsonValue.Create revision
        manifest["sdk"] <- JsonValue.Create producer.Sdk
        manifest["entryDll"] <- JsonValue.Create producer.EntryDll
        manifest["archive"] <- JsonValue.Create producer.ArchiveName

        let files = JsonArray()
        for name, bytes in payloads do
            if name <> "NOTICE.txt" then
                let entry = JsonObject()
                entry["path"] <- JsonValue.Create name
                entry["sha256"] <- JsonValue.Create(sha256 bytes)
                files.Add entry
        manifest["files"] <- files

        let archiveFiles = JsonArray()
        for name in producer.EntryAllowlist do archiveFiles.Add(JsonValue.Create name)
        manifest["archiveFiles"] <- archiveFiles

        let manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
        let archiveBytes = zipBytes (payloads @ [ "distribution.json", manifestBytes ])
        let archiveSha = sha256 archiveBytes
        let manifestSha = sha256 manifestBytes
        // Inner archive and standalone manifest are produced first; preparation.json binds the
        // four known inner/manifest hashes and sizes, then the three-entry outer ZIP is
        // assembled so the receipt never references a stale outer digest.
        let prepared = JsonObject()
        prepared["schemaVersion"] <- JsonValue.Create 1
        prepared["innerArchiveSha256"] <- JsonValue.Create archiveSha
        prepared["innerArchiveSize"] <- JsonValue.Create(int64 archiveBytes.Length)
        prepared["manifestSha256"] <- JsonValue.Create manifestSha
        prepared["manifestSize"] <- JsonValue.Create(int64 manifestBytes.Length)
        let preparationBytes = Encoding.UTF8.GetBytes(prepared.ToJsonString())
        let outerBytes =
            zipBytes
                [ producer.ArchiveName, archiveBytes
                  "distribution.json", manifestBytes
                  "preparation.json", preparationBytes ]
        let outerSha = sha256 outerBytes
        let preparation =
            match ReleaseHandoff.parsePreparationReceipt (prepared.ToJsonString()) "preparation.json" with
            | Ok receipt -> receipt
            | Error error -> failwithf "preparation fixture failed: %s" (ReleaseError.message error)
        let expected : ReleaseHandoff.ExpectedMetadata =
            { ArtifactId = "controlled-artifact"
              ArtifactName = producer.ComponentId + "-candidate"
              Role = "candidate"
              Component = producer.ComponentId
              Repository = "masterlifting/mcp-store"
              RunId = "controlled-run"
              Attempt = "1"
              Sha = revision }
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
            | Ok digest -> digest
            | Error error -> failwithf "outer transport digest fixture failed: %s" (ReleaseError.message error)
        let evidence : ReleaseHandoff.ArtifactEvidence =
            { Expected = expected
              Observed = observed
              ActualOuterDigest = outerDigest
              TransportSourcePath = "controlled-artifact.zip"
              TransportLength = int64 outerBytes.Length
              RawTransportBytes = outerBytes }
        let snapshot =
            match ReleaseHandoff.readRawTransport "controlled-artifact.zip" outerBytes evidence producer.Policy with
            | Ok value -> value
            | Error error -> failwithf "prepared candidate transport failed validation: %s" (ReleaseError.message error)

        if preparation.InnerArchiveSha256.Value <> snapshot.InnerArchiveSha256.Value
           || preparation.InnerArchiveSize <> snapshot.InnerArchiveSize
           || preparation.ManifestSha256.Value <> (snapshot.ManifestSha256 |> Option.map (fun value -> value.Value) |> Option.defaultValue "")
           || preparation.ManifestSize <> (snapshot.ManifestSize |> Option.defaultValue -1L) then
            failwith "preparation receipt did not match the transport-verified snapshot"

        let! verifiedResult =
            VerifiedCandidate.createFromSnapshot
                snapshot
                producer.Policy
                producer.ArchiveName
                revision
                (Path.Combine(root, $"{producer.ComponentId}-metadata-local.json"))
                "masterlifting"
                "mcp-store"
        let verified =
            match verifiedResult with
            | Ok value -> value
            | Error error -> failwithf "verified candidate factory rejected a valid preparation: %s" (ReleaseError.message error)

        return
            { Producer = producer
              ArchiveBytes = archiveBytes
              ArchiveSha = archiveSha
              ManifestSha = manifestSha
              Revision = revision
              Dto = verified.Dto
              Projection = verified.Projection
              Verified = verified }
    }

let private releaseJson (assets: (string * string option * int64) list) =
    let root = JsonObject()
    root["id"] <- JsonValue.Create 99L
    root["immutable"] <- JsonValue.Create false
    let items = JsonArray()
    for name, digest, id in assets do
        let item = JsonObject()
        item["name"] <- JsonValue.Create name
        match digest with
        | Some value -> item["digest"] <- JsonValue.Create value
        | None -> ()
        item["id"] <- JsonValue.Create id
        items.Add item
    root["assets"] <- items
    root.ToJsonString()

let private queueRelease (handler: IntegrityRecordingHandler) assets =
    handler.Queue(TextResponse(200, releaseJson assets))

let private queueAsset (handler: IntegrityRecordingHandler) (bytes: byte[]) =
    handler.Queue(ByteResponse(200, bytes))

let private locator producer =
    { Owner = "masterlifting"
      Repository = "mcp-store"
      Tag = "v" + producer.Version }

let private auth = Some { Token = "sentinel-test-token" }

let private runTransaction root handler candidate =
    ReleaseTransaction.run
        (createTransport handler)
        auth
        true
        (Path.Combine(root, "downloads"))
        (locator candidate.Producer)
        candidate.Verified

let private assertNoPublishedIdentity label (resolution: Resolution) =
    Expect.equal resolution.Status "failed" $"{label}: failed status"
    Expect.isFalse resolution.UploadCompleted $"{label}: no upload completed"
    Expect.isNone resolution.PublishedArchiveSha256 $"{label}: archive identity absent"
    Expect.isNone resolution.PublishedManifestSha256 $"{label}: manifest identity absent"
    Expect.isNone resolution.PublishedRevision $"{label}: revision identity absent"
    Expect.isNone resolution.PublishedMetadataAssetSha256 $"{label}: metadata identity absent"

let private makeMetadataBytes projection =
    Encoding.UTF8.GetBytes(render projection)

let private testPreflightFailures root = async {
    for producer in producers do
        let! candidate = writeCandidate root producer "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
        let goodMeta = candidate.Projection
        let mismatch = { goodMeta with Version = "9.9.9" }
        let cases =
            [ "observed-mismatch", Some(makeMetadataBytes mismatch), Some(sha256 (makeMetadataBytes mismatch)), false
              "orphan-compatible-metadata", Some(makeMetadataBytes goodMeta), None, false
              "corrupt-digest", Some(makeMetadataBytes goodMeta), Some(String.replicate 64 "0"), false
              "malformed-json", Some(Encoding.UTF8.GetBytes "{not-json"), None, false
              "unsupported-schema", Some(makeMetadataBytes { goodMeta with SchemaVersion = metadataSchemaVersion + 1 }), None, false
              "missing-metadata", None, None, true
              "metadata-http-error", None, None, false ]

        for caseName, metadataBytes, claimedDigest, metadataMissing in cases do
            use handler = new IntegrityRecordingHandler()
            queueRelease handler []

            if caseName = "metadata-http-error" then
                handler.Queue(TextResponse(503, "metadata observation unavailable"))
            elif metadataMissing then
                handler.Queue(TextResponse(404, "release missing"))
            else
                let body = Option.get metadataBytes
                let digest = claimedDigest |> Option.defaultValue ("sha256:" + sha256 body)
                queueRelease handler [ candidate.Dto.MetadataAssetName, Some digest, 2L ]
                queueAsset handler body

            let! outcome = runTransaction root handler candidate
            match outcome with
            | Error error -> failtestf "%s/%s transaction returned error: %s" producer.ComponentId caseName (ReleaseError.message error)
            | Ok (resolution, code) ->
                assertNoPublishedIdentity $"{producer.ComponentId}/{caseName}" resolution
                Expect.equal code 1 $"{producer.ComponentId}/{caseName} transaction exit"
                Expect.equal
                    (handler.Requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                    0
                    $"{producer.ComponentId}/{caseName} issues zero POST requests"
                Expect.isTrue (resolution.Reason.Length > 0) $"{producer.ComponentId}/{caseName} has actionable failure reason"
}

let private testInitialObservationDigestFailures root = async {
    let digestCases =
        [ "missing", None
          "malformed", Some "sha256:NOT-A-DIGEST"
          "mismatch", Some("sha256:" + String.replicate 64 "0") ]

    for producer in producers do
        let! candidate = writeCandidate root producer "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"

        for digestCase, claimedDigest in digestCases do
            use archiveHandler = new IntegrityRecordingHandler()
            queueRelease archiveHandler [ producer.ArchiveName, claimedDigest, 1L ]
            queueAsset archiveHandler candidate.ArchiveBytes
            queueRelease archiveHandler []

            let! archiveOutcome = runTransaction root archiveHandler candidate

            match archiveOutcome with
            | Error error -> failtestf "%s/archive-observation/%s returned error: %s" producer.ComponentId digestCase (ReleaseError.message error)
            | Ok (resolution, code) ->
                assertNoPublishedIdentity $"{producer.ComponentId}/archive-observation/{digestCase}" resolution
                Expect.equal code 1 $"{producer.ComponentId}/archive-observation/{digestCase} exits failed"
                Expect.isTrue
                    (resolution.Reason.Contains("digest", StringComparison.OrdinalIgnoreCase)
                     || resolution.Reason.Contains("SHA-256", StringComparison.Ordinal))
                    $"{producer.ComponentId}/archive-observation/{digestCase} reports the claimed digest failure: {resolution.Reason}"
                Expect.equal
                    (archiveHandler.Requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                    0
                    $"{producer.ComponentId}/archive-observation/{digestCase} issues no POST"
                Expect.equal
                    (archiveHandler.Requests |> Array.filter (fun request -> request.Method = "GET") |> Array.length)
                    3
                    $"{producer.ComponentId}/archive-observation/{digestCase} performs one release lookup, one asset download, and the metadata lookup"

            use metadataHandler = new IntegrityRecordingHandler()
            let metadataBytes = makeMetadataBytes candidate.Projection
            queueRelease metadataHandler []
            queueRelease metadataHandler [ candidate.Dto.MetadataAssetName, claimedDigest, 2L ]
            queueAsset metadataHandler metadataBytes

            let! metadataOutcome = runTransaction root metadataHandler candidate

            match metadataOutcome with
            | Error error -> failtestf "%s/metadata-observation/%s returned error: %s" producer.ComponentId digestCase (ReleaseError.message error)
            | Ok (resolution, code) ->
                assertNoPublishedIdentity $"{producer.ComponentId}/metadata-observation/{digestCase}" resolution
                Expect.equal code 1 $"{producer.ComponentId}/metadata-observation/{digestCase} exits failed"
                Expect.isTrue
                    (resolution.Reason.Contains("digest", StringComparison.OrdinalIgnoreCase)
                     || resolution.Reason.Contains("SHA-256", StringComparison.Ordinal))
                    $"{producer.ComponentId}/metadata-observation/{digestCase} reports the claimed digest failure: {resolution.Reason}"
                Expect.equal
                    (metadataHandler.Requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                    0
                    $"{producer.ComponentId}/metadata-observation/{digestCase} issues no POST"
                Expect.equal
                    (metadataHandler.Requests |> Array.filter (fun request -> request.Method = "GET") |> Array.length)
                    3
                    $"{producer.ComponentId}/metadata-observation/{digestCase} performs one archive lookup, one metadata lookup, and one asset download"
}

let private testMetadataIdentityMutations root = async {
    let oldRevision = "fc0aefa9db0fb7262ee6ca1b54bd6bbb752b8b01"

    for producer in producers do
        let! candidate = writeCandidate root producer oldRevision
        let mutations =
            [ "componentId", "componentId", { candidate.Projection with ComponentId = "other-producer" }
              "version", "version", { candidate.Projection with Version = "mismatch-" + producer.Version }
              "assetUri", "assetUri", { candidate.Projection with AssetUri = "https://github.com/other/repo/releases/download/wrong/v1.zip" }
              "archiveName", "archiveName", { candidate.Projection with ArchiveName = "different.zip" }
              "archiveSha256", "archiveSha256", { candidate.Projection with ArchiveSha256 = String.replicate 64 "a" }
              "manifestSha256", "manifestSha256", { candidate.Projection with ManifestSha256 = String.replicate 64 "b" }
              "publishedRevision", "publishedRevision", { candidate.Projection with PublishedRevision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1" }
              "producerSdk-wrong", "producerSdk", { candidate.Projection with ProducerSdk = Some "different-sdk" }
              "producerSdk-missing", "producerSdk", { candidate.Projection with ProducerSdk = None }
              "producerRuntime-wrong", "producerRuntime", { candidate.Projection with ProducerRuntime = Some "different-runtime" }
              "producerRuntime-missing", "producerRuntime", { candidate.Projection with ProducerRuntime = None }
              "producerPlatform-wrong", "producerPlatform", { candidate.Projection with ProducerPlatform = Some "other-platform" }
              "producerPlatform-missing", "producerPlatform", { candidate.Projection with ProducerPlatform = None } ]

        for caseName, field, projection in mutations do
            use handler = new IntegrityRecordingHandler()
            let archiveName = producer.ArchiveName
            let archiveDigest = "sha256:" + candidate.ArchiveSha
            let metadataBytes = makeMetadataBytes projection
            let metadataDigest = "sha256:" + sha256 metadataBytes
            queueRelease handler [ archiveName, Some archiveDigest, 1L; candidate.Dto.MetadataAssetName, Some metadataDigest, 2L ]
            queueAsset handler candidate.ArchiveBytes
            queueRelease handler [ archiveName, Some archiveDigest, 1L; candidate.Dto.MetadataAssetName, Some metadataDigest, 2L ]
            queueAsset handler metadataBytes

            let! outcome = runTransaction root handler candidate
            match outcome with
            | Error error -> failtestf "%s/%s returned error: %s" producer.ComponentId caseName (ReleaseError.message error)
            | Ok (resolution, code) ->
                assertNoPublishedIdentity $"{producer.ComponentId}/{caseName}" resolution
                Expect.equal code 1 $"{producer.ComponentId}/{caseName} transaction exit"
                Expect.isTrue
                    (resolution.Reason.Contains(field, StringComparison.Ordinal))
                    $"{producer.ComponentId}/{caseName} identifies the mismatched field; actual reason: {resolution.Reason}"
                Expect.equal
                    (handler.Requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                    0
                    $"{producer.ComponentId}/{caseName} issues zero POST requests"

        for malformedName, mutate in
            [ "wrong-schema", fun (node: JsonObject) -> node["schemaVersion"] <- JsonValue.Create(metadataSchemaVersion + 1)
              "missing-schema", fun (node: JsonObject) -> node.Remove "schemaVersion" |> ignore
              "missing-required-assetUri", fun (node: JsonObject) -> node.Remove "assetUri" |> ignore
              "producerSdk-malformed", fun (node: JsonObject) -> node["producerSdk"] <- JsonValue.Create 7
              "producerRuntime-malformed", fun (node: JsonObject) -> node["producerRuntime"] <- JsonValue.Create 7
              "producerPlatform-malformed", fun (node: JsonObject) -> node["producerPlatform"] <- JsonValue.Create 7 ] do
            use handler = new IntegrityRecordingHandler()
            let bodyNode = JsonNode.Parse(render candidate.Projection).AsObject()
            mutate bodyNode
            let metadataBytes = Encoding.UTF8.GetBytes(bodyNode.ToJsonString())
            let digest = "sha256:" + sha256 metadataBytes
            queueRelease handler [ producer.ArchiveName, Some("sha256:" + candidate.ArchiveSha), 1L
                                   candidate.Dto.MetadataAssetName, Some digest, 2L ]
            queueAsset handler candidate.ArchiveBytes
            queueRelease handler [ producer.ArchiveName, Some("sha256:" + candidate.ArchiveSha), 1L
                                   candidate.Dto.MetadataAssetName, Some digest, 2L ]
            queueAsset handler metadataBytes

            let! outcome = runTransaction root handler candidate
            match outcome with
            | Error error -> failtestf "%s/%s returned error: %s" producer.ComponentId malformedName (ReleaseError.message error)
            | Ok (resolution, code) ->
                assertNoPublishedIdentity $"{producer.ComponentId}/{malformedName}" resolution
                Expect.equal code 1 $"{producer.ComponentId}/{malformedName} transaction exit"
                Expect.isTrue (resolution.Reason.Length > 0) $"{producer.ComponentId}/{malformedName} reports malformed metadata"
                Expect.equal
                    (handler.Requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                    0
                    $"{producer.ComponentId}/{malformedName} issues zero POST requests"
}

let private testPublishedRevisionAndMetadataRecovery root = async {
    let publishedRevision = "fc0aefa9db0fb7262ee6ca1b54bd6bbb752b8b01"
    let candidateRevision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"

    for producer in producers do
        let! candidate = writeCandidate root producer candidateRevision
        let! published = writeCandidate root producer publishedRevision
        let publishedObservation =
            match ReleaseCompare.observeBytes "published-fixture" published.ArchiveBytes with
            | Ok observation -> observation
            | Error error -> failtestf "%s published fixture invalid: %s" producer.ComponentId (ReleaseError.message error)
        let projection =
            makeProjection producer.ComponentId producer.Version candidate.Dto.AssetUri producer.ArchiveName
                publishedObservation.ArchiveSha256 (Option.defaultValue "" publishedObservation.ManifestSha256) publishedRevision
                (Some producer.Sdk) (Some producer.Runtime) (Some producer.Platform)
        let metadataBytes = makeMetadataBytes projection
        let archiveAsset = producer.ArchiveName, Some("sha256:" + publishedObservation.ArchiveSha256), 1L
        let metadataAsset = candidate.Dto.MetadataAssetName, Some("sha256:" + sha256 metadataBytes), 2L

        use noopHandler = new IntegrityRecordingHandler()
        queueRelease noopHandler [ archiveAsset ]
        queueAsset noopHandler published.ArchiveBytes
        queueRelease noopHandler [ archiveAsset; metadataAsset ]
        queueAsset noopHandler metadataBytes
        let! noopOutcome = runTransaction root noopHandler candidate
        match noopOutcome with
        | Error error -> failtestf "%s revision-only no-op returned error: %s" producer.ComponentId (ReleaseError.message error)
        | Ok (resolution, code) ->
            Expect.equal code 0 $"{producer.ComponentId} revision-only no-op exits successfully"
            Expect.equal resolution.Status "unchanged" $"{producer.ComponentId} revision-only no-op classification"
            Expect.equal resolution.PublishedRevision (Some publishedRevision) $"{producer.ComponentId} reports observed embedded published revision, not candidate HEAD"
            Expect.notEqual resolution.PublishedRevision (Some candidateRevision) $"{producer.ComponentId} no candidate revision substitution"
            Expect.equal
                (noopHandler.Requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                0
                $"{producer.ComponentId} matching revision-only metadata is a no-op"

        use recoveryHandler = new IntegrityRecordingHandler()
        queueRelease recoveryHandler [ archiveAsset ]
        queueAsset recoveryHandler published.ArchiveBytes
        queueRelease recoveryHandler [ archiveAsset ]
        queueRelease recoveryHandler [ archiveAsset ]
        recoveryHandler.Queue(TextResponse(201, "{}"))
        queueRelease recoveryHandler [ archiveAsset; metadataAsset ]
        queueAsset recoveryHandler metadataBytes
        let! recoveryOutcome = withUploadEnabled (fun () -> runTransaction root recoveryHandler candidate)
        match recoveryOutcome with
        | Error error -> failtestf "%s metadata recovery returned error: %s" producer.ComponentId (ReleaseError.message error)
        | Ok (resolution, code) ->
            Expect.equal code 0 $"{producer.ComponentId} metadata-only recovery exits successfully"
            Expect.equal resolution.Status "unchanged" $"{producer.ComponentId} metadata-only recovery remains unchanged"
            Expect.isTrue resolution.UploadCompleted $"{producer.ComponentId} metadata-only recovery records upload completion"
            Expect.equal resolution.PublishedArchiveSha256 (Some publishedObservation.ArchiveSha256) $"{producer.ComponentId} recovery preserves observed archive digest"
            Expect.equal resolution.PublishedManifestSha256 publishedObservation.ManifestSha256 $"{producer.ComponentId} recovery preserves observed manifest digest"
            Expect.equal resolution.PublishedRevision (Some publishedRevision) $"{producer.ComponentId} recovery preserves observed published revision"
            Expect.equal
                (recoveryHandler.Requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                1
                $"{producer.ComponentId} metadata-only recovery performs one POST"
}

let private testValidNewPublication root = async {
    for producer in producers do
        let! candidate = writeCandidate root producer "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
        let archiveAsset = producer.ArchiveName, Some("sha256:" + candidate.ArchiveSha), 1L
        let metadataBytes = makeMetadataBytes candidate.Projection
        let metadataAsset = candidate.Dto.MetadataAssetName, Some("sha256:" + sha256 metadataBytes), 2L

        use handler = new IntegrityRecordingHandler()
        queueRelease handler []
        queueRelease handler []
        queueRelease handler []
        handler.Queue(TextResponse(201, "{}"))
        queueRelease handler [ archiveAsset ]
        queueAsset handler candidate.ArchiveBytes
        queueRelease handler [ archiveAsset ]
        handler.Queue(TextResponse(201, "{}"))
        queueRelease handler [ archiveAsset; metadataAsset ]
        queueAsset handler metadataBytes

        let! outcome = withUploadEnabled (fun () -> runTransaction root handler candidate)
        match outcome with
        | Error error -> failtestf "%s new publication returned error: %s" producer.ComponentId (ReleaseError.message error)
        | Ok (resolution, code) ->
            Expect.equal code 0 $"{producer.ComponentId} new publication exits successfully"
            Expect.equal resolution.Status "published" $"{producer.ComponentId} new publication classification"
            Expect.isTrue resolution.UploadCompleted $"{producer.ComponentId} new publication records completion"
            Expect.equal
                (handler.Requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                2
                $"{producer.ComponentId} uploads archive and metadata exactly once"
            Expect.isTrue
                (handler.Requests
                 |> Array.filter (fun request -> request.Method = "POST")
                 |> Array.forall (fun request -> request.Authorization = "Bearer sentinel-test-token"))
                $"{producer.ComponentId} scripted requests use only the fake sentinel token"
}

let private testClaimedDigestFailures root = async {
    for producer in producers do
        let! candidate = writeCandidate root producer "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
        let digestCases =
            [ "missing", None
              "malformed", Some "sha256:NOT-A-DIGEST"
              "mismatch", Some("sha256:" + String.replicate 64 "0") ]

        for phase in [ "idempotent-observation"; "post-upload-readback" ] do
            for digestCase, claimedDigest in digestCases do
                use handler = new IntegrityRecordingHandler()
                let asset = producer.ArchiveName, claimedDigest, 1L
                if phase = "idempotent-observation" then
                    queueRelease handler [ asset ]
                    queueAsset handler candidate.ArchiveBytes
                else
                    queueRelease handler []
                    handler.Queue(TextResponse(201, "{}"))
                    queueRelease handler [ asset ]
                    queueAsset handler candidate.ArchiveBytes

                let! outcome =
                    withUploadEnabled (fun () ->
                        ReleasePublish.uploadCandidateBytes
                            { Token = "sentinel-test-token" }
                            (locator producer)
                            producer.ArchiveName
                            candidate.ArchiveBytes
                            (Some candidate.ArchiveSha)
                            (createTransport handler))

                match outcome with
                | Ok digest -> failtestf "%s/%s/%s accepted claimed digest and returned %s" producer.ComponentId phase digestCase digest
                | Error error ->
                    let message = ReleaseError.message error
                    Expect.isTrue
                        (message.Contains("digest", StringComparison.OrdinalIgnoreCase)
                         || message.Contains("SHA-256", StringComparison.Ordinal))
                        $"{producer.ComponentId}/{phase}/{digestCase} fails on the API digest: {message}"

                let requests = handler.Requests
                let posts = requests |> Array.filter (fun request -> request.Method = "POST")
                let gets = requests |> Array.filter (fun request -> request.Method = "GET")
                let expectedPosts = if phase = "idempotent-observation" then 0 else 1
                let expectedGets = if phase = "idempotent-observation" then 2 else 3
                Expect.equal posts.Length expectedPosts $"{producer.ComponentId}/{phase}/{digestCase} exact POST count; invalid digest never retries"
                Expect.equal gets.Length expectedGets $"{producer.ComponentId}/{phase}/{digestCase} exact observation/readback GET count"
                if posts.Length = 1 then
                    Expect.equal posts[0].Authorization "Bearer sentinel-test-token" $"{producer.ComponentId}/{phase}/{digestCase} POST uses fake token only"
}

let private testTransactionReadbackDigestFailures root = async {
    for producer in producers do
        let! candidate = writeCandidate root producer "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
        for digestCase, claimedDigest in
            [ "missing", None
              "malformed", Some "sha256:NOT-A-DIGEST"
              "mismatch", Some("sha256:" + String.replicate 64 "0") ] do
            use handler = new IntegrityRecordingHandler()
            let archiveAsset = producer.ArchiveName, claimedDigest, 1L
            queueRelease handler []
            queueRelease handler []
            queueRelease handler []
            handler.Queue(TextResponse(201, "{}"))
            queueRelease handler [ archiveAsset ]
            queueAsset handler candidate.ArchiveBytes

            let! outcome = withUploadEnabled (fun () -> runTransaction root handler candidate)
            match outcome with
            | Error error -> failtestf "%s post-upload/%s transaction returned error: %s" producer.ComponentId digestCase (ReleaseError.message error)
            | Ok (resolution, code) ->
                assertNoPublishedIdentity $"{producer.ComponentId}/post-upload/{digestCase}" resolution
                Expect.equal code 1 $"{producer.ComponentId}/post-upload/{digestCase} transaction exits failed"
                Expect.isTrue
                    (resolution.Reason.Contains("digest", StringComparison.OrdinalIgnoreCase)
                     || resolution.Reason.Contains("SHA-256", StringComparison.Ordinal))
                    $"{producer.ComponentId}/post-upload/{digestCase} exposes digest verification failure: {resolution.Reason}"
                let requests = handler.Requests
                Expect.equal
                    (requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                    1
                    $"{producer.ComponentId}/post-upload/{digestCase} issues one POST and does not retry"
                Expect.equal
                    (requests |> Array.filter (fun request -> request.Method = "GET") |> Array.length)
                    5
                    $"{producer.ComponentId}/post-upload/{digestCase} observes before/after upload and downloads readback"
}

let private testMetadataReadbackDigestFailures root = async {
    let digestCases =
        [ "missing", None
          "malformed", Some "sha256:NOT-A-DIGEST"
          "mismatch", Some("sha256:" + String.replicate 64 "0") ]

    for producer in producers do
        let! candidate = writeCandidate root producer "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"

        for digestCase, claimedDigest in digestCases do
            use handler = new IntegrityRecordingHandler()
            let archiveAsset = producer.ArchiveName, Some("sha256:" + candidate.ArchiveSha), 1L
            let metadataAsset = candidate.Dto.MetadataAssetName, claimedDigest, 2L
            let metadataBytes = makeMetadataBytes candidate.Projection

            queueRelease handler []
            queueRelease handler []
            queueRelease handler []
            handler.Queue(TextResponse(201, "{}"))
            queueRelease handler [ archiveAsset ]
            queueAsset handler candidate.ArchiveBytes
            queueRelease handler [ archiveAsset ]
            handler.Queue(TextResponse(201, "{}"))
            queueRelease handler [ archiveAsset; metadataAsset ]
            queueAsset handler metadataBytes

            let! outcome = withUploadEnabled (fun () -> runTransaction root handler candidate)

            match outcome with
            | Error error -> failtestf "%s/metadata-readback/%s returned error: %s" producer.ComponentId digestCase (ReleaseError.message error)
            | Ok (resolution, code) ->
                assertNoPublishedIdentity $"{producer.ComponentId}/metadata-readback/{digestCase}" resolution
                Expect.equal code 1 $"{producer.ComponentId}/metadata-readback/{digestCase} exits failed"
                Expect.isTrue
                    (resolution.Reason.Contains("digest", StringComparison.OrdinalIgnoreCase)
                     || resolution.Reason.Contains("SHA-256", StringComparison.Ordinal))
                    $"{producer.ComponentId}/metadata-readback/{digestCase} reports the readback digest failure: {resolution.Reason}"

                let requests = handler.Requests
                Expect.equal
                    (requests |> Array.filter (fun request -> request.Method = "POST") |> Array.length)
                    2
                    $"{producer.ComponentId}/metadata-readback/{digestCase} posts archive and metadata once without retry"
                Expect.equal
                    (requests |> Array.filter (fun request -> request.Method = "GET") |> Array.length)
                    8
                    $"{producer.ComponentId}/metadata-readback/{digestCase} observes and reads back both assets"
}

let tests =
    testCaseAsync "W5 actual production transaction integrity regressions" <| async {
        let root = Fixture.newTempDir "mcp-release-integrity-tests"
        try
            do! testPreflightFailures root
            do! testInitialObservationDigestFailures root
            do! testMetadataIdentityMutations root
            do! testPublishedRevisionAndMetadataRecovery root
            do! testValidNewPublication root
            do! testClaimedDigestFailures root
            do! testTransactionReadbackDigestFailures root
            do! testMetadataReadbackDigestFailures root
        finally
            Fixture.safeDeleteDir root
    }
