# Updated main and task evidence location

2026-10-13 coordinator baseline observation. User requested continued remediation, awareness of updated main, and task-related references under `.tasks/GHI-24`.

- Current remediation HEAD: `523f625014dbf81fb788fe5c11877fc7a9626382`, with uncommitted modern-pipeline changes.
- Locally available main: `8d5b34d19da46e305e27779d99eeb4437c2e882c`.
- Remote-tracking origin/main: `e66422f0ad67bbd73cf2f5b633c141ef5b2a09c1`; this is a local cached ref, not a fresh remote observation.
- Merge base: `30d82304e05899e2b1c83d9255fbe2f17abc0b7f`.
- New main changes add task supporting artifacts and fix cross-platform mutex cancellation. No automatic merge, fetch, rebase or overwrite was performed.

Workflow's updated task-directory contract permits root `runtime.json`, optional `SPEC.md`, and regular `docs/` and `scripts/` directories. New supporting references belong under `.tasks/GHI-24/docs/`, not a root `review/` directory. State remains exclusively managed through Workflow MCP. `task_get` confirms GHI-24 exists; its historical evidence and the active GHR-36 successor are preserved.

The updated Workflow README lists thirteen non-distribution suites, including the two new supporting-artifact/scratch suites and packaged-runtime coordination. Distribution tests are a separate release-artifact gate. Final CI selection must account for the two added non-packaging suites and the explicit packaged-runtime gate. Platform-specific skips must be distinguished from missing execution.

Main also changes the old checker files which remediation renamed to `blocking/Checker.fs` and `blocking/Tests.fs`. Integration must preserve the new exact bridge approvals in those canonical renamed files; do not restore compatibility filenames or lose strict checks.

Before final verification, reconcile updated main in a controlled checkpoint after active editors finish. Any merge commit requires its own explicit commit-message approval. Existing `docs/GHI-24/` records are retained pending a bounded evidence relocation plan; do not copy them into a second competing canonical contract or delete useful history.

Current verified evidence is limited to the prior 33-case pre-modern release wrapper, the 14/14 production policy-parser run, and recorded production builds. Modern handoff, publisher trust boundary, updated-main integration, full current test migration, clean-source packaging and independent final reviews remain outstanding.

## Evidence relocation

User authorized moving all GHI-24 documents on 2026-10-13. After the active engineer finished, 36 documents moved from `docs/GHI-24/` to this directory with unchanged SHA-256 hashes. The engineer's supporting note moved from the unsupported task-root `review/Handoff.md` to `docs/review/Handoff.md`, also byte-identical. External operational links in `docs/release/Requirements.md` now use the new paths. Historical commands, quotations and evidence paths within moved records remain verbatim; resolve old `docs/GHI-24/` citations through this relocation mapping. There are no compatibility copies at the old location.
