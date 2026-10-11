# `infrastructure/release`

Shared compiled release runner and the minimal toolchain bootstrap invoked by
the root `release.yml`.

## Bootstrap

`Update.ps1` runs the canonical `Config.fsx` values in two modes:

- `Update.ps1 -Project` reads the SDK/runtime/platform literals without
  evaluating F# or requiring an installed SDK and writes `sdk`/`runtime`/`platform`
  to `$GITHUB_OUTPUT`; run it before installing the SDK.
- `Update.ps1 -Verify -Component <dotnet|workflow>` recomputes the projection,
  exports `RELEASE_EXPECTED_SDK`/`RELEASE_EXPECTED_RUNTIME`/`RELEASE_EXPECTED_PLATFORM`,
  runs `Bootstrap.fsx`, and forwards the verified summary to `$GITHUB_ENV`; run
  it after installing the SDK.

`Bootstrap.fsx` emits `RELEASE_SDK`, `RELEASE_RUNTIME`, `RELEASE_PLATFORM`,
`RELEASE_ARCHITECTURE`, and per-component
`RELEASE_<COMPONENT>_{VERSION,ARCHIVE,ASSET_URI,ENTRY}` to `$GITHUB_ENV`; the
format is additive so older workflows keep parsing.

## Runner

`ReleaseRunner.exe` is built from `Release.fsproj` (Compile order: `Provenance`,
`Archive`, `Compare`, `Metadata`, `Publish`, `Evaluate`, `Runner`) and invoked
per component by `dotnet/release/Release.fsx` and `workflow/release/Release.fsx`.

### Contract DTO

The per-component `Release.fsx` passes a JSON contract DTO with exactly:

- `componentId`
- `version`
- `archiveName`
- `assetUri` (the canonical Config.fsx literal; the runner does NOT
  reconstruct it from environment variables)
- `entryAllowlist`

### F7 structured-JSON-on-failure contract

- `ReleaseRunner.exe` emits one JSON object on stdout before every exit
  (success, failure, bump-required), with `status`/`reason` plus the
  config context (`componentId`, `version`, `archiveName`, `assetUri`)
  whenever the contract has been parsed.
- The per-component `dotnet/release/Release.fsx` and
  `workflow/release/Release.fsx` parse stdout JSON even on nonzero exit.
  Their `build`/`pins`/`tests` phases emit a structured JSON failure
  report (with `phase`, `childExitCode`, `childStderr`, config context)
  before the wrapper exits, so a failure never reaches `Runner.exe`
  silently.
- The empty-argv case retains its `usage` line and `exit 2` (no
  contract context is available).

### Public status set

The Runner's stdout JSON `status` is one of three public values; the
`reason` field carries internal detail:

- `published` — the candidate archive and metadata were uploaded;
  remote digests verified by `gitHubObserver` /
  `gitHubMetadataObserver`. The emitted JSON populates
  `publishedArchiveSha256` and `publishedMetadataSha256` from the verified
  remote bytes (never from candidate values); the verified projection file
  additionally carries `manifestSha256` and `publishedRevision`.
- `unchanged` — strict comparison classified the candidate as
  byte-identical or strictly revision-only equivalent, and the
  immutable metadata asset either matched the candidate render or
  was verified absent. In the verified-absence case the runner
  republishes only the metadata so the release reaches the same
  verified state without touching the archive, and the final JSON
  carries the published archive sha/revision observed remotely plus
  the verified metadata sha. Published archive sha, manifest sha,
  and revision always come from the observed remote archive — never
  from candidate values.
- `failed` — anything else; the `reason` field carries the internal
  detail (`bump-required` with `bumpedVersion`, `publish-pending`,
  transport error, parse failure, immutable container, corrupt /
  mismatching metadata, validation gate failure). On this branch
  every `published*` key is absent: candidate hashes never masquerade
  as published values.

### F2 / F4 / F9 invariants

- F2 reconciliation: the no-op path parses the embedded DISTRIBUTION
  manifest (via `ReleaseCompare.parseEmbeddedManifestEntry`). Metadata
  observation goes through the real `gitHubMetadataObserver`;
  verified absence is the only signal that permits a metadata-only
  recovery. Metadata parse failures, mismatches against the
  candidate render, and transport failures propagate as
  `MetadataCorrupt` and fail closed without overwriting or repairing
  the remote asset.
- F4 wiring: `ReleaseCompare.validate` runs on the candidate observation
  inside `ReleaseEvaluate.evaluate` BEFORE the remote observation and
  any upload branch.
- F9 repository locator: `masterlifting/mcp-store` is the fixed policy.
  The canonical assetUri is taken verbatim from the contract DTO and
  validated against `https://github.com/<owner>/<repo>/releases/download/<component>/<archive>`
  by `ReleaseEvaluate.validateCanonicalAssetUri`; the runner refuses
  to reconstruct it from environment variables.

## Exit codes

`<component>/release/Release.fsx`: `0` = `published`/`unchanged`,
`1` = `failed` (the wrapper derives the status from the runner's JSON, so a
runner guard such as the clean-tree check surfaces here as `1`), `2` =
`failed` with `bumpedVersion`, `4` = compiled runner not found, `5` = runner
JSON parse failure. A `build`/`pins`/`tests` phase failure exits with that
child's code and emits the structured JSON failure object.

## Shared evaluation helper

`infrastructure/release/Evaluate.fsx` is the testable, transport-injected
evaluation API. It owns the canonical classification site so the runner
and tests share one decision path:

- The pure comparison surface is `ReleaseCompare.compare` (no router
  module).
- The Runner's CLI shell is a thin top-level that drives this API,
  dispatches the publish or metadata-recovery branches through the
  existing `ReleasePublish` transport, reconstructs the local metadata
  projection from verified (observed or transport-verified) values —
  never from the raw candidate values — and emits one JSON object on
  stdout.

## Regression suite gate

`Suite.ps1` runs the focused deterministic suite non-publishing: every
`tests/*.fsx` whose role is `test entrypoint`, except `Historical.fsx`, the
optional integration that needs locally downloaded archives. It snapshots
`GITHUB_ENV`/`GITHUB_OUTPUT` (target and bytes) and fails if a child redirects
or writes them. `release.yml`'s read-only `tests` job depends on `bootstrap`,
and the `release` job depends on `tests`, so a failed suite blocks every
upload.

## Workflow result parsing

`release.yml`'s `append <component> structured release result` step parses the
entrypoint's output on every exit code: the JSON failure object a wrapper emits
for its `build`/`pins`/`tests` phases, or the runner's `key=value` summary. It
writes typed fields (reason, version, archiveName, assetUri, `phase`,
`childExitCode`, candidate/published hashes, revisions) to
`release-status-<component>.json`, and a missing structured `status` is
evidence-only, never a guessed exit code. A verified projection is copied to
`published-metadata-<component>.json` for `published` and `unchanged` only when
it carries `archiveSha256`, `manifestSha256`, and `publishedRevision` and its
archive sha matches the observed published archive; candidate pins stay a
separate artifact.

The `ReleaseCompare.Decide` module and `ReleaseTransaction.run` were folded into
`ReleaseEvaluate`; the tests drive `ReleaseEvaluate` directly with no
compatibility aliases.
