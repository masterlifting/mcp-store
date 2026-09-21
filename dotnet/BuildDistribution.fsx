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
let sdk = "11.0.100-rc.1.26425.128"
let version = "1.0.0"
let componentId = "dotnet"
let entryDll = "Mcp.Verifier.dll"

// The allowlist is the runtime contract. Source, project, build, and debug
// files produced by publish are intentionally excluded from the archive.
let publishedFiles =
    [ "FSharp.Core.dll"
      "Mcp.Verifier.deps.json"
      "Mcp.Verifier.dll"
      "Mcp.Verifier.runtimeconfig.json" ]

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

let revision =
    let info = ProcessStartInfo("git", "rev-parse HEAD")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    use childProcess = Process.Start info
    let value = childProcess.StandardOutput.ReadToEnd().Trim()
    childProcess.WaitForExit()

    if childProcess.ExitCode <> 0 || value.Length <> 40 then
        failwith "producer revision could not be resolved"

    value

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
        "--framework"
        "net11.0"
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

    assertExactFiles "verifier publish output is not the deterministic allowlist" expectedFiles actualFiles

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

    let archiveName = $"{componentId}-v{version}.zip"
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
        fileEntry["name"] <- JsonValue.Create file
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
