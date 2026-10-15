## Goal

Automate Dotnet and Workflow producer release publication with a root GitHub Actions workflow so every accepted push to `main` deterministically evaluates the current producer distributions and publishes any new versioned release assets without manual GitHub release work.

`telegram/**` and Telegram-owned workflows are explicitly out of scope.

## Dependency / ordering

Required predecessors:

- #21 — functional error/async baseline (complete);
- #23 — final domain-oriented physical layout;
- #26 — reusable F# script analyzability baseline;
- #27 — harness-neutral Workflow profile baseline.

Implement after the producer structure/profile/script baselines so release automation is written once against the stable producer surface.

```text
#23 -> #26 ─┐
  └──> #27 ─┴-> this issue -> #28 -> #29 -> #18 / #13 / #16
```

The purpose of landing this before later producer feature work is that #28 can establish the final harness-independent distribution contract on top of a stable automated publication path, and subsequent producer changes can publish through that contract.

## Trigger

Add one root workflow, conceptually:

```text
.github/workflows/release.yml
```

Trigger on:

```text
push to main
```

A merged pull request therefore causes release evaluation automatically. Direct accepted pushes to `main` behave the same way; the canonical branch state is the release authority.

Do not require a separate manual release step for the normal path.

A manual `workflow_dispatch` retry/debug entry may be provided if it invokes the exact same deterministic release logic and cannot overwrite an existing version.

## Scope

The root release workflow owns only:

- `dotnet` distribution publication;
- `workflow` distribution publication.

It must not modify or invoke:

- Telegram release/build workflows;
- Telegram packaging;
- Telegram source.

## Source of truth

Keep producer release metadata in source control.

For each producer, the canonical release config remains the single source of truth for at least:

- component id;
- version;
- SDK/runtime requirements needed for packaging;
- archive name;
- release asset URI policy;
- package allowlist/entry assembly.

GitHub Actions must **not** duplicate producer version numbers in YAML.

If SDK setup requires metadata before F# build scripts can run, add the smallest deterministic metadata projection/helper needed to expose the canonical config to the workflow rather than maintaining a second hard-coded SDK/version table.

CI must never auto-increment or invent a semantic version. A version change is an authored source change reviewed with the producer change.

## Harness-independence

Release automation publishes producer artifacts and producer-owned metadata only.

It must not know or modify OpenCode agents/descriptors, Claude Code configuration, Codex configuration, or any other harness-specific integration surface. Consumer pinning/aliasing/configuration is downstream.

## Existing release model

Preserve the currently accepted release-container/asset contract unless a separate issue explicitly changes it:

```text
GitHub release/tag: dotnet
  assets:
    v1.0.3.zip
    v1.0.4.zip
    ...

GitHub release/tag: workflow
  assets:
    v1.0.3.zip
    v1.0.4.zip
    ...
```

Consumer asset URIs therefore remain compatible with the existing producer release config.

Do not redesign the tag/version model in this issue.

## Implementation clarifications — audited against `main` (2026-10-09)

At audit time #21, #23, #26, and #27 were closed; #26 was merged in PR #32. The default branch is `main`; root `.github/workflows` does not exist. Existing `dotnet` and `workflow` releases contain `v1.0.3.zip`; both canonical `release/Config.fsx` files still declare `1.0.3` and SDK `11.0.100-rc.1.26425.128`.

1. **Initial bootstrap:** compare each present-day candidate against already published `v1.0.3.zip` under the strict provenance exception below. If it differs beyond revision, author an **explicit, reviewed version bump in the implementing PR** for that producer. Never silently overwrite the historical asset or auto-increment a version in CI. Do not assume older published bytes match post-refactor code.
2. **Actual SDK:** install exactly the configured SDK and verify the SDK executing builds/tests/publish matches its canonical config; merely stamping the configured value into the manifest does not prove this. Metadata projection must not introduce a second source of truth. Select/validate platform and architecture for reproducible packages.
3. **Source SHA:** check out the event's `github.sha` rather than a moving `main` ref; verify HEAD and manifest revision against it before any new publish. On re-running a stale event, fail closed if branch/release conditions are unsafe; never move existing component tags.
4. **Existing release containers:** preserve fixed `dotnet`/`workflow` tags and release URLs. GitHub immutable-release mode forbids adding assets after publication, which conflicts with this historical multi-version-per-release model. Detect incompatible settings and fail with an actionable message; don't silently change tag policy or bypass immutability.
5. **Additional tests:** two successive main commits with unchanged producer inputs (including docs-only or Telegram-only changes) must classify the second build as unchanged despite different embedded revision. Cover identical archives, narrowly revision-only-equivalent archives, changed runtime/SDK/manifest/archive metadata, malformed or mismatching published digests, pre-existing `v1.0.3` bootstrap, pending-run queue behavior, and no Telegram changes.

These are scope clarifications, not permission to redesign public distribution formats or consumer integration.

## Per-component release transaction

For each owned producer independently:

```text
checkout exact main SHA
  -> establish canonical SDK/toolchain
  -> run focused build/tests required by release policy
  -> build distribution from that exact SHA
  -> run distribution/package validation
  -> prepare/validate producer release metadata
  -> resolve component version + archive name
  -> inspect existing GitHub release asset
  -> publish new immutable asset OR prove exact no-op
```

Dotnet and Workflow must fail/report independently enough that diagnostics identify the affected producer, but publication must never occur before that producer's required validation passes.

## Immutable asset rule

Never overwrite or clobber an already published versioned asset.

For expected asset `v<version>.zip`:

### Asset absent

- validation/build/pins must pass;
- upload the newly built archive;
- report archive SHA-256, manifest SHA-256, producer revision, version, and asset URI.

### Asset already exists

Download/read the existing asset, verify its published digest and embedded manifest, and compare it deterministically with the candidate produced from the checked-out `main` SHA.

**Provenance/byte-equivalence exception.** The existing `infrastructure/Distribution.fsx` embeds committed HEAD as `distribution.json.revision` inside the ZIP; an unchanged producer rebuilt after an unrelated push necessarily generates a different ZIP. Do not falsify or remove the manifest revision to work around this.

- Byte-identical asset -> successful no-op.
- Asset differs **only** because the embedded manifest `revision` differs -> successful no-op **after** verifying the remote digest, archive structure/metadata, every runtime and NOTICE payload byte, and every other manifest field match. The existing published asset remains unchanged.
- Any other difference (including SDK, runtime, other manifest data, archive metadata, or inconclusive equivalence) -> **fail closed** with an explicit version-bump diagnostic.

The comparison must be strict and deterministic and testable without GitHub. For a no-op report the **published** archive hash, manifest hash, and published revision, plus the distinct current evaluated HEAD. Never misreport newly built candidate hashes/revision as published.

Never use `--clobber`, replacement upload, deletion-and-reupload, mutable tags/assets, or another mechanism that changes bytes associated with an existing version.

This invariant prevents a merge that changes packaged bytes without changing the source-controlled producer version from silently mutating an existing release.

## Package-change handling

Do not rely solely on fragile path filters to decide whether a producer changed.

Shared infrastructure, project metadata, SDK/runtime inputs, or restructuring may affect package bytes without touching one obvious producer file.

Preferred correctness model:

```text
build deterministic candidate
  -> compare with released asset for declared version
  -> byte-identical OR strictly revision-only equivalent: no-op
  -> asset absent: publish
  -> any other difference: version-bump failure
```

Path-based optimization may skip obviously unrelated work only when deterministic tests prove it cannot hide a changed package.

## Provenance

Every published archive and manifest must identify the exact committed `main` revision from which it was built.

Preserve the existing clean-HEAD/revision validation semantics in the shared release/pin infrastructure.

Generated `dist/**` output must not weaken clean-source provenance checks.

The Action summary should expose, per component:

- status: `published` | `unchanged` | `failed`;
- version;
- revision;
- asset URI/name;
- archive SHA-256;
- manifest SHA-256;
- concise failure reason when applicable.

## Permissions and concurrency

Use the minimum GitHub Actions permissions required.

Expected write authority is limited to release contents/assets (`contents: write` or the smallest equivalent supported permission).

Do not use personal access tokens when repository `GITHUB_TOKEN` is sufficient.

Serialize release publication for `main` with a repository/component-safe concurrency policy so two rapid merges cannot race to publish/inspect the same version.

A newer run must not silently cancel an in-progress publication. **`cancel-in-progress: false` alone is insufficient**: the default GitHub Actions concurrency queue replaces an earlier *pending* run. Use `queue: max` (or prove an equivalent guarantee), and report any overflow/cancellation requiring reconciliation. Avoid silent loss of intermediate source-controlled versions. Publication must remain serialized even if validation jobs run in parallel.

## Failure semantics

Release is fail-closed.

Examples that must fail the workflow:

- producer build/test/package validation fails;
- manifest revision does not match checked-out HEAD;
- release metadata is internally inconsistent;
- existing asset for the declared version differs from the candidate;
- upload outcome is ambiguous and cannot be verified;
- required release container cannot be safely resolved/created according to current policy.

On an ambiguous upload result, observe the remote release before retrying. Never retry blindly.

One producer's failure must not result in publishing an unvalidated artifact for that producer.

## GitHub release container handling

Use the existing component release containers/tags (`dotnet`, `workflow`).

If the expected container is missing, handle it deterministically: either create it through the accepted existing tag policy or fail with an actionable configuration error. Do not move/rewrite an existing component tag merely to point at the newest release commit; package revision authority lives in the distribution manifest/pins.

## Release metadata

Run the canonical producer pin preparation as part of release validation.

The workflow does not mutate any consumer repository.

Publish one consumer-neutral producer release metadata projection containing the information needed by arbitrary MCP clients to pin an immutable artifact (for example component id, version, asset URI, hashes, producer revision, and protocol/schema version where relevant).

OpenCode, Claude Code, Codex, CI systems, or other consumers own any translation from that producer metadata into their own descriptors/configuration.

Optionally expose the canonical release metadata as a GitHub Actions artifact/summary for downstream automation, but do not create consumer-specific formats inside `mcp-store`.

## Tests

Add deterministic tests/helpers for release-decision logic without requiring real publication for ordinary local test runs.

Cover at least:

1. new Dotnet version -> candidate validated -> publish decision;
2. new Workflow version -> candidate validated -> publish decision;
3. existing identical asset -> no-op;
4. existing different asset for same version -> fail/version bump required;
5. validation failure -> no upload;
6. ambiguous upload -> remote observation before retry;
7. two components with one changed and one unchanged;
8. shared infrastructure change that affects a package is not incorrectly skipped;
9. concurrent release runs cannot clobber each other;
10. Telegram files/workflows are not part of the root release transaction.

Where GitHub API interaction itself cannot be unit tested locally, keep the decision logic deterministic and test it separately from the thin Actions/GitHub transport shell.

## Acceptance criteria

- A successful merge/push to `main` automatically evaluates Dotnet and Workflow releases.
- New source-controlled producer versions are published without a manual GitHub release step.
- Existing byte-identical or strictly revision-only-equivalent versions produce a clean no-op, retaining the published revision and hashes.
- Existing same-version candidates with differences beyond the narrowly defined embedded-revision exception fail and require a version bump.
- No release asset is ever overwritten.
- Release artifacts are built from and identify the exact canonical `main` SHA.
- Producer validation runs before publication.
- Version/SDK/release metadata are not duplicated in workflow YAML.
- Release output exposes deterministic hashes/revision suitable for OpenCode pin updates.
- Concurrent merges cannot race into mutable/duplicate publication.
- Telegram source and Telegram-owned workflows remain untouched.

## Non-goals

- Telegram CI/release automation;
- automatic semantic-version selection/bumping;
- automatically editing OpenCode consumer descriptors;
- changing the existing `dotnet` / `workflow` release-container tag model;
- package registry publication;
- generic CI/CD framework construction.
