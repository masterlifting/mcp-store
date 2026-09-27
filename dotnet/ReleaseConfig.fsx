// Producer-local release policy for the dotnet distribution. Version, SDK, and
// runtime allowlist are owned here so build, pin, and test scripts share one
// source of truth without pulling in another producer's values. The file name
// supplies the implicit module name, so `#load` exposes these as ReleaseConfig.*.
let componentId = "dotnet"
let version = "1.0.3"
let entryDll = "Mcp.Dotnet.dll"
let sdkVersion = "11.0.100-rc.1.26425.128"

// The archive name encodes the version only; the producer identity already
// comes from the producer directory, the manifest `id`, the project/assembly
// identity, the release context, and the consumer descriptor.
let archiveName = $"v{version}.zip"
let assetUri =
    $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/v{version}.zip"

let publishedFiles =
    [ "FSharp.Core.dll"
      "Mcp.Dotnet.deps.json"
      "Mcp.Dotnet.dll"
      "Mcp.Dotnet.runtimeconfig.json" ]
