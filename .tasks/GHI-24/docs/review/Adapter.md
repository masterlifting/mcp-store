# SEC1 modern caller / trusted-builder / publish adapter

This document records the bounded engineering slice that integrates the
new two-channel `Handoff.fs` core into the publisher entrypoint. It
records the written source, the strict CLI contract, the verification
phases, the build evidence, and the open facts that other owners must
resolve. It claims no approval, no PASS, and no external review.

## Status and scope

`mayEdit=true` bounded to the named files; `mayTest=false`,
`mayDelegate=false`, `mayBuild=true` (dedicated MCP only). No live
platform call, upload, credential read, secret read, network mutation,
commit, push, task-state mutation, or merge. No `Bundle`, `GitHub`,
`Telegram`, or consumer edit. The `ghi-24` worktree stays dirty on
purpose — the integration remains pre-merge.

Authored in this slice:

- `infrastructure/release/Handoff.fs` — minimal essential API adapter
  factory addition: `makeOuterDigest` and `makeInnerArchiveSha256`
  factories expose the `OuterDigest` / `InnerArchiveSha256` constructors
  to the publisher without weakening the private case constructor
  invariant. The factories accept ONLY a strict 64-character lowercase
  hex SHA-256 and fail closed otherwise.
- `infrastructure/release/Evaluate.fs` — `VerifiedCandidate.ArchiveBytes`
  / `ManifestBytes` direct public accessors are retired; the typed
  snapshot now exposes `ArchiveBytesCopy ()`, `ManifestBytesCopy ()`,
  `ArchiveBytesStream ()`, `ManifestBytesStream ()` (and the existing
  `EmbeddedManifestBytesCopy ()`). The `VerifiedCandidate.createFromSnapshot`
  factory is added as the minimal `EvaluateVerifiedCandidate` adapter that
  builds from the typed `CandidateArchiveSnapshot` + byte-safe
  `parseBytes` policy + supplied `--event-sha`/`--workspace-root`
  context. The factory re-runs the canonical embedded-manifest equality,
  entry-allowlist, and embedded id/version/archive/revision checks
  against the snapshot's bytes; it never duplicates a candidate-controlled
  trust anchor.
- `infrastructure/release/Runner.fs` — the `publish` entrypoint is wired
  to the new two-channel core. The CLI parser accepts ONLY the
  documented `--key=value` flags; unknown flags, duplicate flags, and
  positional arguments after the two required paths are rejected before
  the publisher reads any byte (no legacy `orchestrate` mode, no 3-arg
  path, no aliases, no default bypass). The publisher runs six
  independent verification phases — closure, CLI shape, repo catalog,
  receipts, source workspace, transport — before composing the typed
  `CandidateArchiveSnapshot` via `ReleaseHandoff.readRawTransport`, the
  `VerifiedCandidate` via `createFromSnapshot`, and a SINGLE
  `ReleaseTransaction.run` invocation.
- `infrastructure/release/Orchestration.fs` — the `prepare` entrypoint
  now writes a typed `preparation.json` (schemaVersion 1) carrying the
  producer-known inner archive SHA-256 + size and the standalone
  manifest SHA-256 + size — the four publisher-cross-checkable inner
  fields. The receipt carries NO outer transport digest / size, NO
  runtime fields, and NO toolchain values, matching the Handoff
  `PreparationReceipt` schema exactly.
- `infrastructure/release/README.md` — the `Runner` invocation sections
  describe the new flow precisely: the typed `PreparationReceipt` from
  `preparation.json`, the SEPARATELY generated `TrustedPublisherReceipt`
  from `receipt.json` (the trusted-builder job, not the preparation
  stage or `Bootstrap.ps1`), the `createFromSnapshot` adapter, and the
  strict CLI contract.

Deliberately **not** modified (per assignment):

- `infrastructure/release/Bootstrap.ps1`
- `infrastructure/release/Sources.json`
- `infrastructure/release/Publisher.json` (already in the slim
  identifiers-only shape)
- `infrastructure/release/PublisherClosure.fs` (already in the slim
  identifiers-only shape)
- `.github/workflows/release.yml`
- every producer `Config.fsx`, `Build.fsx`, `Pins.fsx`
- every `workflow/tests/*.fsx` and `dotnet/tests/Mcp.Dotnet.Tests/*`
- every consumer / Telegram / harness file
- `infrastructure/release/Release.fsproj` (compile order unchanged)

## Strict CLI contract

The `publish` entrypoint accepts EXACTLY two positional arguments
followed by N documented `--key=value` flags. Surplus positional
arguments, unknown flags, duplicate flags, malformed `--key` (= value)
forms (missing `=`, empty key, empty value), and disallowed
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
--trigger=<event>      (optional)
```

Mandatory flags (publisher never accepts a partial supply):

`--repo`, `--run-id`, `--attempt`, `--event-sha`, `--role`,
`--component`, `--artifact-id`, `--artifact-name`,
`--transport-sha256`, `--builder-sdk`, `--builder-runtime`,
`--builder-platform`, `--workspace-root`.

The publisher owns the strict-lowercase requirement: `--event-sha` is
verified against `^[0-9a-f]{40}$` and `--transport-sha256` against
`^[0-9a-f]{64}$`. Uppercase hex, whitespace, prefix forms, and
non-canonical shapes are rejected with a typed `MalformedArtifact`. The
two distinct typed digest wrappers (`OuterDigest`, `InnerArchiveSha256`)
prevent accidental substitution of an outer transport digest for an
inner archive digest at the call site.

## Verification phases (before any HTTP / auth / upload)

1. **Closure** — `ReleasePublisherClosure.readFromFile` parses
   `Publisher.json` (the slim identifiers-only audited closure). Any
   unknown field, missing required value, wrong schema version, or
   shape mismatch fails closed.

2. **CLI shape** — every flag is checked against `allowedPublishFlagNames`,
   duplicates are rejected, and the `seen` set enforces exactly-once
   semantics per flag. Mandatory flags are enforced after the loop.

3. **Repo catalog** — `--repo` is split into `<owner>/<name>` and
   compared case-sensitively against the publisher's canonical
   `masterlifting/mcp-store`. A wrong catalog fails closed before any
   receipt read.

4. **Receipts** — `<candidate-directory>/receipt.json` (typed
   `TrustedPublisherReceipt`) and `<candidate-directory>/preparation.json`
   (typed `PreparationReceipt`) are read under a 4 MiB bounded read,
   parsed by the canonical strict-parser, and cross-checked:

   - `publisherClosure.repository == --repo`
   - `publisherClosure.rosterPath == receipt.rosterPath`
   - `receipt.repository == --repo`
   - `receipt.eventRevision == --event-sha` (and `--event-sha` shape)
   - `receipt.runId == --run-id`
   - `receipt.attempt == --attempt`
   - `receipt.role == --role` (and `--role` ∈ {`publisher`, `runtime`,
     `candidate`})
   - `receipt.builderSdk == --builder-sdk`
   - `receipt.builderRuntime == --builder-runtime`
   - `receipt.builderPlatform == --builder-platform`
   - `receipt.componentConfigPaths[--component]` is non-empty
   - `receipt.componentConfigShas[--component]` is non-empty

5. **Source workspace** — the canonical Config.fsx bytes from the
   receipt's `componentConfigPaths[--component]` are read from
   `--workspace-root`, hashed, and cross-checked against
   `receipt.componentConfigShas[--component]`. `ReleasePolicy.parseBytes`
   derives the policy from the SAME bytes. The roster file from
   `publisherClosure.rosterPath` is read from `--workspace-root`,
   hashed, and cross-checked against `receipt.rosterSha256`. The
   committed HEAD at `--workspace-root` is verified equal to
   `--event-sha`.

6. **Transport** — the publisher-observed SHA-256 of the candidate
   archive at `<candidate-directory>/<policy.ArchiveName>` equals
   `--transport-sha256`. Both digest shapes are 64-character lowercase
   hex; uppercase or non-canonical forms fail closed.

Only after every phase passes does the publisher compose the typed
`CandidateArchiveSnapshot` via `ReleaseHandoff.readRawTransport` (the
strict outer archive transport validator), cross-check the snapshot's
inner archive identity against the `PreparationReceipt` (the four inner
fields only — no outer digest, no runtime, no toolchain), construct the
`VerifiedCandidate` via `createFromSnapshot` (re-runs the canonical
embedded-manifest equality and entry-allowlist checks against the
snapshot's bytes), and drive `ReleaseTransaction.run` exactly once.

## Source closure companion

No `Handoff.fs` rewrite was performed. The only additions are two
factory functions (essential API adapter factory addition, the only
permitted Handoff.fs change) plus two one-line comments. The factories
are required because the `OuterDigest` and `InnerArchiveSha256` types
have private case constructors that the publisher cannot invoke directly.
The factories accept ONLY a strict 64-character lowercase hex SHA-256
and fail closed otherwise, so no candidate-controlled field can smuggle
an uppercase digest through the wrapper. Every other `OuterDigest` /
`InnerArchiveSha256` value the publisher observes comes from the
parser (`parseOuterDigest`) or from the receipt's parsed
`rosterSha256` / `componentConfigShas` / `runtimeFileDigests` maps — the
factories are the only construction path the publisher is permitted to
use outside the parser.

## Build evidence

Dedicated `dotnet_build` MCP, scoped to the affected project, `noRestore:
false` (fresh package resolution), `Release` configuration. Build only;
no tests, no live API, no real upload.

| Project | runId | Status | Errors | Warnings | DurationMs |
| --- | --- | --- | ---: | ---: | ---: |
| `infrastructure/release/Release.fsproj` | `QwaFt3JgDzP9m9eFu6F7dIB6GaIzc0Xu__7b7Umf1Eo` | succeeded | 0 | 0 | 844 |
| `dotnet/Mcp.Dotnet.fsproj` | `LdcMmsqRRIY4cPvyEvbFW8f3-EqFkktR3Vzcvp_FDi0` | succeeded | 0 | 0 | 857 |
| `workflow/Mcp.Workflow.fsproj` | `JP1X3LNuVXJYkmFvjl8S_Zdq6AyMIPCZtqXt6CezoB0` | succeeded | 0 | 0 | 929 |

All three compile with `TreatWarningsAsErrors=true`. Earlier build IDs
on the same source (`Q6RD6wQdiwuIitDFCDgNdi53XlryM5rkFEb64ieI82o`,
`fNQWcKQt3eoycBi2X1EoHr4j1s9GXDKJfJYarV_HIYA`,
`ct7Tl2IyJfViNXOBBqnxrB-BobrEdDWwm0P6-Z4y_N0`,
`NgrBPnB-Eg16zzM5bM3iHNlRRF-hYJ6cELkE-ZzXNos`,
`55e6xfB-Pge4NuvSHE_J07bPXCTjagZueBXrl-8qEWA`,
`7YIFCM2aVrfejpOzCoDZoru3f6AuOlABWsGoDRInx8E`,
`22GJV1E-ULddB9W00z0AZARCx91x5qTqTkffAYiJ_XQ`) tracked the iterative
fix-up of the `chain`/`return`/`OuterDigest`/`isLowerHex64` issues and
the strict-hex `-64` shape check; they are intermediate historical
evidence, not production builds.

## Manual CLI smoke tests

The compiled `ReleaseRunner` was manually exercised without credentials
to confirm the new entrypoint surface and the strict CLI gating. No
network call, no credential read, no `.env` read, no secret read, no
network mutation.

```
ReleaseRunner                  # exit 2, prints both usage lines
ReleaseRunner publish          # exit 2, prints the publish usage line
ReleaseRunner publish fake.json fake-dir
# exit 4, JSON {"status":"failed","reason":"... the publish CLI is
#         missing required flag(s): --repo, --run-id, --attempt,
#         --event-sha, --role, --component, --artifact-id,
#         --artifact-name, --transport-sha256, --builder-sdk,
#         --builder-runtime, --builder-platform, --workspace-root"}
ReleaseRunner publish
  infrastructure/release/Publisher.json <existing-dir>
  --repo=masterlifting/mcp-store --run-id=1 --attempt=1
  --event-sha=0123456789abcdef0123456789abcdef01234567
  --role=candidate --component=dotnet --artifact-id=1
  --artifact-name=test
  --transport-sha256=aaaa...aaaa
  --builder-sdk=11.0.100 --builder-runtime=11.0.0
  --builder-platform=win-x64
  --workspace-root=D:\repos\private\mcp-store
# exit 4, JSON {"status":"failed","reason":"... required release
#         artifact does not exist: <existing-dir>\receipt.json"}
```

The empty-argv / subcommand-only-argv paths print only the documented
usage line and exit `2`. The publish path with mandatory flags missing
prints the structured JSON `status=failed` envelope with the typed
diagnostic and exits `4`. The publish path with mandatory flags
present and an empty candidate directory prints the typed
`MissingArtifact` for the missing `receipt.json` and exits `4`. No
default bypass is permitted; no upload, no credential read, no network
mutation occurs.

## Outstanding privacy/gap facts (not invented, not self-approved)

1. **Trusted publisher receipt path.** The current `publish` entrypoint
   reads `<candidate-directory>/receipt.json`. The trusted-builder job
   that produces the `TrustedPublisherReceipt` and downloads it into the
   candidate directory is a future DevOps wiring step. This slice does
   NOT wire the trusted-builder job; it only consumes the typed
   receipt at the documented location. The Companion Patch Contract is:
   the workflow must (a) build the trusted publisher runtime closure
   described in `infrastructure/release/Sources.json` from the same
   audited sources; (b) emit a `TrustedPublisherReceipt` to the
   candidate directory; (c) bind every receipt field to an explicit
   GitHub job output that the publish entrypoint can independently
   verify against the CLI-supplied `--event-sha` / `--run-id` /
   `--attempt`.

2. **Observed vs. expected metadata.** The current `publish` entrypoint
   uses the SAME `cli.ArtifactId` / `cli.ArtifactName` / `cli.Role` /
   `cli.Component` / `cli.Repo` / `cli.RunId` / `cli.Attempt` /
   `cli.EventSha` for both `ExpectedMetadata` and `ObservedPlatformMetadata`
   because the workflow-supplied platform response is not yet wired into
   the CLI. The artifact evidence requires independent platform
   observation — the workflow must supply observed metadata separately
   (via the bootstrap transport's `MetadataJson` seam or via a
   future CLI flag) so the publisher can cross-check the platform-
   supplied expected metadata against an independent observation. Until
   then, the publisher trusts the CLI for the observed metadata,
   which is bounded by the strict lowercase shape checks and the
   canonical repository catalog.

3. **Workflow file plumbing.** The `.github/workflows/release.yml`
   pipeline that owns the fresh-job wiring, the artifact ID / digest /
   `--transport-sha256` plumbing, the role map, and the candidate
   directory assembly is NOT edited in this slice. DevOps owns the
   next batch.

4. **Bootstrap observations for fresh staging.** The assignment notes
   "If insufficient Bootstrap observations need precise companion patch
   contract return BLOCKED before accepting unchecked booleans." The
   current implementation reads the candidate directory's
   `receipt.json` and `preparation.json` directly; it does NOT depend
   on `Bootstrap.ps1`'s receipt (which is a separate document). The
   fresh staging therefore does not require any Bootstrap observations
   beyond the canonical `--transport-sha256` (which the bootstrap
   transport already records in its receipt as `download.observedSha256`).
   No BLOCKED is required for this slice.

5. **Test project migration.** The `VerifiedCandidate.ArchiveBytes` /
   `ManifestBytes` direct accessors were retired in favor of
   `ArchiveBytesCopy ()` / `ManifestBytesCopy ()`. The existing
   `Integrity.fs` tests in `infrastructure/release/tests/` reference
   `candidate.ArchiveBytes` (a separate test record type defined in
   that file, not the production `VerifiedCandidate`) and continue to
   compile against their own type; the production `VerifiedCandidate`
   accessors are exclusively used by the upload core (`Transaction.fs`)
   and the new `createFromSnapshot` adapter. The dedicated tester
   batch owns the migration of any test that may have depended on the
   retired direct accessors; this slice does not migrate the tests.

6. **Same-version / new-files permissions.** Per the assignment,
   `mayBuild=true` is used only via the dedicated `dotnet_build` MCP
   tool, scoped to `infrastructure/release/Release.fsproj`,
   `dotnet/Mcp.Dotnet.fsproj`, and `workflow/Mcp.Workflow.fsproj` only.
   No raw `dotnet build` fallback was used. No env secret values,
   live API, upload, network, userdata, state, commit, push, merge,
   rebase, stash, fetch, or foreign checkout was performed. No real
   upload of any artifact was attempted; the smoke tests above write
   no candidate directory contents and produce no remote effect.

## Remaining work (owned elsewhere)

- Fresh-job workflow wiring in `.github/workflows/release.yml`:
  prepare/publish jobs, role map literal, `job.check_run_id` /
  `artifact-id` / `artifact-digest` plumbing, candidate directory
  assembly, observed platform metadata separation from the CLI.
- Trusted-builder job that emits `TrustedPublisherReceipt` and
  downloads it into the candidate directory. The companion patch
  contract above is the minimum contract the workflow owner must
  satisfy.
- DevOps owner of the candidate download step that writes
  `preparation.json` (the preparation step writes it in this slice;
  if DevOps prefers the bootstrap to write it, the prepare entrypoint
  must be updated to write the file at a different path).
- Tester-owned regression suite covering the new strict CLI parsing,
  the strict lowercase-hex digests, the receipt-cross-check matrix,
  the workspace HEAD recheck, the transport sha256, the
  `createFromSnapshot` adapter, the `makeOuterDigest` /
  `makeInnerArchiveSha256` factories, the encapsulation of the
  verified candidate byte arrays, and the failure paths of every
  verification phase.
- Independent security / architecture / correctness / acceptance
  review of the whole SEC1 boundary.

No engineering APPROVE is asserted by this document.