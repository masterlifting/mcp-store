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

## 10. SDK source-revision metadata regression (HEAD `962fd64…`)

`infrastructure/release/tests/Revision.fsx` is discovered and run by the
existing `Suite.ps1` entrypoint scan. It checks both configured producer projects
through the SDK's `GetAssemblyAttributes` metadata target. For each project it
evaluates two controlled `SourceRevisionId` inputs with the project's
`IncludeSourceRevisionInInformationalVersion=false`, then evaluates both inputs
with the property explicitly set to `true` as a counterfactual. The test asserts
the default is false, both default informational versions are equal, SHA-free,
and nonempty, and the counterfactual outputs append their respective inputs.
These source-revision values are test inputs only; no packages or provenance
records were generated.

| Regression | Coverage |
| --- | --- |
| **R14** `dotnet::defaultInformationalVersionIgnoresSourceRevision` | The configured opt-out remains false and yields equal, SHA-free metadata for both inputs. |
| **R15** `workflow::defaultInformationalVersionIgnoresSourceRevision` | Same default property and metadata invariant for Workflow. |
| **R16** `dotnet::explicitSdkDefaultCounterfactualWouldAppendSourceRevision` | Explicitly enabling the SDK behavior appends each controlled input, proving this regression detects the former cause. |
| **R17** `workflow::explicitSdkDefaultCounterfactualWouldAppendSourceRevision` | Same sensitivity control for Workflow. |

The script also has separately labelled optional producer-PE integration
assertions. When local producer DLLs exist, `PEReader` reads their custom
attributes without loading producer code; when absent, the integration is
reported `NOT RUN`, never counted as a pass or skip. The normal SDK metadata
assertions do not depend on producer build outputs. In this local run both PE
values were `1.0.0`, matching generated metadata; the owner build records are
`dotnet_build` run IDs `F90rdBZHehaIgybkDeo-0f09vCTzYcaGnxyNyjAkYjw` and
`UkKu2gM63fVE57NWpnLoxDkfGjE4_wV7iQsUVNjbO8c` respectively.

```
$ pwsh -NoProfile -File infrastructure/release/Suite.ps1
release regression suite passed: 9 deterministic script(s)
revision metadata summary: passed=22 failed=0 skipped=0
producer PE integration summary: passed=2 failed=0 notRun=0

$env:MCP_RELEASE_HISTORICAL_ROOT = <local v1.0.3 archive directory>
$ dotnet fsi --nologo infrastructure/release/tests/Historical.fsx
historical-bootstrap-tests summary: passed=10 failed=0
```

The former release suite's 250 assertions and its `GITHUB_ENV`/`GITHUB_OUTPUT`
sentinel checks passed unchanged; the added required SDK assertions bring the
deterministic total to **272 passed, 0 failed, 0 skipped**. The two PE
integration assertions are separate from that required total.

`dotnet/tests/Mcp.Dotnet.Tests/SyncOverAsyncChecker.fs` now has one exact
approval for `infrastructure/release/tests/Revision.fsx`'s documented top-level
bridge; no general suppression was added. A direct source-checker probe on the
new path returned zero blockers.

After the engineer's Release rebuild, the dedicated `dotnet_test` no-build call
returned `succeeded`, exit 0, but supplied `counts=null`, no output, and no TRX;
failed-test details were unavailable because no TRX was produced. The actual
Expecto entrypoint run (`dotnet run --project dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj --configuration Release --no-build`)
reported **150 run, 149 passed, 1 failed, 0 ignored, 0 errored**. The failing
test was `Mcp.Dotnet.sync-over-async checker.repository F# surface.all active
root, dotnet, and workflow source files are checked`, at
`dotnet/tests/Mcp.Dotnet.Tests/SyncOverAsyncCheckerTests.fs:202`:
`Expect.isEmpty failures summary` failed with “Should be empty.” The summary
contained 118 `BLOCK` diagnostics across existing source/test files (including
`dotnet/release/Release.fsx:39:23`,
`infrastructure/release/Bootstrap.fsx:290:26`, and
`workflow/release/Release.fsx:39:23`); Revision's approved bridge was not a
blocker. No broad checker exception or product fix was applied.

These metadata tests are not proof that two real committed package payloads
match. That cross-commit package evidence remains pending the coordinator's
approved commits; no fake-revision package was created and no package version
or release metadata was changed here.

## 11. D4 async-conformance tester evidence

At source context HEAD `962fd646466158afc255311ec9de3746d3c35952`, the test
scripts were converted from per-case synchronous waits to async suite flow.
F1–F9 test cases and their assertions were preserved; no assertion was added.
The final deterministic suite run was **272 passed, 0 failed, 0 skipped**:

| Script | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `Bootstrap.fsx` | 9 | 0 | 0 |
| `Compare.fsx` | 26 | 0 | 0 |
| `Evaluate.fsx` | 70 | 0 | 0 |
| `Invocation.fsx` | 10 | 0 | 0 |
| `Metadata.fsx` | 52 | 0 | 0 |
| `Publish.fsx` | 50 | 0 | 0 |
| `Revision.fsx` | 22 | 0 | 0 |
| `Runner.fsx` | 18 | 0 | 0 |
| `Telegram.fsx` | 15 | 0 | 0 |
| **Deterministic total** | **272** | **0** | **0** |

The separate optional historical run reported **10 passed, 0 failed** with the
local v1.0.3 archives. `Revision.fsx` also reported its separate read-only PE
integration as **2 passed, 0 failed, 0 not-run**. The suite's `GITHUB_ENV` and
`GITHUB_OUTPUT` sentinel targets remained absent. The script analyzer regression
run reported **41 passed, 0 failed, 0 skipped**.

Every test entrypoint has exactly one final bridge, with the listed source line
as the checker's exact-site registry value:

| Registered path | Exact source line |
| --- | --- |
| `dotnet/release/Release.fsx` | `let exitCode = runWrapper () |> Async.RunSynchronously` |
| `workflow/release/Release.fsx` | `let exitCode = runWrapper () |> Async.RunSynchronously` |
| `infrastructure/release/Bootstrap.fsx` | `match verify root |> Async.RunSynchronously with` |
| `infrastructure/release/Runner.fs` | `runAsync () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Bootstrap.fsx` | `runSuite () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Compare.fsx` | `runSuite () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Evaluate.fsx` | `runSuite () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Historical.fsx` | `runSuite () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Invocation.fsx` | `runSuite () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Metadata.fsx` | `runSuite () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Publish.fsx` | `runSuite () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Revision.fsx` | `runSuite () |> Async.RunSynchronously` (pre-existing exact registration) |
| `infrastructure/release/tests/Runner.fsx` | `runSuite () |> Async.RunSynchronously` |
| `infrastructure/release/tests/Telegram.fsx` | `runSuite () |> Async.RunSynchronously` |

`infrastructure/release/tests/Process.fsx` declares `// role: test helper`; it
provides bounded async process capture. `Process.fsx` and `Fixture.fsx` each
contain zero synchronous bridges. No checker behavior or broad exemptions were
added. Running the current checker source and registry directly from FSI yielded
**58 findings, 0 blockers**; this is source-only evidence, not compiled Expecto
gate proof.

Tester source-set digest (SHA-256 over sorted relative-path / `git hash-object`
lines for the checker and changed test scripts):
`993577b668a2c9c5f4abf3668dc321f6a82028f5b521a91636144d3ccc0d785e`.

The real Expecto entrypoint remains pending: the checker source changed, so the
Engineer must rebuild `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj`
before the tester runs it. The previously recorded **150 run, 149 passed, 1
failed** result remains historical evidence and is not represented as a current
pass; the null-count `dotnet_test` result is not substituted for the rebuilt
Expecto run.

### Rebuilt compiled Expecto gate

After the Engineer rebuilt the test project with `dotnet_build` runId
`bJvq-YpOzH2-lIzDurFexqZpSd0I2C7Wly0GXy3uSSk` (Release, 0 errors / 0
warnings), the tester executed the full native Expecto entrypoint without a
build:

```text
dotnet run --project dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj -c Release --no-build
```

Actual result: **150 run, 150 passed, 0 ignored, 0 failed, 0 errored; exit 0**.
There were no failure locations. This is the compiled checker-gate evidence;
the earlier null-count `dotnet_test` result remains non-evidence and was not
used as a fallback. The 272-script suite and optional 10-case Historical run
were already executed against the unchanged test sources recorded above; no
source changed between those runs and this compiled Expecto execution.
