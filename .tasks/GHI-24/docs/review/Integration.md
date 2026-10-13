# Stage1 DevOps integration evidence — GHR-36 W2

Factual record of the DevOps-owned stage-1 integration slice for the compiled
release runner. This is not acceptance evidence and makes no PASS claim for any
criterion. Authority: `Requirements.md`, `Threads.md`, `Baseline.md`,
`Solution.md`, `../original.md`; implementation baseline
`05a69ced8319b61a6bf0e24cad6d320a0a879283`; companion records `Compilation.md`
and `Migration.md`.

## Scope delivered

The release transaction, the release regression gate, the script typecheck gate
and their workflow wiring now use the compiled `ReleaseRunner.exe` and the
compiled Expecto regression project. No producer `Release.fsx` wrapper is
referenced anywhere in `.github/workflows/release.yml`, and the workflow
contains no `dotnet fsi` invocation.

| Path | Change |
| --- | --- |
| `.github/workflows/release.yml` | `release` job transaction step now runs the compiled `infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe orchestrate <component>/release/Config.fsx` (missing-runner guard, `$ErrorActionPreference = "Stop"`, `Tee-Object` log, exit-code file, `GITHUB_OUTPUT`, native `exit $code`); `tests` job builds both Expecto projects, adds a read-only `gh release download` fixture step, and runs `Suite.ps1`; gate step names/comments updated. |
| `infrastructure/release/Suite.ps1` | Runs the complete compiled Expecto suite (native exit code plus `--list-tests` case-count assertion) and the retained producer provenance entrypoint, and fails closed when the pinned historical fixture is absent. |
| `infrastructure/release/Typecheck.ps1` | Default targets re-pointed from the two deleted wrappers to the eight retained release-path scripts; compiled modules stay excluded because their project builds type them. |
| `infrastructure/release/README.md` | Rewritten to the current compiled runner, gates, fixture requirement and exit codes; stale wrapper/`F2`/`F4`/`F9`/`Release.fsx` descriptions removed. |
| `infrastructure/release/Update.ps1` | Inspected only: `-Project`/`-Verify` still project the canonical `Config.fsx` and run the retained `Bootstrap.fsx`, so no stage-1 change was required. |

`Suite.ps1` gate order: resolve the built `Release.Tests.dll`, assert the
retained script entrypoints exist, fail closed on a missing or non-directory
historical fixture root, snapshot `GITHUB_ENV`/`GITHUB_OUTPUT`, enumerate the
compiled case inventory with `--list-tests`, run the complete suite once with
`--sequenced --no-spinner`, compare the executed count against the inventory,
run `dotnet/tests/ProvenanceTests.fsx`, then re-check the output-target
sentinel. No case is filtered, excluded, skipped or rewritten; the packaging
entrypoints (`<component>/tests/DistributionTests.fsx`) stay out of this gate
because they require a clean tree and a producer build, and
`ReleaseRunner.exe orchestrate` executes them per component in its `tests`
phase. Their presence is asserted.

The historical fixture identity (`<componentId>-v1.0.3.zip` and the pinned
SHA-256/size values) is owned by the compiled `Historical` case, which reports a
missing or mismatched archive as a failure. `Suite.ps1` therefore guards only the
fixture root, and the workflow derives the component tags from the roster
projection before fetching the already-published release assets.

## Verified locally (non-publishing, no network, no upload)

| # | Check | Command | Result |
| --- | --- | --- | --- |
| 1 | Script typecheck gate (W2 run; superseded by the roster-derived run in the W4 table) | `pwsh -NoProfile -File infrastructure/release/Typecheck.ps1 -SdkVersion 11.0.100-rc.1.26425.128` | exit 0; `targets=8 missing_targets=0 total_errors=0` |
| 2 | Gate fails closed without fixtures | `pwsh -NoProfile -File infrastructure/release/Suite.ps1` (root unset) | exit 1; `::error::MCP_RELEASE_HISTORICAL_ROOT is not set; … this gate does not exclude cases`; no Expecto process started |
| 3 | Gate fails closed on a non-directory fixture root | same, with `MCP_RELEASE_HISTORICAL_ROOT` set to a missing path | exit 1; `::error::MCP_RELEASE_HISTORICAL_ROOT does not name a directory: …`; no Expecto process started |
| 4 | PowerShell parse | `[Parser]::ParseFile` over `Suite.ps1`, `Typecheck.ps1` and all 16 extracted `run:` blocks | all parse |
| 5 | Workflow static facts | PyYAML `safe_load` + structural asserts | trigger `push` only; `group: release-main`, `queue: max`, `cancel-in-progress: false`; default `contents: read`; jobs `bootstrap`/`tests`/`release`; `release.needs=[bootstrap, tests]`; release `contents: write`, tests `contents: read`; all three `uses:` pinned to 40-hex SHAs; `stale_wrapper_mentions=[]`; `fsi_transaction_invocations=[]` |
| 6 | Transaction launcher | `release.yml` transaction block executed from the repo root with `RUNNER_TEMP` in temp | exit 1; runner emitted one structured JSON object `phase=build`, `childExitCode=1` (dirty-tree refusal), `componentId=dotnet`, `version=1.0.4`, `archiveName=v1.0.4.zip`, `assetUri` from canonical config; `Tee` log, `dotnet-exit-code.txt`, `GITHUB_OUTPUT exit_code=1` written |
| 7 | Summary step | `release.yml` result step executed on that log | `release-status-dotnet.json`: `status=failed`, `verified=false`, `projectionKind=status-evidence-only`, `structuredResult=true`, `exitCode=1`, `phase=build`, `published=null`, `diagnostics=[]`; no `published-metadata-dotnet.json` copied; no candidate pin presented as published |
| 8 | Gate inventory logic | `dotnet <Release.Tests.dll> --list-tests` replayed through the `Suite.ps1` parsing (`--list-tests` only; no case executed) | `inventory=12`, `historical=1` (`compiled release regressions.Historical legacy regression assertions`); the gate therefore requires exactly 12 executed cases |

Checks 6 and 7 used the dirty working tree deliberately: the runner refuses to
package from a dirty tree before any build, pins, or transport call, so nothing
was built, published, or fetched. No `GITHUB_TOKEN`, `MCP_RELEASE_DO_UPLOAD`, or
owner/repository environment variable was set locally.

## Affected producer gate — checker status after the stage-2 rename

The producer Expecto gate that `release.yml` runs
(`dotnet run --project dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj`)
was blocked during W2 by two `Async.RunSynchronously` findings for
`infrastructure/release/Runner.fs`. The stage-2 tester-owned rename moved the
checker to `dotnet/tests/Mcp.Dotnet.Tests/blocking/Checker.fs` (module identity
`Mcp.Dotnet.Tests.SyncOverAsyncChecker` retained) and replaced the stale map with
the two exact current bridge sites:

- approved by the map: `runOrchestrateMode argv |> Async.RunSynchronously` and
  `runTransactionMode argv |> Async.RunSynchronously`;
- observed in `Runner.fs` at lines 288 and 294, byte-identical to the approved
  lines;
- the exemption's documentation token is satisfied by the condensed one-line
  comment at `Runner.fs` line 287, whose wording `synchronous wait is at the host
  boundary` `Checker.fs` now accepts alongside the older literals.

A bounded static cross-check of every `.fs`/`.fsx` bridge site in the tree
against the map found no unmapped site and no stale mapped entry. This is a text
cross-check, **not an execution of the compiled checker**: `#load`ing the renamed
file under FSI fails with `FS0597` (compiled-only parenthesis resolution), and
the locally built `Mcp.Dotnet.Tests.dll` predates the rename. An engineer rebuild
of `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` plus the producer
suite run stays mandatory before any later criterion is claimed PASS.

## Unverified / limits

- Hosted GitHub Actions behavior remains unverified: no workflow run was
  triggered, and the `gh release download` fixture step was authored but never
  executed (no remote acquisition in development). Local validation used the
  approved fixture root
  `C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap`.
- The uploaded-fixture step depends on the published `v1.0.3` assets existing on
  the component release tags; if they are absent the step throws and the gate
  blocks rather than degrading.
- The complete `Suite.ps1` happy path (12 compiled cases plus the provenance
  script) has not been executed by this slice; only its fail-closed preflight was
  executed. The compiled suite itself last ran 12/12 with the approved fixture
  per `Migration.md` before the fixture step existed.
- The real upload path (`MCP_RELEASE_DO_UPLOAD=true`, `uploads.github.com`) and
  any actual publication evidence remain unexecuted.
- `ReleaseRunner.exe` is a Windows `net11.0` binary, so the release workflow
  stays on `windows-latest`; a Linux launcher would need the SDK-style
  `dotnet ReleaseRunner.dll` path instead of the `.exe`.

## Tester handoff — exact commands and expected gates

```powershell
# build (engineer-owned projects; already built at Tests.fsproj run __Y-TLG5jERYTitIB2dRDkdGt1u3htHkpnUHTtggdd0 and Release.fsproj run R4LgZe-3HFAdyt2vAyJS-oKF2tDdXz9IFVTy6-ptRvs, both 0 errors / 0 warnings)
dotnet build infrastructure/release/tests/Tests.fsproj -c Release

# full compiled suite with the approved pinned fixtures
$env:MCP_RELEASE_HISTORICAL_ROOT='C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'
dotnet run --no-build --no-restore -c Release --project infrastructure/release/tests/Tests.fsproj

# the integrated gate (complete compiled suite + provenance script)
pwsh -NoProfile -File infrastructure/release/Suite.ps1
```

Expected gates: the compiled Expecto run exits 0 with **12 cases, 12 passed,
0 ignored, 0 failed, 0 errored** (12 suite cases, not nested assertions; the
last recorded helper counters are Bootstrap 9, Compare unprinted, Evaluate 70,
Historical 10, Invocation 10, Metadata 52, Publish 50, Revision 22 plus 2
producer PE checks, Runner 18, Telegram 19, plus the two shared-orchestration
cases), using only local fixtures and the recording HTTP handler. `Suite.ps1`
must report `release regression gate passed: 12 compiled case(s) (including 1
historical) plus 1 producer script(s)` and must not write or redirect
`GITHUB_ENV`/`GITHUB_OUTPUT`.

A separate producer-suite run (`dotnet run --project
dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj -c Release --no-build`)
after the engineer rebuild is the mandatory confirmation of the checker status
above; that run is owned by the tester.

## W3 PS/YAML comment and naming audit

Audited `release.yml`, `Suite.ps1`, `Typecheck.ps1` and `Update.ps1` against
`capabilities/software/knowledge/comments.md` and `architecture.md`. Four
comment-only corrections in `release.yml`: the tests-job header no longer claims
the job is credential-free (its fixture step now uses the workflow's default
token); the fixture-step and gate-step comments were each condensed from two
lines to one; and the transaction comment no longer restates the stdout JSON
contract, which `infrastructure/release/README.md` owns. `Suite.ps1`,
`Typecheck.ps1` and `Update.ps1` needed no change: each header is the `# role:`
tooling directive plus at most three one-line points, and no review-history
labels (`F2`/`F7`/`F9`), phase names, banners, narration wording, TODOs,
commented-out code or contract-in-comment statements remain. The `# role:`
directives and the pre-SDK `Bootstrap.fsx` invocation are preserved; no F# file
was touched.

Every path added by this slice is one-word (`Suite.ps1`, `Typecheck.ps1`,
`Integration.md`). Project/assembly identities and other tool-mandated external
names are the bounded exceptions already recorded in `Naming.md`. No current
PS/YAML file references the former multi-word checker paths, so no rename-driven
path update was required there; the stale reference in this document's earlier
W2 text was corrected to `blocking/Checker.fs`.

Affected-file recheck after the comment edits: PyYAML `safe_load` plus the
structural asserts pass, all 16 extracted `run:` blocks parse with
`[Parser]::ParseFile`, and `stale_wrapper_mentions` /
`fsi_transaction_invocations` remain empty.

## W4 roster wiring (stage 3 PS/YAML slice)

Scope: replace the competing participant lists in the DevOps surfaces with the
`Participants.json` roster (or a deterministic cross-check) and scope the shared
toolchain restriction to bootstrap.

Changed paths: `infrastructure/release/Roster.ps1` (shared pre-SDK roster reader
mirroring the compiled acceptance rules) and
`infrastructure/release/Coherence.ps1` (cross-stage guard), both new;
`infrastructure/release/Update.ps1` (roster-driven projection and verification,
`components` output, no `$producers` table, no `ValidateSet`),
`infrastructure/release/Typecheck.ps1` (roster-derived targets),
`infrastructure/release/Suite.ps1` (fixture guard no longer restates fixture
names or packaging paths), `.github/workflows/release.yml` (roster-driven matrix,
roster-derived fixture step, roster-wide bootstrap verification, coherence guard
step), `infrastructure/release/README.md`.

| # | Check | Command | Result |
| --- | --- | --- | --- |
| 9 | Roster projection | `pwsh -NoProfile -File infrastructure/release/Update.ps1 -Project` (temp `GITHUB_OUTPUT`) | exit 0; `sdk=11.0.100-rc.1.26425.128`, `runtime=11.0.0-rc.1.26425.128`, `platform=win-x64`, `components=["dotnet","workflow"]`; summary lines `RELEASE_{DOTNET,WORKFLOW}_{VERSION,ENTRY}` from the roster |
| 10 | Roster-wide verification | `Update.ps1 -Verify` | exit 0; `component=all roster participants`, `RELEASE_SDK/RUNTIME/PLATFORM/ARCHITECTURE` and both producers' `VERSION/ARCHIVE/ASSET_URI/ENTRY` forwarded to `$GITHUB_ENV` |
| 11 | Unlisted participant fails before bootstrap | `Update.ps1 -Verify -Component bogus` | exit 1; `component 'bogus' is not enrolled in …\Participants.json; refusing to verify an unlisted participant`; no bootstrap/FSI child started, no `$GITHUB_ENV` write |
| 12 | Controlled roster drift fixtures (`-Root` on a temp copy; no source change) | `Update.ps1 -Project -Root <fixture>` for duplicate `componentId`, config `componentId` mismatch, roster `configPath` drift, empty roster | baseline exit 0; each drift case exit 1 at the roster guard (duplicate, mismatch, missing config for the drifted path, empty roster) |
| 13 | Roster-derived typecheck targets | `pwsh -NoProfile -File infrastructure/release/Typecheck.ps1 -SdkVersion 11.0.100-rc.1.26425.128` | exit 0; `targets=8 missing_targets=0` derived from the roster (2 fixed + 2×3 per participant), `total_errors=0`; the earlier 18-error `Bootstrap.fsx` failure is repaired (see below) |
| 14 | Workflow static facts after the roster wiring | PyYAML probe + `[Parser]::ParseFile` | 17 run blocks parse; `matrix_component=${{ fromJSON(needs.bootstrap.outputs.components) }}`; `bootstrap.outputs.components` present; fixture step env carries `RELEASE_COMPONENTS`; `hardcoded_component_lists=[]`; `stale_wrapper_mentions=[]`; `fsi_transaction_invocations=[]` |
| 15 | Suite fixture guard | `Suite.ps1` with `MCP_RELEASE_HISTORICAL_ROOT` unset and with a missing path | exit 1 for both, with the explicit `::error::` messages; no Expecto process started |
| 16 | Roster-derived packaging entrypoints | `Suite.ps1` fail-closed run after the derivation change | reached the fixture guard instead of throwing `release-path script entrypoint is missing`, so both `<componentRoot>/tests/DistributionTests.fsx` paths resolved from the roster |
| 17 | Component-scoped verification | `Update.ps1 -Verify -Component dotnet` and `-Component workflow` | exit 0 for both; identical canonical summary, roster membership accepted |
| 18 | Cross-stage coherence guard, baseline | `pwsh -NoProfile -File infrastructure/release/Coherence.ps1 -OutputFile <GITHUB_OUTPUT>` after `Update.ps1 -Project` | exit 0; `cross-stage coherence ok: 15 assertions; roster=[dotnet, workflow]` (14 without `-OutputFile`) |
| 19 | Pre-SDK roster exactness | `Update.ps1 -Project -Root <fixture>` with an extra participant (`telegram`), a missing participant, a forbidden `version` field, and an unsupported key | exit 1 for every case, with the compiled-guard wording (`unsupported componentId`/`mandatory supported set`, `componentId set does not match`, `forbidden field`) |

Consistency chain now: `Participants.json` → `Update.ps1 -Project` (`components`)
→ `Coherence.ps1` (matrix expression, bootstrap output, fixture wiring, SDK
projection, CLI entry registration, projection output) → workflow `release`
matrix → `Update.ps1 -Verify -Component <matrix id>` (roster membership) →
`ReleaseRunner orchestrate <configPath>` (`ReleaseEnrollment` componentId/
configPath guard) → transaction mode (`validateComponentIdInRoster`).
`Bootstrap.fsx` iterates the same roster. No PS/YAML file restates a participant
list; the only remaining historical literal outside the compiled case is the
fixture version in the workflow provisioning step, which the compiled `Historical`
case verifies by hash.

### Cross-stage coherence guard

`Coherence.ps1 -Root <root> [-Workflow <path>] [-OutputFile <projection-output>]`
is the deterministic CI guard. It reads the roster through `Roster.ps1`, then
asserts, with anchored marker counts that fail closed on absence *or*
multiplicity: the `components` bootstrap output mapping, the `fromJSON` release
matrix, the `tests` job dependency on bootstrap, the fixture step's
`RELEASE_COMPONENTS`, roster-wide bootstrap verification, matrix-scoped
publication verification, the `orchestrate` CLI argument derived from the matrix,
no hardcoded participant list or CLI path, every `setup-dotnet` use fed by the
canonical projection, and — for each roster entry — that
`<componentId>/release/Config.fsx` equals the registered `configPath`. With
`-OutputFile` it also compares the emitted `components=` projection against the
roster. `release.yml` runs it in the `bootstrap` job immediately after
`Update.ps1 -Project`, so any mismatch fails the job before the SDK install and
therefore before publication.

Deliberate-mismatch validation against the real guard (each a mutated copy of the
workspace workflow/roster under `-Root`; no repository source changed):

| Mismatch | Guard result |
| --- | --- |
| workflow file absent | exit 1 `release workflow not found` |
| matrix replaced by a hardcoded `- dotnet`/`- workflow` list | exit 1 marker count |
| bootstrap `components` output renamed | exit 1 marker count |
| `orchestrate` CLI argument hardcoded to one producer | exit 1 marker count |
| `-Verify -Component dotnet` literal in the publication job | exit 1 marker count |
| fixture step loses `RELEASE_COMPONENTS` | exit 1 marker count |
| `dotnet-version` hardcoded instead of projected | exit 1 marker count |
| emitted `components=["dotnet"]` projection | exit 1 projection-output mismatch |
| roster extra participant / missing participant / forbidden `version` / unsupported key / `configPath` drift | exit 1 roster or CLI-registration check |

### Former blocking production defect (repaired)

`infrastructure/release/Bootstrap.fsx` previously did not parse
(`Bootstrap.fsx(290,9): error FS0010`, a duplicated `match failures with` block
orphaned after the closing brace; FCS reported 18 errors), which blocked
`Update.ps1 -Verify` and the bootstrap job. The production owner has since
repaired it: the roster-derived FCS gate now reports `targets=8 total_errors=0`,
and `Update.ps1 -Verify` exits 0 for all participants. The gate that surfaced the
defect is the same one that now proves it repaired.

### Public surface handed to the tester

- `Update.ps1 -Project` outputs `sdk`, `runtime`, `platform`, `components`
  (JSON array of roster `componentId`s) and the `RELEASE_*` summary.
- `Update.ps1 -Verify [-Component <id>]`: `-Component` must be a roster
  participant; omitted means all participants.
- `Typecheck.ps1` derives targets from the roster (`<configDir>/Build.fsx`,
  `<configDir>/Pins.fsx`, `<componentRoot>/tests/DistributionTests.fsx`) plus
  `infrastructure/release/Bootstrap.fsx` and `dotnet/tests/ProvenanceTests.fsx`.
- `Roster.ps1` exports `Get-ReleaseRoster -Root <root>`; `Coherence.ps1` accepts
  `-Root`, `-Workflow` and `-OutputFile` for fixture-driven checks.
- Compiled guard API unchanged from `Enrollment.md`
  (`loadRoster`/`validateRoster`/`validateProjectionAgainstRoster`/
  `validateComponentIdInRoster`).

### Remaining gaps

- The tests job still names the two producer project files and the two compiled
  test projects as build targets; the roster intentionally carries only
  `componentId`/`configPath`, so deriving those would need new roster fields and
  belongs with #37 multi-language onboarding.
- Hosted CI, the `gh release download` fixture step, and the full compiled suite
  remain unexecuted. Compiled roster inventory tests and production `Coherence.ps1`
  mismatch regressions are authored in `Enrollment.fs` and `Coherence.fs`, but
  their build and execution remain pending.

## Status

No acceptance criterion is claimed PASS here. AC-06 (enrollment consistency) now
has roster-driven PS/YAML wiring and local drift evidence; the compiled
cross-inventory and actual-guard regressions are authored but await build and
execution, as does hosted workflow observation;
AC-07 (toolchain boundary) has the bootstrap-only scoping documented but not
independently reviewed; AC-11 remains open pending the full compiled release
suite run, the producer suite state above, and hosted-CI observation; AC-04/AC-05
remain subject to the independent compiled-test and integrity stages. The former
`Bootstrap.fsx` syntax defect is repaired and the roster-derived gates now pass.
No commit, push, merge, thread resolution, runtime mutation, remote acquisition,
upload, secret access, or production/test F# edit was made.

## W5 new-thread remediation slice (`release.yml` / `Coherence.ps1`)

Factual record of the bounded DevOps slice for the post-baseline
`release.yml`-anchored review threads at local head
`523f625014dbf81fb788fe5c11877fc7a9626382`. This is not acceptance evidence and
claims no PASS.

Threads addressed in this slice: `r4249874725` (P1, step-output isolation),
`r4249894920` (P1, missing Workflow test gate), and the `release.yml` boundary of
`r4249874746` (P2, publication credential isolation). The two
`infrastructure/release/Orchestration.fs` threads (`r4249874735` authorization
order, `r4249904018` comment policy) are F#-engineer-owned and untouched here.

Changed paths: `.github/workflows/release.yml` and
`infrastructure/release/Coherence.ps1` only. No F# source, test, other script,
roster, task state or secret was edited.

### Step-output isolation (`r4249874725`)

The bootstrap coherence step no longer passes its own `$env:GITHUB_OUTPUT` to
the guard (GitHub Actions allocates one output-command file per step, so that
file never contained the project step's `components=` line).

- `Coherence.ps1` now takes `-Components <json>` (the actual
  `${{ steps.project.outputs.components }}` value) instead of
  `-OutputFile <path>`, so the guard has one projection source and never reads a
  step-local output-command file.
- Fail-closed behaviour is retained and extended: an absent or empty
  `-Components` throws, a value that disagrees with the roster throws, and two
  new anchored markers fail the guard before the SDK install if the
  `PROJECTED_COMPONENTS` input or the `Coherence.ps1 -Components` consumption is
  renamed or reverted (a zero-count marker rejects a return to `-OutputFile`).
- The stale `-OutputFile` descriptions earlier in this document (W4 table row
  18, the guard signature at the "Cross-stage coherence guard" subsection, and
  the "Public surface handed to the tester" bullet) describe the superseded
  surface; this section supersedes them.

### Workflow test gate (`r4249894920`)

The read-only `tests` job now runs, after the producer Expecto gate and before
the historical-fixture step, the ten non-packaging Workflow entrypoints as an
explicit deterministic list: `AuthorityMetadataTests.fsx`,
`EphemeralLockInvariantTests.fsx`, `SchemaParityGuardTests.fsx`,
`TaskContractTests.fsx`, `TaskPathSafetyTests.fsx`, `TaskProfileTests.fsx`,
`WorkflowMcpFindingTests.fsx`, `WorkflowMcpNativeCompatTests.fsx`,
`WorkflowMcpTests.fsx`, `WorkflowTests.fsx`. Selection is never globbed, a
missing entrypoint throws, each child's native exit code is read explicitly, and
the step exits with the first failing child's code. The `release` job already
depends on `tests`, so a failing Workflow suite now blocks every upload.

The two package-dependent suites (`<component>/tests/DistributionTests.fsx` and
`workflow/tests/PackagedRuntimeCoordinationTests.fsx`) need a clean tree and the
built distribution, so they remain a pre-upload gate inside
`ReleaseRunner.exe orchestrate` (F#-engineer-owned companion, below).

### Publication credential boundary (`r4249874746`, coordination only)

`release.yml` keeps `GITHUB_TOKEN`/`MCP_RELEASE_DO_UPLOAD` on the transaction
step because the orchestration subprocess needs them for the publish transport;
the isolation must therefore happen where the child environments are built
(`ReleaseOrchestration.runProducerScript` / `ReleaseCapture.runProcessCapture`),
which is F#-engineer-owned and was not edited here.

### Validation executed locally (non-publishing, no network, no upload, no build)

| # | Check | Command | Result |
| --- | --- | --- | --- |
| 20 | Coherence guard baseline | `pwsh -NoProfile -File infrastructure/release/Coherence.ps1 -Components '["dotnet","workflow"]'` | exit 0; `cross-stage coherence ok: 18 assertions; roster=[dotnet, workflow]` |
| 21 | Guard fails closed on a roster-mismatched value | same with `-Components '["dotnet","telegram"]'` | exit 1; `[projection output]: projected participants [dotnet, telegram] do not equal the roster [dotnet, workflow]` |
| 22 | Guard fails closed on an absent projection | `Coherence.ps1` with no `-Components` | exit 1; `[projected components]: -Components must carry the bootstrap projection value` |
| 23 | Guard fails closed on a reverted wiring (temp root copy of the workflow) | workflow text edited back to `Coherence.ps1 -OutputFile $env:GITHUB_OUTPUT` | exit 1; `[coherence guard consumes the projected components]: expected 1 match(es) … found 0` |
| 24 | Guard fails closed on a renamed projection input (temp root copy) | workflow text edited to `PROJECTED_OTHER` | exit 1; `[coherence guard projection input]: expected 1 match(es) …` |
| 25 | Workflow YAML plus structural facts | PyYAML `safe_load` + structural asserts | parse OK; jobs `bootstrap`/`tests`/`release`; `release.needs=[bootstrap, tests]`; coherence step carries `PROJECTED_COMPONENTS: ${{ steps.project.outputs.components }}` and `-Components $env:PROJECTED_COMPONENTS` |
| 26 | PowerShell parse | `[Parser]::ParseInput` over `Coherence.ps1` and all nine `run: \|` blocks (GitHub expressions substituted) | `run_blocks=9 bad=0` |
| 27 | Workflow test-loop control flow (stubbed `dotnet`, no suite executed) | extracted step body with a shim returning exit 7 on the third entrypoint | step exits 7 with `::error::Workflow test entrypoints failed: workflow/tests/SchemaParityGuardTests.fsx (exit 7)`; all-zero shim exits 0 |

Checks 20–24 read repository and fixture text only. Checks 25–27 touched no
network, asset, secret, build output or publication path. The ten Workflow
entrypoints were not executed by this slice; their credential-free exit-0 run is
recorded in `Verification.md` and re-execution is tester-owned.

### Required companion changes (not in this slice)

1. **Tester-owned compiled tests** (`infrastructure/release/tests/`):
   `Coherence.fs::runCoherence` still passes `-OutputFile <path>` and must pass
   `-Components <json>` (including the `components=["dotnet","telegram"]`
   negative case), and `Telegram.fs` line 106–108 still asserts the workflow text
   contains `infrastructure/release/Coherence.ps1 -OutputFile` and must assert
   the `-Components` consumption instead.
2. **Tester-owned step-isolation regression:** run `Update.ps1 -Project` with one
   `GITHUB_OUTPUT` path and `Coherence.ps1 -Components <the emitted value>` with a
   distinct `GITHUB_OUTPUT` path, mirroring the per-step command files Actions
   allocates, and assert pass; assert fail-closed with no `-Components`.
3. **Tester-owned publication-block regression:** show that a failing Workflow
   entrypoint blocks publication — e.g. assert the `tests` job names all ten
   entrypoints and that `release.needs` includes `tests`.
4. **Engineer-owned orchestration:** clear `GITHUB_TOKEN`,
   `MCP_RELEASE_DO_UPLOAD`, `MCP_RELEASE_GITHUB_OWNER` and
   `MCP_RELEASE_GITHUB_REPOSITORY` from the `build`/`pins`/`tests` child
   environments and keep them only for the final transaction subprocess; run
   `workflow/tests/PackagedRuntimeCoordinationTests.fsx` after the component
   `DistributionTests.fsx` and before the transaction so both package-dependent
   suites gate the upload against the clean built distribution, preserving the
   native `childExitCode` and the structured `phase=tests` failure.
5. **`Suite.ps1` / `infrastructure/release/README.md`:** assert the second
   package-dependent entrypoint exists, and update the guard description (the
   emitted `components=` projection paragraph and the `-OutputFile` surface) to
   the `-Components` value input.

### Limits

Local-only source and configuration validation; no hosted Actions run, no
upload, no network, no credentials, no build and no suite execution were
performed by this slice. The Workflow test-gate and credential-isolation changes
require the compiled suite rebuild plus the companion changes above before any
criterion can be re-evaluated.

### Fail-fast amendment (coordinator decision)

The Workflow test step now stops at the first failing entrypoint and exits with
that child's native code, so the native first-failed exit is preserved and no
later entrypoint runs. The explicit ten-entrypoint selection, the
missing-entrypoint guard, the step name and `release.needs = [bootstrap, tests]`
are unchanged, and no tester assertion is relaxed.

Superseded entries above, retained unmodified as history: the "Workflow test
gate" paragraph phrase *"each child's native exit code is read explicitly, and
the step exits with the first failing child's code"* and validation row 27
(which recorded the earlier collect-and-continue body, where the step continued
past the third entrypoint before exiting). The current behaviour is fail-fast.

| # | Check | Result |
| --- | --- | --- |
| 28 | `[Parser]::ParseInput` over all `run: \|` blocks (GitHub expressions substituted) | `run_blocks=9 parse_failures=0` |
| 29 | YAML structural check of the reworked step | exactly 10 entrypoints; no `failed.Add`/`failCode`/`failed.Count` remnant; missing-entrypoint guard present; `exit $code` present and `exit $failCode` absent; `release_needs=[bootstrap, tests]` |
| 30 | Extracted step body run with a stubbed `dotnet` (no suite executed) | first entrypoint exit 7 → step exit 7, 1 stub call; third entrypoint exit 7 → step exit 7, 3 stub calls (`::error::Workflow test entrypoint failed: workflow/tests/SchemaParityGuardTests.fsx (exit 7)`); all-zero stub → step exit 0, 10 stub calls |

The stub-driven native-return assertions against the real compiled suite remain
tester-owned; these checks execute no Workflow suite.
