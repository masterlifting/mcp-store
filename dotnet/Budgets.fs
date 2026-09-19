namespace Mcp.Verifier

open System

module Budgets =
    [<Literal>]
    let MaxRetainedDiagnostics = 4096

    [<Literal>]
    let MaxRetainedTestCases = 4096

    let Defaults =
        { InitialErrors = 8
          InitialWarnings = 8
          InitialFailedTests = 8
          DetailsPageSize = 32
          DetailsMaxPageSize = 128
          MessageMaxLength = 512
          TotalToolResultSize = 16 * 1024
          DefaultTimeout = TimeSpan.FromMinutes 5.0
          MinimumTimeout = TimeSpan.FromMilliseconds 100.0
          MaximumTimeout = TimeSpan.FromMinutes 30.0 }

    let DefaultArtifactQuotas =
        { MaxStdoutBytes = 4L * 1024L * 1024L
          MaxStderrBytes = 4L * 1024L * 1024L
          MaxBinlogBytes = 16L * 1024L * 1024L
          MaxTrxBytes = 16L * 1024L * 1024L
          MaxRunBytes = 40L * 1024L * 1024L
          MaxAggregateBytes = 256L * 1024L * 1024L }

    let validateArtifactQuotas quotas : Result<ArtifactQuotas, VerificationError> =
        let values =
            [ "stdout artifact", quotas.MaxStdoutBytes
              "stderr artifact", quotas.MaxStderrBytes
              "binlog artifact", quotas.MaxBinlogBytes
              "TRX artifact", quotas.MaxTrxBytes
              "run artifact", quotas.MaxRunBytes
              "aggregate artifacts", quotas.MaxAggregateBytes ]

        match values |> List.tryFind (fun (_, value) -> value <= 0L) with
        | Some(name, _) -> Error(InvalidInput $"{name} quota must be positive")
        | None when quotas.MaxRunBytes < quotas.MaxStdoutBytes + quotas.MaxStderrBytes ->
            Error(InvalidInput "run artifact quota is smaller than the output quotas")
        | None when quotas.MaxAggregateBytes < quotas.MaxRunBytes ->
            Error(InvalidInput "aggregate artifact quota is smaller than the run quota")
        | None -> Ok quotas

    let validate budgets : Result<VerifierBudgets, VerificationError> =
        let positive name value =
            if value > 0 then
                Ok()
            else
                Error(InvalidInput $"{name} must be positive")

        let checks =
            [ positive "initial errors" budgets.InitialErrors
              positive "initial warnings" budgets.InitialWarnings
              positive "initial failed tests" budgets.InitialFailedTests
              positive "details page size" budgets.DetailsPageSize
              positive "details max page size" budgets.DetailsMaxPageSize
              positive "message max length" budgets.MessageMaxLength
              positive "total tool-result size" budgets.TotalToolResultSize ]

        if List.exists Result.isError checks then
            checks
            |> List.choose (function
                | Error error -> Some error
                | Ok() -> None)
            |> List.head
            |> Error
        elif budgets.DetailsPageSize > budgets.DetailsMaxPageSize then
            Error(InvalidInput "details page size cannot exceed details max page size")
        elif budgets.TotalToolResultSize < budgets.MessageMaxLength then
            Error(InvalidInput "total tool-result size cannot be smaller than message max length")
        elif
            budgets.MinimumTimeout <= TimeSpan.Zero
            || budgets.MaximumTimeout < budgets.MinimumTimeout
            || budgets.DefaultTimeout < budgets.MinimumTimeout
            || budgets.DefaultTimeout > budgets.MaximumTimeout
        then
            Error(InvalidInput "timeout budget range is invalid")
        else
            Ok budgets

    let validateTimeout budgets timeout =
        let value = timeout |> Option.defaultValue budgets.DefaultTimeout

        if value < budgets.MinimumTimeout || value > budgets.MaximumTimeout then
            Error(InvalidInput $"timeout must be between {budgets.MinimumTimeout} and {budgets.MaximumTimeout}")
        else
            Ok value

    let private shorten maxLength (value: string) =
        if isNull value then ""
        elif value.Length <= maxLength then value
        else value.Substring(0, maxLength)

    let private fit maxItems maxLength values =
        values |> List.truncate maxItems |> List.map (shorten maxLength)

    let diagnosticText diagnostic =
        let location =
            match diagnostic.File, diagnostic.Line, diagnostic.Column with
            | Some file, Some line, Some column -> $"{file}({line},{column})"
            | Some file, _, _ -> file
            | _ -> ""

        let code =
            diagnostic.Code
            |> Option.map (fun value -> $" {value}")
            |> Option.defaultValue ""

        let prefix = if location = "" then "" else $"{location}:"
        $"{prefix}{code} {diagnostic.Message}".Trim()

    let testText testCase =
        match testCase.Message with
        | Some message -> $"{testCase.Name}: {message}"
        | None -> testCase.Name

    let compactBuild budgets runId execution diagnostics =
        let errors =
            diagnostics
            |> List.filter (fun item -> item.Severity = DiagnosticSeverity.ErrorDiagnostic)
            |> List.map diagnosticText

        let warnings =
            diagnostics
            |> List.filter (fun item -> item.Severity = DiagnosticSeverity.WarningDiagnostic)
            |> List.map diagnosticText

        let maxTextItems =
            max 1 (budgets.TotalToolResultSize / max 1 budgets.MessageMaxLength)

        let errors =
            fit (min budgets.InitialErrors maxTextItems) budgets.MessageMaxLength errors

        let warnings =
            fit (min budgets.InitialWarnings maxTextItems) budgets.MessageMaxLength warnings

        { RunId = RunId.value runId
          Status = VerificationStatus.ofProcessStatus execution.Status
          ExitCode =
            match execution.Status with
            | ProcessStatus.Completed code -> Some code
            | _ -> None
          DurationMs = int64 execution.Duration.TotalMilliseconds
          ErrorCount =
            diagnostics
            |> List.filter (fun item -> item.Severity = DiagnosticSeverity.ErrorDiagnostic)
            |> List.length
          WarningCount =
            diagnostics
            |> List.filter (fun item -> item.Severity = DiagnosticSeverity.WarningDiagnostic)
            |> List.length
          Errors = errors
          Warnings = warnings
          DetailsAvailable = true }

    let compactTest budgets runId execution evidence =
        let failures =
            evidence.Cases
            |> List.filter (fun item -> item.Outcome = TestOutcome.Failed)
            |> List.map testText

        let maxTextItems =
            max 1 (budgets.TotalToolResultSize / max 1 budgets.MessageMaxLength)

        { RunId = RunId.value runId
          Status = VerificationStatus.ofProcessStatus execution.Status
          ExitCode =
            match execution.Status with
            | ProcessStatus.Completed code -> Some code
            | _ -> None
          DurationMs = int64 execution.Duration.TotalMilliseconds
          Counts = evidence.Counts
          FailedTests = fit (min budgets.InitialFailedTests maxTextItems) budgets.MessageMaxLength failures
          DetailsAvailable = true
          TrxAvailable = evidence.TrxAvailable
          TrxUnavailableReason = evidence.TrxUnavailableReason }

    let page budgets (request: DetailRequest) (values: string list) =
        let limit = request.Limit |> Option.defaultValue budgets.DetailsPageSize

        if request.Offset < 0 then
            Error(InvalidPagination "offset must be non-negative")
        elif limit < 1 || limit > budgets.DetailsMaxPageSize then
            Error(InvalidPagination $"limit must be between 1 and {budgets.DetailsMaxPageSize}")
        else
            let total = List.length values

            let items =
                values
                |> List.skip (min request.Offset total)
                |> List.truncate limit
                |> List.map (shorten budgets.MessageMaxLength)

            Ok
                { RunId = request.RunId
                  Kind = request.Kind
                  Offset = request.Offset
                  Limit = limit
                  Total = total
                  Items = items
                  HasMore = request.Offset + items.Length < total }

    let pageSequence budgets (request: DetailRequest) (values: seq<string>) =
        let limit = request.Limit |> Option.defaultValue budgets.DetailsPageSize

        if request.Offset < 0 then
            Error(InvalidPagination "offset must be non-negative")
        elif limit < 1 || limit > budgets.DetailsMaxPageSize then
            Error(InvalidPagination $"limit must be between 1 and {budgets.DetailsMaxPageSize}")
        else
            try
                let total = values |> Seq.length

                let items =
                    values
                    |> Seq.skip (min request.Offset total)
                    |> Seq.truncate limit
                    |> Seq.map (shorten budgets.MessageMaxLength)
                    |> Seq.toList

                Ok
                    { RunId = request.RunId
                      Kind = request.Kind
                      Offset = request.Offset
                      Limit = limit
                      Total = total
                      Items = items
                      HasMore = request.Offset + items.Length < total }
            with error ->
                Error(MissingArtifact $"detail artifact could not be paged: {error.Message}")
