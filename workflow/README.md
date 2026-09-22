# Workflow MCP producer

`workflow/` owns the deterministic Workflow state machine and its stdio MCP transport. OpenCode owns orchestration and consumer-side configuration; this producer does not launch agents.

Canonical product identity:

```text
Mcp.Workflow
Mcp.Workflow.fsproj
Mcp.Workflow.dll
```

The sole persisted representation is clean-slate `schemaVersion = 1`. No migration reader is maintained for predecessor schemas.

The producer tools remain:

```text
task_create
task_get
task_apply
task_validate
```

`runtime.lock` is ephemeral mutation state: it exists only while a mutating operation owns the inter-process lock and is removed on release. `stateRevision` CAS and atomic `runtime.json` replacement remain separate correctness mechanisms.

## Producer validation

```text
dotnet build workflow/Mcp.Workflow.fsproj -c Release
dotnet fsi workflow/BuildDistributions.fsx
dotnet fsi workflow/PrepareReleasePins.fsx
```

Run the F# script tests under `workflow/tests/` for state-machine, transport, path-safety, schema, locking, and distribution coverage.
