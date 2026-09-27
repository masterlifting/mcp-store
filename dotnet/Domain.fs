namespace Mcp.Dotnet

open System

[<RequireQualifiedAccess>]
type VerificationOperation =
    | Build
    | Test

[<RequireQualifiedAccess>]
type VerificationStatus =
    | Succeeded
    | Failed
    | TimedOut
    | Cancelled
    | InfrastructureFailure

[<RequireQualifiedAccess>]
type ProcessStatus =
    | Completed of exitCode: int
    | TimedOut
    | Cancelled

type VerificationError =
    | InvalidInput of string
    | UnauthorizedPath of string
    | UnknownRunId of string
    | InvalidPagination of string
    | UnsupportedDetailKind of string
    | UnavailableTestDetail of string
    | MissingArtifact of string
    | ProcessStartFailure of string
    | ArtifactQuotaExceeded of string
    | ArtifactFailure of string

type RunId = private RunId of string

[<RequireQualifiedAccess>]
module RunId =
    let value (RunId value) = value
    let internal create value = RunId value

type DiagnosticSeverity =
    | ErrorDiagnostic
    | WarningDiagnostic

type Diagnostic =
    { Severity: DiagnosticSeverity
      Code: string option
      File: string option
      Line: int option
      Column: int option
      Message: string }

type TestOutcome =
    | Passed
    | Failed
    | Skipped
    | Inconclusive

type TestCase =
    { Name: string
      Outcome: TestOutcome
      Message: string option }

type TestCounts =
    { Total: int
      Passed: int
      Failed: int
      Skipped: int }

type BuildOptions =
    { Target: string option
      Configuration: string option
      NoRestore: bool option
      Timeout: TimeSpan option }

type TestOptions =
    { Target: string option
      Configuration: string option
      Filter: string option
      NoBuild: bool option
      Timeout: TimeSpan option }

type DotnetBudgets =
    { InitialErrors: int
      InitialWarnings: int
      InitialFailedTests: int
      DetailsPageSize: int
      DetailsMaxPageSize: int
      MessageMaxLength: int
      TotalToolResultSize: int
      DefaultTimeout: TimeSpan
      MinimumTimeout: TimeSpan
      MaximumTimeout: TimeSpan }

type ArtifactQuotas =
    { MaxStdoutBytes: int64
      MaxStderrBytes: int64
      MaxBinlogBytes: int64
      MaxTrxBytes: int64
      MaxRunBytes: int64
      MaxAggregateBytes: int64 }

type CompactBuildResult =
    { RunId: string
      Status: VerificationStatus
      ExitCode: int option
      DurationMs: int64
      ErrorCount: int
      WarningCount: int
      Errors: string list
      Warnings: string list
      DetailsAvailable: bool }

type CompactTestResult =
    { RunId: string
      Status: VerificationStatus
      ExitCode: int option
      DurationMs: int64
      Counts: TestCounts option
      FailedTests: string list
      DetailsAvailable: bool
      TrxAvailable: bool
      TrxUnavailableReason: string option }

[<RequireQualifiedAccess>]
type DetailKind =
    | Errors
    | Warnings
    | FailedTests
    | Output

[<RequireQualifiedAccess>]
module DetailKind =
    let all =
        [ DetailKind.Errors
          DetailKind.Warnings
          DetailKind.FailedTests
          DetailKind.Output ]

    let name kind =
        match kind with
        | DetailKind.Errors -> "errors"
        | DetailKind.Warnings -> "warnings"
        | DetailKind.FailedTests -> "failed-tests"
        | DetailKind.Output -> "output"

    let tryParse value = all |> List.tryFind (fun kind -> name kind = value)

type DetailRequest =
    { RunId: string
      Kind: DetailKind
      Offset: int
      Limit: int option }

type DetailPage =
    { RunId: string
      Kind: DetailKind
      Offset: int
      Limit: int
      Total: int
      Items: string list
      HasMore: bool }

type CapturedProcess =
    { Status: ProcessStatus
      Duration: TimeSpan
      StdoutPath: string
      StderrPath: string
      StdoutBytes: int64
      StderrBytes: int64 }

type TestEvidence =
    { Counts: TestCounts option
      Cases: TestCase list
      TrxAvailable: bool
      TrxUnavailableReason: string option }

type RetainedEvidence =
    { Operation: VerificationOperation
      Process: CapturedProcess
      Diagnostics: Diagnostic list
      Tests: TestEvidence option
      MetadataPath: string
      ParsedEvidencePath: string }

module VerificationError =
    let message error =
        match error with
        | InvalidInput text -> text
        | UnauthorizedPath text -> text
        | UnknownRunId text -> text
        | InvalidPagination text -> text
        | UnsupportedDetailKind text -> text
        | UnavailableTestDetail text -> text
        | MissingArtifact text -> text
        | ProcessStartFailure text -> text
        | ArtifactQuotaExceeded text -> text
        | ArtifactFailure text -> text

module VerificationStatus =
    let ofProcessStatus status =
        match status with
        | ProcessStatus.Completed 0 -> VerificationStatus.Succeeded
        | ProcessStatus.Completed _ -> VerificationStatus.Failed
        | ProcessStatus.TimedOut -> VerificationStatus.TimedOut
        | ProcessStatus.Cancelled -> VerificationStatus.Cancelled

module TestCounts =
    let empty =
        { Total = 0
          Passed = 0
          Failed = 0
          Skipped = 0 }

// Shared by the runtime validator and the published tools/list schema so the two
// representations cannot drift.
[<RequireQualifiedAccess>]
module ConfigurationName =
    [<Literal>]
    let MaxLength = 64

    [<Literal>]
    let Default = "Release"

    [<Literal>]
    let Pattern = "^[A-Za-z0-9_.-]+$"

    let private isAllowedCharacter (character: char) =
        (character >= 'A' && character <= 'Z')
        || (character >= 'a' && character <= 'z')
        || (character >= '0' && character <= '9')
        || character = '_'
        || character = '-'
        || character = '.'

    let isValid (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= MaxLength
        && value |> Seq.forall isAllowedCharacter

    let validate (candidate: string option) : Result<string, VerificationError> =
        match candidate with
        | None -> Ok Default
        | Some value when String.IsNullOrWhiteSpace value -> Error(InvalidInput "configuration must be non-empty")
        | Some value when value.Length > MaxLength -> Error(InvalidInput "configuration is too long")
        | Some value when not (value |> Seq.forall isAllowedCharacter) ->
            Error(InvalidInput "configuration contains unsupported characters")
        | Some value -> Ok value

[<RequireQualifiedAccess>]
module TestFilter =
    [<Literal>]
    let MaxLength = 512

    let validate (candidate: string option) : Result<string option, VerificationError> =
        match candidate with
        | None -> Ok None
        | Some value when String.IsNullOrWhiteSpace value ->
            Error(InvalidInput "filter must be non-empty when supplied")
        | Some value when value.Length > MaxLength || value.IndexOf('\u0000') >= 0 ->
            Error(InvalidInput "filter is invalid or too long")
        | Some value -> Ok(Some value)

type internal ResultBuilder() =
    member _.Bind(value, continuation) = Result.bind continuation value
    member _.Return(value) = Ok value
    member _.ReturnFrom(value: Result<'a, 'b>) = value
    member _.Zero() = Ok()
    member _.Combine(first, second) = Result.bind (fun () -> second) first
    member _.Delay(generator: unit -> Result<'a, 'b>) = generator ()

[<AutoOpen>]
module internal ResultWorkflow =
    let result = ResultBuilder()
