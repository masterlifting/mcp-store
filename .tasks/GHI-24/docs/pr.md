## Summary

Implements #24 for the Dotnet and Workflow producers only.

- Evaluate both distributions on every push to `main`, building the exact event
  SHA with canonical producer configuration and verified SDK/runtime identity.
- Serialize evaluations with `queue: max`; retain fixed `dotnet`/`workflow` tags
  and versioned asset URLs, without overwrite, deletion/reupload or tag movement.
- Validate packages and pins before publication; strictly compare raw ZIP data
  and complete manifest content, allowing only revision-derived equivalence.
- Observe and verify remote bytes after successful or ambiguous uploads; publish
  immutable, harness-neutral metadata with published hashes and revision.
- Gate publication on real producer tests, release regressions and strict,
  non-executing entrypoint type checks. Telegram and consumer integration are untouched.

Both producers explicitly move from `1.0.3` to `1.0.4`: actual historical DLL
contents differ beyond the revision-only exception. SDK-generated Git SHA
injection into DLL informational versions is disabled; full revision provenance
remains in the distribution manifest and release metadata.

## Verification

**Locally executed:**
- Release builds of both producers and the release helper: zero errors/warnings.
- Actual compiled Expecto runner: **150/150**, zero ignored/failed/errored.
- Deterministic release regressions: **272 passed**, zero failed/skipped.
- Optional historical integration using downloaded assets: **10 passed**.
- Strict FCS checks of both wrappers, including rejecting malformed input.
- Canonical package/pin validation and repeated byte-identical clean-SHA builds.
- Real raw packages from source commit `d556c20d576d6eba2e66ec4456641267af8c8595`
  and docs-only commit `f13594db7787e2a0300e1151c8e8697cbf98e91d` compare as
  `RevisionOnlyEquivalent` for both producers. No reconstruction was used.

**Simulated API behavior:** actual production transport functions use scripted
HTTP handlers for immutable publication, digest/corruption checks, ambiguous
upload recovery, redirected downloads, credential isolation and failure paths.

**Actual remote observation:** historical `v1.0.3` assets were downloaded and
their GitHub digests verified; current fixed containers were read-only observed.
**No real release assets were uploaded, overwritten, deleted or republished.**

The requirements-to-evidence matrix is `docs/release/Requirements.md`.
Factual commands and hashes are in `docs/GHI-24/proof.md` and `tests.md`.

## Review and limitations

Bounded independent Discovery/remediation/targeted Verification and a separate,
fresh original-task Acceptance Audit have been performed. Internal approval is
not proof of correctness; this PR is ready for the requested additional external
review and must not be merged as part of this task.

Hosted API/auth/CDN behavior, uploads, queue capacity/order and the hosted runner
image remain unverified. Static `queue: max` checks are not presented as live
queue execution. Task evidence temporarily remains under `docs/GHI-24/` because
the installed Workflow MCP permits only `runtime.json` in the task directory.

Closes #24.
