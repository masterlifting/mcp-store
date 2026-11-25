# MCP store

The clean-slate producer set contains exactly these MCP producers:

| Producer | Source boundary | Release/runtime identity |
| --- | --- | --- |
| `Mcp.Dotnet` | `dotnet/` | `dotnet` distribution, `Mcp.Dotnet.dll` |
| `Mcp.Workflow` | `workflow/` | `workflow` distribution, `Mcp.Workflow.dll` |
| `Telegram` | `telegram/` | `telegram-mcp` Python package, `telegram` MCP server |

Dotnet and Workflow publish deterministic framework-dependent distributions.
Telegram is the Python producer and is tested from its `telegram/` boundary with
`uv`. Telegram-only changes do not run the .NET release or Unix qualification
workflows; that exclusion is not a Telegram CI.

The Resolver is a third release participant but not an MCP producer: it is published
through the same release transaction as a CLI-only artifact with the `dotnet-cli`
launch kind, an additive kind inside manifest schema 2. A consumer must verify its pin
and archive before first executing it, and the Resolver does not install itself.

## Installer and resolver

`infrastructure/resolver/` owns the single harness-independent install,
verification, and launch boundary for the owned distributions, and Dotnet and
Workflow both resolve through it. A consumer declares the exact immutable pin
published in `<producer>/dist/consumer-pins.json` and reaches the MCP through that
boundary instead of downloading, verifying, installing, or composing the process
itself:

```text
dotnet exec <absolute>/Resolver.dll resolve --pin <absolute pin file>
dotnet exec <absolute>/Resolver.dll run --pin <absolute pin file> --workspace <absolute directory> --host <absolute dotnet> --input <name>=<value>...
```

Installation goes to the canonical user-local root (`%LOCALAPPDATA%\mcp-store` on
Windows, `$XDG_DATA_HOME` or `~/.local/share` on Linux, `~/Library/Application
Support` on macOS), never to a repository, harness, or workspace directory.
`infrastructure/resolver/README.md` documents the CLI, the resolution snapshot
schema, retention and cleanup, and the current limitation list.

Manifest, pin, archive, and failure contracts are shared by the resolver and
release producer in `infrastructure/distribution/` and `infrastructure/Failure.fs`;
`infrastructure/release/` owns publishing only.
