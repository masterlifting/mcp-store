// role: release/build entrypoint
#load "../../infrastructure/Distribution.fsx"

open System.IO
open BuildProvenance

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let config = DistributionBuild.loadConfig (Path.Combine(__SOURCE_DIRECTORY__, "Config.json"))

// The publication repository is not a free parameter: it is derived from the
// fixed release asset URI so the manifest claim and the pin cannot drift.
let sourceRepository =
    match DistributionContract.sourceRepositoryFromAssetUri config.AssetUri with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

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
          RuntimeVersion = config.RuntimeVersion
          Platform = config.Platform
          SourceRepository = sourceRepository
          EntryDll = config.EntryDll
          ArchiveName = config.ArchiveName
          PublishedFiles = config.PublishedFiles
          LaunchArguments = config.LaunchArguments
          ExtraPublishProperties = []
          Notice = "mcp-store Workflow MCP distribution\n" }

// Standalone entry bridge: the only synchronous wait in this script's flow.
let result =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn "workflow archive=%s sha256=%s manifest=%s" result.ArchiveName result.ArchiveSha256 result.ManifestSha256
