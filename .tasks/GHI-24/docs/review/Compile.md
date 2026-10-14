# GHI-24 compile evidence

The focused test-host fix edits `Authority.fs` (helper takes a `CompressionLevel` argument; inner archive uses `Optimal`, outer keeps `NoCompression`; `authorizedFixedTag` now expects the transaction-FAILED exit code `1` instead of the pre-contract guard exit `4` because the scripted transport returns HTTP 500 inside `ReleaseTransaction.run`, matching the canonical Runner/Integrity transaction FAILED contract — unauthorized guard exit `4` and all other factory/URL/method/no-POST assertions are preserved verbatim) and `Main.fs` (`--prepare-authority-probe` returns the native int from the real `ReleaseRunner.main` and skips the Expecto wrapper). No production source, no `OrchestrationAssertions` cases, and no test-list registration count are touched. A fresh `infrastructure/release/tests/Tests.fsproj` Release build is the only remaining step before the prepared probe can be re-asserted; this record makes no build or PASS claim and waits for the engineer build.

## Focused Release build evidence

The above pending-build note is superseded by this result: the dedicated Dotnet MCP built only `infrastructure/release/tests/Tests.fsproj` in `Release` (`runId=0MF_CAZZNouzGii3Gluz69bNb7We7JmT5dk9xSmbylY`). Result: succeeded, exit 0, 0 errors, 0 warnings. SHA-256 checks before and after the build found no drift in `Tests.fsproj` (both `0AB21C9D7F0A88D5B43AB4BB7408D7FE1FD1A771F2E8C4348496A8C9441F445E`) or the test-source inputs. This is compile evidence only: no test execution, executed-case count, fixture execution, or approval is claimed; the current registered/executed count remains unverified and must be confirmed by the authorized later execution.

## Historical descriptor build evidence

The dedicated Dotnet MCP built only `infrastructure/release/tests/Tests.fsproj` in `Release` (`runId=4GalYdXRum1_TzcF6ZRkMJ-R1o46c7NFy9pkMWWCuHs`). Result: failed, exit 1, 2 errors, 0 warnings. First bounded diagnostic: `infrastructure/release/tests/Historical.fs(128,120): FS0597 This argument expression needs parentheses.` No source files were changed by this assignment. Historical fixture root presence was observed at `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-current-fixtures`, with `Dotnet.zip` (1,289,835 bytes) and `Workflow.zip` (1,514,420 bytes); no fixture test was executed. This is compile evidence only, not a test PASS.

## Workflow Release build evidence

The dedicated Dotnet MCP built `workflow/Mcp.Workflow.fsproj` in `Release` (`runId=ANnfsl0wAHQzEaPrzwsJEh1rzxBcan7_iUbmVR3iL88`). Result: succeeded, exit 0, 0 errors, 0 warnings. The retained build output confirms the canonical project and actual output line: `Mcp.Workflow -> D:\repos\private\mcp-store\workflow\bin\Release\net11.0\Mcp.Workflow.dll`. The artifact is in this workspace at `D:\repos\private\mcp-store\workflow\bin\Release\net11.0\Mcp.Workflow.dll`, size 890,368 bytes, `LastWriteTimeUtc=2026-10-14T09:34:28.2199145Z`, SHA-256 `5A946464EDB5E8FB4FBC6F7C3EC186CC91FE496E096E93EF1F50450BFA8D54F5`. Its pre-build observation was 872,448 bytes, `LastWriteTimeUtc=2026-10-11T14:43:28Z`, SHA-256 `3025123592DA37146BDB71E07BFE79C1C3EC6C065356E23EF7FF8ABD2BFAAEE1`; the post-build artifact is therefore refreshed. Compile evidence only: no tests or packaging were run, and no version/metadata claim is made.

## Simplified release runner integration build

The dedicated Dotnet MCP built only `infrastructure/release/Release.fsproj` in `Release` (`runId=89Y9nngExojxaMUiAsYPWZYQ_3A_lmZaib2P-mBurzI`). Result: failed, exit 1, 16 errors, 0 warnings. The bounded errors page returned 16 diagnostics (the same eight diagnostic entries repeated); distinct compiler errors are:

- `infrastructure/release/Evaluate.fs(286,47)`: FS0039 `CandidateArchiveSnapshot` has no `InnerArchiveBytesCopy` member (suggested member: `ArchiveBytesCopy`).
- `infrastructure/release/Evaluate.fs(288,50)`: FS0039 `CandidateArchiveSnapshot` has no `EmbeddedManifestBytesCopy` member.
- `infrastructure/release/Runner.fs(89,37)`: FS0039 record label `Owner` is not defined.
- `infrastructure/release/Runner.fs(89,52)`: FS0039 record label `Repository` is not defined.
- `infrastructure/release/Runner.fs(89,71)`: FS0039 record label `Tag` is not defined.
- `infrastructure/release/Runner.fs(110,23)`: FS0025 incomplete pattern matches.
- `infrastructure/release/Runner.fs(111,19)` and `(112,19)`: FS0001 expected `Async<Result<ReleaseEnrollment.Roster,ReleaseError>>`, got `Result<'a,'b>`.

No production or test source was edited, and no retry or test execution was performed. This is current integration compile evidence only; fixes belong to the production owner, and the independent tester remains responsible for test sources and execution.

## Historical descriptor syntax fix (pending build)

Tester-scoped single-line edit at `infrastructure/release/tests/Historical.fs:128`: the trailing format argument `fixtures.GetArrayLength()` is now parenthesized inside the `sprintf` call, so F# no longer parses it as a method call on the partial application of `sprintf`. Exact replacement: `expectedEntryCount fixtures.GetArrayLength()` → `expectedEntryCount (fixtures.GetArrayLength())`. No semantic change; no other source, fixture, descriptor, or review file is touched by this edit. This is a syntax correction only and makes no build or PASS claim; a fresh `infrastructure/release/tests/Tests.fsproj` Release build is required to confirm the FS0597 is gone.

## Simplified test integration Release builds

Per the exact-build assignment, the dedicated Dotnet MCP built each requested project once in `Release`:

- `infrastructure/release/tests/Tests.fsproj`, run `D1f1SgDCxWPo78dX9FCD7ruFhnol-AUSN4xdI_sG6VE`: failed, exit 1, 22 emitted diagnostics (11 unique compiler locations), 0 warnings. The bounded errors page repeated each location twice. Unique diagnostics:
  - `infrastructure/release/tests/Evaluate.fs(113,108)`: FS0010 unexpected identifier; expected interpolated-string token or other token.
  - `infrastructure/release/tests/Integrity.fs(207,126)`: same FS0010.
  - `infrastructure/release/tests/Policy.fs(81,60)`: FS0010 unexpected `)`; expected `.` or other token.
  - `infrastructure/release/tests/Handoff.fs` at `(94,102)`, `(105,99)`, `(128,98)`, `(206,101)`, `(227,98)`, `(251,98)`: FS0010 unexpected identifier; expected interpolated-string token or other token.
  - `infrastructure/release/tests/Authority.fs` at `(159,113)` and `(189,136)`: same FS0010.
- `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj`, run `uGp_eDZRIFopv3jLQ8Hq4SJK6ypBHY8WfHvE54-5Va0`: succeeded, exit 0, 0 errors, 0 warnings.

No test execution or test PASS is claimed. The infrastructure test project SHA-256 was unchanged across the build (`74FA6A962346B81EC661FD2DAA2E2585ADE9D9DAE1F3B89FBC3E2A00F28664DE`); the dotnet test project SHA-256 was `9A552A972528EAA731033CFE1C27378B66B4948EA393DA0B95689F085CF949C7` before and after. Source-file hashes were collected after the builds only, so source-input drift cannot be compared against a before snapshot. The working tree was already dirty with simplification changes before the builds; this evidence is not a clean-artifact or clean-checkout proof. No retry or source/test correction was performed.
## Test-only repair pass (mayEdit=true, mayBuild=false, mayTest=false, mayDelegate=false)

Source owner scope: `infrastructure/release/tests/{Evaluate.fs, Integrity.fs, Policy.fs, Handoff.fs, Authority.fs, Tests.fsproj}` and `Main.fs`. Production code untouched; no Snapshots/Fixture/Process/Orchestration/Runner/Compare/Historical/Invocation/Metadata/Publish/Enrollment/Coherence/Revision/Telegram edits. No reflection-arity fallback, no old receipt/Bootstrap/FSI compatibility shim, no env/host value read, no git mutation.

### Errors bounded from MCP `dotnet.details kind=errors` for run `D1f1SgDCxWPo78dX9FCD7ruFhnol-AUSN4xdI_sG6VE` (unique locations deduped to 11)

| # | File | Pos | Code | Cause | Fix |
|---|------|-----|------|-------|-----|
| 1 | Evaluate.fs | (113,108) | FS0010 | `{Guid.NewGuid():ToString N}` malformed F# interpolation; `ToString` is not a valid format token after `:` | `{Guid.NewGuid():N}` |
| 2 | Integrity.fs | (207,126) | FS0010 | same | same |
| 3 | Handoff.fs | (94,102) | FS0010 | same | same |
| 4 | Handoff.fs | (105,99) | FS0010 | same | same |
| 5 | Handoff.fs | (128,98) | FS0010 | same | same |
| 6 | Handoff.fs | (206,101) | FS0010 | same | same |
| 7 | Handoff.fs | (227,98) | FS0010 | same | same |
| 8 | Handoff.fs | (251,98) | FS0010 | same | same |
| 9 | Authority.fs | (159,113) | FS0010 | same | same |
| 10 | Authority.fs | (189,136) | FS0010 | same | same |
| 11 | Policy.fs | (81,60) | FS0010 | `&_` is not addressable in F#; `_` discard cannot be a by-ref receiver; the `|> fun _ -> true` wrapper was hiding an unreachable out-parsed expr | introduced `let mutable parsedVersion: Version = Unchecked.defaultof<Version>` then `Expect.isTrue (Version.TryParse(actual.Version, &parsedVersion))`; same observable (TryParse bool, parsed instance discarded) |

### Tests.fsproj reorder

Moved `<Compile Include="Authority.fs" />` from between Handoff.fs and Revision.fs to after Orchestration.fs to match the `testSequenced` order declared in `Main.fs` (Authority.tests listed last). Authority.fs depends on types from Runner.fs and Handoff.fs, which are now compiled before it; reflection-based `runPublishPipelineWithTransportFactory` lookup still resolves at runtime.

### Items intentionally NOT changed (out of scope per assignment)

- Production `infrastructure/release/Release.fsproj` / policy / YAML / JSON / Dotnet sources.
- `Snapshots` helper, `Fixture`, `Process`, `Compare`, `Historical`, `Invocation`, `Metadata`, `Publish`, `Enrollment`, `Coherence`, `Revision`, `Telegram`, `Orchestration`, `Runner` (no errors reported there).
- Embedded NUL test uses correct `\u0000` F# unicode escape; not asserted broadly.
- 3-job workflow fixtures preserved with the actual signal names (POST=0/1/2/8, GET=3/5/8, exit=0/1/4, factory-call, no-retry, immutable payload, negative revision); no 4-job obsolete signals dropped or masked.
- No new `GetEnvironmentVariable` for host credential values; only NAME-based strip + sentinel dummies per existing tests.

### Verification

- All 11 unique error locations now resolve to valid F# syntax.
- No PASS / build / test claims made (mayBuild=false, mayTest=false).
- Ready for engineer to run `ReleaseTests` MCP rebuild for confirmation; DotnetTests project unchanged.
- Expected outcome: ReleaseTests build → 0 errors.

## Test-only repair pass for run `-86RXRdMaSFySihpQ5fgbXRmUmUrTHci3j10l8OZPN8` (mayEdit=true, mayBuild=false, mayTest=false, mayDelegate=false)

Source owner scope: `infrastructure/release/tests/{Enrollment.fs, Evaluate.fs, Invocation.fs, Handoff.fs, Runner.fs, Authority.fs}`. Production code untouched; no `Tests.fsproj` reorder (Authority.fs already positioned after Orchestration.fs by the previous pass). No reflection-arity fallback, no old receipt/Bootstrap/FSI compatibility shim, no env/host value read, no git mutation.

### Errors bounded from MCP `dotnet.details kind=errors` for run `-86RXRdMaSFySihpQ5fgbXRmUmUrTHci3j10l8OZPN8` (unique locations deduped to 21; 42 emitted diagnostics repeat the 21 because F# re-emits on every overload resolution pass)

| # | File | Pos | Code | Cause | Fix |
|---|------|-----|------|-------|----|
| 1 | Enrollment.fs | (70,61) | FS0041 | `JsonValue.Create componentId` inside `configText` had no inferred arg type; the same applies to the 7 sibling assignments | annotated every parameter of `let private configText (componentId: string) (version: string) (entryDll: string) (sdk: string) (runtime: string) (platform: string) =` so all overloads resolve to the `Create(string?)` path |
| 2 | Enrollment.fs | (71,57) | FS0041 | same | same |
| 3 | Enrollment.fs | (72,58) | FS0041 | same | same |
| 4 | Enrollment.fs | (73,60) | FS0041 | same | same |
| 5 | Enrollment.fs | (74,64) | FS0041 | same | same |
| 6 | Enrollment.fs | (75,58) | FS0041 | same | same |
| 7 | Enrollment.fs | (79,48) | FS0041 | same | same |
| 8 | Evaluate.fs | (116,32) | FS0041 | `Path.GetFileName archivePath` had unknown arg type; `Path.GetFileName` has `(string) → string` and `(ReadOnlySpan<char>) → ReadOnlySpan<char>` overloads | annotated `let private verifiedCandidate (archivePath: string) (manifestPath: string) (evaluatedHead: string) =` (mirrors the existing `dtoFrom (archivePath: string) (manifestPath: string)` pattern); `Path.GetFileName archivePath` now resolves to the `string → string` overload; the 7-argument `VerifiedCandidate.createFromSnapshot` invocation at lines 126-132 already matches the production `snapshot, policy, archiveName, evaluatedHead, metadataLocalPath, repositoryOwner, repositoryName` order at `infrastructure/release/Evaluate.fs:268`, so no production semantic gap |
| 9 | Invocation.fs | (102,16) | FS0039 | `Expect.failtestf` is not exposed; in Expecto 10.2.3 the test-side `failtestf` lives in `Expecto.Tests`, opened through `open Expecto`, but it is reachable as bare `failtestf` (which the other test files already use), not as `Expect.failtestf` | dropped the `Expect.` prefix on the 4 calls at lines 102, 104, 117, 119 |
| 10 | Invocation.fs | (104,16) | FS0039 | same | same |
| 11 | Invocation.fs | (117,16) | FS0039 | same | same |
| 12 | Invocation.fs | (119,16) | FS0039 | same | same |
| 13 | Handoff.fs | (131,18) | FS0041 | `File.WriteAllBytes(...)` had unknown `payload` arg type because the higher-order `entries` parameter of `exactTwoEntryNegativeCase` was unannotated | annotated `entries: ReleasePolicy.PolicyProjection -> byte[] -> byte[] -> (string * byte[]) list`; `payload` resolves to `byte[]` and the `File.WriteAllBytes(path: string, bytes: byte[])` overload binds |
| 14 | Handoff.fs | (287,71) | FS0002 | the lambda passed to `exactTwoEntryNegativeCase "a duplicate inner archive name"` had 4 parameters (`fun policy inner _ _ -> ...`) but the helper expects a 3-parameter `entries` function | changed the lambda to `fun policy inner _ -> [ policy.ArchiveName, inner; policy.ArchiveName, inner ]` |
| 15 | Handoff.fs | (288,69) | FS0002 | the lambda passed to `exactTwoEntryNegativeCase "a case-insensitive collision"` also had 4 parameters | changed the lambda to `fun policy inner _ -> [ policy.ArchiveName, inner; caseVariant policy.ArchiveName, inner ]` |
| 16 | Runner.fs | (254,11) | FS0001 | `runnerPublishRejectsWrongRepo () = testCaseAsync ... <| async { ... }` declared a 0-arg `unit -> Test` function; the surrounding `testList` expects `Test` directly (matches the other tests at lines 41, 52, 61) | dropped the spurious `()` so the binding evaluates to `Test` |
| 17 | Runner.fs | (255,11) | FS0001 | same pattern in `runnerPublishRejectsUppercaseEventSha` | same |
| 18 | Runner.fs | (256,11) | FS0001 | same pattern in `runnerPublishRejectsUnknownFlag` | same |
| 19 | Runner.fs | (257,11) | FS0001 | same pattern in `runnerPublishRejectsMissingRequiredFlag` | same |
| 20 | Runner.fs | (258,11) | FS0001 | same pattern in `runnerPublishRejectsUnregisteredConfig` | same |
| 21 | Authority.fs | (275,12) | FS0066 | `factoryObj :> obj` in the `let invoke : obj[] = [| ... |]` initializer is unnecessary because `factoryObj` is already typed `obj` (line 255 `let factoryObj : obj = box factory`); `TreatWarningsAsErrors=true` makes the warning an error | dropped the redundant `:> obj` so the array element is just `factoryObj` |

### Items intentionally NOT changed (out of scope per assignment)

- Production `infrastructure/release/Release.fsproj` / policy / YAML / JSON / Dotnet / Workflow / Update.ps1 sources; release projects already build 0/0 and DotnetTests 0/0.
- `Tests.fsproj` compile order (Authority.fs is already after Orchestration.fs, Runner.fs and Handoff.fs precede it as required for the `testSequenced` registration); no dependency movement triggered by this edit set.
- `Fixture`, `Process`, `Snapshots`, `Compare`, `Historical`, `Metadata`, `Publish`, `Integrity`, `Revision`, `Telegram`, `Orchestration`, `Coherence`, `Policy`, `Main` (no errors reported there).
- All assertions remain verbatim: zero-POST preflight, full metadata read, digest readback, no-retry post-upload, immutable byte snapshot, source context / hash, runner HTTP 500 (1) on scripted transport FAILED, Authority guard exit (4) on unauthorized preauth; the 7-argument `runPublishPipelineWithTransportFactory` reflection invoke through `runPublishPipelineMethod.Invoke (null, invoke)` keeps the exact boxed tuple order (`factoryObj, policy, snapshot, eventSha, metadataLocalPath, owner, name`) and `parameterCount = 7` guard is unchanged.
- No `GetEnvironmentVariable` for host credential values; only env-NAME strip + sentinel dummies in the owned child per the existing tests.
- No suppression added: only the unnecessary `:> obj` was removed; no `#nowarn` / `TreatWarningsAsErrors` relaxation.

### Verification

- All 21 unique error locations are now resolved: function parameter annotations re-bind the SDK/JSON/Path/FileWrite overloads; the `failtestf` calls match the Expecto 10.2.3 surface via `open Expecto` (bare token, not `Expect.`); the `exactTwoEntryNegativeCase` 4-arg lambdas now match the annotated 3-arg `entries` signature; the five publish-rejection tests return `Test` directly; the Authority reflection array initializer no longer carries an unnecessary upcast.
- No PASS / build / test claims made (mayBuild=false, mayTest=false).
- Ready for engineer to run the dedicated `dotnet test` MCP rebuild for confirmation; both ReleaseTests and DotnetTests projects are unchanged at the project-file level.
- Expected outcome: ReleaseTests build → 0 errors.
