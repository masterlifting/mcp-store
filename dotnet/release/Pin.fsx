// role: release/build entrypoint
#load "../../infrastructure/Pins.fsx"

open System.IO
open SharedFailure

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let configPath = Path.Combine(__SOURCE_DIRECTORY__, "Config.json")
let dist = Path.Combine(root, "dotnet", "dist")
let output = Path.Combine(dist, "consumer-pins.json")

let run () : Async<Result<DistributionPin.Pin, ReleaseError>> =
    // Only the canonical config path crosses this boundary: each FSI load chain compiles its own policy type.
    ReleasePins.writeConsumerPins
        root
        configPath
        dist
        output
        "run dotnet/release/Build.fsx before preparing the dotnet consumer pin"

// Standalone entry bridge: the only synchronous wait in this script's flow.
let pin =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn "dotnet pin id=%s version=%s digest=%s" pin.Id pin.Version pin.Digest
