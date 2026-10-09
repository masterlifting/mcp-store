// role: release/build entrypoint
// Per-component release transaction for the workflow producer. The
// script is a thin orchestrator: it invokes the existing producer
// scripts (Build.fsx, Pins.fsx, DistributionTests.fsx) and the
// compiled release runner (ReleaseRunner.exe) as separate
// processes, parses the runner's JSON output, and renders the
// per-component summary.
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
let dist = Path.Combine(root, "workflow", "dist")
let distributionDirectory = Path.Combine(dist, ReleaseConfig.componentId)
let archivePath = Path.Combine(dist, ReleaseConfig.archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let runnerPath = Path.Combine(root, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

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

let buildExit = runProducerScript (Path.Combine(root, "workflow", "release", "Build.fsx"))
if buildExit <> 0 then
    eprintfn "workflow build phase failed with exit %d" buildExit
    exit buildExit

let pinsExit = runProducerScript (Path.Combine(root, "workflow", "release", "Pins.fsx"))
if pinsExit <> 0 then
    eprintfn "workflow pins phase failed with exit %d" pinsExit
    exit pinsExit

let testsExit = runProducerScript (Path.Combine(root, "workflow", "tests", "DistributionTests.fsx"))
if testsExit <> 0 then
    eprintfn "workflow distribution tests phase failed with exit %d" testsExit
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
builder.AppendLine("## workflow release") |> ignore
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
