# Stage1 compiled regression migration — GHR-36 W2

This ledger records the former `infrastructure/release/tests/*.fsx` assertions
and their compiled replacements. It is migration evidence, not acceptance or
test-pass evidence. The compiled project references the production
`../Release.fsproj`; production modules are not included in the test project's
Compile list. Each old suite is an Expecto `testCaseAsync`, and its preserved
assertion helpers report any failed assertion through the final Expecto check.
`Main.fs` uses an explicit CLI entrypoint and runs the suites sequenced because
some exercise process-global environment/output state. Constructing suite values
does not execute tests or create files.

## Old suite to compiled suite

| Former entrypoint | Compiled module / Expecto case | Migrated assertion scenarios |
| --- | --- | --- |
| `Bootstrap.fsx` | `Bootstrap.fs` | `bootstrapKeysAreStable`; `updatePs1ProjectEmitsCanonicalValues`; `updatePs1ProjectRejectsDuplicateLiteral`; `bootstrapEntryEmitsCanonicalSummary`. The actual PowerShell projection and duplicate-literal subprocess remain exercised. |
| `Compare.fsx` | `Compare.fs` | `byteIdenticalDecision`; `revisionOnlyEquivalentDecision`; `changedSdkRejection`; `changedCompressionMethodRejection`; `malformedArchiveRejected`; `malformedEmbeddedManifestRejected`; `duplicateOrExtraEntryRejected`; `sameRevisionByteIdenticalShortCircuit`; `trailingBytesArchiveRejected`; `declaredPayloadHashMismatchRejection`; `rawBytePatchClassifiedBeyondRevision`; `rawEntryOrderChanged`; `localAndCentralFlagsChanges`; `centralExtraFieldPresentRejected`; `zip64MarkerRejected`; `archiveSizeBoundRejection`; `entryCountUpperBoundRejection`; `compressionRatioBoundRejection`; `duplicateKeyManifestRejected`; `unknownManifestFieldAddedClassifiedBeyondRevision`; `externalAttributesRejected`; `entryCommentRejected`; `totalUncompressedBoundRejection`; `nonCanonicalDeflateBody`; `boundsAtValidate`. These retain actual raw ZIP fixtures, strict comparison and archive-bound assertions. |
| `Evaluate.fsx` | `Evaluate.fs` | `validateCanonicalAssetUriEmptyRejection`; `validateCanonicalAssetUriWrongOwnerRejection`; `validateCanonicalAssetUriAccepted`; `missingManifestRejection`; `missingArchiveRejection`; `publishedOnArchiveAbsent`; `failedOnArchiveMissing`; `failedOnArchiveImmutable`; `failedOnBumpedSdk`; `unchangedOnByteIdentical`; `unchangedOnRevisionOnlyEquivalent`; `unchangedWithMetadataRecoveryNeeded`; `failedOnMetadataCorrupt`; `failedOnMetadataImmutable`; `failedOnObservedMetadataFieldMismatch`; `publicStatusSetIsExhaustive`; `observedMetadataAssetShaDiffersArchiveShaAndCarried`; `finalizeVerifiedPublicationFillsRemoteDigests`; `finalizeRecoveryPreservesVerifiedArchiveFields`; `finalizeFailureClearsAllPublishedButRetainsRender`. These still call production `ReleaseEvaluate`. |
| `Historical.fsx` | `Historical.fs` | `historicalHashPinning`; `historicalSameArchiveIdentical`; `historicalBumpedCandidateDifferent`; `consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent`. Unlike the old `Suite.ps1` exclusion, this is enrolled in the compiled suite and is not silently skipped. |
| `Invocation.fsx` | `Invocation.fs` | `bootstrapRendersCanonicalSummary`; `bootstrapRefusesWrongSdk`; `bootstrapRefusesWrongRuntime`; `bootstrapRefusesWrongPlatform`. These still execute the actual `Bootstrap.fsx` child. |
| `Metadata.fsx` | `Metadata.fs` | `renderParseRoundTrip`; `renderDeterministicByteExact`; `metadataAssetNameDeterministic`; `compareEqualProjections`; `comparePerFieldDiff`; `compareOptionalToolchainDifferences`; `parseRejectsUnsupportedSchemaVersion`; `parseRejectsMissingRequiredFields`; `parseRejectsMalformedJson`; `parseRejectsEmptyRequiredField`; `writeToFileDeterministic`; `sha256OfFileAsyncDeterministic`; `writeToFileTwiceDeterministic`; `roundTripByteExactIdentical`; `sha256OfMissingFileFailsTyped`. These still call production `ReleaseMetadata`. |
| `Publish.fsx` | `Publish.fs` | `observerUnsafeRedirectRejected`; `observerSafeRedirectObservedAsset`; `observerSafe301ChainFollowedToRelease`; `observerCdnRedirectDropsAuthorization`; `observerHttpRedirectRejected`; `observerLookalikeSuffixHostRejected`; `observerNonDefaultPortRejected`; `observerUserInfoRejected`; `observerFollows303Then307Then308ThenRelease`; `observerChainBoundRejected`; `uploadCandidateIdempotentNoPost`; `uploadCandidateRefusesOverwrite`; `uploadCandidateSuccessPath`; `uploadCandidatePostServerErrorNoRetry`; `uploadCandidateAmbiguousPostMustObserveRemote`; `uploadCandidateSnapshotMismatchZeroRequests`; `uploadCandidatePostTimeoutReadsBackVerifiedBytes`; `uploadMetadataTargetsMetadataAssetName`; `archiveSuccessThenMetadataFailure`. `RecordingHandler` continues to run production `ReleasePublish` through its injected `HttpTransport`; requests are captured locally and no real network is used. |
| `Revision.fsx` | `Revision.fs` | For both Dotnet and Workflow: default informational-version metadata ignores `SourceRevisionId`; explicit counterfactual SDK behavior appends the supplied revision; the built producer PE informational version matches the evaluated SDK metadata. Missing producer DLLs are now explicit prerequisite failures rather than `notRun` output. |
| `Runner.fsx` | `Runner.fs` | `runnerBinaryExists`; `contractJsonShape`; `metadataAssetNameDeterministic`; `cleanTreeGuardContract`; `manifestRevisionGuardContract`; `runnerEmptyArgvExitsWithUsage`; `runnerDirtyTreeEmitsStructuredFailureJson`. Missing runner binaries are hard failures, not skipped cases. The child assertions still parse the binary's JSON and check context plus absent published values. |
| `Telegram.fsx` | `Telegram.fs` | `releaseWorkflowExcludesTelegram`; `releaseWorkflowAndRosterUseDynamicParticipants`; `noUvPytestInReleaseWorkflows`; `noTelegramWorkflowFiles`; `releaseScriptsExcludeTelegram`; `runnerSourceExcludesTelegram`; `productionModulesExcludeTelegram`; `telegramProducerExistsButUntouched`. The W4 matrix check supersedes the obsolete literal-list expectation with exact `Participants.json` component/config pairs, a dynamic projected matrix, no hardcoded participant or CLI bypass, and Coherence guard wiring. |

The former `Fixture.fsx` and `Process.fsx` helpers are now `Fixture.fs` and
`Process.fs`, compiled before their consumers. Compare fixture archive creation
and all temp-directory creation occur inside the executed test case, not during
module initialization. The new `Orchestration.fs` tests additionally exercise
the same public shared orchestrator for both producers: controlled child scripts
record build/pins/tests order, a real failing FSI child produces structured
`tests`-phase diagnostics, and the failure stops before transaction. A
credential-absent observer smoke verifies that production does not reach its
network transport without auth.

## Adjacent script tests and exact references

- `dotnet/tests/ProvenanceTests.fsx` remains the focused provenance contract
  script and now loads `../../infrastructure/Provenance.fs`.
- `dotnet/tests/DistributionTests.fsx` and
  `workflow/tests/DistributionTests.fsx` remain the producer packaging tests;
  their child build/pins purpose is not duplicated in the compiled release suite.
- `dotnet/tests/Mcp.Dotnet.Tests/blocking/Tests.fs` expects the current
  `infrastructure/Provenance.fs` source path. The checker and paired-test file
  migration is recorded below.
- Old release `.fsx` suite entrypoints and their helpers were removed after
  migration; the only release regressions now compile against the production
  assembly rather than reloading release modules into FSI.

## Prerequisites and deferred integration

The full compiled suite intentionally fails when required inputs are absent:

- `Historical.fs` requires `MCP_RELEASE_HISTORICAL_ROOT` to identify a local
  directory containing `dotnet-v1.0.3.zip` and `workflow-v1.0.3.zip`; it checks
  the pinned SHA-256/size values and historical comparison behavior.
- `Revision.fs` requires the already built
  `dotnet/bin/Release/net11.0/Mcp.Dotnet.dll` and
  `workflow/bin/Release/net11.0/Mcp.Workflow.dll` for PE assertions.
- `Runner.fs` requires the existing ReleaseRunner output.
- `Bootstrap.fs` requires a usable PowerShell host; `Invocation.fs` requires
  `dotnet` and the local configured SDK for its child invocations.

No test project build or compiled-suite execution was performed by this tester:
the assignment sets `mayBuild=false`, and the new project has not yet been built
by its engineer owner. The required next build target is
`infrastructure/release/tests/Tests.fsproj` in Release configuration. After that
build, run the Expecto executable (the `dotnet test` adapter may report zero
tests for this console-style entrypoint) and report the historical/PE
prerequisites explicitly. `infrastructure/release/Suite.ps1` still discovers
script entrypoints, while those release suite scripts have been migrated; DevOps
must reroute the workflow and launcher to the compiled Expecto runner. This
handoff makes no W2 acceptance claim and does not change AC-01/02/03 integrity
behavior, which remains deferred to stage 4.

## Engineer compile handoff

The engineer's build run `cczAaROQ80XSvmynHPPqedPt7fnSILqqJvX4wMGxu6k` failed
with 54 `FS0046` errors, all for the reserved test identifier `component` in
`Orchestration.fs`; the engineer reported no errors in other files at that
pass. The tester-only repair renamed those local identifiers to `producerId`
without changing assertions or warning policy. The project must be rebuilt
before compiled execution; no compiled tests were run after this edit.

The following engineer build run `fbTfUOliBCFwipdFw7p4f5d1WIJ852s3sNKHw_Zo-Bc`
reported 34 test-project errors and no warnings: ten `FS0002` errors from
passing callbacks to `testCaseAsync` instead of async test values, plus
`Orchestration.fs`'s ambiguous `JsonObject` indexer and under-typed diagnostic,
mixed record labels, and two matching test-case callback errors. The tester
replaced those wrappers with async test values, typed the indexer key and
`ReleaseLocator`, and did not change assertions, production files, or warning
policy. This repair has not been rebuilt or executed; the engineer should
rebuild `infrastructure/release/tests/Tests.fsproj` before any compiled test run.

## W2 initial compiled execution evidence (superseded by the rerun below)

After the successful engineer build, the dedicated no-build test capability was
invoked for `infrastructure/release/tests/Tests.fsproj` in Release configuration:
run `bLaWMBu3X99Q0jRlZ5BK5Gr_rnP01Rzg-H9-M2SaFr4`, status succeeded, exit 0,
`counts=null`, no TRX. This was not treated as test evidence. The actual compiled
Expecto entrypoint was then run without build or restore:

```text
dotnet run --no-build --no-restore -c Release --project infrastructure/release/tests/Tests.fsproj
```

It exited 1 after 79.5016 seconds: **12 tests, 9 passed, 0 ignored, 3 failed,
0 errored**. Suites reported Bootstrap 9/0, Evaluate 70/0, Invocation 10/0,
Metadata 52/0, Publish 50/0, Revision 22/0 plus 2/0 PE checks, and Telegram
19/0. The three failures were:

- `Historical`: 4/4 assertion failures because
  `dotnet-v1.0.3.zip` and `workflow-v1.0.3.zip` were unavailable. This is the
  explicit historical prerequisite, not a skipped test.
- `Runner`: 5/13 assertion failures. The empty-argv assertion expects stderr to
  start `usage: ReleaseRunner`, while `Runner.fs` currently emits `usage:` as
  its first line. The dirty-tree assertion expects component/version/archive/URI
  context, but the observed JSON returned empty values. In `Runner.fs`, the
  already-parsed contract's dirty-tree branch calls `emitPreContractFailure`,
  which renders only status/reason. Preserve the regression assertions and route
  this behavior mismatch to the production engineer; no production edit was
  made by the tester.
- Shared orchestration: its child marker was `build;pins;`, not the asserted
  `build;pins;tests;`, so the controlled test child did not record its step. The
  output did not expose the structured diagnostic because the step assertion
  failed first. The source paths and public orchestrator branch were inspected;
  the evidence is insufficient to classify this as a test-fixture flaw or a
  production failure. Do not weaken the ordering assertion; obtain the child
  diagnostic before assigning a production fix.

The suite uses the recording HTTP handler for publish regressions, and none of
these tests performed an actual upload or network request. After this execution,
only the orchestration step assertion's failure message was augmented with the
captured diagnostic; its expected sequence stayed unchanged. The Runner fixes
and historical fixture were supplied before the rerun documented next. This
initial result is retained as evidence of the pre-fix failures.

## W2 intermediate rerun after Runner fixes (superseded by final rerun below)

After the engineer's successful `Release.fsproj` / `Tests.fsproj` rebuild, the
dedicated no-build test capability was again invoked for the Release test
project: run `j3-iHUwYGNVaURURY9Jewh6cWk8qIsRkVo751-w6CuQ`, status succeeded,
exit 0, `counts=null`, no TRX. The actual Expecto entrypoint was run with the
approved local historical fixture directory and no build or restore:

```text
$env:MCP_RELEASE_HISTORICAL_ROOT='C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-bootstrap'; dotnet run --no-build --no-restore -c Release --project infrastructure/release/tests/Tests.fsproj
```

It ran 12 Expecto cases in 52.2644 seconds: **11 passed, 0 ignored, 1 failed,
0 errored**. `Historical` passed all 10 assertions, including both archives'
existing SHA-256 and size pins; no archives were fetched or copied. `Runner`
passed all 18 assertions after the engineer fixes. Other suite counters were
Bootstrap 9, Evaluate 70, Invocation 10, Metadata 52, Publish 50, Revision 22
plus 2 PE checks, and Telegram 19, all with zero failures. The one remaining
failure is the Dotnet shared-orchestration child-step test.

The captured diagnostic establishes the cause rather than implicating
production orchestration: `phase=tests`, `childExitCode=1`, with child stderr
`DistributionTests.fsx(3,1): error FS0030` because the generated failing script
ended in an unconstrained top-level `failwith`, creating the generic FSI value
`it`. FSI type-checks the script before executing its earlier marker write, so
the marker correctly remained `build;pins;`. The generator is now repaired to
append `|> ignore` to the deliberate `failwith`; this constrains the script's
final expression to `unit` while preserving the intended runtime failure and
all step-order/diagnostic assertions. No production fix was needed. Because this
is a test edit, the engineer must rebuild `Tests.fsproj` before the final
no-build Expecto rerun. This intermediate result is superseded by the successful
rebuild and final runs below. All verification used local fixtures and the
recording HTTP handler; no network or upload occurred.

## Final Stage1 compiled regression run

The engineer rebuilt `infrastructure/release/tests/Tests.fsproj` in Release:
run `__Y-TLG5jERYTitIB2dRDkdGt1u3htHkpnUHTtggdd0`; retained error and warning
evidence both report total 0. The dedicated no-build test capability again
returned success with `counts=null` and no TRX (run
`ebNhEgqQTVIz5n8POh8uXJjbdAXzsAR_VNOT09v89HY`), so the built Expecto entrypoint
was used for proof:

```text
$env:MCP_RELEASE_HISTORICAL_ROOT='C:\Users\andre\AppData\Local\Temp\opencode\ghi-24-bootstrap'; dotnet run --no-build --no-restore -c Release --project infrastructure/release/tests/Tests.fsproj
```

The command exited 0 after 79.396429 seconds: **12 Expecto cases, 12 passed,
0 ignored, 0 failed, 0 errored**. This is 12 suite cases, not a count of nested
assertions. Runtime helper counters printed by the migrated suites were:
Bootstrap 9; Evaluate 70; Historical 10; Invocation 10; Metadata 52; Publish
50; Revision 22 plus 2 producer PE checks; Runner 18; Telegram 19. Compare's
helper count is not printed. The orchestration suite has no helper counter; its
Expecto assertions passed for both producers.

The both-producer orchestration case confirms each child marker is
`build;pins;tests;`, the actual controlled failing FSI child reports structured
`phase=tests`/exit 1 diagnostics with matching schemas/reasons, and the runner
transaction is not reached. The missing-credentials observer smoke also passed.
Historical fixture hash/size pins matched for both local archives. No archives
were copied or fetched. Publish regressions used `RecordingHandler`; no actual
network request or upload occurred. No test or production source was changed
after this successful build/run. AC-01/02/03 integrity negatives remain stage 4;
this does not mark W2 or any acceptance criterion complete.

## Integrated DevOps release-suite gate

After the same successful build, the tester ran the integrated non-publishing
gate with the approved local fixture root:

```text
pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap
```

Result: exit 0. The gate enumerated and executed the complete 12-case compiled
Expecto suite once, reported 12 passed / 0 ignored / 0 failed / 0 errored, then
ran `dotnet/tests/ProvenanceTests.fsx` (`provenance contract passed`). Final gate
output: `release regression gate passed: 12 compiled case(s) (including 1
historical) plus 1 producer script(s)`. Its executed-count inventory matched
the compiled case count, and the `GITHUB_ENV`/`GITHUB_OUTPUT` before/after
sentinel passed. The two-producer orchestration marker and structured-diagnostic
assertions passed within this gate. Historical assets were read only from the
approved local directory; no download, network request, or upload occurred.
This validates the local Stage1 gate, not hosted Actions behavior or later
stages/acceptance criteria.

## W3 checker/test path migration

The tester moved the checker and its paired tests to the one-word paths
`dotnet/tests/Mcp.Dotnet.Tests/blocking/Checker.fs` and
`dotnet/tests/Mcp.Dotnet.Tests/blocking/Tests.fs`. The prior physical paths
`dotnet/tests/Mcp.Dotnet.Tests/SyncOverAsyncChecker.fs` and
`dotnet/tests/Mcp.Dotnet.Tests/SyncOverAsyncCheckerTests.fs` are historical names
only. Module identities (`Mcp.Dotnet.Tests.SyncOverAsyncChecker` and
`Mcp.Dotnet.Tests.SyncOverAsyncCheckerTests`), the `Mcp.Dotnet.Tests` project and
its assembly identity remain unchanged. Compile order is now
`DotnetSchemaParityTests.fs`, `blocking/Checker.fs`, `blocking/Tests.fs`,
`AsyncTypedFailureTests.fs`, then `Main.fs`; path-based test expectations use
the moved files.

The checker bridge roster drops the deleted producer `Release.fsx` wrappers and
the migrated release `.fsx` suite paths. It recognizes only the exact current
`Runner.fs` async CLI bridge lines and requires the whole observed bridge list
to match the exact path-specific list; the moved paired tests include a
regression proving an extra bridge still blocks. No general regex suppression
or broader path exemption was introduced. Earlier `docs/GHI-24/{async,tests}.md`
and `review/Integration.md` references to former checker paths are preserved
historical evidence, not current implementation references.

At the initial W3 source handoff no build or test was run (`mayBuild=false`);
the following engineer build and tester executions supersede that pending state.

## W3 engineer builds and actual test execution

Engineer builds in Release configuration:

| Project | runId | Status | Errors | Warnings |
| --- | --- | --- | ---: | ---: |
| `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` | `8-AsKjbzyA1Ii0EiDS4rxJBqfKmmupcRo2Wg7N3PNjY` | succeeded | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `wkXI9i2KCEkEg-lspCeND_NHSddtSbrOKsOlb7icUos` | succeeded | 0 | 0 |

Dedicated no-build test calls returned exit 0 but null counts/no TRX, so both
Expecto executables were run directly without rebuilding:

```text
dotnet run --no-build --no-restore -c Release --project dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj
pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap
```

The producer suite reported **151 run, 151 passed, 0 ignored, 0 failed,
0 errored**. Its moved checker tests approved the exact two current compiled
Runner CLI bridge lines, rejected an appended third bridge, and the full active
repository source scan passed; no broad exemption was added. The integrated
release gate reported **12 compiled cases, 12 passed, 0 ignored, 0 failed,
0 errored**, then `provenance contract passed`; its inventory/count and
`GITHUB_ENV`/`GITHUB_OUTPUT` sentinel passed. The historical fixture directory
was local; hash/size assertions passed. Publish tests used the recording
handler; no network request, download, or upload occurred. This is W3 test
evidence only and does not claim any acceptance criterion PASS.

## W4 enrollment and coherence assertion migration

The W4 full-suite execution and its four failures are recorded in
`Enrollment.md`; the following bounded test-only migrations preserve the
production contract and scenario coverage:

- An incomplete roster is structurally parsed by `ReleaseEnrollment.loadRoster`,
  then rejected by `ReleaseEnrollment.validateRoster`. The regression now tests
  that composition and retains the exact missing-Workflow assertion. Source
  inspection confirmed `Bootstrap.fsx`, `Orchestration.fs`, and `Runner.fs`
  perform both calls before proceeding, so no publication path was found that
  bypasses exact-set validation.
- The Update and Coherence subprocess assertions retain the actionable
  `missing required componentId(s): workflow` diagnostic. They normalize only
  ANSI styling, PowerShell continuation bars, whitespace, and case, and surface
  captured output when the diagnostic assertion fails; rejection itself remains
  separately asserted.
  Both compound drift tests execute all controlled roster mutations before
  evaluating assertions, so one mismatch cannot skip later scenarios.
- Telegram isolation now verifies the actual roster has exactly the Dotnet and
  Workflow config pairs, excludes Telegram, and that the workflow matrix derives
  from the projected roster without a hardcoded participant list or CLI path.
  The actual Coherence baseline remains covered by its compiled process test.

The Expecto case inventory is unchanged at 25; existing `Enrollment`, `Coherence`,
and Telegram case values remain registered, with the Telegram case retaining its
nested isolation assertions. The first migrations were rebuilt and rerun; the
latest run is recorded below. No assertion has been removed or replaced by a
weaker success condition.

### W4 rerun and bounded follow-up

After engineer build `5c_W39Td28sDUKk8mWT5i0zUA3t5ZLCo3IlRvgE5S_U` succeeded
with zero errors/warnings, `Suite.ps1` ran again against the local historical
fixture without building. All 25 Expecto cases ran, with 22 passed, 0 ignored,
3 failed and 0 errored; inventory matched and provenance passed. Nested failures
were Bootstrap 6/3 and the Update/Coherence missing-participant diagnostic
assertions. PowerShell formatted the expected message across wrapped lines with
continuation bars, so the literal fragment was split. The compound tests ran all
roster mutations before checking assertions, including config-path drift.

The third failure was the Bootstrap `Update.ps1 -Project` test searching stdout
only for canonical `RELEASE_SDK`/`RELEASE_RUNTIME`/`RELEASE_PLATFORM` lines. Its
required value assertions are retained; the helper now combines stdout and
stderr and includes captured output on failure. The diagnostic normalizers now
remove PowerShell continuation bars as well as ANSI styling and whitespace
formatting while retaining the full missing-Workflow fragment. These follow-up
test edits are not rebuilt or rerun yet; engineer build and tester verification
remain required.

### W4 rerun after diagnostic and stream fixes

Engineer build `fyatmCla11xm-2LHzl0NFfAX3SX9m3i1YKCaav01CwQ` succeeded with
zero errors/warnings. The complete local-fixture gate then ran without building:
25 Expecto cases, 24 passed, 0 ignored, 1 failed, 0 errored; inventory matched,
and provenance passed. Nested summaries included Bootstrap 9/0, Evaluate 70/0,
Historical 10/0, Invocation 10/0, Metadata 52/0, Publish 62/0, Revision 22/0
plus PE integration 2/0, Runner 22/0, and Telegram 22/0.

The sole failure was the Update config-identity fixture: it enrolled Dotnet only,
so the exact-set guard rejected missing Workflow before checking the deliberate
Dotnet `Config.fsx` identity mismatch. The Bootstrap config-ID test used the same
incomplete-roster shape, so its broad assertion could pass for the wrong reason.
Both fixtures now use the exact two-entry roster and provide a valid Workflow
config; identity assertions require the specific Dotnet mismatch. Bootstrap
projection tests passed 9/0 in this run. These fixture changes were rebuilt and
rerun in the following verification.

### W4 rerun after exact identity fixtures

Engineer build `MGgUmEFTDD7vGxYPOzPWrWSVfIacV2RdWzssfLCAvmc` succeeded with
zero errors/warnings. The actual complete `Suite.ps1` gate used the approved
local historical fixture without building: **25 cases, 24 passed, 0 ignored,
1 failed, 0 errored**, inventory matched, and provenance passed. Nested results:
Bootstrap 9/0, Evaluate 70/0, Historical 10/0, Invocation 10/0, Metadata 52/0,
Publish 62/0, Revision 22/0 plus PE integration 2/0, Runner 22/0, Telegram
22/0. The Update and Coherence mutation suites passed all roster, identity/path,
toolchain, matrix/output/CLI and policy scenarios.

The remaining failure was the Bootstrap config-ID test expectation. With its
exact roster fixture, `loadProducerConfig` rejects the mismatched Dotnet config
first, producing `componentId does not match the release pin: expected dotnet,
got workflow`, rather than the later roster-projection diagnostic. The test now
asserts the observed production diagnostic.

### W4 final complete-suite verification

Engineer build `9mVDQdm36zeFQlbTkKkhpHjAxYoV3fLOUnRvqJWqdY4` succeeded with
zero errors/warnings. The actual `Suite.ps1` gate ran without building against
the approved local historical fixture: **25 cases, 25 passed, 0 ignored, 0
failed, 0 errored**; inventory matched, provenance passed, and the gate reported
25 compiled cases (including one historical) plus one producer script. Nested
results: Bootstrap 9/0, Evaluate 70/0, Historical 10/0, Invocation 10/0,
Metadata 52/0, Publish 62/0, Revision 22/0 plus PE integration 2/0, Runner
22/0, Telegram 22/0. Update and Coherence mutation scenarios passed, including
the actionable identity diagnostics and all controlled drift cases. No actual
network request or upload occurred. This is tester evidence for the assigned
slice only, not an overall acceptance-criterion PASS.

## W5 Stage 4 integrity test-source handoff

The engineer added the shared-transport `ReleaseTransaction.run` seam; build
`y6eHGUB9I5X3VuuvNO8cSOjAZ1b-uxucmzZdE5yF3Zw` succeeded with zero errors and
warnings. Release test project build `YFcM_wq9XgSeGlLt-XJVjKiR9STbiGTKl4B5NpXozuE`
also succeeded with zero errors and warnings before these tester edits.

Added `infrastructure/release/tests/Integrity.fs`, its project compile entry, and
the additive Expecto registration. This preserves all existing 25 compiled
regressions and adds one named case, `W5 actual production transaction integrity
regressions`. Coverage labels and AC mapping are recorded in `Integrity.md`:
both-producer AC-01 transaction preflight failures/zero POST plus publish and
recovery positives; independent AC-02 metadata identity/schema/toolchain
negatives; and AC-03 missing/malformed/mismatched claimed-digest checks on
idempotent, post-upload and full transaction readback paths with exact request
counts. All HTTP is scripted and uses only an in-memory sentinel token.

At this handoff the new test source had not yet been built or executed. The
following section records the subsequent engineer build and complete tester run.
No acceptance criterion is claimed PASS at this handoff.

### W5 complete-suite execution

Engineer test build `xqN-mWr2tSrqxrXBwkIyFnrcFF9wDtE65Sm8CLASJIo` succeeded
with zero errors/warnings after the source handoff. The tester then ran the
complete gate without building:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
& pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot 'C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'
exit $LASTEXITCODE
```

Result: exit 0, 26 Expecto cases run / 26 passed / 0 ignored / 0 failed / 0
errored; inventory matched (26 compiled cases including one historical),
provenance passed, and one producer script passed. Nested counters: Bootstrap
9/0, Evaluate 70/0, Historical 10/0, Invocation 10/0, Metadata 52/0, Publish
62/0, Revision 22/0 plus PE integration 2/0, Runner 22/0, Telegram 22/0.

The additive W5 integrity case passed its actual production transaction and
recording-HTTP scenarios for Dotnet and Workflow: 14 AC-01 failing preflight
scenarios plus 6 safe positives; 38 AC-02 independent metadata/schema/toolchain
negatives; and 18 AC-03 missing/malformed/mismatched claimed-digest paths. AC-01
and AC-02 rejected transactions asserted zero POSTs and no published identity.
AC-03 assertions required exactly 2 GET/0 POST for idempotent observation,
3 GET/1 POST for direct post-upload readback, and 5 GET/1 POST for the full
transaction readback, per digest form and producer. Candidate-equal downloaded
bytes did not bypass digest rejection; all cases asserted no unsafe retry.

The scripted handler and fake sentinel token prevented real network activity;
upload, credential, and workflow-output environment variables were cleared
before execution. This verifies the assigned Stage 4 tester slice only; no
acceptance criterion is marked PASS and independent review remains outstanding.
