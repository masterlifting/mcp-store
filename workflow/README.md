# Mcp.Workflow producer

`workflow/` owns the Workflow producer: the deterministic state machine, native
stdio MCP host, and Workflow distribution. Downstream harnesses own their
coordination and consumer pins. Project profiles and overlays live under
`<project-root>/.workflow/profiles`.

Producer source and release inputs live in this component; `workflow/dist/`
contains generated distribution artifacts and consumer pins. Neither source
changes nor generated artifacts update a consumer's installed producer or pin.

## Persisted task state

`.tasks/<TASK-ID>/runtime.json` in strict schema v2 is the sole task-state
authority. The task root permits only four entry names/kinds: a regular
`runtime.json` file and optional `docs/`, `scripts/`, and `temp/` directories. Their nested
contents are opaque to ordinary task operations. Cleanup/archive inspect nested
entries according to their safety rules.
`scripts/` holds all task-authored script sources, including one-off sources;
`docs/` holds durable supporting materials and evidence, not authoritative
contract; and `temp/` is a disposable execution workspace for results. Workflow
does not run or classify scripts. `task_clean` removes temporary content or
moves a completed task directory to the archive; see
[Cleaner.md](docs/Cleaner.md) for its contract and manual qualification.
Creation rejects an occupied task root, including one containing only permitted
entries. No persistent lock file is used.

Path resolution rejects reparse-point traversal and validates the root boundary.
Immediate root entries are checked for supported names and kinds; the runtime
sidecar must be a regular, non-reparse file. Windows classification uses
`File.GetAttributes`. Unix classification depends on the .NET 11 internal
`SystemNative_LStat` export and its normalized `FileStatus` layout; this is not a
stable application API. Runtime baseline, ABI, architecture, and filesystem
behavior therefore require qualification before making platform claims. Windows
is the current qualified target; Unix filesystem behavior is unqualified and is
not claimed verified.

Workflow coordinates participating processes with a task-specific named OS
mutex. Mutations validate the expected `stateRevision` before applying; stale or
rejected operations persist nothing. Sidecar writes use a same-directory
temporary file, flush it, then replace or move it into place. These mechanisms
do not protect against hostile external filesystem writers or authorize edits
outside Workflow operations.

User-authority requirements fail closed: ordinary `add-decision` cannot
manufacture User authority, and this producer has no trusted User-authority
ingress. Do not forge authority, edit task records directly, or infer permission
to mutate user data.

Before installing a producer update, obtain operator authorization and safely
inventory the actual task data against the target producer's four-entry root
contract, including relevant deployment copies. Stop on unsupported, unreadable,
or ambiguous entries and obtain an explicit disposition. Producer installation
does not migrate or delete task data. Preserve real data and immutable published
artifacts; do not assume a source upgrade changes an installed producer or
consumer pin. No live upgrade or implicit migration is performed by this
producer.

## Current operations

Evidence-kind identifiers are opaque, 1–64-character ASCII identifiers that
start with a lowercase letter and contain nonempty hyphen-separated segments of
lowercase letters and digits. They are compared exactly: Workflow does not trim,
case-fold, or resolve aliases. Profiles own their evidence-kind vocabulary, such
as `build`, `test`, and `review`.

`task_apply` supports closed work-item patch operations: `addWorkItem`,
`updateWorkItem`, `removeWorkItem`, and `setDependencies`. IDs and references
are validated, dependency cycles and no-op changes are rejected, and removal is
restricted to an unreferenced pending leaf. Baselined changes that weaken
existing obligations require trusted User authority and fail closed without it.
Reopen accepts only the singleton target `task` for completed tasks, is
Coordinator-authorized, preserves records, and does not change
`contractRevision`; aborted and nonterminal tasks cannot be structurally
reopened. See [manual verification](docs/ManualVerification.md) for operator
instructions; the instructions are not evidence of execution.

## Build and distribution

The producer targets `net11.0` and runs on the .NET 11 framework. Its Unix
file-kind path uses the runtime's internal `FileStatus`/`SystemNative_LStat`
interop; Unix filesystem behavior remains unqualified. Build with:

```text
dotnet build workflow/Mcp.Workflow.fsproj -c Release
```

The component-local release scripts build the framework-dependent archive and
consumer pin. The shared resolver owns installation, verification, and launch
(`infrastructure/resolver/README.md`); it validates the distribution manifest
and starts the entry DLL with `dotnet exec`, supplying the manifest-declared
`--profile-catalog <absolute path>` and
`--profile-catalog-sha256 <lowercase SHA-256>` arguments. Consumers must use the
published immutable archive and verified manifest/pin; they must not restore or
build producer source at runtime. See `workflow/release/` for release entry
points and `infrastructure/distribution/README.md` for the shared manifest
contract.
