#load "ReleaseConfig.fsx"
#load "../DistributionBuild.fsx"

open System.IO

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

// Framework-dependent publish of this producer; the allowlist is the launcher
// contract and the shared builder rejects any extra publish output.
let result =
    DistributionBuild.build
        { Root = root
          ProjectPath = "workflow/Mcp.Workflow.fsproj"
          OutputRoot = Path.Combine(root, "workflow", "dist")
          ComponentId = ReleaseConfig.componentId
          Version = ReleaseConfig.version
          SdkVersion = ReleaseConfig.sdkVersion
          EntryDll = ReleaseConfig.entryDll
          ArchiveName = ReleaseConfig.archiveName
          PublishedFiles = ReleaseConfig.publishedFiles
          ExtraPublishProperties = []
          Notice = "mcp-store Workflow MCP distribution\n" }

printfn "workflow archive=%s sha256=%s manifest=%s" result.ArchiveName result.ArchiveSha256 result.ManifestSha256
