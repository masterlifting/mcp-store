// Deterministic contract coverage for the component-local Task Runtime
// producer release. Run after PrepareReleasePins.fsx; regeneration is tested
// against stored release artifacts before the final contract checks.

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json.Nodes

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let workflow = Path.Combine(repoRoot, "workflow")
let dist = Path.Combine(workflow, "dist")
let componentId = "task-runtime"
let version = "0.1.1"
let archiveName = $"{componentId}-v{version}.zip"
let publishedFiles =
    [ "FSharp.Core.dll"
      "Task.Runtime.deps.json"
      "Task.Runtime.dll"
      "Task.Runtime.runtimeconfig.json" ]
let archiveFiles = publishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let assertNotContains name (text: string) (value: string) =
    assertTrue name (not (text.Contains(value, StringComparison.OrdinalIgnoreCase)))

let runBuild () =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- repoRoot
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.ArgumentList.Add "fsi"
    info.ArgumentList.Add(Path.Combine(workflow, "BuildDistributions.fsx"))

    use childProcess = Process.Start info
    let output = childProcess.StandardOutput.ReadToEndAsync()
    let error = childProcess.StandardError.ReadToEndAsync()
    childProcess.WaitForExit()

    if childProcess.ExitCode <> 0 then
        failwithf "BuildDistributions.fsx failed: %s" (error.Result.Trim())

let preserveFile (path: string) (contents: byte array) =
    let existed = File.Exists path

    if not existed then
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllBytes(path, contents)

    existed, File.ReadAllBytes path

let sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let buildScript = File.ReadAllText(Path.Combine(workflow, "BuildDistributions.fsx"))
let pinsScript = File.ReadAllText(Path.Combine(workflow, "PrepareReleasePins.fsx"))

assertNotContains "build script has component-local ownership" buildScript "verifier"
assertNotContains "pin script has component-local ownership" pinsScript "verifier"
assertTrue
    "build script names the Task Runtime project"
    (buildScript.Contains("workflow/Task.Runtime.fsproj", StringComparison.Ordinal))
assertTrue
    "build script names the bootstrap version"
    (buildScript.Contains("let version = \"0.1.1\"", StringComparison.Ordinal))
assertTrue
    "pin script constructs the bootstrap archive"
    (pinsScript.Contains("let archiveName = $\"{componentId}-v{version}.zip\"", StringComparison.Ordinal))

let distributionDirectory = Path.Combine(dist, componentId)
let archivePath = Path.Combine(dist, archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let priorArchivePath = Path.Combine(dist, "task-runtime-v0.1.0.zip")
let priorManifestDirectory = Path.Combine(dist, "mcp-verifier")
let priorManifestDirectoryExisted = Directory.Exists priorManifestDirectory
let priorManifestPath = Path.Combine(priorManifestDirectory, "distribution.json")
let stalePublishPath = Path.Combine(distributionDirectory, "publish", "stale-output.txt")

assertTrue "consumer pins exist before regeneration" (File.Exists pinsPath)
let pinsBefore = File.ReadAllBytes pinsPath
let priorArchiveExisted, priorArchiveContents =
    preserveFile priorArchivePath [| 0x76uy; 0x30uy; 0x2euy; 0x31uy |]
let priorManifestExisted, priorManifestContents =
    preserveFile priorManifestPath [| 0x7buy; 0x7duy |]

try
    Directory.CreateDirectory(Path.GetDirectoryName stalePublishPath) |> ignore
    File.WriteAllText(stalePublishPath, "stale staging output")
    runBuild ()

    assertTrue "regeneration removes task-runtime staging output" (not (File.Exists stalePublishPath))
    assertEqual "stored v0.1.0 archive is preserved" priorArchiveContents (File.ReadAllBytes priorArchivePath)
    assertEqual "unrelated stored manifest is preserved" priorManifestContents (File.ReadAllBytes priorManifestPath)
    assertEqual "existing consumer pins are preserved" pinsBefore (File.ReadAllBytes pinsPath)
finally
    if not priorArchiveExisted && File.Exists priorArchivePath then
        File.Delete priorArchivePath

    if not priorManifestExisted && File.Exists priorManifestPath then
        File.Delete priorManifestPath

    if not priorManifestDirectoryExisted
       && Directory.Exists priorManifestDirectory
       && (Directory.EnumerateFileSystemEntries priorManifestDirectory |> Seq.isEmpty) then
        Directory.Delete priorManifestDirectory

    if File.Exists stalePublishPath then
        File.Delete stalePublishPath

for path in [ distributionDirectory; archivePath; manifestPath; pinsPath ] do
    assertTrue ($"required release artifact exists: {path}") (File.Exists path || Directory.Exists path)

let topLevel =
    Directory.EnumerateFileSystemEntries dist
    |> Seq.map Path.GetFileName
    |> Set.ofSeq

assertTrue
    "dist contains the Task Runtime release outputs"
    (Set.isSubset (Set.ofList [ componentId; archiveName; "consumer-pins.json" ]) topLevel)

let manifest = JsonNode.Parse(File.ReadAllText manifestPath).AsObject()
assertEqual "manifest id" componentId (manifest["id"].GetValue<string>())
assertEqual "manifest version" version (manifest["version"].GetValue<string>())
assertEqual "manifest archive" archiveName (manifest["archive"].GetValue<string>())
assertEqual "manifest entry DLL" "Task.Runtime.dll" (manifest["entryDll"].GetValue<string>())

let manifestFiles =
    manifest["files"].AsArray()
    |> Seq.map (fun value -> value.GetValue<string>())
    |> Set.ofSeq

assertEqual "manifest files are the deterministic allowlist" (Set.ofList archiveFiles) manifestFiles

let verifyArchive () =
    use archive = ZipFile.OpenRead archivePath
    let entries = archive.Entries |> Seq.map (fun entry -> entry.FullName.Replace('\\', '/')) |> Seq.toList
    assertEqual "archive entries are the deterministic allowlist" archiveFiles (entries |> List.sort)

    for entry in entries do
        let lower = entry.ToLowerInvariant()
        assertTrue ($"archive entry is not a source/build artifact: {entry}")
            (not (lower.Contains("/src/")
                  || lower.Contains("/bin/")
                  || lower.Contains("/obj/")
                  || lower.EndsWith(".fs")
                  || lower.EndsWith(".fsproj")
                  || lower.EndsWith(".pdb")
                  || lower.EndsWith(".exe")
                  || lower.Contains("apphost")))

verifyArchive ()

let pins = JsonNode.Parse(File.ReadAllText pinsPath).AsObject()
let pinKeys = pins |> Seq.map (fun pair -> pair.Key) |> Set.ofSeq
assertEqual "consumer pins contain only Task Runtime" (Set.singleton componentId) pinKeys
let pin = pins[componentId].AsObject()
assertEqual "pin asset name" archiveName (pin["assetName"].GetValue<string>())
assertEqual "pin archive SHA-256" (sha256 archivePath) (pin["archiveSha256"].GetValue<string>())
assertEqual "pin manifest SHA-256" (sha256 manifestPath) (pin["manifestSha256"].GetValue<string>())

printfn "Task Runtime distribution contract passed: %s" archiveName
