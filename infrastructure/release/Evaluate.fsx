// role: reusable module/helper
// Pure decision + one transport seam; child-free testability.
// Inlines compare + classify so the runner and tests share one decision site.
// Public statuses are limited to published|unchanged|failed.
module ReleaseEvaluate

#load "../Provenance.fsx"
#load "Archive.fsx"
#load "Compare.fsx"
#load "Metadata.fsx"
#load "Publish.fsx"

open System
open System.IO
open System.Security.Cryptography
open BuildProvenance

type CandidateDto =
    { Contract: ReleaseCompare.ComponentContract
      ManifestPath: string
      ArchivePath: string
      EvaluatedHead: string
      AssetUri: string
      MetadataAssetName: string
      MetadataLocalPath: string
      ProducerSdk: string option
      ProducerRuntime: string option
      ProducerPlatform: string option
      RepositoryOwner: string
      RepositoryName: string }

type ArchiveState =
    | ArchiveObserved of ReleaseCompare.ArchiveObservation
    | ArchiveAbsent
    | ArchiveMissing
    | ArchiveImmutable of detail: string

type MetadataState =
    | MetadataObserved of sha256: string * projection: ReleaseMetadata.ProducerMetadataProjection
    | MetadataAbsent
    | MetadataMissing
    | MetadataImmutable
    | MetadataCorrupt of error: ReleaseError

type RemoteObserver =
    { ObserveArchive: string * string -> Async<Result<ArchiveState, ReleaseError>>
      ObserveMetadata: string * string -> Async<Result<MetadataState, ReleaseError>> }

type Render =
    { ComponentId: string
      Version: string
      ArchiveName: string
      AssetUri: string
      MetadataAssetName: string
      MetadataLocalPath: string
      CandidateArchiveSha256: string
      CandidateManifestSha256: string
      EvaluatedHead: string
      CandidateRevision: string
      ProducerSdk: string option
      ProducerRuntime: string option
      ProducerPlatform: string option
      BumpedVersion: string option }

type Resolution =
    { Status: string
      Reason: string
      UploadCompleted: bool
      Render: Render
      PublishedArchiveSha256: string option
      PublishedManifestSha256: string option
      PublishedRevision: string option
      PublishedMetadataAssetSha256: string option
      MetadataRecoveryNeeded: bool }

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private readCandidateRevision (manifestPath: string) : Async<Result<string, ReleaseError>> =
    async {
        match! BuildProvenance.readAllTextAsync manifestPath with
        | Error error -> return Error error
        | Ok text ->
            let mutable failure : ReleaseError option = None
            let mutable revision : string option = None

            try
                let doc = System.Text.Json.Nodes.JsonNode.Parse text

                if isNull doc then
                    failure <- Some(MalformedArtifact(manifestPath, "the document is empty"))
                else
                    let obj = doc.AsObject()

                    if not (obj.ContainsKey "revision") then
                        failure <- Some(MalformedArtifact(manifestPath, "the 'revision' field is missing"))
                    else
                        let value = obj.["revision"]
                        if isNull value then
                            failure <- Some(MalformedArtifact(manifestPath, "the 'revision' field is missing"))
                        else
                            revision <- Some(value.GetValue<string>())
            with
            | :? System.Text.Json.JsonException as error ->
                failure <- Some(MalformedArtifact(manifestPath, $"the manifest is not valid JSON: {error.Message}"))
            | :? FormatException as error ->
                failure <- Some(MalformedArtifact(manifestPath, $"the manifest is not valid JSON: {error.Message}"))
            | :? InvalidOperationException as error ->
                failure <- Some(MalformedArtifact(manifestPath, $"the manifest is not valid JSON: {error.Message}"))

            match failure, revision with
            | Some error, _ -> return Error error
            | None, Some value -> return Ok value
            | None, None -> return Error(MalformedArtifact(manifestPath, "the manifest did not yield a parseable revision"))
    }

let private hashFileOrError (path: string) : Async<Result<string, ReleaseError>> =
    async {
        try
            let bytes = File.ReadAllBytes path
            return Ok(sha256Hex bytes)
        with
        | :? IOException as error -> return Error(FileFailure("hash", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("hash", path, error.Message))
    }

let private renderFrom (dto: CandidateDto) (candidateArchiveSha: string) (candidateManifestSha: string) (candidateRevision: string) : Render =
    { ComponentId = dto.Contract.ComponentId
      Version = dto.Contract.Version
      ArchiveName = dto.Contract.ArchiveName
      AssetUri = dto.AssetUri
      MetadataAssetName = dto.MetadataAssetName
      MetadataLocalPath = dto.MetadataLocalPath
      CandidateArchiveSha256 = candidateArchiveSha
      CandidateManifestSha256 = candidateManifestSha
      EvaluatedHead = dto.EvaluatedHead
      CandidateRevision = candidateRevision
      ProducerSdk = dto.ProducerSdk
      ProducerRuntime = dto.ProducerRuntime
      ProducerPlatform = dto.ProducerPlatform
      BumpedVersion = None }

// F7 + F2: failure clears `published*` so candidate hashes can never masquerade as observed.
let finalizeFailure (render: Render) (reason: string) : Resolution =
    { Status = "failed"
      Reason = reason
      UploadCompleted = false
      Render = render
      PublishedArchiveSha256 = None
      PublishedManifestSha256 = None
      PublishedRevision = None
      PublishedMetadataAssetSha256 = None
      MetadataRecoveryNeeded = false }

// F2: verified new-publish; archive + metadata sha from transport, manifest sha + revision from the same validated snapshot.
let finalizeVerifiedPublication
    (resolution: Resolution)
    (verifiedArchiveSha: string)
    (verifiedMetadataSha: string)
    : Resolution =
    let publishedManifestSha =
        match resolution.PublishedManifestSha256 with
        | Some v -> Some v
        | None -> Some resolution.Render.CandidateManifestSha256

    let publishedRevision =
        match resolution.PublishedRevision with
        | Some v -> Some v
        | None -> Some resolution.Render.CandidateRevision

    let render =
        { resolution.Render with
            CandidateArchiveSha256 = verifiedArchiveSha
            CandidateManifestSha256 = publishedManifestSha |> Option.defaultValue resolution.Render.CandidateManifestSha256 }

    { Status = "published"
      Reason = "asset and metadata uploaded; remote digests verified"
      UploadCompleted = true
      Render = render
      PublishedArchiveSha256 = Some verifiedArchiveSha
      PublishedManifestSha256 = publishedManifestSha
      PublishedRevision = publishedRevision
      PublishedMetadataAssetSha256 = Some verifiedMetadataSha
      MetadataRecoveryNeeded = false }

// F2: verified-absence metadata-only recovery; status stays `unchanged` and only the metadata sha is refreshed.
let finalizeRecovery (resolution: Resolution) (verifiedMetadataSha: string) : Resolution =
    { Status = "unchanged"
      Reason = "archive unchanged under strict comparison; metadata republished to reconcile the verified absence"
      UploadCompleted = true
      Render = resolution.Render
      PublishedArchiveSha256 = resolution.PublishedArchiveSha256
      PublishedManifestSha256 = resolution.PublishedManifestSha256
      PublishedRevision = resolution.PublishedRevision
      PublishedMetadataAssetSha256 = Some verifiedMetadataSha
      MetadataRecoveryNeeded = false }

let private publishedRevision (published: ReleaseCompare.ArchiveObservation) : Result<string, ReleaseError> =
    match ReleaseCompare.parseEmbeddedManifestEntry published with
    | Ok header -> Ok header.Revision
    | Error error -> Error error

// F9: reject empty/mismatched assetUri so a reconstructed or injected value cannot reach the public JSON.
let validateCanonicalAssetUri
    (dto: CandidateDto)
    (contract: ReleaseCompare.ComponentContract)
    : Result<unit, ReleaseError> =
    let expected =
        $"https://github.com/{dto.RepositoryOwner}/{dto.RepositoryName}/releases/download/{contract.ComponentId}/{contract.ArchiveName}"

    if String.IsNullOrEmpty dto.AssetUri then
        Error(
            MalformedArtifact(
                "contract",
                "assetUri is missing from the canonical DTO; the runner never reconstructs it from environment"))
    elif dto.AssetUri <> expected then
        Error(MalformedArtifact("contract", $"assetUri '{dto.AssetUri}' does not match the expected canonical URL '{expected}'"))
    else
        Ok()

// One classification site; metadata errors fail closed; verified absence is the only signal that permits a metadata-only recovery.
let private classifyInternal
    (dto: CandidateDto)
    (candidateObs: ReleaseCompare.ArchiveObservation)
    (archiveState: Result<ArchiveState, ReleaseError>)
    (metadataState: Result<MetadataState, ReleaseError>)
    (render: Render)
    : Async<Result<Resolution, ReleaseError>> =
    async {
        match archiveState with
        | Error error -> return Ok(finalizeFailure render (ReleaseError.message error))
        | Ok (ArchiveImmutable detail) ->
            return
                Ok
                    (finalizeFailure
                        render
                        $"the {dto.Contract.ComponentId} release is immutable ({detail}); refusing to publish")
        | Ok ArchiveMissing ->
            return
                Ok
                    (finalizeFailure
                        render
                        $"the {dto.Contract.ComponentId} release container does not exist; refusing to silently create one")
        | Ok ArchiveAbsent ->
            return
                Ok
                    { Status = "published"
                      Reason = "the remote release has no asset for the declared version; an upload is required"
                      UploadCompleted = false
                      Render = render
                      PublishedArchiveSha256 = None
                      PublishedManifestSha256 = None
                      PublishedRevision = None
                      PublishedMetadataAssetSha256 = None
                      MetadataRecoveryNeeded = false }
        | Ok (ArchiveObserved publishedObs) ->
            let! comparison = ReleaseCompare.compare dto.Contract publishedObs candidateObs dto.EvaluatedHead
            match comparison with
            | Error error -> return Ok(finalizeFailure render (ReleaseError.message error))
            | Ok (ReleaseCompare.DifferentBeyondRevision (_, _, reason)) ->
                return
                    Ok
                        { Status = "failed"
                          Reason = reason
                          UploadCompleted = false
                          Render = { render with BumpedVersion = Some dto.Contract.Version }
                          PublishedArchiveSha256 = None
                          PublishedManifestSha256 = None
                          PublishedRevision = None
                          PublishedMetadataAssetSha256 = None
                          MetadataRecoveryNeeded = false }
            | Ok (ReleaseCompare.ByteIdentical _)
            | Ok (ReleaseCompare.RevisionOnlyEquivalent _) ->
                match metadataState with
                | Error error -> return Ok(finalizeFailure render (ReleaseError.message error))
                | Ok (MetadataCorrupt error) -> return Ok(finalizeFailure render (ReleaseError.message error))
                | Ok MetadataImmutable ->
                    return
                        Ok
                            (finalizeFailure
                                render
                                $"the metadata asset on the {dto.Contract.ComponentId} release is immutable; no repair allowed")
                | Ok MetadataMissing ->
                    return
                        Ok
                            (finalizeFailure
                                render
                                $"the {dto.Contract.ComponentId} release container does not exist; refusing to silently create one")
                | Ok (MetadataObserved (sha256, parsed)) ->
                    let mismatchedField : (string * string * string) option =
                        if parsed.ComponentId <> dto.Contract.ComponentId then
                            Some ("componentId", dto.Contract.ComponentId, parsed.ComponentId)
                        elif parsed.Version <> dto.Contract.Version then
                            Some ("version", dto.Contract.Version, parsed.Version)
                        elif parsed.ArchiveSha256 <> publishedObs.ArchiveSha256 then
                            Some ("archiveSha256", publishedObs.ArchiveSha256, parsed.ArchiveSha256)
                        else
                            match publishedObs.ManifestSha256 with
                            | Some value when value <> parsed.ManifestSha256 ->
                                Some ("manifestSha256", value, parsed.ManifestSha256)
                            | _ -> None

                    match mismatchedField with
                    | Some (field, expected, actual) ->
                        return Ok(finalizeFailure render (ReleaseError.message (IdentityMismatch(field, expected, actual))))
                    | None ->
                        match publishedRevision publishedObs with
                        | Error error -> return Ok(finalizeFailure render (ReleaseError.message error))
                        | Ok rev ->
                            return
                                Ok
                                    { Status = "unchanged"
                                      Reason = "candidate matches the published archive and the immutable metadata asset under strict comparison"
                                      UploadCompleted = false
                                      Render = render
                                      PublishedArchiveSha256 = Some publishedObs.ArchiveSha256
                                      PublishedManifestSha256 = publishedObs.ManifestSha256
                                      PublishedRevision = Some rev
                                      // Verified remote sha from the metadata transport; never substitute the archive sha or re-render.
                                      PublishedMetadataAssetSha256 = Some sha256
                                      MetadataRecoveryNeeded = false }
                | Ok MetadataAbsent ->
                    match publishedRevision publishedObs with
                    | Error error -> return Ok(finalizeFailure render (ReleaseError.message error))
                    | Ok rev ->
                        return
                            Ok
                                { Status = "unchanged"
                                  Reason = "candidate matches the published archive under strict comparison; metadata upload required to reconcile the verified absence"
                                  UploadCompleted = false
                                  Render = render
                                  PublishedArchiveSha256 = Some publishedObs.ArchiveSha256
                                  PublishedManifestSha256 = publishedObs.ManifestSha256
                                  PublishedRevision = Some rev
                                  PublishedMetadataAssetSha256 = None
                                  MetadataRecoveryNeeded = true }
    }

let evaluate
    (observer: RemoteObserver)
    (dto: CandidateDto)
    : Async<Result<Resolution, ReleaseError>> =
    async {
        match validateCanonicalAssetUri dto dto.Contract with
        | Error error -> return Error error
        | Ok () ->

        if not (File.Exists dto.ManifestPath) then
            return Error(MissingArtifact dto.ManifestPath)
        elif not (File.Exists dto.ArchivePath) then
            return Error(MissingArtifact dto.ArchivePath)
        else
            let! archiveShaResult = hashFileOrError dto.ArchivePath
            match archiveShaResult with
            | Error error -> return Error error
            | Ok candidateArchiveSha ->
                let! manifestShaResult = hashFileOrError dto.ManifestPath
                match manifestShaResult with
                | Error error -> return Error error
                | Ok candidateManifestSha ->
                    let! candidateRevisionResult = readCandidateRevision dto.ManifestPath
                    match candidateRevisionResult with
                    | Error error -> return Error error
                    | Ok candidateRevision ->

                        let render = renderFrom dto candidateArchiveSha candidateManifestSha candidateRevision

                        match! ReleaseCompare.observe dto.ArchivePath with
                        | Error error -> return Error error
                        | Ok candidateObs ->
                            match ReleaseCompare.validate dto.Contract candidateObs "candidate" with
                            | Error error -> return Error error
                            | Ok () ->

                            let! archiveStateResult =
                                observer.ObserveArchive (dto.Contract.ComponentId, dto.Contract.ArchiveName)

                            let! metadataStateResult =
                                observer.ObserveMetadata (dto.Contract.ComponentId, dto.MetadataAssetName)

                            return! classifyInternal dto candidateObs archiveStateResult metadataStateResult render
    }
