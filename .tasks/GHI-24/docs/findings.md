# Frozen Discovery findings

Baseline: `baseline.md`. Review presentation: Discovery completed; remediation
pass 1 begins against the finite accepted set below. Original acceptance remains
unchanged. Reviewer results are independent; duplicate manifestations are grouped.
This file records findings and evidence, not a second task-state authority.

| ID | Severity | Accepted defect and requirement | Provenance | Owner |
| --- | --- | --- | --- | --- |
| F1 | Error | Metadata lookup/upload uses the archive name, not the versioned metadata name; tests use a non-production projection and hide it. Use canonical metadata asset name with production-shaped tests. | Correctness D1; guardian DEVOPS-01; validator R1; DevOps DEVOPS-1 | F# engineer/tester |
| F2 | Error | No-op parses distribution manifest as release projection and never reconciles missing metadata. Correct schema and immutable metadata recovery must precede unchanged success; published hashes/revision remain distinct from evaluated SHA. | Correctness D2; guardian ARCH-02; validator R2; DevOps DEVOPS-1; security S1 | F# engineer/tester |
| F3 | Error | Successful or ambiguous uploads lack remote observation and exact byte verification. Observe after POST outcomes with bounded reconciliation; unresolved/absent/mismatch fails without blind retry, never replacement. | Correctness D3; guardian DEVOPS-02; validator R3; DevOps DEVOPS-2; security S1; actual failing regression | F# engineer/tester |
| F4 | Error | Revision-only comparison omits raw compressed bytes, entry order, ZIP local/central metadata and unknown manifest fields. Implement full strict raw comparison permitting only revision and necessarily derived CRC/size/offset changes; reject unsupported/inconclusive archives. Bound untrusted archive sizes, entry count and expansion before allocating/decompressing. | Correctness D4; validator R4; security S4 | F# engineer/tester |
| F5 | Error | Default automatic redirects bypass checks; suffix/HTTP host policy can leak authority; legitimate asset CDN redirect is unsupported. Disable auto redirects, use exact HTTPS authority checks and bounded manual redirects; never forward token to CDN; preserve safe documented asset download support. | guardian SEC-01; validator R5/R6; security S3; correctness uncertain integration gap | F# engineer/tester |
| F6 | Error | Upload rereads a mutable candidate path after validation without hashing the exact transmitted buffer. Verify the exact bytes immediately before POST and send that same immutable buffer; mismatch prevents any upload. | security S2 | F# engineer/tester |
| F7 | Warning, accepted | Failure/bump branches exit before structured JSON, losing required actionable reason and summary fields. Emit structured failure before nonzero exit; wrapper/workflow preserve status/reason and published-vs-candidate evidence. | validator R7; DevOps owner pre-Discovery factual note | F# and DevOps engineers/tester |
| F8 | Error, with accepted warning | Focused release regression suite is not a CI publication gate. Add non-publishing actual suite execution and propagate failures; recheck event HEAD/ancestry immediately before publication to reduce stale-force-push gap. | DevOps DEVOPS-3/4 | DevOps engineer/tester |
| F9 | Warning, accepted | Runner reconstructs asset URI from environment instead of canonical producer policy, allowing pins/projection divergence. Carry canonical URI and validated identity through the transaction; reject incompatible repository substitution. | guardian ARCH-01 | F# engineer/tester |

## Disposition

All confirmed errors and both warnings are accepted for pass 1. No criterion is
waived or weakened. No speculative unrelated cleanup, general lint framework,
tag redesign, automatic versions or consumer/Telegram changes are accepted.
F5's hosted redirect uncertainty is treated as a required safe adapter integration
case, with deterministic production-path tests and explicit live-evidence limits.

Implementation owners repair only F1–F9 and build. Tester adds real regression
coverage for these defects and executes it. Each original reviewer receives only
its mapped accepted IDs, remediation diff and factual evidence for targeted
Verification (`FIXED`, `NOT FIXED`, `REGRESSION INTRODUCED`). One second pass is
allowed only for remaining blocking findings or blocking introduced regressions;
after that, unresolved blockers stop the task. A separate task-first Acceptance
Audit follows the resolved bounded cycle without these verdicts or narratives.
