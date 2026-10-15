# SEC1 Handoff core strict-input fixes — three production gaps

This document is the factual companion to
`.tasks/GHI-24/docs/review/Handoff-core.md` and `Handoff-fix.md` (prior
two-channel and defect batches). It records the SHORTEST set of source
fixes the production `infrastructure/release/Handoff.fs` needs to
close three independent trust-anchor input loopholes identified by
`.tasks/GHI-24/docs/review/Handoff-tests.md`. It does not restate policy
and claims no approval.

## Status and scope

Source files modified in this batch:

- `infrastructure/release/Handoff.fs` — three targeted, minimal fixes
  against the production two-channel parser (`parseTrustedPublisherReceipt`,
  `parsePreparationReceipt`, `readRawTransport`).
- `.tasks/GHI-24/docs/review/Handoff-strict.md` — this record.

Deliberately **not** modified: `infrastructure/release/Release.fsproj`
(compile order unchanged), `Runner.fs` (the publish entrypoint still
fails closed), `Policy.fs` (canonical parser is final), `PublisherClosure.fs`
(carrying the same `Uri.IsHexDigit` shape but owned by a separate
batch), `Compare.fs` (already has a strict lowercase `isLowerHexRevision`),
`Archive.fs`, `Metadata.fs`, `Publish.fs`, `Capture.fs`, `Enrollment.fs`,
`Evaluate.fs`, `Transaction.fs`, `Orchestration.fs`, `Bootstrap.ps1`,
`Bootstrap.fsx`, `Sources.json`, `.github/workflows/release.yml`,
every producer `Config.fsx`, every Build / Pins script, the Telegram
surface, the producer-package scripts, the existing `tests/*.fs` files
(the dedicated tester batch owns the migration), and every unrelated
producer / consumer / test file.

`mayEdit=true` scoped to `Handoff.fs`, `mayBuild=true` on
`infrastructure/release/Release.fsproj`, `mayTest=false`,
`mayDelegate=false`. No live platform call, no upload, no credential
read, no secret read, no `.env` read, no network mutation, no commit,
no push, no task-state change, no foreign-worktree change performed.
No CLI invocation, no `.fsx` host spawn, no manual execution.

## Frozen shape: lowercase hex, strict, regex, no uppercase

Both digest / event-revision helpers (`isLowerHex40`, `isLowerHex64`)
share a single frozen canonical:

- explicit ASCII range `0-9a-f` only (`^[0-9a-f]{40}$` /
  `^[0-9a-f]{64}$`),
- `RegexOptions.CultureInvariant` (no locale handling),
- WITHOUT `RegexOptions.IgnoreCase` (uppercase `A-F` rejected),
- shared by the two compiled `Regex` instances (`lowerHex40Pattern`,
  `lowerHex64Pattern`).

The prior implementation walked `value` with `Uri.IsHexDigit`, which
accepts both `0-9` and `A-F`. Every digest / event-revision call site
in `Handoff.fs` (`parseOuterDigest`, `readRequiredOuterDigestMap`,
`parseTrustedPublisherReceipt` `eventRevision`, `readRawTransport`
`evidence.ActualOuterDigest.Value`) now flows through the strict
helpers and surfaces a typed `MalformedArtifact` on a non-lowercase
input. No alias or "uppercase-allowed" variant is provided: the
trust-anchor input shape is a single source of truth.

## Runtime triplet — exact set equality, bounded relative paths, no duplicates

The trusted runtime triplet
(`runtimeFilePaths` ↔ `runtimeFileDigests` ↔ `runtimeFileSizes`) is
treated as THREE sets with EXACT equality:

- `runtimeFilePaths` is a list, but the parser now rejects duplicate
  paths via an ordinal `HashSet` cross-walk (a duplicate path shadows
  the prior entry's digest / size check and produces an ambiguous
  install record; the digest / size maps are JSON objects so their
  keys are unique by construction).
- Every runtime path is validated by `isBoundedRelativeRuntimePath`
  which is `pathLengthOk && isPathSafe` (no backslash, no colon, no
  leading slash, no `.` / `..` segment, no non-ASCII byte, length ≤
  256). A trusted receipt that declares `../etc/passwd`,
  `/usr/bin/dotnet`, `C:/dotnet`, or any other absolute / drive /
  UNC / traversal path is rejected before any policy decision runs.
- Every digest key MUST appear in `runtimeFilePaths` (existing
  forward check), every size key MUST appear in `runtimeFilePaths`
  (NEW reverse check that closes the previously-accepted
  `runtimeFileSizes.extra.dll = 7` gap), and every path MUST have a
  matching digest AND size (existing forward check). No
  extra / missing / partial combination survives; the trusted runtime
  triplet is mandatory (runfiles must be non-empty per the prior
  batch) and cross-consistent in both directions.

The reverse size check is the specific gap called out in
`Handoff-tests.md`: the prior loop only checked path → digest and
path → size, never size → path, so a valid `dotnet.exe` triplet plus
`runtimeFileSizes.extra.dll = 7` parsed as `Ok`. The fix surfaces a
typed `MalformedArtifact` carrying the orphan / dup / traversal
detail before the parser reaches the `Ok` branch.

## Standalone vs embedded manifest — raw byte equality BEFORE snapshot

`readRawTransport`'s embedded-manifest branch now compares the
standalone outer manifest bytes (`manifestPayload` — the raw
`distribution.json` entry inside the outer transport ZIP) against
the embedded inner manifest bytes (`embeddedManifestBytes` —
`UTF-8.GetBytes` of the inner producer archive's `distribution.json`
text) BEFORE the `CandidateArchiveSnapshot` is constructed.

A byte-level mismatch returns
`Error(IdentityMismatch("standaloneEmbeddedManifestBytes",
embeddedSha, standaloneSha))`. The comparison:

- uses RAW byte arrays (no semantic JSON equivalence is accepted; a
  re-ordered key, a different newline style, or a trailing whitespace
  change all alter the published digest without changing visible
  content),
- runs BEFORE the snapshot is built so the publisher never observes
  a candidate whose outer / inner manifest identities disagree,
- preserves the existing typed-error vocabulary (`IdentityMismatch`
  is the canonical surface for trust-anchor disagreements between two
  trusted pieces of candidate data; structural shape defects continue
  to use `MalformedArtifact`),
- preserves the inner / outer archive digest distinction — the
  inner producer archive digest and the outer transport archive
  digest remain DISTINCT because they describe different archives,
  and the `preparationOuterDigestIsIndependent` pipeline contract
  is preserved unchanged. The manifest-byte equality check is
  scoped to the `distribution.json` payload bytes that BOTH layers
  carry.

## Interface delta summary

The new production contract (no compat aliases, no shims):

| Surface                          | Before                                                     | After (this batch)                                                                                |
|----------------------------------|------------------------------------------------------------|---------------------------------------------------------------------------------------------------|
| `isLowerHex40` / `isLowerHex64`  | `Uri.IsHexDigit` walker (accepts `A-F`)                    | frozen `^[0-9a-f]{40\|64}$` regex, `CultureInvariant`, no `IgnoreCase`                            |
| `parseTrustedPublisherReceipt` runtime cross-walk | path→digest + path→size + digest→path only; no path-dup check; no traversal guard | + reverse size→path + path-dup reject + `isBoundedRelativeRuntimePath` per entry |
| `readRawTransport` embedded-manifest branch        | `manifestPayload` and `embeddedManifestBytes` snapshotted independently | `IdentityMismatch("standaloneEmbeddedManifestBytes", …)` BEFORE `CandidateArchiveSnapshot` |

The `pathLengthOk` and `isPathSafe` helpers were hoisted from the
outer-transport section into the shared parser-helpers section so the
trusted runtime-path checker can reuse them as a single source of
truth (no duplicated rule lists). The outer-transport section
retains its narrative comment about defense-in-depth over the inner
parser.

## Build run ID (this batch)

- After the three production-gap fixes (`isLowerHex40` / `isLowerHex64`
  regex rewrite, runtime triplet exact-set equality + path-dup + bounded
  relative paths, standalone / embedded manifest byte equality before
  snapshot), the `pathLengthOk` / `isPathSafe` hoist, and the new
  shared `isBoundedRelativeRuntimePath` helper, the project compiles.
  Build run ID `NPuiaGhaRjKpUlTB5SreTT1wIykm85XzyNrSYK4MKHI` via the
  dedicated `dotnet_build` MCP tool
  (`dotnet build infrastructure/release/Release.fsproj -c Release`;
  `noRestore=true` to avoid the slow NuGet re-resolution that produces
  noisy unrelated NETSDK1057 preview messages on `net11.0`).
  **0 errors, 0 warnings**.
- Production DLL produced:
  `infrastructure/release/bin/Release/net11.0/ReleaseRunner.dll`,
  494,080 bytes,
  SHA-256 `865abfd4256578f57646ca092991851e08e5bbec52b0e0465e8535c3efcbce92`.

The dependent `infrastructure/release/tests/Tests.fsproj` is **not**
expected to build in this batch — `mayTest=false`. The dedicated
tester batch owns the migration of the frozen new strict shapes
(positive: canonical lowercase digest / eventRevision,
canonical-non-empty cross-consistent triplet, canonical bounded
relative path; negative: uppercase eventRevision, uppercase
component SHA, uppercase manifest SHA, uppercase outer digest,
runtime-size-orphan, runtime-path-dup, runtime-path-traversal,
runtime-path-absolute, standalone-embedded-manifest trailing-space
mismatch).

## Test rerun readiness (after this batch's compile-only verification)

The dedicated tester batch's expected rerun numbers against the
production `Handoff.fs` after this batch (no test fixtures or
assertion text changed):

```
Total run:        101
Pass:             101   (was 96; +5 closed)
Fail:               0   (was 5;  closed)
Ignore:             0
Error:             0
```

The five closed failures were:

1. `isLowerHex40` accepting uppercase `eventRevision="A" * 40`
   (one case: "an uppercase event revision").
2. `isLowerHex64` accepting uppercase
   `componentConfigShas.dotnet="A" * 64` (one case: "an uppercase
   component SHA-256") and uppercase preparation
   `manifestSha256="A" * 64` (one case: "an uppercase manifest
   SHA-256").
3. Runtime triplet accepting `runtimeFileSizes.extra.dll = 7`
   without a matching path (one case: "an extra runtime size
   without a path").
4. `readRawTransport` accepting a standalone outer manifest with a
   trailing space while the embedded inner manifest carries the
   canonical bytes (one case: "handoff rejects standalone and
   embedded manifest byte mismatch").

The other 96 cases (`parseBytes` positives / negatives, the two
canonical portable positives, the symlink regression, the eight
observed-metadata mismatches, malformed / actual / length transport
negatives, extra / missing / duplicate / case-colliding entries,
absolute / UNC / ADS / backslash / dot outer paths, DOS reparse / Unix
non-regular types, the 4097-entry / per-entry / expansion-ratio /
total-uncompressed caps, copy-mutation / read-only-stream /
`TryGetBuffer` / later-stream protections, forbidden preparation
fields, and trusted / preparation schema-version / null / negative /
unknown-field negatives) all remain strict and unaltered. No test
fixture, assertion, or expected-error message was changed.

The >256 MiB outer transport-byte cap is NOT allocated / tested
(residual >256 MiB test gap acknowledged per the prior batch's
evidence).

## Validation performed

- `dotnet build infrastructure/release/Release.fsproj -c Release` via
  the dedicated `dotnet_build` MCP tool: succeeded with 0 errors and
  0 warnings at the run ID above; the `dotnet.details` output page
  confirms 0 errors / 0 warnings for that run.
- Manual SHA-256 capture of the produced DLL: 494,080 bytes, SHA-256
  `865abfd4256578f57646ca092991851e08e5bbec52b0e0465e8535c3efcbce92`.
- No live platform call, no upload, no commit, no push, no
  task-state mutation, no foreign-worktree change performed.
- No `Release.fsproj` `PackageReference` or `ProjectReference` was
  added; no `Tests.fsproj` registration was performed (the dedicated
  tester batch owns that).
- `Policy.fs`, `Runner.fs`, `Compare.fs`, `Evaluate.fs`,
  `Transaction.fs`, `Orchestration.fs`, `Archive.fs`, `Metadata.fs`,
  `Publish.fs`, `Capture.fs`, `Enrollment.fs`, `PublisherClosure.fs`,
  `Bootstrap.ps1`, `Bootstrap.fsx`, `Sources.json`,
  `.github/workflows/release.yml`, every producer `Config.fsx`,
  every Build / Pins script, the Telegram surface, the
  producer-package scripts, the existing `tests/*.fs` files
  (including `tests/Handoff.fs` which remains unregistered pending
  the tester batch), and every unrelated producer / consumer / test
  file were NOT touched.
- No `dotnet test`, no `ReleaseRunner` invocation, no FSI host
  spawn, no `.env` / secret / credential read, no network call, no
  state mutation, no commit was performed.

## Open interface facts (BLOCKED on workflow / producer inputs) — unchanged from the prior batches

1. **Trusted builder job that emits `TrustedPublisherReceipt`** to a
   separately supplied path — the future workflow adapter supplies
   the path; the publisher reads it via
   `readTrustedPublisherReceiptFromFile`. Until DevOps wires that path,
   the `publish` entrypoint fails closed; no default / placeholder /
   zero fallback is invented.
2. **Preparation-receipt emission by the candidate stage** — the
   future workflow adapter supplies the path inside the candidate
   directory; the publisher reads it via
   `readPreparationReceiptFromFile`. Until DevOps wires that path,
   `readRawTransport` itself does not consume a preparation receipt;
   the publisher is the only party that may cross-check it.
3. **`ReleaseEvaluate.VerifiedCandidate.create` + `VerifiedCandidate`
   refactor** — out of this batch's compile scope.
4. **`ReleaseEnrollment` cross-check against the trusted receipt's
   `componentConfigPaths` and `componentConfigShas`** — out of this
   batch's compile scope.
5. **HEAD recheck at publication time** — out of this batch's
   compile scope.
6. **`ReleaseCompare` standalone / embedded manifest byte equality
   cross-check** — NOW IMPLEMENTED in `readRawTransport` (this
   batch). The `ReleaseCompare` body-comparison path remains the
   canonical authority for inner-payload byte identity at the
   `ReleaseCompare.compareManifestBodies` boundary; the new
   `readRawTransport` check is the OUTER-TRANSPORT-INDEXED mirror
   that runs BEFORE the snapshot is constructed.

This batch does not claim `ReleaseCompare` body-comparison coverage
or any CLI / workflow wiring; both remain open. None of those open
facts are inferred or invented; none of them are required for this
batch's compile to succeed.