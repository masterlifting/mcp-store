# Trusted publisher installation — three-member transport

Factual record for the internal cutover in `Boundary.md` ("Final internal
transport cutover"). Files changed: `infrastructure/release/Build.ps1` and
`infrastructure/release/Bootstrap.ps1`. Claims no approval.

## Builder (`Build.ps1 -Phase Emit`)

The nested two-member transport ZIP is removed, not aliased. Emit authors the
three members the platform uploads directly and emits them independently:

- files: `publisher-runtime.zip`, `runtime-manifest.json`, `receipt.json`
  (`-EnvelopeName` / `-ManifestName` / `-TrustedReceiptName`).
- `runtime-manifest.json` covers runtime files only; it hashes neither itself,
  the envelope nor the receipt.
- `receipt.json` is written after the manifest and envelope (it carries no
  self, envelope or outer hash).
- outputs: `envelopeName`, `manifestName`, `trustedReceiptName`,
  `envelopeSha256`, `manifestSha256`, `receiptSha256`, `expectedOuterEntries`
  (exactly three names), `envelopePath`, `manifestPath`, `trustedReceiptPath`.
  `outerTransportPath` is gone with the wrapper it named.
- `receiptSha256` is the independent expectation the installation authenticates
  uploaded receipt bytes against; the platform outer digest stays unknown until
  upload and is never predicted or conflated with an inner hash.

## Installation (`Bootstrap.ps1 -Mode InstallRuntime`)

New required inputs, rejected in `FetchCandidate` instead of ignored:
`-TrustedReceiptName`, `-TrustedReceiptSha256` (bare lowercase 64-hex),
`-TrustedReceiptPath`. `-ExpectedOuterEntries` must declare exactly the three
direct members, the trusted receipt name must be one of them, and the three
names must be distinct.

Order, all fail-closed before any install or copy:

1. root/mode/role/argument checks (`Resolve-RoleBinding` unchanged: `publisher`
   and `runtime` remain the `InstallRuntime` roles, `candidate` stays
   `FetchCandidate`);
2. pairwise-disjoint fresh-leaf guards for every destination
   (`Assert-FreshProtectedDestination`, one implementation behind
   `Assert-RawTransportDestination` and `Assert-TrustedReceiptDestination`),
   before the install root is created and before any token read or API call;
3. attempt-scoped artifact/run/job binding and the platform digest, matched to
   the same streamed bytes;
4. outer archive bounds, paths, links, case collisions and exact membership;
5. extracted envelope hash against `-EnvelopeSha256`, manifest against
   `-ManifestSha256`, and receipt bytes against `-TrustedReceiptSha256`;
6. runtime manifest is not permitted to declare the receipt member;
7. runtime install from the envelope only, checked against the manifest set and
   per-file hash/size;
8. the authenticated receipt is copied last to its own protected leaf through a
   same-directory temp and rename, re-hashed after copy, and removed on failure
   only when this run created it.

The bootstrap transports receipt bytes; it never parses publisher policy. The
receipt stays outside the install root, so the runtime cannot authenticate
itself (no circularity).

## Not changed / open

- Bootstrap receipt schema stays `schemaVersion 2` with its existing root keys;
  the trusted-receipt evidence lives inside the existing `envelope` subobject.
- `FetchCandidate` keeps raw-transport retention and its existing codes; the
  candidate transport is unchanged.
- Open for other owners: `.github/workflows/release.yml` still references
  `publisher-transport.zip`, the `outerTransportPath` output and the separate
  `trusted-publisher-receipt` artifact; `Coherence.ps1` still asserts that
  separate artifact. The existing 52 `InstallRuntime` scenarios in
  `infrastructure/release/tests/Bootstrap.ps1` need three members plus the three
  new arguments. No build, test, network, commit or publication was performed
  here.
