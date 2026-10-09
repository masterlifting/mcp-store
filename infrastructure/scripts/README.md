# `infrastructure/scripts` analyzer

Lightweight compiled SDK-FCS analyzer for the owned `.fsx` surface under
`infrastructure/`, `dotnet/`, and `workflow/`. Enforces role, closure, and
graph contracts without evaluating scripts.

## CLI

```
Validation <command> [--repo-root <path>]
```

| Command | Behavior |
| --- | --- |
| `validate` | Run every check (default when no command is supplied). |
| `inventory` | Print the discovered scripts and their declared roles. |
| `graph` | Print each script, its role, and its declared `#load` edges. |
| `typecheck` | FCS typecheck every reusable and test-helper root. |

Flags:

- `-r | --repo-root <path>`: explicit repository root. When omitted, the
  analyzer anchors the default root at `AppContext.BaseDirectory` and walks
  upward until a sibling `infrastructure/`, `dotnet/`, or `workflow/` is
  found, so resolution is independent of the caller's current working
  directory.

Exit codes:

- `0`: the requested check passed.
- `2`: usage error (unknown command, missing flag value, or more than one
  positional command).
- `3`: parse error during `inventory`/`graph`/`typecheck`.
- `4`: typecheck failure.
- `5`: validate pass found a contract violation.

## SDK-FCS, no evaluation

The analyzer talks to `FSharp.Compiler.Service` (FCS) only through its
public surface:

- `FSharpChecker.ParseFile` for parse diagnostics.
- `FSharpChecker.TokenizeLine` with carried-in lex state for `#load`
  directive discovery.
- `FSharpChecker.GetProjectOptionsFromScript` for the transitive `#load`
  closure, populated by FCS itself.
- `FSharpChecker.ParseAndCheckProject` for the typecheck pass on reusable
  and test-helper roots.

No private field is mutated and no script is evaluated; reusable roots are
typechecked standalone with framework reference assemblies drawn from the
local SDK ref pack
(`<dotnet>/packs/Microsoft.NETCore.App.Ref/<ver>/ref/net11.0/`). The SDK
root is discovered from the analyzer's own assembly location, so no
`ProgramFiles` path, package id, or environment variable is hard-coded.
Static scanning rejects direct `#r` and `#I` directives, including those
transitively loaded from project `.fs` files, before FCS typechecking; no
package resolution is performed. `#load` targets are rejected when any path
component is a reparse point.

## Role header authority

Every owned `.fsx` script declares its role on the first physical line of
the file:

```fsharp
// role: <one of the five exact values>
```

The five exact values recognized by the analyzer:

- `reusable module/helper`
- `entrypoint/command`
- `test helper`
- `test entrypoint`
- `release/build entrypoint`

The analyzer walks every line through the FCS tokenizer so `// role:` text
inside an F# string is not promoted to a declaration. A UTF-8 BOM at the
very start of the file is ignored when locating it, and line 0 (the first
physical line, after the BOM strip) is the canonical header — blank
lines are not skipped. Any additional `// role:` declaration anywhere in
the file — same value (duplicate) or a different value (conflict) —
fails the role contract, as does a missing first-line header or an
unrecognized value.

## Discovery

Discovery walks four roots by default:

- the repository root itself,
- `infrastructure/`,
- `dotnet/`,
- `workflow/`.

Within each root, `*.fsx` files are enumerated. The directories `bin`,
`obj`, `dist`, `.git`, and `.tasks` are excluded at every depth. `.tasks`
is platform-owned Task Runtime state (see `.gitignore` and `AGENTS.md`);
it is not an owned producer surface and the analyzer keeps no central
script inventory, so a non-owned platform surface is excluded by
directory name rather than whitelisted. Files are deduplicated by
absolute path so a file reachable through more than one root is
inventoried exactly once.

`#load` targets must be script-relative. Rooted or fully-qualified paths
(drive-root `C:\foo`, drive-relative `\foo`, UNC `\\server\share`,
absolute `/foo`) fail with `ROOTED_LOAD_PATH` before any resolution or
typechecking, even when the resolved path happens to land inside the
repository; `../foo` cross-directory paths are allowed as long as they
stay contained. Targets that resolve outside the repository root fail the
`DIRECTORY_ESCAPE` check; targets that do not exist on disk fail the
`MISSING_LOAD_TARGET` check. Reparse-point discovery roots, entries, and
load-target components fail with `SYMLINK_REPARSE` and are not followed.
Indented file-scope `#load` directives are supported; module-body `#load`
forms fail with `UNSUPPORTED_LOAD_FORM` because FSI does not load them.

## Validation lanes

The analyzer keeps static, typecheck, and integration concerns separate:

- **Static lane** (no FCS evaluation): role-header parsing, cycle
  detection over the sorted load graph, missing-dependency and
  reusable-closure checks. `inventory` and `graph` subcommands exercise
  this lane.
- **Typecheck lane** (FCS no-evaluation `ParseAndCheckProject`):
  reusable and test-helper roots only. The `typecheck` subcommand
  exercises this lane.
- **Integration lane** (downstream): out of scope for this project.

The `validate` subcommand composes every static check followed by the
typecheck lane.

## Regression tests

Run the deterministic analyzer and FSI-boundary regression suite from the
repository root:

```text
dotnet fsi infrastructure/scripts/tests/Regression.fsx
```

## #27 boundary

`masterlifting/opencode#27` consumes the producer releases described in
`dotnet/dist/consumer-pins.json` and `workflow/dist/consumer-pins.json`
and owns the Workflow MCP's coordination tests. This analyzer does not
modify `#27` implementation, automation, or workflow domain/protocol
ordering, and does not pin or alter producer consumer-pins.

## Audit snapshot

There is no authoritative file inventory maintained by this project. The
issue audit snapshot recorded 29 owned scripts (4 under
`infrastructure/`, 5 under `dotnet/`, 20 under `workflow/`); the
analyzer's `inventory` subcommand is the live source of truth at any
given commit.
