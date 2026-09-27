// Deterministic contract coverage for the component-local dotnet v1 producer.
// The test regenerates the current v1 release output and verifies the
// manifest/pin contract end-to-end.

#load "../ReleaseConfig.fsx"
#load "../../DistributionTestHelper.fsx"

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

let runDotnet arguments =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- repoRoot
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

    output.Result

let runScript path = runDotnet [ "fsi"; path ]

// Reproduce the F1 precondition: a clean plain Release build must not be able
// to feed a portable-debug assembly into packaging via incremental reuse.
runDotnet [ "clean"; "dotnet/Mcp.Dotnet.fsproj"; "-c"; "Release"; "--nologo" ] |> ignore
runDotnet [ "build"; "dotnet/Mcp.Dotnet.fsproj"; "-c"; "Release"; "--nologo" ] |> ignore

try
    Directory.CreateDirectory(Path.GetDirectoryName stalePublishPath) |> ignore
    File.WriteAllText(stalePublishPath, "stale staging output")
    runScript (Path.Combine(dotnet, "BuildDistribution.fsx")) |> ignore

    DistributionTestHelper.assertTrue "regeneration removes dotnet staging output" (not (File.Exists stalePublishPath))

    runScript (Path.Combine(dotnet, "PrepareReleasePins.fsx")) |> ignore
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

DistributionTestHelper.assertDistributionContract producer layout

// The packaged entry assembly must stay the no-debug output even though a
// plain Release build populated the intermediate outputs before packaging.
let packagedDllPath = Path.Combine(distributionDirectory, ReleaseConfig.entryDll)

let packagedDllHasNoDebugDirectory =
    use stream = File.OpenRead packagedDllPath
    use pe = new PEReader(stream)
    pe.ReadDebugDirectory().IsEmpty

DistributionTestHelper.assertTrue "packaged entry DLL carries no debug directory" packagedDllHasNoDebugDirectory

printfn "dotnet distribution contract passed: %s" archiveName
