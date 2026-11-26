# Release automation requirements and evidence matrix (GHI-24)

Status: W5 narrow pass-1 regression appended; columns distinguish **local**
(tests under `infrastructure/release/tests/` plus updated existing suites),
**simulated** (loopback HttpListener or `localMirrorObserver` injecting the
seam), **actual remote read** (none in this slice), and **unverified hosted**
(GitHub Actions queue, real `api.github.com` uploads, immutable-release
detection). The matrix is not retroactive; rows not touched remain
`unverified`. Authority: [issue #24](https://github.com/masterlifting/mcp-store/issues/24)
is the available live source; it is not an immutable snapshot of the original body.
Requirement numbering below retains the historical 1–72 extraction. The original
issue snapshot (`.tasks/GHI-24/docs/original.md`), baseline and gap analysis
(`.tasks/GHI-24/docs/research.md`), and full evidence narrative
(`.tasks/GHI-24/docs/tests.md`) were removed from the current repository tree when
`.tasks/` was untracked. They are no longer available at those paths; no replacement
evidence location is asserted. If this file and the live issue conflict, the issue
wins. Retained matrix entries are historical claims, not fresh verification.

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
| 1 | One root workflow deterministically evaluates/publishes dotnet + workflow on every `main` push except a Telegram-only revision (the `paths-ignore: ['telegram/**']` exclusion; see row 28) | EV-WF (YAML), EV-BUILD (W3 packages.md), EV-TEST (`Runner.fsx::contractJsonShape`, `runnerBinaryExists`) | local-run static + simulated; hosted unverified |
| 2 | `telegram/**` and Telegram-owned workflows out of scope; Telegram is not a .NET release input, so the release and Unix qualification workflows exclude Telegram-only changes, and that exclusion is not a Telegram CI | EV-WF, EV-TEST (`Telegram.fs` exact-exclusion and non-input proof), EV-REVIEW | local-run, deterministic |
| 3 | Required predecessors #21/#23/#26/#27 satisfied | EV-AUDIT, baseline `research.md` | unverified (status from W3 prior slice) |
| 4 | Dependency graph honored (this → #28 → …) | EV-AUDIT | unverified (status from W3 prior slice) |
| 5 | Land before later producer work on a stable publication path | EV-AUDIT | unverified (status from W3 prior slice) |
| 6 | Single root release/integrity workflow: `.github/workflows/release.yml` owns publication and its trigger stays `push to main` with the single `telegram/**` exclusion; a separate non-publishing qualification workflow may exist without becoming a release path | EV-WF: YAML read of `.github/workflows/release.yml` (`push` → `main`, sole writer) and `.github/workflows/unix.yml` (`pull_request` with the same single exclusion, `permissions: contents: read`, no publish step, no credentials); EV-TEST (`Release.Tests.Coherence::the release workflow isolates preparation, trusted building, and data-only publication`; `Release.Tests.Telegram::release participant isolation assertions`) guards the release graph and participant roster, not the qualification boundary; that boundary rests on local static checks plus focused review, with no deterministic automated enforcement. The trigger shape of both workflows is enforced by `infrastructure/release/Check.ps1` in the publisher job, which blocks the release. The pre-merge subset in `unix.yml` is advisory only: with no required status check on `main`, a pull request that broadens `unix.yml`'s own filter also skips the step that polices it | local-run static + focused review (qualification boundary not auto-enforced; pre-merge guard advisory) |
| 7 | Merged PR and direct push both fire; branch state is authority | EV-WF | unverified hosted |
| 8 | No manual release step on normal path | EV-WF: neither workflow has `workflow_dispatch` (asserted by `Check.ps1`); because dispatch stays forbidden, a failed release is recovered by re-running the failed run from the GitHub Actions UI or by a follow-up push touching a non-Telegram path, since a Telegram-only or empty push no longer triggers the release; EV-TEST (`Telegram.fsx::releaseWorkflowMatrixNamesOnly`) | local-run, deterministic |
| 9 | `workflow_dispatch` only if identical deterministic logic and cannot overwrite | EV-WF | unverified hosted |
| 10 | Canonical per-producer config is single source of truth for the per-component policy (id/version/archive/URI/allowlist/platform). Reconciled (Store #46, revised after PR #85 review): the shared .NET toolchain is not per-component policy and is no longer in `*/release/Config.json`. The SDK version is authored once in the root `global.json`; the TFM, F# language version and `ExpectoVersion` are owned by the root `Directory.Build.props`; `Release.fsproj` carries no duplicate TFM or language version. After SDK setup the selected SDK, TFM, language version and runtime come from native MSBuild evaluation; the runtime is never authored but is the evaluated `DefaultRuntimeFrameworkVersion` of the unique `KnownFrameworkReference` for `Microsoft.NETCore.App` at the evaluated TFM. `platform` stays per component. A producer config that declares `sdkVersion` or `runtimeVersion` is rejected. The earlier strict whole-file reader, PowerShell mirror and documented `Release.fsproj` exception are superseded; evidence recorded before this revision describes that earlier model | EV-BUILD; EV-WF: `Check.ps1` owner assertions; EV-TEST (planned `Release.Tests` policy suites; `Bootstrap.fsx::updatePs1ProjectEmitsCanonicalValues` is earlier, pre-owner evidence). Not qualified in this row: compiled-suite, candidate-comparison and hosted results | local-run partial (native evaluation, `Update.ps1 -Project/-Verify` and `Check.ps1` observed locally); compiled-suite and hosted evidence not claimed |
| 11 | Workflow YAML must not duplicate producer versions | EV-WF; EV-TEST (`Bootstrap.fsx::bootstrapKeysAreStable`) | local-run, deterministic |
| 12 | CI never auto-increments/invents a semantic version | EV-WF, EV-TEST (`Bootstrap.fsx::updatePs1ProjectRejectsDuplicateLiteral`) | local-run, deterministic |
| 13 | Smallest deterministic metadata projection if SDK setup needs config. Reconciled (Store #46, revised after PR #85 review): the pre-SDK projection (`Update.ps1 -Project`) is plain PowerShell that reads only what installation needs from `global.json` and the per-component policy and emits the SDK version, platform, component list and per-component version and entry, but **no runtime version** and no verified handoff, because no SDK exists at that point. `Update.ps1 -Verify` runs after SDK installation, evaluates the SDK natively, derives the runtime, checks installed-runtime compatibility (same major.minor, patch at least the derived value, a prerelease never satisfying a stable value) and only then writes the verified SDK, runtime, TFM and language version to `GITHUB_ENV` | EV-WF, EV-TEST (`Bootstrap.fsx::bootstrapEntryEmitsCanonicalSummary` is earlier evidence; planned `Release.Tests` `Invocation` projection cases) | local-run partial (`-Project` and `-Verify` observed with SDK 11.0.100, runtime 11.0.0; the verified values carried TFM `net11.0` and language version `9.0`); compiled-suite evidence not claimed |
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
| 28 | No filter may skip a .NET-affecting revision | EV-WF: both triggers carry exactly one `paths-ignore` block with the single item `telegram/**`, and no `paths`, `branches-ignore`, `tags*` or extra event, asserted by the exact-shape check in `Check.ps1` and its negative tests (`Coherence.fs`); the release evaluates the head tree, not the diff, so a skip delays a release rather than losing it. GitHub's changed-file comparison has documented limits (an empty diff does not run the workflow; a diff above the file cap can fall outside the evaluated range; a push above the commit cap always runs), so the exclusion is not an absolute claim | local-run, deterministic; hosted trigger behavior unverified |
| 29 | Preferred model: candidate → compare declared version → no-op/publish/bump-fail | EV-CMP, EV-TEST (`Evaluate.fsx::publicStatusSetIsExhaustive`) | local-run, deterministic |
| 30 | Path optimization only if tests prove it cannot hide a changed package; the only filter is the Telegram exclusion, proved by the non-input proof (no production release module, release script, roster entry or workflow line consumes Telegram, extended to the `dotnet/**` and `workflow/**` build inputs) | EV-TEST (`Telegram.fs`; `Compare.fsx::changedCompressionMethodRejection`, `changedPayloadHashRejection`) | local-run, deterministic |
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
| 44 | Use existing component release containers/tags (`dotnet`, `workflow`); the Resolver is a third release artifact with its own `resolver` container/tag, which an explicitly confirmed operator action must create as a published, non-draft release before its first publication, and which is not GitHub-immutable | EV-WF, EV-REMOTE | static YAML checked for `dotnet` and `workflow`; `resolver` container not created or observed; live remote unverified |
| 45 | Missing container: create via accepted tag policy or fail with actionable error | EV-WF, EV-TEST (`Evaluate.fsx::failedOnArchiveMissing`; `Publish.fsx::localMirrorObserver` Missing path) | local-run simulated; live HTTP unverified hosted |
| 46 | Do not move/rewrite an existing component tag | EV-WF, EV-REVIEW | unverified hosted |
| 47 | Run canonical producer pin preparation as part of release validation | EV-BUILD, EV-CMP (`Metadata.fsx::roundTripByteExactIdentical`) | local-run, deterministic |
| 48 | Workflow must not mutate any consumer repository | EV-WF, EV-REVIEW | unverified hosted |
| 49 | Publish one consumer-neutral producer metadata projection | EV-BUILD, EV-TEST (`Metadata.fsx::makeProjection` round-trip, `writeToFileDeterministic`); EV-TEST (`Evaluate.fsx::observedMetadataAssetShaDiffersArchiveShaAndCarried` proves the metadata asset sha is verified separately from the archive sha) | local-run, deterministic |
| 50 | Optional: expose metadata as Actions artifact/summary; no consumer-specific formats | EV-WF: YAML references `release-metadata-<component>` artifact | local-run, deterministic |
| 51 | Deterministic tests/helpers without real publication for ordinary local runs | EV-TEST (`infrastructure/release/tests/*.fsx`, 9 entrypoint scripts and 272 assertions in Test.ps1; `Fixture.fsx` helper + `Historical.fsx` integration = 282 total assertions) | local-run, deterministic |
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
| 64 | Telegram source and Telegram-owned workflows remain untouched (only the exclusion line in the two non-Telegram workflows is added) | EV-TEST (`Telegram.fs`), EV-REVIEW | local-run, deterministic |
| 65 | Non-goals respected (no Telegram CI; the `telegram/**` exclusion is not one, auto bump, consumer edits, tag-model change, registry, generic framework) | EV-AUDIT, EV-REVIEW | local-run (`Telegram.fsx::noTelegramWorkflowFiles`, `noUvPytestInReleaseWorkflows`) |
| 66 | Audit snapshot: #21/#23/#26/#27 closed; no root workflow; both configs `1.0.3` / SDK `11.0.100-rc.1.26425.128` | baseline `research.md` | unverified (status from W3 prior slice); the W4 manifest refresh bumped to 1.0.4 (see `packages.md`) |
| 67 | Historical bootstrap: compare candidate vs published `v1.0.3`; PR authors reviewed bump if beyond revision | EV-REMOTE, EV-CMP, EV-REVIEW (`packages.md` §7 records byte comparison; the 1.0.4 bump is the authored response) | local-run, deterministic on the bootstrap assets at `MCP_RELEASE_HISTORICAL_ROOT` |
| 68 | Install/verify exact configured SDK actually executes build/test/publish; no second source; select/validate platform+arch. Reconciled (Store #46, revised after PR #85 review): the single source for the SDK is the root `global.json` (`rollForward: disable`, `allowPrerelease: false`); `Update.ps1 -Verify` compares the SDK **selected** by native MSBuild evaluation at the repository root with the owner and fails closed on disagreement, and the SDK root chosen by the native resolver is accepted without custom root listing. Only after verification does it hand the verified SDK, runtime, TFM and language version to later steps through `MCP_RELEASE_VERIFIED_*`; `-Project` never writes them. Publication consumes the verified values, fails closed when they are missing or invalid and performs no SDK discovery in the credential step. The isolated publisher stage receives only the verified TFM and language version, stays outside the checkout and disables automatic props/targets imports; `Check.ps1` asserts `-Verify` precedes both the publish invocation and the `GITHUB_TOKEN` step. The earlier `MCP_RELEASE_SDK_ROOT` handoff, single-root rule and strict reader are superseded | EV-BUILD, EV-WF, EV-TEST (planned `Release.Tests` invocation and orchestration suites; `Bootstrap.fsx::updatePs1ProjectEmitsCanonicalValues`, `updatePs1ProjectRejectsDuplicateLiteral` and `Invocation.fsx` negative cases are earlier evidence describing the superseded model) | local-run partial (`-Verify` observed with SDK 11.0.100; isolated-stage generation and publish locally exercised, see task evidence); hosted `setup-dotnet` image resolution, compiled-suite and candidate-comparison evidence unverified |
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
| V3 | Push docs-only change (a Telegram-only push no longer triggers the release or Unix workflows) | Both producers report `unchanged` despite new embedded revision | EV-TEST (`Historical.fsx::consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent`; Req 71) | local-run, deterministic |
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
| V15 | Workflow YAML not a second version/SDK table | Per-component values for all three participants (`dotnet`, `workflow`, `resolver`) trace to their own `release/Config.json`, and the shared SDK to the root `global.json`; none is repeated in workflow YAML | EV-WF, EV-REVIEW | unverified |

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
