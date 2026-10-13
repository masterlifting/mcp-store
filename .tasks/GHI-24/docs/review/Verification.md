# Producer verification evidence

## Scope and source identity

This record reports verification of the current local source tree, not an
acceptance decision. The checkout is at `HEAD`
`05a69ced8319b61a6bf0e24cad6d320a0a879283`, with tracked modifications and
untracked files; the tested source therefore has no commit identity or clean
provenance. The `.worktrees/ghi-36` nested checkout is also present and is
untracked in this tree. The tester did not build; the engineer-owned build is
recorded below.

Before test commands, the upload and GitHub workflow variables
`MCP_RELEASE_DO_UPLOAD`, `GITHUB_TOKEN`, `GITHUB_ENV`, and `GITHUB_OUTPUT` were
removed from the process environment. No upload or remote GitHub observation
was made.

## Dotnet producer

From `dotnet/`, the no-build Expecto run was:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue; & dotnet run --project tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj --configuration Release --no-build; exit $LASTEXITCODE
```

Expecto ran all **151** cases: **150 passed, 0 ignored, 1 failed, 0 errored**.
The failure was `sync-over-async checker.repository F# surface.all active root,
dotnet, and workflow source files are checked`. The checker reported 27
`Async.RunSynchronously` findings in `.worktrees/ghi-36` (including production
bridges and script test entrypoints), ending with `Should be empty.` The active
test was not skipped or weakened. This run used the existing Release test
assembly; it did not build. The registered nested worktree is a separate
checkout, not part of the primary producer source tree. The scanner had walked
into it because it recursively enumerated source extensions without recognizing
descendant Git checkout boundaries; the emitted 27 findings all had the
`.worktrees/ghi-36/` prefix.

The focused provenance script passed:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue; dotnet fsi dotnet/tests/ProvenanceTests.fsx; exit $LASTEXITCODE
```

Result: `provenance contract passed` (exit 0).

The Dotnet `DistributionTests.fsx` packaging test was not run: the documented
packaging flow performs a build and requires a clean source tree, while this
assignment prohibited building and the checkout is dirty. Thus no current
Dotnet package or package provenance was validated.

## Workflow producer script tests

All 12 files with the `test entrypoint` role under `workflow/tests/` were
executed individually with `dotnet fsi`; no test-entrypoint filter was applied.
The four `entrypoint/command` scripts and `MutexAbandonmentHelper.fsx` are
commands/helpers, not suite test entrypoints. Results:

| Result | Count | Entry points |
| --- | ---: | --- |
| Passed (exit 0) | 10 | `AuthorityMetadataTests.fsx`, `EphemeralLockInvariantTests.fsx`, `SchemaParityGuardTests.fsx`, `TaskContractTests.fsx`, `TaskPathSafetyTests.fsx`, `TaskProfileTests.fsx`, `WorkflowMcpFindingTests.fsx`, `WorkflowMcpNativeCompatTests.fsx`, `WorkflowMcpTests.fsx`, `WorkflowTests.fsx` |
| Blocked by clean-source provenance guards (exit 1) | 2 | `DistributionTests.fsx`, `PackagedRuntimeCoordinationTests.fsx` |

The distribution test stopped when `workflow/release/Build.fsx` refused to
package from the dirty tree. The packaged-runtime coordination test stopped
before packaging because it requires a clean source tree. Neither produced a
package or validated package provenance. The remaining ten scripts passed;
some emitted informational `FS3886` diagnostics. No test was ignored.

The executed script command was:

```powershell
$envNames = @('MCP_RELEASE_DO_UPLOAD', 'GITHUB_TOKEN', 'GITHUB_ENV', 'GITHUB_OUTPUT'); foreach ($name in $envNames) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }; $tests = @('workflow/tests/AuthorityMetadataTests.fsx', 'workflow/tests/DistributionTests.fsx', 'workflow/tests/EphemeralLockInvariantTests.fsx', 'workflow/tests/PackagedRuntimeCoordinationTests.fsx', 'workflow/tests/SchemaParityGuardTests.fsx', 'workflow/tests/TaskContractTests.fsx', 'workflow/tests/TaskPathSafetyTests.fsx', 'workflow/tests/TaskProfileTests.fsx', 'workflow/tests/WorkflowMcpFindingTests.fsx', 'workflow/tests/WorkflowMcpNativeCompatTests.fsx', 'workflow/tests/WorkflowMcpTests.fsx', 'workflow/tests/WorkflowTests.fsx'); $failed = [System.Collections.Generic.List[string]]::new(); foreach ($test in $tests) { Write-Output "::group::$test"; & dotnet fsi $test; $code = $LASTEXITCODE; Write-Output "SCRIPT_EXIT $test $code"; Write-Output "::endgroup::"; if ($code -ne 0) { $failed.Add("$test (exit $code)") } }; if ($failed.Count -gt 0) { Write-Output "WORKFLOW_TEST_FAILURES $($failed -join '; ')"; exit 1 } else { Write-Output "WORKFLOW_TESTS_PASSED $($tests.Count) deterministic F# script test entrypoints"; exit 0 }
```

No network or real upload was performed.

## Previously completed release suite

The complete compiled release suite is not repeated here. The preceding tester
run recorded **26 passed, 0 ignored, 0 failed, 0 errored**, with inventory and
provenance gates passing; its full command and nested suite counts remain in
the existing Stage 4 handoff (`Migration.md`). This verification adds no new
claim about that already-executed suite.

## Verification status and remaining gaps

- The 151-case Dotnet result above predates the bounded traversal correction
  and remains a failing historical run, not verification of the corrected code.
- The checker now stops at descendant directories containing a `.git` file or
  directory marker, without reading marker contents; the supplied root itself
  remains scanned. It does not hard-code `.worktrees` or exempt any blocker
  pattern.
- Paired regression coverage verifies that a root `.git` file does not suppress
  primary-source findings, ordinary subdirectories remain scanned, and source
  below descendant `.git` file and directory markers is not enumerated.
- **W6 engineer build (dedicated Dotnet MCP, Release):**
  `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` runId
  `zfzcMf3LRLqErzqmCYmv5Kr_IppqWd4OUfSfX1qtd9I`, status succeeded,
  exitCode 0, errors 0, warnings 0 (durationMs 16829). The W6
  bounded checker scan + paired regression coverage compiled clean;
  Git marker file/dir descendants halt the primary-root scan and
  the production strict checks remain unchanged. No production
  source was edited and the current Release 26-case W5 tests +
  producer builds were not repeated. No foreign worktree, commit,
  runtime, or network operation was performed.
- **W6 full Dotnet Expecto run after that build:** from `dotnet/`, with upload
  and GitHub workflow environment variables cleared:

  ```powershell
  Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue; & dotnet run --no-build --no-restore --configuration Release --project tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj; exit $LASTEXITCODE
  ```

  Result: exit 0; **154 run, 154 passed, 0 ignored, 0 failed, 0 errored** in
  `00:02:01.1220585`. This includes the three new checkout-boundary regressions
  and the primary repository-surface scan. The nested foreign worktree's
  contents were not read or modified. No release or workflow suites were
  repeated.
- Workflow script coverage is incomplete for a green suite: two package-related
  entrypoints correctly stopped at dirty-tree guards. No package was built or
  authenticated from a clean committed source tree.
- Dotnet distribution packaging was not run because it would build and the tree
  is dirty. A committed, clean-source packaging/provenance run remains needed.
- These are simulated/local tests only. No remote HTTP/GitHub observation,
  package upload, publication, or acceptance verdict is evidenced here.
- Independent review and any acceptance decision remain outstanding. This
  record does not mark any acceptance criterion PASS.

## GHR-36 full release-gate execution (first built snapshot)

After the engineer-owned build of
`infrastructure/release/tests/Tests.fsproj`, run
`q4G9UAiwL5Gv_8GkHlzu9r_U0WWQzG3RF0TQolfBx9A` succeeded with zero
errors and zero warnings (verified through retained Dotnet details).
The full Suite gate then ran without rebuilding the assembly. Before
launch, publication credential, upload, workflow-output, and test-probe
environment names were removed from the process environment without
reading their values:

```powershell
$envNames = @('GITHUB_TOKEN', 'GH_TOKEN', 'MCP_RELEASE_DO_UPLOAD', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'MCP_RELEASE_GITHUB_OWNER', 'MCP_RELEASE_GITHUB_REPOSITORY', 'MCP_RELEASE_TEST_PROBE_DIR', 'MCP_RELEASE_TEST_TRANSACTION_PROBE_FILE'); foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }; & pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap; exit $LASTEXITCODE
```

Expecto reported **33 run, 28 passed, 0 ignored, 4 failed, 1 errored**
(suite exit 3); `Suite.ps1` exited 1. Inventory count matched execution,
so no compiled case was omitted. The nested `ProvenanceTests.fsx`
passed. Nested summaries: bootstrap 9/9, evaluate 70/70, historical
bootstrap 10/10, invocation 10/10, metadata 52/52, publish 62/62,
revision 22/22, producer PE integration 2/2, runner 22/22, Telegram
isolation 22/22.

The failures were one stale Enrollment wording assertion, three
Coherence fixture omissions for current projection wiring, and one
orchestration test-host dispatch ordering error. These causes were
bounded from the retained test failures and source. Test-only fixes were
subsequently made in `Enrollment.fs`, `Coherence.fs`, and `Main.fs`;
they are not in this assembly and have not been rebuilt or executed.
The unregistered-config side-effect sentinel and Workflow second-test
failure case were among the 28 passing cases; that Workflow failure case
checks both package-test subprocess environment probes and absence of a
transaction child. The transaction-success test errored before its
transaction child ran, so positive transaction-auth retention and copied
test-apphost execution remain unverified.
This run is historical failure evidence and does not establish current
source verification, clean provenance, package verification, or an
acceptance PASS. The engineer must rebuild the release test project and
the full gate must pass before independent security review proceeds.

## GHR-36 full release-gate rerun (after test-only corrections)

The engineer-recorded test-project build
`V1MB-_0Oj5v7VI3q48BxsZ_YzThZA1Abe7Xn3Fbiimg` was confirmed through
retained Dotnet details: zero errors, zero warnings, no additional
pages. The user-supplied variant ending in an extra `0` was unknown to
the details service. The compiled suite ran without rebuilding, with
publication, upload, workflow-output, and probe environment variables
removed by name without reading values:

```powershell
$envNames = @('GITHUB_TOKEN', 'GH_TOKEN', 'MCP_RELEASE_DO_UPLOAD', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'MCP_RELEASE_GITHUB_OWNER', 'MCP_RELEASE_GITHUB_REPOSITORY', 'MCP_RELEASE_TEST_PROBE_DIR', 'MCP_RELEASE_TEST_TRANSACTION_PROBE_FILE'); foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }; & pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap; exit $LASTEXITCODE
```

Expecto and the Suite inventory agreed: **33 run, 32 passed, 0 ignored,
1 failed, 0 errored**; the compiled suite and Suite gate both exited 1.
Historical bootstrap assertions passed 10/10, and the producer
`ProvenanceTests.fsx` entrypoint passed. The single failure was the
Coherence workflow fixture passing an output-line prefix where the
production `-Components` parameter requires raw JSON. Three fixture
values were corrected in test source after this run; that correction
has not been rebuilt or executed.

The unregistered-config sentinel, Workflow second-test failure gate,
and producer success/credential-boundary tests were among the 32
passes. The copied test apphost ran as a transaction-subprocess probe;
it recorded boolean-only environment observations, demonstrating
scrubbing before transaction and retention at the transaction child.
This is local test-probe evidence, not a production upload. The
historical archives were checked from the supplied local fixture;
HTTP tests used controlled handlers. No live GitHub observation or
upload occurred. Producer packaging tests remain unrun pending a future
approved clean-provenance checkpoint. Independent security review must
wait for a rebuilt full-gate pass; this run is not approval.

## GHR-36 Suite attempt after the raw-JSON fixture build

Engineer build `MeY3nNWUjrgU_IBVbXzeMuQdfr1ZFiiWXvCLkU0SPBE` was
confirmed by retained Dotnet details: zero errors and zero warnings.
The complete Suite command was run without rebuilding, with publication,
upload, workflow-output, and test-probe environment names removed before
launch without reading their values. It used a bounded 300-second
foreground timeout and emitted only the compiled-suite group header
before timing out. The Suite had passed its inventory preflight and
entered the compiled suite, but no Expecto count, actual inventory
comparison, provenance result, or final exit code was returned. Do not
infer a pass or failure count from this incomplete attempt. The previous
completed run remains 33 run, 32 passed, 0 ignored, 1 failed, 0 errored;
that Coherence test-fix result remains pending a completed full run.

### Timeout diagnosis — blocked, no retry

`Suite.ps1` completes `--list-tests` and validates the Historical
inventory before printing the group header. It then invokes the full
Expecto command with `2>&1` captured into a PowerShell variable and
prints those lines only after the child exits. The lone header places
the wait in/after the compiled-suite invocation but cannot isolate a
test or reveal incremental output.

Process inspection was restricted to PID, parent PID, name, and creation
time. No active `dotnet.exe` compiled test process appeared in the
post-timeout snapshot; listed Dotnet processes predated this invocation.
An active `pwsh.exe` PID 15528 (parent 24904, 19:33:15) had descendants
`claude.exe` PID 27804 (19:33:27) and `cmd.exe` PID 27036 (19:33:40).
This tree cannot be confidently attributed to the Suite rather than the
tool host. No process was killed. Whether the timed-out Suite child was
terminated or ended independently is undetermined.

The >300-second cause is **not established**. No duplicate suite or
focused test was launched because the process ownership/status is
ambiguous. Next: confirm the tree owner and that no Suite/Expecto child
remains; then run one bounded Expecto-filtered corrected Coherence case
with retained output. The current 33-case gate is not a PASS.

## GHR-36 bounded diagnosis follow-up: isolated and full live run

The corrected production-Coherence fixture case passed **1/1** in
8.38s; the complete Coherence test list passed **4/4** in 14.83s. The
Expecto help output identifies `--filter-test-list` as the list filter.
The attempted `--filter-test-case 'cross-stage coherence'` selected 0
tests and is not counted as verification; the corrected list filter was
then used.

After those passes, one full, unfiltered live Expecto invocation was run
with `--sequenced --no-spinner`, the approved local Historical root, and
credential/upload/workflow-output/probe environment names removed by
name without reading values. The `--list-tests` inventory reported 33
cases, including Historical. Full run result: **33 run, 33 passed, 0
ignored, 0 failed, 0 errored**, exit 0, duration 4:22.1691527. Historical
fixture assertions passed 10/10; `dotnet/tests/ProvenanceTests.fsx`
passed (exit 0).

The producer credential-boundary test passed using local child
processes and the copied test apphost as a boolean-only transaction
probe. This verifies the test boundary, not a production upload. HTTP
tests used controlled handlers; no remote GitHub observation or upload
occurred.

The complete Expecto suite can finish within the timeout when invoked
directly with output visible, and the corrected Coherence group is not
the source of a persistent hang. The earlier `Suite.ps1` timeout root
cause remains unknown; its wrapper buffers child output, while the
post-timeout process tree was ambiguous. The Suite wrapper was not
rerun, so do not claim that wrapper passed. No source change was made.
Clean-provenance producer packaging and independent review remain
pending; this evidence alone is not an approval.

## GHR-36 exact Suite.ps1 wrapper verification

The exact non-publishing `Suite.ps1` wrapper was run with a 600-second
foreground budget, the same approved Historical root, current built
test assembly, and credential/upload/workflow-output/probe names
removed by name without reading values. It completed with exit 0 and
reported: `release regression gate passed: 33 compiled case(s)
(including 1 historical) plus 1 producer script(s)`.

Expecto: **33 run, 33 passed, 0 ignored, 0 failed, 0 errored**, duration
4:17.0979069; inventory matched. Historical fixture assertions passed
10/10, and `dotnet/tests/ProvenanceTests.fsx` passed. Credential boundary
tests observed boolean-only scrubbing/retention at the local child
process boundary, including the copied test apphost probe. These are
test probes, not a real release transaction. HTTP behavior was
simulated; no remote observation or upload occurred.

This verifies the current regression wrapper. It does not establish why
the earlier 300-second attempt timed out. The prior timeout diagnosis
remains unresolved, with no speculative timeout fix or source edit.
Clean-provenance producer packaging and independent security review
remain pending; this is not an acceptance decision.
