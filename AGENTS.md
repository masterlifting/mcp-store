# Repository Guidance

## Owned MCP producers

- `dotnet/` owns the deterministic .NET build/test/details MCP producer (`Mcp.Dotnet`).
- `workflow/` owns the deterministic workflow/task-state MCP producer (`Mcp.Workflow`).
- `telegram/` owns the Telegram MCP package (`telegram-mcp`). Telegram-only changes do not run the .NET release or Unix qualification workflows (`paths-ignore: ['telegram/**']`); that exclusion is not a Telegram CI.

`infrastructure/resolver/` owns the Resolver, which is also a release participant (`resolver`) but a CLI release artifact rather than an MCP producer: its manifest uses the additive `dotnet-cli` launch kind (schema 2 is unchanged), and the Resolver refuses a `dotnet-cli` manifest as an MCP launch target. Release containers keep asset-level no-clobber; they are not GitHub-immutable releases.

The official GitHub MCP is an external dependency and is not produced or installed by this repository.

## Boundaries

- Producer code, package metadata, manifests, release tooling, package gates, and producer documentation live here.
- Consumer agents, orchestration, capability selection, launchers, and installed runtime layouts do not live here.
- No migration/backward-compatibility aliases are maintained for retired Dotnet/Workflow product identities or persisted schemas.
- Consumers own their integrations and consume published releases through `dotnet/dist/consumer-pins.json` and `workflow/dist/consumer-pins.json`.

## Validation

- The repository carries no automated behavioral test suite. Do not add automated tests, test projects, test frameworks or test-only CI phases without explicit, specific approval for each addition.
- Dotnet: build `dotnet/Dotnet.fsproj` in Release; run the package gate `dotnet fsi dotnet/tests/distribution/Run.fsx`.
- Workflow: `dotnet build workflow/Workflow.fsproj -c Release`; run the package gate `dotnet fsi workflow/tests/distribution/Run.fsx`.
- Resolver: `dotnet build infrastructure/resolver/Resolver.fsproj -c Release`; run the package gate `dotnet fsi infrastructure/resolver/tests/distribution/Run.fsx`.
- The package gates run in clean-tree release preparation and verify archive, manifest, pin and byte contracts; they are not behavioral suites.
- Release tooling: `pwsh infrastructure/release/Check.ps1 -Components '["dotnet","workflow","resolver"]'` and `pwsh infrastructure/release/Typecheck.ps1`.
- Manual smoke: extract each MCP distribution and start it through the Resolver contract by hand; this is not automated.
- Telegram: lint and format checks only (see below).

## Telegram package

- Requires Python 3.10+ and uses `uv` (`telegram/pyproject.toml`, `telegram/uv.lock`).
- From `telegram/`, run `uv sync`.
- Checks: `uv run black --check .`, `uv run flake8 .`, and `uv run pre-commit run --all-files`.
- The package has no automated tests; do not add any without explicit, specific approval.

## Security

- Never read, print, commit, or transmit `.env`, Telegram session files, tokens, credentials, or private keys. Use `.env.example` and dummy values for local validation.
- The Telegram HTTP endpoint is unauthenticated; bind it to localhost unless an explicit security decision authorizes otherwise.
- Workflow task persistence is forward-only schema 3: every persisted task requires a destination-bound integrity receipt; schema 2 is rejected without migration or rewrite. Ordinary `add-decision` cannot manufacture User authority; trusted native confirmation remains a separate host-only path. Existing installed schema-2 task state is not migrated by source changes.
