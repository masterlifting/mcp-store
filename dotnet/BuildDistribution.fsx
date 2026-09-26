#load "ReleaseConfig.fsx"
#load "../BuildProvenance.fsx"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let dotnet = Path.Combine(root, "dotnet")
let outputRoot = Path.Combine(dotnet, "dist")
let sdk = ReleaseConfig.sdkVersion
let version = ReleaseConfig.version
let componentId = ReleaseConfig.componentId
let entryDll = ReleaseConfig.entryDll
let archiveName = ReleaseConfig.archiveName

// The allowlist is the runtime contract. Source, project, build, and debug
// files produced by publish are intentionally excluded from the archive.
let publishedFiles = ReleaseConfig.publishedFiles

let run arguments =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    arguments |> List.iter info.ArgumentList.Add
    use childProcess = Process.Start info
    let output = childProcess.StandardOutput.ReadToEndAsync()
    let error = childProcess.StandardError.ReadToEndAsync()
    childProcess.WaitForExit()

    if childProcess.ExitCode <> 0 then
        failwithf "dotnet %s failed: %s" (String.concat " " arguments) (error.Result.Trim())

BuildProvenance.assertCleanTree root
let revision = BuildProvenance.committedHead root

let sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let writeJson path (value: JsonNode) =
    File.WriteAllText(path, value.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

let archiveFiles = publishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort

let assertExactFiles description expected actual =
    let expectedSet = expected |> Set.ofList
    let actualSet = actual |> Set.ofList

    if actual.Length <> actualSet.Count || actualSet <> expectedSet then
        failwithf "%s: expected exactly %A, got %A" description expected actual

let createArchive archivePath sourceRoot files =
    use archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)

    for relativePath in files do
        let entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)

        use source = File.OpenRead(Path.Combine(sourceRoot, relativePath))
        use target = entry.Open()
        source.CopyTo target

let publish () =
    let destination = Path.Combine(outputRoot, componentId)
    Directory.CreateDirectory destination |> ignore
    let publishRoot = Path.Combine(destination, "publish")

    // Only this release's staging and generated files are replaceable. Stored
    // archives and manifests, including the v0.x release, remain available for inspection.
    if Directory.Exists publishRoot then
        Directory.Delete(publishRoot, true)

    for relativePath in publishedFiles @ [ "NOTICE.txt"; "distribution.json" ] do
        let path = Path.Combine(destination, relativePath)

        if File.Exists path then
            File.Delete path

    run [
        "publish"
        "dotnet/Mcp.Dotnet.fsproj"
        "--configuration"
        "Release"
        "--self-contained"
        "false"
        "-p:UseAppHost=false"
        "-p:DebugType=None"
        "-p:DebugSymbols=false"
        "-p:GenerateDocumentationFile=false"
        "-p:SatelliteResourceLanguages=none"
        "--output"
        publishRoot
    ]

    let actualFiles =
        Directory.EnumerateFiles(publishRoot, "*", SearchOption.AllDirectories)
        |> Seq.map (fun path -> Path.GetRelativePath(publishRoot, path).Replace('\\', '/'))
        |> Seq.sort
        |> Seq.toList

    let expectedFiles = publishedFiles |> List.sort

    assertExactFiles "dotnet publish output is not the deterministic allowlist" expectedFiles actualFiles

    for relativePath in expectedFiles do
        File.Copy(Path.Combine(publishRoot, relativePath), Path.Combine(destination, relativePath))

    Directory.Delete(publishRoot, true)
    File.WriteAllText(Path.Combine(destination, "NOTICE.txt"), "mcp-store dotnet MCP distribution\n")

    let stagedFiles =
        Directory.EnumerateFiles(destination, "*", SearchOption.TopDirectoryOnly)
        |> Seq.map (fun path -> Path.GetRelativePath(destination, path).Replace('\\', '/'))
        |> Seq.sort
        |> Seq.toList

    assertExactFiles "dotnet v1 staging contains unexpected files" (publishedFiles @ [ "NOTICE.txt" ] |> List.sort) stagedFiles

    let manifest = JsonObject()
    manifest["schemaVersion"] <- JsonValue.Create 1
    manifest["id"] <- JsonValue.Create componentId
    manifest["version"] <- JsonValue.Create version
    manifest["revision"] <- JsonValue.Create revision
    manifest["sdk"] <- JsonValue.Create sdk
    manifest["entryDll"] <- JsonValue.Create entryDll
    manifest["archive"] <- JsonValue.Create archiveName
    let files = JsonArray()

    for file in publishedFiles |> List.sort do
        let fileEntry = JsonObject()
        fileEntry["path"] <- JsonValue.Create file
        fileEntry["sha256"] <- JsonValue.Create(sha256 (Path.Combine(destination, file)))
        files.Add(fileEntry)

    manifest["files"] <- files
    let archiveFileNames = JsonArray()

    for file in archiveFiles do
        archiveFileNames.Add(JsonValue.Create file)

    manifest["archiveFiles"] <- archiveFileNames
    writeJson (Path.Combine(destination, "distribution.json")) manifest

    let archiveSources =
        Directory.EnumerateFiles(destination, "*", SearchOption.TopDirectoryOnly)
        |> Seq.map (fun path -> Path.GetRelativePath(destination, path).Replace('\\', '/'))
        |> Seq.sort
        |> Seq.toList

    assertExactFiles "dotnet v1 archive sources contain unexpected files" archiveFiles archiveSources
    let archivePath = Path.Combine(outputRoot, archiveName)

    if File.Exists archivePath then
        File.Delete archivePath

    createArchive archivePath destination archiveFiles
    archiveName, sha256 archivePath, sha256 (Path.Combine(destination, "distribution.json"))

let distribution = publish ()

printfn
    "dotnet archive=%s sha256=%s manifest=%s"
    (let (name, _, _) = distribution in name)
    (let (_, hash, _) = distribution in hash)
    (let (_, _, hash) = distribution in hash)
