// role: reusable module/helper
// Publication-facing release-metadata adapter: asset naming plus file adapters over
// the canonical pin record. The schema itself is implemented once in
// `infrastructure/distribution/Pin.fs`, so this module never holds a second parser
// or renderer.
module ReleaseMetadata

open System
open System.IO
open System.Security.Cryptography
open System.Text
open SharedFailure
open BuildProvenance

// The canonical pin/release-metadata schema version (owned by the shared contract).
let metadataSchemaVersion = DistributionPin.schemaVersion

// The published release-metadata projection is the canonical pin record.
type ProducerMetadataProjection = DistributionPin.Pin

// The filename encodes the version, so a re-publish reuses the slot and the transport refuses the overwrite.
let metadataAssetName (componentId: string) (version: string) : string =
    $"{componentId}-{version}-metadata.json"

let render (projection: ProducerMetadataProjection) : string =
    DistributionPin.render projection

let parse (text: string) : Result<ProducerMetadataProjection, ReleaseError> =
    DistributionPin.parse text

// Applied to an existing remote asset before an idempotent re-upload is allowed.
let compare (existing: ProducerMetadataProjection) (expected: ProducerMetadataProjection) : Result<unit, ReleaseError> =
    DistributionPin.compare existing expected

// Compares a remote asset digest against the locally rendered bytes without depending on the JSON parse path.
let sha256OfFileAsync (path: string) : Async<Result<string, ReleaseError>> =
    async {
        try
            let! bytes = awaitOperational (File.ReadAllBytesAsync path)
            let digest = SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()
            return Ok digest
        with
        | :? OperationCanceledException as error -> return raise error
        | :? IOException as error -> return Error(FileFailure("hash", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("hash", path, error.Message))
    }

// The no-op report and the existing-asset check both read the published metadata through this.
let readFromFile (path: string) : Async<Result<ProducerMetadataProjection, ReleaseError>> =
    async {
        try
            let! text = awaitOperational (File.ReadAllTextAsync path)
            return parse text
        with
        | :? IOException as error -> return Error(FileFailure("read", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("read", path, error.Message))
    }

// Returns the SHA-256 of the bytes written so the runner can cross-check against a remote observation.
let writeToFileAsync (path: string) (projection: ProducerMetadataProjection) : Async<Result<string, ReleaseError>> =
    async {
        let rendered = render projection
        let bytes = Encoding.UTF8.GetBytes rendered

        try
            do! awaitComplete (File.WriteAllBytesAsync(path, bytes))
            let digest = SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()
            return Ok digest
        with
        | :? OperationCanceledException as error -> return raise error
        | :? IOException as error -> return Error(FileFailure("write", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("write", path, error.Message))
    }

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
