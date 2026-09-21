# Workflow MCP producer

`workflow/` owns the executable schema-v3 Task Runtime and its native stdio MCP
host. OpenCode owns the workflow skill, coordinator decisions, and the pinned
profile catalog; this directory owns only the deterministic state machine,
transport implementation, and the workflow distribution.

The distribution scripts are component-local: they emit no verifier artifacts.
The .NET MCP has its own producer boundary under `dotnet/` and its release is
not part of the Task Runtime bootstrap.

The v1 release build produces the immutable `workflow-v1.0.0.zip` framework-
dependent `net11.0` distribution. The consumer must invoke the pinned entry DLL with `dotnet exec`; it must not run
source scripts or restore/build the producer at runtime.

```text
dotnet build workflow/Task.Runtime.fsproj -c Release
dotnet run --project workflow/Task.Runtime.fsproj -c Release --no-build
```

For the immutable `workflow-v1.0.0.zip` asset, run
`dotnet fsi workflow/BuildDistributions.fsx`, then
`dotnet fsi workflow/PrepareReleasePins.fsx`, and verify with
`dotnet fsi workflow/tests/DistributionTests.fsx`. The build stages only
`workflow/Task.Runtime.fsproj` under `workflow/dist/workflow/` and emits:

```text
workflow/dist/workflow-v1.0.0.zip
workflow/dist/workflow/distribution.json
workflow/dist/consumer-pins.json
```

`workflow/dist/consumer-pins.json` contains exactly one `workflow` pin and is the
sole input for the final OpenCode descriptor pin update. The manifest records
the exact archive allowlist and a lowercase SHA-256 for every runtime file. The
archive is allowlisted to the runtime DLL, its framework-dependent metadata,
`NOTICE.txt`, and `distribution.json`; source, project, build output, PDB, and
apphost files are rejected. Regeneration removes only the workflow v1 staging
directory and preserves the stored `task-runtime-v0.1.1` release outputs.

The manifest uses deterministic arrays: `files` contains `{ "name", "sha256" }`
objects for the runtime allowlist, while `archiveFiles` contains the complete
archive entry-name allowlist.
