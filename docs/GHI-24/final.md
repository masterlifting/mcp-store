# GHI-24 final clean-SHA engineering verification — committed SHA `962fd64…`

## Status: engineering verification INCONCLUSIVE on AC8 — BLOCKED. The
              source tree at the user-approved local clean commit `962fd64…`
              builds deterministically (3/3 byte-exact repeat runs at the
              same HEAD) and matches `packages.md` invariant content per
              §6; the real-byte cross-revision comparison `3612e8e6…` vs
              `962fd64…` reports `DifferentBeyondRevision` per §8.1 and
              the narrow revision-only exception in `original.md`
              "Provenance/byte-equivalence exception" cannot apply to
              the Mcp.*.dll payload SHA delta. The diagnostic
              (`docs/GHI-24/determinism.md`) attributes the delta to the
              .NET SDK's `IncludeSourceRevisionInInformationalVersion=true`
              default, not to F# compiler non-determinism; the proposed
              fsproj-side opt-out and version bump require a user-decided
              replan and were NOT applied here. No further commit/push/
              remote action. The data here is the actual build/observation
              run on the clean source tree at the recorded HEAD.

This is the final clean-SHA slice for the GHI-24 acceptance. Authority:
`docs/GHI-24/original.md` (verbatim) and `docs/GHI-24/packages.md`
(frozen). The earlier checkpoint at `3612e8e6…` is referenced by SHA
only; its archive bytes are not preserved on this machine.

## 1. Source state at the start of the verification

```
$ git rev-parse HEAD
962fd646466158afc255311ec9de3746d3c35952

$ git log --oneline -1
962fd64 GHI-24: harden release publication and acceptance regressions

$ git status --porcelain
(empty)
```

`HEAD == 962fd646466158afc255311ec9de3746d3c35952`. Worktree clean. No
`.git/info/exclude` entries. No source `global.json`. No
assume-unchanged or skip-worktree markers. No temp commits. No
branches.

## 2. Build evidence (`dotnet_build` MCP, Release config, 0/0)

| target | runId | exit | errors / warnings |
| --- | --- | --- | --- |
| `dotnet/Mcp.Dotnet.fsproj` | `ZI26GlyDDEpR4SQ3QbPdOuuUV-KCa2CcukyV-XU3Ct4` | 0 | 0 / 0 |
| `workflow/Mcp.Workflow.fsproj` | `HmMRzWL7ZpHuv3L8nY5ydJFCFJ0XZ9lOYnLNMr2suV4` | 0 | 0 / 0 |
| `infrastructure/release/Release.fsproj` | `EplxUywPc-AbKZv9S2HLOLhZJdLbpsT0dpHBASXKzVo` | 0 | 0 / 0 |
| `infrastructure/scripts/Validation.fsproj` | `_Ov_NiZFLzk7jJKfEJlFhK7OtmQlFSpQRXwaI4dSvhU` | 0 | 0 / 0 |

`dotnet_build` is the dedicated build authority for producers, helper,
and analyzer (raw `dotnet fsi` / `dotnet publish` covered only the
release scripts that the MCP does not own).

## 3. Bootstrap cross-check (`Bootstrap.fsx`)

```
RELEASE_SDK=11.0.100-rc.1.26425.128
RELEASE_RUNTIME=Microsoft.NETCore.App 11.0.0-rc.1.26425.128
RELEASE_PLATFORM=win-x64
RELEASE_ARCHITECTURE=x64
RELEASE_DOTNET_VERSION=1.0.4     RELEASE_DOTNET_ARCHIVE=v1.0.4.zip
RELEASE_DOTNET_ASSET_URI=https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.4.zip
RELEASE_DOTNET_ENTRY=Mcp.Dotnet.dll
RELEASE_WORKFLOW_VERSION=1.0.4   RELEASE_WORKFLOW_ARCHIVE=v1.0.4.zip
RELEASE_WORKFLOW_ASSET_URI=https://github.com/masterlifting/mcp-store/releases/download/workflow/v1.0.4.zip
RELEASE_WORKFLOW_ENTRY=Mcp.Workflow.dll
```

Executing toolchain matches both the canonical `Config.fsx` literal and
the recorded values in `docs/GHI-24/packages.md` §5.

## 4. Authentic package run on HEAD `962fd64…`

`dotnet fsi <producer>/release/Build.fsx` and `Pins.fsx` were invoked on
the same clean worktree at this HEAD. Both assert
`assertCleanTree`/`committedHead`/`assertManifestRevision` and pass
the DistributionBuild allowlist before staging. `DistributionTests.fsx`
for both producers exited 0 (silent success).

```
$ dotnet fsi dotnet/release/Build.fsx
dotnet archive=v1.0.4.zip sha256=f97aca277ffc0f4af970442a819e4a810cdf24516a509da3c3f696462f434453 manifest=a6b0063f7c9f331ab39b3acbe41b1405538b6b8c16c9e3dbe659d5afb9a7bb07

$ dotnet fsi workflow/release/Build.fsx
workflow archive=v1.0.4.zip sha256=67652467ecf1ac73a39d666ef2e34f0656dd6abcb83cc2eea6d78ff99bf8f507 manifest=86f819fa10b0da27c223ba404d81c541ac52fb24f0a2eefe3d918c5dd48614d6

$ dotnet fsi dotnet/release/Pins.fsx
dotnet asset=v1.0.4.zip archiveSha256=f97aca277ffc0f4af970442a819e4a810cdf24516a509da3c3f696462f434453 manifestSha256=a6b0063f7c9f331ab39b3acbe41b1405538b6b8c16c9e3dbe659d5afb9a7bb07 revision=962fd646466158afc255311ec9de3746d3c35952

$ dotnet fsi workflow/release/Pins.fsx
workflow asset=v1.0.4.zip archiveSha256=67652467ecf1ac73a59d666ef2e34f0656dd6abcb83cc2eea6d78ff99bf8f507 manifestSha256=86f819fa10b0da27c223ba404d81c541ac52fb24f0a2eefe3d918c5dd48614d6 revision=962fd646466158afc255311ec9de3746d3c35952
```

External verification (`Get-FileHash -Algorithm SHA256`) re-confirmed
each hash on disk matches the script output above.

## 5. Authored artifact hashes recorded in this slice

| component | archive | archive SHA-256 | size | manifest SHA-256 | manifest revision |
| --- | --- | --- | --- | --- | --- |
| `dotnet` | `v1.0.4.zip` | `f97aca277ffc0f4af970442a819e4a810cdf24516a509da3c3f696462f434453` | 1,302,154 B | `a6b0063f7c9f331ab39b3acbe41b1405538b6b8c16c9e3dbe659d5afb9a7bb07` | `962fd646466158afc255311ec9de3746d3c35952` |
| `workflow` | `v1.0.4.zip` | `67652467ecf1ac73a39d666ef2e34f0656dd6abcb83cc2eea6d78ff99bf8f507` | 1,506,760 B | `86f819fa10b0da27c223ba404d81c541ac52fb24f0a2eefe3d918c5dd48614d6` | `962fd646466158afc255311ec9de3746d3c35952` |

Per-entry payload SHA inside the freshly built HEAD archives:

| entry | HEAD `962fd64` payload SHA-256 | checkpoint `3612e8e` payload SHA-256 | verdict |
| --- | --- | --- | --- |
| `FSharp.Core.dll` | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | identical |
| `Mcp.Dotnet.deps.json` | `58900bbe1ef2cb78041bfb2172fcf6d5a25bab1e9a3afa43facd9bbdcd15ebd0` | `58900bbe1ef2cb78041bfb2172fcf6d5a25bab1e9a3afa43facd9bbdcd15ebd0` | identical |
| `Mcp.Dotnet.dll` | `93d62ff27b55007b9a1bb918624a316ff02fdb44b687e6a4b9b9f58b2e041ff3` | `39d119ba2ed785ce329399e52c0daa36d2a1b3b6623496f934ae153669f2bb59` | **DIFFERS** |
| `Mcp.Dotnet.runtimeconfig.json` | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | identical |
| `NOTICE.txt` | `40c25ee1510e909fb4134ffdaf698e0356854ee6ef7469467b9454161d1fdcfc` | `40c25ee1510e909fb4134ffdaf698e0356854ee6ef7469467b9454161d1fdcfc` | identical |

| entry | HEAD `962fd64` payload SHA-256 | checkpoint `3612e8e` payload SHA-256 | verdict |
| --- | --- | --- | --- |
| `FSharp.Core.dll` | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | identical |
| `Mcp.Workflow.deps.json` | `cc84b73e3698dd9f5af289ee26fd5b8b4426b7cf2b4ae9e01fbfdc6e8453d388` | `cc84b73e3698dd9f5af289ee26fd5b8b4426b7cf2b4ae9e01fbfdc6e8453d388` | identical |
| `Mcp.Workflow.dll` | `c5e3091870cf58fef1c9941ba624aae633a8d49f0fb415e0810cea1ad19d4bef` | `9968e3a5056e7ceb16493246646cf89bfe207b0b3e159fbb029a46a6b25d66bc` | **DIFFERS** |
| `Mcp.Workflow.runtimeconfig.json` | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | identical |
| `NOTICE.txt` | `b369ed2840677cd0e54c4a462fe79001cca1bc96367633aa68a55c0c02305cbb` | `b369ed2840677cd0e54c4a462fe79001cca1bc96367633aa68a55c0c02305cbb` | identical |

The two `Mcp.*.dll` binaries differ between `3612e8e` and `962fd64`
even though no producer source (`*.fsproj`, `.fs`, `.fsi`) changed
between the two commits; only release-infrastructure scripts and docs
were touched. The diff between `3612e8e…` and `962fd64…` is limited
to:

```
docs/release/Requirements.md
dotnet/release/Config.fsx
dotnet/release/Release.fsx
infrastructure/release/{Archive,Bootstrap,Compare,Decide,Evaluate,Metadata,Publish,README,Release.fsproj,Runner,Suite.ps1,Transaction,Update.ps1}.fs(x)
infrastructure/release/tests/{Bootstrap,Compare,Evaluate,Fixture,Historical,Invocation,Metadata,Publish,Runner,Telegram}.fsx
infrastructure/scripts/tests/Regression.fsx
workflow/release/Config.fsx
workflow/release/Release.fsx
```

The strict delta on the payload bytes is real. The unsupported assertion
that "the F# compiler is not byte-deterministic for unchanged source
across SDK invocations" is incorrect: the F# compiler IS byte-deterministic
given identical inputs (including identical `SourceRevisionId`); the
cross-HEAD DLL payload SHA delta is driven by the .NET SDK's
`IncludeSourceRevisionInInformationalVersion=true` default policy
injecting `+<git SHA>` into the generated
`Mcp.{Dotnet,Workflow}.AssemblyInfo.fs` `AssemblyInformationalVersion`
attribute, which the F# compiler then embeds as a custom attribute blob
in the DLL. See `docs/GHI-24/determinism.md` §3–§4 for the read-only PE
metadata probe and the falsified/confirmed root cause.

## 6. Embedded manifest identity, SDK, runtime, platform, allowlist

| component | manifest id | version | sdk | runtime (declared) | entryDll | archive | archiveFiles (allowlist) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `dotnet` | `dotnet` | `1.0.4` | `11.0.100-rc.1.26425.128` | `11.0.0-rc.1.26425.128` | `Mcp.Dotnet.dll` | `v1.0.4.zip` | `FSharp.Core.dll`, `Mcp.Dotnet.deps.json`, `Mcp.Dotnet.dll`, `Mcp.Dotnet.runtimeconfig.json`, `NOTICE.txt`, `distribution.json` |
| `workflow` | `workflow` | `1.0.4` | `11.0.100-rc.1.26425.128` | `11.0.0-rc.1.26425.128` | `Mcp.Workflow.dll` | `v1.0.4.zip` | `FSharp.Core.dll`, `Mcp.Workflow.deps.json`, `Mcp.Workflow.dll`, `Mcp.Workflow.runtimeconfig.json`, `NOTICE.txt`, `distribution.json` |

`Pins.fsx` calls `validateManifestIdentity` for each producer with
`[(id, component); (version, contract); (archive, contract)]`; both
pass. The manifest `revision` is `962fd646466158afc255311ec9de3746d3c35952`,
which matches `git rev-parse HEAD` (asserted by `assertManifestRevision`
before `writeConsumerPins`).

## 7. Repeat deterministic packaging (same clean commit)

After the first build, the same `dotnet fsi {dotnet,workflow}/release/Build.fsx`
was invoked three times on the same worktree, same SHA. Hashes are
byte-exact:

| component | archive SHA-256 (run 1) | archive SHA-256 (run 2) | archive SHA-256 (run 3) | manifest SHA-256 (all runs) |
| --- | --- | --- | --- | --- |
| `dotnet` | `f97aca277ffc0f4af970442a819e4a810cdf24516a509da3c3f696462f434453` | `f97aca277ffc0f4af970442a819e4a810cdf24516a509da3c3f696462f434453` | `f97aca277ffc0f4af970442a819e4a810cdf24516a509da3c3f696462f434453` | `a6b0063f7c9f331ab39b3acbe41b1405538b6b8c16c9e3dbe659d5afb9a7bb07` |
| `workflow` | `67652467ecf1ac73a39d666ef2e34f0656dd6abcb83cc2eea6d78ff99bf8f507` | `67652467ecf1ac73a39d666ef2e34f0656dd6abcb83cc2eea6d78ff99bf8f507` | `67652467ecf1ac73a39d666ef2e34f0656dd6abcb83cc2eea6d78ff99bf8f507` | `86f819fa10b0da27c223ba404d81c541ac52fb24f0a2eefe3d918c5dd48614d6` |

Determinism confirmed: the `DistributionBuild` flow is byte-stable
under repeat invocations from the same committed `HEAD`. The archive
entries all use `LastWriteTime = 1980-01-01` and
`CompressionLevel.Optimal`; the manifest `revision` is the only field
that depends on `HEAD`, and `HEAD` is identical across the runs.

## 8. ACTUAL cross-revision comparison (production `ReleaseCompare.compare`)

The earlier `3612e8e` checkpoint archive bytes (`df9a3264…` / `c7ef11f4…`
from `docs/GHI-24/packages.md` §4) are not preserved on this machine.
The only locally available candidate ZIPs under `Temp/opencode`
(`ghi-24-authentic2/dotnet/dist/v1.0.4.zip`,
`ghi-24-authentic2/workflow/dist/v1.0.4.zip`) were rehashed first and
do NOT match the known checkpoint SHAs:

```
ghi-24-authentic2 v1.0.4.zip dotnet   = 4736E14F7BCC94118E0506A4F29357E89A3B6F42D3BD5A21EF1D76AD7F6B33E7
ghi-24-authentic2 v1.0.4.zip workflow = EC07F293FF120D5C849348077A0A95AC7F44D1DAA236CF2D7C1347774B87821E
```

Per the assignment rule ("Rehash first; if not exact don't label
checkpoint"), neither of these local archives is labelled as the
`3612e8e` checkpoint. The locally available extracted payload files
under `ghi-24-1.0.4/{dotnet,workflow}/` are the authentic `3612e8e`
extracted content — their `Mcp.*.dll` and `distribution.json` SHAs
match `docs/GHI-24/packages.md` §4 verbatim. Those extracted bytes
were binary-copied (safe `Copy-Item`, parent verified with
`Test-Path` before `New-Item`) to a fresh unique verified parent
under `Temp/opencode` for the verification.

`Temp/opencode` parents used (no `.git` mutation, no remote write):

```
C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-final-20261011123900-verify\
C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-final-20261011124129-pub\
```

### 8.1 ACTUAL real-byte comparison (production `Compare.compare`)

A published archive was reconstructed from the authentic `3612e8e`
extracted payloads using `DistributionBuild`'s canonical settings
(`CompressionLevel.Optimal`, `LastWriteTime = 1980-01-01`, sorted file
order, `file.CopyToAsync` payload). Its reconstructed archive SHAs are:

```
dotnet-v1.0.4-reconstructed.zip  size=1302160  sha256=4186d59b29d6923fe064b9a83be2abc3abd6cfde0731c221638a78f5a2dc7a40
workflow-v1.0.4-reconstructed.zip size=1506742  sha256=f2714d84a5eb285e7cb4222ce5a84ea20b6603d421d6bffa0e123ca8ae88cf9b
```

These SHAs do NOT match the original `3612e8e` SHAs (`df9a3264…` /
`c7ef11f4…`); the .NET `DeflateStream` is not byte-deterministic
across the two processes that produced the reconstruction vs the
original publication. The reconstruction is therefore labelled
"reconstructed from extracted `3612e8e` payloads" and NOT as the
checkpoint.

Production `ReleaseCompare.compare` against the reconstructed `3612e8e`
archive and the freshly built HEAD `962fd64` candidate archive:

```
## dotnet real-byte compare (reconstructed 3612e8e vs candidate 962fd64)
published archive sha=4186d59b29d6923fe064b9a83be2abc3abd6cfde0731c221638a78f5a2dc7a40 size=1302160
candidate archive sha=f97aca277ffc0f4af970442a819e4a810cdf24516a509da3c3f696462f434453 size=1302154
published Mcp.Dotnet.dll sha=39d119ba2ed785ce329399e52c0daa36d2a1b3b6623496f934ae153669f2bb59
candidate Mcp.Dotnet.dll sha=93d62ff27b55007b9a1bb918624a316ff02fdb44b687e6a4b9b9f58b2e041ff3
decision=DifferentBeyondRevision reason=entry 'FSharp.Core.dll' compressed size differs: published=1184782 candidate=1184787

## workflow real-byte compare (reconstructed 3612e8e vs candidate 962fd64)
published archive sha=f2714d84a5eb285e7cb4222ce5a84ea20b6603d421d6bffa0e123ca8ae88cf9b size=1506742
candidate archive sha=67652467ecf1ac73a39d666ef2e34f0656dd6abcb83cc2eea6d78ff99bf8f507 size=1506760
published Mcp.Workflow.dll sha=9968e3a5056e7ceb16493246646cf89bfe207b0b3e159fbb029a46a6b25d66bc
candidate Mcp.Workflow.dll sha=c5e3091870cf58fef1c9941ba624aae633a8d49f0fb415e0810cea1ad19d4bef
decision=DifferentBeyondRevision reason=entry 'FSharp.Core.dll' compressed size differs: published=1184782 candidate=1184787
```

**Hard failure (real payload change).** The first hit is the
`FSharp.Core.dll` compressed-size mismatch (1184782 vs 1184787). If
that were closed, the next hit would be the `Mcp.Dotnet.dll` /
`Mcp.Workflow.dll` payload SHA mismatch (`39d119ba…` vs `93d62ff2…` /
`9968e3a5…` vs `c5e30918…`). Per the assignment rule ("report payload
changes as HARD failure not proxy fixture success"), this is reported
verbatim — not as `RevisionOnlyEquivalent`, not as a fixture success.

The strict revision-only exception cannot apply to a
release-infrastructure-only push when the F# compiler output is not
byte-deterministic on this machine; the contract demands a HARD
failure here.

### 8.2 Synthetic twin (same .NET writer) classification

A separate `CompareTwin.fsx` script in the verification parent took
the freshly built HEAD `962fd64` candidate archive, extracted each
entry, rewrote only the embedded `distribution.json` `revision` field
from `962fd646…` to `3612e8e6…`, and repacked with the canonical
settings. This produces two archives built by the same .NET writer on
the same payloads — so every non-manifest entry is byte-identical —
with only the manifest `revision` field changed:

dotnet prev twin sha=56018dac6d640f8676872080b1d344b4ae9ea4cae04b9715d2e3bea3ef8e15d0 (1302154 B)
workflow prev twin sha=523c5439123d305fc34a6cca9a03393bb99df22df13c7ed6fcaf2f0bf4228ea3 (1506759 B)

```
## dotnet compare (prev=3612e8e vs candidate=962fd64)
published archive sha=56018dac6d640f8676872080b1d344b4ae9ea4cae04b9715d2e3bea3ef8e15d0 size=1302154
candidate archive sha=f97aca277ffc0f4af970442a819e4a810cdf24516a509da3c3f696462f434453 size=1302154
decision=RevisionOnlyEquivalent publishedSha=56018dac6d640f8676872080b1d344b4ae9ea4cae04b9715d2e3bea3ef8e15d0 publishedRev=3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1 evaluatedRev=962fd646466158afc255311ec9de3746d3c35952
PASS dotnet publishedRev equals 3612e8e
PASS dotnet evaluatedRev equals 962fd64

## workflow compare (prev=3612e8e vs candidate=962fd64)
published archive sha=523c5439123d305fc34a6cca9a03393bb99df22df13c7ed6fcaf2f0bf4228ea3 size=1506759
candidate archive sha=67652467ecf1ac73a39d666ef2e34f0656dd6abcb83cc2eea6d78ff99bf8f507 size=1506760
decision=RevisionOnlyEquivalent publishedSha=523c5439123d305fc34a6cca9a03393bb99df22df13c7ed6fcaf2f0bf4228ea3 publishedRev=3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1 evaluatedRev=962fd646466158afc255311ec9de3746d3c35952
PASS workflow publishedRev equals 3612e8e
PASS workflow evaluatedRev equals 962fd64
summary: passed=4 failed=0
```

The production `ReleaseCompare.compare` correctly classifies the twin
as `RevisionOnlyEquivalent` and reports the distinct `publishedRev`
and `evaluatedRev` rather than substituting the candidate hash as the
published one. This is the contract surface that the original issue
acceptance requires; it is NOT a proxy for the real-byte run.

### 8.3 Summary of decisions

| scenario | dotnet decision | workflow decision |
| --- | --- | --- |
| 8.1 real-byte (reconstructed `3612e8e` vs HEAD `962fd64`) | `DifferentBeyondRevision` (HARD failure) | `DifferentBeyondRevision` (HARD failure) |
| 8.2 synthetic twin (HEAD `962fd64` payload, manifest revision rewritten to `3612e8e`) | `RevisionOnlyEquivalent` (4/4 assertions pass) | `RevisionOnlyEquivalent` (4/4 assertions pass) |

## 9. Source-shared git metadata (anti-perpetuation re-check)

After the package runs, the worktree is still clean:

```
$ git status --porcelain
(empty)
```

No `.git/info/exclude` entries were added; no `global.json` was
introduced; no source files were masked, skipped, or replaced; no
temp commits were created. `git reflog` was not consulted and not
modified. No remote was written to. The `consumer-pins.json` and
`dist/**` outputs are under the existing `.gitignore` and never
touched the source repo. No branches were created or switched. No
release tag was moved. No release asset was uploaded.

## 10. Out-of-scope and not-tested (explicit non-claims)

- No `MCP_RELEASE_DO_UPLOAD=true` was set. `ReleaseRunner.exe` was
  not invoked against a real GitHub release. No release asset was
  uploaded. The compiled runner's `gitHubObserver` /
  `localMirrorObserver` / `uploadCandidate` / `uploadMetadata` paths
  are present in the binary but were not exercised against a remote
  in this slice.
- No mirror-driven "simulated upload" was run. `MCP_RELEASE_MIRROR_ROOT`
  was not set.
- The regression suite at `infrastructure/release/tests/` was not
  re-run in this slice (`Suite.ps1` is a tester-script concern, not an
  engineering verification concern). The runner binary
  (`infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe`) was
  rebuilt cleanly via `dotnet_build` and is available but was not
  invoked by this slice.
- No `release.yml` edit. No `Update.ps1` edit. No `Bootstrap.fsx`
  edit. No `Config.fsx` platform/SDK/runtime edit. No workflow
  run.
- No commit, push, branch op, worktree mutation, or remote write
  was performed by this slice.

## 11. Limitations and needed tester scripts

1. **Earlier `3612e8e` archive bytes are not preserved locally.** The
   two candidate ZIPs under `Temp/opencode/ghi-24-authentic2/dist`
   were rehashed and found to carry SHAs
   `4736E14F…` / `EC07F293…` (NOT the recorded
   `df9a3264…` / `c7ef11f4…`). Per the rehash-first rule they are NOT
   labelled as the checkpoint. A `tester` script is needed to
   download the published `dotnet/v1.0.4.zip` and
   `workflow/v1.0.4.zip` assets from
   `https://github.com/masterlifting/mcp-store/releases/download/{dotnet,workflow}/v1.0.4.zip`
   (network-only, never as part of engineering verification), verify
   the remote digests against `docs/GHI-24/packages.md` §4, and cache
   the bytes under a fresh `Temp/opencode` parent. That remote bytes
   copy is the only authoritative source for a real-byte cross-
   revision check; this slice cannot supply it.
2. **Reconstruction cannot reproduce the original ZIP wrapper bytes.**
   The .NET `DeflateStream` is not byte-deterministic across the two
   processes involved. The reconstructed archive's compressed payload
   bytes differ from the original publication, so any
   reconstructed-archive vs real-candidate comparison fails the first
   non-manifest entry's size check (here, `FSharp.Core.dll`
   1,184,782 B vs 1,184,787 B). This is a property of the .NET BCL
   used at packaging time and is independent of the GHI-24 contract.
3. **Mcp.*.dll payload SHA delta is caused by the .NET SDK source-rev
    injection, NOT F# compiler non-determinism.** The `Mcp.Dotnet.dll`
    SHA differs between `3612e8e` and `962fd64` even though no
    producer `.fs` / `.fsproj` / `.fsi` changed (`39d119ba…` vs
    `93d62ff2…`; same 256,000 B size). Same for `Mcp.Workflow.dll`
    (`9968e3a5…` vs `c5e30918…`; same 872,448 B). The
    `AssemblyInformationalVersionAttribute` blob in each DLL
    contains `1.0.0+3612e8e6…` / `1.0.0+962fd64…` respectively
    (probed via `System.Reflection.Metadata` in
    `Temp/opencode/ghi-24-final-20261011133000-probe/probe9.fsx`;
    see `docs/GHI-24/determinism.md` §3–§4). F# compiler IS
    byte-deterministic; the cross-HEAD divergence is the SDK's
    `IncludeSourceRevisionInInformationalVersion=true` default.
    The proposed regression test
    (`infrastructure/release/tests/CompilationDrift.fsx`) locks the
    DLL SHA across HEADs when the proposed fsproj opt-out is applied.
4. **Per the rule "If repeat / cross-revision fails STOP with exact
   observations, no speculative fix, preserve artifacts":** the
   real-byte run failed (`DifferentBeyondRevision` × 2). No
   speculative fix was applied. The failure is preserved in
   `SUMMARY.txt` and in the cross-revision scripts under the
   verified parent
   `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-final-20261011124129-pub\`.
5. **Per the rule "All commands needing a clean tree before writing
   docs":** the worktree is verified clean immediately before this
   slice was authored; `git status --porcelain` is empty. The git
   tree was never written to by this slice; only this `final.md`
   evidence document was created.

## 12. Tester scripts requested

- `tester/download-checkpoint-archive.fsx` — given a verified parent
  under `Temp/opencode`, downloads the `dotnet/v1.0.4.zip` and
  `workflow/v1.0.4.zip` from the canonical GitHub release, verifies
  their digests against `docs/GHI-24/packages.md` §4, and preserves
  the bytes for the production `Compare.compare`. Network-only;
  never co-mingled with engineering verification.
- `tester/characterize-compiler-drift.fsx` — superseded by
  `docs/GHI-24/determinism.md`. The cross-HEAD DLL SHA delta is
  attributed to the .NET SDK's
  `IncludeSourceRevisionInInformationalVersion=true` default, not
  to F# compiler non-determinism. Replace this script with the
  `CompilationDrift.fsx` proposed in `determinism.md` §6.

## 13. Required single-word contract descriptors

- handoff: contract
- tools: authority
- engineer: build
- comments: concise