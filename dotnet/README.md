# Mcp.Dotnet producer

This directory is the standalone producer boundary for the bounded .NET
build/test MCP server. It owns the Dotnet producer implementation and publishes the
`dotnet` v1.0.3 framework-dependent `net11.0` runtime distribution. Earlier
historical archives remain on disk for inspection; the producer does not
preserve, migrate, or alias them. Consumers do not build or run this source
checkout at runtime.

## Consumer installation

Provision the release asset named by the consumer descriptor into its pinned
`dotnet` install path. Provisioning validates the archive SHA-256 and
`distribution.json` manifest SHA-256 before replacing the destination.

The executable is started with the .NET host injected as an absolute path:

```text
dotnet exec Mcp.Dotnet.dll --dotnet-host <absolute-dotnet-host> --artifact-root <absolute path>
```

The host path is intentionally supplied by the consumer. The producer rejects
relative, non-canonical, workspace-local, missing, or reparse-point hosts.

The producer requires an explicit `artifactRoot` supplied by the consumer. It
must be a local absolute path; the producer does not derive any
OpenCode-specific path. Roots are canonicalized and rejected when they or their
ancestors are reparse points. Per-run isolation, aggregate and per-artifact
quotas, retention, and session cleanup apply identically to external roots.

The MCP host/runtime startup prerequisite is consumer-provisioned and is
independent of project SDK selection. Project build/test runs use the normal
workspace or project SDK selection; the producer neither pins nor overrides it.

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

From the repository root, build and pin the corrected immutable `v1.0.3.zip`
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
build output, PDB, and apphost files are rejected. The archive filename
encodes the version only (`v1.0.3.zip`); the producer identity already comes
from the producer directory, the manifest `id`, the project/assembly
identity, the release context, and the consumer descriptor. Packaging refuses
to run from a dirty source tree and stamps each manifest with the clean
`HEAD` revision it built from.

The manifest uses deterministic arrays: `files` contains `{ "path", "sha256" }`
objects for the runtime allowlist. `path` is an exact slash-separated,
repository-relative archive path matching the OpenCode v1 consumer contract;
paths are unique case-insensitively and each payload hash is lowercase SHA-256.
`archiveFiles` contains the complete archive entry-name allowlist.
