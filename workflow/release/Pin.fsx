// role: release/build entrypoint
#load "../../infrastructure/Distribution.fsx"
#load "../../infrastructure/Pins.fsx"

open System
open System.IO
open SharedFailure
open BuildProvenance

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let configPath = Path.Combine(__SOURCE_DIRECTORY__, "Config.json")
let config = DistributionBuild.loadConfig configPath
let dist = Path.Combine(root, "workflow", "dist")
let output = Path.Combine(dist, "consumer-pins.json")
let componentId = config.ComponentId
let archiveName = config.ArchiveName

let manifestPath = Path.Combine(dist, componentId, "distribution.json")
let archivePath = Path.Combine(dist, archiveName)

let run () : Async<Result<string * string * string, ReleaseError>> =
    if not (File.Exists archivePath) || not (File.Exists manifestPath) then
        async { return Error(MissingArtifact "run workflow/release/Build.fsx before preparing the workflow consumer pin") }
    else
        // Only the canonical config path crosses this boundary: each FSI load chain compiles its own policy type.
        ReleasePins.writeConsumerPins root configPath manifestPath archivePath output

// Standalone entry bridge: the only synchronous wait in this script's flow.
let archiveSha256, manifestSha256, revision =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn
    "workflow asset=%s archiveSha256=%s manifestSha256=%s revision=%s"
    archiveName
    archiveSha256
    manifestSha256
    revision
