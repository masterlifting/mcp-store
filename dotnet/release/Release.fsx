// role: release/build entrypoint
// Per-component release transaction for the dotnet producer. The
// script is a thin orchestrator: it invokes the existing producer
// scripts (Build.fsx, Pins.fsx, DistributionTests.fsx) and the
// compiled release runner (ReleaseRunner.exe) as separate
// processes, parses the runner's JSON output, and renders the
// per-component summary. All release logic lives in the compiled
// runner; this script exists only to keep the per-component
// transaction isolated from the Provenance.fsx instances of the
// existing scripts and to avoid the analyzer's `#r` rule.
//
// Exit codes:
//
//   0 — `published` or `unchanged`. The upload either completed
//        (production transport with the upload flag) or was not
//        required (the existing archive matches under the strict
//        comparison).
//   1 — `failed`. Validation, build, pin, test, or transport error.
//   2 — `bump-required` or `publish-pending`. The strict comparison
//        said the published archive differs beyond the narrow
//        revision-only exception, or the asset was absent on the
//        remote release and the upload was not authorised. The
//        workflow surfaces this exit code so the per-component
//        outcome is observable without conflating it with a
//        hard transport failure.

#load "Config.fsx"

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let dist = Path.Combine(root, "dotnet", "dist")
let distributionDirectory = Path.Combine(dist, ReleaseConfig.componentId)
let archivePath = Path.Combine(dist, ReleaseConfig.archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let runnerPath = Path.Combine(root, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

// Invoke a producer script in a child `dotnet fsi` process so the
// script's own Provenance.fsx instance stays isolated from the
// compiled runner. The function captures stdout/stderr and surfaces
// non-zero exit codes as a typed failure.
let private runProducerScript (scriptPath: string) : int =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.ArgumentList.Add "fsi"
    info.ArgumentList.Add "--nologo"
    info.ArgumentList.Add scriptPath
    use child = Process.Start info
    child.WaitForExit()
    child.ExitCode

// Build the contract JSON the compiled runner expects. The runner
// uses the same ComponentContract shape as the comparison module;
// encoding it here keeps the script's data surface explicit.
let private buildContractJson () : string =
    let node = JsonObject()
    node["componentId"] <- JsonValue.Create ReleaseConfig.componentId
    node["version"] <- JsonValue.Create ReleaseConfig.version
    node["archiveName"] <- JsonValue.Create ReleaseConfig.archiveName
    let allowlist = JsonArray()
    for f in (ReleaseConfig.publishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort) do
        allowlist.Add(JsonValue.Create f)
    node["entryAllowlist"] <- allowlist
    node.ToJsonString(JsonSerializerOptions())

// Compose the per-component transaction: build, pin, contract
// tests, then the strict comparison through the compiled runner.
// Each phase short-circuits the next on failure so the per-component
// report is precise.
let buildExit = runProducerScript (Path.Combine(root, "dotnet", "release", "Build.fsx"))
if buildExit <> 0 then
    eprintfn "dotnet build phase failed with exit %d" buildExit
    exit buildExit

let pinsExit = runProducerScript (Path.Combine(root, "dotnet", "release", "Pins.fsx"))
if pinsExit <> 0 then
    eprintfn "dotnet pins phase failed with exit %d" pinsExit
    exit pinsExit

let testsExit = runProducerScript (Path.Combine(root, "dotnet", "tests", "DistributionTests.fsx"))
if testsExit <> 0 then
    eprintfn "dotnet distribution tests phase failed with exit %d" testsExit
    exit testsExit

if not (File.Exists runnerPath) then
    eprintfn "compiled runner not found: %s" runnerPath
    eprintfn "build it with: dotnet build infrastructure/release/Release.fsproj -c Release"
    exit 4

let contractJson = buildContractJson ()
let info = ProcessStartInfo(runnerPath)
info.WorkingDirectory <- root
info.UseShellExecute <- false
info.RedirectStandardOutput <- true
info.RedirectStandardError <- true
info.ArgumentList.Add manifestPath
info.ArgumentList.Add archivePath
info.ArgumentList.Add contractJson
let child = Process.Start info
let stdout = child.StandardOutput.ReadToEnd()
let stderr = child.StandardError.ReadToEnd()
child.WaitForExit()
child.Dispose()
if child.ExitCode <> 0 then
    eprintfn "release runner stderr: %s" stderr

if child.ExitCode <> 0 then
    eprintfn "release runner failed with exit %d" child.ExitCode
    exit child.ExitCode

// Parse the runner's JSON output and render the per-component
// summary. The runner emits a single JSON object on stdout.
let resultNode =
    try
        JsonNode.Parse(stdout).AsObject()
    with
    | error ->
        eprintfn "failed to parse runner output: %s" error.Message
        exit 5

let status = resultNode["status"].GetValue<string>()
let reason = resultNode["reason"].GetValue<string>()

let builder = StringBuilder()
builder.AppendLine("## dotnet release") |> ignore
builder.AppendLine($"status={status}") |> ignore
builder.AppendLine($"reason={reason}") |> ignore

// Render every canonical field by name. Failures render a typed
// diagnostic; published/unchanged expose published values; bump-
// required and publish-pending expose only candidate/evaluated so
// the per-component summary does not over-claim.
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
renderOptional "publishedMetadataAssetName" "publishedMetadataAssetName"
renderOptional "publishedMetadataSha256" "publishedMetadataSha256"
renderOptional "producerSdk" "producerSdk"
renderOptional "producerRuntime" "producerRuntime"
renderOptional "producerPlatform" "producerPlatform"

printfn "%s" (builder.ToString())

exit
    (match status with
     | "published" -> 0
     | "unchanged" -> 0
     | "bump-required" -> 2
     | "publish-pending" -> 2
     | _ -> 1)
