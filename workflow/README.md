# Mcp.Workflow producer

`workflow/` owns the executable schema-v1 Workflow producer and its native stdio MCP
host. OpenCode owns the workflow skill, coordinator decisions, and the pinned
profile catalog; this directory owns only the deterministic state machine,
transport implementation, and the workflow distribution. Persisted task state is
only `.tasks/<TASK-ID>/runtime.json` in strict schema v1; legacy layouts and
aliases are rejected. Coordination is process-ephemeral, so `runtime.lock` is
never created or retained.

The distribution scripts are component-local: they emit no verifier artifacts.
The .NET MCP has its own producer boundary under `dotnet/` and its release is
not part of the Workflow bootstrap.

The v1 release build produces the corrected immutable `workflow-v1.0.1.zip`
framework-dependent `net11.0` distribution. The v1.0.0 archive remains an
immutable historical release with the original manifest-schema failure. The
consumer must invoke the pinned `Mcp.Workflow.dll` entry DLL with `dotnet exec`;
it must not run source scripts or restore/build the producer at runtime.

```text
dotnet build workflow/Mcp.Workflow.fsproj -c Release
dotnet run --project workflow/Mcp.Workflow.fsproj -c Release --no-build
```

For the corrected immutable `workflow-v1.0.1.zip` asset, run
`dotnet fsi workflow/BuildDistributions.fsx`, then
`dotnet fsi workflow/PrepareReleasePins.fsx`, and verify with
`dotnet fsi workflow/tests/DistributionTests.fsx`. The build stages only
`workflow/Mcp.Workflow.fsproj` under `workflow/dist/workflow/` and emits:

```text
workflow/dist/workflow-v1.0.1.zip
workflow/dist/workflow/distribution.json
workflow/dist/consumer-pins.json
```

`workflow/dist/consumer-pins.json` contains exactly one `workflow` pin and is the
sole input for the final OpenCode descriptor pin update. The manifest records
the exact archive allowlist and a lowercase SHA-256 for every runtime file. The
archive is allowlisted to the runtime DLL, its framework-dependent metadata,
`NOTICE.txt`, and `distribution.json`; source, project, build output, PDB, and
apphost files are rejected. Regeneration removes only the workflow v1 staging
directory and preserves the stored `workflow-v1.0.0.zip` historical release.

The manifest uses deterministic arrays: `files` contains `{ "path", "sha256" }`
objects for the runtime allowlist. `path` is an exact slash-separated,
repository-relative archive path matching the OpenCode v1 consumer contract;
paths are unique case-insensitively and each payload hash is lowercase SHA-256.
`archiveFiles` contains the complete archive entry-name allowlist.
