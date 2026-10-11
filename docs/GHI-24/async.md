# Actual producer regression blocker

The rebuilt Expecto entrypoint reports 150 tests, 149 passing and one failing:
the repository-wide active-source sync-over-async scan. The dedicated
`dotnet_test` call returned exit 0 with null counts and no output/TRX; that result
does not demonstrate that the Expecto executable ran. Earlier exit-zero claims
are superseded by the actual entrypoint result.

## Observed diagnostics

The actual scan reports 118 blocking matches:

- Four process waits in each new producer `release/Release.fsx` wrapper.
- One unregistered top-level bridge in release `Bootstrap.fsx`.
- Nine blocking bridges in release `Runner.fs`.
- The remainder in the new release tests: per-case blocking bridges, synchronous
  process waits/stream drains and an awaiter-result bridge.

The read-only FCS probe also reports FS0001 at both wrappers' no-argument
`WaitForExit()` assignments used as booleans. Isolated probes distinguish the
void overload from the timed boolean overload. This is a static compilation
blocker; no actual publishing wrapper was executed during investigation.

Raw evidence is retained under
`C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-debug\`:
`repo-surface-failure.log`, `fcs-probe.log`, `fcs-min-probe.log` and diagnostic
summary/probe files. No source, checker, test or whitelist changes were made in
that investigation.

## Baseline correction

`3612e8e` is this task's first checkpoint, not the original main baseline.
The wrappers and `infrastructure/release/` did not exist at original main
`30d82304e05899e2b1c83d9255fbe2f17abc0b7f`, verified with `git ls-tree`.
These failures belong to the new implementation; they cannot be dismissed as
pre-existing main failures.

## Required disposition

Do not weaken the checker, add broad exclusions, count unexecuted tests as passed,
or proceed to Acceptance Audit/readiness while this required gate fails. The SDK
revision-suppression repair itself passes its new regression, but does not repair
these async/typing defects. Additional source/test/gate work requires an explicit
bounded re-plan beyond the previously authorized two-property repair. F1–F9's
historical targeted verdicts remain recorded, but are not proof of whole-task
acceptance or functioning publication entrypoints.

## D4 user-authorized async/typing repair

The D4 authorization covers the four source artifacts in `release/` and the
single shared helper. Repair facts at HEAD `962fd64…` on branch `ghi-24`:

- `infrastructure/release/Capture.fsx` (new, 84 lines) — `ReleaseCapture.runProcessCapture`
  reuses the canonical `BuildProvenance` async pattern (concurrent stdout/stderr
  reads, exit captured before `Dispose`, kill+await cleanup on cancellation) and
  returns `Async<Result<int * string * string, ReleaseError>>`. It is a helper,
  contains zero `WaitForExit(` / `ReadToEnd(` / `Async.RunSynchronously` matches,
  and is compiled into the runner via `Release.fsproj` `<Compile Include>`.
- `dotnet/release/Release.fsx` and `workflow/release/Release.fsx` (rewritten) —
  the four `child.WaitForExit()` synchronous waits were the FCS FS0001 void→bool
  blocker; both wrappers now `#load "../../infrastructure/release/Capture.fsx"`
  and run a single `async { … }` pipeline, each with exactly one documented
  `Async.RunSynchronously` bridge at the script entrypoint. The structured JSON
  failure emission runs before the async returns so the wrapper still fails
  closed before the runner is invoked.
- `infrastructure/release/Bootstrap.fsx` (one-line edit) — the existing bridge
  at line 290 now carries the `// Standalone entry bridge` source-level marker
  so a whitelist entry registered by the tester later will satisfy the
  documented-bridge exemption.
- `infrastructure/release/Runner.fs` (rewritten) — all nine `Async.RunSynchronously`
  call sites collapsed into a single `Result<unit,int>` async chain inside
  `main`. The helper `requireObserved` collapses the three "observed-sha
  missing" failure paths into one shape. `emitResolution` no longer calls
  `exit` so the async chain carries the exit code through its return value;
  the single remaining `Async.RunSynchronously` is the entrypoint bridge
  documented in-place. Build evidence: `infrastructure/release/Release.fsproj`
  compiles 0 errors / 0 warnings under `dotnet_build` MCP runId
  `O5aEYOJoK_uCMzIYH6v6aOjmOn3eX_DqqYGz0y3qvco`; producers `Mcp.Dotnet.fsproj`
  and `Mcp.Workflow.fsproj` rebuild 0/0 under runIds
  `8v_dBmknIZLyvGbfUNPr5ucpTwjWATMj7BGq3VxPWlA` and
  `MNMGiAqN5HvbJ-6qB00F9sB2n0x_o2S2OHseKp-4kKE`.

### Source-level bridge count (after repair)

| source file | `Async.RunSynchronously` | `WaitForExit(` | `ReadToEnd(` | other regex hits |
| --- | --- | --- | --- | --- |
| `infrastructure/release/Capture.fsx` | 0 | 0 | 0 | 0 |
| `dotnet/release/Release.fsx` | 1 | 0 | 0 | 0 |
| `workflow/release/Release.fsx` | 1 | 0 | 0 | 0 |
| `infrastructure/release/Runner.fs` | 1 | 0 | 0 | 0 |
| `infrastructure/release/Bootstrap.fsx` | 1 | 0 | 0 | 0 |

Every remaining bridge is at a true standalone process / script entrypoint,
each carries the documented source-level marker, and each will need a
registry entry owned by the tester in the next step.

### FCS-only probe evidence (read-only, no eval)

A typed FCS probe (`Temp/opencode/ghi-24-debug/wrapper-fcs-probe.fsx`,
`ParseAndCheckProject` via `FSharp.Compiler.Service` with `useSdkRefs=true`,
`useFsiAuxLib=true`, `assumeDotNetFramework=false`) processes each wrapper's
`#load` graph (Config.fsx → Provenance.fsx → Capture.fsx → wrapper) and
returns real diagnostics: zero errors, zero FS0001, zero pattern matches
outside comments for all four entrypoints plus the helper. The original
FS0001 void→bool site is gone. Probe retained the do-block of each script
in the type-checked project but never invoked the script's compiled
entrypoint, so no wrapper or runner produced any side effect. Captured
counts:

```
==== dotnet wrapper    ====   referencedSources=4   errors=0 FS0001=0
==== workflow wrapper  ====   referencedSources=4   errors=0 FS0001=0
==== Capture helper    ====   referencedSources=2   errors=0 FS0001=0
==== Bootstrap         ====   referencedSources=2   errors=0 FS0001=0
```

### Build evidence (`dotnet_build` MCP, Release config, 0/0, latest run)

| target | runId | errors / warnings |
| --- | --- | --- |
| `infrastructure/release/Release.fsproj` | `hrjAxyVwHsdRvn3u_FEKmDE1kamaCYxUVx-VmsoJg_8` | 0 / 0 |
| `dotnet/Mcp.Dotnet.fsproj` | `X0qXHnfa6xq87tPf2REaJlIEaWQD4buoxuLcjjt2V9w` | 0 / 0 |
| `workflow/Mcp.Workflow.fsproj` | `7RsOcQvCPF3Zi29GqCin3JBp0txCTVqBzIKrDbYAxNY` | 0 / 0 |
| `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` | `ztLTZtLdAJyQudqypOwONwAyn9ovQNEjbTx5IKri2hc` | 0 / 0 |

### Analyzer evidence (existing `SyncOverAsyncChecker.checkText`, read-only)

Run via `Temp/opencode/ghi-24-debug/analyzer-check.fsx`. No test edits, no
whitelist edits. The bridge comments carry the canonical
`Standalone entry bridge` phrase so the analyzer's documented-bridge
exemption logic will activate once tester registers the four entries in
`topLevelBridgeSites`:

```
dotnet wrapper    blocking=1 allowed=0   <- one bridge at line 215 (tester-managed exemption)
workflow wrapper  blocking=1 allowed=0   <- one bridge at line 215 (tester-managed exemption)
Capture helper    blocking=0 allowed=0
runner compiled   blocking=1 allowed=0   <- one bridge at line 683 (tester-managed exemption)
Bootstrap         blocking=1 allowed=0   <- one bridge at line 293 (tester-managed exemption)
TOTAL             blocking=4 allowed=0
```

The four BLOCK results match the four documented standalone entry bridges;
after the tester's whitelist step they become ALLOW without any further
source or analyzer change. No `WaitForExit(`, `ReadToEnd(`,
`Task.Result` / `Task.Wait(`, `GetAwaiter().GetResult`,
`Task.WaitAll`, or `Task.WaitAny` regex pattern matches anywhere in the
five files; FS0001 (the original void→bool blocker) is gone.

### Capture helper – deadline + cancellation semantics

`ReleaseCapture.runProcessCapture` (Capture.fsx) combines a default
30-minute `CancellationTokenSource` with the caller-supplied token so a hung
child is terminated and the buffers drained before the deadline fires; the
caller's `Async.CancellationToken` still wins when shorter. Stdout and stderr
are read with `ReadToEndAsync` and awaited via `awaitOperational`
concurrently, the exit code is captured from `child.ExitCode` into a
mutable local before the implicit `use` triggers `Dispose`, and a
cancellation path kills the child, awaits `WaitForExitAsync()` again without
the token, drains both pipes, then re-raises `OperationCanceledException`.
None of `WaitForExit(`, `ReadToEnd(`, `Task.Result`, `Task.Wait(`, or
`GetAwaiter().GetResult` appear in Capture.fsx.

## D4 tester-owned async conformance

At HEAD `962fd646466158afc255311ec9de3746d3c35952` (branch `ghi-24`), the
tester changed only `infrastructure/release/tests/*.fsx`, exact documented
entry-bridge registrations in
`dotnet/tests/Mcp.Dotnet.Tests/SyncOverAsyncChecker.fs`, and this evidence.
F1–F9 behavior and historical targeted verdicts remain frozen. No release
implementation, YAML, PowerShell, package, build, remote, or `.git` state was
changed by this tester slice.

The initial repository-surface capture above had 118 blocking matches. The
owner's production async repair accounts for the wrapper, Bootstrap, and Runner
sites. In the test surface, per-case `Async.RunSynchronously` calls now compose
through `async` suites with `let!`/`do!`; process capture uses bounded
`WaitForExitAsync` with concurrent `ReadToEndAsync` drains and kill/await
cleanup; the scripted HTTP handler's `SendAsync` body awaits content reads.
No `Task.Run`, helper bridge, blanket exemption, or false PASS was introduced.

Each of the ten test entrypoints (`Bootstrap`, `Compare`, `Evaluate`,
`Historical`, `Invocation`, `Metadata`, `Publish`, `Revision`, `Runner`, and
`Telegram`) has one documented final bridge. Both test helpers (`Fixture.fsx`
and `Process.fsx`) have zero bridges; `Process.fsx` declares the `test helper`
role and owns bounded asynchronous process capture. The checker received only
exact source registrations for the two producer wrappers, Bootstrap, compiled
Runner, and the nine newly bridged test entrypoints; the existing Revision
registration was retained. The exact registered paths and source lines are in
`docs/GHI-24/tests.md` §11. Checker matching and exemption behavior were not
changed.

### Current test evidence

The deterministic `Suite.ps1` run completed all nine scripts with **272
passed, 0 failed, 0 skipped**; every existing assertion was retained and no
assertions were added. The `GITHUB_ENV` and `GITHUB_OUTPUT` sentinel targets
remained absent. Per-script counts and the optional archive integration are
recorded in `docs/GHI-24/tests.md` §11. The optional `Historical.fsx` run also
completed **10 passed, 0 failed** using the local v1.0.3 archive directory.
`Revision.fsx` reported **22 passed, 0 failed, 0 skipped**, plus its separate
read-only producer-PE check **2 passed, 0 failed, 0 not-run**.

The current checker source and registry were executed directly from FSI against
the working tree: **58 findings, 0 blockers**. This is source-level evidence
only, not proof that the compiled Expecto gate used the updated registry. The
checker source changed, so the Engineer must rebuild
`dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` before the tester runs
the real Expecto entrypoint. No `dotnet_test` null-count result is substituted
for that run; the compiled Expecto result remains pending that rebuild.

Tester source-set digest (SHA-256 over sorted relative-path / `git hash-object`
lines for the checker and changed test scripts):
`993577b668a2c9c5f4abf3668dc321f6a82028f5b521a91636144d3ccc0d785e`.

## D4 CI gate verification (DevOps, read-only, appended — other writers preserved)

Scope: `.github/workflows/release.yml` (read-only `tests` job only), one new
one-word helper `infrastructure/release/Typecheck.ps1`, and
`infrastructure/release/README.md`. No production F#/test edit, no `.git`/
commit/branch, no secret read, no runtime edit, no upload or token use.

The read-only `tests` job (still `permissions: contents: read`) gained three
steps before the existing `Suite.ps1` step and before the `release` write job:
build both producers + the compiled test project, run the actual compiled
Expecto entrypoint (`dotnet run --project dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj -c Release --no-build`, native exit, no `--ignore`/
filter/result rewriting), and strict FCS typecheck both producer Release
entrypoints and their full `#load` closure via `Typecheck.ps1`. The existing
272-assertion suite is retained; the `release` job still `needs: [bootstrap, tests]`.

### Portable one-word helper (`infrastructure/release/Typecheck.ps1`)

Resolves the canonical SDK from `dotnet --list-sdks` (no SDK table, no
hard-coded developer path), then runs that exact SDK's
`FSharp/fsi.dll` against a probe generated under `$RUNNER_TEMP` — outside the
repository — so the probe's SDK `#r` reference never enters the in-repo
analyzer surface. Targets are read and `ParseAndCheckProject`-ed as text only
(`useSdkRefs`/`useFsiAuxLib`, `assumeDotNetFramework=false`); the entrypoints
are never evaluated. The existing `Validation` typecheck covers only reusable/
test-helper roots; this dedicated check adds no framework references and does
not change analyzer behavior.

### Positive compiler check (source correct now)

```text
$ pwsh infrastructure/release/Typecheck.ps1 -SdkVersion 11.0.100-rc.1.26425.128
typecheck sdk=11.0.100-rc.1.26425.128 fcs=<sdk>\FSharp\FSharp.Compiler.Service.dll probe=%RUNNER_TEMP%\ghi24-entrypoint-typecheck.fsx
dotnet/release/Release.fsx referencedSources=4 errors=0
workflow/release/Release.fsx referencedSources=4 errors=0
typecheck total_errors=0 missing_targets=0 targets=2
EXIT=0
```

### Negative compiler check (strictness counterexample)

A malformed fixture (RUNNER_TEMP only) reproducing the former
`let exited = child.WaitForExit()` unit→bool regression:

```text
$ pwsh infrastructure/release/Typecheck.ps1 -Targets <RUNNER_TEMP>\malformed.fsx
<...>\malformed.fsx referencedSources=1 errors=1
  E1 L6C8 This expression was expected to have type     'bool'     but here has type     'unit'
typecheck total_errors=1 missing_targets=0 targets=1
EXIT=1
```

A missing target also fails closed (`missing_targets=1`, `EXIT=1`); a
zero-target run is rejected before the probe runs. This is a real
type-check, not a parse or a 0-count read of output.

### Static/config validation (this slice)

- PyYAML `safe_load`: parses; trigger `push`/`main` only; `concurrency` =
  `group: release-main`, `queue: max`, `cancel-in-progress: false`; default
  `contents: read`, `contents: write` only on the `release` job; one
  `GITHUB_TOKEN` binding, only on the release transaction step; one
  `MCP_RELEASE_DO_UPLOAD: "true"`.
- All 15 embedded `run:` blocks parse with
  `[Parser]::ParseFile` after GitHub-expression substitution.
- `Validation.exe validate --repo-root .` → `OK 51 scripts validated`;
  `Validation.exe typecheck --repo-root .` → `OK reusable and test-helper roots typechecked`.
- No `.fs`/`.fsx` `#r` directive in the repository tree (generated `#r` lives in
  RUNNER_TEMP only).
- `git diff` on `release.yml` is exactly the one-line header-comment refresh plus
  the three new `tests` steps; every other path (`ref: ${{ github.sha }}`,
  ancestry compare, `queue: max`, token-only write boundary, typed failure
  metadata artifacts, no Telegram) is unchanged.

### No effects

The probe is written only under `RUNNER_TEMP`; the repository was not written
except for `release.yml`, `Typecheck.ps1`, `README.md`, and this section. No
`Release.fsx` was evaluated, no token/network/upload used, nothing committed.
The compiled Expecto result (`150 run, 150 passed`) is retained as the
user-supplied rebuilt fact; this slice did not re-run the test suite and does
not substitute a `dotnet_test` null-count result.
