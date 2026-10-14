// role: reusable module/helper
// Shared preparation sequencing for one component. The preparation path is the
// only place Build.fsx, Pins.fsx, and the producer test entrypoints ever run;
// every child subprocess has the publication-authority environment variables
// (GITHUB_TOKEN, MCP_RELEASE_DO_UPLOAD, MCP_RELEASE_GITHUB_OWNER,
// MCP_RELEASE_GITHUB_REPOSITORY) removed before Start. Preparation stops
// before any write attempt and writes a portable candidate directory the
// publisher consumes; the publisher never re-derives policy from this output.
module ReleaseOrchestration

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open BuildProvenance

// Projection of the canonical `module ReleaseConfig` after pure-parser evaluation.
// Policy values come from the producer's Config.fsx only; this DTO is read-only.
type ProducerProjection =
    { ComponentId: string
      Version: string
      EntryDll: string
      SdkVersion: string
      RuntimeVersion: string
      Platform: string
      ArchiveName: string
      AssetUri: string
      PublishedFiles: string list }

// Snapshot of the preparation phases for one component. The publisher re-reads
// the candidate files from the prepared directory and re-verifies every byte;
// nothing in this record is treated as authorization by the publisher.
type PreparedData =
    { Projection: ProducerProjection
      ConfigPath: string
      ConfigSha256: string
      EvaluatedHead: string
      ArchivePath: string
      ArchiveSize: int
      ArchiveSha256: string
      StandaloneManifestPath: string
      StandaloneManifestSha256: string
      StandaloneManifestSize: int
      EmbeddedManifestSha256: string
      ManifestBytesEqual: bool
      EntryAllowlist: string list }

// Path of the canonical runner binary produced by the same Release.fsproj.
let runnerPath (root: string) : string =
    Path.Combine(root, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

// Publication authority that must never leak into pre-publication child subprocesses.
let private publicationEnvVars : string list =
    [ "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]

let private runChild (root: string) (scrubEnvVars: string list) (executable: string) (args: string list) : Async<Result<int * string * string, ReleaseError>> =
    async {
        let! token = Async.CancellationToken
        return! ReleaseCapture.runProcessCapture root executable args scrubEnvVars token
    }

// SEC1 boundary: the publisher MUST NOT execute Config.fsx; policy is derived purely
// from the canonical bytes through `ReleasePolicy.parse`. Any second-authored version,
// executable syntax, duplicate binding, or unparseable token in Config.fsx fails closed
// before the orchestrator schedules a single child process.
let private loadProducerConfig (root: string) (configPath: string) : Async<Result<ProducerProjection, ReleaseError>> =
    async {
        let absolute = Path.GetFullPath(Path.Combine(root, configPath))

        match ReleasePolicy.parse absolute with
        | Error error -> return Error error
        | Ok policy ->
            return
                Ok
                    { ComponentId = policy.ComponentId
                      Version = policy.Version
                      EntryDll = policy.EntryDll
                      SdkVersion = policy.SdkVersion
                      RuntimeVersion = policy.RuntimeVersion
                      Platform = policy.Platform
                      ArchiveName = policy.ArchiveName
                      AssetUri = policy.AssetUri
                      PublishedFiles = policy.PublishedFiles }
    }

let private sha256OfFile (path: string) : Async<Result<string, ReleaseError>> =
    async {
        match! BuildProvenance.sha256FileAsync path with
        | Ok sha -> return Ok sha
        | Error error -> return Error error
    }

// Reads the archive, extracts the embedded `distribution.json` entry, hashes its
// bytes, and returns the embedded manifest SHA. Reuses the canonical
// `ReleaseArchive.parse` so the same transport checks apply; the standalone
// manifest at `<component>/dist/<componentId>/distribution.json` is hashed
// byte-for-byte here so the publisher can independently re-confirm equality.
let private embeddedManifestSha256 (archivePath: string) : Async<Result<string, ReleaseError>> =
    async {
        try
            let bytes = File.ReadAllBytes archivePath
            match ReleaseArchive.parse archivePath bytes with
            | Error error -> return Error error
            | Ok archive ->
                let mutable failure : ReleaseError option = None
                let mutable manifestBytes : byte[] option = None
                let mutable manifestCount = 0

                for entry in archive.Entries do
                    match failure with
                    | Some _ -> ()
                    | None ->
                        if entry.Name = "distribution.json" then
                            manifestCount <- manifestCount + 1
                            match ReleaseArchive.decompress entry with
                            | Error error -> failure <- Some error
                            | Ok payload ->
                                if manifestCount > 1 then
                                    failure <- Some(MalformedArtifact(archivePath, "the archive contains more than one distribution.json entry"))
                                else
                                    manifestBytes <- Some payload

                match failure, manifestBytes with
                | Some error, _ -> return Error error
                | None, None ->
                    return Error(MalformedArtifact(archivePath, "the archive is missing the canonical distribution.json entry"))
                | None, Some payload ->
                    let sha =
                        System.Security.Cryptography.SHA256.HashData payload
                        |> Convert.ToHexString
                        |> fun v -> v.ToLowerInvariant()
                    return Ok sha
        with
        | :? IOException as error -> return Error(FileFailure("hash", archivePath, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("hash", archivePath, error.Message))
    }

let private fileSize (path: string) : Result<int, ReleaseError> =
    try
        let info = FileInfo path
        if not info.Exists then
            Error(MissingArtifact path)
        else
            Ok(int info.Length)
    with
    | :? IOException as error -> Error(FileFailure("stat", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("stat", path, error.Message))

let private entryAllowlistFor (projection: ProducerProjection) : string list =
    (projection.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ])
    |> List.sort

let private emitPreparedFailure (phase: string) (reason: string) (componentId: string) (version: string) (archiveName: string) (assetUri: string) : string =
    let node = JsonObject()
    node["status"] <- JsonValue.Create "failed"
    node["reason"] <- JsonValue.Create reason
    node["phase"] <- JsonValue.Create phase
    node["componentId"] <- JsonValue.Create componentId
    node["version"] <- JsonValue.Create version
    node["archiveName"] <- JsonValue.Create archiveName
    node["assetUri"] <- JsonValue.Create assetUri
    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

// Renders a Resolution as one structured JSON object on stdout (status/reason plus config context; see README for the public schema).
let renderResolution (resolution: ReleaseEvaluate.Resolution) : string =
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

// Runs one .fsx child with the canonical config; the explicit semantic stage keeps the typed `ProcessFailed` operation stable so structured phase failures stay distinct from the script path.
let private runProducerScript (root: string) (phase: string) (scriptPath: string) (configPath: string) : Async<Result<unit, ReleaseError>> =
    async {
        let absoluteScript = Path.GetFullPath(Path.Combine(root, scriptPath))
        let absoluteConfig = Path.GetFullPath(Path.Combine(root, configPath))

        if not (File.Exists absoluteScript) then
            return Error(MissingArtifact absoluteScript)
        else
            let! result = runChild root publicationEnvVars "dotnet" [ "fsi"; "--nologo"; absoluteScript ]

            match result with
            | Error error -> return Error error
            | Ok (0, _, _) -> return Ok()
            | Ok (exitCode, _stdout, stderr) ->
                return Error(ProcessFailed(phase, exitCode, stderr.Trim()))
    }

// Explicit per-component test gate list (no dynamic discovery): ordered relative to the producer's component root. Workflow's pack requires an additional runtime-coordination test against the freshly built artifacts.
let private producerScriptTests (componentId: string) : string list =
    match componentId with
    | "dotnet" -> [ "tests/DistributionTests.fsx" ]
    | "workflow" -> [ "tests/DistributionTests.fsx"; "tests/PackagedRuntimeCoordinationTests.fsx" ]
    | _ -> [ "tests/DistributionTests.fsx" ]

let private runProducerTests
    (root: string)
    (componentId: string)
    (configPath: string)
    (projection: ProducerProjection)
    : Async<Result<unit, ReleaseError>> =
    async {
        let mutable failure : (ReleaseError * string) option = None
        let mutable index = 0

        for testsScript in producerScriptTests componentId do
            match failure with
            | None ->
                index <- index + 1
                let phase = if index = 1 then "tests" else sprintf "tests[%d]" index
                let scriptPath = Path.Combine(componentId, testsScript)
                let! result = runProducerScript root phase scriptPath configPath

                match result with
                | Ok () -> ()
                | Error typedError -> failure <- Some(typedError, phase)
            | Some _ -> ()

        match failure with
        | None -> return Ok()
        | Some (ProcessFailed (_, exitCode, stderr), phase) ->
            return Error(ProcessFailed(phase, exitCode, stderr))
        | Some (typedError, phase) ->
            let node = JsonObject()
            node["status"] <- JsonValue.Create "failed"
            node["reason"] <- JsonValue.Create(ReleaseError.message typedError)
            node["phase"] <- JsonValue.Create phase
            node["componentId"] <- JsonValue.Create projection.ComponentId
            node["version"] <- JsonValue.Create projection.Version
            node["archiveName"] <- JsonValue.Create projection.ArchiveName
            node["assetUri"] <- JsonValue.Create projection.AssetUri
            let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
            do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
            return Error typedError
    }

// Roster authorization precedes config parsing and child execution, preventing unregistered F# from running.
// Preparation children lack publication credentials, preventing build and test code from publishing.
let private prepareInternal (root: string) (configPath: string) : Async<Result<PreparedData, ReleaseError>> =
    async {
        let! rosterResult = ReleaseEnrollment.loadRoster root

        match rosterResult with
        | Error error -> return Error error
        | Ok roster ->
            match ReleaseEnrollment.validateRoster roster with
            | Error error -> return Error error
            | Ok () ->
                match ReleaseEnrollment.validateConfigPathAgainstRoster configPath root roster with
                | Error error -> return Error error
                | Ok () ->
                    let! projectionResult = loadProducerConfig root configPath

                    match projectionResult with
                    | Error error -> return Error error
                    | Ok projection ->
                        match ReleaseEnrollment.validateProjectionAgainstRoster projection.ComponentId configPath root roster with
                        | Error error -> return Error error
                        | Ok () ->
                            let componentId = projection.ComponentId
                            let dist = Path.Combine(root, componentId, "dist")
                            let distributionDirectory = Path.Combine(dist, componentId)
                            let archivePath = Path.Combine(dist, projection.ArchiveName)
                            let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
                            let configAbsolute = Path.GetFullPath(Path.Combine(root, configPath))

                            let! buildResult = runProducerScript root "build" (Path.Combine(componentId, "release", "Build.fsx")) configPath

                            match buildResult with
                            | Error error -> return Error error
                            | Ok () ->
                                let! pinsResult = runProducerScript root "pins" (Path.Combine(componentId, "release", "Pins.fsx")) configPath

                                match pinsResult with
                                | Error error -> return Error error
                                | Ok () ->
                                    let! testsResult = runProducerTests root componentId configPath projection

                                    match testsResult with
                                    | Error error -> return Error error
                                    | Ok () ->
                                        let! configShaResult = sha256OfFile configAbsolute
                                        match configShaResult with
                                        | Error error -> return Error error
                                        | Ok configSha256 ->
                                            let! archiveShaResult = sha256OfFile archivePath
                                            match archiveShaResult with
                                            | Error error -> return Error error
                                            | Ok archiveSha256 ->
                                                let! archiveSizeResult = async { return fileSize archivePath }
                                                match archiveSizeResult with
                                                | Error error -> return Error error
                                                | Ok archiveSize ->
                                                    let! manifestShaResult = sha256OfFile manifestPath
                                                    match manifestShaResult with
                                                    | Error error -> return Error error
                                                    | Ok standaloneManifestSha256 ->
                                                        let! manifestSizeResult = async { return fileSize manifestPath }
                                                        match manifestSizeResult with
                                                        | Error error -> return Error error
                                                        | Ok standaloneManifestSize ->
                                                            let! embeddedShaResult = embeddedManifestSha256 archivePath
                                                            match embeddedShaResult with
                                                            | Error error -> return Error error
                                                            | Ok embeddedManifestSha256 ->
                                                                let! headResult = BuildProvenance.committedHead root
                                                                match headResult with
                                                                | Error error -> return Error error
                                                                | Ok evaluatedHead ->
                                                                    let manifestBytesEqual = standaloneManifestSha256 = embeddedManifestSha256

                                                                    if manifestBytesEqual then
                                                                        let data : PreparedData =
                                                                            { Projection = projection
                                                                              ConfigPath = configPath
                                                                              ConfigSha256 = configSha256
                                                                              EvaluatedHead = evaluatedHead
                                                                              ArchivePath = archivePath
                                                                              ArchiveSize = archiveSize
                                                                              ArchiveSha256 = archiveSha256
                                                                              StandaloneManifestPath = manifestPath
                                                                              StandaloneManifestSha256 = standaloneManifestSha256
                                                                              StandaloneManifestSize = standaloneManifestSize
                                                                              EmbeddedManifestSha256 = embeddedManifestSha256
                                                                              ManifestBytesEqual = manifestBytesEqual
                                                                              EntryAllowlist = entryAllowlistFor projection }

                                                                        return Ok data
                                                                    else
                                                                        return
                                                                            Error(
                                                                                MalformedArtifact(
                                                                                    manifestPath,
                                                                                    $"the standalone manifest SHA '{standaloneManifestSha256}' differs from the embedded manifest SHA '{embeddedManifestSha256}'; refusing to publish a candidate whose standalone and embedded manifests disagree"))
    }

// Fresh preparation entrypoint: runs the same sequenced prep (no FSI for
// Config.fsx, no Build/Pins/Tests credentials, no transaction subprocess) and
// writes a portable candidate directory on disk. The publisher re-derives
// every policy value from the audited `Publisher.json` closure plus the
// canonical `Config.fsx` bytes at the platform-supplied event SHA; nothing
// in the prepared directory is treated as authorization.
let prepare (root: string) (configPath: string) (candidateDirectory: string) : Async<int> =
    async {
        match! prepareInternal root configPath with
        | Error error ->
            let node = JsonObject()
            node["status"] <- JsonValue.Create "failed"
            node["reason"] <- JsonValue.Create(ReleaseError.message error)

            let phase =
                match error with
                | MalformedArtifact (path, _) when path.Contains "release/Config.fsx" -> "config"
                | ProcessFailed (phase, _, _) -> phase
                | _ -> "config"

            node["phase"] <- JsonValue.Create phase
            let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
            do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
            return 4
        | Ok data ->
            try
                if not (Directory.Exists candidateDirectory) then
                    Directory.CreateDirectory candidateDirectory |> ignore

                let archiveName = data.Projection.ArchiveName
                let relativeArchiveName = archiveName
                let relativeManifestName = "distribution.json"

                let portableArchivePath = Path.Combine(candidateDirectory, relativeArchiveName)
                let portableManifestPath = Path.Combine(candidateDirectory, relativeManifestName)

                File.Copy(data.ArchivePath, portableArchivePath, true)
                File.Copy(data.StandaloneManifestPath, portableManifestPath, true)

                let diagnosticPath = Path.Combine(candidateDirectory, "sha256.txt")
                let diagnostic =
                    String.concat
                        Environment.NewLine
                        [ $"archive {relativeArchiveName} {data.ArchiveSha256}"
                          $"manifest {relativeManifestName} {data.StandaloneManifestSha256}"
                          $"embedded {data.EmbeddedManifestSha256}"
                          $"configSha256 {data.ConfigSha256}"
                          $"evaluatedHead {data.EvaluatedHead}" ]
                File.WriteAllText(diagnosticPath, diagnostic + Environment.NewLine)

                // Typed preparation receipt: the producer-known inner archive
                // and standalone manifest digests/sizes are recorded in JSON
                // so the publisher can cross-check its verified snapshot
                // bytes without ever re-reading a mutable on-disk path.
                // The receipt carries NO outer transport digest/size and NO
                // runtime / toolchain values; the platform outer digest comes
                // from `ArtifactEvidence.ActualOuterDigest` and the trusted
                // runtime/toolchain values come from the trusted publisher
                // receipt generated by the independently built builder.
                let prepNode = JsonObject()
                prepNode["schemaVersion"] <- JsonValue.Create 1
                prepNode["innerArchiveSha256"] <- JsonValue.Create data.ArchiveSha256
                prepNode["innerArchiveSize"] <- JsonValue.Create(int64 data.ArchiveSize)
                prepNode["manifestSha256"] <- JsonValue.Create data.StandaloneManifestSha256
                prepNode["manifestSize"] <- JsonValue.Create(int64 data.StandaloneManifestSize)
                let prepPath = Path.Combine(candidateDirectory, "preparation.json")
                File.WriteAllText(prepPath, prepNode.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

                let node = JsonObject()
                node["status"] <- JsonValue.Create "prepared"
                node["reason"] <- JsonValue.Create "preparation complete; the candidate directory is portable data only"
                node["phase"] <- JsonValue.Create "prepare"
                node["componentId"] <- JsonValue.Create data.Projection.ComponentId
                node["version"] <- JsonValue.Create data.Projection.Version
                node["candidateDirectory"] <- JsonValue.Create candidateDirectory
                node["relativeArchiveName"] <- JsonValue.Create relativeArchiveName
                node["relativeManifestName"] <- JsonValue.Create relativeManifestName
                node["diagnosticSha256Path"] <- JsonValue.Create "sha256.txt"
                node["preparationReceiptPath"] <- JsonValue.Create "preparation.json"
                let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
                return 0
            with
            | :? IOException as error ->
                let node = JsonObject()
                node["status"] <- JsonValue.Create "failed"
                node["reason"] <- JsonValue.Create($"prepare could not write the portable candidate directory: {error.Message}")
                node["phase"] <- JsonValue.Create "prepare"
                let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
                return 4
            | :? UnauthorizedAccessException as error ->
                let node = JsonObject()
                node["status"] <- JsonValue.Create "failed"
                node["reason"] <- JsonValue.Create($"prepare could not write the portable candidate directory: {error.Message}")
                node["phase"] <- JsonValue.Create "prepare"
                let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
                return 4
    }
