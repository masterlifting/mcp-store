# Coherence fixture migration

## Fixture and preserved cases

The `Coherence.fs` process fixtures now start from the checked-in modern
`release.yml`, including the `bootstrap`, read-only `tests`, matrix-scoped
`prepare`, read-only `builder`, and isolated `publish` job shapes. The temporary
roster and workflow path still exercise the production `Coherence.ps1`; its
current marker set is checked by successful guard execution, not by pinning an
assertion count. Drift mutations retain the matrix, bootstrap output, emitted
component projection, and matrix-derived prepare CLI diagnostics.

The existing roster-policy/inventory case retains forbidden policy fields,
duplicate and unsupported participants, required-participant omission, and
registered config-path drift. The explicit-projection case still passes the
actual `Update.ps1 -Project` component output as a value and verifies that the
guard does not read a step-local output file. The Workflow native-failure case
still proves that the first failed entrypoint's exit is preserved and later
entrypoints do not run; publication must depend on the read-only test job.
Telegram isolation continues to cover executable-workflow exclusion, exact
roster membership, projected matrix selection, dynamic prepare CLI paths, and
the separate builder/publication job permissions.

## Readiness remains separate from coherence

The actual guard may accept the current workflow's component/job wiring even
though its publish job remains closed. The existing workflow-readiness case is
therefore stricter: it reports a closed publisher, missing executable
`ReleaseRunner.exe publish` invocation, or missing explicit `--trusted-receipt`
path, in addition to unsafe job shape. Against the current workflow this case is
expected to remain **RED**: `publish` has `if: false`, fails closed, and does not
yet receive the independent trusted-receipt path. This is an actual pipeline
readiness blocker, not a reason to weaken the coherence fixtures or skip the
case. The compiled CLI/receipt-path change and workflow enablement are separate
production work.

The native-failure case now asserts that publication depends on exactly
`bootstrap`, `tests`, `prepare`, and `builder`, while retaining the explicit
Workflow list and native-exit/first-call checks. No build or test was run;
compiled verification remains pending.

No build or test execution was performed in this test-source handoff. The
engineer-owned test-project build is still required before compiled verification;
afterward, the readiness case is expected to keep the full gate red until the
trusted-receipt publication path is implemented safely.
