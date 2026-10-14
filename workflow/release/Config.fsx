// role: reusable module/helper
// Canonical per-producer release policy (component id, version, SDK, runtime, platform, archive name, asset URI, allowlist).
module ReleaseConfig

let componentId = "workflow"
// The published v1.0.4 ZIP is immutable, so this policy identifies the next release.
let version = "1.0.5"
let entryDll = "Mcp.Workflow.dll"
let sdkVersion = "11.0.100-rc.1.26425.128"
// Exact shared-framework build the distribution runs on; the workflow cross-checks this against the executing toolchain.
let runtimeVersion = "11.0.0-rc.1.26425.128"
let platform = "win-x64"

let archiveName = $"v{version}.zip"
let assetUri =
    $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/v{version}.zip"

let publishedFiles =
    [ "FSharp.Core.dll"
      "Mcp.Workflow.deps.json"
      "Mcp.Workflow.dll"
      "Mcp.Workflow.runtimeconfig.json" ]
