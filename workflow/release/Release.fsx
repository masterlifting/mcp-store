// role: release/build entrypoint
// Thin per-component orchestrator driving Build.fsx + Pins.fsx + DistributionTests.fsx + ReleaseRunner.exe as child processes.
#load "Config.fsx"
#load "../../infrastructure/release/Capture.fsx"

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance
open ReleaseCapture

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let dist = Path.Combine(root, "workflow", "dist")
let distributionDirectory = Path.Combine(dist, ReleaseConfig.componentId)
let archivePath = Path.Combine(dist, ReleaseConfig.archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let runnerPath = Path.Combine(root, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

// Producer child `dotnet fsi` keeps the producer script's own Provenance.fsx instance isolated from the compiled runner.
let private runProducerAsync (scriptPath: string) : Async<int * string> =
    async {
        let! cancellationToken = Async.CancellationToken
        let! result =
            runProcessCapture root "dotnet" [ "fsi"; "--nologo"; scriptPath ] cancellationToken

        return
            match result with
            | Ok(_exit, _stdout, _stderr) -> 0, ""
            | Error(ProcessFailed(_op, code, detail)) -> code, detail
            | Error _ -> 1, ""
    }

let private buildContractJson () : string =
    let node = JsonObject()
    node["componentId"] <- JsonValue.Create ReleaseConfig.componentId
    node["version"] <- JsonValue.Create ReleaseConfig.version
    node["archiveName"] <- JsonValue.Create ReleaseConfig.archiveName
    // F9: the canonical assetUri travels in the contract DTO so the runner never reconstructs it.
    node["assetUri"] <- JsonValue.Create ReleaseConfig.assetUri
    let allowlist = JsonArray()
    for f in (ReleaseConfig.publishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort) do
        allowlist.Add(JsonValue.Create f)
    node["entryAllowlist"] <- allowlist
    node.ToJsonString(JsonSerializerOptions())

// F7 wrapper: emits structured JSON on stdout so the workflow reads status/reason/config context when the runner never runs.
let private emitStructuredFailure
    (phase: string)
    (childExitCode: int)
    (phaseStderr: string)
    (componentId: string)
    (version: string)
    (archiveName: string)
    (assetUri: string)
    : unit =
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
    let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
    let buffer = Encoding.UTF8.GetBytes payload
    try
        use stdout = Console.OpenStandardOutput()
        stdout.Write(buffer, 0, buffer.Length)
        stdout.Write([| byte '\n' |], 0, 1)
        stdout.Flush()
    with
    | _ -> ()

// F7: drains stdout/stderr concurrently via async reads; the runner's JSON payload returns on stdout even when the runner fails.
let private runRunnerAsync (args: string list) : Async<int * string * string> =
    async {
        let! cancellationToken = Async.CancellationToken
        let! result = runProcessCapture root runnerPath args cancellationToken

        match result with
        | Ok(exit, stdout, stderr) -> return exit, stdout, stderr
        | Error _ -> return 1, "", ""
    }

let private buildSummary (status: string) (reason: string) (resultNode: JsonNode) : unit =
    let builder = StringBuilder()
    builder.AppendLine("## workflow release") |> ignore
    builder.AppendLine($"status={status}") |> ignore
    builder.AppendLine($"reason={reason}") |> ignore

    let renderOptional (label: string) (key: string) =
        match resultNode[key] with
        | null -> ()
        | node -> builder.AppendLine($"{label}={node.GetValue<string>()}") |> ignore

    renderOptional "version" "version"
    renderOptional "archiveName" "archiveName"
    renderOptional "assetUri" "assetUri"
    renderOptional "candidateArchiveSha256" "candidateArchiveSha256"
    renderOptional "candidateManifestSha256" "candidateManifestSha256"
    renderOptional "candidateRevision" "candidateRevision"
    renderOptional "currentEvaluatedHead" "currentEvaluatedHead"
    renderOptional "metadataAssetName" "metadataAssetName"
    renderOptional "metadataLocalPath" "metadataLocalPath"
    renderOptional "publishedArchiveSha256" "publishedArchiveSha256"
    renderOptional "publishedManifestSha256" "publishedManifestSha256"
    renderOptional "publishedRevision" "publishedRevision"
    renderOptional "publishedMetadataSha256" "publishedMetadataSha256"
    renderOptional "producerSdk" "producerSdk"
    renderOptional "producerRuntime" "producerRuntime"
    renderOptional "producerPlatform" "producerPlatform"

    printfn "%s" (builder.ToString())

let private runWrapper () : Async<int> =
    async {
        let! buildExit, buildStderr = runProducerAsync (Path.Combine(root, "workflow", "release", "Build.fsx"))

        if buildExit <> 0 then
            eprintfn "workflow build phase failed with exit %d" buildExit
            emitStructuredFailure
                "build"
                buildExit
                buildStderr
                ReleaseConfig.componentId
                ReleaseConfig.version
                ReleaseConfig.archiveName
                ReleaseConfig.assetUri
            return buildExit
        else
            let! pinsExit, pinsStderr = runProducerAsync (Path.Combine(root, "workflow", "release", "Pins.fsx"))

            if pinsExit <> 0 then
                eprintfn "workflow pins phase failed with exit %d" pinsExit
                emitStructuredFailure
                    "pins"
                    pinsExit
                    pinsStderr
                    ReleaseConfig.componentId
                    ReleaseConfig.version
                    ReleaseConfig.archiveName
                    ReleaseConfig.assetUri
                return pinsExit
            else
                let! testsExit, testsStderr =
                    runProducerAsync (Path.Combine(root, "workflow", "tests", "DistributionTests.fsx"))

                if testsExit <> 0 then
                    eprintfn "workflow distribution tests phase failed with exit %d" testsExit
                    emitStructuredFailure
                        "tests"
                        testsExit
                        testsStderr
                        ReleaseConfig.componentId
                        ReleaseConfig.version
                        ReleaseConfig.archiveName
                        ReleaseConfig.assetUri
                    return testsExit
                else
                    if not (File.Exists runnerPath) then
                        eprintfn "compiled runner not found: %s" runnerPath
                        eprintfn "build it with: dotnet build infrastructure/release/Release.fsproj -c Release"
                        return 4
                    else
                        let contractJson = buildContractJson ()
                        let! exitCode, stdout, stderr = runRunnerAsync [ manifestPath; archivePath; contractJson ]

                        // F7: parse stdout JSON EVEN on nonzero exit so failure surfaces reason/version/URI/name/revision/hashes.
                        let! parsedResultNode =
                            async {
                                try
                                    return Ok(JsonNode.Parse(stdout).AsObject())
                                with error ->
                                    eprintfn "release runner stderr: %s" stderr
                                    eprintfn "failed to parse runner output: %s" error.Message
                                    return Error 5
                            }

                        match parsedResultNode with
                        | Error code -> return code
                        | Ok resultNode ->
                            let status =
                                match resultNode["status"] with
                                | null -> "failed"
                                | node ->
                                    try node.GetValue<string>()
                                    with _ -> "failed"

                            let reason =
                                match resultNode["reason"] with
                                | null -> "the runner did not surface a reason"
                                | node ->
                                    try node.GetValue<string>()
                                    with _ -> "the runner surfaced a non-string reason"

                            buildSummary status reason resultNode

                            return
                                (match status with
                                 // Public-only status mapping: published|unchanged|failed; the reason field carries the internal detail.
                                 | "published" -> 0
                                 | "unchanged" -> 0
                                 | "failed" when resultNode.ContainsKey "bumpedVersion" -> 2
                                 | _ -> exitCode)
    }

// Standalone entry bridge: fsi can only drive a top-level async pipeline
// through one synchronous wait at the host boundary; deeper awaiters
// would leave stdout un-flushed before the host observes process exit.
let exitCode = runWrapper () |> Async.RunSynchronously
exit exitCode
