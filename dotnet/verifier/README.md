# .NET verifier MCP distribution

This directory is the standalone producer boundary for the bounded .NET
verification MCP server. It owns the verifier implementation and publishes the
`dotnet-verifier` framework-dependent `net11.0` runtime distribution. Consumers
do not build or run this source checkout at runtime.

## Consumer installation

Provision the release asset named by the consumer descriptor into its pinned
`dotnet-verifier` install path. Provisioning validates the archive SHA-256 and
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
dotnet run --project tests/Mcp.Verifier.Tests/Mcp.Verifier.Tests.fsproj --configuration Release
```

Run the command from this directory. It targets `net11.0`, references
`Mcp.Verifier.fsproj` directly, and owns the verifier domain, authorization,
process, quota, and MCP transport coverage.

## Producer packaging

From the repository root, build and pin the immutable `dotnet-verifier-v0.2.0`
release with:

```text
dotnet fsi dotnet/verifier/BuildDistribution.fsx
```

The scripts stage only runtime files under `dotnet/verifier/dist/`, emit the
`dotnet-verifier` manifest and pin, and preserve older release artifacts.
