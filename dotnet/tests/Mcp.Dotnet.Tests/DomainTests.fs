module Mcp.Dotnet.Tests.DomainTests

open System
open System.IO
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

let private emptyPaths =
    { Directory = ""
      Metadata = ""
      Stdout = ""
      Stderr = ""
      Binlog = ""
      Trx = ""
      ParsedEvidence = "" }

let private runIdFor action =
    use workspace = new TempWorkspace()
    use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), TimeSpan.FromHours 1.0)
    action (startRun registry).RunId

let private statusTests =
    testList "status mapping" [
        testCase "completed zero maps to succeeded"
        <| fun _ ->
            Expect.equal (VerificationStatus.ofProcessStatus (ProcessStatus.Completed 0)) VerificationStatus.Succeeded "zero exit"

        testCase "non-zero exit maps to failed"
        <| fun _ ->
            Expect.equal (VerificationStatus.ofProcessStatus (ProcessStatus.Completed 1)) VerificationStatus.Failed "non-zero exit"

        testCase "timeout maps to timed out"
        <| fun _ ->
            Expect.equal (VerificationStatus.ofProcessStatus ProcessStatus.TimedOut) VerificationStatus.TimedOut "timeout"

        testCase "cancellation maps to cancelled"
        <| fun _ ->
            Expect.equal (VerificationStatus.ofProcessStatus ProcessStatus.Cancelled) VerificationStatus.Cancelled "cancelled"
    ]

let private budgetTests =
    testList "budget validation" [
        testCase "defaults are valid"
        <| fun _ -> Expect.isOk (Budgets.validate Budgets.Defaults) "defaults must validate"

        testCase "non-positive initial error budget is rejected"
        <| fun _ ->
            Budgets.validate { Budgets.Defaults with InitialErrors = 0 }
            |> expectErrorMatching "zero errors" isInvalidInput
            |> ignore

        testCase "page size above maximum is rejected"
        <| fun _ ->
            Budgets.validate
                { Budgets.Defaults with
                    DetailsPageSize = 200
                    DetailsMaxPageSize = 128 }
            |> expectErrorMatching "page > max" isInvalidInput
            |> ignore

        testCase "total result budget below message length is rejected"
        <| fun _ ->
            Budgets.validate
                { Budgets.Defaults with
                    TotalToolResultSize = 100
                    MessageMaxLength = 512 }
            |> expectErrorMatching "total < message" isInvalidInput
            |> ignore

        testCase "inverted timeout range is rejected"
        <| fun _ ->
            Budgets.validate
                { Budgets.Defaults with
                    MinimumTimeout = TimeSpan.FromMinutes 10.0
                    MaximumTimeout = TimeSpan.FromMinutes 1.0 }
            |> expectErrorMatching "inverted timeout" isInvalidInput
            |> ignore

        testCase "timeout outside the accepted range is rejected"
        <| fun _ ->
            Budgets.validateTimeout Budgets.Defaults (Some(TimeSpan.FromMilliseconds 1.0))
            |> expectErrorMatching "below minimum" isInvalidInput
            |> ignore

            Budgets.validateTimeout Budgets.Defaults (Some(TimeSpan.FromHours 2.0))
            |> expectErrorMatching "above maximum" isInvalidInput
            |> ignore

        testCase "timeout defaults and accepted bounds resolve"
        <| fun _ ->
            Expect.equal
                (Budgets.validateTimeout Budgets.Defaults None |> expectOk "default timeout")
                Budgets.Defaults.DefaultTimeout
                "default timeout"

            Expect.equal
                (Budgets.validateTimeout Budgets.Defaults (Some Budgets.Defaults.MinimumTimeout)
                 |> expectOk "minimum timeout")
                Budgets.Defaults.MinimumTimeout
                "minimum timeout"
    ]

let private compactBuildTests =
    testList "compact build semantics" [
        testCase "bounded diagnostics preserve counts and truncate text"
        <| fun _ ->
            runIdFor (fun runId ->
                let longMessage = String('x', 600)

                let diagnostics =
                    [ for index in 1..12 -> errorDiagnostic $"{longMessage}-{index}" ]
                    @ [ for index in 1..3 -> warningDiagnostic $"warn-{index}" ]

                let execution =
                    capturedProcess (ProcessStatus.Completed 1) (TimeSpan.FromMilliseconds 10.0) emptyPaths

                let result = Budgets.compactBuild Budgets.Defaults runId execution diagnostics

                Expect.equal result.Status VerificationStatus.Failed "failed status"
                Expect.equal result.ExitCode (Some 1) "exit code retained"
                Expect.equal result.ErrorCount 12 "all errors counted"
                Expect.equal result.WarningCount 3 "all warnings counted"
                Expect.equal result.Errors.Length Budgets.Defaults.InitialErrors "error subset bounded"

                Expect.isTrue
                    (result.Errors |> List.forall (fun item -> item.Length <= Budgets.Defaults.MessageMaxLength))
                    "error text bounded"

                Expect.isTrue result.DetailsAvailable "details available")

        testCase "timed out execution reports no exit code"
        <| fun _ ->
            runIdFor (fun runId ->
                let execution = capturedProcess ProcessStatus.TimedOut (TimeSpan.FromMilliseconds 5.0) emptyPaths
                let result = Budgets.compactBuild Budgets.Defaults runId execution []

                Expect.equal result.Status VerificationStatus.TimedOut "timeout status"
                Expect.equal result.ExitCode None "no exit code on timeout")
    ]

let private compactTestTests =
    testList "compact test semantics" [
        testCase "failed subset is bounded while counts are retained"
        <| fun _ ->
            runIdFor (fun runId ->
                let cases =
                    [ for index in 1..12 ->
                          { Name = $"FailingTest{index}"
                            Outcome = TestOutcome.Failed
                            Message = Some "assertion failed" } ]

                let evidence =
                    { Counts =
                        Some
                            { Total = 20
                              Passed = 8
                              Failed = 12
                              Skipped = 0 }
                      Cases = cases
                      TrxAvailable = true
                      TrxUnavailableReason = None }

                let execution = capturedProcess (ProcessStatus.Completed 1) (TimeSpan.FromMilliseconds 10.0) emptyPaths
                let result = Budgets.compactTest Budgets.Defaults runId execution evidence

                Expect.equal result.Status VerificationStatus.Failed "failed status"
                Expect.equal result.FailedTests.Length Budgets.Defaults.InitialFailedTests "failed subset bounded"
                Expect.equal result.Counts (Some evidence.Counts.Value) "counts retained"
                Expect.isTrue result.TrxAvailable "trx reported available")

        testCase "missing TRX reason is surfaced"
        <| fun _ ->
            runIdFor (fun runId ->
                let evidence =
                    { Counts = None
                      Cases = []
                      TrxAvailable = false
                      TrxUnavailableReason = Some "TRX result was not produced by dotnet test" }

                let execution = capturedProcess (ProcessStatus.Completed 0) (TimeSpan.FromMilliseconds 10.0) emptyPaths
                let result = Budgets.compactTest Budgets.Defaults runId execution evidence

                Expect.equal result.TrxAvailable false "trx unavailable"
                Expect.equal result.TrxUnavailableReason evidence.TrxUnavailableReason "reason retained")
    ]

let private paginationTests =
    testList "detail pagination" [
        testCase "negative offset is rejected"
        <| fun _ ->
            Budgets.page Budgets.Defaults { RunId = "r"; Kind = DetailKind.Errors; Offset = -1; Limit = None } [ "a" ]
            |> expectErrorMatching "negative offset" isInvalidPagination
            |> ignore

        testCase "limit below one and above maximum are rejected"
        <| fun _ ->
            Budgets.page Budgets.Defaults { RunId = "r"; Kind = DetailKind.Errors; Offset = 0; Limit = Some 0 } [ "a" ]
            |> expectErrorMatching "zero limit" isInvalidPagination
            |> ignore

            Budgets.page
                Budgets.Defaults
                { RunId = "r"
                  Kind = DetailKind.Errors
                  Offset = 0
                  Limit = Some(Budgets.Defaults.DetailsMaxPageSize + 1) }
                [ "a" ]
            |> expectErrorMatching "limit above maximum" isInvalidPagination
            |> ignore

        testCase "default page size and hasMore are applied"
        <| fun _ ->
            let values = [ for index in 1..40 -> string index ]

            let page =
                Budgets.page Budgets.Defaults { RunId = "r"; Kind = DetailKind.Errors; Offset = 0; Limit = None } values
                |> expectOk "first page"

            Expect.equal page.Limit Budgets.Defaults.DetailsPageSize "default limit"
            Expect.equal page.Items.Length Budgets.Defaults.DetailsPageSize "page item count"
            Expect.equal page.Total 40 "total count"
            Expect.isTrue page.HasMore "more items remain"

        testCase "offset past the end yields an empty terminal page"
        <| fun _ ->
            let page =
                Budgets.page Budgets.Defaults { RunId = "r"; Kind = DetailKind.Errors; Offset = 100; Limit = Some 5 } [ "a"; "b" ]
                |> expectOk "tail page"

            Expect.equal page.Items [] "no items"
            Expect.equal page.HasMore false "no more items"

        testCase "page items are length bounded"
        <| fun _ ->
            let page =
                Budgets.page
                    Budgets.Defaults
                    { RunId = "r"
                      Kind = DetailKind.Errors
                      Offset = 0
                      Limit = Some 1 }
                    [ String('y', 900) ]
                |> expectOk "bounded page"

            Expect.isTrue (page.Items |> List.forall (fun item -> item.Length <= Budgets.Defaults.MessageMaxLength)) "bounded text"
    ]

let private parsingTests =
    testList "parsing" [
        testCase "build diagnostics parse located and unlocated entries"
        <| fun _ ->
            let stdout =
                "  Determining projects to restore...\r\n"
                + "C:\\src\\a.cs(10,5): error CS0103: The name 'x' does not exist\r\n"
                + "C:\\src\\a.cs(11,6): warning CS0168: variable declared but never used\r\n"

            let stderr = "error CS1002: ; expected\nBuild FAILED.\n"
            let diagnostics = Parsers.buildDiagnostics stdout stderr

            Expect.equal diagnostics.Length 3 "diagnostic count"

            let first = diagnostics.Head
            Expect.equal first.Severity DiagnosticSeverity.ErrorDiagnostic "first severity"
            Expect.equal first.Code (Some "CS0103") "first code"
            Expect.equal first.Line (Some 10) "first line"
            Expect.equal first.Column (Some 5) "first column"

            let unlocated = diagnostics |> List.last
            Expect.equal unlocated.Severity DiagnosticSeverity.ErrorDiagnostic "unlocated severity"
            Expect.equal unlocated.Code (Some "CS1002") "unlocated code"
            Expect.equal unlocated.File None "unlocated file"

        testCase "TRX results produce counts and cases"
        <| fun _ ->
            use workspace = new TempWorkspace()

            let trxPath =
                workspace.Write(
                    "results.trx",
                    trxDocument
                        [ ("Alpha", "Passed", None)
                          ("Beta", "Failed", Some "expected 1 but got 2")
                          ("Gamma", "NotExecuted", None) ]
                )

            let evidence = Parsers.tests trxPath "" ""

            Expect.isTrue evidence.TrxAvailable "trx available"
            Expect.equal evidence.Cases.Length 3 "case count"

            Expect.equal
                evidence.Counts
                (Some
                    { Total = 3
                      Passed = 1
                      Failed = 1
                      Skipped = 1 })
                "counts"

            Expect.equal (evidence.Cases |> List.filter (fun item -> item.Outcome = TestOutcome.Failed)).Length 1 "failed case"

        testCase "missing TRX falls back to console counts with an explicit reason"
        <| fun _ ->
            let evidence =
                Parsers.tests "does-not-exist.trx" "Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total: 3" ""

            Expect.equal evidence.TrxAvailable false "trx unavailable"
            Expect.isSome evidence.TrxUnavailableReason "reason present"

            Expect.equal
                evidence.Counts
                (Some
                    { Total = 3
                      Passed = 2
                      Failed = 1
                      Skipped = 0 })
                "console counts"

        testCase "missing TRX without console summary keeps counts absent"
        <| fun _ ->
            let evidence = Parsers.tests "does-not-exist.trx" "" ""
            Expect.equal evidence.TrxAvailable false "trx unavailable"
            Expect.equal evidence.Counts None "no counts"
    ]

let private textTests =
    testList "diagnostic text" [
        testCase "located diagnostic renders location and code"
        <| fun _ ->
            Expect.equal (Budgets.diagnosticText (errorDiagnostic "boom")) "a.cs(1,2): CS0001 boom" "located text"

        testCase "unlocated diagnostic omits location"
        <| fun _ ->
            let diagnostic =
                { Severity = DiagnosticSeverity.WarningDiagnostic
                  Code = Some "CS0168"
                  File = None
                  Line = None
                  Column = None
                  Message = "unused" }

            Expect.equal (Budgets.diagnosticText diagnostic) "CS0168 unused" "unlocated text"

        testCase "test text includes message when present"
        <| fun _ ->
            Expect.equal
                (Budgets.testText
                    { Name = "T1"
                      Outcome = TestOutcome.Failed
                      Message = Some "bad" })
                "T1: bad"
                "with message"

            Expect.equal
                (Budgets.testText
                    { Name = "T1"
                      Outcome = TestOutcome.Passed
                      Message = None })
                "T1"
                "without message"
    ]

let private paginationProperties =
    testList "pagination properties" [
        testProperty "page invariant holds for generated offsets and limits"
        <| fun (rawOffset: int, rawLimit: int) ->
            let offset = ((rawOffset % 100) + 100) % 100
            let limit = ((rawLimit % 128) + 128) % 128 + 1
            let values = [ for index in 1..20 -> string index ]
            let total = values.Length

            let request =
                { RunId = "run"
                  Kind = DetailKind.Errors
                  Offset = offset
                  Limit = Some limit }

            match Budgets.page Budgets.Defaults request values with
            | Error _ -> true
            | Ok page ->
                let expectedCount = if offset >= total then 0 else min limit (total - offset)

                page.Items.Length = expectedCount
                && page.Total = total
                && (page.HasMore = (offset + page.Items.Length < total))
    ]

let tests =
    testList
        "domain"
        [ statusTests
          budgetTests
          compactBuildTests
          compactTestTests
          paginationTests
          parsingTests
          textTests
          paginationProperties ]
