# Mcp.Workflow producer

`workflow/` owns the executable schema-v1 Workflow producer and its native stdio MCP
host. This directory owns only the deterministic state machine, transport
implementation, and the workflow distribution. Downstream harnesses own their own
workflow skill, coordinator decisions, and pinned profile catalog extensions;
project profiles and overlays live under `<project-root>/.workflow/profiles`.
Persisted task state is only `.tasks/<TASK-ID>/runtime.json` in strict schema v1;
legacy layouts and aliases are rejected. Coordination is process-ephemeral, so
`runtime.lock` is never created or retained.

The distribution scripts are component-local: they emit only Workflow artifacts.
The .NET MCP has its own producer boundary under `dotnet/` and its release is
not part of the Workflow bootstrap.

The v1 release build produces the immutable `v1.0.3.zip`
framework-dependent `net11.0` distribution. The archive filename encodes the
version only; the producer identity already comes from the producer directory,
the manifest `id`, the project/assembly identity, the release context, and
the consumer descriptor. The consumer must invoke the pinned `Mcp.Workflow.dll`
entry DLL with `dotnet exec`; it must not run source scripts or restore/build
the producer at runtime.

```text
dotnet build workflow/Mcp.Workflow.fsproj -c Release
dotnet run --project workflow/Mcp.Workflow.fsproj -c Release --no-build
```

For the immutable `v1.0.3.zip` asset, run
`dotnet fsi workflow/release/Build.fsx`, then
`dotnet fsi workflow/release/Pins.fsx`, and verify with
`dotnet fsi workflow/tests/DistributionTests.fsx`. The build stages only
`workflow/Mcp.Workflow.fsproj` under `workflow/dist/workflow/` and emits:

```text
workflow/dist/v1.0.3.zip
workflow/dist/workflow/distribution.json
workflow/dist/consumer-pins.json
```

`workflow/dist/consumer-pins.json` contains exactly one `workflow` pin and is the
sole input for the downstream harness descriptor pin update. The manifest records
the exact archive allowlist and a lowercase SHA-256 for every runtime file. The
archive is allowlisted to the runtime DLL, its framework-dependent metadata,
`NOTICE.txt`, and `distribution.json`; source, project, build output, PDB, and
apphost files are rejected. Regeneration removes only the workflow v1 staging
directory. Packaging refuses to run from a dirty source tree and stamps each
manifest with the clean `HEAD` revision it built from.

The manifest uses deterministic arrays: `files` contains `{ "path", "sha256" }`
objects for the runtime allowlist. `path` is an exact slash-separated,
repository-relative archive path matching the v1 consumer contract; paths are
unique case-insensitively and each payload hash is lowercase SHA-256.
`archiveFiles` contains the complete archive entry-name allowlist.

## Persisted-reference inventory

External working repositories that consumed a prior release may hold legacy
sidecars. Inventory must classify (not merely find-and-replace) every persisted
field below before any reclassification is attempted:

- `profile` identity (`general` | `software` | `harness` | project custom).
- `profileFingerprint` (content-derived SHA-256 over the canonical form that
  produced the fingerprint).
- Lifecycle: `Draft` vs `Baselined`; terminal `complete`/`aborted`.
- Keyed materialized guard set (`validation`, `review`, `investigation`, plus
  project-strengthened keys): keys, applicability, waiver, target-bound
  dispositions.
- Effective project overlays active at creation time.
- Decisions, evidence records, and contract baseline retained by the task.

Custom profiles whose ID and content match unchanged between the old and new
discovery location do **not** change fingerprints when relocated. Renaming an
overlay ID, rewording guidance, or accidentally omitting an overlay does.

## Migration safety policy

This release removes the built-in `opencode` identity and rewires discovery to
`<project-root>/.workflow/profiles`. The producer performs **no implicit
migration**: new tasks use `harness`; legacy tasks keep their stored identity
and fingerprint until a deterministic, authorized operation changes them.

### Draft tasks with a legacy profile

Existing `ReclassifyTask` to `harness` is supported; Draft reclassification
does not evaluate weakening, so ordinary Coordinator authority is sufficient.
The explicit reclassification is **not automatically non-weakening**: before
issuing it, the operator must compare legacy overlay keyed guards and
capabilities against the prospective `harness` target and confirm retention.
Unknown source overlays, unprovable equivalence, or stronger legacy
obligations must stop the rename; built-in guard-count parity alone is not
proof. Terminal Draft records remain historical/read-only unless an
independently authorized lifecycle operation is intended.

### Baselined tasks with a legacy profile

With `opencode` absent from the registry, ordinary mutation fails closed at the
profile-availability gate, and profile-drift reconciliation cannot rename an
unavailable profile. Explicit reclassification treats missing source profile as
weakening and therefore requires `UserAuthority`, which has **no trusted
ingress** in this producer. These records remain read-only / fail-closed.

Operators must **not** forge a source profile, hand-edit a persisted
fingerprint, downgrade a Baselined record to Draft, or add a migration-only
authority exception to bypass that gate. A bounded pre-cutover procedure using
an isolated previous producer may rescue a proven non-weakening record; if the
original source cannot be recovered, or the change requires User authority,
leave the record untouched.

### Operators must not

- Forge a source profile to satisfy reclassification or fingerprint equality.
- Hand-edit `profileFingerprint` or sibling persisted bytes to evade
  capability comparison.
- Downgrade the contract or bypass authority to clear a Baselined legacy task.
- Maintain a producer-side `opencode`→`harness` alias or dual-directory
  discovery; the retired identity is not re-introduced.

Producer-side contract, monotonic overlay semantics, guard keys, capability
envelope, and authority algorithms remain mechanically enforced and are not
relaxed by this release.
