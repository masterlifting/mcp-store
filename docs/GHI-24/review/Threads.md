# Authoritative PR #35 remediation threads

Read via the supported GitHub review-thread API at head
`05a69ced8319b61a6bf0e24cad6d320a0a879283`. All eight required threads are open,
not outdated and authored by `masterlifting`; each contained one comment. Nine
threads were returned on one page (`hasNextPage=false`). Do not resolve them on
the reviewers' behalf. The ninth withdraws deletion and is not a blocking finding.

## 4242892984 — Metadata preflight — AC-01

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4242892984

`[P1] Validate the metadata state before permitting a new archive upload.` `ArchiveAbsent` unconditionally returns a publish decision, although `ObserveMetadata` has already run and may have returned `MetadataObserved` with conflicting contents, `MetadataCorrupt`, or a transport error. `Runner.executeResolution` then uploads the ZIP first and only discovers the incompatible/unavailable metadata when uploading the metadata asset. With immutable asset names, this can leave a newly published ZIP permanently paired with an inconsistent existing metadata asset. Fail closed on corrupt/unknown metadata *before* the ZIP POST, and explicitly validate any pre-existing same-version projection (or a supported recovery case). Add tests for `ArchiveAbsent + MetadataObserved(mismatch)` and `ArchiveAbsent + MetadataCorrupt/Error`, asserting zero POSTs.

## 4242892986 — Metadata identity — AC-02

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4242892986

`[P1] Compare every published metadata identity field against the verified archive.` The `MetadataObserved` branch currently checks only `componentId`, `version`, `archiveSha256`, and `manifestSha256`. It accepts an existing metadata asset with the wrong `publishedRevision`, `assetUri`, `archiveName`, schema/toolchain fields, or other inconsistent values and reports `unchanged`, even though the immutable remote projection remains wrong. At minimum compare `publishedRevision` with `parseEmbeddedManifestEntry publishedObs`, and the URI/name/schema and supported SDK/runtime/platform against their authoritative values (while accounting for historical schema policy). Exercise each field with a negative test: an unchanged archive must not imply consistent metadata.

## 4242892987 — GitHub digest — AC-03

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4242892987

`[P2] Validate the API-reported SHA-256 in the upload readback probe.` `claimedDigest` is bound here but never parsed or compared with the downloaded bytes. Consequently, after POST (or on the idempotent upload path), a release response claiming `sha256:BAD...` is accepted as `AssetVerified` as long as the downloaded bytes equal the candidate. `gitHubObserver` checks this digest on its initial read; the upload/readback path must enforce the same contract. Parse and require a valid claimed digest, compare it to the downloaded SHA-256, and add a scripted-HTTP regression where remote bytes match but the release JSON digest is missing/malformed/mismatched.

## 4242958691 — Compiled application and DRY — AC-04/05

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4242958691

`[P2, architecture] Make the existing compiled F# release project the canonical production/test boundary.` `Release.fsproj` already builds `ReleaseRunner.exe`, but it compiles substantial reusable `.fsx` modules via `<Compile>` while each producer still has a 216-line FSI `Release.fsx` wrapper; release regressions are run as separate `dotnet fsi` scripts by `Suite.ps1`. This creates multiple execution/type-check/init paths for the same release logic. The comment excluding `Bootstrap.fsx` because its top-level async execution hangs the compiled entrypoint is a concrete indication that reusable logic and entrypoint side effects are not cleanly separated. **Additional confirmed DRY evidence:** The 216-line `dotnet/release/Release.fsx` and `workflow/release/Release.fsx` wrappers are structurally identical: after substituting only producer names and ignoring comments/empty lines, all 183 substantive normalized lines match. This is maintained duplicate release orchestration, not merely similar producer-specific configuration. In the compiled refactor, keep per-producer canonical `Config.fsx` values separate but move the shared build/pins/test/runner sequencing and structured failure reporting into one compiled orchestration path. Retain thin producer-specific script entrypoints only if an existing legitimate consumer needs them. Please consolidate **this existing** release application instead of growing a hybrid script framework: put reusable release domain/transport/orchestration logic into ordered compiled `.fs` modules under `Release.fsproj`, with a thin explicit CLI entrypoint; eliminate top-level side effects from reusable modules and redundant `#load` dependency graphs in production; make the compiled runner the normal per-component execution path; extract the duplicated producer-wrapper orchestration where practical; keep `dotnet/release/Config.fsx` and `workflow/release/Config.fsx` authoritative (including versions, SDK, and asset URIs); do **not** introduce a second configuration source or release engine; prefer one compiled regression-test project exercising the same implementation and its existing injected HTTP transport; preserve the comprehensive negative cases, including the three outstanding correctness findings in this review; do not trade away observable acceptance coverage merely to reduce the line count; retain only the genuinely necessary scripts: a minimal **pre-SDK-install bootstrap** (a compiled net11 runner cannot run before the pinned SDK/runtime is available) and existing producer Build/Pins/Distribution entrypoints where converting them would be unrelated scope. This is a maintainability/consistency request, **not** a claim that `.fsx` cannot be compiled or that all scripts must disappear. Avoid a broad rewrite of existing producer packaging and keep fail-closed/idempotent release semantics intact.

## 4243003708 — Enrollment authority — AC-06

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4243003708

`[P2, extensibility foundation] Avoid competing sources of truth for release participation.` This `$producers` table and `[ValidateSet("dotnet", "workflow")]` duplicate the participant list in `Bootstrap.fsx::producerConfigPaths` and `.github/workflows/release.yml::matrix.component`; the FCS target list and regression tests also enumerate exactly two producers. A new one can be silently omitted by some stages. For this PR keep **only Dotnet and Workflow** as supported targets, but use a single authoritative enrollment roster *or* add a deterministic cross-stage consistency check that fails on drift. Each producer's canonical `Config.fsx` must still own its version, SDK, allowlist, and URI; participation is a separate concern. Do not add dynamic discovery or heterogeneous adapters here: [#37](https://github.com/masterlifting/mcp-store/issues/37) owns actual multi-language onboarding.

## 4243003716 — Scoped toolchain invariant — AC-07

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4243003716

`[P2, scoped toolchain invariant] Do not make one shared .NET toolchain a universal release requirement.` `verify` requires one runtime version across *all* registered producers, then compares each SDK/runtime/platform to one execution identity. The GitHub workflow likewise provisions one SDK. This is valid for the **current two .NET producers**, but prevents independent toolchains, platforms, or languages. In #24, confine/document the equality check as a current Dotnet/Workflow bootstrap constraint and keep generic immutable-asset decisions/transport independent of it. Per-producer toolchain setup and separate runner selection are explicitly deferred to [#37](https://github.com/masterlifting/mcp-store/issues/37); no broad adapter-framework rewrite is requested now.

## 4243003720 — Comments — AC-09

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4243003720

`[P3, comments policy] Replace review-history labels with present-tense rationale.` `// F7 + F2`, `// F2` and `// F9` here identify previous findings/phases rather than why the current code needs a non-obvious constraint. Canonical [OpenCode comments.md](https://github.com/masterlifting/opencode/blob/main/capabilities/software/knowledge/comments.md) requires ordinary one-line **why-only** comments without ticket IDs/phase labels or narration of obvious control flow. Apply a bounded pass to the **new release code, scripts and YAML**; keep comments describing real security/immutability traps, required `// role:` tooling metadata and API documentation. Do not rewrite unrelated legacy sources.

## 4243017168 — Filesystem naming — AC-08

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4243017168

`[P2, filesystem architecture] Normalize the naming of the touched checker now, including its paired tests.` `SyncOverAsyncChecker.fs` is already being modified by this PR, and the adjacent `SyncOverAsyncCheckerTests.fs` is compiled with it. Both use multiword semantic filenames, contrary to the canonical [one-word filesystem rule](https://github.com/masterlifting/opencode/blob/main/capabilities/software/knowledge/architecture.md). Please perform the bounded rename as part of the release refactor, even though the rename itself is behavior-neutral: e.g. `dotnet/tests/Mcp.Dotnet.Tests/blocking/Checker.fs` and `blocking/Tests.fs`. Update their ordered `<Compile Include>` entries and any exact-path references while preserving module names/behavior and all existing tests. Do not use the fact that these names predate the PR to perpetuate them in code actively being changed. The existing `Mcp.Dotnet.Tests.fsproj` identity and framework/tool-conventional project naming do not need an unrelated rename. Do not rename unrelated legacy files elsewhere.

## 4242901758 — Withdrawn deletion finding (not blocking)

https://github.com/masterlifting/mcp-store/pull/35#discussion_r4242901758

`Correction / withdrawn as a blocking PR finding.` My earlier request to delete `docs/GHI-24/**` categorically was not justified. The canonical OpenCode `skills/task/SKILL.md` explicitly permits durable task evidence under `.tasks/<TASK-ID>/docs/`, task scripts under `scripts/`, and optional `SPEC.md`; `skills/task/scripts/TaskScratch.fsx` actively supports promoting verified evidence there. However, the current Workflow producer's `validateTaskDirectory` in `workflow/domain/Workflow.fs` rejects every entry other than `runtime.json`, so using the prescribed task-local location breaks subsequent `task_get`/`task_apply`/`task_validate` operations. The `docs/GHI-24/` workaround therefore reflects a real cross-repository contract mismatch, not an inherent prohibition against keeping task documents. Please **preserve useful task evidence** while that mismatch is resolved, rather than deleting it on my earlier instruction. Deciding which records belong in the product's committed documentation versus task-local storage is a separate repository-retention decision; this corrected comment does not block PR #35. The Workflow/OpenCode inconsistency should be addressed with a safe task-directory allowlist, path-safety tests, and an archive policy.
