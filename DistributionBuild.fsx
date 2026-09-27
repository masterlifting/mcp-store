// Repo-shared distribution generation mechanics. Producers supply an explicit
// request so the whole flow — clean-tree provenance, framework-dependent publish,
// transient staging, exact allowlist enforcement, manifest, and deterministic
// archive — is identical between distributions. The file name supplies the
// implicit module name, so `#load` exposes these as DistributionBuild.*.
#load "BuildProvenance.fsx"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

// Everything the shared flow needs; producer release policy stays in the
// producer script that populates it.
type Request =
    { Root: string
      ProjectPath: string
      OutputRoot: string
      ComponentId: string
      Version: string
      SdkVersion: string
      EntryDll: string
      ArchiveName: string
      PublishedFiles: string list
      ExtraPublishProperties: string list
      Notice: string }

type Result =
    { ArchiveName: string
      ArchiveSha256: string
      ManifestSha256: string
      Revision: string }

let private run root arguments =
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

let private sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private writeJson path (value: JsonNode) =
    File.WriteAllText(path, value.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

let private listFiles (root: string) (searchOption: SearchOption) =
    Directory.EnumerateFiles(root, "*", searchOption)
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
    |> Seq.sort
    |> Seq.toList

let private assertExactFiles description expected actual =
    let expectedSet = expected |> Set.ofList
    let actualSet = actual |> Set.ofList

    if actual.Length <> actualSet.Count || actualSet <> expectedSet then
        failwithf "%s: expected exactly %A, got %A" description expected actual

// Entry timestamps are pinned so archive bytes do not depend on the build clock.
let private createArchive archivePath sourceRoot files =
    use archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)

    for relativePath in files do
        let entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)

        use source = File.OpenRead(Path.Combine(sourceRoot, relativePath))
        use target = entry.Open()
        source.CopyTo target

let build (request: Request) =
    BuildProvenance.assertCleanTree request.Root
    let revision = BuildProvenance.committedHead request.Root
    let archiveFiles = request.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort
    let destination = Path.Combine(request.OutputRoot, request.ComponentId)
    Directory.CreateDirectory destination |> ignore
    let publishRoot = Path.Combine(destination, "publish")

    // Only this run's staging and generated files are replaceable; stored
    // archives and manifests remain available for inspection.
    if Directory.Exists publishRoot then
        Directory.Delete(publishRoot, true)

    for relativePath in request.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] do
        let path = Path.Combine(destination, relativePath)

        if File.Exists path then
            File.Delete path

    run
        request.Root
        ([ "publish"
           request.ProjectPath
           "--configuration"
           "Release"
           "--self-contained"
           "false"
           "-p:UseAppHost=false"
           "-p:DebugType=None"
           "-p:DebugSymbols=false" ]
         @ request.ExtraPublishProperties
         @ [ "-p:SatelliteResourceLanguages=none"; "--output"; publishRoot ])

    let actualFiles = listFiles publishRoot SearchOption.AllDirectories
    let expectedFiles = request.PublishedFiles |> List.sort

    assertExactFiles $"{request.ComponentId} publish output is not the deterministic allowlist" expectedFiles actualFiles

    for relativePath in expectedFiles do
        File.Copy(Path.Combine(publishRoot, relativePath), Path.Combine(destination, relativePath))

    Directory.Delete(publishRoot, true)
    File.WriteAllText(Path.Combine(destination, "NOTICE.txt"), request.Notice)

    let stagedFiles = listFiles destination SearchOption.TopDirectoryOnly

    assertExactFiles
        $"{request.ComponentId} v1 staging contains unexpected files"
        (request.PublishedFiles @ [ "NOTICE.txt" ] |> List.sort)
        stagedFiles

    let manifest = JsonObject()
    manifest["schemaVersion"] <- JsonValue.Create 1
    manifest["id"] <- JsonValue.Create request.ComponentId
    manifest["version"] <- JsonValue.Create request.Version
    manifest["revision"] <- JsonValue.Create revision
    manifest["sdk"] <- JsonValue.Create request.SdkVersion
    manifest["entryDll"] <- JsonValue.Create request.EntryDll
    manifest["archive"] <- JsonValue.Create request.ArchiveName
    let files = JsonArray()

    for file in request.PublishedFiles |> List.sort do
        let fileEntry = JsonObject()
        fileEntry["path"] <- JsonValue.Create file
        fileEntry["sha256"] <- JsonValue.Create(sha256 (Path.Combine(destination, file)))
        files.Add fileEntry

    manifest["files"] <- files
    let archiveFileNames = JsonArray()

    for file in archiveFiles do
        archiveFileNames.Add(JsonValue.Create file)

    manifest["archiveFiles"] <- archiveFileNames
    let manifestPath = Path.Combine(destination, "distribution.json")
    writeJson manifestPath manifest

    let archiveSources = listFiles destination SearchOption.TopDirectoryOnly

    assertExactFiles $"{request.ComponentId} v1 archive sources contain unexpected files" archiveFiles archiveSources
    let archivePath = Path.Combine(request.OutputRoot, request.ArchiveName)

    if File.Exists archivePath then
        File.Delete archivePath

    createArchive archivePath destination archiveFiles

    { ArchiveName = request.ArchiveName
      ArchiveSha256 = sha256 archivePath
      ManifestSha256 = sha256 manifestPath
      Revision = revision }
