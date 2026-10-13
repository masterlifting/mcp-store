# Focused Bootstrap/runner interoperability smoke

Status: **partial; found a blocking CLI/receipt contract mismatch for
`InstallRuntime` publisher/runtime receipts.** Scope was intentionally narrow per
the current Issue #24 policy: actual producer receipts, the current compiled
runner, one independent-metadata disagreement, and a few strict-boundary checks;
no permutation matrix or full suite.

## Setup and safety

- Repository HEAD used as `eventSha`: `523f625014dbf81fb788fe5c11877fc7a9626382`.
  No synthetic commit was made. The checkout was not treated as a clean-source
  publication fixture.
- Invoked the existing
  `infrastructure/release/bin/Release/net11.0/ReleaseRunner.exe`; no build was
  run. Production `infrastructure/release/Bootstrap.ps1` ran in separate
  non-interactive PowerShell children with its explicit `TestTransport` seam
  and `https://api.test.invalid` API base. The child environment was cleared
  except for Windows runtime/temp variables and a child-only dummy token; both
  metadata and download operations were answered by local fixture code. No live
  API, signed URL, credential, or upload was used.
- Runner children had publication/auth environment variable names removed
  without reading their values. Bootstrap evidence and candidate roots were
  outside the workspace and separate from each other.
- Harness and receipts are outside the repository under:
  `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4`.
  Entrypoint: `RunInterop.ps1`; generated evidence is under `evidence/`.

The single harness command was:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
  "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\RunInterop.ps1" `
  -Root "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4" `
  -RepositoryRoot "D:\repos\private\mcp-store"
```

From the repository root, the FetchCandidate pass-through command was:

```powershell
infrastructure\release\bin\Release\net11.0\ReleaseRunner.exe publish infrastructure\release\Publisher.json C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\runner-candidate --repo=masterlifting/mcp-store --run-id=5001 --attempt=2 --event-sha=523f625014dbf81fb788fe5c11877fc7a9626382 --role=candidate --component=dotnet --artifact-id=7001 --artifact-name=release-candidate-dotnet --transport-sha256=70c2f06fb04895c81830f24639c3f6e685eb198cf99e8d14852f2859c57c3525 --builder-sdk=11.0.100-rc.1.26425.128 --builder-runtime=11.0.0-rc.1.26425.128 --builder-platform=win-x64 --workspace-root=D:\repos\private\mcp-store --transport-evidence=C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\evidence\fetchcandidate-bootstrap-receipt.json
```

The mismatch invocation was identical except `--run-id=5002` (exit 4,
`bootstrap.runId ... expected 5001, got 5002`). InstallRuntime used the same
closure, candidate directory, repo, event SHA, attempt, builder values, and
workspace root, with `--role=publisher --artifact-id=7001
--artifact-name=release-publisher
--transport-sha256=a7e76ae52fa491c1b77fac86d1ed2ce3a47ce9b3423dfc1c956926ebeb900a13
--transport-evidence=C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\evidence\installruntime-bootstrap-receipt.json`.
That invocation with `--component=` exited 4 as missing required; changing it
to `--component=dotnet` exited 4 at the observed-component mismatch.

## Results

| Case | Observed result |
| --- | --- |
| Production `Bootstrap.ps1 -Mode InstallRuntime`, `publisher` role | Exit 0; actual v2 receipt written to `evidence/installruntime-bootstrap-receipt.json`, status `installed`, component JSON `null`. `apiDigestRaw` was `sha256:<64 lowercase hex>` while `apiDigest`, `platformDigest`, and `download.observedSha256` were bare 64-hex; streamed SHA equaled the normalized platform digest. `schemaVersion` and `artifact.sizeInBytes` serialized as JSON integers. |
| Feed that InstallRuntime receipt to Runner | `--component=` exits 4 in CLI validation with “missing required flag(s): --component”. Supplying `--component=dotnet` gets past CLI parsing and strict receipt parsing but exits 4 at `bootstrap.component`: “expected , got dotnet”. The receipt's null component therefore has no representable valid CLI value today. |
| Production `Bootstrap.ps1 -Mode FetchCandidate`, `candidate` role | Exit 0; actual v2 receipt written to `evidence/fetchcandidate-bootstrap-receipt.json`, status `fetched`, role `candidate`, component `dotnet`. Correct CLI values pass receipt parsing and all nine expected-v-observed field checks; Runner exits 4 at the next gate because the independent candidate `receipt.json` is absent. |
| Expected/observed disagreement | With the actual FetchCandidate receipt (`runId=5001`) but independent CLI `--run-id=5002`, Runner exits 4: `bootstrap.runId ... expected 5001, got 5002`. It rejects before the missing trusted publisher receipt gate. |
| Unknown-field rejection | Added `interopProbe` to a copy of the actual FetchCandidate receipt. Runner exits 4: root contains unknown field `interopProbe`. |
| JSON number vs. numeric string | Changed `artifact.sizeInBytes` in a copy of the actual receipt from a JSON number to string `"1"`. Runner exits 4: field `sizeInBytes` is not an integer. |
| Evidence path inside candidate | Copied the actual FetchCandidate receipt under the candidate root and passed that path. Runner exits 4 before parsing: bootstrap evidence must not be inside the candidate directory. |

Successful producer receipt paths are recorded above. Compact run summary:
`C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\summary.json`.

## Finding and owner action

The strict receipt parser accepts `component: null` as `None`
(`infrastructure/release/Runner.fs:601-612`), and Bootstrap v2 emits null for
publisher/runtime roles. However, `parsePublishCli` treats every mandatory flag
value as non-empty, including `--component` (`Runner.fs:246-268`). Therefore an
empty component is rejected before the receipt is loaded; any non-empty
component disagrees with the observed null (`Runner.fs:912-916`). This
contradicts the intended CLI/receipt contract recorded in
`.tasks/GHI-24/docs/review/Adapter-trust.md:228-233` and makes the
InstallRuntime publisher/runtime path unreachable through `publish`.

Minimal production-owner fix: distinguish presence of `--component` from its
value. Accept a present empty value only for `publisher`/`runtime`; keep a
non-empty supported component mandatory for `candidate`, and reject
role/component combinations that do not match that rule. Do not invent a
placeholder component. Add one focused regression proving an actual null
component receipt reaches the next trusted-receipt gate, alongside the existing
candidate happy-path coverage.

## Boundary and residual risks

The disagreement and path-isolation errors arise in `verifyWorkspace`
(`Runner.fs:1237-1248`); trusted candidate receipts are loaded afterward
(`1249-1252`). `runPublishMode` only enters the pipeline after verification
(`1296-1321`), and release HTTP/auth construction is in that later pipeline
(`1036-1043`). Thus these observed failures stop before release HTTP/auth
composition. This is code-path evidence, not packet-level tracing.

The test seam does **not** verify live metadata Authorization headers, the real
signed-redirect/auth wire, hosted workflow receipt download/wiring, source and
trusted-builder provenance, a clean packaging workspace, or end-to-end upload.
No `dotnet test`, build, or broad Bootstrap launcher was run. Those remain
separate gates; this smoke does not claim publication readiness.
