# Bootstrap standalone test evidence

Status: the candidate-overlap fixture is corrected; the standalone suite was not rerun while the production owner is editing `Bootstrap.ps1`.

## Baseline and correction

- Before this test-only edit, the latest run reported **48/50 scenarios passed** and **26 source-contract checks**; it is not post-fix evidence.
- Retained failures: `receipt-candidate-overlap` expected `receipt-overlaps-candidate` but exited 0 because its receipt was under `candidate-boundary` while `-CandidateRoot` pointed at `unused-candidate-root`; `api-digest-missing` received `unhandled-failure` instead of `missing-platform-field`.
- The launcher now passes the actual case `candidate-boundary` as `-CandidateRoot` and places the fixture receipt beneath that same root; the guard assertion is unchanged.
- Original failure artifacts: `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-bootstrap-tests-9b7bc55904794510aeed48973456e72e`.

## Limits and next gate

- `TestTransport` stubs metadata and download and bypasses `HttpClient`; redirect/Authorization behavior is source-checked only, not verified on the wire or against live hosts. Live API host/version behavior remains unverified.
- Fixtures use a dummy token only; no real environment values, secret stores, network calls, builds, production edits, or commits were used in this slice.
- After production editing is complete, run `pwsh -NoProfile -File infrastructure/release/tests/Bootstrap.ps1`; report the new scenario and source-check counts separately from the 48/50 baseline.

## Fresh actual run

- Command: `pwsh -NoProfile -File infrastructure/release/tests/Bootstrap.ps1`; exit **1**, **49/50 scenarios passed**, **26/26 source-contract assertions passed**, including zero production-script parse errors. The initial 48/50 baseline above remains historical and unchanged.
- Scenario outcomes (all PASS except the marked failure): PASS `runtime-install-two-level-envelope`, `candidate-fetch-exact-entries`, `digest-prefix-prefix`, `digest-prefix-bare`, `digest-bare-prefix`, `digest-bare-bare`.
- PASS `wrong-repository-id`, `wrong-repository-fullname`, `wrong-run-event-sha`, `wrong-run-attempt`, `wrong-event-name`, `wrong-uploader-job-id`, `wrong-uploader-job-name`, `uploader-job-not-completed`, `uploader-job-not-successful`, `contradictory-optional-job-attempt`, `wrong-artifact-id`, `wrong-artifact-name`, `artifact-workflow-run-event-sha`, `artifact-fork-repository-id`, `fork-head-repository-id`, `fork-head-repository-name`, `role-map-artifact-name-mismatch`, `role-component-map-mismatch`.
- PASS `api-digest-mismatch`, `api-digest-missing`, `api-digest-malformed`, `platform-size-mismatch`, `stub-reported-digest-mismatch`, `stub-reported-size-mismatch`, `stub-download-file-missing`, `stub-redirect-host-rejected`, `attempt-scoped-job-listing-pagination`, `production-origin-refuses-test-transport`, `non-production-origin-requires-test-transport`, `manual-install-directory-is-not-overwritten`, `install-source-overlap`.
- FAIL `receipt-candidate-overlap`: expected exit 1 and actual receipt failure `receipt-overlaps-candidate` were observed, but assertion `rejected candidate-boundary path was created` failed; destination absence/preservation is **not** verified. The failure receipt resides at `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-bootstrap-tests-33a6069772aa46b796fb68e678cf9541\case-receipt-candidate-overlap\candidate-boundary\bootstrap-receipt.json`.
- PASS `archive-duplicate-entry`, `archive-case-collision`, `archive-traversal-entry`, `archive-absolute-entry`, `archive-unc-entry`, `archive-drive-entry`, `archive-alternate-stream-entry`, `archive-backslash-entry`, `archive-symlink-entry`, `archive-per-entry-expansion-limit`, `archive-total-expansion-limit`, `archive-entry-count-limit`.
- The missing-digest regression passed with typed receipt failure `missing-platform-field`. Failure evidence for the overlap case is retained at `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-bootstrap-tests-33a6069772aa46b796fb68e678cf9541\case-receipt-candidate-overlap\failure-evidence.json` (suite root: `C:\Users\andre\AppData\Local\Temp\opencode\mcp-store-bootstrap-tests-33a6069772aa46b796fb68e678cf9541`).
- Test transport stubs metadata/download with dummy child-environment credentials; redirect and Authorization behavior remains source-checked only, not exercised over `HttpClient` or live hosts. No builds, source/test edits, live API calls, uploads, or secret values were used; only this evidence document was appended.

## Fresh full run after production guard fix

- Command: `pwsh -NoProfile -File infrastructure/release/tests/Bootstrap.ps1`; exit **0**, **50/50 scenarios passed**, **26/26 source-contract assertions passed**, **0 failed scenarios**. This is fresh post-fix evidence; earlier 48/50 and 49/50 runs above remain unchanged historical evidence.
- `receipt-candidate-overlap` passed without weakening its assertions: the child exited 1 with typed diagnostic `receipt-overlaps-candidate`, wrote **no** receipt at the rejected destination, and left the candidate-boundary root absent. Safe-destination failure scenarios still require a typed failure receipt.
- Production guard inspection confirmed `$receiptDestinationValidated` starts false, is set true only after the receipt/install and receipt/candidate overlap guards pass, and the catch writes a receipt only when it is true.
- The suite's test transport is fixture-only and supplies a dummy child-environment token; no build, live API request, upload, source/test edit, or secret value was used. Successful suite cleanup removed its temporary scenario artifacts; no failure artifact was retained.
- Residual limitation: redirect/Authorization behavior remains source-checked only; the stubbed run does not exercise `HttpClient` or live-host behavior. No explicit standalone scenario covers `InstallRoot` overlapping `ReceiptPath`; the candidate-receipt overlap is the exercised destination-preservation regression.

## Re-anchor rerun

- Re-anchor was required because the previous comment-banner `# ---- runtime manifest` was removed from production source during comment cleanup; the assertion cannot be relaxed and the banner cannot be restored, so it now anchors on the actual code function `function Read-RuntimeManifest` instead, which preserves the transport/manifest boundary invariant by the same `IndexOf` ordinal search and still bounds `$downloadSource` from the download handler through the start of the manifest reader.
- Command: `pwsh -NoProfile -File infrastructure/release/tests/Bootstrap.ps1`; exit **0**, **50/50 scenarios passed**, **26 source-contract assertions passed**, **0 failed scenarios**, **0 parse errors**. The 48/50, 49/50, and 50/50 entries above remain unchanged historical evidence; the historical 50/50 line is not superseded in outcome and is retained as-is.
- No production source change, no `.fs`/`.fsproj`/`.json`/workflow edit, no network or live API call, no environment value, no commit or branch operation accompanied this rerun; the only file changed was `infrastructure/release/tests/Bootstrap.ps1` and only this `Smoke.md` append was written.
