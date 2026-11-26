// role: test entrypoint
#load "../../../infrastructure/Distribution.fsx"
#load "../../../infrastructure/Testing.fsx"

open System.IO
open System.Reflection.PortableExecutable
open SharedFailure

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let dotnet = Path.Combine(repoRoot, "dotnet")
let dist = Path.Combine(dotnet, "dist")

async {
    let! loaded = DistributionBuild.loadConfig (Path.Combine(dotnet, "release", "Config.json"))

    let config =
        match loaded with
        | Ok value -> value
        | Error error -> failwith (DistributionBuild.describeError error)

    let componentId = config.ComponentId
    let archiveName = config.ArchiveName
    let distributionDirectory = Path.Combine(dist, componentId)
    let archivePath = Path.Combine(dist, archiveName)
    let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
    let pinsPath = Path.Combine(dist, "consumer-pins.json")

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
          LaunchKind = DistributionContract.LaunchKind.wireName config.LaunchKind }

    // The helper checks manifest consistency only, so each entrypoint pins its own launch role.
    DistributionTestHelper.assertEqual
        "the dotnet package is an MCP launch target"
        DistributionContract.DotnetExec
        config.LaunchKind

    let layout: DistributionTestHelper.Layout =
        { RepoRoot = repoRoot
          DistributionDirectory = distributionDirectory
          ArchivePath = archivePath
          ManifestPath = manifestPath
          PinsPath = pinsPath }

    do! DistributionTestHelper.assertDistributionContract producer layout

    let packagedDllPath = Path.Combine(distributionDirectory, config.EntryDll)
    let packagedDllHasNoDebugDirectory =
        use stream = File.OpenRead packagedDllPath
        use pe = new PEReader(stream)
        pe.ReadDebugDirectory().IsEmpty

    DistributionTestHelper.assertTrue "packaged entry DLL carries no debug directory" packagedDllHasNoDebugDirectory
}
// FSI needs one synchronous top-level entry for the asynchronous script.
|> Async.RunSynchronously
