# .NET verifier MCP distribution

This directory is the standalone producer boundary for the bounded .NET
verification MCP server. It owns the verifier implementation and can be
installed from an immutable `masterlifting/mcp-store` revision without an
OpenCode checkout.

## Consumer installation

Install this directory into the consumer's `mcp/mcp-store/dotnet`
checkout at the full producer revision recorded by the consumer descriptor.
The checkout must be detached at that revision; a branch or moving tag is not
an equivalent installation.

The executable is started with the .NET host injected as an absolute path:

```text
dotnet run --project Mcp.Verifier.fsproj --configuration Release --no-launch-profile --verbosity quiet -- --dotnet-host <absolute-dotnet-host>
```

The host path is intentionally supplied by the consumer. The verifier rejects
relative, non-canonical, workspace-local, missing, or reparse-point hosts.

The server exposes the fixed tools `verify_dotnet_build`,
`verify_dotnet_test`, and `verification_details` over stdio. The implementation
has no dependency on OpenCode APIs; OpenCode owns only its runtime descriptor
and tool-facing contracts.

## Producer tests

The deterministic F#-native implementation suite is an Expecto executable:

```text
dotnet run --project tests/Mcp.Verifier.Tests/Mcp.Verifier.Tests.fsproj --configuration Release
```

It targets `net11.0`, references `Mcp.Verifier.fsproj` directly, and owns the
verifier domain, authorization, process, quota, and MCP transport coverage.
