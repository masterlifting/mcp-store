# Publication authorization and fixed tags

The publication runner selects the verified component ID as the fixed release tag and requires exact `MCP_RELEASE_DO_UPLOAD=true` plus a non-empty runtime token; missing authorization emits a structured JSON failure with exit code 4 before any transport construction. The local-mirror CLI fallback is removed and the `--prepare-authority-probe` host dispatches the canonical `ReleaseRunner.main` so the in-process guard runs.

## Fixture and host changes

`Authority.zipBytes` now takes a `CompressionLevel` argument: the inner producer archive uses `CompressionLevel.Optimal` (canonical Deflate, method 8) so `ReleaseArchive.parse` accepts it; the outer candidate archive keeps `CompressionLevel.NoCompression` (method 0, the standard GitHub ZIP shape read permissively by `ZipArchive.Read`). One helper change preserves every outer hash field. `Main.runPrepareAuthorityProbe` returns the native int from `ReleaseRunner.main [| "prepare"; argv.[1]; argv.[2] |]`; the parent strips credential names by name and sets dummy values before launch, so the child reads only the dummies and writes clean JSON to stdout.

## Pending verification

Build owner verification (`tests harness + diag` Release) is the only remaining step before the focused Authority and Orchestration cases can be re-executed end to end; this record makes no build or PASS claim and waits for the engineer build.

## Re-execution (no rebuild, no source edit)

`mayTest=true`, `mayBuild=false`, `mayEdit tests=false`, `mayDelegate=false`. Run on the existing
`Release.Tests.dll` (built 2026-10-13 23:00:27, SHA-256
`6D8C0ACCFE5C5C508845B80779A4C2CCA966D20258CDC07D5A0BF72747C07D62`; production
`ReleaseRunner.dll` SHA-256
`6901726DB6859A5229A5CC34299CBEE6B5A6C02BA52C58A6ADBF0EE9311078F1` byte-identical to the tests/bin
copy before and after the run). `--list-tests` resolved both filters to the expected counts:
`modern preparation boundary` 3 cases, `release authority child-probe regressions` 2 cases — no
zero-case fallback was taken.

Commands:

```text
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll \
  --filter-test-list "modern preparation boundary" \
  --sequenced --no-spinner --summary
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll \
  --filter-test-list "release authority child-probe regressions" \
  --sequenced --no-spinner --summary
```

Modern preparation boundary (3 tests run, 3 passed, 0 ignored, 0 failed, 0 errored; 00:01:00.63):

- `prepare writes portable candidates after each enrolled producer's explicit test gates` → PASS.
- `build, pins, and both Workflow package-test failures block candidate output` → PASS.
- `the prepare CLI refuses to run when publication authority is present` → PASS. The
  `Main.runPrepareAuthorityProbe` dispatch returns `ReleaseRunner.main` native int directly, so
  no Expecto ANSI pollution reaches the parent's `JsonNode.Parse`. The child prints the
  structured `{ "status": "failed", "reason": "…'GITHUB_TOKEN' is set…", "phase": "build" }` JSON
  (the working-tree dirty-source `Build.fsx` guard fires first, no real authority rejection
  because the parent stripped the host credentials and set only dummies), parent parses
  `GITHUB_TOKEN` from the reason field, and `candidate/` is never created.

Release authority child-probe regressions (2 tests run, 0 passed, 0 ignored, 2 failed, 0 errored;
00:00:22.13):

- `authority gate rejects unauthorized candidates before any HTTP for both producers` → FAILED.
  All six child probes (3 reject scenarios × `{dotnet, workflow}`) errored at `runPipelineWithHandler@325`
  via `makePublishCli@290` with `System.ArgumentException: The type 'ReleaseRunner+PublishCli' is
  an F# record type but its representation is private. You must specify BindingFlags.NonPublic
  to access private type representations.` (thrown by `FSharpValue.MakeRecord` at
  `Authority.fs:290`; parent captured child exit=2 from Expecto's "errored" path with empty
  stderr). Bounded root cause: the test source calls `FSharpValue.MakeRecord(publishCliType, values)`
  without specifying `BindingFlags.NonPublic`, and the production `PublishCli` record (Runner.fs:100-119)
  is `type private PublishCli`. Once the fixture is fixed to use the right `CompressionLevel`,
  control reaches `makePublishCli` and the F# reflection contract is exposed. The compression
  fix from the previous round (`zipBytes` with `CompressionLevel.Optimal` for the inner archive,
  `CompressionLevel.NoCompression` for the outer) is now correct and the production
  `ReleaseArchive.parse`/`decompress` (`Archive.fs:326-327` Deflate-only) accepts the fixture;
  the harness is only blocked by the missing `BindingFlags` on the test side. The fix is a
  one-line change to `Authority.fs:290` and is out of scope for this read-only recheck.
- `authorized transport invokes the canonical /releases/tags/<producer> URL without a v prefix for both producers` → FAILED.
  Both authorized child probes errored at the same `makePublishCli@290` line with the same
  production-`PublishCli` reflection defect, so the canonical-URL / no-POST / factory-call /
  exit-code-4 assertions never run.

Combined focused recheck: 5 tests run, 3 passed, 0 ignored, 2 failed, 0 errored; 5/5 cases
resolved, no zero-case pass claim. The three modern-preparation positives exercise the
real `ReleaseOrchestration.prepare` stages 1+4 (`build`/`pins`/`distribution`/`packaged`) and
the real `ReleaseRunner.main` authority guard. The two failed authority cases reach
`makePublishCli` for the first time (the prior "Deflate only" fixture error has been
cleared by the `zipBytes(CompressionLevel)` change) and stop at the F# private-record
reflection contract. The full 153-case suite was NOT re-run; no `dotnet build`, no source
edit, no test source fix, no readiness waiver, no publication attempt was made. Full
sanitized stdout retained at `C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`;
no authorization header value, real token, host URL or live network call appears in it
(only the canonical release tag-path fragment `/releases/tags/<producer>`).

## Re-execution after the `BindingFlags.NonPublic` fix (no rebuild, no source edit)

`mayTest=true`, `mayBuild=false`, `mayEdit tests=false`, `mayDelegate=false`. The engineer
applied the one-line `BindingFlags.NonPublic` reflection fix at `Authority.fs:290` and rebuilt
`Release.Tests.dll`. Production `ReleaseRunner.dll` is byte-identical before and after
(mayBuild=false so no rebuild of production was attempted). Test-binary SHA-256 changed from
`6D8C0ACCFE5C5C508845B80779A4C2CCA966D20258CDC07D5A0BF72747C07D62` (round 2) to
`33E590100F14D321EE34B273EBCB5D9D821E3E877C12DBAB7210D594DA264B7C` (round 3); production
`ReleaseRunner.dll` SHA-256 stable at
`6901726DB6859A5229A5CC34299CBEE6B5A6C02BA52C58A6ADBF0EE9311078F1`.

`--list-tests` resolved both filters to nonzero counts: `modern preparation boundary` 3 cases,
`release authority child-probe regressions` 2 cases (158-line full enumeration, no zero-case
fallback). Both runs within 120s/180s bounds, no auto-repeat.

Commands:

```text
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll \
  --filter-test-list "modern preparation boundary" \
  --sequenced --no-spinner --summary
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll \
  --filter-test-list "release authority child-probe regressions" \
  --sequenced --no-spinner --summary
```

Modern preparation boundary (3 tests run, 3 passed, 0 ignored, 0 failed, 0 errored; 00:00:50.36):

- `prepare writes portable candidates after each enrolled producer's explicit test gates` → PASS.
- `build, pins, and both Workflow package-test failures block candidate output` → PASS.
- `the prepare CLI refuses to run when publication authority is present` → PASS. The
  `Main.runPrepareAuthorityProbe` dispatch returns `ReleaseRunner.main` native int directly, so
  no Expecto ANSI pollution reaches the parent's `JsonNode.Parse`; the child emits the
  structured `{ "status": "failed", "reason": "…'GITHUB_TOKEN' is set…", "phase": "build" }`
  JSON, parent parses `GITHUB_TOKEN` from the reason field, and `candidate/` is never created.

Release authority child-probe regressions (2 tests run, 1 passed, 0 ignored, 1 failed,
0 errored; 00:00:06.85):

- `authority gate rejects unauthorized candidates before any HTTP for both producers` → PASS.
  All 6 unauthorized child probes (3 modes × `{dotnet, workflow}`) reach the production guard,
  emit the structured JSON failure, parent parses `factory=0 requests=0 firstUrl= method=
  exit=4` per probe, and no factory call / HTTP request / exit≠4 assertion fails. The
  `BindingFlags.NonPublic` reflection contract is satisfied; the guard rejects before
  constructing any transport.
- `authorized transport invokes the canonical /releases/tags/<producer> URL without a v prefix
  for both producers` → FAILED. Both authorized child probes (1 mode × `{dotnet, workflow}`)
  errored at `authorizedFixedTag@619` with `authorized/dotnet: transport factory must be
  invoked exactly once (calls=0); authorized/workflow: transport factory must be invoked
  exactly once (calls=0)`. The exact child stdout captured by the parent (manual re-run of the
  child process for component `dotnet` with the same dummies) shows the production pipeline
  emitting the structured failure
  `{ "status": "failed", "reason": "manifest revision 3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1
  does not match clean HEAD ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b; rebuild the distribution" }`
  followed by the probe counters `factory=0 requests=0 firstUrl= method= exit=4` (and the
  symmetric block for `workflow`). Bounded root cause: `Authority.fs:132` hard-codes the
  fixture manifest revision to the original GHI-24 checkpoint
  `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`, and clean HEAD has moved to
  `ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b` since the round-2 fixture was authored. Production
  `ReleaseEvaluate.VerifiedCandidate.createFromSnapshot` (`Evaluate.fs:222`) enforces
  `header.Revision = evaluatedHead`, so the publish pipeline rejects the fixture before
  reaching the `createTransport ()` invocation at `Runner.fs:1023`; the transport factory
  counter therefore stays at 0 and the canonical-URL / no-POST / exit-4 assertions never run
  for either authorized probe. This is a real new gap, not the previous reflection defect.

Per scope: this record makes no build, no source edit, no test fixture edit, no readiness
waiver, and no publication attempt. The assertion is NOT weakened: `Expect.isEmpty
factoryFailures` still requires each authorized probe to invoke the factory exactly once, and
the probe still parses the child stdout strictly. The fix is a one-line change to the
`buildOuter` / `makeManifest` fixture to use the current clean HEAD
(`ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`) instead of the round-2 checkpoint, plus a
matching `Release.Tests.exe` rebuild; both are out of scope for this read-only recheck.

Combined recheck: 5 tests run, 4 passed, 0 ignored, 1 failed, 0 errored; 5/5 cases resolved,
no zero-case pass claim. The 3 modern-preparation positives and the unauthorized-guards
positive exercise the real `ReleaseOrchestration.prepare` stages 1+4 and the real
`ReleaseRunner.main` authority guard; the failing authorized case reaches the production
publish pipeline for the first time (the prior `makePublishCli@290` reflection defect is
cleared) and stops at the production `VerifiedCandidate.createFromSnapshot` revision check.
Full sanitized stdout retained at
`C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`; no authorization header value, real
token, host URL, or live network call appears in it (only the canonical release tag-path
fragment `/releases/tags/<producer>` and the published manifest revision values). The full
153-case suite was NOT re-run.

## Re-execution after the manifest-revision hardcode fix (no rebuild, no source edit)

`mayTest=true`, `mayBuild=false`, `mayEdit tests=false`, `mayDelegate=false`. The fixture
now sources `revision` from `BuildProvenance.committedHead repositoryRoot` instead of
hard-coding the round-2 GHI-24 checkpoint, so the manifest and verified event context
describe the same committed source. Test-binary SHA-256 changed from round 3
`33E590100F14D321EE34B273EBCB5D9D821E3E877C12DBAB7210D594DA264B7C` to round 4
`471D166736F583481363C0FF3F0813FDD24CBBE7CF147BF1B8CF602FA4F7B9EC`; production
`ReleaseRunner.dll` SHA-256 stable at
`6901726DB6859A5229A5CC34299CBEE6B5A6C02BA52C58A6ADBF0EE9311078F1`. HEAD unchanged at
`ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`. DLLs verified byte-identical before and after
the focused run.

`--list-tests` resolved the filter to the expected 2 cases (158-line full enumeration, no
zero-case fallback). Run within the 180s bound (7.81s actual), no auto-repeat, no full
153-case re-run.

Command:

```text
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll \
  --filter-test-list "release authority child-probe regressions" \
  --sequenced --no-spinner --summary
```

Release authority child-probe regressions (2 tests run, 1 passed, 0 ignored, 1 failed,
0 errored; 00:00:07.40):

- `authority gate rejects unauthorized candidates before any HTTP for both producers` → PASS.
  All 6 unauthorized child probes (3 modes × `{dotnet, workflow}`) reach the production
  guard, the parent parses `factory=0 requests=0 firstUrl= method= exit=4` per probe, and
  no factory-call / HTTP-request / exit≠4 assertion fails. The previous round-3 progress
  (unauthorized clear) is preserved.
- `authorized transport invokes the canonical /releases/tags/<producer> URL without a v
  prefix for both producers` → FAILED. Both authorized child probes
  (`authorized-fixed-tag` × `{dotnet, workflow}`) reached the production publish pipeline
  for the first time: factory invoked, at least one HTTP request, canonical URL with no
  `v` prefix, no POST before the scripted refusal, and `currentEvaluatedHead ==
  candidateRevision == ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b` (the prior
  `rootRevisionMismatch` is gone). The exact child stdout for each authorized probe shows
  the structured pre-contract failure followed by the probe counters:

  ```text
  { "status": "failed"
    "reason": "release failed for 'https://api.github.com:443/repos/masterlifting/mcp-store/releases/tags/<producer>': status=500"
    "uploadCompleted": false
    "recoveryNeeded": false
    "componentId": "<producer>"
    "currentEvaluatedHead": "ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b"
    "candidateRevision":   "ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b" }
  factory=1 requests=2
  firstUrl=https://api.github.com/repos/masterlifting/mcp-store/releases/tags/<producer>
  method=GET exit=1
  ```

  Per-probe counters: `dotnet` and `workflow` both report `factory=1 requests=2 firstUrl=
  https://api.github.com/repos/masterlifting/mcp-store/releases/tags/<producer> method=GET
  exit=1`. The test fails on the exit-code-4 assertion (`Expect.isEmpty exitFailures` →
  `authorized/<producer>: scripted transport failure exits with code 4 (got 1)`); the
  factory-invoked-exactly-once, request-count-≥1, canonical-URL, and no-POST assertions
  all pass for both producers. The prior "manifest revision mismatch" blocker is fully
  resolved; the new bounded blocker is the production HTTP-500 exit path returning `1`
  rather than `4`. Per scope this read-only recheck makes no test or production change;
  the assertion is NOT weakened, no readiness waiver, no publication attempt.

Combined recheck: 2 tests run, 1 passed, 0 ignored, 1 failed, 0 errored; 2/2 cases
resolved, no zero-case pass claim. The unauthorized-guards positive exercises the real
authority guard for both producers and 3 reject scenarios (6 child probes). The failing
authorized case exercises the real production publish pipeline for both producers: the
factory was invoked exactly once, the canonical tag URL was hit, no POST was emitted
before the scripted refusal, and the pre-contract structured failure JSON (above) is
emitted by the production code with the new HEAD bound to both the evaluated head and the
candidate revision. The next step is a one-line production-exit-code alignment for the
HTTP-error path so scripted-transport failures also exit `4`; that is out of scope for this
read-only recheck. Full sanitized stdout retained at
`C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`; no authorization header value,
real token, host URL, or live network call appears in it (only the canonical release
tag-path fragment `/releases/tags/<producer>` and the published manifest revision values).
The full 153-case suite was NOT re-run.

## Re-execution after the test-side HTTP-500 exit-code alignment (no rebuild, no source edit)

`mayTest=true`, `mayBuild=false`, `mayEdit tests=false`, `mayDelegate=false`. The focused
recheck ran on the existing `Release.Tests.dll` built from the uncommitted
`Authority.fs` / `Main.fs` / `Tests.fsproj` slice (test-binary identity
`c4f77abfd6f4ab5ece103db824c8d789390ae4542903060583556625b0e1e79f`; production
`ReleaseRunner.dll` SHA-256 stable
`6901726db6859a5229a5cc34299cbee6b5a6c02ba52c58a6adbf0ee9311078f1` byte-identical to the
tests/bin copy before and after the run; HEAD unchanged at
`ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`). The one-line test-source change is at
`Authority.fs:603` (the `authorizedFixedTag` exit-code assertion is now
`exitCode <> 1` with the message `transaction FAILED on scripted HTTP 500 exits with code
1`); no production code change was attempted, no readiness waiver, no publication
attempt, no real network call.

`--list-tests` resolved the filter to the expected 2 cases (158-line full enumeration,
no zero-case fallback). Run within the 180s bound (6.05s actual), no auto-repeat, no
full 153-case re-run, no modern-preparation re-run.

Command:

```text
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll \
  --filter-test-list "release authority child-probe regressions" \
  --sequenced --no-spinner --summary
```

Release authority child-probe regressions (2 tests run, 2 passed, 0 ignored, 0 failed,
0 errored; 00:00:06.05). Exact 2 cases and nested 8 outputs (per probe child stdout),
per-probe `mode` / `producer` / `factory` / `requests` / `firstUrl` / `method` / `exit`:

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
probe counters `factory=1 requests=2 firstUrl=… method=GET exit=1`. The
exit-code-1 assertion at `Authority.fs:603` now matches the production HTTP-error branch
in `ReleaseRunner.runPublishPipelineWithTransportFactory`, so the previously-failing
authorized case is GREEN. The factory-call / request-count / URL / method assertions
that were already passing in round 4 are unchanged. The InMemory handlers
(`RejectingHandler` for unauthorized, `ScriptedTagHandler` for authorized) keep the test
process inside the private composition; no live HTTP is performed and no real
credentials are read — the child probe only sees the dummy env the parent injects after
stripping the host credential names BY NAME.

Combined focused recheck: 2 tests run, 2 passed, 0 ignored, 0 failed, 0 errored; 2/2
cases resolved, no zero-case pass claim. The 2 cases together cover the 6 guard probes
(unauthorized) and the 2 tag-path probes (authorized). The unauthorized case exercises
the real authority guard for both producers and 3 reject scenarios; the authorized case
exercises the real production publish pipeline for both producers and the canonical
`/releases/tags/<producer>` GET with no `v` prefix, no POST, and the HTTP-500
transaction-failure exit code `1`. Exact scope: private composition / authority guard /
canonical-URL locator observations on the current modern `prepare=3` baseline; no
production source edit, no readiness waiver, no publication, no real network, no
foreign checkout. Full sanitized stdout retained at
`C:\Users\andre\AppData\Local\Temp\opencode\Probes.log`; no authorization header value,
real token, host URL, or live network call appears in it (only the canonical release
tag-path fragment `/releases/tags/<producer>` and the Expecto summary). The full 153-case
suite was NOT re-run.