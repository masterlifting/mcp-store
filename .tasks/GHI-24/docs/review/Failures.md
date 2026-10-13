# Remaining modern gate failures

The full gate completed 153 cases: 150 passed, 3 failed, none ignored or errored; provenance passed. Full-run failure names were not retained. Subsequent focused diagnostics reproduced three failures across fourteen cases with stable affected source and DLL hashes. Matching counts do not prove they are the same three full-run failures.

1. Coherence native-exit case expects the old publication dependency set. Modern publication also depends on preparation and builder. Update the fixture expectation while preserving the required test dependency and native fail-fast assertions.
2. Coherence publication readiness correctly fails: publication is disabled, there is no executable publication invocation and no independent trusted-receipt path. This is a real blocker; do not waive it.
3. Preparation stage-failure output reports the absolute script path as `phase`, rather than the semantic stage name such as `build`. Repair the production diagnostic contract; retain script path as separate context where appropriate.

Focused output: `C:\Users\andre\AppData\Local\Temp\opencode\Diagnostics.log`. No new full run was performed. The active genuine builder emission smoke and final workflow enablement remain pending.

## Focused regression recheck

The current Expecto inventory contained all 8 scoped cases (5 Coherence, 3 modern preparation). The no-build artifacts postdated the latest relevant edits (`Coherence.fs` 15:42:30Z, `Orchestration.fs` 15:43:37Z, `ReleaseRunner.dll` 15:44:00Z, `Release.Tests.dll` 15:44:16Z). A snapshot of 53 release source/runtime inputs was unchanged across execution: SHA-256 `8E847F4BF767BB7F6BAC4D41242ED01C3764AC78F0D1893406050049B09583B0` before and after.

Commands, run with publication-related environment names removed from the test process:

```text
dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll --list-tests
dotnet run --project infrastructure/release/tests/Tests.fsproj --configuration Release --no-build --no-restore -- --filter-test-list "cross-stage coherence" --sequenced --no-spinner --summary
dotnet run --project infrastructure/release/tests/Tests.fsproj --configuration Release --no-build --no-restore -- --filter-test-list "modern preparation boundary" --sequenced --no-spinner --summary
```

Results: Coherence 5 run, 4 passed and 1 failed; the sole failure remains the genuine publication-readiness blocker (`publication job is disabled`, no executable publish invocation, no independent trusted-receipt path). The actual Workflow native-exit regression passed. Modern preparation 3 run, all 3 passed, including the build/pins/both Workflow package-test failure regression. Combined: 8 run, 7 passed, 1 expected readiness failure, 0 ignored or errored. No full 153-case run, source edit, readiness waiver, or publication attempt was made.
