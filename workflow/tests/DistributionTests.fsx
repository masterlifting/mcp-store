#load "../release/Config.fsx"
#load "../../infrastructure/Testing.fsx"

open System
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

let runScript path : Async<string> =
    async {
        let info = ProcessStartInfo("dotnet")
        info.WorkingDirectory <- repoRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add "fsi"
        info.ArgumentList.Add path

        use childProcess = Process.Start info
        let outputTask = childProcess.StandardOutput.ReadToEndAsync()
        let errorTask = childProcess.StandardError.ReadToEndAsync()
        do! childProcess.WaitForExitAsync() |> Async.AwaitTask
        let! output = outputTask |> Async.AwaitTask
        let! error = errorTask |> Async.AwaitTask
        if childProcess.ExitCode <> 0 then return failwithf "%s failed: %s" path (error.Trim())
        return output
    }

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

// Concurrent pipe drains prevent a child script from blocking on redirected output.
async {
    let! generation =
        async {
        Directory.CreateDirectory(Path.GetDirectoryName stalePublishPath) |> ignore
        File.WriteAllText(stalePublishPath, "stale staging output")
        let! _ = runScript (Path.Combine(workflow, "release", "Build.fsx"))
        DistributionTestHelper.assertTrue "regeneration removes workflow staging output" (not (File.Exists stalePublishPath))
        let! _ = runScript (Path.Combine(workflow, "release", "Pins.fsx"))
        return ()
        }
        |> Async.Catch

    if File.Exists stalePublishPath then File.Delete stalePublishPath

    match generation with
    | Choice1Of2 () -> ()
    | Choice2Of2 error -> return raise error

    do! DistributionTestHelper.assertDistributionContract producer layout
}
// FSI needs one synchronous top-level entry for the asynchronous script.
|> Async.RunSynchronously
