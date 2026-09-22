# Repository Guidance

## Owned MCP producers

- `dotnet/` owns the deterministic .NET build/test MCP producer.
- `workflow/` owns the deterministic workflow/task-state MCP producer.
- `telegram/` owns the Telegram MCP package.

The official GitHub MCP is an external dependency and is not produced or installed by this repository.

## Boundaries

- Producer code, package metadata, manifests, release tooling, and producer tests live here.
- OpenCode agents, skills, orchestration, capability selection, consumer launchers, and installed runtime layout do not live here.
- No migration/backward-compatibility aliases are maintained for retired Dotnet/Workflow product identities or persisted schemas.

## Validation

- Dotnet: `dotnet build dotnet/Mcp.Dotnet.fsproj -c Release` and the Expecto project under `dotnet/tests/Mcp.Dotnet.Tests/`.
- Workflow: `dotnet build workflow/Mcp.Workflow.fsproj -c Release` plus the F# script tests under `workflow/tests/`.
- Telegram: from `telegram/`, use `uv run pytest`; deterministic tests must not require live credentials.

## Security

Never read, print, commit, or transmit `.env`, Telegram session files, tokens, credentials, or private keys. Keep filesystem/path authorization fail-closed.
