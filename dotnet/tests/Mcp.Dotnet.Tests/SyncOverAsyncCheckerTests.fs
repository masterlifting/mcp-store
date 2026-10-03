module Mcp.Dotnet.Tests.SyncOverAsyncCheckerTests

open System
open System.IO
open Expecto
open Mcp.Dotnet.Tests.SyncOverAsyncChecker

/// Positive fixtures prove the scanner flags every documented forbidden
/// pattern even when those patterns are written inside a script body.
let positiveFixtures =
    testList "positive fixtures" [
        testCase "flags bare .Result on a Task-typed receiver" <| fun _ ->
            let text = "let value = task.Result\n"
            let findings = checkText "fixture.fs" text
            let blocking = violations findings
            Expect.equal blocking.Length 1 "blocking .Result on task"
            Expect.equal blocking.[0].Pattern ".Result" "pattern recorded"

        testCase "flags sync .Wait() without timeout" <| fun _ ->
            let text = "release.Wait()\n"
            let findings = checkText "fixture.fs" text
            let blocking = violations findings
            Expect.equal blocking.Length 1 "blocking Wait()"
            Expect.equal blocking.[0].Pattern ".Wait()" "pattern recorded"

        testCase "flags GetAwaiter().GetResult()" <| fun _ ->
            let text = "let x = task.GetAwaiter().GetResult()\n"
            let findings = checkText "fixture.fs" text
            let blocking = violations findings
            Expect.equal blocking.Length 1 "blocking GetAwaiter().GetResult"
            Expect.equal blocking.[0].Pattern "GetAwaiter().GetResult" "pattern recorded"

        testCase "flags Task.WaitAll" <| fun _ ->
            let text = "Task.WaitAll [||]\n"
            let findings = checkText "fixture.fs" text
            let blocking = violations findings
            Expect.isGreaterThan blocking.Length 0 "blocking Task.WaitAll"

        testCase "flags sync WaitForExit() without timeout" <| fun _ ->
            let text = "child.WaitForExit()\n"
            let findings = checkText "fixture.fs" text
            let blocking = violations findings
            Expect.equal blocking.Length 1 "blocking WaitForExit()"
            Expect.equal blocking.[0].Pattern "WaitForExit()" "pattern recorded"

        testCase "flags sync ReadToEnd() without timeout" <| fun _ ->
            let text = "let text = stream.ReadToEnd()\n"
            let findings = checkText "fixture.fs" text
            let blocking = violations findings
            Expect.equal blocking.Length 1 "blocking ReadToEnd()"
            Expect.equal blocking.[0].Pattern "ReadToEnd()" "pattern recorded"
    ]

/// Negative fixtures prove the allowlist absorbs the legitimate cases without
/// requiring blanket exclusion of `.Result` or `.Wait`.
let negativeFixtures =
    testList "negative fixtures" [
        testCase "allowlists WorkItem.Result as a record-field receiver" <| fun _ ->
            let text = "let value = workItem.Result\n"
            let findings = checkText "fixture.fs" text
            Expect.isEmpty (violations findings) "WorkItem.Result is a record field"

        testCase "allowlists a WorkItem.Result in assertEqual" <| fun _ ->
            let text = "assertEqual \"r\" (Some \"x\") workItem.Result\n"
            let findings = checkText "fixture.fs" text
            Expect.isEmpty (violations findings) "WorkItem.Result stays allowed"

        testCase "allowlists a SYNC-OVER-ASYNC-ALLOW marker" <| fun _ ->
            let text =
                "// SYNC-OVER-ASYNC-ALLOW: deliberate owner-thread mutex event\nreleaseRequested.Wait()\n"

            let findings = checkText "fixture.fs" text
            Expect.isEmpty (violations findings) "marked Wait() is allowed"

        testCase "ignore generated dist/ and bin/ paths" <| fun _ ->
            let tempRoot =
                Path.Combine(
                    Path.GetTempPath(),
                    "mcp-dotnet-checker-excludes",
                    Guid.NewGuid().ToString("N")
                )

            try
                Directory.CreateDirectory tempRoot |> ignore
                let distDirectory = Path.Combine(tempRoot, "dist")
                Directory.CreateDirectory distDirectory |> ignore
                File.WriteAllText(Path.Combine(distDirectory, "Generated.fs"), "let v = task.Result\n")
                File.WriteAllText(Path.Combine(tempRoot, "Real.fs"), "let v = workItem.Result\n")

                let findings = checkTree tempRoot
                Expect.isEmpty (violations findings) "generated output excluded"
            finally
                if Directory.Exists tempRoot then
                    Directory.Delete(tempRoot, true)

        testCase "ignore scoped extensions" <| fun _ ->
            let tempRoot =
                Path.Combine(
                    Path.GetTempPath(),
                    "mcp-dotnet-checker-scope",
                    Guid.NewGuid().ToString("N")
                )

            try
                Directory.CreateDirectory tempRoot |> ignore
                File.WriteAllText(Path.Combine(tempRoot, "notes.txt"), "let v = task.Result\n")
                File.WriteAllText(Path.Combine(tempRoot, "code.fs"), "let v = workItem.Result\n")

                let findings = checkTree tempRoot
                Expect.isEmpty (violations findings) ".txt files are out of scope"
            finally
                if Directory.Exists tempRoot then
                    Directory.Delete(tempRoot, true)
    ]

/// End-to-end invariant: the checked-in F# surface must not introduce a new
/// unallowed blocking pattern. The check is best-effort: every block is
/// paired with a fixture so a regression introduces a known failure mode.
let surfaceTests =
    testList "repository surface" [
        testCase "active root F# helpers and dotnet tests stay clean" <| fun _ ->
            let root = repositoryRoot ()
            let checked =
                [ Path.Combine(root, "BuildProvenance.fsx")
                  Path.Combine(root, "DistributionBuild.fsx")
                  Path.Combine(root, "DistributionTestHelper.fsx")
                  Path.Combine(root, "ReleasePins.fsx")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "Support.fs")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "DomainTests.fs")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "SecurityTests.fs")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "ProcessIntegrationTests.fs")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "InvocationTests.fs")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "QuotaTests.fs")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "McpHostTests.fs")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "DotnetSchemaParityTests.fs")
                  Path.Combine(root, "dotnet", "tests", "Mcp.Dotnet.Tests", "SyncOverAsyncChecker.fs") ]

            let unallowed =
                checked
                |> List.collect (fun path ->
                    if File.Exists path then
                        checkFile path
                    else
                        [])

            let blocking = violations unallowed

            for finding in blocking do
                failwithf
                    "%s:%d:%d %s in %s"
                    finding.Path
                    finding.Line
                    finding.Column
                    finding.Text
                    finding.Pattern
    ]

let tests =
    testList "sync-over-async checker"
        [ positiveFixtures
          negativeFixtures
          surfaceTests ]
