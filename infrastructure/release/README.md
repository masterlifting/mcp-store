# `infrastructure/release`

Shared compiled release runner, the pre-SDK toolchain bootstrap, and the
non-publishing regression and typecheck gates invoked by the root `release.yml`.

## Enrollment roster

`Participants.json` is the single authoritative enrollment roster and carries
only `componentId` and `configPath` per participant. Versions, SDK/runtime/
platform, allowlists and URIs stay in each producer's canonical
`*/release/Config.fsx`, so no stage restates them; `ReleaseEnrollment`
(`Enrollment.fs`) rejects any extra field at load time.

The roster drives every participant list: `Update.ps1` iterates it for
projection and verification, `release.yml` builds its release matrix from the
projected component list, `Bootstrap.fsx` iterates it for the toolchain guard,
and `ReleaseOrchestration.prepare` plus `ReleaseRunner publish` reject an
unsupported `componentId`, a `configID` mismatch, or a `configPath` drift
before any child script or contract work runs. The PS gates share
`Roster.ps1`, which mirrors the compiled acceptance rules (forbidden policy
fields, duplicate or empty entries, and the mandatory `dotnet`/`workflow` set
including omissions), so the pre-SDK and compiled decisions agree without copying
version, SDK, allowlist or URI values.

`Coherence.ps1` is the deterministic cross-stage guard: it fails closed when the
`components` bootstrap output, the `fromJSON` release matrix, the fixture-step
wiring, the roster-wide verification, the matrix-scoped publication verification,
the `prepare` / `publish` CLI path, the projected SDK version or the emitted
`components=` projection stop matching the roster, or when a participant list or
CLI path is hardcoded again. `release.yml` runs it in the `bootstrap` job
immediately after `Update.ps1 -Project`, so drift fails before the SDK install and
therefore before any publication.

## Bootstrap

`Update.ps1` runs the canonical `Config.fsx` values in two modes:

- `Update.ps1 -Project` reads the SDK/runtime/platform literals of every roster
  participant without evaluating F# or requiring an installed SDK and writes
  `sdk`/`runtime`/`platform` plus the roster component list as JSON
  (`components=["dotnet","workflow"]`) to `$GITHUB_OUTPUT`; run it before
  installing the SDK.
- `Update.ps1 -Verify [-Component <id>]` recomputes the projection, exports
  `RELEASE_EXPECTED_SDK`/`RELEASE_EXPECTED_RUNTIME`/`RELEASE_EXPECTED_PLATFORM`,
  runs `Bootstrap.fsx`, and forwards the verified summary to `$GITHUB_ENV`. A
  supplied `-Component` must be a roster participant and is rejected before the
  bootstrap runs otherwise; without it the guard verifies all roster
  participants. Run it after installing the SDK.

`Update.ps1` fails closed on a missing, empty, duplicate or non-JSON roster, and
on a `Config.fsx` whose `componentId` contradicts the roster entry for the same
path; it is the pre-SDK half of the roster consistency check, and the compiled
`ReleaseEnrollment` guards re-validate the same invariant after the SDK exists.

`Bootstrap.fsx` emits `RELEASE_SDK`, `RELEASE_RUNTIME`, `RELEASE_PLATFORM`,
`RELEASE_ARCHITECTURE`, and per-component
`RELEASE_<COMPONENT>_{VERSION,ARCHIVE,ASSET_URI,ENTRY}` to `$GITHUB_ENV`; the
format is additive so older workflows keep parsing.

The "one SDK/runtime/platform across all participants" equality check belongs to
this bootstrap guard only, because both current .NET producers share exactly one
toolchain. Generic archive identity, metadata decisions and HTTP transport read
per-producer expectations from `Config.fsx` and never require a global toolchain
identity; per-producer toolchains, platforms and languages are deferred to #37.

## Authorization and credential boundary

The CLI entry path is authorized against the enrollment roster **before any
child `dotnet fsi` process runs**. `ReleaseRunner prepare <configPath>`
loads `Participants.json`, validates the roster, and resolves `<configPath>`
to an absolute path; if no roster entry matches exactly, the orchestrator
emits a structured `phase=config` failure with exit `4` and never spawns
a child process. The pure `ReleasePolicy.parse` parser is the only reader
of `Config.fsx` in the publication path: it never executes the file,
never reads environment variables, and never touches producer code, so an
unregistered or path-drifted file cannot run top-level F# under the
runner’s credentials.

The preparation path sequences `Build.fsx`, `Pins.fsx`, and the producer
test entrypoints as three subprocesses. Every pre-publication subprocess
inherits the parent process environment **minus** the publication-authority
variables `GITHUB_TOKEN`, `MCP_RELEASE_DO_UPLOAD`,
`MCP_RELEASE_GITHUB_OWNER`, and `MCP_RELEASE_GITHUB_REPOSITORY`.
`ReleaseCapture.runProcessCapture` removes those named variables from each
child's `ProcessStartInfo` before `Start()`, so a build, pins, or test
process is structurally unable to inherit the publication authority.
`prepare` refuses to start if any of those four variables is already set, so
the preparation job cannot accidentally inherit write authority.

The legacy credential-bearing transaction subprocess path
(`ReleaseRunner.exe <manifest> <archive> <contract-json>`) has been
retired; the only publication entrypoint is `ReleaseRunner publish
<publisher-closure-path> <candidate-transport.zip> --repo=… --run-id=…
--attempt=… --event-sha=… --role=… --component=… --artifact-id=…
--artifact-name=… --transport-sha256=… --builder-sdk=… --builder-runtime=…
--builder-platform=… --workspace-root=… --trusted-receipt=…
--transport-evidence=… [--trigger=…]`. The publisher
reads the audited `infrastructure/release/Publisher.json` closure (which
carries only the canonical `repository`, `rosterPath`, and `schemaVersion` —
no zero hash placeholders, no policy duplicates), reads the independently supplied
trusted publisher receipt, re-derives every policy value
from the canonical `Config.fsx` bytes at the platform-supplied event SHA
through `ReleasePolicy.parseBytes`, cross-checks the receipt's
`componentConfigShas`, `RosterSha256`, `EventRevision`, `RunId`, `Attempt`,
`Role`, and `BuilderSdk/Runtime/Platform` against the publisher's
independent observations and the CLI-supplied `--workspace-root`,
hashes the bounded raw outer ZIP bytes and verifies them against the CLI-supplied
`--transport-sha256` and FetchCandidate observed download hash/size, validates the outer transport against the strict
`ReleaseArchive.parse` transport validator (which enforces exact names,
entry / expansion limits, no traversal / absolute / drive / UNC /
alternate-stream paths, no links, no duplicates, no case collisions) AND
through the bounded `ReleaseHandoff.Handoff.create` outer-handoff
validator, and drives `ReleaseTransaction.run` directly — the byte-safe
seam never opens a file between verification and upload.

The legacy `ReleaseOrchestration.orchestrate` and `ReleaseRunner orchestrate`
modes have been removed entirely (no compatibility alias, no
environment-mode switch). The tester batch migrates historical tests
that referenced the legacy surface to the new `prepare`/`publish` seam.

## Runner

`ReleaseRunner.exe` is built from `Release.fsproj` (Compile order:
`Provenance`, `Archive`, `Compare`, `Metadata`, `Publish`, `Evaluate`,
`Capture`, `Enrollment`, `Policy`, `PublisherClosure`, `Handoff`,
`Transaction`, `Orchestration`, `Runner`). It is the per-component
release entrypoint; no producer `Release.fsx` wrapper exists.

### Invocation

- `ReleaseRunner.exe prepare <config-path> <candidate-directory>` — the
  SEC1 preparation entrypoint. It authorizes the path against the roster,
  parses `Config.fsx` purely via `ReleasePolicy.parseBytes` (no FSI),
  validates the projection against the roster, then runs `Build.fsx`,
  `Pins.fsx`, and the component-specific tests as scrubbed child
  subprocesses (publication credentials scrubbed). On success it stops
  before any write attempt, snapshots the archive and standalone manifest
  plus their SHA-256 digests, verifies the standalone and embedded manifest
  bytes hash to the same canonical value, then copies the archive and
  manifest to `<candidate-directory>` under their **relative exact names**
  (`<archive-name>.zip` and `distribution.json`). The preparation step
  also writes a typed `preparation.json` carrying the producer-known
  inner archive and standalone manifest digests/sizes (the four
  publisher-cross-checkable inner fields only — no outer transport
  digest, no runtime fields, no toolchain values). The candidate
  directory carries no prep-workspace absolute paths. The independently
  generated trusted publisher receipt is not part of this preparation output;
  the publisher receives it separately via `--trusted-receipt`.

 - `ReleaseRunner.exe publish <publisher-closure-path> <candidate-transport.zip>
  --repo=<owner>/<name> --run-id=<id> --attempt=<n>
  --event-sha=<40-hex> --role=<candidate>
  --component=<dotnet|workflow> --artifact-id=<id> --artifact-name=<name>
   --transport-sha256=<actual-64-hex> --builder-sdk=<x>
   --builder-runtime=<y> --builder-platform=<z> --workspace-root=<path>
   --trusted-receipt=<builder-emitted-receipt.json>
   --transport-evidence=<fetchcandidate-bootstrap-receipt.json>
  [--trigger=<event>]` — the sole data-only publication entrypoint. The second
  positional argument is the actual raw GitHub artifact ZIP (outer transport
  bytes), never an extracted candidate directory or the inner producer archive.
  Its canonical parent is used only for receipt non-overlap guards. The transport
  filename is not policy: `policy.ArchiveName` selects the expected inner ZIP entry.
  The CLI parser accepts ONLY the documented `--key=value` flags; any
  unknown flag, duplicate flag, or positional argument after the two
  required paths is rejected before the publisher reads a single byte
  (no aliases, no legacy `orchestrate` mode, no 3-arg path). Two
  receipt paths are mandatory AND distinct (`--trusted-receipt`,
  `--transport-evidence`); a missing, empty
  or equal value is a typed `MalformedArtifact` before any
  file read. The `--role` value is restricted to `candidate`; a
  `publisher` or `runtime` value is a typed `MalformedArtifact` before
  any file read, token read, or HTTP transport. The `--transport-
  evidence` value MUST point at a `Bootstrap.ps1 -Mode FetchCandidate`
  receipt with `status="fetched"` and a non-null `component` equal to
  `--component`; a `Bootstrap.ps1 -Mode InstallRuntime` receipt
  (status `installed`, component `null`) is rejected before any
  further cross-check because it is protected BOOTSTRAP installation
  evidence, not candidate publish input. The `--trusted-receipt`
  value MUST point at the receipt emitted by the independently built
  trusted builder job; the path is canonicalised, must NOT be inside
  the candidate directory, must NOT equal `<candidate>/receipt.json`,
  and must NOT equal the transport-evidence path. The publisher reads the audited
  `Publisher.json` closure (identifiers only: `schemaVersion`,
  `repository`, `rosterPath`), reads the typed
  `TrustedPublisherReceipt` from the `--trusted-receipt` path, cross-checks the receipt's `Repository`,
  `RosterPath`, `RosterSha256`, `EventRevision`, `RunId`, `Attempt`,
  `Role`, `ComponentConfigPaths`/`Shas`, and
  `BuilderSdk/Runtime/Platform` against the publisher's independent
  observations and the CLI-supplied trusted context, verifies the
  publisher-observed roster SHA-256 equals the receipt's `RosterSha256`,
  re-derives every policy value from the canonical `Config.fsx` bytes
  at the CLI-supplied `--workspace-root` through
  `ReleasePolicy.parseBytes` (the receipt's `componentConfigShas`
  confirms the bytes the parser observes), verifies the checked-out
  HEAD equals the CLI-supplied `--event-sha`, verifies the publisher-
  observed candidate archive SHA-256 equals the CLI-supplied
  `--transport-sha256`, composes the typed `CandidateArchiveSnapshot`
  through `ReleaseHandoff.readRawTransport` (the strict outer archive
  transport validator: exactly the canonical producer archive,
  `distribution.json`, and `preparation.json`; entry / expansion limits, no
  traversal/absolute/drive/UNC/alternate-stream paths, no links, no
  duplicates, no case collisions; standalone/embedded manifest byte
  equality cross-check; separate typed `InnerArchiveSha256` vs.
  `OuterDigest` namespaces; preparation fields are checked against the
  verified member bytes before snapshot creation), (no candidate-controlled
  outer digest; the platform outer digest comes from
  `ArtifactEvidence.ActualOuterDigest`), composes the `VerifiedCandidate`
  snapshot through `VerifiedCandidate.createFromSnapshot` (the minimal
   `EvaluateVerifiedCandidate` encapsulation that builds from the typed
   snapshot; the verified byte arrays live behind private fields with
   defensive-copy accessors), then requires the exact
   `MCP_RELEASE_DO_UPLOAD=true` opt-in and a non-empty `GITHUB_TOKEN` before
   constructing HTTP transport or creating the download directory. Missing
   authorization fails closed with structured `status=failed` and exit `4`;
   there is no local-mirror, simulation, or implicit fallback. The release
   locator tag is the verified enrolled `componentId` (`dotnet` or `workflow`),
   not a version-derived tag; version remains part of archive and metadata
   identity. The runner then drives `ReleaseTransaction.run` directly with a
   SINGLE invocation. `__SOURCE_DIRECTORY__` and the
  build-machine HEAD are never used to derive the trusted root, the
  trusted policy, or the trusted archive digest.

Both modes resolve the repository root from the assembly location, so the
runner may be launched from any working directory. `Bootstrap.fsx` stays outside
the compiled assembly because its top-level `Async.RunSynchronously` would run
in the runner's module initializer.

### Publisher closure (`infrastructure/release/Publisher.json`)

The publisher reads `Publisher.json` from the workspace as the audited
closure that lists every trusted source. It carries only identification
values, never policy duplicates:

- `schemaVersion` (int; must equal 1)
- `repository` (string)
- `rosterPath` (string; relative path to `Participants.json`)

The publisher rejects unknown fields, missing required values, and the
wrong schema version. The Publisher closure carries ONLY identifiers:
no zero hash placeholders, no per-component Config.fsx SHAs, no SDK or
runtime values. Every trust anchor that the publisher needs (the roster
SHA-256, every per-component Config.fsx SHA-256, the observed toolchain,
the run-attempt metadata) comes from the SEPARATELY generated
`receipt.json` that the trusted builder emits and the publisher reads via the
explicit `--trusted-receipt` path (not from the candidate transport).
The publisher independently re-hashes the roster and every Config.fsx
and compares against the receipt; a candidate that manufactures its own
expectations is rejected.

### Trusted publisher receipt (`--trusted-receipt`)

The receipt is GENERATED by the trusted builder job (not by the authored
closure) and carries the full set of runtime evidence the publisher
needs to verify the candidate before any policy decision runs:

- `schemaVersion` (int; must equal 1)
- `repository`, `eventRevision` (40-hex lowercase), `runId`, `attempt`, `trigger`, `role`
- `rosterPath` (relative path), `rosterSha256` (64-hex lowercase)
- `componentConfigPaths` (map of `componentId → canonical Config.fsx path`)
- `componentConfigShas` (map of `componentId → trusted Config.fsx SHA-256`)
- `runtimeFilePaths` (optional; populated only when the receipt was
  emitted by the trusted-builder install-runtime path; a prepare-time
  receipt leaves these empty)
- `runtimeFileDigests` (optional; runtime file SHA-256s)
- `runtimeFileSizes` (optional; runtime file byte sizes)
- `builderSdk`, `builderRuntime`, `builderPlatform`

The publisher validates the receipt against the audited Publisher.json
closure (repository + rosterPath), the CLI-supplied trusted context
(repository, runId, attempt, eventRevision, role, builderSdk/Runtime/
Platform, transportSha256), and the publisher's independent SHA-256s of
the roster and every canonical Config.fsx. The two distinct authored
files (`Sources.json` for the trusted builder, `Publisher.json` for the
audited publish-time closure) and the GENERATED receipt together cover
the SEC1 boundary evidence.

### Raw candidate transport contract

The raw GitHub artifact ZIP passed as the publish CLI's second positional
argument carries exactly three entries (no prep-workspace absolute paths, no
self-asserted JSON envelopes, no other metadata the publisher would have to
reject):

- `<archive-name>.zip` — the candidate archive as written by the producer
- `distribution.json` — the standalone manifest as written by the producer
- `preparation.json` — the producer-known inner archive and standalone manifest hashes and sizes
The publisher reads the raw ZIP bytes directly through
`ReleaseHandoff.Handoff.readRawTransport`, which enforces the bounded outer-archive transport validator
(`ReleaseArchive.parse` — exact names, entry / expansion limits, no
traversal / absolute / drive / UNC / alternate-stream paths, no links, no
duplicates, no case collisions), parses the embedded `distribution.json`
from the archive, and confirms standalone / embedded manifest byte
equality before any policy decision runs. The preparation receipt is parsed
from its verified member bytes using bounded strict UTF-8 and cross-checked
against the actual archive and manifest bytes before the private snapshot is
returned; it is untrusted consistency data only.

### Strict transport archive validator

`ReleaseArchive.parse` is the strict validator the publisher runs BEFORE
any candidate extraction. It enforces:

- Exact entry names (ASCII printable, no special chars, no empty)
- Entry count ≤ 4096, total uncompressed ≤ 256 MiB, per-entry ≤ 256 MiB,
  compression ratio ≤ 200:1
- Canonical Deflate method only, version 20 only, no flags
- No duplicate entry names (case-sensitive set check)
- No multi-disk, no ZIP64, no data descriptors, no encryption, no comments,
  no extra fields, no attributes
- Implicit (via the flags check): no symbolic links, no alternate-stream
  paths, no traversal, no absolute, no drive, no UNC paths (the parser
  rejects non-ASCII entry names that could carry such paths)

The publisher invokes `ReleaseArchive.parse` directly on the candidate
bytes (no extraction, no execution) before any subsequent decision runs.

### Structured-JSON-on-exit contract

`ReleaseRunner.exe` emits one JSON object on stdout before every exit (success,
failure, bump-required): `status`/`reason` plus the config context
(`componentId`, `version`, `archiveName`, `assetUri`) whenever the contract has
been parsed. A `build`/`pins`/`tests` phase failure emits
`status=reason` plus `phase`, `childExitCode`, `childStderr` and the config
context, so a failure never reaches the transaction unnoticed. The empty-argv
case prints only its `usage` lines and exits `2`, because no contract context is
available.

### Public status set

The stdout JSON `status` is one of three public values; the `reason` field
carries internal detail:

- `published` — the candidate archive and metadata were uploaded; remote
  digests were verified. The JSON populates `publishedArchiveSha256` and
  `publishedMetadataSha256` from the verified remote bytes (never candidate
  values); the verified projection file additionally carries `manifestSha256`
  and `publishedRevision`.
- `unchanged` — strict comparison classified the candidate as byte-identical or
  strictly revision-only equivalent and the immutable metadata asset matched the
  candidate render or was verified absent. In the verified-absence case only the
  metadata is republished so the release reaches the same verified state without
  touching the archive. Published archive sha, manifest sha, and revision always
  come from the observed remote archive, never from candidate values.
- `failed` — anything else; `reason` carries the internal detail
  (`bump-required` with `bumpedVersion`, `publish-pending`, transport error,
  parse failure, immutable container, corrupt or mismatching metadata,
  validation-gate failure). On this branch every `published*` key is absent, so
  candidate hashes cannot masquerade as published values.

### Integrity invariants

- Archive and metadata are both observed before any publication; unknown,
  corrupt or conflicting metadata blocks the archive POST, and observation
  errors fail closed.
- Every required metadata identity field is compared against canonical producer
  policy and the verified published bytes; the published revision comes from the
  observed remote embedded manifest, not from the candidate HEAD.
- Initial observation, post-upload readback, and idempotent recovery all
  require a present, well-formed API SHA-256 that matches the downloaded bytes.
  Candidate byte equality is not a substitute for claimed-digest verification.
- `ReleaseCompare.validate` runs on the candidate observation inside
  `ReleaseEvaluate.evaluate` before the remote observation and any upload
  branch.
- `masterlifting/mcp-store` is the fixed repository policy. The canonical
  `assetUri` is taken verbatim from the contract DTO and validated against
  `https://github.com/<owner>/<repo>/releases/download/<component>/<archive>`;
  the runner refuses to reconstruct it from environment variables.
- Existing assets are never overwritten, deleted or replaced; the no-op path
  parses the embedded distribution manifest through
  `ReleaseCompare.parseEmbeddedManifestEntry`.

## Publication role contract

The publish entrypoint only accepts the **candidate** role. The publisher and
runtime Bootstrap.ps1 InstallRuntime outputs are protected BOOTSTRAP
installation evidence and are not candidate publish input; a future DevOps
trust-receipt-path is owned by a separate assignment.

### Trusted builder/runtime install root

`Bootstrap.ps1 -Mode InstallRuntime -Role publisher` and `Bootstrap.ps1
-Mode InstallRuntime -Role runtime` are the transport-only bootstrap that
installs the trusted publisher runtime (and runtime envelopes) into a
fresh isolated directory outside the workspace, the candidate directory,
and the publish install root. The receipt these modes write has
`status="installed"` and `component=null`; it is consumed by the BOOTSTRAP
installation path, never by `ReleaseRunner publish`. The publish entrypoint
rejects a Bootstrap.ps1 InstallRuntime receipt with a typed
`MalformedArtifact` before reading any candidate byte, token, or HTTP
transport.

### Candidate fetch receipt

`Bootstrap.ps1 -Mode FetchCandidate -Role candidate -Component <dotnet|workflow>`
is the transport-only bootstrap that fetches a candidate archive produced
by `ReleaseRunner prepare` and writes a receipt with `status="fetched"`,
`role="candidate"`, and `component=<dotnet|workflow>`. The publish
entrypoint's `--transport-evidence=<path>` MUST point at that receipt.
The receipt's `component` MUST equal the publish CLI `--component`; a
null or mismatching receipt `component` is a typed `MalformedArtifact` /
`IdentityMismatch` before any further cross-check. The CLI
`--role=candidate` and the receipt `role=candidate` are exact-matched;
the receipt `status` MUST be `fetched`; the receipt's `component` MUST
be present.
The bootstrap currently validates and extracts the raw download into its isolated
candidate directory, then removes the temporary install root; it does not expose
the original transport file. Thus this publisher input cannot yet be wired from
FetchCandidate alone. DevOps must retain/copy the actual streamed raw ZIP bytes to
a protected path outside extraction and pass that exact file; never substitute
the extracted inner archive or its digest.

The `--workspace-root` is the trusted canonical source package/repository root
against which roster and component policy paths are resolved. The current
`BuildProvenance.committedHead` implementation invokes Git with that exact path as
its working directory, so it assumes this root is also a Git worktree root (or a
Git-accepted directory within the worktree). A packaged source snapshot without
Git metadata cannot satisfy the existing exact-HEAD check; this assignment does
not relax or replace that trust check.

### Bootstrap CLI values

- `-RoleMapJson` is the independently supplied expectation map; the role,
  artifact name, uploader job name and component are each compared against it and
  never echoed from a platform response. Each role entry requires
  `artifactName` and `uploaderJobName`; `component` must be a string for the
  `candidate` role and null for `publisher`/`runtime`:

  ```json
  { "<role>": { "artifactName": "...", "uploaderJobName": "...", "component": "<componentId>" | null } }
  ```

- `-UploaderJobId` is the platform job id that ran the upload step for this
  artifact; the workflow supplies `job.check_run_id`.
- `-PlatformDigest` carries the trusted upload output and accepts only the
  documented outer platform digest forms `sha256:<64 lowercase hex>` or a bare
  64-character lowercase hex string; the value is normalized to bare lowercase
  hex so the trusted upload output, the platform metadata and the observed raw
  transport bytes are compared in one namespace.

### Why the two paths are separate

The publisher/install bootstrap and the candidate fetch bootstrap run in
different hosted jobs against different platform artifacts, write receipts
to different isolated paths, and produce non-overlapping metadata
(`status`, `component`, `role`). Merging them into one CLI surface would
let a candidate manufacture its own install evidence; keeping them
separate means the publish entrypoint can fail closed on any
publisher/runtime receipt before composing any HTTP transport, auth, or
upload-core.

### Three independent role fields

The publisher carries three role strings, one per channel, and they are
deliberately NOT cross-checked against each other:

| Channel                  | Field source         | Allowed value | Validated by                  |
| ---                      | ---                  | ---           | ---                           |
| Publish CLI              | `--role`             | `candidate`   | `parsePublishCli`             |
| Trusted publisher receipt | `receipt.role`       | `publisher`   | `crossCheckReceipt`           |
| Bootstrap receipt        | `receipt.role`       | `candidate`   | `crossCheckBootstrapReceipt`  |

The CLI role and the bootstrap receipt role happen to share the value
`candidate` (a FetchCandidate receipt documents the candidate fetch that
the publish CLI authorised); that match is enforced because both sides
describe the same fetched artifact. The trusted publisher receipt
describes the independently-built publisher/runtime install and carries
`role=publisher`; binding it to the CLI `--role` would conflate the
trusted channel with the candidate channel and reject a correctly
emitted publisher receipt, so the check is independent. No alias is
maintained between the old "trusted receipt role" wording and the new
"trustedReceipt.role" wording — the channel-level role is forward-only.

## Regression gate

`Suite.ps1` is the non-publishing release gate:

- It executes the compiled Expecto project
  `infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll`
  (`dotnet build infrastructure/release/tests/Tests.fsproj -c Release` first).
  That entrypoint is a console Expecto host, so `dotnet test` reports no counts
  for it; `Suite.ps1` uses the native Expecto exit code and compares the
  executed-case count against the suite's own `--list-tests` inventory, so a
  dropped or unrun case fails the gate.
- It runs the retained producer provenance entrypoint
  `dotnet/tests/ProvenanceTests.fsx`, which is safe from a dirty tree. The
  producer packaging entrypoints (`<componentRoot>/tests/DistributionTests.fsx`,
  derived from each roster participant) need a clean tree and a producer build,
  so `ReleaseRunner.exe prepare` runs them per component in its `tests`
  phase instead; the gate asserts they still exist.
- The compiled suite includes the historical integration, which reads the pinned
  archives through `MCP_RELEASE_HISTORICAL_ROOT` (or `-HistoricalRoot`) and
  re-verifies each archive's SHA-256 and size. `Suite.ps1` fails closed with an
  explicit `::error::` when that root is unset or is not a directory, and the
  compiled case reports a missing or mismatched archive as a failure; no case is
  ever excluded, filtered or rewritten to work around a missing prerequisite.
  The `tests` job of `release.yml` provisions the fixtures from the already
  published release assets, deriving the component tags from the roster
  projection; the fixture version, file names and hash pins stay owned by the
  compiled `Historical` case.
- It snapshots `GITHUB_ENV`/`GITHUB_OUTPUT` (target and bytes) and fails if a
  child redirects or writes them.

## Producer test gates

`ReleaseRunner.exe prepare` runs an explicit per-component test gate
list after `Build.fsx` and `Pins.fsx` succeed and before the publisher
reads the candidate. The list is hard-coded in
`ReleaseOrchestration.producerScriptTests` (no dynamic `tests/*.fsx`
discovery, no roster-driven enumeration of test scripts), so a new
producer test entrypoint is deliberately not picked up until the
orchestrator is updated.

| Component | Tests run by the orchestrator |
| --- | --- |
| `dotnet`   | `dotnet/tests/DistributionTests.fsx` |
| `workflow` | `workflow/tests/DistributionTests.fsx`, `workflow/tests/PackagedRuntimeCoordinationTests.fsx` |

The non-package Workflow suites (`AuthorityMetadataTests.fsx`,
`EphemeralLockInvariantTests.fsx`, `SchemaParityGuardTests.fsx`,
`TaskContractTests.fsx`, `TaskPathSafetyTests.fsx`, `TaskProfileTests.fsx`,
`WorkflowMcpFindingTests.fsx`, `WorkflowMcpNativeCompatTests.fsx`,
`WorkflowMcpTests.fsx`, `WorkflowTests.fsx`) require no GitHub credentials
and are driven by the read-only `tests` job in `release.yml` rather than by
the orchestrator's subprocess contract; the credential-scrubbing boundary
above applies to every orchestrator subprocess, and that boundary is the
reason those suites are not in `producerScriptTests`.

`release.yml`'s read-only `tests` job builds both Expecto projects, runs the
producer gate, provisions the pinned historical fixtures, then runs `Suite.ps1`;
the `release` job depends on `tests`, so a failed gate blocks every upload.

## Strict script typecheck gate

`Typecheck.ps1` is the read-only strict FCS gate for the release-path scripts
that no compiled project build covers. It derives the producer targets from the
roster (`<configDirectory>/Build.fsx`, `<configDirectory>/Pins.fsx` and
`<componentRoot>/tests/DistributionTests.fsx` for each participant) and adds the
fixed non-roster scripts `infrastructure/release/Bootstrap.fsx` and
`dotnet/tests/ProvenanceTests.fsx`; an explicit `-Targets` run overrides the
derivation, so no participation list is restated in the gate. The compiled
modules and the compiled regression project are type-checked by their own builds,
so this gate never re-typechecks that graph. It resolves the canonical SDK from
`dotnet --list-sdks` (no SDK table or hard-coded developer path), writes a probe
under `$RUNNER_TEMP` — outside the repository, so its SDK `#r` reference never
enters the in-repo analyzer surface — and type-checks each target and its
`#load` closure with `useSdkRefs` using the exact canonical SDK's `fsi.dll`.
Targets are read and type-checked as text only; they are never evaluated, so no
assets, pins, token, or network are touched. A zero-target run, a missing derived
target, or any reported error fails closed.

## Workflow result parsing

`release.yml`'s `append <component> structured release result` step parses the
runner's output on every exit code. It writes typed fields (reason, version,
archiveName, assetUri, `phase`, `childExitCode`, candidate/published hashes,
revisions) to `release-status-<component>.json`; a missing structured `status` is
evidence-only, never a guessed exit code. A verified projection is copied to
`published-metadata-<component>.json` for `published` and `unchanged` only when
it carries `archiveSha256`, `manifestSha256`, and `publishedRevision` and its
archive sha matches the observed published archive; candidate pins stay a
separate artifact.

## Exit codes

`ReleaseRunner.exe`: `0` = `published`/`unchanged` (including metadata-only
recovery), `1` = `failed` without a bump suggestion, `2` = `failed` with
`bumpedVersion` (bump-required) or a usage error, `3` = a required manifest or
archive is missing, `4` = the contract DTO is invalid, the worktree is dirty,
HEAD cannot be resolved, the manifest revision does not match HEAD, or the
canonical config could not be projected. A `build`/`pins`/`tests` phase failure
exits with that child's code and emits the structured phase JSON.
