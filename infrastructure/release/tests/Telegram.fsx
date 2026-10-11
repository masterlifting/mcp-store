// role: test entrypoint
// Telegram-isolation regression over executable code paths and YAML action lines; comment-only mentions are excluded so the test asserts real isolation.
open System
open System.IO

let mutable passed = 0
let mutable failed = 0
let failures = ResizeArray<string>()

let assertTrue name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(name)

let assertEqual name expected actual =
    if expected = actual then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(sprintf "%s: expected %A got %A" name expected actual)

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private readIfExists path =
    if File.Exists path then File.ReadAllText path else ""

// True for lines that are part of YAML action execution, not comments or
// documentation. Comments start with `#`; inline comments may follow a
// value but cannot introduce new tokens. The scope here is `run:` (the
// `run:` block's content lines), `shell:` lines, `uses:` references, and
// `env:` / `with:` mappings; blank lines are skipped because they carry
// no executable content.
let private isExecutableYamlLine (line: string) =
    let trimmed = line.TrimStart()
    not (String.IsNullOrWhiteSpace trimmed)
    && not (trimmed.StartsWith("#", StringComparison.Ordinal))
    && not (trimmed.StartsWith("//", StringComparison.Ordinal))
    && not (trimmed.StartsWith(">", StringComparison.Ordinal))
    && not (trimmed.StartsWith("|", StringComparison.Ordinal))

// True for F# source lines that are code, not comments or module
// documentation. The role header (`// role: ...`) is itself a comment.
// `(* ... *)` block comments are not used in this project. The comparison
// uses `code line + not-a-comment` rather than any substring scan.
let private isExecutableFsLine (line: string) =
    let trimmed = line.TrimStart()
    not (String.IsNullOrWhiteSpace trimmed)
    && not (trimmed.StartsWith("//", StringComparison.Ordinal))

let private containsIgnoreCase (haystack: string) (needle: string) =
    haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

// release.yml's executable action lines do not reference Telegram. The
// workflow may document Telegram as out-of-scope (lines starting with `#`)
// — those are deliberately excluded so this test asserts real isolation.
let releaseWorkflowExcludesTelegram () =
    let path = Path.Combine(repoRoot, ".github", "workflows", "release.yml")
    let source = readIfExists path
    assertTrue "release.yml exists on the tracked tree" (not (String.IsNullOrEmpty source))

    let executableLines =
        source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter isExecutableYamlLine

    let telegramHits =
        executableLines
        |> Array.filter (fun line -> containsIgnoreCase line "telegram")
        |> Array.toList

    assertTrue "release.yml executable lines do not reference telegram" (List.isEmpty telegramHits)

// The matrix block enumerates exactly `dotnet` and `workflow`. The block
// is parsed by line position: the YAML indent of the matrix component list
// is 10 spaces (`          - dotnet`/`- workflow`); we collect entries with
// exactly 10 leading spaces that follow the line `matrix:` and stop when
// we see a line whose left margin shrinks below 10 spaces (a sibling key).
let releaseWorkflowMatrixNamesOnly () =
    let path = Path.Combine(repoRoot, ".github", "workflows", "release.yml")
    let source = readIfExists path
    let lines = source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)

    let mutable matrixIndent = -1
    let components = ResizeArray<string>()
    let listIndent = 10
    for raw in lines do
        let trimmed = raw.TrimEnd([| '\r'; ' ' |])
        if matrixIndent < 0 && trimmed.TrimStart() = "matrix:" then
            matrixIndent <- trimmed.Length - "matrix:".Length
        elif matrixIndent >= 0 then
            let leadingSpaces = trimmed |> Seq.takeWhile (fun c -> c = ' ') |> Seq.length
            if leadingSpaces <= matrixIndent && trimmed.TrimStart() <> "" then
                matrixIndent <- -1
            elif leadingSpaces = listIndent && trimmed.TrimStart().StartsWith("- ", StringComparison.Ordinal) then
                let value = trimmed.TrimStart().Substring(2).Trim()
                components.Add(value)

    let componentList = components |> Seq.toList
    assertEqual "matrix.component has exactly two entries" 2 componentList.Length
    assertTrue "matrix.component includes dotnet" (componentList |> List.contains "dotnet")
    assertTrue "matrix.component includes workflow" (componentList |> List.contains "workflow")
    assertTrue "matrix.component excludes telegram" (not (componentList |> List.exists (fun c -> containsIgnoreCase c "telegram")))

// No `uv run pytest` (Telegram's CI command) appears on any executable
// YAML line in any release workflow. Workflow files are loaded by name from
// the workflows directory.
let noUvPytestInReleaseWorkflows () =
    let workflowsDir = Path.Combine(repoRoot, ".github", "workflows")
    let uvHits = ResizeArray<string * string>()
    if Directory.Exists workflowsDir then
        for wf in Directory.GetFiles(workflowsDir, "*.yml") do
            let source = File.ReadAllText wf
            for line in source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries) do
                if isExecutableYamlLine line && containsIgnoreCase line "uv run pytest" then
                    uvHits.Add(Path.GetFileName(wf), line.Trim())
    assertTrue
        (sprintf "no release workflow invokes 'uv run pytest' (exec lines checked: %d)" uvHits.Count)
        (uvHits.Count = 0)

// No Telegram-owned workflow file exists under .github/workflows.
let noTelegramWorkflowFiles () =
    let workflowsDir = Path.Combine(repoRoot, ".github", "workflows")
    if Directory.Exists workflowsDir then
        let telegramFiles =
            Directory.GetFiles(workflowsDir, "*.yml")
            |> Array.filter (fun path -> containsIgnoreCase (Path.GetFileName path) "telegram")
            |> Array.toList
        let allFiles = Directory.GetFiles(workflowsDir, "*.yml") |> Array.length
        assertTrue "no Telegram-named workflow yml files" (List.isEmpty telegramFiles)
        assertTrue "release.yml is among the workflows" (allFiles >= 1)

// The release scripts reachable from the workflow do not load Telegram or
// invoke Telegram's CI commands on any executable code line.
let releaseScriptsExcludeTelegram () =
    let scriptRoots =
        [
            Path.Combine(repoRoot, "dotnet", "release")
            Path.Combine(repoRoot, "workflow", "release")
            Path.Combine(repoRoot, "infrastructure", "release")
        ]
    let mutable hits = ResizeArray<string * string>()
    for scriptRoot in scriptRoots do
        if Directory.Exists scriptRoot then
            for path in Directory.EnumerateFiles(scriptRoot, "*.fsx") do
                let source = File.ReadAllText path
                let executable =
                    source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.filter isExecutableFsLine
                for line in executable do
                    if containsIgnoreCase line "telegram" || containsIgnoreCase line "uv run pytest" then
                        hits.Add(Path.GetFileName(path), line.Trim())
    assertTrue "no release script loads or invokes Telegram" (hits.Count = 0)

// The compiled runner does not load Telegram or use Telegram commands on
// any executable line.
let runnerSourceExcludesTelegram () =
    let path = Path.Combine(repoRoot, "infrastructure", "release", "Runner.fs")
    let source = readIfExists path
    let executable =
        source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter isExecutableFsLine
    let hits =
        executable
        |> Array.filter (fun line -> containsIgnoreCase line "telegram" || containsIgnoreCase line "uv run pytest")
        |> Array.toList
    assertTrue "Runner.fs executable lines exclude telegram" (List.isEmpty hits)

// The shared release modules (Compare, Decide, Metadata, Publish,
// Transaction, Bootstrap, Pins, Provenance, plus per-producer Config.fsx)
// do not load Telegram or invoke Telegram commands. Comments that name the
// release transaction contract are not asserted against.
let productionModulesExcludeTelegram () =
    let moduleFiles =
        [
            Path.Combine(repoRoot, "infrastructure", "release", "Compare.fsx")
            Path.Combine(repoRoot, "infrastructure", "release", "Decide.fsx")
            Path.Combine(repoRoot, "infrastructure", "release", "Metadata.fsx")
            Path.Combine(repoRoot, "infrastructure", "release", "Publish.fsx")
            Path.Combine(repoRoot, "infrastructure", "release", "Transaction.fsx")
            Path.Combine(repoRoot, "infrastructure", "release", "Bootstrap.fsx")
            Path.Combine(repoRoot, "infrastructure", "release", "Pins.fsx")
            Path.Combine(repoRoot, "infrastructure", "release", "Update.ps1")
            Path.Combine(repoRoot, "infrastructure", "Provenance.fsx")
            Path.Combine(repoRoot, "dotnet", "release", "Config.fsx")
            Path.Combine(repoRoot, "workflow", "release", "Config.fsx")
        ]
    // Update.ps1 is PowerShell; an executable line is non-blank, non-comment
    // (`#`-prefixed), and not a Markdown header (`##`).
    let isExecPs1 (line: string) =
        let trimmed = line.TrimStart()
        not (String.IsNullOrWhiteSpace trimmed)
        && not (trimmed.StartsWith("#", StringComparison.Ordinal))
    let mutable hits = ResizeArray<string * string>()
    for path in moduleFiles do
        if File.Exists path then
            let isPs = path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            let source = File.ReadAllText path
            let executable =
                source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.filter (fun line -> if isPs then isExecPs1 line else isExecutableFsLine line)
            for line in executable do
                if containsIgnoreCase line "telegram" || containsIgnoreCase line "uv run pytest" then
                    hits.Add(Path.GetFileName path, line.Trim())
    assertTrue "no production module loads or invokes Telegram" (hits.Count = 0)

// The telegram/ producer exists on disk (the exclusion baseline). No
// release pipeline references any path under `telegram/**` on any
// executable YAML line.
let telegramProducerExistsButUntouched () =
    let telegramDir = Path.Combine(repoRoot, "telegram")
    assertTrue "telegram/ producer directory exists" (Directory.Exists telegramDir)
    let pyproject = Path.Combine(telegramDir, "pyproject.toml")
    assertTrue "telegram/pyproject.toml is the producer manifest" (File.Exists pyproject)

    let workflowsPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml")
    let source = readIfExists workflowsPath
    let executable =
        source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter isExecutableYamlLine
    let hits =
        executable
        |> Array.filter (fun line -> line.Contains("telegram/", StringComparison.OrdinalIgnoreCase))
        |> Array.toList
    assertTrue "release.yml executable lines do not reference telegram/ paths" (List.isEmpty hits)

let runSuite () = async {
    releaseWorkflowExcludesTelegram ()
    releaseWorkflowMatrixNamesOnly ()
    noUvPytestInReleaseWorkflows ()
    noTelegramWorkflowFiles ()
    releaseScriptsExcludeTelegram ()
    runnerSourceExcludesTelegram ()
    productionModulesExcludeTelegram ()
    telegramProducerExistsButUntouched ()

    printfn "telegram-isolation-tests summary: passed=%d failed=%d" passed failed
    if failed > 0 then
        for line in failures do
            eprintfn "FAIL: %s" line
        exit 1
}

// Standalone entry bridge: the FSI process owns the asynchronous regression suite.
runSuite () |> Async.RunSynchronously
