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

// Host-independent matrix rejects every Windows-style syntax form plus Unix-leading-slash before the analyzer resolves the target; retained columns (current, nested) resolve via the existing `../` test.
test "rooted syntax matrix is rejected before target resolution; relative current and nested loads remain valid" (fun () ->
    withFixture (fun root -> async {
        let rejected =
            [ "abs-slash.fsx",          "/outside.fsx"
              "abs-slash-verb.fsx",     "/outside.fsx"
              "drive-rel-bs.fsx",       "\\outside.fsx"
              "drive-c-rel.fsx",        "C:outside.fsx"
              "drive-c-rel-verb.fsx",   "C:outside.fsx"
              "drive-c-root.fsx",       "C:\\outside.fsx"
              "drive-c-fwd.fsx",        "C:/outside.fsx"
              "unc.fsx",                "\\\\server\\share\\outside.fsx"
              "unc-verb.fsx",           "\\\\server\\share\\outside.fsx"
              "device.fsx",             "\\\\.\\COM1"
              "device-verb.fsx",        "\\\\?\\C:\\outside.fsx"
              "drive-z-rel.fsx",        "z:outside.fsx"
              "drive-Z-root.fsx",       "Z:\\outside.fsx"
              "drive-D-root.fsx",       "D:/outside.fsx"
              "drive-a-rel-verb.fsx",   "a:relative-only" ]
        for name, load in rejected do
            write root name (role "entrypoint/command" ("#load \"" + load + "\"")) |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "ROOTED_LOAD_PATH" (code, output, error)
        for name, _ in rejected do
            if not ((output + error).Contains(name, StringComparison.Ordinal)) then
                failwithf "rooted syntax matrix missing diagnostic for %s" name
        // Root policy must fire before resolution; no class substitution is acceptable.
        for other in [ "DIRECTORY_ESCAPE"; "MISSING_LOAD_TARGET"; "TYPECHECK_ERROR"; "SYMLINK_REPARSE" ] do
            if (output + error).Contains(other, StringComparison.Ordinal) then
                failwithf "rooted syntax must fire before %s; saw it in output" other
        for name, _ in rejected do File.Delete(Path.Combine(root, name))
        // Allowed forms must still resolve within the test root.
        write root "keep.fsx" (role "reusable module/helper" "let v = 1") |> ignore
        write root "subdir/keep.fsx" (role "reusable module/helper" "let v = 1") |> ignore
        write root "rel-dot.fsx" (role "entrypoint/command" "#load \"./keep.fsx\"") |> ignore
        write root "rel-nested.fsx" (role "entrypoint/command" "#load \"subdir/keep.fsx\"") |> ignore
        let! graphCode, graph, graphError = runCli "graph" root root
        requireExit 0 (graphCode, graph, graphError)
        for edge in [ "-> keep.fsx" ] do
            if not (containsLine edge graph) then failwithf "valid relative load missing edge %s" edge
        if (graph + graphError).Contains("DIRECTORY_ESCAPE", StringComparison.Ordinal) then
            failwith "valid in-repository relative load was rejected as a directory escape"
        return () }))

// `.fs` files never enter discovery, so the transitive scanner is exercised via the project-source chain; each row pairs an emitter `.fsx` with a loaded `.fs` that holds the rooted directive, and the diagnostic reports the loaded `.fs` path.
test "transitive rooted project-source syntax matrix is rejected before target resolution" (fun () ->
    withFixture (fun root -> async {
        let cases =
            [ "trans-emit-abs.fsx",  "trans-abs.fs",      "#load \"/outside.fs\""
              "trans-emit-c-rel.fsx", "trans-c-rel.fs",    "#load \"C:outside.fs\""
              "trans-emit-c-root.fsx", "trans-c-root.fs", "#load \"C:\\\\outside.fs\""
              "trans-emit-unc.fsx",  "trans-unc.fs",      "#load \"\\\\\\\\server\\\\share\\\\outside.fs\""
              "trans-emit-device.fsx", "trans-device.fs", "#load \"\\\\\\\\.\\\\outside.fs\"" ]
        // Stage 1: emitter .fsx loads the project-source .fs.
        for emitFile, projectFile, _ in cases do
            write root emitFile (role "entrypoint/command" (sprintf "#load \"%s\"\nmodule ProjectEmitter" projectFile)) |> ignore
        // Stage 2: loaded .fs holds the rooted directive under test (no role header: it's not a script).
        for _, projectFile, directive in cases do
            write root projectFile (sprintf "%s\nmodule ProjectWithRoot" directive) |> ignore
        let! code, output, error = runCli "inventory" root root
        requireExit 3 (code, output, error)
        requireCode "ROOTED_LOAD_PATH" (code, output, error)
        for _, projectFile, _ in cases do
            if not ((output + error).Contains(projectFile, StringComparison.Ordinal)) then
                failwithf "transitive rooted load missing diagnostic for %s" projectFile
        for other in [ "DIRECTORY_ESCAPE"; "MISSING_LOAD_TARGET" ] do
            if (output + error).Contains(other, StringComparison.Ordinal) then
                failwithf "transitive root policy must fire before %s; saw it" other
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

// Multi-signal evidence (unique-readback + helper sentinel) classifies the actual FSI load so a silent module-body-to-file-scope downgrade surfaces as a real mismatch.
type FsiLoad =
    | Loaded
    | NotLoaded of reason: string

let classifyFsiLoad (caseName: string) (fsiCode: int) (fsiOut: string) (fsiErr: string) (sentinelExists: bool) : FsiLoad =
    let hasReadback = fsiOut.Contains("unique-reference=91201", StringComparison.Ordinal)
    let hasErr = not (String.IsNullOrEmpty fsiErr)
    if fsiCode = 0 && hasReadback && sentinelExists && not hasErr then Loaded
    elif fsiCode <> 0
         && not sentinelExists
         && fsiErr.Contains("FS0039", StringComparison.Ordinal)
         && fsiErr.Contains("Dependency", StringComparison.Ordinal) then
        NotLoaded "module-body load ignored (FS0039)"
    else
        failwithf "%s: fsi evidence incoherent: code=%d hasReadback=%b sentinel=%b hasErr=%b (err=%s)" caseName fsiCode hasReadback sentinelExists hasErr fsiErr

let private extractDiagnosticCodes (stderr: string) : string list =
    stderr.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun line ->
        if line.StartsWith("[", StringComparison.Ordinal) then
            let closeIdx = line.IndexOf("]", StringComparison.Ordinal)
            if closeIdx > 1 then Some (line.Substring(1, closeIdx - 1)) else None
        else None)
    |> Array.toList

let private stderrAllUnsupported (stderr: string) : bool =
    stderr.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.forall (fun line -> line.StartsWith("[UNSUPPORTED_LOAD_FORM]", StringComparison.Ordinal))

let assertModuleClassifier (caseName: string) (fsiOutcome: FsiLoad) (graphCode: int) (graphStdout: string) (graphStderr: string) =
    let codes = extractDiagnosticCodes graphStderr
    let hasEdge = graphStdout.Contains("-> Dependency.fsx", StringComparison.Ordinal)
    let analyzerAccepted = graphCode = 0 && hasEdge && String.IsNullOrEmpty graphStderr
    let analyzerRejected =
        graphCode = 3
        && not hasEdge
        && codes = [ "UNSUPPORTED_LOAD_FORM" ]
        && stderrAllUnsupported graphStderr
    match fsiOutcome with
    | Loaded when analyzerAccepted -> ()
    | NotLoaded _ when analyzerRejected -> ()
    | Loaded when analyzerRejected ->
        failwithf "%s: FSI loaded but analyzer rejected module-body load (graphCode=%d codes=%A)" caseName graphCode codes
    | NotLoaded reason when analyzerAccepted ->
        failwithf "%s: FSI did not load (%s) but analyzer accepted the edge (graphCode=%d graph=%s)" caseName reason graphCode graphStdout
    | outcome ->
        failwithf "%s: incoherent graph evidence (fsi=%A graphCode=%d codes=%A hasEdge=%b stderr=%s)" caseName outcome graphCode codes hasEdge graphStderr

test "module body classifier agrees with FSI on opener forms via multi-signal evidence" (fun () ->
    withFixture (fun root -> async {
        // Reference Dependency.uniqueValue in each body so an ignored `#load` surfaces FS0039 in stderr; a syntax error surfaces its own code separately.
        let cases =
            [ "multiline-comment", "(*\nmodule Commented =\n*)\n#load \"Dependency.fsx\"\nprintfn \"unique-reference=%d\" Dependency.uniqueValue\n"
              "trailing-block-comment", "module Trailing = (* opener note *)\n    #load \"Dependency.fsx\"\n    printfn \"unique-reference=%d\" Dependency.uniqueValue\n"
              "trailing-line-comment", "module TrailingLine = // opener note\n    #load \"Dependency.fsx\"\n    printfn \"unique-reference=%d\" Dependency.uniqueValue\n"
              "attribute", "[<System.Obsolete>]\nmodule Attributed =\n    #load \"Dependency.fsx\"\n    printfn \"unique-reference=%d\" Dependency.uniqueValue\n" ]
        let sentinel = Path.Combine(root, "loaded-sentinel.marker")
        let dependency = role "reusable module/helper" "module Dependency\nlet uniqueValue = 91201\nSystem.IO.File.WriteAllText(@\"" + sentinel + "\", \"loaded\")"
        write root "Dependency.fsx" dependency |> ignore
        try
            for name, body in cases do
                if File.Exists sentinel then File.Delete sentinel
                let parent = write root (name + ".fsx") (role "reusable module/helper" body)
                let! fsiCode, fsiOut, fsiErr = execute "dotnet" [ "fsi"; "--nologo"; parent ] root
                let! graphCode, graph, graphError = runCli "graph" root root
                let fsiOutcome = classifyFsiLoad name fsiCode fsiOut fsiErr (File.Exists sentinel)
                assertModuleClassifier name fsiOutcome graphCode graph graphError
                File.Delete parent
            if File.Exists sentinel then File.Delete sentinel
            let positive = write root "file-scope.fsx" (role "reusable module/helper" "#load \"Dependency.fsx\"\nprintfn \"unique-reference=%d\" Dependency.uniqueValue")
            let! posFsiCode, posFsiOut, posFsiErr = execute "dotnet" [ "fsi"; "--nologo"; positive ] root
            let! posGraphCode, posGraph, posGraphErr = runCli "graph" root root
            let posFsi = classifyFsiLoad "file-scope-positive" posFsiCode posFsiOut posFsiErr (File.Exists sentinel)
            if not (match posFsi with Loaded -> true | _ -> false) then
                failwithf "file-scope positive control: fsi did not load dependency (outcome=%A, code=%d out=%s err=%s)" posFsi posFsiCode posFsiOut posFsiErr
            if posGraphCode <> 0 || not (containsLine "-> Dependency.fsx" posGraph) then
                failwithf "file-scope load edge missing from analyzer (code=%d graph=%s err=%s)" posGraphCode posGraph posGraphErr
        finally
            if File.Exists sentinel then File.Delete sentinel
            File.Delete(Path.Combine(root, "Dependency.fsx"))
        return () }))

test "negative control: assertModuleClassifier throws on fabricated mismatch (unit corruption case)" (fun () ->
    async {
        let raised (label: string) (outcome: FsiLoad) (graphCode: int) (graphStdout: string) (graphStderr: string) =
            try
                assertModuleClassifier label outcome graphCode graphStdout graphStderr |> ignore
                false
            with _ ->
                true
        // Fabricated disagreements must raise; if any silently returns, the R2 harness is dead.
        if not (raised "fabricated-ignored-accepted" (NotLoaded "fabricated") 0 "  -> Dependency.fsx" "") then
            failwith "fabricated NotLoaded+accepted did not raise; R2 harness is dead"
        if not (raised "fabricated-loaded-rejected" Loaded 3 "" "[UNSUPPORTED_LOAD_FORM] SomeCase.fsx: ...") then
            failwith "fabricated Loaded+rejected did not raise; R2 harness is dead"
        // Coherent agreement must NOT raise; if any raises, the helper is unsafe.
        if raised "agree-loaded" Loaded 0 "  -> Dependency.fsx" "" then
            failwith "agree-loaded should not raise; helper is rejecting a coherent case"
        if raised "agree-not-loaded" (NotLoaded "ok") 3 "" "[UNSUPPORTED_LOAD_FORM] SomeCase.fsx: ..." then
            failwith "agree-not-loaded should not raise; helper is rejecting a coherent case"
        return () })

test "FSI known-syntax FS1161 cannot establish loader agreement (incoherent, not NotLoaded)" (fun () ->
    withFixture (fun root -> async {
        let sentinel = Path.Combine(root, "fs1161-sentinel.marker")
        let dependency = role "reusable module/helper" "module Dependency\nlet uniqueValue = 91201\nSystem.IO.File.WriteAllText(@\"" + sentinel + "\", \"loaded\")"
        write root "Dependency.fsx" dependency |> ignore
        let body = "module Tabbed =\n\t#load \"Dependency.fsx\"\n"
        let script = write root "tab-fs1161.fsx" (role "reusable module/helper" body)
        try
            if File.Exists sentinel then File.Delete sentinel
            let! fsiCode, fsiOut, fsiErr = execute "dotnet" [ "fsi"; "--nologo"; script ] root
            if not (fsiErr.Contains("FS1161", StringComparison.Ordinal)) then
                failwithf "tab FS1161: stderr must contain FS1161 (err=%s)" fsiErr
            if File.Exists sentinel then failwith "tab FS1161: sentinel unexpectedly created"
            let raised =
                try
                    classifyFsiLoad "tab-fs1161" fsiCode fsiOut fsiErr (File.Exists sentinel) |> ignore
                    false
                with _ -> true
            if not raised then failwith "tab FS1161: classifyFsiLoad did not raise on known-syntax case"
            return ()
        finally
            if File.Exists sentinel then File.Delete sentinel
            for src in [ "tab-fs1161.fsx"; "Dependency.fsx" ] do
                let p = Path.Combine(root, src)
                if File.Exists p then File.Delete p
        return () }))

test "nested module-body dedent inside Outer rejects UNSUPPORTED_LOAD_FORM; file-scope after Outer accepts the edge" (fun () ->
    withFixture (fun root -> async {
        let sentinel = Path.Combine(root, "nested-loaded.marker")
        let dependency = role "reusable module/helper" "module Dependency\nlet uniqueValue = 91201\nSystem.IO.File.WriteAllText(@\"" + sentinel + "\", \"loaded\")"
        write root "Dependency.fsx" dependency |> ignore
        let nestedBody = "module Outer =\n    module Inner =\n        let value = 1\n\n    #load \"Dependency.fsx\"\n    let result = Dependency.uniqueValue\n"
        let fileScopeBody = "module Outer =\n    module Inner =\n        let value = 1\n\nmodule After =\n    let outerValue = Outer.Inner.value + 1\n\n#load \"Dependency.fsx\"\nprintfn \"unique-reference=%d\" Dependency.uniqueValue\nlet combined = Dependency.uniqueValue + After.outerValue\n"
        let nested = write root "NestedOuter.fsx" (role "reusable module/helper" nestedBody)
        let fileScope = write root "FileScope.fsx" (role "reusable module/helper" fileScopeBody)
        try
            // Phase 1: nested case. FSI must not load; analyzer must reject UNSUPPORTED_LOAD_FORM with no edge.
            if File.Exists sentinel then File.Delete sentinel
            let! fsiCode, fsiOut, fsiErr = execute "dotnet" [ "fsi"; "--nologo"; nested ] root
            let fsiOutcome = classifyFsiLoad "nested" fsiCode fsiOut fsiErr (File.Exists sentinel)
            let! graphCode, graphStdout, graphStderr = runCli "graph" root root
            match fsiOutcome with
            | NotLoaded _ -> ()
            | _ -> failwithf "nested: FSI concrete fixture expected NotLoaded, got %A (exit=%d out=%s err=%s)" fsiOutcome fsiCode fsiOut fsiErr
            if not (fsiErr.Contains("FS0039", StringComparison.Ordinal) && fsiErr.Contains("Dependency", StringComparison.Ordinal)) then
                failwithf "nested: FSI concrete fixture expected FS0039+Dependency (exit=%d err=%s)" fsiCode fsiErr
            if File.Exists sentinel then failwith "nested: FSI created sentinel despite module-body load"
            let codes = extractDiagnosticCodes graphStderr
            if graphCode = 0 && graphStdout.Contains("-> Dependency.fsx", StringComparison.Ordinal) then
                failwithf "nested: analyzer must not classify module-body-dedent load as file-scope (graphCode=%d stdout=%s)" graphCode graphStdout
            if codes <> [ "UNSUPPORTED_LOAD_FORM" ] then
                failwithf "nested: analyzer must surface exactly UNSUPPORTED_LOAD_FORM (graphCode=%d stderr=%s)" graphCode graphStderr
            // Phase 2: file-scope positive control. FSI loads; analyzer accepts edge.
            File.Delete nested
            if File.Exists sentinel then File.Delete sentinel
            let! posFsiCode, posFsiOut, posFsiErr = execute "dotnet" [ "fsi"; "--nologo"; fileScope ] root
            let posFsiOutcome = classifyFsiLoad "file-scope" posFsiCode posFsiOut posFsiErr (File.Exists sentinel)
            match posFsiOutcome with
            | Loaded -> ()
            | _ -> failwithf "file-scope positive control: expected Loaded, got %A (exit=%d out=%s err=%s)" posFsiOutcome posFsiCode posFsiOut posFsiErr
            if not (File.Exists sentinel) then failwith "file-scope positive control: sentinel missing after FSI load"
            let! posGraphCode, posGraphStdout, posGraphStderr = runCli "graph" root root
            if posGraphCode <> 0 then
                failwithf "file-scope positive control: analyzer must accept (graphCode=%d stderr=%s)" posGraphCode posGraphStderr
            if not (containsLine "-> Dependency.fsx" posGraphStdout) then
                failwithf "file-scope positive control: edge missing (graph=%s)" posGraphStdout
            if (extractDiagnosticCodes posGraphStderr).IsEmpty |> not then
                failwithf "file-scope positive control: analyzer must not surface any diagnostic (stderr=%s)" posGraphStderr
        finally
            if File.Exists sentinel then File.Delete sentinel
            for src in [ "NestedOuter.fsx"; "FileScope.fsx"; "Dependency.fsx" ] do
                let p = Path.Combine(root, src)
                if File.Exists p then File.Delete p
        return () }))

test "R3-F1 scope preservation: blank/comment-only dedents, deeper stack, sibling nested, and string contents do not pop module frame" (fun () ->
    withFixture (fun root -> async {
        let sentinel = Path.Combine(root, "scope-sentinel.marker")
        let dependency = role "reusable module/helper" "module Dependency\nlet uniqueValue = 91201\nSystem.IO.File.WriteAllText(@\"" + sentinel + "\", \"loaded\")"
        write root "Dependency.fsx" dependency |> ignore
        let cases =
            [ "deeper", "module A =\n    module B =\n        module C =\n            let x = 1\n\n        let y = C.x + 1\n    #load \"Dependency.fsx\"\n    let result = Dependency.uniqueValue\n"
              "siblings", "module Outer =\n    module Inner1 =\n        let a = 1\n    let b = Inner1.a + 1\n    module Inner2 =\n        let c = 2\n    let d = Inner2.c + b\n    #load \"Dependency.fsx\"\n    let result = Dependency.uniqueValue\n"
              "blank-lines", "module Outer =\n    module Inner =\n        let value = 1\n\n\n\n    #load \"Dependency.fsx\"\n    let result = Dependency.uniqueValue\n"
              "comment-only", "module Outer =\n    module Inner =\n        let value = 1\n    // line comment\n    (* block comment *)\n    #load \"Dependency.fsx\"\n    let result = Dependency.uniqueValue\n"
              "string-contents", "module Outer =\n    module Inner =\n        let value = 1\n    let text = \"\n#load \\\"fake.fsx\\\"\n\"\n    #load \"Dependency.fsx\"\n    let result = Dependency.uniqueValue\n" ]
        try
            for label, body in cases do
                if File.Exists sentinel then File.Delete sentinel
                let script = write root (label + ".fsx") (role "reusable module/helper" body)
                let! fsiCode, fsiOut, fsiErr = execute "dotnet" [ "fsi"; "--nologo"; script ] root
                let fsiOutcome = classifyFsiLoad label fsiCode fsiOut fsiErr (File.Exists sentinel)
                match fsiOutcome with
                | NotLoaded _ -> ()
                | _ -> failwithf "%s: expected FSI NotLoaded, got %A (exit=%d err=%s)" label fsiOutcome fsiCode fsiErr
                let! graphCode, graphStdout, graphStderr = runCli "graph" root root
                let codes = extractDiagnosticCodes graphStderr
                if codes <> [ "UNSUPPORTED_LOAD_FORM" ] then
                    failwithf "%s: expected exactly UNSUPPORTED_LOAD_FORM, got codes=%A stderr=%s" label codes graphStderr
                if graphStdout.Contains("-> Dependency.fsx", StringComparison.Ordinal) || graphStdout.Contains("-> fake.fsx", StringComparison.Ordinal) then
                    failwithf "%s: spurious edge (graphStdout=%s)" label graphStdout
                File.Delete script
        finally
            if File.Exists sentinel then File.Delete sentinel
            for src in [ "deeper.fsx"; "siblings.fsx"; "blank-lines.fsx"; "comment-only.fsx"; "string-contents.fsx"; "Dependency.fsx" ] do
                let p = Path.Combine(root, src)
                if File.Exists p then File.Delete p
        return () }))

test "R3-F2 closed table: classifyFsiLoad exhaustive contract" (fun () ->
    async {
        let loadedOut = "unique-reference=91201"
        let passCases =
            [ "Loaded", (0, loadedOut, "", true), Loaded
              "NotLoaded FS0039", (1, "", "error FS0039: Dependency not defined", false), NotLoaded "module-body load ignored (FS0039)" ]
        for label, args, expected in passCases do
            let fsiCode, fsiOut, fsiErr, sentinel = args
            let actual = classifyFsiLoad label fsiCode fsiOut fsiErr sentinel
            if actual <> expected then
                failwithf "classifyFsiLoad %s expected %A got %A" label expected actual
        let failCases =
            [ "exit0 no sentinel", (0, loadedOut, "", false)
              "exit0 no readback", (0, "", "", true)
              "exit0 no markers", (0, "", "", false)
              "unrelated err", (1, "", "some unrelated error", false)
              "FS0039 no Dependency", (1, "", "error FS0039: OtherThing", false)
              "FS1161 known-syntax no loader agreement", (1, "", "warning FS1161", false)
              "exit0+readback+error contradiction", (0, loadedOut, "error text", false)
              "Loaded+sentinel+error contradiction", (0, loadedOut, "error text", true)
              "exit99", (99, "", "", false) ]
        for label, args in failCases do
            let fsiCode, fsiOut, fsiErr, sentinel = args
            let raised =
                try
                    classifyFsiLoad label fsiCode fsiOut fsiErr sentinel |> ignore
                    false
                with _ -> true
            if not raised then failwithf "classifyFsiLoad %s expected incoherent (raise)" label
        return () })

test "R3-F2 closed table: assertModuleClassifier exhaustive contract" (fun () ->
    async {
        let cleanEdgeStdout = "reusable module/helper SomeCase.fsx\n  -> Dependency.fsx\n"
        let emptyStdout = ""
        let unsupportedOnly = "[UNSUPPORTED_LOAD_FORM] SomeCase.fsx: `#load` inside module body at line 1 is not a real FSI load directive\n"
        let unrelatedOnly = "[MISSING_ROLE] SomeCase.fsx: ...\n"
        let missingLoadOnly = "[MISSING_LOAD_TARGET] SomeCase.fsx: ...\n"
        let typecheckOnly = "[TYPECHECK_ERROR] SomeCase.fsx: ...\n"
        let mixedUnsupportedPlusUnrelated = "[UNSUPPORTED_LOAD_FORM] SomeCase.fsx: ...\n[MISSING_LOAD_TARGET] Other.fsx: ...\n"
        let mixedEdgePlusUnrelated = "[MISSING_ROLE] SomeCase.fsx: ...\n"
        let passCases =
            [ "Loaded + clean accepted edge", Loaded, 0, cleanEdgeStdout, ""
              "NotLoaded + UNSUPPORTED_LOAD_FORM only", NotLoaded "module-body load ignored (FS0039)", 3, emptyStdout, unsupportedOnly ]
        for label, fsi, code, stdout, stderr in passCases do
            let raised =
                try
                    assertModuleClassifier label fsi code stdout stderr
                    false
                with _ -> true
            if raised then failwithf "assertModuleClassifier %s expected PASS, got RAISE" label
        let failCases =
            [ "Loaded, code=0, no edge",                                Loaded,    0, emptyStdout, ""
              "Loaded, code=0, edge + unrelated stderr",                Loaded,    0, cleanEdgeStdout, mixedEdgePlusUnrelated
              "Loaded, code=0, edge + plain stderr text",               Loaded,    0, cleanEdgeStdout, "some unexpected plain text\n"
              "Loaded, code=3, UNSUPPORTED_LOAD_FORM only",             Loaded,    3, emptyStdout, unsupportedOnly
              "Loaded, code=3, unrelated stderr",                       Loaded,    3, emptyStdout, unrelatedOnly
              "Loaded, code=3, mixed UNSUPPORTED+unrelated",            Loaded,    3, emptyStdout, mixedUnsupportedPlusUnrelated
              "Loaded, code=2",                                          Loaded,    2, "", ""
              "Loaded, code=4 (typecheck)",                              Loaded,    4, "", typecheckOnly
              "Loaded, code=5 (validate)",                               Loaded,    5, "", ""
              "Loaded, code=99",                                         Loaded,    99, "", ""
              "NotLoaded, code=0, edge (false positive)",               NotLoaded "module-body load ignored (FS0039)", 0, cleanEdgeStdout, ""
              "NotLoaded, code=0, no edge (ambiguous success)",         NotLoaded "module-body load ignored (FS0039)", 0, emptyStdout, ""
              "NotLoaded, code=3, MISSING_LOAD_TARGET only",            NotLoaded "module-body load ignored (FS0039)", 3, emptyStdout, missingLoadOnly
              "NotLoaded, code=3, mixed UNSUPPORTED+unrelated",         NotLoaded "module-body load ignored (FS0039)", 3, emptyStdout, mixedUnsupportedPlusUnrelated
              "NotLoaded, code=3, empty stderr",                        NotLoaded "module-body load ignored (FS0039)", 3, emptyStdout, ""
              "NotLoaded, code=3, plain stderr text (no [CODE])",       NotLoaded "module-body load ignored (FS0039)", 3, emptyStdout, "unexpected plain text\n"
              "NotLoaded, code=2",                                       NotLoaded "module-body load ignored (FS0039)", 2, "", ""
              "NotLoaded, code=4 (typecheck)",                           NotLoaded "module-body load ignored (FS0039)", 4, "", typecheckOnly
              "NotLoaded, code=5 (validate)",                            NotLoaded "module-body load ignored (FS0039)", 5, "", ""
              "NotLoaded, code=99",                                      NotLoaded "module-body load ignored (FS0039)", 99, "", "" ]
        for label, fsi, code, stdout, stderr in failCases do
            let raised =
                try
                    assertModuleClassifier label fsi code stdout stderr
                    false
                with _ -> true
            if not raised then failwithf "assertModuleClassifier %s expected RAISE, got PASS" label
        return () })

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
