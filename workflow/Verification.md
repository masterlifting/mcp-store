# GHI-50 verification evidence

Evidence captured 2026-10-28 on Windows, .NET SDK `11.0.100-rc.1.26425.128`,
Expecto `10.2.3`. Compared base `33a2fccca29c57f71239acd84926ef8e55f40e4d`
(tree `f8fd6ced92d95bd4a55476502bcb33dd66604d5a`) with head
`a54b4711f8be7cc2fad254af3697a3c6dcced61d` plus the documented, uncommitted
working-tree refinements. This is evidence, not approval or a gate waiver.

## Results

| Check | Base | Head |
| --- | --- | --- |
| Full Expecto console suite | exit 1; 156 passed, 1 failed, 0 errored, 0 ignored (157) | Same |
| Failure | Repository sync-over-async checker | Same case and same 11 findings |
| Focused Workflow scripts | 4/4 exit 0 | 4/4 exit 0 |
| Supporting-document edit smoke | Not run at base | 2 runs, exit 0 each; see reproducible manual procedure below |

The full .NET gate remains red at both revisions. On 2026-10-28, the owner
accepted the identical BASE/HEAD 156/157 result as a separately owned baseline
failure, with a hard gate requiring separate repair before merge or release;
no suppression or waiver is authorized. Finding-list normalized
SHA-256 at each revision: `66EAB96A74F0048DB65434E3FFCA8303AC5132652F6D0D533AE72ADD208500B9`.
This establishes unchanged findings between these revisions; it is not a green
gate and does not imply merge or release approval.

## Reproduction and provenance

Build each exact source revision with the dedicated Dotnet MCP `build`
capability, targeting the test project in Release; its project reference also
builds the producer project. No raw
`dotnet build`/`dotnet test` fallback is part of this procedure. Retained
baseline test-project build evidence (including the producer project reference): run
`jjXEFBJAq5kcPfTnOzfyAFYlPWeyCNctXM3BLj40kiU`, 0 errors, 0 warnings. Retained
head test-project build evidence: run
`kvp6I6wM-TgUhBoo8odQKWfk63nHC3AS3ujf5iztgao`.
The suite is an Expecto console executable; VSTest returned no case counts
(`trxAvailable=false`), so invoke the built executable with `--summary`.

For a comparable base run, materialize the base source in a fresh,
workspace-relative evidence snapshot, then build the exact test project with the
dedicated Dotnet MCP `build` capability in Release. Its project reference builds
the producer project. Copy the matching built source tree, including its build
outputs, into a fresh operator-chosen external path outside any `obj`, `bin`,
`dist`, `.tasks`, `node_modules`, or `publish` path segment. The scanner excludes
those path segments, and the Expecto stdio cases require the exact matching
producer build output. The baseline snapshot under `.tasks/GHI-50/docs/base` is
selected .NET producer source, not Workflow source or a complete repository;
do not use it as the scan root or imply it is a full repository snapshot.
Verify tracked source hashes in
the materialized source against `git rev-parse 33a2fcc:<path>` before running.
Run from each complete source root:

```powershell
dotnet "<base-root>\dotnet\tests\Mcp.Dotnet.Tests\bin\Release\net11.0\Mcp.Dotnet.Tests.dll" --summary
dotnet dotnet\tests\Mcp.Dotnet.Tests\bin\Release\net11.0\Mcp.Dotnet.Tests.dll --summary
```

The built baseline suite and producer came from the exact baseline tree; the
head command uses the matching head build. Both actual runs exited 1 with 156
passed, 1 failed, 0 errored, 0 ignored (157 cases). Failure:
`Mcp.Dotnet.sync-over-async checker.repository F# surface.all active root,
dotnet, and workflow source files are checked`.

The 11 findings were identical:

```text
BLOCK workflow/domain/Workflow.fs:4825:91 [Task.Result]
BLOCK workflow/domain/Workflow.fs:4984:41 [Task.Result]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:50:56 [Async.RunSynchronously]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:57:226 [Async.RunSynchronously]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:61:100 [Async.RunSynchronously]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:62:99 [Async.RunSynchronously]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:65:94 [Async.RunSynchronously]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:73:86 [Async.RunSynchronously]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:84:23 [Async.RunSynchronously]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:87:135 [Async.RunSynchronously]
BLOCK workflow/tests/EvidenceIdentifierV2Tests.fsx:94:48 [Async.RunSynchronously]
```

## Focused Workflow checks

These four existing scripts passed at both revisions:

```powershell
foreach ($script in @("workflow/tests/TaskSupportingArtifactTests.fsx", "workflow/tests/TaskContractTests.fsx", "workflow/tests/AuthorityMetadataTests.fsx", "workflow/tests/WorkflowTests.fsx")) {
  dotnet fsi --exec --quiet $script
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
```

The host was Windows. The supporting-artifact suite explicitly skipped Unix
special-file cases. The F# script source was scanned by the Expecto repository
checker, but this is not a full .NET build/test gate.

### Supporting-document edit procedure

The prior smoke was run twice by the initial tester and twice by the comparison
tester; it was not run by the producer engineer. Its ignored `Smoke.fsx` is not
a reproducible dependency and is not cited here. To repeat the behavior check,
 an operator may use the existing command entrypoints in an isolated disposable
 dummy project, with an unused task ID and no real task data:

1. Confirm that the isolated dummy project's chosen task path has no existing
   task data. `TaskCreate.fsx` rejects an occupied task path.
2. Create a disposable task using `workflow/tests/TaskCreate.fsx`; follow its
   usage text: `<project-root> <TASK-ID> <title>`. Successful creation creates
   `runtime.json`, not a `docs/` directory.
3. After successful creation, create the task's `docs/` directory and write the
   initial neutral supporting document and evidence there. Capture
   `TaskGet.fsx` and `TaskValidate.fsx` output and the created `runtime.json`
   SHA-256 as the pre-edit baseline.
4. Edit the supporting document under `docs/`. Do not parse or edit
   `runtime.json`.
5. Re-run get and validate; compare their output with the pre-edit output and
   compare the runtime hash with the pre-edit hash. Expected: task output
   remains unchanged, validation succeeds, and the runtime hash is unchanged.

Example command forms (replace placeholders with operator-chosen values):

```powershell
dotnet fsi --exec --quiet workflow/tests/TaskCreate.fsx <project-root> <TASK-ID> "dummy"
dotnet fsi --exec --quiet workflow/tests/TaskGet.fsx <project-root> <TASK-ID>
dotnet fsi --exec --quiet workflow/tests/TaskValidate.fsx <project-root> <TASK-ID>
```

This is an operator procedure, not a claim that these commands were run in
this comparison. The prior smoke evidence reported two successful repetitions.

## Pending gates and retained evidence

- **Full .NET gate:** owner disposition received 2026-10-28: accept the
  identical BASE/HEAD findings as a separately owned baseline failure, keep the
  full gate red, and require separate repair before merge/release. No
  suppression or waiver. Repair remains outstanding.
- **Unix verification:** not performed. Docker's Linux engine was unavailable
  (`npipe:////./pipe/dockerDesktopLinuxEngine`); WSL listed only stopped
  `docker-desktop`. No runtime was started or downloaded. Root-leaf read-only
  and hidden classification is not currently exercised after fixture
  retirement. A targeted run on the pinned .NET 11 target/runtime remains
  required. The owner approved this as a release gate on 2026-10-28: targeted
  Workflow filesystem scripts must pass on the pinned Unix runtime before
  producer promotion; packaging/Resolver CI is insufficient. No Docker
  start/download, deployment/live replacement, waiver, or claim of passed tests
  is authorized. Verification remains outstanding.
- **External inventory and operator approval:** remain pending as described in
  [Cutover.md](Cutover.md); the owner dispositions are not approval to
  merge/release or to perform deployment/live replacement.

This report makes no user-data changes. Generated logs were preserved
byte-for-byte under `.tasks/GHI-50/docs/logs/` after coordinator relocation;
they are supporting evidence, not committed public dependencies.
