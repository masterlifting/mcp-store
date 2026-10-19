// role: reusable module/helper
// Pure decision + one transport seam; child-free testability.
// Inlines compare + classify so the runner and tests share one decision site.
// Public statuses are limited to published|unchanged|failed.
module ReleaseEvaluate

open System
open System.IO
open System.Security.Cryptography
open System.Text
open SharedFailure
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

// Bounded snapshot of one candidate: the verified archive + manifest bytes,
// every parse-observed digest and size, the verified embedded manifest, the
// derived DTO and projection. The internal constructor and exposed factory
// guarantee that no VerifiedCandidate ever exists whose bytes, hashes, sizes,
// toolchain, or embedded manifest disagree with the supplied parsed policy
// and evaluated HEAD; the rejection happens BEFORE any transport, auth, or
// upload-core composition is constructed.
//
// The verified byte arrays live behind private fields; public accessors
// return defensive copies so a caller can never mutate the snapshot, and the
// upload core reads them only via the `ArchiveBytesCopy` / `ManifestBytesCopy`
// accessors or the ownership-transfer stream methods.
type VerifiedCandidate internal
    (
        archiveBytes: byte[],
        manifestBytes: byte[],
        archiveObservation: ReleaseCompare.ArchiveObservation,
        archiveSha256: string,
        manifestSha256: string,
        archiveSize: int,
        manifestSize: int,
        embeddedManifestBytes: byte[],
        embeddedManifestSha256: string,
        embeddedRevision: string,
        dto: CandidateDto,
        projection: ReleaseMetadata.ProducerMetadataProjection
    ) =
    member _.ArchiveBytesCopy () = Array.copy archiveBytes
    member _.ManifestBytesCopy () = Array.copy manifestBytes
    member _.ArchiveBytesStream () = new MemoryStream(archiveBytes, false) :> Stream
    member _.ManifestBytesStream () = new MemoryStream(manifestBytes, false) :> Stream
    member _.ArchiveObservation = archiveObservation
    member _.ArchiveSha256 = archiveSha256
    member _.ManifestSha256 = manifestSha256
    member _.ArchiveSize = archiveSize
    member _.ManifestSize = manifestSize
    member _.EmbeddedManifestBytesCopy () = Array.copy embeddedManifestBytes
    member _.EmbeddedManifestSha256 = embeddedManifestSha256
    member _.EmbeddedRevision = embeddedRevision
    member _.Dto = dto
    member _.Projection = projection

// Factory module: the only path that constructs a VerifiedCandidate. Every
// required check below is run BEFORE any transport, auth, or upload-core
// composition is constructed. A failure in any check yields a typed
// `ReleaseError` without ever touching the network or opening a file.
module VerifiedCandidate =
    let private candidateLabel = "<publisher-candidate>"
    let private manifestLabel = "<verified-manifest>"
    let private archiveLabel = "<verified-archive>"

    let private sha256Hex (bytes: byte[]) : string =
        SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

    let private requireField (label: string) (value: string) : Result<unit, ReleaseError> =
        if String.IsNullOrWhiteSpace value then
            Error(MalformedArtifact(candidateLabel, $"the verified candidate is missing required field '{label}'"))
        else
            Ok()

    let create
        (policy: ReleasePolicy.PolicyProjection)
        (archiveBytes: byte[])
        (manifestBytes: byte[])
        (archiveName: string)
        (evaluatedHead: string)
        (metadataLocalPath: string)
        (repositoryOwner: string)
        (repositoryName: string)
        : Async<Result<VerifiedCandidate, ReleaseError>> =
        // Defensive copies of the caller-supplied byte arrays so the
        // VerifiedCandidate snapshot is intrinsically immutable: even if the
        // caller mutates the underlying byte[] after the factory returns,
        // the VerifiedCandidate still holds the verified snapshot bytes.
        let archiveBytesCopy = Array.copy archiveBytes
        let manifestBytesCopy = Array.copy manifestBytes

        let computation () : Result<VerifiedCandidate, ReleaseError> =
            let fieldCheck label value =
                if String.IsNullOrWhiteSpace value then
                    Error(MalformedArtifact(candidateLabel, "the verified candidate is missing required field '" + label + "'"))
                else
                    Ok ()
            let ensure cond error = if cond then Ok () else Error error
            let bytesAvailable () =
                if isNull archiveBytesCopy || archiveBytesCopy.Length = 0 then
                    Error(MalformedArtifact(candidateLabel, "the verified candidate archive bytes are missing or empty"))
                elif isNull manifestBytesCopy || manifestBytesCopy.Length = 0 then
                    Error(MalformedArtifact(candidateLabel, "the verified candidate manifest bytes are missing or empty"))
                else
                    Ok ()
            let archiveSha = sha256Hex archiveBytesCopy
            let archiveSize = archiveBytesCopy.Length
            let manifestSha = sha256Hex manifestBytesCopy
            let manifestSize = manifestBytesCopy.Length
            let buildCandidate (contract: ReleaseCompare.ComponentContract) (manifest: DistributionContract.DistributionManifest) (observation: ReleaseCompare.ArchiveObservation) (embeddedBytes: byte[]) (embeddedSha: string) =
                let metadataAssetName = ReleaseMetadata.metadataAssetName policy.ComponentId policy.Version
                let dto : CandidateDto =
                    { Contract = contract
                      ManifestPath = manifestLabel
                      ArchivePath = archiveLabel
                      EvaluatedHead = evaluatedHead
                      AssetUri = policy.AssetUri
                      MetadataAssetName = metadataAssetName
                      MetadataLocalPath = metadataLocalPath
                      ProducerSdk = Some policy.SdkVersion
                      ProducerRuntime = Some policy.RuntimeVersion
                      ProducerPlatform = Some policy.Platform
                      RepositoryOwner = repositoryOwner
                      RepositoryName = repositoryName }
                let projection =
                    ReleaseMetadata.makeProjection
                        policy.ComponentId
                        policy.Version
                        policy.AssetUri
                        archiveName
                        archiveSha
                        manifestSha
                        manifest.Revision
                        (Some policy.SdkVersion)
                        (Some policy.RuntimeVersion)
                        (Some policy.Platform)
                VerifiedCandidate(archiveBytesCopy, manifestBytesCopy, observation, archiveSha, manifestSha, archiveSize, manifestSize, embeddedBytes, embeddedSha, manifest.Revision, dto, projection)
            let fieldChecks =
                fieldCheck "policy.componentId" policy.ComponentId
                |> Result.bind (fun _ -> fieldCheck "policy.version" policy.Version)
                |> Result.bind (fun _ -> fieldCheck "policy.entryDll" policy.EntryDll)
                |> Result.bind (fun _ -> fieldCheck "policy.sdkVersion" policy.SdkVersion)
                |> Result.bind (fun _ -> fieldCheck "policy.runtimeVersion" policy.RuntimeVersion)
                |> Result.bind (fun _ -> fieldCheck "policy.platform" policy.Platform)
                |> Result.bind (fun _ -> fieldCheck "policy.archiveName" policy.ArchiveName)
                |> Result.bind (fun _ -> fieldCheck "policy.assetUri" policy.AssetUri)
                |> Result.bind (fun _ -> fieldCheck "archiveName" archiveName)
                |> Result.bind (fun _ -> fieldCheck "evaluatedHead" evaluatedHead)
                |> Result.bind (fun _ -> fieldCheck "metadataLocalPath" metadataLocalPath)
                |> Result.bind (fun _ -> fieldCheck "repositoryOwner" repositoryOwner)
                |> Result.bind (fun _ -> fieldCheck "repositoryName" repositoryName)
            let expectedEntryNames = DistributionContract.trustedArchiveMemberNames policy.PublishedFiles
            let runEmbeddedChecks (contract: ReleaseCompare.ComponentContract) (observation: ReleaseCompare.ArchiveObservation) (embeddedBody: string) (embeddedBytes: byte[]) (embeddedSha: string) =
                let manifestBytesCheck = ensure (embeddedBytes = manifestBytesCopy) (IdentityMismatch("candidate manifest bytes", "embedded == standalone", "embedded " + string embeddedBytes.Length + " bytes (sha " + embeddedSha + ") vs standalone " + string manifestSize + " bytes (sha " + manifestSha + ")"))
                let manifestShaCheck = ensure (embeddedSha = manifestSha) (IdentityMismatch("candidate manifest sha", embeddedSha, manifestSha))
                let archiveShaCheck = ensure (archiveSha = observation.ArchiveSha256) (IdentityMismatch("candidate archive sha", archiveSha, observation.ArchiveSha256))
                let allowlistCheck =
                        let actualEntryNames = List.sort (List.map (fun (e: ReleaseCompare.EntryObservation) -> e.Name) observation.Entries)
                        ensure (actualEntryNames = expectedEntryNames) (AllowlistMismatch("the candidate archive entry allowlist does not match the producer contract", expectedEntryNames, actualEntryNames))
                let entryFieldsChecks (manifest: DistributionContract.DistributionManifest) =
                    let revisionCheck = ensure (manifest.Revision = evaluatedHead) (RevisionMismatch(evaluatedHead, manifest.Revision))
                    let idCheck = ensure (manifest.Id = policy.ComponentId) (IdentityMismatch("id", policy.ComponentId, manifest.Id))
                    let versionCheck = ensure (manifest.Version = policy.Version) (IdentityMismatch("version", policy.Version, manifest.Version))
                    let archiveCheck = ensure (manifest.Archive = archiveName) (IdentityMismatch("archive", archiveName, manifest.Archive))
                    revisionCheck
                    |> Result.bind (fun _ -> idCheck)
                    |> Result.bind (fun _ -> versionCheck)
                    |> Result.bind (fun _ -> archiveCheck)
                manifestBytesCheck
                |> Result.bind (fun _ -> manifestShaCheck)
                |> Result.bind (fun _ -> archiveShaCheck)
                |> Result.bind (fun _ -> allowlistCheck)
                |> Result.bind (fun _ ->
                    ReleaseCompare.parseEmbeddedManifest embeddedBody
                    |> Result.bind (fun manifest -> entryFieldsChecks manifest |> Result.map (fun _ -> manifest)))
            let fromObservation (contract: ReleaseCompare.ComponentContract) (observation: ReleaseCompare.ArchiveObservation) =
                match observation.ManifestBody with
                | None -> Error(MalformedArtifact(candidateLabel, "the candidate archive is missing the canonical distribution.json entry; refusing to publish"))
                | Some embeddedBody ->
                    let embeddedBytes = Encoding.UTF8.GetBytes embeddedBody
                    let embeddedSha = sha256Hex embeddedBytes
                    runEmbeddedChecks contract observation embeddedBody embeddedBytes embeddedSha
                    |> Result.map (fun manifest -> (manifest, observation, embeddedBytes, embeddedSha))
            fieldChecks
            |> Result.bind (fun _ -> bytesAvailable ())
            |> Result.bind (fun _ -> ReleaseCompare.observeBytes candidateLabel archiveBytesCopy)
            |> Result.bind (fun observation ->
                match ReleaseCompare.contractFromPolicy policy archiveName with
                | Error error -> Error error
                | Ok contract ->
                    fromObservation contract observation
                    |> Result.bind (fun (manifest, observed, embeddedBytes, embeddedSha) -> Ok(buildCandidate contract manifest observed embeddedBytes embeddedSha)))
        async { return computation () }

    // SEC1 boundary adapter: builds a VerifiedCandidate from the typed
    // `CandidateArchiveSnapshot` produced by `ReleaseHandoff.readRawTransport`.
    // The snapshot already holds the bounded outer/inner archive bytes, the
    // standalone manifest bytes, the embedded manifest bytes, the canonical
    // outer/inner digests, and the verified `distribution.json` payload —
    // every byte the factory needs is read through defensive-copy or stream
    // accessors so no mutable reference escapes the snapshot. The factory
    // re-runs the canonical embedded-manifest check (entry allowlist, embedded
    // id/version/archive/revision) using the snapshot's bytes; no policy or
    // toolchain values are duplicated from the trusted receipt. The policy
    // argument is the byte-safe `parseBytes` projection re-derived from the
    // canonical Config.json by the publisher, never a candidate-supplied
    // projection. The evaluated head, owner and name come from the platform-
    // supplied CLI / event context; the metadata local path is supplied by the
    // caller and is the only place outside `VerifiedCandidate` that references
    // the on-disk metadata asset.
    let createFromSnapshot
        (snapshot: ReleaseHandoff.CandidateArchiveSnapshot)
        (policy: ReleasePolicy.PolicyProjection)
        (archiveName: string)
        (evaluatedHead: string)
        (metadataLocalPath: string)
        (repositoryOwner: string)
        (repositoryName: string)
        : Async<Result<VerifiedCandidate, ReleaseError>> =
        async {
            let fieldCheck label value =
                if String.IsNullOrWhiteSpace value then
                    Error(MalformedArtifact(candidateLabel, "the verified candidate is missing required field '" + label + "'"))
                else
                    Ok ()
            let ensure cond error = if cond then Ok () else Error error

            let computation () : Result<VerifiedCandidate, ReleaseError> =
                let innerBytesCopy = snapshot.ArchiveBytesCopy ()
                let manifestBytesCopy = snapshot.ManifestBytesCopy ()

                let archiveSha = sha256Hex innerBytesCopy
                let archiveSize = innerBytesCopy.Length
                let manifestSha = sha256Hex manifestBytesCopy
                let manifestSize = manifestBytesCopy.Length

                let buildCandidate (contract: ReleaseCompare.ComponentContract) (manifest: DistributionContract.DistributionManifest) (observation: ReleaseCompare.ArchiveObservation) (embeddedBytesCopy: byte[]) (embeddedSha: string) =
                    let metadataAssetName = ReleaseMetadata.metadataAssetName policy.ComponentId policy.Version
                    let dto : CandidateDto =
                        { Contract = contract
                          ManifestPath = manifestLabel
                          ArchivePath = archiveLabel
                          EvaluatedHead = evaluatedHead
                          AssetUri = policy.AssetUri
                          MetadataAssetName = metadataAssetName
                          MetadataLocalPath = metadataLocalPath
                          ProducerSdk = Some policy.SdkVersion
                          ProducerRuntime = Some policy.RuntimeVersion
                          ProducerPlatform = Some policy.Platform
                          RepositoryOwner = repositoryOwner
                          RepositoryName = repositoryName }
                    let projection =
                        ReleaseMetadata.makeProjection
                            policy.ComponentId
                            policy.Version
                            policy.AssetUri
                            archiveName
                            archiveSha
                            manifestSha
                            manifest.Revision
                            (Some policy.SdkVersion)
                            (Some policy.RuntimeVersion)
                            (Some policy.Platform)
                    VerifiedCandidate
                        (innerBytesCopy,
                         manifestBytesCopy,
                         observation,
                         archiveSha,
                         manifestSha,
                         archiveSize,
                         manifestSize,
                         embeddedBytesCopy,
                         embeddedSha,
                         manifest.Revision,
                         dto,
                         projection)

                let fieldChecks =
                    fieldCheck "policy.componentId" policy.ComponentId
                    |> Result.bind (fun _ -> fieldCheck "policy.version" policy.Version)
                    |> Result.bind (fun _ -> fieldCheck "policy.entryDll" policy.EntryDll)
                    |> Result.bind (fun _ -> fieldCheck "policy.sdkVersion" policy.SdkVersion)
                    |> Result.bind (fun _ -> fieldCheck "policy.runtimeVersion" policy.RuntimeVersion)
                    |> Result.bind (fun _ -> fieldCheck "policy.platform" policy.Platform)
                    |> Result.bind (fun _ -> fieldCheck "policy.archiveName" policy.ArchiveName)
                    |> Result.bind (fun _ -> fieldCheck "policy.assetUri" policy.AssetUri)
                    |> Result.bind (fun _ -> fieldCheck "archiveName" archiveName)
                    |> Result.bind (fun _ -> fieldCheck "evaluatedHead" evaluatedHead)
                    |> Result.bind (fun _ -> fieldCheck "metadataLocalPath" metadataLocalPath)
                    |> Result.bind (fun _ -> fieldCheck "repositoryOwner" repositoryOwner)
                    |> Result.bind (fun _ -> fieldCheck "repositoryName" repositoryName)

                let bytesAvailable () =
                    if isNull innerBytesCopy || innerBytesCopy.Length = 0 then
                        Error(MalformedArtifact(candidateLabel, "the verified candidate inner archive bytes are missing or empty"))
                    elif isNull manifestBytesCopy || manifestBytesCopy.Length = 0 then
                        Error(MalformedArtifact(candidateLabel, "the verified candidate manifest bytes are missing or empty"))
                    else
                        Ok ()

                // The snapshot already cross-checked standalone/embedded
                // manifest byte equality and the canonical outer entry set;
                // the factory re-runs the policy-bound checks against the
                // snapshot's bytes so no candidate-controlled value escapes.
                let expectedEntryNames = DistributionContract.trustedArchiveMemberNames policy.PublishedFiles
                let runSnapshotChecks
                    (contract: ReleaseCompare.ComponentContract)
                    (observation: ReleaseCompare.ArchiveObservation)
                    (embeddedBody: string)
                    (embeddedBytesCopy: byte[])
                    : Result<DistributionContract.DistributionManifest, ReleaseError> =
                    let embeddedSha = sha256Hex embeddedBytesCopy
                    let manifestBytesCheck =
                        ensure (embeddedBytesCopy = manifestBytesCopy)
                            (IdentityMismatch("candidate manifest bytes", "embedded == standalone",
                                $"embedded {embeddedBytesCopy.Length} bytes (sha {embeddedSha}) vs standalone {manifestSize} bytes (sha {manifestSha})"))
                    let manifestShaCheck =
                        ensure (embeddedSha = manifestSha)
                            (IdentityMismatch("candidate manifest sha", embeddedSha, manifestSha))
                    let archiveShaCheck =
                        ensure (archiveSha = observation.ArchiveSha256)
                            (IdentityMismatch("candidate archive sha", archiveSha, observation.ArchiveSha256))
                    let allowlistCheck =
                        let actualEntryNames = List.sort (List.map (fun (e: ReleaseCompare.EntryObservation) -> e.Name) observation.Entries)
                        ensure (actualEntryNames = expectedEntryNames)
                            (AllowlistMismatch("the candidate archive entry allowlist does not match the producer contract", expectedEntryNames, actualEntryNames))
                    let entryFieldsChecks (manifest: DistributionContract.DistributionManifest) =
                        let revisionCheck = ensure (manifest.Revision = evaluatedHead) (RevisionMismatch(evaluatedHead, manifest.Revision))
                        let idCheck = ensure (manifest.Id = policy.ComponentId) (IdentityMismatch("id", policy.ComponentId, manifest.Id))
                        let versionCheck = ensure (manifest.Version = policy.Version) (IdentityMismatch("version", policy.Version, manifest.Version))
                        let archiveCheck = ensure (manifest.Archive = archiveName) (IdentityMismatch("archive", archiveName, manifest.Archive))
                        revisionCheck
                        |> Result.bind (fun _ -> idCheck)
                        |> Result.bind (fun _ -> versionCheck)
                        |> Result.bind (fun _ -> archiveCheck)
                        |> Result.map (fun _ -> manifest)
                    manifestBytesCheck
                    |> Result.bind (fun _ -> manifestShaCheck)
                    |> Result.bind (fun _ -> archiveShaCheck)
                    |> Result.bind (fun _ -> allowlistCheck)
                    |> Result.bind (fun _ ->
                        ReleaseCompare.parseEmbeddedManifest embeddedBody
                        |> Result.bind entryFieldsChecks)

                let fromObservation (contract: ReleaseCompare.ComponentContract) (observation: ReleaseCompare.ArchiveObservation) =
                    match observation.ManifestBody with
                    | None ->
                        Error(MalformedArtifact(candidateLabel, "the candidate archive is missing the canonical distribution.json entry; refusing to publish"))
                    | Some embeddedBody ->
                        let embeddedBytesCopy = Encoding.UTF8.GetBytes embeddedBody
                        if embeddedBytesCopy.Length = 0 then
                            Error(MalformedArtifact(candidateLabel, "the verified candidate embedded manifest bytes are missing or empty"))
                        else
                            let embeddedSha = sha256Hex embeddedBytesCopy
                            runSnapshotChecks contract observation embeddedBody embeddedBytesCopy
                            |> Result.map (fun manifest -> (manifest, observation, embeddedBytesCopy, embeddedSha))

                fieldChecks
                |> Result.bind (fun _ -> bytesAvailable ())
                |> Result.bind (fun _ -> ReleaseCompare.observeBytes candidateLabel innerBytesCopy)
                |> Result.bind (fun observation ->
                    match ReleaseCompare.contractFromPolicy policy archiveName with
                    | Error error -> Error error
                    | Ok contract ->
                        fromObservation contract observation
                        |> Result.map (fun (manifest, observed, embeddedBytesCopy, embeddedSha) -> buildCandidate contract manifest observed embeddedBytesCopy embeddedSha))
            return computation ()
        }


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
    | Ok manifest -> Ok manifest.Revision
    | Error error -> Error error

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

let private renderFrom (candidate: VerifiedCandidate) : Render =
    let dto = candidate.Dto
    { ComponentId = dto.Contract.ComponentId
      Version = dto.Contract.Version
      ArchiveName = dto.Contract.ArchiveName
      AssetUri = dto.AssetUri
      MetadataAssetName = dto.MetadataAssetName
      MetadataLocalPath = dto.MetadataLocalPath
      CandidateArchiveSha256 = candidate.ArchiveSha256
      CandidateManifestSha256 = candidate.ManifestSha256
      EvaluatedHead = dto.EvaluatedHead
      CandidateRevision = candidate.EmbeddedRevision
      ProducerSdk = dto.ProducerSdk
      ProducerRuntime = dto.ProducerRuntime
      ProducerPlatform = dto.ProducerPlatform
      BumpedVersion = None }

// One classification site; metadata errors fail closed; verified absence is the only signal that permits a metadata-only recovery.
// The remote observation flows through the genuine HTTP observer (`RemoteObserver` from the production transport).
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
            match metadataState with
            | Error error -> return Ok(finalizeFailure render (ReleaseError.message error))
            | Ok (MetadataCorrupt error) ->
                return
                    Ok
                        (finalizeFailure
                            render
                            $"the existing metadata asset for {dto.Contract.ComponentId} {dto.Contract.Version} is corrupt: {ReleaseError.message error}")
            | Ok MetadataMissing ->
                return
                    Ok
                        (finalizeFailure
                            render
                            $"the {dto.Contract.ComponentId} release container does not exist; refusing to silently create one")
            | Ok MetadataImmutable ->
                return
                    Ok
                        (finalizeFailure
                            render
                            $"the existing metadata asset for {dto.Contract.ComponentId} {dto.Contract.Version} is immutable; refusing to publish a new archive alongside stale metadata")
            | Ok (MetadataObserved _) ->
                return
                    Ok
                        (finalizeFailure
                            render
                            $"an existing metadata asset for {dto.Contract.ComponentId} {dto.Contract.Version} was observed; refusing to publish a new archive with stale or orphan metadata before the ZIP POST")
            | Ok MetadataAbsent ->
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
                    let optToString = function | Some v -> v | None -> "<absent>"
                    // Remote revision is checked against the remote embedded manifest, not the candidate HEAD; toolchain values participate in identity.
                    let mismatchedField : (string * string * string) option =
                            if parsed.ComponentId <> dto.Contract.ComponentId then
                                Some ("componentId", dto.Contract.ComponentId, parsed.ComponentId)
                            elif parsed.Version <> dto.Contract.Version then
                                Some ("version", dto.Contract.Version, parsed.Version)
                            elif parsed.AssetUri <> dto.AssetUri then
                                Some ("assetUri", dto.AssetUri, parsed.AssetUri)
                            elif parsed.ArchiveName <> dto.Contract.ArchiveName then
                                Some ("archiveName", dto.Contract.ArchiveName, parsed.ArchiveName)
                            elif parsed.ArchiveSha256 <> publishedObs.ArchiveSha256 then
                                Some ("archiveSha256", publishedObs.ArchiveSha256, parsed.ArchiveSha256)
                            else
                                match publishedObs.ManifestSha256 with
                                | Some value when value <> parsed.ManifestSha256 ->
                                    Some ("manifestSha256", value, parsed.ManifestSha256)
                                | _ ->
                                    match ReleaseCompare.parseEmbeddedManifestEntry publishedObs with
                                    | Ok manifest when manifest.Revision <> parsed.PublishedRevision ->
                                        Some ("publishedRevision", manifest.Revision, parsed.PublishedRevision)
                                    | _ ->
                                        if parsed.ProducerSdk <> dto.ProducerSdk then
                                            Some ("producerSdk", optToString dto.ProducerSdk, optToString parsed.ProducerSdk)
                                        elif parsed.ProducerRuntime <> dto.ProducerRuntime then
                                            Some ("producerRuntime", optToString dto.ProducerRuntime, optToString parsed.ProducerRuntime)
                                        elif parsed.ProducerPlatform <> dto.ProducerPlatform then
                                            Some ("producerPlatform", optToString dto.ProducerPlatform, optToString parsed.ProducerPlatform)
                                        else
                                            None

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

// The single canonical classifier entry. The genuine HTTP remote observer
// (built by `buildRemoteObserverWith` in `ReleaseTransaction`) supplies the
// real remote archive + metadata observations; the candidate observation
// comes from `VerifiedCandidate.ArchiveObservation`. The verified snapshot
// never re-reads from a mutable on-disk path.
let evaluate
    (observer: RemoteObserver)
    (candidate: VerifiedCandidate)
    : Async<Result<Resolution, ReleaseError>> =
    async {
        match validateCanonicalAssetUri candidate.Dto candidate.Dto.Contract with
        | Error error -> return Error error
        | Ok () ->

            match ReleaseCompare.validate candidate.Dto.Contract candidate.ArchiveObservation "candidate" with
            | Error error -> return Error error
            | Ok () ->

                let! archiveStateResult =
                    observer.ObserveArchive (candidate.Dto.Contract.ComponentId, candidate.Dto.Contract.ArchiveName)

                let! metadataStateResult =
                    observer.ObserveMetadata (candidate.Dto.Contract.ComponentId, candidate.Dto.MetadataAssetName)

                return! classifyInternal candidate.Dto candidate.ArchiveObservation archiveStateResult metadataStateResult (renderFrom candidate)
    }
