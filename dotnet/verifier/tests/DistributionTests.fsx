// Deterministic contract coverage for the component-local dotnet-verifier
// producer. Run after the producer scripts have generated the release output.

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json.Nodes

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let verifier = Path.Combine(repoRoot, "dotnet", "verifier")
let dist = Path.Combine(verifier, "dist")
let componentId = "dotnet-verifier"
let version = "0.2.0"
let archiveName = $"{componentId}-v{version}.zip"
let publishedFiles =
    [ "FSharp.Core.dll"
      "Mcp.Verifier.deps.json"
      "Mcp.Verifier.dll"
      "Mcp.Verifier.runtimeconfig.json" ]
let archiveFiles = publishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let runScript path =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- repoRoot
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.ArgumentList.Add "fsi"
    info.ArgumentList.Add path

    use childProcess = Process.Start info
    let output = childProcess.StandardOutput.ReadToEndAsync()
    let error = childProcess.StandardError.ReadToEndAsync()
    childProcess.WaitForExit()

    if childProcess.ExitCode <> 0 then
        failwithf "%s failed: %s" path (error.Result.Trim())

    output.Result

let sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let preserveFile (path: string) (contents: byte array) =
    let existed = File.Exists path

    if not existed then
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllBytes(path, contents)

    existed, File.ReadAllBytes path

let buildScript = File.ReadAllText(Path.Combine(verifier, "BuildDistribution.fsx"))
let pinsScript = File.ReadAllText(Path.Combine(verifier, "PrepareReleasePins.fsx"))
assertTrue "build script owns only verifier packaging" (not (buildScript.Contains("task-runtime", StringComparison.OrdinalIgnoreCase)))
assertTrue "pin script owns only verifier packaging" (not (pinsScript.Contains("task-runtime", StringComparison.OrdinalIgnoreCase)))
assertTrue "build script uses verifier project" (buildScript.Contains("dotnet/verifier/Mcp.Verifier.fsproj", StringComparison.Ordinal))
assertTrue "build script names the 0.2.0 release" (buildScript.Contains("let version = \"0.2.0\"", StringComparison.Ordinal))
assertTrue "scripts use the canonical component identity" (buildScript.Contains("let componentId = \"dotnet-verifier\"", StringComparison.Ordinal))

let distributionDirectory = Path.Combine(dist, componentId)
let archivePath = Path.Combine(dist, archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let priorArchivePath = Path.Combine(dist, "mcp-verifier-v0.1.0.zip")
let stalePublishPath = Path.Combine(distributionDirectory, "publish", "stale-output.txt")
let priorArchiveExisted, priorArchiveContents = preserveFile priorArchivePath [| 0x76uy; 0x30uy; 0x2euy; 0x31uy |]

try
    Directory.CreateDirectory(Path.GetDirectoryName stalePublishPath) |> ignore
    File.WriteAllText(stalePublishPath, "stale staging output")
    runScript (Path.Combine(verifier, "BuildDistribution.fsx")) |> ignore

    assertTrue "regeneration removes verifier staging output" (not (File.Exists stalePublishPath))
    assertEqual "stored v0.1.0 archive is preserved" priorArchiveContents (File.ReadAllBytes priorArchivePath)

    runScript (Path.Combine(verifier, "PrepareReleasePins.fsx")) |> ignore
finally
    if not priorArchiveExisted && File.Exists priorArchivePath then
        File.Delete priorArchivePath

    if File.Exists stalePublishPath then
        File.Delete stalePublishPath

for path in [ distributionDirectory; archivePath; manifestPath; pinsPath ] do
    assertTrue ($"required release artifact exists: {path}") (File.Exists path || Directory.Exists path)

let manifest = JsonNode.Parse(File.ReadAllText manifestPath).AsObject()
assertEqual "manifest id" componentId (manifest["id"].GetValue<string>())
assertEqual "manifest version" version (manifest["version"].GetValue<string>())
assertEqual "manifest archive" archiveName (manifest["archive"].GetValue<string>())
assertEqual "manifest entry DLL" "Mcp.Verifier.dll" (manifest["entryDll"].GetValue<string>())

let manifestFiles =
    manifest["files"].AsArray()
    |> Seq.map (fun value -> value.GetValue<string>())
    |> Set.ofSeq

assertEqual "manifest files are the deterministic allowlist" (Set.ofList archiveFiles) manifestFiles

let archive = ZipFile.OpenRead archivePath
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

archive.Dispose()

let pins = JsonNode.Parse(File.ReadAllText pinsPath).AsObject()
let pinKeys = pins |> Seq.map (fun pair -> pair.Key) |> Set.ofSeq
assertEqual "consumer pins contain only dotnet-verifier" (Set.singleton componentId) pinKeys
let pin = pins[componentId].AsObject()
assertEqual "pin asset name" archiveName (pin["assetName"].GetValue<string>())
assertEqual "pin archive SHA-256" (sha256 archivePath) (pin["archiveSha256"].GetValue<string>())
assertEqual "pin manifest SHA-256" (sha256 manifestPath) (pin["manifestSha256"].GetValue<string>())

printfn "dotnet-verifier distribution contract passed: %s" archiveName
