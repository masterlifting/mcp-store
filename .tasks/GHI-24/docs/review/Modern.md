# Tester migration status — Modern.md

This document records the bounded tester-owned migration that
follows the engineer-owned production cutover from the legacy
`Bootstrap.fsx` / `readRawTransport` / 3-entry outer transport /
preparation-receipt / platform-observed evidence surface to the
simplified 2-entry candidate directory, strict JSON `Config.json`,
and `ReleaseHandoff.readCandidate` immutable snapshot surface
described in `Simplification.md`. It is a factual tester status
note, not acceptance evidence, and it does not retitle or
supersede the frozen solution, requirements, or any prior review
thread.

## What changed (tester-owned)

The following tester-owned files in
`infrastructure/release/tests/` and `dotnet/tests/Mcp.Dotnet.Tests/blocking/`
were edited or replaced:

- `Bootstrap.fs` — **deleted**. The compiled release regressions no
  longer load the retired custom FSI gate `infrastructure/release/Bootstrap.fsx`;
  `Main.fs` no longer registers `Bootstrap.tests`, and the
  enrollment-side `writeBootstrapSources`, `runBootstrap`, and
  `bootstrapRejectsConfigIdentityAndRuntimeDrift` helpers were
  removed.
- `testsFixtureHelpers.fs` — **renamed** to `Snapshots.fs`. The
  module identifier `Release.Tests.FixtureHelpers` was kept so the
  existing `Policy.fs` `open` and `readCanonicalConfig` /
  `readConfigFile` / `parseConfigBytes` call sites are unchanged;
  the filesystem one-word rule is satisfied by the new name. The
  reference in `Tests.fsproj` was updated.
- `Tests.fsproj` — `Compile` items updated: removed
  `Bootstrap.fs`; replaced `testsFixtureHelpers.fs` with
  `Snapshots.fs`. No compatibility copy, no parallel file.
- `Main.fs` — suite list updated to drop `Bootstrap.tests`. The
  `--prepare-authority-probe` and `--authority-probe` dispatch path
  is unchanged; it still invokes the real `ReleaseRunner.main`
  prepare and the internal `probeCase` entry.
- `Enrollment.fs` — Bootstrap references removed; the compiled
  enrollment API tests plus `Update.ps1 -Project` and
  `Update.ps1 -Verify` regressions remain.
- `Invocation.fs` — retargeted to the actual production surface:
  one `Update.ps1 -Project` projection regression that reads the
  canonical SDK literal from the Update.ps1 output (no FSI bridge),
  plus two strict-JSON `Config.json` regressions (unknown field
  rejection and embedded-NUL rejection). The custom FSI bootstrap
  invocation suite was retired.
- `Runner.fs` — `missingIndependentClosure` was retired (its
  invocation argument shape, the legacy `bootstrapReceipt` /
  `transportEvidence` / `trustedReceipt` keys, and the per-flag
  preauth assertions described a surface the modern runner does
  not expose). Five meaningful modern-publish-context regressions
  replace it: wrong `--repo`, uppercase `--event-sha`, unknown
  publish flag, missing required flag, and unregistered
  config-path refusal. Each asserts the production JSON exit 4
  diagnostic.
- `Telegram.fs` — `productionModulesExcludeTelegram` no longer
  references the retired `infrastructure/release/Bootstrap.fsx`
  source path. The two remaining CoreSourceProject deps
  (`dotnet/release/Config.json`, `workflow/release/Config.json`)
  and the existing roster/workflow guards are retained.
- `Authority.fs` — the 7-or-8-argument auto-fallback invocation
  was removed. The probe driver now invokes the real production
  `runPublishPipelineWithTransportFactory` with its single actual
  signature: `(createTransport, policy, snapshot, eventSha,
  metadataLocalPath, owner, repo)` — exactly seven positional
  arguments sourced from the current `Runner.fs` line 75. The
  `PublishCli` 5-field record is still constructed via
  `FSharpValue.MakeRecordNonPublic` as a diagnostic; the runtime
  invoke path uses individual fields because the production helper
  consumes positional arguments. The six unauthorized child
  probes (3 reject scenarios × 2 producers) preserve the
  factory=0 / requests=0 / exit=4 invariants; the two authorized
  child probes preserve the in-memory 500-scripted-tag GET-only
  observation with exit=1 (transaction failure, not pre-contract).
- `Integrity.fs` — the verified candidate is constructed through
  the canonical `ReleaseHandoff.readCandidate` data directory
  (archive + `distribution.json` only); the legacy
  `parsePreparationReceipt`, `makeOuterDigest`,
  `ExpectedMetadata` / `ObservedPlatformMetadata` /
  `ArtifactEvidence`, and `readRawTransport` helpers were removed.
  The 14 critical regressions covering the zero-POST / 38 GET /
  18 identity / digest / no-retry / recovery invariants remain
  intact across both Dotnet and Workflow producers.
- `Evaluate.fs` — `verifiedCandidate` was rewritten to call
  `ReleaseHandoff.readCandidate` on a fresh candidate directory
  and then `VerifiedCandidate.createFromSnapshot`. The legacy
  outer-transport / preparation-receipt / 3-entry ZIP builder was
  removed; the 18 canonical evaluator regressions retain the same
  scripted-observer coverage of `ArchiveAbsent` /
  `MissingArchive` / `ImmutableArchive`, recovery, identity mismatch,
  and finalize states.
- `dotnet/tests/Mcp.Dotnet.Tests/blocking/Checker.fs` — the
  `topLevelBridgeSites` allowlist no longer carries the retired
  `infrastructure/release/Bootstrap.fsx` entry. The compiled
  release runner and the remaining per-entry bridge sites are
  retained unchanged.

## Coverage retained

| Surface                                | Coverage retained                                                                   |
| -------------------------------------- | ----------------------------------------------------------------------------------- |
| Release policy (9 fields)              | `Policy.fs` canonical projections + semantic grammar + immutable snapshot             |
| Candidate handoff (data directory)     | `Handoff.fs` canonical positives + unsafe-path / malformed-transport negatives      |
| Authority / publication gate           | `Authority.fs` 6 unauthorized + 2 authorized child probes (Dotnet, Workflow)         |
| Transaction integrity                  | `Integrity.fs` 14 critical regressions across both producers                        |
| Compare / Observe (deflate method 8)   | `Compare.fs` 25 regressions on byte-identical, revision-only, malformed inputs      |
| Evaluate / Decision                   | `Evaluate.fs` 18 canonical decision regressions                                     |
| Publish (HTTP transport)               | `Publish.fs` 20 observer + upload regressions                                        |
| Metadata                               | `Metadata.fs` 13 regressions on render / parse / asset name / round-trip             |
| Historical baseline                     | `Historical.fs` 4 regressions on descriptor, same-archive, bumped-candidate, HEAD    |
| Revision metadata                      | `Revision.fs` 8 regressions on default + counterfactual SDK behavior                 |
| Enrollment / roster                     | `Enrollment.fs` 7 regressions on roster loader, projection, Update.ps1 -Project,      |
|                                        |   -Verify, canonical-path authorization                                              |
| Orchestration / pre-publication gate   | `Orchestration.fs` 3 regressions on prepare, phase failure, authority refusal        |
| Coherence / cross-stage guard          | `Coherence.fs` 5 regressions on Coherence.ps1 acceptance, marker drift, projection    |
|                                        |   propagation, native exit preservation                                              |
| Runner publication boundary             | `Runner.fs` 5 regression on usage, canonical, clean tree, HEAD, plus 5 modern-publish context    |
| Update.ps1 / strict-JSON projection    | `Invocation.fs` 3 regressions on Update.ps1 output + unknown field + NUL byte        |
| Sync-over-async checker (repository)    | `blocking/Tests.fs` + `blocking/Checker.fs` 16 surface fixtures                      |
| Telegram isolation                    | `Telegram.fs` 8 isolation regressions                                                  |

## Retired coverage (intentional)

- `Bootstrap.fs` compiled suite (5 regressions): the production
  Bootstrap.fsx script is gone; the equivalent Update.ps1
  projection regressions moved to `Invocation.fs`.
- `Runner.fs::missingIndependentClosure` (1 regression): the
  legacy `bootstrapReceipt` / `transportEvidence` / `trustedReceipt`
  preauth gate has no modern equivalent.
- `Enrollment.fs` Bootstrap roster boundary (3 regressions): the
  pre-FSI roster boundary lives in `ReleaseEnrollment.loadRoster`
  / `validateConfigPathAgainstRoster`; covered directly in the
  compiled enrollment API list and the persisted "Enrolled config
  identity mismatch" / `completeEnrolledRoster` regressions.
- Legacy `Integrity.fs` outer-transport / preparation-receipt /
  observed-platform-evidence / `readRawTransport` builder paths.
- Legacy `Evaluate.fs` outer-transport / preparation-receipt /
  3-entry ZIP builder.

## What was not changed

- No production source under `infrastructure/release/`,
  `dotnet/`, `workflow/`, or `infrastructure/Provenance.fs` was
  edited. `dotnet/release/Config.fsx` and
  `workflow/release/Config.fsx` were already removed by the prior
  engineer slice; no new retirement was made here.
- No `Update.ps1`, `Roster.ps1`, `Typecheck.ps1`, `Bootstrap.ps1`,
  `Suite.ps1`, `Build.psx`, `Build.fsx`, `Pins.fsx`, or workflow
  YAML was edited.
- No `Verify.ps1`, `Roster.json`, `Publisher.json`, or
  `PublisherClosure.fs` was edited.
- The four canonical `Config.json` files
  (`infrastructure/release/Sources.json` is retired and was not
  re-added; `dotnet/release/Config.json`, `workflow/release/Config.json`)
  were not edited.
- No commit, push, merge, real release, or Telegram capture was performed.
- No environment variable was read by value during this slice;
  the only env-var reads were `MCP_RELEASE_HISTORICAL_ROOT` and
  `MCP_RELEASE_DO_UPLOAD` (set/unset locally, never read from the
  host or CI).

## Verification

`mayBuild=false` and `mayTest=false` for this slice. The test
project structure, compile-item ordering, and the module/file
references above were verified by static source inspection only:

- `infrastructure/release/tests/Tests.fsproj` Compile items
  resolve in dependency order (Fixture, Process, Snapshots,
  Enrollment, Coherence, Compare, Evaluate, Historical, Invocation,
  Metadata, Publish, Integrity, Policy, Handoff, Authority,
  Revision, Runner, Telegram, Orchestration, Main).
- `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` Compile
  items resolve in dependency order (Support, DomainTests,
  InvocationTests, SecurityTests, ProcessIntegrationTests,
  McpHostTests, QuotaTests, DotnetSchemaParityTests,
  blocking/Checker, blocking/Tests, AsyncTypedFailureTests, Main).
- The 7-arg invocation in `Authority.fs` matches the actual
  production signature in `Runner.fs` line 75 by literal argument
  count and ordered type (factory → policy → snapshot → eventSha
  → metadataLocalPath → owner → repo).
- `ReleaseHandoff.readCandidate` is the only seam used by
  `Integrity.fs`, `Evaluate.fs`, `Authority.fs`, and `Handoff.fs`;
  the legacy `parsePreparationReceipt` / `makeOuterDigest` /
  `readRawTransport` / `ArtifactEvidence` / `ExpectedMetadata` /
  `ObservedPlatformMetadata` surface is no longer referenced.
- `BuildProvenance.ReleaseError.message` is the only error
  projection used in the rewritten helper paths; the legacy
  `ReleaseError.message` direct calls remain in Evaluate.fs for
  the scripted-observer cases where the value is already a
  `ReleaseError`.

## Open residual risks

- Engineered Compile/Run evidence for this slice is not yet
  produced; a clean build of `infrastructure/release/tests/Tests.fsproj`
  and a full-suite Expecto run remain pending an engineer-owned
  build plus a tester-owned rerun with the standard credential /
  workflow-output / probe environment scrub described in
  `New.md`. The pre-existing compiled assembly referenced in
  `New.md` predates these edits and is not the rebuild target.
- The `ReleaseHandoff.CandidateArchiveSnapshot` constructor is
  `internal`; the new `Integrity.fs` / `Evaluate.fs` /
  `Authority.fs` helpers construct the snapshot through the public
  `ReleaseHandoff.readCandidate` factory so this remains a normal
  in-isolation fact, not a new cross-assembly edge.
- The `Release.Tests.FixtureHelpers` module identifier was
  retained (allowed by the assignment); the filesystem name is
  `Snapshots.fs`. No compat copy, no second module, no
  `FixtureHelpers` re-export.

## Status

Compile-ready at the source level after the source flags above.
Not yet built or run as part of this slice; an engineer rebuild
plus a tester rerun with credential scrub are the next required
steps.