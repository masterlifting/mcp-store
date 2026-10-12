# PR #35 remediation acceptance baseline

Origin: GHI-24 / masterlifting/mcp-store#24 / PR #35.
Runtime: GHR-35, authorized by the user as a linked remediation task.
Source baseline: `05a69ced8319b61a6bf0e24cad6d320a0a879283`, branch `ghi-24`;
local worktree clean and identical to the observed open, unmerged PR head.

The original issue snapshot in `../original.md` remains authoritative. The eight
review threads in `Threads.md` and the user's criteria below are additional
mandatory inputs. Old internal approvals and test counts do not establish
acceptance of this remediation. Runtime AC1 through AC12 map directly to the
user-facing AC-01 through AC-12; every criterion starts pending/FAIL.

## Scope and non-goals

Use the existing `infrastructure/release/Release.fsproj` as one compiled F#
application with ordered reusable `.fs` modules, an explicit thin CLI and a
compiled regression project using production implementation. Consolidate the
duplicated producer orchestration, not the independent canonical producer policy.
Retain only scripts with a concrete purpose, including pre-SDK bootstrap and
existing producer packaging entrypoints where appropriate.

Exactly Dotnet and Workflow participate. Participation has one authority or a
deterministic cross-stage consistency check. Common SDK/runtime/platform checks
are current bootstrap limitations, not universal release-domain requirements.
Arbitrary languages, dynamic discovery, adapters, plugins and extra toolchain
orchestration are deferred to #37.

Preserve fixed tags/URIs, immutable assets, exact revision provenance, strict
archive equivalence, safe recovery/idempotency, fail-closed decisions and all
useful `docs/GHI-24/` evidence. The deletion request was withdrawn; #36 separately
owns task-document support. No Telegram, consumer repository, unrelated migration,
second engine or compatibility shim. No real release upload during development.
Do not merge or resolve review threads. Commits, pushes and remote review requests
remain separately confirmation-gated.

## AC-01 — Metadata Preflight Safety (P1)

- Before uploading a new ZIP, verify any existing same-component/version metadata.
- Conflicting, corrupt, malformed or unobservable metadata fails before ZIP upload.
- Distinguish supported recovery from incompatible existing metadata explicitly.
- Direct regressions cover `ArchiveAbsent + MetadataObserved(mismatch)`,
  `ArchiveAbsent + MetadataCorrupt`, and metadata observation errors.
- Every failure scenario asserts **zero upload POST requests**.

## AC-02 — Complete Published Metadata Validation (P1)

- Validate component ID, version, archive name, canonical URI, archive hash,
  manifest hash, published revision, schema version and applicable toolchain fields
  against authoritative policy and the verified remote archive.
- Published revision matches the observed embedded revision, not current HEAD.
- No required identity mismatch can return `unchanged`.
- Each relevant field has an independent negative regression.
- Any historical metadata compatibility policy is explicit and tested.

## AC-03 — GitHub Digest Verification (P2)

- Post-upload and idempotent recovery validate the API-claimed SHA-256 against
  downloaded bytes.
- Missing, malformed or mismatched claimed digests fail closed.
- Reject invalid API digests even when downloaded bytes exactly match the candidate.

## AC-04 — Compiled F# Architecture (P2)

- Domain, evaluation, metadata, transport and orchestration are reusable ordered
  `.fs` modules in the existing project.
- CLI is explicit and thin; reusable modules have no unintended initialization effects.
- Dependencies/compile order are explicit; normal execution uses the compiled app.
- Necessary bootstrap and existing packaging scripts remain functional.
- No parallel engine, compatibility layer or speculative abstraction.

## AC-05 — DRY and Single Orchestration Path (P2)

- Consolidate duplicated Dotnet/Workflow release wrappers.
- Build/pins/tests/runner sequencing and failure reporting have one implementation.
- Producer-specific configuration remains independent and canonical.
- Both producers preserve equivalent operational behavior and structured diagnostics.
- Tests prove neither producer bypasses validation/publication safeguards.

## AC-06 — Producer Registration Consistency (P2)

- Exactly Dotnet and Workflow are enrolled.
- One enrollment authority or deterministic consistency across all participant lists.
- Deliberate workflow/bootstrap/CLI/config participation drift fails CI.
- Enrollment duplicates no producer version/SDK/allowlist/URI policy.
- Telegram is neither enrolled nor modified.

## AC-07 — Toolchain Boundary (P2)

- Generic evaluation/archive identity/metadata/transport do not require a global
  .NET SDK/runtime identity.
- Document common toolchain checks as current Dotnet/Workflow bootstrap constraints.
- Preserve actual SDK/runtime/platform verification for both current producers.
- Defer arbitrary languages and independent toolchains to #37.
- Add no plugins or speculative build-adapter framework.

## AC-08 — One-Word Filesystem Naming (P2)

- Normalize the touched checker and paired tests into domain-oriented one-word paths.
- Update ordered project entries, imports, source checks, tests and tooling references.
- No stale old paths remain in affected implementation.
- All new/touched remediation paths obey policy, with bounded documented external
  tooling/public-identity exceptions where applicable.
- Preserve behavior and public compatibility; no unrelated repository restructuring.

## AC-09 — Source Comment Policy (P3)

- Remove ordinary review/phase markers including `F2`, `F7` and `F9`.
- Ordinary comments are one-line explanations of non-obvious reasons/constraints,
  not behavior narration, banners, ticket history or misleading validation claims.
- Preserve relevant security, immutability and failure-handling rationale.
- Preserve tooling directives and legitimate API documentation.
- Check all changed code/scripts/YAML against canonical architecture/comments/F# rules.

## AC-10 — Release Contract Regression

For **both** producers:

- Valid new version reaches the publish decision.
- Exact and strict revision-only-equivalent archives return successful no-op.
- Same-version changes beyond the exception require an actionable version bump.
- Never overwrite, delete or replace existing assets.
- Report hashes/revision from verified published bytes, not candidate substitution.
- Ambiguous uploads are remotely observed before retry.
- Build/tests/package/provenance checks gate publication.
- Fixed tags/URLs and explicitly authored versions preserve original contracts.

## AC-11 — Verification and Test Evidence

- Release project builds with zero errors; both producer projects build successfully.
- Existing producer suites and the complete compiled release suite actually run/pass
  without ignored, silently excluded or weakened required tests.
- New negative tests exercise actual production logic/transport, not alternate models.
- Account for every regression, disabled case and changed assertion.
- Report exact commands, counts, results and limitations.
- Distinguish simulated HTTP from actual GitHub observations; no upload claim
  without actual publication evidence.

## AC-12 — Review Closure and Scope Control

- Disposition all eight findings with links to changes and verification evidence.
- Preserve evidence; do not revive the withdrawn deletion request.
- #37 future extensibility remains deferred; #36 storage is separate.
- No unrelated producer/Telegram/consumer/repository-wide work.
- Independent architecture/correctness/acceptance review checks actual final diff
  against original issue24, all criteria and canonical contracts.
- No unresolved required correctness/architecture finding before review readiness.
- No merge without satisfactory independent review and explicit approval; this task
  does not merge or resolve reviewers' threads.

## Ordered execution and independent gates

1. Architecture and confirmed duplication; compiled build and affected tests.
2. Affected naming and comments; compiled build and affected tests.
3. Bounded enrollment/toolchain foundations; build and affected tests.
4. Three integrity corrections with direct negative tests; build and affected tests.
5. Full actual unit/integration/regression/package/provenance verification.
6. Independent final architecture/correctness/acceptance checks, bounded remediation
   and targeted Verification, then a separate fresh task-first Acceptance Audit.

Do not equate quantity with coverage or implementation claims with PASS. All twelve
criteria must be PASS with verifiable evidence before requesting another external
review; otherwise retain FAIL and state the blocker.

## Required final report

| Criterion | Result | Implementation evidence | Test evidence |
| --- | --- | --- | --- |
| AC-01 | FAIL — pending remediation | No new implementation evidence | Required direct negatives pending |
| AC-02 | FAIL — pending remediation | No new implementation evidence | Per-field negatives pending |
| AC-03 | FAIL — pending remediation | No new implementation evidence | Invalid API digest negatives pending |
| AC-04 | FAIL — pending refactor | Current hybrid baseline | Compiled execution/test proof pending |
| AC-05 | FAIL — duplicated baseline | Current two wrappers | Shared safeguards proof pending |
| AC-06 | FAIL — inventory drift not guarded | Current competing lists | Deliberate drift cases pending |
| AC-07 | FAIL — boundary proof pending | Current bootstrap/global checks | Scoped enforcement proof pending |
| AC-08 | FAIL — renames pending | Touched multiword checker paths | Reference/behavior proof pending |
| AC-09 | FAIL — policy audit pending | Current review labels | Changed-source checks pending |
| AC-10 | FAIL — current integrity blockers | Original contract retained | Both-producer full regressions pending |
| AC-11 | FAIL — new verification pending | No new build baseline | Actual suites/package results pending |
| AC-12 | FAIL — all eight outstanding | Threads.md | Independent final reviews pending |
