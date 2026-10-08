// CLI front end for the analyzer; subcommands expose validate|inventory|graph|typecheck on the same discovery surface.
// Default repository root is anchored at the entry assembly location so CWD never affects resolution.
module Validation.Validate

open System
open System.IO
open Validation.Analysis

// At least one of these siblings must exist for the default root walk to accept a directory; otherwise the candidate is not-a-repo and the walk continues upward.
let private repoRootDirectories = [| "infrastructure"; "dotnet"; "workflow" |]

let private isRepoRootCandidate (candidate: string) =
    let canonical = canonicalPath candidate
    repoRootDirectories
    |> Array.exists (fun name -> Directory.Exists(Path.Combine(canonical, name)))

// AppContext.BaseDirectory is the canonical .NET entry-assembly location and stays accurate under shadow-copy, .deps.json, and dotnet run contexts.
let private entryAssemblyDirectory () =
    AppContext.BaseDirectory

// Walk upward from a starting directory until a repository layout is found or the filesystem root is crossed; the first match wins so the answer is deterministic.
let private findRepoRootFrom (start: string) =
    let canonical = canonicalPath start
    let mutable current : string option = Some canonical
    let mutable found : string option = None

    while current.IsSome do
        let candidate = current.Value

        if isRepoRootCandidate candidate then
            found <- Some candidate
            current <- None
        else
            let parent = Path.GetDirectoryName candidate

            if String.IsNullOrEmpty parent || parent = candidate then
                current <- None
            else
                current <- Some parent

    found

// The default root is anchored at the entry assembly location so the same executable resolves to the same repository regardless of CWD.
let private defaultRepoRoot () =
    let assemblyDir = entryAssemblyDirectory ()
    match findRepoRootFrom assemblyDir with
    | Some root -> root
    | None ->
        failwith
            "Validation could not locate the repository root from the entry assembly directory '"
            + assemblyDir
            + "'. Pass an explicit --repo-root path to point the analyzer at the intended tree."

let defaultRoots (repoRoot: string) : DiscoveryRoot list =
    let canonicalRoot = canonicalPath repoRoot
    [
        { Name = "<repo-root>"
          AbsolutePath = canonicalRoot }
        { Name = "infrastructure"
          AbsolutePath = Path.Combine(canonicalRoot, "infrastructure") }
        { Name = "dotnet"
          AbsolutePath = Path.Combine(canonicalRoot, "dotnet") }
        { Name = "workflow"
          AbsolutePath = Path.Combine(canonicalRoot, "workflow") }
    ]

let printUsage () =
    eprintfn "usage: Validation <command> [--repo-root <path>]"
    eprintfn "commands:"
    eprintfn "  inventory   print the discovered scripts and their declared roles"
    eprintfn "  graph       print each script, its role, and its declared #load edges"
    eprintfn "  typecheck   FCS typecheck every reusable and test-helper root"
    eprintfn "  validate    run every check (default)"
    eprintfn ""
    eprintfn "By default the repository root is derived from the entry assembly location;"
    eprintfn "pass --repo-root <path> to point the analyzer at an explicit tree."
    exit 2

let parseArgs (args: string[]) : string option * string =
    let mutable command : string option = None
    let mutable repoRoot : string option = None
    let mutable i = 0

    while i < args.Length do
        let arg = args.[i]

        match arg with
        | "-r"
        | "--repo-root" ->
            if i + 1 >= args.Length then
                eprintfn "missing value for %s" arg
                exit 2
            else
                repoRoot <- Some args.[i + 1]
                i <- i + 2
        | cmd ->
            if command.IsSome then
                eprintfn "more than one positional command supplied: '%s'" cmd
                exit 2
            else
                command <- Some cmd
                i <- i + 1

    let root = match repoRoot with Some value -> value | None -> defaultRepoRoot ()
    command, root

let run (args: string[]) : Async<int> =
    async {
        let command, repoRoot = parseArgs args
        let roots = defaultRoots repoRoot

        match command with
        | None ->
            let! result = validate repoRoot roots

            match result with
            | Ok inventory ->
                printfn "OK %d scripts validated" inventory.Scripts.Length
                return 0
            | Errors issues ->
                for issue in issues do
                    eprintfn "[%s] %s: %s" issue.Code issue.Script issue.Detail

                return 5
        | Some cmd ->
            match cmd with
            | "inventory" ->
                let inventory = discoverScripts repoRoot roots
                let! parsed = parseScripts repoRoot inventory

                match parsed with
                | Ok parsedInventory ->
                    printfn "%s" (renderInventory parsedInventory)
                    return 0
                | Errors issues ->
                    for issue in issues do
                        eprintfn "[%s] %s: %s" issue.Code issue.Script issue.Detail

                    return 3
            | "graph" ->
                let inventory = discoverScripts repoRoot roots
                let! parsed = parseScripts repoRoot inventory

                match parsed with
                | Ok parsedInventory ->
                    printfn "%s" (renderGraph parsedInventory)
                    return 0
                | Errors issues ->
                    for issue in issues do
                        eprintfn "[%s] %s: %s" issue.Code issue.Script issue.Detail

                    return 3
            | "typecheck" ->
                let inventory = discoverScripts repoRoot roots
                let! parsed = parseScripts repoRoot inventory

                match parsed with
                | Errors issues ->
                    for issue in issues do
                        eprintfn "[%s] %s: %s" issue.Code issue.Script issue.Detail

                    return 3
                | Ok parsedInventory ->
                    let! typecheckResult = typecheckReusableRoots parsedInventory.Scripts

                    match typecheckResult with
                    | Ok() ->
                        printfn "OK reusable and test-helper roots typechecked"
                        return 0
                    | Errors issues ->
                        for issue in issues do
                            eprintfn "[%s] %s: %s" issue.Code issue.Script issue.Detail

                        return 4
            | "validate" ->
                let! result = validate repoRoot roots

                match result with
                | Ok inventory ->
                    printfn "OK %d scripts validated" inventory.Scripts.Length
                    return 0
                | Errors issues ->
                    for issue in issues do
                        eprintfn "[%s] %s: %s" issue.Code issue.Script issue.Detail

                    return 5
            | _ ->
                printUsage ()
                return 2
    }

// Standalone entry bridge for the asynchronous analyzer surface; CLI subcommands compose through `run` and the only synchronous-over-async wait happens here at the .NET process entry.
[<EntryPoint>]
let main args =
    run args |> Async.RunSynchronously
