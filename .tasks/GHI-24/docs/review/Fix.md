# SEC1 Handoff core defect fixes — shortest batch to 3 defects

This document is the factual companion to
`.tasks/GHI-24/docs/review/Handoff-core.md` (preceding two-channel core
batch). It records the SHORTEST set of source fixes the production
`infrastructure/release/Handoff.fs` needs to unblock the two positive
Handoff cases (`Handoff-tests.md` reports 14/14 `parseBytes` pass and
2/2 Handoff positives BLOCKED on a single shape defect at the original
line 906), plus two structural defects the producer-side evidence
contract demands. It does not restate policy and claims no approval.

## Status and scope

Source files modified in this batch:

- `infrastructure/release/Handoff.fs` — three targeted, minimal fixes.
  The two-channel production-validation surface (`TrustedPublisherReceipt`,
  `PreparationReceipt`, `ArtifactEvidence`, `OuterDigest`,
  `InnerArchiveSha256`, `VerifiedEntryName`, `CandidateArchiveSnapshot`,
  `VerifiedEntry`, `readRawTransport`, `parseTrustedPublisherReceipt`,
  `parsePreparationReceipt`, `renderTrustedPublisherReceipt`,
  `renderPreparationReceipt`, `writeTrustedPublisherReceiptToFile`,
  `readTrustedPublisherReceiptFromFile`,
  `readPreparationReceiptFromFile`) is otherwise unchanged. No compat
  alias is retained; the prior surfaces are still retired.
- `.tasks/GHI-24/docs/review/Handoff-fix.md` — this record.

Deliberately **not** modified: `infrastructure/release/Release.fsproj`
(compile order unchanged), `Runner.fs` (the publish entrypoint still
fails closed with the structured `MalformedArtifact` reason; the future
workflow adapter is the only party that may wire the new core into the
publish entrypoint), `Policy.fs` (canonical parser is final; `parseBytes`
remains the byte-safe primary entrypoint), `PublisherClosure.fs`,
`Publisher.json`, `Archive.fs`, `Compare.fs` (inner-payload validation
remains the canonical authority for `PublishedFiles ∪ {NOTICE.txt,
distribution.json}`; the outer Handoff transport no longer overlaps it),
`Metadata.fs`, `Publish.fs`, `Capture.fs`, `Enrollment.fs`, `Evaluate.fs`,
`Transaction.fs`, `Orchestration.fs`, `Bootstrap.ps1`, `Bootstrap.fsx`,
`Sources.json`, `.github/workflows/release.yml`, the existing
`tests/*.fs` files (the dedicated tester batch owns their migration to
the frozen new schema), every producer `Config.fsx`, every Build / Pins
script, the Telegram surface, the producer-package scripts, and every
unrelated producer / consumer / test file.

`mayEdit=true` scoped to the named files, `mayBuild=true` on
`infrastructure/release/Release.fsproj`, `mayTest=false`,
`mayDelegate=false`. No live platform call, no upload, no credential
read, no secret read, no `.env` read, no network mutation, no commit,
no push, no task-state change, no foreign-worktree change performed.

## Snapshot immutability — preserved

The verified-snapshot immutability invariants from the prior batch are
preserved:

- `VerifiedEntry` keeps `nameBytes` and `payloadBytes` in private
  constructor parameters and exposes them only through
  `Array.copy` (defensive copy on every read) and a fresh
  `MemoryStream(payloadBytes, false)` ownership-transfer stream.
- `CandidateArchiveSnapshot` keeps `innerArchiveBytes`, `manifestBytes`,
  and `embeddedManifestBytes` in private constructor parameters and
  exposes them only through `Array.copy` accessors and fresh
  `MemoryStream` ownership-transfer streams.
- No public mutable byte array reference is exposed. Future tests
  preserve their expected immutability assertions.

## Defect 1 — `readRawTransport` digest-shape guard compared digest-string length to transport byte count

**Root cause** (`Handoff.fs` original line 906, now 956 after the fix):

```fsharp
elif int64 boundedBytes.Length <> evidence.ActualOuterDigest.Value.Length
     || isLowerHex64 evidence.ActualOuterDigest.Value |> not then
```

This expressed a shape-validation guard as a comparison between
`boundedBytes.Length` (an `int64` byte count: 1,652 / 1,661 / …) and
`evidence.ActualOuterDigest.Value.Length` (the literal count of hex
characters in the SHA-256 string: always 64 for any valid digest). For
every honest transport that observed `outerBytes.Length = 1652` and a
64-character hex digest, the comparison evaluated to `true` and the
function rejected the valid input as
`MalformedArtifact("candidate-artifact.zip",
"the evidence 'actualOuterDigest' is not a 64-character lowercase hex
SHA-256")`. The error message itself correctly described the intent
(validate digest SHAPE, not literal byte count) — but the predicate
was structurally wrong: it compared two values that live on completely
different scales (bytes vs hex characters), so the truth branch was
always taken.

The evidence recorded in `Handoff-tests.md` matches this:

> Outer sizes were 1,652 and 1,661 bytes respectively. The preparation
> receipt digest was distinct from the post-upload outer transport
> digest. Both production `ReleaseHandoff.readRawTransport` calls
> returned: `MalformedArtifact("candidate-artifact.zip", "the evidence
> 'actualOuterDigest' is not a 64-character lowercase hex SHA-256")`.

**Fix (production Handoff.fs):** keep the distinct invariants the
guard was trying to express, but use the right type of check on each
one:

- `evidence.TransportLength` must equal `boundedBytes.Length` — this
  protects against a platform-supplied length that disagrees with the
  byte count the publisher actually received. (The existing
  `IdentityMismatch("transportLength", …)` guard immediately above the
  defect preserved this invariant; it is unchanged.)
- `evidence.ActualOuterDigest.Value` must be a 64-character lowercase
  hex SHA-256 — this is the digest SHAPE check, expressed by
  `isLowerHex64`.

Removed:

```fsharp
elif int64 boundedBytes.Length <> evidence.ActualOuterDigest.Value.Length || isLowerHex64 evidence.ActualOuterDigest.Value |> not then
    // sanity guard — the evidence's claimed outer digest must be 64-hex
    Error(MalformedArtifact(rawOuter, "the evidence 'actualOuterDigest' is not a 64-character lowercase hex SHA-256"))
```

Replaced with:

```fsharp
elif not (isLowerHex64 evidence.ActualOuterDigest.Value) then
    // sanity guard — the evidence's claimed outer digest must be a
    // 64-character lowercase hex SHA-256. The transport byte count
    // (1652 / 1661 / …) is NOT compared against the digest string
    // length: they describe different shapes; comparing them would
    // reject every honest digest as a 64-vs-bytes mismatch.
    Error(MalformedArtifact(rawOuter, "the evidence 'actualOuterDigest' is not a 64-character lowercase hex SHA-256"))
```

A second pass then hashes the bounded bytes
(`sha256Hex boundedBytes`) and compares against
`evidence.ActualOuterDigest.Value`, returning
`IdentityMismatch("outerTransportSha256", …)` on a mismatch. That
honest transport-content check (separate from the shape guard) is
unchanged.

After the fix, an honest bounded ZIP whose outer length matches
`evidence.TransportLength` and whose digest is a 64-character lowercase
hex SHA-256 advances to the metadata cross-check and the bounded
standard-platform ZIP enumeration; only a malicious or malformed
evidence record fails the guard.

## Defect 2 — Outer allowlist derived from inner-payload `PublishedFiles` instead of the canonical outer set

**Root cause** (`Handoff.fs` original lines 873-883):

```fsharp
let private canonicalEntrySet (policy: ReleasePolicy.PolicyProjection) : Set<string> =
    let mutable all = Set.ofList policy.PublishedFiles
    all <- all.Add manifestEntryName
    all <- all.Add noticeEntryName
    all
```

This is the wrong policy for the OUTER transport layer. The outer
transport archive contains the inner producer ZIP and the standalone
manifest; `policy.PublishedFiles` (e.g., `"Mcp.Dotnet.deps.json"`,
`"Mcp.Dotnet.dll"`, `"Mcp.Dotnet.runtimeconfig.json"`,
`"FSharp.Core.dll"`) and `NOTICE.txt` belong INSIDE the inner producer
ZIP and are verified by `ReleaseCompare` against the canonical
`policy.PublishedFiles ∪ {"NOTICE.txt", "distribution.json"}` allowlist.
The outer allowlist mixing them in rejects every honest portable
candidate after the digest-shape guard is fixed (the defect-1
unblocking exposes this defect).

**Fix (production Handoff.fs):** the outer transport exact entry set
is exactly two entries derived from the canonical policy: the trusted
canonical inner producer ZIP name (`policy.ArchiveName`) and the
platform-side manifest entry (`"distribution.json"`). The inner-payload
allowlist (DLLs + `NOTICE.txt` + embedded `distribution.json`) is
delegated to `ReleaseCompare`, where the canonical policy is already
enforced against the inner producer ZIP. No overlap with
`ReleaseCompare`:

```fsharp
let private canonicalEntrySet (policy: ReleasePolicy.PolicyProjection) : Set<string> =
    Set.ofList [ policy.ArchiveName; manifestEntryName ]
```

After the fix, the canonical outer set for `componentId = "dotnet"`
is `{ "Mcp.Dotnet.zip", "distribution.json" }` (and analogously
`{ "Mcp.Workflow.zip", "distribution.json" }` for
`componentId = "workflow"`). Any honest portable candidate whose
outer archive contains exactly those two entries passes the allowlist;
extra / missing entries fail closed with `AllowlistMismatch(…)`; the
allowlist never includes the inner-payload file names.

The comment block above `canonicalEntrySet` is rewritten to make this
boundary explicit: the OUTER transport is exactly two entries, the
inner payload lives inside the inner ZIP and is verified by
`ReleaseCompare`, and the set is derived from the trusted canonical
policy rather than from any candidate-controlled field.

## Defect 3a — `TrustedPublisherReceipt` accepted an empty runtime triplet

**Root cause** (`Handoff.fs` original lines 416-429):

The prior parser required path / digest / size cross-consistency OR
the consistent empty triplet (all three empty). That is the wrong
policy for the trusted publisher channel: a trusted receipt that
omits runtime install evidence weakens the security boundary by
collapsing runtime attestation into a no-op whenever the trusted
builder skips it. The empty case must be reserved for the
preparation channel (which carries no runtime fields at all).

**Fix (production Handoff.fs):** the `TrustedPublisherReceipt` parser
now rejects an empty `runtimeFilePaths` outright and continues to
require that every path in `runtimeFilePathsValue` has a matching
entry in BOTH `runtimeFileDigestsValue` and `runtimeFileSizesValue`,
and that every entry in `runtimeFileDigestsValue` /
`runtimeFileSizesValue` corresponds to a runtime path:

```fsharp
if runtimePathsEmpty then
    Error(MalformedArtifact(sourcePath,
      $"the {trustedReceiptLabel} declares no runtimeFilePaths; the trusted publisher receipt MUST carry a non-empty cross-consistent runtime triplet"))
elif runtimeDigestsEmpty then
    Error(MalformedArtifact(sourcePath,
      $"the {trustedReceiptLabel} declares runtimeFilePaths but no runtimeFileDigests"))
elif runtimeSizesEmpty then
    Error(MalformedArtifact(sourcePath,
      $"the {trustedReceiptLabel} declares runtimeFilePaths but no runtimeFileSizes"))
else
    // … unchanged cross-consistency checks …
```

The four partial-combination branches in the prior implementation
collapsed into two rejection cases (paths-empty / digests-empty /
sizes-empty) plus the existing cross-consistency loop below. No
compatibility alias is retained.

The file-level doc comment for Channel A is updated to record the
freeze:

> Values MUST be a non-empty cross-consistent PATH / digest / size
> triplet (every path has a matching digest and size; no extra
> digests or sizes; no partial combinations). An empty triplet is
> reserved for the preparation channel, which carries no runtime
> fields at all.

## Defect 3b — `PreparationReceipt` carried circular pre-upload outer-transport expectations

**Root cause** (`Handoff.fs` original lines 549-651): the prior
`PreparationReceipt` record carried
`candidateOuterDigest: OuterDigest` and
`candidateOuterSize: int64`, parsed from JSON fields
`candidateOuterDigest` and `candidateOuterSize`. The platform outer
transport archive bytes are produced *after* `upload-artifact` wraps
the inner producer ZIP; the prep step that emits this receipt
operates strictly *before* the wrap happens and can never observe or
predict the resulting outer transport SHA-256 / size. A pre-upload
candidate receipt that asserts an outer transport digest is circular:
it claims a value whose authority it does not hold, and the
publisher would cross-check it against `ArtifactEvidence.ActualOuterDigest`
(a separate typed surface whose authority is the GitHub platform
metadata for the artifact upload) and likely fail the cross-check.
Pre-binding `candidateOuterDigest` to the platform is a copy that
the candidate can manufacture and a check that the candidate must
not be allowed to satisfy against itself.

The producer-side evidence contract is: prep attests only to what
it actually produced — the inner producer archive (the producer
just wrote the bytes) and the standalone manifest (also just
written). The platform outer archive is attested by
`ArtifactEvidence.ActualOuterDigest` /
`evidence.TransportLength`, a separate typed surface.

**Fix (production Handoff.fs):** the `PreparationReceipt` schema,
allowed-field set, parser, and section header are all updated.
The record replaces `CandidateOuterDigest : OuterDigest` and
`CandidateOuterSize : int64` with four producer-known fields:

```fsharp
type PreparationReceipt =
    { SchemaVersion: int
      InnerArchiveSha256: OuterDigest
      InnerArchiveSize: int64
      ManifestSha256: OuterDigest
      ManifestSize: int64
      SourcePath: string
      SourceSha256: string }
```

JSON shape (schemaVersion 1, all fields mandatory, the core parser
rejects unknown fields, missing fields, null fields, malformed JSON,
wrong schema version, non-64-hex digests, and negative sizes):

```json
{
  "schemaVersion": 1,
  "innerArchiveSha256": "<64-hex lowercase SHA-256>",
  "innerArchiveSize":   <bytes>,
  "manifestSha256":     "<64-hex lowercase SHA-256>",
  "manifestSize":       <bytes>
}
```

Allowed field names:

```fsharp
let private preparationReceiptAllowedFieldNames : Set<string> =
    Set.ofList
        [ "schemaVersion"
          "innerArchiveSha256"
          "innerArchiveSize"
          "manifestSha256"
          "manifestSize" ]
```

The unknown-fields rejection message is widened to document the
circular-field rejection explicitly:

> the preparation receipt contains unknown field(s): …; the
> preparation receipt carries NO runtime fields, NO toolchain values,
> and NO platform outer transport digest or size

`candidateOuterDigest` and `candidateOuterSize` are not in the
allowed-field set, so a candidate that emits the prior schema lands
in the unknown-fields branch and is rejected. No compat alias is
retained for the old fields. The parsed values are typed
`OuterDigest` (same private constructor as the trusted-channel
outer-digest type — `OuterDigest` is itself a generic transport
digest wrapper, here used for inner / manifest identities; the
publisher still cross-checks them against the publisher-observed
`InnerArchiveSha256` / `ManifestSha256` derived from the bounded
transport).

A new private helper `readRequiredInt64Field` provides the bounded
non-negative int64 parse for `innerArchiveSize` and `manifestSize`
so the size validation matches the existing int32 helper for sizes.

The section-header comment is rewritten:

> The PreparationReceipt is what the candidate writes into the
> candidate directory; it carries ONLY the producer-known prep-time
> identity of the inner producer archive (SHA-256 + size) and the
> standalone manifest (SHA-256 + size). These values are known at
> the prep step BEFORE any outer transport wrapper exists.
>
> The platform outer transport digest and size NEVER appear here.
> The outer archive bytes are produced after upload-artifact wraps
> the inner ZIP; the prep step can NOT predict them. A pre-upload
> candidate receipt that claims an outer transport digest / size is
> circular (claims a value whose authority the candidate does not
> hold) and is rejected as such — the platform outer digest is
> computed independently from `ArtifactEvidence.ActualOuterDigest`.

`readPreparationReceiptFromFile` itself is unchanged at the function
boundary: it still reads the bytes, fills `SourceSha256`, and
delegates to `parsePreparationReceipt`. The new JSON schema is the
only contract that changes.

## Interface delta summary

The new production contract (no compat aliases, no env var shims):

| Surface                    | Before                                                  | After (this batch)                                                    |
|----------------------------|---------------------------------------------------------|-----------------------------------------------------------------------|
| `readRawTransport`         | digest-shape guard compared 64-char hex length to bytes | digest-shape guard validates hex shape only                           |
| `canonicalEntrySet`        | `policy.PublishedFiles ∪ {manifest, NOTICE}`            | `{policy.ArchiveName, manifestEntryName}`                             |
| `parseTrustedPublisherReceipt` runtime triplet | empty triplet accepted                    | empty paths / digests / sizes rejected; cross-consistency required     |
| `type PreparationReceipt`   | `CandidateOuterDigest` / `CandidateOuterSize`           | `InnerArchiveSha256` / `InnerArchiveSize` / `ManifestSha256` / `ManifestSize` |
| `parsePreparationReceipt` allowed fields | 3 (`schemaVersion`, `candidateOuterDigest`, `candidateOuterSize`) | 5 (`schemaVersion`, `innerArchiveSha256`, `innerArchiveSize`, `manifestSha256`, `manifestSize`) |
| `parsePreparationReceipt` unknown-fields message | `… NO runtime fields and NO toolchain values` | `… NO runtime fields, NO toolchain values, and NO platform outer transport digest or size` |

Every typed field, every parser, every unknown-fields rejection still
lives in `Handoff.fs`; the only entry-point signatures
(`readRawTransport`, `parseTrustedPublisherReceipt`,
`readTrustedPublisherReceiptFromFile`, `parsePreparationReceipt`,
`readPreparationReceiptFromFile`,
`renderTrustedPublisherReceipt`,
`writeTrustedPublisherReceiptToFile`) keep the same names and arities.
A future caller that consumed the prior `PreparationReceipt` fields
must update to the new four-field shape; the dedicated tester batch
owns the test-file update to the frozen schema.

## Build run ID (this batch)

- After the three fixes (`Handoff.fs` digest-shape guard, canonical
  entry-set, trusted runtime triplet rejection,
  `PreparationReceipt` field redesign), the file-level doc rewrite,
  and the orphan-constant documentation comment:
  build ID `KpKKA9pUR7HwB--gl-WCB3sADwfxYgIS7CMNUpNkl-4` via the
  dedicated `dotnet_build` MCP tool
  (`dotnet build infrastructure/release/Release.fsproj -c Release`;
  `noRestore=true` to avoid the slow NuGet re-resolution that produces
  noisy unrelated NETSDK1057 preview messages on `net11.0`).
  **0 errors, 0 warnings**.
- Earlier milestone of the same source (immediately after the three
  primary fixes, before the orphan-constant comment): build ID
  `C2BqEEtZHPNdaS94vq-cMVtcfNLHwNVKrJCEpY9sdEc`. **0 errors,
  0 warnings**.

The dependent `infrastructure/release/tests/Tests.fsproj` is **not**
expected to build in this batch — `mayTest=false`. The dedicated
tester batch owns the migration to the frozen new schema (positive
inner-archive digest cross-check + positive inner-payload DLL+NOTICE
+ positive manifest-shape parse + negative per-spec-field rejection +
negative unknown-fields rejection + negative circular-fields
rejection + negative diff-length / extra / missing outer entry sets
+ negative empty-runtime-triplet rejection on the trusted channel +
negative cross-consistent-but-conflicting size rejection + defensive
copy / stream immutability on every snapshot accessor).

## Validation performed

- `dotnet build infrastructure/release/Release.fsproj -c Release` via
  the dedicated `dotnet_build` MCP tool: succeeded with 0 errors and
  0 warnings at the run ID above.
- No live platform call, no upload, no commit, no push, no
  task-state mutation, no foreign-worktree change performed.
- No `Release.fsproj` `PackageReference` or `ProjectReference` was
  added.
- `Policy.fs`, `Runner.fs`, `Compare.fs`, `Evaluate.fs`,
  `Transaction.fs`, `Orchestration.fs`, `Archive.fs`, `Metadata.fs`,
  `Publish.fs`, `Capture.fs`, `Enrollment.fs`,
  `PublisherClosure.fs`, `Bootstrap.ps1`, `Bootstrap.fsx`,
  `Sources.json`, `.github/workflows/release.yml`, the existing
  `tests/*.fs` files (already broken by the prior batch's
  `orchestrate` removal; the dedicated tester batch owns the
  migration), every producer `Config.fsx`, every Build / Pins
  script, the Telegram surface, the producer-package scripts, and
  every unrelated producer / consumer / test file were NOT touched.
- No `dotnet test`, no `ReleaseRunner` invocation, no FSI host
  spawn, no `.env` / secret / credential read, no network call, no
  state mutation, no commit was performed.

## Open interface facts (BLOCKED on workflow / producer inputs) — unchanged from the prior batch

1. **Trusted builder job that emits `TrustedPublisherReceipt`** to a
   separately supplied path — the future workflow adapter supplies the
   path; the publisher reads it via
   `readTrustedPublisherReceiptFromFile`. Until DevOps wires that path,
   the `publish` entrypoint fails closed; no default / placeholder /
   zero fallback is invented.
2. **Preparation-receipt emission by the candidate stage** — the
   future workflow adapter supplies the path inside the candidate
   directory; the publisher reads it via
   `readPreparationReceiptFromFile`. Until DevOps wires that path,
   `readRawTransport` itself does not consume a preparation receipt;
   the publisher is the only party that may cross-check it, and the
   frozen new schema (`innerArchiveSha256` / `innerArchiveSize` /
   `manifestSha256` / `manifestSize`) is what cross-check will use.
3. **`ReleaseEvaluate.VerifiedCandidate.create` + `VerifiedCandidate`
   refactor** — out of this batch's compile scope. The current
   `VerifiedCandidate` continues to expose mutable `ArchiveBytes` and
   `ManifestBytes` directly; the future adapter wiring replaces the
   constructor with a typed snapshot derived from
   `CandidateArchiveSnapshot` so the `byte[]` exposure is closed.
4. **`ReleaseEnrollment` cross-check against the trusted receipt's
   `componentConfigPaths` and `componentConfigShas`** — out of this
   batch's compile scope.
5. **HEAD recheck at publication time** — out of this batch's compile
   scope.
6. **`ReleaseCompare` standalone/embedded manifest byte equality
   cross-check** — out of this batch's compile scope.

These are unchanged from `Handoff-core.md`. None of them are inferred
or invented; none of them are required for this batch's compile to
succeed.
