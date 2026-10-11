// role: release/build entrypoint
// Thin CLI shell around `ReleaseEvaluate`; emits one JSON object on stdout before every exit.
module ReleaseRunner

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let private writeResolution (resolution: ReleaseEvaluate.Resolution) : string =
    let node = JsonObject()
    node["status"] <- JsonValue.Create resolution.Status
    node["reason"] <- JsonValue.Create resolution.Reason
    node["uploadCompleted"] <- JsonValue.Create resolution.UploadCompleted
    node["recoveryNeeded"] <- JsonValue.Create resolution.MetadataRecoveryNeeded
    let render = resolution.Render
    node["componentId"] <- JsonValue.Create render.ComponentId
    node["version"] <- JsonValue.Create render.Version
    node["archiveName"] <- JsonValue.Create render.ArchiveName
    node["assetUri"] <- JsonValue.Create render.AssetUri
    node["metadataAssetName"] <- JsonValue.Create render.MetadataAssetName
    node["metadataLocalPath"] <- JsonValue.Create render.MetadataLocalPath
    node["candidateArchiveSha256"] <- JsonValue.Create render.CandidateArchiveSha256
    node["candidateManifestSha256"] <- JsonValue.Create render.CandidateManifestSha256
    node["currentEvaluatedHead"] <- JsonValue.Create render.EvaluatedHead
    node["candidateRevision"] <- JsonValue.Create render.CandidateRevision
    match render.BumpedVersion with
    | Some value -> node["bumpedVersion"] <- JsonValue.Create value
    | None -> ()
    match render.ProducerSdk with
    | Some value -> node["producerSdk"] <- JsonValue.Create value
    | None -> ()
    match render.ProducerRuntime with
    | Some value -> node["producerRuntime"] <- JsonValue.Create value
    | None -> ()
    match render.ProducerPlatform with
    | Some value -> node["producerPlatform"] <- JsonValue.Create value
    | None -> ()
    node["publishedArchiveSha256"] <- JsonValue.Create(resolution.PublishedArchiveSha256 |> Option.toObj)
    node["publishedManifestSha256"] <- JsonValue.Create(resolution.PublishedManifestSha256 |> Option.toObj)
    node["publishedRevision"] <- JsonValue.Create(resolution.PublishedRevision |> Option.toObj)
    node["publishedMetadataSha256"] <- JsonValue.Create(resolution.PublishedMetadataAssetSha256 |> Option.toObj)
    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

let private emitResolution (resolution: ReleaseEvaluate.Resolution) =
    // Structured JSON before every emit so the wrapper recovers status/reason/version/URI/name/revision/hashes on every branch.
    let payload = writeResolution resolution
    let buffer = Encoding.UTF8.GetBytes payload
    try
        use stdout = Console.OpenStandardOutput()
        stdout.Write(buffer, 0, buffer.Length)
        stdout.Write([| byte '\n' |], 0, 1)
        stdout.Flush()
    with
    | _ -> ()

let private canonicalRepository () : string * string = "masterlifting", "mcp-store"

// Render with every known field already filled; missing fields stay empty so partial failures preserve the context that was determined.
let private renderFrom
    (componentId: string)
    (version: string)
    (archiveName: string)
    (assetUri: string)
    (metadataAssetName: string)
    (metadataLocalPath: string)
    (candidateArchiveSha: string)
    (candidateManifestSha: string)
    (evaluatedHead: string)
    (candidateRevision: string)
    (producerSdk: string option)
    (producerRuntime: string option)
    (producerPlatform: string option)
    : ReleaseEvaluate.Render =
    { ComponentId = componentId
      Version = version
      ArchiveName = archiveName
      AssetUri = assetUri
      MetadataAssetName = metadataAssetName
      MetadataLocalPath = metadataLocalPath
      CandidateArchiveSha256 = candidateArchiveSha
      CandidateManifestSha256 = candidateManifestSha
      EvaluatedHead = evaluatedHead
      CandidateRevision = candidateRevision
      ProducerSdk = producerSdk
      ProducerRuntime = producerRuntime
      ProducerPlatform = producerPlatform
      BumpedVersion = None }

let private parseContractJson (contractJson: string) : Result<ReleaseCompare.ComponentContract * string, BuildProvenance.ReleaseError> =
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
        Error(BuildProvenance.MalformedArtifact("contract", $"the contract JSON is not valid: {error.Message}"))
    | :? FormatException as error ->
        Error(BuildProvenance.MalformedArtifact("contract", $"the contract JSON is not valid: {error.Message}"))
    | :? InvalidOperationException as error ->
        Error(BuildProvenance.MalformedArtifact("contract", $"the contract JSON is not valid: {error.Message}"))

// Writes the verified/observed immutable metadata projection; candidate values are never persisted on the success path.
let private persistVerifiedProjection
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
    : Async<Result<unit, BuildProvenance.ReleaseError>> =
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
    : Async<Result<string * string, BuildProvenance.ReleaseError>> =
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

let private runnerObserver
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
                        let transport = ReleasePublish.defaultHttpTransport ()
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
                        let transport = ReleasePublish.defaultHttpTransport ()
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

let private requireObserved
    (render: ReleaseEvaluate.Render)
    (label: string)
    (value: string option)
    : Async<Result<string, int>> =
    async {
        match value with
        | Some v -> return Ok v
        | None ->
            emitResolution (ReleaseEvaluate.finalizeFailure render label)
            return Error 1
    }

let private executeResolution
    (resolution: ReleaseEvaluate.Resolution)
    (auth: ReleasePublish.AuthToken option)
    (locator: ReleasePublish.ReleaseLocator)
    (candidateProjection: ReleaseMetadata.ProducerMetadataProjection)
    (archivePath: string)
    (useGitHub: bool)
    : Async<int> =
    async {
        match resolution.Status with
        | "published" ->
            match auth with
            | Some authValue ->
                let transport = ReleasePublish.defaultHttpTransport ()
                let! uploadResult =
                    uploadArchiveAndMetadata authValue locator candidateProjection (Some archivePath) (Some resolution.Render.CandidateArchiveSha256) transport

                match uploadResult with
                | Error error ->
                    emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error))
                    return 1
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
                        emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error))
                        return 1
                    | Ok () ->
                        // Pure completion: read verified digests from transport, set manifest sha + revision from the same verified snapshot.
                        let published =
                            ReleaseEvaluate.finalizeVerifiedPublication
                                resolution
                                verifiedArchiveSha
                                verifiedMetadataSha
                        emitResolution published
                        return 0
            | None ->
                if useGitHub then
                    emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render "publish-required but GITHUB_TOKEN is missing")
                else
                    emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render "publish-required but no production transport is configured")
                return 1
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
                    | Error code -> return code
                    | Ok verifiedArchiveSha ->
                        let! verifiedManifestShaResult =
                            requireObserved
                                resolution.Render
                                "metadata recovery requires the observed published manifest sha256, which was not captured"
                                resolution.PublishedManifestSha256

                        match verifiedManifestShaResult with
                        | Error code -> return code
                        | Ok verifiedManifestSha ->
                            let! verifiedRevisionResult =
                                requireObserved
                                    resolution.Render
                                    "metadata recovery requires the observed published revision, which was not captured"
                                    resolution.PublishedRevision

                            match verifiedRevisionResult with
                            | Error code -> return code
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

                                let transport = ReleasePublish.defaultHttpTransport ()
                                let! uploadResult = ReleasePublish.uploadMetadata authValue locator recoveryProjection transport

                                match uploadResult with
                                | Error error ->
                                    emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error))
                                    return 1
                                | Ok verifiedMetadataSha ->
                                    let! persistResult = ReleaseMetadata.writeToFileAsync resolution.Render.MetadataLocalPath recoveryProjection

                                    match persistResult with
                                    | Error error ->
                                        emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error))
                                        return 1
                                    | Ok _digest ->
                                        emitResolution (ReleaseEvaluate.finalizeRecovery resolution verifiedMetadataSha)
                                        return 0
                | None ->
                    if useGitHub then
                        emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render "metadata-recovery-required but GITHUB_TOKEN is missing")
                    else
                        emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render "metadata-recovery-required but no production transport is configured")
                    return 1
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
                    emitResolution (ReleaseEvaluate.finalizeFailure resolution.Render (BuildProvenance.ReleaseError.message error))
                    return 1
                | Ok () ->
                    emitResolution { resolution with UploadCompleted = false }
                    return 0
        | _ ->
            let exitCode =
                match resolution.Render.BumpedVersion with
                | Some _ -> 2
                | None -> 1
            emitResolution { resolution with UploadCompleted = false }
            return exitCode
    }

[<EntryPoint>]
let main argv =
    // Standalone entry bridge: the compiled entrypoint must drive its async
    // pipeline through one synchronous wait at the host boundary; a deeper
    // Async.RunSynchronously would freeze stdout after `emitResolution` and
    // lose the recovered JSON before the host observes process exit.
    let runAsync () : Async<int> =
        async {
            if argv.Length < 3 then
                eprintfn "usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>"
                return 2
            else
                try
                    let manifestPath = argv.[0]
                    let archivePath = argv.[1]
                    let contractJson = argv.[2]

                    let! pipeline =
                        async {
                            // Parse the contract up front so structured failures carry the config context.
                            let! contractResult =
                                async {
                                    match parseContractJson contractJson with
                                    | Ok value -> return Ok value
                                    | Error error ->
                                        emitResolution
                                            (ReleaseEvaluate.finalizeFailure
                                                (renderFrom "" "" "" "" "" "" "" "" "" "" None None None)
                                                (BuildProvenance.ReleaseError.message error))
                                        return Error 4
                                }

                            match contractResult with
                            | Error code -> return Error code
                            | Ok (contract, canonicalAssetUri) ->
                                let owner, repository = canonicalRepository ()

                                let preMetadataRender =
                                    renderFrom
                                        contract.ComponentId
                                        contract.Version
                                        contract.ArchiveName
                                        canonicalAssetUri
                                        ""
                                        ""
                                        ""
                                        ""
                                        ""
                                        ""
                                        None
                                        None
                                        None

                                let failedWithConfig reason =
                                    ReleaseEvaluate.finalizeFailure preMetadataRender reason

                                let typedFailedWithConfig (error: BuildProvenance.ReleaseError) =
                                    failedWithConfig (BuildProvenance.ReleaseError.message error)

                                let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

                                let! cleanResult = BuildProvenance.assertCleanTree root

                                match cleanResult with
                                | Error error ->
                                    emitResolution (typedFailedWithConfig error)
                                    return Error 4
                                | Ok () ->
                                    let! headResult = BuildProvenance.committedHead root

                                    match headResult with
                                    | Error error ->
                                        emitResolution (typedFailedWithConfig error)
                                        return Error 4
                                    | Ok head ->
                                        if String.IsNullOrEmpty canonicalAssetUri then
                                            emitResolution
                                                (failedWithConfig "assetUri is missing from the contract DTO; refusing to silently reconstruct it from environment")
                                            return Error 1
                                        elif not (File.Exists manifestPath) then
                                            emitResolution (typedFailedWithConfig (BuildProvenance.MissingArtifact manifestPath))
                                            return Error 3
                                        elif not (File.Exists archivePath) then
                                            emitResolution (typedFailedWithConfig (BuildProvenance.MissingArtifact archivePath))
                                            return Error 3
                                        else
                                            let! revisionResult = BuildProvenance.assertManifestRevision manifestPath head

                                            match revisionResult with
                                            | Error error ->
                                                emitResolution (typedFailedWithConfig error)
                                                return Error 4
                                            | Ok _revisionFromManifest ->
                                                let locator : ReleasePublish.ReleaseLocator =
                                                    { Owner = owner
                                                      Repository = repository
                                                      Tag = contract.ComponentId }

                                                let metadataAssetName =
                                                    ReleaseMetadata.metadataAssetName contract.ComponentId contract.Version

                                                let metadataLocalPath =
                                                    match Environment.GetEnvironmentVariable "MCP_RELEASE_METADATA_LOCAL_PATH" with
                                                    | null | "" -> Path.Combine(Path.GetDirectoryName manifestPath, metadataAssetName)
                                                    | value -> value

                                                let producerSdk = Environment.GetEnvironmentVariable "RELEASE_EXPECTED_SDK"
                                                let producerRuntime = Environment.GetEnvironmentVariable "RELEASE_EXPECTED_RUNTIME"
                                                let producerPlatform = Environment.GetEnvironmentVariable "RELEASE_EXPECTED_PLATFORM"

                                                let producerSdkOpt = if isNull producerSdk then None else Some producerSdk
                                                let producerRuntimeOpt = if isNull producerRuntime then None else Some producerRuntime
                                                let producerPlatformOpt = if isNull producerPlatform then None else Some producerPlatform

                                                let! hashes =
                                                    async {
                                                        let archiveHash =
                                                            try
                                                                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes archivePath)
                                                                |> Convert.ToHexString
                                                                |> fun v -> v.ToLowerInvariant()
                                                            with
                                                            | :? IOException as error ->
                                                                emitResolution (typedFailedWithConfig (BuildProvenance.FileFailure("hash", archivePath, error.Message)))
                                                                ""
                                                            | :? UnauthorizedAccessException as error ->
                                                                emitResolution (typedFailedWithConfig (BuildProvenance.FileFailure("hash", archivePath, error.Message)))
                                                                ""

                                                        let manifestHash =
                                                            try
                                                                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes manifestPath)
                                                                |> Convert.ToHexString
                                                                |> fun v -> v.ToLowerInvariant()
                                                            with
                                                            | :? IOException as error ->
                                                                emitResolution (typedFailedWithConfig (BuildProvenance.FileFailure("hash", manifestPath, error.Message)))
                                                                ""
                                                            | :? UnauthorizedAccessException as error ->
                                                                emitResolution (typedFailedWithConfig (BuildProvenance.FileFailure("hash", manifestPath, error.Message)))
                                                                ""

                                                        let revisionText =
                                                            try
                                                                let text = File.ReadAllText manifestPath
                                                                let node = JsonNode.Parse text
                                                                (node.AsObject()["revision"]).GetValue<string>()
                                                            with
                                                            | _ ->
                                                                emitResolution
                                                                    (typedFailedWithConfig
                                                                        (BuildProvenance.MalformedArtifact(manifestPath, "the embedded 'revision' field is missing or unreadable")))
                                                                ""

                                                        return (archiveHash, manifestHash, revisionText)
                                                    }

                                                let candidateArchiveSha256, candidateManifestSha256, candidateRevision = hashes

                                                let candidateProjection =
                                                    ReleaseMetadata.makeProjection
                                                        contract.ComponentId
                                                        contract.Version
                                                        canonicalAssetUri
                                                        contract.ArchiveName
                                                        candidateArchiveSha256
                                                        candidateManifestSha256
                                                        candidateRevision
                                                        producerSdkOpt
                                                        producerRuntimeOpt
                                                        producerPlatformOpt

                                                let authValue =
                                                    match ReleasePublish.environmentToken () with
                                                    | Ok token -> Some token
                                                    | Error _ -> None

                                                let useGitHub =
                                                    not (String.IsNullOrEmpty (Environment.GetEnvironmentVariable "MCP_RELEASE_GITHUB_OWNER"))
                                                    && not (String.IsNullOrEmpty (Environment.GetEnvironmentVariable "MCP_RELEASE_GITHUB_REPOSITORY"))
                                                    && Option.isSome authValue

                                                let downloadDir =
                                                    match Environment.GetEnvironmentVariable "MCP_RELEASE_DOWNLOAD_DIR" with
                                                    | null | "" -> Path.Combine(Path.GetTempPath(), $"mcp-release-download-{Guid.NewGuid():N}")
                                                    | value -> value

                                                let observer = runnerObserver locator useGitHub authValue downloadDir

                                                let dto : ReleaseEvaluate.CandidateDto =
                                                    { Contract = contract
                                                      ManifestPath = manifestPath
                                                      ArchivePath = archivePath
                                                      EvaluatedHead = head
                                                      AssetUri = canonicalAssetUri
                                                      MetadataAssetName = metadataAssetName
                                                      MetadataLocalPath = metadataLocalPath
                                                      ProducerSdk = producerSdkOpt
                                                      ProducerRuntime = producerRuntimeOpt
                                                      ProducerPlatform = producerPlatformOpt
                                                      RepositoryOwner = owner
                                                      RepositoryName = repository }

                                                // Local metadata write deferred so the file reflects verified/published bytes, not candidate values.
                                                let! evaluateOutcome = ReleaseEvaluate.evaluate observer dto

                                                match evaluateOutcome with
                                                | Ok resolution ->
                                                    let! finalCode = executeResolution resolution authValue locator candidateProjection archivePath useGitHub
                                                    return Ok finalCode
                                                | Error error ->
                                                    emitResolution (typedFailedWithConfig error)
                                                    return Ok 1
                        }

                    return
                        match pipeline with
                        | Ok code -> code
                        | Error code -> code
                with
                | error ->
                    emitResolution
                        (ReleaseEvaluate.finalizeFailure
                            (renderFrom "" "" "" "" "" "" "" "" "" "" None None None)
                            error.Message)
                    return 1
        }

    // Standalone entry bridge: the only synchronous top-level wait so the
    // asynchronous pipeline above drives process exit through the returned code.
    runAsync () |> Async.RunSynchronously
