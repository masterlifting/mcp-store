# GHI-88 — Deterministic semantic refactoring for .NET agents

**Date:** 2026-11-27. **Disposition:** preliminary research; experimental acceptance blocked. No production change proposed for this issue.

## Decision

Defer production integration until measured evidence exists. First qualify the existing C# refactoring tool from JoshuaRamirez, comparing MadQ's explicit preview/apply contract if the first candidate fails safety checks. For F#, qualify FsAutoComplete through its native LSP rename operation. Rider is an optional IDE-connected comparator, not a runtime dependency. Prefer direct consumption of a qualified external tool over a wrapper; if confinement, output bounds or operation exposure cannot be satisfied by the native tool, evaluate a small Store-owned adapter to the existing engine. Do not implement a compiler, shared refactoring DSL, LSP broker or new approval subsystem in this research.

## Current architecture

Store main: [dotnet/Dotnet.fsproj](https://github.com/masterlifting/mcp-store/blob/main/dotnet/Dotnet.fsproj), [protocol/Host.fs](https://github.com/masterlifting/mcp-store/blob/main/dotnet/protocol/Host.fs), [runtime/Authorization.fs](https://github.com/masterlifting/mcp-store/blob/main/dotnet/runtime/Authorization.fs) and [README](https://github.com/masterlifting/mcp-store/blob/main/dotnet/README.md) establish three operations (`build`, `test`, `details`), closed request schemas, bounded results, cancellation, target-path validation and a consumer-supplied artifact root. Current target authorization is not sufficient by itself to confine multi-file mutations: project graphs can reference files beyond the target and root; all affected physical paths must be validated.

Harness main [dotnet.json](https://github.com/masterlifting/harness/blob/main/integrations/opencode/mcp/dotnet.json) and [tools.md](https://github.com/masterlifting/harness/blob/main/core/contracts/tools.md) expose those same three operations. Store #16 owns non-fixing verification; Harness #149 owns knowledge reduction based on demonstrated enforcement; Harness #49 owns consumption of actually released capabilities. The Store resolver accepts its own verified distribution contract; it is not a generic third-party installer today. No consumer pins, descriptors, active runtime or permissions were changed. The [OpenCode V1 LSP tool documentation](https://opencode.ai/v1/docs/tools/#lsp-experimental) does not list rename or code actions among its supported operations; inspect the actual installed version before relying on this. Configuring an LSP alone does not establish an agent-accessible mutation operation.

## Evidence and alternatives

Evidence labels: **source** = upstream code inspected; **documented** = upstream documentation/package metadata inspected; neither means executed. All runtime probes are blocked in this environment. Versions below are observed published examples, not a recommendation to adopt the latest release or proof of compatibility.

| Candidate | Documented capabilities and execution model | License / risk | Disposition |
|---|---|---|---|
| [Rider MCP](https://www.jetbrains.com/help/rider/mcp-server.html), [refactoring skill](https://github.com/JetBrains/skills/blob/main/refactoring-code/SKILL.md) | IDE solution index; rename, extract method/interface/base class, move, signature, namespace and safe-delete. Skill lists F# but per-operation coverage needs verification. Conflict reporting is documented. | Proprietary engine; licensing/redistribution and independently supported headless packaging not established. IDE open-solution lifecycle. | Optional comparator; do not mandate Rider or copy its backend. |
| [ReSharper CLT](https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html), [SDK](https://www.jetbrains.com/help/resharper/sdk/InspectCode.html) | Headless InspectCode and CleanupCode, not a documented general targeted refactoring CLI. Extensibility is available for inspection. | Separate JetBrains license terms; free CLT does not imply redistributable refactoring SDK. | No demonstrated reason to develop an extension. |
| [JoshuaRamirez/RoslynMcpServer](https://github.com/JoshuaRamirez/RoslynMcpServer), [CLI 0.10.0](https://www.nuget.org/packages/RoslynMcp.Cli/0.10.0), [server 0.11.0](https://www.nuget.org/packages/RoslynMcp.Server/) | C#; MCP, standalone JSON CLI and Core library. Rename with position/options, extract, change signature and move operations. Preview and rollback are author claims requiring probes. CLI package targets net9.0; project SDK compatibility separately matters. | MIT; active release evidence, pre-1.0 API, numerous tools and absolute target paths. Avoid bulk allFiles rename. Verify file containment and stale preview semantics. | First headless C# candidate: CLI for manual probes, then native MCP only if justified. |
| [MadQ/RoslynMcp](https://github.com/MadQ/RoslynMcp) | C#; separate `roslyn_preview_rename` and `roslyn_apply_rename`, with token; code actions, general edits and navigation. Source-build requirement .NET 10; conditional net11.0. | MIT; pre-1.0 package identity changed to `MadQ.RoslynMcp`. Its own security note says user-accessible project paths can be targeted; a token is not user authorization. Broad write surface. | Alternative if preview binding materially improves safety; restrict registered write capabilities. |
| [dave-hillier/RefactorMCP](https://github.com/dave-hillier/refactor-mcp) | C#; CLI, stdio MCP, resident solution daemon, `--no-daemon`, documented file refresh; extensive operations and upstream fixtures, including safe delete. SDK >=9 documented. | MPL-2.0 file-level copyleft; review distribution obligations. Cache/daemon lifecycle and atomicity require verification; no verified preview contract. | Alternative for structural operations unavailable or failing elsewhere. Probe without daemon first. |
| [Roslyn Workspaces](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.rename.renamer.renamesymbolasync), [Renamer source](https://github.com/dotnet/roslyn/blob/main/src/Workspaces/Core/Portable/Rename/Renamer.cs), [MSBuildWorkspace source](https://github.com/dotnet/roslyn/blob/main/src/Workspaces/MSBuild/Core/MSBuild/MSBuildWorkspace.cs) | C# (also VB, not F#). Public rename API takes a solution snapshot and symbol, returns a new solution; options for overloads, strings, comments and files. MSBuildWorkspace loads project graph. Not all IDE refactorings have equivalent stable public entry points. | MIT; compile/package compatibility, MSBuild execution and workspace loading are caller responsibilities. | Small adapter fallback for rename only if no external solution meets constraints. |
| [Microsoft roslyn-language-server](https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/roslyn-language-server/README.md), [csharp-ls](https://github.com/razzmatazz/csharp-language-server) | Headless C# LSP; Microsoft tool documents stdio, project loading and code actions, .NET 10+ runtime, prerelease installation. Discover actual rename and actions via initialize/capabilities and requests. | MIT; LSP client lifecycle and edit application still required; Microsoft explicitly warns that loading projects may execute code. | Prefer if an existing trusted client already exposes mutation; avoid a new broker. |
| [FsAutoComplete](https://github.com/ionide/FsAutoComplete), [Ionide docs](https://ionide.io/Tools/fsac.html), [NuGet](https://www.nuget.org/packages/FsAutoComplete/) | F# Compiler Service and project system; documented `textDocument/rename` and code actions. 0.86.1 observed; verify runtime, project SDK, signature files and dependent projects. FCS provides semantic information, not an established equivalent of every Rider operation. | Apache-2.0 (not MIT); exact redistribution notices and dependencies require review. Client must implement workspace edits and synchronization. | First F# candidate. No Roslyn-based fallback for F#; do not build a custom FCS refactoring engine. |
| [VuDZ/RoslynMcpServer](https://github.com/VuDZ/RoslynMcpServer/) | C#; previewed rename and documented lite/group profiles, portable Linux release artifacts. | MIT; independent similarly named project. Verify exact publisher, release, capabilities, and trust. | Secondary option if minimal tool exposure cannot be achieved with first candidate. |

For all candidates, repository license does not establish licensing of every packaged dependency; package notices, provenance, actual executable/runtime dependencies and SDK selection must be qualified before adoption. Upstream source inspection of the third-party write paths and cross-platform execution remain incomplete; do not infer their correctness from README claims. Maintenance evidence here is release/package availability, not a completed maintainer or vulnerability audit.

JetBrains [evaluation](https://blog.jetbrains.com/dotnet/2026/08/19/rider-refactoring-code-skill/) reports a median 157.9s to 26.6s for 15 C# tasks with repeated GPT-5.5/Codex runs; two tasks failed in both arms and one only in the enabled arm. This is vendor evidence, not a measurement of our models or F# workloads. It supports testing the mechanism, not promising a speedup.

## Minimum contract and ownership

- Explicit authorized root, solution/project, exact source location, operation and arguments. Fail ambiguous/unsupported targets; never enable broad rename by name. Verify solution load diagnostics, references and actual tool coverage. A public rename may break external consumers outside the loaded solution; disclose this, never promise global compatibility.
- Separate semantic computation from write authority. Prefer native preview, then apply through existing mutation permission. Preview tokens are not approval. Validate canonical affected files (including creates/deletes/moves, linked files, symlinks/reparse points and project references); reject out-of-scope edits and stale source. LSP `WorkspaceEdit` may contain versioned and unversioned changes and resource operations; the client must handle supported kinds or reject the whole edit. [Protocol specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/).
- Preserve explicit cancellation, honest failure reporting, bounded changed-file summary/diff and existing build verification. Use existing git diff where appropriate; never truncate away information needed to validate scope. No claim of atomicity unless verified under partial failure; don't attempt a generic multi-file transaction subsystem for MVP.
- MSBuild project evaluation, analyzers and code-fix providers can execute code; use disposable, trusted projects, no credentials/network access beyond separately approved setup. Native tool permissions remain the hard boundary. Avoid exposing unrelated external tool mutations, generic text editors or arbitrary paths. Measure cold and warm cost; do not add a persistent service by default.

## Reproducible bounded experiment — pending

Create two independent disposable two-project solutions using the SDK under test. Use `dotnet new sln --name Demo --format sln`, `dotnet new classlib -n Lib -lang C#`, `dotnet new console -n App -lang C#`, `dotnet sln Demo.sln add Lib/Lib.csproj App/App.csproj`, `dotnet add App/App.csproj reference Lib/Lib.csproj`. For F# repeat in another root with `-lang F#`, `.fsproj` paths. Record `dotnet --info`, tool version, OS, project configuration, and hashes. Use a trusted SDK supported by the selected tool and repeat on the actual net11 target if the first probe uses another SDK. Replace the template source files below; don't add test infrastructure to Store.

C# `Lib/Class1.cs`:

```csharp
namespace Demo;
public static class Parser {
    public static int Parse(int value) => value + 1;
    public static int Parse(string value) => value.Length;
}
public static class Other {
    public static int Parse(int value) => value * 2;
}
```

C# `App/Program.cs`:

```csharp
using Demo;
Func<int, int> convert = Parser.Parse;
int Parse(int value) => value - 1;
Console.WriteLine($"{Parser.Parse(2)} {Parser.Parse("ab")} {Other.Parse(2)} {Parse(2)} {convert(2)} Parse");
```

F# `Lib/Library.fs`:

```fsharp
namespace Demo
module Api =
    let parse (value: int) = value + 1
module Other =
    let parse (value: int) = value * 2
```

F# `App/Program.fs`:

```fsharp
open Demo
let convert = Api.parse
let parse value = value - 1
printfn "%d %d %d %d parse" (Api.parse 2) (Other.parse 2) (parse 2) (convert 2)
```

Expected initial and final stdout: C# `3 2 4 1 3 Parse`; F# `3 4 1 3 parse`. Run `dotnet build Demo.sln` and `dotnet run --project App --no-build` before cloning each baseline. These expected outputs were derived by source inspection, not observed from execution.

### C# qualification

Install the selected package into a disposable tool directory (not global), record source and exact version. For example:

```sh
dotnet tool install RoslynMcp.Cli --tool-path "$TOOLS" --version 0.10.0
"$TOOLS/roslyn-cli" "$PWD/Demo.sln" diagnose --format text
"$TOOLS/roslyn-cli" rename-symbol --help
```

Use the live CLI help to confirm supported arguments. Candidate request: `rename-symbol --source-file "$PWD/Lib/Class1.cs" --symbol-name Parse --new-name Read --line 3 --column 23 --preview` (with absolute solution argument first). The selected C# method identifier begins at 1-based line 3, column 23. Explicitly disable overload, string/comment and file renaming using the actual contract if necessary. Preview must not mutate files. Apply the same operation to an unchanged snapshot only after permitted scope review; check actual diff and result. Expected changes: the declaration of `Parser.Parse(int)`, the typed method group `Parser.Parse` and its integer call. The string overload, unrelated class, local function and literal remain unchanged. Rebuild and run. If this candidate cannot meet the contract, inspect MadQ's live `tools/list` and try its preview/apply on a fresh clone.

### F# qualification

Install FsAutoComplete into an isolated tool directory using an exact available version (0.86.1 was listed by NuGet); use its documented launch/config for that version. An existing trusted LSP client may be used for the first probe, not a new generic LSP implementation. Capture initialize capabilities, workspace readiness and the actual JSON-RPC rename response; use a URI for `Lib/Library.fs`, zero-based line 2, character 8, newName `read`. Verify position encoding negotiation. If supported, call `textDocument/prepareRename` first. Expected changed occurrences: the `Api.parse` declaration and both external references; `Other.parse`, the local function and literal remain. Apply only authorized edits on unchanged files, rebuild and run. A plain editor-client rename establishes capability, not agent-level performance; use an actual agent-accessible route for the comparison or mark that arm unavailable. For both languages, after successful basic rename, probe collision, an edit between preview and apply, external linked source or project outside root, cancellation/failure and an omitted consumer of the public API. Add a second structural operation only if rename is insufficient to distinguish viable candidates.

### Comparison protocol

For each language, run ordinary agent text editing and the best qualified semantic route on fresh equivalent copies. Same model/profile, prompt, initial source, environment and final verification; disable unrelated tools in both arms and enable only the candidate in the treatment. Record cold/warm separately, with at least three paired trials per language if proportionate; retain raw traces where allowed, not secrets. Measure wall time including loading, semantic tool calls, total agent tool calls, build attempts, token/cost if observable, final diff correctness, build/run outcome and failure category. An incomplete trial stays incomplete; never substitute a regex for the agent baseline. Keep fixture oracle outside agent-visible inputs. Report raw observations, not only averages; a tiny fixture cannot establish general production gains. If successful, repeat on a disposable representative real project before recommending adoption.

## Actual execution evidence from this session

Linux x86_64 container: `which dotnet` returned no path; standard SDK locations absent; HTTPS probes to `dot.net` and `api.nuget.org` failed with curl exit 6 (DNS resolution). No SDK or packages were installed. No semantic engine, language server, LLM comparison or .NET build ran; cold/warm latency, cost, correctness, SDK compatibility and Windows/Linux cross-platform behavior are unmeasured.

One executed **text-only negative control** used Python `re.findall` with whole-word `Parse`/`parse` on the four exact source snippets above. C# had 10 textual occurrences versus 3 intended changes (7 unintended); F# had 8 versus 3 (5 unintended). The expected 3 changes per language were derived manually from scope and binding. This demonstrates the inadequacy of naive global substitution on these fixtures; it does not measure an LLM or establish any semantic tool's correctness.

## Acceptance status

- Capability and trade-off matrix: preliminary documented/source evidence; third-party internals and live probes pending.
- Reproducible experiment: manual procedure and fixtures prepared; semantic experiment and independent agent comparison not executed.
- Recommendation: defer adoption; qualify existing C# and F# tools first. No implementation issue until evidence supports a specific route.
- No production, release or installed runtime changes; no automated behavioral test suite added. Workflow MCP Task Runtime unavailable in this session; no task state mutated.

## Agent handoff

Continue GHI-88 in this repository using the current Harness and its task skill. Use disposable fixtures or representative project copies, a separately authorized isolated tool configuration if needed, and retain real probe/measurement evidence in this research PR. Read the current issue and repository guidance first. Do not modify Harness source, installed runtime, consumer pins, production MCP code or release contracts in this issue. Report unavailable experiments explicitly; choose an integration route only after evidence and separate approval. Keep this PR in draft until the research acceptance criteria are satisfied.
