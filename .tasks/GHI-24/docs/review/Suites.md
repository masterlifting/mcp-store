# SEC1 Handoff core test evidence

Status: blocked; the 2 canonical producer positives and symlink regression pass.
The expanded 101-case core batch has 5 independent failures: three uppercase
hex inputs, an unpaired runtime size, and a standalone/embedded manifest
mismatch are accepted. This is not an approval or a full-suite result.

## Earlier pre-fix verification (historical)

- Added only `infrastructure/release/tests/Handoff.fs`. It remains unregistered;
  the existing test project migration is pending and no project/Main changes were
  made.
- A temporary FSI host at
  `C:\Users\andre\AppData\Local\Temp\opencode\Handoff.fsx` loaded the test
  source and referenced the existing production
  `infrastructure/release/bin/Release/net11.0/ReleaseRunner.dll` directly.
  No build, production edit, network access, credential access, or live API call
  was performed. The DLL was 487,936 bytes, SHA-256
  `6900b40322304619184a69736b55ee8dedebea6251bb4f1bafdaf43eaa01fa70`.
- The supplied build run ID was unavailable to the retained Dotnet evidence tool
  (`UNKNOWN_RUN_ID`); no replacement build was run. The inner ZIP fixture was
  generated from both current canonical projections and validated by the actual
  production comparison functions; `Orchestration.prepare` itself was not run.
- Command: `dotnet fsi --nologo <approved-temp>\Handoff.fsx`.
- Result: 16 cases; 14 passed, 2 failed, 0 ignored, 0 errored. The 14 existing
  `ReleasePolicy.parseBytes` cases passed. Both new handoff cases first validated
  their canonical policy and the nested producer ZIP using production
  `ReleaseCompare.observeBytes` / `ReleaseCompare.validate`.

## Earlier digest-shape failure (fixed by Handoff-fix)

The Dotnet and Workflow positive portable-candidate cases both passed an outer
standard ZIP containing `<policy.ArchiveName>`, `distribution.json`, and an
untrusted `receipt.json`. Their inner producer ZIPs contained policy-published
files, `NOTICE.txt`, and the embedded manifest. Outer sizes were 1,652 and 1,661
bytes respectively. The preparation receipt digest was distinct from the
post-upload outer transport digest.

Both production `ReleaseHandoff.readRawTransport` calls returned:

```text
MalformedArtifact("candidate-artifact.zip",
  "the evidence 'actualOuterDigest' is not a 64-character lowercase hex SHA-256")
```

Root cause: `infrastructure/release/Handoff.fs:906` compares
`int64 boundedBytes.Length` with `evidence.ActualOuterDigest.Value.Length`. For
the observed inputs that compares 1,652/1,661 with 64 and rejects the valid
digest before hashing or ZIP enumeration. The guard should validate digest shape,
not compare transport byte count to digest-string length. **Owner: engineer /
production Handoff core.** No test was altered to accommodate this defect.

## Earlier source-level conflicts (fixed or superseded)

- `Handoff.fs` previously derived the outer allowlist from inner payload files;
  Handoff-fix changes it to the exact two-entry outer set.
- `Handoff.fs` previously accepted an empty trusted runtime triplet;
  Handoff-fix makes the trusted runtime closure mandatory. This parser has not
  yet been exercised by the current test run.
- The old preparation receipt carried circular outer-digest fields;
  Handoff-fix replaces those with producer-known inner-archive and manifest
  digests and sizes. The current positive test parses those new fields.
- `readRawTransport` currently does not consume/cross-check a preparation
  receipt. The non-circular platform outer digest must remain independently
  observed; a pre-upload candidate receipt cannot supply that digest.

## Prior production build verification (historical)

- Production build run ID: `KpKKA9pUR7HwB--gl-WCB3sADwfxYgIS7CMNUpNkl-4`.
  Retained `dotnet.details` output reports `ReleaseRunner.dll` built with 0
  warnings and 0 errors. The referenced DLL is
  `infrastructure/release/bin/Release/net11.0/ReleaseRunner.dll`, 489,472 bytes,
  SHA-256 `a7082df066e58d79482c95ec84fb34614dbeaf25c1678dd5b360f6badb317cc`.
- Command: `dotnet fsi --nologo
  C:\Users\andre\AppData\Local\Temp\opencode\Handoff.fsx`. The standalone
  FSI harness references that built DLL and Expecto directly; no build ran.
- Result: 2 cases, 0 passed, 2 failed, 0 ignored, 0 errored. Both canonical
  `dotnet` and `workflow` outer fixtures contain exactly
  `{ <policy.ArchiveName>, distribution.json }`. Their producer ZIPs passed
  production `ReleaseCompare.observeBytes` / `ReleaseCompare.validate`; the
  embedded manifest matched the standalone bytes; the typed preparation
  receipt's inner and manifest hashes/sizes matched the independently generated
  fixture values. Both `readRawTransport` calls returned a snapshot, and its
  outer digest and size assertions passed.
- Both cases then fail at `tests/Handoff.fs:176`: expected
  `snapshot.InnerArchiveSha256` to equal the producer ZIP SHA-256, but it equals
  the outer transport ZIP SHA-256. `Handoff.fs:1080-1085` confirms the source
  computes `innerSha` from `boundedBytes`, stores `Array.copy boundedBytes` as
  `innerArchiveBytes`, and uses the outer byte length for the inner size. Thus
  the two-entry shape and outer digest checks now pass, but the snapshot labels
  the outer wrapper as the inner archive. This is the bounded production
  blocker; no fixture was changed to accommodate it.
- Per the positive-first gate, no negative cases were added or run after these
  positive failures. The defensive-copy and stream assertions exist after the
  failing assertion and therefore did not execute. Trusted populated/empty/
  partial runtime-receipt cases, preparation-receipt malformed/legacy-field
  negatives, metadata mismatches, outer archive malformed/path/duplicate/limit
  cases, digest/length mismatches, and reflection/type-distinctness checks all
  remain unimplemented and unverified.
- The `PreparationReceipt.InnerArchiveSha256` record field is declared as
  `OuterDigest` in `Handoff.fs:599-605`. The positive test checks the digest
  value only and does not claim that the field's type enforces inner/outer
  semantic identity. The test helper constructs an `OuterDigest` for platform
  evidence through F# reflection because the private wrapper has no public
  constructor; no semantic type guarantee is inferred from that helper.
- `tests/Handoff.fs` remains unregistered. No `Tests.fsproj` or `Main.fs` changes
  were made, and the complete migrated Expecto suite was not run. No production
  source, build, live API, credential, secret, environment, or network state was
  changed or accessed.

## Fresh positive gate and symbolic-link blocker

- The assigned production build run ID was checked with the retained Dotnet
  evidence tool: `ZSg0-n35sDNS9Oh6F6VLazpWozENo-6JWhPk_By5MGk`; it reports 0
  errors and 0 warnings. The existing referenced `ReleaseRunner.dll` is 490,496
  bytes, SHA-256
  `814ace84d578ac11dbac391279c8e665ba09d1563946a0df50492fa55136e4f4`.
- Before adding any negative cases, command
  `dotnet fsi --nologo C:\Users\andre\AppData\Local\Temp\opencode\Handoff.fsx`
  ran the two canonical portable positives: 2 passed, 0 failed, 0 errored.
  Both cases assert preparation-receipt inner and manifest digests/sizes,
  canonical inner validation, embedded/standalone manifest agreement, verified
  outer digest/size, inner digest/size and bytes, and byte streams.
- After that gate passed, one negative regression was added for a ZIP entry
  carrying Unix symlink file-type bits (`ExternalAttributes` file type `0xA000`)
  on the canonical inner-archive entry. The test first reopens the generated ZIP
  and asserts the central directory retains those symlink bits. The same command
  then ran 3 cases: 2 positives passed and the symlink-rejection case failed
  because `ReleaseHandoff.readRawTransport` returned `Ok` for the marked entry.
  Result: 3 run, 2 passed, 1 failed, 0 ignored, 0 errored.
- Bounded production evidence: `Handoff.fs:992-1003` validates entry name
  safety, duplicate names, and sizes but does not inspect the ZIP entry's
  `ExternalAttributes`; `Handoff.fs:1021-1037` snapshots accepted entry bytes.
  The outer ZIP had the required canonical two-entry set, matching metadata,
  exact length, and actual SHA-256, so the reader accepted and snapshotted the
  symlink-marked canonical entry. **Owner: engineer / production Handoff core.**
  This assignment is test-only; production remediation is not included here.
- Stopped at the first new production behavior gap per the handoff. No further
  negative cases were added or run. Trusted receipt completeness/malformed
  variants, preparation-receipt malformed/forbidden fields, metadata mismatch,
  digest/length mismatch, unsafe paths, duplicate/extra/missing entries, archive
  limits, embedded/standalone manifest mismatch, and defensive-copy mutation,
  read-only streams, or backing-array exposure remain unverified. The positive
  content/stream assertions do not prove those omitted mutation properties.
- No build was run; no project registration, production source, live API,
  credentials, secrets, network, or workflow state was changed or accessed.

## Continued bounded core verification

- The three existing cases were rerun first with
  `dotnet fsi --nologo C:\Users\andre\AppData\Local\Temp\opencode\Handoff.fsx
  --sequenced --no-spinner`: 3 passed, 0 failed, 0 errored. The two existing
  producer positives and the existing symlink test were not changed.
- Added receipt strictness, all eight observed-metadata mismatches, outer digest
  and length failures, entry-set/path/attribute negatives, bounded count/size/
  ratio/total-limit fixtures, snapshot copy/stream exposure checks, and the
  standalone/embedded manifest mismatch case. The total-limit fixture mutates
  only central-directory declarations and keeps the transport small; no large
  decompressed buffer is allocated.
- The same FSI command ran the entire focused batch: **101 run, 96 passed, 5
  failed, 0 ignored, 0 errored**. All cases ran despite the independent
  failures. The total-expansion fixture was corrected after an initial fixture
  encoding mismatch; it now reaches and verifies the production total-size
  guard.
- Production gaps observed (inputs / observations / expected result):
  - `Handoff.fs:126-142` (`isLowerHex40` / `isLowerHex64`) accepts uppercase
    hex because it only calls `Uri.IsHexDigit`. Trusted `eventRevision="A" * 40`
    and `componentConfigShas.dotnet="A" * 64`, plus preparation
    `manifestSha256="A" * 64`, each parse as `Ok`; each must be rejected as
    non-lowercase. These are three failing cases for one lowercase-validation
    gap.
  - `Handoff.fs:450-464` cross-checks every runtime path and digest but never
    checks size-map keys in the reverse direction. A valid `dotnet.exe`
    triplet plus `runtimeFileSizes.extra.dll=7` parses as `Ok`; it must be
    rejected as an extra partial runtime entry.
  - `Handoff.fs:1147-1164` snapshots standalone and embedded manifests without
    comparing them. Appending one space to the valid standalone JSON yields
    distinct SHA-256 values (`6d43a9e795c41e7bd34066b3b79d0a8b00ec99fca0e9df482f6f20e19f3e3879`
    vs `90034ef422c4fe81a19d2d69e4a28c02bd8ef379e578a6382b16099def4aa954`),
    yet `readRawTransport` returns `Ok`; it must reject before transaction.
- Other independent negative inputs passed, including all eight identity
  fields, malformed digest / actual hash / transport length, missing/extra/
  duplicate/case-colliding entries, absolute/UNC/drive/ADS/backslash/dot paths,
  DOS reparse and Unix non-regular types, and the 4097-entry, per-entry,
  expansion-ratio, and total-uncompressed caps. Copy mutation, read-only
  streams, `TryGetBuffer`, and later streams also passed. The >256 MiB outer
  transport-byte cap was not allocated/tested.
- Retained Dotnet details for the supplied run ID
  `buildxAU3G_ZB5dYdQg_K4SCEmjhgnNt1u-lIejnRPjakoNk0` returned
  `UNKNOWN_RUN_ID`; no build was run. The FSI host references the existing
  `infrastructure/release/bin/Release/net11.0/ReleaseRunner.dll` (492,544
  bytes, SHA-256
  `983cd3e12b59052198e1e5041bc71098e0b6c8b5a22fa46fa7412340bf644cb7`). The
  supplied run's provenance therefore remains unverified.
- No production file, `Main.fs`, project file, pipeline caller, or temporary
  harness was changed; the test source remains unregistered. No transaction,
  live API, credential, secret, or network operation was invoked.

## Fresh verification after production fixes

- Retained Dotnet build run `NPuiaGhaRjKpUlTB5SreTT1wIykm85XzyNrSYK4MKHI`
  was verified immediately with `dotnet.details`: build succeeded, 0 warnings,
  0 errors. The FSI-referenced `ReleaseRunner.dll` is 494,080 bytes, SHA-256
  `865abfd4256578f57646ca092991851e08e5bbec52b0e0465e8535c3efcbce92`.
- Ran the unchanged, approved FSI harness against that production DLL and the
  canonical `infrastructure/release/tests/Handoff.fs` source:
  `dotnet fsi --nologo C:\Users\andre\AppData\Local\Temp\opencode\Handoff.fsx --sequenced --no-spinner`.
  Result: **101 run, 101 passed, 0 failed, 0 ignored, 0 errored** (process
  completed successfully). This includes both canonical producer positives and
  the full negative/mutation batch; no cases were excluded and no assertions or
  fixtures were weakened.
- No build was run by the tester. The >256 MiB transport-byte-cap case remains
  unexecuted; no oversized allocation was made. The modern caller and full
  Expecto-suite migration, CI integration, and enabled publishing remain pending.
  This focused FSI result does not claim those follow-ups are complete.
