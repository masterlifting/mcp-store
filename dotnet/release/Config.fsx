// role: reusable module/helper
// Producer-local release policy for the dotnet distribution. Version, SDK, and
// runtime allowlist are owned here so build, pin, and test scripts share one
// source of truth without pulling in another producer's values. Declares the
// ReleaseConfig module loaded by this producer's release entrypoints.
module ReleaseConfig

let componentId = "dotnet"
// Version bump 1.0.3 -> 1.0.4 (GHI-24 implementation PR). Rationale: byte
// comparison against the published v1.0.3 archive
// (Temp/opencode/ghi-24-bootstrap/dotnet-v1.0.3.zip,
//  da3acb20007f8015e541ee7d68b48adf49d232c4eea41190a62073073d1a1614)
// shows Mcp.Dotnet.dll CRC32 differs from the post-#21/#23/#26/#27 refactor
// binary, so the strict revision-only exception cannot apply. The bump is
// authored here exactly so the build stamps it into the manifest; CI never
// auto-increments.
let version = "1.0.4"
let entryDll = "Mcp.Dotnet.dll"
let sdkVersion = "11.0.100-rc.1.26425.128"
// Canonical toolchain selection for reproducible packaging. The release
// workflow projects these literals before installing an SDK, then verifies the
// executing SDK/runtime/platform against them; no workflow YAML or root
// global.json is permitted to restate them. `platform` is the rid-style
// `os-arch` moniker and `runtimeVersion` is the exact shared-framework build the
// distribution runs on.
let runtimeVersion = "11.0.0-rc.1.26425.128"
let platform = "win-x64"

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
