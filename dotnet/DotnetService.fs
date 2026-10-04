namespace Mcp.Dotnet

open System
open System.IO
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

    // The detail boundary owns reading both captured output artifacts and
    // converts failures here; callers page over the resulting owned list.
    let output stdoutPath stderrPath : Async<Result<string list, VerificationError>> =
        async {
            if not (File.Exists stdoutPath) || not (File.Exists stderrPath) then
                return Error(MissingArtifact "captured output artifact is missing")
            else
                try
                    let! stdoutLines = TaskAwait.operational (File.ReadAllLinesAsync stdoutPath)
                    let! stderrLines = TaskAwait.operational (File.ReadAllLinesAsync stderrPath)

                    return
                        Ok(
                            [ yield! stdoutLines |> Array.map (fun line -> $"stdout: {line}")
                              yield! stderrLines |> Array.map (fun line -> $"stderr: {line}") ]
                        )
                with
                | :? OperationCanceledException as error -> return raise error
                | :? IOException as error ->
                    return Error(MissingArtifact $"captured output artifact could not be read: {error.Message}")
                | :? UnauthorizedAccessException as error ->
                    return Error(MissingArtifact $"captured output artifact could not be read: {error.Message}")
        }

type DotnetService(
    workspaceRoot: string,
    artifactRoot: string,
    ?dotnetHost: string,
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

    let rootForArtifacts =
        match PathAuthorization.validateArtifactRoot root artifactRoot with
        | Ok value -> value
        | Error error -> invalidArg (nameof artifactRoot) (VerificationError.message error)

    let registry =
        new ArtifactRegistry(
            rootForArtifacts,
            PathAuthorization.workspaceNamespace root,
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
            let! stdoutResult = ArtifactFiles.readAsync execution.StdoutPath
            let! stderrResult = ArtifactFiles.readAsync execution.StderrPath

            match stdoutResult, stderrResult with
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

            match! ArtifactFiles.writeEvidence handle evidence with
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
                                let! evidence = Parsers.tests handle.Paths.Trx stdout stderr
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

    member _.Details(request: DetailRequest) : Task<Result<DetailPage, VerificationError>> =
        task {
            let runIdResult =
                if String.IsNullOrWhiteSpace request.RunId then
                    Error(UnknownRunId "runId must be non-empty")
                else
                    Ok request.RunId

            match runIdResult with
            | Error error -> return Error error
            | Ok _ ->
                match registry.Get request.RunId with
                | Error error -> return Error error
                | Ok evidence ->
                    if not (File.Exists evidence.MetadataPath) || not (File.Exists evidence.ParsedEvidencePath) then
                        return Error(MissingArtifact "verification evidence artifact is missing")
                    else
                        let! valuesResult =
                            match request.Kind with
                            | DetailKind.Errors ->
                                task { return Ok(DetailSource.diagnosticValues DiagnosticSeverity.ErrorDiagnostic evidence.Diagnostics) }
                            | DetailKind.Warnings ->
                                task { return Ok(DetailSource.diagnosticValues DiagnosticSeverity.WarningDiagnostic evidence.Diagnostics) }
                            | DetailKind.FailedTests ->
                                match evidence.Tests with
                                | Some tests when tests.TrxAvailable -> task { return Ok(DetailSource.failedTests tests.Cases) }
                                | Some _ ->
                                    task { return Error(UnavailableTestDetail "failed-test details require a retained TRX artifact") }
                                | None ->
                                    task { return Error(UnavailableTestDetail "failed-test details are unavailable for this run") }
                            | DetailKind.Output ->
                                DetailSource.output evidence.Process.StdoutPath evidence.Process.StderrPath
                                |> Async.StartImmediateAsTask

                        match valuesResult with
                        | Error error -> return Error error
                        | Ok values -> return Budgets.page effectiveBudgets request values
        }

    member _.EndSession() = registry.EndSession()
    member _.ArtifactCount = registry.Count

    interface IDisposable with
        member this.Dispose() = this.EndSession()
