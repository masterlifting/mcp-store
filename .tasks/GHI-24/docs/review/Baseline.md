# Bounded remediation baseline

Source and local/remote PR head: `05a69ced8319b61a6bf0e24cad6d320a0a879283`.
Branch: `ghi-24`; worktree was clean before the new evidence documents.
Original main: `30d82304e05899e2b1c83d9255fbe2f17abc0b7f`.

Authoritative inputs: `Threads.md`, `Requirements.md`, and the issue snapshot
`../original.md`. The eight full bodies and the withdrawn ninth are preserved;
the supported review API returned `hasNextPage=false`. No thread was resolved.

## Existing implementation surfaces to inspect

- `infrastructure/release/Release.fsproj`: currently compiles reusable `.fsx`
  modules with an explicit `Runner.fs`; `Bootstrap.fsx` is excluded for prior
  module-initialization effects. The required result is a consistent compiled app.
- `infrastructure/release/{Archive,Compare,Metadata,Evaluate,Publish,Capture}.fsx`:
  raw ZIP rules, metadata schema/equality, release states, injected HTTP transport
  and async process handling. Review identifies three unverified integrity branches.
- `dotnet/release/Release.fsx`, `workflow/release/Release.fsx`: duplicated producer
  orchestration. Shared build/pins/tests/runner/failure sequencing must have one owner.
- `dotnet/release/Config.fsx`, `workflow/release/Config.fsx`: canonical policy;
  versions, SDK/runtime/platform, allowlists, archive and URI must not be copied.
- `infrastructure/{Distribution,Pins,Provenance,Testing}.fsx`: existing shared
  packaging/pin/provenance contracts; avoid unrelated rewrite or duplicate truth.
- `infrastructure/release/{Update,Suite,Typecheck}.ps1`, `Bootstrap.fsx`, and
  `.github/workflows/release.yml`: pre-SDK metadata, current toolchain verification,
  test gates and currently repeated participation lists.
- `infrastructure/release/tests/*.fsx`: existing production-path regression cases,
  raw fixtures, HTTP handlers and historical integration. Preserve assertions
  when moving to a compiled project; counts alone are not acceptance proof.
- `dotnet/tests/Mcp.Dotnet.Tests/{SyncOverAsyncChecker,SyncOverAsyncCheckerTests}.fs`
  and its project: rename only these affected files into domain-oriented one-word
  paths, preserve module/project identities and checker behavior.

## Constraints and evidence ownership

No production edits have started in this remediation phase. The user orders six
reviewable stages with builds/affected tests after each. Engineer owns production
and compilation, tester owns regression migration/design/execution, DevOps owns
workflow/launch/bootstrap changes. Architecture proposals are isolated with this
same baseline and must select the smallest consistent compiled design; no plugins,
dynamic producer discovery, language adapters, new release engine or shims.

Current two-producer common toolchain restriction belongs to bootstrap only.
Generic release decisions/metadata/transport may use producer-owned expected
identity data, not an ambient mandatory global SDK/runtime identity. #37 owns
heterogeneous onboarding; #36 owns task-document support. Evidence remains here
temporarily; `.tasks/GHR-35/` must contain only MCP-managed `runtime.json`.

Prior run evidence is historical. It does not prove the new AC-01/02/03 negative
conditions or compiled architecture. Real uploads are prohibited during development;
use production code with controlled HTTP transport and report that distinction.
All acceptance results remain FAIL until direct verification and independent checks.
