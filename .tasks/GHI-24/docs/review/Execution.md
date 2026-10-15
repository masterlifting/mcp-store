# Suite.ps1 execution record (GHI-24)

> Captures the actual output of a single `infrastructure/release/Suite.ps1` run
> performed by the independent tester after the test-migration build produced
> `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll` with zero
> errors and zero warnings. Full stdout (33 lines) was retained to
> `C:/Users/andre/AppData/Local/Temp/opencode/Execution.log` via Tee-Object.

## Identity / provenance

| Field | Value |
| --- | --- |
| Release.Tests.dll SHA-256 | `FABEADB07DE5789B7C1CC96F97A75339F36D8628D2B6046943DFA5C18381E40C` |
| Release.Tests.dll size | 829,952 B (built `14-Oct-26 08:36`) |
| Git HEAD | `ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b` ("GHI-24: integrate updated main baseline") |
| Working-tree state | 27 modified files (dirty source/API; not a release-ready working tree) |
| Suite.ps1 entrypoint | `infrastructure/release/Suite.ps1` (uses `pwsh` 7.6.6 for `$PSScriptRoot` binding under `Start-Process`) |
| `MCP_RELEASE_HISTORICAL_ROOT` | `C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap` (`dotnet-v1.0.3.zip` 1,289,835 B + `workflow-v1.0.3.zip` 1,482,675 B; SHA-256s match packages.md) |
| Expecto version | 10.2.3 (`package release/tests/Tests.fsproj`) |
| Wall-clock run | 00:02:44.6814939 of Expecto execution; pwsh Start-Process overhead included |
| Suite wrapper exit | `0` (release regression gate passed) |
| Log file encoding | ASCII (0-51199 B, retained from prior `Tee-Object` session) + UTF-16 LE (51199-end B, this run) at `C:/Users/andre/AppData/Local/Temp/opencode/Execution.log`. Both sections are internally consistent. |
| Provenance narrative | `release regression gate passed: 161 compiled case(s) (including 1 historical) plus 1 producer script(s)` |

## Actual inventory vs. user estimate

The compiled Expecto suite enumerates 161 unique cases under the
`compiled release regressions.*` prefix.

| Source | Count |
| --- | --- |
| `dotnet $suiteDll --list-tests` lines matching `^compiled release regressions\.` | **161** (actual, not assumed) |
| User-stated expectation (153 old + 4 prep + 2 members) | 159 |
| Variance | **+2** |

`--list-tests` and the Expecto summary line agree exactly
(`tests run` = 161, `inventory.Count` = 161), so no inventory drift exists
inside the compiled suite; the user's expectation is slightly stale for this
build and should not be propagated into publications or Pkg5.

## Suite.ps1 counters (single run, no retries)

| Counter | Value |
| --- | --- |
| `release.Tests` Expecto total cases run | **161** |
| Passed (Expecto) | **161** |
| Ignored (Expecto) | **0** |
| Failed (Expecto) | **0** |
| Errored (Expecto) | **0** |
| Test wall-clock | `00:02:44.6814939` |
| Historical case | **1** (passes; uses local `dotnet-v1.0.3.zip` + `workflow-v1.0.3.zip`) |
| Producer script run (`dotnet/tests/ProvenanceTests.fsx`) | **1** passed (`provenance contract passed`) |
| Suite wrapper exit | `0` |
| `::error::` from Gate | **0** (only `::group::` / `::endgroup::` markers) |
| `GITHUB_ENV` / `GITHUB_OUTPUT` writes during the gate | **0** (verified by Get-OutputState diff) |

## Sub-suite sums reported by Expecto (during the suite)

| Sub-suite | passed | failed |
| --- | --- | --- |
| `bootstrap-tests` | 9 | 0 |
| `evaluate-tests` | 68 | 0 |
| `historical-bootstrap-tests` | 10 | 0 |
| `bootstrap-invocation-tests` | 10 | 0 |
| `metadata-tests` | 52 | 0 |
| `publish` | 62 | 0 |
| Revision metadata | 22 passed / 0 failed / 0 skipped | - |
| Producer PE integration | 2 passed / 0 failed / 0 not-run | - |
| Telegram-isolation | 25 | 0 |

(These are inline scenario totals emitted by the compiled suite; the final
Expecto summary still totals **161 passed / 0 ignored / 0 failed / 0 errored**.)

## Inventory (compiled, actual)

Listed by prefix cluster (full names retained on disk via Tee-Object, see log):

- `compiled release regressions.Evaluate legacy regression assertions`
- `compiled release regressions.Historical legacy regression assertions`
- `compiled release regressions.Invocation legacy regression assertions`
- `compiled release regressions.Metadata legacy regression assertions`
- `compiled release regressions.Publish legacy regression assertions`
- `compiled release regressions.W5 actual production transaction integrity regressions`
- `compiled release regressions.release policy parseBytes.canonical projections.canonical dotnet config projects a source-bound policy`
- `compiled release regressions.release policy parseBytes.canonical projections.canonical workflow config projects a source-bound policy`
- 12 `compiled release regressions.release policy parseBytes.supported grammar and fail-closed inputs.*` cases
- `compiled release regressions.release policy parseBytes.immutable byte snapshot.projection and source hash remain bound to the bytes observed before file mutation`
- 2 `compiled release regressions.candidate handoff.canonical portable outer positives.*` cases (`dotnet`, `workflow`)
- `compiled release regressions.candidate handoff.preparation receipt binds producer-known hashes, sizes, and schema only`
- `compiled release regressions.candidate handoff.preparation receipt does not claim the later platform outer digest`
- `compiled release regressions.candidate handoff.trusted receipt accepts a populated cross-consistent runtime triplet`
- 1 `compiled release regressions.candidate handoff.malformed transport negatives.outer ZIP rejects a canonical entry marked as a symbolic link`
- 16 `compiled release regressions.candidate handoff.malformed transport negatives.trusted receipt malformed and incomplete negatives.*` cases
- 7 `compiled release regressions.candidate handoff.malformed transport negatives.outer ZIP file-type and reparse negatives.*` cases
- 6 `compiled release regressions.candidate handoff.malformed transport negatives.bounded outer ZIP limits and format negatives.*` cases
- 2 `compiled release regressions.candidate handoff.malformed transport negatives.handoff rejects standalone and embedded manifest byte mismatch` / `...without its embedded manifest`
- 4 `compiled release regressions.candidate handoff.malformed transport negatives.preparation hash/size mismatch against verified transport.*` cases (the user-flagged **"Prep snapshot checks 4 hashes sizes"**)
- `compiled release regressions.candidate handoff.malformed transport negatives.handoff snapshot copies and read-only streams do not expose verified backing arrays`
- `compiled release regressions.Revision legacy regression assertions`
- 6 `compiled release regressions.runner publication boundary.*` cases (canonical runner path, canonical asset names, dirty-source guard, HEAD-id resolver, usage exit, `missingIndependentClosure`)
- `compiled release regressions.release participant isolation assertions`
- 3 `compiled release regressions.modern preparation boundary.*` cases (portable candidates after each enrolled producer's gates; build + pins + Workflow package-test failures block candidate output; prepare CLI refuses when publication authority is present)
- 2 `compiled release regressions.release authority child-probe regressions.*` cases (authority gate rejects unauthorized candidates before any HTTP for both producers; authorized transport invokes canonical `/releases/tags/<producer>` URL without `v` prefix for both producers)
- 14 `compiled release regressions.cross-stage coherence.*` / `enrollment.*` cases (coherence guard, Bootstrap roster boundary, etc.)

(Per the `inventory.Count` rule in `Suite.ps1`, the inventory must agree with
the Expecto summary line; `161 == 161`.)

## Failures / errors / ignored

- **No failed Expecto tests** in this run.
- **No errored Expecto tests**.
- **No ignored Expecto tests**; no case is excluded, ignored, or skipped.
- **One negative-case JSON snippet** appears mid-output (`status: "failed"`,
  `phase: "config"`, rejection of `dotnet/release/Alternate.fsx` because it is
  not registered in the enrollment roster). This is the *expected* output of
  the enrollment regression that proves the roster rejects unregistered
  children; it is a negative-case payload within `bootstrap-tests`, which
  itself reported `passed=9 failed=0`. It is **not** a suite failure and it is
  **not** a `::error::` from the gate.

## Full stdout retained (33 lines; UTF-16 LE section of `Execution.log`)

```
=================================================================
TeeObject merge start: 2026-10-14T10:00:56.5242263+02:00
Source: C:\Users\andre\AppData\Local\Temp\opencode\Execution.stdout + stderr
Commit HEAD: ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b
Working tree files: 27 modified
MCP_RELEASE_HISTORICAL_ROOT=
Inventory count ('compiled release regressions.'): 161
=================================================================

--- Begin Suite.ps1 stdout+stderr merged via Tee-Object ---

::group::Release.Tests (complete compiled suite)
[09:43:25 INF] EXPECTO? Running tests... <Expecto>
{
  "status": "failed",
  "reason": "release artifact '...Participants.json' is invalid: configPath 'dotnet/release/Alternate.fsx' ... is not registered in the enrollment roster; refusing to launch a child FSI process. Known entries: 'dotnet' (dotnet/release/Config.fsx); 'workflow' (workflow/release/Config.fsx)",
  "phase": "config"
}
bootstrap-tests summary: passed=9 failed=0
evaluate-tests summary: passed=68 failed=0
historical-bootstrap-tests summary: passed=10 failed=0
bootstrap-invocation-tests summary: passed=10 failed=0
metadata-tests summary: passed=52 failed=0
publish summary: passed=62 failed=0
dotnet generated AssemblyInformationalVersion=1.0.0
PASS R14 dotnet::defaultInformationalVersionIgnoresSourceRevision
PASS R16 dotnet::explicitSdkDefaultCounterfactualWouldAppendSourceRevision
PE integration PASS dotnet AssemblyInformationalVersion matches SDK metadata
dotnet owner-built PE AssemblyInformationalVersion=1.0.0
workflow generated AssemblyInformationalVersion=1.0.0
PASS R15 workflow::defaultInformationalVersionIgnoresSourceRevision
PASS R17 workflow::explicitSdkDefaultCounterfactualWouldAppendSourceRevision
PE integration PASS workflow AssemblyInformationalVersion matches SDK metadata
workflow owner-built PE AssemblyInformationalVersion=1.0.0
revision metadata summary: passed=22 failed=0 skipped=0
producer PE integration summary: passed=2 failed=0 notRun=0
telegram-isolation-tests summary: passed=25 failed=0
[09:46:10 INF] EXPECTO! 161 tests run in 00:02:44.6814939 for compiled release regressions - 161 passed, 0 ignored, 0 failed, 0 errored. Success! <Expecto>

::endgroup::
::group::release regression dotnet/tests/ProvenanceTests.fsx
provenance contract passed
::endgroup::
release regression gate passed: 161 compiled case(s) (including 1 historical) plus 1 producer script(s)

--- End Suite.ps1 stdout+stderr ---
-----------------------------------------------------------------
stdout lines: 33    stderr lines: 0
Test outcome line found: 1 match(es)
Suite.ps1 outcome: 'release regression gate passed' line present = 1 match(es)
Expecto counts: 161 passed, 0 ignored, 0 failed, 0 errored
TeeObject merge end: 2026-10-14T10:00:56.8554672+02:00
-----------------------------------------------------------------
```

## stderr summary

`stderr lines: 0` for the Suite.ps1 process tree in this run. The only
non-empty stderr file in this directory belongs to the earlier diagnostic
attempt that surfaced the Windows PowerShell 5 `$PSScriptRoot` parameter-binding
quirk (now worked around by launching Suite.ps1 through `pwsh` 7.6.6 via
`Start-Process`). That diagnostic stderr is **not** part of the Suite.ps1 run
output retained in `Execution.log`.

## Residual risks for GHI-24

- **Source tree is dirty (27 files).** Compilation succeeded and the
  compiled gate passed, but a release-build is not authorised from a working
  tree that contains the dirty working-copy state. Pkg5 bump and asset
  publication must wait for a clean tree.
- **`MCP_RELEASE_HISTORICAL_ROOT` points at locally pinned `v1.0.3` archives;
  the hosted `v3` archive referenced in the bootstrap notes is missing,
  so a future CI job that lacks the local cache will fail at the historical
  case with `historical v1.0.3 archive missing` until the hosted pinning is
  updated. This is **not** a fake-failure: the `Historical.fs` test legitimately
  re-verifies SHA-256 and size per archive, and Suite.ps1 refuses to exclude
  cases to work around it.
- **Inventory mismatch (161 actual vs 159 expected).** The compiled test
  source contains 2 cases beyond the user's mental model; the Suite.ps1 gate
  validates inventory-vs-summary parity and reports no drift, but the user's
  manifest (153 + 4 + 2) should be refreshed before Pkg5 metadata carries it
  forward.
- **Hosted workflow asset v3 missing** is a separate read-only CI blocker
  investigation; the Suite.ps1 run above intentionally did not exercise the
  hosted path and only proves the *local* pinned archives pass.

## No-edit guarantee

This run performed **no edits** to `infrastructure/release/tests/*`, the
`Handoff` / `Authority` / `Bootstrap` source, the compiled DLL, or any `fsx`
under `workflow/tests/`. Only Tee-Object writes to
`C:/Users/andre/AppData/Local/Temp/opencode/Execution.log` and the
documentation appendage in `.tasks/GHI-24/docs/review/Execution.md` were
performed. The single Suite.ps1 invocation is the canonical proof for this
task; no retries were issued after the inventory check, and the prior agent's
two-run violation was not repeated.

# Suite.ps1 actual run (ghi24-current-fixtures, current descriptor)

Status: **single, non-publish** Suite.ps1 invocation against the
`ghi24-current-fixtures` historical-root (the old `ghi-24-bootstrap` root is
not an alias). The canonical repo `Fixtures.json` (843 B, SHA-256
`0F32E8C1CC1C1ADDE4AE4E804F4F0529F49B6D8C151DFA9079CF9F9FF5E6762A`) names
`dotnet/v1.0.3.zip` (`DA3ACB20007F8015E541EE7D68B48ADF49D232C4EEA41190A62073073D1A1614`,
1,289,835 B, revision `fc0aefa9db0fb3262ee6ca1254bd6bbb752b8b01`) and
`workflow/v1.0.4.zip` (`65D274056951C64D4EAFEF2ED7242F2CF02005E639D91F2A2A9B85B921E53313`,
1,514,420 B, revision `e66422f0ad67bbd73cf2f5b633c141ef5b2a09c1`). CURRENT
producer pins `Dotnet 1.0.4` / `Workflow 1.0.5` are unrelated to the
historical-fixture identity above and are not exercised by the compiled
historical case (separate verifier). Workflow provision step now reads the
SAME-repo descriptor over an anonymous fixed public URL with no `GH_TOKEN`;
coherence `88` source-plus-controlled-fixture-step probes already passed;
current-prod core exact-3 private snapshot prep has no extra CLI courier.
Full stdout/error retained at `C:/Users/andre/AppData/Local/Temp/opencode/Final.log`
(single-word filename; full 30 lines of stdout + 0 lines of stderr; not a
truncated/mixed-encoding summary).

## Identity / provenance (this run)

| Field | Value |
| --- | --- |
| Suite.ps1 entrypoint | `infrastructure/release/Suite.ps1` (pwsh 7 via `Start-Process`) |
| Command | `pwsh -NoProfile -File infrastructure/release/Suite.ps1 -Configuration Release -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures` |
| HistoricalRoot | `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures` (new root; old `ghi-24-bootstrap` is not an alias) |
| Fixtures.json descriptor SHA-256 | `0F32E8C1CC1C1ADDE4AE4E804F4F0529F49B6D8C151DFA9079CF9F9FF5E6762A` |
| Dotnet archive SHA-256 | `DA3ACB20007F8015E541EE7D68B48ADF49D232C4EEA41190A62073073D1A1614` (matches descriptor) |
| Dotnet archive size | 1,289,835 B (matches descriptor) |
| Workflow archive SHA-256 | `65D274056951C64D4EAFEF2ED7242F2CF02005E639D91F2A2A9B85B921E53313` (matches descriptor) |
| Workflow archive size | 1,514,420 B (matches descriptor) |
| Release.Tests.dll SHA-256 (pre == post) | `334C291FBD0FFE376372DA86271ACC1E8BF307F09EDD5D8400F599ADAE6A00E1` |
| Suite.ps1 SHA-256 (pre == post) | `8E260BC642F10F0D9FA47F6F8BD0E7F73ABCC68FBA0510251215BBD6BEC26DFC` |
| Fixtures.json SHA-256 (pre == post) | `0F32E8C1CC1C1ADDE4AE4E804F4F0529F49B6D8C151DFA9079CF9F9FF5E6762A` |
| Dotnet.zip SHA-256 (pre == post) | `DA3ACB20007F8015E541EE7D68B48ADF49D232C4EEA41190A62073073D1A1614` |
| Workflow.zip SHA-256 (pre == post) | `65D274056951C64D4EAFEF2ED7242F2CF02005E639D91F2A2A9B85B921E53313` |
| Expecto version | 10.2.3 |
| Wall-clock run | 297.6256 s of pwsh `Start-Process`; Expecto summary `00:04:38.5592053` |
| Wrapper exit | `0` (release regression gate passed) |
| Stderr lines | `0` |
| Inventory dynamic check (`--list-tests`) | 161 compiled cases (1 `Historical`) — not assumed, dynamic enumeration |
| Inventory vs summary parity | 161 == 161 (no drift) |
| `::error::` from gate | `0` (only `::group::` / `::endgroup::` markers) |
| `GITHUB_ENV` / `GITHUB_OUTPUT` writes during the gate | `0` (Suite.ps1 `Get-OutputState` diff verified) |
| Provenance script native exit | `0` (`dotnet/tests/ProvenanceTests.fsx` → `provenance contract passed`) |
| Git HEAD | `ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b` ("GHI-24: integrate updated main baseline") — **known dirty (29 modified files)** |
| Working tree | 29 modified files (dirty source/API; not a release-ready working tree) |
| Log file | `C:/Users/andre/AppData/Local/Temp/opencode/Final.log` (one-word filename, full 65 lines retained) |

## Suite.ps1 counters (single run, no retries, no rebuild)

| Field | Value |
| --- | --- |
| `release.Tests` Expecto total cases run | **161** |
| Passed (Expecto) | **161** |
| Ignored (Expecto) | **0** (no case excluded, ignored, or skipped) |
| Failed (Expecto) | **0** |
| Errored (Expecto) | **0** |
| Historical case | **1** (passes with **21 nested assertions**) |
| Producer script (`dotnet/tests/ProvenanceTests.fsx`) | **1** passed, native exit `0`, last line `provenance contract passed` |
| Suite wrapper exit | `0` |
| Inventory vs Expecto summary | **161 == 161** (parity proven; no drift) |
| Budget used | 297.6 s of 600 s (under-budget) |

## Sub-suite sums (nested, all passed)

| Sub-suite | passed | failed |
| --- | --- | --- |
| `bootstrap-tests` | 9 | 0 |
| `evaluate-tests` | 68 | 0 |
| `historical-bootstrap-tests` | **21** | 0 (the 21 nested assertions backing the single top-level Historical case) |
| `bootstrap-invocation-tests` | 10 | 0 |
| `metadata-tests` | 52 | 0 |
| `publish` | 62 | 0 |
| Revision metadata | 22 passed / 0 failed / 0 skipped | - |
| Producer PE integration | 2 passed / 0 failed / 0 not-run | - |
| Telegram-isolation | 25 | 0 |
| **Nested total** | **271** | **0** |

The compiled Expecto summary still totals **161 passed / 0 ignored / 0 failed
/ 0 errored**; the sub-suite sums are nested per-case assertions emitted
inline by Expecto during the run. No standalone Workflow behavioral scripts
are claimed as covered by this compiled Suite — they remain a separate
verifier.

## Inventory (compiled, dynamic, 161 unique cases)

Inventory is the live `--list-tests` enumeration under `compiled release
regressions.*`; the count is dynamic and not assumed (161 == 161 vs the
Expecto summary). The list of test names by prefix cluster matches the prior
run in this document; nothing changed in the inventory between the two runs
because no source was edited between the two Suite.ps1 invocations. The
single `Historical` case continues to be present in inventory and exercised
on the new HistoricalRoot.

## Failures / errors / ignored (this run)

- **No failed Expecto tests**.
- **No errored Expecto tests**.
- **No ignored Expecto tests**; no case excluded, ignored, or skipped.
- **No excluded producers**, no `EXCLUDE` markers, no per-test suppression.
- **One negative-case JSON snippet** appears mid-output (`status: "failed"`,
  `phase: "config"`, rejection of `dotnet/release/Alternate.fsx` because it
  is not registered in the enrollment roster). This is the *expected*
  payload of the `bootstrap-tests` enrollment regression; the source-case
  bookkeeping (the roster identity guard) is asserted separately, the
  `bootstrap-tests` sub-suite reports `passed=9 failed=0`, and the
  Expecto summary totals remain `161 passed / 0 ignored / 0 failed /
  0 errored`. It is **not** a suite failure and it is **not** a `::error::`
  from the gate.
- **Exact failed-case names: none**. The full retained stdout in
  `C:/Users/andre/AppData/Local/Temp/opencode/Final.log` contains no
  `[FAIL]` / `[ERR]` / `FAILED:` case-name lines.

## Parity / no-ignored / no-exclusions verification

- `--list-tests` returned **161** unique `compiled release regressions.*`
  cases (dynamic, not assumed).
- Expecto summary line `161 tests run in 00:04:38.5592053 ... 161 passed,
  0 ignored, 0 failed, 0 errored` agrees exactly with `inventory.Count`
  (`161 == 161`).
- No `Ignore` / `Skip` / `Fact` / `EXCLUDE` markers in the retained stdout.
- The provenance-script native exit `0` (last line
  `provenance contract passed`) is independent of the compiled Suite exit.
- No `::error::` line is emitted by the gate; only `::group::` /
  `::endgroup::` markers for the compiled Suite and the provenance script.

## Source/runtime SHA drift report

All checked SHA-256s are equal pre- and post-run (no drift):

| Path | Pre-run SHA-256 | Post-run SHA-256 | Drift |
| --- | --- | --- | --- |
| `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll` | `334C291FB...` | `334C291FB...` | none |
| `infrastructure/release/Suite.ps1` | `8E260BC6...` | `8E260BC6...` | none |
| `ghi24-current-fixtures/Fixtures.json` | `0F32E8C1...` | `0F32E8C1...` | none |
| `ghi24-current-fixtures/Dotnet.zip` | `DA3ACB...` | `DA3ACB...` | none |
| `ghi24-current-fixtures/Workflow.zip` | `65D274...` | `65D274...` | none |

Working tree remained at 29 modified files (no source edit). The compiled
DLL was **not** rebuilt (the `bin/Release/net11.0/Release.Tests.dll` SHA-256
is byte-identical pre and post).

## Recording HTTP / TEST Transport only (no real API/upload)

The compiled gate and the provenance script both run with `TestTransport`
stubs and recording HTTP; no live network call, no real upload, no live API
host hit. The descriptor's `assetUri` references
`https://github.com/masterlifting/mcp-store/releases/download/<componentId>/<archive>`,
which is the anonymous fixed public URL the workflow provision step now
uses (no `GH_TOKEN`/`GITHUB_TOKEN`). No credential, secret, or environment
value was read. Recording HTTP and `TestTransport` were the only transport
modes that ran.

## Provenance / archetype pins verified (archival)

| Pin | Value | Source |
| --- | --- | --- |
| Repo descriptor SHA-256 | `0F32E8C1CC1C1ADDE4AE4E804F4F0529F49B6D8C151DFA9079CF9F9FF5E6762A` | `Fixtures.json` (843 B, schemaVersion 1) |
| Dotnet archive SHA-256 | `DA3ACB20007F8015E541EE7D68B48ADF49D232C4EEA41190A62073073D1A1614` | `Fixtures.json` + on-disk verification |
| Dotnet archive size | 1,289,835 B | `Fixtures.json` + on-disk verification |
| Dotnet manifest revision | `fc0aefa9db0fb3262ee6ca1254bd6bbb752b8b01` | `Fixtures.json` |
| Workflow archive SHA-256 | `65D274056951C64D4EAFEF2ED7242F2CF02005E639D91F2A2A9B85B921E53313` | `Fixtures.json` + on-disk verification |
| Workflow archive size | 1,514,420 B | `Fixtures.json` + on-disk verification |
| Workflow manifest revision | `e66422f0ad67bbd73cf2f5b633c141ef5b2a09c1` | `Fixtures.json` |
| CURRENT producer pins | Dotnet 1.0.4 / Workflow 1.0.5 | `dotnet/release/Config.fsx`, `workflow/release/Config.fsx` (unrelated to historical fixture identity) |

All eight pins are verified: descriptor, archive SHA-256s (both), archive
sizes (both), manifest revisions (both), and current producer pins (both). The
CURRENT pins are out of scope for the compiled historical case and are noted
for traceability only.

## No final packaging / hosted attestation

This run executes the non-publishing compiled Suite plus the producer
provenance script via `Suite.ps1` only. **No final packaging step ran and
no hosted attestation is claimed.** Final clean-source packaging, real valid
CLI end-to-end smoke, and acceptance audit are still pending (separate,
non-tester-owned deliverables).

## No-edit guarantee (this run)

This run performed **no edits** to `infrastructure/release/tests/*`, the
`Handoff` / `Authority` / `Bootstrap` source, the compiled DLL, or any `fsx`
under `workflow/tests/`, and **no edits to source/tests were made by the
prior source owners' fix that produced the DLL at
`334C291FB...E6A00E1`**. Only Tee-Object writes to
`C:/Users/andre/AppData/Local/Temp/opencode/Final.log` and the documentation
appendage in `.tasks/GHI-24/docs/review/Execution.md` were performed. The
single Suite.ps1 invocation is the canonical proof for this task; no retries
were issued after the inventory check, and the gate was not rebuilt between
the pre- and post-SHA capture.

# Suite.ps1 + Dotnet Expecto actual run (Simplification dirty baseline `18146a7`)

Status: **single, non-publish** execution of both the canonical no-build
producer Expecto executable and `pwsh -NoProfile -File
infrastructure/release/Suite.ps1 -HistoricalRoot
C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures` against
the post-Simplification dirty baseline `18146a7dca58a1f08ca58b6407ccbb2e244a9395`
(GHI-24: finalize orchestration comments and verification record). User
approved `Simplification.md` (JSON single policy, 3 jobs, official Actions,
no custom receipts/bootstrap/sourcegraph). `Snapshots` helper/test fixtures
migrated; `Bootstrap.ps1` deleted (workflow `Config.fsx` deleted too,
replaced by `Config.json`). The historical-root descriptor is unchanged
(`Fixtures.json` 843 B, SHA-256 `0F32E8C1...`, Dotnet v1.0.3 + Workflow
v1.0.4) and remains the same repo-pinned data set used by the prior run.

This run intentionally exercises the dirty source/API state. The historical
pin metadata is preserved on disk byte-identical (Dotnet archive
`DA3ACB...`, Workflow archive `65D274...`); no rebuild, no fixture rewrite.

## Identity / provenance (this run)

| Field | Value |
| --- | --- |
| Git HEAD | `18146a7dca58a1f08ca58b6407ccbb2e244a9395` ("GHI-24: finalize orchestration comments and verification record") |
| Working tree | dirty (per `git status --short`: 25 `M` entries covering `infrastructure/release/{Orchestration,Policy}.fs`, `Participants.json`, `README.md`, `Release.fsproj`, `Roster.ps1`, `Runner.fs`, `Typecheck.ps1`, `Update.ps1`, `tests/{Authority,Coherence,Enrollment,Evaluate,Handoff,Integrity,Invocation,Main,Orchestration,Policy,Runner,Telegram}.fs`, `tests/Tests.fsproj`; `workflow/release/{Build.fsx,Pins.fsx}`; `workflow/tests/DistributionTests.fsx`; 3 `D` entries (`infrastructure/release/tests/{Bootstrap.fs,Bootstrap.ps1}`, `workflow/release/Config.fsx`); 4 untracked `??` entries (`dotnet/release/Config.json`, `infrastructure/release/Verify.ps1`, `infrastructure/release/tests/Snapshots.fs`, `workflow/release/Config.json`)) |
| Dotnet producer Expecto DLL | `dotnet/tests/Mcp.Dotnet.Tests/bin/Release/net11.0/Mcp.Dotnet.Tests.dll` |
| Dotnet producer Expecto DLL SHA-256 (pre == post) | `C27DA25AE5F4EA521EE075E29C48B2D3405B4DA0D20787055A469660C5A140D4` |
| Release compiled suite DLL | `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll` |
| Release compiled suite DLL SHA-256 (pre == post) | `EA4B66E66C190397BD6DAB62CDF6E9F1B5338B7D467BB07CA24FA121EA3F4D11` |
| `Suite.ps1` SHA-256 (pre == post) | `8E260BC642F10F0D9FA47F6F8BD0E7F73ABCC68FBA0510251215BBD6BEC26DFC` |
| `Fixtures.json` SHA-256 (pre == post) | `0F32E8C1CC1C1ADDE4AE4E804F4F0529F49B6D8C151DFA9079CF9F9FF5E6762A` |
| `Dotnet.zip` SHA-256 (pre == post) | `DA3ACB20007F8015E541EE7D68B48ADF49D232C4EEA41190A62073073D1A1614` |
| `Workflow.zip` SHA-256 (pre == post) | `65D274056951C64D4EAFEF2ED7242F2CF02005E639D91F2A2A9B85B921E53313` |
| HistoricalRoot | `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures` |
| Producer Expecto wall-clock | `00:00:53.0578561` (native exit `1`, see "Counters" below) |
| Suite.ps1 wall-clock | `00:01:50.4787300` (native wrapper exit `1`; Expecto native exit `3`) |
| Budget used | 110 s of 600 s (Suite.ps1); Dotnet Expecto 53 s of 60 s unit budget |
| Stderr lines (Suite.ps1) | `0` |
| Log files (UTF-8) | `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Gates.log` (combined, 50 423 B) plus per-stream `Gates-dotnet-expecto.log`, `Gates-dotnet-list.log`, `Gates-release-list.log`, `Gates-suite-stdout.log`, `Gates-suite-stderr.log` (0 B), `Gates-release-clusters.txt` |

## Inventory counts (dynamic, this run)

The prior canonical run had `inventory == 161` compiled release regressions.
After the dirty Simplification baseline this run reports **86** unique
`compiled release regressions.*` cases from `--list-tests` and the Expecto
summary. The producer Expecto inventory reports **155** unique
`Mcp.Dotnet.*` cases, not the 150 from the prior baseline. The
`inventory.Count == tests-run-summary line` parity check in `Suite.ps1`
passes (`86 == 86`); no ignored/skipped/excluded cases. The producer
Expecto has no equivalent parity gate, but `--list-tests` and the summary
agree (`155 == 155`).

| Inventory | Source | Pre-baseline | This run |
| --- | --- | --- | --- |
| `compiled release regressions.*` (Suite.ps1, `--list-tests` lines) | `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll` | 161 | **86** |
| `Mcp.Dotnet.*` (producer Expecto, `--list-tests` lines) | `dotnet/tests/Mcp.Dotnet.Tests/bin/Release/net11.0/Mcp.Dotnet.Tests.dll` | 150 | **155** |

The release-cluster breakdown for the 86 cases (live `--list-tests`):

| 3-level cluster | Count |
| --- | --- |
| `compiled release regressions.candidate handoff.readCandidate malformed transport negatives` | 19 |
| `compiled release regressions.release policy parseBytes.strict JSON grammar and fail-closed inputs` | 19 |
| `compiled release regressions.enrollment.compiled enrollment API` | 4 |
| `compiled release regressions.release policy parseBytes.semantic schema relations` | 3 |
| `compiled release regressions.release policy parseBytes.canonical projections` | 2 |
| `compiled release regressions.candidate handoff.readCandidate canonical positives` | 2 |
| `compiled release regressions.cross-stage coherence.the production Coherence` | 2 |
| `compiled release regressions.enrollment.Update` | 2 |
| (singletons across `runner publication boundary`, `modern preparation boundary`, `cross-stage coherence`, `enrollment`, `Release legacy assertions`, etc.) | 33 |

The producer-cluster breakdown for the 155 cases:

| 3-level cluster | Count |
| --- | --- |
| `Mcp.Dotnet.security` | 36 |
| `Mcp.Dotnet.domain` | 24 |
| `Mcp.Dotnet.sync-over-async checker` | 20 |
| `Mcp.Dotnet.invocation` | 16 |
| `Mcp.Dotnet.controlled process execution` | 15 |
| `Mcp.Dotnet.async typed-failure regressions` | 13 |
| `Mcp.Dotnet.Dotnet schema` | 12 |
| `Mcp.Dotnet.MCP stdio host` | 11 |
| `Mcp.Dotnet.artifact quotas` | 8 |

## Counters (this run)

### Dotnet producer Expecto

| Counter | Value |
| --- | --- |
| Native Expecto exit code | `1` |
| `Mcp.Dotnet.*` total cases run | **155** |
| Passed | **153** |
| Ignored | **0** |
| Failed | **2** |
| Errored | **0** |
| Wall-clock | `00:00:53.0578561` |
| `--list-tests` count | 155 (parity with summary) |

Exact failed case names (full Expecto `[ERR]` lines preserved in
`Gates-dotnet-expecto.log`):

1. `Mcp.Dotnet.sync-over-async checker.source scanner fixtures.compiled release runner allows only its two exact CLI bridges` (`dotnet/tests/Mcp.Dotnet.Tests/blocking/Tests.fs:176`).
   - Diagnostic: `the two documented host-boundary branches are allowed. Should be empty.`
2. `Mcp.Dotnet.sync-over-async checker.repository F# surface.all active root, dotnet, and workflow source files are checked` (`dotnet/tests/Mcp.Dotnet.Tests/blocking/Tests.fs:282`).
   - Diagnostics: `BLOCK infrastructure/release/Runner.fs:151:51 [Async.RunSynchronously] -- no approved reason` and `BLOCK infrastructure/release/Runner.fs:152:54 [Async.RunSynchronously] -- no approved reason. Should be empty.`

Both failures are **prod-vs-fixture/source wiring evidence** (sync-over-async
checker reports two unapproved `Async.RunSynchronously` calls in the live
`infrastructure/release/Runner.fs` at lines 151 and 152 that the compiled
fixture expects to be empty / to be limited to exactly two documented
host-boundary branches). No root-cause patch is asserted; the two
`Async.RunSynchronously` lines and the exact fixture message are the
bounded evidence and belong to the source owner for adjudication.

### Suite.ps1 (full release suite + provenance script)

| Counter | Value |
| --- | --- |
| Suite.ps1 wrapper native exit | `1` |
| `release.Tests` Expecto native exit | `3` |
| `compiled release regressions.*` total cases run | **86** |
| Passed | **59** |
| Ignored | **0** |
| Failed | **19** |
| Errored | **8** |
| Inventory vs Expecto parity | `86 == 86` (no drift; gate-check rule satisfied) |
| Wall-clock (Expecto portion) | `00:01:41.9088681` |
| Historical case | passes (21 nested-assertion `historical-bootstrap-tests` summary: `passed=21 failed=0`) |
| Producer script (`dotnet/tests/ProvenanceTests.fsx`) | passes (`provenance contract passed`; last line of group) |
| `::error::` from gate | 1 (gate summary line: `::error::release regression gate failed: compiled release suite (exit 3)`) |
| `GITHUB_ENV` / `GITHUB_OUTPUT` writes during the gate | unchanged from pre-state (Suite.ps1 `Get-OutputState` diff produces no error) |
| Stderr lines | `0` |
| Provenance script native exit | `0` |

Inline sub-suite summaries emitted during the run (these are nested totals
inside the 86 compiled cases, not separate test counts):

| Sub-suite | passed | failed |
| --- | --- | --- |
| `historical-bootstrap-tests` | 21 | 0 |
| `invocation-projections` | 3 | 0 |
| `metadata-tests` | 52 | 0 |
| `publish` | 62 | 0 |
| `revision metadata` | 22 | 0 (skipped 0) |
| `producer PE integration` | 2 | 0 (notRun 0) |
| `telegram-isolation-tests` | 21 | **4** |

The inline `telegram-isolation-tests summary: passed=21 failed=4` is the
release-participant-isolation test (one top-level case whose four
sub-assertions are: roster-projected matrix, read-only prepare CLI path,
independent trusted-builder job for `Publisher.json`, isolated
publication-job write authority). Expecto emits the failed sub-assertions
as a `failed=4` summary line; the top-level case still counts as
`failed=1` in the outer summary (so it appears in the 19 failures list).

Exact failed-case list (full Expecto `[ERR]` lines preserved in
`Gates-suite-stdout.log`):

1. `compiled release regressions.cross-stage coherence.the production Coherence.ps1 accepts roster wiring and rejects marker drift` (`Coherence.fs:267`). Diagnostic: "a hardcoded workflow matrix fails the actual guard. Actual value was false but had expected it to be true."
2. `compiled release regressions.cross-stage coherence.the production Coherence.ps1 rejects roster policy and inventory drift` (`Coherence.fs:330`). Diagnostic: roster policy mismatch via `Roster.ps1:54` — `the enrollment roster entry at index 0 has a configPath 'dotnet/release/Alternate.fsx' that is not a contained relative JSON policy path`.
3. `compiled release regressions.cross-stage coherence.the coherence guard consumes the project step value, not its own output file` (`Coherence.fs:378`). Diagnostic: "the missing explicit projection is diagnosed. Actual value was false but had expected it to be true."
4. `compiled release regressions.cross-stage coherence.the actual Workflow CI step preserves a failing native exit before later entrypoints`.
5. `compiled release regressions.cross-stage coherence.the release workflow isolates preparation, trusted building, and data-only publication`.
6. `compiled release regressions.Evaluate legacy regression assertions`.
7. `compiled release regressions.release policy parseBytes.strict JSON grammar and fail-closed inputs.a non-string componentId is rejected`.
8. `compiled release regressions.release policy parseBytes.strict JSON grammar and fail-closed inputs.a number publishedFiles entry is rejected`.
9. `compiled release regressions.release policy parseBytes.strict JSON grammar and fail-closed inputs.a null required field is rejected`.
10. `compiled release regressions.release policy parseBytes.strict JSON grammar and fail-closed inputs.trailing commas are rejected as strict JSON`.
11. `compiled release regressions.candidate handoff.readCandidate canonical positives.dotnet readCandidate parses the exact-two-entry portable candidate`.
12. `compiled release regressions.candidate handoff.readCandidate canonical positives.workflow readCandidate parses the exact-two-entry portable candidate`.
13. `compiled release regressions.candidate handoff.readCandidate binds source SHA to the verified candidate bytes for both producers`.
14. `compiled release regressions.candidate handoff.readCandidate preserves canonical component, revision and SDK/toolchain identity`.
15. `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate rejects an inner producer archive without its embedded manifest`.
16. `compiled release regressions.runner publication boundary.publish refuses when a required flag is absent` (`Runner.fs:208`). Diagnostic: "the missing flag diagnostic names the missing flag. Actual value was false but had expected it to be true."
17. `compiled release regressions.release participant isolation assertions` (`Telegram.fs:259`). Diagnostic: 4 sub-assertions missing — "expected: 4 / actual: 0" for the four roster/builder/authority bullets.
18. `compiled release regressions.release authority child-probe regressions.authority gate rejects unauthorized candidates before any HTTP for both producers` (`Authority.fs:500`). Diagnostic: 6 child probes failed with `child exit=2 stderr=` for both producers across `reject-unauthorized`, `reject-dummy-do-upload`, `reject-authorized-missing-token` ("Should be empty.").
19. `compiled release regressions.release authority child-probe regressions.authorized transport invokes the canonical /releases/tags/<producer> URL without a v prefix for both producers` (`Authority.fs:558`). Diagnostic: 2 child probes failed with `child exit=2 stderr=` for both producers on the `authorized` URL probe ("Should be empty.").

Exact errored-case list (`errored` status — these exit abnormally before
assertion, no assertion-level message):

1. `compiled release regressions.W5 actual production transaction integrity regressions`.
2. `compiled release regressions.release policy parseBytes.canonical projections.canonical dotnet Config.json projects a source-bound policy`.
3. `compiled release regressions.release policy parseBytes.canonical projections.canonical workflow Config.json projects a source-bound policy`.
4. `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate unsafe path negatives.readCandidate rejects unsafe path '//server/share.txt'`.
5. `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate unsafe path negatives.readCandidate rejects unsafe path 'C:/drive.txt'`.
6. `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate unsafe path negatives.readCandidate rejects unsafe path 'C:relative.txt'`.
7. `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate unsafe path negatives.readCandidate rejects unsafe path 'C:\drive.txt'`.
8. `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate unsafe path negatives.readCandidate rejects unsafe path 'dir\backslash.txt'`.

## Bounded root-cause evidence (no speculative patches)

- **Dotnet sync-over-async checker (2)** — pure source/fixture wiring.
  The compiled fixture at `tests/bsdocking/Tests.fs:173-176` and
  `:264-282` expects the repo's `infrastructure/release/Runner.fs` to have
  no unapproved `Async.RunSynchronously` calls outside exactly two
  documented host-boundary branches. The dirty baseline has two more calls
  at `Runner.fs:151:51` and `Runner.fs:152:54` with no approved reason.
  This is the **prod source** finding, not a checker false positive (the
  other 18 sync-over-async cases pass in this run).
- **Coherence / cross-stage (5)** — the production `Coherence.ps1` and the
  compiled coherence fixture disagree on the hardcoded workflow matrix vs
  the roster-projected component list, the projection step output, and the
  three-job isolation contract. Bounded evidence: the roster-policy-mismatch
  exception text explicitly references `dotnet/release/Alternate.fsx`
  (`Roster.ps1:54`), indicating the projection fixture still encodes the
  pre-Simplification `Config.fsx` paths.
- **release policy parseBytes (4 failed + 2 errored)** — strict-JSON
  grammar/fail-closed and `Config.json` projections differ from the live
  `dotnet/release/Config.json` / `workflow/release/Config.json` content
  (`Config.json` canonical projections errored; specific fail-closed inputs
  on `non-string componentId`, `number publishedFiles`, `null required
  field`, `trailing commas` fail with assertion messages). No patch is
  attempted.
- **readCandidate positives (4 failed) + 1 nested + 5 errored unsafe-path
  negatives** — `candidate handoff.readCandidate` does not parse the
  exact-two-entry portable candidate, does not bind source SHA, does not
  preserve canonical component/revision/SDK/toolchain identity, and the
  5 unsafe-path negatives errored (each `errored in ~0.006 s` with no
  assertion message) — same source/fixture wiring pattern as the
  parseBytes cases. Bounded evidence: the unsafe-path negatives are
  `readCandidate unsafe path negatives.readCandidate rejects unsafe path
  '<absolute/UNC/backslash path>'` and error before the assertion body.
- **runner publication boundary (1)** — `publish refuses when a required
  flag is absent` failed at `Runner.fs:202-208` with "the missing flag
  diagnostic names the missing flag. Actual value was false but had
  expected it to be true." — the live runner does not produce the
  expected missing-flag diagnostic.
- **release participant isolation (1, expected 4)** —
  `Telegram.fs:259-1`; all four bullets missing. The release matrix does
  not project the roster list, prepare does not derive CLI path from
  matrix, builder does not build `Publisher.json`, publication does not
  own write authority after prepare + builder. This is the central
  participant-isolation contract for the user's approved
  `Simplification.md` and is currently unmet.
- **release authority (2)** — `Authority.fs:500` and `:558`. Six child
  probes for the unauthorized gate and two for the authorized
  `/releases/tags/<producer>` URL all returned `child exit=2 stderr=`
  (empty stderr). Indicates the `Runner.fs` child exits with code `2`
  before producing the expected probe output. Same source/fixture wiring
  family as the `Runner.fs:151-152` unapproved `Async.RunSynchronously`
  defect.
- **Historical/bootstrap sub-suites** — all pass (21/0 historical-bootstrap,
  52/0 metadata-tests, 62/0 publish, 22/0 revision metadata, 2/0
  producer PE integration, 3/0 invocation-projections). The
  `Fixtures.json` pin and the historical archives are exercised and verify
  on disk byte-identical to the pre-run state.

## No-retry / no-edit / no-network guarantee

- Two native invocations only: the producer Expecto and the Suite.ps1 gate.
- No rebuild (`dotnet build` not invoked; DLLs pre/post SHA-256 identical).
- No source/test edits; no `git` operations; no commits, pushes or
  thread resolution.
- No HTTP/credentials/publisher/probe environment names touched. The
  only transport paths exercised are the compiled runner child probes
  (internal `child probe` invocations in the test host) and the
  anonymous fixed public URL referenced in the descriptor's `assetUri`
  field. No live `GH_TOKEN`/`GITHUB_TOKEN` was read by this run
  (enrolled by `Simplification.md`).
- Recording HTTP / `TestTransport` continue to be the only transport
  modes that run.
- Combined retained output (UTF-8): `Gates.log` 50 423 B + per-stream
  `Gates-*` files listed above. Source/runtime SHA drift check: all
  recorded pre/post SHAs equal; the working tree remained dirty
  (no source edit, no fixture write).

## Residual risks for the dirty Simplification baseline

- **Working tree dirty (Simplification refactor not yet committed)** — the
  build may succeed and the historical case passes, but the
  Suite.ps1/producer Expecto gate currently reports 27 of 86 (31.4 %)
  release regressions + 2 of 155 (1.3 %) producer regressions as
  failing/unapproved against the live Simplification source. This is
  bounded evidence of fixture-vs-source wiring drift on the Simplification
  boundary, not a release verdict.
- **Inventory delta** — Suite.ps1 compiled regressions shrank from
  161 to 86 cases (75 fewer; 46.6 %); the producer Expecto grew from
  150 to 155 (5 more). The reduction in the compiled suite is consistent
  with `Simplification.md` (custom publisher bootstrap, receipts,
  closure abstraction retired; custom groups removed; `Bootstrap.fs`,
  `Bootstrap.ps1`, `Publisher.json`, `PublisherClosure.fs` deleted). The
  new producer Expecto cases are the 5 added by the dirty refactor
  (likely in `async typed-failure regressions` and
  `sync-over-async checker` clusters). The exact 75 retired cases are
  not enumerated here (out of scope for this run; the user only asked
  for actuals).
- **No final clean-SHA packaging verification** — the dirty tree is not
  committable; the gate is not a release verdict. Final clean-SHA
  packaging / committed-source verification remain pending.
- **No new independent review/AcceptanceAudit dispatched** — no
  source/test edits were made by this run; reviewer/validator role
  hand-offs are unchanged. Owner decision needed before any next
  bounded coverage work.

---

# Consolidated verification cycle — focused group re-execution

> Read-only tester cycle against the current rebuilt assemblies
> (`Release.Tests.dll` SHA-256 `2E87EB9F…0B05BE5`, `ReleaseRunner.dll`
> `FFB5966D…16AF8E`, `Mcp.Dotnet.Tests.dll` `C27DA25A…A140D4`,
> `Mcp.Dotnet.dll` `B82CC36B…7BB825`). No source/test edits, no rebuild,
> no delegation. Full log retained at
> `C:\Users\andre\AppData\Local\Temp\opencode\ghi24-simplified\Verification.log`.

## Identity / provenance (current read)

| Field | Value |
| --- | --- |
| Git HEAD | `18146a7dca58a1f08ca58b6407ccbb2e244a9395` |
| Working tree | 55 modified files (dirty source) |
| `Release.Tests.dll` SHA-256 | `2E87EB9FCB427B0D0DCD4A15968F74528FE9B84DCAAF9AB5C37C76C7E0B05BE5` (assignment match) |
| `Release.Tests.dll` build | `14-Oct-26 20:31:05`, 768 512 B |
| `Policy.fs` last write | `14-Oct-26 20:22:30` (modified AFTER DLL build input timestamp; rebuilt DLL incorporates the modified fixture) |
| `ReleaseRunner.dll` SHA-256 | `FFB5966D06AE42AA2BB92A5BE0F804CDE25D239031F908D20F359C525916AF8E` (assignment match) |
| `Mcp.Dotnet.Tests.dll` SHA-256 | `C27DA25AE5F4EA521EE075E29C48B2D3405B4DA0D20787055A469660C5A140D4` (assignment match) |
| `Mcp.Dotnet.dll` SHA-256 | `B82CC36BFA922856BBAA16E482F7693DB5A5FD4902341C454566769EC07BB825` |
| Process environment | `MCP_RELEASE_DO_UPLOAD`, `GITHUB_TOKEN`, `GITHUB_ENV`, `GITHUB_OUTPUT` removed by NAME only; no `.env`/session-store value read; no network; no upload |

## Focused group outcomes (this cycle)

| Focused group | Native filter | Result |
| --- | --- | --- |
| Producer `sync-over-async checker` (20 cases) | `--run "<full test name>"` per case | **GREEN** — 20 passed / 0 ignored / 0 failed / 0 errored. Includes `Mcp.Dotnet.sync-over-async checker.repository F# surface.all active root, dotnet, and workflow source files are checked` 1/1 PASS |
| Producer full inventory (155 cases) | `--run "<full test name>"` per case | **GREEN** — 155 passed / 0 ignored / 0 failed / 0 errored (all 155 dotnet tests passed; per-test log `dotnet-all-summary-clean.log`) |
| Release `policy parseBytes` (Policy module) | `Release.Tests.exe --list-tests` (or any invocation) | **RED — process never started**. `Policy.fs:108` fixture API misuse: `mutated ignore` round-trips through `JsonNode.Parse` + `ToJsonString(WriteIndented=false)` which strips spaces around `:`; `IndexOf("\"version\": \"1.0.4\"")` returns -1 and `failtest` throws inside the module cctor |
| Release `candidate Handoff` (Handoff module) | cascade via `Main.fs:25` → `Policy.tests` | **RED — process never started** |
| Release `Authority` (Authority module) | cascade via `Main.fs:25` → `Policy.tests` | **RED — process never started** |
| Release `actual transaction Integrity` (Integrity module) | cascade via `Main.fs:25` → `Policy.tests` | **RED — process never started** |
| Release `Evaluate` (Evaluate module) | cascade via `Main.fs:25` → `Policy.tests` | **RED — process never started** |
| Release `Coherence` (Coherence module) | cascade via `Main.fs:25` → `Policy.tests` | **RED — process never started** |
| Release `Runner` (Runner module) | cascade via `Main.fs:25` → `Policy.tests` | **RED — process never started** |
| Release `modern prepare` (`Orchestration` + `Enrollment "modern preparation boundary"`) | cascade via `Main.fs:25` → `Policy.tests` | **RED — process never started** |

The single native EXE invocation for release test (any argument) exits 1
with:

```
Unhandled exception. System.TypeInitializationException: ...
  ---> Expecto.AssertException: duplicate-field fixture lost the
    canonical version literal during JsonNode round-trip
   at Release.Tests.Policy.duplicateVersion@104(Unit unitVar0)
     in D:\repos\private\mcp-store\infrastructure\release\tests\Policy.fs:line 108
   at <StartupCode$Release-Tests>.$Release.Tests.Policy..cctor()
     in D:\repos\private\mcp-store\infrastructure\release\tests\Policy.fs:line 118
  --- End inner exception stack ---
   at Release.Tests.Policy.get_tests()
   at <StartupCode$Release-Tests>.$Release.Tests.Main..cctor()
     in D:\repos\private\mcp-store\infrastructure\release\tests\Main.fs:line 12
  --- End inner exception stack ---
   at Release.Tests.Main.main(String[] argv)
     in D:\repos\private\mcp-store\infrastructure\release\tests\Main.fs:line 53

Exited with code 1
```

Reflection access to `Main.suites` raises the same exception; per-module
isolation is impossible without a source patch. The fixture post-process
`IndexOf("\"version\": \"1.0.4\"", StringComparison.Ordinal)` runs against
`node.ToJsonString(JsonSerializerOptions(WriteIndented = false))` whose
default compact output is `{"componentId":"dotnet","version":"1.0.4",...}`
(no spaces around `:`); the search fails and `failtest` aborts the cctor.

## Full-gate policy decision

Per the cycle plan, `IffocusedANYred STOPfullgates untilrootcause
correction; captureallfocusedfailgroups notjustfirst truncatedsummary.`

Eight release focused groups are RED (process never started via shared
cctor failure). The cycle does NOT run:

- One full Dotnet Expecto no-build pass through `Mcp.Dotnet.Tests.exe`,
- One full `Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures` 600 s pass.

`Suite.ps1` was not invoked; native Expecto was not invoked; no inventory
or provenance is claimed for release. The producer sync-over-async checker
group is GREEN but it is not the entire producer suite — no broader
producer Expecto full run was performed.

## tested DLL identities (no drift)

| DLL | Expected (cycle plan) | Observed (this cycle) | Match |
| --- | --- | --- | --- |
| `Release.Tests.dll` | `2E87EB9FCB427B0D0DCD4A15968F74528FE9B84DCAAF9AB5C37C76C7E0B05BE5` | `2E87EB9FCB427B0D0DCD4A15968F74528FE9B84DCAAF9AB5C37C76C7E0B05BE5` | yes |
| `ReleaseRunner.dll` | `FFB5966D06AE42AA2BB92A5BE0F804CDE25D239031F908D20F359C525916AF8E` | `FFB5966D06AE42AA2BB92A5BE0F804CDE25D239031F908D20F359C525916AF8E` | yes |
| `Mcp.Dotnet.Tests.dll` | `C27DA25AE5F4EA521EE075E29C48B2D3405B4DA0D20787055A469660C5A140D4` | `C27DA25AE5F4EA521EE075E29C48B2D3405B4DA0D20787055A469660C5A140D4` | yes |

All DLL identities are stable; no source drift against the assemblies
under test.

## Residual risk and limits

- **Eight release focused groups RED via shared cctor failure** — the
  cycle cannot verify any release group; source/test owners must repair
  `Policy.fs:104-108` so the `duplicate-field` fixture reflects the
  compact JSON produced by `JsonNode.ToJsonString(WriteIndented=false)`
  (search for `"version":"1.0.4"` without the space, or re-emit baseline
  with an explicit space-preserving serializer, or rebuild the fixture
  byte sequence after the `mutated` round-trip). After rebuild, this
  cycle can re-execute the focused groups.
- **Producer sync-over-async checker GREEN, plus broader inventory GREEN** — that
  specific group was the named focused scope; an additional broader
  full-inventory run (`run-all-dotnet-clean.ps1`) showed 155 passed /
  0 ignored / 0 failed / 0 errored across the entire compiled producer
  Expecto suite (`dotnet-all-summary-clean.log`). No `SyncOverAsync`
  violation was observed across root/dotnet/workflow source trees.
- **No source/test patch, rebuild, commit, push, merge, or upload** was
  performed. The cycle is read-only verification, not approval.
- **Full Suite.ps1 gate is intentionally not invoked** this cycle;
  focused-any-red policy prevents it.
- **No secrets/env-values/session-stores/live API/upload/Git state** —
  all environment names were removed by NAME only; no values were
  read or transmitted. Child dummy data was used for any probe.
- **Clean-source packaging verification, independent review, and
  Acceptance Audit** are out of scope for this cycle; they remain
  pending.

# Suite.ps1 actual run (corrected Release build, single full-gate)

> Read-only tester cycle against the post-fix compiled release suite
> after the source owners' policy-fixture repair (`duplicate JSON fixture
> initialization now lazy inside-case raw compact JSON; version literal
> inserted first + original version with count=2 before production strict
> parse; no module cctor fail`). The DLL under test was rebuilt
> (Policy.fs mtime 20:47:26 + Release.Tests.dll mtime 20:49:54). The
> previous cycle's "process never started" / 8-group-RED outcome is no
> longer reproduced. `--list-tests` instantiates **86** cases (1
> `Historical`); the inventory gate passes; the full Suite.ps1 is
> executed once, no source/test edits, no rebuild, no delegation, no
> producer rerun. Producer Expecto 155 per-case PASS is preserved from
> the prior cycle (`Mcp.Dotnet.Tests.dll` SHA
> `C27DA25AE5F4EA521EE075E29C48B2D3405B4DA0D20787055A469660C5A140D4`
> byte-identical, mtime 20:31:25).
>
> Expected (per coordinator directive) vs actual: 86 dynamic
> `compiled release regressions.*` cases, 1 historical. Producer 155
> inventory claim preserved (no rerun).

## Identity / provenance (this run)

| Field | Value |
| --- | --- |
| Git HEAD | `18146a7dca58a1f08ca58b6407ccbb2e244a9395` ("GHI-24: finalize orchestration comments and verification record") — sourceDirty18146 (user-approved `Simplification.md`) |
| Working tree | 55 modified files (dirty source/API) |
| `Release.Tests.dll` SHA-256 | `F9CDDC7F8976D6FD6175A1577D929035AAB4D7D143C08E784D9C99773E3BE150` (769 536 B, built `14-Oct-26 20:49:54`) — actual read, not prior-cycle `2E87EB9F…0B05BE5` |
| `Release.Tests.dll` size | 769 536 B |
| `Release.Tests.pdb` SHA-256 | `5EA74CAA7B57FF7432D8419DEACFA3926CF156A326A673C90A3A3EAD61C91AC8` |
| `ReleaseRunner.dll` SHA-256 | `FFB5966D06AE42AA2BB92A5BE0F804CDE25D239031F908D20F359C525916AF8E` (437 248 B, prior cycle match — "ProdCopy") |
| `Suite.ps1` SHA-256 (pre == post) | `8E260BC642F10F0D9FA47F6F8BD0E7F73ABCC68FBA0510251215BBD6BEC26DFC` (6 448 B) |
| `Fixtures.json` SHA-256 (pre == post) | `0F32E8C1CC1C1ADDE4AE4E804F4F0529F49B6D8C151DFA9079CF9F9FF5E6762A` (843 B) |
| `ghi24-current-fixtures/Dotnet.zip` SHA-256 (pre == post) | `DA3ACB20007F8015E541EE7D68B48ADF49D232C4EEA41190A62073073D1A1614` (1 289 835 B) |
| `ghi24-current-fixtures/Workflow.zip` SHA-256 (pre == post) | `65D274056951C64D4EAFEF2ED7242F2CF02005E639D91F2A2A9B85B921E53313` (1 514 420 B) |
| HistoricalRoot | `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures` |
| Pre-run DLL/file inventory snapshot | `ghi24-simplified/Release-preshas.json` (UTF-8, JSON) |
| Post-run DLL/file inventory snapshot | `ghi24-simplified/Release-postshas.json` (UTF-8, JSON) |
| Log file (UTF-8, no BOM, LF) | `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Release.log` (12 756 B, 110 lines) |
| Wall-clock (Expecto portion) | `00:02:07.3983603` |
| Wall-clock (pwsh Start-Process wrapper) | 139.687 s |
| Budget used | 139.7 s of 600 s (well under budget) |
| Inventory dynamic check (`--list-tests`) | 86 compiled cases (1 `Historical`) — exit 0, no exception |
| Inventory vs Expecto summary parity | 86 == 86 (no drift; gate parity rule satisfied) |
| `Mcp.Dotnet.Tests.dll` SHA | `C27DA25AE5F4EA521EE075E29C48B2D3405B4DA0D20787055A469660C5A140D4` (332 288 B, mtime 20:31:25 — preserved from prior cycle, NOT rerun this turn) |
| Stderr lines (Suite.ps1) | `0` |
| Provenance script native exit | `0` (`dotnet/tests/ProvenanceTests.fsx` → last line `provenance contract passed`) |
| Expecto native exit | `3` (one errored case exits abnormally) |
| Suite.ps1 wrapper native exit | `1` (gate FAIL — one top-level `::error::` line) |
| `::error::` from gate | 1 (only `::error::release regression gate failed: compiled release suite (exit 3)`) |
| `GITHUB_ENV` / `GITHUB_OUTPUT` writes during the gate | unchanged from pre-state (Suite.ps1 `Get-OutputState` diff produces no error) |
| `MCP_RELEASE_DO_UPLOAD`, `GITHUB_TOKEN`, `GITHUB_ENV`, `GITHUB_OUTPUT`, `MCP_RELEASE_PUB_TOKEN`, `MCP_RELEASE_PROBE_TOKEN` | unset by NAME only before invocation; no values read or transmitted |
| Process environment transport | recording HTTP + `TestTransport` (per Handoff/Authority stubs); no live network/API/upload |

## Inventory (dynamic, post-fix, 86 unique cases)

`dotnet Release.Tests.dll --list-tests` returned **86** unique
`compiled release regressions.*` cases; 1 `Historical` case is present
and was exercised. `--list-tests` exit code `0` (no
`TypeInitializationException` from `Policy.tests` cctor — the prior
cycle's "process never started" blocker is fixed).

| 3-level cluster | Count |
| --- | --- |
| `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate exact-two-entry negatives` | 4 |
| `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate unsafe path negatives` | 6 |
| `compiled release regressions.candidate handoff.readCandidate malformed transport negatives` (other) | 9 |
| `compiled release regressions.candidate handoff.readCandidate canonical positives` | 2 |
| `compiled release regressions.release policy parseBytes.strict JSON grammar and fail-closed inputs` | 19 |
| `compiled release regressions.release policy parseBytes.semantic schema relations` | 3 |
| `compiled release regressions.release policy parseBytes.canonical projections` | 2 |
| `compiled release regressions.candidate handoff.readCandidate binds source SHA …` | 1 |
| `compiled release regressions.candidate handoff.readCandidate preserves canonical component, revision and SDK/toolchain identity` | 1 |
| `compiled release regressions.enrollment.compiled enrollment API` | 4 |
| `compiled release regressions.enrollment.Update` | 2 |
| `compiled release regressions.cross-stage coherence.the production Coherence` | 2 |
| `compiled release regressions.cross-stage coherence` (other 5 cases) | 5 |
| `compiled release regressions.modern preparation boundary` | 3 |
| `compiled release regressions.runner publication boundary` | 11 |
| `compiled release regressions.release authority child-probe regressions` | 2 |
| `compiled release regressions.release participant isolation assertions` | 1 |
| legacy single-case tops (`Evaluate`, `Historical`, `Metadata`, `Publish`, `Integrity`, `Revision`, `Telegram` — non-clustered) | 7 |
| `release policy parseBytes.immutable byte snapshot.projection and source hash remain bound to the bytes observed before file mutation` | 1 |
| `candidate handoff.preparation receipt binds producer-known hashes, sizes, and schema only` | 1 |
| `candidate handoff.preparation receipt does not claim the later platform outer digest` | 1 |
| `candidate handoff.trusted receipt accepts a populated cross-consistent runtime triplet` | 1 |
| `candidate handoff.malformed transport negatives.outer ZIP …` | 1 |
| `candidate handoff.malformed transport negatives.handoff rejects standalone and embedded manifest byte mismatch` / `… without its embedded manifest` | 1 (the other half of the two-case pair is counted in "other 9") |
| `candidate handoff.malformed transport negatives.handoff snapshot copies and read-only streams do not expose verified backing arrays` | 1 |
| `candidate handoff.malformed transport negatives.preparation hash/size mismatch against verified transport` | 4 |
| `compiled release regressions.Historical legacy regression assertions` | 1 |
| `compiled release regressions.W5 actual production transaction integrity regressions` | 1 |

Cluster counts reconcile against the prior cycle's 86-case inventory and
add up to **86** unique cases (parity with `Expecto` summary).

## Counters (post-fix single run)

| Counter | Value |
| --- | --- |
| `--list-tests` lines matching `^compiled release regressions\.` | **86** (dynamic) |
| `Historical` cases in inventory | **1** (exercised) |
| `Expecto` tests run | **86** |
| Passed (Expecto) | **75** |
| Ignored (Expecto) | **0** (no case excluded/ignored/skipped) |
| Failed (Expecto) | **10** |
| Errored (Expecto) | **1** |
| Inventory vs Expecto parity | `86 == 86` (parity gate satisfied) |
| Wall-clock (Expecto portion) | `00:02:07.3983603` |
| Total pwsh wrapper wall-clock | 139.687 s of 600 s (under budget) |
| `Expecto` native exit | `3` (errored case aborts the process before final assertion summary) |
| Suite.ps1 wrapper native exit | `1` (gate FAIL — `::error::release regression gate failed`) |
| Provenance script native exit | `0` (last line `provenance contract passed`) |
| Stderr lines | `0` |
| `::error::` lines from gate | 1 (only the gate-summary failure line) |

### Inline sub-suite totals (nested within the 86 cases, not separate counts)

| Sub-suite | passed | failed |
| --- | --- | --- |
| `historical-bootstrap-tests` | 21 | 0 |
| `invocation-projections` | 3 | 0 |
| `metadata-tests` | 52 | 0 |
| `publish` | 62 | 0 |
| `revision metadata` | 22 | 0 (skipped 0) |
| `producer PE integration` | 2 | 0 (notRun 0) |
| `telegram-isolation-tests` | 23 | **1** |
| (cross-stage coherence, candidate handoff positives/negatives, release policy parseBytes, authority child-probe, runner publication boundary, modern preparation boundary — fail/error inside their parent case) | (counted in outer 10 failed / 1 errored) | — |

The single `telegram-isolation-tests` failure is the sub-assertion
"the fresh read-only preparation job derives its CLI path from the
looped participant" inside the `release participant isolation
assertions` top-level case (`Telegram.fs:254`); the outer summary
reports it as 1 failure in the 10-failed total.

### Exact failed-case list (full `[ERR]` lines preserved in `Release.log`)

1. `compiled release regressions.cross-stage coherence.the production Coherence.ps1 rejects roster policy and inventory drift` (`Coherence.fs:312`). Diagnostic: `the non-contained configPath failure is identified: Exception: …Roster.ps1:54 … the enrollment roster entry at index 0 has a configPath 'dotnet/release/Alternate.fsx' that is not a contained relative JSON policy path. Actual value was false but had expected it to be true.` — the projection fixture still encodes the pre-Simplification `Config.fsx` `Alternate.fsx` rejection path that the live `Roster.ps1` raises on `dotnet/release/Alternate.fsx`. The bounded evidence is `Roster.ps1:54` and the roster-policy-mismatch exception text.
2. `compiled release regressions.Evaluate legacy regression assertions` (`Evaluate.fs:123`). Diagnostic: `candidate directory was rejected by readCandidate: required release artifact does not exist: C:\Users\andre\AppData\Local\Temp\mcp-evaluate-candidate-11f11baeb0794594bd32026680651f56` — the candidate directory produced by `readCandidate` is empty (no `v1.0.4.zip` written into it); same source/fixture wiring family as cases 3–7.
3. `compiled release regressions.candidate handoff.readCandidate canonical positives.dotnet readCandidate parses the exact-two-entry portable candidate` (`Handoff.fs:190`). Diagnostic: `canonical two-entry candidate was rejected (v1.0.4.zip, 1288 bytes, names=["v1.0.4.zip"; "distribution.json"]): MissingArtifact "v1.0.4.zip"`.
4. `compiled release regressions.candidate handoff.readCandidate canonical positives.workflow readCandidate parses the exact-two-entry portable candidate` (`Handoff.fs:190`). Diagnostic: `canonical two-entry candidate was rejected (v1.0.5.zip, 1311 bytes, names=["v1.0.5.zip"; "distribution.json"]): MissingArtifact "v1.0.5.zip"`.
5. `compiled release regressions.candidate handoff.readCandidate binds source SHA to the verified candidate bytes for both producers` (`Handoff.fs:216`). Diagnostic: `dotnet canonical candidate was rejected: MissingArtifact "v1.0.4.zip"`.
6. `compiled release regressions.candidate handoff.readCandidate preserves canonical component, revision and SDK/toolchain identity` (`Handoff.fs:232`). Diagnostic: `dotnet canonical candidate was rejected: MissingArtifact "v1.0.4.zip"`.
7. `compiled release regressions.candidate handoff.readCandidate malformed transport negatives.readCandidate rejects an inner producer archive without its embedded manifest` (`Handoff.fs:278`). Diagnostic: `the malformed inner archive was rejected for an unexpected reason: MissingArtifact "v1.0.4.zip"` (the test's inner-archive setup never wrote the named entry; `readCandidate` surfaces a `MissingArtifact` before reaching the "missing manifest" negative-path expectation).
8. `compiled release regressions.release participant isolation assertions` (`Telegram.fs:254`). Diagnostic: `the fresh read-only preparation job derives its CLI path from the looped participant. expected: 1 / actual: 0` — 1 of 4 sub-assertions fails (the matrix/projection sub-assertion); the top-level case counts as 1 failed in the outer 10.
9. `compiled release regressions.release authority child-probe regressions.authority gate rejects unauthorized candidates before any HTTP for both producers` (`Authority.fs:461`). Diagnostic: 6 child probes failed with `child exit=2 stderr=` (`reject-unauthorized/dotnet`, `reject-unauthorized/workflow`, `reject-dummy-do-upload/dotnet`, `reject-dummy-do-upload/workflow`, `reject-authorized-missing-token/dotnet`, `reject-authorized-missing-token/workflow`); `Should be empty.`
10. `compiled release regressions.release authority child-probe regressions.authorized transport invokes the canonical /releases/tags/<producer> URL without a v prefix for both producers` (`Authority.fs:519`). Diagnostic: 2 child probes failed with `child exit=2 stderr=` (`authorized/dotnet`, `authorized/workflow`); `Should be empty.`

### Exact errored-case list (process aborted before assertion; no assertion-level message)

1. `compiled release regressions.W5 actual production transaction integrity regressions` (`Integrity.fs:214`). Diagnostic: `System.Exception: dotnet candidate was rejected by readCandidate: required release artifact does not exist: v1.0.4.zip` — same `readCandidate` `MissingArtifact v1.0.4.zip` symptom as cases 2–7; `Integrity.writeCandidate` raises inside the test body.

## Bounded root-cause evidence (no speculative patches)

- **Coherence (`Roster.ps1:54` projection) (1)** — the production
  `Coherence.ps1` and the compiled coherence fixture disagree on the
  `Roster.ps1:54` non-contained-configPath rejection. Bounded evidence:
  the live `Roster.ps1` throws on the projection fixture's
  `dotnet/release/Alternate.fsx` (line 54 throw), and the compiled
  expectation is for the projection to have rejected it the same way.
  This is the **Roster.ps1 / fixture** wiring (one bullet of the
  participant-isolation contract).
- **readCandidate positives (5 failed) + W5 Integrity errored (1)** —
  the `candidate handoff.readCandidate`/`readCandidate canonical
  positives`/`Integrity` cases all surface the same `MissingArtifact
  "v1.0.4.zip"` / `MissingArtifact "v1.0.5.zip"` symptom: the candidate
  directory does not contain the expected inner archive after
  `readCandidate` runs. Same source/fixture wiring pattern (Handoff
  contract) — the live `readCandidate` discards the inner archive
  before assertion (cases 3–7, errored case 1, also Evaluate case 2
  via the same contract).
- **`release participant isolation assertions` (1 of 4 sub-bullets) (1)**
  — `Telegram.fs:254`; 1 of 4 sub-assertions (`the fresh read-only
  preparation job derives its CLI path from the looped participant`)
  fails with `expected: 1 / actual: 0`. The other 3 sub-bullets pass
  (the sub-suite prints `telegram-isolation-tests summary: passed=23
  failed=1`). Central participant-isolation contract for the user's
  approved `Simplification.md` is partially met (1 bullet failing).
- **`release authority` child probes (2 cases; 8 child probes total) (2)**
  — `Authority.fs:461` and `:519`. Six child probes for the unauthorized
  gate and two for the authorized `/releases/tags/<producer>` URL all
  return `child exit=2 stderr=` (empty stderr); the compiled
  `Runner.fs` child exits with code `2` before producing the expected
  probe output. Same `Runner.fs` source/fixture wiring family.

## Cross-check against cycle expectations

- `--list-tests` exit code: **0** (prior cycle's `TypeInitializationException` no longer raised; `Policy.tests` cctor is fixed by the source owners' lazy fixture init).
- Inventory: **86** == **86** summary (parity; no drift).
- 1 `Historical` case present and exercised (passes with 21 nested `historical-bootstrap-tests` assertions).
- 1 producer script (`dotnet/tests/ProvenanceTests.fsx`) passed (`provenance contract passed`).
- 8 prior-cycle-RED groups (cross-stage coherence, candidate Handoff, Authority, Integrity, Evaluate, Coherence, Runner, modern prepare/Orchestration) **all instantiable** (the cycle's "process never started" blocker is gone). Each group has at least one entry that ran; the **cases still RED** inside these groups are the 10 failures + 1 errored listed above.
- Producer Expecto 155 inventory claim is preserved (no rerun this cycle; SHA byte-identical to prior PASS).

## Cycle verdict (precise, no zero-case interpretation)

- Gate FAIL: **86 == 86** inventory parity holds (Suite.ps1 gate-check
  rule satisfied), but the Expecto summary reports **10 failed + 1
  errored** out of 86 cases; Suite.ps1 wrapper native exit `1`; the
  gate emits exactly one `::error::` line (`::error::release regression
  gate failed: compiled release suite (exit 3)`).
- Bounded evidence grouped by source/fixture family:
  - **Roster.ps1 / Coherence** (1 failed)
  - **Handoff.readCandidate / Integrity.writeCandidate** (6 failed + 1 errored — same `MissingArtifact v1.0.4.zip` / `v1.0.5.zip` wiring)
  - **Evaluate (readCandidate contract)** (1 failed — same wiring)
  - **Telegram.fs release-participant-isolation** (1 failed — 1 of 4 sub-bullets)
  - **Authority.fs Runner.fs child probes** (2 failed — 8 child probes total; `child exit=2 stderr=`)

## Source/runtime SHA drift (this run)

| Path | Pre SHA-256 | Post SHA-256 | Drift |
| --- | --- | --- | --- |
| `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll` | `F9CDDC7F…E3BE150` | `F9CDDC7F…E3BE150` | none |
| `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.pdb` | `5EA74CAA…1C91AC8` | `5EA74CAA…1C91AC8` | none |
| `infrastructure/release/tests/bin/Release/net11.0/ReleaseRunner.dll` | `FFB5966D…916AF8E` | `FFB5966D…916AF8E` | none |
| `infrastructure/release/Suite.ps1` | `8E260BC6…BEC26DFC` | `8E260BC6…BEC26DFC` | none |
| `infrastructure/release/tests/Fixtures.json` | `0F32E8C1…F6762A` | `0F32E8C1…F6762A` | none |
| `ghi24-current-fixtures/Dotnet.zip` | `DA3ACB20…1A1614` | `DA3ACB20…1A1614` | none |
| `ghi24-current-fixtures/Workflow.zip` | `65D27405…E53313` | `65D27405…E53313` | none |
| `ghi24-current-fixtures/Fixtures.json` | `0F32E8C1…F6762A` | `0F32E8C1…F6762A` | none |

No rebuild; no fixture rewrite; working tree remained 55 modified files
(no source edit by this run).

## No-retry / no-edit / no-network guarantee

- One Suite.ps1 invocation only (139.7 s of 600 s budget; no retries).
- One `--list-tests` dry-run only (exit 0, 86 cases enumerated).
- No `dotnet build`; the compiled DLLs were not rebuilt by this run
  (pre/post SHAs byte-identical).
- No source/test edits; no `git` operations; no commits, pushes, or
  thread resolution.
- No HTTP/credentials/publisher/probe environment values read. The
  credential env names (`MCP_RELEASE_DO_UPLOAD`, `GITHUB_TOKEN`,
  `GITHUB_ENV`, `GITHUB_OUTPUT`, `MCP_RELEASE_PUB_TOKEN`,
  `MCP_RELEASE_PROBE_TOKEN`) were removed by NAME only; no values were
  read or transmitted. The compiled runner child probes are internal
  `child probe` invocations in the test host with dummy children (no
  real network/API/upload); recording HTTP and `TestTransport` are the
  only transport modes that ran.
- Combined retained output: `Release.log` (12 756 B, UTF-8 no BOM, 110
  lines, LF only) at
  `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Release.log`
  plus pre/post SHA JSON snapshots
  (`Release-preshas.json` / `Release-postshas.json`).

## Residual risks

- **Gate FAIL on 11 of 86 release compiled regressions (10 failed + 1
  errored).** Bounded evidence belongs to the source owner for
  adjudication. No patch was attempted by this run. The cluster
  breakdown of red cases is: Roster.ps1 / Coherence wiring (1);
  Handoff.readCandidate + Integrity.writeCandidate `MissingArtifact
  v1.0.4.zip` / `v1.0.5.zip` wiring (5 + 1); Evaluate readCandidate
  contract (1); Telegram.fs release-participant-isolation 1-of-4
  sub-bullet (1); Authority.fs Runner.fs child probes (2 cases / 8
  probes; `child exit=2 stderr=`).
- **Working tree dirty (55 modified files)** — the Simplification
  refactor is not yet committed; this is a bounded fixture-vs-source
  wiring drift on the Simplification boundary, not a release verdict.
- **No final clean-SHA packaging verification, no independent review
  rerun, no Acceptance Audit** — out of scope for this cycle. The
  producer Expecto 155-case PASS claim is preserved (no rerun).
- **`Mcp.Dotnet.Tests.dll` SHA `C27DA25A…A140D4` (332 288 B,
  mtime 20:31:25)** is byte-identical to the prior cycle and
  exercised (no rerun this turn); the 155-case PASS is preserved as
  per the cycle plan.
## Finalgate one-shot (current cycle)

Ran infrastructure/release/Suite.ps1 once against the current
build (infrastructure/release/tests/Tests.fsproj, configuration
Release). Historical root pinned to
C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures.
No rebuild; no source/test edits; no dotnet build; no git
operations; HEAD unchanged (18146a7dca58a1f08ca58b6407ccbb2e244a9395).

### Inventory parity

- --list-tests reported **86 compiled cases** (1 historical); Suite.ps1
  consumed that as the dynamic case count.
- Dataset present locally: Dotnet.zip 1 289 835 B +
  Workflow.zip 1 514 420 B + Fixtures.json 843 B
  (ghi24-current-fixtures). No remote download.

### Result

- **Expecto:** 86 tests run in 00:01:47.6056922 for compiled release
  regressions - 84 passed, 0 ignored, 2 failed, 0 errored.
- **Native Expecto exit code:** 1.
- **Suite.ps1 wrapper exit code:** 1.
- **Wall clock:** 117.69 s of 600 s budget (482.31 s underrun).
- **Gate error line:** ::error::release regression gate failed:
  compiled release suite (exit 1).
- **Provenance script (dotnet/tests/ProvenanceTests.fsx):** exit 0,
  provenance contract passed.
- **Stderr:** empty (only the ::STDERR:: section marker; no
  transport/HTTP/upload side effects; recording HTTP + TestTransport
  are the only transport modes used).

### SHA provenance (pre vs post, byte-identical)

| Artifact | Pre-SHA | Post-SHA | Delta |
| --- | --- | --- | --- |
| infrastructure/release/Suite.ps1 | 8E260BC6…BEC26DFC | 8E260BC6…BEC26DFC | stable |
| infrastructure/release/tests/Tests.fsproj | 601FADDF…DB6DF80 | 601FADDF…DB6DF80 | stable |
| infrastructure/release/tests/Fixtures.json |  F32E8C1…F6762A |  F32E8C1…F6762A | stable |
| infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll | 845D2D8F…269DB91C | 845D2D8F…269DB91C | stable |
| infrastructure/release/tests/bin/Release/net11.0/ReleaseRunner.dll | FFB65CEB…2430570B | FFB65CEB…2430570B | stable |
| infrastructure/release/tests/obj/Release/net11.0/Release.Tests.dll | 845D2D8F…269DB91C | 845D2D8F…269DB91C | stable |
| infrastructure/release/tests/bin/Release/net11.0/Release.Tests.pdb |  DE0D21F…CC73E51 |  DE0D21F…CC73E51 | stable |
| Mcp.Dotnet.Tests.dll (producer, not rerun) | C27DA25A…A140D4 | C27DA25A…A140D4 | stable |

The compiled Release.Tests.dll byte-identical pre vs post this run;
prior cycle SHA F9CDDC7F…E3BE150 is the prior-build marker
(this build is the new head, not a repeat).

### Delta from prior cycle (75 / 10f / 1e → 84 / 2f / 0e)

The Simplification fixes advanced the gate from 75 passed / 10 failed
/ 1 errored to **84 passed / 2 failed / 0 errored** (11 red → 2 red;
9 cases turned green, none regressed):

- **W5 Integrity** (previously rrored) → PASS.
- **Handoff.readCandidate canonical positives (both producers)**,
  **readCandidate binds source SHA to verified bytes**, and
  **preserves canonical component/revision/SDK identity** → PASS.
- **Coherence.the production Coherence.ps1 rejects roster policy and
  inventory drift** (Alternate.fsx CLI registration guard) → PASS.
- **Telegram.release participant isolation** 1-of-4 sub-bullet
  (regex loop, Config.json no phantom prefix) → PASS
  (telegram-isolation-tests sub-suite moved 23 passed / 1 failed →
  24 passed / 0 failed).
- **Authority.gate rejects unauthorized candidates before any HTTP**
  and **Authority.authorized transport invokes the canonical URL
  without a v prefix** (child-probe regressions, 6 + 2 child probes)
  → PASS.
- **Evaluate legacy regression assertions** stayed RED, but with a
  different diagnostic:
  candidate directory was rejected by readCandidate: candidate
  directory must contain exactly the producer archive and
  distribution.json: expected exactly [v1.0.4.zip; distribution.json],
  got [abs.zip; distribution.json] at Evaluate.fs:123.
- **Handoff.readCandidate rejects an inner producer archive without
  its embedded manifest** stayed RED, but with a different diagnostic:
	he inner archive rejection identifies its missing canonical manifest
  entry: candidate producer archive is missing distribution.json.
  Actual value was false but had expected it to be true. at
  Handoff.fs:276.

Both remaining reds are test-contract mismatches in the
Handoff/Evaluate boundary, not production regressions. The
production-side fixes (no nested ZIP, raw-byte equality between
bounded producer ZIP and the standalone manifest, roster-vs-roster
wiring, regex-loop Config.json phantom-prefix elimination) are the
gains reflected in the 9 newly-green cases.

### No-retry / no-edit / no-network guarantee

- One Suite.ps1 invocation only (117.69 s of 600 s budget).
- One --list-tests dry-run only (exit 0, 86 cases enumerated).
- No dotnet build; pre/post SHAs byte-identical.
- No source/test edits; no git operations; no commits, pushes,
  branch creation, or thread resolution. HEAD unchanged.
- No HTTP/credentials/publisher/probe environment values read. The
  credential env names (MCP_RELEASE_DO_UPLOAD, GITHUB_TOKEN,
  GITHUB_ENV, GITHUB_OUTPUT, MCP_RELEASE_PUB_TOKEN,
  MCP_RELEASE_PROBE_TOKEN) were removed by NAME only; no values
  were read or transmitted. The compiled runner child probes are
  internal child probe invocations in the test host with dummy
  children (no real network/API/upload); recording HTTP and
  TestTransport are the only transport modes that ran.

### Sub-suite breakdown

- historical-bootstrap-tests — passed=21 failed=0.
- invocation-projections — passed=3 failed=0.
- metadata-tests — passed=52 failed=0.
- publish — passed=62 failed=0.
- evision metadata — passed=22 failed=0 skipped=0.
- producer PE integration — passed=2 failed=0 notRun=0.
- 	elegram-isolation-tests — passed=24 failed=0.

### Output

- Log: C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Finalgate.log
  (UTF-8 no BOM, LF, 4 659 B, 62 lines).
- Counters: C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Finalgate-counters.json.
- Pre-SHA: C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Finalgate-preshas.json.
- Post-SHA: C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Finalgate-postshas.json.
- Inventory list: C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Finalgate-inventory.txt.

### Residual risks

- **2 red cases** (Evaluate test-contract mismatch on producer
  archive name bs.zip vs expected 1.0.4.zip; Handoff test
  asserting Actual=false vs xpected true for inner-archive
  missing-manifest rejection). Both are bounded evidence for the
  source owner; no patch was attempted by this run.
- **Working tree dirty (55 lines)** — Simplification refactor not yet
  committed; unchanged from prior cycle.
- **No final clean-SHA packaging verification, no independent review
  rerun, no Acceptance Audit** — out of scope for this cycle.
- **Mcp.Dotnet.Tests.dll SHA C27DA25A…A140D4** byte-identical
  to the prior cycle and not rerun (155-case PASS claim preserved
  per cycle plan).


# Suite.ps1 actual run (Passgate, dirty baseline `18146a7` DLL `558C331D`)

Status: **single, non-publish** `pwsh -NoProfile -Command { & infrastructure/release/Suite.ps1 -Configuration Release -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures }` invocation against the post-Simplification dirty baseline `18146a7dca58a1f08ca58b6407ccbb2e244a9395` (GHI-24: finalize orchestration comments and verification record). The compiled DLL `Release.Tests.dll` SHA-256 `558C331DE83895A2C6A020379C300DA0DF2FCBF11B6CFF360866E955F778DF69` (769 536 B) was not rebuilt by this run (pre == post). The historical-root descriptor is unchanged (`Fixtures.json` 843 B, SHA-256 `0F32E8C1...`, Dotnet v1.0.3 + Workflow v1.0.4) and remains the same repo-pinned data set. `MCP_RELEASE_HISTORICAL_ROOT` is set to the `ghi24-current-fixtures` root (no remote download).

## Identity / provenance (this run)

| Field | Value |
| --- | --- |
| Git HEAD | `18146a7dca58a1f08ca58b6407ccbb2e244a9395` (`sourceDirty18146` - user-approved `Simplification.md`) |
| Working tree | 55 modified/added/deleted entries (dirty source/API; not a release-ready working tree) |
| Suite.ps1 entrypoint | `infrastructure/release/Suite.ps1` (pwsh 7) |
| `MCP_RELEASE_HISTORICAL_ROOT` | `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures` |
| `Release.Tests.dll` SHA-256 (pre == post) | `558C331DE83895A2C6A020379C300DA0DF2FCBF11B6CFF360866E955F778DF69` (769 536 B; not rebuilt by this run) |
| `Suite.ps1` SHA-256 (pre == post) | `8E260BC642F10F0D9FA47F6F8BD0E7F73ABCC68FBA0510251215BBD6BEC26DFC` |
| `Fixtures.json` SHA-256 (pre == post) | `0F32E8C1CC1C1ADDE4AE4E804F4F0529F49B6D8C151DFA9079CF9F9FF5E6762A` |
| `Dotnet.zip` SHA-256 (pre == post) | `DA3ACB20007F8015E541EE7D68B48ADF49D232C4EEA41190A62073073D1A1614` (1 289 835 B) |
| `Workflow.zip` SHA-256 (pre == post) | `65D274056951C64D4EAFEF2ED7242F2CF02005E639D91F2A2A9B85B921E53313` (1 514 420 B) |
| Inventory dynamic (`--list-tests` `compiled release regressions.*`) | **86** (including 1 `Historical`) |
| Wall-clock | **103.6312365 s** of 600 s budget |
| Suite.ps1 native exit | **`0`** (gate passed) |
| Expecto native exit | **`0`** (86 passed, 0 ignored, 0 failed, 0 errored) |
| Stderr lines | `0` |
| `::error::` from gate | `0` (only `::group::` / `::endgroup::` markers) |
| `GITHUB_ENV` / `GITHUB_OUTPUT` writes during gate | `0` (Suite.ps1 `Get-OutputState` diff verified) |
| Provenance script native exit | `0` (`dotnet/tests/ProvenanceTests.fsx` -> `provenance contract passed`) |
| Log files (UTF-8) | `ghi24-simplified/Passgate.log`, `Passgate-preshas.json`, `Passgate-postshas.json`, `Passgate-drift.json`, `Passgate.stdout.tmp`, `Passgate.stderr.tmp` |

## Suite.ps1 counters (single run, no retries, no rebuild)

| Counter | Value |
| --- | --- |
| `release.Tests` Expecto total cases run | **86** |
| Passed (Expecto) | **86** |
| Ignored (Expecto) | **`0`** (no case excluded, ignored, or skipped) |
| Failed (Expecto) | **`0`** |
| Errored (Expecto) | **`0`** |
| Historical case | **1** (passes; 21 nested `historical-bootstrap-tests` assertions) |
| Producer script (`dotnet/tests/ProvenanceTests.fsx`) | **1** passed, native exit `0`, last line `provenance contract passed` |
| Suite wrapper exit | **`0`** |
| Inventory vs Expecto summary parity | **`86 == 86`** (parity proven; no drift) |
| Budget used | **103.6 s** of 600 s (under-budget) |

## Sub-suite sums (nested, all passed)

| Sub-suite | passed | failed |
| --- | --- | --- |
| `evaluate-tests` | 68 | 0 |
| `historical-bootstrap-tests` | 21 | 0 |
| `invocation-projections` | 3 | 0 |
| `metadata-tests` | 52 | 0 |
| `publish` | 62 | 0 |
| `revision metadata` | 22 | 0 (skipped 0) |
| `producer PE integration` | 2 | 0 (notRun 0) |
| `telegram-isolation-tests` | 24 | 0 |

The compiled Expecto summary still totals **86 passed / 0 ignored / 0 failed / 0 errored**; the sub-suite sums are nested per-case assertions emitted inline by Expecto during the run. No standalone Workflow behavioral scripts are claimed as covered by this compiled Suite - they remain a separate verifier.

## Inventory (compiled, dynamic, 86 unique cases)

Inventory is the live `--list-tests` enumeration under `compiled release regressions.*`; the count is dynamic and not assumed (86 == 86 vs the Expecto summary). The list of test names by prefix cluster matches the prior `18146a7` baseline; nothing changed in the inventory between the prior `Finalgate.log` (DLL `845d2d8f...`) run and this run because no source was edited by the tester between the two Suite.ps1 invocations. The single `Historical` case continues to be present in inventory and exercised on the `ghi24-current-fixtures` HistoricalRoot.

## Failures / errors / ignored (none)

- **No failed Expecto tests** (the two prior `Finalgate.log` failures - `Evaluate legacy regression assertions` (Archive Name byte-copy: expected `v1.0.4.zip` vs produced `abs.zip`) and `readCandidate rejects an inner producer archive without its embedded manifest` (Handoff `MF typed MalformedArtifact`) - are now both PASS).
- **No errored Expecto tests** (the prior `compiled release regressions.W5 actual production transaction integrity regressions` error with `MissingArtifact "v1.0.4.zip"` is now PASS).
- **No ignored / skipped / excluded Expecto tests**; no `Ignore` / `Skip` / `Fact` / `EXCLUDE` markers in the retained stdout.
- **One negative-case JSON snippet** appears mid-output (`status: "failed"`, `phase: "config"`, rejection of `dotnet/release/Alternate.fsx`). This is the *expected* payload of the `enrollment.compiled enrollment API` regression (the roster identity guard is the assertion itself); it is **not** a suite failure and it is **not** a `::error::` from the gate. The Expecto summary totals remain `86 passed / 0 ignored / 0 failed / 0 errored`.
- **Exact failed-case names: none**. The full retained stdout in `ghi24-simplified/Passgate.stdout.tmp` contains no `[FAIL]` / `[ERR]` / `FAILED:` case-name lines.

## Source/runtime SHA drift report

All checked SHA-256s are equal pre- and post-run (no drift):

| Path | Pre-run SHA-256 | Post-run SHA-256 | Drift |
| --- | --- | --- | --- |
| `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll` | `558C331D...F778DF69` | `558C331D...F778DF69` | none |
| `infrastructure/release/Suite.ps1` | `8E260BC6...C26DFC` | `8E260BC6...C26DFC` | none |
| `ghi24-current-fixtures/Fixtures.json` | `0F32E8C1...6762A` | `0F32E8C1...6762A` | none |
| `ghi24-current-fixtures/Dotnet.zip` | `DA3ACB...1614` | `DA3ACB...1614` | none |
| `ghi24-current-fixtures/Workflow.zip` | `65D274...3313` | `65D274...3313` | none |

Working tree remained at 55 modified/added/deleted entries (no source edit). The compiled DLL was **not** rebuilt (the `Release.Tests.dll` SHA-256 is byte-identical pre and post).

## Recording HTTP / TEST Transport only (no real API/upload)

The compiled gate and the provenance script both run with `TestTransport` stubs and recording HTTP; no live network call, no real upload, no live API host hit. The descriptor's `assetUri` references `https://github.com/masterlifting/mcp-store/releases/download/<componentId>/<archive>`, which is the anonymous fixed public URL the workflow provision step now uses (no `GH_TOKEN`/`GITHUB_TOKEN`). No credential, secret, session-store, or environment value was read (only env NAMES referenced; Suite.ps1 `Get-OutputState` diff verified). Recording HTTP and `TestTransport` were the only transport modes that ran.

## No-edit guarantee (this run)

This run performed **no edits** to `infrastructure/release/tests/*`, the `Handoff` / `Authority` / `Bootstrap` / `Policy` / `Runner` source, the compiled DLL at `558C331D...F778DF69`, or any `fsx` under `workflow/tests/`. Only Tee-Object writes to `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Passgate.log`, `Passgate-preshas.json`, `Passgate-postshas.json`, `Passgate-drift.json`, `Passgate.stdout.tmp`, `Passgate.stderr.tmp`, and the documentation appendage in `.tasks/GHI-24/docs/review/Execution.md` were performed. The single Suite.ps1 invocation is the canonical proof for this task; no retries were issued after the inventory check, and the gate was not rebuilt between the pre- and post-SHA capture.

## Residual risks for the dirty Simplification baseline (Passgate)

- **Working tree dirty (55 entries)** - Simplification refactor is on the `18146a7` baseline but not yet committed; a release-build is not authorised from a working tree that contains this dirty working-copy state. The compiled gate passes against the dirty tree at the pinned DLL `558C331D...F778DF69`. Pkg5 bump and asset publication must wait for a clean tree (separate, non-tester-owned deliverable).
- **No final clean-SHA packaging verification, no new independent reviewer / Acceptance Audit dispatched** - out of scope for this cycle; unchanged from prior cycles.
- **The 75 retired compiled cases** (vs the prior 161-case baseline) remain not individually enumerated here; the 86-case inventory above is the authoritative current list and matches the prior `Finalgate.log` (`84 pass / 2 fail / 0 error`) baseline count of 86. The 2 prior failures (`Evaluate legacy regression assertions` archive-name byte-copy, `readCandidate rejects an inner producer archive without its embedded manifest` typed MalformedArtifact) are now both PASS in this run - the source owner fixed the leaf-policy / detail-contains missing-distribution path before this Passgate run. The reduction from 161 to 86 is consistent with `Simplification.md` (custom publisher bootstrap, receipts, closure abstraction retired; `Bootstrap.fs`, `Bootstrap.ps1`, `Publisher.json`, `PublisherClosure.fs` deleted).
- **`MCP_RELEASE_HISTORICAL_ROOT` points at locally pinned `v1.0.3` (Dotnet) + `v1.0.4` (Workflow) archives; the historical case legitimately re-verifies SHA-256 and size per archive and Suite.ps1 refuses to exclude cases to work around it. This run proves the local pinned archives pass; the hosted archive pinning remains a separate read-only CI blocker investigation outside this Passgate.
# Suite.ps1 + Dotnet producer Expecto actual run (current dirty baseline `18146a7`, 2026-10-14)

Status: **single, non-publish, two-BULK** execution. `dotnet dotnet/tests/Mcp.Dotnet.Tests/bin/Release/net11.0/Mcp.Dotnet.Tests.dll --no-spinner --summary` (155-case inventory, no per-case loop) plus `pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures -Configuration Release` (86-case compiled inventory + provenance script). 600 s budget fully respected; no retries, no rebuild, no source/test edits, no live network/upload. Full UTF-8 log retained at `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-simplified/Verified.log` (131 lines) plus per-stream `Verified-producer.stdout.tmp` (164 lines, exit 0), `Verified-producer.stderr.tmp` (0 lines), `Verified-suite.stdout.tmp` (31 lines, exit 0), `Verified-suite.stderr.tmp` (0 lines).

## Identity / provenance (this run)

| Field | Value |
| --- | --- |
| Git HEAD | `18146a7dca58a1f08ca58b6407ccbb2e244a9395` (GHI-24: finalize orchestration comments and verification record) |
| Working tree | dirty (per `git status --short`: 25 `M` entries covering `infrastructure/release/{Orchestration,Policy}.fs`, `Participants.json`, `README.md`, `Release.fsproj`, `Roster.ps1`, `Runner.fs`, `Typecheck.ps1`, `Update.ps1`, `tests/{Authority,Coherence,Enrollment,Evaluate,Handoff,Integrity,Invocation,Main,Orchestration,Policy,Runner,Telegram}.fs`, `tests/Tests.fsproj`; `workflow/release/{Build.fsx,Pins.fsx}`; `workflow/tests/DistributionTests.fsx`; 3 `D` entries (`infrastructure/release/tests/{Bootstrap.fs,Bootstrap.ps1}`, `workflow/release/Config.fsx`); 4 untracked `??` entries (`dotnet/release/Config.json`, `infrastructure/release/Verify.ps1`, `infrastructure/release/tests/Snapshots.fs`, `workflow/release/Config.json`)) |
| Producer Expecto DLL | `dotnet/tests/Mcp.Dotnet.Tests/bin/Release/net11.0/Mcp.Dotnet.Tests.dll` SHA-256 `C27DA25AE5F4EA521EE075E29C48B2D3405B4DA0D20787055A469660C5A140D4` (pre == post) |
| Release compiled DLL | `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll` SHA-256 `558C331DE83895A2C6A020379C300DA0DF2FCBF11B6CFF360866E955F778DF69` (pre == post) |
| Suite.ps1 SHA-256 | `8E260BC642F10F0D9FA47F6F8BD0E7F73ABCC68FBA0510251215BBD6BEC26DFC` (pre == post) |
| Fixtures.json SHA-256 | `0F32E8C1CC1C1ADDE4AE4E804F4F0529F49B6D8C151DFA9079CF9F9FF5E6762A` (pre == post) |
| Dotnet.zip SHA-256 | `DA3ACB20007F8015E541EE7D68B48ADF49D232C4EEA41190A62073073D1A1614` (pre == post) |
| Workflow.zip SHA-256 | `65D274056951C64D4EAFEF2ED7242F2CF02005E639D91F2A2A9B85B921E53313` (pre == post) |
| HistoricalRoot | `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures` (843 B `Fixtures.json` + Dotnet.zip 1,289,835 B + Workflow.zip 1,514,420 B) |
| Producer Expecto command | `dotnet dotnet/tests/Mcp.Dotnet.Tests/bin/Release/net11.0/Mcp.Dotnet.Tests.dll --no-spinner --summary` |
| Suite.ps1 command | `pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures -Configuration Release` |
| Producer Expecto wall-clock | `00:00:37.6881735` (native exit `0`) |
| Suite.ps1 Expecto wall-clock | `00:01:18.7057254` (Expecto native exit `0`, Suite.ps1 wrapper native exit `0`) |
| Budget used | 156 s of 600 s (combined wall-clock) |
| Stderr lines (both) | `0` (Producer) + `0` (Suite.ps1) |

## Suite.ps1 counters (single run, no retries, no rebuild)

| Counter | Value |
| --- | --- |
| `compiled release regressions.*` total cases run | **86** |
| Passed (Expecto) | **86** |
| Ignored (Expecto) | **0** (no case excluded, ignored, or skipped) |
| Failed (Expecto) | **0** |
| Errored (Expecto) | **0** |
| Inventory vs summary parity | **86 == 86** (no drift) |
| Historical case | **1** (passes with 21 nested-assertion `historical-bootstrap-tests` summary: passed=21 failed=0) |
| Producer script (`dotnet/tests/ProvenanceTests.fsx`) | **1** passed, native exit `0`, last line `provenance contract passed` |
| Suite.ps1 wrapper exit | `0` (final line `release regression gate passed: 86 compiled case(s) (including 1 historical) plus 1 producer script(s)`) |
| `::error::` from gate | **0** (only `::group::` / `::endgroup::` markers) |

### Suite.ps1 nested sub-suite summaries (all pass=0 fail)

| Sub-suite | passed | failed |
| --- | --- | --- |
| `evaluate-tests` | 68 | 0 |
| `historical-bootstrap-tests` | 21 | 0 |
| `invocation-projections` | 3 | 0 |
| `metadata-tests` | 52 | 0 |
| `publish` | 62 | 0 |
| Revision metadata | 22 | 0 (skipped 0) |
| Producer PE integration | 2 | 0 (notRun 0) |
| Telegram-isolation | 24 | 0 |

## Dotnet producer Expecto counters (single run, no retries, no rebuild)

| Counter | Value |
| --- | --- |
| `Mcp.Dotnet.*` total cases run | **155** |
| Passed (Expecto) | **155** |
| Ignored (Expecto) | **0** |
| Failed (Expecto) | **0** |
| Errored (Expecto) | **0** |
| Wall-clock | `00:00:37.6881735` |
| Native exit | `0` (`Success!` in Expecto summary) |
| `--list-tests` parity | 155 == 155 (full list assertion runs in this `--summary` invocation; no drift) |

### Dotnet producer cluster breakdown (live `--summary` enumeration, 155 unique cases)

| 2-level cluster | Count |
| --- | --- |
| `Mcp.Dotnet.security` | 36 |
| `Mcp.Dotnet.domain` | 24 |
| `Mcp.Dotnet.sync-over-async checker` | 20 |
| `Mcp.Dotnet.invocation` | 16 |
| `Mcp.Dotnet.controlled process execution` | 15 |
| `Mcp.Dotnet.async typed-failure regressions` | 13 |
| `Mcp.Dotnet.Dotnet schema` | 12 |
| `Mcp.Dotnet.MCP stdio host` | 11 |
| `Mcp.Dotnet.artifact quotas` | 8 |
| (matches prior `18146a7` baseline cluster breakdown — no source edit, no fixture rewrite) |  |

## Failures / errors / ignored / inventory drift (this run)

- **No failed Expecto tests** in the producer run.
- **No errored Expecto tests** in the producer run.
- **No ignored Expecto tests** in the producer run.
- **No failed Expecto tests** in the Suite.ps1 compiled-suite run.
- **No errored Expecto tests** in the Suite.ps1 compiled-suite run.
- **No ignored Expecto tests** in the Suite.ps1 compiled-suite run.
- **No inventory drift**: Suite.ps1 `86 == 86` parity proven; producer Expecto `155 == 155` parity proven.
- **No provenance-script failures**: native exit `0`, last line `provenance contract passed`.
- **One negative-case JSON snippet** appears mid-output of the Suite.ps1 stdout (`status: "failed"`, `phase: "config"`, rejection of `dotnet/release/Alternate.fsx` because it is not registered in the enrollment roster; known entries are now `'dotnet' (dotnet/release/Config.json)` and `'workflow' (workflow/release/Config.json)` after the Simplification `Config.json` migration). This is the *expected* payload of the `bootstrap-tests` enrollment regression; `bootstrap-tests` reports `passed=9 failed=0` (covered by the inner `evaluate-tests` summary line `passed=68 failed=0`). It is **not** a suite failure and **not** a `::error::` from the gate.
- **Exact failed-case names (both runs): none**. The retained stdout in `Verified-producer.stdout.tmp` and `Verified-suite.stdout.tmp` contains no `[FAIL]` / `[ERR]` / `FAILED:` case-name lines.

## Source/runtime SHA drift report (pre vs post)

All recorded SHA-256s are equal pre and post (no drift, no rebuild, no source/test edit):

| Path | Pre-run SHA-256 | Post-run SHA-256 | Drift |
| --- | --- | --- | --- |
| `dotnet/tests/Mcp.Dotnet.Tests/bin/Release/net11.0/Mcp.Dotnet.Tests.dll` | `C27DA25A…A140D4` | `C27DA25A…A140D4` | none |
| `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll` | `558C331D…F778DF69` | `558C331D…F778DF69` | none |
| `infrastructure/release/Suite.ps1` | `8E260BC6…C26DFC` | `8E260BC6…C26DFC` | none |
| `ghi24-current-fixtures/Fixtures.json` | `0F32E8C1…6762A` | `0F32E8C1…6762A` | none |
| `ghi24-current-fixtures/Dotnet.zip` | `DA3ACB…1614` | `DA3ACB…1614` | none |
| `ghi24-current-fixtures/Workflow.zip` | `65D274…3313` | `65D274…3313` | none |

Working tree remained at the prior `25 M + 3 D + 4 ??` baseline (no source edit, no fixture write).

## No-edit / no-rebuild / no-network guarantee (this run)

- **Two native invocations only**: producer Expecto (no `--list-tests`/per-case loop) + Suite.ps1 (one bulk run, no retries). Total wall-clock 156 s of 600 s budget.
- **No rebuild**: `dotnet build` not invoked; `Mcp.Dotnet.Tests.dll`, `Release.Tests.dll`, `Suite.ps1`, `Fixtures.json`, `Dotnet.zip`, `Workflow.zip` SHA-256s all pre == post.
- **No source/test edits**: `git status --short` pre-state == post-state (25 `M` + 3 `D` + 4 `??`). No `git add`, `commit`, `push`, checkout, or thread resolution performed.
- **No live network**: producer Expecto uses `TestTransport`/recording HTTP; Suite.ps1 uses `TestTransport`/recording HTTP. The only HTTP paths exercised are the compiled runner child probes (internal `child probe` invocations in the test host) and the anonymous fixed public URL referenced in the descriptor's `assetUri` field. **No `GH_TOKEN`/`GITHUB_TOKEN` was read**; the `MCP_RELEASE_DO_UPLOAD`/`GITHUB_TOKEN`/`GH_TOKEN`/`GITHUB_ENV`/`GITHUB_OUTPUT` env vars were stripped by NAME only (unknown values never read).
- **Combined retained output**: `Verified.log` 131 lines + `Verified-producer.stdout.tmp` 164 lines + `Verified-producer.stderr.tmp` 0 lines + `Verified-suite.stdout.tmp` 31 lines + `Verified-suite.stderr.tmp` 0 lines. All in UTF-8.

## Residual risks for the current dirty baseline

- **Working tree dirty (28 entries)**: the Simplification refactor is on `18146a7` but not yet committed. Both gates pass against the dirty tree at the pinned DLLs. A release-build is not authorised from a working tree that contains this dirty state; Pkg5 bump and asset publication must wait for a clean tree (separate, non-tester-owned deliverable).
- **Inventory delta** (unchanged from prior baseline): Suite.ps1 compiled regressions = 86 cases (vs 161 in the pre-Simplification baseline); producer Expecto = 155 cases (vs 150 in the pre-Simplification baseline). The reduction in the compiled suite is consistent with `Simplification.md` (custom publisher bootstrap, receipts, closure abstraction retired; `Bootstrap.fs`, `Bootstrap.ps1`, `Publisher.json`, `PublisherClosure.fs` deleted). The 5 new producer Expecto cases are the additions from the dirty refactor (likely in `async typed-failure regressions` and `sync-over-async checker` clusters).
- **No final clean-SHA packaging verification**: dirty tree is not committable; the gate is not a release verdict. Final clean-SHA packaging / committed-source verification remain pending (separate, non-tester-owned deliverable).
- **No new independent reviewer / Acceptance Audit dispatched**: reviewer/validator role hand-offs are unchanged. Owner decision needed before any next bounded coverage work.

## Exact verification commands (canonical for this run)

```
dotnet dotnet/tests/Mcp.Dotnet.Tests/bin/Release/net11.0/Mcp.Dotnet.Tests.dll --no-spinner --summary
pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures -Configuration Release
```

Exit codes: producer `0` (155/155 pass), Suite.ps1 wrapper `0` (86/86 pass + provenance `0`). No retries.
