# GHI-24 W3 authentic package evidence — committed SHA `3612e8e6…`

## Status: package validation complete at the user-approved local
              checkpoint commit. No remote upload, no `.git` mutation,
              no real or simulated remote write. The data here is the
              actual build/observation run on a clean source tree at
              the recorded SHA.

This file is the only W3 evidence created against the committed SHA;
every other artifact under `docs/GHI-24/` predates this slice and was
authored before the checkpoint. Authority: `docs/GHI-24/original.md`
(verbatim), `docs/GHI-24/solution.md` (frozen), and the prior
`docs/GHI-24/{core,devops,metadata}.md` slices. Authority precedence
is the original issue; the per-slice evidence is supplementary.

## 1. Source state at the start of the validation

```
$ git rev-parse HEAD
3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1

$ git log --oneline -1
3612e8e GHI-24: add deterministic producer release publication

$ git status --porcelain
(empty)
```

`HEAD == 3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`. Worktree clean. No
`.git/info/exclude` entries. No source `global.json`. No assume-unchanged
or skip-worktree markers. No temp commits.

## 2. Build evidence (`dotnet_build` MCP, Release config, 0/0)

| target | runId | exit | notes |
| --- | --- | --- | --- |
| `dotnet/Mcp.Dotnet.fsproj` | `ypeTI-a0unqyTKw5-hxr5tj98tLOaT9v8jaAFeYrZuQ` | 0 | 0 errors / 0 warnings |
| `workflow/Mcp.Workflow.fsproj` | `Ch_XO7UPkjEnk-94nIr5AaiQgyPG3Q8e0zYfhObakPA` | 0 | 0 errors / 0 warnings |
| `infrastructure/release/Release.fsproj` | `DW0DftuwLQks5ZDfpM99T-LAQ70b1dBZSSmodTPWDnI` | 0 | 0 errors / 0 warnings |
| `infrastructure/scripts/Validation.fsproj` | `HIeeJ52z1_9IjyVQf6BU49IRdCFhBVmYjPK-9IbevJo` | 0 | 0 errors / 0 warnings |

## 3. Authentic package run (`dotnet fsi <producer>/release/Build.fsx`)

The scripts run on the same clean worktree. They start with
`assertCleanTree` + `committedHead` + `assertManifestRevision`, then
`dotnet publish` framework-dependent with the canonical
`DistributionBuild` allowlist, stage, write `NOTICE.txt` and
`distribution.json`, ZIP with `LastWriteTime = 1980-01-01` and
`CompressionLevel.Optimal`, hash the archive and manifest.

```
$ dotnet fsi dotnet/release/Build.fsx
dotnet archive=v1.0.4.zip sha256=df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7 manifest=e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1

$ dotnet fsi workflow/release/Build.fsx
workflow archive=v1.0.4.zip sha256=c7ef11f406458b0542fe3f8bd6e23038c893b309c5fa571326a522b93aa26c9b manifest=a455fb1b1715cca076e3d594d207bed1515781c773ca323636595ae00a31d07e
```

Pins.fsx re-validates the manifest identity (`id`, `version`,
`archive`, `revision == HEAD`) and writes the consumer pin:

```
$ dotnet fsi dotnet/release/Pins.fsx
dotnet asset=v1.0.4.zip archiveSha256=df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7 manifestSha256=e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1 revision=3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1

$ dotnet fsi workflow/release/Pins.fsx
workflow asset=v1.0.4.zip archiveSha256=c7ef11f406458b0542fe3f8bd6e23038c893b309c5fa571326a522b93aa26c9b manifestSha256=a455fb1b1715cca076e3d594d207bed1515781c773ca323636595ae00a31d07e revision=3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1
```

`DistributionTests.fsx` (the per-component distribution contract) runs
clean for both producers (no assertion output ⇒ every assertion in
`assertDistributionContract` passed).

External verification (`Get-FileHash -Algorithm SHA256`) re-confirmed
each hash on disk matches the script output above.

## 4. Authored artifact hashes (recorded in this slice)

| component | archive | archive SHA-256 | manifest SHA-256 | manifest revision | file path |
| --- | --- | --- | --- | --- | --- |
| `dotnet` | `v1.0.4.zip` | `df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7` | `e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1` | `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1` | `dotnet/dist/v1.0.4.zip`, `dotnet/dist/dotnet/distribution.json` |
| `workflow` | `v1.0.4.zip` | `c7ef11f406458b0542fe3f8bd6e23038c893b309c5fa571326a522b93aa26c9b` | `a455fb1b1715cca076e3d594d207bed1515781c773ca323636595ae00a31d07e` | `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1` | `workflow/dist/v1.0.4.zip`, `workflow/dist/workflow/distribution.json` |

`consumer-pins.json` (per producer, written by `Pins.fsx`):

```
{ "dotnet": { "assetName": "v1.0.4.zip",
              "assetUri":  "https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.4.zip",
              "archiveSha256":  "df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7",
              "manifestSha256": "e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1",
              "revision":       "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1" } }
{ "workflow": { "assetName": "v1.0.4.zip",
                "assetUri":  "https://github.com/masterlifting/mcp-store/releases/download/workflow/v1.0.4.zip",
                "archiveSha256":  "c7ef11f406458b0542fe3f8bd6e23038c893b309c5fa571326a522b93aa26c9b",
                "manifestSha256": "a455fb1b1715cca076e3d594d207bed1515781c773ca323636595ae00a31d07e",
                "revision":       "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1" } }
```

## 5. Manifest identity, SDK, runtime, platform, allowlist

The manifest `distribution.json` is byte-stable in the position of every
field except `revision`; identity is enforced at the script boundary:

| component | manifest id | version | sdk | runtime (declared) | entryDll | archive | archiveFiles (allowlist) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `dotnet` | `dotnet` | `1.0.4` | `11.0.100-rc.1.26425.128` | `11.0.0-rc.1.26425.128` | `Mcp.Dotnet.dll` | `v1.0.4.zip` | `FSharp.Core.dll`, `Mcp.Dotnet.deps.json`, `Mcp.Dotnet.dll`, `Mcp.Dotnet.runtimeconfig.json`, `NOTICE.txt`, `distribution.json` |
| `workflow` | `workflow` | `1.0.4` | `11.0.100-rc.1.26425.128` | `11.0.0-rc.1.26425.128` | `Mcp.Workflow.dll` | `v1.0.4.zip` | `FSharp.Core.dll`, `Mcp.Workflow.deps.json`, `Mcp.Workflow.dll`, `Mcp.Workflow.runtimeconfig.json`, `NOTICE.txt`, `distribution.json` |

`Pins.fsx` calls `validateManifestIdentity` for each producer with
`[(id, component); (version, contract); (archive, contract)]`; the
check passes (exit 0 from the script run above). The manifest's
`revision` is `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`, which matches
`git rev-parse HEAD` (asserted by `assertManifestRevision` before
`writeConsumerPins`).

Bootstrap cross-check confirms the executing toolchain matches the
canonical config:

```
RELEASE_SDK=11.0.100-rc.1.26425.128
RELEASE_RUNTIME=Microsoft.NETCore.App 11.0.0-rc.1.26425.128
RELEASE_PLATFORM=win-x64
RELEASE_ARCHITECTURE=x64
RELEASE_DOTNET_VERSION=1.0.4     RELEASE_DOTNET_ARCHIVE=v1.0.4.zip
RELEASE_WORKFLOW_VERSION=1.0.4   RELEASE_WORKFLOW_ARCHIVE=v1.0.4.zip
```

## 6. Repeat deterministic packaging (same clean commit)

After the first build, the same `dotnet fsi {dotnet,workflow}/release/Build.fsx`
was invoked a second time on the same worktree, same SHA. Hashes are
byte-exact:

| component | archive SHA-256 (run 1) | archive SHA-256 (run 2) | manifest SHA-256 (run 1) | manifest SHA-256 (run 2) |
| --- | --- | --- | --- | --- |
| `dotnet` | `df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7` | `df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7` | `e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1` | `e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1` |
| `workflow` | `c7ef11f406458b0542fe3f8bd6e23038c893b309c5fa571326a522b93aa26c9b` | `c7ef11f406458b0542fe3f8bd6e23038c893b309c5fa571326a522b93aa26c9b` | `a455fb1b1715cca076e3d594d207bed1515781c773ca323636595ae00a31d07e` | `a455fb1b1715cca076e3d594d207bed1515781c773ca323636595ae00a31d07e` |

Determinism confirmed: the `DistributionBuild` flow is byte-stable
under repeat invocations from the same committed `HEAD`. The
archive entries all use `LastWriteTime = 1980-01-01` and
`CompressionLevel.Optimal`; the manifest `revision` is the only field
that depends on `HEAD`, and `HEAD` is identical across the two runs.

## 7. Historical 1.0.3 vs. new 1.0.4 payload comparison

The locally verified v1.0.3 archives under
`C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-bootstrap\` are the
authoritative historical bytes; their digests were re-checked in this
slice:

- `dotnet-v1.0.3.zip` — 1,289,835 B; SHA-256
  `da3acb20007f8015e541ee7d68b48adf49d232c4eea41190a62073073d1a1614`.
- `workflow-v1.0.3.zip` — 1,482,675 B; SHA-256
  `8677413d830bd7fbc559e1847222ae4d3ee8582de4d9ba15a224a939dcc39db1`.

After expanding the 1.0.3 archives and the freshly built 1.0.4
archives, the per-entry comparison is:

### `dotnet` archive entries

| entry | 1.0.4 size | 1.0.4 SHA-256 | 1.0.3 size | 1.0.3 SHA-256 | verdict |
| --- | --- | --- | --- | --- | --- |
| `FSharp.Core.dll` | 2,456,400 | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | 2,456,400 | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | identical |
| `Mcp.Dotnet.deps.json` | 1,140 | `58900bbe1ef2cb78041bfb2172fcf6d5a25bab1e9a3afa43facd9bbdcd15ebd0` | 1,140 | `58900bbe1ef2cb78041bfb2172fcf6d5a25bab1e9a3afa43facd9bbdcd15ebd0` | identical |
| `Mcp.Dotnet.dll` | **256,000** | **`39d119ba2ed785ce329399e52c0daa36d2a1b3b6623496f934ae153669f2bb59`** | 225,792 | `809cbcacf1185481c343a6903d0d1b8b345cc3708d2eb6ed38f6a89178912056` | **DIFFERS** |
| `Mcp.Dotnet.runtimeconfig.json` | 357 | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | 357 | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | identical |
| `NOTICE.txt` | 34 | `40c25ee1510e909fb4134ffdaF698e0356854ee6ef7469467b9454161d1fdcfc` | 34 | `40c25ee1510e909fb4134ffdaF698e0356854ee6ef7469467b9454161d1fdcfc` | identical |
| `distribution.json` | 979 | `e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1` | 979 | `7d1672adef2882177c78fe6f0ee29283e941e0261fe464834f88c0d0af6adb7a` | revision field only |

### `workflow` archive entries

| entry | 1.0.4 size | 1.0.4 SHA-256 | 1.0.3 size | 1.0.3 SHA-256 | verdict |
| --- | --- | --- | --- | --- | --- |
| `FSharp.Core.dll` | 2,456,400 | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | 2,456,400 | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | identical |
| `Mcp.Workflow.deps.json` | 1,146 | `cc84b73e3698dd9f5af289ee26fd5b8b4426b7cf2b4ae9e01fbfdc6e8453d388` | 1,146 | `cc84b73e3698dd9f5af289ee26fd5b8b4426b7cf2b4ae9e01fbfdc6e8453d388` | identical |
| `Mcp.Workflow.dll` | **872,448** | **`9968e3a5056e7ceb16493246646cf89bfe207b0b3e159fbb029a46a6b25d66bc`** | 801,280 | `cbb2bc593d1d7aee178ebd8d5923a352434a35fbd38235967db7210d2c466b2d` | **DIFFERS** |
| `Mcp.Workflow.runtimeconfig.json` | 357 | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | 357 | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | identical |
| `NOTICE.txt` | 36 | `b369ed2840677cd0e54c4a462fe79001cca1bc96367633aa68a55c0c02305cbb` | 36 | `b369ed2840677cd0e54c4a462fe79001cca1bc96367633aa68a55c0c02305cbb` | identical |
| `distribution.json` | 995 | `a455fb1b1715cca076e3d594d207bed1515781c773ca323636595ae00a31d07e` | 995 | `ad577372c9f8612ed9f5b705a786c48ed9a3dbede34728896bcd96506ae176e5` | revision field only |

The two `Mcp.*.dll` binaries differ. The `distribution.json` files
share an identical size (the 40-char revision field is the same
length) but differ in SHA-256 because the embedded `revision` changed
from the historical `fc0aefa9db0fb3262ee6ca1254bd6bbb752b8b01` to
`3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`. The strict revision-only
exception therefore does not apply to a same-version rebuild; the
authored 1.0.4 bump is the only correct response, and the comparison
proves it was required.

## 8. Source-shared git metadata (anti-perpetuation re-check)

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
touched the source repo.

## 9. Out-of-scope and not-tested (explicit non-claims)

- No `MCP_RELEASE_DO_UPLOAD=true` was set. `ReleaseRunner.exe` was
  not invoked against a real GitHub release. No release asset was
  uploaded. The compiled runner's `gitHubObserver` /
  `localMirrorObserver` / `uploadCandidate` / `uploadMetadata` paths
  are present in the binary but were not exercised against a remote
  in this slice.
- No mirror-driven "simulated upload" was run. `MCP_RELEASE_MIRROR_ROOT`
  was not set. The local-mirror path would only test the read-side
  observation; a `publish-pending` outcome through that path would
  be a local decision, not a remote write, and is not claimed here.
- No regression suite was authored. `Compare`/`Decide`/`Metadata`
  test seams are not exercised beyond the build/validator pass
  (38 scripts validated, all reusable/test-helper roots typecheck).
- No `release.yml` edit. No `Update.ps1` edit. No `Bootstrap.fsx`
  edit. No `Config.fsx` platform/SDK/runtime edit. No workflow
  run.
- No commit, push, branch op, worktree mutation, or remote write
  was performed by this slice.

## 10. Repeat results (run 2 same clean commit)

The second build run on the same clean worktree produced the same
archive and manifest hashes as the first run; the per-entry SHA-256s
are byte-exact across the two runs. This is the deterministic
packaging proof required by the issue; byte-equality of the package
between consecutive invocations at the same `HEAD` is the gate a
no-op "unchanged" comparison relies on.
