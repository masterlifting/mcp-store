# GHI-24 W3 core completion — bounded correction evidence

## Status: implementation complete (build/typecheck evidence); end-to-end
              remote validation deferred to the post-commit host-side
              run.

This slice replaces the W3 draft (`docs/GHI-24/implementation.md`) and
the broken partial machinery it left behind. Each known defect listed
in the assignment header is corrected in source; the per-component
production surface compiles clean and the script analyzer accepts the
new files. The compiled `ReleaseRunner` carries every contract
required by issue requirements 18–26, 41, 42, 51–53, 56–58, 67, 69,
and 70.

## 1. Source tree state at the start of this slice

`git rev-parse HEAD = 30d82304e05899e2b1c83d9255fbe2f17abc0b7f` at
branch `ghi-24`. `git reflog` confirms no commit, reset, branch
operation, or stash has touched the source repo. `.git/info/exclude`
is the committed git template (no `docs/`, `infrastructure/release/`,
or `global.json` exclusions). No `global.json` exists in the source
repo. The four W3 untracked items (`docs/`, `infrastructure/release/`,
`{dotnet,workflow}/release/Release.fsx`) belong to this slice; the
`Config.fsx` 1.0.4 bumps were uncommitted from the W3 attempts and
now land in this slice with explicit rationale comments.

## 2. Bounded corrections applied

### 2.1 Transaction decision vs upload outcome separation

**Defect.** `Transaction.Published None` reported the publish
*decision* under the same outcome label as a confirmed upload; the
descriptor also described asset replacement under the dead
`Published (Some obs)` branch.

**Correction.** `infrastructure/release/Transaction.fsx`:
- `Outcome.Published None` → `Outcome.PublishPending (remoteAbsent:
  bool)` so a publish *decision* is never conflated with a confirmed
  upload.
- The `ReleaseDecide.Publish _` branch is mapped to a typed `Failed`
  failure carrying the reason so an unexpected future comparison
  branch cannot silently advance to a published state.
- `renderStepSummary` distinguishes `publish-pending (asset absent,
  upload not yet performed)` from `unchanged` and
  `bump-required`.

### 2.2 `Compare` strictness for the embedded manifest entry

**Defect.** `compareEntries` checked every entry — including the
embedded `distribution.json` — for CRC, length, compressed length,
last-write timestamp, compression method, and payload SHA-256. The
manifest entry's CRC/length/compressed-length/payload-hash are
*derived* from the body text; they always differ when the embedded
`revision` field changes. The strict check therefore rejected
strictly revision-only-equivalent archives as
`DifferentBeyondRevision`.

**Correction.** `infrastructure/release/Compare.fsx`:
- `partitionManifestEntry` separates the embedded manifest entry
  from the rest.
- `compareNonManifestEntries` compares the non-manifest entries
  byte-for-byte.
- The manifest entry is checked only on metadata fields that are
  independent of the body (`CompressionMethod` and
  `LastWriteTime`); the body itself is compared by parsing each
  side as JSON and running `compareManifests`.
- `compareManifests` checks every required field (`schemaVersion`,
  `id`, `version`, `revision`, `sdk`, `entryDll`, `archive`, `files`,
  `archiveFiles`) for exact equality except for `revision`, where
  the function reports the parsed values so the no-op outcome
  surfaces the *actual* published revision (not the candidate one).
- `compareManifests` returns `(publishedRevision, candidateRevision)`
  so `Decision.RevisionOnlyEquivalent` carries both; a later
  candidateRevision vs evaluatedRevision assertion at the
  `compareInternal` boundary fails closed when the manifest
  revision drifts away from the caller's HEAD observation.

### 2.3 `Decide` reports the actual published revision

**Defect.** `Decide.fsx`'s `RevisionOnlyEquivalent` branch used the
*evaluated* (candidate) revision as the published-revision value
in the surfaced message and summary.

**Correction.** The `Decision.RevisionOnlyEquivalent` shape now
carries `(publishedObservation, publishedRevision,
evaluatedRevision)`. `Decide.fsx` uses the parsed published revision
in both the action reason and the summary renderer. The unknown
revision case fails closed inside `compareManifests` rather than
silently substituting the candidate value.

### 2.4 Bounded HTTP GitHub API adapter replaces the `gh`-CLI shell

**Defect.** `Publish.fsx` used `gh release view` / `gh release
download` for the production transport. `gh` is not available in
the local Windows validation environment, the CI image would have
to install it, and the dependency duplicates concerns the F# code
already owns.

**Correction.** `infrastructure/release/Publish.fsx`:
- `createApiClient` returns an `HttpClient` pinned to
  `https://api.github.com/` with the GitHub-required `Accept`,
  `User-Agent`, and `X-GitHub-Api-Version` headers, a 30 s request
  timeout, and `AllowAutoRedirect` disabled.
- `apiRequestAsync` issues the request with the bearer token in the
  Authorization header; transient errors (`HttpRequestException`,
  `OperationCanceledException`) become typed failures.
- `followIfSafe` follows one 3xx redirect only when the target host
  is `api.github.com` / `github.com`. Redirects to any other host
  (which would not honour the bearer token and might attempt to
  exfiltrate the upload) are rejected as `redirect` typed failures.
- `gitHubObserver` reads `/repos/{owner}/{repo}/releases/tags/{tag}`
  for `id`, `immutable`, and `assets[]`. The asset bytes are
  downloaded through the same client; the downloaded bytes are
  `verifyDigest`-ed against the API-reported `sha256:hex` digest
  before they are observed through `ReleaseCompare.observe`.
- `uploadCandidate` is a real bounded write seam. It looks up the
  release via the API; if the release is configured as immutable,
  the upload is refused; if the asset is already present with the
  same digest (idempotent rerun), the upload recovers without
  writing; if the asset is already present with a different digest,
  the write is refused without retrying; only when the asset is
  absent does it POST to `https://uploads.github.com/...` with the
  bearer token. The response's reported digest must equal the
  candidate's local digest; a mismatch is a typed failure.
  Per-request timeout 30 s, no blind retry.
- `MCP_RELEASE_DO_UPLOAD=true` is the explicit gate for the actual
  write; the function returns a typed failure in the W3 dev
  environment so the local-validation flow performs no remote
  write.
- `localMirrorObserver` is retained for development and tests; the
  mirror transport cannot perform a remote write and explicitly
  reports `publish-pending` rather than a fake `published` outcome.

### 2.5 Runner enforces clean-tree and HEAD-revision invariant

**Defect.** `Runner.fs` did not assert the clean-tree guard before
running the transaction, did not check the candidate manifest
revision against `git rev-parse HEAD`, and conflated the publish
decision with a confirmed upload through the same `published`
status string.

**Correction.** `infrastructure/release/Runner.fs`:
- `assertCleanTree` and `committedHead` are checked first; any
  failure exits `4` so the runner never packages uncommitted or
  HEAD-divergent source.
- `assertManifestRevision manifestPath head` is then enforced; the
  manifest must encode `head` exactly.
- `PublishPending` runs through the production-transport path only
  when `MCP_RELEASE_GITHUB_OWNER`, `MCP_RELEASE_GITHUB_REPOSITORY`,
  `GITHUB_TOKEN`, and `MCP_RELEASE_DO_UPLOAD=true` are all set;
  the local-mirror transport cannot write remotely and exits `2`
  with a clear diagnostic.
- The JSON output distinguishes `published` (upload completed,
  remote digest verified), `unchanged`, `bump-required`,
  `publish-pending`, and `failed`; `uploadCompleted` is a separate
  boolean field so the per-component summary exposes the upload
  state machine independently of the comparison decision.

### 2.6 Per-component orchestrators handle the new exit codes

**Defect.** `dotnet/release/Release.fsx` and
`workflow/release/Release.fsx` mapped `bump-required` to exit `2`
and treated any other non-zero as a generic `failed` exit `1`. The
new `publish-pending` outcome is neither a clean no-op nor a hard
transport failure and needs its own exit code so the workflow can
distinguish it.

**Correction.** Both `*.fsx` orchestrators map `publish-pending` to
exit `2` (alongside `bump-required`), `published` and `unchanged`
to exit `0`, and `failed` to exit `1`. The header comments document
the exit-code mapping.

### 2.7 Authored 1.0.3 → 1.0.4 version bumps

**Defect.** Both producer `Config.fsx` files declared `1.0.3` even
though the post-#21/#23/#26/#27 refactor changes the `Mcp.*.dll`
bytes; the strict revision-only exception cannot apply.

**Correction.** `dotnet/release/Config.fsx` and
`workflow/release/Config.fsx` declare `1.0.4` with a rationale
comment that cites the historical v1.0.3 SHA-256 (the locally
download-and-verified bytes under
`C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-bootstrap\`) and
the CRC drift observed for `Mcp.Dotnet.dll` and `Mcp.Workflow.dll`.
The bumps are authored source edits; CI is never given a path to
auto-increment.

### 2.8 Producer files keep their existing role and contract

`Build.fsx` and `Pins.fsx` for both producers were already aligned
with the SDK/runtime/platform/arch policy; they now read the bumped
1.0.4 version through `ReleaseConfig.version` without further
change. `Release.fsx` for both producers keeps the existing phase
composition (Build → Pins → Tests → Runner) and adds the
`publish-pending` exit-code mapping documented in §2.6.

## 3. Run-ID evidence (`dotnet_build` MCP)

| target | runId | exit | notes |
| --- | --- | --- | --- |
| `dotnet/Mcp.Dotnet.fsproj` | `NDzxO4fflwgcceh2wisQW8FFhcIsmfaDTXUSL3-RdYk` | 0 | Release config; 0 errors / 0 warnings |
| `workflow/Mcp.Workflow.fsproj` | `6xa98kUVPdpAo52cR10zoha84Z7tPW4YWUrITKHqBSQ` | 0 | Release config; 0 errors / 0 warnings |
| `infrastructure/scripts/Validation.fsproj` | `8V5jVAh2d0qkMg6ZmjKj-cIRr8mPZSVy_KEmaznLBpw` | 0 | analyzer rebuilt; 0 errors / 0 warnings |
| `infrastructure/release/Release.fsproj` | `QUBXFUaEK241UnJ1xuSiDN4JMhnh7-gf7UbSvOXQc3U` | 0 | runner rebuilt; 0 errors / 0 warnings |

`Validation validate`: `OK 37 scripts validated`, exit 0.
`Validation typecheck`: `OK reusable and test-helper roots typechecked`, exit 0.

Bootstrap end-to-end (`dotnet fsi
infrastructure/release/Bootstrap.fsx`):

```
# release bootstrap summary
RELEASE_SDK=11.0.100-rc.1.26425.128
RELEASE_RUNTIME=Microsoft.NETCore.App 11.0.0-rc.1.26425.128
RELEASE_PLATFORM=win-x64
RELEASE_ARCHITECTURE=x64
RELEASE_DOTNET_VERSION=1.0.4
RELEASE_DOTNET_ARCHIVE=v1.0.4.zip
RELEASE_DOTNET_ASSET_URI=https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.4.zip
RELEASE_DOTNET_ENTRY=Mcp.Dotnet.dll
RELEASE_WORKFLOW_VERSION=1.0.4
RELEASE_WORKFLOW_ARCHIVE=v1.0.4.zip
RELEASE_WORKFLOW_ASSET_URI=https://github.com/masterlifting/mcp-store/releases/download/workflow/v1.0.4.zip
RELEASE_WORKFLOW_ENTRY=Mcp.Workflow.dll
```

`Update.ps1 -Component dotnet` (the minimal SDK bootstrap shell the
workflow calls): exit 0; output identical to the bootstrap run above.

## 4. Historical v1.0.3 byte-comparison re-confirmed

Both assets under `Temp/opencode/ghi-24-bootstrap` were re-hashed
during this slice:

| asset | size | SHA-256 (lowerhex) | sources confirmed |
| --- | --- | --- | --- |
| `dotnet-v1.0.3.zip` | 1,289,835 B | `da3acb20007f8015e541ee7d68b48adf49d232c4eea41190a62073073d1a1614` | API digest + local recompute match |
| `workflow-v1.0.3.zip` | 1,482,675 B | `8677413d830bd7fbc559e1847222ae4d3ee8582de4d9ba15a224a939dcc39db1` | API digest + local recompute match |

The per-entry comparison documented in
`docs/GHI-24/implementation.md` §2 still holds: the published
`Mcp.*.dll` binaries differ in CRC and SHA-256 from the
post-refactor binaries the source produces today, so the strict
revision-only exception cannot apply to a same-version rebuild.
The 1.0.4 bumps authored in §2.7 are the only correct response.

## 5. Anti-perpetuation: invalid W3 artifacts discarded

The W3 slice's provenance-invalidated state (`.git/info/exclude`
exclusions, source `global.json`, masked-temp builds, the
`global.json`-hidden SDK selection) is no longer present:

- `Test-Path .git/info/exclude` returns the committed git template
  with no extra entries.
- `Test-Path global.json` returns `False`.
- No new commits exist in the source repo's reflog.
- The four `dotnet_build` runs in §3 build from the corrected
  source tree on a fresh build cache; no `obj/` state from the W3
  masked builds persists.

The compiled runner does **not** perform a real remote write
during this validation slice. `MCP_RELEASE_DO_UPLOAD` is unset by
default in the local environment; the function returns the typed
`W3 dev mode: refusing to upload …` failure so a remote write
cannot occur by accident.

## 6. Open facts and seams for the next owner

### 6.1 End-to-end remote run (DevOps + tester)

This slice does not perform a real `Mcp.Dotnet` / `Mcp.Workflow`
1.0.4 publish against `masterlifting/mcp-store`. The actual upload
needs:

- a confirmed commit that lands the 1.0.4 bumps and the
  release-module changes,
- a hosted runner with `GITHUB_TOKEN` and the GitHub releases API
  reachable,
- `MCP_RELEASE_GITHUB_OWNER=masterlifting`,
  `MCP_RELEASE_GITHUB_REPOSITORY=mcp-store`,
  `MCP_RELEASE_DO_UPLOAD=true` set on the run,
- the existing `release.yml` (already authored in W3, uncommitted
  here) reviewed by the DevOps owner before the upload flag is set
  in CI.

### 6.2 Regression suite (tester)

The Decision/Compare/Transaction/Publish surfaces are exposed
through the `RemoteObserver` injection seam; the next tester owner
should target `ReleaseCompare.compare`, `ReleaseTransaction.run`,
and `ReleasePublish.uploadCandidate` with injected transports. The
assigning constraint was `mayTest false`; this slice therefore
authored the production surface but not the regression suite.

### 6.3 `release.yml` review

The W3 `.github/workflows/release.yml` (`concurrency` group
`release-main`, `cancel-in-progress: false`, `queue: max` not
enforced explicitly, pending-run observation step) was authored
under the DevOps owner's scope and is not touched by this slice.
The YAML's literal `dotnet-version: 11.0.100-rc.1.26425.128` is
the install only; the production identity check happens inside
`Bootstrap.fsx` against the canonical producer Config.fsx, which
the YAML does not duplicate.

### 6.4 Concurrent-run behavior

Hosted GitHub Actions concurrency is observed at the pending-run
step only. Genuine `queue: max` parity and the absence of
clobbering on rapid merges cannot be established by publishing
during development; the DevOps owner must observe hosted behavior
during the post-commit run.

## 7. Files in this slice (working tree, uncommitted)

```
M  dotnet/release/Config.fsx               (1.0.3 -> 1.0.4 + rationale)
M  workflow/release/Config.fsx             (1.0.3 -> 1.0.4 + rationale)
M  infrastructure/release/Compare.fsx      (compareInternal refactor)
M  infrastructure/release/Decide.fsx        (RevisionOnlyEquivalent signature + reason)
M  infrastructure/release/Transaction.fsx   (Publish vs Published split, dead-code removed)
M  infrastructure/release/Publish.fsx       (HTTP GitHub API transport + bounded upload)
M  infrastructure/release/Runner.fs         (clean-tree guard, HEAD check, transport branching)
M  dotnet/release/Release.fsx               (publish-pending exit-code mapping)
M  workflow/release/Release.fsx             (publish-pending exit-code mapping)
?? docs/GHI-24/core.md                      (this file)
```

Untracked from W3 that this slice keeps (none modified, none
re-implemented):

```
?? .github/workflows/release.yml            (DevOps owner scope; not touched)
?? docs/GHI-24/                             (release documentation directory; not touched)
?? dotnet/release/Release.fsx               (re-author already present from W3)
?? workflow/release/Release.fsx             (re-author already present from W3)
?? infrastructure/release/                  (new surface; this slice refactored its files)
```

No commits, branch operations, worktree mutations, or remote writes
were performed against the source repo during this slice.
