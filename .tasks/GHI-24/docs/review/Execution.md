# Current release-suite execution

## Scope and source identity

The tester ran the existing compiled suite gate once, without building or
editing production or test sources. Before and after the run, `HEAD` was
`523f625014dbf81fb788fe5c11877fc7a9626382`, the release workflow SHA-256 was
`2827920E1D9A807FEA7F3F7215D2030D81D82A25A182151713458510886C21E5`, and the
compiled `Release.Tests.dll` SHA-256 was
`9908BDA69D61754237C687B4FF9A29BA3A8ECBE0ECD232CCC853DE3466B5E2E6`. Each
before/after value matched; no workflow edit landed during this run.

The suite's current Release test-project build was supplied by the coordinator
and confirmed with Dotnet MCP details: run
`pQPcot3En7JLL-xoYYygsvdwatgGqXPCBIPp1ve46r8`, succeeded, exit 0, zero errors,
zero warnings. The tester did not build.

## Post-run source drift

A later post-run check still found the same `HEAD` and compiled test DLL, but the
working `.github/workflows/release.yml` SHA-256 had changed to
`8A5E3B7E0FB503AD38B153156EE1BDDE3AD86DD40EF6506502785AB4E458EE28`. The suite
result above applies to the workflow hash recorded before and immediately after
the run, `2827920E1D9A807FEA7F3F7215D2030D81D82A25A182151713458510886C21E5`;
the later workflow state has not been verified. No second full run was started.

## Command and inventory

Before launching the wrapper, the process environment variables
`GITHUB_TOKEN`, `GH_TOKEN`, `MCP_RELEASE_DO_UPLOAD`,
`MCP_RELEASE_GITHUB_OWNER`, `MCP_RELEASE_GITHUB_REPOSITORY`, `GITHUB_ENV`,
`GITHUB_OUTPUT`, `MCP_RELEASE_TEST_PROBE_DIR`, and
`MCP_RELEASE_TEST_TRANSACTION_PROBE_FILE` were removed by name without reading
or printing their values. The approved local historical fixture root was used.

```powershell
pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap
```

`Suite.ps1` dynamically enumerated `--list-tests` and its inventory matched the
executed Expecto count: **153**. This run did not assume the prior 33+14+101
estimate: the current inventory contains **15 Policy**, **102 Handoff**, **5
Coherence**, **3 modern preparation**, and **1 Historical** case(s), plus 27
other cases. Expecto reported **153 run, 144 passed, 0 ignored, 7 failed, 2
errored** in `00:02:27.3252563`, exit 3. The wrapper exited 1 because the
compiled suite failed; no inventory mismatch was reported.

Nested counters emitted by the suites were Bootstrap 9/0, Evaluate 68/0,
Historical fixture assertions 10/0, Bootstrap invocation 10/0, Metadata 52/0,
Publish 62/0, Revision 22/0, producer PE integration 2/0, and Telegram
isolation 24/2 (passed/failed). The compiled historical case ran with the
approved local fixture; no fixture was downloaded.

## Failures and diagnosis

Five Coherence cases failed and are the known workflow-wiring readiness reds:

- `the production Coherence.ps1 accepts roster wiring and rejects marker drift`
  (expected exit 0, observed 1);
- `the production Coherence.ps1 rejects roster policy and inventory drift` and
  `the coherence guard consumes the project step value, not its own output file`
  (the guard expected three `Update.ps1 -Verify` matches and found one);
- `the actual Workflow CI step preserves a failing native exit before later
  entrypoints` (publication was not structurally dependent on the tests job);
- `the release workflow isolates preparation, trusted building, and data-only
  publication` (fresh preparation/builder jobs were absent).

The `release participant isolation assertions` Telegram case also failed its
two assertions requiring the independent trusted-builder and data-only
publication jobs. These Coherence and Telegram workflow integration failures
were not skipped or marked ignored; the workflow identity was unchanged during
the run.

The remaining failed case was the production-path Handoff regression `handoff
rejects an inner producer archive without its embedded manifest`. Its test calls
`ReleaseHandoff.readRawTransport`, but the failing assertion checks only for the
phrase `embedded manifest`. Source inspection shows the lower-level production
`ReleaseCompare.observeBytes` rejects the absent `distribution.json` first with
`the archive must contain exactly one 'distribution.json' entry`, before the
later Handoff missing-manifest branch. This points to an expected-diagnostic
mismatch; the transport did reject the malformed archive. No test or production
change was made.

These two modern preparation cases errored in `withTemporaryRoot` cleanup with
`UnauthorizedAccessException` while recursively deleting generated temporary
Git object paths (`Orchestration.fs:34`): `prepare writes portable candidates
after each enrolled producer's explicit test gates` and `build, pins, and both
Workflow package-test failures block candidate output`. The captured evidence
does not establish why deletion was denied; no retry or cleanup workaround was
attempted.

## Other gate phases and limitations

The nested `dotnet/tests/ProvenanceTests.fsx` phase passed (`provenance contract
passed`, exit 0). The wrapper did not report a `GITHUB_ENV`/`GITHUB_OUTPUT`
sentinel change. Distribution and packaged-runtime packaging entrypoints were
only checked for existence by this wrapper; they were not executed, preserving
their clean-checkpoint guards. No build, package, remote GitHub observation,
real HTTP publication, or upload occurred; HTTP behavior in the compiled cases
uses controlled local handlers.

There is still no modern end-to-end smoke covering preparation through the
independent trusted-builder receipt/closure and publish workflow. In the tested
workflow snapshot, the required fresh preparation/builder wiring was absent and
the trusted receipt was not ready for end-to-end verification. The later
workflow state is unverified. The Coherence and Telegram failures describe the
tested snapshot; the Handoff diagnostic assertion and Orchestration cleanup
errors remain separate non-CI test failures to resolve. No acceptance PASS is
claimed.

## 2026-10-13 full-gate execution addendum

The coordinator supplied the compiled test-project build evidence as Dotnet
run `fNOlcu9nPW0PAgxSYBs8Jz_FgdW7l5ugpH9cc4wOLf4` (succeeded, exit 0, zero
errors and warnings); no build was run by the tester. The 20 test-project source
files were hashed before and after this execution and remained unchanged.

The repository's existing full-gate entrypoint is `infrastructure/release/Suite.ps1`
(`ExecuteFULLSuite.ps1` is not present). It was run once, without a build:

```powershell
pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap
```

Before invocation, the process environment names `GITHUB_TOKEN`, `GH_TOKEN`,
`MCP_RELEASE_DO_UPLOAD`, `MCP_RELEASE_GITHUB_OWNER`,
`MCP_RELEASE_GITHUB_REPOSITORY`, `GITHUB_ENV`, `GITHUB_OUTPUT`,
`MCP_RELEASE_TEST_PROBE_DIR`, `MCP_RELEASE_TEST_TRANSACTION_PROBE_FILE`, and
`MCP_RELEASE_HISTORICAL_ROOT` were removed without reading or printing values.
The root was the approved local fixture; no fixture download or live API/upload
was performed.

The live `--list-tests` enumeration exited 0 with **153** cases, and the wrapper
reported no inventory mismatch. Its current inventory comprised **15 Policy**,
**102 Handoff**, **5 Coherence**, **3 modern preparation**, and **1 Historical**
case(s), plus **27** other cases. Expecto reported **153 run, 150 passed, 0
ignored, 3 failed, 0 errored** in `00:03:12.3262802`; the wrapper exited 1 on the
compiled suite result. The provenance phase passed (`provenance contract passed`).
Nested counters were Bootstrap 9 passed/0 failed, Evaluate 68/0, Historical
fixture assertions 10/0, Bootstrap invocation 10/0, Metadata 52/0, Publish 62/0,
Revision 22 passed/0 failed/0 skipped, producer PE integration 2 passed/0
failed/0 not-run, and Telegram isolation 25/0 (passed/failed; no skip counter
reported).

### Run identity and limitation

Before/after, the 20 test-source files had aggregate SHA-256
`CF303BC8B56713280AA954B29E0F117F53EEC85CB5432E110CAA6B22DCB5C60D`; the
`Release.Tests.dll` hash was
`2EAB05473A564F3E46FA782A4C783AA71F584D1E397101DADBCC91D93740A84D`, the
`ReleaseRunner.dll` hash was
`ABCE1E392CC8A7A052AFB24839B5E81EA9882C3A91D24436583E3707E43F9813`, the
`Suite.ps1` hash was
`2C1241C6B87B3780DAA69CABA1C9798D3B7709674195DEB5A56EF33F06F5E176`, and the
release workflow hash was
`8A5E3B7E0FB503AD38B153156EE1BDDE3AD86DD40EF6506502785AB4E458EE28`.
The 53-file full-gate input digest changed from
`AC78F825747E39E33D2680C81F9C9BB63C76233BB44FC18520B046F1940BEE1D` before
the run to `7488C3A93E8989D13999506E3C24DFBBAF67389DDDA596DE3229807527B55DC4`
after it. `infrastructure/release/Build.ps1` had a last-write time of
2026-10-13 17:27:47 during the run; its post-run SHA-256 was
`955641ABB547968A58129F354936F3C24DACB2D9740316A2D32657CDB826DC47`. The
tester did not edit source, test, or workflow files. The cause and pre-run
`Build.ps1` hash were not captured, so full-gate source identity is **not stable**;
the run must not be represented as source-stable evidence. No Git state was
inspected.

The console extraction retained aggregate counters but not the failed-case
names or diagnostics. Therefore the three failures cannot be identified exactly
from retained evidence; no case names are guessed and no suite or focused-test
retry was made. The observed workflow snapshot keeps the `publish` job disabled
with `if: false` and a fail-closed `publisher-wiring-not-enabled` step; the
independent trusted-receipt invocation remains unwired. This is a real readiness
blocker, not a green result or an ignored test, but it is not asserted to explain
all three failed cases. No acceptance PASS is claimed; exact failure attribution
requires a separate authorized diagnostic after source identity is stabilized.
