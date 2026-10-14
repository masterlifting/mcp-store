# Workflow three-job cutover

Status: the YAML/PowerShell cutover is AUTHORED in the working tree on `ghi-24`
and passes the local semantic guard. Readiness remains UNKNOWN and this note is
not an approval; the tester-owned compiled fixture migration and the hosted
publication smoke are still outstanding.

Landed by this slice (working tree, uncommitted):

- `.github/workflows/release.yml` — three frozen jobs; only the publication job
  holds write authority. The publication job transfers all three artifacts with
  the pinned official `actions/download-artifact` commit
  (`d3f86a106a0bac45b974a628896c90dbdf5c8093`, the reviewed `v4` commit),
  addressed only by the exact numeric `artifact-ids` declared as job outputs by
  the two read-only jobs, into isolated `runner.temp` roots. The hand-written
  REST ZIP downloader, its bearer headers and redirect handling, the manual ZIP
  hashing and the `Expand-Archive` extraction are removed; the action's digest
  observation is warning-only, so the independent inventory remains the
  fail-closed proof.
- Verify before execute: the publisher job's own complete file/SHA-256
  inventory is consumed as a job output through `Verify.ps1 -InventoryJson`
  before anything from the downloaded set runs, and the candidate directory is
  proven with `Verify.ps1 -MembersJson (ConvertTo-Json -Compress @($archiveName,
  "distribution.json"))` — one JSON argument instead of a multi-value script
  parameter.
- `infrastructure/release/Coherence.ps1` — 149 assertions covering the pinned
  download commit, the exact `artifact-ids`/`path` bindings per roster
  participant, the absence of any hand-written transport client, and the
  ordering "transfer → verify → execute".
- Deleted: `infrastructure/release/Bootstrap.ps1`, `Build.ps1`, `Resolve.ps1`
  (production consumers were already zero).
- `infrastructure/release/README.md` — retained-script inventory; the stale
  `Bootstrap.fsx` note is dropped.

Local validation performed here (no compiled build, no test execution, no live
API call, no token read):

- `yaml.safe_load` on `release.yml` parses and yields exactly the three jobs.
- PowerShell AST parse of `Verify.ps1`, `Coherence.ps1`, `Update.ps1`,
  `Roster.ps1`, `Typecheck.ps1`, `Suite.ps1`: zero parse errors.
- `Update.ps1 -Project` + `Coherence.ps1 -Components '["dotnet","workflow"]'`:
  `cross-stage coherence ok: 149 assertions` (exit 0).
- `Verify.ps1` host proof through the real `pwsh -NoProfile -File` argument
  shape: the exact two members pass (exit 0); an extra member, a missing member
  and a tampered inventory hash each fail closed (exit 1).
- Negative guard mutations (download-by-name, `merge-multiple`, reintroduced
  `Invoke-WebRequest`/`api.github.com`, `-Member` array regression, renamed
  transfer id, mis-bound `artifact-ids`, unpinned download action) each fail the
  guard.

Still outstanding and owned elsewhere: the tester migration for
`infrastructure/release/tests/Coherence.fs`, `Bootstrap.fs`, `Enrollment.fs`,
`Invocation.fs` and `Telegram.fs`, which still name the retired
`matrix`/`bootstrap`/`builder` wiring, `Bootstrap.ps1`/`Bootstrap.fsx` and
`--trusted-receipt`. `Bootstrap.fsx` was removed from the working tree by its
separate owner while this slice ran; no `.fsx`, `.fs`, `.json` or producer file
was edited here.

## Historical note (superseded): why the cutover could not be authored in the earlier slice

The frozen `Simplification.md` couples the `.github/workflows/release.yml` and
`*.ps1` surface to an F#/JSON companion that is owned by another actor and is
mid-flight. Baseline at `18146a7dca58a1f08ca58b6407ccbb2e244a9395` (clean tree,
`ghi-24`) had neither `Config.json` nor the new CLI; the companion then started
landing in the working tree while this slice was read-only, which is a
stop-and-ask condition (external edits appeared under this role).

Landed so far by the other owner (uncommitted, working tree):

- `dotnet/release/Config.json`, `workflow/release/Config.json` (new) preserving
  the nine fields `componentId`, `version`, `entryDll`, `sdkVersion`,
  `runtimeVersion`, `platform`, `archiveName`, `assetUri`, `publishedFiles`;
  both `*/release/Config.fsx` deleted.
- `Policy.fs` rewritten to a strict bounded `parseBytes` JSON reader
  (`System.Text.Json`, depth 8, unknown/duplicate field rejection).
- `Enrollment.fs` and `Participants.json` updated; `configPath` is now
  `dotnet|workflow/release/Config.json`.
- `dotnet|workflow/release/{Build,Pins}.fsx` and `infrastructure/Distribution.fsx`
  updated.

Still missing or untouched, so the YAML/PS cutover cannot be made valid:

- `Runner.fs:1253` still implements the retired
  `publish <publisher-closure-path> <candidate-transport.zip> --trusted-receipt
  --transport-evidence …` CLI. The proposed signature exists nowhere in the tree.
- `Sources.json`, `Publisher.json` and `PublisherClosure.fs` are still present;
  the closure/receipt/envelope removal has not landed.
- `infrastructure/release/tests/Coherence.fs`, `Telegram.fs` and `Authority.fs`
  still name `Bootstrap.ps1`, `Build.ps1`, `Resolve.ps1`, `Publisher.json` and
  `Coherence.ps1 -Components`; deleting those scripts without the tester-owned
  fixture migration breaks the compiled regression project.

The PS surface is now genuinely stale against the JSON cutover and is the part
this role owns: `Update.ps1` still parses deleted `Config.fsx` literals and runs
`Bootstrap.fsx`, and `Coherence.ps1` still asserts the
`<component>/release/Config.fsx` CLI path and the old five-job wiring. Those two
scripts plus `release.yml` need the rewrite below, but authoring it now would
reference a non-existent CLI, delete scripts the test project still needs, race
an active editor in the same tree, and could not be validated
(`mayBuild=false`, `mayTest=false`). That is a breaking, unverifiable
half-cutover, so it was not performed.

## Target shape (three jobs, no bootstrap job)

- `publisher` — read-only (`contents: read`). Checkout exact event SHA
  (`persist-credentials: false`), setup .NET from the canonical pre-SDK
  projection, verify the executing toolchain, then build the release application
  and run the complete non-publishing regression gate as one independent
  build+test job.
- `preparation` — read-only (`contents: read`), single job, no matrix; loops the
  two fixed roster components from the same projection data (small
  `Update.ps1 -Project` pre-SDK helper, same source data, no independently
  authored table). Runs `ReleaseRunner prepare` per component and uploads the
  candidate transport.
- `publication` — write (`contents: write`, `actions: read`), single job; loops
  the same two components. Public only; no SDK, build, restore, FSI or test.
  Downloads only the exact artifact IDs declared by the preparation job outputs,
  verifies every file against the independent inventory, then runs the verified
  publisher once per component.
- `publication.needs: [publisher, preparation]`; no job outputs bootstrap step.
- Preserve `on: push`/`main` with no path filter and no dispatch, `queue: max`,
  `cancel-in-progress: false`, and one publication concurrency group; preserve
  the actual toolchain identity setup and pinned official action SHAs.

## Removals

- This scope: `infrastructure/release/Bootstrap.ps1`,
  `infrastructure/release/Build.ps1`, `infrastructure/release/Resolve.ps1`.
- Other owner (F#/JSON): `Sources.json`, `Publisher.json`, `PublisherClosure.fs`,
  runtime envelopes, receipt schemas, and the `preparation.json` sidecar.
- Job surface: the `bootstrap` and `builder` jobs, the uploader-job resolver
  steps, and the `job.check_run_id` transport-provenance lookup.

## Candidate artifact and inventory binding

- The candidate transport is exactly two direct members: `<archive-name>.zip`
  and `distribution.json` (no `preparation.json`; the new core `prepare` omits
  the staging sidecar).
- The read-only preparation job emits a bounded two-field array
  `[{path, sha256}]` (standard JSON, about five files, ≤16 KiB) to
  `$GITHUB_OUTPUT`. The publication job verifies every downloaded file's member
  name and SHA-256 against it and fails closed on any extra, missing, mismatched
  or case-colliding member. Expected hashes are the read-only job's own
  observation, never candidate-asserted data, and the candidate-producing job
  cannot rewrite the expected inventory after the fact.

## Exact CLI proposal (needs Runner-owner confirmation; not final)

- `ReleaseRunner.exe prepare <Config.json> <outdir>`
- `ReleaseRunner.exe publish <Config.json> <candidate-directory>
  --workspace-root=<trusted checkout> --event-sha=<github.sha>
  --repo=<github.repository>`

No `--role`, `--component`, `--artifact-id`, `--artifact-name`,
`--transport-sha256`, `--builder-sdk/runtime/platform`, `--trusted-receipt` or
`--transport-evidence` flags; those surfaces retire with the closure/receipt
schemas. The event-SHA ancestry recheck stays native (`git rev-parse HEAD` plus
the existing bounded ancestry comparison) immediately before the single
token-bearing invocation.

## New script

- `Verify.ps1` — one purpose: verify a downloaded candidate directory against the
  expected two-field inventory and bind it to the publisher's declared artifact
  identity. Deterministic, no network, no token, no receipts. Proposed bound
  roughly 90–140 lines; no generic provenance framework.

## Semantic guard (`Coherence.ps1` rewrite)

Assert exactly the three job names and the absence of `bootstrap`/`builder`;
pinned 40-hex action refs; `queue: max` and `cancel-in-progress: false`; write
authority only inside `publication`; zero `secrets.`; a two-member candidate
upload; no `actions/download-artifact` wildcard or latest-name selection.

## Required F#/JSON companion (other owner)

Landed: canonical `dotnet|workflow/release/Config.json` preserving the nine
fields; strict bounded JSON policy reader; `Participants.json` `configPath`
updated to the JSON.

Pending: the `publish` CLI cutover to the proposed signature; removal of
`PublisherClosure`/`Handoff`/envelope/receipt plus `Sources.json`/`Publisher.json`;
and the tester-owned fixture migration for `tests/Coherence.fs`, `Telegram.fs`,
`Authority.fs`.

## Readiness

UNKNOWN. No APPROVE. The YAML/PS slice is authored and locally guard-validated,
but readiness is still blocked on (a) the tester-owned compiled fixture migration
listed above and (b) a hosted publication smoke. The blockers this note
originally recorded — the `publish` CLI cutover, the closure/receipt removal and
the explicit handoff before this role edits `release.yml`/`*.ps1` — have since
landed.

## SEC-1 minimal correction: pre-download artifact-id guard (working tree)

An independent security review of the download bindings (publication-job downloads
and the later declared-output checks) found that the pinned
`actions/download-artifact` treats a blank `artifact-ids` value as "every artifact
in this run", so an empty job output would silently widen the transfer instead of
failing closed. The later `Read-DeclaredOutput` checks remain the fail-closed
integrity proof, but they run *after* the transfer. Scope of this correction is
that one ordering/validation gap; no download, receipt or verification protocol is
added.

Authored here (uncommitted; no F#/JSON/producer/fixture file was edited):

- `.github/workflows/release.yml` — one new shared PowerShell step
  `id: validateArtifacts` ("reject an unusable declared artifact id before any
  transfer") placed before all three pinned download steps. It reads the three ids
  from step `env` bound to the declaring job outputs
  (`needs.publisher.outputs.runtimeArtifactId`,
  `needs.preparation.outputs.candidateArtifactId_dotnet`,
  `needs.preparation.outputs.candidateArtifactId_workflow`), so no value is
  interpolated into the script body, and no token or secret is present. It asserts
  each id is non-empty, matches `^[1-9][0-9]*$` (ASCII, positive, no leading zero)
  and parses as a bounded `uint64` under `InvariantCulture`. Scope is the three ids
  only; digests and archive names stay with the existing later checks.
- `infrastructure/release/Coherence.ps1` — six job-scoped assertions (the step
  exists once, invokes the shared helper three times, embeds no `${{ }}` in its own
  body, and binds each id to its declaring job output) plus one ordering assertion
  (`id: validateArtifacts` before the first `actions/download-artifact`). The guard
  summary is now `cross-stage coherence ok: 156 assertions` (was 149). The count is
  emitted dynamically and no compiled fixture asserts a literal count, so no
  tester-owned file needs to change for the count alone.

Local validation (no compiled build, no test execution, no network, no credential
read):

- `yaml.safe_load` on `release.yml`: exactly three jobs; publication step order is
  `… → validateArtifacts → downloadPublisher → downloadCandidateDotnet →
  downloadCandidateWorkflow → publish → result`.
- `Coherence.ps1 -Components '["dotnet","workflow"]'`: exit 0, 156 assertions.
- Guard mutations rejected (exit 1, named diagnostics): step removed, runtime id
  replaced by a literal, guard moved after the downloads.
- Controlled TEMP stub harness over the extracted step body: 11 cases. Bounded ids
  accept (exit 0); missing, blank, whitespace-padded, non-numeric, `0`,
  leading-zero, negative and `uint64`-overflow ids, plus a missing dotnet id and a
  blank workflow id, each reject (exit 1). The mocked transfer marker was written
  only for the accepted case, so no rejected id reaches a download step.

Unchanged: the three pinned download action refs (13 pins total), the
`needs: [publisher, preparation]` gate, the later in-step `Read-DeclaredOutput`,
inventory and membership checks as defense in depth, `queue: max` /
`cancel-in-progress: false`, the official transfer action, and the policy versions
(Dotnet 1.0.4, Workflow 1.0.5).

Residual coupling (test owner): `infrastructure/release/tests/Coherence.fs`
exercises the guard only through the real `Coherence.ps1` invocation, so no fixture
edit is required by this change. A future fixture that pins the guard count must
read the dynamic total rather than a literal.
