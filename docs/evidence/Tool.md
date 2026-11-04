# Local tool experiment evidence — GHI-46

Review appendix to [Distribution.md](../Distribution.md), for PR #61. This exposes
representative primary-result excerpts without publishing `.tasks/`, machine-local
paths, credentials or the binary prototype. **These are agent-executed,
agent-reported observations, not an independently reproduced or signed run.** An
independent agent checked the retained local records; a PR reviewer can now inspect
the excerpts and reasoning, but has not thereby verified the original execution.

## Provenance and sanitization

The single Windows experiment used source checkout
`0f86909b79838602c79a3bdb95f15f4f71d77137`. Packing reused existing compiled output
whose build/source provenance was not established. One package was created:

| Item | Recorded identity |
| --- | --- |
| Package / command | `Mcp.Dotnet` `1.0.6-ghi46.1` / `mcp-dotnet` |
| Package SHA-256 | `5913c0001a7a1d64000478b88e895ad8143616ef6710a808a29364811896379d` |
| Package size / entries | 1,515,467 bytes / 23 |
| SDK | `11.0.100-rc.1.26425.128` |
| Runtime | `Microsoft.NETCore.App 11.0.0-rc.1.26425.128` |
| Bundled dependency | `FSharp.Core 11.0.101-rc1.26425.128`, including 13 satellite DLLs; no nuspec package dependencies |
| Original app DLL SHA-256 | `9a39ebd51a3f9e1dd941eb7b41ad760e095e425eca962fe65dd3d1eebc0263be` |

JSON below is **field-selected and whitespace-normalized**, not a full transcript.
Unshown fields are omitted, not asserted empty. Selected values and types are
unchanged; path-bearing trees, descriptions, unrelated process information and
duplicate response text are excluded. Commands use symbolic absolute paths, not
the operator's paths. The hashes below bind the exact private source-file bytes,
not this sanitized rendering. They are correlation receipts, **not independently
checkable attestation while the originals remain private**.

| Original record | SHA-256 |
| --- | --- |
| `install.json` | `a60fe776b80eb3a854045716e42596457fbcd2b7b92ac7ec92820514d9176dff` |
| `mcp-session.json` | `ac6be31f4cd13ce6139a6c77180cbac167142410c12c4fd9012f2949fd2d49a2` |
| `cancellation.json` | `981fd991ba1e67b914c303a87d0b284d16f36b228fcc57db4e1dac6dd26e3bc4` |
| `eof.json` | `40a91566f63e7a5b9823088d914cc1c2ffda8fbdb1fa80c4df0bc1c5e561cf6b` |
| `concurrent-install-use.json` | `db9631e825e610fb3fa9d9cda23fb2fa78836a1ea92ef6f821774fa3f73f1b1b` |
| `corrupted-payload.json` | `6a242eb09e1e17f72ab8d74454d5ce2b4cc9090275f3bf9b9705c17a22fe43bf` |
| `process-snapshot-after.json` | `6dd7aecfdc2430aab060646d0f1fb34cbb2705d7dd2db277e3660c902b2e1250` |

## Commands and replay boundary

These are the executed operations with local prefixes replaced by variables.
This appendix does **not** authorize another experiment or downloading missing
inputs. A replay requires an approved disposable directory, a locally installed
matching SDK/runtime, the producer copy and its compiled output, and the local
FSharp.Core package. Without the withheld binary, rebuilding may produce different
package/app hashes; do not claim bit-for-bit reproduction from these commands.

The packaging report records isolated `DOTNET_CLI_HOME`, `NUGET_PACKAGES`,
`NUGET_HTTP_CACHE_PATH`, TEMP/TMP, explicit local-only config and PATH/telemetry/
first-use/no-logo/workload suppression. It does not establish the larger test
launcher's complete environment for that earlier phase.

Before test SDK/FSI/shim invocations, the test launcher additionally isolated
HOME/USERPROFILE/APPDATA/LOCALAPPDATA, NuGet scratch/fallback/plugin paths and set
controls including the following. Each test path-valued variable pointed into
the owned disposable directory; no operator configuration was used as a feed.

```text
DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
DOTNET_NOLOGO=1
DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
DOTNET_CLI_WORKLOAD_ADVERTISEMENT_UPDATE_NOTIFY_DISABLE=true
DOTNET_GENERATE_ASPNET_CERTIFICATE=false
DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
MSBUILDDISABLENODEREUSE=1
NUGET_CERT_REVOCATION_MODE=offline
```

The tool-source config used `<packageSources><clear />` followed by exactly one
local tool feed. Restore used a separate cleared config pointing at the copied
local FSharp.Core package. The fixture workspace also had that local-only config.
The tool-config template below preserves the executed config's package-cache
setting. Replace `LOCAL_TOOL_FEED` and `LOCAL_PACKAGE_CACHE` with the owned feed
and isolated package-cache directories; `NUGET_PACKAGES` was set to that same
isolated cache in the test environment.

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="local-tool-feed" value="LOCAL_TOOL_FEED" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="LOCAL_PACKAGE_CACHE" />
  </config>
</configuration>
```

With `$dotnet` the absolute installed host, `$R` the owned prototype root and `$T`
its test-owned subdirectory, the recorded commands were:

```powershell
& $dotnet --version
& $dotnet restore "$R/src/Dotnet.fsproj" --configfile "$R/isolated/nuget/NuGet.Config" `
  -p:RestorePackagesPath="$R/isolated/packages" -v:minimal
& $dotnet pack "$R/src/Dotnet.fsproj" -c Release --no-build --no-restore `
  -p:PackageId=Mcp.Dotnet -p:PackageVersion=1.0.6-ghi46.1 `
  -p:PackAsTool=true -p:ToolCommandName=mcp-dotnet `
  -p:PackageOutputPath="$R/package" `
  -p:RestoreConfigFile="$R/isolated/nuget/NuGet.Config" -v:minimal
# Hash was independently checked before this install, outside NuGet:
& $dotnet tool install --tool-path "$T/tools/main" `
  --configfile "$T/nuget/NuGet.Tool.Config" --version 1.0.6-ghi46.1 `
  --no-http-cache Mcp.Dotnet
# Absolute shim; process CWD explicitly set to T/workspace by the client:
& "$T/tools/main/mcp-dotnet.exe" --dotnet-host $dotnet --artifact-root "$T/artifacts"
```

The client used `ProcessStartInfo.ArgumentList`, redirected stdin/stdout/stderr,
newline-delimited JSON-RPC and hard process timeouts. It sent `initialize`, then
`notifications/initialized`, `tools/list`, and the real build request:

```json
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"build","arguments":{"target":"fast/Fixture.fsproj","configuration":"Release","timeoutMs":300000}}}
```

`fast/Fixture.fsproj` was a local `Microsoft.NET.Sdk` F# project targeting `net11.0`
with `IsPackable=false`; restore was offline against the local FSharp.Core source.
The slow project added this exact target for cancellation/timeout observations:

```xml
<Target Name="TestDelay" BeforeTargets="Build">
  <Exec Command="ping -n 21 127.0.0.1 &gt; nul" />
</Target>
```

The client first awaited slow build request 10 (the recorded 24,016 ms success).
It then sent build request 11 for `slow/Slow.fsproj` with `timeoutMs=120000`,
then, 1.5 seconds later:

```json
{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":11}}
```

It separately sent build request 12 with producer `timeoutMs=100`. Builds were
calls through the experimental MCP, not raw CLI build/test fallbacks. Details
were requested using the returned fast-build run ID. EOF and an exit notification
were exercised in separate sessions. Startup/fixture/runtime failures and client
syntax/JsonNode failures are described in the main report; no failed attempt was
reclassified as producer success.

## Install and real MCP result excerpts

From `install.json`:

```json
{
  "digestVerifiedBeforeInstall": true,
  "packageSha256BeforeInstall": "5913c0001a7a1d64000478b88e895ad8143616ef6710a808a29364811896379d",
  "exitCode": 0,
  "stdout": "You can invoke the tool using the following command: mcp-dotnet\r\nTool 'mcp.dotnet' (version '1.0.6-ghi46.1') was successfully installed.\r\n",
  "stderr": "",
  "timedOut": false,
  "shimExists": true,
  "globalToolDirectoryCreated": false,
  "packageSha256AfterInstallUnchanged": true
}
```

`globalToolDirectoryCreated=false` refers to the isolated home, not an audit of
the operator's persistent profile. Nineteen installed payload files were compared
by relative path against the package; no hash mismatches were found.

From `mcp-session.json` (response and structured-content fields):

```json
{
  "initializeResponse": {
    "jsonrpc": "2.0",
    "id": 1,
    "result": {
      "protocolVersion": "2024-11-05",
      "capabilities": {"tools": {"listChanged": false}},
      "serverInfo": {"name": "mcp-store-dotnet", "version": "1"}
    }
  },
  "buildStructuredContent": {
    "ok": true,
    "runId": "tD8qsfaT4lHTfXK4_b3fZFJ7d4CCgpR2Cxvksg5sZ5w",
    "operation": "build",
    "status": "succeeded",
    "exitCode": 0,
    "durationMs": 6213,
    "errorCount": 0,
    "warningCount": 0,
    "errors": [],
    "warnings": [],
    "detailsAvailable": true
  },
  "stdoutLineCount": "0",
  "stdoutLines": [],
  "stderr": "",
  "exitCodeAfterExitNotification": 0,
  "finalExitCode": 0
}
```

The zero-line queue was already consumed by response matching. It is **not**
whole-stream purity evidence. Only the later four-line cancellation transcript
below supports that measured stdout claim. The relative target resolved in the
explicit workspace; absolute/missing targets were refused, but this is not a
matrix of all CWD/global.json environments.

## Cancellation, timeout and EOF excerpts

From `cancellation.json`; response content duplicates and unrelated fields omitted:

```json
{
  "slowBuildStatus": "succeeded",
  "slowBuildDurationMs": "24016",
  "cancelResponse": {
    "jsonrpc": "2.0",
    "id": 11,
    "result": {
      "isError": false,
      "structuredContent": {
        "ok": true,
        "runId": "2Ow99fY82AA21LuC1u0DECkhVqOPVd9b-SycTeGgEEU",
        "operation": "build",
        "status": "cancelled",
        "exitCode": null,
        "durationMs": 1547,
        "detailsAvailable": true
      }
    }
  },
  "processAliveAfterCancel": true,
  "timeoutResponse": {
    "jsonrpc": "2.0",
    "id": 12,
    "result": {
      "isError": false,
      "structuredContent": {
        "ok": true,
        "runId": "IrcLW6TE7qdN_4hv45AuGCT9-UW2VdZHuC_L2CaQS3k",
        "operation": "build",
        "status": "timeout",
        "exitCode": null,
        "durationMs": 146,
        "detailsAvailable": true
      }
    }
  },
  "stdoutTranscriptCount": 4,
  "stdoutTranscriptPurity": "all stdout lines parsed as JSON",
  "stderr": "",
  "exitCodeAfterExitNotification": 0,
  "finalExitCode": 0
}
```

From `eof.json`:

```json
{"initialized":true,"exitCodeAfterEof":0,"exited":true,"stderr":"","finalExitCode":0}
```

Cancellation status and the live server are observed; cancellation-time release
of the underlying build child was **not** independently captured. A later process
snapshot is not a substitute for that missing observation.

## Install during use excerpt

While the initialized server remained live, standard install was run first into
`tools/concurrent` with the same exact version/config, then into `tools/main`.
From `concurrent-install-use.json`:

```json
{
  "initialized": true,
  "concurrentInstallExitCode": 0,
  "concurrentInstallStdout": "You can invoke the tool using the following command: mcp-dotnet\r\nTool 'mcp.dotnet' (version '1.0.6-ghi46.1') was successfully installed.\r\n",
  "samePathInstallExitCode": 0,
  "samePathInstallStdout": "Tool 'mcp.dotnet' (version '1.0.6-ghi46.1') is already installed.\r\n",
  "inUseInstanceResponded": true,
  "pingResponse": {"jsonrpc":"2.0","id":2,"result":{}},
  "finalExitCode": 0
}
```

This is an active instance plus installation, **not simultaneous fresh same-path
writers or forced in-use replacement**. Both installed app DLLs later matched the
original app hash above. Missing version `9.9.9.9` was separately requested with
the same local-only config and returned exit 1 with “not found in NuGet feeds”;
no alternate source or download was used.

## Installed-byte modification and recovery excerpts

Only a separate owned installed copy was modified; the nupkg stayed unchanged.
The runtimeconfig was temporarily changed to demand framework `0.0.0.0`, then
restored. The original DLL was followed by exactly 16 bytes, values 1 through 16.
The launch helper supplied the normal absolute host/artifact arguments and closed
stdin: this tests startup and EOF exit, **not another MCP tool call after tamper**.

From `corrupted-payload.json`; the two appended-byte records really are separate
entries, one for process outcome and one for hash comparison:

```json
{
  "observations": [
    {"observation":"baseline","exitCode":0,"stdout":"","timedOut":false},
    {"observation":"invalid-runtimeconfig","exitCode":-2147450730,"stdout":"","timedOut":false},
    {"observation":"appended-assembly-bytes","exitCode":0,"stdout":"","timedOut":false},
    {
      "observation":"appended-assembly-bytes",
      "installedSha256AfterAppend":"c19592cbf51368520d7ed169e92e097de7218f596ec9ac651ed03f4ba2e8c1c7",
      "packagedSha256":"9a39ebd51a3f9e1dd941eb7b41ad760e095e425eca962fe65dd3d1eebc0263be",
      "hashDiffersFromPackage":true
    },
    {"observation":"after-recovery","exitCode":0,"stdout":"","timedOut":false}
  ],
  "uninstallExitCode":0,
  "reinstallExitCode":0,
  "restoredPayload": {
    "restoredSha256":"9a39ebd51a3f9e1dd941eb7b41ad760e095e425eca962fe65dd3d1eebc0263be",
    "packagedSha256":"9a39ebd51a3f9e1dd941eb7b41ad760e095e425eca962fe65dd3d1eebc0263be",
    "identicalToPackage":true
  },
  "packageSha256Unchanged":true
}
```

The loader's altered-runtimeconfig stderr began `You must install or update .NET
to run this application.` It listed framework `Microsoft.NETCore.App` version
`0.0.0.0`. That failure is framework resolution, not independent integrity
validation. Appended-byte startup acceptance supports the narrower claim that
this ordinary launch path did not reject the changed exact DLL bytes; it is not
proof of arbitrary malicious execution, package-signature policy or every CLR
tampering behavior. Recovery used standard commands:

```powershell
& $dotnet tool uninstall --tool-path "$T/tools/corrupt" Mcp.Dotnet
& $dotnet tool install --tool-path "$T/tools/corrupt" `
  --configfile "$T/nuget/NuGet.Tool.Config" --version 1.0.6-ghi46.1 `
  --no-http-cache Mcp.Dotnet
```

## Process release, cleanup and limits

From `process-snapshot-after.json`, excluding unrelated process data:

```json
{"label":"snapshot-after","capturedAtUtc":"2026-11-04T18:32:26.8467259Z","ownedProcessCount":0,"ownedProcesses":[]}
```

The snapshot filtered by the owned disposable root. It does not prove that every
possible descendant was independently tracked at cancellation time. Following
authorized research closure, dedicated Workflow cleanup succeeded and a later
existence check found the disposable prototype directory absent; retained private
evidence and the inert research package were preserved. No shell-deletion fallback
or active installation changes were used.

No fresh producer build, hosted feed/signature/publication validation, exact
newer-version update, release no-op equivalence, Workflow producer qualification,
filesystem-link/TOCTOU equivalence or production cutover was performed. Source
isolation is configuration-level, not OS egress proof. The PATH-suppression flag
was set; no registry before/after invariant was measured. The recommendation in
the main report remains an engineering judgement under unchanged guarantees,
not proof that a fully implemented tool-based replacement could never be simpler.
