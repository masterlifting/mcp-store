// role: test entrypoint
#load "../release/Config.fsx"
#load "../../infrastructure/Testing.fsx"

open System
open System.Diagnostics
open System.IO
open System.Reflection.PortableExecutable

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let dotnet = Path.Combine(repoRoot, "dotnet")
let dist = Path.Combine(dotnet, "dist")
let componentId = ReleaseConfig.componentId
let archiveName = ReleaseConfig.archiveName
let distributionDirectory = Path.Combine(dist, componentId)
let archivePath = Path.Combine(dist, archiveName)
let manifestPath = Path.Combine(distributionDirectory, "distribution.json")
let pinsPath = Path.Combine(dist, "consumer-pins.json")
let stalePublishPath = Path.Combine(distributionDirectory, "publish", "stale-output.txt")

let runDotnet arguments : Async<string> =
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

// Concurrent pipe drains prevent child output from filling a redirected stream.
async {
    do! runDotnet [ "clean"; "dotnet/Mcp.Dotnet.fsproj"; "-c"; "Release"; "--nologo" ] |> Async.Ignore
    do! runDotnet [ "build"; "dotnet/Mcp.Dotnet.fsproj"; "-c"; "Release"; "--nologo" ] |> Async.Ignore

    try
        Directory.CreateDirectory(Path.GetDirectoryName stalePublishPath) |> ignore
        File.WriteAllText(stalePublishPath, "stale staging output")
        do! runScript (Path.Combine(dotnet, "release", "Build.fsx")) |> Async.Ignore
        DistributionTestHelper.assertTrue "regeneration removes dotnet staging output" (not (File.Exists stalePublishPath))
        do! runScript (Path.Combine(dotnet, "release", "Pins.fsx")) |> Async.Ignore
    finally
        if File.Exists stalePublishPath then File.Delete stalePublishPath

    do! DistributionTestHelper.assertDistributionContract producer layout

    let packagedDllPath = Path.Combine(distributionDirectory, ReleaseConfig.entryDll)
    let packagedDllHasNoDebugDirectory =
        use stream = File.OpenRead packagedDllPath
        use pe = new PEReader(stream)
        pe.ReadDebugDirectory().IsEmpty

    DistributionTestHelper.assertTrue "packaged entry DLL carries no debug directory" packagedDllHasNoDebugDirectory
}
// FSI needs one synchronous top-level entry for the asynchronous script.
|> Async.RunSynchronously
