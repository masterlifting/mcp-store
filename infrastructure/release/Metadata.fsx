// role: reusable module/helper
// Repo-shared consumer-neutral release-metadata projection loaded as the
// explicit ReleaseMetadata module. Owns the per-component release-metadata
// artifact the workflow publishes alongside the versioned archive: the
// projection identifies the immutable artifact any MCP client needs in
// order to pin the producer release without depending on a
// consumer-specific descriptor.
//
// The shape is the seven-field projection required by the original issue
// (componentId, version, assetUri, archiveSha256, manifestSha256,
// publishedRevision) plus the producer-distribution schema version. The
// transport protocol is byte-exact: the JSON serialization is identical
// across local-write and remote-upload paths so existing-asset checks
// compare against the exact previously-published bytes.
//
// The module is intentionally pure: it never performs remote I/O and
// never consults the producer runtime. It is the test seam for metadata
// consistency (round-trip identity, strict-field equality, hash
// equality, immutable-versioning by archive-name suffix). Test code can
// exercise every public function without a remote, an SDK, or a
// GitHub token.
module ReleaseMetadata

#load "../Provenance.fsx"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance

// The current schema version of the projection. Older writes stay
// readable; newer writes are rejected when the schema version does not
// match, so a future schema change cannot silently produce a projection
// that bypasses the strict field check.
let metadataSchemaVersion = 1

// The current schema's required field set. A future schema change must
// either update this list (together with the parser and renderer) or
// add a parallel projection branch.
let private requiredFields =
    [ "schemaVersion"
      "componentId"
      "version"
      "assetUri"
      "archiveName"
      "archiveSha256"
      "manifestSha256"
      "publishedRevision" ]

// The seven-field consumer-neutral projection the original issue
// requires plus the schema/protocol version, the archive name, and the
// optional producer toolchain context (the SDK/runtime/platform the
// archive was built against). The toolchain context is informational
// only; consumer pinning uses the immutable archive + manifest hashes
// and the published revision.
type ProducerMetadataProjection =
    { SchemaVersion: int
      ComponentId: string
      Version: string
      AssetUri: string
      ArchiveName: string
      ArchiveSha256: string
      ManifestSha256: string
      PublishedRevision: string
      ProducerSdk: string option
      ProducerRuntime: string option
      ProducerPlatform: string option }

// The asset name is derivable from `componentId` and `version` so the
// canonical Config.fsx does not need a parallel literal. Versioning the
// filename is what makes the asset immutable: a re-publish at the same
// version reuses the same filename and the safe-immutable transport
// refuses the overwrite; a new version produces a new filename.
let metadataAssetName (componentId: string) (version: string) : string =
    $"{componentId}-{version}-metadata.json"

// Render the projection as a stable JSON document. The keys are
// emitted in a fixed order; the hashes are already lowercase hex so
// the byte-exact transport never observes a normalization drift.
let render (projection: ProducerMetadataProjection) : string =
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create projection.SchemaVersion
    node["componentId"] <- JsonValue.Create projection.ComponentId
    node["version"] <- JsonValue.Create projection.Version
    node["assetUri"] <- JsonValue.Create projection.AssetUri
    node["archiveName"] <- JsonValue.Create projection.ArchiveName
    node["archiveSha256"] <- JsonValue.Create projection.ArchiveSha256
    node["manifestSha256"] <- JsonValue.Create projection.ManifestSha256
    node["publishedRevision"] <- JsonValue.Create projection.PublishedRevision

    match projection.ProducerSdk with
    | Some value -> node["producerSdk"] <- JsonValue.Create value
    | None -> ()

    match projection.ProducerRuntime with
    | Some value -> node["producerRuntime"] <- JsonValue.Create value
    | None -> ()

    match projection.ProducerPlatform with
    | Some value -> node["producerPlatform"] <- JsonValue.Create value
    | None -> ()

    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

// Parse an existing projection body. The function is the round-trip
// companion of `render`; mismatched schema versions, missing required
// fields, and malformed JSON fail closed so the safe-immutable
// transport cannot accept a remote asset that does not match the
// expected projection byte-for-byte.
let parse (text: string) : Result<ProducerMetadataProjection, ReleaseError> =
    let mutable failure : ReleaseError option = None

    let fail error =
        failure <- Some error
        Error error

    let mutable result : ProducerMetadataProjection option = None

    try
        let doc = JsonNode.Parse(text)

        if isNull doc then
            failure <- Some(MalformedArtifact("metadata", "the document is empty"))
        else
            let root = doc.AsObject()

            let readStr (name: string) : Result<string, ReleaseError> =
                if not (root.ContainsKey name) then
                    fail(MalformedArtifact("metadata", $"the metadata projection is missing required field '{name}'"))
                else
                    let valueNode = root.[name]

                    if isNull valueNode then
                        fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is null"))
                    else
                        try
                            Ok(valueNode.GetValue<string>())
                        with
                        | :? InvalidOperationException as error ->
                            fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is not a string: {error.Message}"))

            let readOptional (name: string) : Result<string option, ReleaseError> =
                match root[name] with
                | null -> Ok None
                | value ->
                    if isNull value then
                        Ok None
                    else
                        try
                            let text = value.GetValue<string>()
                            if String.IsNullOrEmpty text then Ok None else Ok(Some text)
                        with
                        | _ -> Ok None

            let schemaResult =
                try
                    Ok(root.["schemaVersion"].GetValue<int>())
                with
                | _ -> fail(MalformedArtifact("metadata", "the metadata projection 'schemaVersion' is not an integer"))

            match schemaResult with
            | Error _ -> ()
            | Ok schemaVersion when schemaVersion <> metadataSchemaVersion ->
                failure <-
                    Some(
                        MalformedArtifact(
                            "metadata",
                            $"unsupported schema version {schemaVersion}; expected {metadataSchemaVersion}"))
            | Ok schemaVersion ->
                match readStr "componentId", readStr "version", readStr "assetUri", readStr "archiveName", readStr "archiveSha256", readStr "manifestSha256", readStr "publishedRevision", readOptional "producerSdk", readOptional "producerRuntime", readOptional "producerPlatform" with
                | Ok componentId, Ok version, Ok assetUri, Ok archiveName, Ok archiveSha256, Ok manifestSha256, Ok publishedRevision, Ok producerSdk, Ok producerRuntime, Ok producerPlatform
                    when not (String.IsNullOrEmpty componentId)
                         && not (String.IsNullOrEmpty version)
                         && not (String.IsNullOrEmpty assetUri)
                         && not (String.IsNullOrEmpty archiveName)
                         && not (String.IsNullOrEmpty archiveSha256)
                         && not (String.IsNullOrEmpty manifestSha256)
                         && not (String.IsNullOrEmpty publishedRevision) ->
                    result <-
                        Some
                            { SchemaVersion = schemaVersion
                              ComponentId = componentId
                              Version = version
                              AssetUri = assetUri
                              ArchiveName = archiveName
                              ArchiveSha256 = archiveSha256
                              ManifestSha256 = manifestSha256
                              PublishedRevision = publishedRevision
                              ProducerSdk = producerSdk
                              ProducerRuntime = producerRuntime
                              ProducerPlatform = producerPlatform }
                | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, _, _, _ ->
                    failure <- Some(MalformedArtifact("metadata", "the metadata projection contains empty required fields"))
                | _ -> ()
    with
    | :? JsonException as error ->
        failure <- Some(MalformedArtifact("metadata", $"the metadata is not valid JSON: {error.Message}"))
    | :? FormatException as error ->
        failure <- Some(MalformedArtifact("metadata", $"the metadata is not valid JSON: {error.Message}"))
    | :? InvalidOperationException as error ->
        failure <- Some(MalformedArtifact("metadata", $"the metadata is not valid JSON: {error.Message}"))

    match failure, result with
    | Some error, _ -> Error error
    | None, Some value -> Ok value
    | None, None ->
        Error(MalformedArtifact("metadata", "the metadata projection did not produce a parseable result"))

// Strict field-by-field comparison between two projections. Used as
// the test seam for "metadata consistency" and as the unit the
// safe-immutable transport applies to an existing remote asset before
// allowing an idempotent re-upload.
let compare (existing: ProducerMetadataProjection) (expected: ProducerMetadataProjection) : Result<unit, ReleaseError> =
    let failures = ResizeArray<string>()
    if existing.SchemaVersion <> expected.SchemaVersion then
        failures.Add($"schemaVersion differs: existing={existing.SchemaVersion} expected={expected.SchemaVersion}")
    if existing.ComponentId <> expected.ComponentId then
        failures.Add($"componentId differs: existing={existing.ComponentId} expected={expected.ComponentId}")
    if existing.Version <> expected.Version then
        failures.Add($"version differs: existing={existing.Version} expected={expected.Version}")
    if existing.AssetUri <> expected.AssetUri then
        failures.Add($"assetUri differs: existing={existing.AssetUri} expected={expected.AssetUri}")
    if existing.ArchiveName <> expected.ArchiveName then
        failures.Add($"archiveName differs: existing={existing.ArchiveName} expected={expected.ArchiveName}")
    if existing.ArchiveSha256 <> expected.ArchiveSha256 then
        failures.Add($"archiveSha256 differs: existing={existing.ArchiveSha256} expected={expected.ArchiveSha256}")
    if existing.ManifestSha256 <> expected.ManifestSha256 then
        failures.Add($"manifestSha256 differs: existing={existing.ManifestSha256} expected={expected.ManifestSha256}")
    if existing.PublishedRevision <> expected.PublishedRevision then
        failures.Add($"publishedRevision differs: existing={existing.PublishedRevision} expected={expected.PublishedRevision}")
    if existing.ProducerSdk <> expected.ProducerSdk then
        failures.Add($"producerSdk differs: existing={existing.ProducerSdk} expected={expected.ProducerSdk}")
    if existing.ProducerRuntime <> expected.ProducerRuntime then
        failures.Add($"producerRuntime differs: existing={existing.ProducerRuntime} expected={expected.ProducerRuntime}")
    if existing.ProducerPlatform <> expected.ProducerPlatform then
        failures.Add($"producerPlatform differs: existing={existing.ProducerPlatform} expected={expected.ProducerPlatform}")

    if failures.Count > 0 then
        Error(MalformedArtifact("metadata", String.concat "; " failures))
    else
        Ok()

// Compute the SHA-256 of the bytes on disk so a remote-upload transport
// can compare a remote asset's digest against the locally rendered
// projection without depending on the JSON parse path. The function
// is async-friendly and Result-valued so transport failures stay typed
// at the release boundary.
let sha256OfFileAsync (path: string) : Async<Result<string, ReleaseError>> =
    async {
        try
            let! bytes = File.ReadAllBytesAsync(path) |> Async.AwaitTask
            let digest = SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()
            return Ok digest
        with
        | :? OperationCanceledException as error -> return raise error
        | :? IOException as error -> return Error(FileFailure("hash", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("hash", path, error.Message))
    }

// Load and parse an existing metadata file from disk. Used by
// observability (the no-op report observes the published metadata) and
// by the safe-immutable transport (existing remote asset observed).
let readFromFile (path: string) : Async<Result<ProducerMetadataProjection, ReleaseError>> =
    async {
        try
            let! text = File.ReadAllTextAsync(path) |> Async.AwaitTask
            return parse text
        with
        | :? IOException as error -> return Error(FileFailure("read", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("read", path, error.Message))
    }

// Write the rendered projection to disk and return the SHA-256 of the
// resulting bytes. The hash is reported so the runner can cross-check
// the file just written against a remote-asset observation.
let writeToFileAsync (path: string) (projection: ProducerMetadataProjection) : Async<Result<string, ReleaseError>> =
    async {
        let rendered = render projection
        let bytes = Encoding.UTF8.GetBytes rendered

        try
            do! File.WriteAllBytesAsync(path, bytes) |> Async.AwaitTask
            let digest = SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()
            return Ok digest
        with
        | :? OperationCanceledException as error -> return raise error
        | :? IOException as error -> return Error(FileFailure("write", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("write", path, error.Message))
    }

// Convenience constructor used by the runner for the most common case:
// a fully verified upload.
let makeProjection
    (componentId: string)
    (version: string)
    (assetUri: string)
    (archiveName: string)
    (archiveSha256: string)
    (manifestSha256: string)
    (publishedRevision: string)
    (producerSdk: string option)
    (producerRuntime: string option)
    (producerPlatform: string option)
    : ProducerMetadataProjection =
    { SchemaVersion = metadataSchemaVersion
      ComponentId = componentId
      Version = version
      AssetUri = assetUri
      ArchiveName = archiveName
      ArchiveSha256 = archiveSha256
      ManifestSha256 = manifestSha256
      PublishedRevision = publishedRevision
      ProducerSdk = producerSdk
      ProducerRuntime = producerRuntime
      ProducerPlatform = producerPlatform }
