# Release automation requirements and evidence matrix (GHI-24)

Status: W5 narrow pass-1 regression appended; columns distinguish **local**
(tests under `infrastructure/release/tests/` plus updated existing suites),
**simulated** (loopback HttpListener or `localMirrorObserver` injecting the
seam), **actual remote read** (none in this slice), and **unverified hosted**
(GitHub Actions queue, real `api.github.com` uploads, immutable-release
detection). The matrix is not retroactive; rows not touched remain
`unverified`. Authority: the original issue body is preserved verbatim at
`docs/GHI-24/original.md`. Requirement numbering below is the 1–72
extraction of that body (`docs/GHI-24/research.md` records the baseline and
open gaps). If this file and the issue conflict, the issue wins. Full
evidence narrative at `docs/GHI-24/tests.md`.

Evidence IDs: `EV-BUILD` producer build/package validation; `EV-TEST`
deterministic tests; `EV-WF` static/config validation of the root workflow;
`EV-REMOTE` read-only GitHub API metadata/digest observation; `EV-CMP` new
deterministic comparison/provenance helper tests; `EV-REVIEW` independent
review; `EV-AUDIT` separate task-first acceptance audit. W5 evidence layer:
updates test names to the post-W4 actual paths (e.g. `TransactionTests`
removed; coverage now lives under `Evaluate.fsx::finalizeVerifiedPublication*`,
`finalizeRecovery*`, `finalizeFailure*`), aligns status qualifiers, and adds
the metadata-asset-sha distinct-from-archive-sha invariant.

---

## 1. Requirement inventory → planned evidence

| Req | Requirement (condensed) | Planned evidence | Status |
| --- | --- | --- | --- |
| 1 | One root workflow deterministically evaluates/publishes dotnet + workflow on every `main` push | EV-WF (YAML), EV-BUILD (W3 packages.md), EV-TEST (`Runner.fsx::contractJsonShape`, `runnerBinaryExists`) | local-run static + simulated; hosted unverified |
| 2 | `telegram/**` and Telegram-owned workflows out of scope | EV-WF, EV-TEST (`Telegram.fsx` ×8), EV-REVIEW | local-run, deterministic |
| 3 | Required predecessors #21/#23/#26/#27 satisfied | EV-AUDIT, baseline `research.md` | unverified (status from W3 prior slice) |
| 4 | Dependency graph honored (this → #28 → …) | EV-AUDIT | unverified (status from W3 prior slice) |
| 5 | Land before later producer work on a stable publication path | EV-AUDIT | unverified (status from W3 prior slice) |
| 6 | Single root `.github/workflows/release.yml`; trigger `push to main` | EV-WF: YAML parse; EV-TEST (`Telegram.fsx::releaseWorkflowMatrixNamesOnly`) | local-run, deterministic |
| 7 | Merged PR and direct push both fire; branch state is authority | EV-WF | unverified hosted |
| 8 | No manual release step on normal path | EV-WF: YAML has no `workflow_dispatch`; EV-TEST (`Telegram.fsx::releaseWorkflowMatrixNamesOnly`) | local-run, deterministic |
| 9 | `workflow_dispatch` only if identical deterministic logic and cannot overwrite | EV-WF | unverified hosted |
| 10 | Canonical per-producer config is single source of truth (id/version/SDK/archive/URI/allowlist) | EV-BUILD; EV-TEST (`Bootstrap.fsx::updatePs1ProjectEmitsCanonicalValues`) | local-run, deterministic |
| 11 | Workflow YAML must not duplicate producer versions | EV-WF; EV-TEST (`Bootstrap.fsx::bootstrapKeysAreStable`) | local-run, deterministic |
| 12 | CI never auto-increments/invents a semantic version | EV-WF, EV-TEST (`Bootstrap.fsx::updatePs1ProjectRejectsDuplicateLiteral`) | local-run, deterministic |
| 13 | Smallest deterministic metadata projection if SDK setup needs config | EV-WF, EV-TEST (`Bootstrap.fsx::bootstrapEntryEmitsCanonicalSummary`) | local-run, deterministic |
| 14 | Harness-independent: producers + producer metadata only | EV-WF; EV-TEST (`Telegram.fsx::runnerSourceExcludesTelegram`) | local-run, deterministic |
| 15 | Preserve fixed `dotnet`/`workflow` tags and `v1.0.3.zip`-style assets; no tag-model redesign | EV-REMOTE | unverified hosted |
| 16 | Per-component transaction: SHA → SDK → build/tests → distribution → validation → metadata → asset absent/no-op | EV-BUILD, EV-TEST (`Evaluate.fsx::publishedOnArchiveAbsent`, `unchangedOnByteIdentical`, `unchangedOnRevisionOnlyEquivalent`, `failedOnBumpedSdk`, `finalizeVerifiedPublicationFillsRemoteDigests`) | local-run, deterministic |
| 17 | Producers fail/report independently; no publish before validation | EV-TEST (`Evaluate.fsx` failedEvaluation against `MetadataCorrupt`, `MetadataImmutable`, `ArchiveMissing`, `ArchiveImmutable`) | local-run, deterministic |
| 18 | Never overwrite/clobber a published versioned asset | EV-TEST (`Compare.fsx`, `Publish.fsx::uploadCandidateRefusesOverwrite`) | local-run, deterministic; live HTTP unverified |
| 19 | Asset absent: validation passes → upload; report archive/manifest SHA-256, revision, version, URI | EV-BUILD, EV-TEST (`Evaluate.fsx::publishedOnArchiveAbsent` carries config context into the Render; `Runner.fsx::runnerDirtyTreeEmitsStructuredFailureJson` asserts every published* key absent on failure) | local-run, deterministic; live HTTP unverified |
| 20 | Asset exists: download/read, verify digest + embedded manifest, deterministic compare | EV-CMP, EV-REMOTE (`Publish.fsx::uploadCandidateIdempotentNoPost` issues GET release + GET asset and verifies digest) | local-run simulated; live HTTP unverified hosted |
| 21 | Embedded `revision` exception is exact; do not falsify/remove revision | EV-CMP (`Compare.fsx::revisionOnlyEquivalentDecision`), EV-REVIEW | local-run, deterministic |
| 22 | Byte-identical asset → successful no-op | EV-CMP (`Compare.fsx::byteIdenticalDecision`) | local-run, deterministic |
| 23 | Revision-only difference → no-op only after full remote digest/structure/payload/manifest match | EV-CMP, EV-REMOTE (`Publish.fsx::uploadCandidateIdempotentNoPost` verifies digest + bytes; `Compare.fsx::revisionOnlyEquivalentDecision`) | local-run, deterministic; live HTTP unverified |
| 24 | Any other/inconclusive difference → fail closed with version-bump diagnostic | EV-CMP (`Compare.fsx::changedSdkRejection`, `changedPayloadHashRejection`, `changedCompressionMethodRejection`, `unknownManifestFieldAddedClassifiedBeyondRevision`, `externalAttributesRejected`, `entryCommentRejected`); EV-TEST (`Evaluate.fsx::failedOnBumpedSdk`) | local-run, deterministic |
| 25 | Comparison strict, deterministic, testable without GitHub | EV-CMP (`Compare.fsx` 26 cases) | local-run, deterministic |
| 26 | No-op reports published hash/manifest/revision plus distinct evaluated HEAD | EV-CMP (`Compare.fsx::revisionOnlyEquivalentDecision`); EV-TEST (`Evaluate.fsx::observedMetadataAssetShaDiffersArchiveShaAndCarried`) | local-run, deterministic |
| 27 | No `--clobber`/replacement/delete-reupload/mutable tags/assets | EV-TEST (`Publish.fsx::uploadCandidateRefusesOverwrite`, `uploadMetadataRefusesOverwrite`) | local-run, deterministic |
| 28 | Do not rely solely on fragile path filters | EV-WF: YAML has no `paths`/`paths-ignore`; EV-TEST (`Telegram.fsx::releaseWorkflowMatrixNamesOnly`) | local-run, deterministic |
| 29 | Preferred model: candidate → compare declared version → no-op/publish/bump-fail | EV-CMP, EV-TEST (`Evaluate.fsx::publicStatusSetIsExhaustive`) | local-run, deterministic |
| 30 | Path optimization only if tests prove it cannot hide a changed package | EV-TEST (`Compare.fsx::changedCompressionMethodRejection`, `changedPayloadHashRejection`) | local-run, deterministic |
| 31 | Every archive/manifest identifies exact committed `main` revision | EV-BUILD, EV-CMP (`Runner.fsx::manifestRevisionGuardContract`) | local-run, deterministic |
| 32 | Preserve clean-HEAD/revision validation semantics in shared infra | EV-CMP, EV-TEST (`Runner.fsx::cleanTreeGuardContract`) | local-run, deterministic |
| 33 | `dist/**` output must not weaken clean-source provenance checks | EV-BUILD, EV-TEST (`Runner.fsx::cleanTreeGuardContract`, `runnerDirtyTreeEmitsStructuredFailureJson`) | local-run, deterministic |
| 34 | Per-component summary: status published/unchanged/failed, version, revision, URI/name, archive+manifest SHA-256, reason | EV-BUILD; EV-TEST (`Evaluate.fsx::unchangedOnByteIdentical`, `observedMetadataAssetShaDiffersArchiveShaAndCarried`; `Runner.fsx::runnerDirtyTreeEmitsStructuredFailureJson` parses JSON for status/component/version/archiveName/assetUri/reason/published* null) | local-run, deterministic |
| 35 | Minimum GitHub Actions permissions; write limited to contents/assets | EV-WF: YAML `permissions: contents: read` default and `contents: write` only on the release job; EV-TEST (`Telegram.fsx::releaseWorkflowMatrixNamesOnly`) | local-run, deterministic |
| 36 | Prefer `GITHUB_TOKEN`; no PAT | EV-WF: YAML references `secrets.GITHUB_TOKEN` only | local-run, deterministic |
| 37 | Repo/component-safe serialization so rapid merges cannot race | EV-WF, EV-TEST | unverified hosted; YAML checked statically only |
| 38 | Newer run must not silently cancel an in-progress publication | EV-WF, EV-TEST | unverified hosted |
| 39 | `cancel-in-progress: false` alone insufficient (pending-run replacement) | EV-WF, EV-TEST | unverified hosted |
| 40 | Use `queue: max` (or prove equivalent); report overflow/cancellation; keep publication serialized | EV-WF: YAML has `queue: max`; EV-TEST | unverified hosted (no hosted runner available) |
| 41 | Fail closed on build/test/package failure, revision mismatch, metadata inconsistency, asset diff, ambiguous upload, unresolvable container | EV-TEST (`Evaluate.fsx::failedOnMissingManifest`, `failedOnArchiveMissing`, `failedOnArchiveImmutable`, `failedOnMetadataCorrupt`, `finalizeFailureClearsAllPublishedButRetainsRender`; `Runner.fsx::runnerDirtyTreeEmitsStructuredFailureJson`) | local-run, deterministic |
| 42 | On ambiguous upload, observe remote before retry; never retry blindly | EV-TEST (`Publish.fsx::uploadCandidateAmbiguousPostMustObserveRemote`, `uploadCandidatePostTimeoutReadsBackVerifiedBytes`, `uploadCandidatePostServerErrorNoRetry`) | local-run simulated; live HTTP unverified hosted |
| 43 | One producer's failure must not publish an unvalidated artifact for that producer | EV-TEST (`Evaluate.fsx::finalizeFailureClearsAllPublishedButRetainsRender`); the Runner CLI exercises the same helpers and never invents a fake extra API | local-run, deterministic |
| 44 | Use existing component release containers/tags (`dotnet`, `workflow`) | EV-WF, EV-REMOTE | static YAML checked; live remote unverified |
| 45 | Missing container: create via accepted tag policy or fail with actionable error | EV-WF, EV-TEST (`Evaluate.fsx::failedOnArchiveMissing`; `Publish.fsx::localMirrorObserver` Missing path) | local-run simulated; live HTTP unverified hosted |
| 46 | Do not move/rewrite an existing component tag | EV-WF, EV-REVIEW | unverified hosted |
| 47 | Run canonical producer pin preparation as part of release validation | EV-BUILD, EV-CMP (`Metadata.fsx::roundTripByteExactIdentical`) | local-run, deterministic |
| 48 | Workflow must not mutate any consumer repository | EV-WF, EV-REVIEW | unverified hosted |
| 49 | Publish one consumer-neutral producer metadata projection | EV-BUILD, EV-TEST (`Metadata.fsx::makeProjection` round-trip, `writeToFileDeterministic`); EV-TEST (`Evaluate.fsx::observedMetadataAssetShaDiffersArchiveShaAndCarried` proves the metadata asset sha is verified separately from the archive sha) | local-run, deterministic |
| 50 | Optional: expose metadata as Actions artifact/summary; no consumer-specific formats | EV-WF: YAML references `release-metadata-<component>` artifact | local-run, deterministic |
| 51 | Deterministic tests/helpers without real publication for ordinary local runs | EV-TEST (`infrastructure/release/tests/*.fsx`, 9 entrypoint scripts and 272 assertions in Suite.ps1; `Fixture.fsx` helper + `Historical.fsx` integration = 282 total assertions) | local-run, deterministic |
| 52 | Cover publish/no-op/bump-fail/validation-gate/ambiguous/two-component/shared-infra/concurrency/Telegram-exclusion | EV-TEST (publish/no-op/bump-fail/validation-gate/ambiguous/two-component/shared-infra-change/Telegram are covered; concurrency is YAML-static only) | local-run simulated; concurrency hosted unverified |
| 53 | Keep decision logic deterministic and separate from the thin transport shell | EV-CMP (`Compare.fsx`, `Metadata.fsx`), EV-REVIEW | local-run, deterministic |
| 54 | Merge/push to `main` auto-evaluates both producers | EV-WF, EV-REMOTE | unverified hosted |
| 55 | New source-controlled versions publish without manual release step | EV-WF, EV-REMOTE | unverified hosted |
| 56 | Byte-identical / revision-only equivalent → clean no-op retaining published revision/hashes | EV-CMP (`Compare.fsx::byteIdenticalDecision`, `revisionOnlyEquivalentDecision`); EV-TEST (`Evaluate.fsx::finalizeRecoveryPreservesVerifiedArchiveFields`) | local-run, deterministic |
| 57 | Same-version candidates differing beyond the exception fail, require bump | EV-CMP (`Compare.fsx::changedSdkRejection`, `changedPayloadHashRejection`, `unknownManifestFieldAddedClassifiedBeyondRevision`, `externalAttributesRejected`, `entryCommentRejected`); EV-TEST (`Evaluate.fsx::failedOnBumpedSdk`) | local-run, deterministic |
| 58 | No asset is ever overwritten | EV-TEST (`Publish.fsx::uploadCandidateRefusesOverwrite`, `uploadMetadataRefusesOverwrite`) | local-run simulated; live HTTP unverified hosted |
| 59 | Artifacts built from and identify the exact canonical `main` SHA | EV-BUILD (packages.md SHA-256), EV-CMP (`Runner.fsx::manifestRevisionGuardContract`) | local-run, deterministic |
| 60 | Producer validation runs before publication | EV-BUILD, EV-TEST (`Evaluate.fsx::missingManifestRejection`, `missingArchiveRejection`) | local-run, deterministic |
| 61 | Version/SDK/release metadata not duplicated in workflow YAML | EV-WF, EV-TEST (`Bootstrap.fsx::updatePs1ProjectEmitsCanonicalValues`) | local-run, deterministic |
| 62 | Release output exposes deterministic hashes/revision suitable for pin updates | EV-BUILD, EV-CMP (`Metadata.fsx::renderDeterministicByteExact`, `writeToFileDeterministic`); EV-TEST (`Evaluate.fsx::finalizeVerifiedPublicationFillsRemoteDigests`) | local-run, deterministic |
| 63 | Concurrent merges cannot race into mutable/duplicate publication | EV-WF, EV-TEST | unverified hosted |
| 64 | Telegram source and Telegram-owned workflows remain untouched | EV-TEST (`Telegram.fsx` ×8), EV-REVIEW | local-run, deterministic |
| 65 | Non-goals respected (no Telegram CI, auto bump, consumer edits, tag-model change, registry, generic framework) | EV-AUDIT, EV-REVIEW | local-run (`Telegram.fsx::noTelegramWorkflowFiles`, `noUvPytestInReleaseWorkflows`) |
| 66 | Audit snapshot: #21/#23/#26/#27 closed; no root workflow; both configs `1.0.3` / SDK `11.0.100-rc.1.26425.128` | baseline `research.md` | unverified (status from W3 prior slice); the W4 manifest refresh bumped to 1.0.4 (see `packages.md`) |
| 67 | Historical bootstrap: compare candidate vs published `v1.0.3`; PR authors reviewed bump if beyond revision | EV-REMOTE, EV-CMP, EV-REVIEW (`packages.md` §7 records byte comparison; the 1.0.4 bump is the authored response) | local-run, deterministic on the bootstrap assets at `MCP_RELEASE_HISTORICAL_ROOT` |
| 68 | Install/verify exact configured SDK actually executes build/test/publish; no second source; select/validate platform+arch | EV-BUILD, EV-WF, EV-TEST (`Bootstrap.fsx::updatePs1ProjectEmitsCanonicalValues`, `updatePs1ProjectRejectsDuplicateLiteral`; `Invocation.fsx` negative cases) | local-run; hosted `setup-dotnet` image resolution unverified |
| 69 | Check out event `github.sha` (not moving `main`); verify HEAD+manifest; stale re-run fails closed; never move tags | EV-WF (YAML `ref: ${{ github.sha }}`), EV-CMP, EV-TEST (`Runner.fsx::runnerDirtyTreeEmitsStructuredFailureJson` exercises the clean-tree guard with a real runner invocation) | local-run, deterministic; hosted unverified |
| 70 | Preserve fixed tags/URLs; detect incompatible immutable-release settings and fail with actionable message | EV-REMOTE, EV-WF, EV-TEST (`Evaluate.fsx::failedOnArchiveImmutable`, `Publish.fsx::observerImmutableReleaseReportsImmutable`) | local-run simulated; hosted unverified |
| 71 | Additional tests: consecutive unrelated HEADs → unchanged; identical/revision-only/changed/malformed/bootstrap/pending-queue/Telegram | EV-TEST (`Compare.fsx` covers identical/revision-only/changed/malformed; `Bootstrap.fsx` covers bootstrap; `Telegram.fsx` covers Telegram; `Historical.fsx::consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent` covers consecutive unrelated HEADs; pending-queue **unverified hosted**) | local-run, deterministic (pending-queue hosted unverified) |
| 72 | Clarifications are scope clarifications, not permission to redesign public formats | EV-AUDIT, EV-REVIEW | unverified |

## 2. User verification scenarios

Each scenario is how a reviewer/user confirms an acceptance criterion (#54–64)
or required test (#52/#71). None is executed in this slice.

| # | Scenario | Expected observable | Planned evidence | Status |
| --- | --- | --- | --- | --- |
| V1 | Push/merge new Dotnet version to `main` | `dotnet` run reports `published` with archive/manifest SHA-256, revision, URI | EV-WF, EV-BUILD, EV-REMOTE | unverified |
| V2 | Push/merge new Workflow version to `main` | `workflow` run reports `published` | EV-WF, EV-BUILD, EV-REMOTE | unverified |
| V3 | Push docs-only/Telegram-only change | Both producers report `unchanged` despite new embedded revision | EV-TEST (`Historical.fsx::consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent`; Req 71) | local-run, deterministic |
| V4 | Re-publish byte-identical asset | `unchanged`; published revision/hashes retained | EV-CMP (`Evaluate.fsx::finalizeRecoveryPreservesVerifiedArchiveFields`) | local-run, deterministic |
| V5 | Same version, candidate differs beyond revision (runtime/SDK/manifest/ZIP metadata) | `failed` with version-bump diagnostic; no upload | EV-CMP, EV-TEST | unverified |
| V6 | Malformed/mismatched published digest or manifest | `failed`; no upload | EV-CMP, EV-TEST | unverified |
| V7 | Producer build/test/package validation fails | No upload for that producer; other producer unaffected | EV-BUILD, EV-TEST | unverified |
| V8 | Manifest revision ≠ checked-out HEAD | Fail closed before publish | EV-CMP, EV-TEST | unverified |
| V9 | Ambiguous upload result | Remote observed before any retry; no blind retry | EV-TEST | unverified |
| V10 | Two rapid merges / pending runs | No clobber; serialized publication; overflow reported | EV-WF, EV-TEST | unverified |
| V11 | Shared infrastructure / project metadata change | Not incorrectly skipped; change is detected | EV-TEST | unverified |
| V12 | Historical `v1.0.3` bootstrap comparison | Explicit decision: no-op or reviewed bump, never overwrite | EV-REMOTE, EV-CMP, EV-REVIEW | unverified |
| V13 | Immutable-release setting incompatible | Fail closed with actionable message; tags unmoved | EV-REMOTE, EV-TEST | unverified |
| V14 | Audit Telegram boundaries | Telegram source/workflows unchanged by the release transaction | EV-TEST, EV-REVIEW | unverified |
| V15 | Workflow YAML not a second version/SDK table | Config values trace to `release/Config.fsx` only | EV-WF, EV-REVIEW | unverified |

## 3. Constraints carried into design (not yet frozen)

- The immutable-container conflict must fail closed; no new tag model.
- Only the strict embedded-`revision` exception is allowed; actual historical
  byte comparison is required later (blocked by the remote-download transport
  gap in `research.md` §5.I).
- Review profile is combined plus DevOps/security because release integrity and
  API credentials are material.
- No implementation solution is frozen by this document.

## 4. Known blocking ambiguities

A1 concurrency group key / `queue: max` equivalence; A2 `workflow_dispatch`
inputs; A3 immutable-conflict response (configure vs fail); A4 ambiguous-upload
test determinism boundary; A5 failure-reason conciseness / transient post-upload
5xx. See `research.md` §5 for evidence and impact.

W5 tester additions to `Ambiguities`:

A6 runner binary state: `infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe`
is the owner-built apphost that ships with the repo (W4 build evidence
`runId=sSO_NO5zPkb5zICwmItBZtjZzLLmK_aeLwrMzl7jcf8`, 0 errors / 0 warnings).
The tester cannot rebuild (engineer-owned). `Runner.fsx` exercises both the
empty-argv exit-2 path and the dirty-tree structured-JSON + exit-4 path
against the real binary; the binary is owned by the engineer, the test
asserts observable contract behaviour.

A7 hosted GitHub Actions behavior: actual `api.github.com` reads, POST
uploads, redirect handling, 404/5xx semantics, and `queue: max` FIFO
semantics are **unverified hosted**. The loopback HttpListener and
`localMirrorObserver` simulate the typed-state classification but do not
exercise the hosted API. No independent queue adapter test is labeled as
hosted verification.

A8 consecutive unrelated HEADs → unchanged classification: exercised locally
via `Historical.fsx::consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent`
when `MCP_RELEASE_HISTORICAL_ROOT` points at the locally downloaded v1.0.3
archives; otherwise the test skips gracefully. The `Compare` decision
matrix short-circuit + revision-only-equivalent path is the underlying
contract covered by `Compare.fsx::revisionOnlyEquivalentDecision`.
