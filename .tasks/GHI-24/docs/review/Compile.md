# Test-project compile correction

The test-project build `OvQXB8YAJq3-JHSQvKJZzYgPqxZb56veuEhME7ulFjk` failed
with two identical FS3373 error entries and zero warnings.
`infrastructure/release/tests/Runner.fs` line 100 triggered the error because
its interpolated argument contained a nested string literal in
`String.replicate 64 "a"`.

The test now computes the same 64-character hash value in a local binding and
interpolates that binding, preserving the exact CLI argument and existing
assertions. No test expectations or release matrix entries changed. The source
fix is pending the engineer-owned rebuild; no build or test was run here.

| Build | runId | Status | exitCode | errors | warnings | snapshot |
| --- | --- | --- | --- | --- | --- | --- |
| post-fix `Tests.fsproj` Release, dedicated Dotnet MCP, exactly one invocation | `4KJ8u0iHMnDubYI26QNv6VAryndp17nHvSCDi_5bw7w` | failed | 1 | 6 (FS0003 `Policy.fs(120,7)`, FS0039 `Orchestration.fs(63,74)` "ReleaseError is not defined", FS0001 `Orchestration.fs(150,69)` StageFailure string-mismatch — each emitted twice; `Runner.fs` line 100 no longer reported) | 0 | unchanged during run (git status --porcelain before/after equal) |

The test sources now separate the policy test-list cases explicitly, qualify the
release error formatter through `BuildProvenance`, and apply the stage-failure
helper with the current fixture component. Existing test cases and assertions
are unchanged. This source correction is pending the engineer-owned rebuild; no
build or test was run here.

The pending status above records the earlier handoff state; the current build
confirmation below supersedes it.

## Current Release test-project build

The coordinator supplied the current Release build evidence for
`infrastructure/release/tests/Tests.fsproj`; Dotnet MCP details confirmed zero
errors and zero warnings. The tester consumed the already-built assembly and did
not build.

| Build | runId | Status | exitCode | errors | warnings | snapshot |
| --- | --- | --- | ---: | ---: | ---: | --- |
| current `Tests.fsproj` Release build, coordinator-supplied Dotnet MCP | `pQPcot3En7JLL-xoYYygsvdwatgGqXPCBIPp1ve46r8` | succeeded | 0 | 0 | 0 | compiled assembly SHA-256 recorded in `Execution.md` |

## Pending runner-test build

`infrastructure/release/tests/Runner.fs` now passes the three mandatory external
receipt paths and writes a valid FetchCandidate v2 fixture (`mode=FetchCandidate`,
`role=candidate`, non-null `component=dotnet`). The regression requires the exact
typed missing-closure diagnostic, so a generic exit code 4 or a CLI-guard failure
cannot satisfy it. The source update has not been built or tested in this handoff.

| Build target | Status | Evidence |
| --- | --- | --- |
| `infrastructure/release/tests/Tests.fsproj` Release | pending engineer-owned build | edited test source is not yet compiled |

## Engineer-owned Release test-project build

The engineer ran the build through the dedicated Dotnet MCP exactly once on
`infrastructure/release/tests/Tests.fsproj` in `Release` configuration. No F#
project files, producer code, Build.ps1 serialization, or test sources were
edited; `mayEdit=false`/`mayTest=false`/`mayDelegate=false` held. Pre-build and
post-build SHA-256 snapshots of the 20 test-project source files match
(`.tasks/GHI-24/docs/review/Compile-snap-pre.txt` vs `Compile-snap-post.txt`),
so no external change landed during the run; the pre-existing
`git status --porcelain` set (10 modified + 3 untracked) is unchanged before
and after, confirming zero stale evidence and zero source drift introduced by
the MCP invocation. Receipt flags: Release, 0 errors / 0 warnings. No separate
producer build, raw `dotnet` fallback, test execution, live API call,
environment-secret read, network call, state mutation, commit, or foreign
checkout was performed.

| Build | runId | Status | exitCode | errors | warnings | snapshot |
| --- | --- | --- | ---: | ---: | ---: | --- |
| `Tests.fsproj` Release, dedicated Dotnet MCP, exactly one invocation | `fNOlcu9nPW0PAgxSYBs8Jz_FgdW7l5ugpH9cc4wOLf4` | succeeded | 0 | 0 | 0 | pre/post SHA-256 equal across all 20 test-project files; `git status --porcelain` unchanged before vs after the run |

Known CI READINESS red signal: the engineer-owned build itself is green (0
errors, 0 warnings, 0 source drift) and the produced assembly is ready for the
tester. The pre-existing READINESS red surfaces (modern checked-in
workflow/drift/readiness with publish disabled; production `Receipts.md` empty;
runner-test fixture readiness) are owned by their respective test source
owners (Coherence, Receipts, Runner) and are out of scope for the build
contract. The test sources covering Handoff missing-entry assertion,
Orchestration Git-readonly cleanup, Runner new receipt flags + fixtures,
Coherence modern checked-in workflow/drift/readiness (must RED with publish
disabled), and Telegram modern markers are all compiled clean by this build
and are not ignored.
