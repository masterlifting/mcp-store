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
