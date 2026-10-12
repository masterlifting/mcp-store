// role: reusable module/helper
// Shared per-component orchestration: one compiled path sequences child build, child pins, child distribution tests, and the compiled transaction.
// The canonical producer Config.fsx is the sole policy authority; this module never mirrors versions, toolchains, allowlists or URIs.
// The orchestrator always emits one structured JSON object on stdout before exit.
module ReleaseOrchestration

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open BuildProvenance

// Projection of the canonical `module ReleaseConfig` after isolated-FSI evaluation.
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

// Path of the canonical runner binary produced by the same Release.fsproj.
let runnerPath (root: string) : string =
    Path.Combine(root, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

let private runChild (root: string) (executable: string) (args: string list) : Async<Result<int * string * string, ReleaseError>> =
    async {
        let! token = Async.CancellationToken
        return! ReleaseCapture.runProcessCapture root executable args token
    }

// Writes a temp FSI wrapper that #load's the canonical Config.fsx and prints one KEY=VALUE
// per field. The wrapper lives in a fresh temp directory so concurrent runs cannot race.
let private writeConfigWrapper (root: string) (configPath: string) : string =
    let tempDir = Path.Combine(Path.GetTempPath(), $"mcp-orchestration-{Guid.NewGuid():N}")
    Directory.CreateDirectory tempDir |> ignore
    let wrapperPath = Path.Combine(tempDir, "Wrapper.fsx")
    let absoluteConfig = Path.GetFullPath(Path.Combine(root, configPath))
    let content =
        sprintf "#load @\"%s\"\n" absoluteConfig
        + "printfn \"COMPONENT_ID=%s\" ReleaseConfig.componentId\n"
        + "printfn \"VERSION=%s\" ReleaseConfig.version\n"
        + "printfn \"ENTRY_DLL=%s\" ReleaseConfig.entryDll\n"
        + "printfn \"SDK=%s\" ReleaseConfig.sdkVersion\n"
        + "printfn \"RUNTIME=%s\" ReleaseConfig.runtimeVersion\n"
        + "printfn \"PLATFORM=%s\" ReleaseConfig.platform\n"
        + "printfn \"ARCHIVE=%s\" ReleaseConfig.archiveName\n"
        + "printfn \"ASSET_URI=%s\" ReleaseConfig.assetUri\n"
        + "printfn \"PUBLISHED_FILES=%s\" (String.concat \",\" ReleaseConfig.publishedFiles)\n"
    File.WriteAllText(wrapperPath, content)
    wrapperPath

let private cleanupWrapper (wrapperPath: string) =
    let dir = Path.GetDirectoryName wrapperPath
    if not (String.IsNullOrEmpty dir) && Directory.Exists dir then
        try Directory.Delete(dir, true) with _ -> ()

let private loadProducerConfig (root: string) (configPath: string) : Async<Result<ProducerProjection, ReleaseError>> =
    async {
        let wrapperPath = writeConfigWrapper root configPath

        try
            let! result = runChild root "dotnet" [ "fsi"; "--nologo"; wrapperPath ]

            match result with
            | Error error -> return Error error
            | Ok (_, stdout, _) ->
                let pairs =
                    stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.choose (fun line ->
                        let idx = line.IndexOf('=')
                        if idx <= 0 then None
                        else Some(line.Substring(0, idx), line.Substring(idx + 1)))
                    |> Map.ofArray

                let require (name: string) : Result<string, ReleaseError> =
                    match Map.tryFind name pairs with
                    | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
                    | _ ->
                        Error(
                            MalformedArtifact(
                                configPath,
                                $"orchestration could not read producer field '{name}' from Config.fsx output; observed:\n{stdout}"))

                match require "COMPONENT_ID", require "VERSION", require "ENTRY_DLL", require "SDK", require "RUNTIME", require "PLATFORM", require "ARCHIVE", require "ASSET_URI", require "PUBLISHED_FILES" with
                | Error error, _, _, _, _, _, _, _, _ -> return Error error
                | _, Error error, _, _, _, _, _, _, _ -> return Error error
                | _, _, Error error, _, _, _, _, _, _ -> return Error error
                | _, _, _, Error error, _, _, _, _, _ -> return Error error
                | _, _, _, _, Error error, _, _, _, _ -> return Error error
                | _, _, _, _, _, Error error, _, _, _ -> return Error error
                | _, _, _, _, _, _, Error error, _, _ -> return Error error
                | _, _, _, _, _, _, _, Error error, _ -> return Error error
                | _, _, _, _, _, _, _, _, Error error -> return Error error
                | Ok id, Ok version, Ok entryDll, Ok sdk, Ok runtime, Ok platform, Ok archive, Ok assetUri, Ok published ->
                    let files =
                        published.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.toList
                    return
                        Ok
                            { ComponentId = id
                              Version = version
                              EntryDll = entryDll
                              SdkVersion = sdk
                              RuntimeVersion = runtime
                              Platform = platform
                              ArchiveName = archive
                              AssetUri = assetUri
                              PublishedFiles = files }
        finally
            cleanupWrapper wrapperPath
    }

let private emitStructuredFailure
    (phase: string)
    (childExitCode: int)
    (phaseStderr: string)
    (componentId: string)
    (version: string)
    (archiveName: string)
    (assetUri: string)
    : string =
    let node = JsonObject()
    node["status"] <- JsonValue.Create "failed"
    node["reason"] <- JsonValue.Create $"the {phase} phase exited with code {childExitCode}; the runner was not invoked"
    node["phase"] <- JsonValue.Create phase
    node["childExitCode"] <- JsonValue.Create childExitCode
    node["childStderr"] <- JsonValue.Create(phaseStderr.TrimEnd())
    node["componentId"] <- JsonValue.Create componentId
    node["version"] <- JsonValue.Create version
    node["archiveName"] <- JsonValue.Create archiveName
    node["assetUri"] <- JsonValue.Create assetUri
    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

// Renders a Resolution as one structured JSON object on stdout. The runner contract is:
// always emit one JSON object, even on the failure path, with status/reason plus the
// config context (componentId/version/archiveName/assetUri) when the contract has been parsed.
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

// Runs one .fsx child with the canonical config; classifies a non-zero exit as a typed
// `ProcessFailed` so structured phase failures are observable.
let private runProducerScript (root: string) (scriptPath: string) (configPath: string) : Async<Result<unit, ReleaseError>> =
    async {
        let absoluteScript = Path.GetFullPath(Path.Combine(root, scriptPath))
        let absoluteConfig = Path.GetFullPath(Path.Combine(root, configPath))

        if not (File.Exists absoluteScript) then
            return Error(MissingArtifact absoluteScript)
        else
            let! result = runChild root "dotnet" [ "fsi"; "--nologo"; absoluteScript ]

            match result with
            | Error error -> return Error error
            | Ok (0, _, _) -> return Ok()
            | Ok (exitCode, _stdout, stderr) ->
                return Error(ProcessFailed(absoluteScript, exitCode, stderr.Trim()))
    }

// Builds the contract DTO from the projected canonical config plus the deterministic
// per-component allowlist (`publishedFiles @ [NOTICE.txt; distribution.json]`, sorted).
let private buildContractJson (projection: ProducerProjection) (canonicalAssetUri: string) : string =
    let node = JsonObject()
    node["componentId"] <- JsonValue.Create projection.ComponentId
    node["version"] <- JsonValue.Create projection.Version
    node["archiveName"] <- JsonValue.Create projection.ArchiveName
    // The canonical assetUri travels in the contract DTO; the runner never reconstructs it.
    node["assetUri"] <- JsonValue.Create canonicalAssetUri
    let allowlist = JsonArray()
    for f in (projection.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort) do
        allowlist.Add(JsonValue.Create f)
    node["entryAllowlist"] <- allowlist
    node.ToJsonString(JsonSerializerOptions())

// Run the compiled transaction as a child process so a failure in HTTP transport does
// not take the orchestrator down. The contract DTO, manifest, and archive are passed as
// CLI args; the runner emits its own JSON payload.
let private runTransaction (root: string) (projection: ProducerProjection) (manifestPath: string) (archivePath: string) (contractJson: string) : Async<Result<string * int, ReleaseError>> =
    async {
        let path = runnerPath root

        if not (File.Exists path) then
            return
                Error(
                    FileFailure(
                        "runner",
                        path,
                        "the compiled runner is missing; build infrastructure/release/Release.fsproj -c Release"))
        else
            let! result = runChild root path [ manifestPath; archivePath; contractJson ]

            match result with
            | Error error -> return Error error
            | Ok (exitCode, stdout, _stderr) -> return Ok(stdout, exitCode)
    }

// Shared per-component orchestration. Reads the canonical Config.fsx via isolated FSI,
// runs child build/pins/tests, then the compiled transaction. Emits one structured JSON
// object on stdout; returns the exit code.
let orchestrate (root: string) (configPath: string) : Async<int> =
    async {
        let! rosterResult = ReleaseEnrollment.loadRoster root

        match rosterResult with
        | Error error ->
            let node = JsonObject()
            node["status"] <- JsonValue.Create "failed"
            node["reason"] <- JsonValue.Create(ReleaseError.message error)
            node["phase"] <- JsonValue.Create "config"
            let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
            do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
            return 4
        | Ok roster ->
            match ReleaseEnrollment.validateRoster roster with
            | Error error ->
                let node = JsonObject()
                node["status"] <- JsonValue.Create "failed"
                node["reason"] <- JsonValue.Create(ReleaseError.message error)
                node["phase"] <- JsonValue.Create "config"
                let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
                return 4
            | Ok () ->
                let! projectionResult = loadProducerConfig root configPath

                match projectionResult with
                | Error error ->
                    // The config never loaded; emit a structured failure without config context.
                    let node = JsonObject()
                    node["status"] <- JsonValue.Create "failed"
                    node["reason"] <- JsonValue.Create(ReleaseError.message error)
                    node["phase"] <- JsonValue.Create "config"
                    let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                    do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
                    return 4
                | Ok projection ->
                    match ReleaseEnrollment.validateProjectionAgainstRoster projection.ComponentId configPath root roster with
                    | Error error ->
                        // The projection is not enrolled or the config path drifted from the roster; refuse to proceed.
                        let node = JsonObject()
                        node["status"] <- JsonValue.Create "failed"
                        node["reason"] <- JsonValue.Create(ReleaseError.message error)
                        node["phase"] <- JsonValue.Create "config"
                        node["componentId"] <- JsonValue.Create projection.ComponentId
                        let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                        do! Console.Out.WriteLineAsync(payload) |> Async.AwaitTask
                        return 4
                    | Ok () ->
                        let componentId = projection.ComponentId
                        let dist = Path.Combine(root, componentId, "dist")
                        let distributionDirectory = Path.Combine(dist, componentId)
                        let archivePath = Path.Combine(dist, projection.ArchiveName)
                        let manifestPath = Path.Combine(distributionDirectory, "distribution.json")

                        let! buildResult = runProducerScript root (Path.Combine(componentId, "release", "Build.fsx")) configPath

                        match buildResult with
                        | Error (ProcessFailed (_, exitCode, stderr)) ->
                            do! Console.Out.WriteLineAsync
                                    (emitStructuredFailure "build" exitCode stderr projection.ComponentId projection.Version projection.ArchiveName projection.AssetUri)
                                |> Async.AwaitTask
                            return exitCode
                        | Error error ->
                            let payload =
                                emitStructuredFailure "build" 1 (ReleaseError.message error) projection.ComponentId projection.Version projection.ArchiveName projection.AssetUri
                            do! Console.Out.WriteLineAsync payload |> Async.AwaitTask
                            return 1
                        | Ok () ->
                            let! pinsResult = runProducerScript root (Path.Combine(componentId, "release", "Pins.fsx")) configPath

                            match pinsResult with
                            | Error (ProcessFailed (_, exitCode, stderr)) ->
                                do! Console.Out.WriteLineAsync
                                        (emitStructuredFailure "pins" exitCode stderr projection.ComponentId projection.Version projection.ArchiveName projection.AssetUri)
                                    |> Async.AwaitTask
                                return exitCode
                            | Error error ->
                                let payload =
                                    emitStructuredFailure "pins" 1 (ReleaseError.message error) projection.ComponentId projection.Version projection.ArchiveName projection.AssetUri
                                do! Console.Out.WriteLineAsync payload |> Async.AwaitTask
                                return 1
                            | Ok () ->
                                let! testsResult = runProducerScript root (Path.Combine(componentId, "tests", "DistributionTests.fsx")) configPath

                                match testsResult with
                                | Error (ProcessFailed (_, exitCode, stderr)) ->
                                    do! Console.Out.WriteLineAsync
                                            (emitStructuredFailure "tests" exitCode stderr projection.ComponentId projection.Version projection.ArchiveName projection.AssetUri)
                                        |> Async.AwaitTask
                                    return exitCode
                                | Error error ->
                                    let payload =
                                        emitStructuredFailure "tests" 1 (ReleaseError.message error) projection.ComponentId projection.Version projection.ArchiveName projection.AssetUri
                                    do! Console.Out.WriteLineAsync payload |> Async.AwaitTask
                                    return 1
                                | Ok () ->
                                    let contractJson = buildContractJson projection projection.AssetUri
                                    let! transactionResult = runTransaction root projection manifestPath archivePath contractJson

                                    match transactionResult with
                                    | Error error ->
                                        let payload =
                                            emitStructuredFailure "transaction" 1 (ReleaseError.message error) projection.ComponentId projection.Version projection.ArchiveName projection.AssetUri
                                        do! Console.Out.WriteLineAsync payload |> Async.AwaitTask
                                        return 1
                                    | Ok (stdout, exitCode) ->
                                        // The compiled runner already emitted the structured JSON payload.
                                        do! Console.Out.WriteLineAsync(stdout.TrimEnd()) |> Async.AwaitTask
                                        return exitCode
    }
