# `infrastructure/release`

The compiled release runner separates producer preparation from publication. Producer
build, packaging, and the package gates run only during preparation with publication
credentials removed; publication validates data and then uses the existing single transaction.

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

The project compile order is explicit in `Release.fsproj`, and that ordered
`<Compile Include>` list is the single authored source list: the release workflow
stages the publisher from it verbatim. It compiles the shared, pure distribution
contract first — `infrastructure/distribution/Contract.fs` (manifest types, strict
parser/renderer, semantic validation, bounded root resolution) and
`infrastructure/distribution/Pin.fs` (canonical consumer pin / release metadata) —
then `Policy.fs`, which parses a producer's canonical `Config.json` and nothing
else; the archive name and asset URI derive there from the validated component id and
version and the trusted Store repository, so `Config.json` declares neither.
`ReleaseCompare.contractFromPolicy` projects that policy into the trusted
`ComponentContract` so identity, member allowlist, source repository, launch
descriptor, and build provenance are re-derived rather than re-implemented by each
caller. See `infrastructure/distribution/README.md` for the schema and its
invariants.

## Participants and launch kind

`Participants.json` is an enrollment-only roster of `dotnet`, `workflow` and
`resolver`, in that order so producer publication order is unchanged. Enrollment is a
closed supported set, not open-ended, and the roster may not carry policy values
such as `launchKind`. `resolver` is a release artifact, not an MCP producer: its
`infrastructure/resolver/release/Config.json` declares the `dotnet-cli` launch kind
with no arguments, while the producers declare `dotnet-exec`. The kind comes from the
per-component policy, not from a hardcoded value. Component roots are derived from
each participant's `configPath`, so a nested location such as `infrastructure/resolver/`
needs no `<componentId>/release` layout.

Each participant publishes into its own release container and tag, and the `resolver`
container must exist before its first publication. Store's guarantee is asset-level:
an asset is never overwritten and a same-version byte change is rejected. The
containers are not GitHub-immutable releases, because the publisher refuses to publish
into an immutable one. No Resolver artifact has been qualified or published yet.

## Toolchain owners

One .NET toolchain version is authored: the SDK version in the root `global.json`
(`rollForward: disable`, `allowPrerelease: false`). Nothing else states a version.

| Selection | Owner |
| --- | --- |
| SDK version | root `global.json` (`rollForward: disable`, `allowPrerelease: false`); the pre-SDK projection reads only what installation needs, and the SDK selected by the native resolver must equal it |
| TFM, F# language version | root `Directory.Build.props`; `Release.fsproj` carries no copy of the TFM or language version |
| runtime version | never authored; the evaluated SDK's `DefaultRuntimeFrameworkVersion` of the single `KnownFrameworkReference` for `Microsoft.NETCore.App` at the evaluated TFM |
| platform | never authored per component; the host-observed RID is the manifest provenance, and `Update.ps1 -Verify` holds the single publisher qualification (`win-x64`) |

`*/release/Config.json` therefore remains the single source only for the per-component
policy; it declares none of `sdkVersion`, `runtimeVersion` or `platform`, and each is rejected.
There is no second SDK, TFM or runtime reader: after SDK setup the values come from native
MSBuild evaluation of `Release.fsproj` at the repository root, with no parsing of
`dotnet --info` and no runtime computed from the TFM string. The one evaluator is
`Toolchain.fs`; `Toolchain.fsx` is its thin FSI entry with a single synchronous bridge,
and preparation and packaging reuse the same module. The owner read takes only
`sdk.version` (exact stable), `rollForward: disable` and `allowPrerelease: false`,
rejects a repeated authoritative key and allows unrelated valid fields.

`Update.ps1 -Verify`, run after the SDK is installed, invokes `Toolchain.fsx`, which
performs that evaluation and prints exactly four values that PowerShell validates before
accepting them (`NETCoreSdkVersion`, `TargetFramework`, `LangVersion`, `BundledNETCoreAppTargetFramework`
and the `KnownFrameworkReference` items). It fails closed unless the selected SDK equals
the owner, the evaluated TFM and language version are plain `netX.Y` and `X.Y` tokens,
the SDK-bundled TFM equals the evaluated one, and exactly one matching framework
reference exists. It then checks that an installed `Microsoft.NETCore.App` is compatible
with the derived runtime (same major.minor, patch at least the derived value; a
prerelease never satisfies a stable value). Only after every check passes it writes the
verified handoff to `GITHUB_ENV`: `MCP_RELEASE_VERIFIED_SDK`,
`MCP_RELEASE_VERIFIED_RUNTIME`, `MCP_RELEASE_VERIFIED_PLATFORM`, `MCP_RELEASE_VERIFIED_TARGET_FRAMEWORK` and
`MCP_RELEASE_VERIFIED_LANGUAGE_VERSION`. `-Project` never writes this handoff.
`publish` consumes the verified values, fails closed when they are missing or invalid,
reads the on-disk owner independently and requires it to equal the verified SDK, and runs
no discovery or evaluation, so no such process runs while the publication credential
is present. `Check.ps1` asserts that `-Verify` precedes the publish invocation and the
`GITHUB_TOKEN` step.

The isolated publisher stage imports no repository build files. The workflow generates
its project from the ordered `<Compile Include>` items of `Release.fsproj`, injects only
the verified `TargetFramework` and `LangVersion` values (validated again as plain tokens),
keeps the stage outside the checkout and restores and publishes with
`ImportDirectoryBuildProps` and `ImportDirectoryBuildTargets` set to `false`.

On the candidate path the manifest's declared `producer.runtime` must equal the runtime
derived from the packaged bytes; observation of already published archives is unchanged.

The pre-SDK toolchain projection is plain PowerShell over `global.json` and each
producer's canonical `Config.json`, so `-Project` evaluates no F# before an SDK exists.
It emits the SDK version and per-component values but **no runtime version**,
because the runtime is a property of the SDK, which does not exist yet. Once the SDK is
available, producer F# scripts reuse the compiled `ReleasePolicy` parser;
PowerShell remains the minimal bootstrap projection because it must run before that
parser can be built.

## Retained scripts

- `Update.ps1 -Project` / `-Verify` — canonical toolchain projection and executing-toolchain check.
- `Check.ps1 -Components <projection>` — cross-stage guard over `release.yml`, the pinned action transfers and the enrollment roster.
- `Verify.ps1 -Root <dir> -InventoryJson <json>` / `-MembersJson <json>` — deterministic membership, path and SHA-256 proof of a downloaded artifact; it reads no network, token or receipt state, so it runs before anything from the verified set executes.
- `Toolchain.fsx` — credential-free FSI entry that prints the four verified toolchain values for `Update.ps1 -Verify`.
- `Typecheck.ps1 -SdkVersion <sdk>` — strict FCS typecheck of each enrolled component's `Build.fsx`, `Pin.fsx` and `tests/distribution/Run.fsx`, plus `Toolchain.fsx` and the Resolver `Smoke.fsx`.

Preparation runs, per component and in order, `Build.fsx`, `Pin.fsx` and then the
`tests/distribution/Run.fsx` package gate (reported as phase `tests` on failure). The gate only
reads the freshly built and pinned `dist`; it never builds or regenerates, and the workflow
carries no separate gate loop. After every candidate is prepared and before any candidate upload,
preparation runs `infrastructure/resolver/tests/distribution/Smoke.fsx` on the final
regenerated Resolver distribution, after confirming the candidate archive bytes equal the
distribution archive the smoke reads. The publisher job builds the producers and the Resolver and runs
`Check.ps1` and `Typecheck.ps1`. The repository carries no automated behavioral test suite;
adding automated tests requires explicit, specific approval.
