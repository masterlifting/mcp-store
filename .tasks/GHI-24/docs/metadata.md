# GHI-24 W3 metadata continuation — neutral PUBLISHED projection

## Status: bounded continuation complete for the assigned F# surface.
              End-to-end remote publish and the regression suite remain
              blocked on the post-commit hosted run and on the tester
              owner. Final package guards continue to reject until the
              implementation PR is committed.

This slice adds the consumer-neutral release-metadata projection the
DevOps slice reported as missing (`docs/GHI-24/devops.md` §5) and the
upload transport that publishes the projection alongside the versioned
archive. Authority: `docs/GHI-24/original.md` (verbatim) and
`docs/GHI-24/solution.md` §7 ("Publish versioned harness-neutral
metadata, immutable and verified, reporting published hashes/revision
and evaluated HEAD separately. Existing no-ops use published values,
not candidate pins. Metadata conflicts fail closed."). The DevOps
updates in `Update.ps1` and the bootstrap cross-check are *not*
modified here.

## 1. Production surface authored in this slice

| path | role | notes |
| --- | --- | --- |
| `infrastructure/release/Compare.fsx` | reusable module/helper | `ArchiveObservation.ManifestSha256` captured from the embedded manifest bytes; observation never recomputes the digest at publish time |
| `infrastructure/release/Metadata.fsx` (NEW) | reusable module/helper | pure metadata projection: schema-versioned JSON, render/parse round-trip, strict field-by-field compare, file read/write, sha256 |
| `infrastructure/release/Publish.fsx` | reusable module/helper | `uploadMetadata` seam: idempotent recovery, no-overwrite, immutable-by-versioning, dev-mode gate `MCP_RELEASE_DO_UPLOAD` |
| `infrastructure/release/Runner.fs` | compiled entrypoint | orchestrates archive + metadata upload; only emits `published` after *both* remote digests are verified |
| `{dotnet,workflow}/release/Release.fsx` | release/build entrypoint | renders the canonical per-component summary including `published*` and `metadata*` fields |

`dotnet/release/Config.fsx`, `workflow/release/Config.fsx`,
`infrastructure/release/Bootstrap.fsx`, `infrastructure/release/Update.ps1`,
`infrastructure/release/Bootstrap.fsx`, and the root workflow YAML
are intentionally untouched in this slice (DevOps scope).

## 2. Producer-metadata projection contract

The shape is the seven-field projection the original issue requires
plus the schema/protocol version, archive name, and optional producer
toolchain context:

```fsharp
type ProducerMetadataProjection =
    { SchemaVersion: int
      ComponentId: string
      Version: string
      AssetUri: string
      ArchiveName: string
      ArchiveSha256: string              // lowercase-hex SHA-256 of the archive bytes
      ManifestSha256: string            // lowercase-hex SHA-256 of the embedded distribution.json bytes
      PublishedRevision: string         // committed HEAD revision (verified == candidateRevision)
      ProducerSdk: string option        // optional SDK from RELEASE_EXPECTED_SDK
      ProducerRuntime: string option    // optional runtime from RELEASE_EXPECTED_RUNTIME
      ProducerPlatform: string option   // optional platform from RELEASE_EXPECTED_PLATFORM }
```

`metadataSchemaVersion = 1`. Future schema changes update the constant
and the parse path together; older rendered bytes still parse.

The asset name is the deterministic `<componentId>-<version>-metadata.json`
(e.g. `dotnet-1.0.4-metadata.json`). Versioning the filename is what
makes the metadata immutable: re-publishing the same version reuses
the same filename, and the safe-immutable transport refuses the
overwrite.

## 3. Pure transport contract (the test seam)

`Metadata.fsx` owns these functions and never performs remote I/O. They
are the regression suite's primary test surface for metadata
consistency:

| function | inputs | output |
| --- | --- | --- |
| `render projection` | `ProducerMetadataProjection` | byte-exact JSON string (indented) |
| `parse text` | JSON text | `Result<ProducerMetadataProjection, ReleaseError>` (mismatched schema, missing fields, malformed JSON fail closed) |
| `compare existing expected` | two projections | `Result<unit, ReleaseError>` (strict field-by-field equality) |
| `sha256OfFileAsync path` | path | `Result<string, ReleaseError>` (the exact bytes the transport will send) |
| `readFromFile path` | path | `Async<Result<ProducerMetadataProjection, ReleaseError>>` (read + parse) |
| `writeToFileAsync path projection` | path + projection | `Async<Result<string, ReleaseError>>` (write + return SHA-256 of bytes written) |
| `makeProjection` (constructor) | canonical fields | projection |
| `metadataAssetName componentId version` | names | deterministic asset filename |

Round-trip property: `parse (render p) = Ok p`. Strict compare is the
existing-asset check the upload transport uses.

## 4. Bounded HTTP-effects upload transport

`Publish.uploadMetadata auth locator projection : Async<Result<string, ReleaseError>>`
publishes the projection to the versioned asset slot on the
component's release. The transport is the **safe-immutable** path:

1. Render the projection locally; hash the bytes.
2. Look up the release via `apiRequestAsync` against `api.github.com`.
3. **If the release is configured as immutable** → `Error(immutable)`.
4. **If the release does not exist** → `Error(release missing)`;
   the runner does not create releases arbitrarily.
5. **If the asset is already present with the same digest** → return
   `Ok existingDigest` (idempotent recovery; no remote write).
6. **If the asset is already present with a different digest** →
   `Error(overwrite refused)`. No retry. No clobber.
7. **If the asset is absent** → POST to
   `https://uploads.github.com/repos/{owner}/{repo}/releases/{release_id}/assets?name={filename}`
   with `Authorization: Bearer ${token}`, `Accept:
   application/vnd.github+json`, and the projection bytes as
   `application/vnd.github+json`.
8. Verify the response's reported digest equals the candidate
   digest. A mismatch is a typed failure; the runner cannot advance
   to `published`.

The transport also gates the actual remote write on
`MCP_RELEASE_DO_UPLOAD=true`. In the W3 dev environment the function
returns the typed failure `W3 dev mode: refusing to upload metadata…`
without ever calling the GitHub API.

The HTTP client behavior is shared with `uploadCandidate`:

- `HttpClient` pinned to `https://api.github.com/` (lookup) or
  `https://uploads.github.com/` (write) via absolute URLs.
- 30 s per-request timeout.
- `Authorization: Bearer ${GITHUB_TOKEN}` only; never redirected to
  a non-`github.com` host.
- No automatic redirects; the safe-redirect helper follows only
  `api.github.com` / `github.com` 3xx responses.
- No blind retry on transient 5xx; the workflow observes the remote
  before the next evaluation.

## 5. Runner coordination — `published` requires both verified

The runner enforces `published` strictly: it advances only after both
`uploadCandidate` and `uploadMetadata` return observed remote digests
that match the candidate. The state machine:

| outcome | archive upload | metadata upload | final status |
| --- | --- | --- | --- |
| `PublishPending` and (useGitHub && MCP_RELEASE_DO_UPLOAD) | `Ok` | `Ok` | **`published`** with `publishedArchiveSha256`, `publishedManifestSha256`, `publishedRevision`, `publishedMetadataSha256` |
| `PublishPending` and (useGitHub && MCP_RELEASE_DO_UPLOAD) | `Ok` | `Error` | **`failed`** (metadata path is safe-immutable; runner reports the typed failure and exits `1`) |
| `PublishPending` and (useGitHub && MCP_RELEASE_DO_UPLOAD) | `Error` | (skipped) | **`failed`**; archive success+metadata failure path is safe-resumable: next evaluation observes the remote and either recovers (`Error(overwrite refused)` becomes a no-op after matching digest) or fails closed |
| `PublishPending` and `!useGitHub` | (skipped) | (skipped) | **`publish-pending`** with exit code `2`; local-mirror transport cannot perform a remote write |
| `PublishPending` and `useGitHub && !MCP_RELEASE_DO_UPLOAD` | (skipped) | (skipped) | **`publish-pending`** with exit code `2` |
| `Unchanged` | (skipped — no remote write) | (skipped) | **`unchanged`** with `publishedArchiveSha256`, `publishedManifestSha256` (from observation), `publishedRevision` (from the observed manifest) |
| `BumpRequired` | (skipped) | (skipped) | **`bump-required`** with `currentVersion` and the typed diagnostic; exit `2` |
| `Failed` | (skipped) | (skipped) | **`failed`** with the typed diagnostic; exit `1` |

`Unchanged` reports published values from the remote observation
(`published.ArchiveSha256`, `published.ManifestSha256`, manifest's
`revision`). It never reports `candidateSha256` as a published
hash. The runner never claims published values for
`publish-pending` or `bump-required`.

## 6. Per-component summary fields (canonical)

The runner always emits the canonical field set in its JSON output,
plus the status-specific overlays. The Release.fsx script renders the
same fields in the per-component step summary:

| field | always present | status-specific notes |
| --- | --- | --- |
| `status` | yes | `published \| unchanged \| bump-required \| publish-pending \| failed` |
| `reason` / `failureReason` | yes | typed diagnostic; required `failureReason` field is shared with `reason` |
| `componentId` | yes | equal to the producer contract |
| `version` | yes | declared in the producer Config.fsx |
| `archiveName` | yes | declared in the producer Config.fsx |
| `assetUri` | yes | deterministic `releases/download/<component>/<archive>` |
| `candidateArchiveSha256` | yes | actual bytes-on-disk digest |
| `candidateManifestSha256` | yes | actual embedded `distribution.json` bytes digest |
| `currentEvaluatedHead` | yes | `git rev-parse HEAD^{commit}` |
| `candidateRevision` | yes | the candidate manifest's `revision` |
| `metadataAssetName` | yes | `<component>-<version>-metadata.json` |
| `metadataLocalPath` | yes | `MCP_RELEASE_METADATA_LOCAL_PATH` or `dist/<component>/<metadataAssetName>` |
| `producerSdk` / `producerRuntime` / `producerPlatform` | when env set | informational toolchain context |
| `publishedArchiveSha256` | `published` / `unchanged` | the verified remote digest |
| `publishedManifestSha256` | `published` / `unchanged` | the verified remote manifest digest |
| `publishedRevision` | `published` / `unchanged` | the parsed published `revision` |
| `publishedMetadataAssetName` / `publishedMetadataSha256` | `published` | observed metadata asset name and digest |
| `uploadCompleted` | yes | boolean; `true` only after both verified |

The status enum is exactly the issue requirement: `published | unchanged
| failed` plus the typed helpers `bump-required` and `publish-pending`
the runner uses to express the "decision is publish, no upload yet"
and "comparison requires a reviewed version bump" branches.

## 7. File paths (where the metadata lives)

- **Source of truth**: `<dist>/<component>/<metadataAssetName>` (alongside
  `distribution.json`). The runner computes this path automatically; an
  explicit `MCP_RELEASE_METADATA_LOCAL_PATH` overrides it for tests.
- **Release asset** (production only, `MCP_RELEASE_DO_UPLOAD=true`):
  `https://github.com/<owner>/<repo>/releases/download/<component>/<metadataAssetName>`
  via `uploads.github.com`. The DevOps-owned `release.yml` is the
  place to add a per-component Actions artifact path that surfaces the
  local file under the run summary; the YAML edit is left for the
  DevOps owner.

## 8. Run-ID evidence (`dotnet_build` MCP)

| target | runId | exit | notes |
| --- | --- | --- | --- |
| `dotnet/Mcp.Dotnet.fsproj` | `mFfYDFNPJ9M2XORPwKhslXYo5wfPRaUQKUFgakyYh2Y` | 0 | Release config; 0 errors / 0 warnings |
| `workflow/Mcp.Workflow.fsproj` | `0F5f255JFuk25PHdm2Mxxg-6gWHSGACl6sOeKpixshI` | 0 | Release config; 0 errors / 0 warnings |
| `infrastructure/scripts/Validation.fsproj` | `HT9xqiZtLczip-7z_Pai11mgXqJyBeG75dmh7cNjG1c` | 0 | analyzer rebuilt; 0 errors / 0 warnings |
| `infrastructure/release/Release.fsproj` | `39gnV9CaFuUrTx_c5_zkJ8yS9j_5mfqW6wR4v_pbnfY` | 0 | runner rebuilt; 0 errors / 0 warnings (follow-up after Unchanged revision fix) |

`Validation validate`: `OK 38 scripts validated`, exit 0 (Metadata.fsx
makes 37 → 38 scripts).
`Validation typecheck`: `OK reusable and test-helper roots typechecked`,
exit 0.

`Bootstrap.fsx` smoke run:

```
RELEASE_SDK=11.0.100-rc.1.26425.128
RELEASE_RUNTIME=Microsoft.NETCore.App 11.0.0-rc.1.26425.128
RELEASE_PLATFORM=win-x64
RELEASE_DOTNET_VERSION=1.0.4
RELEASE_DOTNET_ARCHIVE=v1.0.4.zip
RELEASE_WORKFLOW_VERSION=1.0.4
RELEASE_WORKFLOW_ARCHIVE=v1.0.4.zip
```

## 9. Behavior contract — strict-published verification

A `published` status is emitted only when both:

1. The archive's remote digest, reported by
   `api.github.com/.../releases/tags/{tag}`, equals the candidate's
   `archiveSha256`; AND
2. The metadata asset's remote digest, reported on the same response,
   equals the locally rendered metadata SHA-256.

If either verification fails, the runner reports `failed` with the
typed diagnostic. The next evaluation observes the remote again:
matching digests recover as `published`; mismatches fail closed with
the no-overwrite diagnostic and the typed comparison error.

The runner never reports `publishedRevision` from a candidate-side
value: the published revision is either the just-uploaded candidate
revision (after the metadata upload returns the observed digest), the
parsed manifest's `revision` (for an observed remote `unchanged`
archive), or absent (for `publish-pending` / `bump-required` /
`failed`).

## 10. Open facts and seams

- The transition from `W3 dev mode` to a real hosted run needs the
  same `MCP_RELEASE_DO_UPLOAD=true` + `GITHUB_TOKEN` flag as the
  archive upload; the metadata path does not introduce new env
  variables.
- The Actions artifact path (a separate concern owned by DevOps) is
  the natural way to surface the locally rendered metadata file in
  the workflow run summary; no core F# change is required.
- The regression suite (next tester owner) exercises `Metadata.render ∘
  parse == identity`, `Metadata.compare` strict-field equality on
  every field, `sha256OfFileAsync` determinism for two renderings of
  the same projection, and the upload-transport idempotent/different
  branches via injected observers. Test code can target the public
  surface of `Metadata.fsx` without a remote, an SDK, or a token.
- End-to-end remote publish still waits on a confirmed commit and the
  hosted runner. The runner's `assertCleanTree` enforces the gate.

## 11. Files in this slice (working tree, uncommitted)

```
M  infrastructure/release/Compare.fsx          (manifest SHA-256 in observation)
M  infrastructure/release/Publish.fsx          (uploadMetadata transport seam)
M  infrastructure/release/Release.fsproj       (Metadata.fsx added to compilation)
M  infrastructure/release/Runner.fs            (metadata write + verify before published)
M  dotnet/release/Release.fsx                  (canonical field set rendering)
M  workflow/release/Release.fsx                (canonical field set rendering)
?? infrastructure/release/Metadata.fsx         (NEW)
?? docs/GHI-24/metadata.md                     (this file)
```

Untracked from W3 + core.md slices that this slice keeps (none
modified):

```
?? .github/workflows/release.yml
?? docs/GHI-24/{core.md,devops.md,implementation.md,original-issue.md,research.md,solution.md}
?? dotnet/release/Release.fsx (now edited; was untracked)
?? infrastructure/release/ (now edited; was untracked)
?? workflow/release/Release.fsx (now edited; was untracked)
```

No commits, branch operations, worktree mutations, or remote writes
were performed against the source repo during this slice.
