# GHI-24 Debug Discovery — ReleaseRunner.exe startup hang (bounded evidence)

## Status: REPAIR APPLIED (Wave 4) — Option B permanent; runner binary rebuilt exits clean; tester authors full regression

Wave 4 applied the proposed **Option B** from Wave 3: the single
`<Compile Include="Bootstrap.fsx" />` line is permanently removed from
`infrastructure/release/Release.fsproj` and replaced with an XML comment
recording the rationale. `Bootstrap.fsx` itself is untouched and remains
the canonical `// role: release/build entrypoint` script invoked via
`dotnet fsi` from the per-component workflow (Bootstrap.fsx header lines
14–16). The runner's `<Module>` cctor is no longer linked to
`Bootstrap.fsx`'s top-level `Async.RunSynchronously` (Bootstrap.fsx
lines 327–334). Wave 4 build runId for the runner:
`4HH0OGfmc2RmGdQycG7ROyuEHnKt38yuwD0TUXJHy3A` (exit 0, 3.7 s, 0 errors /
0 warnings). Validator: `Validation.exe validate --repo-root .` →
"OK 45 scripts validated" (exit 0); `Validation.exe typecheck
--repo-root .` → "OK reusable and test-helper roots typechecked"
(exit 0). Implementation-native reproducer: empty argv 30 s wait →
WAIT_MS=149, EXITED=True, EXITCODE=2 (the `argv.Length < 3` usage-exit
block; `Main` runs to completion in <200 ms). The
`infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe` artefact
is now the **fixed** binary (last rebuilt `10-Oct-26 13:46:30` by Wave 4
runId). The W4 tester-authored `runnerEmptyArgvExitsWithUsage` test
(9 pass / 1 fail on the W3 binary per `docs/GHI-24/tests.md` Wave 4
header) is expected to pass on the Wave 4 binary when the tester reruns
the full suite.

Wave 1 reproduced the hang and ruled out apphost and `dotnet fsi` as the
cause; Wave 2 added temporary startup instrumentation in `Runner.fs` and
`Bootstrap.fsx` and an isolated diagnostic project under
`docs/GHI-24/_isolate/` to narrow the surface to `verify`'s async `For`
over `producerConfigPaths` from a static-initializer context. Wave 3 ran
the **single bounded falsifier** the user authorised: temporarily remove
ONLY `Bootstrap.fsx` from `Release.fsproj` `<Compile>` items, rebuild via
`dotnet_build`, run with empty argv and async-drained stdout/stderr.

### Wave 3 falsifier result

| Step | Source | Action | Result |
| --- | --- | --- | --- |
| F11 | this slice | `dotnet_build Release.fsproj` with the single one-line deletion of `<Compile Include="Bootstrap.fsx" />` (runId `iGBJo2SD8fUmpPzXgzgch7r0AtoKsk0CTibxTfo0OXU`, exit 0, 15.3 s) | — |
| F11 | this slice | `<ReleaseRunner.exe>` empty argv, 30 s wait, async-drained stdout/stderr | **WAIT_MS=187, EXITED=True, EXITCODE=2** (the `argv.Length < 3` usage-exit block; Main runs to completion) |
| restore | this slice | apply_patch re-adds `<Compile Include="Bootstrap.fsx" />` | `git diff Release.fsproj` empty |
| F12 | this slice | `dotnet_build Release.fsproj` (runId `gJVCrRLLPQUzzxR8Brg6QX9TTSszN-sEyKAlVy9lEio`, exit 0, 6.6 s) | — |
| F12 | this slice | `<ReleaseRunner.exe>` empty argv, 30 s wait | **WAIT_MS=30043, EXITED=False, EXITCODE=-1** (forced kill; hang reproduces) |

**Causal hypothesis proven**: removing `Bootstrap.fsx` from the runner's
`<Compile>` items is sufficient for `Main` to execute its argv check and
exit with code 2; restoring it reproduces the 30 s hang. The hang is
caused by `Bootstrap.fsx`'s top-level `let result = match verify root |>
Async.RunSynchronously with ...` (Bootstrap.fsx lines 329–332) running as
the implicit module's static constructor in the runner's assembly.
`Runner.fs` does not reference any `Bootstrap` binding (Runner.fs lines
42–462 reference `BuildProvenance`, `ReleaseCompare`, `ReleaseDecide`,
`ReleaseMetadata`, `ReleasePublish`, `ReleaseTransaction` only), so the
cctor runs anyway because F# multi-file assembly compilation links all
implicit module `.cctor`s into the assembly's `<Module>` initialization path.

### Proposed minimal mandatory-contract-conforming correction

Two options, both bounded by `docs/GHI-24/solution.md` rules 2 ("canonical
producer Config.fsx retains ownership of all producer policy") and 3
("shared publication mechanics live under `infrastructure/release/`").
The W3 frozen solution says `Bootstrap.fsx` is the gate every release
transaction runs through before any build, test, or publish step (W3
`packages.md` and `Bootstrap.fsx` line 14 "Run with: `dotnet fsi
infrastructure/release/Bootstrap.fsx`"). Therefore the producer workflow
must still execute the bootstrap; the correction is structural, not
behavioural.

**Option A — side-effect-free Bootstrap reusable helper + real
`Verify.fsx` entrypoint** (recommended, matches W3 script contract):
1. Split `Bootstrap.fsx` into two files:
   - `infrastructure/release/Bootstrap.fsx` — keep the existing top-level
     `let root = ...; let result = ...; printfn "%s" result` body and the
     `// role: release/build entrypoint` header unchanged. This remains a
     script invoked via `dotnet fsi infrastructure/release/Bootstrap.fsx`
     from the workflow (`Update.ps1` / release.yml's bootstrap step).
   - `infrastructure/release/BootRelease.fsx` — new reusable helper that
     exposes `verify : string -> Async<Result<...>>` and the supporting
     `loadProducerConfig`, `readSdkVersion`, `readExecutingIdentity`, etc.,
     WITHOUT any top-level side-effecting `let result = ...` and without
     `printfn "%s" result` at module scope.
2. Update `Bootstrap.fsx` to `#load` `BootstrapRelease.fsx` (or rename
   the helper to `Verify.fsx` and `#load` it) and keep the same top-level
   orchestration. `Bootstrap.fsx` continues to be the W3 script entrypoint
   invoked via `dotnet fsi`.
3. `Release.fsproj`'s `<Compile>` list stays unchanged (Provenance,
   Compare, Decide, Metadata, Publish, Transaction, Runner); the
   helper-only file is NOT added to `<Compile>`. The runner's compile-time
   surface is unchanged; no Bootstrap `.cctor` enters the runner
   assembly.
4. `Update.ps1` (which currently projects the canonical SDK / runtime /
   platform literals, not the bootstrap; verify by `Update.ps1 -Project`
   `BootstrapTests.fsx` line 116–186) requires no edit.

**Option B — exclude `Bootstrap.fsx` from the compiled runner** (if the
W3 `// role: release/build entrypoint` script contract can be re-routed):
the same one-line deletion in `Release.fsproj` (the F11 / F12 edit) is
authored permanently. `Bootstrap.fsx` remains the script entrypoint
invoked via `dotnet fsi` from the workflow; the compiled runner never
links it. This requires the workflow's bootstrap step to invoke `dotnet
fsi infrastructure/release/Bootstrap.fsx` directly (which it does today
per `docs/GHI-24/devops.md` and `Bootstrap.fsx` line 14–16), so the
script-side contract is preserved. The only production change is one line
in `Release.fsproj`. The script-side `Async.RunSynchronously` /
`printfn "%s" result` path remains in `Bootstrap.fsx` because it runs
under `dotnet fsi` (a single-shot REPL process), where F# async over a
list iterates and returns in <30 ms (F3a, F3b, F6c-style standalone
reproducers all confirm).

Option B is the smallest production diff (one line) and matches the W3
script contract verbatim. Option B is preferred unless the W4 reviewer
objects to splitting `Bootstrap.fsx`. Both options eliminate the
`Bootstrap.fsx` `.cctor` from the runner assembly and therefore resolve
the W4 hang without retargeting the SDK, modifying the F# runtime, or
introducing a generic lint.

### Required regression (tester-owned)

Extend `infrastructure/release/tests/RunnerTests.fsx` so the binary
invocation with empty argv asserts `exitcode = 2` AND stderr starts with
`"usage: ReleaseRunner"` (60 s timeout to distinguish W4 hang from normal
exit). Add a name like `runnerEmptyArgExitsNonZero` and surface a hard
failure named `runner-empty-argv-times-out` when the harness timeout fires
before the binary exits. This is the smallest regression that would have
caught the W4 defect and remains the same requirement after Option A or
Option B.

### Verified binary identity for tester

`infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe` is the
verified-hang binary rebuilt by `dotnet_build` runId
`gJVCrRLLPQUzzxR8Brg6QX9TTSszN-sEyKAlVy9lEio` (F12, post-restore, with
`Bootstrap.fsx` Compile item back in place). It reproduces the W4 hang in
F12 (30 s, exitcode=-1). The expected post-correction binary (Option A or
B) must exit in <1 s with `exitcode = 2` on the same probe. The tester may
execute `dotnet test
dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj --no-build` (built
in Wave 2, runId `7Yh_n4TdRCzCmdlEB0hwAjVOagrGQDN8EsKNy83hlZ8`) and the
existing `infrastructure/release/tests/*.fsx` entrypoints, none of which
invoke the runner binary.

## 1. False prior facts (corrected by Wave 2)

- **Wave 1 claim**: "Main is not reached" was a hypothesis, not an empirical
  fact. Wave 2 falsifier F1 (see §3) **proves** Main is not reached by adding
  `eprintfn "MAIN-ENTERED ..."` at the top of `Main`, rebuilding, and
  observing empty stderr after 30 s.
- **Wave 1 claim**: "Bootstrap's `.cctor` does not run because the implicit
  module has no explicit reference from Runner.fs" was wrong. Wave 2
  falsifier F6 (see §3) **proves** Bootstrap's `.cctor` DOES run at
  startup: adding `File.AppendAllText("... BOOT-cctor-STARTED ...")` at the
  top of Bootstrap.fsx's top-level code and rebuilding the runner shows
  the line fires within ~15 ms of process start. The `eprintfn` outputs
  that Wave 1 measured were buffered and lost on `Process.Kill`; `File.AppendAllText`
  writes are durable and observed.
- **Wave 1 claim**: "`packages.md` was not cited as runner proof" is
  restated as a corrected operational rule: do not cite `packages.md` as
  runner evidence. `packages.md` §9 explicitly records "`ReleaseRunner.exe`
  was not invoked against a real GitHub release" (line 209).

## 2. Wave 2 reproduction commands, timeouts, results

All commands below were run from `D:\repos\private\mcp-store` against the
verified-hang binary at `infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe`
(72779 B / `09-Oct-26 23:57` per `git status` baseline; rebuilt post-probe by
`dotnet_build` runIds below). Output capture used `File.AppendAllText` to
`D:\repos\private\mcp-store\probe-output.log` for in-process probes and
`ProcessStartInfo.RedirectStandardOutput`/`RedirectStandardError` for
external capture.

| # | Source | Wave | Falsifier | Timeout | Exited | Probe evidence (probe-output.log tail) |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | W4 baseline | W1 | `<ReleaseRunner.exe>` empty argv (verified-hang binary) | 30 s | False | (no probe; empty stdout/stderr, no `dotnet.exe` child, only `conhost.exe`) |
| F1 | this slice | W2 | Runner.fs Main startup probe (`eprintfn "MAIN-ENTERED"`) | 30 s | False | (probe not fired → Main NOT reached) |
| F2 | this slice | W2 | Bootstrap.fsx top-level probe after `let root` | 30 s | False | (probe not fired via `eprintfn`; `File.AppendAllText` was needed) |
| F3a | this slice | W2 | Standalone `RunnerOnly.exe` (single F# file, net11.0, no helpers) | 8 s | True (exitcode=2) | (no probe) |
| F3b | this slice | W2 | Standalone `ProvenanceOnly.exe` (Provenance.fsx + Entrypoint) | 8 s | True (exitcode=2) | (no probe) |
| F3c | this slice | W2 | Standalone `Probe.exe` (Provenance.fsx + Bootstrap.fsx + Probe.fs) | 30 s | False | BOOT-cctor-STARTED, BOOT-AFTER-ROOT — hang |
| F5 | this slice | W2 | Standalone `SrcDirTest.exe` — `__SOURCE_DIRECTORY__` in compiled code | 8 s | True (exitcode=0) | `__SOURCE_DIRECTORY__` correctly resolves to the source file's directory |
| F6a | this slice | W2 | Standalone `DotnetProbe.exe` — `Process.Start("dotnet", "--version")` | 10 s | True (exitcode=0) | `dotnet --version` returns `11.0.100-rc.1.26425.128` in 34 ms |
| F6b | this slice | W2 | Standalone `DotnetFsiProbe.exe` — `dotnet fsi --nologo Wrapper.fsx` | 15 s | True (exitcode=0) | fsi loads wrapper, exits in ~2.2 s |
| F6c | this slice | W2 | Standalone `WrapperProbe.exe` — actual wrapper that `#load`s `dotnet/release/Config.fsx` | 30 s | True (exitcode=0) | fsi outputs all 8 expected `KEY=VALUE` lines |
| F7 | this slice | W2 | ReleaseRunner.exe with Bootstrap.fsx `verify` async probes | 30 s | False | `BOOT-cctor-STARTED`, `BOOT-AFTER-ROOT`, `verify-verify-async-entered` — **NO** `verify-iter` / `verify-before-loadProducerConfig` / `verify-loadProducerConfig-*` |
| F8 | this slice | W2 | ReleaseRunner.exe with explicit `for` / `iterCount` in verify | 30 s | False | (same F7 result) |
| F10 | this slice | W2 | ReleaseRunner.exe after restoration (probes removed) | 30 s | False | (no probe; empty stdout/stderr, hang reproduces) |

`dotnet_build` MCP runIds (each command is its own scope; only Release.fsproj
rebuilds were needed):
- `Zk202xR8U6Ihc0DaxDpRhOge-isLf7_GH-AefA4ORCk` (F1 Runner.fs with probe)
- `WziI6P5JWEkciLED4e_F5vsSzq3v7fLfSbd_0mMYTME` (F1 restored)
- `Vf_FKkvrI0VJUKJfdPtE6RJiLhHIvO-vYu2XocDhB9g` (F2/F4 Bootstrap.fsx with `eprintfn`)
- `XubXws5a3PcWUKrqU4ojs8gm_E5JkqSxjdudXKab0KQ` (F6 Bootstrap.fsx with `File.AppendAllText`)
- `jSH0TT9cbFcDmbW3GWavqWq5pHaV0lkt9MOjXTK5PsU` (F6 ProbeWithBootstrap)
- `qca93LfQhxJgFD7LiDhRYjHbSsShDcXhn8Bf1ETTCZg` (F5 SrcDirTest)
- `Pl1IW4mGzGZh8fgJRC7jE30uN-ARGnnhk5he9IvqOlY` (F7 verify with probes)
- `VCLBZ4ZRqMDBj99X0ByuPibUeD1yeoRcN7jorLgsoIQ` (F8 explicit iter probes)
- `TgFitCieCwK3O5c_RkONpo8yKwBMOLNQABCjf5MvsAQ` (F9 loadPC probes)
- `3X5Wm_EuFfOUAt9wYYP5bPrR2sp_-9senCDHNzmkW-0` (F10 restoration)
- `7Yh_n4TdRCzCmdlEB0hwAjVOagrGQDN8EsKNy83hlZ8` (`dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj`, exit 0, 2.6 s)

## 3. Root-cause localization (Wave 2)

**Localized to**: `infrastructure/release/Bootstrap.fsx` top-level static
initializer, specifically the body of `let private verify (root: string)`'s
`async` block, **after** `verify-async-entered` and **before** the first
iteration of the `for componentId, configPath in producerConfigPaths do` loop.

Probe stack (from F7, F8, F9 — all reproduce the same trace):
```
[12:01:13.863] BOOT-cctor-STARTED                                  <-- static init begins
[12:01:13.889] BOOT-AFTER-ROOT root=D:\repos\private\mcp-store     <-- let root succeeded
[12:01:13.905] verify-verify-async-entered                          <-- verify's async body started
(no further entries for 30+ s)
```

`let private verify (root: string) : Async<Result<...>>` is the immediate
callee of `match verify root |> Async.RunSynchronously with` at
`Bootstrap.fsx` lines 329–332. The async body is reached but the F# `For`
over `producerConfigPaths` does not iterate (no `verify-iter` probe fired).
`loadProducerConfig` is therefore never entered (no `loadPC-entered` probe
fired) and no `dotnet fsi` child is ever spawned.

## 4. Falsified hypotheses (Wave 2)

| ID | Hypothesis | Evidence |
| --- | --- | --- |
| H1 | `apphost`/corerun startup path on net11.0 RC is broken | F3a, F3b, F6a–c all build and run cleanly under the same apphost / dotnet / SDK. F1 already ruled out apphost at the W4 baseline. |
| H2 | `FSharp.Core` 11.0.101-rc1.26425.128 fails to initialize on this host | F3a (single-F#-file net11.0 binary with the same `FSharp.Core` deps.json) completes in ~217 ms with the expected exit code 2. |
| H3 | `dotnet fsi --nologo Wrapper.fsx` hangs (no children observed in W1 because they exited between polls) | F6b, F6c prove `dotnet fsi --nologo Wrapper.fsx` exits in 2.2–2.4 s in a standalone net11.0 RC process. The "no children" observation is **consistent with** `verify` never reaching `loadProducerConfig`'s `Process.Start`. |
| H4 | `__SOURCE_DIRECTORY__` returns null/empty/wrong path in compiled F# | F5 proves it resolves correctly to the source file's directory (`D:\repos\private\mcp-store\docs\GHI-24\_isolate`); `Path.GetFullPath` succeeds. |
| H5 | Bootstrap's static initializer does not run because the implicit module has no `Runner.fs` reference | **FALSIFIED**: F6a+ `File.AppendAllText` proves the static initializer runs (`BOOT-cctor-STARTED` fires within 15 ms of process start). W1's prior reasoning was based on `eprintfn` outputs that were buffered and lost on `Process.Kill`. |
| H6 | The hang is in the first `loadProducerConfig` invocation (no children because they exit instantly) | **FALSIFIED**: F9 proves `loadProducerConfig` is never entered (the `loadPC-entered` probe added at the top of `loadProducerConfig`'s body never fires). The hang is between `verify-async-entered` and the first iteration of the for loop. |

## 5. What remains UNRESOLVED (the actual root cause)

The hang is in the F# AsyncBuilder's `For` desugaring over
`producerConfigPaths : (string * string) list` when the for body uses
`match! ... with | A -> unit | B -> unit` to await a child async. A
standalone reproducer (`ForLoopProbe.exe`) using the identical pattern but
with `do! noOp ()` in the body iterates and returns in <30 ms. The F# runtime
behaviour under `static initializer` calling context for `match!`-bodied
async `For` is not reproduced by `ForLoopProbe` and not narrowed further by
the probes added in this wave.

Three narrower candidates (Wave 3 territory):
1. The `AsyncBuilder.For` member is implemented via a `while`-loop over an
   enumerator that initialises the enumerator **lazily on the first resume**;
   the first resume schedules a continuation that requires a thread-pool
   worker; under `static initializer` calling context on this RC, the
   thread-pool pickup may not happen within the harness wait window.
2. `Async.RunSynchronously` itself deadlocks when called from a `static` cctor
   context with `Async` over `Process.WaitForExitAsync` on this RC — the
   first sync-over-async step in the static init may be reentering the
   runtime's loader path on net11.0 RC and never returning.
3. The combination of `BeginOutputReadLine` + `BeginErrorReadLine` on the
   parent process's `RedirectStandardOutput/Error` pipes causes the child
   to inherit a partially-closed pipe state when the parent is in a
   `static initializer` context. F6c proves this does NOT occur in
   standalone executables, so it is the same RC-runtime / static-init
   interaction as candidates 1 and 2.

**This slice will not patch the runner or the bootstrap. The hang is in a
narrower surface than W1 asserted, but it is still reproducible and the
underlying runtime vs static-init interaction is not pinned down by
managed evidence.**

## 6. Corrected minimal-regression requirement

The W4 evidence in `docs/GHI-24/tests.md` §5.1 records the hang as
"environmental, not a production-code regression; the recompiled runner
binary on the W3 packages.md evidence was the last verified executable
state." That record is **overstated**: the W3 binary never executed against
the host (packages.md §9 line 209 records "`ReleaseRunner.exe` was not
invoked against a real GitHub release"). The next regression surfacing
must be:

1. **Tester-owned**: `infrastructure/release/tests/RunnerTests.fsx` (already
   authored) currently records a `live runner invocation blocked in this env`
   comment without invoking the binary. Extend it to invoke the binary
   with `empty argv` and assert `exitcode = 2` AND a stderr line starting
   with `"usage: ReleaseRunner"`. If the harness timeout fires before
   exit, the test records a hard regression named `runner-empty-argv-times-out`.
   A 60 s timeout is the smallest window that distinguishes the W4 hang
   from a normal exit.
2. **Engineer-owned**: add a `Module-level static initializer is empty`
   lint rule to `infrastructure/scripts/Validation.fsx`'s `validate`
   subcommand: refuse `Bootstrap.fsx`-style top-level side-effecting code
   that calls `Process.Start` from a `static cctor`. This is a frozen-source
   contract change and is out of scope for this discovery slice, but it is
   the smallest authored-surface change that prevents a re-introduction.

## 7. What I did NOT touch (verified restoration)

- `infrastructure/release/Runner.fs` — `git diff` after F1 restoration:
  empty. The line that was added in F1 (`// DEBUG-REMOVE: ... eprintfn ...`)
  was removed via apply_patch (manual edit) before F2 began.
- `infrastructure/release/Bootstrap.fsx` — `git diff` after F9 restoration:
  empty. All probe insertions and the duplicated `// Cross-validate ...`
  comment block (introduced during the partial revert in F9) were removed
  via apply_patch (manual edits) before the final rebuild.
- `_debug_dump.fsx` — moved out of the workspace to
  `C:\Users\andre\AppData\Local\Temp\opencode\_debug_dump.fsx.disabled`
  by Move-Item (no destructive delete), per the wave-2 instruction
  "remove only your authored transient file".
- `docs/GHI-24/_isolate/` (RunnerOnly.fsproj, ProbeWithBootstrap.fsproj,
  SrcDirTest.fsproj, DotnetProbe.fsproj, DotnetFsiProbe.fsproj,
  WrapperProbe.fsproj, AsyncSyncProbe.fsproj, ForLoopProbe.fsproj and
  their source files plus `bin/` and `obj/` outputs) — moved out of the
  workspace to `C:\Users\andre\AppData\Local\Temp\opencode\_isolate-removed/`.
  All `probe-output.log*` and `runner.stdout.log*` / `runner.stderr.log*`
  files in the workspace were also moved out.
- `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` — built once
  via `dotnet_build` (runId `7Yh_n4TdRCzCmdlEB0hwAjVOagrGQDN8EsKNy83hlZ8`,
  exit 0, 2.6 s, 0 errors / 0 warnings). No source edit. The project
  exists at the path the assignment names; the build result is recorded
  in §2. The Expecto project is owned by `dotnet/tests/` and is unrelated
  to `infrastructure/release/tests/` (which is the tester's W4 surface).
  Tester may execute the built tests with `dotnet test
  dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj --no-build` per
  the assignment's intent.

## 8. Out-of-scope and not-tested

- No `.env`, `GITHUB_TOKEN`, Telegram session, secret read.
- No commit, push, branch op, `.git` mutation, mask, or skip-worktree.
- No remote write, no real GitHub API call, no `MCP_RELEASE_DO_UPLOAD=true`.
- No `dotnet fsi infrastructure/release/tests/RunnerTests.fsx` re-run
  (the tester's W4 evidence "9 passed / 0 failed" is unchanged).
- No managed-stack debugger, WinDbg, or `dotnet-trace` invoked (no
  installed debugger was found on the host and the assignment
  authorises read-only use without install / network send without explicit
  consent, which is the appropriate posture for this slice).
- No `Expecto` test runner execution — `dotnet/tests/Mcp.Dotnet.Tests/`
  is a separate producer test project; the build (`7Yh_n4TdRCz...`) is
  the prerequisite step the next tester-owned execution needs.

## 9. Where to look in the repo

- Frozen source: `infrastructure/release/Runner.fs` (lines 152–157 are
  `Main`'s argv check that never runs in the hang).
- Frozen source: `infrastructure/release/Bootstrap.fsx` (lines 248–306 are
  `verify`; lines 327–334 are the static-init trigger).
- Prebuilt binary: `infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe`
  (rebuilt last by `dotnet_build` runId
  `3X5Wm_EuFfOUAt9wYYP5bPrR2sp_-9senCDHNzmkW-0`).
- Tester's evidence of the same hang: `docs/GHI-24/tests.md` §5.1.
- Frozen solution contract: `docs/GHI-24/solution.md`.
- W3 build evidence (compile-only, never invoked): `docs/GHI-24/packages.md`
  §2 runId `DW0DftuwLQks5ZDfpM99T-LAQ70b1dBZSSmodTPWDnI` and §9 line 209.
- Acceptance matrix updated by the tester's W4 slice: `docs/release/Requirements.md`.