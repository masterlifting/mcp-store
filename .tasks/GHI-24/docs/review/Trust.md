# SEC1 adapter trust separation — expected vs. observed channel split

This document is the factual companion to `Adapter.md` (previous adapter
slice), `Boundary.md` (frozen decisions) and `Bootstrap-fix.md`
(strict Bootstrap.ps1 v2 receipt shape). It records the bounded
engineering slice that closes the self-report same-CLI trust bypass
called out in the SEC1 review: the publisher now derives
`ObservedPlatformMetadata` from the SEPARATELY parsed Bootstrap.ps1 v2
receipt at a separate `--transport-evidence` path, instead of from the
CLI flags. It records the strict CLI contract addition, the strict
receipt parser, the cross-check matrix, the path isolation, the build
evidence, the manual CLI smoke tests, the outstanding gaps, and the
precise DevOps needs. It claims no APPROVE, no PASS, and no external
review.

## Status and scope

`mayEdit=true` bounded to `infrastructure/release/Runner.fs` and this
document; `mayTest=false`, `mayDelegate=false`, `mayBuild=true`
(dedicated `dotnet_build` MCP only). No live platform call, upload,
credential read, secret read, network mutation, commit, push,
task-state mutation, or merge. No `Handoff.fs`, `Bootstrap.ps1`,
`Sources.json`, `Publisher.json`, `PublisherClosure.fs`,
`Orchestration.fs`, `Evaluate.fs`, `Policy.fs`, `Transaction.fs`,
`README.md`, `.github/workflows/release.yml`, every producer
`Config.fsx` / `Build.fsx` / `Pins.fsx`, every `workflow/tests/*.fsx`
and `dotnet/tests/Mcp.Dotnet.Tests/*`, every consumer / Telegram /
harness file was modified. The `ghi-24` worktree stays dirty on
purpose — the integration remains pre-merge.

Authored in this slice:

- `infrastructure/release/Runner.fs` — new mandatory CLI flag
  `--transport-evidence=<path>`; new typed `BootstrapReceipt`
  surface; new strict `parseBootstrapReceipt` parser; new
  `loadBootstrapReceipt` bounded reader; new
  `isolateBootstrapEvidencePath` path-isolation check; new
  `crossCheckBootstrapReceipt` matrix; new
  `verifyBootstrapUploaderJob` success-evidence check; new
  `loadAndVerifyBootstrapReceipt` composite that runs BEFORE any HTTP
  transport, auth, or upload-core composition; updated `buildEvidence`
  to derive `ObservedPlatformMetadata` from the Bootstrap.ps1 v2
  receipt fields instead of from the CLI; updated
  `VerifiedWorkspace` and `verifyWorkspace` to thread the bootstrap
  receipt through the pipeline; updated CLI usage strings.

## The closed bypass

The previous `Adapter.md` documented a self-report bypass: the
`publish` entrypoint populated `ExpectedMetadata` and
`ObservedPlatformMetadata` from the SAME CLI flags because the
Bootstrap.ps1 v2 receipt had not yet been wired into the CLI. The
`crossCheckMetadata` function in `Handoff.fs` still ran, but compared
the CLI to itself, so the "cross-check" was trivially satisfied.
This slice closes that bypass. The CLI `--repo`, `--run-id`,
`--attempt`, `--event-sha`, `--role`, `--component`, `--artifact-id`,
`--artifact-name`, `--transport-sha256` remain the
`ExpectedMetadata` (workflow-supplied trusted context). The new
`--transport-evidence=<path>` is the SOLE source of every
`ObservedPlatformMetadata` field (`ArtifactId`, `ArtifactName`,
`Role`, `Component`, `Repository`, `RunId`, `Attempt`, `Sha`). The
two channels are produced by independent jobs (the workflow job
composes the CLI; the transport-only Bootstrap.ps1 job writes the
receipt) and any disagreement on any field fails closed before any
HTTP / auth / upload runs.

## Strict CLI contract

The `publish` entrypoint now accepts EXACTLY two positional arguments
followed by N documented `--key=value` flags, plus the new mandatory
`--transport-evidence=<path>`. Surplus positional arguments, unknown
flags, duplicate flags, malformed `--key=value` forms, and disallowed
combinations are rejected before the publisher reads a single byte.

Allowed flags (no aliases, no short forms):

```
--repo=<owner>/<name>
--run-id=<id>
--attempt=<n>
--event-sha=<40-hex-lowercase>
--role=<publisher|runtime|candidate>
--component=<dotnet|workflow>
--artifact-id=<id>
--artifact-name=<name>
--transport-sha256=<64-hex-lowercase>
--builder-sdk=<x>
--builder-runtime=<y>
--builder-platform=<z>
--workspace-root=<path>
--transport-evidence=<bootstrap-receipt.json>      (NEW, mandatory)
--trigger=<event>                                  (optional)
```

Mandatory flags (publisher never accepts a partial supply):

`--repo`, `--run-id`, `--attempt`, `--event-sha`, `--role`,
`--component`, `--artifact-id`, `--artifact-name`,
`--transport-sha256`, `--builder-sdk`, `--builder-runtime`,
`--builder-platform`, `--workspace-root`, `--transport-evidence`.

`--transport-evidence` is NEW. It accepts a single `--transport-evidence=<path>`
pair, rejects duplicates through the existing `seen` set (preserved from
the prior slice), and the empty value case is rejected by the same
missing-flag check that already protects every other mandatory flag.

## Bootstrap.ps1 v2 receipt — strict parser

The publisher reads the Bootstrap.ps1 v2 receipt from the
`--transport-evidence` path via `loadBootstrapReceipt`, which:

1. Reads the file through the existing 4 MiB bounded read
   (`receiptReadLimit`) so a hostile receipt cannot exhaust memory.
2. Calls `parseBootstrapReceipt text path`, which:
   - rejects unknown top-level fields against
     `bootstrapAllowedFieldNames`;
   - requires `schemaVersion == 2`;
   - requires `mode` and `status` as non-empty strings;
   - requires `status ∈ {"installed", "fetched"}` (the bootstrap job
     itself must have completed; a `failed` status cannot authorise
     publication);
   - requires `repository`, `runId`, `runAttempt`, `eventSha`,
     `role` as non-empty strings;
   - accepts `component` as either a JSON string or JSON null
     (`publisher` and `runtime` roles declare null; `candidate`
     declares a non-null string);
   - requires the `artifact` object and rejects unknown fields
     against `bootstrapArtifactAllowedFieldNames`;
   - requires `artifact.id`, `artifact.name`, `artifact.apiDigest`,
     `artifact.platformDigest` as non-empty strings and
     `artifact.sizeInBytes` as a non-negative integer;
   - requires the `download` object and rejects unknown fields
     against `bootstrapDownloadAllowedFieldNames`;
   - requires `download.bytesRead` as a non-negative integer and
     `download.observedSha256` as a non-empty string;
   - requires the `uploaderJob` object and rejects unknown fields
     against `bootstrapUploaderJobAllowedFieldNames`;
   - requires `uploaderJob.id` as a non-empty string and
     `uploaderJob.conclusion` as a non-empty string;
   - requires `runAttemptObservation.headSha` (when present) to equal
     the receipt `eventSha`.

A missing field, malformed value, or unknown field produces a typed
`MalformedArtifact` before the publisher composes any
`ObservedPlatformMetadata`. No default / placeholder / zero fallback
is invented.

## Path isolation

`isolateBootstrapEvidencePath` enforces that the
`--transport-evidence` path is OUTSIDE every directory the candidate
or the workspace could write to:

- the resolved (canonical) evidence path MUST NOT equal or be inside
  the canonical candidate root;
- the canonical evidence path MUST NOT be inside the canonical
  workspace root;
- the canonical evidence path MUST NOT equal
  `<candidate>/receipt.json` (the trusted publisher receipt and the
  bootstrap receipt are separate documents at separate paths).

The Bootstrap.ps1 source already enforces
`install-overlaps-receipt`, `receipt-overlaps-candidate`, and
`install-overlaps-checkout` at the bootstrap side; this publisher
re-check is defence in depth so a future DevOps wiring bug cannot
let a value inside the candidate or the workspace reach the parser.
A path-isolation violation is a typed `MalformedArtifact` before any
byte is read.

## Cross-check matrix (CLI expected vs. receipt observed)

`crossCheckBootstrapReceipt` enforces exact field-by-field agreement
between the CLI expected metadata and the parsed Bootstrap.ps1 v2
receipt observed metadata, with these fields:

| Field | Source | Comparison |
| --- | --- | --- |
| `repository` | receipt `repository` | `cli --repo` |
| `runId` | receipt `runId` | `cli --run-id` |
| `runAttempt` | receipt `runAttempt` | `cli --attempt` |
| `eventSha` | receipt `eventSha` | `cli --event-sha` |
| `role` | receipt `role` | `cli --role` |
| `component` | receipt `component` (or `""` when null) | `cli --component` |
| `artifactId` | receipt `artifact.id` | `cli --artifact-id` |
| `artifactName` | receipt `artifact.name` | `cli --artifact-name` |
| `transportSha256` | receipt `download.observedSha256` | `cli --transport-sha256` |

A mismatch on any field is a typed `MalformedArtifact` before the
publisher constructs any HTTP transport, auth, or upload-core
composition. The existing `crossCheckMetadata` in `Handoff.fs` (the
typed wrapper that builds `ArtifactEvidence`) re-applies the
`ExpectedMetadata` vs. `ObservedPlatformMetadata` comparison a
second time, so the strict-lowercase shape and the typed
`OuterDigest` wrapper carry through the same channel separation.

## Uploader-job success check

`verifyBootstrapUploaderJob` enforces that the Bootstrap.ps1 v2 receipt's
`uploaderJob.conclusion == "success"` and that `uploaderJob.id`
is a numeric platform job id. The check is independent of the CLI:
the candidate cannot manufacture a successful upload claim because
the `uploaderJob` block is supplied by the platform's attempt-scoped
jobs listing (per `Bootstrap-fix.md` section 3). A non-successful or
missing uploader-job entry is a typed `MalformedArtifact` before any
HTTP / auth / upload runs.

## `buildEvidence` — observed metadata from the receipt

`buildEvidence` now derives `ObservedPlatformMetadata` exclusively
from the parsed Bootstrap.ps1 v2 receipt:

```fsharp
let observed : ReleaseHandoff.ObservedPlatformMetadata =
    { ArtifactId   = bootstrap.ArtifactId
      ArtifactName = bootstrap.ArtifactName
      Role         = bootstrap.Role
      Component    = observedComponent
      Repository   = bootstrap.Repository
      RunId        = bootstrap.RunId
      Attempt      = bootstrap.RunAttempt
      Sha          = bootstrap.EventSha }
```

`ExpectedMetadata` continues to come from the CLI. The two surfaces
now originate from different inputs in different jobs; the `crossCheck`
inside `Handoff.fs` is no longer a self-report.

The CLI `--component` is empty for the `publisher` and `runtime`
roles; for those roles the receipt `component` is JSON `null` and
the publisher compares the empty CLI value against the empty resolved
receipt value. For the `candidate` role the receipt MUST carry a
non-null `component`; a null receipt component for a candidate role
is a hard failure surfaced by `buildEvidence`.

## Verification phases (before any HTTP / auth / upload)

1. **Closure** — `ReleasePublisherClosure.readFromFile` parses
   `Publisher.json`.
2. **CLI shape** — every flag is checked against
   `allowedPublishFlagNames`, duplicates are rejected, mandatory
   flags are enforced (now including `--transport-evidence`).
3. **Repo catalog** — `--repo` is split and compared against
   `masterlifting/mcp-store`.
4. **Bootstrap evidence** — NEW. `loadAndVerifyBootstrapReceipt`
   runs `isolateBootstrapEvidencePath`, `loadBootstrapReceipt`,
   `verifyBootstrapUploaderJob`, and `crossCheckBootstrapReceipt`
   in that order. Any failure is a typed `MalformedArtifact` before
   any HTTP / auth / upload runs.
5. **Receipts** — `<candidate>/receipt.json` (typed
   `TrustedPublisherReceipt`) and `<candidate>/preparation.json`
   (typed `PreparationReceipt`) are read, parsed, and cross-checked
   against the CLI.
6. **Source workspace** — canonical `Config.fsx` and roster bytes are
   hashed and cross-checked; the committed HEAD is rechecked against
   `--event-sha`.
8. **Transport** — the publisher-observed SHA-256 of the candidate
   archive equals `--transport-sha256` AND the bootstrap receipt's
   `download.observedSha256`.
9. **Snapshot** — `ReleaseHandoff.readRawTransport` composes the
   typed `CandidateArchiveSnapshot`; `crossCheckMetadata` re-applies
   the `Expected` vs. `Observed` comparison via the existing typed
   wrappers; `VerifiedCandidate.createFromSnapshot` builds the
   verified candidate.
10. **Upload** — `ReleaseTransaction.run` is driven exactly once.

The Bootstrap evidence phase is added between the repo-catalog phase
and the candidate-directory receipt phase, so the publisher has an
independent platform observation before any byte of the candidate
directory is opened.

## Build evidence (this slice)

Dedicated `dotnet_build` MCP, scoped to the affected projects,
`noRestore: false` (fresh package resolution), `Release`
configuration. Build only; no tests, no live API, no real upload.

| Project | runId | Status | Errors | Warnings | DurationMs |
| --- | --- | --- | ---: | ---: | ---: |
| `infrastructure/release/Release.fsproj` | `IhK3lu2y4etRTkin6EFNtMRhY2-vWlZjUlkypzq5t9A` | succeeded | 0 | 0 | 32778 |
| `dotnet/Mcp.Dotnet.fsproj` | `IZ8VrcTPpFzHR_z9Dm99m9MRVvfsWKl6PwNtIusSjf8` | succeeded | 0 | 0 | 8884 |
| `workflow/Mcp.Workflow.fsproj` | `dQtq30ijYYtkZpdjaAQPmaVANrlV8MWoBPFV12gCqlI` | succeeded | 0 | 0 | 6203 |

All three compile with `TreatWarningsAsErrors=true`. The two prior
intermediate build IDs (`...` etc.) tracked the initial compile-error
fix-up of the partial-application and `Path.DirectorySeparatorChar`
type issues and are intermediate historical evidence, not production
builds.

## Manual CLI smoke tests (this slice)

The compiled `ReleaseRunner` was manually exercised without
credentials to confirm the new entrypoint surface and the strict
CLI gating. No network call, no credential read, no `.env` read, no
secret read, no network mutation.

```
ReleaseRunner                  # exit 2, prints both usage lines (with --transport-evidence)
ReleaseRunner publish          # exit 2, prints the publish usage line
ReleaseRunner publish fake.json fake-dir
# exit 4, JSON {"status":"failed","reason":"... the publish CLI is
#         missing required flag(s): --repo, --run-id, --attempt,
#         --event-sha, --role, --component, --artifact-id,
#         --artifact-name, --transport-sha256, --builder-sdk,
#         --builder-runtime, --builder-platform, --workspace-root,
#         --transport-evidence"}
```

The empty-argv / subcommand-only-argv paths print only the documented
usage line (now listing `--transport-evidence`) and exit `2`. The
publish path with mandatory flags missing prints the structured JSON
`status=failed` envelope with the typed diagnostic and exits `4`.
The previously-documented receipt-missing-file smoke test from
`Adapter.md` still applies, but now requires `--transport-evidence`
to be supplied as well; the publisher never accepts a partial supply.

## Outstanding privacy / gap facts (not invented, not self-approved)

1. **Bootstrap-evidence production wiring.** The
   `--transport-evidence` flag is wired into the publisher; the
   workflow that authors the flag value and downloads the
   Bootstrap.ps1 v2 receipt into a fresh isolated directory is a
   separate DevOps task. Until the workflow is wired, the publisher
   fails closed with a typed `MalformedArtifact` for the missing
   mandatory flag — no token / auth / HTTP ever runs. No deferred
   DevOps proof enables an unsafe CLI.

2. **Bootstrap.ps1 field parity.** This slice uses
   `bootstrap.SchemaVersion = 2`, `artifact.apiDigest`,
   `artifact.platformDigest`, `download.observedSha256`,
   `uploaderJob.id`, `uploaderJob.conclusion`,
   `runAttemptObservation.headSha`, plus the top-level
   `repository` / `runId` / `runAttempt` / `eventSha` / `role` /
   `component` fields. These are the documented Bootstrap.ps1 v2
   schema fields. If a future Bootstrap.ps1 revision changes any
   field name, the strict parser rejects the receipt with a typed
   `MalformedArtifact` until the parser is updated.

3. **`uploaderJob.artifactLinkLimit` fact.** The Bootstrap.ps1 source
   notes that the platform exposes no artifact-to-job link; the
   receipt records `uploaderJob.artifactLinkLimit` as a string
   describing the limit. The publisher does not enforce this limit;
   the receipt's `uploaderJob.conclusion == "success"` and
   `uploaderJob.id` being numeric is the strongest available binding
   per the Bootstrap-fix.md fact 3.

4. **Bootstrap.ps1 internal `ApiVersion` divergence.** The Bootstrap.ps1
   source pins `X-GitHub-Api-Version: 2022-11-28`; the public REST
   reference now documents `2026-03-10`. This is recorded as a
   separate fact in `Bootstrap-fix.md` open fact 2 and is owned by
   the joint decision the Bootstrap-fix batch left open. The
   publisher does not enforce the API version; it enforces the
   receipt shape.

5. **Test project migration.** The `tests/` scenarios that
   reference the prior `buildEvidence` shape still need to be
   migrated to the new mandatory `--transport-evidence` flag.
   Parallel tester owns that migration; this slice does not migrate
   tests (`mayTest=false`).

6. **Same-version / new-files permissions.** Per the assignment,
   `mayBuild=true` is used only via the dedicated `dotnet_build` MCP
   tool, scoped to `infrastructure/release/Release.fsproj`,
   `dotnet/Mcp.Dotnet.fsproj`, and `workflow/Mcp.Workflow.fsproj`
   only. No raw `dotnet build` fallback was used. No env secret
   values, live API, upload, network, userdata, state, commit,
   push, merge, rebase, stash, fetch, or foreign checkout was
   performed. No real upload of any artifact was attempted; the
   smoke tests above write no candidate directory contents and
   produce no remote effect.

## Remaining work (owned elsewhere)

- Fresh-job workflow wiring in `.github/workflows/release.yml`:
  prepare / publish jobs, role-map literal, `job.check_run_id` /
  `artifact-id` / `artifact-digest` plumbing, candidate directory
  assembly, Bootstrap.ps1 invocation, and the wiring that supplies
  `--transport-evidence=<freshly-isolated-bootstrap-receipt.json>`
  to `ReleaseRunner publish`.
- DevOps confirmation of the signed-redirect-host contract
  (`*.blob.core.windows.net` default in `Bootstrap.ps1`) and the
  `X-GitHub-Api-Version` pin (Bootstrap-fix.md open facts 1 and 2).
- Parallel tester-owned regression suite covering the new strict
  Bootstrap.ps1 v2 receipt parser, the path-isolation matrix, the
  CLI / receipt cross-check matrix, the uploader-job success check,
  the new mandatory flag, the duplicate-flag rejection, and the
  failure paths of every new verification phase.
- Independent security / architecture / correctness / acceptance
  review of the whole SEC1 boundary.

No engineering APPROVE is asserted by this document.