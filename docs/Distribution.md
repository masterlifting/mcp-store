# Distribution decision — GHI-46

**Proposed decision: retain ZIP/manifest/Resolver. Approval pending.** Complete its
missing trusted Resolver delivery, rather than replacing the producer distribution
or selecting an unproven refactor. Retention is not a zero-change option and is
not end-to-end delivery acceptance.

This documentation-only proposal implements the research deliverable of
[Store #46](https://github.com/masterlifting/mcp-store/issues/46). It authorizes
no production change, publication, active-runtime configuration or cutover.
Keep exactly one supported distribution path. Store #60 (Windows test PATH
isolation) is separate and does not select the distribution format.

## Evidence and reconciliation

On 2026-11-04, GitHub `main` resolved to:

- Store: `c9d7d5f8f9b55b058b41a4deda1cd18f09426c08`.
- Harness: `f4bc4e770f7ea42e2e846890d2bb9b65fbac2cb7`.

Both local checkouts have those HEADs. The coordinator checked the Store worktree
was clean before creating this task. A separate read-only auditor inspected the
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

## Alternatives: complete lifecycle

The standard-tool capabilities below are documented in the accepted audits and
their Microsoft references, not newly executed experiments. In particular, exact
tool versions and custom absolute tool paths exist; tools do not require global
PATH registration. A version alone is not the current independently pinned
content digest. NuGet source mapping, signatures and trusted signers are real
controls, not evidence of automatic equivalence to the present contract.

| Responsibility | Retain | Simplify existing | Replace: .NET tool / NuGet | Replace: executable |
| --- | --- | --- | --- | --- |
| Build and publication | Existing prepare/publish transaction, immutable assets and no-op policy; add Resolver artifact | Same, unless a specific responsibility can safely disappear | Two tool packages, feed ownership/configuration, package publish and release-policy integration | Per-RID apphost or self-contained publish; immutable artifact publication still needed |
| Provenance, integrity, immutable versions, exact pins | Reviewed independent pins plus trusted origin and archive/member verification | Preserve these; no demonstrated field can be removed | Exact version and standard package integrity/signing available; still decide publisher trust, feed exclusivity, independently pinned digest and cache validation | Signing optional; source/digest pins and immutable selection still custom |
| Bootstrap | Trusted .NET host/runtime plus published Resolver; #49 → #120 missing | Same unresolved bootstrap | SDK for install/restore plus runtime for framework-dependent tools; SDK/feed/certificate provisioning replaces Resolver bootstrap, not all bootstrap | Self-contained removes shared runtime prerequisite but adds payload size, RID and runtime servicing; framework-dependent executable does not |
| Installation, cache, concurrency | Existing staging, canonical cache, locks, leases, revalidation | Small local factoring does not eliminate owners | Standard restore/extraction/tool stores can replace download/extract/cache plumbing; exact isolated tool-path writer behavior, installed-tree trust and in-use cleanup equivalence remain unverified | Executable format provides none of acquisition/staging/cache/coordination; must retain or acquire an installer owner |
| MCP stdio and failure | Existing absolute launch; diagnostics on stderr, corrupt install refusal | Preserve rejection timing and pre-execution checks | Absolute shim can launch; prove clean stdout, workspace/inputs, cancellation, exit propagation and missing/corrupt failure | Direct launch removes Resolver hop only; same MCP/process/input obligations |
| Updates and cleanup | New exact pin, immutable side-by-side install, no automatic repair/GC; documented operator recovery | Do not add GC or repair merely as cleanup | Exact update/uninstall commands exist; preserve active processes, user files and rollback/recovery boundaries; shared cache deletion is not safe owned cleanup by definition | Same side-by-side/cutover/owned-cleanup duties unless an external installer supplies them |
| Net transition cost | Two required bootstrap deliveries plus focused end-to-end verification; keep substantial existing maintenance | No material Store-side deletion demonstrated; bootstrap cost unchanged | Potentially largest removal, but new SDK/feed/trust integration, release-policy adaptation, both producer packages, consumer transition and retirement | New publish/launch integration and servicing; much of current lifecycle survives |

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

### Recommendation and net cost

**Retain**, including completion of trusted bootstrap. This is a decision
recommendation for approval, not merely a repetition of “retain pending #46.”
The current system already implements the unusual required content/no-op/safety
contract. No worthwhile responsibility deletion has been demonstrated for
“simplify.” Executable delivery retains most infrastructure while adding RID
servicing. Tools offer credible larger long-term deletion, but satisfying the
unchanged trust/content/no-op contract and replacing both consumer integrations
has not been demonstrated to cost less than completing #49/#120.

The comparison includes retention's extra release participant/artifact and
consumer acquisition/repair integration. Those are bounded additions to existing
owners, not free work. Replacement would add two package definitions, feed and
trust operations, SDK installation dependency, release-policy adaptation,
consumer migration and old-path retirement. These work units are not equal-effort
estimates; no measured implementation-hours or replacement LOC are available.
Given the invariant-preserving requirement, the lowest demonstrated incremental
maintenance/transition cost is retention. This does **not** establish it as a
universal optimum or prove the missing bootstrap cheaper empirically.

Future non-.NET producers need trusted immutable artifacts, exact selection and
reliable processes, not necessarily this .NET launch descriptor. Telegram already
has its own Python package boundary. Do not enroll it in this decision or add
generic language adapters. Revisit delivery only for an actual producer need.

## Ordered implementation plan — approval required

Each row names the intended owner and bounded file scope. No row below has been
implemented. Exact artifact bytes, checksums and versions must come from an actual
verified release, never invented in this document.

1. **Approve the selection and contract (Store #46).** Approve retention with
   archive/member content pins, trusted source, revision-only no-op behavior,
   fail-closed admission, one delivery path and no automatic producer-cache GC
   unchanged. Accept that delivery remains incomplete until steps 2–5. This PR
   changes only `docs/Distribution.md`; it does not close the dependent issues.
2. **Publish the actual Resolver (Store #49).** Extend the existing release owner:
   `infrastructure/resolver/Resolver.fsproj`, proposed
   `infrastructure/resolver/release/Config.json`,
   `infrastructure/release/Participants.json`, `.github/workflows/release.yml`,
   `infrastructure/resolver/README.md`, and `docs/release/Requirements.md`.
   Use existing release policy/transaction/verification rather than a second
   publisher. Before implementation, confirm the roster can admit this executable
   without pretending it is an MCP producer; review `Policy.fs`, `Compare.fs` and
   `Evaluate.fs` under `infrastructure/release/` and request an amended file plan
   if their contract needs changing. Publish the complete runnable dependency
   closure (including FSharp.Core when required), source revision and immutable
   digest identity. Extend focused release/resolver tests only for the added
   participant and invalid/missing closure. Do not make Resolver install itself
   or bootstrap recursively through an untrusted Resolver.
3. **Consume one trusted selection (Harness #120, after #49).** Review/update
   `core/mcp/` with one proposed `pin.json` for Resolver, distinct from producer
   release metadata and the existing producer pins. Integrate acquisition and
   verification into `integrations/opencode/Install.ps1`, `Update.ps1`,
   `bootstrap/Bootstrap.ps1`, `agents/Installer.fsx`,
   `infrastructure/Update.fsx`, `infrastructure/Validator.fsx`, and `Assets.fsx`
   under `integrations/opencode/` as required by their existing ownership.
   Reuse their install/update/receipt/recovery transaction; specify the minimal
   trusted pre-execution verifier and owned artifact destination in #120's exact
   approval plan. Do not copy Store code, add a second package resolver, or defer
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
concurrency and stdio. No build, test suite, prototype, package publication,
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

Outstanding approval: retain versus a different selection; the bounded #49/#120
file plans and bootstrap trust/placement details; separate publication and runtime
activation consent. No prototype is necessary to recommend retention. If the owner
wants empirical evidence before rejecting tools, first approve **one isolated,
nonpublished, one-producer tool-path experiment**: controlled source/config and
exact digest/version, clean cache, concurrent install, absolute-shim MCP initialize
and tool call, cancellation, corrupt/missing failure and owned-temp cleanup. Its
acceptance is unchanged trust/content/no-op guarantees plus a concrete inventory
of code deleted versus SDK/feed/trust/wrapper work added. No production second path
or broad experiment suite is authorized by this proposal.
