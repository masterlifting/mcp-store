// role: test entrypoint
// Validates the freshly built and pinned Resolver distribution: published contract and archive closure.
// Only the packaged bytes are read: no build, network, credentials, or host runtime state.

#load "../../../Distribution.fsx"
#load "../../../Testing.fsx"

open System.IO
open System.IO.Compression
open SharedFailure
open DistributionContract

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", ".."))
let resolverRoot = Path.Combine(repoRoot, "infrastructure", "resolver")
let dist = Path.Combine(resolverRoot, "dist")

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

    DistributionTestHelper.assertEqual "the resolver is enrolled as a host-run CLI" (LaunchKind.wireName DotnetCli) producer.LaunchKind
    do! DistributionTestHelper.assertDistributionContract producer layout

    DistributionTestHelper.assertExactFiles
        "the resolver archive is the agreed flat CLI closure"
        expectedArchiveMembers
        (archiveMembers archivePath)

    printfn "OK resolver package: %s is a %s artifact with the agreed closure" archiveName producer.LaunchKind
}
// FSI needs one synchronous top-level entry for the asynchronous script.
|> Async.RunSynchronously
