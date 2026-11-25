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

The project compile order is explicit in `Release.fsproj`, and that ordered
`<Compile Include>` list is the single authored source list: the release workflow
stages the publisher from it verbatim. It compiles the shared, pure distribution
contract first — `infrastructure/distribution/Contract.fs` (manifest types, strict
parser/renderer, semantic validation, bounded root resolution) and
`infrastructure/distribution/Pin.fs` (canonical consumer pin / release metadata) —
then `Policy.fs`, which parses a producer's canonical `Config.json` and nothing
else. `ReleaseCompare.contractFromPolicy` projects that policy into the trusted
`ComponentContract` so identity, member allowlist, source repository, launch
descriptor, and build provenance are re-derived rather than re-implemented by each
caller. See `infrastructure/distribution/README.md` for the schema and its
invariants.

## Toolchain owners

One .NET toolchain version is authored: the SDK version in the root `global.json`
(`rollForward: disable`, `allowPrerelease: false`). Nothing else states a version.

| Selection | Owner |
| --- | --- |
| SDK version | root `global.json`, read strictly: one `sdk` object, exactly `version`, `rollForward` and `allowPrerelease`, no duplicate or unknown key, bounded size, version accepted only as a plain version token |
| runtime version | never authored; derived from the pinned SDK's `Microsoft.NETCoreSdk.BundledVersions.props` — `DefaultRuntimeFrameworkVersion` of the single `KnownFrameworkReference` for `Microsoft.NETCore.App` whose `TargetFramework` is the owned TFM |
| TFM, F# language version, `ExpectoVersion` | root `Directory.Build.props` (closed property set) |
| `platform` | each component's `*/release/Config.json`: the RID the artifact is qualified for, not a version |

`*/release/Config.json` therefore remains the single source only for the per-component
policy; it declares neither `sdkVersion` nor `runtimeVersion`, and either is rejected.
`Toolchain.fs` is the one compiled reader (loaded by the scripts like `Policy.fs`) and
`Toolchain.ps1` is its PowerShell mirror, shared by `Check.ps1` and `Update.ps1`; both
apply the same rules. The reader takes the SDK root as an explicit input and never
discovers it. A pinned version found under more than one SDK root fails closed.

`Release.fsproj` is the one documented exception: it keeps `TargetFramework` and
`LangVersion` because the publisher stage deliberately excludes repository-wide build
props. `Check.ps1` fails closed when either value differs from `Directory.Build.props`.

`Update.ps1 -Verify`, run after the SDK is installed, compares the selected SDK
(`dotnet --version` at the repository root) with the owner, resolves the root, derives
the runtime and checks that an installed `Microsoft.NETCore.App` is compatible (same
major.minor, patch at least the derived value; a prerelease never satisfies a stable
value). Only after those checks pass it writes `MCP_RELEASE_SDK_ROOT` to `GITHUB_ENV`.
The `publish` entry point reads only that variable and has no path to the SDK listing,
so SDK discovery spawns no process while the publication credential is present;
`prepare` and the other entry points resolve the root locally and must agree with the
variable when it is set. `Check.ps1` asserts that `-Verify` precedes the publish
invocation and the `GITHUB_TOKEN` step, and does not run `dotnet --version` itself
because the SDK is installed after it.

On the candidate path the manifest's declared `producer.runtime` must equal the runtime
derived from the packaged bytes; observation of already published archives is unchanged.

The pre-SDK toolchain projection is plain PowerShell over `global.json` and each
producer's canonical `Config.json`, so nothing here evaluates F# before an SDK exists.
It emits the SDK version, platform and per-component values but **no runtime version**,
because the runtime is a property of the SDK, which does not exist yet. Once the SDK is
available, producer F# scripts reuse the compiled `ReleasePolicy` parser;
PowerShell remains the minimal bootstrap projection because it must run before that
parser can be built.

## Retained scripts

- `Update.ps1 -Project` / `-Verify` — canonical toolchain projection and executing-toolchain check.
- `Check.ps1 -Components <projection>` — cross-stage guard over `release.yml`, the pinned action transfers and the enrollment roster.
- `Verify.ps1 -Root <dir> -InventoryJson <json>` / `-MembersJson <json>` — deterministic membership, path and SHA-256 proof of a downloaded artifact; it reads no network, token or receipt state, so it runs before anything from the verified set executes.
- `Typecheck.ps1 -SdkVersion <sdk>` — strict FCS typecheck of the retained producer scripts.
- `Test.ps1` — the complete compiled release regression gate.
