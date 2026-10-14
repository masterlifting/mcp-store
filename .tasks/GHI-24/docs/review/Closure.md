# Final local verification and acceptance

## Source and verdict scope

The independently audited clean source was `1738b927f37ea4541f3103b718b59ebe084bda2c`. User-approved release versions are Dotnet 1.0.4 and Workflow 1.0.5. Published Workflow 1.0.4 remains unchanged; its DLL contains commit-stamped informational-version bytes, so the reviewed suppression change required the explicit bump rather than broadening revision-only equivalence.

Architecture and security reviews approved their current-source boundaries. Correctness review approved orphan-metadata rejection and production digest/readback behavior. The AC-01–AC-12 audit approved local pre-merge acceptance. Its subsequent reporting addendum contained stale disabled-publication claims and is not authoritative for current wiring or final-SHA test attribution.

Targeted reviewer `ses_ec5fb8852ffeYL94UWziRvABdl` confirmed current pre-FSI roster authorization (`Orchestration.fs`), child credential scrubbing (`Capture.fs`), and protected builder/bootstrap/publication wiring. It identified a dangling comment fragment, subsequently removed with unchanged effective F# tokens. Release build `vOOBEJyoqgfbYW3g10jJoRKK-r501BradGa8gwDKG7A` passed with zero errors/warnings. This comment edit changes source-closure identity; prior packages are not attributed to a future commit.

## Actual verification

- Complete compiled release gate: 161/161, zero ignored/failed/errored; inventory parity and provenance passed. See `Execution.md`.
- Producer Expecto: recorded 155/155 baseline, not a newly executed final-SHA run. Do not conflate it with the 161-case release gate.
- Workflow non-packaging entrypoints: 12/12 native exit zero. Windows skipped the documented Unix-only subcases; TaskScratch consumer smoke executed. See `Behavior.md`.
- Bootstrap: 55/55 controlled scenarios plus 26 source assertions. Coherence: 88 assertions, not 88 compiled tests. See `Smoke.md` and `Pipeline.md`.
- Historical fixtures: repository-owned `infrastructure/release/tests/Fixtures.json`, verified Dotnet 1.0.3 and Workflow 1.0.4 archives; focused historical gate passed 21 nested assertions and the updated full gate passed. See `Published.md`.
- Exact clean-SHA producer packaging at `1738b927…`: both ZIP/manifest/pins triples matched byte-for-byte across two runs. Independently staged self-contained publisher contained 213 verified runtime files. Evidence retained under `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-final/` (`verification.txt`, `determinism.txt`, publisher logs).
- Package-dependent tests: Dotnet Distribution, Workflow Distribution and packaged coordination all native exit zero; 105 and 104 distribution assertions respectively, coordination 30 direct plus a documented conditional check. See the retained `ghi24-final/tests.md`; no aggregate double-count is asserted.
- Final smoke: real canonical preparation for both producers; controlled bootstrap installation authenticated the independent publisher receipt, and candidate acquisition retained exact-three raw transport bytes. Installed self-contained CLI passed receipt/policy/snapshot validation and stopped at the upload-opt-in guard. Three copied-input negatives rejected bootstrap digest, trusted receipt revision and component mismatch before authorization. Evidence retained under `C:/Users/andre/AppData/Local/Temp/opencode/ghi24-smoke-20261014121618/Smoke.md` and logs.

The publisher runtime envelope hash is `3f9578358cfcb122d62ef06e66a80f3e86538fa792af1d76c3d461ee792dd3bb`; the synthetic outer artifact hash is `382ab466b958320b1fe55f506d20cda860313b563294802b3d90372541372868`. The envelope contains runtime files; the outer contains envelope, manifest and receipt. These namespaces are not interchangeable.

## Finding dispositions

The original eight findings are addressed by metadata preflight zero-POST rejection, complete published identity checks, shared API digest verification, compiled shared orchestration, exact-two producer enrollment, scoped toolchain projection, renamed checker files and comment cleanup. Their identifiers and criterion mapping are preserved in `Threads.md`, `Requirements.md`, and the independent audit responses.

Later findings are addressed as follows: `r4249874725` projected step-output isolation; `r4249894920` twelve explicit Workflow gates/native failure propagation; `r4249874735` roster authorization before producer script execution; `r4249874746` read-only preparation plus child credential removal; `r4249904018` final orchestration comment correction. SEC1 is addressed through fresh isolated jobs, independently built and hash-verified publisher runtime/receipt, exact artifact bindings and data-only publication. Current source includes explicit trusted-receipt and transport-evidence flags; historical disabled-job notes are not current blockers.

## Limits and tracking

No hosted workflow execution, live signed-redirect/header verification, release upload, push, PR merge or thread resolution is claimed. Authorized upload/classification/readback is exercised separately through the genuine production controlled HTTP seam, not through the installed CLI against live services. Local synthetic bootstrap metadata is not platform attestation.

GHR-36 was recreated through Workflow MCP with explicit user approval as a documented recovery, not an exact restoration. Its draft/pending runtime does not establish lifecycle completion. Remaining administrative work must use native Workflow tools; no manual runtime edits are authorized.

Final comment-source commit approval and fresh source-identity packaging/provenance remain pending at the time of this report. The prior valid artifacts and successful tests remain historical evidence and must not be silently relabeled.
