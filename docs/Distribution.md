# Distribution decision — GHI-46

**Decision status: not ready for architecture approval.** Keep the existing
ZIP/manifest/Resolver working while completing the comparison below. Retention
has lower demonstrated migration exposure, not a demonstrated steady-state
maintenance advantage. Keeping today's runtime unchanged is not approval to
complete or extend its distribution architecture.

This documentation-only proposal implements the research deliverable of
[Store #46](https://github.com/masterlifting/mcp-store/issues/46). It authorizes
no production change, publication, active-runtime configuration or cutover.
Keep exactly one supported distribution path. Store #60 (Windows test PATH
isolation) is separate and does not select the distribution format.

## Evidence and reconciliation

On 2026-11-04, GitHub `main` resolved to:

- Store: `c9d7d5f8f9b55b058b41a4deda1cd18f09426c08`.
- Harness: `f4bc4e770f7ea42e2e846890d2bb9b65fbac2cb7`.

The original research used local checkouts at those HEADs. The documentation PR
is on `ghi-46`; both GitHub `main` tips were rechecked unchanged during the review
follow-up. The coordinator checked the Store worktree was clean before creating
this task. A separate read-only auditor inspected the
current source, not just the earlier conclusions. Evidence below is static unless
explicitly stated otherwise. Paths are relative to the named repository at these
commits; line ranges are current-main anchors.

Inputs read: Store `AGENTS.md`, issue #46, closed/completed #47 and
[`docs/Audit.md`](Audit.md); Harness closed/completed #117,
[`docs/Audit.md`](https://github.com/masterlifting/harness/blob/f4bc4e770f7ea42e2e846890d2bb9b65fbac2cb7/docs/Audit.md),
`docs/Architecture.md`, and decision-dependent Store #49 / Harness #120 / #116.
Both audit issues returned no comments. Their merged reports are the accepted
research inputs, not implementation approvals. Only their distribution scope is
reconciled here; unrelated Harness findings are not re-audited.

| Finding | Current source evidence | Result / decision consequence |
| --- | --- | --- |
| Actual producer format | Store `dotnet/Dotnet.fsproj:3,6`, `workflow/Workflow.fsproj:3,6`, both `release/Config.json:5-19` | Framework-dependent `net11.0` executables; four runtime payload files, `win-x64` release selection. Not tool packages. Unix qualification in `.github/workflows/unix.yml:31-37,99-125` is not Unix release publication. |
| Release no-op is deliberate | Store `infrastructure/release/Compare.fs:437-498`, `Evaluate.fs:586-599`; `docs/release/Requirements.md:56-58,90-91,105,117-119` | Both archives are validated before digest short-circuit. Only revision-only manifest substitution may be ignored; changed product bytes at the same version fail. Published revision/hashes survive a no-op. A single archive digest cannot replace this policy. |
| Exact content identity | Store `infrastructure/resolver/Transport.fs:55-65,197-200`, `Payload.fs:40-43,57-105,115,126-130`, `Identity.fs:8-9` | Fixed source shape, archive and manifest digest pins, exact member inventory and member hashes, pin-to-manifest binding. Cache identity hashes the canonical parsed pin, not JSON whitespace/order. |
| Trust is not a hash alone | Store `infrastructure/resolver/Transport.fs:18-22,34-41,116-118`; Harness `core/workflow/pin.json`, `domains/software/dotnet/pin.json` | Allowed HTTPS origins and reviewed consumer pins form the trust chain. Neither per-file hashes nor NuGet signatures by themselves prove the intended publisher/source selection. Independent consumer selections are required, not duplicate release authority. |
| Bootstrap is unfinished | Store `infrastructure/release/Participants.json:2-5`, `.github/workflows/release.yml:201-233`; Harness `integrations/opencode/runtime/mcp/Start.ps1:70-86`, `integrations/opencode/README.md:387-409` | Resolver is built/tested but not a release participant. Harness requires operator-provisioned absolute Resolver/host paths. Store #49 then Harness #120 are required retention costs, not optional polish. No checkout-independent equivalent was verified. |
| Filesystem and concurrency have real owners | Store `infrastructure/resolver/Disk.fs:12-14,34-72,162-222`, `Kind.fs:117-232`, `Lock.fs:79-127`, `Install.fs:76-161,241-260` | Bounded link-aware admission, exclusive coordination, same-volume staging publication, shared process lease; corrupt installs stop rather than silently overwrite. These are substantial custom maintenance, not guarantees supplied by SHA-256. |
| Input checks are complementary | Harness `Start.ps1:28-44,113-129` under the runtime MCP directory; Store `infrastructure/resolver/Input.fs:26-67`, `dotnet/runtime/Invocation.fs:65-98`, `workflow/protocol/Mcp.fs:1001-1016` | Runtime confinement, storage-root exclusion, host/workspace constraints and catalog-content checks protect different boundaries. Later validation cannot replace safety checks before executing a bootstrap. |
| MCP launch is explicit | Harness `integrations/opencode/runtime/mcp/Start.ps1:91-95,134`; Store `infrastructure/resolver/Child.fs:33-80`, `Run.fs:49-93`, `Main.fs:32-38` | Absolute host and argument vector, inherited stdio, lease through child exit, exit/signal propagation. Static call-chain evidence, not a new observed MCP session. |
| Pin parsing overlaps, but binding does not | Harness `core/mcp/Pin.fsx:37-79`, `agents/Installer.fsx:785-787`, `infrastructure/Validator.fsx:503-505` under `integrations/opencode/`; Store `infrastructure/distribution/Pin.fs:76-232` | Consumer checks bind descriptor to component and canonical release location. Discarding the parsed record does not make validation useless. Entire 84-line module deletion is not justified; the overlapping parsing block is only about 37 lines. |

The Store audit's release-policy citation is baseline-relative and one cited
line does not support the same-version rejection clause. The current anchors in
the table correct the evidence trail without changing that true conclusion.
Small projection-construction duplication in Store
`infrastructure/release/Transaction.fs:25-41,207-234` and field comparisons in
`Compare.fs:297-371` remain factoring hypotheses, not demonstrated savings.

The auditor reproduced 32 immediate `.fs` modules across distribution (3),
Resolver (18), and release (11), and 1,084 nonblank distribution lines. It also
reproduced 2,296 physical Resolver lines. The prior Store audit reports 6,434
nonblank lines overall; the Resolver/release nonblank subtotals were not
independently recounted here. None of these inventories estimates removable code
or equivalent maintenance effort.

### Review follow-up: Resolver publication is a contract change

PR #61's [cost comment](https://github.com/masterlifting/mcp-store/pull/61#discussion_r4470117414)
and [bootstrap comment](https://github.com/masterlifting/mcp-store/pull/61#discussion_r4470117421)
are accepted. A separate read-only F# architect traced the actual publication
path. The earlier proposal to add a Resolver config/roster entry is insufficient:

- `infrastructure/release/Enrollment.fs:35-37,176-206` and
  `Roster.ps1:5,59-69` under the same directory require exactly Dotnet and Workflow.
- `infrastructure/release/Policy.fs:116-178`, its pre-SDK mirror
  `Update.ps1:65-94`, and `infrastructure/distribution/Contract.fs:282-301`
  require typed flag/value arguments. `Compare.fs:53-71` under release and
  `infrastructure/Distribution.fsx:191-209` independently fix `dotnet-exec`.
- Resolver requires positional `resolve` or `run`
  (`infrastructure/resolver/Cli.fs:83-122`); current launch composition emits only
  declared flag/value pairs (`Launch.fs:36-73`). Fake arguments or just allowing
  an empty list do not express a usable Resolver launch contract. The issue is
  the descriptor semantics, not inability to execute Resolver with `dotnet exec`.
- Release preparation assumes `<componentId>/release/...` and
  `<componentId>/dist` (`Orchestration.fs:236-264,289-329` under release).
  Resolver lives under infrastructure. CI also has component-specific transfers
  and output/guard counts (`.github/workflows/release.yml:383-478,521-569,642-662`;
  `infrastructure/release/Check.ps1:130,239-248`).

**Smallest defensible path identified, not a proved minimal patch:** publish a
CLI artifact through the existing ZIP/manifest transaction, with one explicit
CLI-only descriptor, for example `dotnet-cli`. It identifies the verified DLL and
runtime prerequisite, not MCP startup arguments. The trusted Harness bootstrap
verifies its complete closure before invoking its documented CLI directly.
Resolver must reject CLI-only artifacts as MCP `run` targets. Do not add literal,
optional or repeated argument templates, arbitrary passthrough, a generic command
engine, a root-level producer facade, or recursive Resolver self-installation.

This proposal changes the distribution schema, canonical policy readers, both
manifest projections, participant enrollment, nested-source-root handling and CI
transfers. The file-specific conditional plan below prices those changes rather
than hiding them behind #49. It is one mechanism with a concrete additional
artifact type, not a second publisher. Replacing only Resolver packaging with a
tool would leave the producer ZIP stack intact and is not the replacement option
being evaluated.

The candidate Resolver runtime closure is `Resolver.dll`, `Resolver.deps.json`,
`Resolver.runtimeconfig.json`, and `FSharp.Core.dll`; the shared builder adds
`NOTICE.txt` and `distribution.json`. The project and existing local deps output
show the implicit FSharp.Core dependency. That local output is **not release
provenance**. The shared publish recipe suppresses satellites/apphost, but no fresh
Resolver publish verified this four-file closure. The present README's three-file
bootstrap list is therefore insufficient evidence. The absolute .NET host/runtime
is another prerequisite, not part of this closure.

## Alternatives: complete lifecycle

The standard-tool capabilities below are documented in the accepted audits and
their Microsoft references, not newly executed experiments. In particular, exact
tool versions and custom absolute tool paths exist; tools do not require global
PATH registration. A version alone is not the current independently pinned
content digest. NuGet source mapping, signatures and trusted signers are real
controls, not evidence of automatic equivalence to the present contract.

| Responsibility | Retain | Simplify existing | Replace: .NET tool / NuGet | Replace: executable |
| --- | --- | --- | --- | --- |
| Build and publication | Existing transaction/no-op policy; Resolver adds CLI descriptor/schema, roster, source-root, CI transfer and package work | Same bootstrap obligations unless a specific responsibility can safely disappear | Two tool packages, feed ownership/configuration, package publish and release-policy integration | Per-RID apphost or self-contained publish; immutable artifact publication still needed |
| Provenance, integrity, immutable versions, exact pins | Reviewed independent pins plus trusted origin and archive/member verification | Preserve these; no demonstrated field can be removed | Exact version and standard package integrity/signing available; still decide publisher trust, feed exclusivity, independently pinned digest and cache validation | Signing optional; source/digest pins and immutable selection still custom |
| Bootstrap | Trusted .NET host/runtime plus published Resolver; #49 → #120 missing | Same unresolved bootstrap | SDK for install/restore plus runtime for framework-dependent tools; SDK/feed/certificate provisioning replaces Resolver bootstrap, not all bootstrap | Self-contained removes shared runtime prerequisite but adds payload size, RID and runtime servicing; framework-dependent executable does not |
| Installation, cache, concurrency | Existing staging, canonical cache, locks, leases, revalidation | Small local factoring does not eliminate owners | Standard restore/extraction/tool stores can replace download/extract/cache plumbing; exact isolated tool-path writer behavior, installed-tree trust and in-use cleanup equivalence remain unverified | Executable format provides none of acquisition/staging/cache/coordination; must retain or acquire an installer owner |
| MCP stdio and failure | Existing absolute launch; diagnostics on stderr, corrupt install refusal | Preserve rejection timing and pre-execution checks | Absolute shim can launch; prove clean stdout, workspace/inputs, cancellation, exit propagation and missing/corrupt failure | Direct launch removes Resolver hop only; same MCP/process/input obligations |
| Updates and cleanup | New exact pin, immutable side-by-side install, no automatic repair/GC; documented operator recovery | Do not add GC or repair merely as cleanup | Exact update/uninstall commands exist; preserve active processes, user files and rollback/recovery boundaries; shared cache deletion is not safe owned cleanup by definition | Same side-by-side/cutover/owned-cleanup duties unless an external installer supplies them |
| Transition / ongoing exposure | Shared contract/CI changes plus producer-version and bootstrap/consumer delivery; retain substantial custom maintenance | No material Store-side deletion demonstrated; bootstrap cost unchanged | Potentially largest removal, but new SDK/feed/trust integration, release-policy adaptation, both producer packages, consumer transition and retirement | New publish/launch integration and servicing; much of current lifecycle survives |

### What standard tooling actually removes

- **Tool/NuGet:** standard package construction, restore transport, package
  extraction, ordinary package-cache coordination and shim generation can replace
  corresponding custom plumbing. This is the strongest replacement candidate.
  It does not prove the whole Resolver or release directory deletable: content
  admission, current installed-tree guarantees, typed producer inputs and the
  explicit cross-revision no-op policy must either have equivalent owners or an
  explicitly approved contract change. A digest wrapper retaining most of those
  duties could erase the expected net saving.
- **Executable:** apphost launch removes `dotnet exec` composition; self-contained
  publication additionally removes shared-runtime provisioning. Neither provides
  trusted acquisition, immutable pins, staging, leases, failure recovery or GC.
- **Both:** consumer MCP registration/permissions, reviewed selections, source
  trust, release provenance, user-data preservation and authorized activation
  remain. Standard tools do not eliminate these responsibilities merely by
  moving them into feed or host configuration.

For a full producer-tool replacement, the concrete Store deletion/adaptation
candidates are as follows. These are conditional responsibility mappings, **not
an implemented or measured deletion estimate**:

| Current files under `infrastructure/` | Standard-tool opportunity | Duties not discharged by tool packaging alone |
| --- | --- | --- |
| `resolver/Transport.fs`, `Payload.fs`, `Install.fs`, `Disk.fs`, `Storage.fs`, `Lock.fs` | Transfer ordinary package acquisition/extraction/cache mechanics to NuGet/tool stores | Trusted exact bytes before execution, installed-tree tamper handling, safe paths, concurrent writers and running-version preservation; assess any required wrapper before counting deletion |
| `resolver/Launch.fs`, `Child.fs`, `Run.fs`, CLI/report modules | Absolute tool shim may replace Resolver invocation/launch composition | Workspace/host/typed inputs, stdio, process lifetime and failures; no proof the whole directory can disappear |
| `Distribution.fsx`, `distribution/Archive.fs`, producer `release/Build.fsx` and `Pin.fsx` | Tool packing replaces ZIP layout/normalization and current pin projection | Exact independently reviewed package identity, dependency closure and trusted provenance still need an owner |
| `distribution/Contract.fs`, `Pin.fs`, release `Compare.fs`, `Handoff.fs`, `Evaluate.fs`, `Transaction.fs`, `Publish.fs` | ZIP-specific parser/comparison/forms could retire or change | Immutable publication, trusted candidate validation, cross-revision no-op, same-version rejection, publication ambiguity/recovery do not disappear |
| release enrollment, toolchain/CI controls and associated tests | Adapt to the one selected packaging mechanism | Credential separation, trusted source/event revision, package tests, corruption/failure coverage and consumer activation remain |

### Recommendation and net cost

**Withhold final retain/simplify/replace approval.** The first draft recommended
retention as the lowest demonstrated “maintenance/transition” cost. PR #61 review
correctly challenged that combined claim: source inspection supports continuity
and less migration exposure, but does not establish lower long-term complexity.
Absence of a verified replacement saving is not proof that retention is cheaper.

Separate the two cost horizons:

- **Transition:** retention keeps the existing producer/consumer launch contract,
  but still needs a valid trusted Resolver publication path and Harness acquisition,
  verification, repair and materialization. Replacement needs package definitions,
  feed/trust configuration, release-policy adaptation, consumer migration and
  retirement. No equivalent-effort or implementation-hours estimate exists;
  #49 cannot be priced as just a config/roster addition.
- **Steady state:** retention continues the measured custom distribution,
  Resolver and release surface, plus bootstrap maintenance. Tools could transfer
  package construction, acquisition, extraction, ordinary package-cache handling
  and shim generation to maintained standard tooling. The retained exact-content,
  installed-tree, release-no-op and consumer safety duties must be mapped to real
  owners before counting net deletion. No prototype or equivalent replacement
  implementation establishes that net cost yet. Raw existing LOC is exposure,
  not a prediction of replacement LOC or ongoing engineering hours.

No worthwhile Store-side “simplify” deletion has been demonstrated. Executable
format alone leaves the installation lifecycle substantially intact. Tools remain
the strongest replacement candidate, not a selected replacement. Continue the
existing mechanism operationally while resolving this evidence gap; do not treat
that safety default as the final simplest-architecture verdict.

The source investigation now establishes a technically plausible retention path,
but also expands its known transition surface to shared schema/CI and producer
version changes. It does not settle which option has lower steady-state cost.
**Recommended next decision: approve only the single offline tool experiment
specified below, not retention implementation.** Use its actual wrapper/packaging
inventory to decide retain or replace; choose simplify only if it demonstrates a
worthwhile removable responsibility within the existing mechanism. No experiment
has been approved or performed.

Future non-.NET producers need trusted immutable artifacts, exact selection and
reliable processes, not necessarily this .NET launch descriptor. Telegram already
has its own Python package boundary. Do not enroll it in this decision or add
generic language adapters. Revisit delivery only for an actual producer need.

## Ordered implementation plan — approval required

The next action is the separately approved experiment below. Steps 2–6 are a
**conditional retention plan**, not instructions to implement before the decision.
No row has been implemented. Exact artifact bytes, checksums and versions must
come from an actual verified release, never invented in this document.

1. **Finish the selection gate (Store #46).** Review the bootstrap findings and
   approve/reject the one experiment. Compare observed added wrapper/packaging
   work against the conditional deletion inventory. Then select retain, simplify
   or replace explicitly; do not equate migration risk with maintenance cost.
   Preserve content pins, source trust, no-op policy, fail-closed admission and
   one delivery path unless an explicit contract amendment is authorized. This
   documentation PR does not close #46 or the dependent issues.
2. **If retention is selected, publish Resolver (Store #49).** Approve the
   following coordinated change surface before implementation. All paths in this
   table are Store-relative; parenthesized directory prefixes apply to every file
   in that cell.

   | Files | Required responsibility/change |
   | --- | --- |
   | `infrastructure/distribution/Contract.fs`, its README and contract tests | Advance the strict manifest schema for an explicit CLI-only publication descriptor; retain typed MCP descriptors and integrity/path constraints. No legacy reader or generic command grammar. |
   | (`infrastructure/release/`) `Policy.fs`, `Update.ps1`, `Compare.fs`; `infrastructure/Distribution.fsx` | Add policy-owned kind and distinct argument validation in both readers; derive both trusted comparison and emitted manifest from it. Preserve strict fields and the release no-op policy. |
   | `dotnet/release/Config.json`, `workflow/release/Config.json` and their `Build.fsx` entrypoints | Declare unchanged MCP launch semantics under the new policy/schema. Bump producer release versions for changed manifest bytes; do not overwrite old assets or label schema changes revision-only no-ops. |
   | (`infrastructure/release/`) `Participants.json`, `Enrollment.fs`, `Roster.ps1` | Explicitly admit Resolver as a third release artifact, not as an MCP server; replace the exact-two participant restriction without introducing open-ended enrollment. |
   | (`infrastructure/release/`) `Orchestration.fs`, `Update.ps1`; `.github/workflows/release.yml`, `.github/workflows/unix.yml` | Derive source roots from canonical config paths, as `Typecheck.ps1` already does; keep identity separate from nested filesystem location. Wire the actual Resolver candidate outputs/transfers and tests. |
   | `infrastructure/release/Check.ps1` and release workflow | Extend pinned transfers, artifact-ID guards and graph/coherence checks together; preserve credential separation and data-only trusted publication. |
   | Proposed `infrastructure/resolver/release/{Config.json,Build.fsx,Pin.fsx}`; `Resolver.fsproj` under resolver if needed; proposed `infrastructure/resolver/tests/distribution/Run.fsx` | Produce deterministic ZIP/metadata through the shared builder and publish transaction, verify exact dependency closure and exercise the packaged CLI. Generated output stays under Resolver's `dist/`, not a new root-level producer facade. |
   | `infrastructure/resolver/Run.fs`, applicable `Report.fs` handling and resolver tests | Refuse CLI-only publications as MCP `run` targets before child execution, releasing leases on failure. Define any `resolve` snapshot effect explicitly rather than silently changing its contract. |
   | (`infrastructure/release/tests/`) `Policy.fs`, `Enrollment.fs`, `Coherence.fs`, `Contract.fs` and affected fixture/preparation/handoff helpers | Cover CLI/MCP distinction, nested roots, candidate transfers, immutable publish/no-op/bump-fail and invalid/missing packaged closure. Complete a call-site audit; this table is a mandatory responsibility inventory, not an exhaustive compiled patch. |
   | `AGENTS.md`, root README, three infrastructure READMEs, `docs/release/Requirements.md` | Reconcile release-artifact versus MCP ownership, dependency closure, trusted bootstrap contract and evidence/acceptance matrix. |

   The pin schema is not shown to need a change merely for another component:
   existing archive/manifest digests and revision can identify it. The manifest
   schema **does** change: current `Contract.fs:14,262-263` accepts schema 2 only.
   Newly released producer payloads, the new Resolver and consumer selections
   therefore need coordinated cutover; old assets remain immutable and the old
   complete runtime stays usable until separately authorized activation. No
   schema-2 compatibility reader or dual supported distribution is proposed.
   Verify the new producer releases before selecting them with the new Resolver.
   Store supplies the artifact and published trust contract; Harness verifies it
   before first execution. Do not execute Resolver to establish its own trust.
3. **Consume one trusted selection (Harness #120, after #49).** Review/update
   `core/mcp/` with one proposed `pin.json` for Resolver, distinct from producer
   release metadata and the existing producer pins. Integrate acquisition and
   verification into `integrations/opencode/Install.ps1`, `Update.ps1`,
   `bootstrap/Bootstrap.ps1`, `agents/Installer.fsx`,
   `infrastructure/Update.fsx`, `infrastructure/Validator.fsx`, and `Assets.fsx`
   under `integrations/opencode/` as required by their existing ownership.
   Reuse their install/update/receipt/recovery transaction; acquisition and the
   pre-execution closure verifier are still new obligations, not guarantees of
   that transaction. Specify them and the owned artifact destination in #120's
   exact approval plan. Do not copy Store code, add a second package resolver, or defer
   verification until after running downloaded code. Keep producer payload cache
   ownership in Resolver. Extend `tests/integrations/opencode/bootstrap/BootstrapTests.fsx`
   and `tests/integrations/opencode/Delivery.fsx` only for enrollment, rejection
   and authorized repair. Unknown/manual runtime edits must block overwrite.
4. **Bind actual launch (Harness #116).** After #120, validate whether existing
   `integrations/opencode/runtime/mcp/Start.ps1`,
   `core/workflow/pin.json`, `domains/software/dotnet/pin.json` and their authored
   native bindings already satisfy the approved contract. Change only demonstrated
   gaps; otherwise verify without rewriting. Project the installed absolute
   Resolver/host into `mcp/resolver.json` through the existing materializer; never
   hand-edit active runtime configuration. Update `integrations/opencode/README.md`
   and `README.md` to remove the manual-provisioning requirement only after it is
   actually discharged. Do not include independent pin-lint deletion here.
5. **Prove delivery before accepting it (joint, #116 owns consumer evidence).**
   On an authorized disposable Windows environment without Store checkout or
   manually provisioned Resolver, perform the targeted checks below. Store first
   proves the published bootstrap closure; Harness then proves the real installed
   path. Leave #49/#120/#116 open until their own criteria are met. Decision
   approval alone cannot justify closing #46 as “no justified change.”
6. **Activate only with separate operator consent.** Protect user-owned config,
   task data and running processes using the existing reconciliation/recovery
   transaction. Do not restart/cut over the active runtime during source
   validation. Retention introduces no displaced producer mechanism to retire;
   remove obsolete manual bootstrap instructions after verified enrollment, not
   user files or retained payloads. No compatibility mode or parallel delivery.

If replacement is approved instead, stop before step 2, skip #49/#120, and replace
this plan with a verified producer-first package/publication → consumer #116 →
authorized cutover → old-mechanism retirement plan. Do not publish a Resolver
bootstrap for an already rejected architecture. Independent pin-parser factoring
requires its own value demonstration and descriptor-binding/rejection-timing plan.

## Verification and remaining decisions

Performed for this proposal: GitHub issue/main identity reads; initial local Store
Git state check; accepted-report reconciliation; independent targeted source
inspection of producer format, release policy, pins, bootstrap, admission,
concurrency and stdio; targeted F# architect follow-up of actual Resolver release
admission, policy, CLI, preparation and CI assumptions and existing dependency
metadata. Its output identifies required responsibility changes, not an exhaustive
compile-verified patch. No build, test suite, prototype, package publication,
artifact download, clean installation, MCP call or runtime cutover was performed.
Prior Sandbox PATH observations belong to the accepted audits and #60, not proof
of any distribution property tested here.

Required after plan approval, through existing targeted checks and one disposable
consumer run rather than a speculative matrix:

- Verify published Resolver source/digest and dependency closure before execution;
  ordinary launch succeeds and missing/invalid artifact fails clearly.
- Install both producers with no Store checkout, initialize real MCP stdio and
  make one representative tool call each; stdout contains protocol only.
- Select an exact newer reviewed pin, observe the intended immutable install and
  unchanged prior bytes; exercise one simultaneous install/use to check the
  bootstrap integration does not bypass existing coordination.
- Reject wrong digest/component, corrupt cached payload and missing artifact
  without fallback, overwriting unknown files or silently repairing producer data.
  Exercise missing-owned-Resolver repair only through authorized install/update.
- Check cancellation/exit propagation and cleanup of only owned disposable data;
  preserve running versions and user configuration. Record actual commands,
  release/pin identities and results, distinguishing static checks from execution.

### One proposed experiment — separate approval required

**Question:** for one actual producer (Dotnet), does standard tool installation
remove enough custom install/launch work after preserving exact-content and
installed-tree verification, or does the required wrapper recreate that work?

Use a disposable copy and one local-only, nonpublished tool package, isolated SDK
home, tool path and package cache, with an explicit local-only NuGet config and a
reviewed package digest/version. Do not contact a feed, obtain credentials,
publish packages, edit the production projects or install into an active runtime.
If required SDK/package inputs are unavailable locally, stop and request exact
download approval; do not silently expand this experiment. Suppress first-run
PATH registration in the disposable process; this is not a #60 fix or PATH probe.

Acceptance/evidence, bounded to this one producer and path:

1. Record the actual packaging changes, tool/SDK/runtime prerequisite and complete
   package identity. Install the exact verified package into a clean isolated
   tool path. Invoke its absolute shim with the real workspace/typed inputs;
   observe MCP initialization, one representative tool call and cancellation/exit
   behavior without stdout pollution.
2. Exercise one concurrent install/use and one corrupted installed-payload
   refusal/recovery scenario. Record any bespoke verification, locking, lifetime
   or launch wrapper actually needed. Missing package must fail without network
   or alternate-source fallback. No destructive cleanup outside owned scratch.
3. Deliver a file/responsibility deletion-versus-addition inventory for full
   producer replacement, separating runtime plumbing from release policy. Map
   every retained trust/content/no-op obligation to its concrete owner; an
   unmapped obligation fails equivalence rather than becoming an assumed saving.
   Compare that actual wrapper/packaging work to the conditional retention plan.

This experiment does not prove public-feed provenance, immutable hosted package
publication, Workflow behavior or a production cutover. Retain the no-op release
owner unless an equivalent is separately demonstrated; a successful tool install
is not permission to delete `Compare.fs`. Stop after the evidence report, remove
only authorized owned scratch, and make the architectural decision before any
production implementation. No permanent second distribution path is created.

Outstanding decisions: experiment approval; final retain/simplify/replace choice;
if retaining, CLI descriptor/schema and third-participant approval plus bootstrap
trust/placement and coordinated producer version changes; if replacing, equivalent
release/trust ownership and producer-first plan. Documentation publication and
runtime activation each retain their separate consent gates.
