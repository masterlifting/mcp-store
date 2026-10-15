# Frozen remediation solution — GHR-35

Active tracking successor: GHR-36, explicitly authorized by the user after the
coordinator recorded unsatisfiable guard placement in GHR-35 E4. The original
record is preserved. All criteria, stage ordering and independent review gates
remain mandatory; this changes tracking only, not the frozen technical solution.

Authority: Requirements.md, Threads.md and ../original.md. Baseline:
05a69ced8319b61a6bf0e24cad6d320a0a879283. Review profile: combined, with
DevOps and security review for publication/workflow trust boundaries.

## Independent design gate

Primary: ses_ed2cb8abcffeSFpPGsym1MaOoe.
Challenger: ses_ed2cb8a45ffe6OR7zFkS0W7l4e.
Both inspected equivalent inputs independently; neither built, tested or edited.
Both recommend one compiled application, canonical configuration projected by
isolated FSI, shared orchestration, production-referencing compiled tests, and
toolchain enforcement scoped to bootstrap. These are proposals, not acceptance.

## Selected boundaries

- Consolidate the existing Release.fsproj, retaining ReleaseRunner identity.
  Reusable release logic becomes ordered .fs modules without initialization
  side effects. The existing Runner.fs becomes the explicit thin entrypoint.
- Preserve canonical Config.fsx policy. A bounded policy loader evaluates it in
  an isolated child FSI process and strictly parses the projection. Never mirror
  versions, toolchains, archive allowlists or URIs into enrollment/configuration.
- Rename shared Provenance.fsx to Provenance.fs, retaining its module and types;
  packaging scripts load that same source and the application compiles it.
  Update exact references, without rewriting packaging or adding a shim.
- Keep current pure archive/comparison/metadata/evaluation boundaries. Extract
  transaction execution from Runner only as needed to expose the real production
  path to tests. Capture/policy/process/HTTP belong to the imperative boundary.
  One shared orchestration module sequences build, pins, producer tests, candidate
  validation and transaction, and owns structured phase failures.
- Remove duplicated producer Release.fsx wrappers; no legitimate consumer has
  been identified that requires a compatibility shim. Necessary Build/Pins and
  distribution-test entrypoints remain child scripts.
- Use one compiled test project referencing Release.fsproj. Preserve every
  existing release assertion through a migration ledger, including historical
  integration. Missing prerequisites must be reported, not silently skipped.
- Use a small Participants.json roster containing only the two current IDs and
  canonical config paths. Bootstrap, CLI and workflow consume or deterministically
  validate against it. Explicit rejection of enrollment drift is required.
- Shared SDK/runtime/platform equality is a current bootstrap limitation;
  generic decisions, metadata and HTTP transport use producer-owned expectations
  rather than requiring an ambient global toolchain identity.

Module order follows actual dependencies: shared provenance; contract; archive;
comparison; metadata; transport; evaluation; process capture; policy; bootstrap
verification; transaction; orchestration; CLI. Do not create unused layers or
split modules merely to match this list. All new source paths use one-word names.

## Integrity policy

Archive and metadata must both be observed before any publication. Unknown,
corrupt or conflicting metadata blocks archive POST. Orphan metadata with no
archive is rejected unless an explicitly verified safe recovery can be proven;
no speculative orphan recovery is required. Existing verified equivalent archive
with absent metadata retains metadata-only recovery.

Validate every required metadata identity field against canonical producer policy
and verified published bytes. Published revision comes from the remote embedded
manifest, not candidate HEAD. Do not introduce historical leniency: retain only
an existing justified compatibility policy, make it explicit and test it. If the
baseline cannot establish that policy, fail closed and surface the question.

Initial observation, post-upload readback and idempotent recovery all require a
present well-formed API SHA-256 matching downloaded bytes. Candidate byte equality
is not a substitute for claimed-digest verification.

## Stages and evidence

Follow W2–W6 in the user's order. Engineer owns production/builds; tester owns
compiled migration, focused regression design and execution; DevOps owns workflow
and launcher integration. Only one editable owner operates at a time in this tree.
Each stage gets compilation and relevant affected tests before advancing.

Stage 1 preserves current behavior while changing execution architecture. Stage 2
renames blocking/Checker.fs and blocking/Tests.fs and audits affected comments.
Stage 3 integrates enrollment/bootstrap boundaries. Stage 4 fixes three integrity
defects with actual transport negatives and zero-POST preflight assertions.
Stage 5 executes complete producer/release/package/provenance verification.

Independent Discovery follows actual build/test evidence; freeze accepted findings,
perform bounded remediation and targeted Verification, then a fresh task-first
Acceptance Audit. All twelve criteria remain pending until verified. No real
publication, merge, thread resolution, unconfirmed commit/push or consumer changes.

## Rejected alternatives

No retained hybrid reusable .fsx release engine, duplicate wrappers, parallel
application, test-only decision model, policy mirrors, plugin framework, dynamic
discovery, arbitrary-language adapters, or unrelated producer packaging rewrite.
Future multi-language support belongs to #37; task-document support to #36.
