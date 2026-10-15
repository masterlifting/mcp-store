# SEC1 trusted bootstrap — transport-only batch

This document is the factual companion to `Boundary.md` (frozen decisions) and
`Boundary-build.md` (first compiled batch). It records what was authored in the
bounded bootstrap batch, the interfaces those files expose, and the interface
facts that remain open. It does not restate policy and claims no approval.

## Status and scope

Authored in this batch (new files only):

- `infrastructure/release/Bootstrap.ps1` — transport-only bootstrap.
- `infrastructure/release/Sources.json` — audited explicit input closure.
- `docs/GHI-24/review/Bootstrap.md` — this record.

Deliberately **not** modified: `infrastructure/release/Bootstrap.fsx`,
`infrastructure/release/Publisher.json`, `infrastructure/release/Runner.fs`,
`.github/workflows/release.yml`, `infrastructure/release/README.md`, and every
producer/test/workflow file. Workflow wiring and F# interfaces remain another
owner's batch. `mayEdit=true`, `mayBuild=false`, `mayTest=false`,
`mayDelegate=false`; parser/source validation was used, no build, no test, no
delegation.

No live platform call, upload, credential read, secret read, network mutation,
commit, or task-state change was performed.

## Frozen correction carried into this batch

The trusted builder **cannot predict GitHub's outer artifact zip**, because the
outer zip is created by `actions/upload-artifact` at upload time. Two distinct
digests are therefore kept separate and must never be conflated:

- **outer platform digest** — the `actions/upload-artifact` step output
  `artifact-digest` (SHA-256 of the outer platform zip). Cross-checked against
  the platform API `digest` for the exact artifact id.
- **inner builder envelope hash** — the SHA-256 the trusted builder computes
  over the archive *it authored* before upload, plus the SHA-256 over its
  `runtime-manifest.json`. These are builder-reported job outputs.

The bootstrap verifies the outer digest against the actual streamed transport
bytes, and the inner envelope hash against the extracted envelope. It never
treats the outer digest as the builder's digest or the reverse.

## `Bootstrap.ps1` — CLI input contract

Two modes: `InstallRuntime` (write jail) and `FetchCandidate` (candidate outer
fetch, reusing the same transport helper). Selection is by **exact artifact id**;
the artifact name is a cross-check, never a selector.

Identity / platform binding (all fail closed):

| Input | Purpose |
| --- | --- |
| `-Repository` | `owner/name`, compared against the observed run |
| `-RunId`, `-RunAttempt` | run-attempt-specific observation, never the latest attempt |
| `-EventSha` | 40-hex event SHA, bound to artifact `workflow_run.head_sha` and run `head_sha` |
| `-Role`, `-Component` | artifact role and (candidate) component |
| `-ArtifactName`, `-ArtifactId` | name cross-check + exact-id selector |
| `-PlatformDigest` | outer platform digest from the upload step output |
| `-ExpectedEvent` | default `push` |

Archive contract:

| Input | Purpose |
| --- | --- |
| `-ExpectedOuterEntries` | exact outer entry set (names) |
| `-EnvelopeSha256` (InstallRuntime) | inner builder envelope hash |
| `-ManifestSha256` (InstallRuntime) | builder manifest transport hash |
| `-ExpectedEnvelopeName`, `-ExpectedManifestName` | exact inner names |
| `-RequiredRuntimeFile` | default `ReleaseRunner.exe` |

Isolation / limits:

| Input | Purpose |
| --- | --- |
| `-InstallRoot` | fresh, must not exist, must not overlap checkout/candidate/receipt |
| `-ReceiptPath` | JSON receipt destination |
| `-CheckoutRoot`, `-CandidateRoot` | overlap assertions |
| `-AllowedRedirectHosts` | default `*.blob.core.windows.net` (see open facts) |
| `-ApiBase`, `-TokenEnvironmentVariable` | API base; token is read by env name only |
| `MaxEntryCount`/`MaxTotalUncompressedBytes`/`MaxEntryUncompressedBytes`/`MaxCompressionRatio`/`MaxTransportBytes` | bounded limits mirroring the compiled transport limits |

## `Bootstrap.ps1` — receipt schema (`-ReceiptPath`)

```json
{
  "schemaVersion": 1,
  "mode": "InstallRuntime",
  "status": "installed | fetched | failed",
  "repository": "owner/repo",
  "runId": "…", "runAttempt": "…", "eventSha": "…",
  "role": "…", "component": null,
  "artifact": { "id": "…", "name": "…", "sizeInBytes": "…", "expired": false,
                "apiDigest": "…", "platformDigest": "…",
                "digestSource": "actions/upload-artifact artifact-digest (outer platform zip)" },
  "download": { "redirectHost": "…", "bytesRead": 0, "observedSha256": "…" },
  "outerArchive": { "entryCount": 0, "totalUncompressedBytes": 0, "entries": [] },
  "envelope": { "name": "…", "builderSha256": "…", "observedSha256": "…",
                "manifestName": "…", "manifestSha256": "…", "entryCount": 0 },
  "runtime": { "installRoot": "…", "fileCount": 0, "requiredEntry": "ReleaseRunner.exe" },
  "failures": [ { "code": "…", "detail": "…" } ]
}
```

Exit `0` on `installed`/`fetched`; exit `1` with one typed `code` in
`failures[]`. On any failure the fresh install root is removed so no partially
installed runtime persists.

## Trusted builder output schema (contract only; not wired here)

Job outputs the publish job must pass into `Bootstrap.ps1`:

| Output | Source |
| --- | --- |
| `runtime_artifact_id` | `actions/upload-artifact` `artifact-id` |
| `platform_digest` | `actions/upload-artifact` `artifact-digest` (outer zip) |
| `envelope_sha256` | builder-computed SHA-256 over its authored inner archive |
| `manifest_sha256` / `manifest_artifact_id` | builder-computed over `runtime-manifest.json` |
| `expected_outer_entries` | exact outer entry names |

Builder inner envelope layout (uploaded as one artifact): the authored runtime
archive plus `runtime-manifest.json`. Manifest schema consumed by the bootstrap:

```json
{ "schemaVersion": 1, "files": [ { "path": "ReleaseRunner.exe", "sha256": "<64-hex>", "bytes": 0 } ] }
```

## Platform metadata used, and its limits

Read endpoints (implemented; **not executed live in this batch**):

- `GET {api}/repos/{repo}/actions/artifacts/{artifact_id}` — `id`, `name`,
  `size_in_bytes`, `digest`, `expired`, `workflow_run.id`, `workflow_run.head_sha`.
- `GET {api}/repos/{repo}/actions/runs/{run_id}` — `id`, `run_attempt`,
  `head_sha`, `event`.
- `GET {api}/repos/{repo}/actions/artifacts/{artifact_id}/zip` — signed 302.

Limits that shape the checks:

- The artifact object carries **no upload-attempt field**. Attempt binding is by
  the workflow-supplied `-RunAttempt` compared against the run object's
  `run_attempt`; it is a cross-check, not proof of the artifact's attempt.
- `digest` is `null` for artifacts predating the digest field. The bootstrap
  **fails closed** and reports the missing platform field rather than
  proceeding.
- `download-artifact` cannot return the outer zip, so the bootstrap streams the
  outer bytes itself via the API and hashes them first-hand.

## Redirect / credential policy

- Redirects are not followed automatically; exactly one 302 is expected.
- Scheme must be `https`; host must match `-AllowedRedirectHosts`.
- The `Authorization` header is **not** forwarded to the signed cross-origin URL.
- The signed URL and its query are never printed; only the redirect **host** is
  recorded in the receipt.
- Unknown scheme/host fails closed with a typed diagnostic instead of
  proceeding.

## Archive safety before any write

`Assert-ZipArchiveSafety` runs before any entry is materialized and enforces:
exact entry set, entry count, per-entry and total expansion, compression ratio,
ASCII-printable relative names, no traversal/rooted/UNC/colon/backslash/empty
segments, no duplicate entries, no case collisions, and no DOS reparse-point or
Unix symbolic-link attributes. Extraction is manual per-entry
(`Expand-SafeArchive`); `ExtractToDirectory` is never used, and each target is
re-checked to stay inside the destination.

Deliberately **not** duplicated from the compiled `ReleaseArchive.parse`:
compression-method, general-purpose flag, ZIP64, comment, and extra-field
checks. Those remain the compiled validator's responsibility for candidate
content, which the installed trusted runtime validates after install.

## Deliberately out of scope

- No `Config.fsx` reader, no policy derivation, no toolchain identity, no
  transaction, no upload, no SDK invocation.
- No workflow YAML, no `Publisher.json`, no `Runner.fs`, no README change.
- No F# interface change (the `--platform-digest` removal and the runtime
  manifest writer belong to the pending F#/compiler batch).

## `Sources.json` — audited input closure

Enumerates only exact paths and compile-order imports; it carries no SDK,
runtime, platform, version, policy or digest values and no placeholder hashes
(`forbiddenFields` records the drift guard).

- `paths` (14, in compile order): `infrastructure/release/Release.fsproj`,
  `infrastructure/Provenance.fs`, then `Archive.fs`, `Compare.fs`, `Metadata.fs`,
  `Publish.fs`, `Evaluate.fs`, `Capture.fs`, `Enrollment.fs`, `Policy.fs`,
  `PublisherClosure.fs`, `Transaction.fs`, `Orchestration.fs`, `Runner.fs` under
  `infrastructure/release/`.
- `imports`: 13 `compile` edges from the project to each source, in the same
  order as the `<Compile Include>` items.
- `excluded`: MSBuild/NuGet hooks (`Directory.Build.props`,
  `Directory.Build.targets`, `Directory.Packages.props`, `*.props`, `*.targets`,
  `NuGet.config`, `global.json`), producer scripts (`Build/Pins/Release/Config.fsx`),
  tests, and the retained PowerShell/F#x tooling.
- `stageRoot`: staged outside the checkout, preserving repo-relative paths so
  `../../infrastructure/Provenance.fs` still resolves.
- `nuget`: `explicit` mode with an audited deterministic cache; the closure
  declares no package versions.

Repository facts observed while authoring (read-only): no `Directory.Build.*`,
`Directory.Packages.props`, `NuGet.config`, `global.json`, or `packages.lock.json`
exists in the tracked tree; the only `*.props`/`*.targets` present are generated
`workflow/obj/*.nuget.g.*`; `Release.fsproj` declares no `PackageReference` or
`ProjectReference`.

## Open interface facts (blocked, not invented)

1. **Signed redirect host contract** — the default `-AllowedRedirectHosts`
   (`*.blob.core.windows.net`) is the presumed Azure artifact endpoint; the live
   platform contract must be confirmed before wiring, and the bootstrap fails
   closed and reports on any other host.
2. **`X-GitHub-Api-Version`** — defaults to `2022-11-28`; the public REST
   reference currently documents a newer API version. Confirm and pin.
3. **Outer zip directory entries** — the bootstrap rejects trailing-slash
   entries; the live platform output for a two-file artifact must be confirmed.
4. **Attempt binding** — the artifact object has no attempt field; per-attempt
   provenance ultimately rests on the workflow-supplied run attempt.

## Validation performed

- Both new JSON/PS files parse (`System.Management.Automation.Language.Parser`
  over `Bootstrap.ps1`; `ConvertFrom-Json` over `Sources.json`) — syntax-only,
  no execution, no network.
- No live API call, no upload, no build, no test, no credential or secret read.
- Tester-owned coverage (wire seam, negative archive cases, receipt assertions)
  and F# interface changes remain pending.
