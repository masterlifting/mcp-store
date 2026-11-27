// role: release/build entrypoint
#load "../../Pins.fsx"

open System.IO
open SharedFailure

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let configPath = Path.Combine(__SOURCE_DIRECTORY__, "Config.json")
let dist = Path.Combine(root, "infrastructure", "resolver", "dist")
let output = Path.Combine(dist, "consumer-pins.json")

let run () : Async<Result<DistributionPin.Pin, ReleaseError>> =
    // Only the canonical config path crosses this boundary: each FSI load chain compiles its own policy type.
    ReleasePins.writeConsumerPins
        root
        configPath
        dist
        output
        "run infrastructure/resolver/release/Build.fsx before preparing the resolver consumer pin"

// Standalone entry bridge: the only synchronous wait in this script's flow.
let pin =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn "resolver pin id=%s version=%s digest=%s" pin.Id pin.Version pin.Digest
