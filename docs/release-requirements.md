# Release automation requirements and evidence matrix (GHI-24)

Status: **research deliverable — implementation plan not frozen; every evidence
row is `unverified`.** This document maps the authoritative issue #24
requirements and user verification scenarios to *planned* evidence. It does not
claim any verification has occurred.

Authority: the original issue body is preserved verbatim at
`docs/GHI-24/original-issue.md`. Requirement numbering below is the
1–72 extraction of that body (`docs/GHI-24/research.md` records the
baseline and open gaps). If this file and the issue conflict, the issue wins.

Evidence IDs: `EV-BUILD` producer build/package validation; `EV-TEST`
deterministic tests; `EV-WF` static/config validation of the root workflow;
`EV-REMOTE` read-only GitHub API metadata/digest observation; `EV-CMP` new
deterministic comparison/provenance helper tests; `EV-REVIEW` independent
review; `EV-AUDIT` separate task-first acceptance audit.

---

## 1. Requirement inventory → planned evidence

| Req | Requirement (condensed) | Planned evidence | Status |
| --- | --- | --- | --- |
| 1 | One root workflow deterministically evaluates/publishes dotnet + workflow on every `main` push | EV-WF, EV-BUILD, EV-TEST | unverified |
| 2 | `telegram/**` and Telegram-owned workflows out of scope | EV-WF, EV-TEST, EV-REVIEW | unverified |
| 3 | Required predecessors #21/#23/#26/#27 satisfied | EV-AUDIT, baseline `research.md` | unverified |
| 4 | Dependency graph honored (this → #28 → …) | EV-AUDIT | unverified |
| 5 | Land before later producer work on a stable publication path | EV-AUDIT | unverified |
| 6 | Single root `.github/workflows/release.yml`; trigger `push to main` | EV-WF | unverified |
| 7 | Merged PR and direct push both fire; branch state is authority | EV-WF, EV-TEST | unverified |
| 8 | No manual release step on normal path | EV-WF | unverified |
| 9 | `workflow_dispatch` only if identical deterministic logic and cannot overwrite | EV-WF, EV-TEST | unverified |
| 10 | Canonical per-producer config is single source of truth (id/version/SDK/archive/URI/allowlist) | EV-BUILD, EV-REVIEW | unverified |
| 11 | Workflow YAML must not duplicate producer versions | EV-WF, EV-REVIEW | unverified |
| 12 | CI never auto-increments/invents a semantic version | EV-WF, EV-TEST | unverified |
| 13 | Smallest deterministic metadata projection if SDK setup needs config | EV-WF, EV-REVIEW | unverified |
| 14 | Harness-independent: producers + producer metadata only | EV-WF, EV-REVIEW | unverified |
| 15 | Preserve fixed `dotnet`/`workflow` tags and `v1.0.3.zip`-style assets; no tag-model redesign | EV-REMOTE, EV-REVIEW | unverified |
| 16 | Per-component transaction: SHA → SDK → build/tests → distribution → validation → metadata → asset absent/no-op | EV-BUILD, EV-TEST | unverified |
| 17 | Producers fail/report independently; no publish before validation | EV-TEST, EV-BUILD | unverified |
| 18 | Never overwrite/clobber a published versioned asset | EV-TEST, EV-REVIEW | unverified |
| 19 | Asset absent: validation passes → upload; report archive/manifest SHA-256, revision, version, URI | EV-BUILD, EV-REMOTE | unverified |
| 20 | Asset exists: download/read, verify digest + embedded manifest, deterministic compare | EV-CMP, EV-REMOTE | unverified |
| 21 | Embedded `revision` exception is exact; do not falsify/remove revision | EV-CMP, EV-REVIEW | unverified |
| 22 | Byte-identical asset → successful no-op | EV-CMP | unverified |
| 23 | Revision-only difference → no-op only after full remote digest/structure/payload/manifest match | EV-CMP, EV-REMOTE | unverified |
| 24 | Any other/inconclusive difference → fail closed with version-bump diagnostic | EV-CMP, EV-TEST | unverified |
| 25 | Comparison strict, deterministic, testable without GitHub | EV-CMP | unverified |
| 26 | No-op reports published hash/manifest/revision plus distinct evaluated HEAD | EV-CMP, EV-REMOTE | unverified |
| 27 | No `--clobber`/replacement/delete-reupload/mutable tags/assets | EV-TEST, EV-REVIEW | unverified |
| 28 | Do not rely solely on fragile path filters | EV-WF, EV-TEST | unverified |
| 29 | Preferred model: candidate → compare declared version → no-op/publish/bump-fail | EV-CMP, EV-TEST | unverified |
| 30 | Path optimization only if tests prove it cannot hide a changed package | EV-TEST | unverified |
| 31 | Every archive/manifest identifies exact committed `main` revision | EV-BUILD, EV-CMP | unverified |
| 32 | Preserve clean-HEAD/revision validation semantics in shared infra | EV-CMP, EV-TEST | unverified |
| 33 | `dist/**` output must not weaken clean-source provenance checks | EV-BUILD, EV-TEST | unverified |
| 34 | Per-component summary: status published/unchanged/failed, version, revision, URI/name, archive+manifest SHA-256, reason | EV-BUILD, EV-REMOTE | unverified |
| 35 | Minimum GitHub Actions permissions; write limited to contents/assets | EV-WF, EV-REVIEW | unverified |
| 36 | Prefer `GITHUB_TOKEN`; no PAT | EV-WF, EV-REVIEW | unverified |
| 37 | Repo/component-safe serialization so rapid merges cannot race | EV-WF, EV-TEST | unverified |
| 38 | Newer run must not silently cancel an in-progress publication | EV-WF, EV-TEST | unverified |
| 39 | `cancel-in-progress: false` alone insufficient (pending-run replacement) | EV-WF, EV-TEST | unverified |
| 40 | Use `queue: max` (or prove equivalent); report overflow/cancellation; keep publication serialized | EV-WF, EV-TEST | unverified |
| 41 | Fail closed on build/test/package failure, revision mismatch, metadata inconsistency, asset diff, ambiguous upload, unresolvable container | EV-TEST, EV-BUILD | unverified |
| 42 | On ambiguous upload, observe remote before retry; never retry blindly | EV-TEST | unverified |
| 43 | One producer's failure must not publish an unvalidated artifact for that producer | EV-TEST | unverified |
| 44 | Use existing component release containers/tags (`dotnet`, `workflow`) | EV-WF, EV-REMOTE | unverified |
| 45 | Missing container: create via accepted tag policy or fail with actionable error | EV-WF, EV-TEST | unverified |
| 46 | Do not move/rewrite an existing component tag | EV-WF, EV-REVIEW | unverified |
| 47 | Run canonical producer pin preparation as part of release validation | EV-BUILD, EV-CMP | unverified |
| 48 | Workflow must not mutate any consumer repository | EV-WF, EV-REVIEW | unverified |
| 49 | Publish one consumer-neutral producer metadata projection | EV-BUILD, EV-REVIEW | unverified |
| 50 | Optional: expose metadata as Actions artifact/summary; no consumer-specific formats | EV-WF | unverified |
| 51 | Deterministic tests/helpers without real publication for ordinary local runs | EV-TEST, EV-CMP | unverified |
| 52 | Cover publish/no-op/bump-fail/validation-gate/ambiguous/two-component/shared-infra/concurrency/Telegram-exclusion | EV-TEST | unverified |
| 53 | Keep decision logic deterministic and separate from the thin transport shell | EV-CMP, EV-REVIEW | unverified |
| 54 | Merge/push to `main` auto-evaluates both producers | EV-WF, EV-REMOTE | unverified |
| 55 | New source-controlled versions publish without manual release step | EV-WF, EV-REMOTE | unverified |
| 56 | Byte-identical / revision-only equivalent → clean no-op retaining published revision/hashes | EV-CMP | unverified |
| 57 | Same-version candidates differing beyond the exception fail, require bump | EV-CMP, EV-TEST | unverified |
| 58 | No asset is ever overwritten | EV-TEST, EV-REVIEW | unverified |
| 59 | Artifacts built from and identify the exact canonical `main` SHA | EV-BUILD, EV-CMP | unverified |
| 60 | Producer validation runs before publication | EV-BUILD, EV-TEST | unverified |
| 61 | Version/SDK/release metadata not duplicated in workflow YAML | EV-WF, EV-REVIEW | unverified |
| 62 | Release output exposes deterministic hashes/revision suitable for pin updates | EV-BUILD, EV-CMP | unverified |
| 63 | Concurrent merges cannot race into mutable/duplicate publication | EV-WF, EV-TEST | unverified |
| 64 | Telegram source and Telegram-owned workflows remain untouched | EV-TEST, EV-REVIEW | unverified |
| 65 | Non-goals respected (no Telegram CI, auto bump, consumer edits, tag-model change, registry, generic framework) | EV-AUDIT, EV-REVIEW | unverified |
| 66 | Audit snapshot: #21/#23/#26/#27 closed; no root workflow; both configs `1.0.3` / SDK `11.0.100-rc.1.26425.128` | baseline `research.md` | unverified |
| 67 | Historical bootstrap: compare candidate vs published `v1.0.3`; PR authors reviewed bump if beyond revision | EV-REMOTE, EV-CMP, EV-REVIEW | unverified |
| 68 | Install/verify exact configured SDK actually executes build/test/publish; no second source; select/validate platform+arch | EV-BUILD, EV-WF, EV-TEST | unverified |
| 69 | Check out event `github.sha` (not moving `main`); verify HEAD+manifest; stale re-run fails closed; never move tags | EV-WF, EV-CMP | unverified |
| 70 | Preserve fixed tags/URLs; detect incompatible immutable-release settings and fail with actionable message | EV-REMOTE, EV-WF, EV-TEST | unverified |
| 71 | Additional tests: consecutive unrelated HEADs → unchanged; identical/revision-only/changed/malformed/bootstrap/pending-queue/Telegram | EV-TEST | unverified |
| 72 | Clarifications are scope clarifications, not permission to redesign public formats | EV-AUDIT, EV-REVIEW | unverified |

## 2. User verification scenarios

Each scenario is how a reviewer/user confirms an acceptance criterion (#54–64)
or required test (#52/#71). None is executed in this slice.

| # | Scenario | Expected observable | Planned evidence | Status |
| --- | --- | --- | --- | --- |
| V1 | Push/merge new Dotnet version to `main` | `dotnet` run reports `published` with archive/manifest SHA-256, revision, URI | EV-WF, EV-BUILD, EV-REMOTE | unverified |
| V2 | Push/merge new Workflow version to `main` | `workflow` run reports `published` | EV-WF, EV-BUILD, EV-REMOTE | unverified |
| V3 | Push docs-only/Telegram-only change | Both producers report `unchanged` despite new embedded revision | EV-TEST (Req 71) | unverified |
| V4 | Re-publish byte-identical asset | `unchanged`; published revision/hashes retained | EV-CMP | unverified |
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
