// role: release/build entrypoint
#load "../../Pins.fsx"

open System.IO
open SharedFailure

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let configPath = Path.Combine(__SOURCE_DIRECTORY__, "Config.json")
let dist = Path.Combine(root, "infrastructure", "resolver", "dist")
let output = Path.Combine(dist, "consumer-pins.json")

let run () : Async<Result<string * string * string * string, ReleaseError>> =
    // Only the canonical config path crosses this boundary: each FSI load chain compiles its own policy type.
    ReleasePins.writeConsumerPins
        root
        configPath
        dist
        output
        "run infrastructure/resolver/release/Build.fsx before preparing the resolver consumer pin"

// Standalone entry bridge: the only synchronous wait in this script's flow.
let archiveName, archiveSha256, manifestSha256, revision =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn
    "resolver asset=%s archiveSha256=%s manifestSha256=%s revision=%s"
    archiveName
    archiveSha256
    manifestSha256
    revision
