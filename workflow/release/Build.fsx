// role: release/build entrypoint
#load "../../infrastructure/Distribution.fsx"

open System.IO
open BuildProvenance

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let config = DistributionBuild.loadConfig (Path.Combine(__SOURCE_DIRECTORY__, "Config.json"))

// Framework-dependent publish of this producer; the allowlist is the launcher
// contract and the shared builder rejects any extra publish output.
let run () =
    DistributionBuild.build
        { Root = root
          ProjectPath = "workflow/Mcp.Workflow.fsproj"
          OutputRoot = Path.Combine(root, "workflow", "dist")
          ComponentId = config.ComponentId
          Version = config.Version
          SdkVersion = config.SdkVersion
          EntryDll = config.EntryDll
          ArchiveName = config.ArchiveName
          PublishedFiles = config.PublishedFiles
          ExtraPublishProperties = []
          Notice = "mcp-store Workflow MCP distribution\n" }

// Standalone entry bridge: the only synchronous wait in this script's flow.
let result =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn "workflow archive=%s sha256=%s manifest=%s" result.ArchiveName result.ArchiveSha256 result.ManifestSha256
