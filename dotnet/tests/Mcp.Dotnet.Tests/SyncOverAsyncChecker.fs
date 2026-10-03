module Mcp.Dotnet.Tests.SyncOverAsyncChecker

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions

type Finding =
    { Path: string
      Line: int
      Column: int
      Pattern: string
      Exemption: string option }

type private Rule =
    { Name: string
      Regex: Regex }

let private rules =
    let make name pattern =
        { Name = name
          Regex = Regex(pattern, RegexOptions.Compiled ||| RegexOptions.CultureInvariant) }

    [ make "Task.Result" @"\b[A-Za-z_][A-Za-z0-9_']*\s*\.\s*Result\b"
      make "Task.Wait" @"\b[A-Za-z_][A-Za-z0-9_']*\s*\.\s*Wait\s*\("
      make "GetAwaiter().GetResult" @"GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\("
      make "Task.WaitAll" @"\bTask\s*\.\s*WaitAll\b"
      make "Task.WaitAny" @"\bTask\s*\.\s*WaitAny\b"
      make "WaitForExit" @"\b[A-Za-z_][A-Za-z0-9_']*\s*\.\s*WaitForExit\s*\("
      make "ReadToEnd" @"\b[A-Za-z_][A-Za-z0-9_']*\s*\.\s*ReadToEnd\s*\("
      make "Async.RunSynchronously" @"\bAsync\s*\.\s*RunSynchronously\b" ]

let private excludedSegments =
    Set.ofList [ "obj"; "bin"; "dist"; ".tasks"; "node_modules"; "publish" ]

let private sourceExtensions = Set.ofList [ ".fs"; ".fsx" ]

let private topLevelBridgeSites =
    Map.ofList
        [ "dotnet/BuildDistribution.fsx", "match run () |> Async.RunSynchronously with"
          "dotnet/PrepareReleasePins.fsx", "match run () |> Async.RunSynchronously with"
          "dotnet/tests/DistributionTests.fsx", "|> Async.RunSynchronously"
          "dotnet/tests/ProvenanceTests.fsx", "suite () |> Async.RunSynchronously"
          "dotnet/Host.fs", "McpHost.run service |> Async.RunSynchronously"
          "workflow/BuildDistributions.fsx", "match run () |> Async.RunSynchronously with"
          "workflow/PrepareReleasePins.fsx", "match run () |> Async.RunSynchronously with"
          "workflow/WorkflowMcp.fs", "run () |> Async.RunSynchronously"
          "workflow/tests/DistributionTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/TaskApply.fsx", "|> Async.RunSynchronously"
          "workflow/tests/TaskCreate.fsx", "match execute (CreateTask { Root = root; ProfileId = profile; Request = request }) |> Async.RunSynchronously with"
          "workflow/tests/TaskGet.fsx", "match execute (GetTask { Root = args.[0]; TaskId = args.[1] }) |> Async.RunSynchronously with"
          "workflow/tests/TaskValidate.fsx", "match execute (ValidateTask { Root = args.[0]; TaskId = args.[1] }) |> Async.RunSynchronously with"
          "workflow/tests/TaskPathSafetyTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/TaskContractTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/EphemeralLockInvariantTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/AuthorityMetadataTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/WorkflowTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/TaskProfileTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/WorkflowMcpTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/WorkflowMcpNativeCompatTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/WorkflowMcpFindingTests.fsx", "|> Async.RunSynchronously"
          "workflow/tests/PackagedRuntimeCoordinationTests.fsx", "|> Async.RunSynchronously" ]

let isExcludedPath (path: string) =
    path.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
    |> Array.exists excludedSegments.Contains

let isInScope (path: string) = sourceExtensions.Contains(Path.GetExtension path)

let private blankRange (chars: char array) startIndex endIndex =
    for i in startIndex .. endIndex - 1 do
        if chars.[i] <> '\n' && chars.[i] <> '\r' then chars.[i] <- ' '

let private maskStringsAndComments (source: string) =
    let chars = source.ToCharArray()
    let mutable i = 0

    while i < chars.Length do
        if i + 1 < chars.Length && chars.[i] = '/' && chars.[i + 1] = '/' then
            let startIndex = i
            while i < chars.Length && chars.[i] <> '\n' do i <- i + 1
            blankRange chars startIndex i
        elif i + 1 < chars.Length && chars.[i] = '(' && chars.[i + 1] = '*' then
            let startIndex = i
            i <- i + 2
            let mutable depth = 1
            while i < chars.Length && depth > 0 do
                if i + 1 < chars.Length && chars.[i] = '(' && chars.[i + 1] = '*' then
                    depth <- depth + 1
                    i <- i + 2
                elif i + 1 < chars.Length && chars.[i] = '*' && chars.[i + 1] = ')' then
                    depth <- depth - 1
                    i <- i + 2
                else
                    i <- i + 1
            blankRange chars startIndex i
        elif chars.[i] = '"' || ((chars.[i] = '$' || chars.[i] = '@') && i + 1 < chars.Length && chars.[i + 1] = '"') then
            let startIndex = i
            if chars.[i] <> '"' then i <- i + 1

            if i + 2 < chars.Length && chars.[i] = '"' && chars.[i + 1] = '"' && chars.[i + 2] = '"' then
                i <- i + 3
                let mutable closed = false
                while i + 2 < chars.Length && not closed do
                    if chars.[i] = '"' && chars.[i + 1] = '"' && chars.[i + 2] = '"' then
                        i <- i + 3
                        closed <- true
                    else
                        i <- i + 1
            else
                let verbatim = chars.[i] = '@'
                i <- i + 1
                let mutable closed = false
                while i < chars.Length && not closed do
                    if chars.[i] = '"' then
                        if verbatim && i + 1 < chars.Length && chars.[i + 1] = '"' then
                            i <- i + 2
                        elif not verbatim && i > 0 && chars.[i - 1] = '\\' then
                            i <- i + 1
                        else
                            i <- i + 1
                            closed <- true
                    else
                        i <- i + 1
            blankRange chars startIndex i
        elif chars.[i] = '\'' && i + 2 < chars.Length then
            let startIndex = i
            i <- i + 1
            if chars.[i] = '\\' then i <- i + 1
            i <- min chars.Length (i + 2)
            blankRange chars startIndex i
        else
            i <- i + 1

    String chars

let private lineColumn (text: string) offset =
    let bounded = max 0 (min offset text.Length)
    let mutable line = 1
    let mutable lineStart = 0
    for i in 0 .. bounded - 1 do
        if text.[i] = '\n' then
            line <- line + 1
            lineStart <- i + 1
    line, bounded - lineStart + 1

let private precedingResultMarker (lines: string array) lineNumber =
    if lineNumber < 2 then None
    else
        let candidate = lines.[lineNumber - 2].Trim()
        let prefix = "// Non-Task Result field:"
        if candidate.StartsWith(prefix, StringComparison.Ordinal)
           && candidate.Substring(prefix.Length).Trim().Length > 0 then
            Some(candidate.Substring(prefix.Length).Trim())
        else None

let private relativePath (root: string) (path: string) =
    Path.GetRelativePath(root, path).Replace('\\', '/')

let private documentedBridge (relative: string) (code: string) (lines: string array) (lineNumber: int) =
    let normalized = relative.Replace('\\', '/')
    let approvedLine = Map.tryFind normalized topLevelBridgeSites
    let bridgeRule = rules |> List.find (fun rule -> rule.Name = "Async.RunSynchronously")
    let bridgeLines =
        bridgeRule.Regex.Matches(code)
        |> Seq.cast<Match>
        |> Seq.map (fun matched ->
            let line, _ = lineColumn code matched.Index
            line, matched.Index)
        |> Seq.toArray

    if approvedLine.IsNone || bridgeLines.Length <> 1 || fst bridgeLines.[0] <> lineNumber then
        None
    else
        let line = lines.[lineNumber - 1].Trim()
        let bridgeShape = line = approvedLine.Value
        let documentation =
            lines
            |> Array.exists (fun comment ->
                let trimmed = comment.Trim()
                trimmed.StartsWith("//", StringComparison.Ordinal)
                && (trimmed.Contains("Standalone entry bridge", StringComparison.Ordinal)
                    || trimmed = "// FSI needs one synchronous top-level entry for the asynchronous script."))
        if bridgeShape && documentation then Some "documented standalone entry bridge" else None

let checkText (path: string) (source: string) : Finding list =
    let code = maskStringsAndComments source
    let lines = source.Replace("\r\n", "\n").Split('\n')
    let relative = path.Replace('\\', '/')

    [ for rule in rules do
          for matched in rule.Regex.Matches(code) do
              let line, column = lineColumn code matched.Index
              let exemption =
                  match rule.Name with
                  | "Task.Result" -> precedingResultMarker lines line
                  | "Async.RunSynchronously" -> documentedBridge relative code lines line
                  | _ -> None
              yield
                  { Path = path
                    Line = line
                    Column = column
                    Pattern = rule.Name
                    Exemption = exemption } ]

let checkFile path =
    if File.Exists path then
        checkText path (File.ReadAllText path)
    else []

let enumerateFiles root =
    let stack = Stack<string>()
    let files = ResizeArray<string>()
    stack.Push root

    while stack.Count > 0 do
        let path = stack.Pop()
        if not (isExcludedPath path) then
            if Directory.Exists path then
                for child in Directory.EnumerateFileSystemEntries path do
                    if not (isExcludedPath child) then stack.Push child
            elif File.Exists path && isInScope path then
                files.Add path

    files |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)) |> Seq.toList

let checkTree root =
    let canonicalRoot = Path.GetFullPath root
    enumerateFiles canonicalRoot
    |> List.collect (fun path ->
        let relative = relativePath canonicalRoot path
        checkText relative (File.ReadAllText path))

let violations findings = findings |> List.filter (fun finding -> finding.Exemption.IsNone)

let formatFinding (finding: Finding) =
    let status = if finding.Exemption.IsSome then "ALLOW" else "BLOCK"
    let reason = finding.Exemption |> Option.defaultValue "no approved reason"
    $"{status} {finding.Path}:{finding.Line}:{finding.Column} [{finding.Pattern}] -- {reason}"
