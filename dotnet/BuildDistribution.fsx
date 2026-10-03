#load "ReleaseConfig.fsx"
#load "../DistributionBuild.fsx"

open System.IO
open BuildProvenance

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

// Framework-dependent publish of this producer; the allowlist is the runtime
// contract and the shared builder rejects any extra publish output.
let run () =
    DistributionBuild.build
        { Root = root
          ProjectPath = "dotnet/Mcp.Dotnet.fsproj"
          OutputRoot = Path.Combine(root, "dotnet", "dist")
          ComponentId = ReleaseConfig.componentId
          Version = ReleaseConfig.version
          SdkVersion = ReleaseConfig.sdkVersion
          EntryDll = ReleaseConfig.entryDll
          ArchiveName = ReleaseConfig.archiveName
          PublishedFiles = ReleaseConfig.publishedFiles
          ExtraPublishProperties = [ "-p:GenerateDocumentationFile=false" ]
          Notice = "mcp-store dotnet MCP distribution\n" }

// Standalone entry bridge: the only synchronous wait in this script's flow.
let result =
    match run () |> Async.RunSynchronously with
    | Ok value -> value
    | Error error -> failwith (ReleaseError.message error)

printfn "dotnet archive=%s sha256=%s manifest=%s" result.ArchiveName result.ArchiveSha256 result.ManifestSha256
