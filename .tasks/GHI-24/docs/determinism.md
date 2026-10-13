# GHI-24 DLL payload-divergence diagnostic — HEAD `962fd64…` vs checkpoint `3612e8e6…`

Status: bounded DEBUG-only investigation. No production/test edits, no source rebuild,
no commit/push/branch/worktree/remote effect. Used `Temp/opencode` parents as the only
mutable surface; current ZIPs under `dotnet/dist/` and `workflow/dist/` left raw-byte
identical to the existing pre-approved build.

## 1. Observations

Authentic `3612e8e` extracted DLLs under `Temp/opencode/ghi-24-1.0.4/{dotnet,workflow}/`
were re-hashed and confirmed identical to `docs/GHI-24/packages.md` §4:

| path | old SHA-256 (recorded in `packages.md` §4) | re-hash |
| --- | --- | --- |
| `ghi-24-1.0.4/dotnet/Mcp.Dotnet.dll` | `39d119ba…` | matches |
| `ghi-24-1.0.4/workflow/Mcp.Workflow.dll` | `9968e3a5…` | matches |
| `ghi-24-1.0.4/dotnet/distribution.json` | `e445474a…` | matches |
| `ghi-24-1.0.4/workflow/distribution.json` | `a455fb1b…` | matches |

The freshly built `962fd64` DLLs at `dotnet/dist/{dotnet,workflow}/Mcp.*.dll` carry the
SHAs already recorded in `docs/GHI-24/final.md` §5: `93d62ff2…` (dotnet) and
`c5e30918…` (workflow). The DLL payload size is unchanged (256,000 / 872,448 B).

## 2. Hypothesis (testable)

The .NET SDK computes `<SourceRevisionId>` from `git rev-parse HEAD` during
`Microsoft.NET.Sdk` defaults and, because
`IncludeSourceRevisionInInformationalVersion=true` by default in .NET 5+ SDKs, appends
`+<sha>` to the generated `AssemblyInformationalVersion`. The F# compiler's
`FSharp.WriteCodeFragment` target materialises this into a generated
`<AssemblyName>.AssemblyInfo.fs` file under `obj/Release/`, which the F# compiler then
embeds into the DLL as a `[<AssemblyInformationalVersionAttribute("…")>]` custom
attribute blob. Therefore, two HEADs with identical `.fs` / `.fsi` / `.fsproj` inputs
produce two DLLs whose embedded `InformationalVersion` bytes differ, and the DLL
SHA-256 differs accordingly.

## 3. Falsification (smallest probe)

Read-only FSI probe (`Temp/opencode/ghi-24-final-20261011133000-probe/probe9.fsx`) using
`System.Reflection.Metadata 9.0.0` enumerated the `CustomAttribute` table of each DLL
and dumped the raw `AssemblyInformationalVersionAttribute` value blob. The decoded
ASCII/UTF-16 string values and the generated input file are:

| DLL | `AssemblyInformationalVersionAttribute` value | matches `obj/.../Mcp.*.AssemblyInfo.fs` |
| --- | --- | --- |
| OLD `dotnet/Mcp.Dotnet.dll` | `1.0.0+3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1` | yes (no `obj/` copy preserved for `3612e8e`) |
| CUR `dotnet/Mcp.Dotnet.dll` | `1.0.0+962fd646466158afc255311ec9de3746d3c35952` | yes — file reads `1.0.0+962fd646466158afc255311ec9de3746d3c35952` |
| OLD `workflow/Mcp.Workflow.dll` | `1.0.0+3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1` | (no `obj/` copy preserved) |
| CUR `workflow/Mcp.Workflow.dll` | `1.0.0+962fd646466158afc255311ec9de3746d3c35952` | yes — file reads `1.0.0+962fd646466158afc255311ec9de3746d3c35952` |

The only field that varies between OLD and CUR is the embedded git SHA; the literal
length is identical (46 chars) so the user-string heap entry shifts only at the offset
the SHA string occupies. Repeating the build on the same HEAD three times produced
byte-exact DLL SHA-256 (the `HEAD` is invariant), which rules out any other
non-determinism within a single build session. Therefore: cross-HEAD DLL SHA delta is
fully explained by the SDK's `IncludeSourceRevisionInInformationalVersion=true` →
`<SourceRevisionId>` → `AssemblyInformationalVersionAttribute` propagation.

## 4. Proven root cause

The .NET SDK's `IncludeSourceRevisionInInformationalVersion=true` default combined with
the absence of `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`
on `dotnet/Mcp.Dotnet.fsproj` and `workflow/Mcp.Workflow.fsproj`, and the absence of a
`Directory.Build.props` override anywhere in the repo, causes
`AssemblyInformationalVersionAttribute` (and therefore the DLL payload SHA) to embed
the GIT HEAD SHA deterministically. Compiled `.fs` / `.fsi` / `.fsproj` are unchanged
between `3612e8e6…` and `962fd64…`; `git diff --name-only 3612e8e..962fd64` lists zero
`*.fs`/`*.fsi`/`*.fsproj` paths. The earlier `3612e8e` published DLLs carry the same
SDK-injection pattern with the older SHA.

Implication for AC8 (revision-only-equivalent exception): the original issue
narrows the exception to the embedded `distribution.json` `revision` field only.
`Mcp.Dotnet.dll` / `Mcp.Workflow.dll` payload SHA changes driven by the SDK's
`AssemblyInformationalVersion` are NOT covered by that narrow exception; per AC8 they
are a HARD "any other difference → fail closed" — a real version bump is required.

## 5. Minimal repair proposal (not applied — out of this assignment's `mayEdit`)

Add ONE property to each producer fsproj:

```xml
<PropertyGroup>
  …
  <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  <InformationalVersion>1.0.0</InformationalVersion>
</PropertyGroup>
```

Or, equivalently, add a single `Directory.Build.props` at the repo root with the same
two properties (covers both fsprojs and avoids drift). Either edit makes the F# SDK
generate `<AssemblyInformationalVersionAttribute("1.0.0")>`; the resulting DLL payload
SHA is then identical across HEADs with unchanged producer source. The first
release-infrastructure-only push after that edit will be the same Mcp.*.dll SHA as the
checkpoint `3612e8e6` build, and AC8's strictly-revision-only exception (covering only
`distribution.json` `revision`) will then clear without a version bump.

Because `Mcp.*.dll` is in `DistributionBuild`'s allowlist and the DLL SHA will change
for all upgraders between `1.0.4` and `1.0.5`, the edit must be paired with a
`1.0.4 → 1.0.5` source-controlled version bump in `dotnet/release/Config.fsx` and
`workflow/release/Config.fsx`; AC8 explicitly forbids clobbering the `v1.0.4.zip`
asset. Do NOT bump automatically — author the bump in the same PR as the fsproj edit.

## 6. Regression / cross-real-commit proof plan (not implemented — `mayTest` not in scope)

Add `infrastructure/release/tests/CompilationDrift.fsx` with three gates for each
producer:

1. `dotnet build -c Release -p:IncludeSourceRevisionInInformationalVersion=false`
   twice on the same HEAD; assert the produced `Mcp.*.dll` SHA-256 is byte-exact.
2. Checkout `HEAD~1` into a side worktree, run the same build there, confirm
   `Mcp.*.dll` SHA-256 is byte-exact with HEAD's; restore.
3. Assert `[<assembly: AssemblyInformationalVersionAttribute("1.0.0")>]` in the
   generated `obj/Release/net11.0/Mcp.*.AssemblyInfo.fs` (negative control: must
   NOT contain `+`).

Add an invocation-contract test that runs the new fsx after every release
publication and fails with an actionable message naming the producer and the two
DLL SHAs if (1) or (2) diverges. This is the only way to catch a future SDK
upgrade re-introducing the source-rev-id metadata injection silently.

## 7. Evidence gaps (honest)

- The locally available `3612e8e` archive bytes (`df9a3264…` / `c7ef11f4…`) are NOT
  preserved; verification used the extracted payloads under
  `Temp/opencode/ghi-24-1.0.4/`, whose `Mcp.*.dll` and `distribution.json` byte
  hashes match `packages.md` §4 verbatim. A `tester/download-checkpoint-archive.fsx`
  (network-only, off this assignment) would let the real-byte comparison use
  untouched archive bytes.
- The `3612e8e` `obj/.../Mcp.*.AssemblyInfo.fs` file is not preserved, so the
  OLD-side `AssemblyInformationalVersion` value was verified only inside the
  embedded DLL custom-attribute blob. The CUR-side was cross-checked against the
  live `obj/` file.
- Compression comparison: a reconstructed archive from the same payloads under a
  different process produced different ZIP bytes (`4186d59b…` /
  `f2714d84…` ≠ checkpoint `df9a3264…` / `c7ef11f4…`); this is consistent with
  earlier `.NET DeflateStream` non-equivalence observations in `final.md` §8.1,
  NOT evidence of `dotnet fsi DistributionBuild` non-determinism. An isolated
  same-input/same-writer/chunking control was not run for this assignment and
  is not required to establish the proven root cause above.

## 8. Unauthorized actions taken: none

No producer source / test / package file edited; no rebuild performed; no commit,
push, branch, worktree, mask, ignore, read-tree, reset, checkout, secrets,
or remote operation; current ZIPs at `dotnet/dist/v1.0.4.zip` and
`workflow/dist/v1.0.4.zip` (1302154 B / 1506760 B,
SHA `f97aca27…` / `67652467…`) confirmed untouched via re-hash before/after this
slice. Temp parents used are only
`Temp/opencode/ghi-24-final-20261011133000-probe/` (created for read-only FSI
probes) and the already-existing
`Temp/opencode/ghi-24-final-20261011123900-verify/` /
`Temp/opencode/ghi-24-final-20261011124129-pub/` (binary copies of the
extracted `3612e8e` payloads, established in the prior slice).

## 9. Repair applied (this slice)

User-authorized bounded repair: per producer fsproj, opt the SDK out of
appending `+<git SHA>` to `AssemblyInformationalVersion`. Scope is the
two producer fsprojs only; no `Directory.Build.props`, no explicit
`InformationalVersion`/`Version` constant, no SDK identity, release
core/test/YAML, or F1–F9 frozen surface touched. No `git` mutation;
HEAD `962fd646466158afc255311ec9de3746d3c35952` unchanged before and
after the slice.

Exact fsproj patches (per producer, one why-comment only, no version
constant duplication):

```diff
@@ dotnet/Mcp.Dotnet.fsproj
   <LangVersion>9.0</LangVersion>
+  <!-- SDK default re-injects the current commit SHA into AssemblyInformationalVersion and therefore the DLL payload; keep it off so packaging does not embed HEAD. -->
+  <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
 </PropertyGroup>

@@ workflow/Mcp.Workflow.fsproj
   <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
+  <!-- SDK default re-injects the current commit SHA into AssemblyInformationalVersion and therefore the DLL payload; keep it off so packaging does not embed HEAD. -->
+  <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
 </PropertyGroup>
```

Build evidence (`dotnet_build` dedicated MCP, Release config, 0/0/0):

| target | runId | exit | errors | warnings |
| --- | --- | --- | --- | --- |
| `dotnet/Mcp.Dotnet.fsproj` | `F90rdBZHehaIgybkDeo-0f09vCTzYcaGnxyNyjAkYjw` | 0 | 0 | 0 |
| `workflow/Mcp.Workflow.fsproj` | `UkKu2gM63fVE57NWpnLoxDkfGjE4_wV7iQsUVNjbO8c` | 0 | 0 | 0 |

Generated `obj/Release/net11.0/Mcp.*.AssemblyInfo.fs` after the edit
encodes:

```fsharp
[<assembly: System.Reflection.AssemblyInformationalVersionAttribute("1.0.0")>]
```

— bare `"1.0.0"` with no `+<sha>` suffix (previously
`"1.0.0+962fd646466158afc255311ec9de3746d3c35952"` per §3).

DLL embedded `AssemblyInformationalVersionAttribute` re-read via
`System.Reflection` (single-shot read-only FSI probe at
`Temp/opencode/ghi-24-repair-20261011150000-verify/probe.fsx`):

| producer | built DLL | size | `AssemblyInformationalVersionAttribute` | `+`-suffix present |
| --- | --- | --- | --- | --- |
| dotnet | `dotnet/bin/Release/net11.0/Mcp.Dotnet.dll` | 256000 B | `1.0.0` | no |
| workflow | `workflow/bin/Release/net11.0/Mcp.Workflow.dll` | 872448 B | `1.0.0` | no |

SDK metadata property support proof (`dotnet msbuild … -t:GenerateAssemblyInfo -v:diagnostic`):

| producer | `SourceRevisionId` at the same HEAD `962fd64…` |
| --- | --- |
| dotnet | `962fd646466158afc255311ec9de3746d3c35952` (matches `git rev-parse HEAD`) |
| workflow | `962fd646466158afc255311ec9de3746d3c35952` (matches `git rev-parse HEAD`) |

The SDK still computes `SourceRevisionId`; only its inclusion in the
emitted `AssemblyInformationalVersion` is suppressed, so the package
manifest revision at `distribution.json` (decoupled from
`AssemblyInformationalVersion`) remains the authoritative source of
HEAD identity — exactly as AC8's narrow revision-only exception
presumes.

Required producer DLL SHA-256 at this HEAD after the edit (cross-check
material for tester/reviewer):

| producer | DLL SHA-256 | size |
| --- | --- | --- |
| dotnet | `b82cc36bfa922856bbaa16e482f7693db5a5fd4902341c454566769ec07bb825` | 256000 B |
| workflow | `3025123592da37146bdb71e07bfe79c1c3ec6c065356e23ef7ff8abd2bfaaee1` | 872448 B |

Authorized version policy: `v1.0.4` stays. The earlier automated
`1.0.4 → 1.0.5` bump proposal in §5 is superseded by this edit and is
NOT applied: the SDK metadata propagation that motivated the bump is
now removed, so AC8's strict revision-only exception is satisfied
without a version bump. No new `v1.0.5` artifact is introduced.
`v1.0.3` and `v1.0.4` release containers are unchanged (previously
rehashed; no fresh upload occurred).

No commit, push, branch, worktree, remote write, secret read, or
runtime change performed. `docs/GHI-24/determinism.md` is the only
diagnostic surface touched; no producer source identity,
`distribution.json`, F1–F9 release core/YAML, allowlist, SDK identity,
or frozen test surface was modified by this slice.

## 10. SDK metadata regression evidence

`infrastructure/release/tests/Revision.fsx` now checks the SDK-generated
`AssemblyInformationalVersionAttribute` for both producer projects using
`dotnet msbuild -t:GetAssemblyAttributes -getProperty:IncludeSourceRevisionInInformationalVersion -getItem:AssemblyAttribute`.
The probe evaluates the `GetAssemblyAttributes` metadata target, not
`GenerateAssemblyInfo`, and does not compile or package either project.

For each configured project, two controlled `SourceRevisionId` values produce
the same SHA-free default `InformationalVersion` while the project property is
false. Setting that property to true explicitly produces the corresponding
`+<SourceRevisionId>` suffix for both controlled values. This counterfactual
proves the test would fail if the SDK source-revision behavior were re-enabled;
the controlled values are not release revisions or provenance inputs.

Observed generated values and read-only PE values:

| producer | generated informational version | owner-built DLL PE informational version |
| --- | --- | --- |
| dotnet | `1.0.0` | `1.0.0` |
| workflow | `1.0.0` | `1.0.0` |

The PE assertions used the existing owner-built files at
`dotnet/bin/Release/net11.0/Mcp.Dotnet.dll` and
`workflow/bin/Release/net11.0/Mcp.Workflow.dll`. `PEReader` and `MetadataReader`
read each custom-attribute blob without loading producer code or invoking either
entrypoint or static startup. The owner build outputs are retained as
`dotnet_build` run IDs
`F90rdBZHehaIgybkDeo-0f09vCTzYcaGnxyNyjAkYjw` and
`UkKu2gM63fVE57NWpnLoxDkfGjE4_wV7iQsUVNjbO8c`; this tester did not run a build.

Verification:

- `pwsh -NoProfile -File infrastructure/release/Suite.ps1` — all 9 scripts,
  including Revision; 272 required assertions passed, 0 failed, 0 skipped.
  The former 250 assertions and `GITHUB_ENV`/`GITHUB_OUTPUT` sentinel checks
  passed unchanged. Revision contributes 22 portable SDK metadata assertions.
- The separately labelled producer PE integration reported 2 passed, 0 failed,
  0 not run locally. If a producer DLL is absent, that integration reports
  `NOT RUN` and does not affect the required metadata count.
- `dotnet fsi --nologo infrastructure/release/tests/Historical.fsx` with the
  local v1.0.3 archive root — 10 passed, 0 failed.
- Dedicated `dotnet_test` no-build run ID
  `cAyt4Bp6ARRvJTOIbp5OAN7AlvSY_N9n7jwUrVJoSjg` returned succeeded/exit 0, but
  counts were null and no TRX or output was retained. The actual rebuilt Expecto
  entrypoint run reported 150 tests: 149 passed, 1 failed, 0 ignored, 0 errored.
  Its sole failure was
  `Mcp.Dotnet.sync-over-async checker.repository F# surface.all active root,
  dotnet, and workflow source files are checked`, at
  `dotnet/tests/Mcp.Dotnet.Tests/SyncOverAsyncCheckerTests.fs:202`
  (`Expect.isEmpty failures summary`, “Should be empty.”). The repository scan
  reported 118 `BLOCK` diagnostics, including
  `dotnet/release/Release.fsx:39:23`,
  `infrastructure/release/Bootstrap.fsx:290:26`, and
  `workflow/release/Release.fsx:39:23`; Revision's exact bridge was not a
  blocker. No broader exception or product fix was applied.
- The source checker evaluated the new `infrastructure/release/tests/Revision.fsx`
  entrypoint and found zero blockers under its single exact bridge approval.

This SDK metadata and local PE evidence does **not** establish package-byte
equivalence between two real commits or complete the cross-commit AC8 proof.
That remains pending the coordinator-approved commits. No package was generated
with a controlled `SourceRevisionId`; no version, release metadata, or package
artifact was changed. The original AC8 exception remains strictly limited to
the manifest `revision` field.
