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

---

## B verification at HEAD f13594db7787e2a0300e1151c8e8697cbf98e91d

B = real commit on `ghi-24` whose only change versus parent A (`d556c20`) is this `docs/GHI-24/proof.md` section. Compiled source, producer `Config.fsx` / `Build.fsx` / `Pins.fsx`, infrastructure scripts, and SDK2 `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>` properties are unchanged. Re-ran from the clean B HEAD with the same MCP tool, child `ProcessStartInfo` invocation, and token-removal discipline as A. A-bytes were re-hashed read-only first and retained IMMUTABLY (no `Copy-Item -Force` overwrite / no reconstruct / no repack / no metadata-only substitute).

### Rehash-A (read-only, before any B work)

| A path | Rehash SHA-256 (lower) | Pre-B expected |
| --- | --- | --- |
| `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\dotnet\v1.0.4.zip` | `8e5265b07785516575db639b8274f11338d770e585742767657397ca3630df1c` | matches A record above |
| `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-a-packages\workflow\v1.0.4.zip` | `1ddabf172c3abe5bc5776303908edd536718b2ccf2e18ede889e4873ef3b5c21` | matches A record above |

A-bytes stay at their original paths with the original SHAs; nothing under `ghi-24-a-packages/` was overwritten, reconstructed, or repacked.

### Helper / producer builds from clean B (`dotnet_build` MCP, `Release`, scoped)

| Project | Build ID | Outcome |
| --- | --- | --- |
| `infrastructure/release/Release.fsproj` | `p8_L2DLeS27_C1n4dk1pgzUjoUG2cBToVO8zbDFkn8g` | 0 / 0 |
| `dotnet/Mcp.Dotnet.fsproj` | `0DSXOdtoMOSPCMAX96Tn3dpAXiVhlmkHG72Gt7mcIiI` | 0 / 0 |
| `workflow/Mcp.Workflow.fsproj` | `N73AJK85IWjxgK6gMCxrbADLD5Y1Hdh_9dItJdyvFB4` | 0 / 0 |

### Canonical B producer packaging (child `ProcessStartInfo`, tokens REMOVED, `MCP_RELEASE_DO_UPLOAD` not set)

Logged at `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-b-packages\producer-phases\{dotnet-build,dotnet-pins,dotnet-tests,workflow-build,workflow-pins,workflow-tests}.log` (also `producer-phases-repeat/` for the byte-exact repeat). All six phases exit=0. First-pass stdout:

- `dotnet/release/Build.fsx`: `dotnet archive=v1.0.4.zip sha256=8f64d0203791655ee4a1027f137a39ee69c919664132fc4703b2409b19c91ea6 manifest=7a4b8259424cf9b730abf25dca314bb73ca03f0ca52d972d6f8591fb382e621c`
- `dotnet/release/Pins.fsx`: `dotnet asset=v1.0.4.zip archiveSha256=8f64d0203791655ee4a1027f137a39ee69c919664132fc4703b2409b19c91ea6 manifestSha256=7a4b8259424cf9b730abf25dca314bb73ca03f0ca52d972d6f8591fb382e621c revision=f13594db7787e2a0300e1151c8e8697cbf98e91d`
- `workflow/release/Build.fsx`: `workflow archive=v1.0.4.zip sha256=f18573dd34bf572c616f2af8be819b1c2e1a795ec9872cae36c68ba2ca072d64 manifest=b96bcb4661bdc0bbd0a335002575731c081614b7fdfca501064af95ac71ab9b5`
- `workflow/release/Pins.fsx`: `workflow asset=v1.0.4.zip archiveSha256=f18573dd34bf572c616f2af8be819b1c2e1a795ec9872cae36c68ba2ca072d64 manifestSha256=b96bcb4661bdc0bbd0a335002575731c081614b7fdfca501064af95ac71ab9b5 revision=f13594db7787e2a0300e1151c8e8697cbf98e91d`

Network wrapper smoke intentionally NOT re-run at B (per request; A pass already proved the transport-failed structured `status=failed` outcome, no extra failure-safety needed).

### B source == B destination SHA pairs (binary copy into distinct verified parent)

| Source path | Destination path | SHA-256 (lower) | Size (B) |
| --- | --- | --- | --- |
| `D:\repos\private\mcp-store\dotnet\dist\v1.0.4.zip` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-b-packages\dotnet\v1.0.4.zip` | `8f64d0203791655ee4a1027f137a39ee69c919664132fc4703b2409b19c91ea6` | 1,301,967 |
| `D:\repos\private\mcp-store\workflow\dist\v1.0.4.zip` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-b-packages\workflow\v1.0.4.zip` | `f18573dd34bf572c616f2af8be819b1c2e1a795ec9872cae36c68ba2ca072d64` | 1,506,569 |
| `D:\repos\private\mcp-store\dotnet\dist\dotnet\distribution.json` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-b-packages\dotnet\distribution.json` | `7a4b8259424cf9b730abf25dca314bb73ca03f0ca52d972d6f8591fb382e621c` | 979 |
| `D:\repos\private\mcp-store\workflow\dist\workflow\distribution.json` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-b-packages\workflow\distribution.json` | `b96bcb4661bdc0bbd0a335002575731c081614b7fdfca501064af95ac71ab9b5` | 995 |
| `D:\repos\private\mcp-store\dotnet\dist\consumer-pins.json` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-b-packages\dotnet\consumer-pins.json` | `5380fc2b513ae61dc361720cb83d3afec8a387b20b936e06d0b2835deb742f2f` | 396 |
| `D:\repos\private\mcp-store\workflow\dist\consumer-pins.json` | `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-b-packages\workflow\consumer-pins.json` | `1cf6fe672dd08f69a2ce623b4434bc0e8a6a1cf75fed604e6730637c78141013` | 400 |

Source SHA == destination SHA on every pair. Distinct parent from A's `ghi-24-a-packages`.

### B repeat packaging byte-exact at the same HEAD

Second producer packaging pass (`producer-phases-repeat/`) produced identical hashes for both producers:

| Component | Run #1 archiveSha256 | Run #2 archiveSha256 | Run #1 manifestSha256 | Run #2 manifestSha256 |
| --- | --- | --- | --- | --- |
| dotnet | `8f64d0203791655ee4a1027f137a39ee69c919664132fc4703b2409b19c91ea6` | `8f64d0203791655ee4a1027f137a39ee69c919664132fc4703b2409b19c91ea6` | `7a4b8259424cf9b730abf25dca314bb73ca03f0ca52d972d6f8591fb382e621c` | `7a4b8259424cf9b730abf25dca314bb73ca03f0ca52d972d6f8591fb382e621c` |
| workflow | `f18573dd34bf572c616f2af8be819b1c2e1a795ec9872cae36c68ba2ca072d64` | `f18573dd34bf572c616f2af8be819b1c2e1a795ec9872cae36c68ba2ca072d64` | `b96bcb4661bdc0bbd0a335002575731c081614b7fdfca501064af95ac71ab9b5` | `b96bcb4661bdc0bbd0a335002575731c081614b7fdfca501064af95ac71ab9b5` |

Post-repeat SHA of `D:\repos\private\mcp-store\dotnet\dist\v1.0.4.zip` (`8f64d0203791655ee4a1027f137a39ee69c919664132fc4703b2409b19c91ea6`) and `…\workflow\dist\v1.0.4.zip` (`f18573dd34bf572c616f2af8be819b1c2e1a795ec9872cae36c68ba2ca072d64`) match the saved `ghi-24-b-packages/` copies.

### Production `ReleaseCompare.compare` A-vs-B verdict

Loaded the production `Compare.fsx` (no proxy, no reduced surface) and invoked `ReleaseCompare.compare contract obsA obsB bHead` where:
- `obsA` = `ReleaseCompare.observe` on `…\ghi-24-a-packages\dotnet\v1.0.4.zip` (A raw bytes),
- `obsB` = `ReleaseCompare.observe` on `…\ghi-24-b-packages\dotnet\v1.0.4.zip` (B raw bytes),
- `bHead` = `f13594db7787e2a0300e1151c8e8697cbf98e91d` (B evaluated HEAD).

| Component | Decision | A published archive sha256 | A published manifest sha256 | A published revision | B evaluated HEAD |
| --- | --- | --- | --- | --- | --- |
| dotnet | `RevisionOnlyEquivalent` | `8e5265b07785516575db639b8274f11338d770e585742767657397ca3630df1c` | `0202f7193f50b2bb46793323723d5cfac5c5f08e07a195218d4941fac96e0992` | `d556c20d576d6eba2e66ec4456641267af8c8595` | `f13594db7787e2a0300e1151c8e8697cbf98e91d` |
| workflow | `RevisionOnlyEquivalent` | `1ddabf172c3abe5bc5776303908edd536718b2ccf2e18ede889e4873ef3b5c21` | `7f5e207f6c4b1b475eea97dec0ad09f98c56849c17f660f6ccecd74b2c5fae91` | `d556c20d576d6eba2e66ec4456641267af8c8595` | `f13594db7787e2a0300e1151c8e8697cbf98e91d` |

Production `ReleaseCompare.compare` reports `Decision = RevisionOnlyEquivalent` for both producers. Published (A) hashes / manifest sha / revision are reported as the published values; B evaluated HEAD is reported as the evaluated value. No candidate value was emitted as published. Captured fields come strictly from `obsA` / `obsB` observations and `bHead`; nothing reconstructed or proxied.

### Per-entry payload / compressed-byte / CRC32 cross-check

| Component | Entry | A payload sha256 | B payload sha256 | A compressed | B compressed | A CRC32 | B CRC32 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| dotnet | `FSharp.Core.dll` | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | 1,184,787 | 1,184,787 | `0xFEDCC537` | `0xFEDCC537` |
| dotnet | `Mcp.Dotnet.deps.json` | `58900bbe1ef2cb78041bfb2172fcf6d5a25bab1e9a3afa43facd9bbdcd15ebd0` | `58900bbe1ef2cb78041bfb2172fcf6d5a25bab1e9a3afa43facd9bbdcd15ebd0` | 460 | 460 | `0x2B70B945` | `0x2B70B945` |
| dotnet | `Mcp.Dotnet.dll` | `b82cc36bfa922856bbaa16e482f7693db5a5fd4902341c454566769ec07bb825` | `b82cc36bfa922856bbaa16e482f7693db5a5fd4902341c454566769ec07bb825` | 115,313 | 115,313 | `0xB11BCA98` | `0xB11BCA98` |
| dotnet | `Mcp.Dotnet.runtimeconfig.json` | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | 227 | 227 | `0x1DC9F51E` | `0x1DC9F51E` |
| dotnet | `NOTICE.txt` | `40c25ee1510e909fb4134ffdaf698e0356854ee6ef7469467b9454161d1fdcfc` | `40c25ee1510e909fb4134ffdaf698e0356854ee6ef7469467b9454161d1fdcfc` | 36 | 36 | `0xC63AAB53` | `0xC63AAB53` |
| dotnet | `distribution.json` (manifest entry) | `0202f7193f50b2bb46793323723d5cfac5c5f08e07a195218d4941fac96e0992` | `7a4b8259424cf9b730abf25dca314bb73ca03f0ca52d972d6f8591fb382e621c` | 454 | 456 | `0x1BD6A3DD` | `0x3BA5F44D` |
| workflow | `FSharp.Core.dll` | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | `ee7306b40cb17e9029f585150d9315eeb25a8affd3faa502c5a3afcd7323fa29` | 1,184,787 | 1,184,787 | `0xFEDCC537` | `0xFEDCC537` |
| workflow | `Mcp.Workflow.deps.json` | `cc84b73e3698dd9f5af289ee26fd5b8b4426b7cf2b4ae9e01fbfdc6e8453d388` | `cc84b73e3698dd9f5af289ee26fd5b8b4426b7cf2b4ae9e01fbfdc6e8453d388` | 462 | 462 | `0xBD94CC8F` | `0xBD94CC8F` |
| workflow | `Mcp.Workflow.dll` | `3025123592da37146bdb71e07bfe79c1c3ec6c065356e23ef7ff8abd2bfaaee1` | `3025123592da37146bdb71e07bfe79c1c3ec6c065356e23ef7ff8abd2bfaaee1` | 319,899 | 319,899 | `0xE421A881` | `0xE421A881` |
| workflow | `Mcp.Workflow.runtimeconfig.json` | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | `7ed127d518025dc407a379eb73f23a389adcbbc780fde71314303e0ca1df615b` | 227 | 227 | `0x1DC9F51E` | `0x1DC9F51E` |
| workflow | `NOTICE.txt` | `b369ed2840677cd0e54c4a462fe79001cca1bc96367633aa68a55c0c02305cbb` | `b369ed2840677cd0e54c4a462fe79001cca1bc96367633aa68a55c0c02305cbb` | 38 | 38 | `0xEACFF967` | `0xEACFF967` |
| workflow | `distribution.json` (manifest entry) | `7f5e207f6c4b1b475eea97dec0ad09f98c56849c17f660f6ccecd74b2c5fae91` | `b96bcb4661bdc0bbd0a335002575731c081614b7fdfca501064af95ac71ab9b5` | 457 | 456 | `0x882A227B` | `0xCC5A1C0F` |

All non-manifest payload bytes are byte-identical between A and B (same payload sha256, compressed size, CRC32, Method=Deflate (`8us`), Flags=0x0). The DLL and runtime payload hashes are stable across the unrelated `docs/GHI-24/proof.md`-only commit — no SHA injection, no compiler non-determinism. The only compressed-byte difference is the embedded `distribution.json` itself, whose uncompressed body is 979 / 995 bytes in both A and B, and the only character change is the 40-char hex `revision` value (A=`d556c20d576d6eba2e66ec4456641267af8c8595`, B=`f13594db7787e2a0300e1151c8e8697cbf98e91d`). The production `compareManifestBodies` substitution check (`publishedText.Replace(published.Revision, candidate.Revision) = candidateText`) is satisfied without any reconstructed-proxy comparator or unproven-compiler label.

### B manifest revision / sdk (both consumers)

- B manifest `revision` (both producers, both `…\dist\{component}\distribution.json` and `…\dist\consumer-pins.json`) = `f13594db7787e2a0300e1151c8e8697cbf98e91d`.
- B manifest `sdk` = `11.0.100-rc.1.26425.128` (unchanged from A — same canonical `Config.fsx` projection).
- B manifest `id` / `version` / `archive` = `dotnet|workflow` / `1.0.4` / `v1.0.4.zip` (unchanged from A; no version bump).
- B consumer-pins `archiveSha256` / `manifestSha256` / `revision` equal the B hashes/revision above for both producers.
- B `…\dotnet\dist\consumer-pins.json` sha256 = `5380fc2b513ae61dc361720cb83d3afec8a387b20b936e06d0b2835deb742f2f`.
- B `…\workflow\dist\consumer-pins.json` sha256 = `1cf6fe672dd08f69a2ce623b4434bc0e8a6a1cf75fed604e6730637c78141013`.

### B preservation and audit-ready evidence

- Distinct pre-approved Temp parent `C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-b-packages\` created and used for B (`Test-Path` true before copy); six raw B-bytes + producer-phase logs + repeat-phase logs + `compare-a-b.fsx` script + repeat script preserved there. Nothing under `ghi-24-a-packages/` was touched (rehash-A confirmed).
- No reconstruction / repack / metadata-only substitute. Bytes preserved as produced by the canonical `Build.fsx` + `Pins.fsx` child `ProcessStartInfo` invocations, copied with `Copy-Item -Force`, source SHA == destination SHA on every pair.
- `git status` after this pass shows `docs/GHI-24/proof.md` modified (uncommitted append); HEAD remains `f13594db7787e2a0300e1151c8e8697cbf98e91d`. No commits, no `.git` masks / reset / temp commits / worktrees / branch / global props / secret reads / publish / runtime mutations; no production / test edits; native audit reads the uncommitted append.