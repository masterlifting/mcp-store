# Task Runtime MCP producer

`workflow/` owns the executable schema-v3 Task Runtime and its native stdio MCP
host. OpenCode owns the workflow skill, coordinator decisions, and the pinned
profile catalog; this directory owns only the deterministic state machine and
transport implementation.

The release build produces a framework-dependent `net11.0` distribution. The
consumer must invoke the pinned entry DLL with `dotnet exec`; it must not run
source scripts or restore/build the producer at runtime.

```text
dotnet build workflow/Task.Runtime.fsproj -c Release
dotnet run --project workflow/Task.Runtime.fsproj -c Release --no-build
```

After the producer release commit and asset upload, run
`dotnet fsi workflow/BuildDistributions.fsx`, then
`dotnet fsi workflow/PrepareReleasePins.fsx`. The generated
`workflow/dist/consumer-pins.json` is the sole input for the final OpenCode
descriptor pin update; until that step, consumer descriptors intentionally use
`POST_RELEASE_PIN_REQUIRED` and fail closed.
