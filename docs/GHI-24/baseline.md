# Discovery baseline

The authoritative original issue is `docs/GHI-24/original.md`; its body is
unchanged from blob `5381c2945b5ac78a24b68040dfa605acd1e18651` in checkpoint
`3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`. The additional user requirements
include task-runtime lifecycle, independent acceptance audit, real acceptance
tests, no release publication during development, comments and one-word naming.
The requirements matrix is `docs/release/Requirements.md`; the frozen solution
is `docs/GHI-24/solution.md`. Neither replaces the original acceptance baseline.

## Identity

Branch `ghi-24`, HEAD `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`, plus the
current working-tree implementation and tests. Git blob hash of
`git diff HEAD -- .github dotnet/release workflow/release infrastructure/release infrastructure/scripts/tests/Regression.fsx`
is `8184f325a83b61c9a782b97103ce08f2ac8b071e`.

| New test path under `infrastructure/release/tests/` | Git blob hash |
| --- | --- |
| `Compare.fsx` | `327f361d1e667ff8b9c6e59456a9df524f0bcf2f` |
| `Metadata.fsx` | `e0d92eb49859bd96af70eb4194a03274465c9c6b` |
| `Transaction.fsx` | `d6189d7786013a8fde1b0414a382b3947c80da13` |
| `Publish.fsx` | `7b4846b212e769b0d5d74e8056b280b43e5ddeea` |
| `Runner.fsx` | `48459ebf3b621699b62bb332f7d2b49c7014f5d2` |
| `Bootstrap.fsx` | `65a5dc1c8b53d48c1d20a0fe9f07b54113c9cd45` |
| `Telegram.fsx` | `8e17c4e6e962ff55cbe4632123446376c1c332ed` |
| `Invocation.fsx` | `09580a09b1237f7b524eb224d9289ddfe5cfe0e6` |
| `Historical.fsx` | `ccd06568c39c39f796950ba2fa7499b422372c7b` |

No editable work runs during the independent Discovery wave.

## Factual verification

- Current helper Release build: `wrxBBNHlkylP4lrjVe25BHS743I9Pf5vm-l6uIMU0qU`,
  exit 0, zero errors/warnings; script validation: 47 scripts, reusable and
  helper roots typechecked. Comment-only changes verified against prior source.
- Current release regression suite: 176 assertions pass, one fails; exact
  commands and results in `docs/GHI-24/tests.md`.
- The failing actual transport test is `uploadCandidateAmbiguousPostMustObserveRemote`:
  GET absent, POST timeout, queued remote GET is never issued. This factual
  failing regression is not a prior reviewer verdict.
- Existing Expecto MCP execution exits 0, run ID
  `dlHx89NT43tZPiNKdDQX_jQd3jMrQFyX6XgYGGbrmTw`; existing workflow scripts
  and analyzer regressions have recorded passing execution in the test report.
- Clean checkpoint packages and repeated deterministic hashes are recorded in
  `docs/GHI-24/packages.md`. These identify the checkpoint, not later worktree
  source. Historical release bytes were downloaded and digest-verified; no real
  upload has been executed. Mock HTTP handler execution is distinguished from
  live GitHub observation and hosted queue behavior.

## Review boundaries

One independent Discovery wave uses the combined profile (correctness,
conformity, contract adequacy), with DevOps and security added for the concrete
publication and trust surfaces. Findings will be triaged and frozen into a
finite remediation set. A separate task-first Acceptance Audit follows bounded
remediation and targeted Verification; it receives no Discovery verdicts.

Evidence storage under `docs/GHI-24/` is a user-authorized temporary exception:
the installed Workflow MCP permits only `runtime.json` in `.tasks/GHI-24/`.
Requested `.tasks/GHI-24/references/` relocation remains deferred until a
compatible producer is available. Existing runtime evidence paths to earlier
document names are historical references; current names above are authoritative.
