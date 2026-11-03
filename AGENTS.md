# Repository Guidance

## Owned MCP producers

- `dotnet/` owns the deterministic .NET build/test/details MCP producer (`Mcp.Dotnet`).
- `workflow/` owns the deterministic workflow/task-state MCP producer (`Mcp.Workflow`).
- `telegram/` owns the Telegram MCP package (`telegram-mcp`).

The official GitHub MCP is an external dependency and is not produced or installed by this repository.

## Boundaries

- Producer code, package metadata, manifests, release tooling, producer tests, and producer documentation live here.
- Consumer agents, orchestration, capability selection, launchers, and installed runtime layouts do not live here.
- No migration/backward-compatibility aliases are maintained for retired Dotnet/Workflow product identities or persisted schemas.
- Consumers own their integrations and consume published releases through `dotnet/dist/consumer-pins.json` and `workflow/dist/consumer-pins.json`.

## Validation

- Dotnet: build `dotnet/Dotnet.fsproj` and `dotnet/tests/Tests.fsproj` in Release; the compiled Expecto suite is owned separately from its deferred FSI scripts.
- Workflow: `dotnet build workflow/Workflow.fsproj -c Release` plus the F# script tests under `workflow/tests/`.
- Telegram: from `telegram/`, use `uv run pytest`; deterministic tests must not require live credentials.

## Telegram package

- Requires Python 3.10+ and uses `uv` (`telegram/pyproject.toml`, `telegram/uv.lock`).
- From `telegram/`, run `uv sync`, then use `uv run pytest` for tests. Coverage: `uv run pytest --cov --cov-report=term-missing --cov-report=xml`.
- Checks: `uv run black --check .`, `uv run flake8 .`, and `uv run pre-commit run --all-files`.
- Add or update deterministic tests for behavior changes; do not depend on live Telegram credentials.

## Security

- Never read, print, commit, or transmit `.env`, Telegram session files, tokens, credentials, or private keys. Use `.env.example` and dummy values for local validation.
- The Telegram HTTP endpoint is unauthenticated; bind it to localhost unless an explicit security decision authorizes otherwise.
- Workflow task persistence is forward-only schema 3: every persisted task requires a destination-bound integrity receipt; schema 2 is rejected without migration or rewrite. Ordinary `add-decision` cannot manufacture User authority; trusted native confirmation remains a separate host-only path. Existing installed schema-2 task state is not migrated by source changes.
