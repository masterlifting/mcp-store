// role: release/build entrypoint
#load "../../infrastructure/Distribution.fsx"
#load "../../infrastructure/Pins.fsx"

open System
open System.IO
open BuildProvenance

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let config = DistributionBuild.loadConfig (Path.Combine(__SOURCE_DIRECTORY__, "Config.json"))
let dist = Path.Combine(root, "dotnet", "dist")
let output = Path.Combine(dist, "consumer-pins.json")
let componentId = config.ComponentId
let archiveName = config.ArchiveName

let manifestPath = Path.Combine(dist, componentId, "distribution.json")
let archivePath = Path.Combine(dist, archiveName)

let run () : Async<Result<string * string * string, ReleaseError>> =
    if not (File.Exists archivePath) || not (File.Exists manifestPath) then
        async { return Error(MissingArtifact "run dotnet/release/Build.fsx before preparing the dotnet consumer pin") }
    else
        ReleasePins.writeConsumerPins root config manifestPath archivePath output

// Standalone entry bridge: the only synchronous wait in this script's flow.
let archiveSha256, manifestSha256, revision =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn
    "dotnet asset=%s archiveSha256=%s manifestSha256=%s revision=%s"
    archiveName
    archiveSha256
    manifestSha256
    revision
