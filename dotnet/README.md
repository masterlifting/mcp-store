# .NET MCP distribution

This directory is the standalone producer boundary for the bounded .NET
verification MCP server. It owns the verifier implementation and publishes the
`dotnet` v1.0.1 framework-dependent `net11.0` runtime distribution. The
v1.0.0 archive remains an immutable historical release with the original
manifest-schema failure. Consumers do not build or run this source checkout at
runtime.

## Consumer installation

Provision the release asset named by the consumer descriptor into its pinned
`dotnet` install path. Provisioning validates the archive SHA-256 and
`distribution.json` manifest SHA-256 before replacing the destination.

The executable is started with the .NET host injected as an absolute path:

```text
dotnet exec Mcp.Verifier.dll --dotnet-host <absolute-dotnet-host>
```

The host path is intentionally supplied by the consumer. The verifier rejects
relative, non-canonical, workspace-local, missing, or reparse-point hosts.

The server exposes the fixed tools `verify_dotnet_build`,
`verify_dotnet_test`, and `verification_details` over stdio. The implementation
has no dependency on OpenCode APIs; OpenCode owns only its consumer descriptor,
launcher, and tool-facing contracts.

## Producer tests

The deterministic F#-native implementation suite is an Expecto executable:

```text
dotnet run --project tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj --configuration Release
```

Run the command from this directory. It targets `net11.0`, references
`Mcp.Dotnet.fsproj` directly, and owns the verifier domain, authorization,
process, quota, and MCP transport coverage.

## Producer packaging

From the repository root, build and pin the corrected immutable `dotnet-v1.0.1.zip`
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
stored `dotnet-verifier-v0.2.0` release outputs under `dotnet/verifier/dist/`.

The manifest uses deterministic arrays: `files` contains `{ "path", "sha256" }`
objects for the runtime allowlist. `path` is an exact slash-separated,
repository-relative archive path matching the OpenCode v1 consumer contract;
paths are unique case-insensitively and each payload hash is lowercase SHA-256.
`archiveFiles` contains the complete archive entry-name allowlist.
