// role: test entrypoint
#load "../../../infrastructure/Distribution.fsx"
#load "../../../infrastructure/Testing.fsx"

open System.IO
open SharedFailure

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let workflow = Path.Combine(repoRoot, "workflow")
let dist = Path.Combine(workflow, "dist")

async {
    let! loaded = DistributionBuild.loadConfig (Path.Combine(workflow, "release", "Config.json"))

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
          PublishedFiles = config.PublishedFiles
          LaunchKind = DistributionContract.LaunchKind.wireName config.LaunchKind }

    // The helper checks manifest consistency only, so each entrypoint pins its own launch role.
    DistributionTestHelper.assertEqual
        "the workflow package is an MCP launch target"
        DistributionContract.DotnetExec
        config.LaunchKind

    let layout: DistributionTestHelper.Layout =
        { RepoRoot = repoRoot
          DistributionDirectory = distributionDirectory
          ArchivePath = archivePath
          ManifestPath = manifestPath
          PinsPath = pinsPath }

    do! DistributionTestHelper.assertDistributionContract producer layout
}
// FSI needs one synchronous top-level entry for the asynchronous script.
|> Async.RunSynchronously
