# Mcp.Dotnet producer

This directory is the standalone producer boundary for the bounded .NET
build/test MCP server. It owns the Dotnet producer implementation and publishes the
`dotnet` v1.0.2 framework-dependent `net11.0` runtime distribution. The
v1.0.0 archive remains an immutable historical release with the original
manifest-schema failure. Consumers do not build or run this source checkout at
runtime.

## Consumer installation

Provision the release asset named by the consumer descriptor into its pinned
`dotnet` install path. Provisioning validates the archive SHA-256 and
`distribution.json` manifest SHA-256 before replacing the destination.

The executable is started with the .NET host injected as an absolute path:

```text
dotnet exec Mcp.Dotnet.dll --dotnet-host <absolute-dotnet-host> [--artifact-root <path>]
```

The host path is intentionally supplied by the consumer. The producer rejects
relative, non-canonical, workspace-local, missing, or reparse-point hosts.

The producer may be configured with an explicit `artifactRoot` outside the
workspace. Relative roots remain workspace-contained; external roots must be
local absolute paths. Roots are canonicalized and rejected when they or their
ancestors are reparse points. Per-run isolation, aggregate and per-artifact
quotas, retention, and session cleanup apply identically to external roots.

Child SDK commands run from a private temporary launch directory containing an
empty `global.json`, so a workspace `global.json` cannot select the producer's
SDK. Project and solution targets remain authorized against the workspace.

The server exposes the fixed tools `build`, `test`, and `details` over stdio.
The implementation has no dependency on OpenCode APIs; OpenCode owns only its
consumer descriptor, launcher, and tool-facing contracts.

## Producer tests

The deterministic F#-native implementation suite is an Expecto executable:

```text
dotnet run --project tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj --configuration Release
```

Run the command from this directory. It targets `net11.0`, references
`Mcp.Dotnet.fsproj` directly, and owns the dotnet build/test domain, authorization,
process, quota, and MCP transport coverage.

## Producer packaging

From the repository root, build and pin the corrected immutable `dotnet-v1.0.2.zip`
release with:

```text
dotnet fsi dotnet/BuildDistribution.fsx
dotnet fsi dotnet/PrepareReleasePins.fsx
dotnet fsi dotnet/tests/DistributionTests.fsx
```

The scripts stage only the allowlisted runtime files under
`dotnet/dist/dotnet/`. `distribution.json` records the exact archive
allowlist and a lowercase SHA-256 for every runtime file. The generated archive
contains the runtime files, `NOTICE.txt`, and the manifest; source, project,
build output, PDB, and apphost files are rejected. The v1 scripts preserve the
stored `dotnet-v1.0.0.zip` historical release under `dotnet/dist/`.

The manifest uses deterministic arrays: `files` contains `{ "path", "sha256" }`
objects for the runtime allowlist. `path` is an exact slash-separated,
repository-relative archive path matching the OpenCode v1 consumer contract;
paths are unique case-insensitively and each payload hash is lowercase SHA-256.
`archiveFiles` contains the complete archive entry-name allowlist.
