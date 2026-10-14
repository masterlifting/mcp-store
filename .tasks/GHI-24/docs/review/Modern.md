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

## Exact-three outer transport cutover — test scenario evidence

Compiled tests now exercise the modern exact-three-member candidate platform artifact:
canonical inner archive, standalone `distribution.json`, and preparation sidecar. The internal
two-member transport and the `--preparation-receipt` CLI flag are retired; the publisher only
reads `Snapshot.Preparation` from the verified transport bytes.

| Scenario | Test module / case | Coverage role |
| --- | --- | --- |
| Canonical three-entry outer accepts and round-trips parsed receipt | `Handoff.tests.portableCandidate` (dotnet, workflow) | proves inner + standalone manifest + preparation.json survive `readRawTransport` and produce a typed `Preparation` receipt on the snapshot |
| Strict three-member entry set; missing inner, manifest, or preparation rejected | `Handoff.tests.exactOuterEntryNegativeTests` | added cases cover `a missing preparation receipt`, `a duplicate preparation receipt name`, alongside the existing inner/manifest missing/duplicate/case-collision negatives |
| Strict three-member entry set with a fourth entry rejected | `Handoff.tests.exactOuterEntryNegativeTests (an extra entry)` | one combined "extra entry" case keeps the strict-set assertion without an exhaustive matrix |
| Ordinary Unix regular file attribute still accepted across all three entries | `Handoff.tests.ordinaryUnixRegularEntryAccepted` | confirms the file-type guard accepts `S_IFREG` on every member after the cutover |
| Bounded outer ZIP limits still reject entry count, entry size, expansion ratio, total size, and non-ZIP transport bytes | `Handoff.tests.outerLimitsNegativeTests` | member patches reference the now-three-member set |
| Outer ZIP rejects unsafe paths regardless of exact entry count | `Handoff.tests.unsafePathTests` | path safety remains an entry-level property, not a set-level property |
| Outer ZIP rejects DOS reparse-point and disallowed Unix file-type bits on the inner archive | `Handoff.tests.zipAttributeNegativeTests` | attribute guard continues to apply with the sidecar added |
| Inner producer archive without its embedded manifest still rejected | `Handoff.tests.missingEmbeddedManifestRejected` | inner ZIP validity independent of the outer exact-three set |
| Standalone/embedded manifest byte mismatch is rejected with no dual reader | `Handoff.tests.mismatchedStandaloneManifestRejected` | the old two-member early-rejection path is retired; the three-member transport is the only path |
| Snapshot exposes read-only byte copies and streams only | `Handoff.tests.snapshotStorageIsNotExposed` | member streaming covers the new preparation entry alongside inner/manifest |
| Preparation receipt does not claim a later outer digest | `Handoff.tests.preparationOuterDigestIsIndependent` | the receipt still binds only known inner/manifest hashes/sizes, not the outer transport digest |
| Preparation JSON mutation negatives (101 cases) | `Handoff.tests.preparationReceiptMutationTests` | retained: schema, missing/null/uppercase/negative/forbidden-field negatives on the parser |
| Trusted publisher receipt mutation negatives (one coherent family) | `Handoff.tests.trustedReceiptMutationTests` | retained: independent of the cutover |
| Preparation receipt bound hashes/sizes disagree with verified transport bytes | `Handoff.tests.malformedPrepMismatchTests` | one combined case loops the four mismatch dimensions (inner SHA, inner size, manifest SHA, manifest size) |
| Authority gate refuses unauthorized candidates before any HTTP | `Authority.tests.unauthorizedGuards` (3 reject modes × 2 producers = 6 children) | retained: same six guards and two fixed-tag observations, with the pipeline now invoked through the 8-arg `runPublishPipelineWithTransportFactory` whose `Snapshot.Preparation` carries the parsed receipt |
| Authorized controlled transport invokes canonical `/releases/tags/<producer>` without `v` prefix and exits 1 on scripted 500 | `Authority.tests.authorizedFixedTag` | retained: 2 producers, in-process scripted `HttpMessageHandler`, parent never reads host credentials |
| Prepared candidate survives the modern exact-three outer in `ReleaseTransaction` integrity regressions | `Integrity.tests.tests` (W5) | the produced outer now carries preparation.json alongside inner/manifest; recovery, idempotent, byte-identical, no-op and metadata-recovery scenarios all flow through the verified snapshot |
| `VerifiedCandidate.createFromSnapshot` accepts the new exact-three outer | `Evaluate.tests.runSuite` | the verified candidate now exposes the parsed preparation on its snapshot |
| CLI rejects directories in the candidate positional and rejects the retired `--preparation-receipt` flag | `Runner.tests.missingIndependentClosure` | the test now passes a raw-file stub for `boundedTransportPath` and stops asserting on the missing publisher closure only; no old positional-path probe is run |
| Workflow lists twelve non-packaging Workflow entrypoints | `Coherence.tests.releaseWorkflowStopsBeforePublicationWhenWorkflowTestsFail` | aligned to the modern CI step composition |

The 9→8 production signature cutover is reflected in three test touchpoints: `Authority.fs`
drops the separate `Preparation` arg, drops `PublishCli.PreparationReceiptPath`, and the
`makePublishCli` reflection now sees an 18-field record whose field order matches the live
type. `Handoff.tests` and `Runner.tests` keep all retained scenarios; the parser-level
preparation negatives, the trust-receipt negatives, and the eight authority-gate probes
preserve their prior meanings.
