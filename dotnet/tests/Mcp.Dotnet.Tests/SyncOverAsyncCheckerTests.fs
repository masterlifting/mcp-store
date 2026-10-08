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
            Expect.equal findings.Head.Column 20 "column identifies the member token"

        testCase "member blockers match expression receivers and preserve token locations" <| fun _ ->
            let source =
                "let x = (getTask()).Result\nlet item = tasks.[0].Result\nlet selected = task'.Result\nlet timed = (getTask()).Wait()\n"
            let findings = checkText "sample.fs" source |> violations
            Expect.equal
                (findings |> List.map (fun finding -> finding.Pattern, finding.Line, finding.Column))
                [ "Task.Result", 1, 20
                  "Task.Result", 2, 21
                  "Task.Result", 3, 21
                  "Task.Wait", 4, 24 ]
                "arbitrary receivers and primed identifiers retain precise locations"

        testCase "character literals and type variables are not apostrophe truncation points" <| fun _ ->
            let source = "type Generic<'T> = 'T\nlet character = '.'\nlet selected = workItem.Result\n"
            let findings = checkText "sample.fs" source |> violations
            Expect.equal (findings |> List.map (fun finding -> finding.Line, finding.Column)) [ 3, 24 ] "only the member token is reported"

        testCase "nested interpolation quotes trigger fail-closed raw scanning" <| fun _ ->
            let source =
                "let nestedResult = $\"{(if \"x\" = \"x\" then pendingTask else otherTask).Result}\"\nlet nestedWait = $\"{(if \"x\" = \"x\" then pendingTask else otherTask).Wait()}\"\nlet multipleHoles = $\"{(if \"x\" = \"x\" then pendingTask else otherTask).Result}{(if \"y\" = \"y\" then pendingTask else otherTask).Wait()}\"\nlet commentBraceResult = $\"{(if (* } *) \"x\" = \"x\" then pendingTask else otherTask).Result}\"\nlet commentBraceWait = $\"{(if (* } *) \"x\" = \"x\" then pendingTask else otherTask).Wait()}\"\nlet closedHoleFirst = $\"{pendingTask.Result}{(if \"z\" = \"z\" then pendingTask else otherTask).Wait()}\"\n"
            let lines = source.Split('\n')
            let findings = checkText "sample.fs" source |> violations
            Expect.equal
                (findings |> List.map (fun finding -> finding.Pattern, finding.Line, finding.Column))
                [ "Task.Result", 1, lines.[0].IndexOf(".Result", StringComparison.Ordinal) + 1
                  "Task.Result", 3, lines.[2].IndexOf(".Result", StringComparison.Ordinal) + 1
                  "Task.Result", 4, lines.[3].IndexOf(".Result", StringComparison.Ordinal) + 1
                  "Task.Result", 6, lines.[5].IndexOf(".Result", StringComparison.Ordinal) + 1
                  "Task.Wait", 2, lines.[1].IndexOf(".Wait(", StringComparison.Ordinal) + 1
                  "Task.Wait", 3, lines.[2].IndexOf(".Wait(", StringComparison.Ordinal) + 1
                  "Task.Wait", 5, lines.[4].IndexOf(".Wait(", StringComparison.Ordinal) + 1
                  "Task.Wait", 6, lines.[5].IndexOf(".Wait(", StringComparison.Ordinal) + 1 ]
                "quotes inside open holes cannot hide member blockers"

        testCase "interpolated contents remain visible, including conservative literal matches" <| fun _ ->
            let source =
                "let formatted = $\"{task.Result}\"\nlet literal = $\"literal task.Wait()\"\nlet verbatim = $@\"literal task.Result\"\nlet verbatimAlt = @$\"literal task.Wait()\"\nlet raw = $\"\"\"literal task.Result\"\"\"\n"
            let findings = checkText "sample.fs" source |> violations
            Expect.equal
                (findings |> List.map (fun finding -> finding.Pattern, finding.Line, finding.Column))
                [ "Task.Result", 1, 24
                  "Task.Result", 3, 31
                  "Task.Result", 5, 27
                  "Task.Wait", 2, 29
                  "Task.Wait", 4, 34 ]
                "interpolation expressions and literal text are scanned conservatively"

            Expect.isEmpty
                (checkText "sample.fs" "WaitHandle.WaitAny [| releaseRequested; mutex |]\n" |> violations)
                "native wait-handle arbitration is distinct from Task.WaitAny"

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

        testCase "ordinary strings consume escapes forward and verbatim strings use doubled quotes" <| fun _ ->
            let source =
                """let even = "value\\"
let afterEven = pendingTask.Result
let odd = "value\\\" pendingTask.Result and pendingTask.Wait() still"
let afterOdd = pendingTask.Wait()
let continued = "value\
    pendingTask.Result"
let afterContinuation = pendingTask.Wait()
let verbatim = @"quoted "" pendingTask.Wait() "" value"
let afterVerbatim = pendingTask.Result
"""
            let lines = source.Split('\n')
            let findings = checkText "sample.fs" source |> violations
            Expect.equal
                (findings |> List.map (fun finding -> finding.Pattern, finding.Line, finding.Column))
                [ "Task.Result", 2, lines.[1].IndexOf(".Result", StringComparison.Ordinal) + 1
                  "Task.Result", 9, lines.[8].IndexOf(".Result", StringComparison.Ordinal) + 1
                  "Task.Wait", 4, lines.[3].IndexOf(".Wait(", StringComparison.Ordinal) + 1
                  "Task.Wait", 7, lines.[6].IndexOf(".Wait(", StringComparison.Ordinal) + 1 ]
                "escaped literals stay masked without swallowing later receiver tokens"

        testCase "verbatim prefix keeps trailing backslashes from escaping the closing quote" <| fun _ ->
            let source =
                """let trailingBackslash = @"path\"
let afterTrailingBackslash = pendingTask.Result
let evenBackslashes = @"path\\"
let afterEvenBackslashes = pendingTask.Wait()
let doubledQuotes = @"quoted ""value"" with pendingTask.Result in literal"
let afterDoubledQuotes = pendingTask.GetAwaiter().GetResult()
let trailingBackslashAndDoubledQuotes = @"path\ ""value"" still"
let afterCombo = pendingTask.Wait()
let literalCalls = @"pendingTask.Result pendingTask.Wait()"
"""
            let lines = source.Split('\n')
            let findings = checkText "sample.fs" source |> violations
            Expect.equal
                (findings |> List.map (fun finding -> finding.Pattern, finding.Line, finding.Column))
                [ "Task.Result", 2, lines.[1].IndexOf(".Result", StringComparison.Ordinal) + 1
                  "Task.Wait", 4, lines.[3].IndexOf(".Wait(", StringComparison.Ordinal) + 1
                  "Task.Wait", 8, lines.[7].IndexOf(".Wait(", StringComparison.Ordinal) + 1
                  "GetAwaiter().GetResult", 6, lines.[5].IndexOf("GetAwaiter()", StringComparison.Ordinal) + 1 ]
                "verbatim strings close at unpaired quotes regardless of backslash runs"

        testCase "native mutex arbitration does not match Task.WaitAny" <| fun _ ->
            let findings = checkText "sample.fs" "WaitHandle.WaitAny [| releaseRequested; mutex |]\nTask.WaitAny [||]\n" |> violations
            Expect.equal (findings |> List.map _.Pattern) [ "Task.WaitAny" ] "only the Task wait primitive is forbidden"

        testCase "only reasoned non-Task Result field marker is accepted" <| fun _ ->
            let allowed = "// Non-Task Result field: This is the Workflow WorkItem record payload.\nlet saved = workItem.Result\n"
            Expect.isEmpty (violations (checkText "sample.fs" allowed)) "one exact reason marker permits the record field"

            let unmarked = checkText "sample.fs" "let saved = workItem.Result\n"
            Expect.equal (violations unmarked |> List.map _.Pattern) [ "Task.Result" ] "receiver names do not form an allowlist"

            let emptyReason = "// Non-Task Result field:\nlet saved = workItem.Result\n"
            Expect.equal (violations (checkText "sample.fs" emptyReason) |> List.length) 1 "empty marker is not an exemption"

            let mixed =
                "// Non-Task Result field: the left member is a Workflow record field.\nlet pair = workItem.Result + pendingTask.Result\n"
            let mixedFindings = checkText "sample.fs" mixed |> violations
            Expect.equal (mixedFindings |> List.map _.Exemption) [ None; None ] "one marker cannot exempt a record/task pair"

            let multiple =
                "// Non-Task Result field: generic reason cannot identify one occurrence.\nlet pair = left.Result + right.Result\n"
            let multipleFindings = checkText "sample.fs" multiple |> violations
            Expect.equal (multipleFindings |> List.map _.Exemption) [ None; None ] "one marker cannot exempt multiple same-line occurrences"

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
            let hasInfrastructureProvenance =
                allFiles
                |> List.exists (fun path ->
                    String.Equals(
                        Path.GetRelativePath(root, path).Replace('\\', '/'),
                        "infrastructure/Provenance.fsx",
                        StringComparison.Ordinal
                    ))
            let hasDotnet = allFiles |> List.exists (fun path -> path.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
            let hasWorkflow = allFiles |> List.exists (fun path -> path.Contains("workflow", StringComparison.OrdinalIgnoreCase))
            Expect.isTrue hasInfrastructureProvenance "shared infrastructure provenance is included"
            Expect.isTrue hasDotnet "dotnet source and tests are included"
            Expect.isTrue hasWorkflow "workflow source and tests are included"

            let failures = checkTree root |> violations
            let summary = failures |> List.map formatFinding |> String.concat Environment.NewLine
            Expect.isEmpty failures summary
    ]

let tests = testList "sync-over-async checker" [ fixtureTests; repositorySurfaceTests ]
