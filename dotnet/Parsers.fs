namespace Mcp.Dotnet

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq

module Parsers =
    let private shorten (value: string) =
        if isNull value || value.Length <= Budgets.Defaults.MessageMaxLength then
            value
        else
            value.Substring(0, Budgets.Defaults.MessageMaxLength)

    let private diagnosticPattern =
        Regex(
            "^(?<file>.*?)(?:\\((?<line>\\d+),(?<column>\\d+)\\))?:\\s*(?<severity>error|warning)\\s*(?<code>[A-Za-z0-9]+)?\\s*:\\s*(?<message>.*)$",
            RegexOptions.Compiled ||| RegexOptions.IgnoreCase
        )

    let private unlocatedDiagnosticPattern =
        Regex(
            "^(?<severity>error|warning)\\s+(?<code>[A-Za-z0-9]+)?\\s*:\\s*(?<message>.*)$",
            RegexOptions.Compiled ||| RegexOptions.IgnoreCase
        )

    let private parseLine (line: string) =
        let matchResult = diagnosticPattern.Match line

        let matchResult, hasLocation =
            if matchResult.Success then
                matchResult, true
            else
                unlocatedDiagnosticPattern.Match line, false

        if not matchResult.Success then
            None
        else
            let severity =
                if matchResult.Groups.["severity"].Value.Equals("error", StringComparison.OrdinalIgnoreCase) then
                    DiagnosticSeverity.ErrorDiagnostic
                else
                    DiagnosticSeverity.WarningDiagnostic

            let optionalInt (name: string) =
                match Int32.TryParse matchResult.Groups.[name].Value with
                | true, value -> Some value
                | false, _ -> None

            let file =
                if not hasLocation then
                    None
                else
                    matchResult.Groups.["file"].Value.Trim()
                    |> function
                        | "" -> None
                        | value -> Some value

            let code =
                matchResult.Groups.["code"].Value.Trim()
                |> function
                    | "" -> None
                    | value -> Some value

            Some
                { Severity = severity
                  Code = code
                  File = file
                  Line = optionalInt "line"
                  Column = optionalInt "column"
                  Message = matchResult.Groups.["message"].Value.Trim() |> shorten }

    let buildDiagnostics (stdout: string) (stderr: string) =
        let lines (text: string) =
            seq {
                use reader = new StringReader(text)
                let mutable line = reader.ReadLine()

                while not (isNull line) do
                    yield line
                    line <- reader.ReadLine()
            }

        Seq.append (lines stdout) (lines stderr)
        |> Seq.choose parseLine
        |> Seq.truncate Budgets.MaxRetainedDiagnostics
        |> Seq.toList

    let private localName name (element: XElement) = element.Name.LocalName = name

    let private parseOutcome (value: string) =
        match value.ToLowerInvariant() with
        | "passed" -> TestOutcome.Passed
        | "failed" -> TestOutcome.Failed
        | "notexecuted"
        | "skipped" -> TestOutcome.Skipped
        | _ -> TestOutcome.Inconclusive

    // Pure parsing over an already-acquired document.
    let private parseTrxDocument (document: XDocument) =
        let cases =
            document.Descendants()
            |> Seq.filter (localName "UnitTestResult")
            |> Seq.map (fun item ->
                let attribute (name: string) =
                    item.Attribute(XName.Get name) |> Option.ofObj |> Option.map _.Value

                { Name = attribute "testName" |> Option.defaultValue "unnamed test" |> shorten
                  Outcome =
                    attribute "outcome"
                    |> Option.map parseOutcome
                    |> Option.defaultValue TestOutcome.Inconclusive
                  Message =
                    item.Descendants()
                    |> Seq.filter (fun child ->
                        child.Name.LocalName = "Message" || child.Name.LocalName = "StackTrace")
                    |> Seq.tryHead
                    |> Option.map _.Value
                    |> Option.map shorten
                    |> Option.bind (fun value -> if String.IsNullOrWhiteSpace value then None else Some value) })
            |> Seq.truncate Budgets.MaxRetainedTestCases
            |> Seq.toList

        let counts =
            let total = cases.Length

            { Total = total
              Passed = cases |> List.filter (fun item -> item.Outcome = TestOutcome.Passed) |> List.length
              Failed = cases |> List.filter (fun item -> item.Outcome = TestOutcome.Failed) |> List.length
              Skipped = cases |> List.filter (fun item -> item.Outcome = TestOutcome.Skipped) |> List.length }

        cases, Some counts

    // Acquisition boundary: expected absence/quota rejection are classified here;
    // the XML parse itself is pure over an owned text snapshot.
    let private readTrxAsync path : Async<Result<XDocument, string>> =
        async {
            if not (File.Exists path) then
                return Error "TRX result was not produced by dotnet test"
            else
                try
                    if FileInfo(path).Length > Budgets.DefaultArtifactQuotas.MaxTrxBytes then
                        return Error "TRX result exceeded the retained artifact quota"
                    else
                        let! text = File.ReadAllTextAsync path |> Async.AwaitTask
                        return Ok(XDocument.Parse text)
                with error ->
                    return Error $"TRX result could not be read: {error.Message}"
        }

    let private consoleCounts stdout stderr =
        let summaryPattern =
            Regex(
                "Failed:\\s*(?<failed>\\d+),\\s*Passed:\\s*(?<passed>\\d+),\\s*Skipped:\\s*(?<skipped>\\d+)",
                RegexOptions.Compiled ||| RegexOptions.IgnoreCase
            )

        let text = stdout + Environment.NewLine + stderr
        let result = summaryPattern.Match text

        if not result.Success then
            None
        else
            // Oversized or malformed summary numbers are rejected rather than
            // overflowing the total; the caller falls back to unavailable counts.
            let number (name: string) =
                match Int64.TryParse result.Groups.[name].Value with
                | true, value -> Some value
                | false, _ -> None

            match number "failed", number "passed", number "skipped" with
            | Some failed, Some passed, Some skipped
                when failed <= int64 Int32.MaxValue
                     && passed <= int64 Int32.MaxValue
                     && skipped <= int64 Int32.MaxValue
                     && failed + passed + skipped <= int64 Int32.MaxValue ->
                Some
                    { Total = int (failed + passed + skipped)
                      Passed = int passed
                      Failed = int failed
                      Skipped = int skipped }
            | _ -> None

    let tests trxPath stdout stderr : Async<TestEvidence> =
        async {
            match! readTrxAsync trxPath with
            | Ok document ->
                let cases, counts = parseTrxDocument document

                return
                    { Counts = counts
                      Cases = cases
                      TrxAvailable = true
                      TrxUnavailableReason = None }
            | Error reason ->
                return
                    { Counts = consoleCounts stdout stderr
                      Cases = []
                      TrxAvailable = false
                      TrxUnavailableReason = Some reason }
        }
