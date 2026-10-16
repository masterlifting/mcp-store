# Mcp.Dotnet producer

This directory is the standalone producer boundary for the bounded .NET
build/test MCP server. It owns the Dotnet producer implementation and publishes
the `dotnet` v1.0.5 framework-dependent `net11.0` runtime distribution. Consumers
do not build or run this source checkout at runtime.

## Consumer installation

The consumer provisions the release asset into an extraction root **it** selects
and verifies the archive SHA-256 and `distribution.json` manifest SHA-256 before
use. The manifest describes launch metadata only: extraction-root selection,
caching, and resolution are consumer-owned and are not part of this distribution.

The entry assembly is started through an absolute .NET host with the ordered
required arguments the manifest declares:

```text
<absolute-dotnet-host> exec <extraction-root>/Mcp.Dotnet.dll --dotnet-host <absolute-dotnet-host> --artifact-root <absolute path>
```

The host path is intentionally supplied by the consumer. The producer rejects
relative, non-canonical, workspace-local, missing, or reparse-point hosts.

The producer requires an explicit `artifactRoot` supplied by the consumer. It
must be a fully-qualified local absolute path outside the trusted workspace; the
producer never resolves a relative path and never derives an OpenCode-specific
path. Roots are canonicalized, validated without side effects, and rejected when
they are the workspace root or a descendant, an existing file, a reparse point,
a path that traverses a reparse point, or a Windows UNC/network path.

Run state is namespaced as `<artifact-root>/<workspace-id>/<run-id>`, where
`workspace-id` is the lowercase SHA-256 hex of the canonical trusted workspace
(case-normalized only on Windows) and `run-id` is an opaque directory name that
is independent of the public run identifier. The root itself is consumer-owned.
Cleanup recursively removes only registry-owned run directories and then removes
the owned workspace namespace non-recursively only when it is empty; it never
removes the shared root, sibling workspaces, or unrelated entries. Per-run
isolation, aggregate and per-artifact quotas, retention, and cleanup apply
identically to every external root.

The MCP host/runtime startup prerequisite is consumer-provisioned and is
independent of project SDK selection. Project build/test runs use the normal
workspace or project SDK selection; the producer neither pins nor overrides it.

The server exposes the fixed tools `build`, `test`, and `details` over stdio.
The implementation has no dependency on OpenCode APIs; OpenCode owns only its
consumer descriptor, launcher, and tool-facing contracts.

## Producer tests

The deterministic F#-native implementation suite is an Expecto executable:

```text
dotnet run --project tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj --configuration Release
```

Run the command from this directory. It targets `net11.0`, references
`Mcp.Dotnet.fsproj` directly, and owns the dotnet build/test domain, authorization,
process, quota, and MCP transport coverage.

The repo-shared provenance guard has a focused check that does not require a clean
tree:

```text
dotnet fsi dotnet/tests/ProvenanceTests.fsx
```

## Producer packaging

From the repository root, build and pin the immutable `v1.0.5.zip`
release with:

```text
dotnet fsi dotnet/release/Build.fsx
dotnet fsi dotnet/release/Pins.fsx
dotnet fsi dotnet/tests/DistributionTests.fsx
```

The scripts stage only the allowlisted runtime files under
`dotnet/dist/dotnet/`. `distribution.json` records the exact archive
allowlist and a lowercase SHA-256 for every runtime file. The generated archive
contains the runtime files, `NOTICE.txt`, and the manifest; source, project,
build output, PDB, and apphost files are rejected. The archive filename
encodes the version only (`v1.0.5.zip`); the producer identity already comes
from the producer directory, the manifest `id`, the project/assembly
identity, the release context, and the consumer descriptor. Packaging refuses
to run from a dirty source tree and stamps each manifest with the clean
`HEAD` revision it built from.

The manifest is the shared distribution schema v2
(`infrastructure/distribution/README.md`): `producer` (build provenance only),
`launch` (`kind = dotnet-exec`, the entry DLL, and the ordered required
`--dotnet-host` / `--artifact-root` argument descriptors with their closed value
types), `runtime` (framework and version derived from the packaged
`Mcp.Dotnet.runtimeconfig.json` and cross-checked against its hashed bytes),
`sourceRepository` (derived from the fixed release asset URI), and `files`
(`{ "path", "sha256" }` for every non-manifest archive member, `NOTICE.txt`
included, sorted ordinally). Archive membership is exactly
`files ∪ { "distribution.json" }`; there is no separate `archiveFiles` list.
`path` is an exact slash-separated relative member path; paths are unique
case-insensitively and each payload hash is lowercase SHA-256.
`dotnet/dist/consumer-pins.json` is the single unwrapped consumer pin record for
this component.
