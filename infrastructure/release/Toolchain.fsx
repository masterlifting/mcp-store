// role: release/build entrypoint
// Prints the four verified toolchain values, one `NAME=value` line each, for `Update.ps1 -Verify` to validate and hand over.
#load "../Failure.fs"
#load "../Provenance.fs"
#load "Toolchain.fs"

open System.IO
open SharedFailure

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

// Standalone entry bridge: the only synchronous wait in this script's flow.
let verified =
    match ReleaseToolchain.verifyLocal root |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn "MCP_RELEASE_VERIFIED_SDK=%s" verified.Toolchain.SdkVersion
printfn "MCP_RELEASE_VERIFIED_RUNTIME=%s" verified.Toolchain.RuntimeVersion
printfn "MCP_RELEASE_VERIFIED_TARGET_FRAMEWORK=%s" verified.TargetFramework
printfn "MCP_RELEASE_VERIFIED_LANGUAGE_VERSION=%s" verified.LangVersion
