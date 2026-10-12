# `infrastructure/release`

Shared compiled release runner, the pre-SDK toolchain bootstrap, and the
non-publishing regression and typecheck gates invoked by the root `release.yml`.

## Enrollment roster

`Participants.json` is the single authoritative enrollment roster and carries
only `componentId` and `configPath` per participant. Versions, SDK/runtime/
platform, allowlists and URIs stay in each producer's canonical
`*/release/Config.fsx`, so no stage restates them; `ReleaseEnrollment`
(`Enrollment.fs`) rejects any extra field at load time.

The roster drives every participant list: `Update.ps1` iterates it for
projection and verification, `release.yml` builds its release matrix from the
projected component list, `Bootstrap.fsx` iterates it for the toolchain guard,
and `ReleaseOrchestration.orchestrate` plus `ReleaseRunner` transaction mode
reject an unsupported `componentId`, a `configID` mismatch, or a `configPath`
drift before any child script or contract work runs. The PS gates share
`Roster.ps1`, which mirrors the compiled acceptance rules (forbidden policy
fields, duplicate or empty entries, and the mandatory `dotnet`/`workflow` set
including omissions), so the pre-SDK and compiled decisions agree without copying
version, SDK, allowlist or URI values.

`Coherence.ps1` is the deterministic cross-stage guard: it fails closed when the
`components` bootstrap output, the `fromJSON` release matrix, the fixture-step
wiring, the roster-wide verification, the matrix-scoped publication verification,
the `orchestrate` CLI path, the projected SDK version or the emitted
`components=` projection stop matching the roster, or when a participant list or
CLI path is hardcoded again. `release.yml` runs it in the `bootstrap` job
immediately after `Update.ps1 -Project`, so drift fails before the SDK install and
therefore before any publication.

## Bootstrap

`Update.ps1` runs the canonical `Config.fsx` values in two modes:

- `Update.ps1 -Project` reads the SDK/runtime/platform literals of every roster
  participant without evaluating F# or requiring an installed SDK and writes
  `sdk`/`runtime`/`platform` plus the roster component list as JSON
  (`components=["dotnet","workflow"]`) to `$GITHUB_OUTPUT`; run it before
  installing the SDK.
- `Update.ps1 -Verify [-Component <id>]` recomputes the projection, exports
  `RELEASE_EXPECTED_SDK`/`RELEASE_EXPECTED_RUNTIME`/`RELEASE_EXPECTED_PLATFORM`,
  runs `Bootstrap.fsx`, and forwards the verified summary to `$GITHUB_ENV`. A
  supplied `-Component` must be a roster participant and is rejected before the
  bootstrap runs otherwise; without it the guard verifies all roster
  participants. Run it after installing the SDK.

`Update.ps1` fails closed on a missing, empty, duplicate or non-JSON roster, and
on a `Config.fsx` whose `componentId` contradicts the roster entry for the same
path; it is the pre-SDK half of the roster consistency check, and the compiled
`ReleaseEnrollment` guards re-validate the same invariant after the SDK exists.

`Bootstrap.fsx` emits `RELEASE_SDK`, `RELEASE_RUNTIME`, `RELEASE_PLATFORM`,
`RELEASE_ARCHITECTURE`, and per-component
`RELEASE_<COMPONENT>_{VERSION,ARCHIVE,ASSET_URI,ENTRY}` to `$GITHUB_ENV`; the
format is additive so older workflows keep parsing.

The "one SDK/runtime/platform across all participants" equality check belongs to
this bootstrap guard only, because both current .NET producers share exactly one
toolchain. Generic archive identity, metadata decisions and HTTP transport read
per-producer expectations from `Config.fsx` and never require a global toolchain
identity; per-producer toolchains, platforms and languages are deferred to #37.

## Runner

`ReleaseRunner.exe` is built from `Release.fsproj` (Compile order: `Provenance`,
`Archive`, `Compare`, `Metadata`, `Publish`, `Evaluate`, `Capture`,
`Enrollment`, `Transaction`, `Orchestration`, `Runner`). It is the per-component
release entrypoint; no producer `Release.fsx` wrapper exists.

### Invocation

- `ReleaseRunner.exe orchestrate <config-path>` — the per-component pipeline
  used by `release.yml`, e.g. `orchestrate dotnet/release/Config.fsx`. It reads
  the canonical `*/release/Config.fsx` through an isolated child `dotnet fsi`
  process (no policy mirror), then sequences child `Build.fsx`, child `Pins.fsx`,
  child `DistributionTests.fsx`, and the compiled transaction, and emits one
  structured JSON object on stdout before every exit.
- `ReleaseRunner.exe <manifest-path> <archive-path> <contract-json>` —
  standalone transaction driving `ReleaseEvaluate` and `ReleaseTransaction`
  directly; the contract DTO and canonical `assetUri` travel as arguments.

Both modes resolve the repository root from the assembly location, so the
runner may be launched from any working directory. `Bootstrap.fsx` stays outside
the compiled assembly because its top-level `Async.RunSynchronously` would run
in the runner's module initializer.

### Contract DTO

The orchestrator builds this DTO from the projected canonical config; it carries
exactly:

- `componentId`
- `version`
- `archiveName`
- `assetUri` (the canonical `Config.fsx` literal; the runner never reconstructs
  it from environment variables)
- `entryAllowlist`

### Structured-JSON-on-exit contract

`ReleaseRunner.exe` emits one JSON object on stdout before every exit (success,
failure, bump-required): `status`/`reason` plus the config context
(`componentId`, `version`, `archiveName`, `assetUri`) whenever the contract has
been parsed. A `build`/`pins`/`tests` phase failure emits
`status=reason` plus `phase`, `childExitCode`, `childStderr` and the config
context, so a failure never reaches the transaction unnoticed. The empty-argv
case prints only its `usage` lines and exits `2`, because no contract context is
available.

### Public status set

The stdout JSON `status` is one of three public values; the `reason` field
carries internal detail:

- `published` — the candidate archive and metadata were uploaded; remote
  digests were verified. The JSON populates `publishedArchiveSha256` and
  `publishedMetadataSha256` from the verified remote bytes (never candidate
  values); the verified projection file additionally carries `manifestSha256`
  and `publishedRevision`.
- `unchanged` — strict comparison classified the candidate as byte-identical or
  strictly revision-only equivalent and the immutable metadata asset matched the
  candidate render or was verified absent. In the verified-absence case only the
  metadata is republished so the release reaches the same verified state without
  touching the archive. Published archive sha, manifest sha, and revision always
  come from the observed remote archive, never from candidate values.
- `failed` — anything else; `reason` carries the internal detail
  (`bump-required` with `bumpedVersion`, `publish-pending`, transport error,
  parse failure, immutable container, corrupt or mismatching metadata,
  validation-gate failure). On this branch every `published*` key is absent, so
  candidate hashes cannot masquerade as published values.

### Integrity invariants

- Archive and metadata are both observed before any publication; unknown,
  corrupt or conflicting metadata blocks the archive POST, and observation
  errors fail closed.
- Every required metadata identity field is compared against canonical producer
  policy and the verified published bytes; the published revision comes from the
  observed remote embedded manifest, not from the candidate HEAD.
- Initial observation, post-upload readback, and idempotent recovery all
  require a present, well-formed API SHA-256 that matches the downloaded bytes.
  Candidate byte equality is not a substitute for claimed-digest verification.
- `ReleaseCompare.validate` runs on the candidate observation inside
  `ReleaseEvaluate.evaluate` before the remote observation and any upload
  branch.
- `masterlifting/mcp-store` is the fixed repository policy. The canonical
  `assetUri` is taken verbatim from the contract DTO and validated against
  `https://github.com/<owner>/<repo>/releases/download/<component>/<archive>`;
  the runner refuses to reconstruct it from environment variables.
- Existing assets are never overwritten, deleted or replaced; the no-op path
  parses the embedded distribution manifest through
  `ReleaseCompare.parseEmbeddedManifestEntry`.

## Regression gate

`Suite.ps1` is the non-publishing release gate:

- It executes the compiled Expecto project
  `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll`
  (`dotnet build infrastructure/release/tests/Tests.fsproj -c Release` first).
  That entrypoint is a console Expecto host, so `dotnet test` reports no counts
  for it; `Suite.ps1` uses the native Expecto exit code and compares the
  executed-case count against the suite's own `--list-tests` inventory, so a
  dropped or unrun case fails the gate.
- It runs the retained producer provenance entrypoint
  `dotnet/tests/ProvenanceTests.fsx`, which is safe from a dirty tree. The
  producer packaging entrypoints (`<componentRoot>/tests/DistributionTests.fsx`,
  derived from each roster participant) need a clean tree and a producer build,
  so `ReleaseRunner.exe orchestrate` runs them per component in its `tests`
  phase instead; the gate asserts they still exist.
- The compiled suite includes the historical integration, which reads the pinned
  archives through `MCP_RELEASE_HISTORICAL_ROOT` (or `-HistoricalRoot`) and
  re-verifies each archive's SHA-256 and size. `Suite.ps1` fails closed with an
  explicit `::error::` when that root is unset or is not a directory, and the
  compiled case reports a missing or mismatched archive as a failure; no case is
  ever excluded, filtered or rewritten to work around a missing prerequisite.
  The `tests` job of `release.yml` provisions the fixtures from the already
  published release assets, deriving the component tags from the roster
  projection; the fixture version, file names and hash pins stay owned by the
  compiled `Historical` case.
- It snapshots `GITHUB_ENV`/`GITHUB_OUTPUT` (target and bytes) and fails if a
  child redirects or writes them.

`release.yml`'s read-only `tests` job builds both Expecto projects, runs the
producer gate, provisions the pinned historical fixtures, then runs `Suite.ps1`;
the `release` job depends on `tests`, so a failed gate blocks every upload.

## Strict script typecheck gate

`Typecheck.ps1` is the read-only strict FCS gate for the release-path scripts
that no compiled project build covers. It derives the producer targets from the
roster (`<configDirectory>/Build.fsx`, `<configDirectory>/Pins.fsx` and
`<componentRoot>/tests/DistributionTests.fsx` for each participant) and adds the
fixed non-roster scripts `infrastructure/release/Bootstrap.fsx` and
`dotnet/tests/ProvenanceTests.fsx`; an explicit `-Targets` run overrides the
derivation, so no participation list is restated in the gate. The compiled
modules and the compiled regression project are type-checked by their own builds,
so this gate never re-typechecks that graph. It resolves the canonical SDK from
`dotnet --list-sdks` (no SDK table or hard-coded developer path), writes a probe
under `$RUNNER_TEMP` — outside the repository, so its SDK `#r` reference never
enters the in-repo analyzer surface — and type-checks each target and its
`#load` closure with `useSdkRefs` using the exact canonical SDK's `fsi.dll`.
Targets are read and type-checked as text only; they are never evaluated, so no
assets, pins, token, or network are touched. A zero-target run, a missing derived
target, or any reported error fails closed.

## Workflow result parsing

`release.yml`'s `append <component> structured release result` step parses the
runner's output on every exit code. It writes typed fields (reason, version,
archiveName, assetUri, `phase`, `childExitCode`, candidate/published hashes,
revisions) to `release-status-<component>.json`; a missing structured `status` is
evidence-only, never a guessed exit code. A verified projection is copied to
`published-metadata-<component>.json` for `published` and `unchanged` only when
it carries `archiveSha256`, `manifestSha256`, and `publishedRevision` and its
archive sha matches the observed published archive; candidate pins stay a
separate artifact.

## Exit codes

`ReleaseRunner.exe`: `0` = `published`/`unchanged` (including metadata-only
recovery), `1` = `failed` without a bump suggestion, `2` = `failed` with
`bumpedVersion` (bump-required) or a usage error, `3` = a required manifest or
archive is missing, `4` = the contract DTO is invalid, the worktree is dirty,
HEAD cannot be resolved, the manifest revision does not match HEAD, or the
canonical config could not be projected. A `build`/`pins`/`tests` phase failure
exits with that child's code and emits the structured phase JSON.
