# GHI-24 frozen solution contract

## Authority and baseline

`original-issue.md` preserves the original issue verbatim. The user request adds
verification and delivery obligations; runtime AC1–AC9 and
`docs/release-requirements.md` map them without weakening the issue.
Baseline: `30d82304e05899e2b1c83d9255fbe2f17abc0b7f`, branch `ghi-24`.
Review profile: combined, with additive DevOps and security review.

## Selected solution

1. A single push-to-main workflow, no path filters or optional dispatch, checks
   out the exact event SHA. One repository/main release concurrency group uses
   `queue: max` and `cancel-in-progress: false`; producer evaluations remain
   independent. No real release writes during development.
2. Canonical producer Config.fsx retains ownership of all producer policy.
   A minimal bootstrap projection reads canonical SDK literals, rejects ambiguous
   input and is checked against evaluated F# config. Install/select the exact SDK;
   verify the executing SDK and runtime identity, project/package identity and
   canonical platform/architecture before validation or publication. Do not move
   policy into a duplicate root global.json or YAML table.
3. Shared publication mechanics live under `infrastructure/release/`, with small
   one-word modules separating strict archive validation/comparison, deterministic
   decisions/transaction and GitHub effects. Producer release entrypoints compose
   existing Build/Pins and focused validation. Follow existing script closure,
   role-header and async ownership contracts. No generic release framework.
4. Both producers run actual builds, required deterministic tests, distribution
   validation and canonical pin preparation. Clean committed HEAD and manifest
   revision must equal the event SHA. Failed validation cannot reach upload.
5. Strict ZIP comparison validates structure and all local/central metadata,
   payload hashes, manifest fields and remote digest. The sole permitted change
   is the embedded validated revision string and its necessarily derived CRC,
   compressed size and subsequent offsets. No broad JSON normalization or
   unpack/repack comparison that discards metadata. Unsupported/inconclusive
   representations fail closed. Exact equality is also validated, not trusted.
6. Resolve existing fixed tags/releases; missing or immutable containers fail
   actionably. Enumerate assets, reject ambiguity, verify remote digest and bytes.
   Never move tags or overwrite/delete assets. After any upload response, observe
   the remote and verify exact candidate publication. Ambiguity permits bounded
   observation, never blind retry; unresolved results fail closed.
7. Publish versioned harness-neutral metadata, immutable and verified, reporting
   published hashes/revision and evaluated HEAD separately. Existing no-ops use
   published values, not candidate pins. Metadata conflicts fail closed.
8. Historical v1.0.3 packages must actually be inspected and compared. Any
   differences beyond revision require authored producer version bumps in this
   PR, separately supported by evidence; no automatic CI bump.
9. Stale events retain exact SHA and must be proven safe ancestors of main;
   unsafe/inconclusive reruns fail rather than substituting newer source. Queue
   overflow/cancellation needs observable reconciliation diagnostics, not claims
   based only on YAML text. Distinguish simulated API/queue evidence from actual
   hosted behavior, which cannot be established by publishing during development.

## Non-goals and rejected alternatives

Telegram/consumer/harness integration, real release publication during development,
auto versions, tag redesign, path-only skips, ambient SDK, duplicated metadata,
mutable existing assets, generic frameworks and unrelated refactoring are excluded.

## Verification and gates

Engineer owns implementation/build/package verification; tester owns regression
design/execution. Tests exercise actual comparison/transaction/bootstrap/identity
and queue behavior, with injected transport where remote writes are prohibited.
One independent Discovery wave follows factual build/test evidence. Freeze a
finite accepted findings set; at most two bounded remediation passes with targeted
Verification. Separate task-first Acceptance Audit receives original requirements,
final code identity and factual evidence, without prior verdicts or narratives.
Commit/push/PR and Complete status remain explicit confirmation gates.
