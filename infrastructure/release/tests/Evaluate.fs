module Release.Tests.Evaluate

open Expecto
open Release.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance
open ReleaseCompare
open ReleaseEvaluate
open ReleaseMetadata

type RemoteArchiveState =
    | AbsentArchive
    | MissingArchive
    | ImmutableArchive

let private archiveStateToResult (state: RemoteArchiveState) : Result<ArchiveState, ReleaseError> =
    match state with
    | AbsentArchive -> Ok ArchiveAbsent
    | MissingArchive -> Ok ArchiveMissing
    | ImmutableArchive -> Ok (ArchiveImmutable "the existing release is configured as immutable")

let private scriptedObserver archiveState =
    { ObserveArchive = fun _ -> async { return archiveStateToResult archiveState }
      ObserveMetadata = fun _ -> async { return Ok MetadataAbsent } }

let private scriptedObserverWith archiveResult metadataResult =
    { ObserveArchive = fun _ -> async { return archiveResult }
      ObserveMetadata = fun _ -> async { return metadataResult } }

let mutable passed = 0
let mutable failed = 0
let mutable failures = ResizeArray<string>()

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
let private sha256HexBytes (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

let private metadataAssetSha (projection: ProducerMetadataProjection) : string =
    sha256HexBytes (Encoding.UTF8.GetBytes (ReleaseMetadata.render projection))

let private makeProjection (archiveSha: string) (manifestSha: string) (revision: string) =
    ReleaseMetadata.makeProjection
        Fixture.contractComponentId
        Fixture.contractVersion
        Fixture.canonicalAssetUri
        Fixture.contractArchiveName
        archiveSha manifestSha revision
        (Some Fixture.canonicalSdk)
        (Some Fixture.canonicalRuntime)
        (Some Fixture.canonicalPlatform)

let private contract =
    {
        ComponentId = Fixture.contractComponentId
        Version = Fixture.contractVersion
        ArchiveName = Fixture.contractArchiveName
        EntryAllowlist = Fixture.contractEntryAllowlist
    }

let private tempRoot = Path.Combine(Path.GetTempPath(), "mcp-release-evaluate-tests-" + Guid.NewGuid().ToString("N"))
let private dtoFrom (archivePath: string) (manifestPath: string) =
    {
        Contract = contract
        ManifestPath = manifestPath
        ArchivePath = archivePath
        EvaluatedHead = Fixture.canonicalEvaluatedRevision
        AssetUri = Fixture.canonicalAssetUri
        MetadataAssetName = ReleaseMetadata.metadataAssetName Fixture.contractComponentId Fixture.contractVersion
        MetadataLocalPath = Path.Combine(tempRoot, "metadata.json")
        ProducerSdk = Some Fixture.canonicalSdk
        ProducerRuntime = Some Fixture.canonicalRuntime
        ProducerPlatform = Some Fixture.canonicalPlatform
        RepositoryOwner = "masterlifting"
        RepositoryName = "mcp-store"
    }
let validateCanonicalAssetUriEmptyRejection () = async {
    let archivePath = Path.Combine(tempRoot, "uri-empty.zip")
    let manifestPath = Path.Combine(tempRoot, "uri-empty.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let dto = { dtoFrom archivePath manifestPath with AssetUri = "" }
    let observer = scriptedObserver AbsentArchive
    match! ReleaseEvaluate.evaluate observer dto with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("validateCanonicalAssetUri empty: expected Error")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "empty assetUri is fail-closed" (message.Contains("assetUri", StringComparison.Ordinal))
}
let validateCanonicalAssetUriWrongOwnerRejection () = async {
    let archivePath = Path.Combine(tempRoot, "uri-wrong.zip")
    let manifestPath = Path.Combine(tempRoot, "uri-wrong.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let dto =
        { dtoFrom archivePath manifestPath with
            AssetUri = "https://github.com/somebodyelse/mcp-store/releases/download/dotnet/v1.0.4.zip" }
    let observer = scriptedObserver AbsentArchive
    match! ReleaseEvaluate.evaluate observer dto with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("validateCanonicalAssetUri wrong owner: expected Error")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "mismatched assetUri is fail-closed" (message.Contains("assetUri", StringComparison.Ordinal) || message.Contains("does not match", StringComparison.Ordinal))
}
let validateCanonicalAssetUriAccepted () = async {
    let archivePath = Path.Combine(tempRoot, "uri-ok.zip")
    let manifestPath = Path.Combine(tempRoot, "uri-ok.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let dto = dtoFrom archivePath manifestPath
    let observer = scriptedObserver AbsentArchive
    match! ReleaseEvaluate.evaluate observer dto with
    | Ok resolution ->
        assertEqual "canonical assetUri produces a published classification" "published" resolution.Status
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "validateCanonicalAssetUri accepted: %s" (ReleaseError.message error))
}
let missingManifestRejection () = async {
    let archivePath = Path.Combine(tempRoot, "missing-manifest.zip")
    let manifestPath = Path.Combine(tempRoot, "missing.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    File.Delete manifestPath
    let dto = dtoFrom archivePath manifestPath
    let observer = scriptedObserver AbsentArchive
    match! ReleaseEvaluate.evaluate observer dto with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("missing manifest: expected Error")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "missing manifest fails closed" (message.Contains("does not exist", StringComparison.Ordinal) || message.Contains("missing", StringComparison.Ordinal))
}
let missingArchiveRejection () = async {
    let archivePath = Path.Combine(tempRoot, "missing-archive.zip")
    let manifestPath = Path.Combine(tempRoot, "missing-archive.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    File.Delete archivePath
    let dto = dtoFrom archivePath manifestPath
    let observer = scriptedObserver AbsentArchive
    match! ReleaseEvaluate.evaluate observer dto with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("missing archive: expected Error")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "missing archive fails closed" (message.Contains("does not exist", StringComparison.Ordinal) || message.Contains("missing", StringComparison.Ordinal))
}
let publishedOnArchiveAbsent () = async {
    let archivePath = Path.Combine(tempRoot, "abs.zip")
    let manifestPath = Path.Combine(tempRoot, "abs.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let dto = dtoFrom archivePath manifestPath
    let observer = scriptedObserver AbsentArchive
    match! ReleaseEvaluate.evaluate observer dto with
    | Ok resolution ->
        assertEqual "ArchiveAbsent produces published" "published" resolution.Status
        assertEqual "ArchiveAbsent reason names the no-asset condition"
            "the remote release has no asset for the declared version; an upload is required"
            resolution.Reason
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "published on archive absent: %s" (ReleaseError.message error))
}
let failedOnArchiveMissing () = async {
    let archivePath = Path.Combine(tempRoot, "missing-rel.zip")
    let manifestPath = Path.Combine(tempRoot, "missing-rel.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let dto = dtoFrom archivePath manifestPath
    let observer = scriptedObserver MissingArchive
    match! ReleaseEvaluate.evaluate observer dto with
    | Ok resolution ->
        assertEqual "ArchiveMissing produces failed" "failed" resolution.Status
        assertTrue "ArchiveMissing reason names the missing container"
            (resolution.Reason.Contains("does not exist", StringComparison.Ordinal))
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "ArchiveMissing failed: %s" (ReleaseError.message error))
}
let failedOnArchiveImmutable () = async {
    let archivePath = Path.Combine(tempRoot, "imm.zip")
    let manifestPath = Path.Combine(tempRoot, "imm.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let dto = dtoFrom archivePath manifestPath
    let observer = scriptedObserver ImmutableArchive
    match! ReleaseEvaluate.evaluate observer dto with
    | Ok resolution ->
        assertEqual "ArchiveImmutable produces failed" "failed" resolution.Status
        assertTrue "ArchiveImmutable reason names immutability"
            (resolution.Reason.Contains("immutable", StringComparison.Ordinal))
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "ArchiveImmutable failed: %s" (ReleaseError.message error))
}
let failedOnBumpedSdk () = async {
    let publishedPath = Path.Combine(tempRoot, "bump-published.zip")
    let publishedManifest = Path.Combine(tempRoot, "bump-published.json")
    Fixture.writeRevisionVariantArchive publishedPath publishedManifest Fixture.canonicalPublishedRevision
    let candidatePath = Path.Combine(tempRoot, "bump-candidate.zip")
    let candidateManifest = Path.Combine(tempRoot, "bump-candidate.json")
    let bumpedDllBytes = Encoding.UTF8.GetBytes "dotnet-dll-fixture-bumped"
    Fixture.writeModifiedDotnetDllArchive candidatePath candidateManifest bumpedDllBytes Fixture.canonicalEvaluatedRevision
    let realDto = dtoFrom candidatePath candidateManifest
    let! publishedResult = observe publishedPath
    let publishedObs =
        match publishedResult with
        | Ok value -> value
        | Error error -> failwithf "observe published: %s" (ReleaseError.message error)
    let observer = scriptedObserverWith (Ok (ArchiveObserved publishedObs)) (Ok MetadataAbsent)
    match! ReleaseEvaluate.evaluate observer realDto with
    | Ok resolution ->
        assertEqual "bumped-sdk produces failed" "failed" resolution.Status
        assertEqual "bumped version recorded" (Some Fixture.contractVersion) resolution.Render.BumpedVersion
        assertTrue "bumped reason references differs/manifests"
            (resolution.Reason.Contains("differs", StringComparison.Ordinal)
             || resolution.Reason.Contains("manifests", StringComparison.Ordinal))
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "bumped sdk failed: %s" (ReleaseError.message error))
}
let unchangedOnByteIdentical () = async {
    let archivePath = Path.Combine(tempRoot, "idem.zip")
    let manifestPath = Path.Combine(tempRoot, "idem.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let! observationResult = observe archivePath
    let observation =
        match observationResult with
        | Ok value -> value
        | Error error -> failwithf "observe: %s" (ReleaseError.message error)
    let projection =
        makeProjection
            observation.ArchiveSha256
            (observation.ManifestSha256 |> Option.defaultValue "")
            Fixture.canonicalEvaluatedRevision
    let observer = scriptedObserverWith (Ok (ArchiveObserved observation)) (Ok (MetadataObserved (metadataAssetSha projection, projection)))
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok resolution ->
        assertEqual "byte-identical + matching metadata -> unchanged" "unchanged" resolution.Status
        assertEqual "publishedArchiveSha256 reports the observed remote digest"
            (Some observation.ArchiveSha256) resolution.PublishedArchiveSha256
        assertEqual "publishedManifestSha256 reports the observed remote digest"
            (Some (observation.ManifestSha256 |> Option.defaultValue "")) resolution.PublishedManifestSha256
        assertEqual "publishedRevision reports the observed remote revision"
            (Some Fixture.canonicalEvaluatedRevision) resolution.PublishedRevision
        assertTrue "unchanged branch never claims uploadCompleted" (not resolution.UploadCompleted)
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "unchanged byte-identical: %s" (ReleaseError.message error))
}
let unchangedOnRevisionOnlyEquivalent () = async {
    let publishedPath = Path.Combine(tempRoot, "rev-pub.zip")
    let publishedManifest = Path.Combine(tempRoot, "rev-pub.json")
    Fixture.writeRevisionVariantArchive publishedPath publishedManifest Fixture.canonicalPublishedRevision
    let candidatePath = Path.Combine(tempRoot, "rev-cand.zip")
    let candidateManifest = Path.Combine(tempRoot, "rev-cand.json")
    Fixture.writeRevisionVariantArchive candidatePath candidateManifest Fixture.canonicalEvaluatedRevision
    let! publishedResult = observe publishedPath
    let publishedObs =
        match publishedResult with
        | Ok value -> value
        | Error error -> failwithf "observe published: %s" (ReleaseError.message error)
    let projection =
        makeProjection
            publishedObs.ArchiveSha256
            (publishedObs.ManifestSha256 |> Option.defaultValue "")
            Fixture.canonicalPublishedRevision
    let observer = scriptedObserverWith (Ok (ArchiveObserved publishedObs)) (Ok (MetadataObserved (metadataAssetSha projection, projection)))
    match! ReleaseEvaluate.evaluate observer (dtoFrom candidatePath candidateManifest) with
    | Ok resolution ->
        assertEqual "revision-only + matching metadata -> unchanged" "unchanged" resolution.Status
        assertEqual "publishedArchiveSha256 reports the published remote digest"
            (Some publishedObs.ArchiveSha256) resolution.PublishedArchiveSha256
        assertEqual "publishedRevision reports the published remote revision"
            (Some Fixture.canonicalPublishedRevision) resolution.PublishedRevision
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "unchanged revision-only: %s" (ReleaseError.message error))
}
let unchangedWithMetadataRecoveryNeeded () = async {
    let archivePath = Path.Combine(tempRoot, "rec.zip")
    let manifestPath = Path.Combine(tempRoot, "rec.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let! observationResult = observe archivePath
    let observation =
        match observationResult with
        | Ok value -> value
        | Error error -> failwithf "observe: %s" (ReleaseError.message error)
    let observer = scriptedObserverWith (Ok (ArchiveObserved observation)) (Ok MetadataAbsent)
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok resolution ->
        assertEqual "verified metadata absence -> unchanged" "unchanged" resolution.Status
        assertTrue "metadataRecoveryNeeded flag set" resolution.MetadataRecoveryNeeded
        assertEqual "publishedArchiveSha256 reports the observed remote digest"
            (Some observation.ArchiveSha256) resolution.PublishedArchiveSha256
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "recovery-needed: %s" (ReleaseError.message error))
}
let failedOnMetadataCorrupt () = async {
    let archivePath = Path.Combine(tempRoot, "corrupt-meta.zip")
    let manifestPath = Path.Combine(tempRoot, "corrupt-meta.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let! observationResult = observe archivePath
    let observation =
        match observationResult with
        | Ok value -> value
        | Error error -> failwithf "observe: %s" (ReleaseError.message error)
    let observer =
        scriptedObserverWith
            (Ok (ArchiveObserved observation))
            (Ok (MetadataCorrupt (FileFailure("verify", "metadata", "metadata transport unavailable"))))
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok resolution ->
        assertEqual "MetadataCorrupt -> failed" "failed" resolution.Status
        assertTrue "MetadataCorrupt reason references verify/transport"
            (resolution.Reason.Contains("verify", StringComparison.Ordinal)
             || resolution.Reason.Contains("transport", StringComparison.Ordinal))
        assertEqual "MetadataCorrupt never sets publishedMetadataSha256" None resolution.PublishedMetadataAssetSha256
        assertEqual "MetadataCorrupt never sets recoveryNeeded" false resolution.MetadataRecoveryNeeded
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "MetadataCorrupt failed: %s" (ReleaseError.message error))
}
let failedOnMetadataImmutable () = async {
    let archivePath = Path.Combine(tempRoot, "imm-meta.zip")
    let manifestPath = Path.Combine(tempRoot, "imm-meta.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let! observationResult = observe archivePath
    let observation =
        match observationResult with
        | Ok value -> value
        | Error error -> failwithf "observe: %s" (ReleaseError.message error)
    let observer = scriptedObserverWith (Ok (ArchiveObserved observation)) (Ok MetadataImmutable)
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok resolution ->
        assertEqual "MetadataImmutable -> failed" "failed" resolution.Status
        assertTrue "MetadataImmutable reason references immutability"
            (resolution.Reason.Contains("immutable", StringComparison.Ordinal))
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "MetadataImmutable failed: %s" (ReleaseError.message error))
}
let failedOnObservedMetadataFieldMismatch () = async {
    let archivePath = Path.Combine(tempRoot, "mismatch-meta.zip")
    let manifestPath = Path.Combine(tempRoot, "mismatch-meta.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let! observationResult = observe archivePath
    let observation =
        match observationResult with
        | Ok value -> value
        | Error error -> failwithf "observe: %s" (ReleaseError.message error)
    let projection =
        ReleaseMetadata.makeProjection
            Fixture.contractComponentId
            "1.0.5"
            Fixture.canonicalAssetUri
            Fixture.contractArchiveName
            observation.ArchiveSha256
            (observation.ManifestSha256 |> Option.defaultValue "")
            Fixture.canonicalEvaluatedRevision
            (Some Fixture.canonicalSdk)
            (Some Fixture.canonicalRuntime)
            (Some Fixture.canonicalPlatform)
    let observer = scriptedObserverWith (Ok (ArchiveObserved observation)) (Ok (MetadataObserved (metadataAssetSha projection, projection)))
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok resolution ->
        assertEqual "metadata field mismatch -> failed" "failed" resolution.Status
        assertTrue "field mismatch reason references version"
            (resolution.Reason.Contains("version", StringComparison.Ordinal))
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "field mismatch: %s" (ReleaseError.message error))
}
let publicStatusSetIsExhaustive () = async {
    let archivePath = Path.Combine(tempRoot, "status.zip")
    let manifestPath = Path.Combine(tempRoot, "status.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let dto = dtoFrom archivePath manifestPath

    let cases : (RemoteArchiveState * MetadataState * string) list =
        [
            AbsentArchive, MetadataAbsent, "published"
            MissingArchive, MetadataMissing, "failed"
            ImmutableArchive, MetadataImmutable, "failed"
        ]
    for archiveState, metadataState, expected in cases do
        let observer = scriptedObserverWith (archiveStateToResult archiveState) (Ok metadataState)
        match! ReleaseEvaluate.evaluate observer dto with
        | Ok resolution ->
            assertEqual (sprintf "status %A %A" archiveState metadataState) expected resolution.Status
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "public status %A %A: %s" archiveState metadataState (ReleaseError.message error))
}
let observedMetadataAssetShaDiffersArchiveShaAndCarried () = async {
    let archivePath = Path.Combine(tempRoot, "sha-carry.zip")
    let manifestPath = Path.Combine(tempRoot, "sha-carry.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let! observationResult = observe archivePath
    let observation =
        match observationResult with
        | Ok value -> value
        | Error error -> failwithf "observe: %s" (ReleaseError.message error)
    let projection =
        makeProjection
            observation.ArchiveSha256
            (observation.ManifestSha256 |> Option.defaultValue "")
            Fixture.canonicalEvaluatedRevision
    let metadataAssetShaValue = metadataAssetSha projection
    assertTrue "observed metadata sha differs from archive sha"
        (metadataAssetShaValue <> observation.ArchiveSha256)
    let observer = scriptedObserverWith (Ok (ArchiveObserved observation)) (Ok (MetadataObserved (metadataAssetShaValue, projection)))
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok resolution ->
        assertEqual "PublishedMetadataAssetSha256 carries the observed metadata sha unchanged"
            (Some metadataAssetShaValue) resolution.PublishedMetadataAssetSha256
        assertTrue "PublishedMetadataAssetSha256 is distinct from PublishedArchiveSha256"
            (Some metadataAssetShaValue <> resolution.PublishedArchiveSha256)
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "metadata sha carried: %s" (ReleaseError.message error))
}
let finalizeVerifiedPublicationFillsRemoteDigests () = async {
    let archivePath = Path.Combine(tempRoot, "fin-pub.zip")
    let manifestPath = Path.Combine(tempRoot, "fin-pub.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let! observationResult = observe archivePath
    let observation =
        match observationResult with
        | Ok value -> value
        | Error error -> failwithf "observe: %s" (ReleaseError.message error)
    let projection =
        makeProjection
            observation.ArchiveSha256
            (observation.ManifestSha256 |> Option.defaultValue "")
            Fixture.canonicalEvaluatedRevision
    let observer = scriptedObserverWith (Ok (ArchiveObserved observation)) (Ok (MetadataObserved (metadataAssetSha projection, projection)))
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok initial ->
        let verifiedArchiveSha = "ff" + String('f', 62)
        let verifiedMetadataSha = "ee" + String('e', 62)
        let published = ReleaseEvaluate.finalizeVerifiedPublication initial verifiedArchiveSha verifiedMetadataSha
        assertEqual "verified publication status is published" "published" published.Status
        assertTrue "verified publication UploadCompleted is true" published.UploadCompleted
        assertEqual "verifiedArchiveSha256 = verifiedArchiveSha" (Some verifiedArchiveSha) published.PublishedArchiveSha256
        assertEqual "verifiedMetadataAssetSha256 = verifiedMetadataSha" (Some verifiedMetadataSha) published.PublishedMetadataAssetSha256
        assertEqual "publishedManifestSha256 carried from snapshot"
            initial.PublishedManifestSha256 published.PublishedManifestSha256
        assertEqual "publishedRevision carried from snapshot"
            initial.PublishedRevision published.PublishedRevision
        assertEqual "Render.EvaluatedHead (current HEAD) carried"
            initial.Render.EvaluatedHead published.Render.EvaluatedHead
        assertEqual "MetadataRecoveryNeeded cleared" false published.MetadataRecoveryNeeded
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "finalizeVerifiedPublication: %s" (ReleaseError.message error))
}
let finalizeRecoveryPreservesVerifiedArchiveFields () = async {
    let archivePath = Path.Combine(tempRoot, "fin-rec.zip")
    let manifestPath = Path.Combine(tempRoot, "fin-rec.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let! observationResult = observe archivePath
    let observation =
        match observationResult with
        | Ok value -> value
        | Error error -> failwithf "observe: %s" (ReleaseError.message error)
    let observer = scriptedObserverWith (Ok (ArchiveObserved observation)) (Ok MetadataAbsent)
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok initial ->
        assertTrue "recovery-needed initial resolution" initial.MetadataRecoveryNeeded
        let verifiedMetadataSha = "dd" + String('d', 62)
        let recovered = ReleaseEvaluate.finalizeRecovery initial verifiedMetadataSha
        assertEqual "recovery status is unchanged" "unchanged" recovered.Status
        assertTrue "recovery UploadCompleted is true" recovered.UploadCompleted
        assertEqual "recovery preserves publishedArchiveSha256"
            initial.PublishedArchiveSha256 recovered.PublishedArchiveSha256
        assertEqual "recovery preserves publishedManifestSha256"
            initial.PublishedManifestSha256 recovered.PublishedManifestSha256
        assertEqual "recovery preserves publishedRevision"
            initial.PublishedRevision recovered.PublishedRevision
        assertEqual "recovery clears MetadataRecoveryNeeded" false recovered.MetadataRecoveryNeeded
        assertEqual "recovery sets new verifiedMetadataAssetSha256"
            (Some verifiedMetadataSha) recovered.PublishedMetadataAssetSha256
        assertEqual "recovery preserves current HEAD (EvaluatedHead)"
            initial.Render.EvaluatedHead recovered.Render.EvaluatedHead
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "finalizeRecovery: %s" (ReleaseError.message error))
}
let finalizeFailureClearsAllPublishedButRetainsRender () = async {
    let archivePath = Path.Combine(tempRoot, "fin-fail.zip")
    let manifestPath = Path.Combine(tempRoot, "fin-fail.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath Fixture.canonicalEvaluatedRevision
    let observer = scriptedObserver MissingArchive
    match! ReleaseEvaluate.evaluate observer (dtoFrom archivePath manifestPath) with
    | Ok initial ->
        assertEqual "ArchiveMissing produces a populated Render" "failed" initial.Status
        let failed = ReleaseEvaluate.finalizeFailure initial.Render "synthetic failure"
        assertEqual "failure status is failed" "failed" failed.Status
        assertEqual "failure clears publishedArchiveSha256" None failed.PublishedArchiveSha256
        assertEqual "failure clears publishedManifestSha256" None failed.PublishedManifestSha256
        assertEqual "failure clears publishedRevision" None failed.PublishedRevision
        assertEqual "failure clears publishedMetadataAssetSha256" None failed.PublishedMetadataAssetSha256
        assertEqual "failure clears MetadataRecoveryNeeded" false failed.MetadataRecoveryNeeded
        assertEqual "failure preserves Render.ComponentId" initial.Render.ComponentId failed.Render.ComponentId
        assertEqual "failure preserves Render.Version" initial.Render.Version failed.Render.Version
        assertEqual "failure preserves Render.ArchiveName" initial.Render.ArchiveName failed.Render.ArchiveName
        assertEqual "failure preserves Render.AssetUri" initial.Render.AssetUri failed.Render.AssetUri
        assertEqual "failure preserves Render.MetadataAssetName" initial.Render.MetadataAssetName failed.Render.MetadataAssetName
        assertEqual "failure preserves Render.EvaluatedHead" initial.Render.EvaluatedHead failed.Render.EvaluatedHead
        assertEqual "failure retains UploadCompleted=false" false failed.UploadCompleted
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "finalizeFailure setup: %s" (ReleaseError.message error))
}

let runSuite () = async {
    do! validateCanonicalAssetUriEmptyRejection ()
    do! validateCanonicalAssetUriWrongOwnerRejection ()
    do! validateCanonicalAssetUriAccepted ()
    do! missingManifestRejection ()
    do! missingArchiveRejection ()
    do! publishedOnArchiveAbsent ()
    do! failedOnArchiveMissing ()
    do! failedOnArchiveImmutable ()
    do! failedOnBumpedSdk ()
    do! unchangedOnByteIdentical ()
    do! unchangedOnRevisionOnlyEquivalent ()
    do! unchangedWithMetadataRecoveryNeeded ()
    do! failedOnMetadataCorrupt ()
    do! failedOnMetadataImmutable ()
    do! failedOnObservedMetadataFieldMismatch ()
    do! publicStatusSetIsExhaustive ()
    do! observedMetadataAssetShaDiffersArchiveShaAndCarried ()
    do! finalizeVerifiedPublicationFillsRemoteDigests ()
    do! finalizeRecoveryPreservesVerifiedArchiveFields ()
    do! finalizeFailureClearsAllPublishedButRetainsRender ()

    printfn "evaluate-tests summary: passed=%d failed=%d" passed failed
    Fixture.safeDeleteDir tempRoot
}


let tests =
    testCaseAsync "Evaluate legacy regression assertions" <| async {
        Directory.CreateDirectory tempRoot |> ignore
        try
            do! runSuite ()
            Expect.equal 0 failed (String.concat Environment.NewLine failures)
        finally
            Fixture.safeDeleteDir tempRoot
    }
