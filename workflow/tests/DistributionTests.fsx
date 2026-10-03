// Deterministic contract coverage for the component-local workflow v1 producer.
// The test regenerates the current v1 release output and verifies the
// manifest/pin contract end-to-end.

#load "../ReleaseConfig.fsx"
#load "../../DistributionTestHelper.fsx"

open System.Diagnostics
open System.IO

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let workflow = Path.Combine(repoRoot, "workflow")
let dist = Path.Combine(workflow, "dist")
let componentId = ReleaseConfig.componentId
let archiveName = ReleaseConfig.archiveName
let distributionDirectory = Path.Combine(dist, componentId)
let archivePath = Path.Combine(dist, archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let stalePublishPath = Path.Combine(distributionDirectory, "publish", "stale-output.txt")

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

try
    Directory.CreateDirectory(Path.GetDirectoryName stalePublishPath) |> ignore
    File.WriteAllText(stalePublishPath, "stale staging output")
    runScript (Path.Combine(workflow, "BuildDistributions.fsx")) |> ignore

    DistributionTestHelper.assertTrue "regeneration removes workflow staging output" (not (File.Exists stalePublishPath))

    runScript (Path.Combine(workflow, "PrepareReleasePins.fsx")) |> ignore
finally
    if File.Exists stalePublishPath then
        File.Delete stalePublishPath

let producer: DistributionTestHelper.Producer =
    { ComponentId = ReleaseConfig.componentId
      Version = ReleaseConfig.version
      SdkVersion = ReleaseConfig.sdkVersion
      EntryDll = ReleaseConfig.entryDll
      ArchiveName = ReleaseConfig.archiveName
      AssetUri = ReleaseConfig.assetUri
      PublishedFiles = ReleaseConfig.publishedFiles }

let layout: DistributionTestHelper.Layout =
    { RepoRoot = repoRoot
      DistributionDirectory = distributionDirectory
      ArchivePath = archivePath
      ManifestPath = manifestPath
      PinsPath = pinsPath }

// Standalone entry bridge: the only synchronous wait in this script's flow;
// the contract check itself composes asynchronously.
DistributionTestHelper.assertDistributionContract producer layout |> Async.RunSynchronously

printfn "workflow distribution contract passed: %s" archiveName
