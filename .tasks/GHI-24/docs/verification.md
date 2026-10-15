# Targeted Verification baseline

Remediation pass 1 has implemented the finite F1–F9 set. Engineering continuation
labels in `remediation.md` denote implementation slices, not additional review or
remediation passes. No targeted Verification has occurred before this baseline.
No editable agents run during the independent Verification wave.

## Source identity

HEAD `3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1`, branch `ghi-24`, plus current
worktree. Blob hash of the implementation diff command recorded in `baseline.md`:
`b9f5a6e761f22835c87be57e1ba0a0dbcabb1244`.

| New path under `infrastructure/release/` | Git blob hash |
| --- | --- |
| `Archive.fsx` | `cfaaa292dd7450d7402ae08894013f73166c5843` |
| `Evaluate.fsx` | `af45e09b5ebc2681a8c145dbe5899f29a9027152` |
| `Suite.ps1` | `e31f722e309390705a451a4d26fad5a4a0e34384` |
| `tests/Bootstrap.fsx` | `29add72da0afcaaa8584e70b77b1bf15b46a37ef` |
| `tests/Compare.fsx` | `c8dc188e398c991c2286a2eea195de71343f68d0` |
| `tests/Evaluate.fsx` | `92892b6c7d3a7cef81d3e3403cbb4157cf22a0c3` |
| `tests/Fixture.fsx` | `592ef39aee666302f7bd675eb185a49bbdd30d28` |
| `tests/Historical.fsx` | `84f7c30bbece70478395729dd0d4278f1830dda5` |
| `tests/Invocation.fsx` | `2cf28be047ffeeb9924ef082d4c670a7019075b9` |
| `tests/Metadata.fsx` | `992f1a9a0733058d5afeccaa96c9d9db4a24b6b9` |
| `tests/Publish.fsx` | `af1a90d394829d96de9ed031cf597d9c7770a971` |
| `tests/Runner.fsx` | `70ebbc0fb3b9da27024d4cf78b6648804f58df59` |
| `tests/Telegram.fsx` | `d4bca56cfab65507523627dccefc41a97cb9167e` |

## Factual evidence

- Current helper build: `sSO_NO5zPkb5zICwmItBZtjZzLLmK_aeLwrMzl7jcf8`, zero
  errors/warnings. Prior all-producer builds at pass-1 source are recorded in
  `remediation.md`; production component compilation inputs are unchanged.
- Validation build: `_s5nB5UOx3-Q7V-1SIkOKkMni48A90CWpYLgOmyVRVI`; analyzer
  reports 48 scripts validated. Existing analyzer regressions: 41 pass.
- Current actual deterministic suite: 250 assertions pass, zero failures/skips;
  optional historical integration against downloaded assets: 10 pass. Exact
  commands, test names and limitations: `tests.md`.
- Existing Expecto MCP execution: `K1u_5MVC5ydiogohL4LBsfG3P1E44CFSscGUSH4A2f0`,
  exit 0. Existing workflow scripts have recorded passing execution.
- Package evidence in `packages.md` identifies the earlier clean checkpoint, not
  the later worktree. Final clean-commit packaging and actual two-revision
  comparison remain required before Acceptance Audit/readiness.
- Actual publication was not performed. HTTP handler tests execute production
  functions with simulated responses. Live GitHub queue/API/CDN behavior remains
  explicitly unverified; historical public asset downloads and digests were
  actually observed.

## Mandate

Each original reviewer verifies only its mapped accepted IDs in `findings.md`,
their constraints, remediation diff and regression evidence. Verdict per ID:
`FIXED`, `NOT FIXED`, or `REGRESSION INTRODUCED`. No new generic Discovery or
non-blocking preference findings. A blocking introduced regression may be
reported with evidence. At most one second remediation pass is available for
remaining blockers. A separate original-task Acceptance Audit follows resolution.

## Pass-1 targeted results

| Independent owner | Accepted scope | Result |
| --- | --- | --- |
| F# correctness reviewer | D1–D4, mapped F1–F4, relevant F5 | FIXED; no blocking introduced regression |
| F# guardian | F1, F2, F3, F5, F9 | FIXED; no material complexity/boundary regression |
| F# validator | R1–R7, mapped F1–F5 and F7 | FIXED; no blocking introduced regression |
| DevOps reviewer | DEVOPS-1–4, mapped F1/F2/F3/F8 and F7 integration | FIXED; no blocking introduced regression |
| Security reviewer | S1–S4, mapped F2/F3/F4/F5/F6 | FIXED |

The finite review set is frozen after pass 1; no second remediation pass was
needed. These targeted results do not substitute for final committed-package
verification, the separate task-first Acceptance Audit, or external review.
