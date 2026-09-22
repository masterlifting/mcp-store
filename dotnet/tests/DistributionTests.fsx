// Deterministic contract coverage for the component-local dotnet v1 producer.
// The test regenerates v1.0.1 output and proves the stored v1.0.0 output is untouched.

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json.Nodes

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let dotnet = Path.Combine(repoRoot, "dotnet")
let dist = Path.Combine(dotnet, "dist")
let componentId = "dotnet"
let version = "1.0.1"
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

let assertExactFiles (name: string) (expected: string list) (actual: string list) =
    assertEqual $"{name} has no duplicate entries" actual.Length (actual |> Set.ofList |> Set.count)
    assertEqual name expected (actual |> List.sort)

let assertNoCaseInsensitiveDuplicates name (values: string list) =
    let duplicates =
        values
        |> List.groupBy _.ToLowerInvariant()
        |> List.filter (fun (_, entries) -> entries.Length > 1)

    assertTrue name duplicates.IsEmpty

let assertConsumerRelativePath label (path: string) =
    let normalized = path.Replace('\\', '/')
    let segments = normalized.Split('/')
    let driveQualified = normalized.Length >= 2 && Char.IsLetter(normalized.[0]) && normalized.[1] = ':'

    assertEqual $"{label} uses slash separators" normalized path
    assertTrue $"{label} is not rooted" (not (normalized.StartsWith("/", StringComparison.Ordinal)))
    assertTrue $"{label} is not drive-qualified" (not driveQualified)
    assertTrue $"{label} has no traversal or empty segments"
        (segments |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> ".."))

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

let sha256Stream (stream: Stream) =
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let buildScript = File.ReadAllText(Path.Combine(dotnet, "BuildDistribution.fsx"))
let pinsScript = File.ReadAllText(Path.Combine(dotnet, "PrepareReleasePins.fsx"))
assertTrue "build script owns only dotnet packaging" (not (buildScript.Contains("task-runtime", StringComparison.OrdinalIgnoreCase)))
assertTrue "pin script owns only dotnet packaging" (not (pinsScript.Contains("task-runtime", StringComparison.OrdinalIgnoreCase)))
assertTrue "build script uses dotnet project" (buildScript.Contains("dotnet/Mcp.Dotnet.fsproj", StringComparison.Ordinal))
assertTrue "build script names the corrected v1.0.1 release" (buildScript.Contains("let version = \"1.0.1\"", StringComparison.Ordinal))
assertTrue "scripts use the canonical component identity" (buildScript.Contains("let componentId = \"dotnet\"", StringComparison.Ordinal))
assertTrue "manifest records per-file hashes" (buildScript.Contains("fileEntry[\"sha256\"]", StringComparison.Ordinal))
assertTrue "manifest uses the OpenCode consumer path field" (buildScript.Contains("fileEntry[\"path\"]", StringComparison.Ordinal))
assertTrue "build script does not emit the incompatible name field" (not (buildScript.Contains("fileEntry[\"name\"]", StringComparison.Ordinal)))
assertTrue "pin script names the corrected v1.0.1 release" (pinsScript.Contains("let version = \"1.0.1\"", StringComparison.Ordinal))

let distributionDirectory = Path.Combine(dist, componentId)
let archivePath = Path.Combine(dist, archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let priorArchivePath = Path.Combine(dist, "dotnet-v1.0.0.zip")
let stalePublishPath = Path.Combine(distributionDirectory, "publish", "stale-output.txt")
let priorArchiveContents = if File.Exists priorArchivePath then Some(File.ReadAllBytes priorArchivePath) else None

try
    Directory.CreateDirectory(Path.GetDirectoryName stalePublishPath) |> ignore
    File.WriteAllText(stalePublishPath, "stale staging output")
    runScript (Path.Combine(dotnet, "BuildDistribution.fsx")) |> ignore

    assertTrue "regeneration removes verifier staging output" (not (File.Exists stalePublishPath))

    runScript (Path.Combine(dotnet, "PrepareReleasePins.fsx")) |> ignore
finally
    if File.Exists stalePublishPath then
        File.Delete stalePublishPath

match priorArchiveContents with
| Some contents -> assertEqual "stored v1.0.0 archive is preserved" contents (File.ReadAllBytes priorArchivePath)
| None -> ()

for path in [ distributionDirectory; archivePath; manifestPath; pinsPath ] do
    assertTrue ($"required release artifact exists: {path}") (File.Exists path || Directory.Exists path)

let manifest = JsonNode.Parse(File.ReadAllText manifestPath).AsObject()
assertEqual "manifest id" componentId (manifest["id"].GetValue<string>())
assertEqual "manifest version" version (manifest["version"].GetValue<string>())
assertEqual "manifest archive" archiveName (manifest["archive"].GetValue<string>())
assertEqual "manifest entry DLL" "Mcp.Verifier.dll" (manifest["entryDll"].GetValue<string>())

let manifestEntries =
    manifest["files"].AsArray()
    |> Seq.map (fun value ->
        let entry = value.AsObject()
        assertExactFiles "manifest file entry properties" [ "path"; "sha256" ] (entry |> Seq.map (fun pair -> pair.Key) |> Seq.toList)
        entry["path"].GetValue<string>(), entry["sha256"].GetValue<string>())
    |> Seq.toList

let manifestFileNames = manifestEntries |> List.map fst
assertExactFiles "manifest runtime files are the deterministic allowlist" publishedFiles manifestFileNames
assertNoCaseInsensitiveDuplicates "manifest paths are unique case-insensitively" manifestFileNames
assertEqual "manifest archive files are the deterministic allowlist" archiveFiles
    (manifest["archiveFiles"].AsArray() |> Seq.map (fun value -> value.GetValue<string>()) |> Seq.toList)
assertNoCaseInsensitiveDuplicates "archive allowlist paths are unique case-insensitively" archiveFiles

for file, hash in manifestEntries do
    assertConsumerRelativePath $"manifest path {file}" file
    assertTrue $"manifest path {file} is not distribution.json" (not (file.Equals("distribution.json", StringComparison.OrdinalIgnoreCase)))
    assertEqual $"manifest path is the exact runtime allowlist entry {file}" file (publishedFiles |> List.find ((=) file))
    assertEqual $"manifest SHA-256 for {file}" (sha256 (Path.Combine(distributionDirectory, file))) hash
    assertTrue $"manifest SHA-256 is lowercase hexadecimal for {file}"
        (hash.Length = 64 && hash = hash.ToLowerInvariant() && hash |> Seq.forall Uri.IsHexDigit)

let archive = ZipFile.OpenRead archivePath
let entries = archive.Entries |> Seq.map (fun entry -> entry.FullName.Replace('\\', '/')) |> Seq.toList
assertExactFiles "archive entries are the deterministic allowlist" archiveFiles entries
assertNoCaseInsensitiveDuplicates "archive entries are unique case-insensitively" entries

for entry in entries do
    assertConsumerRelativePath $"archive entry {entry}" entry
    let lower = entry.ToLowerInvariant()
    let segments = lower.Split('/')
    assertTrue ($"archive entry is not a source/build artifact: {entry}")
        (not (segments |> Array.exists (fun segment -> segment = "src" || segment = "bin" || segment = "obj")
              || lower.StartsWith("src/")
              || lower.StartsWith("bin/")
              || lower.StartsWith("obj/")
              || lower.EndsWith(".fs")
              || lower.EndsWith(".fsproj")
              || lower.EndsWith(".pdb")
              || lower.EndsWith(".exe")
               || lower.Contains("apphost")))

let manifestHashes = manifestEntries |> Map.ofList
for entry in archive.Entries do
    match manifestHashes |> Map.tryFind (entry.FullName.Replace('\\', '/')) with
    | Some expectedHash ->
        use payload = entry.Open()
        assertEqual $"archive payload hash for {entry.FullName}" expectedHash (sha256Stream payload)
    | None -> ()

archive.Dispose()

let pins = JsonNode.Parse(File.ReadAllText pinsPath).AsObject()
let pinKeys = pins |> Seq.map (fun pair -> pair.Key) |> Set.ofSeq
assertEqual "consumer pins contain only dotnet" (Set.singleton componentId) pinKeys
let pin = pins[componentId].AsObject()
assertEqual "pin asset name" archiveName (pin["assetName"].GetValue<string>())
assertEqual "pin archive SHA-256" (sha256 archivePath) (pin["archiveSha256"].GetValue<string>())
assertEqual "pin manifest SHA-256" (sha256 manifestPath) (pin["manifestSha256"].GetValue<string>())

printfn "dotnet distribution contract passed: %s" archiveName
