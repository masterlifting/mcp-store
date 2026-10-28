# GHI-50 verification evidence

Evidence captured 2026-10-28 on Windows, .NET SDK `11.0.100-rc.1.26425.128`,
Expecto `10.2.3`. This page distinguishes the historical pre-repair comparison
from the current repaired working-tree evidence. Neither is merge or release
approval.

## Current post-repair evidence

The full Expecto suite passed: 157/157, 0 ignored, 0 failed, 0 errored, exit 0.
The native test-DLL command and matching apphost rerun both produced that result.
The test DLL SHA-256 was
`525A378B2B433DA3F9D8B91EE9280DC8B00833AD4C4EE196F7C2B1117FF502E0`. At capture
time, the Workflow Release build (run
`rJgbwcYXp9yaY8d7_r7OD5imNL-HxOtPSLTwpmvMj24`) and .NET test-project Release
build (run `NcobS-2umQWXPX5RcSXUFr9Ia3azg6UyLCcQLTtgnfE`) each reported 0
errors/warnings. MCP run-ID retention is bounded: IDs may expire and are not
durable public evidence. The test project's build references the .NET producer
`Mcp.Dotnet.fsproj`, not Workflow; Workflow has its own separate build run above.

The authorized repair restored direct Workflow domain record-pattern bindings,
moved F# test async work behind one outer caller bridge, and registered the
exact checker path/shape. Source probing found 0 violations; the bridge occurs
once at `EvidenceIdentifierV2Tests.fsx:110`. Wrong-path, missing-document,
historical nine-bridge, and repeated-bridge negative controls remained rejected.
No checker exemption logic was changed, no broad exemptions were added, and no
new test cases were added. Eight existing Workflow suites passed: Evidence
Identifier v2, Workflow, Task Contract, Task Path Safety, Task Supporting
Artifact, Schema Parity Guard, Task Profile, and Authority Metadata (all exit
0). These results establish the local repair gate only.

Privacy evidence was filenames-only: the actual `rg -l` procedure on a
synthetic 12-file fixture emitted three matching paths and no sentinel content;
the `-n` negative control emitted content. These and the test-DLL hash were
child-owned console observations, not retained head-gate log transcripts. No
real user documents were read. The privacy procedure is documented in
[Cutover.md](Cutover.md); no privacy script was committed and no new automated
privacy test was added. `.tasks/GHI-50/scripts/CheckerProbe.fsx` was a local,
non-committed supporting artifact used only for checker scanning; it is not a
privacy probe or a public, reproducible dependency. Coherence reported 174
assertions passing. The 157/157 gate, checker-probe, eight FSI suite, and
Coherence logs are retained under `.tasks/GHI-50/docs/logs/head-gate-*.log`;
the DLL hash, manual negative controls, and 12-file privacy observations are
not asserted to have retained transcripts. No process/MCP suites were
rerun for this follow-up, so prior 13/13 evidence is historical, not a fresh
claim. No fresh Unix/process coverage is asserted.

## Historical pre-repair comparison

The original comparison was base `33a2fcc` versus pre-repair HEAD
`bb1aa4c`; both recorded the same 156/157 result and identical 11 checker
findings. The detailed historical evidence follows.

| Check | Base | Head |
| --- | --- | --- |
| Full Expecto console suite | exit 1; 156 passed, 1 failed, 0 errored, 0 ignored (157) | Same |
| Failure | Repository sync-over-async checker | Same case and same 11 findings |
| Focused Workflow scripts | 4/4 exit 0 | 4/4 exit 0 |
| Supporting-document edit smoke | Not run at base | 2 runs, exit 0 each; see reproducible manual procedure below |

The full .NET gate was red at both revisions. On 2026-10-28, the owner
accepted the identical BASE/HEAD 156/157 result as a separately owned baseline
failure, with a hard gate requiring separate repair before merge or release;
no suppression or waiver is authorized. Finding-list normalized
SHA-256 at each revision: `66EAB96A74F0048DB65434E3FFCA8303AC5132652F6D0D533AE72ADD208500B9`.
This establishes unchanged findings between these revisions; it is not a green
gate and does not imply merge or release approval.

## Reproduction and provenance

Build each exact source revision with the dedicated Dotnet MCP `build`
capability, targeting the test project in Release; its project reference builds
the .NET producer project, not Workflow. No raw
`dotnet build`/`dotnet test` fallback is part of this procedure. Retained
historical baseline test-project build evidence (including the producer project
reference): run `jjXEFBJAq5kcPfTnOzfyAFYlPWeyCNctXM3BLj40kiU`, recorded at capture
as 0 errors, 0 warnings. Historical head test-project build evidence: run
`kvp6I6wM-TgUhBoo8odQKWfk63nHC3AS3ujf5iztgao`. Those historical run results
were recorded at capture; run-ID retention is bounded, so details may expire.
At the later evidence check, the Workflow run ID above returned
`UNKNOWN_RUN_ID`, while the test-project run ID still resolved. This availability
is time-dependent; neither ID is durable public evidence.
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

### Historical focused Workflow checks

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

## Remaining gates and retained evidence

- **Full .NET gate:** the local repair evidence above now satisfies the
  previously required full 157/157 gate. There was no waiver or suppression;
  the original owner disposition remains historical policy, not merge/release
  approval.
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
