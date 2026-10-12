// role: release/build entrypoint
// Thin CLI shell. Two modes: `orchestrate <config-path>` runs the per-component pipeline; `<manifest> <archive> <contract-json>` runs the standalone transaction.
// Emits one JSON object on stdout before every exit; structured failure on parse error and on the empty-argv case.
module ReleaseRunner

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let private emitResolution (resolution: ReleaseEvaluate.Resolution) : unit =
    let payload = ReleaseOrchestration.renderResolution resolution
    let buffer = Encoding.UTF8.GetBytes(payload + "\n")
    try
        use stdout = Console.OpenStandardOutput()
        stdout.Write(buffer, 0, buffer.Length)
        stdout.Flush()
    with
    | _ -> ()

let private emitPreContractFailure (message: string) : unit =
    let node = JsonObject()
    node["status"] <- JsonValue.Create "failed"
    node["reason"] <- JsonValue.Create message
    let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
    let buffer = Encoding.UTF8.GetBytes(payload + "\n")
    try
        use stdout = Console.OpenStandardOutput()
        stdout.Write(buffer, 0, buffer.Length)
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

// Standalone-transaction pipeline: parse the contract DTO, validate the on-disk manifest, hash the artifacts, then drive the single `ReleaseTransaction.run` seam (observe / evaluate / execute with one shared transport).
let private runTransactionMode (argv: string[]) : Async<int> =
    async {
        let manifestPath = argv.[0]
        let archivePath = argv.[1]
        let contractJson = argv.[2]

        let! contractResult =
            async {
                match ReleaseTransaction.parseContractJson contractJson with
                | Ok value -> return Ok value
                | Error error ->
                    emitPreContractFailure
                        (sprintf "the contract DTO is invalid: %s" (BuildProvenance.ReleaseError.message error))
                    return Error 4
            }

        match contractResult with
        | Error code -> return code
        | Ok (contract, canonicalAssetUri) ->
            let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
            let! rosterResult = ReleaseEnrollment.loadRoster root

            match rosterResult with
            | Error error ->
                emitPreContractFailure (BuildProvenance.ReleaseError.message error)
                return 4
            | Ok roster ->
                match ReleaseEnrollment.validateRoster roster with
                | Error error ->
                    emitPreContractFailure (BuildProvenance.ReleaseError.message error)
                    return 4
                | Ok () ->
                    match ReleaseEnrollment.validateComponentIdInRoster contract.ComponentId roster with
                    | Error error ->
                        emitPreContractFailure (BuildProvenance.ReleaseError.message error)
                        return 1
                    | Ok () ->
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

                        let! cleanResult = BuildProvenance.assertCleanTree root

                        match cleanResult with
                        | Error error ->
                            emitResolution (typedFailedWithConfig error)
                            return 4
                        | Ok () ->
                            let! headResult = BuildProvenance.committedHead root

                            match headResult with
                            | Error error ->
                                emitResolution (typedFailedWithConfig error)
                                return 4
                            | Ok head ->
                                if String.IsNullOrEmpty canonicalAssetUri then
                                    emitPreContractFailure "assetUri is missing from the contract DTO; refusing to silently reconstruct it from environment"
                                    return 1
                                elif not (File.Exists manifestPath) then
                                    emitPreContractFailure (BuildProvenance.ReleaseError.message (BuildProvenance.MissingArtifact manifestPath))
                                    return 3
                                elif not (File.Exists archivePath) then
                                    emitPreContractFailure (BuildProvenance.ReleaseError.message (BuildProvenance.MissingArtifact archivePath))
                                    return 3
                                else
                                    let! revisionResult = BuildProvenance.assertManifestRevision manifestPath head

                                    match revisionResult with
                                    | Error error ->
                                        emitPreContractFailure (BuildProvenance.ReleaseError.message error)
                                        return 4
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
                                                        emitPreContractFailure (BuildProvenance.ReleaseError.message (BuildProvenance.FileFailure("hash", archivePath, error.Message)))
                                                        ""
                                                    | :? UnauthorizedAccessException as error ->
                                                        emitPreContractFailure (BuildProvenance.ReleaseError.message (BuildProvenance.FileFailure("hash", archivePath, error.Message)))
                                                        ""

                                                let manifestHash =
                                                    try
                                                        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes manifestPath)
                                                        |> Convert.ToHexString
                                                        |> fun v -> v.ToLowerInvariant()
                                                    with
                                                    | :? IOException as error ->
                                                        emitPreContractFailure (BuildProvenance.ReleaseError.message (BuildProvenance.FileFailure("hash", manifestPath, error.Message)))
                                                        ""
                                                    | :? UnauthorizedAccessException as error ->
                                                        emitPreContractFailure (BuildProvenance.ReleaseError.message (BuildProvenance.FileFailure("hash", manifestPath, error.Message)))
                                                        ""

                                                let revisionText =
                                                    try
                                                        let text = File.ReadAllText manifestPath
                                                        let node = JsonNode.Parse text
                                                        (node.AsObject()["revision"]).GetValue<string>()
                                                    with
                                                    | _ ->
                                                        emitPreContractFailure
                                                            (BuildProvenance.ReleaseError.message
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

                                        // Compose the transport once at the CLI boundary; the same instance drives observe / evaluate / execute in one seam.
                                        let transport = ReleasePublish.defaultHttpTransport ()

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
                                        let! runOutcome =
                                            ReleaseTransaction.run transport authValue useGitHub downloadDir locator dto candidateProjection archivePath

                                        match runOutcome with
                                        | Ok (finalResolution, finalCode) ->
                                            emitResolution finalResolution
                                            return finalCode
                                        | Error error ->
                                            emitResolution (typedFailedWithConfig error)
                                            return 1
    }

let private runOrchestrateMode (argv: string[]) : Async<int> =
    async {
        if argv.Length < 2 then
            eprintfn "usage: ReleaseRunner orchestrate <config-path>"
            return 2
        else
            let configPath = argv.[1]
            let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
            return! ReleaseOrchestration.orchestrate root configPath
    }

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        eprintfn "usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>"
        eprintfn "usage: ReleaseRunner orchestrate <config-path>"
        2
    elif argv.[0] = "orchestrate" then
        // The synchronous wait is at the host boundary; a deeper Async.RunSynchronously would freeze stdout after `emitResolution` and lose the recovered JSON before the host observes process exit.
        runOrchestrateMode argv |> Async.RunSynchronously
    else
        if argv.Length < 3 then
            eprintfn "usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>"
            2
        else
            runTransactionMode argv |> Async.RunSynchronously
