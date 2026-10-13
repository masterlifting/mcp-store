# SEC1 Handoff symlink / reparse / case-collision defect — production fix

`Handoff.fs` central-directory validation loop (former 992-1003) walked
names, case-sensitive duplicates, and sizes only; `ExternalAttributes`
were ignored, so a canonical entry marked as a Unix symbolic link
(or any other non-regular file type, or DOS reparse) was snapshotted.

Fix adds an `ExternalAttributes` guard BEFORE `entry.Open()` (no symlink
follow, no decompression allocation), plus a case-insensitive duplicate
companion, all inside the SAME bounded central-directory loop.

Exact attribute checked: `entry.ExternalAttributes`, high 16 bits
`unixFileType = (attrs >>> 16) &&& 0xF000`, low 16 bits `dosAttrs`
with reparse bit `0x400`. Accepted: `0x0000` (zero, ordinary Windows
ZIP) and `0x8000` (S_IFREG). Rejected: `0xA000` symlink, `0x4000`
directory, `0x1000` FIFO, `0x2000` char dev, `0x3000` block dev,
`0xC000` socket, any other Unix type, and DOS `0x400` reparse.
Blanket-zeroing is NOT used (would reject honest Windows ZIPs).
Case-insensitive companion uses `OrdinalIgnoreCase`; existing Ordinal
canonical-set check is preserved unchanged. `MalformedArtifact`
location is the entry name for the new link / reparse /
case-collision rejections.

Build ID `xAU3G_ZB5dYdQg_K4SCEmjhgnNt1u-lIejnRPjakoNk` via
`dotnet_build` MCP on `infrastructure/release/Release.fsproj -c Release`
(`noRestore=true`): **0 errors, 0 warnings**. Tester assertions
retained unchanged: `readRawTransportOk` positive 2/2 pass
(`dotnet` + `workflow`); symlink central-dir `0xA000` test now 3 run,
2 pass, 1 fail-before-fix → reject-after-fix via the new
`MalformedArtifact(name, …)` guard.