# Builder Emit and receipt interoperability

## Result

`Build.ps1 -Phase Emit` succeeded with exit `0` and produced a real `receipt.json`,
`runtime-manifest.json`, inner runtime envelope, and outer builder transport. The
manifest and receipt each describe the same five-file staged runtime. The strict
`ReleaseHandoff.parseTrustedPublisherReceipt` parser in the current built
`ReleaseRunner.dll` accepted both the actual emitted receipt and a clearly
controlled one-field role variant with non-empty runtime entries.

The result is partial for two observed role-contract mismatches. The actual
builder receipt has `role="candidate"`, not the requested independent trusted
builder role `publisher`. A controlled copy changed only that role to
`publisher`; the current compiled publisher, invoked with the candidate CLI role,
rejected it at `trustedReceipt.role` instead of continuing to the deliberately
invalid candidate digest. This is an observed runner result, not an inference
from source. The strict receipt parser itself accepts the publisher-role variant.

## Scope and inputs

- Checkout HEAD was `523f625014dbf81fb788fe5c11877fc7a9626382`. The harness
  compared the roster and every roster-listed `Config.fsx` blob to `HEAD` before
  invoking Emit; the same exact bytes were subsequently hashed into the receipt.
- Runtime serializer input was the already-staged fixture at
  `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-bce25215f8df40f8a569ec9e7ce9715e\run\stage\publish`.
  It was copied to a fresh external stage root. This controlled runtime fixture
  does **not** prove the source closure was staged or that a clean-source,
  self-contained publish was performed.
- The actual Bootstrap v2 `FetchCandidate` fixture was
  `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\evidence\fetchcandidate-bootstrap-receipt.json`.
  Its run context was `5001`, attempt `2`, repository `masterlifting/mcp-store`,
  event SHA `523f625014dbf81fb788fe5c11877fc7a9626382`, role `candidate`,
  component `dotnet`.
- Builder inputs were explicitly supplied as SDK
  `11.0.100-rc.1.26425.128`, runtime `11.0.0-rc.1.26425.128`, platform
  `win-x64`, and trigger `push`.
- No Stage phase, `dotnet build`, producer package/publish, full suite, real
  credential read, live API, network request, or repository test edit was
  performed. Runner `publish` CLI probes stopped in verification before HTTP or
  auth composition; child auth/publication environment variable names were
  removed without reading their values.

## Commands and counts

The actual production command was run twice while repairing the temporary CLI
probe argument assembly. Both runs used the same inputs, separate fresh external
stage/output roots, returned exit `0`, and produced byte-identical receipt,
manifest, and envelope outputs. The second run is the evidence root below:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File "D:\repos\private\mcp-store\infrastructure\release\Build.ps1" -Phase Emit -Root "D:\repos\private\mcp-store" -StageRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\run\stage" -OutputRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\run\output" -Repository masterlifting/mcp-store -EventSha 523f625014dbf81fb788fe5c11877fc7a9626382 -RunId 5001 -RunAttempt 2 -Trigger push -BuilderSdk 11.0.100-rc.1.26425.128 -BuilderRuntime 11.0.0-rc.1.26425.128 -BuilderPlatform win-x64
```

A separate PowerShell AST-only parse of `infrastructure/release/Build.ps1`
reported `0` parse errors (`2458` tokens); that static check did not execute the
script.

The harness command was:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-02e0ddb3ce574f5f872b16ea52c73d78\Emit.ps1" -RepositoryRoot "D:\repos\private\mcp-store" -SmokeRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361" -RuntimeFixtureRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-bce25215f8df40f8a569ec9e7ce9715e\run\stage" -BootstrapFixtureRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4" -PreparationFixturePath "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-bce25215f8df40f8a569ec9e7ce9715e\run\output\preparation.json"
```

The parser command was run twice and returned `0` both times; each run parsed
the actual emitted receipt and the test-only publisher-role variant with the
current built DLL:

```powershell
dotnet fsi --exec --quiet "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\run\Parse.fsx"
```

The final focused runner invocation supplied all three explicit `--key=value` receipt
flags as single arguments, at distinct paths outside the candidate directory.
Four runner probes returned exit `4`: the authentic emitted receipt stopped at
the deliberately mismatched candidate transport digest; missing trusted receipt
and mismatched `eventRevision` failed as typed early errors; the publisher-role
versus candidate-CLI check failed at the role cross-check. The two focused
negative cases passed their early-failure expectations; the role-compatibility
expectation did not.

Earlier temporary probe attempts had split receipt option names from their path
arguments and stopped at CLI validation. Those attempts are excluded from the
probe results; the corrected `Probe.ps1` records its exact argv in adjacent
`.args` files. The corrected probe command was:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\Probe.ps1" -RepositoryRoot "D:\repos\private\mcp-store" -SmokeRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361" -BootstrapFixtureRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4"
```

The publisher-role compatibility probe used the compiled runner and these
separate receipt paths:

```powershell
dotnet "D:\repos\private\mcp-store\infrastructure\release\bin\Release\net11.0\ReleaseRunner.dll" publish "D:\repos\private\mcp-store\infrastructure\release\Publisher.json" "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\run\candidate" --repo=masterlifting/mcp-store --run-id=5001 --attempt=2 --event-sha=523f625014dbf81fb788fe5c11877fc7a9626382 --role=candidate --component=dotnet --artifact-id=7001 --artifact-name=release-candidate-dotnet --transport-sha256=70c2f06fb04895c81830f24639c3f6e685eb198cf99e8d14852f2859c57c3525 --builder-sdk=11.0.100-rc.1.26425.128 --builder-runtime=11.0.0-rc.1.26425.128 --builder-platform=win-x64 --workspace-root="D:\repos\private\mcp-store" --trusted-receipt="C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\run\evidence\publisher-role-derived-from-emit.json" --preparation-receipt="C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\run\evidence\preparation.json" --transport-evidence="C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\evidence\fetchcandidate-bootstrap-receipt.json" --trigger=push
```

## Emitted evidence

Output root:
`C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\run\output`

| File | SHA-256 | Result |
| --- | --- | --- |
| `receipt.json` | `0d6e78224963aef4eb3fd03089329386ebba59ece59ab427db6e5ca53e2545d1` | Strict schema `1`; five runtime paths/digests/sizes; role is actually `candidate` |
| `runtime-manifest.json` | `fb8475562bbcbe64309ee7bd99865dae515f84496adf914d26fc2284033ec9ba` | Schema `1`; five runtime entries |
| `publisher-runtime.zip` | `d09515fd9b90a180b29c3bde6fa424b42c04560ee5c71fd619ca15762c66ce23` | Deterministic inner envelope |
| `publisher-transport.zip` | Not used as a platform digest | Local outer builder transport; exact entries are `publisher-runtime.zip` and `runtime-manifest.json` |

The receipt binds roster `infrastructure/release/Participants.json` at SHA-256
`8b7e5cd53c9780e4aca562099fd7e494257f6fd3c59d1b0fc75ce7508cce05af` and two
canonical configs: `dotnet/release/Config.fsx` at
`932fea19eec25b0aa3f61b89ee47de5f4a207bdcaf2771178c28f01d7fdf9654`, and
`workflow/release/Config.fsx` at
`e52c9a1f56a7ff637954fb3225b83d2ed48235d96a4b534a1cf700312463cf82`. No
platform outer digest is present in the trusted receipt; it is a separate
pre-upload value.

Strict parser target:
`D:\repos\private\mcp-store\infrastructure\release\bin\Release\net11.0\ReleaseRunner.dll`,
SHA-256 `abce1e392cc8a7a052afb24839b5e81ea9882c3a91d24436583e3707e43f9813`.
Parser output: `actual-role=candidate actual-runtime-files=5
publisher-role-parse=publisher publisher-runtime-files=5`.

Runner evidence is under
`C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-65782266eca24ed9a7468efeab649361\run`.
The controlled invalid candidate digest was expected
`70c2f06fb04895c81830f24639c3f6e685eb198cf99e8d14852f2859c57c3525`, observed
`aba9a3a99261fc1f9816b696b981e024c09d680faa49ddc4909fa65dcc380ac3`; the runner
returned before HTTP/auth composition. Missing trusted receipt returned
`required release artifact does not exist`; the mismatched receipt returned
`trustedReceipt.eventRevision` mismatch. The independent role probe returned
`trustedReceipt.role: role does not match the release pin: expected candidate,
got publisher`.

Residual boundary: this proves successful Emit serialization and parser
interoperability only for a controlled pre-staged runtime fixture. It does not
prove full source closure staging, clean-source provenance, self-contained
publishing, hosted artifact upload, or real platform digest observation.

## Follow-up: publisher-role interoperability

### Result

The actual `Build.ps1 -Phase Emit` completed once with exit `0`; its emitted
`receipt.json` carried `role="publisher"`, schema `1`, five non-empty runtime
entries, the roster SHA-256, and two component Config.fsx paths and hashes. The
strict `ReleaseHandoff.parseTrustedPublisherReceipt` parser in the current
compiled `ReleaseRunner.dll` accepted that exact receipt with a consistent
runtime path/digest/size set. The positive Runner CLI invocation consumed the
emitted receipt directly, the four producer-known preparation fields, and the
separate Bootstrap v2 FetchCandidate receipt. It accepted the independent role
channels (`publisher` trusted receipt; `candidate` CLI and FetchCandidate
receipt), then returned exit `4` at the deliberately invalid local candidate
digest before auth or release HTTP composition.

The focused role-negative case changed only `role` to `candidate` in one copy of
the emitted receipt; it failed at typed `trustedReceipt.role`. The missing
trusted-receipt case failed at the required-artifact guard. There were three
unique cases (positive, wrong-role, missing-receipt); the two negative probes
were each repeated once by a small evidence-capture script, for five Runner CLI
invocations total. Initial temporary argument-array assembly attempts stopped
at CLI missing-flag validation and are excluded from those cases.

### Inputs and commands

- Checkout HEAD and the independent FetchCandidate receipt event SHA were both
  `523f625014dbf81fb788fe5c11877fc7a9626382`; run `5001`, attempt `2`, repository
  `masterlifting/mcp-store`, trigger `push`.
- Bootstrap v2 input:
  `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\evidence\fetchcandidate-bootstrap-receipt.json`.
  It parsed as `mode=FetchCandidate`, `status=fetched`, `role=candidate`,
  `component=dotnet`, artifact `7001` / `release-candidate-dotnet`.
- The preparation receipt contained exactly `innerArchiveSha256`,
  `innerArchiveSize`, `manifestSha256`, and `manifestSize` (plus `schemaVersion`),
  derived from the existing controlled candidate fixture. The trusted receipt,
  preparation receipt, and FetchCandidate receipt used separate paths outside
  the candidate directory; candidate and receipt paths were outside the
  workspace.
- Runtime serializer input was copied from the previously approved fixture at
  `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-bce25215f8df40f8a569ec9e7ce9715e\run\stage\publish`.
  The new evidence root is
  `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-af1153a203ef416ea399aa15cff61480\run`.

The actual Emit command was:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File "D:\repos\private\mcp-store\infrastructure\release\Build.ps1" -Phase Emit -Root "D:\repos\private\mcp-store" -StageRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-af1153a203ef416ea399aa15cff61480\run\stage" -OutputRoot "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-af1153a203ef416ea399aa15cff61480\run\output" -Repository masterlifting/mcp-store -EventSha 523f625014dbf81fb788fe5c11877fc7a9626382 -RunId 5001 -RunAttempt 2 -Trigger push -BuilderSdk 11.0.100-rc.1.26425.128 -BuilderRuntime 11.0.0-rc.1.26425.128 -BuilderPlatform win-x64
```

The strict parser command was:

```powershell
dotnet fsi --exec --quiet "C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-af1153a203ef416ea399aa15cff61480\run\Parse.fsx"
```

The positive Runner command used one `--key=value` argument per flag; the only
changes for the two negative probes were the `--trusted-receipt` path
(`output\Wrongrole.json` or the absent `run\Missing.json`):

```powershell
dotnet D:\repos\private\mcp-store\infrastructure\release\bin\Release\net11.0\ReleaseRunner.dll publish D:\repos\private\mcp-store\infrastructure\release\Publisher.json C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-af1153a203ef416ea399aa15cff61480\run\candidate --repo=masterlifting/mcp-store --run-id=5001 --attempt=2 --event-sha=523f625014dbf81fb788fe5c11877fc7a9626382 --role=candidate --component=dotnet --artifact-id=7001 --artifact-name=release-candidate-dotnet --transport-sha256=70c2f06fb04895c81830f24639c3f6e685eb198cf99e8d14852f2859c57c3525 --builder-sdk=11.0.100-rc.1.26425.128 --builder-runtime=11.0.0-rc.1.26425.128 --builder-platform=win-x64 --workspace-root=D:\repos\private\mcp-store --preparation-receipt=C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-af1153a203ef416ea399aa15cff61480\run\output\Preparation.json --trigger=push --trusted-receipt=C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-builder-smoke-af1153a203ef416ea399aa15cff61480\run\output\receipt.json --transport-evidence=C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-ghi24-interop-6a64458893d1477188ded360f17523d4\evidence\fetchcandidate-bootstrap-receipt.json
```

### Results and hashes

| Check or file | Result / SHA-256 |
| --- | --- |
| Actual Emit | exit `0`; `runtimeFiles=5`; receipt role `publisher` |
| Strict parser | exit `0`; `trusted-role=publisher runtime-files=5 component-config-hashes=2 roster-hash=present` |
| Positive Runner probe | exit `4`; stopped at `transport-sha256`; expected Bootstrap digest `70c2f06fb04895c81830f24639c3f6e685eb198cf99e8d14852f2859c57c3525`, observed dummy candidate digest `aa0c71506f63235fd7dd5485ff7ed403127cbf688a70349e2096622c52aa1d72` |
| Wrong-role Runner probe | exit `4`; typed `trustedReceipt.role`; the sole mutated copy carried `role=candidate` |
| Missing trusted receipt | exit `4`; `required release artifact does not exist` |
| `receipt.json` | `bed7b9479fe53a751b895ae867eae69077f2b2d7f1fae3ec8476c04073e8b8ec` |
| `runtime-manifest.json` | `fb8475562bbcbe64309ee7bd99865dae515f84496adf914d26fc2284033ec9ba` |
| `publisher-runtime.zip` | `d09515fd9b90a180b29c3bde6fa424b42c04560ee5c71fd619ca15762c66ce23` |
| Local `publisher-transport.zip` | `d7238aec3bc656aa0ad3dd26fd7c45aa2139ac5d129e5ee8d9cdb4386a6c6be4`; not a platform-observed digest |
| Current parser/Runner DLL | `652b3925941b70e6c9f37e5adbd1bebe60ee00801e5a240a12a18eb5a2ccb633` |
| Runtime-fixture `ReleaseRunner.dll` | `abce1e392cc8a7a052afb24839b5e81ea9882c3a91d24436583e3707e43f9813` |
| `Build.ps1` used for Emit | `6ae3425bc97ba6d9a383c849dbb6e71bdb72bf240289ae5e72f9161f2752a3fe` |

Receipt trust anchors were roster
`8b7e5cd53c9780e4aca562099fd7e494257f6fd3c59d1b0fc75ce7508cce05af`,
`dotnet/release/Config.fsx`
`932fea19eec25b0aa3f61b89ee47de5f4a207bdcaf2771178c28f01d7fdf9654`, and
`workflow/release/Config.fsx`
`e52c9a1f56a7ff637954fb3225b83d2ed48235d96a4b534a1cf700312463cf82`. The
preparation fixture recorded inner archive SHA/size
`eac75b929bf16f10697660ec72afe11de3e0ac1f17ea360ac97797326dedf3f0` / `27` and
manifest SHA/size
`0e9561cfb83d50990a103b3896fe249a11fe27fa28985448187f93ec12116d72` / `19`.
The Bootstrap receipt SHA-256 is
`0d8e56456fddc489c0383f396f19352add1119c304fb5b0f766bad97b6e824df`.

### Residual boundary

The positive candidate bytes were intentionally invalid, so the runner stopped
before strict raw-transport parsing, preparation-to-transport content
cross-checking, committed-HEAD verification, auth, or HTTP. The Bootstrap v2
receipt was a controlled existing fixture, not a new live platform observation.
No `Build.ps1 -Phase Stage`, restore, `dotnet build`, producer publish/upload, or
repository test was run. The `ReleaseRunner publish` CLI calls were verification
only and stopped before publication. The previously staged runtime fixture was
copied only as Emit serializer input; this does not prove trusted source-closure
staging, a self-contained build, clean source ancestry, hosted artifact upload,
or a real platform outer digest. The local `Build.ps1` used here is not part of
the fixture's event commit, so its output is controlled serialization evidence,
**not a production attestation or readiness approval**. Publication-authority
environment variable names were removed from the controlled subprocesses
without reading their values; no network or credential-backed action occurred.

Runner probe logs and structured evidence are under the smoke root's `run`
directory (`RunnerEvidence.json` and `BoundaryEvidence.json`).
