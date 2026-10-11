// role: release/build entrypoint
// Thin per-component orchestrator driving Build.fsx + Pins.fsx + DistributionTests.fsx + ReleaseRunner.exe as child processes.
#load "Config.fsx"

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let dist = Path.Combine(root, "workflow", "dist")
let distributionDirectory = Path.Combine(dist, ReleaseConfig.componentId)
let archivePath = Path.Combine(dist, ReleaseConfig.archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let runnerPath = Path.Combine(root, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

// Child `dotnet fsi` keeps the producer script's own Provenance.fsx instance isolated from the compiled runner.
let private runProducerScript (scriptPath: string) : int * string * string =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.ArgumentList.Add "fsi"
    info.ArgumentList.Add "--nologo"
    info.ArgumentList.Add scriptPath
    use child = Process.Start info
    let stdout = StringBuilder()
    let stderr = StringBuilder()
    child.OutputDataReceived.Add(fun data ->
        if not (isNull data.Data) then stdout.AppendLine data.Data |> ignore)
    child.ErrorDataReceived.Add(fun data ->
        if not (isNull data.Data) then stderr.AppendLine data.Data |> ignore)
    child.BeginOutputReadLine()
    child.BeginErrorReadLine()
    let exited = child.WaitForExit()
    if not exited then
        try child.Kill true with _ -> ()
        child.WaitForExit()
    child.Dispose()
    child.ExitCode, stdout.ToString(), stderr.ToString()

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

// F7 wrapper: emits structured JSON on stdout before exiting so the workflow reads status/reason/config context even when the runner never runs.
let private structuredWrapperFailure
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
    exit childExitCode

// F7: drains stdout/stderr concurrently via .NET events BEFORE waiting so a half-closed pipe cannot deadlock the child.
let private runRunnerProcess (args: string list) : int * string * string =
    let info = ProcessStartInfo(runnerPath)
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true
    for a in args do info.ArgumentList.Add a
    let child = Process.Start info
    let stdout = StringBuilder()
    let stderr = StringBuilder()
    child.OutputDataReceived.Add(fun data ->
        if not (isNull data.Data) then stdout.AppendLine data.Data |> ignore)
    child.ErrorDataReceived.Add(fun data ->
        if not (isNull data.Data) then stderr.AppendLine data.Data |> ignore)
    child.BeginOutputReadLine()
    child.BeginErrorReadLine()
    let exited = child.WaitForExit()
    if not exited then
        try child.Kill true with _ -> ()
        child.WaitForExit()
    child.Dispose()
    child.ExitCode, stdout.ToString(), stderr.ToString()

let buildExit, buildStderr, _ = runProducerScript (Path.Combine(root, "workflow", "release", "Build.fsx"))
if buildExit <> 0 then
    eprintfn "workflow build phase failed with exit %d" buildExit
    structuredWrapperFailure
        "build"
        buildExit
        buildStderr
        ReleaseConfig.componentId
        ReleaseConfig.version
        ReleaseConfig.archiveName
        ReleaseConfig.assetUri

let pinsExit, pinsStderr, _ = runProducerScript (Path.Combine(root, "workflow", "release", "Pins.fsx"))
if pinsExit <> 0 then
    eprintfn "workflow pins phase failed with exit %d" pinsExit
    structuredWrapperFailure
        "pins"
        pinsExit
        pinsStderr
        ReleaseConfig.componentId
        ReleaseConfig.version
        ReleaseConfig.archiveName
        ReleaseConfig.assetUri

let testsExit, testsStderr, _ = runProducerScript (Path.Combine(root, "workflow", "tests", "DistributionTests.fsx"))
if testsExit <> 0 then
    eprintfn "workflow distribution tests phase failed with exit %d" testsExit
    structuredWrapperFailure
        "tests"
        testsExit
        testsStderr
        ReleaseConfig.componentId
        ReleaseConfig.version
        ReleaseConfig.archiveName
        ReleaseConfig.assetUri

if not (File.Exists runnerPath) then
    eprintfn "compiled runner not found: %s" runnerPath
    eprintfn "build it with: dotnet build infrastructure/release/Release.fsproj -c Release"
    exit 4

let contractJson = buildContractJson ()
let exitCode, stdout, stderr = runRunnerProcess [ manifestPath; archivePath; contractJson ]

// F7: parse stdout JSON EVEN on nonzero exit so failure surfaces reason/version/URI/name/revision/hashes.
let resultNode =
    try
        JsonNode.Parse(stdout).AsObject()
    with
    | error ->
        eprintfn "release runner stderr: %s" stderr
        eprintfn "failed to parse runner output: %s" error.Message
        exit 5

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

let builder = StringBuilder()
builder.AppendLine("## workflow release") |> ignore
builder.AppendLine($"status={status}") |> ignore
builder.AppendLine($"reason={reason}") |> ignore

let renderOptional label key =
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

// Public-only status mapping: published|unchanged|failed; the reason field carries the internal detail.
exit
    (match status with
     | "published" -> 0
     | "unchanged" -> 0
     | "failed" when resultNode.ContainsKey "bumpedVersion" -> 2
     | _ -> 1)
