# Release requirements

Status: current normative contract. The implementation is
`.github/workflows/release.yml`, `.github/workflows/unix.yml`, and
`infrastructure/release/` (`ReleaseRunner`, `Check.ps1`, `Update.ps1`,
`Verify.ps1`, `Typecheck.ps1`). The shared manifest and pin contracts are in
[`infrastructure/distribution/`](../../infrastructure/distribution/README.md).
Where this summary is incomplete, the implementation and its guards are
authoritative; a requirement never authorizes bypassing a fail-closed check.

Related current contracts:
[distribution and consumer boundary](../Distribution.md),
[`infrastructure/release/README.md`](../../infrastructure/release/README.md),
[`infrastructure/resolver/README.md`](../../infrastructure/resolver/README.md), and
[`AGENTS.md`](../../AGENTS.md).

## 1. Trigger and scope

- One root release workflow owns publication. It runs on `push` to `main` and on
  no other event, branch, tag, or manual dispatch on the normal path.
- The only path filter in both workflows is the single `telegram/**` exclusion.
  Telegram source is outside the release transaction and is a verified non-input
  to the .NET release; the exclusion is not a Telegram CI. No `paths`,
  `branches-ignore`, or tag filter may skip a .NET-affecting revision.
- An empty or Telegram-only push does not trigger the release; the head tree,
  not the diff, is what a triggered run evaluates.
- A merged pull request and a direct push both fire the same workflow. The
  checked-out event SHA is the authority.
- A product version authored in a component's policy is published by the next
  qualifying push without a manual release step.
- Recovery of a failed release is a re-run of the failed workflow run or a
  follow-up push touching a non-Telegram path. `workflow_dispatch` remains
  forbidden, because it could bypass the push gate.
- The separate Unix workflow is pull-request-only, read-only, holds no write
  token, and never publishes or transfers an artifact. Its pre-merge trigger
  check is advisory; the release workflow's `Check.ps1` is the enforcing trigger
  guard for both workflows.

## 2. Version and toolchain ownership

- The SDK version is authored once in the root `global.json`
  (`rollForward: disable`, `allowPrerelease: false`). A pre-SDK projection reads
  only what installation needs.
- The target framework and F# language version are owned by the root
  `Directory.Build.props`; no project may re-declare them. No competing
  `Directory.Build.targets`, nested `Directory.Build.*`, `Directory.Packages.props`,
  or `NuGet.config` may exist.
- The runtime is never authored. After SDK setup, native MSBuild evaluation of
  the release project derives it from the single
  `KnownFrameworkReference` for `Microsoft.NETCore.App` at the evaluated target
  framework. The runtime is not computed from the target-framework string.
  One credential-free F# evaluator (`infrastructure/release/Toolchain.fs`)
  owns that query and its decoding; the PowerShell verification step and the
  F# preparation and packaging paths all use it, and PowerShell carries no
  second copy.
- Each component's `*/release/Config.json` owns only its per-component policy
  (`componentId`, version, entry DLL, platform RID, archive name, asset URI,
  published files, launch kind, and ordered launch arguments). A config that
  declares `sdkVersion` or `runtimeVersion` is rejected.
- `Update.ps1 -Verify` runs after SDK installation and invokes
  `Toolchain.fsx`, the thin entry to the evaluator, before any credential step.
  The evaluator reads only the `sdk.version`, `sdk.rollForward` and
  `sdk.allowPrerelease` fields of `global.json` (exact stable version,
  `disable`, `false`), rejects a repeated authoritative key and allows unrelated
  valid fields. Verification fails closed unless the
  selected SDK equals the owner, the evaluated target framework and language
  version are plain tokens, the SDK-bundled framework matches, exactly one
  matching framework reference exists, the installed runtime is compatible
  (same major.minor, patch at least the derived value; a prerelease never
  satisfies a stable value), and the executing platform RID equals the
  configured `platform`.
- Only after verification may the workflow publish
  `MCP_RELEASE_VERIFIED_SDK`, `MCP_RELEASE_VERIFIED_RUNTIME`,
  `MCP_RELEASE_VERIFIED_TARGET_FRAMEWORK`, and
  `MCP_RELEASE_VERIFIED_LANGUAGE_VERSION`. `-Project` never writes them.
- Publication consumes the verified values, fails closed when they are missing
  or invalid, independently reads the on-disk owner and requires it to equal the
  verified SDK, and performs no SDK discovery or evaluation while the credential
  is present.
  The isolated publisher stage imports no repository build files and receives
  only the verified target framework and language version.
- Workflow YAML must not duplicate producer versions, the SDK version, or the
  runtime version. CI never increments, invents, or infers a product version.

## 3. Participants, policy, and candidate input

- `infrastructure/release/Participants.json` is a closed enrollment roster of
  `dotnet`, `workflow`, and `resolver`, in that order. It carries component IDs
  and canonical config paths only; policy values such as `launchKind` come from
  each component's config.
- The transaction is harness-independent: it consumes only producer source,
  component policy, and generated metadata, never a harness checkout or consumer
  runtime.
- Component roots derive from each participant's registered `configPath`.
  A nested location such as `infrastructure/resolver/` needs no
  `<componentId>/release` layout.
- The candidate is a data-only directory containing exactly the producer archive
  and standalone `distribution.json`. It carries no sidecar, receipt, or
  executable instruction, and candidate members are never executed.
- The canonical manifest and consumer pin are produced by the component's
  release scripts from that policy and the packaged bytes. The manifest records
  the clean source revision, the exact member inventory and per-member SHA-256,
  the runtime derived from the packaged runtime configuration, and the launch
  descriptor. The pin records the exact selected identity: component, version,
  archive and manifest digests, and `publishedRevision`. The archive name
  (`v<version>.zip`) and canonical HTTPS asset URI derive from the validated
  component and version and the single trusted Store repository; the policy
  declares neither, and producer build provenance stays in the manifest only.
- The manifest schema stays at 2 and the pin schema is 2. The pin reader accepts
  only schema 2; a schema 1 document fails closed, including at an existing
  version slot. `dotnet-cli` is an
  additive launch kind inside schema 2; `dotnet-exec` remains the MCP launch
  kind. An unknown kind, unknown field, duplicate key, missing member, or
  malformed value fails closed. No compatibility reader or migration path
  exists.
- The Resolver refuses a `dotnet-cli` manifest as an MCP launch target in both
  `run` and `resolve`, before staging or installing, and releases the lease on
  the refusal branch.

## 4. Per-component publication transaction

Each participant advances through the same ordered transaction, and a failure
stops that participant before publication:

1. Check out and verify the exact event revision.
2. Select and verify the SDK, target framework, language version, platform, and
   runtime.
3. Build and package the producer; prepare the canonical manifest and pin.
4. Run the component's `tests/distribution/Run.fsx` package gate against the
   build and pin just produced.
5. Verify candidate archive membership, inner archive structure, manifest
   identity, and event revision.
6. Observe the remote container and asset, compare declared and published
   identity, and decide publish, no-op, or fail.
7. Publish only data, through the existing transaction, with the existing
   no-clobber policy.

Additional requirements:

- The canonical producer pin is prepared as part of release validation.
- Producer validation completes before publication. An invalid or unproven
  candidate is never published.
- `dist/**` output does not weaken clean-source provenance. Packaging refuses a
  dirty source tree and stamps the clean `HEAD` revision it built from.
- The record for each component reports its published, unchanged, or failed
  status, version, revision, asset URI/name, archive and manifest SHA-256, and
  failure reason. Release output exposes deterministic hashes and the revision
  suitable for consumer pin updates. A published metadata projection is
  consumer-neutral and deterministic; optional workflow summary/artifact output
  must not add consumer-specific formats.
- No asset is overwritten, replaced, deleted and re-uploaded, or made mutable
  through a tag or asset rename.

## 5. Comparison, no-op, and failure

- The candidate is compared against the declared version and the published
  asset. The decision logic is deterministic, strict, and separate from the thin
  network shell, so it can be exercised without a live GitHub publication.
- An absent asset that passes validation is uploaded. The result reports the
  archive and manifest SHA-256, revision, version, and URI.
- A byte-identical asset is a successful no-op.
- A revision-only difference is a no-op only after a full remote digest,
  structure, payload, and manifest match. Only the exact embedded revision
  exception is allowed; the published revision and hashes are retained, never
  replaced by candidate values.
- Any other difference, including a changed SDK, runtime, payload hash,
  compression method, manifest field, ZIP attribute, or entry comment, fails
  closed with a version-bump diagnostic and no upload.
- A malformed, mismatched, or inconsistent published digest or manifest fails
  closed. An inconclusive comparison fails closed.
- A no-op result reports the published hashes and revision separately from the
  distinct evaluated HEAD.

## 6. Recovery, stale runs, and serialization

- A stale re-run fails closed. The workflow checks out `github.sha` rather than a
  moving branch tip, verifies `HEAD` equals the event SHA, and proves in the
  credential-bearing step that the event SHA is still the `main` tip or a safe
  ancestor with the event SHA as merge base. Tags are never moved.
- An ambiguous upload result requires observing the remote before any retry.
  There is no blind retry.
- One participant's failure is reported without publishing an unvalidated
  artifact for that participant. Other participants retain their own validated
  outcome.
- Publication is serialized. The workflow uses `queue: max` and
  `cancel-in-progress: false`; the publication job has its own serialized group
  with the same settings, so a newer run does not silently replace or cancel a
  pending publication.
- Partial publication is possible because components publish independently. A
  retry must inspect the actual published assets, reuse the verified no-op and
  recovery checks, and never overwrite published bytes. See
  [partial publication and recovery](../Distribution.md#partial-publication-and-recovery).
- Missing container: create it through the accepted tag policy or fail with an
  actionable error. The `resolver` container must already exist as a published,
  non-draft, non-immutable release before its first publication. An immutable
  container setting is an incompatible, fail-closed condition.
- The release workflow does not mutate any consumer repository.
- Component releases use the fixed `dotnet`, `workflow`, and `resolver` tags,
  and assets keep the `vX.Y.Z.zip` name form. An existing tag is never moved,
  rewritten, or used as a mutable asset pointer; the tag model is not
  redesigned.

## 7. Security and release-boundary safeguards

- Minimum permissions: workflow default `contents: read`. Only the publication
  job holds `contents: write`; it alone also holds `actions: read`.
- The only credential is the platform `GITHUB_TOKEN`. No repository secret or
  PAT is read. The token is scoped to the one publication step and is absent
  from the publisher and preparation jobs and from the toolchain-verification
  step.
- Action references are reviewed 40-hex commit pins. No floating tag or short
  reference is allowed.
- Transfers use exact, validated numeric artifact IDs and isolated declared
  roots. No name, pattern, latest-run, repository, token, or merge lookup
  exists. Blank or unusable IDs are rejected before the first transfer.
- The publication job verifies the downloaded runtime against the publisher
  job's independent file/SHA-256 inventory and each candidate against the exact
  declared members before executing anything from either transfer. It does not
  override the transfer's documented digest or extraction behavior.
- The publisher job builds from the release project's ordered `<Compile
  Include>` list, contains every staged item inside the checkout, and publishes
  with repository build imports suppressed. The publication job's text carries no
  direct build, restore, F# script, project-run, or test invocation. Its
  pre-credential `Update.ps1 -Verify` step runs nested FSI and MSBuild evaluation;
  the credential-bearing step runs none.
- The control plane is rechecked in the credential-bearing step immediately
  before the native publisher call. No hand-written artifact client or direct
  API upload path bypasses the verified publisher.
- The Resolver trust boundary is unchanged: a Resolver is verified from its pin
  and closure, never by running an unverified Resolver.

## 8. Verification gates and support boundaries

Current automated verification, deterministic and non-behavioral:

- Builds: `dotnet/Dotnet.fsproj`, `workflow/Workflow.fsproj`,
  `infrastructure/resolver/Resolver.fsproj`, and
  `infrastructure/release/Release.fsproj`, all in Release.
- Package gates: `dotnet/tests/distribution/Run.fsx`,
  `workflow/tests/distribution/Run.fsx`, and
  `infrastructure/resolver/tests/distribution/Run.fsx`. They read the freshly
  built and pinned `dist` and verify the archive, manifest, pin, and byte
  contracts; they neither build nor regenerate anything and are not behavioral
  suites. Preparation runs each one after that component's build and pin.
- Resolver packaged smoke: `infrastructure/resolver/tests/distribution/Smoke.fsx`
  extracts the Resolver archive from the final regenerated `dist`, launches the
  packaged DLL with no arguments (exit 2, empty stdout, the documented usage
  line), then calls the compiled `ResolverPayload.verify` with the generated pin.
  The real archive must reach the authenticated CLI-only `launch.kind` refusal and
  an in-memory changed copy must fail at the archive digest. It runs in the
  preparation job after every candidate is prepared and before any candidate
  upload (the job holds no credential), and on each hosted Unix leg after packing
  and the Resolver build. It covers packaged launch and those two refusals only,
  not install, lease, process lifetime or consumer acceptance.
- Guards: `infrastructure/release/Check.ps1` (roster, trigger shape, job
  structure, action pins, artifact bindings, transfer roots, CLI paths, credential
  separation, cross-stage ordering) and
  `infrastructure/release/Typecheck.ps1` (strict FCS typecheck, as text, of each
  enrolled component's `Build.fsx`, `Pin.fsx` and `tests/distribution/Run.fsx`,
  plus `Toolchain.fsx` and the Resolver `Smoke.fsx`).
- `Verify.ps1` proves downloaded membership, paths, and SHA-256 before
  execution.

None of these gates performs a live publication or requires a hosted
credential.

The repository has no automated behavioral test suite. Behavioral confirmation
of an extracted MCP is a manual smoke through the Resolver contract. Adding
automated tests, test projects, or test-only CI phases requires explicit,
specific approval for each addition.

Support boundaries:

- Hosted GitHub Actions behavior is not independently observed: upload
  semantics, immutable-release detection, and `queue: max` FIFO ordering are
  configured and guarded but not claimed as hosted verification.
- GitHub's changed-file comparison has documented limits: an empty diff does not
  run the workflow, and a very large diff can fall outside the evaluated range.
  The Telegram exclusion is therefore not an absolute claim.
- Resolver 1.0.0 was published with Dotnet 1.0.7 and Workflow 3.0.1. Those
  baseline assets were downloaded and independently verified.
- The revised schema v2 releases are not yet published. Their published
  downloads have not been verified, and local qualification hashes are not
  publication identities.
- Windows x64 is the release platform selection. Unix qualification is a
  read-only pull-request check and is not Unix release publication.

## 9. Non-goals

- No Telegram CI, automatic version bump, consumer-repository edit, tag-model
  change, package registry, or generic language/framework adapter.
- No parallel producer delivery mechanism, second package resolver, generic
  command engine, or recursive Resolver self-installation.
- No change to public wire names, persisted schemas, or existing consumer
  contracts except through a separately approved contract migration.
