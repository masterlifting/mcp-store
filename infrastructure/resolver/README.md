# `infrastructure/resolver`

One harness-independent installer and resolver for the owned MCP distributions. A
consumer declares the exact immutable pin it requires and starts the MCP through
this boundary; the resolver owns cache validation, download, verification,
installation, entrypoint resolution, and process launch. Harnesses own their own
registration, permissions, routing, and guidance, and never download, verify,
install, cache, update, or garbage-collect a distribution themselves.

The pin and manifest schemas are the shared ones: the three-field `DistributionPin` (`id`, `version`, `digest`) and
`DistributionContract` schema v2, both owned by `infrastructure/distribution`.
This project adds no competing schema, no producer-ID branch, and no consumer
adapter.

## Bootstrap

The resolver is a framework-dependent `net11.0` executable. Provision
`Resolver.dll`, `Resolver.deps.json`, `Resolver.runtimeconfig.json`, and
`FSharp.Core.dll` from a trusted build or release, then invoke it with an absolute
.NET host. A release archive carries those four files plus `NOTICE.txt` and
`distribution.json`, six flat members in all; the .NET host and runtime are a
separate prerequisite, not part of that closure:

```text
dotnet exec <absolute>/Resolver.dll <command> [options]
```

The resolver never uses an SDK, FSI, project file, or `global.json`; it launches
its child with the same absolute-host model. That bootstrap trust decision
(which bytes are provisioned, and how) belongs to the consumer or operator, not
to this program.

The resolver is itself published through the shared release transaction as a
CLI-only artifact: its manifest uses the `dotnet-cli` launch kind, with entrypoint
`Resolver.dll` and no arguments. Because the resolver is the trust root for every
producer, a consumer must verify the pin, archive and member digests before first
executing it, and it is never obtained or verified by running the resolver. The
resolver therefore refuses a `dotnet-cli` manifest as a launch target in both `run`
and `resolve`, before it stages anything, so it cannot install itself. Resolver 1.0.0
is the previously published baseline; the revised three-field-pin Resolver release
is not yet published or verified (see `docs/Distribution.md`).

## CLI

| Command | Behavior |
| --- | --- |
| `resolve` | Validate the cache, install the pinned revision if necessary, and print a resolution snapshot to stdout. |
| `run` | Resolve, validate the launch inputs, then start the entrypoint and stay attached to the child. |

| Option | Required | Meaning |
| --- | --- | --- |
| `--pin <absolute file>` | both commands | The canonical trusted pin JSON (`id`, `version`, `digest`) for the exact revision to use. |
| `--root <absolute directory>` | no | Explicit install root. Default: the canonical platform user-local root. Reserved for tests and offline operator use. |
| `--workspace <absolute directory>` | `run` | Existing directory used as the child's working directory. |
| `--host <absolute file>` | `run` | The .NET host that runs the entrypoint. Supplied by the caller, never discovered. |
| `--input <name>=<value>` | no, repeatable | Value for one manifest-declared launch input, matched by its declared `input` name. |

`run` composes the child command line as
`<host> exec <install>/<entrypoint> <flag> <value>...` from the manifest's ordered
argument descriptors plus the supplied inputs. Arguments go through
`ProcessStartInfo.ArgumentList`; no shell is involved at any point. The host and
entrypoint paths are absolute, and the child's working directory is always the
explicit `--workspace` value, so the inherited CWD and any project `global.json`
cannot select a different program.

### Exit codes

- `0`: `resolve` printed a snapshot, or the child exited `0`.
- `1`: resolver failure, including transport, integrity, install, lease, and
  start failures.
- `2`: the command line was refused (unknown or repeated option, missing value,
  non-absolute path, missing required option).
- any other value: the child's own exit status, reported unchanged. When the
  resolver itself terminates the child after `SIGINT`/`SIGTERM`, it reports `130`
  and `143`.

A child that exits `1` or `2` is therefore not distinguishable from a
resolver-level failure by exit code alone; a consumer that needs the child status
exactly can use `resolve` and start the child itself.

### Stdio

`run` redirects nothing: the child inherits the resolver's standard handles, so a
stdio MCP session stays between the harness and the child. **stdout is the child's
channel.** The resolver writes nothing to stdout in that mode and reports its own
diagnostics, including a signal-termination notice, on stderr. `resolve` is the
one command that writes a snapshot to stdout, because it launches no child.

## Resolution snapshot

`resolve` prints one JSON object (schema **2**; a field change is a schema change).
Its top-level key order is stable:

```json
{
  "schemaVersion": 2,
  "key": "<sha256 of the canonical pin>",
  "root": "<absolute install root>",
  "install": "<absolute installed revision directory>",
  "entrypoint": "<absolute entry file inside the install>",
  "id": "dotnet",
  "version": "1.0.8",
  "digest": "sha256:<64 lowercase hex>",
  "revision": "<40 lowercase hex, from the authenticated manifest>",
  "kind": "dotnet-exec",
  "runtime": {
    "framework": "Microsoft.NETCore.App",
    "version": "11.0.0",
    "config": "Mcp.Dotnet.runtimeconfig.json"
  },
  "arguments": [
    { "flag": "--dotnet-host", "input": "dotnetHost", "type": "absolute-file" }
  ]
}
```

`key` is the installation identity: the SHA-256 of the canonical pin bytes, so any
pin field difference names a different installation. `revision` is read from the
manifest after the whole archive is authenticated against `digest`; the archive
name and asset URI are derived and not reported. `runtime`
is the prerequisite derived from the packaged, hashed `runtimeconfig.json`, and
the manifest is rejected when it declares anything else. Producer build
provenance is deliberately absent: it is not a consumer requirement and not a
supported platform claim.

The snapshot is produced while the shared lease is held and the lease is then
released, because a `resolve` caller starts no child of its own.

## Storage

The default root is canonical and user-local, never repository- or
harness-relative:

| Platform | Root |
| --- | --- |
| Windows | `%LOCALAPPDATA%\mcp-store` |
| Linux | `$XDG_DATA_HOME/mcp-store`, else `~/.local/share/mcp-store` |
| macOS | `~/Library/Application Support/mcp-store` |

The root and all of its ancestors must be link-free, because containment is
decided on path text; a root reached through a link is refused with the offending
component instead of being accepted at an ambiguous location.

Each pin gets its own directory set, addressed only by the derived pin hash and
the validated archive name:

```text
<root>/installs/<key>              immutable installed revision
<root>/archives/<key>/<archive>    retained verified archive bytes
<root>/staging/<key>/<guid>        one staging tree per install attempt
<root>/locks/<key>.guard           exclusive install guard
<root>/locks/<key>.lease           shared usage claim
```

Installation is transactional: a staged tree is verified against the trusted pin
and published by same-filesystem rename, so a partial tree is never a valid
install and a valid revision is never overwritten in place. Competing processes
serialize on the guard file, and coordination files are never unlinked, so no
stale-lock reclamation protocol exists to get wrong.

Before opening a guard or lease, the resolver rejects linked paths, non-directory
ancestors, and existing non-regular leaves; a missing leaf may be created. This
classification is not atomic with opening, so concurrent same-user filesystem
mutation remains outside this protection.

Guard exclusivity is proven rather than assumed: the guard is held with
`FileShare.None` and then re-opened exclusively, and the platform must refuse that
second open. Where the refusal does not happen — an advisory lock the filesystem
silently skips, or a runtime with file locking disabled — installation fails
closed with `CoordinationUnsupported` instead of proceeding on a lock that is not
there.

Every resolution verifies the retained ZIP against the pin before trusting its
embedded manifest. It checks the manifest identity, exact archive membership,
and SHA-256 of every packaged file, then compares the installed tree against
that verified inventory. A cache that does not match the trusted pin is never
launched. Entry kind is decided before any open, read, hash, or delete, so a
link, FIFO, socket, or device fails the operation closed; the same rule covers the
pin file and every launch input.

## Retention and cleanup

There is no automatic garbage collection and no automatic replacement. A valid
install is retained indefinitely, because another harness may be the consumer that
installed it, and no cross-harness reachability signal exists. Cleanup is explicit
and operator-owned:

- Delete `<root>/installs/<key>` and `<root>/archives/<key>` only for a revision
  you have decided no consumer needs. Do not delete them from a harness update
  path to save space.
- An installed tree that fails re-verification is never moved or replaced by the
  resolver. A valid retained archive is re-verified and reused; if it is absent
  or invalid, the pinned archive is downloaded and verified. The run then fails
  closed telling you to move the tree aside offline; a tree a running consumer
  may be executing stays where it is.
- `<root>/staging/<key>` holds per-attempt trees. An install attempt deletes an
  entry only when its name is a resolver-generated GUID and every file inside is a
  byte-identical copy of a verified member; links, special entries, unrecognized
  names, and leftover `.partial` files are preserved for the operator.
- Locks are 0-byte coordination files. Deleting one while it is held also defeats
  the protocol; leave them in place.

## Failure reporting

Expected failures are typed and printed as one line on stderr: `ReleaseError.message`.
A missing pin file, a non-absolute path, a refused archive, a corrupt cache, an
unavailable coordination file, or a rejected launch input all fail closed with
the path and the reason rather than falling back to another pin. A link anywhere
in the chain of the pin, host, workspace, or typed input is refused with the
component that is a link, and an installation that must be replaced is refused
with the operator action that unblocks it. A missing or incompatible runtime is
reported by the invoked .NET host itself, on stderr, with its own exit code; the
resolver provisions no runtime and does not substitute another revision.

## Limitations

These are current, unverified, or unimplemented properties. Do not read this
document as a claim beyond them.

- **Runtime qualification is bounded to the configured targets.** Automated
  resolver verification is limited to the package gate
  (`tests/distribution/Run.fsx`: archive, manifest, pin and closure bytes of the
  built and pinned `dist`), the
  packaged smoke (`tests/distribution/Smoke.fsx`: the extracted DLL's no-argument
  usage contract, the authenticated CLI-only launch refusal and an archive-digest
  refusal) and the native build; install, lease and process behavior are not
  covered by an automated suite.
  Windows x64 is the release platform selection. Qualification covers only the
  hosted Unix targets configured in `.github/workflows/unix.yml` (`linux-x64` on
  Ubuntu 24, `osx-arm64` on macOS 15, and `osx-x64` on macOS 15). It does not
  establish support for all Linux architectures, musl, or other OS versions.
  The published 1.0.0 baseline was independently downloaded and verified
  (see `docs/Distribution.md`); download verification for the final
  three-field releases is pending publication.
- **Unix coordination has bounded runtime evidence.** The guard's exclusivity
  acquisition probe runs at install time; its runtime evidence is bounded to the
  filesystems used by the hosted Unix qualification targets. On Unix, .NET
  implements `FileShare.None` as a non-blocking
  `flock(LOCK_EX)` and every other share value as `LOCK_SH`, and it ignores lock
  failures other than `EWOULDBLOCK` — exactly the silent absence the acquisition
  probe detects. The shared lease remains a best-effort usage claim; no safety
  property depends on it, because an existing installation is never replaced
  automatically. This evidence is not a guarantee for all filesystems. Locking
  behavior on NFS/SMB, or against writers that do not use .NET, is unverified;
  neither file is a cross-machine lock.
- **Unix entry-kind classification uses the platform `stat` utility.** Linux
  requires GNU `stat` at `/usr/bin/stat`; macOS requires its BSD `stat` at that
  path. The implementation invokes it directly with fixed platform arguments,
  bounds each call to at most two seconds, and fails closed on unsupported
  platforms, execution errors, timeout, or malformed output. Its runtime
  evidence is bounded to the hosted Unix qualification targets; that does not
  qualify other Unix platforms or runtime versions.
- **Classification has subprocess cost.** Inventory and cleanup classify entries
  sequentially and may launch one process per entry (up to the 8,192-entry
  bound). Each scan/cleanup has a cooperative 60-second deadline, with the
  remaining time limiting each classification call; inventory also checks its
  deadline between digest chunks. These bounds do not interrupt an arbitrary
  kernel filesystem stall, and no hosted timing evidence has qualified the cost.
- **An installation that fails re-verification needs an operator.** Nothing here
  moves, renames, or deletes an existing installation, so a tree whose trusted
  names carry different bytes stays in place and blocks that pin until an operator
  moves it aside offline. That is the cost of never replacing bytes a process the
  resolver did not start may still be executing.
- **Orphaned children are not tracked.** If the resolver is killed by a signal it
  cannot intercept, the OS closes its lease handle while the child keeps running.
  The resolver terminates its child on `SIGINT`/`SIGTERM` and holds the lease until
  the child exits, but it does not supervise, re-adopt, or reap processes it did
  not start; the fail-closed replacement policy above covers that window.
- **External paths must be canonical.** A symlink anywhere in the chain of the
  pin, host, workspace, or typed input is refused, because containment is decided
  on path text; supply the resolved path. Hard links, bind mounts, and mount
  points that make two textual paths the same physical entry are not detected.
- **Path admission is not atomic with filesystem operations.** Link and kind
  checks precede opens and deletes, but another same-user writer can mutate a
  path between the check and operation.
- **Input content hashes are the product's contract.** The resolver validates
  shape, kind, existence, and location for typed launch inputs; a declared
  SHA-256 input value is passed through, and the product that consumes it owns
  that check.

## Example

```json
{
  "id": "dotnet",
  "version": "1.0.8",
  "digest": "sha256:<64 lowercase hex>"
}
```

Now the pin for one exact revision:

```text
dotnet exec /opt/mcp-store/Resolver.dll resolve --pin /etc/mcp-store/dotnet.json
dotnet exec /opt/mcp-store/Resolver.dll run \
  --pin /etc/mcp-store/dotnet.json \
  --workspace /home/me/project \
  --host /usr/share/dotnet/dotnet \
  --input dotnetHost=/usr/share/dotnet/dotnet \
  --input artifactRoot=/home/me/.local/state/mcp-dotnet
```

The launcher host (`--host`) and a manifest input such as `dotnetHost` are
separate values that are usually the same path: the first is what the resolver
executes, the second is what the child receives and uses for its own work.
