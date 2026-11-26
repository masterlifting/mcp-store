// role: test entrypoint
// Packages the Resolver through the shared distribution flow and validates the published contract and
// archive closure. Only local release scripts and the packaged bytes are involved: no network,
// credentials, or host runtime state.

#load "../../../Distribution.fsx"
#load "../../../Testing.fsx"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open SharedFailure
open DistributionContract

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", ".."))
let resolverRoot = Path.Combine(repoRoot, "infrastructure", "resolver")
let dist = Path.Combine(resolverRoot, "dist")

let runDotnet (arguments: string list) : Async<string> =
    async {
        let info = ProcessStartInfo("dotnet")
        info.WorkingDirectory <- repoRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        arguments |> List.iter info.ArgumentList.Add

        use childProcess = Process.Start info
        let outputTask = childProcess.StandardOutput.ReadToEndAsync()
        let errorTask = childProcess.StandardError.ReadToEndAsync()
        do! childProcess.WaitForExitAsync() |> Async.AwaitTask
        let! output = outputTask |> Async.AwaitTask
        let! error = errorTask |> Async.AwaitTask

        if childProcess.ExitCode <> 0 then
            return failwithf "dotnet %s failed: %s" (String.concat " " arguments) (error.Trim())

        return output
    }

let runScript path = runDotnet [ "fsi"; path ]

// The consumption contract admits exactly this flat closure, so the expectation stays independent of
// the policy that drives the shared contract assertions.
let expectedArchiveMembers =
    sortOrdinal
        [ "FSharp.Core.dll"
          "NOTICE.txt"
          "Resolver.deps.json"
          "Resolver.dll"
          "Resolver.runtimeconfig.json"
          manifestEntryName ]

let archiveMembers (path: string) : string list =
    let archive = ZipFile.OpenRead path
    try
        archive.Entries
        |> Seq.map (fun entry -> entry.FullName.Replace('\\', '/'))
        |> Seq.toList
    finally
        archive.Dispose()

// Concurrent pipe drains prevent a child script from blocking on redirected output.
async {
    let! loaded = DistributionBuild.loadConfig (Path.Combine(resolverRoot, "release", "Config.json"))

    let config =
        match loaded with
        | Ok value -> value
        | Error error -> failwith (DistributionBuild.describeError error)

    let componentId = config.ComponentId
    let archiveName = config.ArchiveName
    let distributionDirectory = Path.Combine(dist, componentId)
    let archivePath = Path.Combine(dist, archiveName)
    let manifestPath = Path.Combine(distributionDirectory, manifestEntryName)
    let pinsPath = Path.Combine(dist, "consumer-pins.json")
    let stalePublishPath = Path.Combine(distributionDirectory, "publish", "stale-output.txt")

    let producer: DistributionTestHelper.Producer =
        { ComponentId = config.ComponentId
          Version = config.Version
          SdkVersion = config.SdkVersion
          RuntimeVersion = config.RuntimeVersion
          Platform = config.Platform
          EntryDll = config.EntryDll
          ArchiveName = config.ArchiveName
          AssetUri = config.AssetUri
          PublishedFiles = config.PublishedFiles
          LaunchKind = LaunchKind.wireName config.LaunchKind }

    let layout: DistributionTestHelper.Layout =
        { RepoRoot = repoRoot
          DistributionDirectory = distributionDirectory
          ArchivePath = archivePath
          ManifestPath = manifestPath
          PinsPath = pinsPath }

    try
        Directory.CreateDirectory(Path.GetDirectoryName stalePublishPath) |> ignore
        File.WriteAllText(stalePublishPath, "stale staging output")
        do! runScript (Path.Combine(resolverRoot, "release", "Build.fsx")) |> Async.Ignore
        DistributionTestHelper.assertTrue "regeneration removes resolver staging output" (not (File.Exists stalePublishPath))
        do! runScript (Path.Combine(resolverRoot, "release", "Pin.fsx")) |> Async.Ignore

        DistributionTestHelper.assertEqual "the resolver is enrolled as a host-run CLI" (LaunchKind.wireName DotnetCli) producer.LaunchKind
        do! DistributionTestHelper.assertDistributionContract producer layout

        DistributionTestHelper.assertExactFiles
            "the resolver archive is the agreed flat CLI closure"
            expectedArchiveMembers
            (archiveMembers archivePath)

        printfn "OK resolver package: %s is a %s artifact with the agreed closure" archiveName producer.LaunchKind
    finally
        if File.Exists stalePublishPath then File.Delete stalePublishPath
}
// FSI needs one synchronous top-level entry for the asynchronous script.
|> Async.RunSynchronously
