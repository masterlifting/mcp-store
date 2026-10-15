# GHI-24 remediation evidence

This file is pass-1 remediation evidence appended per slice. It records facts and
limits; it is not a second task-state authority. `docs/GHI-24/original.md`,
`docs/GHI-24/solution.md`, and `docs/GHI-24/findings.md` remain authoritative.

## Slice F4 — strict raw ZIP32 comparison and resource bounds

Scope: accepted finding F4 only (correctness D4, validator R4, security S4).
Files changed: `infrastructure/release/Compare.fsx`,
`infrastructure/release/Archive.fsx` (new reusable helper),
`infrastructure/release/Release.fsproj` (compile `Archive.fsx` before `Compare.fsx`).
No transport, Runner, CI, consumer, Telegram, harness, or test file was edited.

### Canonical layout observed on real assets (read-only)

`Archive.fsx` parses `dotnet/dist/v1.0.4.zip`, `workflow/dist/v1.0.4.zip`, and the
historical `dotnet-v1.0.3.zip` / `workflow-v1.0.3.zip`. All four are ZIP32 with six
entries, version 20/20, flags 0, method 8 (Deflate), `1980-01-01` timestamps, no
extra fields, comments, data descriptors, ZIP64, or trailing bytes. Local and
central headers agree byte-for-byte, and the central directory abuts the EOCD.

### Implementation

- `Archive.fsx` validates the raw bytes: EOCD position/comment/trailing coverage,
  multi-disk and ZIP64 rejection, central/local signature and field agreement,
  contiguous local headers from byte zero, name bytes and uniqueness, and payload
  coverage with no gaps or overlaps. It rejects encryption/patch/UTF-8 flags,
  non-Deflate methods, extra fields, comments, non-zero attributes, and non-ASCII
  names as unsupported layouts instead of normalizing or repacking them.
- Resource bounds are applied before allocation/decompression: archive size,
  entry count, per-entry and total uncompressed size, and a 200:1 compression
  ratio. `decompress` caps actual output at the declared size and verifies length
  and CRC-32, so a lying header cannot expand past the bound.
- `Compare.fsx` compares non-manifest entries in raw order over every stored field
  plus the compressed payload bytes and the decompressed payload SHA-256.
- The manifest entry compares every non-derived field; only CRC, compressed size,
  uncompressed size, and consequent offsets may differ. It re-proves the stored
  bytes are `Deflate(CompressionLevel.Optimal)` for their content, rejects
  duplicate JSON keys, and requires one top-level 40-hex revision. Full manifest
  byte substitution over that single revision must reproduce the candidate bytes
  exactly, so unknown fields, formatting, and key order cannot drift.
- Observations self-validate before use: exactly one manifest entry, canonical
  schema, `archiveFiles` equals the actual entries, and every declared `files`
  hash matches the actual decompressed payload. Declared hashes are never trusted.
- Exact equality is re-validated through the same allowlist/manifest/payload path
  before returning `ByteIdentical`; it is not a shortcut past malformed input.
- `validate` is public and usable before any remote publication; it checks the
  contract allowlist and manifest identity.

### Build and analyzer evidence

| check | runId / result | outcome |
| --- | --- | --- |
| `dotnet_build infrastructure/release/Release.fsproj -c Release` | `eQm2j2Kf2maE69o4lsC54GVf7dOmFfFEFrl-Wy5cmwM` | exit 0, 0 errors / 0 warnings |
| `infrastructure/scripts/Validation.fsproj` build | `fumkwrotqcdE-Rm5N3vcW0_4LPKcIn3440Pku09Z0gY` | exit 0, 0 errors / 0 warnings |
| `Validation validate --repo-root ...` | `OK 48 scripts validated` (was 47) | static + reusable/test-helper typecheck lanes pass |

### Implementation-native package evidence (19/19)

A bounded read-only FSI harness parsed the actual checkpoint and historical
archives and exercised the real `ReleaseCompare`/`ReleaseArchive` code:

- real `dotnet`/`workflow` v1.0.4 and historical v1.0.3 assets parse with all raw
  metadata retained and manifest hash present;
- v1.0.4 vs itself is `ByteIdentical` and reports the published sha256;
- a candidate derived from the real v1.0.4 archive by changing only the top-level
  revision is `RevisionOnlyEquivalent`, reporting the published revision and the
  evaluated revision separately;
- a changed manifest field (`sdk`) and a changed non-manifest payload byte are
  both `DifferentBeyondRevision`;
- a flags-zero archive whose manifest uses a non-canonical Deflate encoding fails
  closed; a data-descriptor flag fails closed; duplicate JSON keys, trailing
  bytes, an entry-count overflow (4097), and a deflate bomb are all rejected.

### Limits and non-claims

- No remote download, upload, publish, commit, or `.git` mutation was performed.
- No regression suite or test file was run or edited; committed-package retest
  awaits a later separately confirmed commit.
- Real hosted queue/redirect behavior and the actual GitHub asset download remain
  out of this slice and are covered by other findings.
- Live validators in `Publish.fsx`/`Runner.fs` still call only `observe`/`compare`;
  wiring the public `validate` into the absent-asset path is a bounded follow-up
  outside the F4 edit scope.

### Tester fixture guidance

Add deterministic fixtures for: raw revision-only equivalence (compressed size
may change), unknown manifest field, duplicate JSON key, non-canonical manifest
Deflate with flags 0, data-descriptor/encryption/ZIP64/trailing-byte archives,
duplicate or non-ASCII entry names, entry-count/size/ratio bounds, and
manifest/payload hash self-consistency mismatch. Fixtures must carry real payload
hashes in `files` because observations now cross-check them.

## Slice F1/F3/F5/F6 — transport identity, post-POST observation, origin policy, immutable send buffer

Scope: accepted findings F1, F3, F5, F6 only. Files changed:
`infrastructure/release/Publish.fsx` only. `Comparison`/`Archive` (F4) were read
and not changed; `Runner.fs` was not changed because the public signatures
(`gitHubObserver`, `uploadCandidate`, `uploadMetadata`, `ReleaseLocator`,
`RemoteState`, `RemoteObserver`, `defaultHttpTransport`) are preserved. No test,
YAML, PowerShell, consumer, Telegram, harness, or config file was edited.

### F1 — metadata asset identity

`uploadMetadata` now uses `ReleaseMetadata.metadataAssetName componentId version`
(e.g. `dotnet-1.0.4-metadata.json`) for the lookup, the download asset, and the
POST `?name=`; the projection's `ArchiveName` remains the ZIP name used only for
the archive identity and the production projection.

### F3 — post-POST observation and exact byte verification

One shared `uploadBytes` path drives both the archive and the metadata. For the
target asset name it: (1) GETs the release; (2) if the asset is present,
downloads the exact bytes and verifies SHA-256, byte-equality, and content
(archive validate / metadata parse+compare), returning the verified digest or a
typed *refusing to overwrite* failure; (3) if absent, POSTs the immutable buffer
and then always re-GETs the release and re-downloads/verifies before success.
Any POST outcome (2xx, 4xx/5xx, timeout) leads to the bounded remote observation;
an unobservable/absent asset fails without a second POST. Existing same-name
bytes are idempotent only when exact; otherwise the run fails closed.
`parseRelease` rejects duplicate asset names and unsupported/unknown shapes.

Deterministic call order (for the tester's scripted handler):

- idempotent exact: `GET release` -> `GET/download asset` -> Ok (2 requests).
- overwrite refusal: `GET release` -> `GET/download asset` -> Error (2 requests).
- publish: `GET release` -> `POST upload` -> `GET release` -> `GET/download asset` -> Ok (4 requests).
- unresolved: `GET release` -> `POST upload` -> `GET release` -> Error (3 requests, <=1 POST).

### F5 — origin policy and bounded redirects

All default clients use `HttpClientHandler` with `AllowAutoRedirect = false`.
Every GET runs a bounded manual redirect chain (301/302/303/307/308, limit 5)
that follows only exact HTTPS authorities: token origins `api.github.com` /
`uploads.github.com` (token sent) and the release-asset CDN
`release-assets.githubusercontent.com` (token dropped). Suffix hosts, HTTP,
non-default ports, and userinfo are rejected; error text uses scheme/host/port/
path only, never the signed query or token. POST is never redirected: a 3xx POST
response is a typed failure. `objects.githubusercontent.com` was not added because
no evidence supports it; it can be added later behind the same exact-authority
check. All response bodies are read under a hard cap (JSON 4 MiB, metadata asset
1 MiB, archive `ReleaseArchive.maxArchiveBytes`).

### F6 — immutable send buffer

`uploadCandidate` snapshots the candidate bytes, recomputes SHA-256, and refuses
before any request when it differs from the caller's validated digest; the
snapshot buffer itself is transmitted. `uploadMetadata` renders once, hashes
those bytes, and transmits the same buffer.

### Build and analyzer evidence

| check | runId / result | outcome |
| --- | --- | --- |
| `dotnet_build infrastructure/release/Release.fsproj -c Release` | `xa7-0VhmVEfQDrlqsmL2z5UfpYnyCw0QAt8bVNbFDdQ` | exit 0, 0 errors / 0 warnings |
| `Validation validate --repo-root .` | `OK 48 scripts validated` | static + reusable/test-helper typecheck lanes pass |
| `Validation typecheck` | `OK reusable and test-helper roots typechecked` | exit 0 |

### Implementation-native transport evidence (20/20)

A bounded read-only FSI harness (outside the repository) drove the real
`ReleasePublish` functions through an injected scripted handler and the real
`dotnet/dist/v1.0.4.zip`, asserting: F6 snapshot mismatch returns a typed error
with zero requests; F3 mismatch refuses overwrite in exactly lookup+download; F3
publish observes the remote after POST and returns the verified digest (4
requests); F3 ambiguous timeout is recovered by observation; F3 unresolved fails
with <=1 POST; F1 metadata POST targets `dotnet-1.0.4-metadata.json` (never the
zip name) and observes after POST; F5 CDN redirect sends the token to the API
origin, drops it at `release-assets.githubusercontent.com`, and still digest-
verifies; F5 unsafe redirect is rejected with no egress.

### Limits and non-claims

- No remote write, release, commit, `.git` mutation, or secret read was performed.
- The tester suite was not run or edited; its existing scripts encode the prior
  {{GET, POST}} sequences and will be realigned to the call order above by the
  tester's next slice.
- Live GitHub behaviour (real CDN host, real hosted redirect, real queue) remains
  out of this slice.

### Remaining Runner obligations (next slice)

- F2: reconcile the manifest schema and missing immutable metadata on the no-op
  path; keep published hashes/revision distinct from the evaluated HEAD.
- F7: emit structured failure JSON before the nonzero exit so the wrapper keeps
  status/reason and published-vs-candidate evidence.
- F9: carry the canonical asset URI and validated identity through the
  transaction instead of reconstructing it from the environment.

## Slice F2/F4-wire/F7/F9 — schema correctness, validate gating, JSON-before-exit, canonical URI

Scope: accepted findings F2, F7, F9 and the F4 validate-gate wiring. Files
changed: `infrastructure/release/Compare.fsx` (additive embedded-manifest
header only), new `infrastructure/release/Evaluate.fsx`,
`infrastructure/release/Runner.fs`, `infrastructure/release/Release.fsproj`
(compiles `Evaluate.fsx` before `Runner.fs`),
`dotnet/release/Release.fsx`, `workflow/release/Release.fsx`,
`infrastructure/release/README.md`. No transport, Archive, Compare behavior,
PowerShell, YAML, consumer, Telegram, harness, or test file was edited.

### F2 — DISTRIBUTION schema and immutable metadata reconciliation

`Compare.fsx` exposes a strictly additive `parseEmbeddedManifestHeader` /
`parseEmbeddedManifestEntry` helper that returns the DISTRIBUTION schema's
`Id`, `Version`, `Archive`, `Revision`, `ArchiveFiles`, and file-count from
the embedded `distribution.json` body. The Runner never invokes
`ReleaseMetadata.parse` against this body again; the F2 schema cross-wiring
that previously surfaced as `the observed published manifest did not parse as
the current projection schema` and exited 1 cannot recur.

`Evaluate.fsx` introduces the `metadata-recovery-needed` outcome. After the
comparison declares `Unchanged`, the evaluator probes the immutable metadata
asset on the remote. The published `archiveSha256`/`manifestSha256` from the
candidate render are compared with the remote metadata projection; mismatch,
absence, or any parse failure flips the unchanged classification to
`metadata-recovery-needed` with the precise field delta in the diagnostic.
When recovery is required, the recovery path republishes only the metadata
asset; the archive is never re-uploaded and the `recovered` status reports the
verified remote digest. Published archive sha256, manifest sha256, and
revision remain read from the observed archive bytes, never from the
candidate values.

### F4 wiring — `validate` ahead of every remote effect and upload

`ReleaseCompare.validate` runs on the candidate observation inside
`ReleaseEvaluate.evaluate` BEFORE the existing `ReleaseTransaction.run`
remote-observation path. The `ReleaseRunner` `--version` flow and the new
`Evaluate.fsx` shared API share the same gate, so a candidate that fails the
allowlist, hash-identity, or DISTRIBUTION manifest contract cannot reach
either the GitHub upload, the mirror upload, or the metadata probe. The
existing compare-then-upload order preserves the clean-HEAD / manifest
revision / event-SHA agreement asserted at the runner's startup.

### F7 — JSON before every exit, concurrent drains, parses even on nonzero

The Runner renders the final `ReleaseEvaluate.Resolution` JSON to stdout
THEN exits. `emitAndExit` writes through `Console.OpenStandardOutput`,
flushes, and only then calls `exit`. The empty-argv `exit 2` usage line is
retained. Every failure and bump-required branch produces the same
indent-rendered JSON on stdout as the success branches.

The per-component `Release.fsx` wrapper switched from sequential
`StandardOutput.ReadToEnd()` blocking reads to `OutputDataReceived` /
`ErrorDataReceived` event drains that start before `WaitForExit()`. Both
streams are drained concurrently so a half-closed pipe cannot deadlock the
child. The wrapper then parses stdout as JSON regardless of the exit code,
rendering status and reason even on nonzero. The status-to-exit-code map adds
`recovered → 0`; the documented mapping now distinguishes
`publish-pending`, `bump-required`, `failed`, `metadata-recovery-needed`,
`recovered`, `unchanged`, and `published`.

### F9 — canonical assetUri in the contract DTO; fixed-policy locator

The per-component `Release.fsx` script now carries `ReleaseConfig.assetUri`
verbatim into the contract JSON. The Runner parses `assetUri` from the DTO
and refuses to silently reconstruct it from environment variables; the
`assetUri is empty` failure surfaces as a typed error before any candidate
read. `masterlifting/mcp-store` is the fixed-policy repository locator and
the runner never substitutes environment values for either field.

### Shared evaluation helper

`infrastructure/release/Evaluate.fsx` is the new one-word shared module.
It exposes a typed `CandidateDto`, an `RemoteObserver` with separate
`ObserveArchive` and `ObserveMetadata` injection points, and a `Resolution`
that captures status/reason/published hashes/revision/assetUri and a
`recoveryNeeded` flag. The Runner's CLI shell is now a thin top-level that
builds the `CandidateDto`, calls `ReleaseEvaluate.evaluate`, renders the
`Resolution` JSON, and dispatches the upload/recovery branches. The shared
test seam means the tester can exercise exact-equality, revision-only,
recovery-needed, and failure-JSON outcomes against an in-memory observer
without an authenticated child process.

### Build, analyzer, and smoke evidence

| check | runId / result | outcome |
| --- | --- | --- |
| `dotnet_build infrastructure/release/Release.fsproj -c Release` | `0G4a0zSDTJ5F2F_79AsQCgJEzz_aLDVdJqBVC23wHkU` | exit 0, 0 errors / 0 warnings |
| `dotnet_build infrastructure/scripts/Validation.fsproj -c Release` | `rgwdZvChtAd176yz9w9xg3-8N167wlWZ4NJfO6Wn7tY` | exit 0, 0 errors / 0 warnings |
| `Validation validate --repo-root ...` | `OK 49 scripts validated` (was 48) | static + reusable/test-helper typecheck lanes pass |
| `Validation typecheck --repo-root ...` | `OK reusable and test-helper roots typechecked` | exit 0 |
| `ReleaseRunner.exe` empty argv | `usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>` and `exit 2` | usage retained |
| `ReleaseRunner.exe` candidate invocation on the dirty working tree | `clean-tree guard: refusing to package from a dirty source tree` and `exit 4` | F4 invariant intact |

### Limits and non-claims

- No remote write, release, commit, `.git` mutation, or secret read was
  performed. The metadata probe production stub reports
  `MetadataAbsent` until a sibling implementation in `Publish.fsx`
  validates JSON-shaped assets; `Evaluate.fsx` already supports the typed
  observer seam so that wire-up is additive when it ships.
- The existing `infrastructure/release/tests/*.fsx` files were not run or
  edited; the `Transaction` shape they exercise remains intact. New
  regression coverage for the F2 reconciliation, F4 validate gate, F7
  failure-JSON, F9 canonical-URI, and the four-headed `Evaluate` outcomes
  is the tester's next slice.
- The `recovered` status is a new outcome on the runner's CLI; the legacy
  test fixture (Runner test #5) only exercises empty-argv, contract shape,
  and the metadata asset name, all preserved unchanged.
- Authenticated live GitHub behavior (CDN host, real hosted redirect,
  queue) remains out of this slice.

## Slice pass-2 F2/F4/F7/F9 — production metadata observation, fail-closed, public-only statuses, fold routing

Scope: complete the F2 metadata observation actually, public-only status
mapping, F9 canonical URI validation, F4 validate-gate wiring, F7 JSON-on-
failure wrappers, and the architecture simplification that folds the
obsolete `Transaction` / `Decide` routing into a single decision site in
`Evaluate`. No compatibility aliases were introduced.

### Removed modules / project-loads (no aliases)

- `infrastructure/release/Transaction.fsx` — removed. Decision site folded
  into `ReleaseEvaluate.classifyInternal`, which talks to `ReleaseCompare`
  directly. The Runner and the tests that previously called
  `ReleaseTransaction.run` must drive `ReleaseEvaluate.evaluate` instead.
- `infrastructure/release/Decide.fsx` — removed. The
  `Unchanged`/`Publish`/`BumpRequired` mapping lived on top of `Compare`
  with no transport state and added no behavior beyond labeling. `Evaluate`
  consumes `Compare.Decision` directly and emits the public status from
  one match.
- `infrastructure/release/tests/Transaction.fsx`,
  `infrastructure/release/tests/Runner.fsx` — not edited; they currently
  fail to load because the removed modules are referenced via `#load`.
  The test owner migrates these next.

### F2 — production metadata observation via `gitHubMetadataObserver`

`infrastructure/release/Publish.fsx` gains a sibling to `gitHubObserver`
for the JSON-shaped metadata asset. `gitHubMetadataObserver` shares the
bounded transport, `getRelease`, `findUniqueAsset`, `downloadAssetBytes`,
and `sha256Hex`; adds `RemoteMetadataState`, `RemoteMetadataObserver`, and
a `MetadataObserved (body, sha256, parsed)` / `MetadataAbsent` /
`MetadataMissing` / `MetadataImmutable` surface.

`RunOptions.ObserveMetadata` in `Runner.fs` now calls the real
`gitHubMetadataObserver`. Metadata observation errors do not fold into
`MetadataAbsent`; the new `Evaluate.MetadataCorrupt` case propagates them
as `failed` with the release-error message preserved. A verified
`MetadataAbsent` from the GitHub transport is the only signal that
permits a metadata-only recovery upload (no archive upload, no
"repair" of corrupt or mismatching metadata, no overwrite of an
immutable metadata asset).

### F4 — validate-gate wiring

`Compare.validate` runs on the candidate observation inside `Evaluate`
BEFORE `ObserveArchive` and BEFORE `ObserveMetadata`. The Runner CLI
still asserts `clean tree / committed HEAD / manifest revision ==
HEAD` at startup; the `Evaluate` gate is the explicit fail-closed
surface for absent input.

### F7 — JSON-on-failure wrappers

`Runner.fs` renders the JSON object on stdout before every exit
(success, failure, bump-required). `emitAndExit` writes through
`Console.OpenStandardOutput`, flushes, and only then calls `exit`. The
empty-argv `exit 2` usage line is retained.

The per-component `dotnet/release/Release.fsx` and
`workflow/release/Release.fsx` parse stdout JSON even on nonzero exit;
the published/reason/version/URI/name/revision/hashes fields remain
parseable in every branch.

### F9 — canonical assetUri validation

`ReleaseEvaluate.validateCanonicalAssetUri` rejects empty or mismatched
assetUri before any candidate read. The expected URL is
`https://github.com/<RepositoryOwner>/<RepositoryName>/releases/download/<ComponentId>/<ArchiveName>`. The runner never reconstructs assetUri
from environment; the fixed-policy locator is `masterlifting/mcp-store`.

### Public-only status mapping

| Internal branch (Evaluate classification) | Public `Status` | Reason carries |
| --- | --- | --- |
| ArchiveAbsent + transport available | `published` | upload report |
| ArchiveAbsent + no transport | `failed` | "publish-required but no transport is configured" |
| ArchiveMissing / ArchiveImmutable | `failed` | release container state |
| Compare.DifferentBeyondRevision | `failed` (with `bumpedVersion` populated) | full diagnostic; matches the existing `BumpRequired` reason |
| Compare.ByteIdentical/RevisionOnlyEquivalent + MetadataObserved matches candidate | `unchanged` | strict comparison summary |
| Same archive equality + MetadataAbsent (verified) | `unchanged` with `metadataRecoveryNeeded: true` | "metadata upload required to reconcile the verified absence" |
| Same archive equality + MetadataAbsent + successful recovery upload | `unchanged` (`uploadCompleted: true`) | "metadata republished to reconcile the verified absence" |
| Same archive equality + MetadataCorrupt / transport error | `failed` | typed ReleaseError message |
| Same archive equality + MetadataImmutable | `failed` | "no repair allowed" |
| Same archive equality + MetadataObserved field mismatch | `failed` | `IdentityMismatch` field name |

On every `failed` branch, every `publishedArchiveSha256`,
`publishedManifestSha256`, `publishedRevision`,
`publishedMetadataSha256` is emitted as `null`; the candidate hashes
never masquerade as published values. The exact-equality and
revision-only-equivalence paths populate them from the observed remote
archive, so a verified unchanged run retains the published revision and
hash even after a metadata-only recovery.

### Header / comment style

`Evaluate.fsx` opens with `// role: reusable module/helper` and three
why-only lines. The header no longer narrates the F2/F7/F9 finding
identifiers; narrative comments inside the body were replaced with
single-line why notes (e.g. `metadata observation errors fail closed`,
`verified absence is the only signal that permits a metadata-only
recovery`).

### Build / analyzer / smoke evidence

| check | runId / result | outcome |
| --- | --- | --- |
| `dotnet_build infrastructure/release/Release.fsproj -c Release` | `85yKYxzXf7EWtvWW-WovgQGHMZaJbmJ0Z0CqtTEUu1M`, confirmation `3XKvTAIu96xdZYteZe-rr2HjYWYby3iNknkLtydD4uo`, `Fwgawnle0KjGFQlr5xxKsD-cub6-nWtmDhC_JuN5l9I` | exit 0, 0 errors / 0 warnings (after the runner rebuild loop closed) |
| `dotnet_build infrastructure/scripts/Validation.fsproj -c Release` | `_uNR34l8uMj4OReTQQsEMJ1z5A23gnrRVz0OleX-gmo`, `euouprv5RVMu8P8_T8QNEqnvut8Fdk5jXwecIT0GDHs` | exit 0, 0 errors / 0 warnings |
| `Validation validate --repo-root ...` | exit 5 (4 `MISSING_LOAD_TARGET` lines from the superseded `Decide.fsx` / `Transaction.fsx` `#load` directives the test owner migrates next; no other diagnostics) | expected test-migration signal |
| `Validation typecheck --repo-root ...` | exit 3 (same 4 migration-only `MISSING_LOAD_TARGET` diagnostics) | expected test-migration signal |
| `ReleaseRunner.exe` empty argv | `usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>` and `exit 2` | usage retained |
| `ReleaseRunner.exe` candidate invocation on the dirty working tree | `clean-tree guard: refusing to package from a dirty source tree` and `exit 4` | F4 invariant intact |

### Limits and non-claims

- The legacy `infrastructure/release/tests/*.fsx` fixtures were not
  edited. They will not load until the test owner updates them to
  `#load "Evaluate.fsx"` and drive `ReleaseEvaluate.evaluate` directly.
  This is the documented test-migration follow-up; no shim modules were
  added.
- Authenticated live GitHub behavior (real CDN host, real hosted
  redirect, real queue) remains out of this slice.
- Live `dotnet fsi` regression for `tests/Compare.fsx`,
  `tests/Publish.fsx`, etc. is not exercised by this slice; the test
  runner owner picks those up with the test migration.

## Slice pass-2 F8/F7 — CI regression gate, boundary recheck, structured result

Scope: accepted findings F8 (DEVOPS-3/4) and F7 (validator R7) only. Files
changed: `.github/workflows/release.yml`, new
`infrastructure/release/Suite.ps1`, `infrastructure/release/README.md`.
`Update.ps1` was not needed. No production F#, test, config, consumer,
Telegram, harness, or `.git` edit.

### F8 — non-publishing regression suite gate and boundary recheck

- New read-only `tests` job (`contents: read`, no token, no
  `MCP_RELEASE_DO_UPLOAD`) checks out the exact event SHA, installs the
  canonical SDK, builds `Release.fsproj`, and runs
  `infrastructure/release/Suite.ps1` non-publishing. `Suite.ps1` runs every
  deterministic `tests/*.fsx` except the optional historical integration that
  needs locally downloaded archives, reads each child exit code, and exits
  nonzero if any fails.
- `release` job now `needs: [bootstrap, tests]`, so a failed suite blocks
  every upload for both producers; the write job remains the only job with
  `contents: write`.
- The release job re-proves `git rev-parse HEAD == event SHA` and the
  `event SHA` safe-ancestor relationship against the API `main` tip at the
  publication boundary, immediately before the transaction. The bootstrap
  result is not cached across the test gate.

### F7 — structured failure result in summary and artifact

- The summary/artifact step parses the entrypoint's structured `key=value`
  result on every exit code, including nonzero, and writes the typed fields
  (reason, version, archiveName, assetUri, candidate/published hashes,
  revisions, evaluated head, producer inputs) to
  `release-status-<component>.json`.
- A bare exit code is no longer mapped to a guessed status: a missing
  structured `status` line records `structuredResult=false` with an explicit
  reason, never `not-published-exit-2`.
- `verified` is true only for `published`/`unchanged` with observed remote
  values; on failure the `published` projection is `null` so a candidate hash
  cannot be presented as verified. The verified metadata projection is copied
  only after a verified remote archive and metadata observation; the candidate
  pins artifact stays separate and distinctly labelled.

### Build / static evidence

| check | result | outcome |
| --- | --- | --- |
| YAML `safe_load` on `release.yml` | parses | exit 0 |
| PowerShell `[Parser]::ParseInput` over every `run:` block (GitHub placeholders replaced) | 5/5 blocks parse | exit 0 |
| `Suite.ps1` `[Parser]::ParseFile` | parses | exit 0 |
| executable `release.yml` lines containing `telegram` / `telegram/` / `uv run pytest` | 0 | isolation invariant holds |

### Limits and non-claims

- The tester-owned `infrastructure/release/tests/*.fsx` suite was not executed
  and no test file was edited. `Runner.fsx`/`Transaction.fsx` still reference
  the removed `Decide`/`Transaction` modules, so the gate is wired ahead of the
  test-API migration and passes once the tester completes it.
- No remote write, release, commit, `.git` mutation, or secret read was
  performed; authenticated hosted GitHub behavior remains out of this slice.

## Slice pass-3 F2/F7 — verified-projection persistence, structured-failure wrappers, comment hygiene

Scope: complete the F2 verified-projection persistence, wire F7 structured
JSON before every Runner / wrapper failure exit, and clean the
multiline-history comments that accumulated during the prior passes. No
new layers, no compatibility aliases, no generic abstractions.

### F2 — projection rebuilt from verified/observed values only

`ReleaseRunner.executeResolution` no longer prewrites a candidate
projection to disk. The local `metadataLocalPath` file is written only
inside the success / recovery branches and is constructed from values
that the runner actually verified:

- The `published` branch captures the verified remote archive digest
  from `uploadCandidate` and the verified remote metadata digest from
  `uploadMetadata`. The final `Render.CandidateArchiveSha256` is
  reassigned to the remote-verified archive sha so the JSON never
  reports the on-disk candidate sha as if it were the published sha;
  the `published*` keys are populated from the verified remote values
  and the verified metadata sha is recorded under
  `PublishedMetadataSha256`. The local metadata file is rewritten
  through `ReleaseMetadata.writeToFileAsync` using the same projection.
- The `unchanged` clean branch (no recovery) rebuilds the metadata file
  from the observed published archive sha, manifest sha, and revision,
  so the on-disk file mirrors the remote state byte-for-byte. The
  `published*` JSON keys are populated from the observed values.
- The `unchanged` recovery branch (verified absence) reconstructs the
  projection from the observed published archive sha, manifest sha,
  and revision (NOT candidate hashes/revision), uploads the metadata
  through `ReleasePublish.uploadMetadata`, captures the verified remote
  metadata digest, and writes the projection so the local file matches
  what is now published.
- Failure branches never persist a metadata file, so the on-disk
  record only reflects values the runner verified or successfully
  uploaded.

### F7 — structured JSON before every failure exit

`ReleaseRunner.main` parses the contract up front so every structured
failure emits a JSON object with `status: "failed"`, the typed
`reason`, and the config context
(`componentId`, `version`, `archiveName`, `assetUri`) whenever the
contract has been read. The argv-count / `usage` path is the only
exception and retains the documented `exit 2`. Each previously bare
`eprintfn ... exit` site was replaced with `emitAndExit failedWithConfig
...` using `failedWithConfig` and `typedFailedWithConfig` helpers, so:

- dirty tree / committed HEAD / contract parse failures
- `assetUri is empty` from environment-rejection path
- `manifest not found` / `archive not found`
- manifest revision vs HEAD mismatch
- hash failures for the candidate archive / manifest
- metadata malformed-`revision` failure

all emit the same JSON shape with `publishedArchiveSha256`,
`publishedManifestSha256`, `publishedRevision`,
`publishedMetadataSha256` set to `null` (candidate hashes never
masquerade as observed values).

The per-component `dotnet/release/Release.fsx` and
`workflow/release/Release.fsx` wrappers now drain stdout/stderr
concurrently via .NET events (no `WaitForExit()`-then-read deadlock)
and emit a structured `phase`/`childExitCode`/`childStderr` JSON for
any `build`/`pins`/`tests` failure before exiting with the same code.
The wrapper summary text output still runs after the runner returns,
so its current behavior (parse JSON nonzero) is preserved.

### Comment style

- `Evaluate.fsx` opens with `// role: reusable module/helper` plus three
  why-only lines.
- Internal narrative blocks (the F9 / classification-site /
  verified-absence comments) were replaced with single-line why notes.
- `Runner.fs` historical comments at the prior `lines 228–231` and the
  4-line `persistVerifiedProjection` / 3-line `Local metadata write
  deferred` blocks were reduced to single-line why-only lines.
- `Compare.fsx` multiline additions (the public header / passthrough
  / entry parser) were collapsed to single-line why-only lines; the
  surrounding comments stay narrow.
- `Publish.fsx` 5-line `Sibling observation` and 3-line `Reuses`
  comments were reduced to single-line why-only lines.
- The per-component wrappers replace their multiline origin notes with
  short why-only lines tied to F7 / F9 / child-process isolation.

No change to the existing role-only headers on `Archive`,
`Bootstrap`, `Compare`, `Metadata`, `Publish`; those were not edited
in this slice.

### Build / analyzer / smoke evidence

| check | runId / result | outcome |
| --- | --- | --- |
| `dotnet_build infrastructure/release/Release.fsproj -c Release` | `tjmkkP1cBgq3tJo1k3kG6rsGpu2tpk1AQCPxQDACttA`, `31bc12Uns7Cjoz7x0exGKSAL8mUNmzDNUyBWg1rjlew`, `-2J-yv5ftx3w4H1rUR1xXf3Y5sks34cbvUBUmILZKU8`, `rTjH_VyYuIQhyQyaDbwlHCeYi5z7qakY6-B3AMCTEy0` | exit 0, 0 errors / 0 warnings on every rebuild after each pass-3 edit |
| `dotnet_build infrastructure/scripts/Validation.fsproj -c Release` | `o0e8Q4MEmp05ueTeU-7LqR5IhS9UJWO9RXme8uv6eBo` | exit 0, 0 errors / 0 warnings |
| `Validation validate --repo-root ...` | exit 5 (`MISSING_LOAD_TARGET` for superseded `Decide.fsx` / `Transaction.fsx` referenced by legacy test fixtures — migration work tracked for the test owner) | no other diagnostics |
| `ReleaseRunner.exe` empty argv | `usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>` and `exit 2` | usage retained |
| `ReleaseRunner.exe` candidate invocation on the dirty working tree | structured JSON `status: "failed"`, `componentId: "dotnet"`, `version: "1.0.4"`, `assetUri` populated, `publishedArchiveSha256: null` and other `published*` keys null, `exit 4` | F4 invariant intact, config context preserved |

### Limits and non-claims

- The tester-owned `infrastructure/release/tests/*.fsx` suite was not
  run and no test file was edited in this slice. The
  `tests/Runner.fsx`/`tests/Transaction.fsx` `MISSING_LOAD_TARGET`
  diagnostics still flag the test-migration follow-up.
- The metadata probe in the local-mirror path still surfaces
  `MetadataCorrupt` with a `metadata transport unavailable` typed
  reason; the production GitHub transport is the recovery-required
  path.
- Authenticated live CDN / redirect / queue behavior remains out of
  scope.

## Slice pass-4 F8/F7 — suite gate corrected, structured failure parsed, verified projection

Scope: validate the tester's out-of-scope `Suite.ps1` edit and finish the F8/F7
CI integration after the suite migration. Files changed:
`infrastructure/release/Suite.ps1` (corrected), `.github/workflows/release.yml`,
`infrastructure/release/README.md`. No test file was edited.

### Suite.ps1 correction (F8)

- Header reduced to three one-line points; the tester's two-line,
  ticket-bearing header and two-line guard comment became why-only single
  lines.
- The suite selects only `role: test entrypoint` scripts, so the new
  `Fixture.fsx` (`test helper`) is not run as a standalone case.
  `Historical.fsx` stays excluded as the optional, separately labelled
  integration that needs locally downloaded archives.
- The pollution guard now snapshots each output env var's target and bytes and
  fails if a child redirects or writes `GITHUB_ENV`/`GITHUB_OUTPUT`; the prior
  comparison used only the env var values and could not detect an append.

### F7 workflow integration

- The result step parses the wrapper's JSON failure object (emitted for its
  `build`/`pins`/`tests` phases) and the runner's `key=value` summary on any
  exit code, so a phase failure no longer degrades to "no structured status".
  Fields are mapped exactly as emitted: `status`, `reason`, `phase`,
  `childExitCode`, `childStderr`, `version`, `archiveName`, `assetUri`.
- `published`/`unchanged` copy the verified projection only when it carries
  `archiveSha256`, `manifestSha256`, and `publishedRevision` and its archive
  sha equals the observed published archive. The runner's emitted `published`
  JSON carries `publishedArchiveSha256`/`publishedMetadataSha256` but not the
  manifest sha or revision, so the workflow reads those two verified values
  from the projection file; `failed` stays `verified=false` with `published`
  null and candidate pins in a separate artifact.

### Build / analyzer / native evidence

| check | runId / result | outcome |
| --- | --- | --- |
| `dotnet_build infrastructure/release/Release.fsproj -c Release` | `y5XP41exAxPSMd5Su2kL0VDFfHW-88He2rUYeWhcpbk` | exit 0, 0 errors / 0 warnings |
| `dotnet_build infrastructure/scripts/Validation.fsproj -c Release` | `iXCRx4hSPiixcD25zMb8loukQfcGo1BvVGs_Uui3REM` | exit 0, 0 errors / 0 warnings |
| `dotnet_build dotnet/Mcp.Dotnet.fsproj -c Release` | `nzx683rhHMTyJAEv2fCKpMMRO1WyIIuxAhE9wt-v5is` | exit 0, 0 errors / 0 warnings |
| `dotnet_build workflow/Mcp.Workflow.fsproj -c Release` | `pFhQvCYNqyl5wXuMVVxhYZfVKQ0YEqdmmtiEo66pyFg` | exit 0, 0 errors / 0 warnings |
| `Validation validate --repo-root .` | `OK 48 scripts validated` | exit 0 |
| `Validation typecheck --repo-root .` | `OK reusable and test-helper roots typechecked` | exit 0 |
| `pwsh infrastructure/release/Suite.ps1` (proportional CI-helper native check) | `release regression suite passed: 8 deterministic script(s)`; 9+26+36+10+52+50+18+15 = 216/0 | exit 0; Fixture/Historical excluded |
| YAML `safe_load` + PowerShell `[Parser]` over every `run:` block (placeholders replaced) | parses; 5/5 blocks | exit 0 |

### Limits and non-claims

- Tester-owned tests were not edited. The regression suite was executed only as
  the proportional native check of the CI helper; no `dotnet_test` suite ran.
- The runner's emitted `published` JSON does not carry `publishedManifestSha256`
  or `publishedRevision`; the workflow sources them from the verified projection
  file rather than backfilling candidate values. This is a source-observed gap
  for the core owner.
- Authenticated live CDN / redirect / queue behavior remains out of scope.

## Slice pass-4 F2/F7 — verified metadata sha, completion helpers, comment hygiene

Scope: complete the F2 verified metadata sha propagation, the F7
published-branch `PublishedManifestSha256` / `PublishedRevision`
back-fill, the failure-render context preservation, and the Runner.fs:61
"what" comment. No new layers, no compatibility aliases, no generic
abstractions.

### F2 — verified remote metadata sha through the seam

- `ReleaseEvaluate.MetadataState` now carries the verified remote sha on
  `MetadataObserved of sha256: string * projection` (was just
  `of projection`). `classifyInternal` reads the carried sha into
  `PublishedMetadataAssetSha256`; it never substitutes `parsed.ArchiveSha256`
  and never re-renders the projection to guess the observed sha.
- `ReleaseRunner.runnerObserver` plumbing passes the
  `gitHubMetadataObserver` verified sha through directly:
  `MetadataObserved (_, sha256, parsed) -> MetadataObserved (sha256, parsed)`.
- Three pure completion helpers live inside `Evaluate.fsx` (no new
  module / no new framework / no state adapter): `finalizeFailure`,
  `finalizeVerifiedPublication`, `finalizeRecovery`. The Runner calls
  them after every verified upload and on every structured failure.
- `finalizeVerifiedPublication` derives `PublishedManifestSha256` and
  `PublishedRevision` from the validated snapshot (the
  `resolution.Render.CandidateManifestSha256` /
  `CandidateRevision`), which is bytes-identical to what
  `uploadCandidate` just observed remotely. `CandidateArchiveSha256`
  in the final `Render` is reassigned to the remote-verified archive
  digest so the JSON never reports the on-disk candidate sha as if it
  were the published sha.
- `finalizeRecovery` keeps status `unchanged`, refreshes only
  `PublishedMetadataAssetSha256` with the transport-verified sha, and
  drops `MetadataRecoveryNeeded`.

### F7 — failure renders preserve known context

Every Runner failure path now calls
`ReleaseEvaluate.finalizeFailure <render> <reason>`. The
`preMetadataRender` built at the top of `main` carries every
config-context field the contract has exposed
(`componentId`, `version`, `archiveName`, `assetUri`) so a
manifest-not-found / dirty-tree / hash failure still surfaces the
producer's identity. `published*` keys stay `null` on every
`finalizeFailure` path: candidate hashes cannot masquerade as
observed values.

### F2 / F7 — what-comments removed to comments.md style

- Runner.fs:61 "F9: failure branches leave `published*` keys absent
  ..." (what-narration with F9 finding id) collapsed to a single
  why-only line about preserving the determined Render.
- Evaluate.fsx multiline "Pure completion function for the failed branch"
  / "verified new-publish" / "verified-absence metadata-only recovery"
  three-line narratives reduced to one-line why notes prefixed with
  their F2/F7 finding rationale.

### Test migration (still pending)

The tester's `infrastructure/release/tests/Evaluate.fsx` and
`tests/Fixture.fsx` reference the old
`MetadataObserved projection` shape and the previously-private
`failureResolution`. They will not compile against the new API until
the test owner reruns the source baseline rerun. The `analyzer`
`Validation validate` keeps reporting the superseded-load-targets for
the legacy tests (which also reference the removed `Decide` /
`Transaction` modules from pass-2). These are reported, not masked;
no compatibility aliases were added.

### Build / analyzer / smoke evidence

| check | runId / result | outcome |
| --- | --- | --- |
| `dotnet_build infrastructure/release/Release.fsproj -c Release` | `cc6kRbMLGhmP6ZIb-gC2gf4HmhD05okdAG6CId_uO4k`, `LQFvY4c6T-iYJoraxREyHBAsJf4o4hwDc6i_HbaPneE`, `xGJK2vhsA36fofvJtOIW0hIx98Pr34Mbdv30TxvnAyw` | exit 0, 0 errors / 0 warnings after the API change rebuild |
| `dotnet_build infrastructure/scripts/Validation.fsproj -c Release` | `xT_WVEWHsdBFT9Fwsnd4slLkLylp6tIo9hLGavIxlLY` | exit 0, 0 errors / 0 warnings |
| `Validation validate --repo-root ...` | `OK 48 scripts validated` | 0 errors (88 down-from-89: dropped the comments at F4) |
| Empty-argv `ReleaseRunner.exe` | `usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>` and `exit 2` | usage retained |
| Dirty-tree candidate invocation | structured JSON, `status: "failed"`, `componentId: "dotnet"`, `version: "1.0.4"`, `assetUri` populated, every `published*` key `null`, `exit 4` | failure render preserves config context |

### Source contracts (post-slice)

- `ReleaseEvaluate.MetadataState` is now
  `MetadataObserved of sha256: string * projection: ReleaseMetadata.ProducerMetadataProjection`
  (tester fixtures need migration).
- `ReleaseEvaluate.finalizeFailure / finalizeVerifiedPublication / finalizeRecovery` are the only completion helpers;
  no new modules / framework / state adapters.
- `ReleaseCompare`, `ReleasePublish` (including `gitHubMetadataObserver`),
  `ReleaseRunner.runnerObserver` — no behavior change; only the
  metadata observer passes through the additional sha.
