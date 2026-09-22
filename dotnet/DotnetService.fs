namespace Mcp.Dotnet

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

module private DetailSource =
    let diagnosticValues severity diagnostics =
        diagnostics
        |> List.filter (fun item -> item.Severity = severity)
        |> List.map Budgets.diagnosticText

    let failedTests tests =
        tests
        |> List.filter (fun item -> item.Outcome = TestOutcome.Failed)
        |> List.map Budgets.testText

    let output stdoutPath stderrPath =
        if not (File.Exists stdoutPath) || not (File.Exists stderrPath) then
            Error(MissingArtifact "captured output artifact is missing")
        else
            let readLines path =
                try
                    Ok(
                        seq {
                            use reader = new StreamReader(path: string)
                            let mutable line = reader.ReadLine()

                            while not (isNull line) do
                                yield line
                                line <- reader.ReadLine()
                        }
                    )
                with error ->
                    Error(MissingArtifact $"captured output artifact could not be read: {error.Message}")

            result {
                let! stdout = readLines stdoutPath
                let! stderr = readLines stderrPath

                return
                    Seq.append
                        (stdout |> Seq.map (fun line -> $"stdout: {line}"))
                        (stderr |> Seq.map (fun line -> $"stderr: {line}"))
            }

type DotnetService(
    workspaceRoot: string,
    ?dotnetHost: string,
    ?artifactRoot: string,
    ?budgets: DotnetBudgets,
    ?retention: TimeSpan
) =
    let rootResult = PathAuthorization.validateWorkspace workspaceRoot

    let root =
        match rootResult with
        | Ok value -> value
        | Error error -> invalidArg (nameof workspaceRoot) (VerificationError.message error)

    let effectiveBudgets = budgets |> Option.defaultValue Budgets.Defaults
    let injectedHost = dotnetHost |> Option.defaultValue ""

    do
        match Budgets.validate effectiveBudgets with
        | Ok _ -> ()
        | Error error -> invalidArg (nameof budgets) (VerificationError.message error)

    let requestedArtifactRoot =
        artifactRoot
        |> Option.defaultWith (fun () ->
            Path.Combine(Path.GetTempPath(), "mcp-dotnet-state"))

    let artifactBase =
        match PathAuthorization.validateArtifactRoot root requestedArtifactRoot with
        | Ok value -> value
        | Error error -> invalidArg (nameof artifactRoot) (VerificationError.message error)

    let workspaceIdentity =
        let canonical =
            if OperatingSystem.IsWindows() then root.ToUpperInvariant() else root
        SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let rootForArtifacts = Path.Combine(artifactBase, workspaceIdentity)

    let registry =
        new ArtifactRegistry(
            rootForArtifacts,
            retention |> Option.defaultValue (TimeSpan.FromHours 1.0),
            quotas = Budgets.DefaultArtifactQuotas
        )

    let emptyPaths =
        { Directory = ""
          Metadata = ""
          Stdout = ""
          Stderr = ""
          Binlog = ""
          Trx = ""
          ParsedEvidence = "" }

    let readOutput execution =
        task {
            match ArtifactFiles.read execution.StdoutPath, ArtifactFiles.read execution.StderrPath with
            | Ok stdout, Ok stderr -> return Ok(stdout, stderr)
            | Error error, _ -> return Error error
            | _, Error error -> return Error error
        }

    let complete handle execution operation diagnostics tests =
        task {
            let evidence =
                { Operation = operation
                  Process = execution
                  Diagnostics = diagnostics
                  Tests = tests
                  MetadataPath = handle.Paths.Metadata
                  ParsedEvidencePath = handle.Paths.ParsedEvidence }

            match ArtifactFiles.writeEvidence handle evidence with
            | Error error -> return Error error
            | Ok() -> return registry.Complete(handle, evidence)
        }

    let abort handle error =
        registry.Abort handle
        Error error

    member _.VerifyBuild
        (options: BuildOptions, ?cancellationToken: CancellationToken)
        : Task<Result<CompactBuildResult, VerificationError>> =
        task {
            let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None

            match Invocation.build root injectedHost effectiveBudgets options emptyPaths with
            | Error error -> return Error error
            | Ok _ ->
                match registry.Start VerificationOperation.Build with
                | Error error -> return Error error
                | Ok handle ->
                    match Invocation.build root injectedHost effectiveBudgets options handle.Paths with
                    | Error error -> return abort handle error
                    | Ok invocation ->
                        let! processResult =
                            ProcessRunner.runWithQuotas
                                invocation
                                handle.Paths
                                cancellationToken
                                Budgets.DefaultArtifactQuotas
                                (fun () -> registry.CheckQuota handle)

                        match processResult with
                        | Error error -> return abort handle error
                        | Ok processExecution ->
                            let! outputResult = readOutput processExecution

                            match outputResult with
                            | Error error -> return abort handle error
                            | Ok(stdout, stderr) ->
                                let diagnostics = Parsers.buildDiagnostics stdout stderr

                                let! completion =
                                    complete handle processExecution VerificationOperation.Build diagnostics None

                                match completion with
                                | Error error -> return abort handle error
                                | Ok() ->
                                    return
                                        Ok(
                                            Budgets.compactBuild
                                                effectiveBudgets
                                                handle.RunId
                                                processExecution
                                                diagnostics
                                        )
        }

    member _.VerifyTest
        (options: TestOptions, ?cancellationToken: CancellationToken)
        : Task<Result<CompactTestResult, VerificationError>> =
        task {
            let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None

            match Invocation.test root injectedHost effectiveBudgets options emptyPaths with
            | Error error -> return Error error
            | Ok _ ->
                match registry.Start VerificationOperation.Test with
                | Error error -> return Error error
                | Ok handle ->
                    match Invocation.test root injectedHost effectiveBudgets options handle.Paths with
                    | Error error -> return abort handle error
                    | Ok invocation ->
                        let! processResult =
                            ProcessRunner.runWithQuotas
                                invocation
                                handle.Paths
                                cancellationToken
                                Budgets.DefaultArtifactQuotas
                                (fun () -> registry.CheckQuota handle)

                        match processResult with
                        | Error error -> return abort handle error
                        | Ok processExecution ->
                            let! outputResult = readOutput processExecution

                            match outputResult with
                            | Error error -> return abort handle error
                            | Ok(stdout, stderr) ->
                                let evidence = Parsers.tests handle.Paths.Trx stdout stderr
                                let diagnostics = Parsers.buildDiagnostics stdout stderr

                                let! completion =
                                    complete
                                        handle
                                        processExecution
                                        VerificationOperation.Test
                                        diagnostics
                                        (Some evidence)

                                match completion with
                                | Error error -> return abort handle error
                                | Ok() ->
                                    return Ok(Budgets.compactTest effectiveBudgets handle.RunId processExecution evidence)
        }

    member _.Details(request: DetailRequest) : Result<DetailPage, VerificationError> =
        let runIdResult =
            if String.IsNullOrWhiteSpace request.RunId then
                Error(UnknownRunId "runId must be non-empty")
            else
                Ok request.RunId

        result {
            let! _ = runIdResult
            let! evidence = registry.Get request.RunId

            if not (File.Exists evidence.MetadataPath) || not (File.Exists evidence.ParsedEvidencePath) then
                return! Error(MissingArtifact "verification evidence artifact is missing")

            let! values =
                match request.Kind with
                | DetailKind.Errors ->
                    Ok(DetailSource.diagnosticValues DiagnosticSeverity.ErrorDiagnostic evidence.Diagnostics |> Seq.ofList)
                | DetailKind.Warnings ->
                    Ok(DetailSource.diagnosticValues DiagnosticSeverity.WarningDiagnostic evidence.Diagnostics |> Seq.ofList)
                | DetailKind.FailedTests ->
                    match evidence.Tests with
                    | Some tests when tests.TrxAvailable -> Ok(DetailSource.failedTests tests.Cases |> Seq.ofList)
                    | Some _ -> Error(UnavailableTestDetail "failed-test details require a retained TRX artifact")
                    | None -> Error(UnavailableTestDetail "failed-test details are unavailable for this run")
                | DetailKind.Output -> DetailSource.output evidence.Process.StdoutPath evidence.Process.StderrPath

            return! Budgets.pageSequence effectiveBudgets request values
        }

    member _.EndSession() = registry.EndSession()
    member _.ArtifactCount = registry.Count

    interface IDisposable with
        member this.Dispose() = this.EndSession()
