module Mcp.Dotnet.Tests.SyncOverAsyncCheckerTests

open System
open System.IO
open Expecto
open Mcp.Dotnet.Tests.SyncOverAsyncChecker
open Mcp.Dotnet.Tests.Support

let private fixtureTests =
    testList "source scanner fixtures" [
        testCase "task.Result and parameterless task.Wait are detected" <| fun _ ->
            let findings = checkText "sample.fs" "let a = pendingTask.Result\nlet b = pendingTask.Wait()\n"
            Expect.equal (violations findings |> List.map _.Pattern) [ "Task.Result"; "Task.Wait" ] "both receiver patterns match"
            Expect.equal findings.Head.Line 1 "first line is one-based"
            Expect.equal findings.Head.Column 9 "column is one-based"

        testCase "blocking APIs are detected" <| fun _ ->
            let source =
                "task.GetAwaiter().GetResult()\nTask.WaitAll [||]\nTask.WaitAny [||]\nchild.WaitForExit(1000)\nreader.ReadToEnd()\n"
            let findings = checkText "sample.fs" source |> violations
            Expect.equal (findings |> List.map _.Pattern)
                [ "GetAwaiter().GetResult"; "Task.WaitAll"; "Task.WaitAny"; "WaitForExit"; "ReadToEnd" ]
                "all blocking forms are covered"

        testCase "comments, strings, and test fixture literals are not source calls" <| fun _ ->
            let source =
                "// task.Result\nlet text = \"task.Result and stream.ReadToEnd()\"\nlet longText = \"\"\"task.Wait()\"\"\"\n(* task.Wait() *)\n"
            Expect.isEmpty (checkText "sample.fs" source) "non-code text is masked"

        testCase "only reasoned non-Task Result field marker is accepted" <| fun _ ->
            let allowed = "// Non-Task Result field: This is the Workflow WorkItem record payload.\nlet saved = workItem.Result\n"
            Expect.isEmpty (violations (checkText "sample.fs" allowed)) "one exact reason marker permits the record field"

            let unmarked = checkText "sample.fs" "let saved = workItem.Result\n"
            Expect.equal (violations unmarked |> List.map _.Pattern) [ "Task.Result" ] "receiver names do not form an allowlist"

            let emptyReason = "// Non-Task Result field:\nlet saved = workItem.Result\n"
            Expect.equal (violations (checkText "sample.fs" emptyReason) |> List.length) 1 "empty marker is not an exemption"

        testCase "only listed top-level entry bridge site is accepted" <| fun _ ->
            let documented =
                "// Standalone entry bridge: this script owns the process entry.\nmatch execute (GetTask { Root = args.[0]; TaskId = args.[1] }) |> Async.RunSynchronously with\n"
            let allowed = checkText "workflow/tests/TaskGet.fsx" documented
            Expect.isEmpty (violations allowed) "documented exact entry path is allowed"

            let helper = "let bridge () = Async.RunSynchronously(work)\n"
            Expect.equal (violations (checkText "workflow/tests/TaskGet.fsx" helper) |> List.length) 1 "helper bridge is rejected"

            Expect.equal (violations (checkText "other.fsx" documented) |> List.length) 1 "unlisted flow bridge is rejected"

            let duplicate = documented + "runAgain () |> Async.RunSynchronously\n"
            Expect.equal (violations (checkText "workflow/tests/TaskGet.fsx" duplicate) |> List.length) 2 "more than one bridge in a flow is rejected"

        testCase "ordinary inline comments cannot exempt blocking calls" <| fun _ ->
            let source = "// SYNC-OVER-ASYNC-ALLOW: no suppression syntax exists\ntask.Wait()\n"
            Expect.equal (violations (checkText "sample.fs" source) |> List.length) 1 "inline comment is not an exemption"

        testCase "generated output directories are excluded, active code is scanned" <| fun _ ->
            let root = Path.Combine(Path.GetTempPath(), "mcp-checker", Guid.NewGuid().ToString("N"))
            try
                let generated = Path.Combine(root, "workflow", "obj")
                Directory.CreateDirectory generated |> ignore
                File.WriteAllText(Path.Combine(generated, "Generated.fs"), "task.Result")
                File.WriteAllText(Path.Combine(root, "active.fs"), "task.Result")
                let findings = checkTree root |> violations
                Expect.equal findings.Length 1 "only active source is scanned"
                Expect.equal (Path.GetFileName findings.Head.Path) "active.fs" "active path is reported"
            finally
                if Directory.Exists root then Directory.Delete(root, true)
    ]

let private repositorySurfaceTests =
    testList "repository F# surface" [
        testCase "all active root, dotnet, and workflow source files are checked" <| fun _ ->
            let root = repositoryRoot ()
            let allFiles = enumerateFiles root
            let hasRootHelpers = allFiles |> List.exists (fun path -> Path.GetFileName(path) = "BuildProvenance.fsx")
            let hasDotnet = allFiles |> List.exists (fun path -> path.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
            let hasWorkflow = allFiles |> List.exists (fun path -> path.Contains("workflow", StringComparison.OrdinalIgnoreCase))
            Expect.isTrue hasRootHelpers "root helper scripts are included"
            Expect.isTrue hasDotnet "dotnet source and tests are included"
            Expect.isTrue hasWorkflow "workflow source and tests are included"

            let failures = checkTree root |> violations
            let summary = failures |> List.map formatFinding |> String.concat Environment.NewLine
            Expect.isEmpty failures summary
    ]

let tests = testList "sync-over-async checker" [ fixtureTests; repositorySurfaceTests ]
