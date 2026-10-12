# Stage 3 — enrollment roster + toolchain boundary (GHR-36 W4)

Authoritative inputs: `Solution.md` (selected boundaries),
`Requirements.md` (AC-06, AC-07), `Threads.md` (4243003708, 4243003716).
Baseline head: `05a69ced8319b61a6bf0e24cad6d320a0a879283`. This records the
Stage 3 engineering and DevOps roster wiring. The tester-owned compiled
regressions are present; compilation and execution evidence remain pending.

## Scope and non-scope

- **In scope** — `Participants.json` roster; `ReleaseEnrollment` module
  (reusable, one-word); wiring into `ReleaseOrchestration.orchestrate`
  and `ReleaseRunner.runTransactionMode`; roster-driven loading in
  `Bootstrap.fsx`; `Release.fsproj` compile order; production build
  verification.
- **Out of scope** — AC-01/02/03 integrity fixes (Stage 4);
  `SyncOverAsyncChecker` rename (Stage 2 already shipped).

## Enrollment roster (AC-06)

`infrastructure/release/Participants.json` is the single authoritative
enrollment roster. It carries only the two fields the cross-stage
guard needs:

```json
{
  "participants": [
    { "componentId": "dotnet",   "configPath": "dotnet/release/Config.fsx" },
    { "componentId": "workflow", "configPath": "workflow/release/Config.fsx" }
  ]
}
```

Forbidden fields (rejected at load time so the roster never becomes a
policy mirror): `version`, `sdkVersion`, `runtimeVersion`, `platform`,
`archiveName`, `assetUri`, `entryAllowlist`, `publishedFiles`,
`entryDll`. Each of those lives in the per-producer `Config.fsx` and
is the single source of truth for that producer.

## Cross-stage validation API

`ReleaseEnrollment` (compiled in `Enrollment.fs`, before
`Transaction.fs` / `Orchestration.fs` so the orchestrator and runner
can both reference it) exposes:

- `rosterPath (root: string) : string` — `infrastructure/release/Participants.json`
  under the supplied repository root.
- `loadRoster (root: string) : Async<Result<Roster, ReleaseError>>` —
  reads the file, parses the JSON, and rejects:
  - non-existent file → `MissingArtifact`
  - non-JSON content → `MalformedArtifact`
  - missing/null `participants` array → `MalformedArtifact`
  - entry has any forbidden field → `MalformedArtifact` (the field
    name is in the message; the roster is rejected as a policy mirror)
  - entry missing `componentId` or `configPath`, or either is empty →
    `MalformedArtifact`
  - duplicate `componentId` across entries → `MalformedArtifact`
- `validateRoster (roster: Roster) : Result<unit, ReleaseError>` —
  non-empty + duplicate-`componentId` defense-in-depth check for
  callers that build a `Roster` directly.
- `validateProjectionAgainstRoster (componentId: string) (projectedConfigPath: string) (root: string) (roster: Roster) : Result<unit, ReleaseError>` —
  normalises both paths to absolute and rejects:
  - `unsupported componentId` — projection not present in the roster
  - `configID mismatch` — same `componentId` with a different path
  - `path drift` — the absolute path of the projected `configPath`
    does not equal the absolute path of the roster's `configPath`
- `validateComponentIdInRoster (componentId: string) (roster: Roster) : Result<unit, ReleaseError>` —
  transaction-mode check; the contract DTO carries no `configPath`,
  so only the componentId is checked.

## Wiring

### `ReleaseOrchestration.orchestrate`

Loads the roster first; rejects with `phase=config` if missing or
structurally invalid. After the producer projection is loaded from
the canonical `Config.fsx`, the projection is validated against the
roster (`componentId` + `configPath`); the orchestrator refuses to
proceed on unsupported componentId, configID mismatch, or path drift.
The structured-failure JSON includes the projected `componentId` so
the workflow summary can report which producer the guard rejected.

### `ReleaseRunner.runTransactionMode`

Loads the roster immediately after the contract DTO is parsed;
rejects with the pre-contract failure path (exit `4` for missing /
malformed roster, `1` for unsupported componentId). The transaction
mode carries no `configPath`; only `validateComponentIdInRoster` is
used. The CLI's `orchestrate` mode is covered by the orchestrator's
guard, so the same cross-stage invariant is enforced regardless of
entry mode.

### `Bootstrap.fsx`

The hardcoded `producerConfigPaths` table is removed. The bootstrap
script now loads the roster and iterates `roster.Entries`. Each entry's
`Config.fsx` is loaded in an isolated child `dotnet fsi`; the loaded
`ProducerSummary` is then re-validated against the roster so a
mid-flight `Config.fsx` swap cannot route to a different component.
The cross-producer toolchain equality check (one runtime across all
producers) stays in this script and is explicitly scoped to the
bootstrap toolchain guard.

## Toolchain boundary (AC-07)

| Module | .NET requirement | Note |
| --- | --- | --- |
| `ReleaseArchive` | runtime only | pure F# raw ZIP reader; no SDK, no transport. |
| `ReleaseCompare` | runtime only | pure F# byte comparison; no SDK, no transport. |
| `ReleaseMetadata` | runtime only | pure F# JSON render/parse; no SDK, no transport. |
| `ReleasePublish` | runtime only | uses `HttpClient`; no SDK. |
| `ReleaseEvaluate` | runtime only | pure decision logic; no SDK. |
| `ReleaseTransaction` | runtime only | orchestrates the above; no SDK. |
| `ReleaseCapture` | runtime only | `Process` API; no SDK. |
| `ReleaseEnrollment` | runtime only | JSON parsing; no SDK. |
| `ReleaseOrchestration` | **runtime + SDK** (for `dotnet fsi`) | spawns an isolated child `dotnet fsi` per `Config.fsx` load. This is a per-producer read of the canonical policy, not a global toolchain identity. |
| `Bootstrap.fsx` | **runtime + SDK** | verifies the executing SDK/runtime/platform against each producer's canonical `Config.fsx`. |

The current bootstrap script enforces "one runtime across all
registered producers" because the two existing producers share
exactly one .NET SDK and one .NET runtime. This restriction is
**scoped to the bootstrap toolchain guard**; the orchestrator and
runner do not enforce it. Each producer's expected identity stays in
its own `Config.fsx`; the orchestrator, runner, and capture layer
read those values but never require a global toolchain identity.

Per-producer toolchain setup and separate runner selection are
deferred to #37 (multi-language onboarding). No plugins, language
adapters, or additional orchestration is added in this stage.

## Files changed in this stage

- `infrastructure/release/Participants.json` — new; two-entry roster.
- `infrastructure/release/Enrollment.fs` — new; reusable module.
- `infrastructure/release/Release.fsproj` — `Enrollment.fs` added
  between `Capture.fs` and `Transaction.fs`.
- `infrastructure/release/Orchestration.fs` — `orchestrate` loads +
  validates the roster; rejects unsupported / drifted / mismatched
  projections before any child script runs.
- `infrastructure/release/Runner.fs` — `runTransactionMode` loads +
  validates the roster; rejects unsupported componentId before any
  contract-driven work.
- `infrastructure/release/Bootstrap.fsx` — hardcoded
  `producerConfigPaths` removed; the script now iterates the roster
  and re-validates each loaded summary against the roster.

No other production file was modified by the engineer slice. The tester-owned
test-project changes are listed below. The tester changed no `Update.ps1`,
workflow, PowerShell, or YAML source.

## Build runIds

Dedicated Dotnet MCP, `Release`, this resumption:

| Project | runId | status | exitCode | errors | warnings |
| --- | --- | --- | --- | --- | --- |
| `infrastructure/release/Release.fsproj` | `x5UdXYadH41WwlVCa8HNd7eaymVpzyvOKLI5BrQlMW8` | succeeded | 0 | 0 | 0 |
| `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` | `8PZInfaR2JztvqVsYtmcXtFxw3N8ivRp_BJkKLw_cA8` | succeeded | 0 | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `zzVS-SfZ_TZ46JBl2e_M_bKoTEA8ofhXHTSV9qU4QCo` | **failed** | 1 | 30 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `QJg9eKcevYFgXzl1sP1jkubRCtKZ1QJdjVTfg7qZT3c` | **failed** | 1 | 12 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `i3LMJg2JG3pVePtek78lItXBxMcvgckHX6BPJroT9HY` | succeeded | 0 | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `5c_W39Td28sDUKk8mWT5i0zUA3t5ZLCo3IlRvgE5S_U` | succeeded | 0 | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `fyatmCla11xm-2LHzl0NFfAX3SX9m3i1YKCaav01CwQ` | succeeded | 0 | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `MGgUmEFTDD7vGxYPOzPWrWSVfIacV2RdWzssfLCAvmc` | succeeded | 0 | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `9mVDQdm36zeFQlbTkKkhpHjAxYoV3fLOUnRvqJWqdY4` | succeeded | 0 | 0 | 0 |

All projects compile with `TreatWarningsAsErrors=true`. The
`Tests.fsproj` failure for `QJg9eKcevYFgXzl1sP1jkubRCtKZ1QJdjVTfg7qZT3c`
is entirely inside `infrastructure/release/tests/Coherence.fs`
(tester-owned file; six unique FS0041 overload-resolution locations
on lines 31, 32, 35, 35, 62, 65 — for `JsonValue.Create`,
`JsonNode.set_Item`, and `File.WriteAllText`); no production source
was edited by this resumption. Prior `Tests.fsproj` runId
`zzVS-SfZ_TZ46JBl2e_M_bKoTEA8ofhXHTSV9qU4QCo` (30 errors in
`Enrollment.fs`) is superseded. The retry after the tester's
string-annotation fix
(`i3LMJg2JG3pVePtek78lItXBxMcvgckHX6BPJroT9HY`) succeeded with 0/0,
tester assertions unchanged; no production source was edited and
no producer test rebuild was repeated. The follow-up build after the
tester's bounded contract-migration repairs across Enrollment,
Coherence, and Telegram (25 unchanged cases preserved; required
scenarios recorded in `Enrollment.md` and `Migration.md`) —
`5c_W39Td28sDUKk8mWT5i0zUA3t5ZLCo3IlRvgE5S_U` — also succeeded with
0/0; no production source was edited and no producer test rebuild
was repeated. The follow-up build after the tester's bounded
PowerShell continuation-format diagnostic and Bootstrap both-stream
assertion repairs (all test assertions preserved) —
`fyatmCla11xm-2LHzl0NFfAX3SX9m3i1YKCaav01CwQ` — also succeeded with
0/0; no production source was edited and no producer test rebuild
was repeated. The follow-up build after the background tester
Suite run (25 cases; 24 pass, 1 fail) identified exact-set fixture
masking config-identity drift, and the tester's fixture repair in
Enrollment/Bootstrap using the exact `{dotnet, workflow}` two-entry
roster plus a required `Dotnet` identity-mismatch diagnostic —
`MGgUmEFTDD7vGxYPOzPWrWSVfIacV2RdWzssfLCAvmc` — also succeeded with
0/0; current changes recorded in `Enrollment.md` and `Migration.md`,
no production source was edited, and no producer or other prior
build was repeated. The follow-up build after the tester actual-gate
run (25 cases; 24 pass, 1 fail) in which the Bootstrap test was
updated to expect the later observed actionable message (the
production early componentId-pin mismatch already returns the
correct diagnostic; no production edit) —
`9mVDQdm36zeFQlbTkKkhpHjAxYoV3fLOUnRvqJWqdY4` — also succeeded with
0/0; no production source was edited and no producer or other prior
build was repeated. Prior `Release.fsproj` runIds
`KLezwqX-7idclXZybGX-i93PwTd0Qx_aI4Qo64AVyY8`,
`wdXJvZl2ZoMm8t85CB5-xZCAP7lVP4qksPrbDUb5buc`,
`t1kT1M7vKN9aZX4VcSt0y_qQuL6485sNJgKJFLY8Q9w`,
`x5UdXYadH41WwlVCa8HNd7eaymVpzyvOKLI5BrQlMW8` are preserved.

## AC-06 mandatory exact-set constraint (added in this resumption)

`ReleaseEnrollment.supportedComponentIds` is now
`Set.ofList [ "dotnet"; "workflow" ]`. This is a fixed
validation constraint, not a roster mirror: the set is
hard-coded in the compiled module, carries no version, SDK, runtime,
platform, archive, URI, or allowlist data, and is not duplicated
into `Participants.json`. A second roster authority is not
introduced; the roster file still carries only the two-entry
identity-to-config-path projection.

Two guards now apply:

1. **Per-entry guard** in `parseRoster` — rejects any roster entry
   whose `componentId` is not in `supportedComponentIds` with
   `MalformedArtifact("... has unsupported componentId '<id>';
   the mandatory supported set is [dotnet, workflow]")`.
2. **Set-equality guard** in `validateRoster` — after the per-entry
   and duplicate guards, the collected `Set<string>` must equal
   `supportedComponentIds`. A roster that is missing a required
   producer or adds an extra one is rejected with
   `MalformedArtifact("... does not match the mandatory supported
   set [dotnet, workflow]; unsupported componentId(s): <x>;
   missing required componentId(s): <y>")`.

The existing duplicate and path-drift guards are retained. No
dynamic discovery, no second roster authority, no version or SDK
policy is copied into the constraint.

## Tester follow-up (changes to test code only)

The compiled project exercises the mandatory exact-set contract in
`infrastructure/release/tests/Enrollment.fs`; the actual cross-stage CI guard is
covered by `infrastructure/release/tests/Coherence.fs`. Both are registered in
`Tests.fsproj` and `Main.fs`. The Coherence regression invokes the production
`Coherence.ps1` against controlled temporary roots and covers a passing baseline,
hardcoded matrix, renamed output marker, mismatched projection output, hardcoded
CLI config path, forbidden policy field, duplicate ID, unsupported extra
participant, missing required participant, and config-path registration drift.
These are test-source facts, not verified results.

The compiled project previously failed to build because Enrollment cases
exercised the previous "any valid non-empty duplicate-free roster" contract.
The exact-set expectation updates are now present:

- Test cases that build a custom non-exact roster (e.g. a
  single-entry roster, or a roster that adds a third component)
  must assert `MalformedArtifact` with the exact-set message, not
  `Ok ()`.
- Test cases that build a roster missing one of the two required
  componentIds must assert `MalformedArtifact` with the
  `missing required componentId(s)` message.
- Test cases that build a roster adding an extra componentId must
  assert `MalformedArtifact` with the `unsupported componentId(s)`
  message.
- Test cases that build the exact two-entry roster
  (`{ "dotnet", "workflow" }`) continue to assert `Ok ()`.
- Negative-fixture test cases for duplicates, forbidden fields,
  missing fields, and path drift are unchanged.
- `Publish`, `Runner`, and `Orchestration` direct-production
  negative fixtures prepared in `infrastructure/release/tests/`
  must also assert the new exact-set `MalformedArtifact` outcome
  whenever they construct or load a non-exact roster.

No production source was edited to accommodate the tester's
expectations. The test project is tester-owned per the W2 / W3
handoff; the engineer cannot edit it per the assignment
(`mayEdit=false` for test files). The release contract takes
priority over the tester's pre-existing expectations; the tester
updates the assertions.

## DevOps follow-up (completed)

The pre-SDK and cross-stage guards are now implemented; the production-layer
constraint remains authoritative and the PS layer mirrors it fail-closed.

- `infrastructure/release/Roster.ps1` (`role: reusable module/helper`) exports
  `Get-ReleaseRoster -Root <root>`, the single pre-SDK reader used by
  `Update.ps1`, `Typecheck.ps1`, `Suite.ps1` and `Coherence.ps1`. It rejects a
  missing/non-JSON/empty roster, empty or missing `componentId`/`configPath`,
  forbidden policy fields (`version`, `sdkVersion`, `runtimeVersion`, `platform`,
  `archiveName`, `assetUri`, `entryAllowlist`, `publishedFiles`, `entryDll`),
  duplicate `componentId`s, unsupported `componentId`s, and a roster that omits
  a required participant — mirroring `ReleaseEnrollment` without copying any
  version, SDK, allowlist or URI value.
- `Update.ps1 -Project` and `-Verify` consume that reader, so the exact
  `{dotnet, workflow}` set and the forbidden-field rule now fail before
  `setup-dotnet` lands.
- `infrastructure/release/Coherence.ps1` is the deterministic cross-stage guard
  executed by `release.yml` in the `bootstrap` job immediately after
  `Update.ps1 -Project`.

Commands and results (local, non-publishing):

```text
pwsh -NoProfile -File infrastructure/release/Update.ps1 -Project
  exit 0; sdk=11.0.100-rc.1.26425.128 runtime=11.0.0-rc.1.26425.128 platform=win-x64
  components=["dotnet","workflow"]

pwsh -NoProfile -File infrastructure/release/Coherence.ps1 -OutputFile $env:GITHUB_OUTPUT
  exit 0; cross-stage coherence ok: 15 assertions; roster=[dotnet, workflow]

pwsh -NoProfile -File infrastructure/release/Update.ps1 -Verify        # roster-wide
pwsh -NoProfile -File infrastructure/release/Update.ps1 -Verify -Component dotnet
pwsh -NoProfile -File infrastructure/release/Update.ps1 -Verify -Component workflow
  exit 0 each; Bootstrap.fsx verifies the executing toolchain and forwards
  RELEASE_SDK/RUNTIME/PLATFORM/ARCHITECTURE plus both producers' VERSION/ARCHIVE/
  ASSET_URI/ENTRY to $GITHUB_ENV
```

Deliberate-mismatch cases executed against the real guards on `-Root` temp
fixtures (no repository source changed): hardcoded matrix list, renamed
`components` output, hardcoded `orchestrate` CLI path, `-Component dotnet`
literal in the publication job, missing `RELEASE_COMPONENTS`, hardcoded
`dotnet-version`, drifted emitted projection, roster with an extra participant, a
missing participant, a forbidden `version` field, an unsupported key, and a
`configPath` drift — every case exits 1 with the specific guard message. Results
are recorded in `Integration.md` (W4 section, rows 17–19 and the
deliberate-mismatch table).

Workflow wiring: `release.yml` carries no participant list — the matrix is
`fromJSON(needs.bootstrap.outputs.components)`, the fixture step derives its
component tags from the same output, bootstrap verification is roster-wide, and
publication verification uses `${{ matrix.component }}`. The guard step fails the
job before the SDK install, and therefore before publication.

## Bootstrap FCS typecheck

`infrastructure/release/Typecheck.ps1` (FCS non-executing strict
typecheck of retained release-path scripts) run after the Bootstrap
syntax fix, with SDK `11.0.100-rc.1.26425.128`:

```
dotnet/release/Build.fsx             errors=0
dotnet/release/Pins.fsx              errors=0
dotnet/tests/DistributionTests.fsx   errors=0
dotnet/tests/ProvenanceTests.fsx     errors=0
infrastructure/release/Bootstrap.fsx errors=0
workflow/release/Build.fsx           errors=0
workflow/release/Pins.fsx            errors=0
workflow/tests/DistributionTests.fsx errors=0
typecheck total_errors=0 missing_targets=0 targets=8
```

## Bootstrap syntax / FCS root cause (resolved)

The earlier `Update -Verify` failure was an `FS0010` on
`Bootstrap.fsx`. Root cause: the prior refactor of `verify` into
`verify` + `continueVerify` left two artefacts in the source:

1. An orphaned `match failures with | first :: _ -> … | [] -> …` block
   duplicated after the closing `}` of `continueVerify` (original
   block was the tail of the pre-refactor `verify` body, lines
   290–336 of the working tree before the fix). The compiled build
   accepted the orphans because the orphaned expressions were
   unreachable; FCS rejected the whole file with
   `E1 This expression was expected to have type 'unit' but here has
   type 'Result<'a,'b>'`.
2. `and private continueVerify` (mutual recursion) where the
   `verify` / `continueVerify` split is sequential, not mutually
   recursive. FCS raised `E576 The declaration form 'let ... and ...'
   for non-recursive bindings is not used in F# code` plus the
   cascading `E39 continueVerify is not defined` because `and`
   requires both bindings to be defined together but `verify` already
   closed its scope.

Both resolved by:

- removing the orphaned duplicate block;
- switching `and private continueVerify` to `let private continueVerify`;
- reordering so `continueVerify` is defined before `verify` (FCS is
  stricter than the compiled build about source order);
- replacing the `match loaded with | [] -> return Error … | _ -> ()`
  with a single-arm `match loaded with | [] -> return Error … | _ ->
  <rest>` so the async block sees a consistent `Result` type across
  the empty-loaded branch and the rest of the function.

No identity checks were weakened. The cross-producer toolchain
equality check (`IdentityMismatch` for SDK / runtime / platform
mismatches between any two loaded producers) is unchanged and still
runs after the roster load + per-projection validation.

## Production roster-inventory policy (closed by W4 owners)

The earlier W4 implementation accepted any non-empty, duplicate-free roster.
That evidence is historical and is superseded by the owner changes below:
`ReleaseEnrollment` now validates exact membership in `{dotnet, workflow}`;
`Roster.ps1` applies the same set and forbidden-field checks before SDK setup;
`Coherence.ps1` validates workflow markers against the roster projection.
Tester assertions exercise these actual boundaries; they are not substitutes
for the production/CI guards.

**Status: closed.** `ReleaseEnrollment` now enforces the mandatory
`{dotnet, workflow}` set (per-entry and set-equality guards) and
`Roster.ps1` mirrors it pre-SDK along with the forbidden-policy-field rule, so
neither boundary accepts an omitted or additional participant.

## DevOps roster wiring (completed W4)

- `release.yml` no longer carries a `matrix.component` list: the `release` job
  matrix is `fromJSON(needs.bootstrap.outputs.components)`, projected by
  `Update.ps1 -Project` from this roster, and the fixture-provisioning step
  derives the component tags from the same output. A hand-edited list cannot
  drift from bootstrap or the CLI.
- `Update.ps1` now consumes the roster for projection (`-Project`) and for
  verification (`-Verify [-Component <id>]`, where a supplied id must be a roster
  participant and is rejected before the bootstrap runs). The former `$producers`
  table and `[ValidateSet("dotnet","workflow")]` are gone.
- `Typecheck.ps1` derives its producer targets from the roster instead of an
  enumerated list; `Suite.ps1` no longer restates the historical fixture names.
- Documentation scoping and the exact commands/results are recorded in
  `Integration.md` (W4 section).
- The Bootstrap parse defect reported by the initial W4 run is resolved by the
  production owner; the later Release/FCS evidence above is clean.

## Tester W4 enrollment regression slice

The tester added `infrastructure/release/tests/Enrollment.fs` and
`infrastructure/release/tests/Coherence.fs` to the compiled Expecto suite.
Coverage uses the actual `ReleaseEnrollment` API, isolated executions of the
real Bootstrap source, the compiled runner, `Update.ps1 -Root` controlled
fixtures, and the production `Coherence.ps1` guard. The cases cover:

- tester-edited paths are `infrastructure/release/tests/Enrollment.fs`,
  `Coherence.fs`, `Tests.fsproj`, and `Main.fs`; `Enrollment.fs` compiles after
  the shared fixture/process helpers and before the release suite consumers.

- the checked-in roster's exact Dotnet/Workflow inventory;
- duplicate IDs, all forbidden policy fields, and empty-roster validation;
- projection rejection for omitted/extra IDs and config-path drift;
- Bootstrap rejection for config-ID mismatch, policy mirrors and
  cross-producer runtime drift;
- actual transaction CLI rejection for an unenrolled component and compiled
  orchestrate CLI rejection for a config path outside the roster;
- `Update.ps1 -Project -Root` roster-derived projection order, duplicate/config
  identity rejection, and common-runtime mismatch; `-Verify` rejects an
  unenrolled component before invoking Bootstrap;
- generic canonical asset/metadata identity for independent producer-owned
  toolchains, and injected metadata transport for Dotnet/Workflow with all
  `RELEASE_EXPECTED_*` ambient values cleared. The shared orchestration fixture
  supplies a two-entry roster and distinct per-producer SDK/runtime/platform
  values, then verifies the same pipeline reaches each producer's test gate.
- actual `Update.ps1 -Project -Root <fixture>` projections are driven from the
  temporary roster (including reversed current-participant order), while
  `Runner.fs` tests transaction-mode rejection of an unregistered component.
- actual `Coherence.ps1 -Root -Workflow -OutputFile` baseline passes and
  deliberate matrix, renamed-output, projection-output, CLI-path, forbidden
  field, duplicate-ID, extra/missing-participant and config-registration drifts
  fail against isolated temporary fixtures.

The previous report of missing exact-set/field and workflow-marker guards is
superseded by the current production/DevOps implementation: `ReleaseEnrollment`
and `Roster.ps1` enforce the exact roster and forbidden fields, while the
bootstrap job invokes the actual `Coherence.ps1` cross-stage guard. The tester
does not add a static YAML check as a substitute.

**Implementation status after the DevOps slice:** both owner gaps are closed. `Roster.ps1` rejects the
extra/omitted participant and every forbidden policy field pre-SDK, and
`Coherence.ps1` runs in the `bootstrap` job to reject a hardcoded or mismatched
matrix marker, a renamed projection output, a hardcoded CLI path and a drifted
emitted projection. The local DevOps mismatch executions are recorded in
`Integration.md`; current compiled test execution evidence and failures are
recorded below.

The initial tester handoff preceded the successful Release test build and full
suite run recorded below. The separate producer Expecto executable was not
rerun; the moved checker source was unchanged, and the requested full release
gate ran the compiled release suite plus its provenance script.

## Risk and stop point

- Engineer production builds and the roster-driven FCS gate pass. The final
  compiled suite execution passed all cases; earlier bounded failures and
  test-only repairs remain recorded below as history.
- No acceptance claim is made here. AC-06's exact-inventory and workflow-marker
  implementation gaps are closed by the DevOps slice recorded above (pre-SDK
  `Roster.ps1` guard plus the `Coherence.ps1` bootstrap-job guard); compiled
  local compiled regression verification passed as recorded below; hosted CI and
  independent review remain outstanding.
  AC-07 retains the strict common bootstrap toolchain guard and adds independent
  per-producer generic tests.
- W4 implementation wiring and the tester-owned compiled verification are
  complete for coordinator synthesis; hosted CI and independent review remain
  separate outstanding evidence.
- No commits, no pushes, no network uploads, no real release
  publication.

## W4 compiled suite execution

Engineer build `i3LMJg2JG3pVePtek78lItXBxMcvgckHX6BPJroT9HY` succeeded with
exit 0, zero errors and zero warnings. The complete suite was then run without
building, with the approved historical fixture and upload/credential/workflow
output environment variables removed:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
& pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot 'C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'
exit $LASTEXITCODE
```

Result: exit 1. Expecto ran all 25 compiled test cases: 21 passed, 0 ignored,
4 failed, 0 errored. The suite's count matched its inventory; no case was
filtered or excluded. `dotnet/tests/ProvenanceTests.fsx` passed. Nested summaries
included Bootstrap 9/0, Evaluate 70/0, Historical 10/0, Invocation 10/0,
Metadata 52/0, Publish 62/0, Revision 22/0, producer PE integration 2/0, Runner
22/0, and Telegram 16 passed/3 failed.

The four failing compiled cases in that pre-repair run were:

| Case | Observed failure | Bounded diagnosis |
| --- | --- | --- |
| `enrollment.compiled enrollment API.the roster loader rejects duplicate identities and copied policy` | A one-entry Dotnet roster loaded successfully; test expected `loadRoster` itself to reject the omitted Workflow entry. | Test/API boundary mismatch: `loadRoster` parses entries; production callers and the direct API separately invoke `validateRoster`, which enforces exact membership. Preserve the exact-set assertion by asserting it through `validateRoster`, rather than changing production parsing semantics. |
| `enrollment.Update.ps1 -Project uses the controlled roster and rejects duplicate/config identity/toolchain drift` | Omitted participant was rejected, but captured output did not contain the test's expected `missing required componentId(s): workflow` fragment. | Diagnostic assertion mismatch remains unresolved. `Roster.ps1`'s missing-set branch throws that diagnostic at lines 62–65; the failed test did not include captured output, so the actual emitted text needs bounded follow-up before changing the assertion. |
| `cross-stage coherence.the production Coherence.ps1 rejects roster policy and inventory drift` | Omitted participant was rejected, but captured output did not contain the expected missing-set fragment. | Same unresolved diagnostic assertion mismatch. The real guard delegates roster validation to `Roster.ps1`; this run does not show that it accepted the incomplete roster. The config-path mismatch branch later in this case was not reached. |
| `Telegram legacy regression assertions` | Three nested checks expected a literal two-item `matrix.component` list and found zero entries. | Stale test expectation: the current workflow derives the matrix from `needs.bootstrap.outputs.components`; update the isolation assertion to enforce that roster-driven expression and absence of Telegram, without restoring literals or relaxing the exclusion contract. |

The first `Coherence.ps1` regression case passed, covering the baseline and
deliberate matrix, renamed-output, projected-output, and CLI-path mismatches. In
the second case, forbidden-field, duplicate-ID and extra-participant checks ran
before the omitted-participant diagnostic assertion failed. No production,
PowerShell or YAML source was changed for this execution; the remaining
test-only repairs require a new engineer build before another run.

## Bounded tester migration after the W4 run

- Enrollment omission now composes `loadRoster` followed by `validateRoster` and
  still asserts the exact missing-Workflow failure. `Bootstrap.fsx`,
  `Orchestration.fs`, and `Runner.fs` all use this same load-then-validate path;
  no publication caller was found that accepts the roster without exact-set
  validation.
- Update and Coherence stderr assertions retain the full actionable
  `missing required componentId(s): workflow` fragment, normalize ANSI styling,
  PowerShell continuation bars, whitespace and case, and include captured output
  in any failure message.
- Both compound PowerShell drift tests now run every roster mutation/guard
  invocation before evaluating the captured assertions, so an earlier diagnostic
  mismatch cannot prevent later config-path or toolchain drift scenarios from
  being exercised.
- The Telegram suite retains its single Expecto case and replaces obsolete
  literal-matrix checks with exact `Participants.json` identity/config pairs,
  no Telegram participant, the roster-projection matrix expression, no
  hardcoded participant list or CLI path, and the workflow's Coherence guard
  wiring. The Coherence suite separately executes its actual guard baseline.

The initial repairs were rebuilt and exercised by the rerun below. The later
PowerShell continuation-bar and Bootstrap stream-capture refinements remain
unverified until an engineer rebuild and tester rerun.

## W4 rerun after initial test migration

Engineer build `5c_W39Td28sDUKk8mWT5i0zUA3t5ZLCo3IlRvgE5S_U` succeeded with
zero errors and warnings. The same complete local-fixture gate was rerun without
building, with upload/credential/workflow-output variables removed:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
& pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot 'C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'
exit $LASTEXITCODE
```

Result: exit 1; all 25 compiled cases ran, 22 passed, 0 ignored, 3 failed, 0
errored; inventory matched. Provenance passed. Nested summaries: Bootstrap 6
passed/3 failed, Evaluate 70/0, Historical 10/0, Invocation 10/0, Metadata 52/0,
Publish 62/0, Revision 22/0 plus producer PE integration 2/0, Runner 22/0, and
Telegram 22/0. The two W4 missing-participant failures showed that PowerShell's
formatted exception wraps the actionable phrase across lines with continuation
bars (the expected text is split as `missing | required componentId(s):
workflow`). The compound Enrollment and Coherence fixtures ran every roster
mutation before their diagnostic assertions, including the later config-path
drift case.

The third failure was the retained Bootstrap `Update.ps1 -Project` test: it
searched stdout only and reported missing `RELEASE_SDK`, `RELEASE_RUNTIME`, and
`RELEASE_PLATFORM` lines. Its assertions still require each canonical value;
the test now searches the combined stdout/stderr and includes captured output in
failure details. This is a bounded test-capture correction, not a production
projection change.

The latest test-only refinements strip PowerShell continuation bars when
normalizing the full missing-participant diagnostic. They are unverified until
the next engineer build and tester run.

## Tester compile repair handoff

Engineer build run `zzVS-SfZ_TZ46JBl2e_M_bKoTEA8ofhXHTSV9qU4QCo` reported 30
repeated diagnostics, all in `infrastructure/release/tests/Enrollment.fs`, with
0 warnings. Bounded retained diagnostics identified three source causes:

- FS0041 at the roster text helper: `File.WriteAllText` could not infer whether
  the contents were `string` or `ReadOnlySpan<char>`; the `text` argument now
  has an explicit `string` type.
- FS0041 at the error assertion helper: `String.Contains` overload inference
  lacked a type for `fragment`; `label` and `fragment` now have explicit `string`
  types. The missing `contains` helper used by Bootstrap diagnostics is defined
  as the same ordinal substring check.
- The Bootstrap process helper unwraps `ReleaseTestProcess.capture` and returns
  `(exitCode, stdout, stderr)`, but three callers matched it as a `Result`; they
  now destructure that tuple. No assertion conditions were weakened.

## Coherence fixture compile repair

Engineer build `QJg9eKcevYFgXzl1sP1jkubRCtKZ1QJdjVTfg7qZT3c` failed with 12
diagnostics / 6 unique errors, all `FS0041` overload-resolution errors in
`infrastructure/release/tests/Coherence.fs` at the `JsonValue.Create`,
`JsonNode.set_Item`, and `File.WriteAllText` fixture helpers. The test helper
inputs `root`, participant IDs/config paths, extra-field names/values, workflow
text and projection output are now explicitly typed as strings and string pairs
to resolve the overloads. Assertions and production code are unchanged; the test
project has not yet been rebuilt after this repair.

The repaired release test project was rebuilt successfully in Release by the
engineer build recorded above. The previously reported production builds remain
evidence only:
`Release.fsproj` `KLezwqX-7idclXZybGX-i93PwTd0Qx_aI4Qo64AVY8` and producer test
build `8PZInfaR2JztvqVsYtmcXtFxw3N8ivRp_BJkKLw_cA8` each succeeded with 0/0.

## W4 suite rerun after diagnostic and stream fixes

Engineer build `fyatmCla11xm-2LHzl0NFfAX3SX9m3i1YKCaav01CwQ` succeeded with
zero errors and warnings. The actual complete local-fixture gate ran without
building, after removing upload/credential/workflow-output variables:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
& pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot 'C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'
exit $LASTEXITCODE
```

Result: exit 1; all 25 Expecto cases ran, 24 passed, 0 ignored, 1 failed,
0 errored; inventory matched and provenance passed. Nested summaries: Bootstrap
9/0, Evaluate 70/0, Historical 10/0, Invocation 10/0, Metadata 52/0, Publish
62/0, Revision 22/0 plus PE integration 2/0, Runner 22/0, Telegram 22/0.
`Enrollment.updateProjectUsesRosterProjectionAndRejectsDrift` failed only its
config-identity diagnostic assertion. The controlled identity fixture contained
only Dotnet, so the exact-set guard rejected the missing Workflow participant
before `Update.ps1` could load the mismatched Dotnet config. This is a test
fixture defect, not a production projection or config-validation failure.

The same missing-roster fixture made the Bootstrap config-ID regression capable
of passing on the wrong failure (missing Workflow). Its fixture now has the exact
two-participant roster and valid Workflow config, and the assertion requires the
actual Dotnet config/roster mismatch diagnostic. No Bootstrap projection failed
in the recorded run. These final fixture corrections are test-only and await an
  engineer rebuild and tester rerun; the 24/1 run remains the latest execution.

## W4 suite rerun after exact identity fixtures

Engineer build `MGgUmEFTDD7vGxYPOzPWrWSVfIacV2RdWzssfLCAvmc` succeeded with
zero errors and warnings. The complete suite was run without building against the
same approved local historical fixture, with upload/credential/workflow-output
variables removed:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
& pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot 'C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'
exit $LASTEXITCODE
```

Result: exit 1; all 25 Expecto cases ran, 24 passed, 0 ignored, 1 failed,
0 errored; inventory matched and provenance passed. Nested summaries: Bootstrap
9/0, Evaluate 70/0, Historical 10/0, Invocation 10/0, Metadata 52/0, Publish
62/0, Revision 22/0 plus producer PE integration 2/0, Runner 22/0, Telegram
22/0. Update and Coherence controlled drift cases all passed, including the
exact-set, config identity/path, toolchain, workflow matrix/output/CLI, and
policy-field scenarios.

The only failure was the compiled Bootstrap config-ID regression's expected
diagnostic. With the corrected exact two-entry fixture, production
`loadProducerConfig` rejects the mismatched Dotnet config first and reports
`componentId does not match the release pin: expected dotnet, got workflow`;
the test had expected the later roster-projection diagnostic. This is a stale
test expectation, not a production defect. The assertion now matches the
observed actionable diagnostic; this final test-only edit awaits an engineer
rebuild and tester rerun.

## W4 final complete-suite verification

Engineer build `9mVDQdm36zeFQlbTkKkhpHjAxYoV3fLOUnRvqJWqdY4` succeeded with
zero errors and warnings. The actual complete gate ran without building, using
the approved local historical fixture and with upload/credential/workflow-output
variables removed:

```powershell
Remove-Item Env:MCP_RELEASE_DO_UPLOAD, Env:GITHUB_TOKEN, Env:GITHUB_ENV, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
& pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot 'C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'
exit $LASTEXITCODE
```

Result: exit 0; all 25 compiled cases ran, 25 passed, 0 ignored, 0 failed, 0
errored; the suite inventory matched. Provenance passed and the gate reported
`25 compiled case(s) (including 1 historical) plus 1 producer script(s)`.
Nested counters: Bootstrap 9/0, Evaluate 70/0, Historical 10/0, Invocation
10/0, Metadata 52/0, Publish 62/0, Revision 22/0 plus PE integration 2/0,
Runner 22/0, Telegram 22/0.

The passing enrollment assertions include Update duplicate, omitted/extra
participant, forbidden-policy, config identity and common-runtime diagnostics;
the Bootstrap config-ID check reports `componentId does not match the release
pin: expected dotnet, got workflow`. The actual Coherence guard passed its
baseline and deliberate matrix, renamed-output, projected-output, CLI-path,
roster policy, duplicate, unsupported/omitted participant and config-path drift
cases. No actual network request or upload occurred. This records Stage 3 tester
verification only; it does not mark any acceptance criterion PASS.
