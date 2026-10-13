# SEC1 bootstrap F5 correction — run/attempt, digest and transport repair

This document is the factual companion to `Boundary.md` (frozen decisions),
`Boundary-build.md` (compiled batch) and `Bootstrap.md` (original bootstrap batch).
It records the bounded correction batch applied to `infrastructure/release/Bootstrap.ps1`
and `infrastructure/release/Sources.json`, the interfaces those files now expose, the
checks performed, the test seams that are now available, and the interface facts that
remain open. It does not restate policy and claims no approval.

## Status and scope

Authored in this batch:

- `infrastructure/release/Bootstrap.ps1` — corrected transport-only bootstrap.
- `infrastructure/release/Sources.json` — closure realigned with the current project.
- `.tasks/GHI-24/docs/review/Bootstrap-fix.md` — this record.

Deliberately **not** modified: every F# source (`Handoff.fs`, `Runner.fs`, `Policy.fs`,
`Orchestration.fs`, …), `Release.fsproj`, `Publisher.json`, `README.md`,
`.github/workflows/release.yml`, every producer/test/workflow file, and every other
review document.

`mayEdit=true` bounded to the two files above plus this record; `mayBuild=false`,
`mayTest=false`, `mayDelegate=false`. No build, no test execution, no live platform
call, no credential or secret read, no network mutation, no commit, no task-state change.
Source validation was parser/JSON only (see **Checks performed**).

## Corrections applied (the F5 list)

### 1. Attempt-specific run observation, no latest-attempt fallback

The previous revision called `GET /repos/{owner}/{repo}/actions/runs/{run_id}`, which the
platform answers with the **latest attempt**. The bootstrap now calls

```
GET {api}/repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{run_attempt}
```

and requires the returned document to agree exactly with the bound context:
`id`, `run_attempt`, `head_sha`, `event` (case-sensitive ordinal, lowercase hex for SHAs),
`repository.id` against the new `-RepositoryId`, and `repository.full_name` against
`-Repository`. There is no call to the unauthored-attempt run endpoint anywhere in the file.

### 2. Explicit repository identity and case policy

- New mandatory `-RepositoryId` (numeric platform repository id) is compared against the
  attempt-specific `run.repository.id`, which the platform documents as a required field.
- `artifact.workflow_run.repository_id` and `artifact.workflow_run.head_repository_id`, when
  present, must equal `-RepositoryId` (`artifact-repository-mismatch`,
  `artifact-head-repository-mismatch`) so a fork-produced artifact cannot be accepted. Both
  artifact sub-fields are optional in the documented schema, so they are rejected on
  contradiction rather than demanded; the artifact is already addressed through the repository
  path and bound by `workflow_run.id`/`head_sha`, and the run's repository object carries the
  strict check.
- `run.repository.full_name` and `run.head_repository.full_name` are compared
  **case-insensitively** (`OrdinalIgnoreCase`) because owner/name are case-insensitive
  platform identifiers, and the **observed canonical** `full_name` is recorded in the receipt.
- Every other identifier — ids, SHAs, artifact name, uploader job name, event name, digests —
  is compared with explicit `Ordinal` semantics. The file no longer relies on PowerShell's
  case-insensitive `-eq`/`-notmatch` for any of those comparisons.

### 3. Attempt evidence from the attempt-scoped job listing, not from the artifact object

The artifact object carries no creation-attempt field, and the single-job response does not
require one either. The new binding is:

- `-UploaderJobId` (mandatory, numeric) — the platform job id of the job that ran the upload
  step. The workflow-side source is `job.check_run_id` (to be confirmed at wiring time; the
  attempt-scoped jobs listing is the fallback derivation).
- The uploader job is located in
  `GET {api}/repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{run_attempt}/jobs`
  (`per_page=100`, bounded to 10 pages / 1000 declared jobs). The attempt is fixed by the
  request URL, so a job id belonging to any other attempt cannot satisfy the lookup:
  `uploader-job-not-in-attempt`, `uploader-job-listing-too-large`.
- The located job must report `id`, `run_id`, `head_sha`, `name`, `status = completed`,
  `conclusion = success` (codes: `uploader-job-id-mismatch`, `uploader-job-run-mismatch`,
  `uploader-job-event-sha-mismatch`, `uploader-job-name-mismatch`,
  `uploader-job-not-completed`, `uploader-job-not-successful`). The optional `run_attempt`
  field is only rejected on contradiction (`uploader-job-attempt-mismatch`); it is never the
  attempt binding.

**Stated limit (not papered over):** the platform exposes no artifact→job link, so the
artifact id cannot be proven to originate from that job through the API. The receipt records
this explicitly as `uploaderJob.artifactLinkLimit`; the artifact id is the upload step's
`artifact-id` output and the attempt-scoped successful job is the strongest available
platform evidence for the attempt.

### 4. Role / component / artifact name are validated, not echoed

New mandatory `-RoleMapJson` carries the independently supplied expectation map:

```json
{
  "publisher": { "artifactName": "release-publisher",       "uploaderJobName": "build-publisher", "component": null },
  "runtime":   { "artifactName": "release-runtime",         "uploaderJobName": "build-runtime",   "component": null },
  "candidate": { "artifactName": "release-candidate-dotnet","uploaderJobName": "prepare-dotnet",  "component": "dotnet" }
}
```

- The map is parsed strictly (`-AsHashtable`): unknown role keys, unknown entry fields,
  missing `artifactName`/`uploaderJobName`, empty values, and duplicate role keys
  (`duplicate-role-map-key`) all fail closed.
- `-ArtifactName` must equal the mapped expectation (`role-map-artifact-name-mismatch`);
  `-UploaderJobName` is taken from the map and compared against the platform job
  (`uploader-job-name-mismatch`); `-Component` is validated against the mapped component
  (`role-component-mismatch`).
- `-Role` is now `ValidateSet("publisher", "runtime", "candidate")` and mode-bound:
  `candidate` ⇔ `FetchCandidate`, `publisher`/`runtime` ⇔ `InstallRuntime`
  (`role-mode-mismatch`). **This mode↔role rule is a binding decision of this batch**; it
  must be reconciled with the compiled `--role` semantics before workflow wiring.

### 5. Metadata transport: redirects disabled by this script

`Invoke-RestMethod` is gone. `Get-MetadataDocument` uses an `HttpClient` this script
constructs and configures:

- `AllowAutoRedirect = false`, `UseCookies = false`, `AutomaticDecompression = None`.
- Any `3xx` is a typed failure (`metadata-redirect-rejected`) instead of a silent hop that
  could carry the bearer credential to another origin.
- Non-`200` → `metadata-request-failed`; malformed JSON → `malformed-platform-response`;
  response body bounded by `-MaxMetadataBytes` (default 4194304) → `metadata-response-too-large`.
- The bearer token is still read only from the environment variable named by
  `-TokenEnvironmentVariable`, is never accepted on the command line, and is never printed.

### 6. Signed download boundary preserved

The cross-origin behaviour is kept and tightened, not replaced: one manually followed `3xx`,
`https` only, host allowlist enforced (including for the test seam), `Authorization` never
forwarded to the signed URL, signed URL/query never printed, streaming SHA-256 computed over
the exact bytes and bounded by `-MaxTransportBytes`. Added: explicit
`AutomaticDecompression = None` / `UseCookies = false` so no transforming hop can alter the
hashed bytes, request-message disposal, and the redirect **status code** recorded in the receipt.

### 7. Explicit outer platform digest decoding

- `ConvertFrom-PlatformDigest` accepts **only** `sha256:<64 lowercase hex>` or a bare
  `<64 lowercase hex>` string; it normalizes to bare lowercase hex. Missing →
  `missing-platform-field`; any other form (uppercase, `sha512:`, whitespace, garbage) →
  `malformed-platform-field`. `-PlatformDigest` accepts the same two forms.
- One normalized value is compared against the platform API digest
  (`platform-digest-mismatch`) and against the streamed bytes
  (`transport-digest-mismatch`, `transport-api-digest-mismatch`); the platform size and the
  streamed byte count must also agree (`transport-platform-size-mismatch`).
- The inner builder envelope/manifest hashes remain a **separate** namespace
  (`-EnvelopeSha256`, `-ManifestSha256`, receipt `envelope.*`), never merged with the outer
  digest, and are now compared case-strictly.
- All of this happens **before** any install/extraction step, and no placeholder or
  unverifiable digest is ever substituted.

### 8. Byte bounds, unsafe-entry and case-collision handling preserved

`Assert-ZipArchiveSafety` / `Expand-SafeArchive` are unchanged in behaviour: exact entry set,
count/total/per-entry/ratio limits, ASCII-relative names, no traversal/rooted/UNC/colon/
backslash/empty segments, no duplicates, no case collisions, no reparse-point or symlink
attributes, manual per-entry extraction, and `ExtractToDirectory` still never used.
One guard was added: the bootstrap receipt must not be written into the candidate payload
directory (`receipt-overlaps-candidate`), because the compiled publisher reads a
**different** `receipt.json` from there (see the coordination note below).

### 9. Test seam (injectable transport)

New optional `-TestTransport` (`IDictionary`) with exactly two script blocks:

```powershell
$seam = @{
  MetadataJson = { param([string]$Uri, [string]$Token) return '<json text for that URI>' }
  DownloadRaw  = { param([string]$DownloadUri, [string]$Token, [string]$DestinationPath,
                         [string[]]$AllowedHosts, [long]$MaxBytes)
                   # write the fixture bytes to $DestinationPath and return:
                   [pscustomobject]@{ Sha256 = '<64 lowercase hex of those bytes>'; Bytes = <int>; RedirectHost = '<host>' }
                 }
}
```

Refusal rules (this is what keeps the seam from becoming a production bypass):

- `-ApiBase` not the production origin **and** no seam → `non-production-api-base`.
- Seam **and** `-ApiBase` = `https://api.github.com` → `test-transport-on-production-origin`.
- Unknown/missing/non-script-block seam entries → `invalid-test-transport`;
  a seam that returns a bad SHA-256/byte count, omits the file, or returns a non-allowed
  redirect host still fails closed.
- A seam run is marked in the receipt (`transport = "test-stub"`, `apiOrigin` recorded), so a
  stubbed run is always distinguishable from a real one.

Stub JSON is parsed by the same code path as live metadata, so negative cases (wrong
attempt, wrong repository id, `sha256:` vs bare digest, redirect-shaped metadata) are
exercisable without a network call. Tests use a dummy token value in the token environment
variable; no live credential is required.

## CLI delta for the workflow-wiring batch

| Parameter | Change |
| --- | --- |
| `-RepositoryId` | **new, mandatory** (`${{ github.repository_id }}`) |
| `-UploaderJobId` | **new, mandatory** (`${{ job.check_run_id }}`) |
| `-RoleMapJson` | **new, mandatory** (independently supplied expectation map) |
| `-Role` | now restricted to `publisher`/`runtime`/`candidate`, mode-bound |
| `-PlatformDigest` | accepts `sha256:<64 hex>` or bare 64-hex (normalized) |
| `-ApiBase` | constrained: production origin unless the test seam is supplied |
| `-MaxMetadataBytes` | new optional bound (default 4194304) |
| `-TestTransport` | new optional, test-only seam |
| `-ApiBase` call sites | run endpoint is now `…/runs/{runId}/attempts/{runAttempt}`, plus the attempt-scoped jobs listing |

Receipt changes (schemaVersion **1 → 2**), all additive except the version: `transport`,
`apiOrigin`, `repositoryId`, `observedEvent`, `runAttemptObservation`, `uploaderJob`,
`artifact.apiDigestRaw/role/component/sizeInBytes` (numeric), `download.redirectCode`,
`download.platformBytes`, `envelope.digestSource`. The `-ReceiptPath` document is the
bootstrap's own evidence document; it is **not** the compiled publisher's `receipt.json`.

## Sources.json correction

`paths` and `imports` now mirror the **current** `Release.fsproj` `<Compile Include>` order
exactly (15 paths / 14 compile edges), which means: `Handoff.fs` added between
`PublisherClosure.fs` and `Evaluate.fs`, and the previously drifted `Evaluate.fs`/`Capture.fs`
order corrected. `excluded` gained `**/Sources.json` and `**/Publisher.json` (closure documents,
not build inputs). No SDK/runtime/platform/version/policy/digest value and no placeholder hash
was introduced; `forbiddenFields` is unchanged.

## Checks performed

- `[System.Management.Automation.Language.Parser]::ParseFile` over `Bootstrap.ps1`:
  **0 parse errors** (8144 tokens). Syntax/tokenization only — no execution.
- `ConvertFrom-Json` over `Sources.json`: 15 paths, 14 imports, 21 excluded entries, no
  duplicate paths.
- Existence check of every closure path against the working tree (all present).
- Read-only comparison of `Sources.json` order against `Release.fsproj` `<Compile Include>`
  order (now equal, `Handoff.fs` included).
- Grep checks: no remaining `Invoke-RestMethod`/`Invoke-GitHubApiJson`,
  no remaining call to the attempt-less run endpoint or the attempt-less single-job endpoint,
  no remaining case-insensitive hex validation.
- Read-only read of the current public REST reference for the three endpoints used
  (`actions/artifacts`, `workflow-runs` attempts, `workflow-jobs` attempt-scoped listing) to
  confirm the paths, the response fields, and which fields the platform marks as required.
  No request was issued against any live endpoint.
- No build, no test run, no `dotnet` invocation, no live API request, no secret read.

## Open facts and blocked items (not invented)

1. **Signed redirect host contract (blocked).** `-AllowedRedirectHosts` still defaults to
   `*.blob.core.windows.net`. The live artifact-download redirect host cannot be confirmed
   without a real platform call, which this batch is not allowed to make. Behaviour stays
   fail-closed on any other host; the wiring batch must confirm or correct the default.
2. **API version divergence (needs a joint decision).** `Bootstrap.ps1` keeps
   `X-GitHub-Api-Version: 2022-11-28` to stay consistent with the compiled channel
   (`Publish.fs` pins the same value), while the public REST reference now documents
   `2026-03-10`. Changing one channel alone would create divergent control-plane behaviour,
   so this is recorded rather than changed unilaterally.
3. **Mode↔role binding (needs confirmation).** The `candidate ⇔ FetchCandidate`,
   `publisher|runtime ⇔ InstallRuntime` rule and the role vocabulary are derived from the
   compiled `Handoff.md` role set; the framework owner must confirm it matches the intended
   workflow wiring before the map is authored there.
4. **Size cross-check assumption.** `transport-platform-size-mismatch` assumes
   `artifact.size_in_bytes` equals the streamed outer-zip byte count. It is documented as a
   platform-consistency check and cannot be verified live in this batch.
5. **Receipt schemaVersion 2 (consumer decision).** The new evidence fields require a
   consumer, which does not exist until the workflow-wiring batch. The bump is deliberate and
   documented so no consumer can silently read a partially understood document.
6. **Candidate-directory interface (wiring batch).** For `FetchCandidate`,
   `-ExpectedOuterEntries` must be exactly the three relative names the compiled
   `Handoff.readFromDirectory` requires (`<archive-name>.zip`, `distribution.json`,
   `receipt.json`). The bootstrap cannot derive `<archive-name>.zip` (policy-derived) and
   therefore validates the exact set it is given; the wiring batch must supply that exact set
   and must download the **trusted-builder-generated** `receipt.json` into the candidate
   directory (the README already states that contract).
7. **Two-channel coordination.** The compiled handoff batch (`Handoff.fs`, `Runner.fs`) and
   this bootstrap batch are separate channels. The concrete interface points are: the
   bootstrap's CLI context (`repo`/`repositoryId`/`run`/`attempt`/`eventSha`/`role`/`component`/
   `artifact id+name`) must equal the publisher CLI context for the same run, and the
   bootstrap receipt must never be confused with the compiled candidate `receipt.json`. Both
   are recorded here as pending agreement; no compiled file was edited to force alignment.
   Two facts read from the current compiled source (read-only) pin the split: `Runner.fs`
   states that the CLI `--artifact-name`/`--artifact-id` are diagnostics only and that
   "Bootstrap.ps1's platform-observed artifact cross-check remains the canonical outer check",
   and the publisher's `--transport-sha256` is documented as the actual SHA-256 of the streamed
   outer bytes. This batch therefore leaves `download.observedSha256` in the bootstrap receipt
   as the value the wiring batch must forward as `--transport-sha256`; the bootstrap is the
   only place the platform artifact identity/digest/attempt is checked.

## Remaining work (owned elsewhere)

- Workflow wiring in `.github/workflows/release.yml`: fresh prepare/publish jobs, the role map
  literal, the `job.check_run_id`/`artifact-id`/`artifact-digest` plumbing, and the candidate
  directory assembly (compiled + DevOps owner).
- Tester-owned deterministic coverage over the new seam: positive install/fetch paths, wrong
  attempt, wrong repository id, fork head repository, uploader job absent from the attempt /
  not successful / mismatched name, both digest forms, malformed digest, redirect-shaped
  metadata, paginated job listings, and archive negative cases.
- Independent security/architecture/acceptance review of the whole SEC1 boundary.

No engineering APPROVE is asserted by this document.
