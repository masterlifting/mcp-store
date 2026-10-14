# Workflow behavioral verification — non-packaging twelve entrypoints

## Scope

Independent tester execution of exactly the twelve non-packaging `workflow/tests/*.fsx`
entrypoints listed in `.github/workflows/release.yml` (lines 204–218) on the merged
ed59 + dirty-current producer source. Credential-free by construction; no
publication, probe, or output names referenced. Distribution- and package-shaped
tests (`DistributionTests.fsx`, `PackagedRuntimeCoordinationTests.fsx`) are excluded
from this batch per the assignment and belong to a later packaging gate.

Canonical invocation is `dotnet fsi --exec --quiet <script>` per `workflow/tests/README.md`.
Native non-zero exits are preserved. No fixture edits; the working copy of
`workflow/tests/*.fsx` is read-only.

## Build identity used for process-boundary tests

The current Release DLL on disk is `workflow/bin/Release/net11.0/Mcp.Workflow.dll`
(assembly `Mcp.Workflow`, version `1.0.0.0`, neutral, unsigned). Filesystem mtime is
2026-10-11 16:43:28Z.

The current producer source has changed since that build:

- `workflow/Mcp.Workflow.fsproj` gained `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>` in commit `d556c20` (2026-10-11 19:50:10 +0200). The DLL on disk was emitted before that property existed.
- `workflow/domain/Workflow.fs` carries the GHI-36 supporting-artifact and Unix-mutex changes landed on the merge commit `ed59d7c` (2026-10-13 20:10:57 +0200, "GHI-24: integrate updated main baseline"). The merge stat shows 302 lines changed in `workflow/domain/Workflow.fs` and 387 lines added to `workflow/tests/TaskSupportingArtifactTests.fsx`. The DLL on disk was emitted before any of those source changes were committed.

Strings from the GHI-36 cancellation/supporting-artifact surface
(`canceled-acquisition`, `publication-after-cancel`, `pre-publication gate`,
`UnixCancellation`, `UnixAcquisitionCancel`) are absent from the on-disk DLL
binary, while the corresponding constructs are present in current `Workflow.fs`.
The DLL is therefore stale relative to the merged/current producer source.

Per the assignment's "inspect current build identity" gate, the DLL identity
does not match current producer source, so `MCP_WORKFLOW_PACKAGED_DLL` is not
wired in and the three process-boundary suites are not executed against the
stale DLL. The exact engineer build prerequisite is:

```
dotnet build workflow/Mcp.Workflow.fsproj -c Release
```

The executing SDK is already canonical: `11.0.100-rc.1.26425.128`, matching
`workflow/release/Config.fsx` `sdkVersion` and `runtimeVersion`. No further
toolchain action is needed before the engineer build. The tester does not build.

## Commands executed

All nine source-only suites ran from `D:\repos\private\mcp-store` with the
release SDK on the PATH, no `MCP_*` authority environment variables set, and
`OPENCODE_TASKSCRATCH_FSX` not set (so the default OpenCode user script is
consulted).

```
dotnet fsi --exec --quiet "workflow/tests/AuthorityMetadataTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/EphemeralLockInvariantTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/SchemaParityGuardTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/TaskContractTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/TaskPathSafetyTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/TaskProfileTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/TaskScratchCompatibilityTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/TaskSupportingArtifactTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/WorkflowTests.fsx"
```

The three process-boundary suites are intentionally not invoked in this batch
(stale DLL mismatch documented; engineer build prerequisite recorded above):

```
# SKIPPED — stale DLL identity: requires `dotnet build workflow/Mcp.Workflow.fsproj -c Release`
# workflow/tests/WorkflowMcpTests.fsx
# workflow/tests/WorkflowMcpNativeCompatTests.fsx
# workflow/tests/WorkflowMcpFindingTests.fsx
```

## Native results and assertion summaries

| # | Entrypoint | Exit | Native stdout |
|---|------------|------|--------------|
| 1 | `AuthorityMetadataTests.fsx` | 0 | `OK Workflow authority metadata contract: AuthorityDenied round-trips, non-authority errors stay envelope-clean, render handles None fields, schema description documents the contract` |
| 2 | `EphemeralLockInvariantTests.fsx` | 0 | `SKIP Unix native-acquisition cancellation (EPH-2): Windows uses the atomic WaitAny path`; `SKIP Unix native-acquisition cancellation (EPH-3): Windows uses the atomic WaitAny path`; `OK ephemeral lock invariant: create/get/apply/validate leave only runtime.json`; observed task-directory snapshots `65 samples, 3 distinct: [".runtime.json.1fe631808a4c46e2940786e69ddff0c4.tmp"; ".runtime.json.67f7ca30a5734a3c9b3bbf7ac1b46935.tmp"; "runtime.json"]` |
| 3 | `SchemaParityGuardTests.fsx` | 0 | `OK recursive Workflow schema/parser parity: required/properties invariant, command/patch/plan variant coverage, and enum round-trip` |
| 4 | `TaskContractTests.fsx` | 0 | `OK schema-v1 contract: strict version, serializer/parser parity, legacy-layout rejection, and ephemeral locking` |
| 5 | `TaskPathSafetyTests.fsx` | 0 | `OK path safety: trusted roots, existing-record boundaries, traversed/leaf reparses, and strict v1 task layouts fail closed` |
| 6 | `TaskProfileTests.fsx` | 0 | `OK profile resolver: harness registry without OpenCode files or identity, explicit Draft reclassification vs fail-closed Baselined legacy profile, no automatic migration, strict profiles, monotonic overlays/guards, drift lifecycle, and built-in software and harness policies` |
| 7 | `TaskSupportingArtifactTests.fsx` | 0 | `Windows root allowlist comparison: ordinal-ignore-case`; `Windows reparse coverage: task-root and sidecar links plus docs/scripts junctions`; `SKIP Unix special-file cases: this test host is Windows`; `OK supporting-artifact layout: allowlisted root entries work, occupied creation fails closed, and nested links remain opaque` |
| 8 | `TaskScratchCompatibilityTests.fsx` | 0 | `TaskScratch consumer: C:\Users\andre\.config\opencode\skills\task\scripts\TaskScratch.fsx (sha256=3271adb253315db790b92c3059d95e4e92ed2f49d4348429be431170ad701903)`; `OK TaskScratch compatibility: direct docs/scripts promotion, byte preservation, opaque nested documents, state/CAS invariance, and no-follow consumer traversal` |
| 9 | `WorkflowTests.fsx` | 0 | `OK task runtime recursive Work Tree, dependencies/readiness, ancestor activation, completion gating, Wait/Block/Resume, research, evidence DTO, AddEvidence, verify scope, supersession cascade, Guards (DTO/scope/checkpoints/independence/dispositions), Decisions/Open Questions (strict DTO/graph/targeting, TaskWide and WorkItem blocking, resolution), completion evidence, CanCompleteTask, CAS, persistence, Coordinator-only invocation authority (User/ProfilePolicy/confirmationRef sidecar rejection, exact target-bound Guard dispositions, Coordinator disposition creation/reuse), WorkItem ownership/inheritance, terminal handoff persistence/history, and targeted reopen (AC/WorkItem/Guard, fail-closed targets, aborted User-only)` |

## Skipped vs. limited

Three explicit platform skips appeared in the run output, all are
platform-conditional, not missing coverage, and they are the documented Windows
side of the new GHI-36 cases:

- `EPH-2` and `EPH-3` in `EphemeralLockInvariantTests.fsx` — Unix
  native-acquisition cancellation cases; the suite itself prints `Windows uses the
  atomic WaitAny path` and skips them on Windows. Counterpart Unix runs are
  owned by a separate Unix verification gate.
- `Unix special-file cases` in `TaskSupportingArtifactTests.fsx` — the suite
  itself prints `this test host is Windows` and skips on Windows. The Windows
  reparse and forwarded-shape cases run in the same suite and pass. Counterpart
  Unix runs are owned by a separate Unix verification gate.

The `TaskScratchCompatibilityTests.fsx` consumer smoke did execute (not skip).
The default OpenCode user script `TaskScratch.fsx` is present at
`%USERPROFILE%\.config\opencode\skills\task\scripts\TaskScratch.fsx`
(89 288 bytes, written 2026-10-13 18:55:34Z). The suite hashed the consumer
script and reported the digest in stdout; the consumer source was not read by
the tester. `OPENCODE_TASKSCRATCH_FSX` was not set. The suite's own
explicit-consumer SKIP branch was not taken.

Three entrypoints are held back from execution in this batch — not as a pass —
because the DLL they require is stale. They are not counted as run, not
counted as skip, and not counted as pass:

- `WorkflowMcpTests.fsx`
- `WorkflowMcpNativeCompatTests.fsx`
- `WorkflowMcpFindingTests.fsx`

`DistributionTests.fsx` and `PackagedRuntimeCoordinationTests.fsx` are
explicitly excluded from this batch by the assignment; they remain the
packaging engineer's tests against a clean-final-SHA build and later tester
smoke.

## Run/skip counts

- Run: 9 (AuthorityMetadataTests, EphemeralLockInvariantTests, SchemaParityGuardTests, TaskContractTests, TaskPathSafetyTests, TaskProfileTests, TaskScratchCompatibilityTests, TaskSupportingArtifactTests, WorkflowTests)
- Platform skip (documented Windows side): 3 (EPH-2, EPH-3, Unix special-file cases inside `TaskSupportingArtifactTests.fsx`)
- Hold for engineer build prerequisite (stale DLL): 3 (WorkflowMcpTests, WorkflowMcpNativeCompatTests, WorkflowMcpFindingTests)
- Excluded from batch by assignment: 2 (DistributionTests, PackagedRuntimeCoordinationTests)
- Out-of-scope command entrypoints / child helpers (not standalone suites per `workflow/tests/README.md`): 5 (TaskCreate.fsx, TaskGet.fsx, TaskApply.fsx, TaskValidate.fsx, MutexAbandonmentHelper.fsx)

All nine executed suites returned exit code 0 and emitted their `OK ...`
assertion summary. No native non-zero exit was observed. No fixture was
modified. No credential or session-store read occurred. No live Telegram or
GitHub call occurred. No upload, commit, push, or branch/checkout mutation
occurred. Temp files generated by the suites stayed under the per-suite
fixture roots (`%TEMP%\opencode\...`, `%TEMP%\task-runtime-profile-catalog-<guid>.json`,
in-suite temp directories).

## Limitation

This batch does not constitute overall acceptance. The three process-boundary
suites still need to run against a `Workflow` MCP DLL freshly built from the
merged/current source; the engineer prerequisite is
`dotnet build workflow/Mcp.Workflow.fsproj -c Release` on the executing SDK
`11.0.100-rc.1.26425.128`. The compiled Expecto gate `Suite161/161` and the
bootstrap gate `Bootstrap55/55` are separate dimensions in `release.yml` and
are not exercised in this batch.

## Appendix A — Process-boundary three-suite run (fresh exact prerequisite build)

### DLL identity

The on-disk `workflow/bin/Release/net11.0/Mcp.Workflow.dll` was refreshed by
the engineer prerequisite (`dotnet build workflow/Mcp.Workflow.fsproj -c Release`)
after the prior9 source-suite batch. The build run identifier recorded by the
engineer was `ANnfsl0wAHQzEaPrzwsJEh1rzxBcan7_iUbmVR3iL88`, with zero errors and
zero warnings.

| Phase | bytes | LastWriteTime (UTC) | SHA-256 |
|-------|-------|---------------------|---------|
| Before batch | 890368 | 2026-10-14 09:34:28Z | `5a946464edb5e8fb4fbc6f7c3ec186cc91fe496e096e93ef1f50450bfa8d54f5` |
| After batch  | 890368 | 2026-10-14 09:34:28Z | `5a946464edb5e8fb4fbc6f7c3ec186cc91fe496e096e93ef1f50450bfa8d54f5` |

DLL identity unchanged through this batch. No build, no source edit, no git
mutation occurred during this run.

### Isolated launch environment

Per script (separate process via inline script-block so closures do not leak):

- `MCP_WORKFLOW_PACKAGED_DLL=D:\repos\private\mcp-store\workflow\bin\Release\net11.0\Mcp.Workflow.dll`
  (the exact known path; the hard-coded `releaseEntryDll` inside the three
  scripts is the same path so this is also a re-confirmation, not a redirect).
- The following ambient env-var **names** were cleared by NAME only (no values
  read, printed, transmitted, or persisted): `EA_TELEGRAM_BOT_TOKEN`,
  `GITHUB_TOKEN`, `NUGET_API_KEY`, `YOUTRACK_API`.
- No token, registry, or credential value was accessed, logged, or otherwise
  exposed.
- `OPENCODE_TASKSCRATCH_FSX` left untouched (not relevant to these three
  scripts; `TaskScratchCompatibilityTests.fsx` is not in this batch).

### Commands executed (only the three)

```
dotnet fsi --exec --quiet "workflow/tests/WorkflowMcpTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/WorkflowMcpNativeCompatTests.fsx"
dotnet fsi --exec --quiet "workflow/tests/WorkflowMcpFindingTests.fsx"
```

Each invocation was preceded by the isolated launch environment above.

### Native results and assertion summaries

| # | Entrypoint | Exit | Native stdout (verbatim, with informational compiler hints retained) |
|---|------------|------|--------------------------------------------------------------------------|
| 1 | `WorkflowMcpTests.fsx` | 0 | `WorkflowMcpTests.fsx(291,102): info FS3886: This list expression contains a single tuple element. Did you mean to use ';' instead of ',' to separate list elements?`; `WorkflowMcpTests.fsx(578,79): info FS3886: This list expression contains a single tuple element. Did you mean to use ';' instead of ',' to separate list elements?`; `OK platform-internal task runtime MCP boundary: stdio handshake, tools/list, structured results, library/CLI parity, TaskApply CLI unknown-flag/authority fail-closed exit, workspace rejection, CAS preservation, authority/receipt/effect rejection, bounded failures, and stdout cleanliness` |
| 2 | `WorkflowMcpNativeCompatTests.fsx` | 0 | `OK MCP protocol negotiation: initialize accepted '2025-11-25' and negotiated '2024-11-05'` |
| 3 | `WorkflowMcpFindingTests.fsx` | 0 | `WorkflowMcpFindingTests.fsx(307,26): info FS3886: This list expression contains a single tuple element. Did you mean to use ';' instead of ',' to separate list elements?`; `OK task runtime MCP findings: D1 JSON-RPC 2.0/ID validation, D2 strict tagged DTO fields, D3 negative revision rejected without write, ARCH-INFRA005-001 trusted workspace containment and reparse rejection` |

The `info FS3886` lines are pre-existing informational compiler hints in the
unmodified source scripts (`WorkflowMcpTests.fsx` lines 291 and 578,
`WorkflowMcpFindingTests.fsx` line 307). They are not warnings, not errors, and
do not affect native exit. They are reported verbatim because the assignment
asks for actual assertion summaries, not sanitized.

No native non-zero exit was observed. No test fixture or filesystem state was
mutated beyond per-suite temp files under `%TEMP%\opencode\...` and
`%TEMP%\task-runtime-profile-catalog-<guid>.json`. No live network, no upload,
no commit, no push, no source edit, no git mutation, no credential or session
read occurred during this batch.

### Aggregate 12 status

Following the same accounting convention as the prior9 source-suite batch:

- Run: 3 (WorkflowMcpTests, WorkflowMcpNativeCompatTests, WorkflowMcpFindingTests)
- Platform skip: 0 (the three scripts use Windows process calls and run; no
  Unix-only branch was hit on this Windows host)
- Excluded from batch by assignment: 2 (DistributionTests,
  PackagedRuntimeCoordinationTests)
- Out-of-scope command entrypoints / child helpers (not standalone suites per
  `workflow/tests/README.md`): 5 (TaskCreate.fsx, TaskGet.fsx, TaskApply.fsx,
  TaskValidate.fsx, MutexAbandonmentHelper.fsx)

All 12 non-packaging entrypoints listed in `.github/workflows/release.yml`
(lines 204–218) are now accounted for across this batch and the prior9
source-suite batch: **9 source suites + 3 process-boundary suites = 12 run**,
plus the documented 2 excluded-by-assignment packaging entries (Distribution and
PackagedRuntimeCoordination, which are separate clean-package gates owned by
the packaging engineer / packaging tester) and the 5 out-of-scope command /
helper entries. Native exits: **12 of 12 = 0**, **0 of 12 non-zero**.

### Explicit prior platform skips (re-listed for traceability)

These three documented platform skips remained in the prior9 batch and are
not affected by this batch (the three process-boundary scripts are themselves
Windows-resolved, so they neither added nor cleared any platform skip):

- `EPH-2` and `EPH-3` in `EphemeralLockInvariantTests.fsx` — Unix
  native-acquisition cancellation cases; Windows uses the atomic WaitAny path
  and the suite self-skips on this host. Counterpart Unix runs are owned by a
  separate Unix verification gate.
- `Unix special-file cases` in `TaskSupportingArtifactTests.fsx` — the suite
  self-skips on Windows. The Windows reparse and forwarded-shape cases run in
  the same suite and pass. Counterpart Unix runs are owned by a separate Unix
  verification gate.

This appendix does not constitute overall acceptance or packaging proof.
`DistributionTests.fsx` and `PackagedRuntimeCoordinationTests.fsx` remain
separate clean-package gates and are not covered by this batch.