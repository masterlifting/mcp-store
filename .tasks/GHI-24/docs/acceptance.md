# Acceptance and delivery evidence

## Audited source

Source commit: `f13594db7787e2a0300e1151c8e8697cbf98e91d`.
The separate, fresh task-first validator assessed the original issue and user
constraints, intent, completeness and integration without receiving earlier
review verdicts or remediation narratives. Result: completed, APPROVE, no
confirmed defect. It independently checked current, preserved A and historical
archive hashes and the current embedded revision/SDK.

This internal assessment supports readiness for the requested additional external
review; it is not sufficient evidence of final correctness and authorizes no merge.

## Actual verification

- Real compiled Expecto entrypoint: 150 run, 150 passed, zero ignored/failed/errored.
  An empty `dotnet_test` result with null counts is not used as execution proof.
- Non-publishing deterministic release suite: 272 assertions passed, zero
  failed/skipped. Optional historical-asset integration: 10 passed.
- Strict SDK FCS entrypoint checks: both producer wrappers have zero type errors;
  a malformed unit/bool counterexample fails, as do missing or zero targets.
- Actual clean-commit package A (`d556c20…`) and docs-only commit B (`f13594d…`)
  use preserved raw archives, not reconstructed archives or controlled revision
  fixtures. Production comparison and an independent tester both classify each
  producer as `RevisionOnlyEquivalent`. All non-manifest payloads, compressed
  bytes and archive metadata are equal; only revision and derived ZIP fields vary.
- Repeated packaging at each clean SHA is byte-identical. Canonical build/pins,
  allowlists, embedded/external manifest hashes and SHA provenance are checked.
- Actual non-publishing wrapper smoke runs complete package validation then emit
  structured transport-not-selected failures with no authentication or upload.
- Actual remote reads: both historical `v1.0.3.zip` assets were downloaded and
  digest-verified; fixed releases were re-observed with only those assets present.
  Neither `v1.0.4` archive nor metadata was published during development.

Full commands, hashes, run IDs and retained raw paths are in `proof.md` and
`tests.md`; requirement mapping is in `../release/Requirements.md`.

## Known limits

Hosted GitHub API/auth/CDN behavior, actual uploads, hosted queue capacity/order,
and execution on the hosted runner image are unverified. Local HTTP tests run the
production functions through scripted handlers; static workflow checks and the
documented `queue: max` contract are not claimed as observed hosted execution.
No live release-write test is permitted during development by the user constraint.

## Artifact retention

No debug source or uncontrolled source-tree scratch remains. Preserved raw A/B
packages, historical downloads and diagnostic logs remain outside the repository
as supporting evidence for external review. They were not registered as disposable
TaskScratch manifests and have not been automatically deleted. No user or unknown
files were removed. Task documents remain at `docs/GHI-24/` under the user's
temporary exception; the installed Workflow MCP rejects task-directory entries
other than `runtime.json`.

## Delivery gates

Final documentation commit, push, PR creation and terminal Task state require
explicit confirmation. No push, PR, merge or publication is implied by this file.
