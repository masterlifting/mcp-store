# Modern test migration review

Baseline: prior suite/scenario inventory in `HEAD:infrastructure/release/tests/Main.fs`, `HEAD:infrastructure/release/tests/Tests.fsproj`, and `.tasks/GHI-24/docs/review/Migration.md`; this maps behavior, not Expecto top-level count.

| Prior meaningful scenario | Current replacement / disposition |
| --- | --- |
| Enrollment/projection and roster identity, exact membership, config-path/toolchain drift | `Enrollment.tests` plus Bootstrap subprocess cases; assertions remain registered. |
| Coherence matrix/output/CLI drift; Telegram isolation (`releaseWorkflowAndRosterUseDynamicParticipants`, `releaseWorkflowExcludesTelegram`) | `Coherence.tests` modern workflow checks; `Telegram.tests` checks roster, prepare/publish wiring and isolation. |
| Orchestration ordering, child failures, and publication-authority refusal | `Orchestration.tests` now covers `prepare` success/failure/auth boundaries; `Runner.tests` covers missing trusted publisher closure. |
| W5 AC-01/02/03 integrity: preflight identity, claimed digests, recovery | `Integrity.tests` exercises both producers: zero-POST negatives, strict remote digest checks, observed identity/revision, no-op and metadata-only recovery, exact readback GET/POST counts. |
| Former core suites (Bootstrap, Compare, Evaluate, Historical, Invocation, Metadata, Publish, Revision, Runner) | Still explicitly registered in `Main.fs`; existing production-facing scenario assertions remain in their modules. |
| No prior policy-parser / typed-handoff equivalents | Additive `Policy.tests` and `Handoff.tests` are compiled and registered in `Tests.fsproj` and `Main.fs`. |

Retiring tests that invoke `ReleaseOrchestration.orchestrate` is justified only by the removed CLI mode; `Runner.fs` exposes `prepare`/`publish` and rejects legacy positional paths (no compatibility alias). The negative “workflow contains no orchestrate” check remains valuable.

**RED — integration coverage gap, not a compiler finding:** Coherence and Telegram verify workflow text/markers; `Orchestration.tests` exercises preparation only. No current test drives the full prepare → independent trusted-builder receipt/closure → publish workflow end to end.

**Stale production API reference:** `infrastructure/release/Coherence.ps1:54-55` still requires an `orchestrate` workflow command. `infrastructure/release/tests/Coherence.fs:57` mirrors that legacy command in its synthetic baseline, so its guard assertions do not establish coherence against the modern workflow. Update these in a separately owned source change.

Build/test verification was not run in this doc-only review; engineer-owned test-project build evidence is separate. No source or test files were changed.
