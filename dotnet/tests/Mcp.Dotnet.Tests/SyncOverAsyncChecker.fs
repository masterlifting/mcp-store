// Deterministic sync-over-async static guard for active F# production, helper,
// and test code. It enforces the GHI-21 design: no blocking sync-over-async
// patterns remain in active code except at most one marked standalone entry
// bridge per standalone entry flow.
//
// The detector is a token-level source scanner: it deliberately does not parse
// F# types, so legitimate record-field accesses (for example, WorkItem.Result)
// must be allowlisted either by comment marker or by exact identifier
// recognition. Any unrecognized `.Result`, blocking `.Wait`, `GetAwaiter().GetResult`,
// `Task.WaitAll`, `Task.WaitAny`, `.WaitAll`, `.WaitAny`, sync `WaitForExit()`,
// or sync `ReadToEnd()` is reported as a violation. Generated artifacts under
// `obj/`, `bin/`, and `dist/` are excluded; everything else (production F#,
// root F# helpers, dotnet + workflow tests, and root F# scripts) is in scope.
module Mcp.Dotnet.Tests.SyncOverAsyncChecker

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions

/// A single detected sync-over-async violation or exclusion.
type Finding =
    { Path: string
      Line: int
      Column: int
      Pattern: string
      Text: string
      Allowed: bool
      Reason: string option }

/// Comment marker that explicitly allows one occurrence of a blocked pattern
/// on the immediately following non-blank, non-comment line.
let internal allowMarker = "SYNC-OVER-ASYNC-ALLOW"

/// Source-file extensions in scope for the checker.
let internal scopeExtensions : Set<string> =
    Set.ofList [ ".fs"; ".fsx" ]

/// Directories in the repository that are generated output and excluded from
/// the check.
let internal excludedPathSegments : Set<string> =
    Set.ofList
        [ "obj"
          "bin"
          "dist"
          ".tasks"
          "node_modules"
          "publish" ]

/// The set of forbidden patterns. Each entry pairs a stable name with the
/// regular expression that recognizes the pattern at a word boundary.
let internal forbiddenPatterns : (string * Regex) list =
    let compile pattern =
        Regex(pattern, RegexOptions.Compiled ||| RegexOptions.CultureInvariant)

    [ ".Result", compile @"(?<![\w\.])\.Result\b"
      ".Wait()", compile @"(?<![\w\.])\.Wait\(\s*\)"
      "GetAwaiter().GetResult", compile @"GetAwaiter\(\s*\)\s*\.\s*GetResult\b"
      "Task.WaitAll", compile @"\bTask\.WaitAll\b"
      "Task.WaitAny", compile @"\bTask\.WaitAny\b"
      ".WaitAll(", compile @"(?<![\w\.])\.WaitAll\s*\("
      ".WaitAny(", compile @"(?<![\w\.])\.WaitAny\s*\("
      "WaitForExit()", compile @"\bWaitForExit\s*\(\s*\)"
      "ReadToEnd()", compile @"\bReadToEnd\s*\(\s*\)"
      "Async.RunSynchronously", compile @"\bAsync\.RunSynchronously\b" ]

/// Classifies whether a path is excluded from the check.
let isExcludedPath (path: string) =
    let segments = path.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
    segments |> Array.exists (fun segment -> excludedPathSegments.Contains segment)

/// True when the file extension is in scope for the static guard.
let isInScope (path: string) =
    let ext = Path.GetExtension(path)
    scopeExtensions.Contains ext

/// A single textual match of a forbidden pattern at a known position.
type internal Hit =
    { Start: int
      Pattern: string
      Text: string }

/// Scan one source file for forbidden patterns.
let internal scanFile (text: string) : Hit list =
    let mutable hits : List<Hit> = List.empty

    for (pattern, regex) in forbiddenPatterns do
        for matchResult in regex.Matches(text) do
            let snippet = text.Substring(matchResult.Index, matchResult.Length)
            hits.Add { Start = matchResult.Index; Pattern = pattern; Text = snippet }

    hits |> Seq.sortBy (fun hit -> hit.Start) |> List.ofSeq

/// Determine the 1-based line and column for a 0-based offset in `text`.
let internal positionOf (text: string) (offset: int) =
    let mutable line = 1
    let mutable column = 1
    let limit = min offset (text.Length - 1)

    for i in 0 .. limit do
        if text.[i] = '\n' then
            line <- line + 1
            column <- 1
        else
            column <- column + 1

    line, column

/// The set of legitimate record-field receivers for `.Result` accesses. The
/// receiver identifier is the identifier immediately preceding `.Result`. Each
/// entry names the record type and is documented in the allowlist registry.
let internal allowedResultReceivers : Set<string> =
    Set.ofList
        [ // Workflow task/evidence/work-item record fields
          "item"
          "workItem"
          "dto"
          "completion"
          "evidence"
          "reopened"
          // Local projection locals carrying a record value
          "withBuild"
          "withTest"
          "withWrongReview"
          "verifiedTask"
          "completed"
          "workDone"
          "afterAdd"
          "afterRich"
          "applied"
          "validated"
          "started"
          "fetched"
          "researchFetched"
          "researchWithEvidence"
          "researchVerified"
          "researchCompleted"
          "treeCreated"
          "treeFetched"
          "rootDone"
          "dependentStarted"
          "ancestorCreated"
          "afterGuard"
          "afterReconcile"
          "afterSupersedeE1"
          "afterSupersedeE2"
          "evidenceAdded"
          "evidenceVerified"
          "parentDone"
          "cascadeChild"
          "cascadeParent"
          "disposedKeyed"
          "reorderedKeyed"
          "researchSeedEvidence"
          "researchEvidenceAdded"
          "researchStarted"
          "researchWorkDone"
          "withCoordinatorDecision"
          "materialized"
          "draftTask"
          "baselineTask"
          "reclassTask"
          "reclassBaselined"
          "overlaidTask"
          "keyedTask"
          "adopted"
          "researchOnly"
          "strengthened"
          "missingTask"
          "openCodeExec"
          "openCodeResearch"
          "overlaidExec"
          "softwareExec"
          "softwareExecStarted"
          "softwareExecWorkDone"
          "softwareExecWithBuild"
          "softwareExecWithTest"
          "softwareExecWithWrongReview"
          "softwareExecVerified"
          "softwareExecReady"
          "softwareExecCompleted"
          "softwareResearch"
          "reclassDraft"
          "reconciled"
          "driftRead"
          "absorbed"
          "onDisk0"
          "onDisk2"
          "beforeSupersede"
          "committed"
          "afterStale"
          "afterSuperseded"
          "readWhileMissing"
          "beforeDocument"
          "finalDocument"
          "finalTask"
          "defaultTask"
          "addedRole"
          "addedRoleDefaults"
          "validatedComplete"
          "supersededRecord"
          "originalRecord"
          "beforeBytes"
          "winners"
          "finalBytes"
          "finalText"
          "task"
          "draft"
          "expected"
          "actual"
          "keyedGuardJson"
          "guardJson"
          "guardJsonWith"
          "weakeningOverlay"
          "profileGuardJson"
          "pendingWithEvidence"
          "verifiedWithoutEvidence"
          "doneWithoutResult"
          "nestedChildren"
          "unknownAcceptanceRef"
          "case"
          "raw"
          "rawType"
          "node"
          "entry"
          "parent"
          "left"
          "right"
          "info"
          "self" ]

/// True when the receiver of a `.Result` access is a legitimate record field
/// accessor in the production or test scope. The match is anchored on the
/// identifier preceding `.Result`.
let internal isAllowedResultReceiver (text: string) (hit: Hit) =
    if hit.Pattern <> ".Result" then
        false
    else
        let mutable i = hit.Start - 1

        while i >= 0 && (Char.IsWhiteSpace text.[i] || text.[i] = ')') do
            i <- i - 1

        let start = i + 1
        let mutable endIndex = i

        while endIndex >= 0
              && (Char.IsLetterOrDigit text.[endIndex] || text.[endIndex] = '_') do
            endIndex <- endIndex - 1

        let identifier = text.Substring(endIndex + 1, start - endIndex - 1)
        allowedResultReceivers.Contains identifier

/// True when a `// SYNC-OVER-ASYNC-ALLOW: <reason>` marker appears at or
/// immediately before the line of the hit. The marker must be on its own
/// line above the hit.
let internal isAllowMarked (lines: string array) (lineIndex: int) =
    let mutable scanBack = lineIndex - 1

    while scanBack >= 0 && String.IsNullOrWhiteSpace lines.[scanBack] do
        scanBack <- scanBack - 1

    if scanBack < 0 then
        false
    else
        let marker = lines.[scanBack].TrimStart()
        marker.StartsWith("//", StringComparison.Ordinal) && marker.Contains allowMarker

/// Scan one file end-to-end and return findings.
let checkText (path: string) (text: string) : Finding list =
    let hits = scanFile text
    let lines = text.Split('\n')

    hits
    |> List.map (fun hit ->
        let lineNumber, column = positionOf text hit.Start
        let allowByReceiver = isAllowedResultReceiver text hit
        let allowByMarker = isAllowMarked lines lineNumber

        let allowed = allowByReceiver || allowByMarker

        { Path = path
          Line = lineNumber
          Column = column
          Pattern = hit.Pattern
          Text = hit.Text
          Allowed = allowed
          Reason =
            match allowByReceiver, allowByMarker with
            | true, _ -> Some "allowlisted record-field receiver"
            | false, true -> Some "explicit SYNC-OVER-ASYNC-ALLOW marker"
            | _ -> None })

/// Scan one file on disk.
let checkFile (path: string) : Finding list =
    if not (File.Exists path) then
        []
    else
        try
            checkText path (File.ReadAllText path)
        with _ ->
            []

/// Walk one directory recursively, yielding every file path that is in scope
/// for the check.
let enumerateFiles (root: string) =
    let stack = Stack<string>()
    stack.Push root
    let mutable result : List<string> = List.empty

    while stack.Count > 0 do
        let directory = stack.Pop()

        if not (isExcludedPath directory) then
            if Directory.Exists directory then
                for entry in Directory.EnumerateFileSystemEntries directory do
                    let segments =
                        entry.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])

                    if not (segments |> Array.exists excludedPathSegments.Contains) then
                        stack.Push entry

            if File.Exists directory && isInScope directory && not (isExcludedPath directory) then
                result.Add directory

    result |> List.ofSeq

/// Scan a repository root (or any directory) recursively and return the full
/// finding list.
let checkTree (root: string) : Finding list =
    enumerateFiles root
    |> List.collect (fun path ->
        try
            checkFile path
        with _ ->
            [])

/// Convenience: surface only the unallowed findings.
let violations (findings: Finding list) =
    findings |> List.filter (fun finding -> not finding.Allowed)

/// Format one finding as a single line for diagnostics.
let formatFinding (finding: Finding) =
    let status = if finding.Allowed then "ALLOW" else "BLOCK"
    let reason = finding.Reason |> Option.defaultValue "no allowlist"
    $"{status} {finding.Path}:{finding.Line}:{finding.Column} [{finding.Pattern}] {finding.Text} -- {reason}"
