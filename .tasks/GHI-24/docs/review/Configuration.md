# Canonical producer configuration

The authoritative policy files are `dotnet/release/Config.json` and
`workflow/release/Config.json`. Each is a UTF-8 JSON object containing exactly
these fields:

| Field | Type | Meaning |
| --- | --- | --- |
| `componentId` | string | Enrolled producer identity (`dotnet` or `workflow`) |
| `version` | string | Immutable release version; no automatic increment |
| `entryDll` | string | Runtime entry assembly |
| `sdkVersion` | string | Required SDK version |
| `runtimeVersion` | string | Required shared runtime version |
| `platform` | string | Producer platform |
| `archiveName` | string | Canonical versioned archive filename |
| `assetUri` | string | Fixed GitHub release asset URI |
| `publishedFiles` | string array | Exact runtime distribution allowlist |

The publisher's `ReleasePolicy.parse` consumes bounded source bytes without
executing a script. It rejects invalid UTF-8/JSON, embedded NUL, duplicate or
unknown properties, missing properties, incorrect JSON types, and oversized
input. `SourceSha256` is computed over the original JSON bytes. The producer
build and pin entrypoints use `DistributionBuild.loadConfig` from
`infrastructure/Distribution.fsx`; their script inputs, roster `configPath`
values, and release runner projection all name the same `Config.json` files.

Current immutable policy values are Dotnet `1.0.4` and Workflow `1.0.5`;
both use SDK `11.0.100-rc.1.26425.128`, runtime `11.0.0-rc.1.26425.128`, and
platform `win-x64`. Each archive is `v<version>.zip`; the asset URI is the
fixed `masterlifting/mcp-store/releases/download/<componentId>/<archiveName>`
location. There is no independent version table or compatibility F# config.
