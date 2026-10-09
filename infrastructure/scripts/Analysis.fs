// Discovers owned .fsx scripts under dynamic roots and validates role, closure, and graph contracts.
// Reusable and test-helper roots typecheck through FCS public surface only; scripts are never evaluated.
// `#load` discovery walks the FCS tokenizer with state carried across lines so multi-line strings or comments bracket correctly.
module Validation.Analysis

open System
open System.IO
open System.Text
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open FSharp.Compiler.Tokenization

type Role =
    | ReusableModule
    | EntrypointCommand
    | TestHelper
    | TestEntrypoint
    | ReleaseBuildEntrypoint

module Role =
    let toString role =
        match role with
        | ReusableModule -> "reusable module/helper"
        | EntrypointCommand -> "entrypoint/command"
        | TestHelper -> "test helper"
        | TestEntrypoint -> "test entrypoint"
        | ReleaseBuildEntrypoint -> "release/build entrypoint"

    let fromString (text: string) =
        match text.Trim().ToLowerInvariant() with
        | "reusable module/helper" -> Some ReusableModule
        | "entrypoint/command" -> Some EntrypointCommand
        | "test helper" -> Some TestHelper
        | "test entrypoint" -> Some TestEntrypoint
        | "release/build entrypoint" -> Some ReleaseBuildEntrypoint
        | _ -> None

type Script =
    { RepositoryPath: string
      AbsolutePath: string
      Role: Role
      Loads: string list }

type DiscoveryRoot =
    { Name: string
      AbsolutePath: string }

type Issue =
    { Code: string
      Script: string
      Detail: string }

type Inventory =
    { Scripts: Script list
      Roots: DiscoveryRoot list
      // Discovery-time issues (e.g. reparse points encountered during the walk) so they surface as `Errors` even when `parseScripts` is not invoked.
      DiscoveryIssues: Issue list }

type Result<'T> =
    | Ok of 'T
    | Errors of Issue list

// Canonicalize and reject traversal; trim trailing separators so containment checks are input-shape independent.
let canonicalPath (path: string) =
    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

let forwardSlashes (path: string) = path.Replace('\\', '/')

let repositoryRelative (repoRoot: string) (absolutePath: string) =
    let canonicalRoot = canonicalPath repoRoot
    let canonicalValue = canonicalPath absolutePath
    let relative = Path.GetRelativePath(canonicalRoot, canonicalValue)
    forwardSlashes relative

let isInside (parent: string) (child: string) =
    let canonicalParent = canonicalPath parent
    let canonicalChild = canonicalPath child
    let relative = Path.GetRelativePath(canonicalParent, canonicalChild)
    let parts = relative.Split(Path.DirectorySeparatorChar, Path.PathSeparator)
    parts |> Array.forall (fun segment -> segment <> ".." && segment <> "")

// Generated directories excluded from every discovery root at any depth. `.tasks` is platform-owned Task Runtime state (see .gitignore and AGENTS.md "durable task records and historical evidence"); it is non-owned for this analyzer, not a generated build output.
let excludedDirectoryNames = Set.ofList [ "bin"; "obj"; "dist"; ".git"; ".tasks" ]

let private isExcludedDirectory (path: string) =
    excludedDirectoryNames.Contains(Path.GetFileName path)

// True when the filesystem entry is a symbolic link or reparse point; the analyzer must not follow such entries because they may escape the repository or trigger external resolution at FCS-follow time. Fail-closed: any IO attribute failure raises so the caller can decide, not a silent `false`.
let private isReparsePoint (path: string) =
    File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)

// Reject rooted or fully-qualified `#load` paths explicitly before resolution/typechecking: `Path.IsPathRooted` covers drive-root (`C:\foo`), drive-relative (`\foo`), UNC (`\\server\share`), and absolute (`/foo`) on the current platform; relative `../foo` cross-directory traversal stays inside the repository and is allowed.
let private isRootedLoadPath (value: string) : bool =
    if String.IsNullOrEmpty value then
        false
    else
        Path.IsPathRooted value

// True when any path component between the canonical root and the file (inclusive) is a reparse point. Physical containment is required because lexical `Path.GetFullPath` does not resolve symbolic links and a directory-level link would let a load target reach outside the repository.
let private hasReparseComponent (canonicalRoot: string) (filePath: string) : bool =
    let stop = canonicalPath canonicalRoot
    let mutable node = canonicalPath filePath
    let mutable found = false

    while not found
          && not (String.Equals(node, stop, StringComparison.OrdinalIgnoreCase)) do
        if isReparsePoint node then
            found <- true
        else
            let parent = Path.GetDirectoryName node

            if String.IsNullOrEmpty parent || parent = node then
                node <- stop
            else
                node <- parent

    found

let private enumerateFiles (root: DiscoveryRoot) =
    let absoluteRoot = canonicalPath root.AbsolutePath
    let accumulator = ResizeArray<string>()
    let linkIssues = ResizeArray<Issue>()

    // Fail closed: an IO attribute failure surfaces to the caller instead of being treated as "no link".
    let rootIsLink, rootExists =
        let exists = File.Exists absoluteRoot || Directory.Exists absoluteRoot
        let link = exists && isReparsePoint absoluteRoot
        link, exists

    if rootExists && rootIsLink then
        linkIssues.Add(
            { Code = "SYMLINK_REPARSE"
              Script = root.Name
              Detail = "discovery root is a symbolic link or reparse point and is not followed: " + absoluteRoot }
        )
    elif rootExists then
        let rec walk (current: string) =
            if not (Directory.Exists current) then
                ()
            else
                for entry in Directory.EnumerateFileSystemEntries(current) do
                    if not (isExcludedDirectory entry) then
                        if isReparsePoint entry then
                            // Directory link: do not follow, surface as a discovery diagnostic so the analysis fails closed on directory/file links.
                            let relative = repositoryRelative absoluteRoot entry
                            linkIssues.Add(
                                { Code = "SYMLINK_REPARSE"
                                  Script = relative
                                  Detail = "directory is a symbolic link or reparse point and is not followed: " + entry }
                            )
                        elif Directory.Exists entry then
                            walk entry

                for file in Directory.EnumerateFiles(current, "*.fsx") do
                    if isReparsePoint file then
                        let relative = repositoryRelative absoluteRoot file
                        linkIssues.Add(
                            { Code = "SYMLINK_REPARSE"
                              Script = relative
                              Detail = "file is a symbolic link or reparse point and is not followed: " + file }
                        )
                    else
                        let canonicalFile = canonicalPath file

                        if isInside absoluteRoot canonicalFile then
                            accumulator.Add canonicalFile

        walk absoluteRoot

    accumulator |> Seq.sort |> Seq.toList, linkIssues |> Seq.toList

// The repository root is itself a discovery root; supplied roots are deduplicated by absolute path so a file under any root is inventoried exactly once.
let private inventoryFiles (canonicalRepoRoot: string) (roots: DiscoveryRoot list) =
    let mutable byAbsolute = Map.empty<string, string>
    let discoveryIssues = ResizeArray<Issue>()

    for root in roots do
        let files, rootIssues = enumerateFiles root

        for issue in rootIssues do
            discoveryIssues.Add issue

        for file in files do
            let canonicalFile = canonicalPath file
            let relative = repositoryRelative canonicalRepoRoot canonicalFile
            byAbsolute <- Map.add canonicalFile relative byAbsolute

    let scripts =
        byAbsolute
        |> Map.toList
        |> List.sortBy snd
        |> List.map (fun (absolute, relative) ->
            { RepositoryPath = relative
              AbsolutePath = absolute
              Role = ReusableModule
              Loads = [] })

    scripts, discoveryIssues |> Seq.toList

let discoverScripts (repoRoot: string) (roots: DiscoveryRoot list) : Inventory =
    let canonicalRepoRoot = canonicalPath repoRoot
    let scripts, discoveryIssues = inventoryFiles canonicalRepoRoot roots
    { Scripts = scripts
      Roots = roots
      DiscoveryIssues = discoveryIssues }

// The FCS hash-line lexer consumes the directive identifier without emitting it as a token; only the raw source substring between the HASH and the next non-identifier character is authoritative.
let private directiveIdent (line: string) (hashToken: FSharpTokenInfo) =
    let startIndex = hashToken.RightColumn + 1

    if startIndex >= line.Length then
        None
    else
        let mutable index = startIndex

        while index < line.Length
              && (Char.IsWhiteSpace line.[index]) do
            index <- index + 1

        let identStart = index

        while index < line.Length
              && (Char.IsLetterOrDigit line.[index] || line.[index] = '_') do
            index <- index + 1

        let identEnd = index

        if identEnd > identStart then
            Some(line.Substring(identStart, identEnd - identStart))
        else
            None

// One decoded #load directive: every literal argument (regular or verbatim) as a path, or the offending detail when the directive is unsupported (e.g. `#load pathValue`). All decoding stays on the FCS public tokenizer surface.
type private LoadDirective =
    | LoadedPaths of string list
    | Unsupported of string

// Walk tokens after the `#load` identifier; collect a path for each contiguous STRING/STRING_TEXT group separated by whitespace, and reject any non-string argument so unsupported forms surface as diagnostics instead of silently disappearing.
let private decodeLoadDirective (tokens: FSharpTokenInfo[]) (line: string) (startIndex: int) =
    let paths = ResizeArray<string>()
    let mutable current : StringBuilder option = None
    let mutable unsupported : string option = None
    let mutable j = startIndex

    while j < tokens.Length && unsupported.IsNone do
        let tok = tokens.[j]
        let name = string tok.TokenName

        if String.Equals(name, "STRING", StringComparison.Ordinal) then
            // Closing quote finalizes the current argument; an immediate pair with no content emits an empty path.
            match current with
            | Some builder -> paths.Add(builder.ToString())
            | None -> paths.Add("")
            current <- None
        elif String.Equals(name, "STRING_TEXT", StringComparison.Ordinal) then
            match current with
            | None ->
                // First STRING_TEXT of an argument is the opening token: a single `"` for regular, `@"` for verbatim. The prefix character (`@`) is verbatim syntax, not path content.
                let verbatim =
                    tok.LeftColumn < line.Length && line.[tok.LeftColumn] = '@'
                let skip = if verbatim then 2 else 1
                let contentStart = tok.LeftColumn + skip
                let contentEnd =
                    min tok.RightColumn (line.Length - 1)

                if contentStart <= contentEnd && contentStart < line.Length then
                    let length = contentEnd - contentStart + 1
                    current <- Some(StringBuilder(line.Substring(contentStart, length)))
                else
                    current <- Some(StringBuilder())
            | Some builder ->
                let safeStart = max 0 tok.LeftColumn
                let safeEnd = min tok.RightColumn (line.Length - 1)
                let length = safeEnd - safeStart + 1

                if length > 0 then
                    builder.Append(line.Substring(safeStart, length)) |> ignore
        elif String.Equals(name, "WHITESPACE", StringComparison.Ordinal) then
            // Whitespace ends the current argument; the next STRING starts the next path.
            current <- None
        else
            unsupported <- Some("#load argument uses unsupported token '" + name + "'; only regular or verbatim string literals are accepted")

        j <- j + 1

    match unsupported with
    | Some detail -> LoadDirective.Unsupported detail
    | None -> LoadDirective.LoadedPaths(paths |> Seq.toList)

// Scan every directive the FCS tokenizer recognizes; HASH tokens inside strings or comments are reclassified by FCS so the carried-in lex state is the only filter needed (whitespace-indented directives are accepted). Pre-flight detection of `#r` and `#I` lets the analyzer reject external-resolution forms before FCS follows them at typecheck time.
type private DirectiveScan =
    { Loads: string list
      References: string list
      Includes: string list
      Unsupported: string list }

let private findDirectives (source: string) : DirectiveScan =
    let checker = FSharpChecker.Create()
    let lines = source.Split('\n')
    let loads = ResizeArray<string>()
    let references = ResizeArray<string>()
    let includes = ResizeArray<string>()
    let unsupported = ResizeArray<string>()
    let mutable lexState = FSharpTokenizerLexState.Initial
    // Module-body `#load` is a syntactic artifact in F# that FSI ignores; tracking `module X =` indentation keeps file-scope directives distinct from nested ones so the analyzer mirrors FSI semantics.
    let mutable activeModuleIndent : int option = None

    for lineIndex in 0 .. lines.Length - 1 do
        let line = lines.[lineIndex]
        let trimmed = line.TrimStart()
        let lineIndent = line.Length - trimmed.Length

        let tokens, finalState = checker.TokenizeLine(line, lexState)
        lexState <- finalState

        let mutable i = 0

        while i < tokens.Length do
            let current = tokens.[i]

            if String.Equals(current.TokenName, "HASH", StringComparison.Ordinal) then
                let hashInsideModuleBody =
                    match activeModuleIndent with
                    | Some mIndent -> current.LeftColumn > mIndent
                    | None -> false

                match hashInsideModuleBody with
                | true ->
                    unsupported.Add("`#load` inside module body at line " + (lineIndex + 1).ToString() + " is not a real FSI load directive")
                | false ->
                    match directiveIdent line current with
                    | Some ident ->
                        match decodeLoadDirective tokens line (i + 1) with
                        | LoadDirective.LoadedPaths paths when String.Equals(ident, "load", StringComparison.Ordinal) ->
                            for path in paths do
                                loads.Add path
                        | LoadDirective.LoadedPaths paths when String.Equals(ident, "r", StringComparison.Ordinal) ->
                            for path in paths do
                                references.Add path
                        | LoadDirective.LoadedPaths paths when String.Equals(ident, "I", StringComparison.Ordinal) ->
                            for path in paths do
                                includes.Add path
                        | LoadDirective.Unsupported detail when String.Equals(ident, "load", StringComparison.Ordinal) ->
                            unsupported.Add detail
                        | LoadDirective.Unsupported detail ->
                            unsupported.Add(ident + ": " + detail)
                        | _ -> ()
                    | None -> ()

            i <- i + 1

        // Update module-body state after the line is processed.
        let opensModuleBody =
            trimmed.StartsWith("module ", StringComparison.Ordinal)
            && (trimmed.EndsWith(" =", StringComparison.Ordinal)
                || trimmed.EndsWith(" begin", StringComparison.Ordinal))

        if opensModuleBody then
            activeModuleIndent <- Some lineIndent
        elif activeModuleIndent.IsSome
             && not (String.IsNullOrEmpty trimmed)
             && lineIndent <= activeModuleIndent.Value then
            activeModuleIndent <- None

    { Loads = loads |> Seq.sort |> Seq.toList
      References = references |> Seq.sort |> Seq.toList
      Includes = includes |> Seq.sort |> Seq.toList
      Unsupported = unsupported |> Seq.toList }

// FCS parse errors are surfaced independently of the typecheck pass because the script-aware parser only raises severity-error diagnostics for incomplete or malformed source. Missing/invalid source must fail closed: any exception propagates to the caller instead of being swallowed into an empty success that masks the actual defect.
let private parseSource (path: string) : Async<FSharpDiagnostic list * DirectiveScan> =
    async {
        let checker = FSharpChecker.Create()
        let parsingOptions, _ =
            checker.GetParsingOptionsFromCommandLineArgs(
                [],
                isInteractive = true,
                isEditing = false
            )

        let sourceText = File.ReadAllText path
        let source = SourceText.ofString sourceText
        // `ParseFile` requires `SourceFiles` to be non-empty; the default options come back empty so the declaring script is registered explicitly here.
        let! parseResult =
            checker.ParseFile(path, source, { parsingOptions with SourceFiles = [| path |] })

        let diagnostics =
            parseResult.Diagnostics
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
            |> Array.toList

        let scan = findDirectives sourceText

        return diagnostics, scan
    }

// Drop a leading UTF-8 BOM so line 0 of the strip is the authoritative first-line role header; a BOM in front of `// role:` would otherwise mis-anchor the canonical header search.
let private stripBom (source: string) =
    if source.Length > 0 && source.[0] = '﻿' then
        source.Substring 1
    else
        source

// Walk every line via the FCS tokenizer so `// role:` text inside F# strings is not promoted to a declaration; the first non-BOM line is the canonical header and any further declaration in the file is a duplicate or conflict. FCS splits a single line comment into multiple `LINE_COMMENT` tokens (one per character group), so the per-line scan concatenates them by `LeftColumn` order before applying the `// role:` check.
let private parseRoleHeader (path: string) : Result<Role> =
    let source = File.ReadAllText path
    let stripped = stripBom source
    let lines = stripped.Split('\n')

    let checker = FSharpChecker.Create()
    let mutable lexState = FSharpTokenizerLexState.Initial
    let declarations = ResizeArray<int * string>()

    for lineIndex in 0 .. lines.Length - 1 do
        let line = lines.[lineIndex]
        let tokens, finalState = checker.TokenizeLine(line, lexState)
        lexState <- finalState

        let commentParts = ResizeArray<FSharpTokenInfo>()

        for token in tokens do
            if String.Equals(string token.TokenName, "LINE_COMMENT", StringComparison.Ordinal) then
                commentParts.Add token

        if commentParts.Count > 0 then
            let ordered =
                commentParts
                |> Seq.sortBy (fun t -> t.LeftColumn)
                |> Seq.toList

            let builder = StringBuilder()

            for token in ordered do
                let startCol = max 0 token.LeftColumn
                let endCol = min token.RightColumn (line.Length - 1)

                if startCol <= endCol && startCol < line.Length then
                    builder.Append(line.Substring(startCol, endCol - startCol + 1)) |> ignore

            let commentText = builder.ToString()
            let marker = "// role:"

            if commentText.StartsWith(marker, StringComparison.Ordinal) then
                let value = commentText.Substring(marker.Length).Trim()
                declarations.Add(lineIndex, value)

    let firstLineDecl =
        declarations |> Seq.tryFind (fun (lineIndex, _) -> lineIndex = 0)

    match firstLineDecl with
    | None ->
        let detail =
            if Seq.isEmpty declarations then
                "the script does not declare a role header (`// role: ...`)"
            else
                "the script does not declare a role header on the first line"

        Errors
            [ { Code = "MISSING_ROLE"
                Script = path
                Detail = detail } ]
    | Some (_, firstValue) ->
        match Role.fromString firstValue with
        | None ->
            let detail =
                "the role header '" + firstValue + "' is not one of the five issue roles"

            Errors
                [ { Code = "AMBIGUOUS_ROLE"
                    Script = path
                    Detail = detail } ]
        | Some role ->
            let others =
                declarations
                |> Seq.filter (fun (lineIndex, _) -> lineIndex <> 0)
                |> Seq.map snd
                |> Seq.toList

            if others.IsEmpty then
                Ok role
            else
                let allValues = firstValue :: others
                let summary = allValues |> List.map (sprintf "'%s'") |> String.concat "; "

                Errors
                    [ { Code = "AMBIGUOUS_ROLE"
                        Script = path
                        Detail = "the script declares more than one role header: " + summary } ]

// .fs project source loaded from a test entrypoint is out of script-inventory scope; the cycle and missing-dependency checks ignore it but its existence is still asserted.
let private isProjectSourceLoad (path: string) =
    path.EndsWith(".fs", StringComparison.Ordinal)
    && not (path.EndsWith(".fsx", StringComparison.Ordinal))

// Transitively scan `.fs` load targets for external-resolution directives (`#r` / `#I`); these would otherwise be silently followed by FCS during typecheck of the declared closure. File is read directly without invoking FCS so package resolution cannot occur.
let rec private scanTransitiveExternalDirectives (canonicalRepoRoot: string) (filePath: string) (visited: Set<string>) : Issue list =
    if Set.contains filePath visited then
        []
    elif not (File.Exists filePath) then
        []
    else
        let visited' = visited.Add filePath
        let sourceText = File.ReadAllText filePath
        let scan = findDirectives sourceText
        let relative = repositoryRelative canonicalRepoRoot filePath

        let externalIssues =
            [ for _ in scan.References do
                  { Code = "EXTERNAL_REFERENCE"
                    Script = relative
                    Detail = "#r external reference is forbidden; the analyzer does not call package resolution" }
              for _ in scan.Includes do
                  { Code = "EXTERNAL_INCLUDE"
                    Script = relative
                    Detail = "#I external include is forbidden; the analyzer does not call package resolution" } ]

        let scriptDirectory = Path.GetDirectoryName filePath

        let transitive =
            [ for load in scan.Loads do
                  // Apply the same script-relative policy to ordinary `.fs` source edges so a transitive load cannot escape the invariant through a project source chain.
                  if isRootedLoadPath load then
                      let detail =
                          "#load path must be script-relative; rooted or fully-qualified paths are rejected: " + load

                      yield
                          { Code = "ROOTED_LOAD_PATH"
                            Script = relative
                            Detail = detail }
                  else
                      let resolved = canonicalPath(Path.Combine(scriptDirectory, load))

                      if isProjectSourceLoad (Path.GetFileName load) && isInside canonicalRepoRoot resolved then
                          yield! scanTransitiveExternalDirectives canonicalRepoRoot resolved visited'
                      else
                          () ]

        externalIssues @ transitive

let parseScripts (repoRoot: string) (inventory: Inventory) : Async<Result<Inventory>> =
    async {
        let canonicalRepoRoot = canonicalPath repoRoot
        let issues = ResizeArray<Issue>()
        let scripts = ResizeArray<Script>()

        for script in inventory.Scripts do
            match parseRoleHeader script.AbsolutePath with
            | Errors headerIssues ->
                for issue in headerIssues do
                    issues.Add issue
            | Ok role ->
                let! parseDiagnostics, scan = parseSource script.AbsolutePath

                for diagnostic in parseDiagnostics do
                    issues.Add(
                        { Code = "PARSE_ERROR"
                          Script = script.RepositoryPath
                          Detail = diagnostic.Message }
                    )

                // Unsupported #load forms (e.g. `#load pathValue`) must surface as a diagnostic; the per-script `Loads` list intentionally does not dedup typechecker source strings because FSI re-evaluates repeated loads, only the structural graph canonicalizes identity.
                for detail in scan.Unsupported do
                    issues.Add(
                        { Code = "UNSUPPORTED_LOAD_FORM"
                          Script = script.RepositoryPath
                          Detail = detail }
                    )

                // External-resolution directive forms (`#r` / `#I`) must be rejected before FCS follows them at typecheck time; the static scan never invokes package resolution.
                for _ in scan.References do
                    issues.Add(
                        { Code = "EXTERNAL_REFERENCE"
                          Script = script.RepositoryPath
                          Detail = "#r external reference is forbidden; the analyzer does not call package resolution" }
                    )

                for _ in scan.Includes do
                    issues.Add(
                        { Code = "EXTERNAL_INCLUDE"
                          Script = script.RepositoryPath
                          Detail = "#I external include is forbidden; the analyzer does not call package resolution" }
                    )

                let scriptDirectory = canonicalPath(Path.GetDirectoryName script.AbsolutePath)
                let loads = ResizeArray<string>()
                let mutable seenRelative = Set.empty
                let mutable transitiveVisited = Set.empty

                for value in scan.Loads do
                    // Reject rooted or fully-qualified paths before any resolution or typechecking step; cross-directory `../foo` traversal stays script-relative and is allowed.
                    if isRootedLoadPath value then
                        let detail = "#load path must be script-relative; rooted or fully-qualified paths are rejected: " + value

                        issues.Add(
                            { Code = "ROOTED_LOAD_PATH"
                              Script = script.RepositoryPath
                              Detail = detail }
                        )
                    else
                        let combined = Path.Combine(scriptDirectory, value)
                        let resolvedPath = canonicalPath combined

                        // #load must stay inside the repository root, not the declaring script's directory, so cross-subtree `../domain/*.fs` loads are allowed.
                        if isInside canonicalRepoRoot resolvedPath |> not then
                            let detail = "#load resolves outside the repository root: " + resolvedPath

                            issues.Add(
                                { Code = "DIRECTORY_ESCAPE"
                                  Script = script.RepositoryPath
                                  Detail = detail }
                            )
                        else
                            let relative = repositoryRelative canonicalRepoRoot resolvedPath

                            // The declared load target's existence is verified even when the target is out of role scope.
                            if not (File.Exists resolvedPath) then
                                let detail =
                                    "#load target file is missing on disk: " + relative

                                issues.Add(
                                    { Code = "MISSING_LOAD_TARGET"
                                      Script = script.RepositoryPath
                                      Detail = detail }
                                )

                            // Reparse-point / symlink load targets and parent-component links are rejected before any FCS read so the analyzer never follows an external-resolution link. The dependency also surfaces as `MISSING_DEPENDENCY` because the inventory walk does not follow reparse points, so a downstream `detectCycles` / validation sees a coherent inventory state.
                            if File.Exists resolvedPath && (isReparsePoint resolvedPath || hasReparseComponent canonicalRepoRoot resolvedPath) then
                                issues.Add(
                                    { Code = "SYMLINK_REPARSE"
                                      Script = script.RepositoryPath
                                      Detail = "#load target or a path component is a symbolic link or reparse point and is not followed: " + relative }
                                )

                                issues.Add(
                                    { Code = "MISSING_DEPENDENCY"
                                      Script = relative
                                      Detail = "declared #load target is not in the inventory because a path component is a symbolic link or reparse point: " + relative }
                                )

                            // Transitive `.fs` load chain: pre-flight scan for `#r` / `#I` so FCS does not silently follow external-resolution directives in project source.
                            if isProjectSourceLoad relative && Set.contains resolvedPath transitiveVisited |> not then
                                for transitiveIssue in
                                    scanTransitiveExternalDirectives canonicalRepoRoot resolvedPath transitiveVisited do
                                    issues.Add transitiveIssue
                                transitiveVisited <- transitiveVisited.Add resolvedPath

                            if Set.contains relative seenRelative |> not then
                                seenRelative <- seenRelative.Add relative

                                if not (isProjectSourceLoad relative) then
                                    loads.Add relative

                scripts.Add(
                    { script with
                        Role = role
                        Loads = loads |> Seq.sort |> Seq.toList }
                )

        let combined =
            [ yield! inventory.DiscoveryIssues
              yield! issues |> Seq.toList ]

        let sortedScripts = scripts |> Seq.sortBy _.RepositoryPath |> Seq.toList

        return
            if combined.IsEmpty then
                Ok({ Scripts = sortedScripts
                     Roots = inventory.Roots
                     DiscoveryIssues = inventory.DiscoveryIssues })
            else
                Errors combined
    }

// Cycle detection over the sorted load graph; the offending cycle is returned in canonical lexical order for deterministic output.
let detectCycles (scripts: Script list) : Result<unit> =
    let byPath = scripts |> List.map (fun s -> s.RepositoryPath, s) |> Map.ofList
    let separator = " -> "
    let mutable issues = ResizeArray<Issue>()
    let mutable visited = Set.empty

    let rec dfs (path: string) (stack: string list) =
        if List.contains path stack then
            let cycleStart = List.findIndex (fun value -> value = path) stack
            let cycle = List.skip cycleStart stack @ [ path ]
            let sortedCycle = cycle |> List.sort
            let joined = String.concat separator sortedCycle

            issues.Add(
                { Code = "DEPENDENCY_CYCLE"
                  Script = path
                  Detail = "#load cycle: " + joined }
            )
        elif Set.contains path visited |> not then
            visited <- visited.Add path

            match Map.tryFind path byPath with
            | Some script ->
                for load in script.Loads do
                    dfs load (path :: stack)
            | None ->
                if not (isProjectSourceLoad path) then
                    let detail = "declared #load target is not in the inventory: " + path

                    issues.Add(
                        { Code = "MISSING_DEPENDENCY"
                          Script = path
                          Detail = detail }
                    )

    for script in scripts |> List.sortBy _.RepositoryPath do
        dfs script.RepositoryPath []

    if issues.Count = 0 then Ok() else Errors(issues |> Seq.toList)

let private closure (scripts: Script list) (roots: string list) : Set<string> =
    let byPath = scripts |> List.map (fun s -> s.RepositoryPath, s) |> Map.ofList
    let mutable acc = Set.empty

    let rec visit path =
        if Set.contains path acc |> not then
            acc <- acc.Add path

            match Map.tryFind path byPath with
            | Some script ->
                for load in script.Loads do
                    visit load
            | None -> ()

    for root in roots do
        visit root

    acc

// A reusable closure must not transitively depend on an entrypoint or release script: helpers are pure libraries and entrypoints are effectful.
let validateReusableClosures (scripts: Script list) : Result<unit> =
    let byPath = scripts |> List.map (fun s -> s.RepositoryPath, s) |> Map.ofList
    let reusableRoots =
        scripts
        |> List.filter (fun s -> s.Role = ReusableModule || s.Role = TestHelper)
        |> List.map _.RepositoryPath

    let mutable issues = ResizeArray<Issue>()

    for root in reusableRoots |> List.sort do
        for memberPath in closure scripts [ root ] |> Set.toList |> List.sort do
            match Map.tryFind memberPath byPath with
            | Some entry when entry.Role = EntrypointCommand
                          || entry.Role = ReleaseBuildEntrypoint
                          || entry.Role = TestEntrypoint ->
                let executableKind =
                    match entry.Role with
                    | EntrypointCommand -> "entrypoint/command"
                    | ReleaseBuildEntrypoint -> "release/build entrypoint"
                    | TestEntrypoint -> "test entrypoint"
                    | _ -> "executable"

                let detail = "reusable closure pulls in " + executableKind + " '" + memberPath + "'"

                issues.Add(
                    { Code = "REUSABLE_CLOSURE_VIOLATION"
                      Script = root
                      Detail = detail }
                )
            | _ -> ()

    if issues.Count = 0 then Ok() else Errors(issues |> Seq.toList)

// SDK ref packs are discovered from the analyzer's own assembly location (under shared/Microsoft.NETCore.App/<ver>/) so System.Security.Cryptography and System.Text.Json surface without hard-coded ProgramFiles paths.
let private packRootFromRuntime (runtimeAssembly: string) =
    let path = Path.GetFullPath runtimeAssembly
    // .../shared/Microsoft.NETCore.App/<ver>/<file>.dll -> .../shared/
    let normalized = path.Replace('\\', '/')
    let marker = "/shared/"

    match normalized.IndexOf(marker, StringComparison.Ordinal) with
    | idx when idx >= 0 ->
        let head = normalized.Substring(0, idx)
        Some(Path.Combine(head, "packs"))
    | _ -> None

let private refPackDir (packName: string) =
    match packRootFromRuntime typeof<System.Text.Json.Nodes.JsonNode>.Assembly.Location with
    | Some packsRoot ->
        let packRoot = Path.Combine(packsRoot, packName + ".Ref")

        if Directory.Exists packRoot then
            let candidates = Directory.GetDirectories(packRoot)
            let mutable best : string option = None

            for candidate in candidates do
                let dir = Path.Combine(candidate, "ref", "net11.0")

                if Directory.Exists dir then
                    match best with
                    | None -> best <- Some dir
                    | Some current ->
                        // Prefer the lexicographically greatest version, which matches the SDK's own resolution order on the install.
                        if String.Compare(current, dir, StringComparison.Ordinal) < 0 then
                            best <- Some dir

            best
        else
            None
    | None -> None

let private enumerateRefPackAssemblies (packName: string) : string list =
    match refPackDir packName with
    | Some dir ->
        Directory.GetFiles(dir, "*.dll")
        |> Array.filter (fun path -> not (Path.GetFileName(path).StartsWith("api-ms-")))
        |> Array.map (fun path -> "-r:" + path)
        |> Array.toList
    | None -> []

let private frameworkReferenceAssemblies () : string list =
    enumerateRefPackAssemblies "Microsoft.NETCore.App"

// Typecheck via the public FCS API only; GetProjectOptionsFromScript already populates the #load closure so no private fields are mutated, and framework references outside the NETCore.App facade are appended to OtherOptions.
let private typecheckRoot (script: Script) : Async<Result<unit>> =
    async {
        let checker = FSharpChecker.Create()
        let source = SourceText.ofString(File.ReadAllText script.AbsolutePath)

        let! projectOptionsResult =
            checker.GetProjectOptionsFromScript(script.AbsolutePath, source)

        let projectOptions, _ = projectOptionsResult

        let augmented =
            { projectOptions with
                OtherOptions =
                    Array.append
                        projectOptions.OtherOptions
                        (frameworkReferenceAssemblies () |> List.toArray) }

        let! checkResult = checker.ParseAndCheckProject(augmented)

        let errors =
            checkResult.Diagnostics
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
            |> Array.toList

        return
            match errors with
            | [] -> Ok()
            | first :: _ ->
                Errors
                    [ { Code = "TYPECHECK_ERROR"
                        Script = script.RepositoryPath
                        Detail = first.Message } ]
    }

let typecheckReusableRoots (scripts: Script list) : Async<Result<unit>> =
    async {
        let reusableRoots =
            scripts
            |> List.filter (fun s -> s.Role = ReusableModule || s.Role = TestHelper)
            |> List.sortBy _.RepositoryPath

        let issues = ResizeArray<Issue>()

        for script in reusableRoots do
            let! result = typecheckRoot script

            match result with
            | Ok() -> ()
            | Errors errorList ->
                for issue in errorList do
                    issues.Add issue

        return if issues.Count = 0 then Ok() else Errors(issues |> Seq.toList)
    }

let validate (repoRoot: string) (roots: DiscoveryRoot list) : Async<Result<Inventory>> =
    async {
        let inventory = discoverScripts repoRoot roots

        let! parsedResult = parseScripts repoRoot inventory

        match parsedResult with
        | Errors issues -> return Errors issues
        | Ok parsed ->
            match detectCycles parsed.Scripts with
            | Errors issues -> return Errors issues
            | Ok() ->
                match validateReusableClosures parsed.Scripts with
                | Errors issues -> return Errors issues
                | Ok() ->
                    let! typecheckResult = typecheckReusableRoots parsed.Scripts

                    match typecheckResult with
                    | Errors issues -> return Errors issues
                    | Ok() -> return Ok parsed
    }

let renderGraph (inventory: Inventory) : string =
    let builder = StringBuilder()

    for script in inventory.Scripts do
        builder.AppendLine(Role.toString script.Role + " " + script.RepositoryPath) |> ignore

        for load in script.Loads do
            builder.AppendLine("  -> " + load) |> ignore

    builder.ToString()

let renderInventory (inventory: Inventory) : string =
    let builder = StringBuilder()

    for script in inventory.Scripts do
        builder.AppendLine(Role.toString script.Role + "\t" + script.RepositoryPath) |> ignore

    builder.ToString()
