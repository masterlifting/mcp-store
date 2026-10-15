# SEC1 modern job composition — prepare / trusted builder / publication wiring

Status: **partial by design; publication is explicitly not enabled.** This record is the
factual companion to `Boundary.md` (frozen decisions), `Bootstrap.md` /
`Bootstrap-fix.md` (transport-only bootstrap), `Adapter.md` / `Adapter-trust.md`
(strict publish CLI + separate Bootstrap receipt channel), `Modern.md` (test
migration) and `Issue.md` (current-body policy). It records the job-composition
batch, the interfaces it wires, the interfaces it deliberately leaves closed, and
what is still unverified. It claims no APPROVE, no PASS, and no hosted run.

## Status and scope

Authored in this batch:

- `.github/workflows/release.yml` — replaced the credential-bearing `release` job
  with fresh hosted `prepare`, `builder` jobs and a disabled `publish` job.
- `infrastructure/release/Coherence.ps1` — replaced the retired `orchestrate` CLI
  markers with modern job/CLI invariants plus per-job strict invariants.
- `infrastructure/release/Sources.json` — added `**/Build.ps1` to `excluded`.
- `infrastructure/release/Build.ps1` — NEW trusted-builder transport glue.
- `.tasks/GHI-24/docs/review/Pipeline.md` — this record.

Deliberately **not** modified: `infrastructure/release/Bootstrap.ps1`,
`Runner.fs`, `Handoff.fs`, `Policy.fs`, `Orchestration.fs`, `Transaction.fs`,
`Publisher.json`, `PublisherClosure.fs`, `README.md`, every `tests/**` file
(including `tests/Coherence.fs`), every producer `Config.fsx` / `Build.fsx` /
`Pins.fsx`, and every consumer / Telegram / harness file.

`mayEdit=true` bounded to the files above; `mayBuild=false`, `mayTest=false`,
`mayDelegate=false`. Validation was source-level only (PowerShell parser, YAML
parser, JSON parser, and one local coherence-wiring run). No build, no test
execution, no live API, no upload, no credential or secret value read, no commit,
no push, no merge, no task-state change.

## Job composition

Five jobs, four of them fresh hosted and read-only, one holding the only write
scope and disabled:

| Job | Runner | Permissions | Builds/restores/FSI | Role |
| --- | --- | --- | --- | --- |
| `bootstrap` | `windows-latest` | `contents: read`, `actions: read` | SDK install + release-runner build | project the canonical toolchain, prove event SHA + safe main ancestry, coherence guard, bounded queue reconciliation |
| `tests` | `windows-latest` | `contents: read` | SDK install + producer/Expecto builds + `dotnet fsi` suites | non-publishing regression gate |
| `prepare ${{ matrix.component }}` | `windows-latest` | `contents: read` | SDK install + release-runner build, then `ReleaseRunner.exe prepare` | producer `Build.fsx` / `Pins.fsx` / test gate; emits the candidate transport only |
| `build-publisher` | `windows-latest` | `contents: read` | SDK install, then `Build.ps1 -Phase Stage` (restore) and `dotnet publish` over the staged closure, then `Build.ps1 -Phase Emit` | compile the trusted publisher runtime and emit the trusted receipt |
| `publish` | `windows-latest` | `contents: write`, `actions: read` | none (asserted) | the single write boundary — **`if: false`, not enabled** |

`prepare` and `builder` both `needs: [bootstrap]`; `publish` needs
`bootstrap` + `tests` + `prepare` + `builder`. `queue: max` and
`cancel-in-progress: false` are unchanged, so publication stays serialized and
pending runs are not replaced.

### Preparation (`prepare <component>`)

```text
ReleaseRunner.exe prepare "<componentId>/release/Config.fsx" "$RUNNER_TEMP/candidate/<componentId>"
```

The job sets no `GITHUB_TOKEN`, `MCP_RELEASE_DO_UPLOAD`,
`MCP_RELEASE_GITHUB_OWNER` or `MCP_RELEASE_GITHUB_REPOSITORY`, and `prepare`
refuses to start while any of them is set, so the preparation job cannot publish.
It uploads the candidate transport with exactly the two publisher-consumed
relative names and fails on a missing file:

```text
release-candidate-<componentId>
  <archive-name>.zip      (policy-derived, matched by *.zip inside the private candidate directory)
  distribution.json
```

`preparation.json` and `sha256.txt` are uploaded as a separate diagnostics
artifact (`candidate-preparation-<componentId>`) and are never part of the
candidate transport. The upload step's `artifact-id` and `artifact-digest` are
captured as the `prepare` job outputs `candidateArtifactId` /
`candidateArtifactDigest`; nothing downstream selects an artifact by name.

### Trusted builder (`build-publisher`)

```text
pwsh infrastructure/release/Build.ps1 -Phase Stage -Root . -StageRoot <temp>/builder-stage -OutputRoot <temp>/builder-output
  -SourcesPath infrastructure/release/Sources.json -PublisherClosure infrastructure/release/Publisher.json
  -Repository <repo> -BuilderPlatform <bootstrap platform>
dotnet publish <stage>/infrastructure/release/Release.fsproj -c Release -r <bootstrap platform>
  --self-contained true --no-restore -o <stage>/publish
  -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false
pwsh infrastructure/release/Build.ps1 -Phase Emit -Root . -StageRoot <temp>/builder-stage -OutputRoot <temp>/builder-output
  -Repository <repo> -EventSha <github.sha> -RunId <github.run_id> -RunAttempt <github.run_attempt>
  -Trigger <github.event_name> -BuilderSdk/-BuilderRuntime/-BuilderPlatform <bootstrap projection>
```

`Build.ps1 -Phase Stage` stages only the `Sources.json` path list outside the
checkout with repo-relative layout (so `../../infrastructure/Provenance.fs` still
resolves), writes its own staged `NuGet.config` with `<clear/>`, and restores with
`-p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false` so no
repository or machine build hook participates. It also validates the audited
`Publisher.json` closure (strict shape, event repository, non-empty roster path)
so the document the publisher will read is confirmed at the event SHA without
duplicating any policy value. The compile step publishes only the staged project
self-contained; `Build.ps1 -Phase Emit` then hashes the compiled output. No phase
runs a producer script, `Build.fsx`, `Pins.fsx`, a test, or FSI, and none parses
`Config.fsx`.

Builder outputs:

| Output | Source |
| --- | --- |
| `stagedProject`, `publishRoot` | `Build.ps1 -Phase Stage` |
| `envelopeSha256` | SHA-256 over the builder-authored inner envelope archive |
| `manifestSha256` | SHA-256 over `runtime-manifest.json` |
| `outerTransportPath` | the outer transport archive the platform uploads |
| `runtimeArtifactId` / `runtimeArtifactDigest` | `actions/upload-artifact` step outputs (`artifact-id`, `artifact-digest`) |

Artifacts: `release-publisher` (the outer transport archive containing the inner
envelope plus `runtime-manifest.json`) and `trusted-publisher-receipt`
(`receipt.json`). The platform outer digest and the builder inner envelope hash
stay in separate namespaces and are never conflated; the builder cannot and does
not predict the outer platform zip.

### Publication (`publish`) — blocked

The job exists with `permissions: contents: write` / `actions: read`, `if: false`,
and a first step that fails closed with the typed marker
`publisher-wiring-not-enabled` if the job is enabled early. Its intended wiring is
recorded as an inert comment inside the job. Nothing in the job installs an SDK,
builds, restores, runs FSI, runs tests, or verifies a toolchain.

## Trusted receipt and raw-artifact transport interfaces

`Build.ps1` writes `receipt.json` in the exact strict shape
`ReleaseHandoff.parseTrustedPublisherReceipt` accepts (unknown fields are
rejected), with values it computes rather than placeholders:

```json
{ "schemaVersion": 1, "repository": "...", "eventRevision": "<40-hex>", "runId": "...",
  "attempt": "...", "trigger": "...", "role": "candidate",
  "rosterPath": "infrastructure/release/Participants.json", "rosterSha256": "<64-hex>",
  "componentConfigPaths": { "<componentId>": "<config path>" },
  "componentConfigShas": { "<componentId>": "<64-hex>" },
  "runtimeFilePaths": [ "..." ], "runtimeFileDigests": { "...": "<64-hex>" },
  "runtimeFileSizes": { "...": <int> }, "builderSdk": "...", "builderRuntime": "...",
  "builderPlatform": "..." }
```

`runtimeFilePaths` / `runtimeFileDigests` / `runtimeFileSizes` are derived from the
published self-contained output, and the inner envelope is built from exactly the
same sorted file set, so the bootstrap's `Assert-ManifestMatchesDirectory` and the
receipt describe one closure.

Raw outer transport is fetched only by the transport-only bootstrap, never by
`download-artifact` (which cannot return the outer platform zip) and never by a
floating artifact name. Per-job expectation maps (the role map is keyed per
invocation, and the uploader job name is the rendered job name):

```json
{ "publisher": { "artifactName": "release-publisher", "uploaderJobName": "build-publisher", "component": null } }
{ "candidate": { "artifactName": "release-candidate-<componentId>", "uploaderJobName": "prepare <componentId>", "component": "<componentId>" } }
```

## Exact F# companion required (blocker)

Publication cannot be enabled from workflow source alone. Two compiled facts block
it, both read from the current `Runner.fs`:

1. **Trusted-receipt path.** `Runner.fs:1106` composes the trusted publisher
   receipt as `Path.Combine(candidateRoot, "receipt.json")`, and
   `Runner.fs:735` explicitly refuses a `--transport-evidence` path that collides
   with `<candidate>/receipt.json`. The receipt must therefore be read from an
   explicit, independently supplied path; copying the trusted receipt into the
   candidate directory would let the candidate directory carry the very document
   that authorises it, which is the self-report the SEC1 amendment removes.
2. **InstallRuntime role reachability.** `Runner.fs:154` sets
   `allowedRoles = { "candidate" }` and `Runner.fs:246-252` requires a non-empty
   `--component` for every role. `Bootstrap.ps1` emits `component: null` for the
   `publisher`/`runtime` `InstallRuntime` receipt, and `Adapter-trust.md`
   (lines 228-233) documents that the empty CLI `--component` must be accepted for
   those roles. `Interop.md` confirmed both halves empirically: `--component=`
   exits 4 with "missing required flag(s): --component", and any non-empty value
   exits 4 at the observed-component mismatch. The publisher/runtime
   `InstallRuntime` path is currently unreachable through `publish`.

Minimal companion change (owned by the compiled/engineer channel, not authored
here):

- add a mandatory explicit trusted-receipt path flag (for example
  `--trusted-receipt=<path>`), isolated against the candidate root and the
  workspace root exactly like `--transport-evidence`, and load
  `TrustedPublisherReceipt` / `PreparationReceipt` from it instead of from
  `<candidate>/receipt.json` / `<candidate>/preparation.json`;
- distinguish presence of `--component` from its value so a present empty value is
  accepted for `publisher` / `runtime` while `candidate` keeps a non-empty
  supported component, and reject role/component combinations outside that rule;
- add the matching regression coverage in the tester-owned project.

Until that lands, wiring the `publish` steps would either require copying the
trusted receipt into candidate data (rejected) or invoke a CLI shape that
currently fails closed at parse time. Neither is an acceptable intermediate state,
so the job is closed rather than half-wired.

## Coherence guard changes

`Coherence.ps1` no longer requires an `orchestrate` command. It now asserts:

- the retired `orchestrate` mode appears zero times (no compatibility alias);
- `prepare` derives its CLI path from `${{ matrix.component }}` and contains no
  participant literal;
- per-job blocks (`Get-JobBlock`, anchored at two-space indentation so a nested
  `steps:`/`matrix:` key cannot masquerade as a job) enforce: bootstrap projects
  and disables credential persistence; tests is read-only with exactly ten
  entrypoints; prepare and builder are read-only, token-free and upload their exact
  artifacts; publish alone holds `contents: write` and runs no SDK, build, FSI,
  test or toolchain verification;
- every action reference is a reviewed 40-hex commit (12 references, no floating
  tag) and `actions/checkout` is the reviewed pinned commit (4 uses);
- the publication token (`secrets.GITHUB_TOKEN`) appears zero times while
  publication is disabled, and `write` is not granted at workflow scope;
- `queue: max` / `cancel-in-progress: false` are retained.

`tests/Coherence.fs:57` still mirrors the retired `orchestrate` command in its
synthetic baseline (per `Modern.md`), and its synthetic `baselineWorkflow` /
drift fixtures still carry the legacy `Update.ps1 -Verify -Component …` and
`ReleaseRunner.exe orchestrate …` lines. Those fixtures, and the
`coherenceGuardChecksActualRosterAndWorkflowMarkers` case that consumes them, are
tester-owned and now stale: they must be migrated to the modern job blocks before
their guard assertions establish coherence against this workflow. The tester-owned
`modernWorkflowGaps` case (fresh prepare job, separate trusted builder, data-only
publisher) already matches this workflow and reports no gaps.

## Integration gaps (reported, not skipped)

- The read-only `tests` job lists the ten non-packaging Workflow entrypoints that
  exist at this HEAD. Two further entrypoints (the task supporting-artifact and
  task-scratch families) are expected from the pending main integration. They are
  **not** listed, because listing a not-yet-merged path would fail the loop's
  existence check; they must be added when that merge lands, taking the list from
  10 to the frozen 12.
- The upgraded producer source is on main and is **not** in this HEAD
  (`523f625014dbf81fb788fe5c11877fc7a9626382`). The integration coordinator's
  approval is pending. This workflow therefore **requires integration tests** after
  the merge; nothing here should be read as current-main readiness.

## Verification performed (source-level only)

- `[System.Management.Automation.Language.Parser]::ParseFile` over `Build.ps1`
  (2454 tokens) and `Coherence.ps1` (1012 tokens): **0 parse errors**.
- `python -c "yaml.safe_load"` over `.github/workflows/release.yml`: parses; jobs
  are exactly `bootstrap`, `tests`, `prepare`, `builder`, `publish`.
- `ConvertFrom-Json` over `Sources.json`: 15 paths, 14 imports, 22 excluded, no
  duplicate paths.
- Local coherence-wiring run against the new workflow:
  `pwsh -NoProfile -File infrastructure/release/Coherence.ps1 -Components '["dotnet","workflow"]'`
  → `cross-stage coherence ok: 48 assertions; roster=[dotnet, workflow]`, exit 0.
- A source-level reproduction of the tester-owned `modernWorkflowGaps` heuristics
  against the new workflow reports no gaps (fresh prepares job invoking
  `ReleaseRunner prepare`, a separate trusted-builder job containing
  `Publisher.json` and `dotnet publish`, a separate data-only publisher job
  depending on both).
- Not verified: `Build.ps1` was never executed, so its staging, restore, publish,
  archive, manifest and receipt outputs are unproven; `Runner.fs` compatibility of
  the generated receipt is unproven; no workflow job was run; no hosted
  publication was attempted. No `.taskdocs` compilation row was added because no
  build ran in this batch.

## Open wiring facts (not invented)

1. `job.check_run_id` is the intended `-UploaderJobId` source for the bootstrap
   (documented in the GitHub `job` context). It is not exercised here. If it
   proves unavailable at enablement, the fallback is the attempt-scoped jobs
   listing matched by the exact rendered job name; `Bootstrap.ps1` already
   requires `-UploaderJobId` up front, so an unavailable value is a hard wiring
   stop, not a placeholder.
2. `artifact-digest` form: `Bootstrap.ps1 -PlatformDigest` accepts both
   `sha256:<64 hex>` and bare 64-hex, so either documented form is accepted.
3. Signed-redirect host (`*.blob.core.windows.net` default) and the
   `X-GitHub-Api-Version` pin remain the open facts recorded in `Bootstrap.md`
   and `Bootstrap-fix.md`; the publish job must not be enabled before they are
   settled.
4. The platform exposes no artifact-to-job link; `Bootstrap.ps1` records
   `uploaderJob.artifactLinkLimit` and the attempt-scoped successful job is the
   strongest available binding.

No engineering APPROVE is asserted by this document.

## Batch: post-integration gate reconciliation (main `ed59d7cfe5360dbbeb3fd14dd7a0321b83c2ca1b`)

- Read-only `tests` job takes the non-packaging Workflow list from ten to twelve by
  adding `workflow/tests/TaskScratchCompatibilityTests.fsx` and
  `workflow/tests/TaskSupportingArtifactTests.fsx`, both of which exist at that main
  SHA and run from source with no built distribution; the frozen count of 12 is the
  integration assertion, so a missing path still fails closed at the loop's
  `Test-Path` check.
- `Coherence.ps1` exact-count gate moves from `-Expected 10` to `-Expected 12`
  ("twelve non-packaging Workflow entrypoints"); action-reference, pinned-commit, and
  SDK-projection assertions are untouched because this batch adds no `uses:` line and
  no version value.
- Append-only: the earlier "Integration gaps" bullet is superseded by this batch, and
  the two expected entrypoints land exactly as it predicted. `tests/Coherence.fs`
  fixture migration to the twelve-entrypoint list stays with the tester channel, and
  publication stays `if: false` with the `publisher-wiring-not-enabled` guard.

## Batch: historical fixture provisioning repair

- Blocker resolved: the retired step ran `gh release download` per roster participant
  with one shared `fixtureVersion = "v1.0.3"`, so the `workflow` leg requested the
  absent `workflow/v1.0.3.zip`; the pinned historical workflow archive is
  `workflow/v1.0.4.zip` (`65d274…`, 1,514,420 bytes).
- `.github/workflows/release.yml`, `tests` job, provisioning step only: the
  `fixtureVersion` loop is replaced by a bounded read of the REPO-authored descriptor
  `infrastructure/release/tests/Fixtures.json` (843 bytes,
  `0f32e8c1cc1c1adde4ae4e804f4f0529f49b6d8c151dfa9079cf9f9ff5e6762a`). The step now
  requires `schemaVersion == 1`, exactly two entries, a descriptor `componentId` set
  equal to the roster projection, a contained-basename `archiveFile`, and an `assetUri`
  equal to the fixed `https://github.com/masterlifting/mcp-store/releases/download/<componentId>/<assetName>`.
  No version, SDK or archive-name literal is authored in YAML; the step consumes the
  roster projection marker `RELEASE_COMPONENTS` exactly as before.
- Fetch and accept: each entry is fetched anonymously (`Invoke-WebRequest`, no
  credential, cookie or header) to `<archiveFile>.staged`, then its size and SHA-256 are
  compared against the descriptor before the staged file is moved to the final
  `<archiveFile>` path. Any missing, unroutable or mismatched fixture throws and fails
  the step; no bypass, skip or fallback exists.
- `GH_TOKEN` is removed from the step environment, so the step no longer binds the
  platform token; the `tests` job header no longer claims the fixture step uses it.
- `infrastructure/release/Coherence.ps1`: the only guard edit is the
  "platform workflow token is the only credential value" marker count `10` → `9`, which
  the removed `GH_TOKEN` binding requires. The `RELEASE_COMPONENTS` fixture marker, the
  zero-match `*.zip` wildcard rule, the 12 pinned action references and every per-job
  assertion are unchanged.
- Root: `$RUNNER_TEMP/mcp-historical-fixtures` carries `Dotnet.zip` and `Workflow.zip`
  and `MCP_RELEASE_HISTORICAL_ROOT` is exported to `GITHUB_ENV`. The descriptor is read
  from the checkout because `Historical.fs` resolves it from `__SOURCE_DIRECTORY__`;
  `Published.md`'s "CI copies the REPO descriptor into the caller-supplied HistoricalRoot"
  sentence is imprecise on that point.
- Source-level validation only: YAML `safe_load` (fixture step env is `RELEASE_COMPONENTS`
  alone; no `gh release download`, no `fixtureVersion`); 20/20 `run:` blocks parse after
  GitHub-expression substitution; `Coherence.ps1` parses with 0 errors and reports
  `cross-stage coherence ok: 88 assertions; roster=[dotnet, workflow]` exit 0; and a
  controlled extracted-step stub run over 8 cases in a runner-temp directory accepted the
  matched pair (exit 0, no `.staged` remainder) and exited 1 for a size mismatch, a hash
  mismatch at the real pinned dotnet size, a non-canonical URI, a roster mismatch, a
  missing descriptor, an absent published asset and an escaping `archiveFile`.
- Not executed: no live fetch, no hosted Actions run, no build and no compiled Expecto
  run; the engineer rebuild and tester execution of the historical gate remain separate.
