// role: release/build entrypoint
#load "../../infrastructure/Pins.fsx"

open System.IO
open SharedFailure

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let configPath = Path.Combine(__SOURCE_DIRECTORY__, "Config.json")
let dist = Path.Combine(root, "dotnet", "dist")
let output = Path.Combine(dist, "consumer-pins.json")

let run () : Async<Result<string * string * string * string, ReleaseError>> =
    // Only the canonical config path crosses this boundary: each FSI load chain compiles its own policy type.
    ReleasePins.writeConsumerPins
        root
        configPath
        dist
        output
        "run dotnet/release/Build.fsx before preparing the dotnet consumer pin"

// Standalone entry bridge: the only synchronous wait in this script's flow.
let archiveName, archiveSha256, manifestSha256, revision =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn
    "dotnet asset=%s archiveSha256=%s manifestSha256=%s revision=%s"
    archiveName
    archiveSha256
    manifestSha256
    revision
