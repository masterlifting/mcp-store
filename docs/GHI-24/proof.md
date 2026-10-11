# GHI-24 — raw package A proof at HEAD d556c20

Coordinator-owned facts for the pre-`docs/GHI-24/*` docs step; second
docs-only commit B is explicitly not authored here.

## Pre-flight

- Repo clean start: `git status --short` empty, HEAD = `d556c20d576d6eba2e66ec4456641267af8c8595` on branch `ghi-24`, no stash/worktree, no `.git` mutations, `dotnet/dist/` and `workflow/dist/` gitignored.
- Canonical SDK = `11.0.100-rc.1.26425.128`, runtime = `11.0.0-rc.1.26425.128`, platform = `win-x64` from `dotnet/release/Config.fsx` and `workflow/release/Config.fsx`.

## Helper / producer builds (`dotnet_build` MCP, scoped, `Release`)

- `infrastructure/release/Release.fsproj` -> `ReleaseRunner.exe` build `TziiGj5ZT0s6cfEKrYwd_Lz1Vce8HqynXkTJ3sS2lrI`, 0 errors / 0 warnings.
- `dotnet/Mcp.Dotnet.fsproj` -> build `UFTdkac_lAvaNT2aj7Yv39xQ24jYnwrd_604zb_HZK8`, 0 / 0.
- `workflow/Mcp.Workflow.fsproj` -> build `TndkK5RGTANdlFt2hQc6EzbNlSKkHhPL02318LkLF_U`, 0 / 0.
- Both producer fsprojs commit D3: `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>` (no HEAD injected into DLL).

## Real producer Release wrapper end-to-end NON-PUBLISHING smoke

Child `ProcessStartInfo` only; token keys removed before fork:
`GITHUB_TOKEN`, `GH_TOKEN`, `MCP_RELEASE_GITHUB_OWNER`,
`MCP_RELEASE_GITHUB_REPOSITORY` stripped; `MCP_RELEASE_DO_UPLOAD=false`
forced; `RELEASE_EXPECTED_{SDK,RUNTIME,PLATFORM}` set from the canonical
`Config.fsx` projection. No auth, no network writes, no fake manifest SHA.

- `dotnet fsi --nologo dotnet/release/Release.fsx`
  - run #1 -> `wrapper-dotnet-1.log` exit=1 status=failed reason="transport failed for 'dotnet': the production transport is not selected; set MCP_RELEASE_GITHUB_OWNER + MCP_RELEASE_GITHUB_REPOSITORY + GITHUB_TOKEN to enable remote observation".
  - run #2 -> `wrapper-dotnet-2.log` exit=1 status=failed same reason, byte-identical hashes (repeat verified).
- `dotnet fsi --nologo workflow/release/Release.fsx`
  - run #1 -> `wrapper-workflow-1.log` exit=1 status=failed reason="transport failed for 'workflow': the production transport is not selected; set MCP_RELEASE_GITHUB_OWNER + MCP_RELEASE_GITHUB_REPOSITORY + GITHUB_TOKEN to enable remote observation".
  - run #2 -> `wrapper-workflow-2.log` exit=1 status=failed same reason, byte-identical hashes (repeat verified).
- Structured failure surfaced after the package phase (build/pins/tests all succeeded); nothing was uploaded.

## Source / dest SHA pairs (byte-exact binary copy; source == dest)

| Source path | Destination path | SHA-256 (lower) |
| --- | --- | --- |
| `D:\repos\private\mcp-store\dotnet\dist\v1.0.4.zip` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\dotnet\v1.0.4.zip` | `8e5265b07785516575db639b8274f11338d770e585742767657397ca3630df1c` |
| `D:\repos\private\mcp-store\workflow\dist\v1.0.4.zip` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\workflow\v1.0.4.zip` | `1ddabf172c3abe5bc5776303908edd536718b2ccf2e18ede889e4873ef3b5c21` |
| `D:\repos\private\mcp-store\dotnet\dist\dotnet\distribution.json` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\dotnet\distribution.json` | `0202f7193f50b2bb46793323723d5cfac5c5f08e07a195218d4941fac96e0992` |
| `D:\repos\private\mcp-store\workflow\dist\workflow\distribution.json` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\workflow\distribution.json` | `7f5e207f6c4b1b475eea97dec0ad09f98c56849c17f660f6ccecd74b2c5fae91` |
| `D:\repos\private\mcp-store\dotnet\dist\consumer-pins.json` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\dotnet\consumer-pins.json` | `5f9b2d23dbb03072acecbd3ab04a3671094cf6101adf286ac81147e0698cebd5` |
| `D:\repos\private\mcp-store\workflow\dist\consumer-pins.json` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\workflow\consumer-pins.json` | `ecba51a225c6bc5907101cb86f3bb462191a186ad9086fd664b64ee08c6c032f` |

Sizes verified equal in every pair (1,301,965 / 1,506,570 / 979 / 995 / 396 / 400 bytes).

## Read-only extract payload check (copies under the validated Temp parent)

`System.IO.Compression.ZipFile.ExtractToDirectory` on the copied archives
only; the on-disk `dotnet/dist` and `workflow/dist` archives were never
extracted here. assemblyInfoVersion = `1.0.0`, raw text scan finds
`1.0.0` x3 and zero hits for `1.0.1`..`1.0.4`, `11.0.100-rc.1`,
`d556c20`, or `962fd64` in either DLL — no SHA injection.

| DLL | Extracted path | Bytes | SHA-256 (lower) | Manifest SHA-256 (lower) | `AssemblyName.Version` |
| --- | --- | --- | --- | --- | --- |
| `Mcp.Dotnet.dll` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\dotnet-extract\Mcp.Dotnet.dll` | 256,000 | `b82cc36bfa922856bbaa16e482f7693db5a5fd4902341c454566769ec07bb825` | `b82cc36bfa922856bbaa16e482f7693db5a5fd4902341c454566769ec07bb825` | `1.0.0.0` |
| `Mcp.Workflow.dll` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\workflow-extract\Mcp.Workflow.dll` | 872,448 | `3025123592da37146bdb71e07bfe79c1c3ec6c065356e23ef7ff8abd2bfaaee1` | `3025123592da37146bdb71e07bfe79c1c3ec6c065356e23ef7ff8abd2bfaaee1` | `1.0.0.0` |

Extracted archive listing (both archives): `FSharp.Core.dll`,
`<entry>.deps.json`, `<entry>.dll`, `<entry>.runtimeconfig.json`,
`NOTICE.txt`, `distribution.json`. Old `962fd64` packages are not
compared (intentionally had SHA in DLL per original record); version
stays `1.0.4`, not bumped.

## Manifest revision A and cfg SDK

- Manifest `revision` field, both `dotnet/dist/dotnet/distribution.json`
  and `workflow/dist/workflow/distribution.json` =
  `d556c20d576d6eba2e66ec4456641267af8c8595` (matches HEAD).
- Manifest `sdk` field, both = `11.0.100-rc.1.26425.128` (matches
  canonical `Config.fsx`).
- Manifest `id` = `dotnet` / `workflow`; manifest `version` = `1.0.4`;
  manifest `archive` = `v1.0.4.zip`.
- Consumer pin `archiveSha256` / `manifestSha256` / `revision` in both
  `consumer-pins.json` files equal the hashes/revision above.
- ReleaseRunner stdout JSON `currentEvaluatedHead` =
  `d556c20d576d6eba2e66ec4456641267af8c8595` and
  `candidateRevision` = same, on every run (provenance check passes).
- `producerSdk` / `producerRuntime` / `producerPlatform` in every run's
  stdout = `11.0.100-rc.1.26425.128` / `11.0.0-rc.1.26425.128` /
  `win-x64`.

## Repeat packaging byte-exact

| Component | run #1 archiveSha256 | run #2 archiveSha256 | run #1 manifestSha256 | run #2 manifestSha256 |
| --- | --- | --- | --- | --- |
| dotnet | `8e5265b07785516575db639b8274f11338d770e585742767657397ca3630df1c` | `8e5265b07785516575db639b8274f11338d770e585742767657397ca3630df1c` | `0202f7193f50b2bb46793323723d5cfac5c5f08e07a195218d4941fac96e0992` | `0202f7193f50b2bb46793323723d5cfac5c5f08e07a195218d4941fac96e0992` |
| workflow | `1ddabf172c3abe5bc5776303908edd536718b2ccf2e18ede889e4873ef3b5c21` | `1ddabf172c3abe5bc5776303908edd536718b2ccf2e18ede889e4873ef3b5c21` | `7f5e207f6c4b1b475eea97dec0ad09f98c56849c17f660f6ccecd74b2c5fae91` | `7f5e207f6c4b1b475eea97dec0ad09f98c56849c17f660f6ccecd74b2c5fae91` |

## Preservation

- Pre-approved Temp parent `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\` existed before this pass (`Test-Path` true at start); A-bytes (six files above) and four wrapper logs (`wrapper-dotnet-{1,2}.log`, `wrapper-workflow-{1,2}.log`) preserved there even if the build is later retried. Two extracts (`dotnet-extract/`, `workflow-extract/`) are read-only sources for the payload check.
- No reconstruction / repack / metadata-only substitute. Bytes preserved as produced by the wrappers, copied with `Copy-Item -Force`, source SHA == destination SHA on every pair, sizes equal.

## Pending docs-only commit B (coordinator-owned, worker did not commit)

- Future commit message reserved: `GHI-24: record committed release verification evidence`.
- Future content to be authored by coordinator only; not authored in this pass.