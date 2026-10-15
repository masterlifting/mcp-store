# Published-baseline historical fixture migration

## REPO-authored descriptor (canonical pin source)

`infrastructure/release/tests/Fixtures.json` is now the single canonical pin
table for the historical gate. CI emits copies of this descriptor into the
caller-supplied HistoricalRoot; the local `ghi24-current-fixtures/` and
`ghi24-fixtures/` roots below only carry byte-pinned archives plus a generated
copy of this same descriptor. No second authoring location exists.

| Field | Bytes | SHA-256 (lowercase) |
| --- | --- | --- |
| `Fixtures.json` | 843 | `0f32e8c1cc1c1adde4ae4e804f4f0529f49b6d8c151dfa9079cf9f9ff5e6762a` |

The descriptor contains exactly two entries and seven required fields per entry:
`assetUri`, `archiveFile`, `assetName`, `componentId`, `sha256`, `size`,
`verifiedManifestRevision`. No `version` field is encoded; the published version
is carried by `assetName` only, which keeps the descriptor free of any SDK or
producer-version duplication.

## Fresh root (new local HistoricalRoot, archives-only)

`C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures`

Three files live in the new root. The descriptor is a byte-identical copy of
the REPO-authored `Fixtures.json`; the two archives are byte-identical copies of
already-approved sources. No download, network, or remote interaction was
performed; no `.env` or secret value was read.

| File | Bytes | SHA-256 (lowercase) |
| --- | --- | --- |
| `Dotnet.zip` | 1,289,835 | `da3acb20007f8015e541ee7d68b48adf49d232c4eea41190a62073073d1a1614` |
| `Workflow.zip` | 1,514,420 | `65d274056951c64d4eafef2ed7242f2cf02005e639d91f2a2a9b85b921e53313` |
| `Fixtures.json` | 843 | `0f32e8c1cc1c1adde4ae4e804f4f0529f49b6d8c151dfa9079cf9f9ff5e6762a` |

The earlier `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-fixtures` root is
preserved verbatim (its `Fixtures.json` predates the REPO-authored descriptor
and is no longer used by `loadDescriptor`); both carry the same three files
and the same hashes today.

## Pre/post copy verification

Both archives were verified before copy and again after copy using
`Get-FileHash -Algorithm SHA256`. The on-disk hashes after copy match the
descriptor and the published asset values exactly.

- `Dotnet.zip` source: `C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap/dotnet-v1.0.3.zip` (already approved historical root, size 1,289,835, SHA `da3acb20007f8015e541ee7d68b48adf49d232c4eea41190a62073073d1a1614`).
- `Workflow.zip` source: `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-baseline/workflow.zip` (verified v4 file, size 1,514,420, SHA `65d274056951c64d4eafef2ed7242f2cf02005e639d91f2a2a9b85b921e53313`).
- `Fixtures.json` source: `D:/repos/private/mcp-store/infrastructure/release/tests/Fixtures.json` (REPO-authored; copied byte-identical into both `ghi24-fixtures/` and `ghi24-current-fixtures/`).

After copy to the new `ghi24-current-fixtures/` root, the three files re-hashed
to the values above. The user-owned historical source files at
`ghi-24-bootstrap/` and the verified `ghi24-baseline/workflow.zip` were not
modified; the older `ghi24-fixtures/` root is preserved untouched.

## Descriptor schema

`Fixtures.json` is a small pinned-data descriptor; it is **not** canonical
current producer policy. The current canonical release versions remain
`Dotnet 1.0.4` / `Workflow 1.0.5` with no edits. The descriptor exists only to
let the historical regression gate and the separate DevOps workflow
provisioning consume the same path/SHA/size pin table.

Schema (top-level object, exactly one `fixtures` array of exactly two
entries):

- `schemaVersion` (number, required, must equal `1`)
- `fixtures` (array, required, length exactly `2`)
  - `componentId` (string, required) — canonical component tag.
  - `archiveFile` (string, required) — basename of the local archive under
    HistoricalRoot; no path components, no `..`, not rooted.
  - `assetName` (string, required) — published asset name
    (`v1.0.3.zip` / `v1.0.4.zip`).
  - `assetUri` (string, required) — fixed component tag URI of the published
    asset (`https://github.com/masterlifting/mcp-store/releases/download/<componentId>/<assetName>`).
  - `sha256` (string, required) — 64 lowercase hex chars.
  - `size` (integer, required) — positive byte count.
  - `verifiedManifestRevision` (string, required) — 40 lowercase hex chars,
    extracted from each archive's `distribution.json` `revision` field.

The test enforces: schema-version match, exact two entries, archiveFile
basename containment, no duplicate `componentId`/`sha256`, assetUri contains
the fixed component tag and ends with the asset name, sha256 is lowercase hex,
and the now-required `verifiedManifestRevision` is 40 lowercase hex. Equal
archive `size` values across the two entries are legitimate (not an auth
indicator) and are not treated as a duplicate constraint.

## Current descriptor contents

`dotnet`:

- `archiveFile`: `Dotnet.zip`
- `assetName`: `v1.0.3.zip`
- `assetUri`: `https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.3.zip`
- `sha256`: `da3acb20007f8015e541ee7d68b48adf49d232c4eea41190a62073073d1a1614`
- `size`: `1289835`
- `verifiedManifestRevision`: `fc0aefa9db0fb3262ee6ca1254bd6bbb752b8b01` (read
  from `Dotnet.zip`'s `distribution.json` `revision` field by extracting the
  zip in-process; no hardcoded fallback used by `loadDescriptor`).

`workflow`:

- `archiveFile`: `Workflow.zip`
- `assetName`: `v1.0.4.zip`
- `assetUri`: `https://github.com/masterlifting/mcp-store/releases/download/workflow/v1.0.4.zip`
- `sha256`: `65d274056951c64d4eafef2ed7242f2cf02005e639d91f2a2a9b85b921e53313`
- `size`: `1514420`
- `verifiedManifestRevision`: `e66422f0ad67bbd73cf2f5b633c141ef5b2a09c1` (read
  from `Workflow.zip`'s `distribution.json` `revision` field; matches the
  `origin/main` HEAD observed in the prior `inspect-published.md` baseline
  evidence. ACTUAL PE `AssemblyInformationalVersion` is the 40-hex
  `1.0.0+e66422f0ad67bbd73cf2f5b633c141ef5b2a09c1` — this is the
  manifest/Git-SHA string, not a 64-hex PE-derived value; no erroneous
  64-hex PE assertion is added).

## `infrastructure/release/tests/Historical.fs` changes

Single file edited, no new `.fs` module added, no Fixture.fs change (the
descriptor reader is consumed only by Historical.fs, so no shared reader was
needed).

- Added: `repoRoot` (standard `Path.GetFullPath` / `__SOURCE_DIRECTORY__`
  helper) and `repoDescriptorPath` pointing at
  `infrastructure/release/tests/Fixtures.json`. `loadDescriptor` now resolves
  the descriptor from this REPO-authored path; `MCP_RELEASE_HISTORICAL_ROOT`
  only supplies the archive-bearing directory, never a self-authored
  descriptor override.
- `FixtureEntry.VerifiedManifestRevision` changed from `string option` to
  `string`; the parser rejects any entry missing the field.
- The legacy hardcoded revision fallback
  (`"fc0aefa9db0fb3262ee6ca1254bd6bbb752b8b01"`) in
  `historicalSameArchiveIdentical` is removed; the same-archive compare now
  uses `dotnetEntry.VerifiedManifestRevision` directly.
- `historicalDescriptorSchema` always validates `VerifiedManifestRevision`
  is 40 lowercase hex (no `Some/None` branch).
- Duplicate detection now treats `componentId` and `sha256` only as auth
  indicators; the prior `size` uniqueness check is removed because equal
  archive sizes are legitimate accidental comparison (not an auth indicator).
- The dotnet `dotnet-v1.0.3.zip` / `workflow-v1.0.3.zip` filename constants
  and their hardcoded SHA/size pin values are removed; path/SHA/size come
  from `Fixtures.json` only (single pin table).
- New `loadDescriptor ()` parses `Fixtures.json` with the schema and
  duplicate checks above, and returns `Result<FixtureEntry list, string>`.
- New `historicalDescriptorSchema ()` replaces the old
  `historicalHashPinning ()` and verifies per-entry size and SHA-256 against
  the on-disk archive, plus per-entry schema/format assertions.
- `historicalSameArchiveIdentical ()` and
  `historicalBumpedCandidateDifferent ()` now resolve the dotnet archive
  through the descriptor and use the descriptor's `VerifiedManifestRevision`
  for the same-archive compare (the bumped-candidate compare still uses the
  synthetic bumped revision `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`).
- `consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent ()` is
  unchanged; it is synthetic and does not read a real archive.
- No alias reader for the old `dotnet-v1.0.3.zip` / `workflow-v1.0.3.zip`
  filenames; the obsolete `workflow-v1.0.3.zip` prerequisite is removed.

## Preserved case mapping

| Original scenario | New behaviour | Same-archive / changed / revision-only |
| --- | --- | --- |
| `historicalHashPinning` (dotnet + workflow) | `historicalDescriptorSchema` reads SHA/size from the REPO-authored `Fixtures.json` and verifies on-disk files | descriptor is the single pin table; old hardcoded constants are gone |
| `historicalSameArchiveIdentical` (dotnet v1.0.3, byte-identical) | dotnet path from descriptor; contract `Version=1.0.3` and `ArchiveName` from descriptor `assetName`; manifest revision from descriptor `verifiedManifestRevision` (no hardcoded fallback) | same `ByteIdentical` decision, same contract allowlist |
| `historicalBumpedCandidateDifferent` (dotnet v1.0.3 + bogus DLL) | dotnet path from descriptor; contract `ArchiveName` from descriptor `assetName`; bumped revision `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1` still hardcoded as the candidate revision | same `DifferentBeyondRevision` decision, same DLL/manifest payload diff |
| `consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent` | unchanged synthetic two-archive scenario | same `RevisionOnlyEquivalent` decision over synthetic archives |

The strict DLL/ZIP metadata checks in `infrastructure/release/Compare.fs`
are untouched: the Git-SHA-stamped `Mcp.Workflow.dll`
`1.0.0+e66422f0ad67bbd73cf2f5b633c141ef5b2a09c1` payload is **not**
normalized, and the revision-only exception still admits only the embedded
manifest `revision` value. The descriptor does not encode a 64-hex
PE-derived string; the verified manifest revision is the 40-hex Git commit
and matches the existing DLL `InformationalVersion` blob.

## Source diff (Historical.fs only)

Net changes summarized:

- Removed: hardcoded `bootstrapDir`/`dotnetArchive103Path`/`workflowArchive103Path`,
  hardcoded `historicalHashPinning` and its embedded SHA/size constants
  (`da3acb…`, `8677…`, `1289835`, `1482675`), and any reference to
  `workflow-v1.0.3.zip`.
- Removed: `descriptorPath` derived from `MCP_RELEASE_HISTORICAL_ROOT`
  (descriptor no longer read from the HistoricalRoot input).
- Removed: the legacy hardcoded revision fallback
  `"fc0aefa9db0fb3262ee6ca1254bd6bbb752b8b01"` in
  `historicalSameArchiveIdentical`.
- Removed: the `sizeDups` duplicate-detector entry (equal archive sizes are
  legitimate, not an auth indicator).
- Added: `repoRoot` and `repoDescriptorPath` REPO-authored descriptor
  resolver; `FixtureEntry.VerifiedManifestRevision` is now `string`
  (required, not optional); `loadDescriptor` rejects any missing
  `verifiedManifestRevision` and any non-40-lowercase-hex `verifiedManifestRevision`;
  `historicalDescriptorSchema` enforces the now-required field; and the
  same-archive compare uses `dotnetEntry.VerifiedManifestRevision` directly.
- Unchanged: `observe`, `writeTempCopy`, `writeModifiedV104LikeArchive`,
  `consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent`,
  `runSuite`, and the outer `tests` value.

No Fixture.fs edits. No new `.fs` modules. No Suite.ps1, release.yml,
Compare.fs, Metadata.fs, or producer/canonical-config edits. No version
bumps. No commits, pushes, or state transitions.

## Build/test status

Source diff only. Per assignment: `mayBuild=false`, `mayTest=false`,
`mayDelegate=false`. Actual test execution waits for the engineer to
rebuild the test project; the suite inventory and case-count guard are
unchanged (one `Historical` test case in the `testSequenced` roster).

## DevOps follow-up

DevOps updates workflow provisioning separately using the same
`Fixtures.json` descriptor at
`D:/repos/private/mcp-store/infrastructure/release/tests/Fixtures.json`
(REPO-authored). CI copies the REPO descriptor into the caller-supplied
HistoricalRoot before `loadDescriptor` runs. The descriptor is the single
source of truth for path/SHA/size so the test and provisioning cannot drift.
