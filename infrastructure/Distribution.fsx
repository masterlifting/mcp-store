// Repo-shared deterministic distribution flow loaded as the explicit
// DistributionBuild module. One producer-supplied request keeps clean-tree
// provenance, publish, staging, manifest, and archive identical across producers.
module DistributionBuild

#load "Provenance.fsx"

open System
open System.IO
open System.IO.Compression
open System.Text.Json.Nodes
open System.Threading
open BuildProvenance

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

type DistributionResult =
    { ArchiveName: string
      ArchiveSha256: string
      ManifestSha256: string
      Revision: string }

let private listFiles (root: string) (searchOption: SearchOption) =
    Directory.EnumerateFiles(root, "*", searchOption)
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
    |> Seq.sort
    |> Seq.toList

let private assertExactFiles description expected actual : Result<unit, ReleaseError> =
    let expectedSet = expected |> Set.ofList
    let actualSet = actual |> Set.ofList

    if actual.Length <> actualSet.Count || actualSet <> expectedSet then
        Error(AllowlistMismatch(description, expected, actual))
    else
        Ok()

// Synchronous filesystem primitives (create/delete/copy/move) have no async
// counterpart; classify their expected failure at this boundary.
let private attempt operation path (work: unit -> unit) : Result<unit, ReleaseError> =
    try
        work ()
        Ok()
    with
    | :? IOException as error -> Error(FileFailure(operation, path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure(operation, path, error.Message))

// Entry timestamps are pinned so archive bytes do not depend on the build clock.
let private createArchive archivePath sourceRoot files : Async<Result<unit, ReleaseError>> =
    async {
        try
            use archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)

            for relativePath in files do
                let entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal)
                entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)

                use source = File.OpenRead(Path.Combine(sourceRoot, relativePath))
                use target = entry.Open()
                do! awaitComplete (source.CopyToAsync target)

            return Ok()
        with
        | :? OperationCanceledException as error -> return raise error
        | :? IOException as error -> return Error(FileFailure("archive", archivePath, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("archive", archivePath, error.Message))
        | :? NotSupportedException as error -> return Error(FileFailure("archive", archivePath, error.Message))
    }

let private buildWith
    (request: Request)
    (cancellationToken: CancellationToken)
    : Async<Result<DistributionResult, ReleaseError>> =
    releaseResult {
        do! BuildProvenance.assertCleanTree request.Root
        let! revision = BuildProvenance.committedHead request.Root
        let archiveFiles = request.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort
        let destination = Path.Combine(request.OutputRoot, request.ComponentId)
        let publishRoot = Path.Combine(destination, "publish")
        do! attempt "create output root" destination (fun () -> Directory.CreateDirectory destination |> ignore)

        // This run owns its staging directory and generated component files; clear
        // any prior copies before regenerating them.
        if Directory.Exists publishRoot then
            do! attempt "clear publish staging" publishRoot (fun () -> Directory.Delete(publishRoot, true))

        for relativePath in request.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] do
            let path = Path.Combine(destination, relativePath)

            if File.Exists path then
                do! attempt "clear staged file" path (fun () -> File.Delete path)

        let! _ =
            runProcess
                request.Root
                "dotnet"
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
                cancellationToken

        let actualFiles = listFiles publishRoot SearchOption.AllDirectories
        let expectedFiles = request.PublishedFiles |> List.sort

        do! assertExactFiles $"{request.ComponentId} publish output is not the deterministic allowlist" expectedFiles actualFiles

        for relativePath in expectedFiles do
            do!
                attempt "stage published file" relativePath (fun () ->
                    File.Copy(Path.Combine(publishRoot, relativePath), Path.Combine(destination, relativePath)))

        do! attempt "clear publish staging" publishRoot (fun () -> Directory.Delete(publishRoot, true))
        do! attempt "write NOTICE" (Path.Combine(destination, "NOTICE.txt")) (fun () -> File.WriteAllText(Path.Combine(destination, "NOTICE.txt"), request.Notice))

        let stagedFiles = listFiles destination SearchOption.TopDirectoryOnly

        do!
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
            let! fileHash = sha256FileAsync (Path.Combine(destination, file))
            let fileEntry = JsonObject()
            fileEntry["path"] <- JsonValue.Create file
            fileEntry["sha256"] <- JsonValue.Create fileHash
            files.Add fileEntry

        manifest["files"] <- files
        let archiveFileNames = JsonArray()

        for file in archiveFiles do
            archiveFileNames.Add(JsonValue.Create file)

        manifest["archiveFiles"] <- archiveFileNames
        let manifestPath = Path.Combine(destination, "distribution.json")
        do! writeJsonAsync manifestPath manifest

        let archiveSources = listFiles destination SearchOption.TopDirectoryOnly

        do! assertExactFiles $"{request.ComponentId} v1 archive sources contain unexpected files" archiveFiles archiveSources
        let archivePath = Path.Combine(request.OutputRoot, request.ArchiveName)

        if File.Exists archivePath then
            do! attempt "clear previous archive" archivePath (fun () -> File.Delete archivePath)

        do! createArchive archivePath destination archiveFiles
        let! archiveSha256 = sha256FileAsync archivePath
        let! manifestSha256 = sha256FileAsync manifestPath

        return
            { ArchiveName = request.ArchiveName
              ArchiveSha256 = archiveSha256
              ManifestSha256 = manifestSha256
              Revision = revision }
    }

let build (request: Request) : Async<Result<DistributionResult, ReleaseError>> =
    async {
        let! cancellationToken = Async.CancellationToken
        return! buildWith request cancellationToken
    }
