# GHI-24 research — release baseline and factual open gaps

Research-only slice (W1). No implementation solution frozen. No builds/tests run
as evidence in this slice. Original issue body preserved verbatim in
`original-issue.md`; requirement-to-evidence matrix in
 [`docs/release-requirements.md`](../release-requirements.md).

## 1. Verified baseline

| Fact | Value | Source |
| --- | --- | --- |
| Repo | `D:\repos\private\mcp-store` | working tree |
| Branch | `ghi-24` | `git branch --show-current` |
| `ghi-24` HEAD = `main` = `origin/main` | `30d82304e05899e2b1c83d9255fbe2f17abc0b7f` | `git rev-parse` |
| Worktree | clean | `git status --short` empty |
| Root `.github/` | absent | tree / `git ls-files` |
| Only YAML workflows | `telegram/.github/workflows/{docker-build,python-lint-format,tests}.yml` (out of scope) | `git ls-files` |
| Root config files | none of `global.json`, `Directory.Build.props/targets`, `Directory.Packages.props`, `NuGet.config`, `.config/dotnet-tools.json` | recursive search |
| `dotnet/dist/`, `workflow/dist/` | gitignored; no committed `consumer-pins.json` | `.gitignore`, `git ls-files` |
| Recent merges | #32 (GHI-26, HEAD), #31 (GHI-27), #30 (GHI-23), #25 (GHI-21) | `git log` |
| Configured SDK (both producers) | `11.0.100-rc.1.26425.128` | `*/release/Config.fsx` |
| Local `dotnet --version` | `11.0.100-rc.1.26425.128` | local probe |
| Local installed SDKs | `6.0.428`, `9.0.318`, `10.0.112`, `11.0.100-rc.1.26425.128` | local probe |
| Local runtimes | include `Microsoft.NETCore.App 11.0.0-rc.1.26425.128` | local probe |
| Local platform | Windows `10.0.26300`, `X64` | local probe |
| Target framework | `net11.0` both producers | `*/Mcp.*.fsproj` |

## 2. Canonical surfaces

| Surface | Path | Module / role |
| --- | --- | --- |
| Dotnet release policy | `dotnet/release/Config.fsx` | `ReleaseConfig` (componentId, version, entryDll, sdkVersion, archiveName, assetUri, publishedFiles) |
| Workflow release policy | `workflow/release/Config.fsx` | `ReleaseConfig` |
| Shared distribution build | `infrastructure/Distribution.fsx` | `DistributionBuild` |
| Shared provenance/errors/process | `infrastructure/Provenance.fsx` | `BuildProvenance`, `ReleaseError`, `releaseResult` |
| Shared consumer pins | `infrastructure/Pins.fsx` | `ReleasePins` |
| Shared test helper | `infrastructure/Testing.fsx` | `DistributionTestHelper` |
| Producer entrypoints | `{dotnet,workflow}/release/{Build,Pins}.fsx` | `release/build entrypoint` |
| Distribution tests | `dotnet/tests/DistributionTests.fsx`, `workflow/tests/DistributionTests.fsx` | `test entrypoint` |
| Provenance test | `dotnet/tests/ProvenanceTests.fsx` | `test entrypoint` |
| Script analyzer | `infrastructure/scripts/{Analysis,Validate}.fs`, `infrastructure/scripts/tests/Regression.fsx` | role/closure/graph + FCS typecheck |
| READMEs | root, `dotnet/`, `workflow/`, `infrastructure/scripts/`, `workflow/tests/` | local rationale |

## 3. Actual existing release behavior

- `DistributionBuild.build` (`infrastructure/Distribution.fsx`):
  `assertCleanTree` -> `committedHead` -> `dotnet publish` (framework-dependent,
  `UseAppHost=false`, `DebugType=None`, `DebugSymbols=false`, no satellites) ->
  exact allowlist assert -> stage -> `NOTICE.txt` -> `distribution.json`
  (`schemaVersion=1`, `id`, `version`, `revision=<HEAD>`, `sdk=<Config value>`,
  `entryDll`, `archive`, `files[{path,sha256}]`, `archiveFiles`) -> ZIP with
  fixed `1980-01-01` entry timestamps and `CompressionLevel.Optimal` -> SHA-256
  of archive and manifest.
- `ReleasePins.writeConsumerPins` (`infrastructure/Pins.fsx`):
  `validateManifestIdentity(id,version,archive)` -> `assertCleanTree` ->
  `committedHead` -> `assertManifestRevision == HEAD` -> SHA-256 archive/manifest
  -> `consumer-pins.json` `{assetName,assetUri,archiveSha256,manifestSha256,revision}`.
- Packaging refuses a dirty tree and stamps the clean `HEAD`. SdkVersion is
  **stamped into the manifest only**; nothing selects or verifies the SDK.
- No release-decision/compare helper exists; no remote inspection exists; no
  GitHub Actions workflow exists at root.

## 4. Remote release facts (read-only GitHub API)

| Container tag | Release id | `immutable` | Asset | Size (bytes) | Asset digest (sha256) | Download URL |
| --- | --- | --- | --- | --- | --- | --- |
| `dotnet` | 397324627 | `false` | `v1.0.3.zip` | 1,289,835 | `da3acb20007f8015e541ee7d68b48adf49d232c4eea41190a62073073d1a1614` | `.../releases/download/dotnet/v1.0.3.zip` |
| `workflow` | 397324399 | `false` | `v1.0.3.zip` | 1,482,675 | `8677413d830bd7fbc559e1847222ae4d3ee8582de4d9ba15a224a939dcc39db1` | `.../releases/download/workflow/v1.0.3.zip` |

Both fixed tags are multi-version containers (single asset each today under the
preserved model). `immutable: false` on both current releases.

## 5. Factual open gaps / blocking ambiguities

- **A. SDK not enforced (Req 68).** No `global.json`/props pin; `dotnet publish`
  uses ambient SDK selection. Local match is incidental, not enforced, and
  non-reproducible on a differently provisioned runner. "Actual canonical SDK
  enforcement" (AC1) has no mechanism yet.
- **B. No comparison/decision logic (Req 19-26, 29, 51-53).** Nothing reads a
  remote asset or decides published/unchanged/failed. Req 25 requires a strict,
  deterministic, GitHub-free comparison helper; it must be authored.
- **C. No workflow YAML (Req 6, 35-40, 44-46).** Root `.github/workflows/` is
  absent. Permissions, concurrency, and container handling are unimplemented.
- **D. Immutable-release conflict (A3, Req 70).** Current releases report
  `immutable: false`, so no immediate conflict, but the issue mandates fail-closed
  on incompatible settings. Whether the workflow may configure the release or only
  fail with an actionable message is unresolved. Decision for this task: fail
  closed; no new tag model.
- **E. Concurrency group key unspecified (A1, Req 37-40).** Per-component vs
  single `main` group, and the definition of a `queue: max` equivalent, are open.
- **F. `workflow_dispatch` input surface unspecified (A2, Req 9).**
- **G. Ambiguous-upload determinism boundary (A4, Req 42, 52-53).** Whether a
  fake transport is required or transport-mocked decision logic suffices is open.
- **H. Failure-reason conciseness / transient post-upload 5xx (A5, Req 34, 41-42).**
- **I. Remote byte download transport gap (Req 20-24, 67).** The dedicated GitHub
  surface exposes release metadata and per-asset `digest` but no asset-content
  download. Actual historical `v1.0.3` byte comparison cannot be performed through
  the dedicated API and requires the workflow's own authenticated download (or an
  explicitly authorized fetch). This slice deliberately does not perform it.
- **J. No committed consumer pins (Req 47-49, 62, AC7).** `dist/consumer-pins.json`
  is generated and gitignored; the consumer-neutral metadata projection is produced
  only at release time.
- **K. Platform reproducibility (Req 68, AC1).** Only a local Windows/X64 host has
  been observed; the CI runner OS/arch is unspecified and unproven for byte-stable
  packages.
- **L. Historical bootstrap (Req 67, AC5).** Pre-refactor `v1.0.3` bytes may differ
  from a current candidate beyond `revision`; a reviewed version bump may be
  required. Not yet determined (blocked by I).

## 6. Closed ambiguity

- **A6** ("shared release/pin infrastructure path not named"): located at
  `infrastructure/{Distribution,Pins,Provenance}.fsx`; producers load them via
  `#load`. Closed by inspection above.
