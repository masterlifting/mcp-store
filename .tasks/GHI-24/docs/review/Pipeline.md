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
