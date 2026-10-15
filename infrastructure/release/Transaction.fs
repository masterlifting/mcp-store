// role: reusable module/helper
module ReleaseTransaction

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open BuildProvenance

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
                            Ok(
                                ReleaseEvaluate.MetadataCorrupt(
                                    BuildProvenance.FileFailure(
                                        "metadata",
                                        tag,
                                        "GITHUB_TOKEN is not set; the metadata observation cannot run")))
                else
                    return
                        Ok(
                            ReleaseEvaluate.MetadataCorrupt(
                                BuildProvenance.FileFailure(
                                    "metadata",
                                    tag,
                                    "the local mirror cannot satisfy the metadata probe; the GitHub transport is required for recovery")))
            } }

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

let private execute
    (transport: ReleasePublish.HttpTransport)
    (resolution: ReleaseEvaluate.Resolution)
    (auth: ReleasePublish.AuthToken option)
    (locator: ReleasePublish.ReleaseLocator)
    (candidate: ReleaseEvaluate.VerifiedCandidate)
    : Async<ReleaseEvaluate.Resolution * int> =
    async {
        match resolution.Status with
        | "published" ->
            match auth with
            | Some authValue ->
                let! archiveUpload =
                    ReleasePublish.uploadCandidateBytes
                        authValue
                        locator
                        resolution.Render.ArchiveName
                        (candidate.ArchiveBytesCopy ())
                        (Some candidate.ArchiveSha256)
                        transport

                match archiveUpload with
                | Error error ->
                    return ReleaseEvaluate.finalizeFailure resolution.Render (ReleaseError.message error), 1
                | Ok verifiedArchiveSha ->
                    let! metadataUpload =
                        ReleasePublish.uploadMetadataBytes authValue locator candidate.Projection transport

                    match metadataUpload with
                    | Error error ->
                        return ReleaseEvaluate.finalizeFailure resolution.Render (ReleaseError.message error), 1
                    | Ok verifiedMetadataSha ->
                        let verifiedManifestSha =
                            match resolution.PublishedManifestSha256 with
                            | Some v -> v
                            | None -> candidate.ManifestSha256

                        let verifiedRevision =
                            match resolution.PublishedRevision with
                            | Some v -> v
                            | None -> candidate.EmbeddedRevision

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
                            return ReleaseEvaluate.finalizeFailure resolution.Render (ReleaseError.message error), 1
                        | Ok () ->
                            let published =
                                ReleaseEvaluate.finalizeVerifiedPublication resolution verifiedArchiveSha verifiedMetadataSha
                            return published, 0
            | None ->
                return ReleaseEvaluate.finalizeFailure resolution.Render "publish-required but GITHUB_TOKEN is missing", 1
        | "unchanged" ->
            if resolution.MetadataRecoveryNeeded then
                match auth with
                | Some authValue ->
                    match resolution.PublishedArchiveSha256, resolution.PublishedManifestSha256, resolution.PublishedRevision with
                    | Some verifiedArchiveSha, Some verifiedManifestSha, Some verifiedRevision ->
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

                        let! uploadResult =
                            ReleasePublish.uploadMetadataBytes authValue locator recoveryProjection transport

                        match uploadResult with
                        | Error error ->
                            return ReleaseEvaluate.finalizeFailure resolution.Render (ReleaseError.message error), 1
                        | Ok verifiedMetadataSha ->
                            let! persistResult =
                                ReleaseMetadata.writeToFileAsync resolution.Render.MetadataLocalPath recoveryProjection

                            match persistResult with
                            | Error error ->
                                return ReleaseEvaluate.finalizeFailure resolution.Render (ReleaseError.message error), 1
                            | Ok _digest ->
                                return ReleaseEvaluate.finalizeRecovery resolution verifiedMetadataSha, 0
                    | _ ->
                        return resolution, 1
                | None ->
                    return ReleaseEvaluate.finalizeFailure resolution.Render "metadata-recovery-required but GITHUB_TOKEN is missing", 1
            else
                let verifiedArchiveSha =
                    match resolution.PublishedArchiveSha256 with
                    | Some v -> v
                    | None -> candidate.ArchiveSha256

                let verifiedManifestSha =
                    match resolution.PublishedManifestSha256 with
                    | Some v -> v
                    | None -> candidate.ManifestSha256

                let verifiedRevision =
                    match resolution.PublishedRevision with
                    | Some v -> v
                    | None -> candidate.EmbeddedRevision

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
                | Error error -> return ReleaseEvaluate.finalizeFailure resolution.Render (ReleaseError.message error), 1
                | Ok () -> return { resolution with UploadCompleted = false }, 0
        | _ ->
            let exitCode =
                match resolution.Render.BumpedVersion with
                | Some _ -> 2
                | None -> 1
            return { resolution with UploadCompleted = false }, exitCode
    }

let run
    (transport: ReleasePublish.HttpTransport)
    (auth: ReleasePublish.AuthToken option)
    (useGitHub: bool)
    (downloadDir: string)
    (locator: ReleasePublish.ReleaseLocator)
    (candidate: ReleaseEvaluate.VerifiedCandidate)
    : Async<Result<ReleaseEvaluate.Resolution * int, ReleaseError>> =
    async {
        let observer = buildRemoteObserverWith transport locator useGitHub auth downloadDir

        match! ReleaseEvaluate.evaluate observer candidate with
        | Ok resolution ->
            let! final = execute transport resolution auth locator candidate
            return Ok final
        | Error error -> return Error error
    }

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
