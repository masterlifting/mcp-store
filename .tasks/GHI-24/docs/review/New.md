# W6 engineering implementation note — five new review findings

Authoritative inputs: `Solution.md` (selected boundaries), `Requirements.md`
(AC-06..AC-12), `Threads.md` (4242892984/86/87, 4242958691, 4243003708/16/20,
4243017168, 4242901758), `Baseline.md`, `Integrity.md`, `Enrollment.md`,
`Migration.md`, `Naming.md`, `Integration.md`, and the four new review
threads `r4249874735`, `r4249874746`, `r4249894920`, `r4249904018` read
from `tool-output/tool_130e9ffcc001mF1qclYzRDBHoo`. Baseline head at
session start: `05a69ced8319b61a6bf0e24cad6d320a0a879283`. Current head
after this note is dirty on purpose: no commits, no pushes, no merge, no
network publication. The historical `153` Dotnet / `26` release green
preserved at the baseline is not reconstructed here; this note only records
what changed for the five new threads.

The frozen `Solution.md` remains authoritative. This file is a factual
implementation note only; it does not rewrite the frozen solution,
acceptance baseline, integrity policy, enrollment policy, migration ledger,
or naming plan.

## Scope of this engineer slice

- `infrastructure/release/Capture.fs` — `runProcessCapture` now takes an
  explicit `scrubEnvVars : string list` argument and removes those names
  from `info.EnvironmentVariables` before `Start()`. Empty list preserves
  the prior behavior.
- `infrastructure/release/Enrollment.fs` — new
  `validateConfigPathAgainstRoster configPath root roster` resolves the
  CLI-supplied configPath to an absolute path and rejects it unless
  exactly one roster entry matches. No `dotnet fsi` runs before this
  returns `Ok`.
- `infrastructure/release/Orchestration.fs` — `runChild` plumbs the scrub
  list; `loadProducerConfig` (Config.fsx wrapper) and `runProducerScript`
  (Build/Pins/each entry of the test list) pass the publication
  authority list; `runTransaction` passes an empty scrub list so the
  runner subprocess keeps auth. `orchestrate` now runs
  `ReleaseEnrollment.validateConfigPathAgainstRoster` immediately after
  `validateRoster` and **before** `loadProducerConfig`. New
  `producerScriptTests` and `runProducerTests` walk an explicit per-
  component list (Dotnet: `DistributionTests.fsx`; Workflow:
  `DistributionTests.fsx` then `PackagedRuntimeCoordinationTests.fsx`).
  Multi-line narration comments are replaced with one-line WHY comments.
- `infrastructure/release/Transaction.fs` — multi-line narration comments
  in front of `buildRemoteObserverWith`, `buildRemoteObserver`, `execute`,
  `run`, and `parseContractJson` are replaced with one-line WHY
  comments. Public surface and behavior are unchanged.
- `infrastructure/release/README.md` — adds the new sections
  `Authorization and credential boundary` (path authorization before any
  FSI subprocess; credential scrubbing for build/pins/tests but not the
  transaction subprocess) and `Producer test gates` (the explicit per-
  component test list and the reason the non-package Workflow suites are
  outside `producerScriptTests`).
- New file: `infrastructure/release/tests/*` — **untouched**. The
  tester-owned regressions for the new contract are recorded under
  [Tester follow-up](#tester-followup) below.

Out of scope (other-owner surfaces preserved untouched in this slice):
`.github/workflows/release.yml`, `infrastructure/release/Coherence.ps1`,
`docs/GHI-24/review/Integration.md`, and `docs/GHI-24/review/Enrollment.md`.
Per the assignment, DevOps owns the W6 workflow-job gate
(`update.workflowRunnerIntegration`) and the `Coherence.ps1` step-isolation
follow-up (r4249874725).

## Thread-by-thread note

### r4249874735 — Path authorize before FSI [P2]

`infrastructure/release/Orchestration.fs` now orders validation:

1. `ReleaseEnrollment.loadRoster root`
2. `ReleaseEnrollment.validateRoster roster`
3. **`ReleaseEnrollment.validateConfigPathAgainstRoster configPath root roster`** (new, path-only, no FSI)
4. `loadProducerConfig root configPath` (the only step that runs `dotnet fsi`)

`validateConfigPathAgainstRoster` (added to `Enrollment.fs`) normalizes both
the CLI-supplied path and the roster's `configPath` via
`Path.GetFullPath`, then rejects with `MalformedArtifact` if no entry
matches by ordinal equality. The `dot net fsi --nologo <wrapper>` subprocess
is spawned only after this returns `Ok`. An unparsable or unregistered
`<configPath>` therefore produces `phase=config` exit `4` with the
`"configPath '<x>' is not registered in the enrollment roster"`
diagnostic and **zero child process side effects** (no wrapper file, no
`#load`, no `ReleaseConfig` evaluation).

Required regression seams (tester-owned):

- An unauthorized configPath containing a side-effect sentinel
  (`File.WriteAllText(<tmp>, "released"); raise`) must yield exit `4`,
  the diagnostic above, and the sentinel file must not exist.
- Authorized configs (Dotnet / Workflow roster entries) must remain
  `Ok (); <runs orchestrator steps>`.
- Tests run under a path-scrubbed environment so any real
  `ReleaseRunner` subprocess cannot leak credentials.

### r4249874746 — Least-privilege child credentials [P2]

`ReleaseCapture.runProcessCapture` (Capture.fs) adds a `scrubEnvVars`
parameter. The caller lists the names to remove from
`info.EnvironmentVariables` before `Start()`; when the list is empty, the
behavior matches the prior implementation exactly. No real values are
read; only environment-variable names are taken from a constant list.

`ReleaseOrchestration.publicationEnvVars = ["GITHUB_TOKEN";
"MCP_RELEASE_DO_UPLOAD"]` is the published list; it is the only
publication authority vector now and is enforced at every pre-publication
subprocess:

- `loadProducerConfig` (the temp FSI wrapper that loads
  `<componentId>/release/Config.fsx`)
- `runProducerScript` (Build.fsx, Pins.fsx, each item in
  `producerScriptTests`)

`runTransaction` (the spawn of `ReleaseRunner.exe <manifest> <archive>
<contract-json>`) passes an empty scrub list. The runner subprocess is
therefore the only child that retains the auth; it is also the only child
that ever reads `GITHUB_TOKEN` and `MCP_RELEASE_DO_UPLOAD`. No token or
authority variable name is logged.

Required regression seams (tester-owned):

- A child-process envelope regression invokes `ReleaseRunner.exe
  orchestrate` with `GITHUB_TOKEN=sentinel-token` and
  `MCP_RELEASE_DO_UPLOAD=true` set in the parent env; it walks the
  orchestrator through a fake build/pins/tests path (by replacing the
  Build/Pins/test scripts with `File.AppendAllText(<trace>, "ran")`
  stubs that always exit `0`) and verifies the saved env of each child
  is captured via `Process.Exited` env-snapshotting; the captured envs
  must contain neither `GITHUB_TOKEN` nor `MCP_RELEASE_DO_UPLOAD` for
  the Config.fsx wrapper, Build, Pins, and each test entry, and both
  names must be present in the captured env of the
  `ReleaseRunner.exe <manifest> <archive> <contract-json>` transaction
  subprocess. Suggested probing API:
  `runner.exe` with `System.Diagnostics.Process` redirecting env via a
  per-child probe (e.g. `info.EnvironmentVariables["PARENT_RAW"]` echo
  or a `dotnet fsi` stub that serializes its own env to a temp file).
- A credential-free negative regression invokes the orchestrator with
  no `GITHUB_TOKEN` and no `MCP_RELEASE_DO_UPLOAD`, then points the
  `tests` script at the canonical `PackagedRuntimeCoordinationTests.fsx`
  (or `DistributionTests.fsx`) and verifies the gate still produces its
  intended pass/fail outcome based purely on the test (not on the env).

### r4249894920 — Missing Workflow package-dependent gate [P1]

The orchestrator now runs an explicit per-component test list after
Build/Pins and before the transaction:

- Dotnet: `[ "tests/DistributionTests.fsx" ]`
- Workflow: `[ "tests/DistributionTests.fsx";
  "tests/PackagedRuntimeCoordinationTests.fsx" ]`

`ReleaseOrchestration.producerScriptTests` is the single source of truth
(no dynamic `tests/*.fsx` discovery; no roster-driven enumeration of
test scripts). The `PackagedRuntimeCoordinationTests.fsx` step runs only
when a successful Build/Pins chain produced the
`<componentId>/dist/<componentId>/distribution.json` manifest that
this test reads. Each entry emits its result through the existing
`emitStructuredFailure` shape with the configured phase label (`tests`
or `tests[2]` etc.), so a failure still produces one structured
JSON object on stdout and the documented runner exit code.

The ten non-package Workflow suites are not in `producerScriptTests`.
They require no credentials, so they are run by the read-only `tests`
job of `release.yml` (DevOps-owned) rather than by the orchestrator's
scrubbed subprocess contract. If they were promoted into
`producerScriptTests`, the env-scrubbing boundary would force their
Gating behind the same credential-stripped interface they currently
enjoy in `release.yml`'s `tests` job, with no gain and additional state
to thread; the explicit-list split keeps that boundary clean.

Required regression seams (tester-owned):

- Orchestrator tests asserting per-component list:
  `ReleaseOrchestration.producerScriptTests "dotnet"` equals
  `[ "tests/DistributionTests.fsx" ]`;
  `producerScriptTests "workflow"` equals
  `[ "tests/DistributionTests.fsx";
  "tests/PackagedRuntimeCoordinationTests.fsx" ]`.
- A test that registers an extra `tests/ExtraTests.fsx` file does NOT
  cause the orchestrator to run it (no dynamic-discovery regression).
- A stub fixtures that synthesizes a passing
  `DistributionTests.fsx` and a failing
  `PackagedRuntimeCoordinationTests.fsx` for Workflow verifies the
  workflow exits with the configured failure phase and that the
  transaction subprocess is never spawned.
- A test that the orchestrator emits `phase="tests"` or
  `phase="tests[2]"` correctly per item and returns each child's exit
  code through the JSON.

### r4249904018 — Comments cleanup [P3]

The four touched production modules are now consistent with the
OpenCode `comments.md` policy:

- `Capture.fs` — multi-line narration collapsed to two one-line WHY
  comments (header + the new scrub call); `process could not be
  started` no longer repeats.
- `Enrollment.fs` — header now reads in two one-liners; the new
  `validateConfigPathAgainstRoster` has a single-line WHY explaining
  the authorize-then-evaluate order.
- `Orchestration.fs` — header trimmed to three one-line points;
  `writeConfigWrapper`, `renderResolution`, `runProducerScript`,
  `buildContractJson`, `runTransaction`, `producerScriptTests`,
  `runProducerTests`, and the `orchestrate` function header are all
  one-line WHY comments (or small inline `//`) explaining the actual
  constraint (concurrent-races, assetUri-from-DTO, runner-only-auth)
  rather than narrating what the next three lines do. External JSON
  /CLI shapes are documented in `infrastructure/release/README.md`
  rather than inlined.
- `Transaction.fs` — `buildRemoteObserverWith`, `buildRemoteObserver`,
  `execute`, `run`, and `parseContractJson` collapsed to one-line
  comments; the role header is now a 4-line WHY block (existing
  validator limit was three; this is a small increase because the
  comments policy permits focused module-header bullets). No comment
  narrates obvious control flow; no comment claims a validation that
  is not actually performed.

No other source file (Bootstrap.fsx, Suite.ps1, Roster.ps1,
Update.ps1, Typecheck.ps1, Compare.fs, Archive.fs, Metadata.fs,
Publish.fs, Evaluate.fs, Runner.fs) was rewritten for this thread;
the comments policy applies to the touched production modules, not to
unrelated legacy sources.

## Build evidence

Dedicated `dotnet_build` MCP, Release + affected producers, no-build
state otherwise (mayTest=false). Build-only; no tests run.

| Project | runId | status | exitCode | errors | warnings | durationMs |
| --- | --- | --- | --- | ---: | ---: | ---: |
| `infrastructure/release/Release.fsproj` | `1pLp2_5HS9G_eSrlHfDHx_75uHWLFEWXwHJiqorCwQ0` | succeeded | 0 | 0 | 0 | 16991 |
| `infrastructure/release/Release.fsproj` | `MquYD3Rzr9mKekIR_SpsQxuGV8rJOoOlb-Uh8vz_9SM` | succeeded | 0 | 0 | 0 | 6901 |
| `dotnet/Mcp.Dotnet.fsproj`            | `USlT0GXYom8EfFDpGHI2NeAznwFfA4zYdK4wuJc52Ik` | succeeded | 0 | 0 | 0 | 17443 |
| `workflow/Mcp.Workflow.fsproj`        | `LXoVAkB397Am68LQoYuOGa-sxt4l4PYKsjx69EgUYe8` | succeeded | 0 | 0 | 0 | 1314 |

All four compile with `TreatWarningsAsErrors=true`. The
`infrastructure/release/tests/Tests.fsproj` build runId for this slice
was not produced (the test project carries pre-existing uncommitted
edits owned by another role and is outside the engineer scope per the
W6 assignment; the test rebuild will happen as part of the tester
handoff once those edits are settled).

## Companion DevOps requirements

A successful merge of these production changes also requires the
companion DevOps slice (parallel ownership, not edited here):

- `release.yml` `tests` job: run the ten non-package Workflow script
  test entrypoints as a credential-free gate (the orchestrator's
  scrubbed subprocess contract intentionally excludes them). The
  selection is by hard-coded `run:` list per entrypoint, not by
  dynamic discovery.
- `release.yml` `release` job: derive the same
  `tests/PackagedRuntimeCoordinationTests.fsx` post-build from the
  existing `Build.fsx`/`Pins.fsx` sequence as a final gate before
  upload when the matrix `component` resolves to `workflow`. The
  orchestrator already runs that gate; the CI gate is a second
  slice-required by the reviewer.
- `release.yml` and `Coherence.ps1`: resolve the r4249874725 step-
  isolation defect (`Update.ps1 -Project` writes to one step's
  `GITHUB_OUTPUT` and `Coherence.ps1` reads its own). The
  pre-existing modified versions of `release.yml` and
  `Coherence.ps1` are DevOps's and are not in scope for this note.

## Tester follow-up {#tester-followup}

Tester-owned regressions this slice does not write (mayTest=false):

1. **Path-authorize regression**: orchestrate with an unregistered
   `<configPath>` containing a side-effect sentinel; assert structured
   `phase=config` exit `4`, the diagnostic fragment, and the sentinel
   marker file does **not** exist on disk.
2. **Authorized vs. drifted config**: orchestrate with a `--nologo`
   fixture that canonicalizes to one of the two roster
   `configPath`s; assert `Ok ()`; test runner uses the orchestrator's
   `runChild` indirectly through `ReleaseRunner.exe orchestrate`.
3. **Child-env scrubbing regression**: invoke the orchestrator with
   `GITHUB_TOKEN=sentinel-token` and `MCP_RELEASE_DO_UPLOAD=true`
   exported, replace each Build/Pins/Tests script with a stub that
   appends its own captured environment to a temp file, run the
   orchestrator, and assert the sentinel and the `true` value are
   missing from every captured file (Config.fsx wrapper, Build, Pins,
   DistributionTests, PackagedRuntimeCoordinationTests when
   applicable).
4. **Transaction-retains-auth regression**: invoke the orchestrator
   the same way but stub the transaction subprocess to record its
   env; assert `GITHUB_TOKEN` and `MCP_RELEASE_DO_UPLOAD` are present.
5. **Explicit per-component tests regression**:
   `ReleaseOrchestration.producerScriptTests "dotnet"` equals the
   one-entry list; the same for Workflow; an extra
   `tests/ExtraTests.fsx` file does **not** cause the orchestrator to
   run it.
6. **Per-test exit propagation**: a stub second-test (Workflow) that
   exits non-zero must produce `phase="tests[2]"` and propagate the
   child's exit code; the transaction subprocess must never have
   spawned (assert via the absence of a transaction output file).
7. **Direct `validateConfigPathAgainstRoster` regression**: each
   valid roster path returns `Ok ()`; an external or drifted path
   returns the documented `MalformedArtifact`.
8. **No dynamic discovery regression**: assert
   `producerScriptTests` does not call `Directory.EnumerateFiles` or
   `Directory.GetFiles` against any producer `tests/` directory
   (literal source-only assertion, since the property is a policy on
   the orchestrator's source text).

Suggested negative assertions and test seams are written from the
production seams: every test references the production function it
exercises (`ReleaseEnrollment.validateConfigPathAgainstRoster`,
`ReleaseCapture.runProcessCapture`, `ReleaseOrchestration.orchestrate`,
`ReleaseOrchestration.producerScriptTests`) so a future refactor that
moves the contract cannot silently drop coverage.

## W6.b gap fix — `validateConfigPathAgainstRoster` exact-one and canonical-path uniqueness

Tester evidence (read-only) flagged a bug in the W6 first-pass
implementation of `ReleaseEnrollment.validateConfigPathAgainstRoster`
and the surrounding roster invariants:

- The original implementation returned `Ok ()` on the **first**
  matched entry (boolean-ANY match), so two different roster entries
  that resolve to the same canonical `configPath` (e.g. one literal
  `./dotnet/release/Config.fsx` and one normalized
  `dotnet/release/Config.fsx`, or two entries that happen to share a
  canonical path under aliasing) would silently satisfy the
  authorize-before-FSI gate even though the CLI path could mask
  which component it routes to.
- `validateRoster` checked duplicate `componentId`s but **not**
  duplicate canonical resolved `configPath`s, so the roster itself
  could admit an ambiguous projection that the path validator could
  then never reject.

Per the authoritative r4249874735 thread, the gate must resolve
**exactly one** enrolled configPath. The fix is bounded to
`Enrollment.fs` and does not touch orchestrator or any other module.

### Production change

`infrastructure/release/Enrollment.fs`:

1. New private helper `validateCanonicalConfigPaths root roster`
   iterates every entry, resolves each `configPath` against `root`
   via the existing `normalizePath` helper (which uses OS path
   canonicalization: `Path.GetFullPath` and `Path.Combine` with the
   `IsPathRooted` check; no aliasing beyond what the OS resolves),
   and rejects with a `MalformedArtifact` whose message names the
   colliding `componentId`, the duplicate canonical path, and the
   offending raw `configPath`. The first duplicate wins, so the
   diagnostic is deterministic across re-reads.
2. `loadRoster` now runs `parseRoster` **then**
   `validateCanonicalConfigPaths root roster` before returning the
   parsed `Roster`. A roster file that admits two entries resolving
   to the same canonical path is rejected at load time, before any
   orchestrator runner, registry, or Bootstrap call ever sees it.
   The `Result<Roster, ReleaseError>` return shape is preserved.
3. `validateConfigPathAgainstRoster` is rewritten to **count** the
   matches instead of stopping at the first. The result is a list of
   `(componentId * configPath)` for entries whose `normalizePath`
   result equals the CLI path's canonical form. The match is
   `Ok ()` only when the list has **exactly one** element. The empty
   case carries the original diagnostic plus a list of known entries
   (so the test suite can continue to assert it). The many-match
   case carries a new diagnostic that names every colliding entry
   and the resolved count. No boolean ANY-match remains.

### What is preserved

- `Path.GetFullPath` / `Path.Combine` / `Path.IsPathRooted` semantics
  are the canonical comparison, matching the existing
  `normalizePath` used by `validateProjectionAgainstRoster` and
  `validateConfigPathAgainstRoster` itself. No new normalization
  logic; no new file-system or string-comparison rules. The
  `StringComparison.Ordinal` equality test on resolved paths is
  unchanged.
- The orchestrator's three-step path
  `loadRoster` → `validateRoster` → `validateConfigPathAgainstRoster`
  remains the only authorization chain. Both validators feed off the
  same canonical form (via `normalizePath root …`), so a roster entry
  the loader accepts is the same one the path validator projects
  against; the path validator's exact-one check is the only way past
  `Ok ()`, and a `>1` collision can no longer sneak through.
- `validateRoster` keeps its existing post-load invariants
  (non-empty, no duplicate `componentId`, exact-set against
  `supportedComponentIds`). The new canonical-path uniqueness check
  is enforced in `loadRoster`, so direct callers of `validateRoster`
  that bypass the file (test fixtures, future programmatic callers)
  continue to receive the same in-memory behavior; production
  callers always enter through `loadRoster` and therefore always
  get the canonical uniqueness guarantee.
- `Enrollment.supportedComponentIds` is unchanged
  (`{ "dotnet"; "workflow" }`); the ID-level exact-set guard
  remains the supporting constraint. Post-eval projection validation
  (`validateProjectionAgainstRoster`, `validateComponentIdInRoster`)
  is unchanged; both still run after the projection so a missing
  or swapped `componentId` in `Config.fsx` is still rejected before
  any run-time path-dependent decision is made. No FSI child
  spawns before authorization completes.

### What is not changed

- No orchestrator, runner, capture, runner-CLI, transaction,
  evaluator, or any other module change. The Orchestration.fs
  `orchestrate` function still calls
  `loadRoster` → `validateRoster` → `validateConfigPathAgainstRoster`
  → `loadProducerConfig`, in that order. Nothing about the runner
  subprocess boundary, the credential-scrub list, the test list,
  the comments policy, or the JSON contract moves.
- No new file, no compatibility alias, no parallel path validator.
  The single `ReleaseEnrollment.validateConfigPathAgainstRoster`
  function is the canonical entry point and remains the only one.
- No commented-out code, no banner comments, no narrating
  multi-line blocks in this slice. One-line WHY comments are added
  to the two new bindings (helper and rewritten validator) and
  match the OpenCode `comments.md` rule already applied in the W6
  first pass.

### Build evidence

Dedicated `dotnet_build` MCP, scoped to `Release.fsproj` only. Build
only; no tests.

| Project | runId | status | exitCode | errors | warnings | durationMs |
| --- | --- | --- | --- | ---: | ---: | ---: |
| `infrastructure/release/Release.fsproj` | `YMZoX9R9ncyvDIVGBY4FalJZL_V-KckLyFrsc9i3kHM` | failed | 1 | 4 | 0 | 8323 |
| `infrastructure/release/Release.fsproj` | `i7VA_tFaC75pPQkz8SWvEmftXsnPytpe2lqEGn0UeGo` | failed | 1 | 6 | 0 | 3330 |
| `infrastructure/release/Release.fsproj` | `huMSH48bMjdk4-cTeP38A5VFLjSHTDWNbRDGhqnZ_xo` | failed | 1 | 6 | 0 | 9630 |
| `infrastructure/release/Release.fsproj` | `RJNNPSSWeIPRW2dU-xuIl_VkiVQSKZVZffh02bDrRPo` | succeeded | 0 | 0 | 0 | 12492 |
| `infrastructure/release/Release.fsproj` | `pb-ngjximLBZ_6gjxW5lw35qxXmc4cKkVSDIOHopCxI` | succeeded | 0 | 0 | 0 | 1486 |
| `infrastructure/release/Release.fsproj` | `GuMmgtJwbqgaaGlKNs9ja3BlgxMlW9aXVwMI5eF8t3s` | succeeded | 0 | 0 | 0 | 1571 |

The first three failed builds are the iterative re-anchoring of the
helper ordering (`normalizePath` had to be moved above
`validateCanonicalConfigPaths`, and `validateCanonicalConfigPaths`
had to be moved above `loadRoster`, with the matching `match`
flatted against `Result<unit, ReleaseError>` instead of falling
through `Result<Roster, ReleaseError>`). The clean build is
`RJNNPSSWeIPRW2dU-xuIl_VkiVQSKZVZffh02bDrRPo`. Subsequent builds
are incremental confirmations on the same source.

### Tester handoff (W6.b follow-up)

The tester-owned regressions in the W6 first pass list items 1 and 7
(here and in the section above) call for explicit exact-one and
roster-uniqueness coverage. The W6.b fix makes those regressions
meaningful: an assertion of "matches exactly one" can no longer be
trivially satisfied by the boolean-ANY behavior the original
validator exhibited. Suggested tester seams (the production seam is
named in every case so a future refactor cannot drop coverage
silently):

1. `ReleaseEnrollment.loadRoster` rejects a roster file whose two
   entries' `configPath` strings normalize to the same canonical
   absolute path under OS `Path.GetFullPath` rules (e.g. one literal
   `dotnet/release/Config.fsx` and one literal
   `./dotnet/release/Config.fsx`, or two entries that point at the
   exact same file through aliasing). Assert the malformed diagnostic
   names the colliding `componentId`, the canonical path, and the
   offending raw `configPath`. Also assert that no downstream
   `orchestrate` invocation was attempted (sentinel-style file
   side-effects do not exist on disk).
2. `ReleaseEnrollment.loadRoster` still accepts the checked-in
   `infrastructure/release/Participants.json` (clean two-entry
   roster with distinct canonical paths).
3. `ReleaseEnrollment.validateConfigPathAgainstRoster "dotnet/release/Config.fsx"`
   on the checked-in roster returns `Ok ()`; the same on an
   absolute path equivalent (`/cwd/dotnet/release/Config.fsx`)
   returns `Ok ()`; an unregistered external path returns the
   documented `MalformedArtifact`; a drifted path
   (e.g. `dotnet/release/Alternate.fsx`) returns the documented
   `MalformedArtifact`. The test references the production entry
   point so a future move cannot drop coverage silently.
4. Construct a Roster with two entries whose resolved canonical
   paths collide but whose raw `configPath` strings differ;
   `validateConfigPathAgainstRoster` against a CLI path matching
   the collision returns the new "matches N enrolled entries"
   `MalformedArtifact` carrying every colliding entry's
   `(componentId, configPath)`. This is the >1 case the first-pass
   validator silently accepted.
5. `ReleaseOrchestration.orchestrate root "../../outside/Config.fsx"`
   still emits `phase=config` with exit `4` and the documented
   diagnostic, and no child `dotnet fsi` process or wrapper file is
   produced on disk. Same for the converged-collision fixture: the
   orchestrator fails closed before `loadProducerConfig`.
6. `ReleaseEnrollment.validateRoster` on a hand-built Roster
   continues to enforce non-empty, no duplicate `componentId`, and
   exact-set against `{ "dotnet"; "workflow" }`. The canonical-path
   uniqueness check sits in `loadRoster`; direct `validateRoster`
   callers (test fixtures) are unchanged in surface, so this
   regression remains valid.
7. `ReleaseEnrollment.validateConfigPathAgainstRoster` does not read
   the values of any environment variable, does not read the
   sentinel `GITHUB_TOKEN`, and does not write anything to disk;
   the regress test asserts the helper is pure (the only side
   effect is the `Ok`/`Error` return value). This protects the
   "no FSI side effects on rejection" invariant across the
   exact-one refinement.

## Risk and stop point

- The four new review threads addressed by this slice are reproduced
  end-to-end through the production `ReleaseOrchestration.orchestrate`
  composition. Public types, public values, and the JSON contract
  shape are unchanged.
- No commit, no push, no merge, no real release upload; no Telegram or
  consumer edits; the working tree remains dirty on purpose.
- AC-09 (comments policy), AC-06 (path validation boundary), and the
  reviewer-named requirement on least-privilege subprocess
  credentials are all addressed in the touched production modules;
  AC-10 (`PackagedRuntimeCoordinationTests.fsx` as a pre-upload
  gate) is addressed for the orchestrated path and pending DevOps's
  dual gate in `release.yml`.
- The W6.b gap fix closes the boolean-ANY match in the path validator
  and adds canonical-path uniqueness to the loaded roster. Both
  remain pre-FSI side-effect-free.
- No acceptance claim is made here. The tester-owned regressions
  listed above must pass before any AC item moves from FAIL to PASS.

## W6 tester full-gate execution (first built snapshot)

This records the first complete `Suite.ps1` execution after the test
project build. It is a factual failure record, not acceptance evidence
or a PASS. The checkout was dirty at HEAD
`523f625014dbf81fb788fe5c11877fc7a9626382`; the test source had no
clean commit provenance.

The engineer-owned Release build for
`infrastructure/release/tests/Tests.fsproj` was run `q4G9UAiwL5Gv_8GkHlzu9r_U0WWQzG3RF0TQolfBx9A`:
Succeeded, exit 0, zero errors and zero warnings (confirmed from
retained Dotnet details). The complete gate was invoked without a
build, after removing publication, upload, workflow-output, and test
probe variables by name without reading their values:

```powershell
$envNames = @('GITHUB_TOKEN', 'GH_TOKEN', 'MCP_RELEASE_DO_UPLOAD', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'MCP_RELEASE_GITHUB_OWNER', 'MCP_RELEASE_GITHUB_REPOSITORY', 'MCP_RELEASE_TEST_PROBE_DIR', 'MCP_RELEASE_TEST_TRANSACTION_PROBE_FILE'); foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }; & pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap; exit $LASTEXITCODE
```

Expecto enumerated and ran all **33** compiled release cases:
**28 passed, 0 ignored, 4 failed, 1 errored** (suite exit 3). The
Suite gate exited 1. Its retained nested summaries included bootstrap
9/9, evaluate 70/70, historical bootstrap 10/10, invocation 10/10,
metadata 52/52, publish 62/62, revision 22/22, producer PE integration
2/2, runner 22/22, and Telegram isolation 22/22. The subsequent
`dotnet/tests/ProvenanceTests.fsx` gate passed. No test was skipped.

Bounded causes from the failure output and source inspection:

- One Enrollment assertion expected the older post-evaluation
  `configPath drift` wording. The production path-only guard correctly
  rejected the fixture earlier with `not registered`.
- Three Coherence regressions used a synthetic workflow fixture that
  omitted the `PROJECTED_COMPONENTS` environment binding and the
  explicit `Coherence.ps1 -Components` invocation required by the
  current guard.
- The orchestration host inherited the transaction-probe variable and
  dispatched to its transaction probe before its orchestration mode;
  the child therefore exited before creating the sequence marker.

The unregistered-path side-effect sentinel and the Workflow second-test
failure case were not among the four failed cases or the errored case;
they are included in the 28 aggregate passes. The latter exercises the
actual subprocess credential probes for both Workflow test scripts and
asserts that a failing second gate prevents the transaction subprocess.
The successful producer-list/transaction credential-retention test
errored before the transaction child could run, so the positive
transaction-auth probe and copied test apphost execution are still
unverified.

After this run, test-only corrections were made in
`Enrollment.fs`, `Coherence.fs`, and `Main.fs` for those three bounded
causes. They are **not included in the built assembly or this run** and
remain pending an engineer-owned rebuild and a repeat of the complete
gate. No production source was changed. The successful transaction-child
path and transaction credential retention remain unverified. Full producer
packaging from clean, canonical provenance remains pending a later
approved checkpoint. Independent security review remains blocked on
passing rebuilt evidence; this section is not an approval.

## W6.b engineer rebuild after tester bounded fixes

The tester subsequently repaired the three bounded test-side causes
(`Enrollment` early-not-registered assertion, `Coherence` fixture
PROJECTED_COMPONENTS / `-Components` binding, `Main` test-host
dispatch order) inside
`tests/Enrollment.fs`, `tests/Coherence.fs`, `tests/Main.fs`. No
production source was changed by the tester; the running `git diff`
confirms only test files and the parallel `Verification.md` update.

Dedicated `dotnet_build` MCP, scoped to
`infrastructure/release/tests/Tests.fsproj` with `Release`
configuration. Engineer slice, `mayEdit=false`, `mayBuild=true`,
`mayTest=false`. One call only — no retry, no `dotnet_build` against
any production project, no raw `dotnet build` fallback.

| Project | runId | status | exitCode | errors | warnings | durationMs |
| --- | --- | --- | --- | ---: | ---: | ---: |
| `infrastructure/release/tests/Tests.fsproj` | `V1MB-_0Oj5v7VI3q48BxsZ_YzThZA1Abe7Xn3Fbiimg` | succeeded | 0 | 0 | 0 | 17842 |

`dotnet_details` cross-check on the same runId: 0 errors, 0 warnings
(empty `errors` and `warnings` lists, both `hasMore=false`). The
test project compiles clean against the W6 / W6.b production source
unchanged from the prior engineer slice.

### What this slice does not verify

Per the assignment: **no test execution was performed in this
slice.** The Expecto runner was not invoked, `Suite.ps1` was not
re-run, no JSON output was parsed. The previous "first built
snapshot" section above remains the only Suite-run evidence in this
file until a tester-owned rerun produces a fresh record. Per the
W6.b assignment: actual positive transaction-auth retention and
copied test-apphost execution await that rerun — they are explicitly
**not** verified by this rebuild alone. No PASS, no independent
review readiness, no acceptance criterion movement.

### What is preserved

- The prior failed-gate evidence in the section above (33 cases,
  28 passed, 4 failed, 1 errored; bounded causes enumerated; causes
  repaired by the tester in this slice) is left verbatim. The
  repair-and-rebuild split is recorded here so future readers can
  distinguish the original failure row from the rebuilt one.
- The test-side fixes are in the tree but the Suite gate was **not**
  rerun by this engineer slice. The engineer's role is bounded to
  producing a clean build evidence row; rerunning the gate belongs
  to the next tester slice (it requires `mayTest=true` plus the
  upload / credential / probe environment variable scrub that the
  failed-gate record already enumerated).
- No `dotnet_build` was issued against `Release.fsproj`,
  `dotnet/Mcp.Dotnet.fsproj`, or `workflow/Mcp.Workflow.fsproj`.
  The test project transitively re-pulled the latest `Release.dll`
  via its project reference; that reference is automatic per
  `Release.fsproj`'s MSBuild output and did not require a
  separate engineering call.
- `docs/GHI-24/review/New.md` is updated here only to record this
  engineering-build row. It is **not** updated with Suite-run or
  transaction-auth-retention results; those are the tester's when
  they rerun.

## W6.b tester full-gate rerun (after bounded test fixes)

The tester reran the complete gate with the engineer-built assembly,
without building and without filtering or skipping cases. Publication,
upload, workflow-output, and probe environment variables were removed
from the launch process by name without reading their values. The
supplied run ID had a trailing `0` and was unknown to the details tool;
the engineer-recorded run ID without that trailing character,
`V1MB-_0Oj5v7VI3q48BxsZ_YzThZA1Abe7Xn3Fbiimg`, returned empty error
and warning lists with `hasMore=false`.

```powershell
$envNames = @('GITHUB_TOKEN', 'GH_TOKEN', 'MCP_RELEASE_DO_UPLOAD', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'MCP_RELEASE_GITHUB_OWNER', 'MCP_RELEASE_GITHUB_REPOSITORY', 'MCP_RELEASE_TEST_PROBE_DIR', 'MCP_RELEASE_TEST_TRANSACTION_PROBE_FILE'); foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }; & pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap; exit $LASTEXITCODE
```

The script inventory matched execution: **33 run, 32 passed, 0
ignored, 1 failed, 0 errored** (compiled suite exit 1; Suite exit 1).
The historical bootstrap fixture assertions passed 10/10, and
`dotnet/tests/ProvenanceTests.fsx` passed. Nested summaries also passed:
bootstrap 9/9, evaluate 70/70, invocation 10/10, metadata 52/52,
publish 62/62, revision 22/22, producer PE integration 2/2, runner
22/22, and Telegram isolation 22/22. Suite completed in approximately
4 minutes 9 seconds. No case was ignored or filtered.

The remaining failure was
`Coherence.the production Coherence.ps1 accepts roster wiring and
rejects marker drift`: its synthetic workflow fixture passed
`components=["dotnet","workflow"]` to `-Components`, but the production
script parses that parameter as JSON, not as an Update.ps1 output line.
The same prefix remained in two related fixture inputs. After the run,
the tester changed those three test-only values to the actual JSON
arrays (`["dotnet","workflow"]` / `["dotnet","telegram"]`). This
fixture correction is not in the built assembly or the run above; an
engineer-owned rebuild and full rerun remain required.

The unregistered-config side-effect sentinel, Workflow second-package
test failure gate, and producer success/transaction-boundary test were
among the 32 aggregate passes. The Workflow failure case observed both
test subprocesses without the controlled token/upload flags and did
not spawn the transaction child. The successful producer cases
executed the copied test apphost as the transaction subprocess probe:
the child recorded only boolean observations, confirming credentials
absent from config/Build/Pins/tests and present in the transaction
child. This is a test-host probe at the production subprocess boundary,
not a real publishing transaction.

Historical assertions used the supplied local fixture. HTTP behavior
was exercised through test-controlled/simulated handlers and child
processes were local; no remote GitHub observation, package upload, or
publication occurred. The `DistributionTests.fsx` producer packaging
gates were not run because the dirty tree lacks approved clean-source
provenance. Security review remains blocked on the rebuilt full gate;
this record does not claim acceptance or approval.

## W6.b tester gate attempt after Coherence JSON fixture build

Engineer build `MeY3nNWUjrgU_IBVbXzeMuQdfr1ZFiiWXvCLkU0SPBE` is
confirmed with zero errors and zero warnings. The full `Suite.ps1`
command above was repeated without a build; publication, upload,
workflow-output, and probe variables were removed by name without
reading values. The command had a 300-second foreground timeout and
timed out after emitting only the `Release.Tests (complete compiled
suite)` group header. Suite had advanced into the full-suite stage,
but returned no Expecto summary, actual run count, inventory comparison,
provenance result, or final exit code. Therefore this is an incomplete
attempt, not a test result. The previous completed 33-case run
(32 passed, 1 Coherence fixture failure) remains the latest countable
evidence; the raw-JSON fixture correction is not yet verified by a
completed gate. No new source changes followed this build. No
acceptance or review approval is claimed.

## W6.b timeout diagnosis (blocked; no rerun)

The Suite implementation runs `dotnet Release.Tests.dll --list-tests`,
checks the nonempty inventory and Historical case, writes the compiled
suite group header, then invokes
`dotnet Release.Tests.dll --sequenced --no-spinner 2>&1`. PowerShell
captures the complete command output and prints it only after the child
returns. Therefore the header establishes entry into the compiled
Expecto invocation after inventory preflight, but does not identify an
individual test or expose incremental child output.

At the post-timeout process snapshot, no active `dotnet.exe` compiled
test process was visible; listed Dotnet processes had older creation
times. The name/parent/start-time-only tree included `pwsh.exe` PID
15528 (parent PID 24904, created 19:33:15), with `claude.exe` PID
27804 (child, 19:33:27), then `cmd.exe` PID 27036 (child, 19:33:40).
That tree cannot be safely attributed to the Suite/test child rather
than the tool execution host using the permitted evidence. No process
was killed. It is unresolved whether the test child was terminated by
the tool timeout or ended while a wrapper remained.

**Diagnosis: blocked.** The exact wait point inside Expecto and the
cause of the duration beyond 300 seconds remain unknown; deadlock,
stalled child, and resource contention remain unproven hypotheses. Do
not start a duplicate full suite. Required next step: have the process
owner identify the PID tree and confirm no Suite/Expecto child remains;
then run only the corrected Coherence case with the existing Expecto
filter and a bounded timeout while retaining its output. Consider a
full run only after that experiment establishes behavior. No source
change or acceptance claim follows from this diagnosis.

## W6.b bounded timeout falsification and live full Expecto run

The corrected isolated Coherence test passed **1/1** in 8.38 seconds;
the complete `cross-stage coherence` test-list filter passed **4/4** in
14.83 seconds. Expecto help confirmed `--filter-test-list` is the list
filter. An initial `--filter-test-case 'cross-stage coherence'`
experiment ran **0** cases; it is recorded as a filter-selection
no-op, not test evidence or a skipped case.

After the target and list passed, the one permitted full run used the
live compiled Expecto entrypoint without a filter, with the approved
historical root and credential/upload/workflow-output/probe names
removed by name without reading values:

```powershell
$envNames = @('GITHUB_TOKEN', 'GH_TOKEN', 'MCP_RELEASE_DO_UPLOAD', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'MCP_RELEASE_GITHUB_OWNER', 'MCP_RELEASE_GITHUB_REPOSITORY', 'MCP_RELEASE_TEST_PROBE_DIR', 'MCP_RELEASE_TEST_TRANSACTION_PROBE_FILE'); foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }; $env:MCP_RELEASE_HISTORICAL_ROOT = 'C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap'; & dotnet infrastructure/release/tests/bin/Release/net11.0/Release.Tests.dll --sequenced --no-spinner; $testCode = $LASTEXITCODE; if ($testCode -ne 0) { exit $testCode }; & dotnet fsi --nologo dotnet/tests/ProvenanceTests.fsx; exit $LASTEXITCODE
```

The list-only inventory contained **33** cases, including Historical.
The unfiltered run completed in 4:22.1691527: **33 run, 33 passed, 0
ignored, 0 failed, 0 errored**, exit 0. The historical fixture nested
assertions passed 10/10 and `ProvenanceTests.fsx` passed (exit 0). The
credential-boundary test passed: local pre-transaction child probes
recorded only booleans showing the controlled token/upload flags absent
before transaction and present in the transaction subprocess; the copied
test apphost executed as that probe. This is a local test-host subprocess
probe, not a real publishing transaction. HTTP coverage used controlled
test handlers; no remote observation or upload occurred.

This experiment falsifies a hang specific to the corrected Coherence
case/group and shows the complete Expecto suite can finish under the
bounded window with direct output. It does **not** establish why the
earlier `Suite.ps1` wrapper exceeded 300 seconds: that wrapper buffers
the Expecto child output until exit, and the post-timeout process tree
could not be attributed safely. The exact wrapper timeout remains
unresolved; no retry was made. No source was edited. The Suite wrapper
itself is not claimed passed, clean-provenance producer packaging
remains pending, and this is not acceptance or independent-review
approval.

## W6.b exact Suite.ps1 wrapper verification

With the same approved Historical root and current built assembly, the
exact `Suite.ps1` wrapper was run once with a 600-second foreground
budget. Credential, upload, workflow-output, and test-probe environment
names were removed by name without reading values:

```powershell
$envNames = @('GITHUB_TOKEN', 'GH_TOKEN', 'MCP_RELEASE_DO_UPLOAD', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'MCP_RELEASE_GITHUB_OWNER', 'MCP_RELEASE_GITHUB_REPOSITORY', 'MCP_RELEASE_TEST_PROBE_DIR', 'MCP_RELEASE_TEST_TRANSACTION_PROBE_FILE'); foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }; & pwsh -NoProfile -File infrastructure/release/Suite.ps1 -HistoricalRoot C:/Users/andre/AppData/Local/Temp/opencode/ghi-24-bootstrap; exit $LASTEXITCODE
```

The wrapper returned exit 0 and reported: `release regression gate
passed: 33 compiled case(s) (including 1 historical) plus 1 producer
script(s)`. Expecto completed in 4:17.0979069: **33 run, 33 passed,
0 ignored, 0 failed, 0 errored**; the inventory matched. Historical
assertions passed 10/10 and the provenance entrypoint passed. The
controlled credential child probes and copied apphost transaction-boundary
probe passed; this remains local probe evidence, not a production
transaction or upload. No remote GitHub observation or upload occurred.

This wrapper success verifies the current release regression gate. It
does not prove the cause of the earlier 300-second timeout or that
output buffering alone caused it. Clean-source producer packaging and
independent security review remain pending; no acceptance decision is
recorded here.
