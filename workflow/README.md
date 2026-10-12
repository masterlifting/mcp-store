# Mcp.Workflow producer

`workflow/` owns the executable schema-v1 Workflow producer and its native stdio MCP
host. This directory owns only the deterministic state machine, transport
implementation, and the workflow distribution. Downstream harnesses own their own
workflow skill, coordinator decisions, and pinned profile catalog extensions;
project profiles and overlays live under `<project-root>/.workflow/profiles`.
Persisted task state is only `.tasks/<TASK-ID>/runtime.json` in strict schema v1;
legacy layouts and aliases are rejected. Coordination is process-ephemeral, so
`runtime.lock` is never created or retained. A task directory may also carry
narrowly allowlisted task-owned supporting material: an optional `SPEC.md`
regular file and optional `docs/` and `scripts/` directories; nested contents
under those directories are opaque to Workflow and never followed or scanned.
Task creation rejects any occupied task directory (allowlisted-only
prepopulation included), and an already-empty task directory remains reusable.

## Task-root file-kind classification

Immediate task-root leaf classification that gates the allowlist relies on
the following per-OS contract:

- **Windows** uses `File.GetAttributes` and accepts any non-`Directory`,
  non-`ReparsePoint` leaf as a regular file. Read-only and hidden
  attributes do not disqualify an otherwise-valid regular file.
- **Unix** uses the .NET 11 runtime's native stat shim
  `libSystem.Native!SystemNative_LStat`, declared in
  `release/11.0/src/libraries/Common/src/Interop/Unix/System.Native/Interop.Stat.cs`
  and implemented as an `lstat(2)` shim in
  `release/11.0/src/native/libs/System.Native/pal_io.c:277-288`. The
  output is the normalized sequential `FileStatus` from
  `release/11.0/src/native/libs/System.Native/pal_io.h:17-38`; only the
  POSIX `S_IFREG` bit of `Mode` is read, so the runtime owns every
  libc / architecture / kernel-layout translation. The path argument is
  marshalled as UTF-8 (`UnmanagedType.LPUTF8Str`) under the Cdecl calling
  convention. A missing or layout-mismatched native shim is surfaced as
  `RuntimeError.PersistenceFailure`; per-entry native failures (missing
  path, permission denied) are mapped to "unsupported entry" so the
  outer operation can continue.

### Internal runtime dependency qualification

`SystemNative_LStat` is an **internal runtime export**, not a
compatibility-guaranteed .NET application API. This implementation
therefore accepts the dependency only for the qualified .NET 11 runtime
configurations inspected at write-time. Re-validate the struct
definition, export name, marshalling conventions, and `S_IFREG` value
against the targeted runtime whenever the .NET runtime baseline changes.
Do not infer additional supported platforms or runtimes.

### Known verification limits of this producer

- **Verified Unix configuration:** Linux x64 glibc on the official
  `mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1` image pinned to
  `sha256:207b30ec860b1c4edef3bfe33dd7af8d38726533cf5a77cb782006451281872e`
  (Ubuntu 26.04.1, glibc 2.43, runtime `11.0.0-rc.1.26425.128`).
  Read-only file / hidden-flag / symlink / FIFO / Unix-domain-socket /
  read-only `SPEC.md` cases at both `SPEC.md` and `runtime.json`
  reached their assertions on this configuration; the cancellation
  response bound (see below) and the no-persistent-`runtime.lock`
  invariant were exercised against the same runtime. The Windows 13/13
  Workflow suites and the .NET Expecto 151/151 suite are also green.
- **Not yet verified Unix configurations:** Linux ARM64, musl-on-Linux,
  macOS x64 / ARM64, BSD variants, Solaris / illumos, and any other
  Unix runtime / architecture that exports `SystemNative_LStat` from a
  different libc / kernel / build. macOS x64 / ARM64 in particular is
  a structural dependency of the producer distribution and must be
  exercised against a matching .NET 11 runtime before being claimed
  supported.
- **Internal runtime shim upgrade boundary:** `SystemNative_LStat`
  is an internal runtime export whose struct definition, export name,
  marshalling conventions, and `S_IFREG` value are owned by the .NET
  runtime. On any runtime baseline change (release upgrade, servicing
  patch, private build) the `FileStatus` declaration and the
  `libSystem.Native` resolution path must be re-validated against the
  targeted runtime; do not promote a release that has not been
  re-verified.
- **Failure injection:** missing native shim (`DllNotFoundException`),
  missing entry point (`EntryPointNotFoundException`), and
  layout-mismatch (`TypeLoadException`) surface as
  `RuntimeError.PersistenceFailure`. Per-entry native failures (path
  missing, permission denied) surface as `RuntimeError.InvalidInput`
  with "unsupported entry" so other entries in the same task directory
  can still be classified. Other unexpected exceptions are **not**
  swallowed and propagate to the caller; do not add blanket-catch
  around `FileKind.isRegularFile`.
- **Unix task-root mutex polling reason and cancellation bound:**
  `WaitHandle.WaitAny` rejects any array containing a named OS mutex
  with `PlatformNotSupportedException` on Unix (see .NET runtime
  `release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/Mutex.Unix.cs`),
  so `withLock`'s owner thread polls `Mutex.WaitOne(50ms)` and a
  non-blocking cancellation check on the same dedicated thread. Worst-
  case cancellation latency is bounded at roughly two poll intervals
  (~100 ms plus scheduling); the verified Linux measurement was 55.7 ms
  against a 500 ms bound. The Windows branch uses `WaitAny` exactly as
  before and is unaffected.
- **TOCTOU and metadata latency:** the classifier avoids blocking
  **content opens** on planted special files; it does not bound
  metadata latency on unhealthy filesystems or guarantee atomic
  check/use ordering against a concurrent Unix filesystem substitution.
  The OS-named mutex coordinates Workflow participants, not hostile
  filesystem writers; a hostile external actor can replace a
  classified sidecar before `ReadAllTextAsync` reads it, and that race
  is not closed by this fix.

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
