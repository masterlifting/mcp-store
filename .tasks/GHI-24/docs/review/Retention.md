# Bootstrap raw transport retention

Status: source-level only. The batch adds one explicit `Bootstrap.ps1` CLI input and
one atomic retention step; no build, no test run, no live API call, and no receipt
schema change. This record claims no APPROVE and no PASS.

Typed binding fix (pending test): the protected-root call now filters the optional
`-CandidateRoot` before the call. The verification block below records a source-level
parse check only, so the pre-existing 50/50 package result is not re-verified here and
the fix remains pending the test owner's re-run.

## Scope

Authored: `infrastructure/release/Bootstrap.ps1` and this record only.

Not modified: `Runner.fs`, `Handoff.fs`, every `tests/**` file (including
`tests/Bootstrap.ps1`), `.github/workflows/release.yml`, `README.md`, `Sources.json`,
and the compiled `Release.fsproj` closure. `mayEdit=true`, `mayBuild=false`,
`mayTest=false`, `mayDelegate=false`.

No live API call, environment value read, credential/secret read, network mutation,
commit, push, merge, or task-state change was performed.

## Problem this closes

`FetchCandidate` streamed the raw outer ZIP to `InstallRoot/artifact-download.zip`,
verified digest/size/outer entries, extracted the candidate, and then left the raw
bytes only inside the run's isolated install root. The publish CLI's second
positional argument is the actual raw outer ZIP, so no genuine publisher invocation
could be wired from `FetchCandidate` outputs alone. `Transport.md` assigned that
retention to DevOps.

## New CLI input

| Input | Rule |
| --- | --- |
| `-RawTransportPath` | Required by `FetchCandidate`; rejected by `InstallRuntime`; no default, no alias, no fallback |

- `FetchCandidate` without a value fails with typed `missing-input`.
- `InstallRuntime` with a non-empty value fails with typed `malformed-input`; the
  install operation retains no raw transport, so a supplied destination is never
  silently ignored.
- The value is never written into the receipt (see schema below) and never echoed to
  stdout; the workflow already knows the value it passed.

## Destination safety before any write or platform call

`Assert-RawTransportDestination` runs after the receipt/install overlap guards and
before `CreateDirectory InstallRoot` and before the token read and metadata calls, so
an unsafe target has zero write and zero network effect:

1. canonicalized with `Get-CanonicalPath` (`GetFullPath`);
2. rejected when the leaf already exists — `raw-transport-exists` (file or directory),
   so an owned file or manual data is never replaced;
3. the parent must already exist as a directory — `raw-transport-parent-unsafe` /
   `raw-transport-parent-missing`; the bootstrap never creates an unaudited parent;
4. `Test-PathOverlap` (OrdinalIgnoreCase, the existing helper) against `-InstallRoot`,
   `-ReceiptPath`, `-CheckoutRoot`, and `-CandidateRoot` — `raw-transport-overlap`;
   the optional `-CandidateRoot` participates only when supplied, so a genuine
   FetchCandidate that omits it still reaches every typed check;
5. every ancestor directory of the parent is rejected when it carries
   `FileAttributes.ReparsePoint` — `raw-transport-reparse-ancestor` — so a junction or
   symlink cannot retarget the retained write out of the verified parent.

The guard reuses `Get-CanonicalPath`, `Test-PathOverlap`, `$script:Separator`, and the
`OrdinalIgnoreCase` comparison policy; no second path-comparison rule was introduced.

## Atomic retention

Retention runs only after artifact id/name/expired, `workflow_run` id/`head_sha`/
repository ids, the attempt-specific run, the attempt-scoped uploader job, the platform
digest, the API digest, the streamed byte count, and `Assert-ZipArchiveSafety` over
`-ExpectedOuterEntries` have all succeeded.

The same verified bytes are copied by `File.Copy` into a same-directory temp
(`<destination>.tmp-<guid>`), the temp is re-hashed and size-checked against the
streamed observation (`raw-transport-copy-mismatch` /
`raw-transport-copy-size-mismatch`), and only then renamed to the destination with
`File.Move`. The destination is therefore either absent or exactly the verified bytes,
and a pre-existing file can never be replaced. The retained bytes are the streamed
outer ZIP; the inner envelope, its digest, and any extracted file are never
substituted. Because `InstallRoot` is fresh and overlap-guarded, and the download temp
is written by the bootstrap's own bounded transport, there is no writable producer
identity that could mutate the source between verification and copy.

## Failure cleanup

- `$createdRawTransport` is set only after the atomic rename, and
  `$rawTransportTempPath` is cleared only then. The catch removes only the temp this
  run wrote and the retained file this run created.
- A rejected pre-existing destination is never deleted: the run fails before
  `$createdRawTransport` is set, so the cleanup is a no-op.
- `InstallRuntime` keeps its existing behavior; the failure path still removes the
  fresh install root, and the FetchCandidate install root still persists on success.
- Redirect and credential policy, `X-GitHub-Api-Version`, and
  `-AllowedRedirectHosts` are untouched.

## Receipt schema unchanged

`$script:ReceiptSchemaVersion` stays `2` and no receipt field was added.
`Runner.fs` parses the FetchCandidate receipt with an explicit field allowlist, so a
new top-level or `download` key would be an unknown-field rejection; the existing
`download.observedSha256` and `download.bytesRead` fields are the retained-bytes proof
and already cross-check `--transport-sha256` and the observed raw file size. Path
exposure is the caller's explicit CLI value, not receipt data.

## Required test companion (tester channel, not applied here)

`infrastructure/release/tests/Bootstrap.ps1` must be migrated before its 50-case
inventory is valid again, because every `FetchCandidate` scenario now needs an explicit
`-RawTransportPath`:

- `New-Scenario`: add a `RawTransportPath` field; `New-ScenarioSource`: append
  `-RawTransportPath <literal>` only for `FetchCandidate`.
- `Invoke-Scenario`: create the destination parent under `case-<name>`, and on success
  assert the retained file's SHA-256 equals the platform outer `$transportSha256` and
  is distinct from `$package.InnerSha256`, and that its length equals
  `$transportBytes.LongLength`.
- Failure cleanup: assert the retained file is absent after a post-retention failure,
  and unchanged after a pre-existing-destination rejection.
- New negative cases: `missing-input`, `raw-transport-exists`,
  `raw-transport-parent-missing`, `raw-transport-overlap` (destination inside install /
  checkout / candidate / receipt), `raw-transport-reparse-ancestor` (junction parent).
- `Test-StaticContracts` needs no change: the new helper sits before
  `Get-MetadataDocument`, so the metadata/download/manifest delimiters are unaffected.

## Required workflow companion (not applied here)

Publication stays `if: false`. The future `publish` wiring must run
`Bootstrap.ps1 -Mode FetchCandidate ... -RawTransportPath <fresh leaf outside the
checkout, install root, candidate root, receipt, and the separately installed publisher
runtime>` and then pass that exact same path as the second positional argument to
`ReleaseRunner.exe publish`. The `InstallRuntime` invocation must not pass
`-RawTransportPath`. The residual limitation is that this invocation can only guard the
paths it is given; isolation from a separately installed publisher runtime is the
caller's responsibility. No live signed-redirect host or API-version assumption was
changed or settled by this batch.

## Verification performed

```text
[System.Management.Automation.Language.Parser]::ParseFile(Bootstrap.ps1, ...)  # 0 parse errors
byte scan of Bootstrap.ps1                                                    # 1228 CRLF, 0 lone LF
```

Not verified: no build, no suite execution, no `HttpClient`/live host behavior, and no
genuine retained file was produced, so this is not publication-readiness evidence.

Typed fix follow-up: the binding defect — a Mandatory `[string[]]` receiving the
empty-string `-CandidateRoot` element of `@($InstallRoot, $ReceiptPath, $CheckoutRoot,
$CandidateRoot)` — was reproduced in isolation and closed by filtering null/whitespace
protected roots at the call site only; the required roots and every `Test-PathOverlap`
comparison, reparse-ancestor, parent-existence, and exists check are unchanged. Pending:
the test owner re-runs `infrastructure/release/tests/Bootstrap.ps1`, whose 50 cases
previously passed only because the wrapper always bound a non-empty `-CandidateRoot`
workaround; one genuine FetchCandidate scenario that omits `-CandidateRoot` and one that
asserts a pre-existing raw leaf is preserved unchanged are still owed.
