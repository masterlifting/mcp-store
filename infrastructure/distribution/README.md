# `infrastructure/distribution`

One immutable distribution contract shared by every producer. The wire schema is
producer-independent: a consumer can resolve identity, integrity, launch
requirements, and runtime prerequisites without producer-specific knowledge, and
no producer-ID branch exists anywhere in this module.

`Contract.fs`, `Pin.fs`, and `Archive.fs` are compiled into both the resolver and
`infrastructure/release/Release.fsproj` through explicit ordered `<Compile
Include>` items. Producer release scripts also load the shared sources with
script-relative `#load` paths. `infrastructure/Failure.fs` is the shared pure
failure contract used by both owners. These shared modules contain decision logic:
no processes, filesystem writes, network, credentials, or harness state.

## `Contract.fs` — `DistributionContract`

Distribution schema **v2**. A future schema change must update the version
constant, the parser, and the renderer together; there is no compatibility reader
for an older shape.

```json
{
  "schemaVersion": 2,
  "id": "dotnet",
  "version": "1.0.5",
  "revision": "<40 lowercase hex>",
  "sourceRepository": "https://github.com/masterlifting/mcp-store",
  "producer": { "sdk": "…", "runtime": "…", "platform": "…" },
  "archive": "v1.0.5.zip",
  "launch": {
    "kind": "dotnet-exec",
    "entrypoint": "Mcp.Dotnet.dll",
    "arguments": [ { "flag": "--dotnet-host", "input": "dotnetHost", "type": "absolute-file" } ]
  },
  "runtime": { "framework": "Microsoft.NETCore.App", "version": "…", "config": "Mcp.Dotnet.runtimeconfig.json" },
  "files": [ { "path": "Mcp.Dotnet.dll", "sha256": "<64 lowercase hex>" } ]
}
```

Invariants enforced by `parse`/`validate`:

- Every displayed field is required; unknown, duplicate, missing, null, or
  wrongly typed fields fail closed. `parse` rejects duplicate JSON keys anywhere
  in the document so two parses cannot silently disagree.
- `launch.kind` accepts only `dotnet-exec`. `launch.arguments` is a non-empty,
  ordered list of structured `flag`/`input`/`type` descriptors — never a shell
  template — and `type` is one of `absolute-file`, `absolute-directory`, or
  `sha256`. Flags must be `--`-prefixed, and flags and input names are unique
  case-insensitively.
- `producer` records build provenance only. It is not a consumer SDK requirement
  and not a supported-platform claim.
- `runtime.framework` is `Microsoft.NETCore.App`, and `runtime.config` must be the
  packaged runtime configuration of `launch.entrypoint`
  (`Path.ChangeExtension(entrypoint, ".runtimeconfig.json")`).
  `runtimeRequirementFromConfig` derives framework and version from those bytes
  and rejects duplicate keys first, so the derived prerequisite never depends on
  property order and the manifest never assumes an SDK-to-runtime correspondence.
- `files` covers **every non-manifest archive member**, including `NOTICE.txt`,
  sorted ordinally. Archive membership is exactly `files ∪ {manifest}`;
  `archiveMemberNames` and `trustedArchiveMemberNames` own that derivation.
- Every member path must be a safe slash-separated relative path: no rooted,
  drive-qualified, backslash, empty, current, parent, control, or non-ASCII
  segment. Hashes are exact lowercase 64-character SHA-256 and `revision` is an
  exact lowercase 40-character hex revision; there is no permissive
  normalization and no case-insensitive fallback.
- `resolveUnderRoot` composes a validated relative member beneath an explicitly
  supplied root and fails if the result escapes it. This resolves metadata only:
  installation-root selection, caching, and host discovery are not part of this
  contract.

`sourceRepositoryFromAssetUri` derives the single publication repository from the
fixed release asset URI so a source-repository claim and the pin cannot drift.

`render`/`toJsonNode` produce the canonical bytes: fixed key order, sorted member
files, and the shared indented formatting, so the standalone and embedded copies
of the manifest are byte-identical by construction.

## `Pin.fs` — `DistributionPin`

The canonical consumer pin, which is also the published
`<component>-<version>-metadata.json` projection: one strict parser, one canonical
renderer, and one whole-record comparison. Schema **v1** (this is the retained
existing release-metadata schema, not the distribution schema).

```json
{
  "schemaVersion": 1,
  "componentId": "dotnet",
  "version": "1.0.5",
  "assetUri": "…",
  "archiveName": "v1.0.5.zip",
  "archiveSha256": "<64 lowercase hex>",
  "manifestSha256": "<64 lowercase hex>",
  "publishedRevision": "<40 lowercase hex>",
  "producerSdk": "…",
  "producerRuntime": "…",
  "producerPlatform": "…"
}
```

- `consumer-pins.json` is exactly one unwrapped record, not an object keyed by
  component ID. The five-field keyed schema and its readers are removed with no
  alias or dual reader.
- `producerSdk`, `producerRuntime`, and `producerPlatform` are optional
  informational fields: omission is permitted, a present explicit null or a
  non-string is malformed, and an empty string is malformed.
- Hashes and revisions use the same exact lowercase length checks as the manifest.
- `compare` participates every field in identity, so an existing remote asset or
  an existing pin cannot be silently reused for a different artifact. A
  revision-only no-op retains the **published** revision and hashes, never
  candidate values.

## Consumers of these contracts

- `infrastructure/Distribution.fsx` (`DistributionBuild`) emits the manifest and
  archive.
- `infrastructure/Pins.fsx` (`ReleasePins`) writes the canonical consumer pin.
- `infrastructure/release/Policy.fs` parses the producer `Config.json` only;
  `ReleaseCompare.contractFromPolicy` projects it into the trusted
  `ComponentContract` used by comparison, handoff, evaluate, and orchestration.
- `infrastructure/resolver/` uses the same manifest, pin, archive, and failure
  contracts for installation and launch.

Release and resolver are separate consumers of these shared sources. Producer
release scripts load them with script-relative `#load` paths, so `Policy.fs`
deliberately does not depend on the comparison modules and build/pin scripts
never compile the full release graph.
