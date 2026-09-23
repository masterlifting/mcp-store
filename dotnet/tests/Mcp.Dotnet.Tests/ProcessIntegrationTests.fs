module Mcp.Dotnet.Tests.ProcessIntegrationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

// Real process integration is sequenced to keep the short-timeout case deterministic
// and to avoid many concurrent dotnet builds competing for machine resources.
let private serviceFor (workspace: TempWorkspace) =
    new VerifierService(
        workspace.Root,
        dotnetHost = dotnetHost (),
        artifactRoot = Path.Combine(workspace.Root, ".mcp-store", "dotnet-verification"),
        retention = TimeSpan.FromHours 1.0
    )

let private buildOptions target =
    { Target = target
      Configuration = None
      NoRestore = None
      Timeout = None }

let private outputRequest runId =
    { RunId = runId
      Kind = DetailKind.Output
      Offset = 0
      Limit = None }

let private quotaPaths (workspace: TempWorkspace) =
    let directory = Path.Combine(workspace.Root, "quota-run")
    Directory.CreateDirectory directory |> ignore

    { Directory = directory
      Metadata = Path.Combine(directory, "metadata.json")
      Stdout = Path.Combine(directory, "stdout.log")
      Stderr = Path.Combine(directory, "stderr.log")
      Binlog = Path.Combine(directory, "build.binlog")
      Trx = Path.Combine(directory, "test-results.trx")
      ParsedEvidence = Path.Combine(directory, "parsed-evidence.json") }

let private quotaBuildOptions =
    { Target = Some "lib.csproj"
      Configuration = None
      NoRestore = None
      Timeout = None }

let tests =
    testSequenced
        (testList "controlled process execution" [
            testCaseTask "successful build returns compact success with retained output" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use service = serviceFor workspace
                    let! result = service.VerifyBuild(buildOptions (Some "lib.csproj"))
                    let compact = result |> expectOk "successful build"

                    Expect.equal compact.Status VerificationStatus.Succeeded "succeeded"
                    Expect.equal compact.ExitCode (Some 0) "exit code zero"
                    Expect.equal compact.ErrorCount 0 "no errors"
                    Expect.isTrue compact.DetailsAvailable "details available"

                    let details = service.Details(outputRequest compact.RunId) |> expectOk "output details"
                    Expect.isTrue (details.Total > 0) "output retained outside the compact result"
                    Expect.isTrue (details.Items |> List.exists (fun item -> item.StartsWith "stdout:")) "stdout retained"
                })

            testCaseTask "build ignores a workspace global.json SDK selection" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    workspace.Write("global.json", "{\"sdk\":{\"version\":\"99.99.99\",\"rollForward\":\"disable\"}}")
                    |> ignore
                    use service = serviceFor workspace

                    let! result = service.VerifyBuild(buildOptions (Some "lib.csproj"))
                    let compact = result |> expectOk "build with workspace global.json"
                    Expect.equal compact.Status VerificationStatus.Succeeded "workspace SDK selection was isolated"
                })

            testCaseTask "an external artifact root retains the normal evidence contract" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    let externalRoot = Path.Combine(Path.GetTempPath(), "mcp-verifier-external", Guid.NewGuid().ToString("N"))

                    try
                        use service =
                            new VerifierService(
                                workspace.Root,
                                dotnetHost = dotnetHost (),
                                artifactRoot = externalRoot,
                                retention = TimeSpan.FromHours 1.0
                            )

                        let! result = service.VerifyBuild(buildOptions (Some "lib.csproj"))
                        let compact = result |> expectOk "external artifact-root build"
                        Expect.equal compact.Status VerificationStatus.Succeeded "external-root build succeeded"
                        Expect.isTrue (Directory.Exists externalRoot) "external root exists"
                        Expect.isTrue
                            (Directory.EnumerateFiles(externalRoot, "metadata.json", SearchOption.AllDirectories)
                             |> Seq.isEmpty
                             |> not)
                            "external root retains metadata"
                    finally
                        if Directory.Exists externalRoot then
                            Directory.Delete(externalRoot, true)
                })

            testCaseTask "failing build reports bounded diagnostics and retrievable details" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", invalidClassSource) |> ignore
                    use service = serviceFor workspace
                    let! result = service.VerifyBuild(buildOptions (Some "lib.csproj"))
                    let compact = result |> expectOk "failing build"

                    Expect.equal compact.Status VerificationStatus.Failed "failed status"
                    Expect.isTrue (compact.ExitCode |> Option.exists (fun code -> code <> 0)) "non-zero exit"
                    Expect.isTrue (compact.ErrorCount >= 1) "error counted"
                    Expect.isTrue (compact.Errors.Length <= Budgets.Defaults.InitialErrors) "bounded error subset"
                    Expect.isTrue (compact.Errors |> List.forall (fun item -> item.Length <= Budgets.Defaults.MessageMaxLength)) "bounded error text"

                    let details =
                        service.Details
                            { RunId = compact.RunId
                              Kind = DetailKind.Errors
                              Offset = 0
                              Limit = None }
                        |> expectOk "error details"

                    Expect.isTrue (details.Total >= compact.ErrorCount) "full diagnostics retained"

                    service.Details
                        { RunId = compact.RunId
                          Kind = DetailKind.FailedTests
                          Offset = 0
                          Limit = None }
                    |> expectErrorMatching "failed-tests on build" isUnavailableTestDetail
                    |> ignore
                })

            testCaseTask "timed out build reports timeout rather than success" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use service = serviceFor workspace

                    let! result =
                        service.VerifyBuild(
                            { buildOptions (Some "lib.csproj") with
                                Timeout = Some(TimeSpan.FromMilliseconds 100.0) }
                        )

                    let compact = result |> expectOk "timed out build"
                    Expect.equal compact.Status VerificationStatus.TimedOut "timeout status"
                    Expect.equal compact.ExitCode None "no exit code on timeout"
                })

            testCaseTask "pre-cancelled execution reports cancellation" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use service = serviceFor workspace
                    use cancellation = new CancellationTokenSource()
                    cancellation.Cancel()

                    let! result =
                        service.VerifyBuild(buildOptions (Some "lib.csproj"), cancellationToken = cancellation.Token)

                    let compact = result |> expectOk "cancelled build"
                    Expect.equal compact.Status VerificationStatus.Cancelled "cancelled status"
                })

            testCaseTask "concurrent builds keep artifact directories isolated" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibraryIn("alpha", "alpha", validClassSource) |> ignore
                    workspace.CreateClassLibraryIn("beta", "beta", validClassSource) |> ignore
                    use service = serviceFor workspace

                    let alphaTask: Task<Result<CompactBuildResult, VerificationError>> =
                        service.VerifyBuild(buildOptions (Some "alpha/alpha.csproj"))

                    let betaTask: Task<Result<CompactBuildResult, VerificationError>> =
                        service.VerifyBuild(buildOptions (Some "beta/beta.csproj"))

                    let! results = Task.WhenAll [| alphaTask; betaTask |]

                    let alpha: CompactBuildResult = results.[0] |> expectOk "alpha build"
                    let beta: CompactBuildResult = results.[1] |> expectOk "beta build"

                    Expect.equal alpha.Status VerificationStatus.Succeeded "alpha succeeded"
                    Expect.equal beta.Status VerificationStatus.Succeeded "beta succeeded"
                    Expect.notEqual alpha.RunId beta.RunId "unique run ids"
                    Expect.equal service.ArtifactCount 2 "two isolated runs"
                })

            testCaseTask "output quotas truncate captured artifacts without failing the run" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    let paths = quotaPaths workspace

                    let invocation =
                        Invocation.build workspace.Root (dotnetHost ()) Budgets.Defaults quotaBuildOptions paths
                        |> expectOk "quota invocation"

                    let quotas =
                        { Budgets.DefaultArtifactQuotas with
                            MaxStdoutBytes = 64L
                            MaxStderrBytes = 64L
                            MaxRunBytes = 1024L }

                    let! result =
                        ProcessRunner.runWithQuotas invocation paths CancellationToken.None quotas (fun () -> Ok())

                    let captured = result |> expectOk "quota run"
                    Expect.equal captured.Status (ProcessStatus.Completed 0) "completed"
                    Expect.equal captured.StdoutBytes 64L "stdout capped"
                    Expect.isTrue (captured.StderrBytes <= 64L) "stderr capped"
                    Expect.equal (FileInfo(paths.Stdout).Length) 64L "stdout artifact capped on disk"
                })

            testCaseTask "a failing quota check aborts the run with a quota error" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    let paths = quotaPaths workspace

                    let invocation =
                        Invocation.build workspace.Root (dotnetHost ()) Budgets.Defaults quotaBuildOptions paths
                        |> expectOk "quota invocation"

                    let! result =
                        ProcessRunner.runWithQuotas
                            invocation
                            paths
                            CancellationToken.None
                            Budgets.DefaultArtifactQuotas
                            (fun () -> Error(VerificationError.ArtifactQuotaExceeded "simulated quota"))

                    result
                    |> expectErrorMatching "quota abort" isArtifactQuotaExceeded
                    |> ignore
                })

            testCaseTask "a real registry quota aborts an in-flight build" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    let artifactRoot = Path.Combine(workspace.Root, "artifacts")

                    let quotas =
                        { Budgets.DefaultArtifactQuotas with
                            MaxStdoutBytes = 64L
                            MaxStderrBytes = 64L
                            MaxRunBytes = 256L
                            MaxAggregateBytes = 4096L }

                    use registry = new ArtifactRegistry(artifactRoot, TimeSpan.FromHours 1.0, quotas = quotas)
                    let handle = startRun registry

                    let invocation =
                        Invocation.build workspace.Root (dotnetHost ()) Budgets.Defaults quotaBuildOptions handle.Paths
                        |> expectOk "quota invocation"

                    let! result =
                        ProcessRunner.runWithQuotas
                            invocation
                            handle.Paths
                            CancellationToken.None
                            quotas
                            (fun () -> registry.CheckQuota handle)

                    result
                    |> expectErrorMatching "real registry quota abort" isArtifactQuotaExceeded
                    |> ignore
                })

            testCaseTask "a real build retains a binlog artifact outside the compact result" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use service = serviceFor workspace
                    let! result = service.VerifyBuild(buildOptions (Some "lib.csproj"))
                    result |> expectOk "successful build" |> ignore

                    let artifactRoot = Path.Combine(workspace.Root, ".mcp-store", "dotnet-verification")

                    let binlogs =
                        Directory.EnumerateFiles(artifactRoot, "*.binlog", SearchOption.AllDirectories)
                        |> Seq.toList

                    Expect.isTrue (binlogs.Length >= 1) "a binlog artifact was retained"
                    Expect.isTrue (binlogs |> List.forall (fun path -> FileInfo(path).Length > 0L)) "binlog is non-empty"
                })

            testCaseTask "a test run without TRX returns an actionable unavailable-detail error" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use service = serviceFor workspace

                    let! result =
                        service.VerifyTest(
                            { Target = Some "lib.csproj"
                              Configuration = None
                              Filter = None
                              NoBuild = Some true
                              Timeout = None }
                        )

                    let compact = result |> expectOk "test run without TRX"
                    Expect.isFalse compact.TrxAvailable "no TRX was produced"
                    Expect.isSome compact.TrxUnavailableReason "unavailable reason is surfaced"

                    service.Details
                        { RunId = compact.RunId
                          Kind = DetailKind.FailedTests
                          Offset = 0
                          Limit = None }
                    |> expectErrorMatching "failed-test detail unavailable" isUnavailableTestDetail
                    |> ignore
                })
        ])
