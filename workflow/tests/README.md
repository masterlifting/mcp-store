# Workflow tests

## Profile fixture

`profile/Run.fsx` covers profile resolution, overlay merge, and drift handling. Its retired-identity fixture registers the `opencode` profile as a temporary project profile, so the recorded fingerprint is a content digest of that registration rather than a fixed constant; the draft and the baselined task keep that fingerprint, the `G1` and `G2` guard ids, and their `profileGuardKeys` provenance (`G1` → `validation`, `G2` → `review`), and a task whose profile registration was removed fails closed as profile drift instead of migrating.

## Projection suites

`Projections.fsx` covers the compact `task_status` and `task_ready` projections directly against the domain sources: lenient status reads, strict readiness reads, dependency and ancestor blockers, blocker truncation, pagination bounds, applied-state tracking, the lenient/strict split on drifted state, read-only guarantees, and start-eligibility parity. It needs no packaged DLL.

`protocol/Run.fsx` covers the same two projections across the native stdio boundary, and `schema/Run.fsx` pins their published schema bounds.

## Integrity receipt suites

`integrity/Run.fsx` covers the integrity-receipt foundation in two layers: 12 pure HMAC cases over dummy bytes, with no filesystem and no per-account key access at all, and 59 receipt-bearing persistence cases, which run in a fresh isolated child process whose application-data and home directories are redirected to a unique directory under the approved temporary base. The canonical creation bootstraps the per-account integrity key and emits a framed receipt, and every later read and write is verified against the canonical frame. The parent asserts that the child resolved the isolated key directory inside its own run root and not the real per-account key directory (`<LocalAppData>\mcp-store\workflow`), and it removes the run root it created after the run, including after a rejected guard; a failed child case keeps the run root for diagnosis.

`integrity/Cases.fsx` is the child-process half of that suite. It refuses to start unless its resolved per-account application-data directory is exactly the isolated directory it was given, so it can never read, create, or rotate the real per-account key. It is a helper script for `integrity/Run.fsx`, not a standalone suite. Its User fixture mints the User decision through the canonical trusted apply, so the persisted claim carries both the User authority and the confirmation reference of a deterministic test double, and the later ordinary apply re-signs the same task, so the persisted task ends with the User decision and that ordinary decision; no case in it is evidence that an operator approved the operation.

## Authenticated boundary suite

`authentication/Run.fsx` covers the mandatory authenticated task boundary: canonical creation emits a task with schema version 3 and a framed receipt and the read, projection, validate, and ordinary-apply paths accept it; missing, null, empty, unknown, altered, absent-provenance, fabricated unsigned, and schema version 2 state is rejected without a rewrite; the receipt binds the requested task id and the canonical project root; repeated independently confirmed User waivers on one task succeed while the ordinary caller still cannot mint User authority; and the key lifecycle creates, never bootstraps on a missing key, never rotates a malformed key, and never overwrites an occupied creation. It runs three isolated child processes: "main" (the creation, tampering, destination-binding, repeated trusted-waiver, and snapshot cases), "restart" (the same isolated root as "main", started as a fresh process, proving the confirmed state survives an authenticated restart), and "key" (a second isolated root for the key-lifecycle cases). Every child runs with its per-account application-data and home directories redirected to a unique directory under the approved temporary base; the parent asserts that each child resolved the isolated key directory inside its own run root and not the real per-account key directory (`<LocalAppData>\mcp-store\workflow`), and it removes the run root it created after the run; a failed child case keeps its run root for diagnosis. Every confirmation in the child is a deterministic test double standing in for the native host form, so no case in it is evidence that a human approved any operation.

`authentication/Cases.fsx` is the child-process half of that suite. It refuses to start unless its resolved per-account application-data directory is exactly the isolated directory it was given, so it can never read, create, or rotate the real per-account key. It is a helper script for `authentication/Run.fsx`, not a standalone suite.

## Trusted ingress suites

`authority/Run.fsx` covers the trusted User-ingress boundary in a fresh isolated child process whose application-data and home directories are redirected to a unique directory under the approved temporary base, and it reports 104 current cases. The parent asserts that the child resolved the isolated key directory inside its own run root and not the real per-account key directory (`<LocalAppData>\mcp-store\workflow`), and it removes the run root it created after the run, including after a rejected guard; a failed child case keeps the run root for diagnosis.

`authority/Cases.fsx` is the child-process half of that suite. It refuses to start unless its resolved per-account application-data directory is exactly the isolated directory it was given, so it can never read, create, or rotate the real per-account key. It is a helper script for `authority/Run.fsx`, not a standalone suite. Every confirmation it exercises is a deterministic test double standing in for the native host form, so no case in it is evidence that a human approved any operation. Its history fixture mints the User decision through the canonical trusted apply against one of the profile's two User-authority targets, so a historical User decision never authorizes the other target.

`Receipt.fsx` is the shared independent framing oracle that `authority/Cases.fsx` and `integrity/Cases.fsx` both load. It is a pure helper: it reads no key material and touches no file, and it derives the expected frame bytes and the `hmac-sha256-v1:` tag from its own implementation of the length-prefixed field layout, so each suite checks the producer's framing against an independent implementation rather than against the producer's own framing. Its fixed golden frames pin the byte layout of the frame itself, so a change to the prefix or to the field order is visible even when the payload under test moves.

## Native protected-operation suite

`confirmation/Run.fsx` covers the producer's native protected-operation path over the real stdio boundary: JSON-RPC initialize with an `elicitation.form` capability, `tools/call`, the server-initiated `elicitation/create` request, and the correlated client answer, plus the rejection, pending, stale, duplicate, and EOF variants. It drives the compiled Release producer, so it needs an existing Release entry DLL. The suite runs in a fresh isolated child process whose application-data and home directories are redirected to a unique directory under the approved temporary base, and the isolation guard is proven before the producer starts, so the producer can never reach the real per-account key. Every answer to an elicitation is a deterministic JSON-RPC test double standing in for the native host form, so no case in it is evidence that a human approved any operation.

`confirmation/Isolation.fsx` is the isolation-guard helper for that suite. It refuses to report success unless the resolved per-account application-data directory is exactly the isolated directory it was given, so it can never read, create, or rotate the real per-account key. It is a helper script for `confirmation/Run.fsx`, not a standalone suite.

### Authenticated-state expectations in the native protected-operation suite

The schema-v3 authenticated boundary signs the persisted sidecar when the task is created, so a persisted task already carries an `integrityReceipt` before any protected operation runs. The tool response task DTO deliberately carries no receipt, so the receipt and the bytes it binds are read from the persisted state, never from the response payload. The rejection and stale variants therefore assert that the persisted signed bytes and their receipt are unchanged by an answer that cannot authorize, and that the receipt is a framed `hmac-sha256-v1:` tag over 64 lowercase hexadecimal characters. They do not assert the absence of a receipt.

### Second protected operation, unsupported clients, and the confirmation deadline

Three further scenarios in the same suite cover the native boundary cases that need their own producer processes, so each of them starts and stops its own producer against the same isolated application data and workspace.

The `second` scenario creates one harness task, confirms the waiver of its first user-waivable guard, restarts the producer, and then confirms a second, distinct user-waivable guard of the same task with its own elicitation. It asserts that the consumed answer of the first operation does not authorize the second one, that the persisted task carries two User decisions and two waived dispositions at the expected revision, that the second operation re-signs the state, and that a further restart still verifies the re-signed receipt.

The `unsupportedNoForm` and `unsupportedOldProtocol` scenarios initialize a client that cannot answer the elicitation form: the first negotiates the accepted protocol version without the `elicitation.form` capability, the second negotiates the separately accepted older protocol version while declaring that capability. Both assert that an ordinary apply still succeeds prompt-free, that the protected apply returns a bounded failure without emitting any elicitation, and that the persisted bytes, receipt, revision, decisions, and guard disposition are unchanged. Both also assert that the failure is distinguishable from a human decline: the producer returns an `INVALID_TRANSITION` "native User confirmation is unsupported: the connected client must negotiate protocol 2025-11-25 and form elicitation" failure before it reaches any confirmation callback, so its wording differs from the "trusted User confirmation was rejected or cancelled" wording of a declined form, and no `elicitation/create` request reaches the client.

The `timeout` scenario withholds the answer of a received form past the producer's own two-minute confirmation deadline and asserts that the call ends with a bounded failure, that the persisted bytes, receipt, revision, decisions, and guard disposition are unchanged, that the late answer of the timed-out form cannot mutate the state or complete the call a second time, and that the transport stays usable: the ping still completes and a new protected operation receives its own form and applies after its own answer. The deadline is the production one; the suite only bounds how long it waits for it, and it prints the measured elapsed time of the timed case.

## Regression suites and the isolation guard

The mandatory authenticated boundary signs every persisted task with the producer-owned per-account integrity key, and that key path is resolved from the per-account `LocalApplicationData` directory. Routine persistence reaches the key too, so a plain task creation is no longer a key-free operation.

`Isolation.fsx` is the shared isolation guard for every entrypoint that does not isolate itself. It is `#load`ed as the first directive of `authority/Metadata.fsx`, `locking/Run.fsx`, `evidence/Run.fsx`, `contract/Run.fsx`, `profile/Run.fsx`, `protocol/Run.fsx`, `domain/Run.fsx`, `Projections.fsx`, `schema/Run.fsx`, `path/Run.fsx`, `artifacts/Run.fsx`, `protocol/Findings.fsx`, `protocol/Negotiation.fsx`, and `coordination/Run.fsx`, ahead of the domain sources and the producer launch, so the resolved per-account application-data directory is verified before any key path is captured. The guard refuses to start a suite unless `Environment.GetFolderPath(LocalApplicationData)` is exactly the isolated directory named by `MCP_WORKFLOW_TEST_EXPECTED_APP_DATA`, and it additionally refuses a resolved directory equal to the real per-account directory named by `MCP_WORKFLOW_TEST_REAL_APP_DATA`. It compares paths only: no key material is read, created, printed, or rotated. A missing variable, an unresolved directory, or a mismatch aborts the suite before any producer call.

It is a helper script for those suites, not a standalone suite.

### Authenticated expectations in these suites

`contract/Run.fsx` covers the strict schema3 persistence boundary: the transport DTO carries the canonical schema version and deliberately omits the persistence receipt, the unsigned transport DTO is not a task load, the persisted sidecar carries a framed `hmac-sha256-v1:` receipt and parses back to the same model, the authenticated read returns the same model, only the canonical version is accepted, a legacy schema2 record is rejected with the typed unsupported-schema error and is never rewritten, legacy layouts are still rejected, and the runtime lock stays ephemeral.

`protocol/Run.fsx` covers the same projections across the native stdio boundary. The `task_create` reply is the transport model, not the authenticated record, so it carries the canonical schema version and no receipt. The profile-drift case is produced by changing the resolved profile registry with a project profile overlay instead of editing the signed sidecar bytes: `task_ready` reports the strict `PROFILE_DRIFT` failure, `task_status` stays lenient, and the signed bytes and their receipt are unchanged.

`authority/Metadata.fsx` and `locking/Run.fsx` keep their original scenarios unchanged; only the isolation precondition was added.

## Running the suites

The Workflow behavior suites are standalone F# scripts. They do not use `dotnet test`.

The suites that own their isolation internally spawn their own isolated child processes and are run directly: `integrity/Run.fsx`, `authority/Run.fsx`, `confirmation/Run.fsx`, and `authentication/Run.fsx`.

`authority/Metadata.fsx`, `locking/Run.fsx`, `evidence/Run.fsx`, `contract/Run.fsx`, `profile/Run.fsx`, `protocol/Run.fsx`, `domain/Run.fsx`, `Projections.fsx`, `schema/Run.fsx`, `path/Run.fsx`, `artifacts/Run.fsx`, `protocol/Findings.fsx`, `protocol/Negotiation.fsx`, and `coordination/Run.fsx` load `Isolation.fsx`, so each must run in a fresh child process whose per-account directories are redirected to its own isolated sandbox; the guard refuses to start them otherwise. The release workflow runs the thirteen selected suites through the same redirection, one sandbox root per suite. The root [Run.ps1](../../Run.ps1) helper starts a suite in a redirected child, drains both output streams before waiting, forwards bounded output, preserves the child exit code, and removes only its own run root. Invoke it from the repository root:

```powershell
pwsh -NoProfile -File ./Run.ps1 -Script workflow/tests/contract/Run.fsx
```

The `-Script` argument accepts any guarded entrypoint, so the path is the only difference between runs:

```powershell
$suites = @(
  "workflow/tests/authority/Metadata.fsx",
  "workflow/tests/locking/Run.fsx",
  "workflow/tests/evidence/Run.fsx",
  "workflow/tests/Projections.fsx",
  "workflow/tests/schema/Run.fsx",
  "workflow/tests/contract/Run.fsx",
  "workflow/tests/path/Run.fsx",
  "workflow/tests/profile/Run.fsx",
  "workflow/tests/artifacts/Run.fsx",
  "workflow/tests/protocol/Findings.fsx",
  "workflow/tests/protocol/Negotiation.fsx",
  "workflow/tests/protocol/Run.fsx",
  "workflow/tests/domain/Run.fsx",
  "workflow/tests/coordination/Run.fsx"
)
foreach ($suite in $suites) {
  pwsh -File Run.ps1 -Script $suite
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
```

The `protocol/Run.fsx`, `protocol/Negotiation.fsx`, `protocol/Findings.fsx`, and `confirmation/Run.fsx` process-boundary scripts require an existing Release entry DLL. `coordination/Run.fsx` otherwise publishes the producer, so set `MCP_WORKFLOW_PACKAGED_DLL` to an already-authorized Release DLL in the environment that starts the recipe when builds are out of scope. Unix FIFO/socket cases in `artifacts/Run.fsx` are explicitly skipped on Windows hosts.

`distribution/Run.fsx` invokes the release build and pin-generation scripts, which mutate distribution artifacts; run it only when release-artifact generation is authorized. `coordination/Abandon.fsx` is a child-process helper for `coordination/Run.fsx`, `integrity/Cases.fsx` is a child-process helper for `integrity/Run.fsx`, `authority/Cases.fsx` is a child-process helper for `authority/Run.fsx`, `authentication/Cases.fsx` is a child-process helper for `authentication/Run.fsx`, `confirmation/Isolation.fsx` is an isolation-guard helper for `confirmation/Run.fsx`, and `Isolation.fsx` is the shared isolation-guard helper for `authority/Metadata.fsx`, `locking/Run.fsx`, `evidence/Run.fsx`, `contract/Run.fsx`, `profile/Run.fsx`, `protocol/Run.fsx`, `domain/Run.fsx`, `Projections.fsx`, `schema/Run.fsx`, `path/Run.fsx`, `artifacts/Run.fsx`, `protocol/Findings.fsx`, `protocol/Negotiation.fsx`, and `coordination/Run.fsx`; `commands/Create.fsx`, `commands/Get.fsx`, `commands/Apply.fsx`, and `commands/Validate.fsx` are command entrypoints rather than standalone suites.
