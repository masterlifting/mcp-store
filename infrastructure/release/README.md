# `infrastructure/release`

The compiled release runner separates producer preparation from publication. Producer
build, packaging, and tests run only during preparation with publication credentials
removed; publication validates data and then uses the existing single transaction.

## CLI

```text
ReleaseRunner prepare <Config.json> <outdir>
ReleaseRunner publish <Config.json> <candidate-directory> --workspace-root=<trusted-checkout> --event-sha=<exactHEAD> --repo=<masterlifting/mcp-store>
```

The publish flags are required, unique, and the only accepted flags. The repository
is fixed to `masterlifting/mcp-store`. The trusted checkout must resolve to the exact
event HEAD. Candidate input is a data-only directory containing exactly the producer
archive and standalone `distribution.json`; candidate members are never executed.

Preparation writes only those two files. Publication reads the canonical roster and
producer policy from the trusted checkout, validates candidate archive membership,
inner archive structure, manifest identity, and event revision before requiring the
exact `MCP_RELEASE_DO_UPLOAD=true` opt-in and `GITHUB_TOKEN`. There is no local-mirror
fallback. Remote archive and metadata observations, immutable assets, and recovery
remain owned by `ReleaseEvaluate` and `ReleaseTransaction`.

The project compile order is explicit in `Release.fsproj`, and the pre-SDK toolchain
projection is plain PowerShell over each producer's canonical `Config.json`, so nothing
here evaluates F# before an SDK exists. Once the SDK is available, producer F# scripts
reuse the compiled `ReleasePolicy` parser; PowerShell remains the minimal bootstrap
projection because it must run before that parser can be built.

## Retained scripts

- `Update.ps1 -Project` / `-Verify` — canonical toolchain projection and executing-toolchain check.
- `Coherence.ps1 -Components <projection>` — cross-stage guard over `release.yml`, the pinned action transfers and the enrollment roster.
- `Verify.ps1 -Root <dir> -InventoryJson <json>` / `-MembersJson <json>` — deterministic membership, path and SHA-256 proof of a downloaded artifact; it reads no network, token or receipt state, so it runs before anything from the verified set executes.
- `Typecheck.ps1 -SdkVersion <sdk>` — strict FCS typecheck of the retained producer scripts.
- `Suite.ps1` — the complete compiled release regression gate.
