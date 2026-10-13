# SEC1 publication boundary — implementation notes

This document records the implementation decisions and observable artifacts of
the first compiled-batch (`batch1`) of the SEC1 publication boundary amendment
described in [`Boundary.md`](./Boundary.md). It is the factual companion to the
frozen design decisions; the design itself remains authoritative and this
document never restates policy.

## Scope and ownership

- Scope of the change: `infrastructure/release/*.fs`, `Release.fsproj`,
  `infrastructure/release/README.md`, plus this file and a new
  `infrastructure/release/Publisher.json` audited closure. No tests, workflows,
  PowerShell scripts, producer `Config.fsx`, or producer `.fsproj` files were
  modified.
- Authority boundary: the existing compiled release application and
  `ReleaseTransaction.run` / `ReleaseTransaction.runFromBytes` remain the
  single transaction engine. The credential-bearing transaction subprocess
  path and the legacy `ReleaseOrchestration.orchestrate` /
  `ReleaseRunner orchestrate` surfaces have been retired entirely (no
  compatibility alias, no environment-mode switch). The only publication
  entrypoint is `ReleaseRunner publish …`.

## Implemented components

### `Publisher.json` — audited closure for the publisher job

Located at `infrastructure/release/Publisher.json`. Enumerates only the
trusted sources the publisher reads:

- `schemaVersion` (int; must equal 1)
- `repository` (string)
- `rosterPath` (string; relative path to `Participants.json`)
- `expectedComponentIds` (string list; the exact mandatory component set)
- `expectedConfigShas` (object map of `componentId → 64-hex lowercase
  SHA-256`; the publisher hashes the canonical `Config.fsx` bytes it reads
  and compares with this expected value before parsing)

The publisher rejects unknown fields, missing required values, the wrong
schema version, non-64-hex SHA-256 digests, and mismatched component sets.
The DevOps batch must populate `expectedConfigShas` with the exact canonical
SHA-256 of each `Config.fsx` at the trusted event SHA before the publisher
job runs.

### `Policy.fs` — pure Config.fsx parser

- Strict, fail-closed parser for the exact `module ReleaseConfig` subset each
  `*/release/Config.fsx` declares: nine named `let` bindings, each carrying a
  string literal, a `$"..."` interpolated string, or a string list of those two
  forms.
- Resolves `$"..."` interpolations against previously-bound string values;
  rejects undeclared references, `{{`/`}}` literal escapes, and raw newlines
  inside any string form.
- Fails closed on duplicate `let` bindings, a non-`ReleaseConfig` module name,
  any unrecognized top-level syntax (function decls, type decls, mutable, etc.),
  missing required fields, or unparseable text.
- Returns a `PolicyProjection` record plus the source file's SHA-256 so the
  publisher can bind the policy to the exact bytes the publisher observed.
- Never executes the file, never reads environment variables, never spawns a
  child process, and never imports any F# runtime beyond the canonical parser
  surface.

#### `parseBytes` — byte-safe primary entrypoint

```
let parseBytes (sourcePath: string) (boundedBytes: byte[]) : Result<PolicyProjection, ReleaseError>
```

This is the canonical, byte-safe entrypoint. The caller (the future
publisher) supplies the exact bytes it observed; `parseBytes` interprets them
strictly as UTF-8, computes the source SHA-256, and returns the projection.
The caller is responsible for verifying the bytes against
`Publisher.json.expectedConfigShas` BEFORE invoking this function.

#### `parse(path)` — temporary thin adapter (NOT final design)

`parse(sourcePath)` is retained ONLY because the un-migrated `Runner.fs` and
`Orchestration.fs` callers still pass a path. Removing it would break those
un-migrated callers; making it private would also break them since they are
separate modules. The adapter is the smallest possible wrapper:

```
let parse (sourcePath: string) : Result<PolicyProjection, ReleaseError> =
    try
        let absolute = Path.GetFullPath sourcePath
        if not (File.Exists absolute) then
            Error(MissingArtifact absolute)
        else
            parseBytes absolute (File.ReadAllBytes absolute)
    with
        ...
```

The Caller batch migrates every caller of `parse(path)` to
`parseBytes(sourcePath, preVerifiedBytes)` and removes this adapter entirely.
This is explicitly NOT part of the final design; it is a back-compat seam
kept narrow so removing it later does not require touching any other module.

#### Grammar fixes applied in this batch

Two grammar gaps were closed without changing the canonical Config.fsx
files:

1. Whitespace between the `let` keyword and the binding identifier is now
   consumed (`skipTrivia` after the `let` token) so `let componentId`
   (with one space) parses correctly. Without this, the parser rejected the
   canonical form.
2. List elements inside `[ ... ]` are now separated by any combination of
   `;`, whitespace, and newlines. The canonical `publishedFiles` lists its
   elements on separate lines without a `;` separator; the parser now
   accepts that style as well as the explicit `;`-separated form.

By source reasoning, both canonical `dotnet/release/Config.fsx` and
`workflow/release/Config.fsx` now parse successfully:

- `module ReleaseConfig` ✓
- `let componentId = "dotnet"` / `"workflow"` ✓ (whitespace after `let`)
- `let version = "1.0.4"` ✓
- `let entryDll = "Mcp.<Component>.dll"` ✓
- `let sdkVersion = "11.0.100-rc.1.26425.128"` ✓
- `let runtimeVersion = "11.0.0-rc.1.26425.128"` ✓
- `let platform = "win-x64"` ✓
- `let archiveName = $"v{version}.zip"` ✓ (`{version}` resolves to `"1.0.4"`)
- `let assetUri = $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/v{version}.zip"` ✓
  (both `{componentId}` and `{version}` resolve)
- `let publishedFiles = [ "FSharp.Core.dll" "Mcp.<Component>.deps.json" "Mcp.<Component>.dll" "Mcp.<Component>.runtimeconfig.json" ]` ✓
  (newline-separated items without `;`)

The tester batch owns execution-side proof of parseability; the batch
above only modifies the parser.

### `PublisherClosure.fs` — strict closure parser

- Strict `parse` rejects unknown top-level fields, missing required fields,
  null values, wrong-kind values, and non-64-hex SHA-256 digests before any
  closure values are consumed.
- `readFromFile` is the publisher's only consumer entrypoint; it returns the
  parsed record plus the closure file's own SHA-256 (used only as a diagnostic,
  never as authorization).

### `Orchestration.fs` — preparation flow refactor

- `loadProducerConfig` uses `ReleasePolicy.parse` exclusively; the wrapper +
  child `dotnet fsi` projection is gone.
- The private `prepareInternal` helper drives the entire preparation sequence:
  roster validation, pure policy projection, roster/projection cross-check,
  scrubbed `Build.fsx`, scrubbed `Pins.fsx`, scrubbed producer tests, then
  SHA-256 snapshot of `Config.fsx`, the archive, the standalone manifest, the
  embedded manifest, plus the evaluated HEAD. It fails closed before any write
  attempt and before any transport construction.
- The new `prepare` function performs the same sequenced preparation and
  copies the archive (`<archive-name>.zip`) and standalone manifest
  (`distribution.json`) into a portable candidate directory under their
  **relative exact names**. A diagnostic `sha256.txt` is written alongside;
  the publisher never trusts its contents.

### `Runner.fs` — thin CLI shell

- `ReleaseRunner prepare <config-path> <candidate-directory>` delegates to
  `ReleaseOrchestration.prepare`. It refuses to start if any of the four
  publication-authority environment variables is set.
- `ReleaseRunner publish <publisher-closure-path> <candidate-directory>
  --repo=… --run-id=… --attempt=… --event-sha=… --component=…
  --platform-digest=…` is the sole data-only publication entrypoint. The
  publisher:
  1. Reads `Publisher.json` and verifies every required field (fails closed
     otherwise).
  2. Reads trusted platform context from CLI flags or environment variables
     (fails closed if any required field is missing or malformed).
  3. Reads the canonical `Config.fsx` bytes from the workspace at the
     checked-out event SHA, hashes them, and compares with the closure's
     `expectedConfigShas[component]`. Fails closed on any mismatch.
  4. Parses the verified Config.fsx bytes via `ReleasePolicy.parse`; every
     policy value (componentId, version, entryDll, sdkVersion, runtimeVersion,
     platform, archiveName, assetUri, publishedFiles) comes from this parse.
  5. Verifies the checked-out HEAD equals the platform-supplied event-sha
     (`BuildProvenance.committedHead`).
  6. Verifies the publisher-derived toolchain digest
     (`{Platform}:{RuntimeVersion}:{SdkVersion}`) matches the
     platform-supplied `--platform-digest`.
  7. Locates the candidate archive and standalone manifest by their
     relative exact names in the portable candidate directory (never reads
     from the prep workspace).
  8. Runs `ReleaseArchive.parse` on the candidate archive bytes BEFORE any
     extraction — this enforces exact names, entry / expansion limits, no
     traversal / absolute / drive / UNC / alternate-stream paths, no links,
     no duplicates, no case collisions.
  9. Re-hashes every byte the publisher observes; verifies the embedded
     `distribution.json` bytes equal the standalone manifest bytes; verifies
     the embedded manifest revision equals the platform event-sha.
  10. Drives `ReleaseTransaction.runFromBytes` (the byte-safe adapter of the
      existing `ReleaseTransaction.run` seam) with the verified bytes; the
      publisher never opens a file between verification and upload.
- The legacy `ReleaseRunner orchestrate` mode has been REMOVED. The legacy
  `ReleaseOrchestration.orchestrate` function has been REMOVED. There is no
  compatibility alias and no environment-mode switch.

### `Transaction.fs` — ONE canonical transaction, no candidate-as-remote

The prior batch's `byteSafeObserver` and the parallel `run` / `runFromBytes`
implementations are gone. The transaction layer now exposes a single canonical
executor (`execute`, byte-only, takes a `VerifiedCandidate`) and a single
canonical entry (`runFromBytes`, takes a `VerifiedCandidate`). The legacy
path-based `run`, the duplicate `executeFromBytes`, and the unsafe
`byteSafeObserver` have been removed entirely.

- `runFromBytes (transport, auth, useGitHub, downloadDir, locator,
  candidate: VerifiedCandidate)` is the ONE entry. The `VerifiedCandidate`
  carries the bounded snapshot (archive bytes, manifest bytes, parse-observed
  hashes / sizes / embedded-manifest bytes / embedded revision / DTO /
  projection) that the publisher has already validated. The classifier
  (`ReleaseEvaluate.evaluate`) is fed the genuine HTTP remote observation
  via `buildRemoteObserverWith`; the candidate observation is taken from
  `VerifiedCandidate.ArchiveObservation`. The remote revision comes from the
  remote's verified bytes via `ReleaseCompare.parseEmbeddedManifestEntry
  publishedObs` — never from the candidate.
- `execute (transport, resolution, auth, locator, candidate)` is the ONE
  executor. The candidate bytes flow into `ReleasePublish.uploadCandidateBytes`
  / `uploadMetadataBytes`; no mutable on-disk path is reopened between
  verification and upload. The verified-byte contract is preserved at every
  branch (published, unchanged + recovery, unchanged no-op).
- `persistVerifiedProjection`, `buildRemoteObserverWith`,
  `buildRemoteObserver`, `requireObserved`, `parseContractJson` are retained
  as helpers; none of them expose a parallel write path.

### `Compare.fs` — shared validation core

`observe` and `observeBytes` both delegate to a single private
`validateParsedArchive` core. The validation pipeline (entries, manifest
extraction, payload SHA-256, manifest-count check, self-consistency) is
written once and shared between the path-based and byte-based public entries.
`observeBytes` is now sync (`Result<ArchiveObservation, ReleaseError>`) so the
factory can call it from a pure synchronous computation without `async`.

### `Publish.fs` — byte-upload wrappers share one core

`ReleasePublish.uploadCandidateBytes` and `ReleasePublish.uploadMetadataBytes`
are the only public upload entrypoints; both call the private `uploadBytes`
core which performs the `snapshotDigest` re-verification before the POST.
The path-based `uploadCandidate` and the projection-shaped `uploadMetadata`
have been removed per the boundary's "no compat legacy/path adapter" rule.

### `Evaluate.fs` — `VerifiedCandidate` bounded snapshot type

A new `VerifiedCandidate` type with an `internal` constructor carries every
field the publisher already verified:

- `ArchiveBytes`, `ManifestBytes`, `ArchiveObservation`
- `ArchiveSha256`, `ManifestSha256`, `ArchiveSize`, `ManifestSize`
- `EmbeddedManifestBytes`, `EmbeddedManifestSha256`, `EmbeddedRevision`
- `Dto` (canonical `CandidateDto`) and `Projection`
  (canonical `ReleaseMetadata.ProducerMetadataProjection`)

The `VerifiedCandidate.create` factory is the ONLY path that constructs an
instance. Every required check (policy fields non-empty, archive / manifest
bytes present, embedded manifest exists, embedded manifest bytes equal
standalone bytes, embedded manifest SHA equals standalone SHA, archive SHA
matches `observation.ArchiveSha256`, embedded revision equals
`evaluatedHead`, embedded `Id` / `Version` / `Archive` match the policy,
entry allowlist matches `publishedFiles @ [NOTICE.txt; distribution.json]`)
runs BEFORE any transport, auth, or upload-core composition is constructed.
A mismatch yields a typed `ReleaseError` without ever touching the network
or opening a file.

`evaluate` now takes the `VerifiedCandidate` directly; the candidate
observation is `candidate.ArchiveObservation` (no candidate-file hash
round-trip; the verified bytes are the only bytes the executor ever sees).
The remote observation is supplied by the genuine `buildRemoteObserverWith`
HTTP remote observer built inside `runFromBytes`.

## Invariants preserved

- The compiled release application and the existing
  `ReleaseEvaluate`/`ReleaseTransaction` decision pipeline are unchanged in
  semantics; only new byte-safe entrypoints were added.
- Existing immutable metadata preflight, complete metadata identity, API
  digest checks, strict revision-only equivalence, idempotent recovery, and
  the no-overwrite refusal all remain in the publisher's transport path.
- The exact-two enrollment roster (`Participants.json`) and the
  `dotnet`/`workflow` mandatory scope are unchanged.
- The pure Config.fsx parser enforces the exact same identity fields the
  delegate against roster already enforced (`componentId` against the roster
  entry, exact configPath match); the only new check is byte-level purity
  of the projection (no FSI execution, no second-authored version, no
  executable syntax).

## Boundaries the next batch must wire

The DevOps batch that follows this code-batch must:

1. **Fresh-job workflow split in `.github/workflows/release.yml`** —
   replace the existing `release` job's `orchestrate` invocation with:
   - Preparation job (`needs: [bootstrap, tests]`, no publication authority):
     checkout at exact `github.sha`, `setup-dotnet`, build `Release.fsproj`,
     then `ReleaseRunner prepare <component>/release/Config.fsx
     <candidate-directory>`. Upload the candidate directory as a job
     artifact named `release-candidate-<component>` (relative-path tarball
     or directory artifact).
   - Publication job (`needs: [prepare]`, `permissions: contents: write`):
     download the candidate directory as an input artifact, set
     `MCP_RELEASE_GITHUB_OWNER`/`MCP_RELEASE_GITHUB_REPOSITORY`/
     `MCP_RELEASE_DO_UPLOAD`/`GITHUB_TOKEN`, then `ReleaseRunner publish
     infrastructure/release/Publisher.json <candidate-directory>
     --repo=<owner>/<name> --run-id=<github.run_id>
     --attempt=<github.run_attempt> --event-sha=<github.sha>
     --component=<dotnet|workflow>
     --platform-digest=<os-arch:sdk:runtime>`. Parse the JSON result and
     copy the verified projection per the existing `append <component>
     structured release result` logic.

2. **Regenerate `Publisher.json` at the trusted event SHA** — the
   preparation job (or a separate workflow step) must compute the exact
   SHA-256 of `dotnet/release/Config.fsx` and `workflow/release/Config.fsx`
   at the trusted `github.sha` and write them into `Publisher.json` before
   the publication job consumes it. The publisher fails closed if the
   observed SHA does not match.

3. **Protected-download interface for hosted fetches** — if the platform
   downloads the candidate archive via a separate workflow step rather
   than passing it as an artifact, the download step MUST verify the
   archive against `ReleaseArchive.parse` BEFORE the publisher job reads it,
   so the publisher never observes a partially-validated candidate. The
   existing strict archive parser already enforces every required invariant
   (exact names, entry / expansion limits, no traversal / absolute / drive /
   UNC / alternate-stream paths, no links, no duplicates, no case
   collisions). The publisher then re-validates against the same parser
   for defense in depth.

4. **Test refresh (tester batch)** — migrate every test that previously
   referenced `ReleaseOrchestration.orchestrate`,
   `ReleaseRunner orchestrate`, or `ReleaseRunner.exe <manifest> <archive>
   <contract-json>` to the new `prepare` / `publish` seam. The current
   `infrastructure/release/tests/*.fs` references the legacy surface and
   will not compile until the tester batch updates them; this is the
   expected consequence of the boundary decision to retire the legacy
   orchestrator surface.

5. **No `dotnet fsi` on `Config.fsx`** — DevOps MUST NOT add a `dotnet fsi`
   invocation back into the publisher; the only publisher-side reader of
   `Config.fsx` bytes is `ReleasePolicy.parse`.

6. **No secret leakage to the prepare job** — the workflow must NOT set
   `GITHUB_TOKEN`/`MCP_RELEASE_DO_UPLOAD`/`MCP_RELEASE_GITHUB_OWNER`/
   `MCP_RELEASE_GITHUB_REPOSITORY` on the prepare job; the `prepare` mode
   fails closed if any of those is present.

7. **Publisher.json must be a trusted read** — the publisher reads
   `Publisher.json` from the workspace at the checked-out event SHA. DevOps
   must ensure the publisher's workspace checkout uses the same exact
   `github.sha` that the closure's `expectedConfigShas` values were
   computed against.

The batch must NOT:

- Add producer `Config.fsx` evaluation back into the publication path.
- Add alias CLI modes that re-enable the credential-bearing transaction
  subprocess or the legacy `orchestrate` surface.
- Add registry, plugin, or generic framework code to the release pipeline.
- Touch the Telegram source, Telegram workflows, or unrelated producer
  packaging.

## Material design decisions that depend on DevOps / cannot be implemented
inside this batch

- The fresh-job workflow wiring (item 1 above) requires changes to
  `.github/workflows/release.yml`, which is outside the production-code
  scope of this batch.
- Regenerating `Publisher.json` at the trusted event SHA (item 2) requires
  DevOps-side tooling (a `git rev-parse` + SHA-256 + JSON write step) that
  must be added to the workflow before the publisher job runs.
- The `expectedComponentIds` map in `Publisher.json` currently contains
  zeroed SHA placeholders; the DevOps batch must populate them with the
  exact canonical Config.fsx SHAs at the trusted event SHA before the
  publisher can run end-to-end.

These three items are explicitly **BLOCKED** by missing platform-side
infrastructure outside the production-code scope. The code-batch
delivers everything inside `infrastructure/release/` that is required for
the next batch to wire those platform-side items.

## Explicitly unresolved items recorded in this batch

The following items are intentionally NOT solved by this code-batch and must
NOT be presented as solved. Each is a known gap that an independent
architecture checkpoint (C1–C7) flagged as part of the larger SEC1
amendment; the current batch fixes C1 and the other gaps remain.

### C1 — `byteSafeObserver` candidate-as-remote REMOVED

The prior batch's `ReleaseTransaction.byteSafeObserver` (which substituted
the candidate's archive and manifest bytes for the remote observation) and
the parallel `run` / `runFromBytes` / `executeFromBytes` implementations are
gone. The canonical transaction entry is now `ReleaseTransaction.run` (renamed
from `runFromBytes` per the canonical name rule); it uses the genuine HTTP
remote observer (`buildRemoteObserverWith`) so the candidate is never observed
as the remote; the remote revision comes from `publishedObs`, not the
candidate. A single canonical executor (`execute`, private) consumes the
bounded snapshot from `VerifiedCandidate` and uploads the verified bytes
directly via `uploadCandidateBytes` / `uploadMetadataBytes`; no mutable
on-disk path is reopened between verification and upload.

**Runner canonical integration**: the Runner's `publish` mode now builds
the verified snapshot via `ReleaseEvaluate.VerifiedCandidate.create(policy,
archiveBytes, manifestBytes, archiveName, evaluatedHead,
metadataLocalPath, owner, repositoryName)` and passes the resulting
`VerifiedCandidate` to `ReleaseTransaction.run`. The transport is composed
AFTER the verified candidate exists. The prior manual digest comparison,
embedded-manifest byte equality check, embedded-revision check, and
DTO/projection construction in Runner have been replaced by the private
factory call.

**Defensive copies**: `VerifiedCandidate.create` calls `Array.copy archiveBytes`
and `Array.copy manifestBytes` before any hash / parse work. The
`VerifiedCandidate` snapshot is therefore intrinsically immutable: even if
the caller mutates the original `byte[]` after the factory returns, the
`VerifiedCandidate` still holds the verified snapshot bytes. The publisher
also passes the verified bytes directly to `uploadCandidateBytes` /
`uploadMetadataBytes` (no second disk read between verification and upload).

**Build run ID** (this batch): `SBJhbHOMDdVvCybdAWyII7WzSgkE2bFeqXnBsOZk6og`
from `dotnet build infrastructure/release/Release.fsproj -c Release` (succeeded,
0 errors, 0 warnings).

**Policy.fs compile fix**: the prior partial state of `Policy.fs` line 262
contained a nested interpolated expression inside a single-quote string,
which F# rejects (`FS3373 Invalid interpolated string`). The fix extracted
the `String.concat ", " requiredFields` call into a `let requiredFieldsList =
...` binding and interpolated it as `[{requiredFieldsList}]`. The Policy.fs
batch remains the owner of that file; this was a compile-only edit performed
because the diagnostic blocked the build.

### C2 — `parse(path)` is a temporary back-compat seam, not a final design

`ReleasePolicy.parse(sourcePath)` reads the file from disk and delegates to
`parseBytes`. It is kept ONLY because the un-migrated `Runner.fs` and
`Orchestration.fs` callers still call `parse(path)`. The Caller batch owns
the migration of those callers to `parseBytes(sourcePath, preVerifiedBytes)`
and the subsequent removal of `parse(path)` from the public surface.

### C2 — `parse(path)` is a temporary back-compat seam, not a final design

`ReleasePolicy.parse(sourcePath)` reads the file from disk and delegates to
`parseBytes`. It is kept ONLY because the un-migrated `Runner.fs` and
`Orchestration.fs` callers still call `parse(path)`. The Caller batch owns
the migration of those callers to `parseBytes(sourcePath, preVerifiedBytes)`
and the subsequent removal of `parse(path)` from the public surface.

### C3 — `Publisher.json` `expectedConfigShas` are zero placeholders

The DevOps workflow must populate `Publisher.json.expectedConfigShas` with
the exact canonical `Config.fsx` SHAs at the trusted event SHA before the
publisher can run end-to-end.

### C4 — Fresh-job workflow wiring

`.github/workflows/release.yml` still references the legacy
`ReleaseRunner.exe orchestrate "..."` invocation. The DevOps batch owns
the split into a prepare job (no publication authority) and a fresh
publisher job (with `contents: write` and `GITHUB_TOKEN`).

### C5 — Protected-download interface for hosted fetches

If the platform downloads the candidate archive in a separate step, the
download step MUST validate the archive against `ReleaseArchive.parse`
BEFORE the publisher observes it. The interface for that protected
download is still to be defined; the Caller / DevOps batches own it.

### C6 — `ReleaseOrchestration.orchestrate` is gone; tests don't compile yet

The legacy `ReleaseOrchestration.orchestrate` function and
`ReleaseRunner orchestrate` mode have been removed per boundary decision 4.
`infrastructure/release/tests/*.fs` references the removed surface and
will not compile against the current `Release.fsproj`; the tester batch
owns the migration of those scenarios to `prepare` / `publish`.

### C7 — Independent security / architecture / acceptance review

The next stage after the Caller batch is the independent security,
architecture, correctness, and acceptance reviews of the full SEC1
amendment. No engineer APPROVE as review readiness is asserted by this
batch.