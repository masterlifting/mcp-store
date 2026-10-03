#load "ReleaseConfig.fsx"
#load "../ReleasePins.fsx"

open System
open System.IO
open BuildProvenance

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let dist = Path.Combine(root, "dotnet", "dist")
let output = Path.Combine(dist, "consumer-pins.json")
let componentId = ReleaseConfig.componentId
let version = ReleaseConfig.version
let archiveName = ReleaseConfig.archiveName
let assetUri = ReleaseConfig.assetUri

let manifestPath = Path.Combine(dist, componentId, "distribution.json")
let archivePath = Path.Combine(dist, archiveName)

let run () : Async<Result<string * string * string, ReleaseError>> =
    if not (File.Exists archivePath) || not (File.Exists manifestPath) then
        async { return Error(MissingArtifact "run dotnet/BuildDistribution.fsx before preparing dotnet v1 pins") }
    else
        releaseResult {
            do!
                ReleasePins.validateManifestIdentity
                    manifestPath
                    [ "id", componentId; "version", version; "archive", archiveName ]

            return!
                ReleasePins.writeConsumerPins root manifestPath archivePath output componentId archiveName assetUri
        }

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
