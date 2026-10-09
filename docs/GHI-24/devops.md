# GHI-24 W3 DevOps completion — CI/toolchain evidence

## Status: implementation complete for the assigned CI/toolchain surface;
              hosted GitHub Actions behavior and the real upload path remain
              unverified and must be observed on the hosted run. The core F#
              metadata API that this document previously reported missing is
              now implemented (`docs/GHI-24/metadata.md`); the workflow surfaces
              the VERIFIED published projection and labels candidate evidence
              distinctly. See §5.

Authority: `docs/GHI-24/original-issue.md` (verbatim) and the frozen
`docs/GHI-24/solution.md`. Core F# transaction/comparison/publish/metadata
surfaces are authored by the core owner (`docs/GHI-24/metadata.md`); this
DevOps slice owns the root workflow, the minimal SDK bootstrap, the canonical
producer toolchain declarations, the Bootstrap projection cross-check, and this
document.

## 1. Corrected workflow facts (`.github/workflows/release.yml`)

| Surface | Corrected behavior | Requirement |
| --- | --- | --- |
| Trigger | `on.push.branches: [main]` only. No `workflow_dispatch`, no `paths`/`paths-ignore`. | 6, 7, 8 |
| Event SHA | Checks out `github.sha` exactly and asserts `git rev-parse HEAD == github.sha`. | 69 |
| Stale ancestry | Resolves `origin/main` through the bounded compare API and requires the event SHA to be `ahead`/`identical` relative to it; a proven-ancestor stale event releases the exact SHA (never the newer tip) with a warning; an orphaned/diverged SHA fails closed. | 69 |
| Concurrency | Literal `group: release-main`, `queue: max`, `cancel-in-progress: false`. `queue: max` is the documented GitHub Actions value (up to 100 pending runs; cancels only additional runs when full). | 37, 38, 39, 40 |
| Permissions | Workflow default `contents: read`; bootstrap job `contents: read` + `actions: read`; only the publishing matrix job grants `contents: write`. `GITHUB_TOKEN` is wired only to the release transaction step (no PAT). | 35, 36 |
| Runner | `windows-latest` (x64), matching the canonical `win-x64` platform; the compiled `ReleaseRunner.exe` is a Windows binary. | 68 |
| SDK install | `actions/setup-dotnet` receives `${{ steps.project.outputs.sdk }}` projected from canonical `Config.fsx`; no SDK literal exists in YAML and no root `global.json` is introduced. | 10, 11, 13, 61, 68 |
| Toolchain verify | `Update.ps1 -Verify` runs F# `Bootstrap.fsx`, which checks executing SDK, exact runtime, and platform against the evaluated canonical config and against the projection recomputed in the same command. | 68 |
| Tests before publish | `Release.fsx` runs Build → Pins → DistributionTests → strict transaction before any upload seam. | 16, 60 |
| Independent producers | Matrix `fail-fast: false`; each producer reports independently. | 17, 43 |
| Exit preservation | The transaction pipes through `Tee-Object` and then `exit $LASTEXITCODE`; the summary step is `if: always()`, so a non-zero native exit cannot be masked. | 41 |
| Upload gate | `MCP_RELEASE_DO_UPLOAD: "true"` and `GITHUB_TOKEN` are set only on the release transaction step; no local run sets the flag. | 19, 27, 58 |
| Verified metadata artifact | `release-metadata-<component>` contains a status-labelled `release-status.json` plus `published-metadata-<component>.json` **only when the runner emits `status=published`**. Verified published hashes/revision are parsed from the runner's canonical summary; candidate values are never emitted as published. | 26, 49, 62, optional 50 |
| Candidate pin evidence | `consumer-pins.json` is uploaded under the distinct artifact `candidate-consumer-pins-<component>` and labelled "candidate pin preparation only; NOT release metadata" in the step summary. | 47 |
| Artifact on failure | When the runner does not report `status=published`, the metadata artifact is `status-evidence-only` (exit code + captured diagnostics); the pre-upload candidate metadata file (written by the runner to `<dist>/<component>/<component>-<version>-metadata.json`) is **not** copied, so it can never be mislabelled published. | 41, 42 |
| Queue reconciliation | A real, bounded `gh api` call reads one page (≤100) of push runs for this workflow on `main`, prints a run table to `$GITHUB_STEP_SUMMARY`, and warns on cancelled push runs. No echo-only diagnostic. | 40, 71 |

Official source for the concurrency syntax:
<https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency>
(confirms `queue: single|max`, `queue: max` allows up to 100 pending runs and
cancels additional runs when full, and that `queue: max` cannot combine with
`cancel-in-progress: true`).

## 2. Canonical toolchain projection

`infrastructure/release/Update.ps1` now has two modes:

- `-Project`: strict, deterministic text projection of the plain string
  literals `componentId`, `version`, `entryDll`, `sdkVersion`, `runtimeVersion`,
  and `platform` from both `*/release/Config.fsx`. It fails closed on a missing
  or duplicate literal and on producer disagreement, requires no F# evaluation
  and no installed SDK, and writes `sdk`/`runtime`/`platform` to
  `$GITHUB_OUTPUT`.
- `-Verify -Component <c>`: recomputes the same projection, exports
  `RELEASE_EXPECTED_SDK`/`RELEASE_EXPECTED_RUNTIME`/`RELEASE_EXPECTED_PLATFORM`,
  runs F# `Bootstrap.fsx`, and forwards the verified summary to `$GITHUB_ENV`.

`dotnet/release/Config.fsx` and `workflow/release/Config.fsx` now declare the
canonical `runtimeVersion` and `platform` alongside `sdkVersion`, so SDK,
runtime, platform, and architecture are sourced only from canonical config.
`Bootstrap.fsx` reads those fields through its isolated child `dotnet fsi`
evaluation, requires the exact runtime version (no prefix fallback), and
cross-checks projection vs evaluated config vs executing toolchain.

## 3. Local validation evidence (factual)

| Check | Command | Result |
| --- | --- | --- |
| Release runner builds (incl. `Metadata.fsx`) | `dotnet_build infrastructure/release/Release.fsproj` (Release) | exit 0, 0 errors / 0 warnings; runId `MOdJLC0IT_dk9aFxf8E3yqvGvLE93PQayx6uuOl5eVQ` |
| Analyzer builds | `dotnet_build infrastructure/scripts/Validation.fsproj` (Release) | exit 0, 0 errors / 0 warnings; runId `ojOHWZjgIYw40FIwvtn4yjyclqOF2659-Skb1yY-v2g` |
| Script contracts | `Validation.exe validate --repo-root .` | `OK 38 scripts validated`, exit 0 |
| Typecheck lane | `Validation.exe typecheck --repo-root .` | `OK reusable and test-helper roots typechecked`, exit 0 |
| SDK projection (no F#) | `Update.ps1 -Project` | exit 0; `RELEASE_SDK=11.0.100-rc.1.26425.128`, `RELEASE_RUNTIME=11.0.0-rc.1.26425.128`, `RELEASE_PLATFORM=win-x64` |
| Toolchain cross-check | `Update.ps1 -Verify -Component dotnet\|workflow` | exit 0; executing SDK/runtime/platform match canonical/projection |
| Cross-check fails closed | `Bootstrap.fsx` with a wrong `RELEASE_EXPECTED_SDK` | exit 1 (terminating mismatch) |
| Workflow YAML parses | PyYAML `safe_load` | parses; trigger=`push` only; `queue: max`; `cancel-in-progress: false`; `contents: read` default, `contents: write` release job only |
| Embedded PowerShell parses | `[Parser]::ParseFile` over every `run:` block (9 blocks) | all parse |
| Action pinning | every `uses:` is a 40-hex commit SHA with a version comment | no floating ref |
| Summary/artifact logic | summary step exercised against a synthetic `status=published` log and a synthetic `exit=2` log | published path emits `published-metadata-<c>.json`; failure path emits `status-evidence-only` and does **not** copy candidate metadata |

The runner and analyzer build evidence is the core owner's
(`docs/GHI-24/metadata.md` §8) re-confirmed here; the YAML/script/summary
checks are this DevOps slice's own. These are local static/config/build checks
only. No workflow run, release upload, or remote write was performed.

## 4. Requirement matrix — factual status of the changed surfaces

| Req | Surface | Factual status in this slice |
| --- | --- | --- |
| 6, 7, 8 | single push-to-main trigger, no dispatch | implemented + YAML-verified |
| 35, 36 | minimum permissions, no PAT | implemented + YAML-verified |
| 37–40 | concurrency serialization + `queue: max` + overflow observability | YAML-verified syntax; hosted queue behavior unverified (§6) |
| 10, 11, 13, 61, 68 | canonical-only SDK/version/runtime/platform; pre-install projection | implemented; projection and cross-check locally executed |
| 16, 17, 43, 60 | validation before publication, independent producers | implemented in workflow composition; end-to-end hosted run unverified |
| 41, 42 | fail-closed, no blind retry | implemented in the workflow entrypoint; core transaction owned by prior engineer |
| 26, 49, 62 | verified metadata projection + pin preparation | verified `published`/observed values surface in the step summary and `release-metadata-*` artifact; candidate pins are a separate, distinctly labelled artifact (§5) |
| 69 | exact SHA + safe ancestry | implemented via bounded compare API; hosted behavior unverified |
| 64, 65 | Telegram untouched, non-goals respected | no Telegram file is referenced or modified |

Rows not listed (core comparison/decision semantics, historical bootstrap
comparison, regression tests) remain owned by the prior core engineer and the
next tester; this slice makes no new claim about them.

## 5. Core metadata API — resolved and wired into the workflow

The core owner implemented the previously-missing neutral projection
(`docs/GHI-24/metadata.md`). The exact emitted path the workflow consumes:

- Local projection file (always written by the runner before the transaction):
  `<dist>/<component>/<component>-<version>-metadata.json`, i.e.
  `dotnet/dist/dotnet/dotnet-1.0.4-metadata.json` and
  `workflow/dist/workflow/workflow-1.0.4-metadata.json`. It is rendered from
  candidate-side facts; on its own it is **not** proof of publication.
- Runner canonical summary (stdout, parsed from the transaction log) carries
  `status`, `publishedArchiveSha256`, `publishedManifestSha256`,
  `publishedRevision`, `publishedMetadataAssetName`,
  `publishedMetadataSha256`, `metadataLocalPath`, and the candidate fields.
- `status=published` is emitted only after the archive **and** metadata remote
  digests are verified; `status=unchanged` reports observed published values.

The workflow now (a) parses those fields, (b) writes a `verified` provenance
record, (c) copies the local projection into the artifact only on
`status=published`, and (d) never presents candidate values (including the
pre-upload candidate metadata file) as published. Candidate pin preparation
(`consumer-pins.json`) remains a separate, distinctly labelled artifact.

The runner selects the local metadata path deterministically; the workflow does
not hard-code a second path and does not duplicate the projection fields in
YAML.

## 6. Remaining hosted limits (cannot be established locally)

1. Actual GitHub Actions queue behavior (`queue: max` capacity, FIFO order) is
   documented but cannot be exercised without a hosted run; the reconciliation
   step only observes the bounded run page.
2. The real upload path (`uploadCandidate` → `uploads.github.com` with digest
   verification) is gated by `MCP_RELEASE_DO_UPLOAD` and was not executed.
3. Immutable-release container detection and historical `v1.0.3` byte
   comparison depend on authenticated remote bytes and were not executed.
4. Stale-event handling releases a proven ancestor with a warning; the runner's
   observer resolves the specific declared asset, so detecting that a *newer*
   version already exists for the tag (a stale-regression guard) is still not
   an enforced gate. This is a known residual, not a silent success.
5. `actions/setup-dotnet` must resolve the exact canonical SDK on the hosted
   image; local verification used the preinstalled SDK.
6. The workflow uses the `windows-latest` alias, which is x64 but moves between
   Windows Server images over time. The canonical `win-x64` platform and the
   exact runtime are enforced, but byte-level reproducibility across hosted
   image updates is not proven; pinning a dated `windows-20xx` image is the
   follow-up if byte-stability across image changes is required.

## 7. Non-goals respected

No Telegram source/workflow is referenced. No commit, push, branch, PR, reset,
stash, `.git` mutation, temp commit, assume-unchanged/exclude mask, or remote
write was performed. No root `global.json` was introduced. No producer version
was auto-incremented; the authored 1.0.4 values are unchanged from the core
slice.

## 8. Clean-commit package-validation commands (canonical)

`BuildProvenance.assertCleanTree` refuses to package from a dirty tree, and the
runner asserts `manifest.revision == git HEAD`. These commands are therefore
valid only on a clean checkout of the committed implementation SHA; they mirror
what `release.yml` executes per component. Do **not** run the upload path
locally.

From the repository root, on the clean commit:

1. `dotnet build infrastructure/release/Release.fsproj -c Release`
2. `dotnet fsi <component>/tests/DistributionTests.fsx`
3. `dotnet fsi <component>/release/Build.fsx`
4. `dotnet fsi <component>/release/Pins.fsx`
5. `dotnet fsi <component>/release/Release.fsx`
6. For a network-free decision, set `MCP_RELEASE_MIRROR_ROOT=<bootstrap-dir>`
   and omit the production transport; exit `2` means `publish-pending` or
   `bump-required` (no publication), exit `0` means `published`/`unchanged`.
7. Repeat steps 2–6 for both `dotnet` and `workflow`.

For the authenticated, non-writing observation (steps 2–5 with the production
transport but `MCP_RELEASE_DO_UPLOAD` unset) set env **names only**:
`MCP_RELEASE_GITHUB_OWNER`, `MCP_RELEASE_GITHUB_REPOSITORY`, `GITHUB_TOKEN`.
The single hosted write path is `release.yml` with
`MCP_RELEASE_DO_UPLOAD=true`; that flag must never be set on a workstation.
