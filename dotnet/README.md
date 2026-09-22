# Dotnet MCP producer

`dotnet/` owns the deterministic, bounded .NET build/test MCP producer.

Canonical product identity:

```text
Mcp.Dotnet
Mcp.Dotnet.fsproj
Mcp.Dotnet.dll
```

The server exposes producer tools `build`, `test`, and `details`; an MCP consumer with server id `dotnet` therefore sees `dotnet_build`, `dotnet_test`, and `dotnet_details`.

The consumer supplies two startup inputs: an authorized absolute `--dotnet-host` and an absolute `--artifact-root`. The caller workspace remains the working directory for project authorization and project SDK/global.json semantics. The artifact root is external to the workspace, is created lazily on the first verification run, and is namespaced by a deterministic workspace identity.

The packaged MCP host requirement is independent of the SDK selected by the consumer project's `global.json`.

## Producer validation

```text
dotnet build dotnet/Mcp.Dotnet.fsproj -c Release
dotnet run --project dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj -c Release
dotnet fsi dotnet/BuildDistribution.fsx
dotnet fsi dotnet/PrepareReleasePins.fsx
dotnet fsi dotnet/tests/DistributionTests.fsx
```

The distribution is framework-dependent `net11.0`, uses deterministic allowlists/hashes, and contains no source, project, PDB, or apphost files.
