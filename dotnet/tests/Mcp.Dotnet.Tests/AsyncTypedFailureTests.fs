module Mcp.Dotnet.Tests.AsyncTypedFailureTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

// These tests pin the typed async error contract: every failure surface
// reachable from the producer's async effects returns a typed
// `Result<_, VerificationError>` whose case names the canonical reason.
// Exceptions never escape the async boundary as normal control flow.

let private serviceFor (workspace: TempWorkspace) =
    new DotnetService(
        workspace.Root,
        dotnetHost = dotnetHost (),
        artifactRoot = workspace.ExternalArtifactRoot,
        retention = TimeSpan.FromHours 1.0
    )

let private artifactReadsTests =
    testList "ArtifactFiles async read/write" [
        testCaseTask "readAsync returns Ok for a present file" (fun () ->
            task {
                use workspace = new TempWorkspace()
                let path = workspace.Write("evidence.txt", "hello")

                let! result = ArtifactFiles.readAsync path
                Expect.isOk result "present file returns Ok"
                Expect.equal (result |> expectOk "read") "hello" "content round-trips"
            })

        testCaseTask "readAsync returns MissingArtifact for an absent file" (fun () ->
            task {
                use workspace = new TempWorkspace()
                let absent = Path.Combine(workspace.Root, "missing-evidence.txt")

                let! result = ArtifactFiles.readAsync absent
                result
                |> expectErrorMatching "missing artifact" isMissingArtifact
                |> ignore
            })

        testCaseTask "readAsync never throws on read failure" (fun () ->
            task {
                use workspace = new TempWorkspace()
                let directory = Path.Combine(workspace.Root, "evidence-dir")
                Directory.CreateDirectory directory |> ignore

                let! result = ArtifactFiles.readAsync directory
                Expect.isError result "directory as file returns Error"
                result
                |> expectErrorMatching "read failure classified" isMissingArtifact
                |> ignore
            })

        testCaseTask "writeEvidence persists both metadata and parsed evidence" (fun () ->
            task {
                use workspace = new TempWorkspace()
                use registry =
                    new ArtifactRegistry(
                        Path.Combine(workspace.Root, "artifacts"),
                        workspace.Namespace,
                        TimeSpan.FromHours 1.0
                    )

                let handle = startRun registry

                let evidence =
                    { Operation = VerificationOperation.Build
                      Process = capturedProcess (ProcessStatus.Completed 0) (TimeSpan.FromSeconds 1.0) handle.Paths
                      Diagnostics = [ errorDiagnostic "boom" ]
                      Tests = None
                      MetadataPath = handle.Paths.Metadata
                      ParsedEvidencePath = handle.Paths.ParsedEvidence }

                let! result = ArtifactFiles.writeEvidence handle evidence
                Expect.isOk result "write succeeds"

                Expect.isTrue (File.Exists handle.Paths.Metadata) "metadata written"
                Expect.isTrue (File.Exists handle.Paths.ParsedEvidence) "parsed evidence written"
            })
    ]

let private detailsAsyncTests =
    testList "async Details boundary" [
        testCaseTask "Details returns typed UnknownRunId for blank runId" (fun () ->
            task {
                use workspace = new TempWorkspace()
                use service = serviceFor workspace

                let! result =
                    service.Details
                        { RunId = "   "
                          Kind = DetailKind.Errors
                          Offset = 0
                          Limit = None }

                result
                |> expectErrorMatching "blank runId" isUnknownRunId
                |> ignore
            })

        testCaseTask "Details returns typed UnknownRunId for an unknown runId" (fun () ->
            task {
                use workspace = new TempWorkspace()
                use service = serviceFor workspace

                let! result =
                    service.Details
                        { RunId = "missing-run"
                          Kind = DetailKind.Errors
                          Offset = 0
                          Limit = None }

                result
                |> expectErrorMatching "unknown runId" isUnknownRunId
                |> ignore
            })

        testCaseTask "Details returns typed UnavailableTestDetail for failed-tests without TRX" (fun () ->
            task {
                use workspace = new TempWorkspace()
                use service = serviceFor workspace

                let! result =
                    service.Details
                        { RunId = "n/a"
                          Kind = DetailKind.FailedTests
                          Offset = 0
                          Limit = None }

                result
                |> expectErrorMatching "failed-tests unavailable" isUnavailableTestDetail
                |> ignore
            })
    ]

let private cancellationAsyncTests =
    testList "async cancellation semantics" [
        testCaseTask "a pre-cancelled VerifyBuild reports a typed Cancelled status" (fun () ->
            task {
                use workspace = new TempWorkspace()
                workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                use service = serviceFor workspace
                use cancellation = new CancellationTokenSource()
                cancellation.Cancel()

                let! result =
                    service.VerifyBuild(
                        { Target = Some "lib.csproj"
                          Configuration = None
                          NoRestore = None
                          Timeout = None },
                        cancellationToken = cancellation.Token
                    )

                let compact = result |> expectOk "cancelled build"
                Expect.equal compact.Status VerificationStatus.Cancelled "typed cancellation"
                Expect.equal compact.ExitCode None "no exit code on cancellation"
            })

        testCaseTask "a pre-cancelled VerifyTest reports a typed Cancelled status" (fun () ->
            task {
                use workspace = new TempWorkspace()
                workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                use service = serviceFor workspace
                use cancellation = new CancellationTokenSource()
                cancellation.Cancel()

                let! result =
                    service.VerifyTest(
                        { Target = Some "lib.csproj"
                          Configuration = None
                          Filter = None
                          NoBuild = Some true
                          Timeout = None },
                        cancellationToken = cancellation.Token
                    )

                let compact = result |> expectOk "cancelled test"
                Expect.equal compact.Status VerificationStatus.Cancelled "typed cancellation"
            })
    ]

let private typedHostFailureTests =
    testList "host async typed-error surface" [
        testCaseTask "an invalid configuration is rejected as typed InvalidInput" (fun () ->
            task {
                use workspace = new TempWorkspace()
                use service = serviceFor workspace

                let! result =
                    service.VerifyBuild(
                        { Target = None
                          Configuration = Some "Release; rm -rf /"
                          NoRestore = None
                          Timeout = None }
                    )

                result
                |> expectErrorMatching "configuration pattern" isInvalidInput
                |> ignore
            })

        testCaseTask "an unauthorized target is rejected as typed UnauthorizedPath" (fun () ->
            task {
                use workspace = new TempWorkspace()
                use service = serviceFor workspace

                let! result =
                    service.VerifyBuild(
                        { Target = Some "../outside.csproj"
                          Configuration = None
                          NoRestore = None
                          Timeout = None }
                    )

                result
                |> expectErrorMatching "target traversal" isUnauthorizedPath
                |> ignore
            })

        testCaseTask "an oversized limit returns typed InvalidPagination" (fun () ->
            task {
                use workspace = new TempWorkspace()
                use service = serviceFor workspace

                let! result =
                    service.Details
                        { RunId = "missing-run"
                          Kind = DetailKind.Errors
                          Offset = 0
                          Limit = Some(Budgets.Defaults.DetailsMaxPageSize + 1) }

                result
                |> expectErrorMatching "limit above maximum" isInvalidPagination
                |> ignore
            })
    ]

let tests =
    testList "async typed-failure regressions"
        [ artifactReadsTests
          detailsAsyncTests
          cancellationAsyncTests
          typedHostFailureTests ]
