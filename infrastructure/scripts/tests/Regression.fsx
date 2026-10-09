// role: test entrypoint

open System
open System.Diagnostics
open System.IO

type SkipCase(message: string) =
    inherit Exception(message)

let tests = ResizeArray<string * (unit -> Async<unit>)>()
let mutable passed = 0
let mutable failed = 0
let mutable skipped = 0

let test name action = tests.Add(name, action)

let analyzer =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "bin", "Release", "net11.0", "Validation.exe"))

let execute (fileName: string) (arguments: string list) (workingDirectory: string) : Async<int * string * string> =
    async {
        let start = ProcessStartInfo(fileName)
        arguments |> List.iter start.ArgumentList.Add
        start.WorkingDirectory <- workingDirectory
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false
        use child = Process.Start start
        let outputTask = child.StandardOutput.ReadToEndAsync()
        let errorTask = child.StandardError.ReadToEndAsync()

        try
            do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 30.0) |> Async.AwaitTask
        with :? TimeoutException ->
            if not child.HasExited then child.Kill true
            do! child.WaitForExitAsync() |> Async.AwaitTask
            return failwithf "process timed out: %s %s" fileName (String.Join(" ", arguments))

        let! output = outputTask |> Async.AwaitTask
        let! error = errorTask |> Async.AwaitTask
        return child.ExitCode, output, error
    }

let runCli command root cwd = execute analyzer [ command; "--repo-root"; root ] cwd

let tempRoot () =
    let path = Path.Combine(Path.GetTempPath(), "mcp-analyzer-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory path |> ignore
    path

let rec removeTree path =
    if Directory.Exists path || File.Exists path then
        let attributes = File.GetAttributes path

        if attributes.HasFlag(FileAttributes.ReparsePoint) then
            if attributes.HasFlag(FileAttributes.Directory) then Directory.Delete(path, false)
            else File.Delete path
        elif attributes.HasFlag(FileAttributes.Directory) then
            for entry in Directory.EnumerateFileSystemEntries path do removeTree entry
            Directory.Delete(path, false)
        else
            File.Delete path

let withFixture action =
    async {
        let root = tempRoot ()
        try return! action root
        finally removeTree root
    }

let write (root: string) (relative: string) (text: string) =
    let path = Path.Combine(root, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)
    path

let role roleName source = "// role: " + roleName + "\n" + source

let requireCode (code: string) (_: int, output: string, error: string) =
    if not ((output + error).Contains("[" + code + "]", StringComparison.Ordinal)) then
        failwithf "expected diagnostic %s, got stdout=%s stderr=%s" code output error

let requireExit (expectedCode: int) (actualCode: int, output: string, error: string) =
    if actualCode <> expectedCode then
        failwithf "expected exit %d, got %d; stdout=%s stderr=%s" expectedCode actualCode output error

let containsLine (fragment: string) (output: string) =
    output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.exists (fun line -> line.Contains(fragment, StringComparison.Ordinal))

test "dynamic discovery covers root and owned roots while excluding generated trees" (fun () ->
    withFixture (fun root -> async {
        write root "fresh.fsx" (role "reusable module/helper" "let value = 1") |> ignore
        write root "infrastructure/scripts/fresh.fsx" (role "test helper" "let value = 2") |> ignore
        write root "rolemetadata.fs" "module RoleMetadata" |> ignore
        [ "bin"; "obj"; "dist"; ".git" ]
        |> List.iter (fun name -> write root (Path.Combine("workflow", name, "hidden.fsx")) (role "reusable module/helper" "let value = 3") |> ignore)
        let! code, output, error = runCli "inventory" root root
        requireExit 0 (code, output, error)
        if not (containsLine "fresh.fsx" output && containsLine "infrastructure/scripts/fresh.fsx" output) then
            failwith "new root or owned-root script was not discovered"
        if output.Contains("hidden.fsx", StringComparison.Ordinal) || output.Contains("rolemetadata.fs", StringComparison.Ordinal) then
            failwith "generated directory or .fs metadata file entered the script inventory"
        return () }))

test "task runtime scripts are excluded while ordinary owned roots remain discovered" (fun () ->
    withFixture (fun root -> async {
        write root ".tasks/GHI-26/scripts/dummy.fsx" (role "reusable module/helper" "let hidden = 1") |> ignore
        write root "new-root.fsx" (role "reusable module/helper" "let rootValue = 1") |> ignore
        write root "infrastructure/scripts/new-owned.fsx" (role "test helper" "let ownedValue = 2") |> ignore
        write root "infrastructure/scripts/nested/new-nested.fsx" (role "test helper" "let nestedValue = 3") |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 0 (code, output, error)
        for included in [ "new-root.fsx"; "infrastructure/scripts/new-owned.fsx"; "infrastructure/scripts/nested/new-nested.fsx" ] do
            if not (containsLine included output) then failwithf "owned script omitted: %s" included
        if output.Contains(".tasks", StringComparison.Ordinal) || output.Contains("dummy.fsx", StringComparison.Ordinal) then failwith "Task Runtime file entered inventory"
        return () }))

test "only exact canonical roles are accepted; missing, unknown, duplicate and ambiguous headers fail" (fun () ->
    withFixture (fun root -> async {
        write root "none.fsx" "let value = 0" |> ignore
        write root "unknown.fsx" (role "wizard" "let value = 0") |> ignore
        write root "alias.fsx" (role "reusable" "let value = 0") |> ignore
        write root "duplicate.fsx" (role "reusable module/helper" "// role: test helper\nlet value = 0") |> ignore
        write root "prefix.fsx" "// note role: reusable module/helper\nlet value = 0" |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        for diagnostic in [ "MISSING_ROLE"; "AMBIGUOUS_ROLE" ] do requireCode diagnostic (code, output, error)
        return () }))

test "role headers require the first line, accept BOM, and reject late duplicate or conflicting declarations" (fun () ->
    withFixture (fun root -> async {
        write root "bom.fsx" ("\uFEFF" + role "reusable module/helper" "let value = 0") |> ignore
        write root "late-only.fsx" "let value = 0\n// role: test helper" |> ignore
        let spacer = [ 1 .. 10 ] |> List.map string |> String.concat "\n"
        let lateDuplicate = "let value = 0\n" + spacer + "\n// role: reusable module/helper"
        let lateConflict = "let value = 0\n" + spacer + "\n// role: test helper"
        write root "late-duplicate.fsx" (role "reusable module/helper" lateDuplicate) |> ignore
        write root "late-conflict.fsx" (role "reusable module/helper" lateConflict) |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "MISSING_ROLE" (code, output, error)
        requireCode "AMBIGUOUS_ROLE" (code, output, error)
        for source in [ "late-only.fsx"; "late-duplicate.fsx"; "late-conflict.fsx" ] do
            if not ((output + error).Contains(source, StringComparison.Ordinal)) then failwithf "role error missing for %s" source
        for source in [ "late-only.fsx"; "late-duplicate.fsx"; "late-conflict.fsx" ] do File.Delete(Path.Combine(root, source))
        let! validCode, validOutput, validError = runCli "inventory" root root
        requireExit 0 (validCode, validOutput, validError)
        if not (containsLine "bom.fsx" validOutput) then failwith "BOM-prefixed canonical role was rejected"
        return () }))

test "role lookalikes in string and block comments are ignored and all five roles are recognized" (fun () ->
    withFixture (fun root -> async {
        let roles = [ "reusable module/helper"; "entrypoint/command"; "test helper"; "test entrypoint"; "release/build entrypoint" ]
        roles |> List.iteri (fun index roleName -> write root (sprintf "role-%d.fsx" index) (role roleName "let value = 0") |> ignore)
        write root "lookalikes.fsx" (role "test helper" "let text = \"// role: unknown\"\n(* // role: fake *)\nlet value = 1") |> ignore
        write root "late-unknown.fsx" (role "test helper" (String.concat "\n" ([ 1 .. 10 ] |> List.map string) + "\n// role: wizard")) |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "AMBIGUOUS_ROLE" (code, output, error)
        if not ((output + error).Contains("late-unknown.fsx", StringComparison.Ordinal)) then failwith "unknown late role declaration was not rejected"
        File.Delete(Path.Combine(root, "late-unknown.fsx"))
        let! validCode, validOutput, validError = runCli "inventory" root root
        requireExit 0 (validCode, validOutput, validError)
        if not (containsLine "lookalikes.fsx" validOutput) then failwith "comment/string lookalikes changed role parsing"
        for index in 0 .. 4 do
            if not (containsLine (sprintf "role-%d.fsx" index) validOutput) then failwithf "canonical role %d not recognized" index
        return () }))

test "direct, transitive and diamond loads typecheck with repeated graph edges deduplicated and no evaluation" (fun () ->
    withFixture (fun root -> async {
        let marker = Path.Combine(root, "loaded.marker")
        write root "root.fsx" (role "reusable module/helper" "#load \"left.fsx\"\n#load \"right.fsx\"\n#load \"left.fsx\"\nlet result = Left.leftValue + Right.rightValue") |> ignore
        write root "left.fsx" (role "reusable module/helper" "#load \"shared.fsx\"\nlet leftValue = Shared.sharedValue") |> ignore
        write root "right.fsx" (role "reusable module/helper" "#load \"shared.fsx\"\nlet rightValue = Shared.sharedValue") |> ignore
        write root "shared.fsx" (role "reusable module/helper" (sprintf "System.IO.File.WriteAllText(@\"%s\", \"loaded\")\nlet sharedValue = 42" marker)) |> ignore
        let! graphCode, graph, graphError = runCli "graph" root root
        requireExit 0 (graphCode, graph, graphError)
        if not (containsLine "-> left.fsx" graph && containsLine "-> right.fsx" graph && containsLine "-> shared.fsx" graph) then
            failwith "direct or transitive edge missing from graph"
        let directEdges = graph.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.filter (fun line -> line = "  -> left.fsx" || line = "  -> right.fsx")
        if directEdges.Length <> 2 then failwithf "repeated direct load produced duplicate graph edges: %A" directEdges
        let! code, output, error = runCli "typecheck" root root
        requireExit 0 (code, output, error)
        if File.Exists marker then failwith "typecheck evaluated a top-level effect"
        return () }))

test "missing .fsx and .fs targets and escaped targets fail statically" (fun () ->
    withFixture (fun root -> async {
        write root "missing.fsx" (role "reusable module/helper" "#load \"absent.fsx\"") |> ignore
        write root "project.fsx" (role "test entrypoint" "#load \"absent.fs\"") |> ignore
        write root "escape.fsx" (role "entrypoint/command" "#load \"../../outside.fsx\"") |> ignore
        write root "escape-indented.fsx" (role "entrypoint/command" "    #load \"../../outside.fsx\"") |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "MISSING_LOAD_TARGET" (code, output, error)
        requireCode "DIRECTORY_ESCAPE" (code, output, error)
        for source in [ "escape.fsx"; "escape-indented.fsx" ] do
            if not ((output + error).Contains(source, StringComparison.Ordinal)) then
                failwithf "escape from %s was not reported" source
        return () }))

test "rooted load paths fail while repository-relative parent traversal remains valid" (fun () ->
    withFixture (fun root -> async {
        let target = write root "shared.fsx" (role "reusable module/helper" "let value = 1")
        let insideRooted = role "entrypoint/command" (sprintf "#load @\"%s\"" target)
        write root "rooted.fsx" insideRooted |> ignore
        write root "nested/relative.fsx" (role "entrypoint/command" "#load \"../shared.fsx\"") |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "ROOTED_LOAD_PATH" (code, output, error)
        if not ((output + error).Contains("rooted.fsx", StringComparison.Ordinal)) then failwith "absolute in-repository load was not attributed"
        File.Delete(Path.Combine(root, "rooted.fsx"))
        let! relativeCode, graph, relativeError = runCli "graph" root root
        requireExit 0 (relativeCode, graph, relativeError)
        if not (containsLine "-> shared.fsx" graph) then failwith "valid relative parent traversal was not resolved"
        if (graph + relativeError).Contains("DIRECTORY_ESCAPE", StringComparison.Ordinal) then failwith "valid in-repository parent traversal was rejected"
        return () }))

test "rooted transitive project-source load is rejected" (fun () ->
    withFixture (fun root -> async {
        let target = write root "outside-target.fsx" (role "reusable module/helper" "let value = 1")
        write root "root.fsx" (role "test entrypoint" "#load \"nested/Project.fs\"") |> ignore
        write root "nested/Project.fs" (sprintf "#load @\"%s\"\nmodule Project" target) |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "ROOTED_LOAD_PATH" (code, output, error)
        if not ((output + error).Contains("nested/Project.fs", StringComparison.Ordinal)) then failwith "transitive rooted load was not attributed to project source"
        return () }))

test "drive and UNC rooted load syntax is rejected without resolving external paths" (fun () ->
    withFixture (fun root -> async {
        write root "drive.fsx" (role "entrypoint/command" "#load @\"C:\\outside.fsx\"") |> ignore
        write root "unc.fsx" (role "entrypoint/command" "#load @\"\\\\server\\share\\outside.fsx\"") |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "ROOTED_LOAD_PATH" (code, output, error)
        for source in [ "drive.fsx"; "unc.fsx" ] do
            if not ((output + error).Contains(source, StringComparison.Ordinal)) then failwithf "rooted syntax missing diagnostic for %s" source
        return () }))

test "dependency cycles are rejected by validate" (fun () ->
    withFixture (fun root -> async {
        write root "a.fsx" (role "reusable module/helper" "#load \"b.fsx\"") |> ignore
        write root "b.fsx" (role "reusable module/helper" "#load \"a.fsx\"") |> ignore
        let! code, output, error = runCli "validate" root root
        requireExit 5 (code, output, error)
        requireCode "DEPENDENCY_CYCLE" (code, output, error)
        return () }))

test "module-first loaded script preserves module identity in FSI" (fun () ->
    withFixture (fun root -> async {
        write root "Loaded.fsx" "module Loaded\nlet value = 17" |> ignore
        let parent = write root "Parent.fsx" "#load \"Loaded.fsx\"\nprintfn \"module-result=%d\" Loaded.value"
        let! code, output, error = execute "dotnet" [ "fsi"; "--nologo"; parent ] root
        requireExit 0 (code, output, error)
        if not (output.Contains("module-result=17", StringComparison.Ordinal)) then failwith "loaded module value was not visible"
        return () }))

test "parent FSI load resolves a transitive module from an unrelated CWD" (fun () ->
    withFixture (fun root -> async {
        write root "Leaf.fsx" "module Leaf\nlet value = 23" |> ignore
        write root "Middle.fsx" "#load \"Leaf.fsx\"" |> ignore
        let parent = write root "Parent.fsx" "#load \"Middle.fsx\"\nprintfn \"transitive-result=%d\" Leaf.value"
        let unrelated = Path.Combine(root, "cwd")
        Directory.CreateDirectory unrelated |> ignore
        let! code, output, error = execute "dotnet" [ "fsi"; "--nologo"; parent ] unrelated
        requireExit 0 (code, output, error)
        if not (output.Contains("transitive-result=23", StringComparison.Ordinal)) then failwith "transitive module value was not visible"
        return () }))

test "FSI resolves SOURCE_DIRECTORY from the declaring script" (fun () ->
    withFixture (fun root -> async {
        write root "neighbor.txt" "lexical-path" |> ignore
        write root "Helper.fsx" "open System.IO\nlet sourceValue = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, \"neighbor.txt\"))" |> ignore
        let parent = write root "Parent.fsx" "#load \"Helper.fsx\"\nprintfn \"source-result=%s\" Helper.sourceValue"
        let unrelated = Path.Combine(root, "cwd")
        Directory.CreateDirectory unrelated |> ignore
        let! code, output, error = execute "dotnet" [ "fsi"; "--nologo"; parent ] unrelated
        requireExit 0 (code, output, error)
        if not (output.Contains("source-result=lexical-path", StringComparison.Ordinal)) then failwith "loaded helper used caller CWD"
        return () }))

test "repeated FSI load directives execute their top-level effect per directive" (fun () ->
    withFixture (fun root -> async {
        let marker = Path.Combine(root, "effects.txt")
        write root "Effect.fsx" (sprintf "System.IO.File.AppendAllText(@\"%s\", \"x\")" marker) |> ignore
        let parent = write root "Parent.fsx" "#load \"Effect.fsx\"\n#load \"Effect.fsx\""
        let! code, _, error = execute "dotnet" [ "fsi"; "--nologo"; parent ] root
        requireExit 0 (code, "", error)
        let count = if File.Exists marker then File.ReadAllText(marker).Length else 0
        if count <> 2 then failwithf "expected one effect per directive, observed %d" count
        return () }))

test "directive lexer ignores strings/comments and preserves multiple and verbatim arguments" (fun () ->
    withFixture (fun root -> async {
        let source = """let text = "#load \"fake.fsx\""
(*
#load "comment.fsx"
*)
#load "first.fsx" "second.fsx"
#load @"verbatim.fsx"
"""
        write root "entry.fsx" (role "entrypoint/command" source) |> ignore
        write root "first.fsx" (role "reusable module/helper" "module First\nlet value = 20") |> ignore
        write root "second.fsx" (role "reusable module/helper" "module Second\nlet value = 22") |> ignore
        write root "verbatim.fsx" (role "reusable module/helper" "let value = 1") |> ignore
        let! code, graph, error = runCli "graph" root root
        requireExit 0 (code, graph, error)
        for edge in [ "-> first.fsx"; "-> second.fsx"; "-> verbatim.fsx" ] do
            if not (containsLine edge graph) then failwithf "expected graph edge %s" edge
        if graph.Contains("fake.fsx", StringComparison.Ordinal) || graph.Contains("comment.fsx", StringComparison.Ordinal) then
            failwith "directive-like string or comment became a dependency"
        let parent = write root "Parent.fsx" "#load \"first.fsx\" \"second.fsx\"\nprintfn \"multi-result=%d\" (First.value + Second.value)"
        let! fsiCode, output, fsiError = execute "dotnet" [ "fsi"; "--nologo"; parent ] root
        requireExit 0 (fsiCode, output, fsiError)
        if not (output.Contains("multi-result=42", StringComparison.Ordinal)) then failwith "multiple loaded modules were not visible"
        return () }))

test "unsupported load forms fail before FCS typechecking" (fun () ->
    withFixture (fun root -> async {
        write root "entry.fsx" (role "entrypoint/command" "#load pathValue") |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "UNSUPPORTED_LOAD_FORM" (code, output, error)
        return () }))

test "transitive type errors are reported from a reusable root" (fun () ->
    withFixture (fun root -> async {
        write root "root.fsx" (role "reusable module/helper" "#load \"dependency.fsx\"\nlet result = Dependency.missing") |> ignore
        write root "dependency.fsx" (role "reusable module/helper" "module Dependency = let value = 1") |> ignore
        let! code, output, error = runCli "typecheck" root root
        requireExit 4 (code, output, error)
        requireCode "TYPECHECK_ERROR" (code, output, error)
        return () }))

test "ambient symbols fail and no-evaluation sentinel remains absent" (fun () ->
    withFixture (fun root -> async {
        let marker = Path.Combine(root, "evaluated.marker")
        write root "helper.fsx" (role "reusable module/helper" (sprintf "System.IO.File.WriteAllText(@\"%s\", \"evaluated\")\nlet result = AmbientOnly.value" marker)) |> ignore
        write root "parent.fsx" (role "entrypoint/command" "module AmbientOnly = let value = 1") |> ignore
        let! code, output, error = runCli "typecheck" root root
        requireExit 4 (code, output, error)
        requireCode "TYPECHECK_ERROR" (code, output, error)
        if File.Exists marker then failwith "typecheck evaluated a top-level effect"
        return () }))

test "independent roots with the same module name typecheck separately" (fun () ->
    withFixture (fun root -> async {
        write root "first.fsx" (role "reusable module/helper" "module Config = let value = 1") |> ignore
        write root "second.fsx" (role "reusable module/helper" "module Config = let value = 2") |> ignore
        let! code, output, error = runCli "typecheck" root root
        requireExit 0 (code, output, error)
        return () }))

test "reusable closures reject executable script dependencies" (fun () ->
    withFixture (fun root -> async {
        write root "helper.fsx" (role "reusable module/helper" "#load \"command.fsx\"") |> ignore
        write root "command.fsx" (role "entrypoint/command" "let value = 1") |> ignore
        let! code, output, error = runCli "validate" root root
        requireExit 5 (code, output, error)
        requireCode "REUSABLE_CLOSURE_VIOLATION" (code, output, error)
        return () }))

test "external directives in scripts are blocked without resolver execution" (fun () ->
    withFixture (fun root -> async {
        let marker = Path.Combine(root, "evaluated.marker")
        write root "reference.fsx" (role "reusable module/helper" (sprintf "#r \"not-a-real-local-assembly.dll\"\nSystem.IO.File.WriteAllText(@\"%s\", \"evaluated\")" marker)) |> ignore
        write root "include.fsx" (role "test entrypoint" "#I \"missing-local-include\"\nlet value = 1") |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "EXTERNAL_REFERENCE" (code, output, error)
        requireCode "EXTERNAL_INCLUDE" (code, output, error)
        if File.Exists marker then failwith "static external-directive rejection evaluated a script"
        return () }))

test "transitive project-source external directives are rejected before typechecking" (fun () ->
    withFixture (fun root -> async {
        write root "root.fsx" (role "test entrypoint" "#load \"Project.fs\"") |> ignore
        write root "Project.fs" "#r \"not-a-real-local-assembly.dll\"\n#I \"missing-local-include\"\nmodule Project" |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "EXTERNAL_REFERENCE" (code, output, error)
        requireCode "EXTERNAL_INCLUDE" (code, output, error)
        return () }))

test "directive-like strings and comments do not trigger external resolution blocks" (fun () ->
    withFixture (fun root -> async {
        let source = """let text = "#r \"fake.dll\""
(* #I "fake-path" *)
let value = text.Length"""
        write root "safe.fsx" (role "reusable module/helper" source) |> ignore
        let! code, output, error = runCli "typecheck" root root
        requireExit 0 (code, output, error)
        return () }))

test "indented load directives are recognized and resolved" (fun () ->
    withFixture (fun root -> async {
        write root "entry.fsx" (role "entrypoint/command" "    #load \"dependency.fsx\"") |> ignore
        write root "dependency.fsx" (role "reusable module/helper" "let value = 1") |> ignore
        let! code, graph, error = runCli "graph" root root
        requireExit 0 (code, graph, error)
        if not (containsLine "-> dependency.fsx" graph) then failwith "indented load directive was not discovered"
        return () }))

test "module-contained hash text is not treated as an FSI load directive" (fun () ->
    withFixture (fun root -> async {
        write root "Dependency.fsx" (role "reusable module/helper" "module Dependency\nlet value = 31") |> ignore
        let parent = write root "Parent.fsx" (role "reusable module/helper" "module Parent =\n    #load \"Dependency.fsx\"\n    let result = Dependency.value")
        let! code, output, error = execute "dotnet" [ "fsi"; "--nologo"; parent ] root
        if code = 0 || not (error.Contains("Dependency", StringComparison.Ordinal)) then
            failwithf "module-contained directive had unexpected FSI result: %s %s" output error
        let! graphCode, graph, graphError = runCli "graph" root root
        requireExit 3 (graphCode, graph, graphError)
        requireCode "UNSUPPORTED_LOAD_FORM" (graphCode, graph, graphError)
        if containsLine "-> Dependency.fsx" graph then failwith "analyzer classified a module-body directive as a real FSI load"
        return () }))

test "module body classifier comment boundaries match FSI for plausible opener forms" (fun () ->
    withFixture (fun root -> async {
        let cases =
            [ "multiline-comment", "(*\nmodule Commented =\n*)\n#load \"Dependency.fsx\"\n"
              "trailing-comment", "module Trailing = (* opener note *)\n    #load \"Dependency.fsx\"\n"
              "attribute", "[<System.Obsolete>]\nmodule Attributed =\n    #load \"Dependency.fsx\"\n"
              "tab-indentation", "module Tabbed =\n\t#load \"Dependency.fsx\"\n" ]
        write root "Dependency.fsx" (role "reusable module/helper" "module Dependency\nlet value = 31") |> ignore
        for name, source in cases do
            let parent = write root (name + ".fsx") (role "reusable module/helper" source)
            let! fsiCode, fsiOutput, fsiError = execute "dotnet" [ "fsi"; "--nologo"; parent ] root
            let! graphCode, graph, graphError = runCli "graph" root root
            if fsiCode = 0 then
                if graphCode <> 0 || not (containsLine "-> Dependency.fsx" graph) then
                    printfn "CLASSIFIER MISMATCH %s: FSI accepts load; analyzer output=%s error=%s" name graph graphError
                else printfn "CLASSIFIER MATCH %s: FSI accepts load" name
            elif graphCode = 0 || not ((graph + graphError).Contains("UNSUPPORTED_LOAD_FORM", StringComparison.Ordinal)) then
                printfn "CLASSIFIER MISMATCH %s: FSI rejects load; FSI=%s %s analyzer=%s %s" name fsiOutput fsiError graph graphError
            else printfn "CLASSIFIER MATCH %s: FSI rejects load" name
            File.Delete parent
        return () }))

let createFileLink link target =
    try File.CreateSymbolicLink(link, target) |> ignore
    with error ->
        match error with
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException
        | :? IOException -> raise (SkipCase("file symbolic links unavailable: " + error.Message))
        | _ -> raise error

let createDirectoryLink link target =
    try Directory.CreateSymbolicLink(link, target) |> ignore
    with error ->
        match error with
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException
        | :? IOException -> raise (SkipCase("directory symbolic links unavailable: " + error.Message))
        | _ -> raise error

test "file and directory discovery links and loops fail closed without traversal" (fun () ->
    withFixture (fun root -> async {
        let targetFile = write root "target.fsx" (role "reusable module/helper" "let value = 1")
        let targetDirectory = Path.Combine(root, "real")
        Directory.CreateDirectory targetDirectory |> ignore
        write root "real/helper.fsx" (role "reusable module/helper" "let value = 2") |> ignore
        createFileLink (Path.Combine(root, "file-link.fsx")) targetFile
        createDirectoryLink (Path.Combine(root, "directory-link")) targetDirectory
        createDirectoryLink (Path.Combine(root, "loop-link")) root
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "SYMLINK_REPARSE" (code, output, error)
        for linkName in [ "file-link.fsx"; "directory-link"; "loop-link" ] do
            if not ((output + error).Contains(linkName, StringComparison.Ordinal)) then
                failwithf "discovery did not report reparse entry %s" linkName
        return () }))

test "direct file symlink load targets are rejected" (fun () ->
    withFixture (fun root -> async {
        let target = write root "target.fsx" (role "reusable module/helper" "let value = 1")
        createFileLink (Path.Combine(root, "file-link.fsx")) target
        write root "parent.fsx" (role "entrypoint/command" "#load \"file-link.fsx\"") |> ignore
        let! code, output, error = runCli "graph" root root
        requireExit 3 (code, output, error)
        requireCode "SYMLINK_REPARSE" (code, output, error)
        if not ((output + error).Contains("parent.fsx", StringComparison.Ordinal)) then
            failwith "linked #load target was not attributed to its declaring script"
        return () }))

test "parent-component symlinks in load paths are rejected" (fun () ->
    withFixture (fun root -> async {
        let targetDirectory = Path.Combine(root, "obj", "real")
        Directory.CreateDirectory targetDirectory |> ignore
        write root "obj/real/helper.fsx" (role "reusable module/helper" "let value = 1") |> ignore
        createDirectoryLink (Path.Combine(root, "obj", "directory-link")) targetDirectory
        write root "parent.fsx" (role "entrypoint/command" "#load \"obj/directory-link/helper.fsx\"") |> ignore
        let! code, output, error = runCli "graph" root root
        requireExit 3 (code, output, error)
        requireCode "SYMLINK_REPARSE" (code, output, error)
        if not ((output + error).Contains("parent.fsx", StringComparison.Ordinal)) then
            failwith "parent-component link was not reported on its declaring #load"
        return () }))

test "a linked dependency hidden from inventory is rejected before typechecking" (fun () ->
    withFixture (fun root -> async {
        let actual = Path.Combine(root, "obj", "actual")
        Directory.CreateDirectory actual |> ignore
        write root "obj/actual/helper.fsx" (role "reusable module/helper" "let value = 9") |> ignore
        createDirectoryLink (Path.Combine(root, "obj", "linked")) actual
        write root "parent.fsx" (role "reusable module/helper" "#load \"obj/linked/helper.fsx\"") |> ignore
        let! code, output, error = runCli "validate" root root
        requireExit 5 (code, output, error)
        requireCode "MISSING_DEPENDENCY" (code, output, error)
        if (output + error).Contains("TYPECHECK_ERROR", StringComparison.Ordinal) then
            failwith "typechecking ran after the hidden dependency was rejected"
        return () }))

test "a symlinked repository root is rejected" (fun () ->
    withFixture (fun root -> async {
        let target = Path.Combine(root, "target-root")
        Directory.CreateDirectory target |> ignore
        write target "entry.fsx" (role "entrypoint/command" "let value = 1") |> ignore
        let alias = Path.Combine(root, "linked-root")
        createDirectoryLink alias target
        let! code, output, error = runCli "inventory" alias root
        requireExit 3 (code, output, error)
        requireCode "SYMLINK_REPARSE" (code, output, error)
        return () }))

test "current repository discovery and typecheck pass from the compiled CLI" (fun () ->
    async {
        let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
        let other = tempRoot ()
        try
            let! inventoryCode, inventory, inventoryError = execute analyzer [ "inventory" ] other
            requireExit 0 (inventoryCode, inventory, inventoryError)
            if not (containsLine "infrastructure/scripts/tests/Regression.fsx" inventory) then
                failwith "new regression script was not in repository discovery"
            let! validateCode, validateOutput, validateError = runCli "validate" repoRoot other
            requireExit 0 (validateCode, validateOutput, validateError)
            if not (validateOutput.Contains("OK 30 scripts validated", StringComparison.Ordinal)) then
                failwithf "unexpected live inventory result: %s" validateOutput
            let! typecheckCode, typecheckOutput, typecheckError = runCli "typecheck" repoRoot other
            requireExit 0 (typecheckCode, typecheckOutput, typecheckError)
        finally
            Directory.Delete(other, true)
    })

let runTests () =
    async {
        for name, action in tests do
            try
                do! action ()
                passed <- passed + 1
                printfn "PASS %s" name
            with
            | :? SkipCase as skip ->
                skipped <- skipped + 1
                printfn "SKIP %s: %s" name skip.Message
            | error ->
                failed <- failed + 1
                eprintfn "FAIL %s: %s" name error.Message

        printfn "Analyzer regression totals: %d passed; %d failed; %d skipped" passed failed skipped
        if failed <> 0 then exit 1
    }

// Standalone entry bridge: FSI does not await a top-level Async value.
runTests () |> Async.RunSynchronously
