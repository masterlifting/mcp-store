# Distribution and consumer boundary

One supported .NET producer distribution path: each .NET MCP producer publishes
a deterministic ZIP with a shared manifest, and a consumer installs and launches
it through the Resolver. The same release transaction also publishes the
Resolver itself as a CLI-only artifact. No second .NET producer path,
compatibility alias, dual reader, or migration path exists.

The normative release requirements are in
[`release/Requirements.md`](release/Requirements.md). The manifest and pin wire
contracts are in
[`infrastructure/distribution/README.md`](../infrastructure/distribution/README.md).
The Resolver CLI, storage, and failure contract are in
[`infrastructure/resolver/README.md`](../infrastructure/resolver/README.md).

## Participants and artifacts

- `dotnet` and `workflow` are MCP producers. Their manifests use the
  `dotnet-exec` launch kind, name the entry DLL, and declare ordered launch
  arguments supplied by the Resolver.
- `resolver` is a release participant but not an MCP producer. Its manifest uses
  the `dotnet-cli` launch kind with entrypoint `Resolver.dll` and no arguments.
  Its archive is a flat, exact six-member closure: `FSharp.Core.dll`,
  `NOTICE.txt`, `Resolver.deps.json`, `Resolver.dll`,
  `Resolver.runtimeconfig.json`, and `distribution.json`.
- The distribution manifest stays at schema 2. `dotnet-cli` is an additive
  launch kind inside that schema, not a schema change. The consumer pin is
  exactly `id`, `version` and `digest`; its archive name and asset URI derive
  from the identity and the trusted Store repository.
- An unknown launch kind, unknown field, missing member, duplicate key, or
  malformed value fails closed. There is no permissive reader and no
  case-insensitive fallback.
- The Resolver refuses a `dotnet-cli` manifest as an MCP launch target in both
  `run` and `resolve`, before it stages or installs anything. A refused
  `resolve` prints no snapshot and installs nothing; the lease is released on
  the refusal branch. A resolution snapshot reports the kind, so a CLI-only
  artifact is never presented as an MCP launch target.
- The cutover is forward-only. The previously published Resolver 1.0.0 and the
  historical pins, releases and running installations are untouched. Resolver
  2.0.0 reads only the three-field pin; no legacy reader exists, and the
  intermediate six-field format was never published. Each consumer must review
  and select the published pin for a new producer release.

## Host and runtime prerequisites

The artifacts are framework-dependent. The Resolver does not provision a
runtime and does not discover a host.

- The runtime prerequisite is separate from the six-file archive closure:
  `Microsoft.NETCore.App` at the version derived from the single pinned SDK
  (currently `11.0.0`). The Resolver derives the same requirement from the
  packaged `runtimeconfig.json` and rejects a manifest that declares anything
  else.
- The consumer supplies an absolute .NET host to the Resolver. The Resolver also
  rejects a relative, missing, or otherwise invalid host or typed input.
- A producer may require further typed launch inputs. Dotnet requires an
  external absolute `artifactRoot`; Workflow requires an absolute profile
  catalog path and its lowercase SHA-256. These are validated by the producer
  and Resolver; they are not distribution members.
- The host/runtime startup prerequisite is consumer-provisioned and independent
  of any project SDK selection. A .NET host does not roll a prerelease framework
  forward to a stable one.

## Trust and release handoff

A consumer trusts a reviewed pin, not a file hash in isolation. The pin fixes
the component, version, and the digest of the complete archive
(`sha256:` plus 64 lowercase hexadecimal characters); the archive name and HTTPS
asset URI derive from the component and version. The Resolver authenticates the
whole archive against that digest before it trusts the embedded manifest, then
takes the source revision, launch descriptor and runtime requirement from that
authenticated manifest. The trust chain is the reviewed consumer pin, allowed
HTTPS origins, and verification of the manifest, archive members, and the exact
archive closure before first execution.

- The Resolver is the trust root for every producer. It is never obtained or
  verified by running the Resolver, and it never establishes its own trust by
  running unverified code.
- The Resolver verifies the pin, archive, member digests, manifest identity, and
  installed tree. A cache that does not match the trusted pin is never launched.
- Independent consumer selections are required. Per-file hashes or NuGet
  signatures alone do not prove the intended publisher or source selection.
- The release transaction produces the canonical consumer pin at
  `<producer>/dist/consumer-pins.json`. Local qualification hashes are not
  publication identities; use only identities verified after release.

## No-clobber and the release container

Store's guarantee is asset-level: a published asset is never overwritten, and a
same-version candidate whose product bytes differ is rejected. Only the exact
embedded revision may be substituted; a non-revision difference requires a
version bump. Published revision and hashes survive a verified no-op.

The release containers use asset-level no-clobber and are not GitHub-immutable
releases. The publisher refuses to publish into an immutable one. The `resolver`
container must exist as a published, non-draft, non-immutable release before its
first publication.

## Partial publication and recovery

Publication is not atomic across components. Each component is validated and
published independently; if Resolver publication fails, Dotnet or Workflow
assets may already be published. One component's failure never authorizes
publishing an unvalidated artifact for that component.

Before retrying, inspect which assets were actually published and use the
existing verified no-op and recovery checks. Never overwrite published bytes.
Recovery is a re-run of the failed release run or a follow-up push that touches
a non-Telegram path; a Telegram-only or empty push does not trigger the release.
Local qualification does not establish a hosted publication identity.

## Consumer responsibilities

The consumer or harness owns its integration, acquisition, installation,
repair, update, materialization, and stdio acceptance. It must verify the
trusted artifact identity and the complete closure before executing anything
from it.

The consumer also owns its own pin selection and registry/runtime configuration.
The store does not mutate a consumer repository, install a consumer agent, or
provide a second package resolver. A checkout-independent consumer delivery has
not been verified.

Telegram remains a separate Python package boundary with its own entrypoint and
transports. It is not enrolled in this .NET distribution transaction.

## Current status and support boundaries

- Resolver 1.0.0 was previously published (alongside Dotnet 1.0.7 and Workflow
  3.0.1), and those baseline assets were independently verified. The revised
  Dotnet, Workflow and Resolver 2.0.0 releases are not yet published or verified
  by a real release. Only after that verification do they go to Harness #120,
  and checkout-independent consumer acceptance remains outstanding.
- Windows x64 is the release platform selection, asserted once by the
  publisher toolchain verification; each manifest records the RID the build host
  observed. Unix qualification is a read-only pull-request check, not Unix
  release publication.
- A packaged Resolver smoke (`infrastructure/resolver/tests/distribution/Smoke.fsx`)
  checks that the extracted Resolver archive launches with its usage contract,
  that the compiled verifier refuses the CLI-only archive as an MCP launch
  target, and that a changed archive fails its digest. It is not install, lease
  or consumer acceptance.
- Historical baseline downloads were verified; no download of a revised
  published release has been verified.
- Hosted GitHub Actions upload, immutable-release detection, and queue ordering
  behavior are not independently observed. The release workflow configures
  serialization and fails closed, but this document does not claim hosted
  verification.
