// role: reusable module/helper
// Transaction execution: drives a `Resolution` through the publish / metadata-recovery / failure branches via injected transport.
// Pure of CLI/JSON concerns so tests and the compiled entrypoint share one execution path.
// Capture/policy/HTTP/process live here as the imperative boundary; classification is in `ReleaseEvaluate`.
module ReleaseTransaction

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance

// Writes the verified/observed immutable metadata projection; candidate values are never persisted on the success path.
let persistVerifiedProjection
    (metadataLocalPath: string)
    (componentId: string)
    (version: string)
    (assetUri: string)
    (archiveName: string)
    (verifiedArchiveSha: string)
    (verifiedManifestSha: string)
    (verifiedRevision: string)
    (producerSdk: string option)
    (producerRuntime: string option)
    (producerPlatform: string option)
    : Async<Result<unit, ReleaseError>> =
    async {
        let projection =
            ReleaseMetadata.makeProjection
                componentId
                version
                assetUri
                archiveName
                verifiedArchiveSha
                verifiedManifestSha
                verifiedRevision
                producerSdk
                producerRuntime
                producerPlatform

        match! ReleaseMetadata.writeToFileAsync metadataLocalPath projection with
        | Ok _ -> return Ok()
        | Error error -> return Error error
    }

let private uploadArchiveAndMetadata
    (auth: ReleasePublish.AuthToken)
    (locator: ReleasePublish.ReleaseLocator)
    (candidateProjection: ReleaseMetadata.ProducerMetadataProjection)
    (archivePath: string option)
    (candidateArchiveSha: string option)
    (transport: ReleasePublish.HttpTransport)
    : Async<Result<string * string, ReleaseError>> =
    async {
        match archivePath, candidateArchiveSha with
        | Some path, Some archiveSha ->
            let! archiveDigest =
                ReleasePublish.uploadCandidate auth locator candidateProjection.ArchiveName path archiveSha transport

            match archiveDigest with
            | Error error -> return Error error
            | Ok verifiedArchiveSha ->
                let! metadataDigest = ReleasePublish.uploadMetadata auth locator candidateProjection transport

                match metadataDigest with
                | Error error -> return Error error
                | Ok verifiedMetadataSha ->
                    return Ok(verifiedArchiveSha, verifiedMetadataSha)
        | _ ->
            let! metadataDigest = ReleasePublish.uploadMetadata auth locator candidateProjection transport

            match metadataDigest with
            | Error error -> return Error error
            | Ok verifiedMetadataSha ->
                return Ok("", verifiedMetadataSha)
    }

// Adapter from `ReleasePublish` (transport-owned state) to `ReleaseEvaluate` (decision-owned state).
// The transport is supplied so the same single instance drives observation, evaluate, and execute in one seam.
let private buildRemoteObserverWith
    (transport: ReleasePublish.HttpTransport)
    (locator: ReleasePublish.ReleaseLocator)
    (useGitHub: bool)
    (auth: ReleasePublish.AuthToken option)
    (downloadDir: string)
    : ReleaseEvaluate.RemoteObserver =
    { ObserveArchive =
        fun (tag, assetName) ->
            async {
                if useGitHub then
                    match auth with
                    | Some authValue ->
                        let ghObserver = ReleasePublish.gitHubObserver authValue downloadDir transport
                        let! result = ghObserver.Observe locator assetName
                        return
                            match result with
                            | Ok (ReleasePublish.Observed observation) ->
                                Ok(ReleaseEvaluate.ArchiveObserved observation)
                            | Ok ReleasePublish.Absent -> Ok ReleaseEvaluate.ArchiveAbsent
                            | Ok ReleasePublish.Missing -> Ok ReleaseEvaluate.ArchiveMissing
                            | Ok ReleasePublish.Immutable ->
                                Ok(ReleaseEvaluate.ArchiveImmutable "the existing release is configured as immutable")
                            | Error error -> Error error
                    | None ->
                        return
                            Error(
                                BuildProvenance.FileFailure(
                                    "transport",
                                    tag,
                                    "GITHUB_TOKEN is not set; the production transport cannot run"))
                else
                    return
                        Error(
                            BuildProvenance.FileFailure(
                                "transport",
                                tag,
                                "the production transport is not selected; set MCP_RELEASE_GITHUB_OWNER + MCP_RELEASE_GITHUB_REPOSITORY + GITHUB_TOKEN to enable remote observation"))
            }
      ObserveMetadata =
        fun (tag, assetName) ->
            async {
                if useGitHub then
                    match auth with
                    | Some authValue ->
                        let metadataObserver = ReleasePublish.gitHubMetadataObserver authValue downloadDir transport
                        let! result = metadataObserver.ObserveMetadata locator assetName
                        return
                            match result with
                            | Ok (ReleasePublish.MetadataObserved (_, sha256, parsed)) ->
                                Ok(ReleaseEvaluate.MetadataObserved (sha256, parsed))
                            | Ok ReleasePublish.MetadataAbsent -> Ok ReleaseEvaluate.MetadataAbsent
                            | Ok ReleasePublish.MetadataMissing -> Ok ReleaseEvaluate.MetadataMissing
                            | Ok ReleasePublish.MetadataImmutable -> Ok ReleaseEvaluate.MetadataImmutable
                            | Error error ->
                                Ok(ReleaseEvaluate.MetadataCorrupt error)
                    | None ->
                        return
                            Error(
                                BuildProvenance.FileFailure(
                                    "metadata",
                                    assetName,
                                    "GITHUB_TOKEN is not set; the metadata observation cannot run"))
                else
                    // Local-mirror metadata failures must fail closed; the GitHub transport is required for recovery.
                    return
                        Ok(
                            ReleaseEvaluate.MetadataCorrupt(
                                BuildProvenance.FileFailure(
                                    "metadata",
                                    assetName,
                                    "the local mirror cannot satisfy the metadata probe; the GitHub transport is required for recovery")))
            } }

// Convenience composition: builds the observer with the default production transport so existing callers
// (notably the missing-credentials test) retain their original signature. The canonical, transport-injected
// path is `Transaction.run` and `Transaction.execute` below.
let buildRemoteObserver
    (locator: ReleasePublish.ReleaseLocator)
    (useGitHub: bool)
    (auth: ReleasePublish.AuthToken option)
    (downloadDir: string)
    : ReleaseEvaluate.RemoteObserver =
    buildRemoteObserverWith (ReleasePublish.defaultHttpTransport ()) locator useGitHub auth downloadDir

let private requireObserved
    (render: ReleaseEvaluate.Render)
    (label: string)
    (value: string option)
    : Async<Result<string, int>> =
    async {
        match value with
        | Some v -> return Ok v
        | None ->
            return Error 1
    }

// Drive a classified Resolution through the publish / metadata-recovery / failure branches
// using the supplied transport. Returns the final Resolution; exit-code mapping is the
// caller's responsibility.
let execute
    (transport: ReleasePublish.HttpTransport)
    (resolution: ReleaseEvaluate.Resolution)
    (auth: ReleasePublish.AuthToken option)
    (locator: ReleasePublish.ReleaseLocator)
    (candidateProjection: ReleaseMetadata.ProducerMetadataProjection)
    (archivePath: string)
    (useGitHub: bool)
    : Async<ReleaseEvaluate.Resolution * int> =
    async {
        match resolution.Status with
        | "published" ->
            match auth with
            | Some authValue ->
                let! uploadResult =
                    uploadArchiveAndMetadata authValue locator candidateProjection (Some archivePath) (Some resolution.Render.CandidateArchiveSha256) transport

                match uploadResult with
                | Error error ->
                    return ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error), 1
                | Ok (verifiedArchiveSha, verifiedMetadataSha) ->
                    let verifiedManifestSha =
                        match resolution.PublishedManifestSha256 with
                        | Some v -> v
                        | None -> resolution.Render.CandidateManifestSha256

                    let verifiedRevision =
                        match resolution.PublishedRevision with
                        | Some v -> v
                        | None -> resolution.Render.CandidateRevision

                    let! persistResult =
                        persistVerifiedProjection
                            resolution.Render.MetadataLocalPath
                            resolution.Render.ComponentId
                            resolution.Render.Version
                            resolution.Render.AssetUri
                            resolution.Render.ArchiveName
                            verifiedArchiveSha
                            verifiedManifestSha
                            verifiedRevision
                            resolution.Render.ProducerSdk
                            resolution.Render.ProducerRuntime
                            resolution.Render.ProducerPlatform

                    match persistResult with
                    | Error error ->
                        return ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error), 1
                    | Ok () ->
                        // Pure completion: read verified digests from transport, set manifest sha + revision from the same verified snapshot.
                        let published =
                            ReleaseEvaluate.finalizeVerifiedPublication
                                resolution
                                verifiedArchiveSha
                                verifiedMetadataSha
                        return published, 0
            | None ->
                if useGitHub then
                    return ReleaseEvaluate.finalizeFailure resolution.Render "publish-required but GITHUB_TOKEN is missing", 1
                else
                    return ReleaseEvaluate.finalizeFailure resolution.Render "publish-required but no production transport is configured", 1
        | "unchanged" ->
            if resolution.MetadataRecoveryNeeded then
                match auth with
                | Some authValue ->
                    let! verifiedArchiveShaResult =
                        requireObserved
                            resolution.Render
                            "metadata recovery requires the observed published archive sha256, which was not captured"
                            resolution.PublishedArchiveSha256

                    match verifiedArchiveShaResult with
                    | Error code -> return resolution, code
                    | Ok verifiedArchiveSha ->
                        let! verifiedManifestShaResult =
                            requireObserved
                                resolution.Render
                                "metadata recovery requires the observed published manifest sha256, which was not captured"
                                resolution.PublishedManifestSha256

                        match verifiedManifestShaResult with
                        | Error code -> return resolution, code
                        | Ok verifiedManifestSha ->
                            let! verifiedRevisionResult =
                                requireObserved
                                    resolution.Render
                                    "metadata recovery requires the observed published revision, which was not captured"
                                    resolution.PublishedRevision

                            match verifiedRevisionResult with
                            | Error code -> return resolution, code
                            | Ok verifiedRevision ->
                                let recoveryProjection =
                                    ReleaseMetadata.makeProjection
                                        resolution.Render.ComponentId
                                        resolution.Render.Version
                                        resolution.Render.AssetUri
                                        resolution.Render.ArchiveName
                                        verifiedArchiveSha
                                        verifiedManifestSha
                                        verifiedRevision
                                        resolution.Render.ProducerSdk
                                        resolution.Render.ProducerRuntime
                                        resolution.Render.ProducerPlatform

                                let! uploadResult = ReleasePublish.uploadMetadata authValue locator recoveryProjection transport

                                match uploadResult with
                                | Error error ->
                                    return ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error), 1
                                | Ok verifiedMetadataSha ->
                                    let! persistResult = ReleaseMetadata.writeToFileAsync resolution.Render.MetadataLocalPath recoveryProjection

                                    match persistResult with
                                    | Error error ->
                                        return ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error), 1
                                    | Ok _digest ->
                                        return ReleaseEvaluate.finalizeRecovery resolution verifiedMetadataSha, 0
                | None ->
                    if useGitHub then
                        return ReleaseEvaluate.finalizeFailure resolution.Render "metadata-recovery-required but GITHUB_TOKEN is missing", 1
                    else
                        return ReleaseEvaluate.finalizeFailure resolution.Render "metadata-recovery-required but no production transport is configured", 1
            else
                let verifiedArchiveSha =
                    match resolution.PublishedArchiveSha256 with
                    | Some v -> v
                    | None -> resolution.Render.CandidateArchiveSha256

                let verifiedManifestSha =
                    match resolution.PublishedManifestSha256 with
                    | Some v -> v
                    | None -> resolution.Render.CandidateManifestSha256

                let verifiedRevision =
                    match resolution.PublishedRevision with
                    | Some v -> v
                    | None -> resolution.Render.CandidateRevision

                let! persistResult =
                    persistVerifiedProjection
                        resolution.Render.MetadataLocalPath
                        resolution.Render.ComponentId
                        resolution.Render.Version
                        resolution.Render.AssetUri
                        resolution.Render.ArchiveName
                        verifiedArchiveSha
                        verifiedManifestSha
                        verifiedRevision
                        resolution.Render.ProducerSdk
                        resolution.Render.ProducerRuntime
                        resolution.Render.ProducerPlatform

                match persistResult with
                | Error error ->
                    return ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error), 1
                | Ok () ->
                    return { resolution with UploadCompleted = false }, 0
        | _ ->
            let exitCode =
                match resolution.Render.BumpedVersion with
                | Some _ -> 2
                | None -> 1
            return { resolution with UploadCompleted = false }, exitCode
    }

// The single canonical transaction path: builds the observer with the supplied transport, drives
// `ReleaseEvaluate.evaluate`, then dispatches the resolution through the same transport.
// Default production composition happens once at the CLI boundary; tests bind a recording transport
// here for the full observe/evaluate/execute pipeline.
let run
    (transport: ReleasePublish.HttpTransport)
    (auth: ReleasePublish.AuthToken option)
    (useGitHub: bool)
    (downloadDir: string)
    (locator: ReleasePublish.ReleaseLocator)
    (dto: ReleaseEvaluate.CandidateDto)
    (candidateProjection: ReleaseMetadata.ProducerMetadataProjection)
    (archivePath: string)
    : Async<Result<ReleaseEvaluate.Resolution * int, ReleaseError>> =
    async {
        let observer = buildRemoteObserverWith transport locator useGitHub auth downloadDir
        match! ReleaseEvaluate.evaluate observer dto with
        | Ok resolution ->
            let! final = execute transport resolution auth locator candidateProjection archivePath useGitHub
            return Ok final
        | Error error -> return Error error
    }

// Parses the JSON contract DTO and returns either the structured contract + the canonical assetUri
// it carried, or a typed failure. The runner must never reconstruct the assetUri from environment.
let parseContractJson (contractJson: string) : Result<ReleaseCompare.ComponentContract * string, ReleaseError> =
    try
        let doc = System.Text.Json.Nodes.JsonNode.Parse(contractJson)
        let obj = doc.AsObject()
        let assetUriNode = obj.["assetUri"]
        let assetUri =
            if isNull assetUriNode then ""
            else
                try assetUriNode.GetValue<string>()
                with _ -> ""
        let allowlist =
            obj.["entryAllowlist"].AsArray()
            |> Seq.choose (fun v ->
                if isNull v then None
                else
                    try Some (v.GetValue<string>())
                    with _ -> None)
            |> Seq.toList
        Ok
            ({ ComponentId = obj["componentId"].GetValue<string>()
               Version = obj["version"].GetValue<string>()
               ArchiveName = obj["archiveName"].GetValue<string>()
               EntryAllowlist = allowlist }, assetUri)
    with
    | :? System.Text.Json.JsonException as error ->
        Error(MalformedArtifact("contract", $"the contract JSON is not valid: {error.Message}"))
    | :? FormatException as error ->
        Error(MalformedArtifact("contract", $"the contract JSON is not valid: {error.Message}"))
    | :? InvalidOperationException as error ->
        Error(MalformedArtifact("contract", $"the contract JSON is not valid: {error.Message}"))
