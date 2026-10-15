# Stage1 compilation evidence — GHR-36 W2

Authoritative inputs: `Solution.md` (selected boundaries), `Requirements.md`
(AC-04/AC-05), `Baseline.md` (existing surfaces to inspect). Baseline head:
`05a69ced8319b61a6bf0e24cad6d320a0a879283`. No acceptance PASS claims; this is
factual implementation/build evidence only.

## Module order (matches frozen design)

The `infrastructure/release/Release.fsproj` Compile list, in source order:

1. `../../infrastructure/Provenance.fs`
2. `Archive.fs`
3. `Compare.fs`
4. `Metadata.fs`
5. `Publish.fs`
6. `Evaluate.fs`
7. `Capture.fs`
8. `Transaction.fs` *(new, extracted from Runner.fs)*
9. `Orchestration.fs` *(new, shared per-component pipeline)*
10. `Runner.fs` *(thin CLI shell — orchestrate | transaction)*

`Bootstrap.fsx` remains excluded with the existing rationale: compiling its
top-level `Async.RunSynchronously` into the runner's `<Module>` cctor hangs
Main on net11.0 RC. The same `Provenance.fs` source is loaded by the producer
packaging scripts (`Build.fsx`, `Pins.fsx`, `Bootstrap.fsx`, `Distribution.fsx`,
`Testing.fsx`) and compiled by `infrastructure/release/tests/Tests.fsproj`
through its reference to `Release.fsproj`.

## Reusable .fsx → .fs conversion (AC-04)

- `infrastructure/Provenance.fsx` → `infrastructure/Provenance.fs`
- `infrastructure/release/Archive.fsx` → `infrastructure/release/Archive.fs`
- `infrastructure/release/Compare.fsx` → `infrastructure/release/Compare.fs`
- `infrastructure/release/Metadata.fsx` → `infrastructure/release/Metadata.fs`
- `infrastructure/release/Publish.fsx` → `infrastructure/release/Publish.fs`
- `infrastructure/release/Evaluate.fsx` → `infrastructure/release/Evaluate.fs`
- `infrastructure/release/Capture.fsx` → `infrastructure/release/Capture.fs`

Module names, public types, and signatures are preserved. Each file's
`#load "../Provenance.fsx"` was removed; the new `.fs` files rely on
in-assembly binding (the compiled `Provenance.fs` is part of the same
`ReleaseRunner` assembly).

## New modules (AC-05)

`infrastructure/release/Transaction.fs` — `ReleaseTransaction`. Extracts the
publish / metadata-recovery / failure branches plus the
`buildRemoteObserver` adapter and `parseContractJson` DTO parser that used to
live in `Runner.fs`. Pure of CLI/JSON emission; the imperative boundary is
contained here.

`infrastructure/release/Orchestration.fs` — `ReleaseOrchestration`. Single
shared per-component pipeline. Reads the canonical `Config.fsx` through an
isolated child `dotnet fsi` process (no policy mirror), sequences
`build` → `pins` → `tests` → transaction, and owns structured phase
failures (one JSON object on stdout before every exit).

## Deletions (no shim)

- `dotnet/release/Release.fsx`
- `workflow/release/Release.fsx`

The duplicated per-component wrapper orchestration is gone; the compiled
`ReleaseRunner orchestrate <config-path>` is the normal per-component path.

## CLI surface (Runner.fs)

Two modes dispatched by the first argument:

- `ReleaseRunner orchestrate <config-path>` — full per-component pipeline.
- `ReleaseRunner <manifest> <archive> <contract-json>` — standalone
  transaction (preserves the pre-stage1 caller contract exactly).

Empty argv and insufficient-argv cases print usage and exit `2`. On
structured failure, one JSON object is emitted on stdout before exit (status,
reason, phase, childExitCode, childStderr, componentId, version,
archiveName, assetUri when the contract has been parsed).

## Compiled build evidence (Dotnet MCP, Release configuration)

| Project | Status | runId | exitCode | errors | warnings |
| --- | --- | --- | --- | --- | --- |
| `infrastructure/release/Release.fsproj` | succeeded | `R4LgZe-3HFAdyt2vAyJS-oKF2tDdXz9IFVTy6-ptRvs` | 0 | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | succeeded | `mU_eeoaejXYvJBgJQs7IpzQk1pq5pUA-ojFhv3a_ODc` | 0 | 0 | 0 |
| `dotnet/Mcp.Dotnet.fsproj` | succeeded | `X8zEM61FURrNz2uC0PN-Vp1pgzwxtXxKKiAsldW3tF8` | 0 | 0 | 0 |
| `workflow/Mcp.Workflow.fsproj` | succeeded | `OGZxHdiU7mlzc5zS8davAdtcNimBBspmbCb1qN-6his` | 0 | 0 | 0 |

All four projects compile with `TreatWarningsAsErrors=true`. The
`Release.fsproj` and `Tests.fsproj` runIds above are from the post-test-failure
fix; the prior `Release.fsproj` runId `WXt11NBGOUDxP-EtMsWSx-IR8cSFq6k4_9EOMuQNfCE`
is superseded. The `Mcp.Dotnet.fsproj` and `Mcp.Workflow.fsproj` runIds remain
valid because their source is unchanged.

## Behavioural smoke checks (ReleaseRunner.exe)

- `ReleaseRunner` (no args) → prints
  `usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>` and
  `usage: ReleaseRunner orchestrate <config-path>`, exit `2`. ✓
- `ReleaseRunner orchestrate` (no config) → prints
  `usage: ReleaseRunner orchestrate <config-path>`, exit `2`. ✓
- `ReleaseRunner foo bar` (transaction mode, <3 args) → prints
  `usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>`,
  exit `2`. ✓
- `ReleaseRunner orchestrate dotnet/release/Config.fsx` → isolated-FSI
  projection succeeds; build phase detects dirty tree (expected while W2
  edits are uncommitted) and emits the structured failure object with
  `phase=build`, `childExitCode=1`, `childStderr`, `componentId=dotnet`,
  `version=1.0.4`, `archiveName=v1.0.4.zip`, `assetUri=…`, exit `1`. ✓

The structured-failure JSON path is exercised end-to-end without upload.

## CI consumer changes (not made — DevOps owns next)

- `.github/workflows/release.yml` currently invokes
  `dotnet fsi dotnet/release/Release.fsx` and
  `dotnet fsi workflow/release/Release.fsx`. Both wrappers are deleted.
  The replacement is
  `ReleaseRunner.exe orchestrate dotnet/release/Config.fsx` and
  `ReleaseRunner.exe orchestrate workflow/release/Config.fsx` from
  `infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe`.
- `infrastructure/release/Typecheck.ps1` defaults target
  `dotnet/release/Release.fsx` and `workflow/release/Release.fsx`. Both
  files are gone; the FCS typecheck list is empty unless it is re-pointed
  at the compiled runner's `#load` closure (e.g. `Runner.fs`,
  `Orchestration.fs`, `Transaction.fs`).

Both are blocking gaps for the post-stage1 release transaction until
DevOps reroutes them. Until then the published entry point is the binary.

## Stage1 acceptance status (no PASS claim)

AC-04 (compiled F# architecture): implementation present and builds clean;
**independent compiled test migration/execution is required next** before
acceptance. AC-05 (DRY one shared orchestration): implementation present
and builds clean; the dual wrappers are gone, the shared orchestrator owns
build/pins/tests/transaction. The three integrity defects (AC-01/AC-02/AC-03)
are stage 4 and were not touched in this stage; integrity branches keep
their pre-stage1 status.

## W2 recovery — post-abort resumption

The prior engineer invocation was interrupted before returning a result; the
working tree was left in the documented partial state. The source under
`Release.fsproj`, `Mcp.Dotnet.fsproj`, `Mcp.Workflow.fsproj` is unchanged
from the prior evidence, so the prior build verdict is preserved. A clean
rebuild through the dedicated Dotnet MCP confirms the runIds above (0
errors, 0 warnings with `TreatWarningsAsErrors=true`). No new production
edits were made in this resumption; reconciliation was limited to the
evidence file update.

## Stage1 tester migration

The compiled release regression project and suite mapping are recorded in
`Migration.md`. It references the existing production project; release-domain
test code no longer recompiles production modules through script `#load` paths.
The historical suite is included and reports missing local archives as failures,
not skips. Producer packaging and provenance scripts remain separate because
they exercise their child-script contracts.

The producer `Release.fsx` wrappers remain absent. `Suite.ps1` still discovers
script entrypoints and must be rerouted to the compiled Expecto runner by DevOps;
the workflow and Typecheck defaults also still target the deleted wrappers.

## DevOps-required integration (deferred to DevOps)

The shared compiled runner is the canonical Stage-1 release entrypoint, but
two CI surfaces still point at the deleted wrappers:

- `.github/workflows/release.yml` invokes
  `dotnet fsi dotnet/release/Release.fsx` and
  `dotnet fsi workflow/release/Release.fsx`. Both wrappers are deleted; the
  replacement is
  `ReleaseRunner.exe orchestrate dotnet/release/Config.fsx` and
  `ReleaseRunner.exe orchestrate workflow/release/Config.fsx` from
  `infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe`.
- `infrastructure/release/Typecheck.ps1` defaults target the same two
  deleted wrappers. The FCS typecheck list is empty unless it is re-pointed
  at the compiled runner's `#load` closure (`Runner.fs`, `Orchestration.fs`,
  `Transaction.fs`).

Until DevOps reroutes them, the published entry point is the binary; the
`.github/workflows/release.yml` workflow cannot drive Stage 1 from a
re-run. The README inside `infrastructure/release/` still describes the
removed wrapper shape (e.g. `Release.fsx` contract, exit-code table rows,
F2/F4/F9, `Suite.ps1` role-header gate, `Typecheck.ps1` summary); the
binary path mentioned in the README is `ReleaseRunner.exe`, which is what
the new orchestration actually emits. README alignment is part of the
DevOps integration slice, not the Stage-1 compiled-architecture deliverable.

## Risk and stop point

- Stage 1 compiled-architecture deliverable is complete and the build is
  reproducible through the dedicated Dotnet MCP.
- Stage 1 acceptance is **not** claimed here: AC-04 requires compiled test
  migration/execution (tester next), AC-05 requires compiled regression
  proofs (tester next), and AC-01/AC-02/AC-03 (the three integrity fixes)
  are Stage 4 and intentionally untouched.
- Stage 2 (naming/comments), Stage 3 (enrollment/bootstrap boundary), and
  Stage 4 (three integrity fixes with direct negative tests) are deferred
  per the W2 scope.
- W2 is **not** marked done; the worktree remains in a dirty state on
  purpose until the tester + DevOps slices reconcile the path references
  above.

## Test-failure root cause and minimal fix (post-test-execution)

The tester's executed compiled suite reported 9 passed / 3 failed / 0 ignored.
Two of the three failures are regressed original observables in the new
`ReleaseRunner` CLI; the third is in `Orchestration.fs` and is left
unmodified per the assignment ("don't change it without bounded source
evidence; tester diagnostic rerun after rebuild next").

### Failure 1 — `Runner.fs(287..291)` empty-argv stderr observable

- **Original observable** (`git show HEAD:infrastructure/release/Runner.fs`
  line 462): `eprintfn "usage: ReleaseRunner <manifest-path> <archive-path> <contract-json>"`
  — single line, no `usage:` banner, exits `2`.
- **New observable** (pre-fix, `infrastructure/release/Runner.fs`
  lines 287–291): `eprintfn "usage:"` followed by two indented
  `eprintfn "  ReleaseRunner ..."` lines — the new orchestrator-mode
  entrypoint changed the layout.
- **Test expectation** (`infrastructure/release/tests/Runner.fs`
  line 111): `errText.StartsWith("usage: ReleaseRunner", …)` — fails
  because stderr now begins with `usage:`.
- **Minimal fix** (applied): each usage line now starts with
  `usage: ReleaseRunner …` (no standalone `usage:` banner, no leading
  spaces). One entry-point change, two `eprintfn` lines.

### Failure 2 — `Runner.fs(121..124)` dirty-tree JSON observable

- **Original observable** (HEAD `Runner.fs` line 517):
  `emitResolution (typedFailedWithConfig error)` after the contract
  is parsed — the rendered `Resolution` carries the contract context
  (`componentId`, `version`, `archiveName`, `assetUri`, `reason`)
  via the pre-populated `preMetadataRender`.
- **New observable** (pre-fix, `Runner.fs` lines 121–124):
  `emitPreContractFailure (ReleaseError.message error)` — only
  `{status, reason}`, contract context dropped.
- **Test expectation** (`tests/Runner.fs` lines 159–164):
  asserts `componentId=dotnet`, `version=1.0.4`,
  `archiveName=v1.0.4.zip`, `assetUri` contains
  `masterlifting/mcp-store`, `reason` mentions
  `dirty|refusing|clean`; also asserts `publishedArchiveSha256` /
  `publishedManifestSha256` / `publishedRevision` /
  `publishedMetadataSha256` / `bumpedVersion` are null/missing.
- **Minimal fix** (applied): switch the dirty-tree branch (and the
  parallel `committedHead` branch that follows it) from
  `emitPreContractFailure` to `emitResolution (typedFailedWithConfig error)`,
  matching the original observable. The `preMetadataRender` already
  carries the contract context from the parsed DTO.
- **Why not also fix the other post-contract branches?** The
  remaining post-contract branches (empty canonicalAssetUri,
  `File.Exists manifestPath`, `File.Exists archivePath`,
  `assertManifestRevision`, hash-failure helpers) still use
  `emitPreContractFailure`. They are not asserted by the current
  test path; touching them would be a speculative change. They are
  recorded here for a future bounded pass.

### Failure 3 — `Orchestration.fs` unknown sequence (build;pins missing tests)

- **Tester observation**: child-step marker records `build;pins;`
  instead of the expected `build;pins;tests;`.
- **Source evidence check**: `infrastructure/release/Orchestration.fs`
  lines 260 / 274 / 288 already invoke
  `runProducerScript root (Path.Combine(componentId, "release",
  "Build.fsx")) configPath`,
  `runProducerScript root (Path.Combine(componentId, "release",
  "Pins.fsx")) configPath`, and
  `runProducerScript root (Path.Combine(componentId, "tests",
  "DistributionTests.fsx")) configPath` — i.e. the `tests` step is
  in the production pipeline. No source change has been made.
- **Hypothesis (not patched)**: the failure may be a stale-binary
  effect (the test harness may have invoked a cached `ReleaseRunner.exe`
  built before the test-suite binary was linked; the orchestration
  child path is an `Async.RunSynchronously` bridge at the host
  boundary and the cached binary does not call the new
  `Orchestration.fs`). This is **evidence-only**, not a patch.
- **Action**: re-run the compiled suite after the
  `Release.fsproj` / `Tests.fsproj` rebuild below and capture the
  updated marker bytes; if `build;pins;tests;` is still missing,
  escalate as a real `Orchestration.fs` regression with the full
  marker capture.

### Historical test fixture

- The compiled `Historical.fs` case requires
  `MCP_RELEASE_HISTORICAL_ROOT` pointing at a directory containing
  `dotnet-v1.0.3.zip` and `workflow-v1.0.3.zip` (pinned SHA-256 +
  size). The coordinator located the approved temp directory
  `ghi-24-bootstrap/{dotnet,workflow}-v1.0.3.zip`; no actual
  network is required. The tester re-runs the compiled suite with
  that env var set to exercise the historical case.

### Build runIds after the fix

| Project | runId | Status |
| --- | --- | --- |
| `infrastructure/release/Release.fsproj` | `R4LgZe-3HFAdyt2vAyJS-oKF2tDdXz9IFVTy6-ptRvs` | succeeded (0/0) |
| `infrastructure/release/tests/Tests.fsproj` | `mU_eeoaejXYvJBgJQs7IpzQk1pq5pUA-ojFhv3a_ODc` | succeeded (0/0) |

No acceptance claim is made here. The next step is the tester's
re-run of the compiled suite with the rebuild applied and the
historical fixture env var set; W2 is still **not** marked done.
