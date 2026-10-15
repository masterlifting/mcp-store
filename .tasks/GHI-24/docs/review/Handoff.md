# SEC1 trusted policy / context / candidate handoff — implementation notes

This document is the factual companion to `docs/GHI-24/review/Boundary.md`
(frozen decisions) and `docs/GHI-24/review/Boundary-build.md` (first compiled
batch). It records what was authored in the bounded handoff batch, the
interfaces those files expose, and the interface facts that remain open.
It does not restate policy and claims no approval.

## Status and scope

Authored in this batch (resumed by a fresh session after the prior
engineer was quota-exhausted mid-implementation):

- `infrastructure/release/Handoff.fs` — outer-handoff validator and
  `Receipt` evidence document.
- `infrastructure/release/PublisherClosure.fs` — slim closure schema
  (identifiers only, no zero hash placeholders, no author policy
  duplicates).
- `infrastructure/release/Publisher.json` — reduced to
  `schemaVersion` / `repository` / `rosterPath`.
- `infrastructure/release/Runner.fs` — modern `publish` CLI with typed
  IDs / role / component / repo / runattempt / SHA / artifact / actual
  transport SHA-256 / toolchain flags; explicit `--workspace-root`;
  no `--platform-digest`, no env-var aliasing, no `--archive-name`
  override, no unknown / duplicate flags; receipt cross-check against
  CLI context; handoff bundle validation before any policy decision.
- `infrastructure/release/README.md` — updated to describe the new
  architecture (Publisher closure vs Receipt split, the typed CLI
  surface, the candidate-directory three-file contract, the
  `ReleaseHandoff.Handoff.readFromDirectory` entry, the explicit
  `--workspace-root` invariant).

Deliberately **not** modified: `infrastructure/release/Policy.fs`
(canonical parser is final; `parseBytes` is the byte-safe primary
entrypoint the handoff re-uses unchanged), `Transaction.fs`,
`Evaluate.fs`, `Compare.fs`, `Publish.fs`, `Capture.fs`, `Enrollment.fs`
(`ReleaseEnrollment.validateComponentIdInRoster` is consumed unchanged
in `Runner.publish`), `Metadata.fs`, `Archive.fs`, `Bootstrap.ps1`,
`Sources.json`, `.github/workflows/release.yml`, the existing `tests/*.fs`
test files (already broken by the prior batch's `orchestrate`
removal; the dedicated tester batch owns their migration to
`prepare` / `publish`), every producer Config.fsx, every Build/Pins
script, the Telegram surface, and every unrelated producer / consumer
/ test file.

`Policy.fs` is the canonical `ReleasePolicy.parseBytes` interface for
`module ReleaseConfig`; the `Runner.publish` flow hashes and parses the
SAME bounded bytes via `parseBytes(absoluteConfigPath, preVerifiedBytes)`
so the publisher cannot accidentally re-read a candidate-mutated file
between integrity verification and policy derivation.

The auth / transport / HTTP / classifier / executor semantics in
`Transaction.run`, `Evaluate.evaluate`, `Compare.compare`,
`Publish.uploadCandidateBytes / uploadMetadataBytes`,
`Provenance.committedHead`, and `Capture.runProcessCapture` are NOT
rewritten in this batch; the canonical C1 semantics remain the only
publish-time decision pipeline.

## `Handoff.fs` — Receipt evidence document and outer-archive validator

`Receipt` is an F# record (was an `internal` constructor class in the
prior partial batch; the class-with-object-expression trick broke the
`{ receipt with SourceSha256 = sha }` syntax in `readReceiptFromFile`,
fixed by converting to a record carrying the same fields plus `role`).

Receipt schema (`renderReceipt` / `parseReceipt`, schemaVersion 1):

```json
{
  "schemaVersion": 1,
  "repository": "owner/name",
  "eventRevision": "<40-hex>",
  "runId": "<id>",
  "attempt": "<n>",
  "trigger": "<push|...>",
  "role": "<publisher|runtime|candidate>",
  "rosterPath": "<relative path>",
  "rosterSha256": "<64-hex>",
  "componentConfigPaths": { "<componentId>": "<relative path>", ... },
  "componentConfigShas": { "<componentId>": "<64-hex>", ... },
  "runtimeFilePaths": [ "<relative path>", ... ],
  "runtimeFileDigests": { "<relative path>": "<64-hex>", ... },
  "runtimeFileSizes": { "<relative path>": <bytes>, ... },
  "builderSdk": "<sdk>",
  "builderRuntime": "<runtime>",
  "builderPlatform": "<platform>"
}
```

The runtime-file triplet is OPTIONAL and populated only when the receipt
was emitted by the trusted-builder install-runtime path. A prepare-time
receipt (orchestrator-generated) leaves all three lists empty; the
parser fails closed on partial declarations (e.g., `runtimeFilePaths`
non-empty but `runtimeFileDigests` empty) but accepts an empty triplet
on all three sides.

`Handoff.Handoff.create` validates the supplied `archiveBytes` via
`ReleaseArchive.parse` BEFORE any extraction (exact names, entry /
expansion limits, no traversal / absolute / drive / UNC / alternate-stream
paths, no links, no duplicates, no case collisions) and then verifies
that the embedded `distribution.json` entry in the archive hashes
byte-for-byte to the standalone manifest bytes supplied by the caller.
A mismatch yields a typed `ReleaseError`; no bytes are written or
returned until every check passes.

`Handoff.Handoff.readFromDirectory` reads the three candidate-directory
files (`<archive-name>.zip`, `distribution.json`, `receipt.json`)
through their relative exact names, parses the receipt via
`parseReceipt` (which reads it as bounded UTF-8 from the exact path;
no `__SOURCE_DIRECTORY__` shortcut, no allocation shortcut), and calls
`Handoff.create` with the defensive copies (`Array.copy`) so the
`Handoff` record holds intrinsically immutable bytes even if the
caller mutates the underlying `byte[]` after the factory returns.

`Handoff.bootstrapFetchCandidate` is the helper the bootstrap install
path uses to materialize a `Handoff` from the bootstrap-time receipt +
the just-streamed archive bytes + the candidate manifest; it shares the
exact same defensive-copy + outer-archive + standalone/embedded equality
checks before any policy decision runs.

`Handoff.renderReceipt` and `Handoff.writeReceiptToFile` exist for the
trusted-builder emission step (out of this batch's compile scope; the
builder job consumes them). `writeReceiptToFile` writes the rendered
JSON via a unique `.tmp-<guid>` atomic move so a partial write never
overwrites an existing receipt.

## `PublisherClosure.fs` / `Publisher.json` — identifiers only

The prior batch left `Publisher.json` carrying
`expectedConfigShas`/`expectedComponentIds` zero placeholders that
**duplicated the very values the trusted builder was supposed to
generate**. Both fields are removed in this batch. The audited closure
now carries ONLY:

- `schemaVersion` (must equal 1)
- `repository` (canonical `masterlifting/mcp-store`)
- `rosterPath` (relative path to `Participants.json`)

`PublisherClosure.fs` parses strictly: unknown fields, missing fields,
null values, wrong schema version, and empty strings are rejected before
any closure value is consumed. The publisher never invents or trusts a
closure value; this parser is the only path that loads `Publisher.json`
into the publisher job.

Every trust anchor that the publisher needs — the roster SHA-256, every
per-component Config.fsx SHA-256, the observed toolchain, the run-attempt
metadata — is supplied by the SEPARATELY generated `receipt.json` that
the trusted builder emits and the bootstrap step downloads into the
candidate directory. The publisher independently re-hashes the roster
and every `Config.fsx` and compares against the receipt; a candidate
that manufactures its own expectations is rejected.

## `Runner.fs` — modern publish CLI

The `publish` entrypoint now requires the following CLI flags (and
NO environment-variable aliasing; the boundary amendment explicitly
removes the misleading os-sdk-label / env alias surface):

```
ReleaseRunner publish <publisher-closure-path> <candidate-directory>
    --repo=<owner>/<name>
    --run-id=<id>
    --attempt=<n>
    --event-sha=<40-hex>
    --role=<publisher|runtime|candidate>
    --component=<dotnet|workflow>
    --artifact-id=<id>
    --artifact-name=<name>
    --transport-sha256=<actual-64-hex>     # ACTUAL SHA-256 of streamed outer bytes
    --builder-sdk=<sdk>                    # observed SDK (separate from --transport-sha256)
    --builder-runtime=<runtime>            # observed runtime
    --builder-platform=<platform>          # observed platform
    --workspace-root=<path>                # explicit trusted checkout root; __SOURCE_DIRECTORY__ is forbidden
    [--trigger=<event>]                    # default: push
```

Unknown flags, empty flag values, missing flags, non-hex `--event-sha`,
non-hex `--transport-sha256`, a `--repo` that is not of the form
`owner/name`, and a non-existent `--workspace-root` all fail closed
with structured typed diagnostics before any network or transport
code runs.

The publisher executes the following boundaries, in order, with the
genuine HTTP remote observer (`ReleaseTransaction.buildRemoteObserverWith`)
composed only AFTER the verified snapshot exists:

1. Read the audited `Publisher.json` (`ReleasePublisherClosure.readFromFile`)
   — every subsequent decision derives from its identifiers or fails closed.
2. Read the candidate directory via `ReleaseHandoff.Handoff.readFromDirectory`,
   which runs `ReleaseArchive.parse` and confirms standalone / embedded
   manifest equality before any policy decision runs. The publisher
   never reads from a pre-extracted publisher/runtime directory.
3. Read the trusted CLI context (no env aliasing) — every missing or
   malformed flag fails closed.
4. Cross-check the closure `Repository` against the CLI `--repo` AND
   against the receipt `Repository`. Cross-check the receipt
   `RosterPath` against the audited closure `RosterPath`. Hash the
   `closure.RosterPath` at the `--workspace-root` and assert the
   publisher-observed roster SHA-256 matches both the receipt
   `RosterSha256`. Cross-check the receipt `EventRevision`,
   `RunId`, `Attempt`, and `Role` against the CLI flags.
5. Resolve the trusted canonical `Config.fsx` path from the receipt's
   `componentConfigPaths[--component]`, hash the bytes, and assert the
   hash matches the receipt's `componentConfigShas[--component]`. Feed
   the SAME bounded bytes through `ReleasePolicy.parseBytes` so policy
   is derived independently from the same bytes that were hashed. The
   receipt provides the expected SHA-256; the candidate cannot
   manufacture that expectation.
6. Cross-check the receipt `BuilderSdk`/`BuilderRuntime`/`BuilderPlatform`
   against the parsed `PolicyProjection.SdkVersion`/`RuntimeVersion`/`Platform`
   AND against the CLI `--builder-sdk`/`--builder-runtime`/`--builder-platform`.
   The toolchain identity is separate from the CLI
   `--transport-sha256`; no flag is conflated with another.
7. Cross-check the `Handoff.ArchiveSha256` against the CLI
   `--transport-sha256` so the publisher-observed outer-archive digest
   equals the workflow-claimed outer-archive digest.
8. Recheck the workspace HEAD at publication time against the CLI
   `--event-sha` (`BuildProvenance.committedHead`) so a stale or moved
   workspace cannot publish under the wrong revision. The HEAD check
   uses the explicit `--workspace-root`; `__SOURCE_DIRECTORY__` and
   the build-machine HEAD are forbidden.
9. Compose the verified snapshot via the private `VerifiedCandidate.create`
   factory (defensive `Array.copy` of archive + manifest bytes) and drive
   the existing `ReleaseTransaction.run` with that snapshot. The
   publisher never re-reads from disk between verification and upload.

The legacy `--platform-digest`, the `--archive-name` archive override,
the `--component` env-var alias (`MCP_RELEASE_COMPONENT`), and the
`MCP_RELEASE_*` env aliases for repo / run-id / attempt / event-sha /
platform-digest are REMOVED in this batch. The legacy
`ReleaseRunner orchestrate` mode and the
`ReleaseRunner.exe <manifest> <archive> <contract-json>` 3-argument
transaction surface are still REMOVED (no compatibility alias, no
environment-mode switch) per boundary decision 4 — they are not
re-introduced here.

## Open interface facts (BLOCKED on DevOps / producer inputs)

1. **`receipt.json` in the candidate directory** — the publisher reads
   `<candidate-directory>/receipt.json` via `Handoff.readFromDirectory`.
   Who writes it and when:
   - The trusted builder job emits it as one of its outputs and uploads
     it to GitHub Actions as an artifact (separate workflow step).
   - The bootstrap step downloads that artifact into the candidate
     directory (DevOps workflow wiring; future batch).
   - The publish step then reads the three exact-named files.
   Until DevOps wires that path, `Handoff.readFromDirectory` will fail
   closed with `MissingArtifact` for `receipt.json` and the publisher
   will refuse to publish; no default / placeholder / zero fallback
   is invented.

2. **`expectedRuntimeManifestSha256` placeholder** — the prior batch's
   `Bootstrap.md` documents `expectedRuntimeManifestSha256` as a
   forbidden field on `Sources.json`; the trusted-builder install-runtime
   receipt is not produced by any compiled tool in this batch. Until
   the trusted builder emits the full runtime manifest, the publisher
   accepts an empty runtime-triplet (a strict subset of `Receipt`) and
   the bootstrap / install path is the only consumer of the populated
   runtime triplet. This is the factual state at compile time; the
   "fresh-job workflow wiring" batch that delivers the full builder
   job will close this gap.

3. **Receipt's `role` field** — every publish-time receipt must carry
   a non-empty `role` whose value matches the CLI `--role`. The publisher
   cross-checks them. Possible values today: `publisher`, `runtime`,
   `candidate`. The candidate can never be a publisher; the trusted
   builder emits a `publisher` role receipt only when the install path
   is selected.

## `Release.fsproj` and compile order

`Handoff.fs` is inserted in the compile order before `Evaluate.fs` (which
still consumes `ReleaseCompare.EmbeddedManifestHeader` and
`ReleaseCompare.ArchiveObservation` unchanged) so the verified receipt
type is visible to `Runner.fs` without circular-project orderings.

```
Provenance → Archive → Compare → Metadata → Publish → Capture
            → Enrollment → Policy → PublisherClosure → Handoff
            → Evaluate → Transaction → Orchestration → Runner
```

`Policy.fs` keeps the original byte-safe `parseBytes` surface; the
runner hashes the canonical Config.fsx bytes once and feeds the SAME
`byte[]` to `ReleasePolicy.parseBytes(absoluteConfigPath, configBytes)`.
The candidate cannot manufacture a `PolicyProjection` the publisher
did not derive from the canonical bytes.

## Build run IDs (this batch)

- Initial state (prior batch partial): failing (8 compile errors).
- After `Handoff.fs` record conversion + Receipt `role`: build ID
  `uzL0WRrqLuPMsfszwcAHSjpph7KnPLFA8zJ3kPwixdU` (0 errors, 0 warnings).
- After `PublisherClosure.fs` slim + `Runner.fs` legacy closure
  references removed: build ID
  `tLDKksyCRntntYeKQbkpdKAKpxxuTY5vde_hwGrodWk` (0 errors, 0 warnings).
- After final `README.md` updates: build ID
  `1c7u-JC9fc1nnq87A6BChES6QwUvuoHXr6Y6anl_Uuo` (0 errors, 0 warnings).

## Validation performed

- `dotnet build infrastructure/release/Release.fsproj -c Release` via
  the dedicated `dotnet_build` MCP tool: succeeded at all three milestones
  above with 0 errors and 0 warnings; `noRestore=true` to avoid the
  slow NuGet re-resolution that produces noisy unrelated NETSDK1057
  preview messages on `net11.0`.
- The dependent `infrastructure/release/tests/Tests.fsproj` is NOT
  expected to build in this batch — it references the removed legacy
  `ReleaseOrchestration.orchestrate` and `ReleaseRunner orchestrate`
  surfaces per boundary decision 4. The dedicated tester batch owns
  the migration of those scenarios to `prepare` / `publish` and the
  re-validation of the compiled regression suite.
- No live platform call, no upload, no credential read, no `.env`
  read, no secret read, no network mutation, no commit, no push, no
  task-state mutation, no foreign-worktree change performed.
- No `Release.fsproj` `PackageReference` or `ProjectReference` was
  added (the Sources.json audit closes that gap by enumerating only
  exact source paths under `infrastructure/release/*.fs` plus the
  shared `infrastructure/Provenance.fs`).
- `Policy.fs` was NOT touched in this batch; the policy parser was
  already corrected for the canonical F# grammar in the prior batch
  (`Boundary-build.md` C1) and remains the trust anchor for the
  publisher's independent policy derivation.
