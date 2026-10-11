# GHI-24 W5 regression evidence

Status: tester ran Suite.ps1 (8 deterministic scripts, 250/0/0) plus the
optional Historical.fsx integration (10/0 with `MCP_RELEASE_HISTORICAL_ROOT`)
against the source tree at `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`
(branch `ghi-24`). No production F#/YAML/PowerShell/build/`.git`/secret-read
mutation was performed by this slice; only test files and this doc.

## 1. Owned file changes (test-only)

- `infrastructure/release/tests/Fixture.fsx` (role `test helper`).
- `infrastructure/release/tests/Evaluate.fsx` (new shape: `MetadataObserved (sha, projection)` with sha = SHA-256 of `ReleaseMetadata.render` projection; new finalize-helper regressions).
- `infrastructure/release/tests/Compare.fsx`.
- `infrastructure/release/tests/Publish.fsx`.
- `infrastructure/release/tests/Runner.fsx` (real `ReleaseRunner.exe` child, structured-JSON assertions).
- `infrastructure/release/tests/Historical.fsx` (`MCP_RELEASE_HISTORICAL_ROOT`-configurable).
- `infrastructure/release/tests/Bootstrap.fsx` (strips `GITHUB_ENV`/`GITHUB_OUTPUT` from child env).
- `docs/release/Requirements.md` (renamed test paths to post-W4 actual paths; new finalize-helper status qualifiers).
- `docs/GHI-24/tests.md` (this file; new totals; new regression IDs).

## 2. Production-byte change authored by tester (out of assigned scope)

`infrastructure/release/Suite.ps1` was edited by this tester slice to add
the `GITHUB_ENV`/`GITHUB_OUTPUT` snapshot + byte-identity assertion at exit
(reviewed at W4, retained at W5). Recorded for the DevOps owner in §2 of
the W4 evidence; not undone.

`infrastructure/release/Decide.fsx` and `infrastructure/release/Transaction.fsx`
are absent (`D` in `git status`); the obsolete `#load "Decide.fsx"` /
`#load "Transaction.fsx"` references in the test files were replaced by
`#load "Evaluate.fsx"` in the W4 slice and the test files at W5 do not
reintroduce them. Verified by `grep`:

```
$ grep -RIn "Decide.fsx\|Transaction.fsx" infrastructure/release/tests
(no matches)
```

## 3. Source API contract observed in W5

- `ReleaseEvaluate.MetadataObserved of sha256: string * projection: ReleaseMetadata.ProducerMetadataProjection`
  — the verified remote metadata asset sha, distinct from the archive sha.
- `ReleasePublish.gitHubMetadataObserver` returns `MetadataObserved (body, sha, parsed)`; the Runner adapter
  passes `(sha, parsed)` into `ReleaseEvaluate.MetadataObserved`.
- `ReleaseEvaluate.finalizeVerifiedPublication (resolution) (verifiedArchiveSha) (verifiedMetadataSha)` — fills
  archive + metadata REMOTE digests; manifest sha + revision carried from the validated snapshot.
- `ReleaseEvaluate.finalizeRecovery (resolution) (verifiedMetadataSha)` — preserves `PublishedArchiveSha256`,
  `PublishedManifestSha256`, `PublishedRevision`, `Render.EvaluatedHead` (current HEAD).
- `ReleaseEvaluate.finalizeFailure (render) (reason)` — retains `Render` (config context), clears every
  `Published*` key and `MetadataRecoveryNeeded`.
- Runner CLI uses the SAME helpers; no fake extra API is introduced in the test fixtures.

## 4. Counts (FSI, post-W4 source, GITHUB_ENV/OUTPUT sentinel files)

```
$env:GITHUB_ENV   = "$env:TEMP\test-gh-env-file"   (pre-existing sentinel, absent at entry)
$env:GITHUB_OUTPUT= "$env:TEMP\test-gh-output-file" (pre-existing sentinel, absent at entry)
$env:MCP_RELEASE_HISTORICAL_ROOT="C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-bootstrap"

$ pwsh -NoProfile -File infrastructure/release/Suite.ps1
release regression suite passed: 8 deterministic script(s)

$ dotnet fsi --nologo infrastructure/release/tests/Historical.fsx
historical-bootstrap-tests summary: passed=10 failed=0
```

| Test | Pass / Fail / Skip | Kind |
| --- | --- | --- |
| `Bootstrap.fsx` | 9 / 0 / 0 | real `Update.ps1` invocation; duplicate-literal gate; `GITHUB_OUTPUT` stripped from child env |
| `Compare.fsx` | 26 / 0 / 0 | real `Compare.compare`; bounds, raw byte patches, unknown-field added classified, external attrs, entry comment, total uncompressed bound |
| `Evaluate.fsx` | **70 / 0 / 0** | real `ReleaseEvaluate.evaluate` with injected `RemoteObserver`; **4 new finalize-helper regressions** |
| `Fixture.fsx` | 0 / 0 / 0 | helper, no assertions of its own |
| `Historical.fsx` | 10 / 0 / 0 | real v1.0.3 bytes via `MCP_RELEASE_HISTORICAL_ROOT`; optional separately labelled |
| `Invocation.fsx` | 10 / 0 / 0 | real `Bootstrap.fsx` child; SDK/runtime/platform mismatch via env |
| `Metadata.fsx` | 52 / 0 / 0 | real `Metadata` module |
| `Publish.fsx` | 50 / 0 / 0 | real production functions with injected scripted `HttpMessageHandler` |
| `Runner.fsx` | 18 / 0 / 0 | real `ReleaseRunner.exe` child; empty-argv exit 2; dirty tree -> structured JSON + exit 4 + all published* keys null |
| `Telegram.fsx` | 15 / 0 / 0 | tracked-tree executable/YAML isolation scan |
| **Suite.ps1** | **250 / 0 / 0** | 8 deterministic scripts |
| **+ Historical.fsx** | **260 / 0 / 0** | including optional integration |

`GITHUB_ENV` and `GITHUB_OUTPUT` sentinel files are absent after Suite.ps1
exits (asserted byte-identical to entry snapshot).

Existing suites: `dotnet_test` Expecto noBuild (`dotnet/tests/Mcp.Dotnet.Tests`)
`runId=K1u_5MVC5ydiogohL4LBsfG3P1E44CFSscGUSH4A2f0` exit 0.
Analyzer regression `infrastructure/scripts/tests/Regression.fsx`: 41 / 0 / 0.

## 5. New regression IDs (W5)

- **R10** `Evaluate.fsx::observedMetadataAssetShaDiffersArchiveShaAndCarried` — observed metadata asset sha is distinct from the archive sha; the metadata sha is what carries into `PublishedMetadataAssetSha256` unchanged.
- **R11** `Evaluate.fsx::finalizeVerifiedPublicationFillsRemoteDigests` — production helper fills `PublishedArchiveSha256`/`PublishedMetadataAssetSha256` from verified values; `PublishedManifestSha256`/`PublishedRevision`/`Render.EvaluatedHead` carried from the validated snapshot.
- **R12** `Evaluate.fsx::finalizeRecoveryPreservesVerifiedArchiveFields` — production helper preserves `PublishedArchiveSha256`/`PublishedManifestSha256`/`PublishedRevision`/`Render.EvaluatedHead`; only `PublishedMetadataAssetSha256` is refreshed.
- **R13** `Evaluate.fsx::finalizeFailureClearsAllPublishedButRetainsRender` — production helper clears every `Published*` and `MetadataRecoveryNeeded`; retains every field of the `Render` (config context).

## 6. F1–F9 regression matrix

| Finding | Coverage | Status |
| --- | --- | --- |
| metadata POST targets canonical asset name | `Publish.fsx::uploadMetadataTargetsMetadataAssetName`; `Runner.fsx::metadataAssetNameDeterministic`; `Metadata.fsx::metadataAssetNameDeterministic` | covered |
| DISTRIBUTION schema + immutable metadata reconciliation | `Evaluate.fsx` (missing/corrupt/immutable metadata, observed field mismatch, recovery-needed, exhaustive public status set, R10/R11/R12/R13) | covered |
| post-POST observation + exact byte verify, ≤1 POST | `Publish.fsx` (success, server-error, ambiguous, idempotent, refuse-overwrite, post-timeout readback, archive-success/metadata-fail) | covered |
| strict raw ZIP, bounds, duplicate keys, raw non-canonical deflate | `Compare.fsx` (26 cases) | covered |
| origin policy + bounded manual redirects + token | `Publish.fsx` (unsafe redirect, safe redirect, safe 301 chain to verified Absent with token on every hop, CDN redirect drops Authorization, HTTP redirect, lookalike suffix, non-default port, userinfo, 303/307/308 chain, 5-hop chain bound) | covered |
| immutable send buffer | `Publish.fsx::uploadCandidateSnapshotMismatchZeroRequests` | covered |
| JSON before every exit, public statuses only | `Evaluate.fsx` (public status set, missing manifest/archive); `Runner.fsx` (empty-argv exit 2, dirty-tree structured JSON + exit 4 + all published* null) | covered |
| non-publishing CI gate, no `GITHUB_ENV`/`GITHUB_OUTPUT` pollution | `Suite.ps1` snapshots both env vars at entry and asserts byte-identity on exit; `Bootstrap.fsx` strips them from child env | covered (asserted: files ABSENT after Suite.ps1) |
| canonical assetUri, no env reconstruction | `Evaluate.fsx` (empty/wrong-owner/canonical assetUri); `Runner.fsx::contractJsonShape` | covered |

## 7. Resource bounds covered by Compare.fsx

| Bound | Value | Test |
| --- | --- | --- |
| Archive size | 256 MiB (`maxArchiveBytes`) | `archiveSizeBoundRejection` |
| Entry count | 4096 (`maxEntryCount`) | `entryCountUpperBoundRejection` |
| Per-entry uncompressed | 256 MiB (`maxEntryUncompressedBytes`) | `boundsAtValidate` |
| Total uncompressed | 256 MiB (`maxTotalUncompressedBytes`) | `totalUncompressedBoundRejection` |
| Compression ratio | 200:1 (`maxCompressionRatio`) | `compressionRatioBoundRejection` |
| ZIP64 marker | EOCD-locator presence | `zip64MarkerRejected` |
| Central extra fields | non-zero length | `centralExtraFieldPresentRejected` |
| Local/central flag agreement | byte 3 bit 3 (data descriptor) | `localAndCentralFlagsChanges` |
| Central external attributes | byte 38 | `externalAttributesRejected` |
| Central entry comment | non-zero length | `entryCommentRejected` |
| Trailing bytes | EOCD position | `trailingBytesArchiveRejected` |
| Duplicate JSON keys | manifest body | `duplicateKeyManifestRejected` |
| Unknown manifest field | body diff vs valid revision | `unknownManifestFieldAddedClassifiedBeyondRevision` |
| Non-canonical Deflate | Optimal vs Fastest | `nonCanonicalDeflateBody` |
| Declared vs actual payload hash | manifest `files[]` vs decompressed | `declaredPayloadHashMismatchRejection` |
| Raw byte patch (compressed payload) | CRC fail-closed at observation | `rawBytePatchClassifiedBeyondRevision` |
| Entry order | raw bytes | `rawEntryOrderChanged` |

## 8. Suite.ps1 + Historical run commands (sentinel GITHUB_ENV/OUTPUT)

```pwsh
$env:GITHUB_ENV     = "$env:TEMP\test-gh-env-file"
$env:GITHUB_OUTPUT = "$env:TEMP\test-gh-output-file"
pwsh -NoProfile -File infrastructure/release/Suite.ps1
# Observed: `release regression suite passed: 8 deterministic script(s)`

$env:MCP_RELEASE_HISTORICAL_ROOT = "C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-bootstrap"
dotnet fsi --nologo infrastructure/release/tests/Historical.fsx
# Observed: `historical-bootstrap-tests summary: passed=10 failed=0`
```

## 9. Limits and non-claims

- No production F#/YAML/PowerShell/build/commit/`.git` mutation. Suite.ps1
  edit recorded in §2 (out of assigned scope).
- No remote writes, no secret reads. `MCP_RELEASE_DO_UPLOAD` is set to a
  dummy inside each Publish.fsx test only.
- Hosted API, hosted CDN redirect, hosted queue, hosted `gh`/token path,
  and the live `ReleaseRunner.exe` (apphost build evidence
  `runId=sSO_NO5zPkb5zICwmItBZtjZzLLmK_aeLwrMzl7jcf8`, 0 errors / 0 warnings,
  owned by the engineer; tester cannot rebuild) remain **hosted-unverified**.
- Suite.ps1 does not invoke `Validation validate`/`typecheck`; the prior
  slice's evidence (`OK 49 scripts validated`) is reused.
- F4 unknown-manifest-field: production `parseManifest` silently ignores
  extras; the regression `unknownManifestFieldAddedClassifiedBeyondRevision`
  asserts the documented invariant (every other manifest field matches;
  unknown fields added/changed with valid revision classify as
  `DifferentBeyondRevision`) — accept and reject are both acceptable when
  the raw bytes are otherwise identical.