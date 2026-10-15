# Stage 4 — three integrity corrections (GHR-36 W5)

Authoritative inputs: `Solution.md` (selected boundaries / integrity policy),
`Requirements.md` (AC-01, AC-02, AC-03), `Threads.md` (4242892984, 4242892986,
4242892987), `Baseline.md` and the prior stage evidence in `Compilation.md`,
`Migration.md`, `Naming.md`, `Enrollment.md`, `Integration.md`. Baseline head:
`05a69ced8319b61a6bf0e24cad6d320a0a879283`. This is implementation/build
evidence only; no acceptance claim is made and no compiled test execution is
run by this engineer slice (`mayTest=false`).

## Scope

- `infrastructure/release/Evaluate.fs::classifyInternal` — the
  `ArchiveAbsent` branch now fails closed before any ZIP POST unless the
  metadata observation is a verified absence. The `MetadataObserved` branch
  now compares every required identity field against canonical policy and
  the verified embedded manifest / archives.
- `infrastructure/release/Publish.fs` — a single shared
  `verifyAssetDigest` is the bytes-verifier for the initial archive
  observer, the metadata observer, and the post-upload / idempotent probe.
  The upload/readback path no longer trusts byte equality in place of the
  claimed digest.

## Non-scope

- The engineer changed no test files; the compiled negatives remain
  tester-owned and are recorded in the W5 coverage ledger below.
- No build/test/branches/commit/push, no real network upload, no Telegram
  or consumer edits, no immutable-asset bypass, no original-URL changes,
  no tag/version policy relaxation, no revision-exception expansion.
- `Metadata.fs`, `Compare.fs`, `Archive.fs`, `Orchestration.fs`,
  `Enrollment.fs`, and `Bootstrap.fsx` are untouched. `Transaction.fs` and
  `Runner.fs` contain the transport seam and its CLI wiring. The CLI surface is
  unchanged; `ReleaseTransaction.execute` now receives the transport explicitly.

## Integrity policy implementation

### AC-01 — Metadata preflight safety (thread 4242892984)

The classification cascade in `Evaluate.fs::classifyInternal` now rejects
the new-ZIP branch on every same-version metadata observation that is not a
verified absence:

| `ArchiveAbsent` + | Result |
| --- | --- |
| `Error` | `failed` — metadata transport error reported verbatim. |
| `Ok MetadataCorrupt error` | `failed` — observed existing metadata asset is corrupt; no POST. |
| `Ok MetadataMissing` | `failed` — release container does not exist; refusing to silently create one. |
| `Ok MetadataImmutable` | `failed` — existing metadata asset is immutable; refusing to publish a new archive alongside stale metadata. |
| `Ok (MetadataObserved _)` | `failed` — an existing metadata asset for the declared version was observed; refusing to publish a new archive with stale or orphan metadata before the ZIP POST. |
| `Ok MetadataAbsent` | `published` — the only safe new-ZIP path; canonical metadata is uploaded together with the archive after the POST. |

The supported recovery for the existing-asset branch is unchanged:
`ArchiveObserved` + `ByteIdentical | RevisionOnlyEquivalent` + `Ok
MetadataAbsent` still produces `unchanged` with `MetadataRecoveryNeeded =
true`, so the metadata-only recovery remains intact.

### AC-02 — complete published metadata validation (thread 4242892986)

The `MetadataObserved` arm of `classifyInternal` now cascades the strict
identity comparison over every required field. The published revision
comes from the remote embedded manifest
(`ReleaseCompare.parseEmbeddedManifestEntry publishedObs | Ok header ->
header.Revision`), never from the candidate HEAD. Toolchain values
participate in identity; a `None`-vs-`Some v` is a strict mismatch and is
reported through `IdentityMismatch`. Order of the field cascade:

1. `componentId` against `dto.Contract.ComponentId`.
2. `version` against `dto.Contract.Version`.
3. `assetUri` against `dto.AssetUri` (the canonical URL the runner passed
   through the contract DTO; `validateCanonicalAssetUri` already
   guarantees this is the canonical shape).
4. `archiveName` against `dto.Contract.ArchiveName`.
5. `archiveSha256` against `publishedObs.ArchiveSha256`.
6. `manifestSha256` against `publishedObs.ManifestSha256`.
7. `publishedRevision` against the embedded manifest revision
   (`parseEmbeddedManifestEntry publishedObs`); this is the verified
   source, not the candidate HEAD.
8. `producerSdk` against `dto.ProducerSdk` (Option-to-Option).
9. `producerRuntime` against `dto.ProducerRuntime`.
10. `producerPlatform` against `dto.ProducerPlatform`.

Required fields missing from the published metadata are already rejected
by `ReleaseMetadata.parse`, so the schema-level invariants are preserved
without changing the parser.

### AC-03 — claimed digest verification (thread 4242892987)

A single strict `verifyAssetDigest` helper now owns the digest path. It
returns:

- `MalformedArtifact("release", ...)` when the claimed digest does not
  match the recognized `sha256:` / 64-hex shape (no payload substitution,
  no scheme fallback).
- `FileFailure("verify", assetName, ...)` when the parsed claim does not
  equal the SHA-256 of the downloaded bytes.
- `Ok ()` only when both checks pass.

The three call sites each run the verifier and refuse to advance on
either failure:

- `gitHubObserver` (initial archive observation).
- `gitHubMetadataObserver` (initial metadata observation; same digest
  semantics, distinct JSON parse afterward).
- `probe` (upload readback and idempotent recovery). The verifier runs
  immediately after `downloadAssetBytes` and before the existing
  byte-level overwrite guard or the `verify` callback. The `AssetVerified`
  digest returns `sha256Hex remoteBytes`, computed after the API claim has
  matched the downloaded bytes.

Candidate byte equality in place of the API digest is no longer a
substitute for the claimed-digest check.

## Transport-injection seam (W5 follow-up)

`ReleaseTransaction` now exposes one canonical, transport-parameterized
seam that drives observation + evaluate + execute with the same
`HttpTransport` instance, so the tester can drive the actual production
pipeline with a `RecordingHandler` instead of an alternate test model.

| Function | Visibility | Signature change | Behaviour |
| --- | --- | --- | --- |
| `ReleaseTransaction.buildRemoteObserverWith` | private | added | transport-aware observer; `gitHubObserver` / `gitHubMetadataObserver` are built from the supplied `HttpTransport`, no internal `defaultHttpTransport ()` call. |
| `ReleaseTransaction.buildRemoteObserver` | public | unchanged signature | thin convenience: calls `buildRemoteObserverWith (defaultHttpTransport ()) …` so the missing-credentials orchestrator test and any other callers keep their original contract. |
| `ReleaseTransaction.execute` | public | new first parameter: `transport : ReleasePublish.HttpTransport` | publish and metadata-recovery branches use the supplied transport; no internal `defaultHttpTransport ()` call anywhere. |
| `ReleaseTransaction.run` | public | new | the single path the runner uses: builds the observer with the supplied transport, drives `ReleaseEvaluate.evaluate`, then dispatches the resolution through the same transport via `execute`. Returns `Async<Result<Resolution * int, ReleaseError>>`. No parallel release engine. |

Default production composition happens exactly once at the CLI boundary
inside `ReleaseRunner.runTransactionMode`, which now constructs a single
`ReleasePublish.defaultHttpTransport ()` and hands the same instance to
`ReleaseTransaction.run`. Behaviour, secrets, token authority, and the
existing `Authorization` guard inside the transport are unchanged. The
CLI surface (`ReleaseRunner <manifest> <archive> <contract-json>`,
`ReleaseRunner orchestrate <config-path>`) is unchanged.

## Files changed (engineer slice)

- `infrastructure/release/Evaluate.fs` — `classifyInternal` rewritten in
  place; the file header, public types, public values, signature of
  `evaluate` and the `CandidateDto` / `Resolution` / `ArchiveState` /
  `MetadataState` shapes are unchanged.
- `infrastructure/release/Publish.fs` — added `private verifyAssetDigest`
  (helper, single call site shape); `gitHubObserver`,
  `gitHubMetadataObserver` and `probe` collapsed onto it; no public
  signature change.
- `infrastructure/release/Transaction.fs` — added `private
  buildRemoteObserverWith transport …`, refactored `execute` to take the
  transport as its first parameter, and added `run transport …` as the
  single canonical pipeline. `buildRemoteObserver` preserves its original
  signature by delegating to `buildRemoteObserverWith
  (defaultHttpTransport ())`.
- `infrastructure/release/Runner.fs` — `runTransactionMode` now
  constructs one `defaultHttpTransport ()` and hands it to
  `ReleaseTransaction.run`; header comment updated to reference the
  single path.

No test file, no script, no workflow, no PS/YAML was changed by this
engineer slice.

## Production API seams for the tester

The tester owns the regressions. The seams are:

- `ReleaseTransaction.run transport auth useGitHub downloadDir locator dto candidateProjection archivePath`
  is the single canonical production path. The same function is what
  `ReleaseRunner.runTransactionMode` calls; the tester calls it
  directly with the existing `RecordingHandler` +
  `transportFromHandler` helper from `infrastructure/release/tests/Publish.fs`.
  No second engine and no test-only alternative.
- `ReleaseTransaction.execute transport resolution auth locator candidateProjection archivePath useGitHub`
  is the same dispatch the runner uses internally; `run` simply
  composes the observer with the shared `transport` and calls
  `evaluate` before `execute`.
- `ReleaseEvaluate.evaluate` (`observer` + `dto`) returns
  `Async<Result<Resolution, ReleaseError>>`. Direct production path; no
  parallel test model. Inject scripted metadata observation through the
  existing `Release.Tests.Evaluate.scriptedObserverWith` shape.
- `ReleaseEvaluate.ArchiveState` carries `ArchiveAbsent`,
  `ArchiveObserved`, `ArchiveMissing`, `ArchiveImmutable`.
- `ReleaseEvaluate.MetadataState` carries `MetadataAbsent`,
  `MetadataObserved (sha256 * ProducerMetadataProjection)`,
  `MetadataMissing`, `MetadataImmutable`, `MetadataCorrupt error`.
- `ReleasePublish.uploadCandidate`, `ReleasePublish.uploadMetadata`,
  `ReleasePublish.gitHubObserver`, `ReleasePublish.gitHubMetadataObserver`
  accept an injected `HttpTransport`; the existing
  `RecordingHandler` + `transportFromHandler` helper in
  `infrastructure/release/tests/Publish.fs` already drives every negative.

How the tester drives the actual same production pipeline for AC-01/02/03
zero-POST assertions and post-POST readback verification:

- Construct one `RecordingHandler`; promote it to an `HttpTransport`
  via `transportFromHandler`.
- Queue the scripted archive / metadata / digest HTTP responses that
  trigger the same preflight or readback path the runner would hit
  (`Ok ArchiveAbsent + Ok (MetadataObserved …)` for AC-01;
  `ArchiveObserved` + a mismatched metadata projection for AC-02; a
  well-formed release with malformed or wrong claimed digests for AC-03).
- Call `ReleaseTransaction.run` with that recording transport, the
  canonical `ReleaseLocator { Owner = "masterlifting"; Repository =
  "mcp-store"; Tag = componentId }`, the assembled `CandidateDto` /
  `candidateProjection` / `archivePath`, `auth = Some
  { Token = "sentinel" }`, `useGitHub = true`, and the existing
  `tempRoot` for `downloadDir`.
- For preflight failures (`failed` resolution, default `execute`
  branch), no POST is ever issued; assert `resolution.Status =
  "failed"`, the named field or reason fragment, and zero `POST`
  requests captured. For the readback path, assert the expected digest
  verification error and the recorded request count. The same path is
  used for both producers because the policy is producer-owned.

The concrete compiled regression scenarios, producer coverage, and request-count
assertions are listed in the W5 test-source coverage ledger below. They exercise
the canonical `ReleaseTransaction.run` composition and production
`ReleasePublish.uploadCandidate` probe; they do not substitute a test-only
transaction model.

The existing `failedOnObservedMetadataFieldMismatch` assertion (version
mismatch) must continue to pass; the field cascade returns
`IdentityMismatch("version", ...)` first, so the asserted reason
fragment is preserved.

## Build evidence (engineer-owned, dedicated Dotnet MCP, Release)

| Project | runId | status | exitCode | errors | warnings |
| --- | --- | --- | --- | ---: | ---: |
| `infrastructure/release/Release.fsproj` | `QGD_8VVccNc1ekihjXrZzF1NlFR_Pwp0aWC6WILfyag` | succeeded | 0 | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `YFcM_wq9XgSeGlLt-XJVjKiR9STbiGTKl4B5NpXozuE` | succeeded | 0 | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `xqN-mWr2tSrqxrXBwkIyFnrcFF9wDtE65Sm8CLASJIo` | succeeded | 0 | 0 | 0 |
| `dotnet/Mcp.Dotnet.fsproj` | `j1vd7JHS67F7ECIRERKOHJ0_1sSr4xtB3AY6BNvwLlE` | succeeded | 0 | 0 | 0 |
| `workflow/Mcp.Workflow.fsproj` | `ky14qOv94ifrEyTwn5vVxEIUQdojkFONx9SVorHdPqA` | succeeded | 0 | 0 | 0 |

All four compile with `TreatWarningsAsErrors=true`. The Release build
supersedes the prior stage 4 runId
(`5Qkn1Ptn9kzLpYB6EohBVtufQl1QuzTFZSmRlz3c0x8`) and the earlier
forward-reference attempt
(`YK7sxbkXRWv1uxi-qf3KCwimSJs8P98Pi7ZOs46xtwY`, F# `FS0039` raised when
`run` referenced `execute` before its top-level declaration; fixed by
moving `run` after `execute` so the canonical pipeline reads in source
order). The W5 follow-up build after the tester registered
`Integrity.fs` in `Tests.fsproj`/`Main.fs`, adding 1 suite case while
the prior 25 were retained (expected 26 cases) —
`xqN-mWr2tSrqxrXBwkIyFnrcFF9wDtE65Sm8CLASJIo` — also succeeded with
0/0, superseding `YFcM_wq9XgSeGlLt-XJVjKiR9STbiGTKl4B5NpXozuE` for
W5; no production source was edited and no other build (Release,
Mcp.Dotnet, Mcp.Workflow) was repeated. The tests + producer builds
remain clean in this slice.

## Compatibility policy (status)

- Required fields (`schemaVersion`, `componentId`, `version`, `assetUri`,
  `archiveName`, `archiveSha256`, `manifestSha256`, `publishedRevision`)
  are strictly enforced by `ReleaseMetadata.parse`. Adding a new field is
  rejected by the parser until `metadataSchemaVersion` and the
  `requiredFields` list are updated together. No historical leniency is
  introduced.
- Optional toolchain fields (`producerSdk`, `producerRuntime`,
  `producerPlatform`) participate in identity. The `compare` helper and
  the publisher both treat `Some` vs `None` as a strict mismatch when the
  other projection carries a value.
- The tester adds independent wrong-value, omitted-value, and malformed-type
  transaction negatives for each toolchain field. No historical compatibility
  exception is authorized or supported by this evidence; omission remains
  fail-closed unless a separate explicit policy is approved.

### Bounded question — historical producer-toolchain metadata

The current producer `Config.fsx` records toolchain values as `Some`. A
historical metadata asset published without the producer toolchain
keys (renderer omitted them) would deserialize as `producerSdk = None`,
`producerRuntime = None`, `producerPlatform = None`. Re-pointing an
immutable release's metadata at the new canonical projection would then
fail AC-02 strict equality on those three fields, and no overwrite of the
immutable asset is permitted.

No historical `<componentId>-<version>-metadata.json` asset is
present in the worktree (the local historical fixture only contains
`dotnet-v1.0.3.zip` and `workflow-v1.0.3.zip`), and no documented schema
is available for one. The fail-closed path is the strict equality check
above; this is recorded here as a bounded question rather than a
historical-opt-out toggle. If a real remote metadata asset of a prior
version without toolchain fields exists and is expected to remain, an
explicit compatibility policy is required before AC-02 can be marked
PASS.

## Risk and stop point

- The three integrity defects are reproduced end-to-end through the
  production evaluate + transport boundary; the public surface is
  unchanged.
- Stage 4 does not claim PASS for AC-01, AC-02 or AC-03. Tester-owned
  compiled regressions against the seams above, the integrated
  `Suite.ps1` gate (`25 cases, 25 passed`), and the producer Expecto
  suite (`151 run, 151 passed`) remain required before any acceptance
  claim can be made. Hosted CI and independent review stay out of scope
  for this slice.
- No commit, no push, no merge, no real release upload, no Telegram or
  consumer edits; the working tree remains dirty on purpose.

## W5 compiled test-source coverage ledger

Tester-added source: `infrastructure/release/tests/Integrity.fs`, compiled after
`Publish.fs` and registered as the one additive Expecto case
`W5 actual production transaction integrity regressions`. Existing 25 compiled
cases are retained. The table-driven assertions report producer, scenario, and
field/phase in their failure labels.

| AC | Production entrypoint | Case labels / assertions | Producers |
| --- | --- | --- | --- |
| AC-01 | `ReleaseTransaction.run` | `observed-mismatch`, `orphan-compatible-metadata`, `corrupt-digest`, `malformed-json`, `unsupported-schema`, `missing-metadata`, `metadata-http-error`; `ValidNewPublication`; `RevisionOnlyEquivalentWithMatchingOldMetadata`; `MetadataAbsentRecovery`. Every rejection asserts failed status, no published identity and zero POSTs. | Dotnet + Workflow |
| AC-02 | `ReleaseTransaction.run` | Independent mismatches: `componentId`, `version`, `assetUri`, `archiveName`, `archiveSha256`, `manifestSha256`, `publishedRevision`; each producer toolchain field has separate `wrong`, `missing` (`Some` vs `None`), and malformed-type cases; also `wrong-schema`, `missing-schema`, and `missing-required-assetUri`. Mismatches require the field diagnostic; malformed projections fail closed; no POST or published identity. | Dotnet + Workflow |
| AC-03 | `ReleasePublish.uploadCandidate` and `ReleaseTransaction.run` | `missing`, `malformed`, and `mismatch` claimed digests on `idempotent-observation`, direct `post-upload-readback`, and transaction `post-upload-readback`; candidate-equal bytes do not waive API digest validation. Exact GET/POST counts assert no unsafe retry. | Dotnet + Workflow |

The transaction cases call the same `ReleaseTransaction.run` composition used by
the CLI, with one scripted `HttpMessageHandler` transport shared across observe,
evaluate and execute. The only token is the fake sentinel passed in memory. No
actual network or credential environment variable is used. Positive publish
cases temporarily set and restore `MCP_RELEASE_DO_UPLOAD`.

At the source-handoff point, this was unbuilt and unexecuted; the subsequent
engineer build and tester verification are recorded below. No AC-01/02/03 PASS
claim is made.

## W5 complete-suite verification

Engineer test build `xqN-mWr2tSrqxrXBwkIyFnrcFF9wDtE65Sm8CLASJIo` succeeded
with zero errors and warnings. The tester ran the actual complete gate without
building, against the approved local historical fixture:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
& pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot 'C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'
exit $LASTEXITCODE
```

Result: exit 0; Expecto reported **26 tests run, 26 passed, 0 ignored, 0 failed,
0 errored**. The inventory gate matched 26 compiled cases, including one
historical case; provenance passed; the gate completed with 1 producer script.
Nested output: Bootstrap 9/0, Evaluate 70/0, Historical 10/0, Invocation 10/0,
Metadata 52/0, Publish 62/0, Revision 22/0 plus PE integration 2/0, Runner
22/0, Telegram 22/0.

The new integrity Expecto case passed. Its source runs these table-driven counts
through the canonical transaction for both producers: AC-01 has 14 rejecting
preflight cases (7 each) and 6 positive publish/no-op/recovery cases; AC-02 has
38 negatives (19 each: 13 field-value/option mismatches and 6 malformed or
missing-schema/required/toolchain-type inputs); AC-03 has 18 invalid-digest
cases (9 each: three digest forms across direct idempotent, direct post-upload,
and transaction post-upload paths). Every rejected transaction asserts no
published identity. AC-01 and AC-02 failures assert zero POSTs. AC-03 exact
request assertions per digest form and producer are: idempotent **2 GET / 0
POST**, direct post-upload **3 GET / 1 POST**, and full transaction post-upload
**5 GET / 1 POST**. The invalid digest response is rejected even though the
downloaded archive bytes equal the candidate; no retry occurs.

The transaction tests used only the scripted recording handler and in-memory
sentinel token. Upload/credential/workflow-output environment variable names
were removed before the run; no actual network request or upload occurred. This
is verification evidence for the tester slice, not an acceptance-criterion PASS
or independent review.
