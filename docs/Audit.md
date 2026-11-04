# Distribution audit — GHI-47

## Decision

**Simplify the current ZIP/manifest/Resolver path; do not authorize replacement yet.**
The custom lifecycle has substantial maintenance cost, but replacing it with tools
does not automatically preserve consumer-pinned content, filesystem admission,
or the existing release no-op policy. Conversely, standard tools do support exact
versions and installation without User PATH registration. Neither architecture is
categorically safer. Keep one delivery path; no parallel production experiment.

This is research for [Store #47](https://github.com/masterlifting/mcp-store/issues/47),
coordinated with [Harness #117](https://github.com/masterlifting/harness/issues/117).
All changes below are advisory and require a separate, file-specific approval.

## Evidence baseline

Inspected Store `bc6dca3d22415834d83182402a23a011c930a7e9` and Harness
`b86e030646ae58b30cad0053aa6f08c81ca8ee10`. Source references below are
`repository/path:line` at those commits. Both repositories' open issues were
listed (7 Store, 24 Harness); Store open PRs and ten recently updated PRs were
checked. Existing distribution work is already merged in Store #44/#45; #58
removed consumer-specific documentation coupling. This audit does not redo them.

Two read-only audit passes inspected distribution/resolution, release comparison,
evaluation/publication policy, CI, producer projects and actual consumer launch
and pin callsites. The second pass did not independently read the issue bodies;
the coordinator read #47/#46 and Harness #117/#116, plus the open-issue listing
bodies used for the related ownership notes. This is not acceptance of
the wider Harness #117 orchestration audit.

## Prioritized findings and smallest changes

### 1. Disposable SDK homes need explicit persistent-PATH suppression

**Verified static fact:** Store `Run.ps1:9-13,36-45` and
`.github/workflows/release.yml:263-301` redirect homes but do not set
`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`. The SDK documents global-tool PATH
registration as enabled by default. Redirecting process HOME/TEMP is not a
guarantee against persistent User PATH writes.

Corresponding Harness sites include
`tests/integrations/opencode/bootstrap/BootstrapTests.fsx:208,403`,
`tests/integrations/opencode/runtime/NativeIntegration.mjs:136,185,220`,
`tests/integrations/opencode/Delivery.fsx:322` and
`tests/integrations/opencode/plugins/session-runtime.test.ts:87`.

**Observed, limited verification:** a tester ran a fresh-home
`dotnet fsi --exec --quiet <temporary probe>` with suppression, telemetry opt-out
and offline controls. SDK 11.0.100-rc.1.26425.128 executed first-run initialization;
first-use sentinels appeared, a toolpath sentinel did not. Persistent User PATH
was unchanged: length 2351 and SHA-256
`1d112352596962d0ce8d67a3c3c71a1e864c2bb595bf4a55ec4c0ffd4c6ff1dc`
before/after. No raw PATH is published. Temporary artifacts were removed.

**Limitations/deviations:** this was not one complete `Run.ps1` or CI regression.
The tester exceeded the one-probe bound with diagnostics and a second SDK/cwd
probe (9.0.320). An initial attempt triggered a workload download before offline
controls; a parent `dotnet --version` wrote SDK telemetry spool files, left
untouched. No unprotected counterfactual was run. Thus actual test-origin PATH
pollution, its exact entry path and its occurrence on hosted CI remain
**unproven**, not confirmed defects. `--info`/`--version` alone did not exercise
first-run and would be vacuous regression probes.

**Proposal:** add the suppression to the existing child environment setup in
`Run.ps1` and the release suite runner; preserve/restore any enclosing environment
where the runner changes it. Coordinate the same invariant, not a shared
framework, with Harness #117. Do not alter production shim registration in
Harness `integrations/opencode/Install.ps1:209,290-345`, or clean the user's PATH.

### 2. Consumer pin-schema duplication is real; independent selection is required

Store `infrastructure/distribution/Pin.fs:14-29,76-232` owns strict pin parsing.
Harness `core/mcp/Pin.fsx:37-79` independently encodes its fields and formats.
Both consumer callsites discard the parsed value:
`integrations/opencode/agents/Installer.fsx:785-787` and
`integrations/opencode/infrastructure/Validator.fsx:503-505`.
Resolver `infrastructure/resolver/Run.fs:12-16` parses again at actual use.

**Opportunity:** retire the duplicated full-schema consumer parser module
(`core/mcp/Pin.fsx`, 84 lines)
and its two semantic-validation callsites **only after** preserving the actual
consumer-specific descriptor/component binding and deciding whether early lint
is useful. Missing/wrong-component pins must fail before an unintended producer
can launch. Do not import producer checkout code or invent a shared parser SDK.

Harness `core/workflow/pin.json` and `domains/software/dotnet/pin.json` are
legitimate independently owned exact selections, not proof of conflicting
release authority. Different JSON field order does not prove hand maintenance.
Copying a reviewed published pin is not itself redundant state. Dropping fields
or adding pin schema v2 is **not justified by this audit**: all canonical pin
fields participate in installation identity
(`infrastructure/resolver/README.md:107-108`).

### 3. Release comparison is expensive but protects an explicit policy

`infrastructure/release/Compare.fs:297-427,437-498` compares raw archive entries
and permits only a manifest revision substitution. `Evaluate.fs:598-599` treats
byte-identical and revision-only-equivalent candidates as unchanged.
`docs/release/Requirements.md:53-54,86-87,101,113-115` requires unchanged product bytes at
unrelated HEADs to retain the published release; different product bytes at the
same version must fail. Published hashes/revision remain authoritative
(`infrastructure/release/Metadata.fs:75-97`).

**Do not delete this in favor of one archive hash.** Same-revision reproducibility
does not establish cross-revision equivalence. A digest short-circuit already
exists (`Compare.fs:450-451`). This is required behavior, not redundant validation.

**Small local candidate:** factor duplicated projection construction/writing in
`infrastructure/release/Transaction.fs:25-41,207-234`, and common raw-entry
comparison fields in `Compare.fs:297-326,346-371`, if the resulting code is
smaller and equally explicit. Preserve their different allowed-field semantics;
no estimated deletion count or safety claim is established without a patch.

### 4. Resolver cost is concrete; boundary checks are not interchangeable

`infrastructure/resolver/Kind.fs:124-205,232` uses bounded Unix `stat` subprocesses;
`Disk.fs:263-374` owns bounded inventory/deletion; `Lock.fs:80-86,108-113` owns
exclusivity checks and retry. These are custom maintenance, not free guarantees
of SHA-256. `README.md:170-190,204-267` records indefinite retention, operator
recovery for corrupt installations, Unix classification cost and non-atomic
path admission. Do not add automatic repair/GC without an observed need.

Harness `integrations/opencode/runtime/mcp/Start.ps1:28-44,117-122` confines inputs
to the runtime and creates required directories. Resolver
`infrastructure/resolver/Input.fs:26-67` instead enforces existence and exclusion
from its storage root. Producer checks add workspace/host and catalog-content
requirements (`dotnet/runtime/Invocation.fs:65-98`,
`workflow/protocol/Mcp.fs:1001-1016`). These boundaries are complementary.
In particular, do not delete the host/resolver safety checks that run **before**
executing the resolver merely because later code validates an argument.

**Opportunity:** consider removing only demonstrably repeated non-security lint,
such as SHA-256 syntax checking in `Start.ps1:128` versus resolver launch parsing,
if maintaining earlier diagnostics costs more than it helps. This is a small
candidate, not the principal saving. No shared path-safety framework is proposed.

## Complete lifecycle comparison

Current producers are framework-dependent `net11.0` executables, not tools
(`dotnet/Dotnet.fsproj:3,6`, `workflow/Workflow.fsproj:3,6`). Release configs
select `win-x64` and four runtime payload files
(`dotnet/release/Config.json:5-19`, `workflow/release/Config.json:5-19`).
Preview runtime compatibility is a real prerequisite in either framework-dependent
route. Unix PR CI builds/tests three RIDs but does not publish those distributions
(`.github/workflows/unix.yml:3-16,31-37,99-125`).

| Concern | ZIP/manifest/Resolver now | .NET tool / NuGet | Standalone executable |
| --- | --- | --- | --- |
| Build/publication | Publish + custom manifest, ZIP, comparison and GitHub transaction | PackAsTool + immutable package publication; feed ownership/auth/signing and release policy still needed | Publish apphost or self-contained RID payload; preserve immutable asset publication |
| Identity/pins | Consumer selects archive/manifest hashes, revision and launch identity | Exact `--version` supported; version alone is not an independently pinned digest; trusted source/signature policy and cache assumptions need decisions | Exact artifact digest/source pin still needed; executable format provides neither |
| Provenance/integrity | Trusted pin origin + allowed source, archive/per-file hashes and identity checks; hashes alone do not authenticate author | Source mapping, package hashes and optional trusted signatures; default signature mode is accept, not require | Authenticode/signing optional; otherwise retain trusted-source/digest handling |
| Bootstrap/install | Runtime + separately trusted Resolver artifact; custom safe staging/rename | SDK for install/restore, runtime for framework-dependent execution; `--tool-path` avoids PATH enrollment | Framework-dependent apphost still needs runtime; self-contained removes runtime bootstrap but adds size/RID servicing |
| Cache/concurrency | Content-keyed installs, explicit locks, re-verification, no automatic repair/GC | Standard NuGet/tool stores remove much custom code; isolated tool-path concurrency, cache contamination and recovery must be checked, not assumed equivalent | Download/staging/locks and cache still consumer or producer work unless another installer owns them |
| Stdio/processes | Explicit workspace/host/typed inputs, inherited stdio, typed failures | Absolute shim invocation avoids command shadowing; prove clean stdio, cancellation, exit propagation and working directory | Direct process is simpler to invoke; same MCP lifetime/stdio requirements remain |
| Updates/failure/cleanup | New pin/install; never mutate old release; corrupt install stops for operator | Exact version update or fresh tool path; uninstall/cache APIs exist but are not a guarantee of safe concurrent custom cleanup | New immutable directory + cutover; old-process and retained-version cleanup still needed |
| Net cost | Largest custom release/resolver surface; known operational limitations | Largest potential deletion, but new SDK/feed/trust configuration and migration work | Removes launch/runtime dependencies only when self-contained; does not remove the distribution lifecycle |

**Measured maintenance surface:** the three immediate source directories contain
3 distribution, 18 resolver and 11 release `.fs` modules (32 total), with 1,084,
1,981 and 3,369 nonblank lines respectively (6,434 total). This excludes tests,
scripts, CI, projects and consumer code; it is inventory, not a predicted saving.
The consumer pin module is another 84 physical lines at two callsites.

For comparison, a tool replacement introduces at least five concrete integration
obligations: two producer tool packages, one feed/publication arrangement, one
consumer install/launch integration, and one source/signature/cache trust
configuration. Self-contained standalone delivery requires two producer RID
publish changes and one consumer executable-launch integration, while retaining
artifact pin/install/cache responsibilities. These are work-unit counts, not
equivalent effort estimates; shared wiring may overlap. No prototype or patch
exists to establish replacement LOC, performance or maintenance savings.

NuGet does **not** imply weak private-feed TLS-only trust: author/repository
signatures and trusted signers exist. Nor does a per-file manifest establish
superior author provenance. Source mapping can be bypassed by an already-cached
package, and a configured source is not sufficient by itself to preserve the
current digest-pinning guarantee. Package extraction locks are not evidence of
equivalent atomic repair or link admission.

Language-neutral requirements are immutable identity, authenticated source,
integrity, safe installation, exact selection and reliable processes. Current
`.dll`, runtimeconfig, host and SDK assumptions are .NET-specific. Do not build
generic language enrollment: Store #37 is explicitly deferred until a real need.

## Primary guidance and unresolved decisions

Official guidance consulted:

- [SDK environment controls](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables): `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH` defaults to true.
- [Global tools](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools) and [install](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install): secure `--tool-path`, absolute invocation, exact versions, controlled manifests. Custom tool paths are not automatically added to PATH.
- [Source mapping](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping), [NuGet configuration](https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file), [signed packages](https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference): distinguish source selection, cached content, integrity and authenticated publisher. Use an explicit config; review command-specific source overrides.
- [Tool troubleshooting](https://learn.microsoft.com/en-us/dotnet/core/tools/troubleshoot-usage-issues) and [RID tools](https://learn.microsoft.com/en-us/dotnet/core/tools/rid-specific-tools): framework/runtime constraints versus SDK 10+ self-contained RID packaging and its publication-order cost.

**Unresolved:** acceptable feed and certificate ownership; preservation of exact
content pins versus a differently trusted package selection; revision-only no-op
policy; install-time SDK acceptability; actual concurrent install/cleanup and
stdio behavior. These prevent an unconditional replacement recommendation, not
the bounded simplifications above. No standard-tool or standalone prototype,
build, complete test suite, release or live runtime cutover was executed here.

**At most one targeted prototype, only if replacement remains attractive:**
under Store #46, approve an isolated, nonpublished tool-path trial for one actual
producer. Specify the trusted package/digest source and clean cache; verify exact
install, a second concurrent install, absolute-shim MCP initialization, one tool
call, cancellation, corrupt/missing artifact failure and owned-temp cleanup.
Measure producer/consumer code actually removable against added SDK/feed/trust
work. Do not spend the prototype allowance on archive determinism: that cannot
justify deleting revision-only policy. Do not touch active runtimes or add a
second supported distribution path.

## Ownership and safe execution order

1. **#47 + Harness #117:** review this evidence together. Approve any PATH fix as
   an exact file plan for existing child environment sites; one bounded offline
   first-run regression must compare persistent User PATH without printing it.
   No cleanup of historical user entries. Harness owns its corresponding sites.
2. **Store #46:** accept/reject each simplification and settle the distribution
   trust/feed/no-op decisions. Retain current policy unless explicitly changed.
   If needed, approve the single prototype above before a replacement plan.
3. **Store #46:** implement only the approved producer/contract/release changes;
   publish and verify immutable artifacts before consumer selection. Existing
   runtime must remain usable until separately authorized cutover.
4. **Harness #116:** remove justified schema duplication while preserving
   component binding and pre-execution safety; consume actual released artifacts
   using one independent pin per producer. Prove clean install, exact update,
   stdio/tool operation and failure handling without a Store checkout.
5. **Operator-authorized consumer cutover, then retirement:** protect user data,
   verify current processes and recovery, activate once, and only then remove
   obsolete source paths. No compatibility reader, parallel delivery path or
   speculative installer framework. If retain/simplify wins, no distribution
   cutover or new pin schema is required.

Related issue boundaries: Store #49 / Harness #120 own an interim trusted Resolver
bootstrap only if still needed; do not build that path if superseded. Store #52
owns toolchain-version deduplication; #33 and Harness #91 own optional evidence
visibility, not another audit ledger. Harness #49 capability adoption and #111
cleanup do not justify a consumer-owned resolver. This report does not authorize
or close any of those issues. Submit documentation for independent review; do
not merge or implement before the operator approves the relevant exact actions.
