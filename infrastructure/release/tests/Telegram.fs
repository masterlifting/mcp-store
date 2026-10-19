module Release.Tests.Telegram

open Expecto
open Release.Tests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

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
let private isExecutableYamlLine (line: string) =
    let trimmed = line.TrimStart()
    not (String.IsNullOrWhiteSpace trimmed)
    && not (trimmed.StartsWith("#", StringComparison.Ordinal))
    && not (trimmed.StartsWith("//", StringComparison.Ordinal))
    && not (trimmed.StartsWith(">", StringComparison.Ordinal))
    && not (trimmed.StartsWith("|", StringComparison.Ordinal))
let private isExecutableFsLine (line: string) =
    let trimmed = line.TrimStart()
    not (String.IsNullOrWhiteSpace trimmed)
    && not (trimmed.StartsWith("//", StringComparison.Ordinal))

let private containsIgnoreCase (haystack: string) (needle: string) =
    haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

let private workflowJobBody (workflow: string) (jobName: string) =
    let pattern = "(?ms)^  " + Regex.Escape(jobName) + ":\\r?\\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\\r?\\n|\\z)"
    let matched = Regex.Match(workflow, pattern)
    if matched.Success then matched.Groups.["body"].Value else ""

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
let releaseWorkflowAndRosterUseDynamicParticipants () =
    let rosterPath = Path.Combine(repoRoot, "infrastructure", "release", "Participants.json")
    assertTrue "Participants.json exists" (File.Exists rosterPath)

    let participants =
        if File.Exists rosterPath then
            let rosterText: string = File.ReadAllText rosterPath
            use document = JsonDocument.Parse(rosterText)
            document.RootElement.GetProperty("participants").EnumerateArray()
            |> Seq.map (fun entry ->
                let componentId: string = entry.GetProperty("componentId").GetString()
                let configPath: string = entry.GetProperty("configPath").GetString()
                componentId, configPath)
            |> Seq.toList
        else
            []

    let expectedParticipants =
        [ "dotnet", "dotnet/release/Config.json"
          "workflow", "workflow/release/Config.json" ]

    assertEqual "Participants.json registers exactly the supported component/config pairs" expectedParticipants participants
    assertTrue "Participants.json excludes Telegram" (participants |> List.forall (fun (id, _) -> not (containsIgnoreCase id "telegram")))

    let workflowPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml")
    let source = readIfExists workflowPath
    let lines = source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
    let preparationJob = workflowJobBody source "preparation"
    let publicationJob = workflowJobBody source "publication"
    assertTrue
        "release workflow projects the roster-derived component list to every job"
        (containsIgnoreCase source "${{ steps.project.outputs.components }}"
         || containsIgnoreCase source "$env:RELEASE_COMPONENTS")

    let hardcodedParticipantList =
        lines
        |> Array.exists (fun line ->
            let trimmed = line.Trim()
            trimmed = "- dotnet" || trimmed = "- workflow")

    assertTrue "release workflow has no hardcoded participant list" (not hardcodedParticipantList)

    assertTrue
        "the fresh read-only preparation job derives its CLI path from the looped participant"
        (Regex.IsMatch(preparationJob, "(?i)runs-on:\\s*windows-latest")
         && Regex.IsMatch(preparationJob, "(?i)contents:\\s*read")
         && Regex.IsMatch(preparationJob, "(?i)&\\s+\\$\\w+\\s+prepare\\b")
         && Regex.IsMatch(preparationJob, "(?i)Join-Path\\s+\\(Get-Location\\)\\s+\"\\$\\w+/release/Config\\.json\""))
    assertTrue
        "the publication job is the only writer and gates on the preparation job"
        (Regex.IsMatch(publicationJob, "(?i)contents:\\s*write")
         && Regex.IsMatch(publicationJob, "(?m)^\\s*actions:\\s*read\\s*$")
         && Regex.IsMatch(publicationJob, "(?im)^ {4}needs:\\s*$")
         && containsIgnoreCase publicationJob "- preparation"
         && containsIgnoreCase publicationJob "- publisher"
         && not (Regex.IsMatch(preparationJob, "(?i)contents:\\s*write")))

    let hardcodedPreparePath =
        lines
        |> Array.exists (fun line ->
            containsIgnoreCase line "prepare \"dotnet/release/Config.json\""
            || containsIgnoreCase line "prepare \"workflow/release/Config.json\"")

    assertTrue "release prepare path is not hardcoded to a participant" (not hardcodedPreparePath)
    assertTrue
        "the publisher job invokes the production cross-stage coherence guard"
        (containsIgnoreCase source "Coherence.ps1 -Components $env:PROJECTED_COMPONENTS")
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
let releaseScriptsExcludeTelegram () =
    let scriptRoots =
        [
            Path.Combine(repoRoot, "dotnet", "release")
            Path.Combine(repoRoot, "workflow", "release")
            Path.Combine(repoRoot, "infrastructure", "release")
        ]
    let mutable hits = ResizeArray<string * string>()
    for scriptRoot in scriptRoots do
        assertTrue (sprintf "release source directory exists: %s" scriptRoot) (Directory.Exists scriptRoot)
        for path in Directory.EnumerateFiles(scriptRoot, "*.fsx") do
            let source = File.ReadAllText path
            let executable =
                source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.filter isExecutableFsLine
            for line in executable do
                if containsIgnoreCase line "telegram" || containsIgnoreCase line "uv run pytest" then
                    hits.Add(Path.GetFileName(path), line.Trim())
    assertTrue "no release script loads or invokes Telegram" (hits.Count = 0)
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
let productionModulesExcludeTelegram () =
    let moduleFiles =
        [
            Path.Combine(repoRoot, "infrastructure", "distribution", "Archive.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Compare.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Evaluate.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Metadata.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Publish.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Capture.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Transaction.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Orchestration.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Runner.fs")
            Path.Combine(repoRoot, "infrastructure", "release", "Update.ps1")
            Path.Combine(repoRoot, "infrastructure", "Provenance.fs")
            Path.Combine(repoRoot, "dotnet", "release", "Config.json")
            Path.Combine(repoRoot, "workflow", "release", "Config.json")
        ]
    let isExecPs1 (line: string) =
        let trimmed = line.TrimStart()
        not (String.IsNullOrWhiteSpace trimmed)
        && not (trimmed.StartsWith("#", StringComparison.Ordinal))
    let mutable hits = ResizeArray<string * string>()
    let missing = moduleFiles |> List.filter (File.Exists >> not)
    assertTrue (sprintf "all expected compiled release sources exist: %A" missing) (List.isEmpty missing)
    for path in moduleFiles do
        let isPs = path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
        let source = File.ReadAllText path
        let executable =
            source.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.filter (fun line -> if isPs then isExecPs1 line else isExecutableFsLine line)
        for line in executable do
            if containsIgnoreCase line "telegram" || containsIgnoreCase line "uv run pytest" then
                hits.Add(Path.GetFileName path, line.Trim())
    assertTrue "no production module loads or invokes Telegram" (hits.Count = 0)
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
    releaseWorkflowAndRosterUseDynamicParticipants ()
    noUvPytestInReleaseWorkflows ()
    noTelegramWorkflowFiles ()
    releaseScriptsExcludeTelegram ()
    runnerSourceExcludesTelegram ()
    productionModulesExcludeTelegram ()
    telegramProducerExistsButUntouched ()

    printfn "telegram-isolation-tests summary: passed=%d failed=%d" passed failed
}


let tests =
    testCaseAsync "release participant isolation assertions" <| async {
        do! runSuite ()
        Expect.equal 0 failed (String.concat Environment.NewLine failures)
    }
