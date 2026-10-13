# SEC1 bootstrap digest decoder — typed missing-platform-field

Factual companion to `Boundary.md` (frozen decisions) and `Bootstrap-fix.md` (F5 batch). It
records one bounded correction to the outer platform digest decoder in
`infrastructure/release/Bootstrap.ps1`, the exact defect evidence it addresses, the source
check performed, and the verification still owed to the tester. It restates no policy and
claims no approval.

## Status and scope

Authored in this batch:

- `infrastructure/release/Bootstrap.ps1` — the digest decoder `ConvertFrom-PlatformDigest`
  (comment, parameter block, unchanged body) only.
- `.tasks/GHI-24/docs/review/Bootstrap-digest.md` — this record.

Deliberately **not** modified: `infrastructure/release/tests/Bootstrap.ps1` (owned by the
parallel overlap-fixture fix), every other release/F#/workflow/README file, and every other
review document.

`mayEdit=true`, `mayBuild=false`, `mayTest=false`, `mayDelegate=false`. No suite execution,
no live platform call, no credential/secret read, no network, no commit. Source validation
was the PowerShell parser only.

## Defect (observed, not assumed)

Evidence `C:/Users/andre/AppData/Local/Temp/opencode/mcp-store-bootstrap-tests-9b7bc55904794510aeed48973456e72e`
(`case-api-digest-missing/failure-evidence.json`): 50 scenarios, 48 pass, 2 fail. This record
covers only `api-digest-missing`; the second failure (`receipt-candidate-overlap`) is the
parallel test-fixture owner's scope.

- expected receipt failure: `missing-platform-field`
- observed receipt failure: `unhandled-failure` —
  `Cannot bind argument to parameter 'Value' because it is an empty string.`

## Root cause and fix

The decoder declared `[Parameter(Mandatory = $true)][string]$Value`. When the platform
artifact metadata carries no `digest`, the caller passes the interpolated empty string, and
PowerShell's parameter binder rejects it before the function body runs. The typed
`missing-platform-field` branch was therefore unreachable and the outer `catch` classified the
binding error as `unhandled-failure` (frozen `Boundary.md` requires typed, fail-closed digest
failure).

Correction: `-Value` no longer declares `Mandatory` and now accepts empty/null
(`[AllowNull()][AllowEmptyString()]`), so a missing digest reaches the existing explicit typed
check. The check, the two accepted forms, the `malformed-platform-field` typed error, and the
bare-lowercase-hex normalization (single comparison namespace; inner envelope/manifest hashes
stay separate) are unchanged. `-FieldName` stays mandatory.

## Source check performed

`[Parser]::ParseFile` on `infrastructure/release/Bootstrap.ps1`: **0 parse errors**. Static AST
read of the decoder: `Value` attributes are `AllowNull, AllowEmptyString, string` and carry no
`Parameter`/`Mandatory` attribute.

## Verification owed (pending tester)

Rerun `infrastructure/release/tests/Bootstrap.ps1`; expect `api-digest-missing` →
`missing-platform-field` and `api-digest-malformed` → `malformed-platform-field`, with the four
digest form/namespace scenarios and all passing cases unchanged.
