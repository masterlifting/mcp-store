# Stage 2 — release naming + comment audit (GHR-36 W3)

Authoritative inputs: `Solution.md`, `Requirements.md` (AC-08, AC-09),
`Baseline.md`, and the canonical `comments.md` / `architecture.md`
policies. Baseline head: `05a69ced8319b61a6bf0e24cad6d320a0a879283`.
This records the bounded Stage 2 engineering comment pass and the tester-owned
checker/test path migration. DevOps owns any PS/YAML comment changes.

## Scope and non-scope

- **In scope** — bounded pass on new/modified production release code
  (the converted `.fsx` → `.fs` reusable modules, the new
  `Transaction.fs` / `Orchestration.fs`, the thin `Runner.fs`, the
  retained `Bootstrap.fsx` and the `#load`-loaded
  `infrastructure/{Testing,Pins,Distribution}.fsx` and `Provenance.fs`).
  Producer `Config.fsx` / `Build.fsx` / `Pins.fsx` were not modified
  in W2 and are out of scope for this audit.
- **Outside the tester slice** — DevOps owns the `Typecheck.ps1` and
  `.github/workflows/release.yml` comments; that work is recorded in
  `Integration.md`.
  release functional / integrity fixes (Stage 4).

## Naming audit (AC-08)

Every new release source path is one-word:

| Path | One-word | Externally mandated identity preserved |
| --- | --- | --- |
| `infrastructure/Provenance.fs` | yes | n/a |
| `infrastructure/release/Archive.fs` | yes | n/a |
| `infrastructure/release/Compare.fs` | yes | n/a |
| `infrastructure/release/Metadata.fs` | yes | n/a |
| `infrastructure/release/Publish.fs` | yes | n/a |
| `infrastructure/release/Evaluate.fs` | yes | n/a |
| `infrastructure/release/Capture.fs` | yes | n/a |
| `infrastructure/release/Transaction.fs` | yes | n/a |
| `infrastructure/release/Orchestration.fs` | yes | n/a |
| `infrastructure/release/Runner.fs` | yes | n/a |
| `infrastructure/release/Release.fsproj` | yes | project identity `ReleaseRunner` retained per Solution.md |
| `infrastructure/release/Bootstrap.fsx` | yes | n/a |
| `infrastructure/release/tests/Tests.fsproj` | yes | assembly identity `Release.Tests` retained |
| `dotnet/tests/Mcp.Dotnet.Tests/blocking/Checker.fs` | yes | module identity `Mcp.Dotnet.Tests.SyncOverAsyncChecker` retained |
| `dotnet/tests/Mcp.Dotnet.Tests/blocking/Tests.fs` | yes | module identity `Mcp.Dotnet.Tests.SyncOverAsyncCheckerTests` retained |

The former multi-word checker paths were renamed by the tester to the two
one-word paths above. The old path spellings are retained here only as migration
history; active project compile entries, tests and checker implementation use
the `blocking/Checker.fs` and `blocking/Tests.fs` paths.

## Comment audit (AC-09)

Policy source: `capabilities/software/knowledge/comments.md`.
Applied to the new/modified production release code only.

### Violations removed in this pass

| File | Lines (HEAD) | Issue | Fix |
| --- | --- | --- | --- |
| `infrastructure/release/Runner.fs` | 2–5 (header) | Header was 4 comment lines; 3-line file-header cap exceeded. | Condensed to 2 lines plus the `// role:` tooling directive (3 logical points). |
| `infrastructure/release/Runner.fs` | 70–73 (was) | Inline comment referenced `pre-stage1 behavior` — phase marker. | Replaced with a single-line description of the standalone-transaction pipeline (no phase reference). |
| `infrastructure/release/Runner.fs` | 287–290 (was) | `// Standalone entry bridge: …` was 4 lines; policy: "if one line is insufficient, retain only the most important reason." | Condensed to one line; the synchronous-wait rationale is retained. |
| `infrastructure/release/Orchestration.fs` | 2–5 (header) | Header was 4 comment lines; 3-line cap exceeded. | Condensed to 3 lines (orchestration summary + sole-policy-authority + JSON contract). |
| `infrastructure/release/Transaction.fs` | 2–5 (header) | Header was 4 comment lines; 3-line cap exceeded. | Condensed to 3 lines (transaction summary + pure-of-CLI rationale + imperative-boundary rationale). |

### Retained (already conformant)

- `// role:` tooling directives preserved in every header.
- One-line `why` comments preserved everywhere else — examples
  preserved verbatim:
  - `infrastructure/release/Archive.fs` — `// Decompresses one entry
    under its declared size, then verifies size and CRC so a lying
    header cannot expand past the bound.`
  - `infrastructure/release/Compare.fs` — `// ManifestBody /
    ManifestSha256 retain the published manifest bytes and hash; the
    revision it carries stays distinct from the evaluated HEAD.`
  - `infrastructure/release/Metadata.fs` — `// A mismatched schema
    version is rejected so a schema change cannot bypass the strict
    field check.`
  - `infrastructure/release/Publish.fs` — `// Minimum token authority
    is the canonical GITHUB_TOKEN environment variable only.`
  - `infrastructure/release/Capture.fs` — `// Bound so a hung child
    cannot deadlock the wrapper or the compiled runner past the
    publishing window; the wrapper's ambient token still wins if
    shorter.`
  - `infrastructure/release/Transaction.fs` — `// Writes the
    verified/observed immutable metadata projection; candidate values
    are never persisted on the success path.`
  - `infrastructure/release/Orchestration.fs` — `// The canonical
    assetUri travels in the contract DTO; the runner never
    reconstructs it.`
  - `infrastructure/release/Runner.fs` — `// Local metadata write
    deferred so the file reflects verified/published bytes, not
    candidate values.`
  - `infrastructure/release/Bootstrap.fsx` — `// Requires the exact
    canonical runtime patch so a different toolchain cannot quietly
    reach the build path.`
  - `infrastructure/Provenance.fs` — `// Async.AwaitTask reports a
    faulted task as AggregateException; surface its single
    operational cause so focused catches can classify it.`

### Patterns checked, none present

- `F2` / `F7` / `F9` / `Slice N` / `phase` / `step` / `pre-stage1` /
  `pre-step` — none remaining in the new/modified production release
  code.
- `// TODO`, `// FIXME`, `// HACK`, `// XXX` — none.
- Banners / `===` separators — none.
- Section headers like `// --- foo ---` — none.
- `deliberately` / `intentionally` / `on purpose` — none.
- Comments that claim a check or validation the code does not
  perform — none.
- API / runtime contracts inside comments — none of the changed
  comments document exit codes, JSON shapes, or CLI syntax. The
  binary's structured-failure JSON contract is documented in
  `infrastructure/release/README.md`, the workflow summary format is
  in `Bootstrap.fsx` source, and the orchestrator's per-phase JSON
  shape is asserted by the compiled `Tests.fsproj` suite.

## Engineer files changed during the comment pass

- `infrastructure/release/Runner.fs` — header condensed; `pre-stage1`
  reference removed; `Standalone entry bridge` comment condensed.
- `infrastructure/release/Orchestration.fs` — header condensed.
- `infrastructure/release/Transaction.fs` — header condensed.

No other production file was modified in the engineering comment pass. The
separate tester-owned path migration is recorded below.

## Build runId

`infrastructure/release/Release.fsproj` rebuilt through the dedicated
Dotnet MCP after the source edits:

- runId: `qq0zBkLCFRNlvcxbwxgRLCU24ZScNCmU0sMRdHsnnnk`
- status: `succeeded`; exitCode `0`; errorCount `0`; warningCount `0`
- configuration: `Release`
- `TreatWarningsAsErrors=true`
- prior runId `R4LgZe-3HFAdyt2vAyJS-oKF2tDdXz9IFVTy6-ptRvs` (the
  runner-observable-regression fix) is superseded; the
  `Tests.fsproj` runId `mU_eeoaejXYvJBgJQs7IpzQk1pq5pUA-ojFhv3a_ODc`
  predates the W3 checker/test path edits and must not be treated as a build
  result for the current test-project sources.

## W3 affected test-project builds

After the checker/test path migration and exact bridge-map update, the engineer
rebuilt both affected projects:

| Project | runId | Status | Errors | Warnings |
| --- | --- | --- | ---: | ---: |
| `dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj` | `8-AsKjbzyA1Ii0EiDS4rxJBqfKmmupcRo2Wg7N3PNjY` | succeeded | 0 | 0 |
| `infrastructure/release/tests/Tests.fsproj` | `wkXI9i2KCEkEg-lspCeND_NHSddtSbrOKsOlb7icUos` | succeeded | 0 | 0 |

The source-current producer build has no checker violations; the Release project
comment-pass build remains `qq0zBkLCFRNlvcxbwxgRLCU24ZScNCmU0sMRdHsnnnk` (0/0).

## Tester / DevOps handoff (no acceptance claim)

- **Tester (completed and verified)** — renamed the paired
  checker files, changed ordered project entries, retained module identities,
  updated the `Provenance.fs` test expectation, and replaced stale bridge-map
  entries with exact current sites. The two current `Runner.fs` CLI bridges are
  approved only as the exact pair; an added third bridge remains blocking.
  Both project builds succeeded with 0 errors / 0 warnings; the producer suite
  and integrated release suite results are recorded below.
- **DevOps (completed W3)** — `Typecheck.ps1` and
  `.github/workflows/release.yml` no longer mention the deleted
  `dotnet/release/Release.fsx` / `workflow/release/Release.fsx` wrappers: the
  workflow drives `ReleaseRunner.exe orchestrate <config-path>` plus the compiled
  Expecto gate, and the PS/YAML comment audit is recorded in `Integration.md`.
- **No Stage 4 work** — AC-01 / AC-02 / AC-03 integrity fixes are
  not touched in Stage 2.
- **No commits / pushes / network uploads** — working tree is dirty
  on purpose; W3 is **not** marked done.

## Tester-owned checker path migration

| Former path (historical) | Current implementation path | Module identity |
| --- | --- | --- |
| `dotnet/tests/Mcp.Dotnet.Tests/SyncOverAsyncChecker.fs` | `dotnet/tests/Mcp.Dotnet.Tests/blocking/Checker.fs` | `Mcp.Dotnet.Tests.SyncOverAsyncChecker` |
| `dotnet/tests/Mcp.Dotnet.Tests/SyncOverAsyncCheckerTests.fs` | `dotnet/tests/Mcp.Dotnet.Tests/blocking/Tests.fs` | `Mcp.Dotnet.Tests.SyncOverAsyncCheckerTests` |

The existing `Mcp.Dotnet.Tests.fsproj`, assembly identity and `Main.fs` module
references are unchanged. `blocking/Checker.fs` no longer whitelists deleted
producer release wrappers or migrated `.fsx` suites. Its `Runner.fs` entries
are the exact `runOrchestrateMode argv |> Async.RunSynchronously` and
`runTransactionMode argv |> Async.RunSynchronously` lines; the paired tests
prove that both are accepted while an additional bridge is rejected. Old path
references in preserved `docs/GHI-24/async.md`, `tests.md` and earlier evidence
are historical records, not active source/project/tooling references.

The previous engineer build `n2oCl5NnihaRyTyDfaW9-EA4FI-Jo5Kaf6IrQKD_teQ`
reported two repeated `FS0597` diagnostics at `blocking/Checker.fs(282,70)`;
the tester added only the required `.Trim()` argument parentheses. The clean
builds and actual test results above/below supersede that failure.

## Checker traversal boundary

The nested-checkout traversal correction retains the existing one-word paths
`dotnet/tests/Mcp.Dotnet.Tests/blocking/Checker.fs` and
`dotnet/tests/Mcp.Dotnet.Tests/blocking/Tests.fs`, along with their established
module identities. It introduces no filename or compile-order changes. The
root cause, regression cases, and pending verification are recorded in
`Verification.md`.

## W3 actual producer and release test execution

The producer Expecto executable ran without rebuilding:

```text
dotnet run --no-build --no-restore -c Release --project dotnet/tests/Mcp.Dotnet.Tests/Mcp.Dotnet.Tests.fsproj
```

Result: **151 run, 151 passed, 0 ignored, 0 failed, 0 errored**. The moved
checker tests include the exact Runner two-site approval and a synthetic third
bridge rejection; the repository-surface scan also passed. The strict checker
did not add a broad regex exemption.

The Stage2-affected integrated release suite also ran without rebuilding:

```text
pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap
```

Result: exit 0; **12 compiled cases passed, 0 ignored, 0 failed, 0 errored**,
plus the provenance script passed and the output sentinel passed. Historical
fixtures were local and SHA/size-pinned; no download, network request or upload
occurred. No W3 test/source edits followed these runs. This records only the
tester-owned W3 validation; it is not an acceptance PASS or a W2 completion claim.
