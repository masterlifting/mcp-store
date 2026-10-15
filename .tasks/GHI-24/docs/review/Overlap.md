# Bootstrap failure-path receipt-overlap correction

This document is the factual companion to `Bootstrap-fix.md` (corrected transport
batch), `Bootstrap.md` (original bootstrap batch) and `Bootstrap-tests.md` (standalone
test evidence). It records the production defect in the `Bootstrap.ps1` failure path,
the bounded correction applied to `infrastructure/release/Bootstrap.ps1`, the exact
resulting logic, the checks performed, and the tester-owned expectation change that
remains. It restates no policy and claims no approval.

## Status and scope

Authored in this batch:

- `infrastructure/release/Bootstrap.ps1` — failure-path receipt guard correction.
- `.tasks/GHI-24/docs/review/Bootstrap-overlap.md` — this record.

Deliberately **not** modified: every F# source, `Release.fsproj`, `Sources.json`,
`Publisher.json`, `README.md`, `.github/workflows/release.yml`, the standalone test
harness `infrastructure/release/tests/Bootstrap.ps1`, and every other review document.

`mayEdit=true` bounded to the two files above; `mayBuild=false`, `mayTest=false`,
`mayDelegate=false`. No build, no test execution, no live platform call, no credential
or secret read, no network access, no state change, no commit. Validation was source
parsing and static inspection only (see **Checks performed**).

## Defect (established)

The pre-write path guards reject a receipt destination that overlaps the candidate
payload directory (before the fix, lines 914–916):

```powershell
if (-not [string]::IsNullOrWhiteSpace($CandidateRoot) -and (Test-PathOverlap $ReceiptPath $CandidateRoot)) {
    Fail "receipt-overlaps-candidate" "the bootstrap receipt must not be written into the candidate payload directory"
}
```

`Fail` throws a typed `code|detail` exception, which the `try` block's `catch`
(lines 1164–1175 before the fix) handled by unconditionally calling
`Write-BootstrapReceipt -Receipt $receipt -Path $ReceiptPath`.

`Write-BootstrapReceipt` (lines 833–845 before the fix) creates the receipt's parent
directory before writing:

```powershell
$parent = [System.IO.Path]::GetDirectoryName($full)
if (-not [string]::IsNullOrEmpty($parent)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
```

Net effect: the run that **rejected** the receipt destination as overlapping still
created that rejected parent directory and wrote a failure receipt into it — the one
write the guard existed to prevent. This is not specific to
`receipt-overlaps-candidate`; any typed failure raised before the receipt destination
was known to be safe could reach the same unconditional write.

**Frozen invariant:** NO writes to an invalid or overlapping receipt target. The
rejected destination must remain absent, for **every** invalid receipt path/overlap
case and not only the one typed exception code above.

## Correction (exact logic)

A single script-scoped boolean gates the failure-path write. It is `$false` until
every receipt-destination guard has passed, is set `$true` immediately after the last
of those guards, and is the only condition under which the `catch` writes a receipt.

1. Declared false before the `try` (line 878–882), next to the existing
   `$createdInstallRoot` flag:

   ```powershell
   $createdInstallRoot = $false
   # Stays false until every receipt-destination guard below has passed. The failure path may write
   # the failed receipt only when this is true, so a rejected or overlapping receipt target is never
   # created by the very run that rejected it.
   $receiptDestinationValidated = $false
   ```

2. The two receipt-destination guards run first inside the `try`, before any platform
   call, credential read or install action, and the flag is raised only after both
   have passed (lines 888–899):

   ```powershell
   if (Test-PathOverlap $InstallRoot $ReceiptPath) { Fail "install-overlaps-receipt" "the install root must not overlap the receipt path" }
   if (-not [string]::IsNullOrWhiteSpace($CandidateRoot) -and (Test-PathOverlap $ReceiptPath $CandidateRoot)) {
       Fail "receipt-overlaps-candidate" "the bootstrap receipt must not be written into the candidate payload directory"
   }
   $receiptDestinationValidated = $true
   ```

   Both typed codes are retained unchanged (`install-overlaps-receipt`,
   `receipt-overlaps-candidate`). The install-root guards (`install-overlaps-checkout`
   line 926, `install-overlaps-candidate` line 927, `install-root-exists` line 928)
   stay where they were, after the flag is raised.

3. The `catch` writes the failed receipt only when the flag is true, otherwise it
   emits a typed diagnostic and writes nothing (lines 1184–1190):

   ```powershell
   Write-Error -Message ("bootstrap failed [{0}] {1}" -f $code, $detail) -ErrorAction Continue
   if ($receiptDestinationValidated) {
       Write-BootstrapReceipt -Receipt $receipt -Path $ReceiptPath
   } else {
       Write-Error -Message ("bootstrap failure receipt suppressed [{0}]: the receipt destination '{1}' was not validated as a safe, non-overlapping path, so no failure receipt was written anywhere" -f $code, $ReceiptPath) -ErrorAction Continue
   }
   exit 1
   ```

   There is no fallback write to any other path, no partially written receipt, and no
   directory creation for the rejected destination. Exit code stays `1`.

### Why the two guards move to the front

The failure receipt is only suppressed when the receipt destination itself has not
been validated. To keep that true for all *other* typed failures — which are expected
to still emit their failed receipt — the receipt-destination guards must run before
the failures that may precede them (API-origin resolution, role-map validation,
platform-digest decoding, and the install-root guards). The three `malformed-input`
format checks (EventSha/RepositoryId/UploaderJobId, lines 884–886) stay ahead of the
guards; a malformed input now suppresses the receipt, because the destination was not
yet validated. No existing scenario exercises that path, and it is the conservative
direction for the frozen invariant.

## Failure-path behaviour

| Failure point | Receipt written? | Notes |
| --- | --- | --- |
| `install-overlaps-receipt` (line 893) | **No** | destination rejected before `$receiptDestinationValidated = $true` |
| `receipt-overlaps-candidate` (line 897) | **No** | the reported defect; rejected parent directory is never created |
| `malformed-input` / `missing-input` before line 899 | **No** | destination not yet validated (conservative) |
| API origin, role map, digest, install-root and later failures | **Yes** | flag already `true`; unchanged behaviour and receipt codes |

## Preserved behaviour

- Fresh install-root cleanup in the `catch` is unchanged
  (`$createdInstallRoot` + `Remove-Item` at lines 1181–1183). The overlap case never
  creates the root, so there is nothing to remove.
- A pre-existing manual install directory is untouched (`install-root-exists` guard and
  `$createdInstallRoot` semantics unchanged).
- `receipt-overlaps-candidate` and `install-overlaps-receipt` remain typed codes, each
  emitted exactly once.
- Success path is unchanged: it is reached only after the guards pass, so it still
  writes the receipt at line 1193.
- No compatibility alias, bypass or fallback path was added.

## Checks performed

- `[System.Management.Automation.Language.Parser]::ParseFile` over
  `infrastructure/release/Bootstrap.ps1`: **0 parse errors**, 8470 tokens.
  Syntax/tokenization only — the script was not executed.
- Static occurrence counts: `$receiptDestinationValidated = $false` ×1 (line 882),
  `= $true` ×1 (line 899), guard read ×1 (line 1185); `Write-BootstrapReceipt` ×2
  (success line 1193 and guarded `catch` line 1186); `receipt-overlaps-candidate` ×1,
  `install-overlaps-receipt` ×1.
- Guard order confirmed by line inspection: receipt-destination guards at 893/897 run
  before the flag is raised at 899; install guards remain at 926–928.
- No build, no test run, no `dotnet`/`pwsh`-suite execution, no live API request, no
  secret read, no commit.

## Tester handoff

The `receipt-candidate-overlap` scenario in `infrastructure/release/tests/Bootstrap.ps1`
currently asserts that a failed execution wrote its bootstrap receipt (harness line
~426). For the overlap case the receipt must now be **absent**. The tester-owned change
is: for the overlap scenario, expect the receipt file to be absent, while keeping the
`ExpectCandidateRootAbsent = true` candidate-boundary absence assertion and the
`receipt-overlaps-candidate` exit-code/failure-code assertion intact. The
candidate-boundary absence check must not be weakened.

The overlap scenario is expected to still exit `1` and to report the typed
`receipt-overlaps-candidate` failure on the error stream; the failure receipt simply
does not exist, and the rejected parent directory is not created. This change was not
applied here because tests are tester-owned and `mayTest=false`.

## Limits (not papered over)

- Redirect/Authorization transport behaviour is out of scope for this batch.
- The `malformed-input`/`missing-input` suppression above is a deliberate
  conservative consequence of validating the receipt destination after those checks;
  it is untested by the current suite and is recorded rather than hidden.
- No standalone suite was executed in this batch; the documented 49/50 result in
  `Bootstrap-tests.md` remains the last executed baseline and is expected to become
  50/50 only after the tester applies the handoff above.

No engineering APPROVE is asserted by this document.
