// role: release/build entrypoint
#load "../../Distribution.fsx"

open System.IO
open SharedFailure
open BuildProvenance

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let config = DistributionBuild.loadConfig (Path.Combine(__SOURCE_DIRECTORY__, "Config.json"))

// Derive the publication repository from the fixed asset URI to prevent manifest and pin drift.
let sourceRepository =
    match DistributionContract.sourceRepositoryFromAssetUri config.AssetUri with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

// The allowlist defines runtime output and rejects extra publish files. The Resolver is a CLI artifact: it declares no MCP launch arguments.
let run () =
    DistributionBuild.build
        { Root = root
          ProjectPath = "infrastructure/resolver/Resolver.fsproj"
          OutputRoot = Path.Combine(root, "infrastructure", "resolver", "dist")
          ComponentId = config.ComponentId
          Version = config.Version
          SdkVersion = config.SdkVersion
          RuntimeVersion = config.RuntimeVersion
          Platform = config.Platform
          SourceRepository = sourceRepository
          EntryDll = config.EntryDll
          ArchiveName = config.ArchiveName
          PublishedFiles = config.PublishedFiles
          LaunchKind = config.LaunchKind
          LaunchArguments = config.LaunchArguments
          ExtraPublishProperties = [ "-p:GenerateDocumentationFile=false" ]
          Notice = "mcp-store resolver CLI distribution\n" }

// Standalone entry bridge: release work is asynchronous.
let result =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn "resolver archive=%s sha256=%s manifest=%s" result.ArchiveName result.ArchiveSha256 result.ManifestSha256
