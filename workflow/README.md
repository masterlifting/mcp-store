# Task Runtime MCP producer

`workflow/` owns the executable schema-v3 Task Runtime and its native stdio MCP
host. OpenCode owns the workflow skill, coordinator decisions, and the pinned
profile catalog; this directory owns only the deterministic state machine,
transport implementation, and the Task Runtime distribution.

The distribution scripts are component-local: they emit no verifier artifacts.
The verifier has its own producer boundary under `dotnet/` and its release is
not part of the Task Runtime bootstrap.

The release build produces a framework-dependent `net11.0` distribution. The
consumer must invoke the pinned entry DLL with `dotnet exec`; it must not run
source scripts or restore/build the producer at runtime.

```text
dotnet build workflow/Task.Runtime.fsproj -c Release
dotnet run --project workflow/Task.Runtime.fsproj -c Release --no-build
```

For the immutable `task-runtime-v0.1.1` bootstrap asset, run
`dotnet fsi workflow/BuildDistributions.fsx`, then
`dotnet fsi workflow/PrepareReleasePins.fsx`, and verify with
`dotnet fsi workflow/tests/DistributionTests.fsx`. The build stages only
`workflow/Task.Runtime.fsproj` under `workflow/dist/task-runtime/` and emits:

```text
workflow/dist/task-runtime-v0.1.1.zip
workflow/dist/task-runtime/distribution.json
workflow/dist/consumer-pins.json
```

`workflow/dist/consumer-pins.json` contains exactly one `task-runtime` pin and
is the sole input for the final OpenCode descriptor pin update; until that step,
consumer descriptors intentionally use `POST_RELEASE_PIN_REQUIRED` and fail
closed. The archive is allowlisted to the runtime DLL, its framework-dependent
metadata, `NOTICE.txt`, and `distribution.json`; source, project, build output,
PDB, and apphost files are rejected. Regeneration removes only the Task Runtime
staging directory and generated current-output files, preserving stored release
archives, manifests, and pins already present in `workflow/dist/`.
