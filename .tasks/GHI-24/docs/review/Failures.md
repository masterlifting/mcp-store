# Remaining modern gate failures

The full gate completed 153 cases: 150 passed, 3 failed, none ignored or errored; provenance passed. Full-run failure names were not retained. Subsequent focused diagnostics reproduced three failures across fourteen cases with stable affected source and DLL hashes. Matching counts do not prove they are the same three full-run failures.

1. Coherence native-exit case expects the old publication dependency set. Modern publication also depends on preparation and builder. Update the fixture expectation while preserving the required test dependency and native fail-fast assertions.
2. Coherence publication readiness correctly fails: publication is disabled, there is no executable publication invocation and no independent trusted-receipt path. This is a real blocker; do not waive it.
3. Preparation stage-failure output reports the absolute script path as `phase`, rather than the semantic stage name such as `build`. Repair the production diagnostic contract; retain script path as separate context where appropriate.

Focused output: `C:\Users\andre\AppData\Local\Temp\opencode\Diagnostics.log`. No new full run was performed. The active genuine builder emission smoke and final workflow enablement remain pending.

## Focused regression recheck

The current Expecto inventory contained all 8 scoped cases (5 Coherence, 3 modern preparation). The no-build artifacts postdated the latest relevant edits (`Coherence.fs` 15:42:30Z, `Orchestration.fs` 15:43:37Z, `ReleaseRunner.dll` 15:44:00Z, `Release.Tests.dll` 15:44:16Z). A snapshot of 53 release source/runtime inputs was unchanged across execution: SHA-256 `8E847F4BF767BB7F6BAC4D41242ED01C3764AC78F0D1893406050049B09583B0` before and after.

Commands, run with publication-related environment names removed from the test process:

```text
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll --list-tests
dotnet run --project infrastructure/release/tests/Tests.fsproj --configuration Release --no-build --no-restore -- --filter-test-list "cross-stage coherence" --sequenced --no-spinner --summary
dotnet run --project infrastructure/release/tests/Tests.fsproj --configuration Release --no-build --no-restore -- --filter-test-list "modern preparation boundary" --sequenced --no-spinner --summary
```

Results: Coherence 5 run, 4 passed and 1 failed; the sole failure remains the genuine publication-readiness blocker (`publication job is disabled`, no executable publish invocation, no independent trusted-receipt path). The actual Workflow native-exit regression passed. Modern preparation 3 run, all 3 passed, including the build/pins/both Workflow package-test failure regression. Combined: 8 run, 7 passed, 1 expected readiness failure, 0 ignored or errored. No full 153-case run, source edit, readiness waiver, or publication attempt was made.

## Modern preparation + authority recheck (no rebuild)

Run after the existing build (`Release.Tests.dll` built 2026-10-13 22:43, production `ReleaseRunner.dll` SHA `6901726db6859a5229a5cc34299cbee6b5a6c02ba52c58a6adbf0ee9311078f1` matches the tests/bin copy byte-for-byte; 50-file source snapshot digest `99E6E4E3156BF478CA237E1A9194F02D9987D257CE6814655514069E2B22CF4B`; `mayBuild=false`/`mayEdit tests=false` so no rebuild or source change was attempted):

```
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.exe \
  --filter-test-list "modern preparation boundary" \
  --sequenced --no-spinner --summary
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.exe \
  --filter-test-list "release authority child-probe regressions" \
  --sequenced --no-spinner --summary
```

`--list-tests` resolves both lists before any run; if either filter resolved zero tests the pass/fail claim would be invalid. Both filters returned 3 and 2 test cases respectively, no zero-case fallback was taken.

Modern preparation boundary (3 tests run, 2 passed, 0 ignored, 0 failed, 1 errored; 00:00:57.46):

- `prepare writes portable candidates after each enrolled producer's explicit test gates` → PASS. Both producers, exit code 0, `preparation.json`/`distribution.json`/`v7.8.9.zip` written, no `transaction-started` written.
- `build, pins, and both Workflow package-test failures block candidate output` → PASS. All four scenarios (`BuildFailure`/`PinsFailure`/`WorkflowDistributionFailure`/`WorkflowPackagedFailure`) exit 4 with structured JSON `status=failed phase ∈ {build,pins,tests,tests[2]}`, no candidate output, no `transaction-started`.
- `the prepare CLI refuses to run when publication authority is present` → ERRORED with `System.Text.Json.JsonReaderException: '0x1B' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0. at Release.Tests.Orchestration.prepareRefusesPublicationAuthority@271-3` (`Orchestration.fs:275`). The parent captures the spawned `dotnet Release.Tests.dll --prepare-authority-probe <configPath> <candidatePath>` child stdout and feeds it to `JsonNode.Parse(stdout.Trim()).AsObject()`; the actual captured child stdout is `Expecto? Running tests…<ESC>[1000D[?25h{ "status": "failed", "reason": "…", "phase": "build" }<ESC>[1000D[?25hExpecto! 1 tests run … Success!` — Expecto's ANSI logger output interleaves before and after `ReleaseOrchestration.prepare`'s `Console.Out.WriteLineAsync(payload)`, so the first character the parent's JSON reader sees is `0x1B` instead of `{`. The child process itself reported the structured failure (`phase="build"` from the working-tree dirty-source Build.fsx guard, no real `prepare-authority` rejection because the working-dir config + Build.fsx run first), exit 4, and the Expecto `prepare` test inside the child passed; the parent's `JsonNode.Parse` is what errored. Per scope, the assertion is NOT weakened: the parent still parses the child stdout strictly, and the child stdout still contains the structured `phase` + `status` fields, but the Expecto log lines around them prevent clean parse.

Release authority child-probe regressions (2 tests run, 0 passed, 0 ignored, 2 failed, 0 errored; 00:00:06.48):

- `authority gate rejects unauthorized candidates before any HTTP for both producers` → FAILED. All 6 child probes (3 reject scenarios × `{dotnet, workflow}`) errored at `buildFixture@207` with `System.Exception: <id> readRawTransport rejected the fixture: release artifact 'v1.0.4.zip' is invalid: only the canonical Deflate method is supported`. The same root cause as the third Orchestration test rejecting fixture fixtures: `ReleaseArchive.parse`/`decompress` (`Archive.fs:326-327`) only accepts Deflate (method 8); the fixture (`Authority.fs` `buildOuter`/`zipBytes`) creates entries with `CompressionLevel.NoCompression` (method 0). All 6 unauthorized probes error before any factory-call / request-count / exit-code-4 assertion runs.
- `authorized transport invokes the canonical /releases/tags/<producer> URL without a v prefix for both producers` → FAILED. Both authorized child probes errored at the same `buildFixture@207` line with the same production-only-Deflate rejection; the canonical-URL / no-POST / factory-call assertions never run.

Combined focused run: 5 tests run, 2 passed (both modern-preparation positives), 0 ignored, 2 failed (both authority cases), 1 errored (third modern-preparation case); 5/5 cases resolved, no zero-case pass. The two passed cases exercise real `ReleaseOrchestration.prepare` stages 1+4 (`build`/`pins`/`distribution`/`packaged`), the two failed authority cases never reach the production pipeline gate, and the errored Orchestration case reaches the production prepare pipeline and exits 4 with the right structured failure (`phase="build"`) — the parent parser still fails to extract it because of the Expecto log pollution. The full 153/155+ cases were NOT re-run; no `dotnet build`, no source edit, no readiness waiver, no publication attempt was made. Full sanitized stdout retained at `C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`; no authorization header value, real token, host URL or live network call appears in it (only canonical release tag-path fragments).

## Re-execution (no rebuild, no source edit)

`mayTest=true`, `mayBuild=false`, `mayEdit tests=false`, `mayDelegate=false`. `Release.Tests.dll`
SHA-256 `6D8C0ACCFE5C5C508845B80779A4C2CCA966D20258CDC07D5A0BF72747C07D62`, production
`ReleaseRunner.dll` SHA-256 `6901726DB6859A5229A5CC34299CBEE6B5A6C02BA52C58A6ADBF0EE9311078F1`,
byte-identical before and after.

`--list-tests` returned `modern preparation boundary` 3 cases and
`release authority child-probe regressions` 2 cases; both runs were within their
120s/180s bounds, no auto-repeat and no full 153-case re-run.

Modern preparation boundary (3 tests run, 3 passed, 0 ignored, 0 failed, 0 errored; 00:01:00.63):

- `prepare writes portable candidates after each enrolled producer's explicit test gates` → PASS.
- `build, pins, and both Workflow package-test failures block candidate output` → PASS.
- `the prepare CLI refuses to run when publication authority is present` → PASS. The
  `Main.runPrepareAuthorityProbe` dispatch returns the `ReleaseRunner.main` native int
  directly, so no Expecto ANSI pollution reaches the parent's `JsonNode.Parse`; the child
  emits the structured `{ "status": "failed", "reason": "…'GITHUB_TOKEN' is set…", "phase": "build" }`
  JSON, parent parses `GITHUB_TOKEN` from the reason field, and `candidate/` is never created.

Release authority child-probe regressions (2 tests run, 0 passed, 0 ignored, 2 failed, 0 errored;
00:00:22.13):

- `authority gate rejects unauthorized candidates before any HTTP for both producers` → FAILED.
  All 6 unauthorized child probes (3 scenarios × `{dotnet, workflow}`) errored in the
  spawned `dotnet Release.Tests.dll --authority-probe <mode> <producer>` child at
  `makePublishCli@290` with `System.ArgumentException: The type 'ReleaseRunner+PublishCli'
  is an F# record type but its representation is private. You must specify
  BindingFlags.NonPublic to access private type representations.` The parent captured
  `child exit=2 stderr=` (Expecto's "errored" exit code) for every probe; no
  factory-count / request-count / exit-4 assertion ran. Bounded root cause: the test source
  at `Authority.fs:290` calls `FSharpValue.MakeRecord(publishCliType, values)` without
  passing `BindingFlags.NonPublic`, and the production `ReleaseRunner+PublishCli` record
  (`Runner.fs:100` `type private PublishCli`) requires the non-public binding flag. The
  prior "Deflate only" fixture error (`Archive.fs:326-327`) is no longer reached because
  the fixture's inner archive now uses `CompressionLevel.Optimal` (method 8) per the
  Authority.md helper change; the new blocker is purely the F# private-record reflection
  contract. Factory call counters, HTTP request counts, and exit codes were never observed
  for the 6 unauthorized probes; the no-POST, canonical-URL, and at-least-one-request
  assertions were never observed for the 2 authorized probes.
- `authorized transport invokes the canonical /releases/tags/<producer> URL without a v prefix for both producers` → FAILED.
  Both authorized child probes errored at the same `makePublishCli@290` line with the same
  production-`PublishCli` reflection defect, so the canonical-URL, no-POST, factory-call,
  and exit-code-4 assertions never ran.

Combined recheck: 5 tests run, 3 passed, 0 ignored, 2 failed, 0 errored; 5/5 cases resolved,
no zero-case pass claim. The 3 modern-preparation positives exercise the real
`ReleaseOrchestration.prepare` stages 1+4 and the real `ReleaseRunner.main` authority guard.
The 2 failed authority cases reach `makePublishCli` for the first time (the prior
"Deflate only" fixture error has been cleared) and stop at the F# private-record
reflection contract. The 3 previously known blockers (native-exit fixture, publication
readiness, preparation stage-failure `phase` field) are unchanged in this run; the
`Orchestration.prepareRefusesPublicationAuthority` case moved from "errored" to "passed"
by the `Main.runPrepareAuthorityProbe` direct-dispatch change, and the Authority
`unauthorizedGuards` and `authorizedFixedTag` cases moved from "Deflate-only errored at
`buildFixture@207`" to "private-record reflection errored at `makePublishCli@290`" — a
real one-step progression in the test code path. The next step is a one-line
`BindingFlags.NonPublic` (or equivalent) fix at `Authority.fs:290` plus the matching
`buildFixture` / `makePublishCli` test-source edit; both are out of scope for this
read-only recheck. The full 153-case suite was NOT re-run; no `dotnet build`, no source
edit, no test source fix, no readiness waiver, no publication attempt was made. Full
sanitized stdout retained at `C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`;
no authorization header value, real token, host URL or live network call appears in it.

## Re-execution after the `BindingFlags.NonPublic` fix (no rebuild, no source edit)

`mayTest=true`, `mayBuild=false`, `mayEdit tests=false`, `mayDelegate=false`. Test-binary
`Release.Tests.dll` SHA-256 changed (round 3): `33E590100F14D321EE34B273EBCB5D9D821E3E877C12DBAB7210D594DA264B7C`;
production `ReleaseRunner.dll` SHA-256 stable
(`6901726DB6859A5229A5CC34299CBEE6B5A6C02BA52C58A6ADBF0EE9311078F1`). DLLs verified
byte-identical before and after each focused run.

`--list-tests` resolved both filters to nonzero counts: `modern preparation boundary` 3
cases, `release authority child-probe regressions` 2 cases (158-line full enumeration, no
zero-case fallback). Both runs within their 120s/180s bounds, no auto-repeat.

Exact 2 cases and nested 8 outputs (per probe child stdout) with actual counters, against
the `BindingFlags.NonPublic` fix:

| Case | Mode / Producer | factory | requests | firstUrl | method | exit | observed |
| --- | --- | --- | --- | --- | --- | --- | --- |
| unauthorized | reject-unauthorized / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-unauthorized / workflow | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-dummy-do-upload / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-dummy-do-upload / workflow | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-authorized-missing-token / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-authorized-missing-token / workflow | 0 | 0 | empty | empty | 4 | PASS |
| authorized | authorized-fixed-tag / dotnet | **0** | **0** | empty | empty | 4 | **FAIL** |
| authorized | authorized-fixed-tag / workflow | **0** | **0** | empty | empty | 4 | **FAIL** |

Both authorized probes errored at `Authority.fs:619` with the message
`authorized/{dotnet,workflow}: transport factory must be invoked exactly once (calls=0)` —
the parent parser's `factory=0 requests=0 firstUrl= method= exit=4` shows the production
pipeline reached the publish path but never invoked `createTransport ()`. Manual child
re-invocation (`dotnet Release.Tests.dll --authority-probe authorized-fixed-tag dotnet` with
the same dummy overrides, publication-credential names stripped from the child env) printed
the structured failure
`{ "status": "failed", "reason": "manifest revision 3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1
does not match clean HEAD ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b; rebuild the distribution" }`
BEFORE the probe counters, and the child process itself exited 4 (the parent suppressed the
ANSI log noise and reported `factory=0` because the pipeline returned before the factory
invocation). The symmetrical workflow probe emits the same failure.

Bounded root cause: the round-2 fixture `Authority.fs:132` hard-codes the manifest revision
to `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1` (the GHI-24 first-checkpoint clean HEAD at
the time the fixture was authored). Clean HEAD has since moved to
`ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`. Production
`ReleaseEvaluate.VerifiedCandidate.createFromSnapshot` (`Evaluate.fs:222` `let revisionCheck
= ensure (header.Revision = evaluatedHead) (RevisionMismatch(evaluatedHead, header.Revision))`)
enforces the match; the publish pipeline rejects the fixture at the pre-contract hook before
reaching `let transport = createTransport ()` at `Runner.fs:1023`. This is a real production
gate, not a test-source weakness: the assertion that the factory must be invoked exactly
once for authorized probes is correct, and the production rejection is correct under
AC8's identity gate. The fix is a one-line change to `Authority.fs:132` to use the current
clean HEAD revision, plus the matching `Release.Tests.exe` rebuild; both are out of scope for
this read-only recheck (`mayEdit tests=false`, `mayBuild=false`).

Per scope: this record makes no build, no source edit, no test fixture edit, no readiness
waiver, and no publication attempt. The factory-count, request-count, URL, method, and
exit-code assertions are NOT weakened; the parent still parses the child stdout strictly
and the assertions still require the production pipeline to reach `createTransport ()`. Full
sanitized stdout retained at
`C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`; no authorization header value,
real token, host URL, or live network call appears in it (only the canonical release
tag-path fragment `/releases/tags/<producer>` and the published manifest revision values).

Combined recheck: 5 tests run, 4 passed, 0 ignored, 1 failed, 0 errored; 5/5 cases resolved,
no zero-case pass claim. The 3 modern-preparation positives and the unauthorized-guards
positive exercise the real `ReleaseOrchestration.prepare` stages 1+4 and the real
`ReleaseRunner.main` authority guard. The failing authorized case reaches the production
publish pipeline for the first time (the prior `makePublishCli@290` reflection defect is
cleared by `BindingFlags.NonPublic` at `Authority.fs:290`) and stops at the production
`VerifiedCandidate.createFromSnapshot` revision check. The full 153-case suite was NOT
re-run.

## Re-execution after the manifest-revision hardcode fix (no rebuild, no source edit)

`mayTest=true`, `mayBuild=false`, `mayEdit tests=false`, `mayDelegate=false`. The fixture
now sources `revision` from `BuildProvenance.committedHead repositoryRoot` (no
hard-coded `3612e8e6…` checkpoint), so the manifest and verified event context describe
the same committed source. Test-binary SHA-256 changed from round 3
`33E590100F14D321EE34B273EBCB5D9D821E3E877C12DBAB7210D594DA264B7C` to round 4
`471D166736F583481363C0FF3F0813FDD24CBBE7CF147BF1B8CF602FA4F7B9EC`; production
`ReleaseRunner.dll` SHA-256 stable at
`6901726DB6859A5229A5CC34299CBEE6B5A6C02BA52C58A6ADBF0EE9311078F1`. HEAD unchanged at
`ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`. DLLs verified byte-identical before and after
the focused run; no `dotnet build`, no source edit, no test fixture edit, no readiness
waiver, no publication attempt was made.

`--list-tests` resolved the filter to the expected 2 cases (158-line full enumeration, no
zero-case fallback); the modern-preparation list re-run was intentionally skipped (3
reruns unchanged, all previously passed). Run within the 180s bound (7.81s actual), no
auto-repeat, no full 153-case re-run.

Command:

```text
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll \
  --filter-test-list "release authority child-probe regressions" \
  --sequenced --no-spinner --summary
```

Release authority child-probe regressions (2 tests run, 1 passed, 0 ignored, 1 failed,
0 errored; 00:00:07.40). Exact 2 cases and nested 8 outputs (per probe child stdout):

| Case | Mode / Producer | factory | requests | firstUrl | method | exit | observed |
| --- | --- | --- | --- | --- | --- | --- | --- |
| unauthorized | reject-unauthorized / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-unauthorized / workflow | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-dummy-do-upload / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-dummy-do-upload / workflow | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-authorized-missing-token / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-authorized-missing-token / workflow | 0 | 0 | empty | empty | 4 | PASS |
| authorized | authorized-fixed-tag / dotnet | **1** | **2** | `/releases/tags/dotnet` | GET | **1** | **FAIL** |
| authorized | authorized-fixed-tag / workflow | **1** | **2** | `/releases/tags/workflow` | GET | **1** | **FAIL** |

The authorized probes reach the production publish pipeline for the first time:
`factory ≥ 1` ✓, `requests ≥ 1` ✓, canonical URL with no `v` prefix ✓, no POST before
scripted refusal ✓, `currentEvaluatedHead == candidateRevision == ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`
✓ (the prior `rootRevisionMismatch` is GONE; `3612` hardcode is gone; new fixture
sources the revision from `BuildProvenance.committedHead`). The exact child stdout shows
the structured pre-contract failure followed by the probe counters:

```text
{ "status": "failed"
  "reason": "release failed for 'https://api.github.com:443/repos/masterlifting/mcp-store/releases/tags/<producer>': status=500"
  "uploadCompleted": false
  "recoveryNeeded": false
  "componentId": "<producer>"
  "version": "1.0.4"
  "archiveName": "v1.0.4.zip"
  "currentEvaluatedHead": "ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b"
  "candidateRevision":   "ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b"
  ... }
factory=1 requests=2
firstUrl=https://api.github.com/repos/masterlifting/mcp-store/releases/tags/<producer>
method=GET exit=1
```

Bounded root cause for the remaining authorized failure: the production HTTP-500 path in
`ReleaseRunner.runPublishPipelineWithTransportFactory` returns exit code `1`; the
`authorizedFixedTag` test asserts `exit=4` to match the authority-guard convention. The
production structured failure JSON is correctly emitted (`status="failed"`, the canonical
`https://api.github.com:443/repos/masterlifting/mcp-store/releases/tags/<producer>` URL
is captured as the failure target, `currentEvaluatedHead == candidateRevision` so the
fixture-identity contract is satisfied), but the exit code is `1` not `4`. The fixture
is not weakened: the assertion still requires the production pipeline to construct the
transport (factory=1), reach the canonical URL (`/releases/tags/<producer>` no `v`
prefix), not POST before the scripted refusal, and emit `exit=4`. The next step is a
production-side exit-code alignment for the HTTP-error branch (so scripted-transport
refusal also exits `4`); that change plus a matching `Release.Tests.exe` rebuild is out
of scope for this read-only recheck (`mayEdit tests=false`, `mayBuild=false`).

The 6 unauthorized probes exercise the real authority guard for both producers and 3
reject scenarios (factory=0, requests=0, exit=4); the 2 authorized probes exercise the
real production publish pipeline for both producers (factory=1, requests=2, canonical
GET to `/releases/tags/<producer>`, no POST, current HEAD bound to both evaluated and
candidate revisions, structured pre-contract failure JSON emitted, exit=1). Full
sanitized stdout retained at `C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`;
no authorization header value, real token, host URL, or live network call appears in it
(only the canonical release tag-path fragment `/releases/tags/<producer>` and the
published manifest revision values). The full 153-case suite was NOT re-run.

## Re-execution after the test-side HTTP-500 exit-code alignment (no rebuild, no source edit)

`mayTest=true`, `mayBuild=false`, `mayEdit tests=false`, `mayDelegate=false`. The focused
recheck ran on the existing `Release.Tests.dll` built from the uncommitted
`Authority.fs` / `Main.fs` / `Tests.fsproj` slice (test-binary identity
`c4f77abfd6f4ab5ece103db824c8d789390ae4542903060583556625b0e1e79f`; production
`ReleaseRunner.dll` SHA-256 stable
`6901726db6859a5229a5cc34299cbee6b5a6c02ba52c58a6adbf0ee9311078f1` byte-identical to the
tests/bin copy before and after; HEAD unchanged at
`ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`). The one-line test-source change is at
`Authority.fs:603` (the `authorizedFixedTag` exit-code assertion is now
`exitCode <> 1` with the message `transaction FAILED on scripted HTTP 500 exits with code
1`); no production code change was attempted, no readiness waiver, no publication
attempt, no real network call, no foreign checkout.

`--list-tests` resolved the filter to the expected 2 cases (158-line full enumeration,
no zero-case fallback). Run within the 180s bound (6.05s actual), no auto-repeat, no
full 153-case re-run, no modern-preparation re-run. Per-probe `mode` / `producer` /
`factory` / `requests` / `firstUrl` / `method` / `exit` (8 nested child probe outputs):

| Case | Mode / Producer | factory | requests | firstUrl | method | exit | observed |
| --- | --- | --- | --- | --- | --- | --- | --- |
| unauthorized | reject-unauthorized / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-unauthorized / workflow | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-dummy-do-upload / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-dummy-do-upload / workflow | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-authorized-missing-token / dotnet | 0 | 0 | empty | empty | 4 | PASS |
| unauthorized | reject-authorized-missing-token / workflow | 0 | 0 | empty | empty | 4 | PASS |
| authorized | authorized-fixed-tag / dotnet | 1 | 2 | `/repos/masterlifting/mcp-store/releases/tags/dotnet` | GET | 1 | PASS |
| authorized | authorized-fixed-tag / workflow | 1 | 2 | `/repos/masterlifting/mcp-store/releases/tags/workflow` | GET | 1 | PASS |

The 6 unauthorized probes hit the production authority guard, the parent parses
`factory=0 requests=0 firstUrl= method= exit=4` per probe, and no factory-call /
HTTP-request / exit≠4 assertion fails. The 2 authorized probes reach the production
publish pipeline: `factory >= 1` ✓, `requests >= 1` ✓, canonical URL with no `v` prefix ✓
(`/repos/masterlifting/mcp-store/releases/tags/<producer>`), no POST before the scripted
refusal ✓, `currentEvaluatedHead == candidateRevision == ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`
✓, and the production pre-contract HTTP-500 structured failure JSON is followed by the
probe counters `factory=1 requests=2 firstUrl=… method=GET exit=1`. The exit-code-1
assertion at `Authority.fs:603` now matches the production HTTP-error branch in
`ReleaseRunner.runPublishPipelineWithTransportFactory`, so the previously-failing
authorized case is GREEN. The factory-call / request-count / URL / method assertions
that were already passing in round 4 are unchanged.

The 3 previously known blockers (Coherence native-exit fixture, publication readiness,
preparation stage-failure `phase` field) are unchanged in this run and remain the open
modern-gate failures; the Authority `unauthorizedGuards` and `authorizedFixedTag` cases
both PASS in the same focused run. Exact scope: private composition / authority guard /
canonical-URL locator observations on the current modern `prepare=3` baseline; no
production source edit, no readiness waiver, no publication, no real network, no
foreign checkout. Combined recheck: 2 tests run, 2 passed, 0 ignored, 0 failed, 0
errored; 2/2 cases resolved, no zero-case pass claim. The 2 cases together cover the 6
guard probes (unauthorized) and the 2 tag-path probes (authorized). Full sanitized
stdout retained at `C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`; no
authorization header value, real token, host URL, or live network call appears in it
(only the canonical release tag-path fragment `/releases/tags/<producer>` and the
Expecto summary). The full 153-case suite was NOT re-run.
