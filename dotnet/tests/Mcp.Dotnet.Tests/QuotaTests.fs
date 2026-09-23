module Mcp.Dotnet.Tests.QuotaTests

open System
open System.IO
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

let private smallQuotas =
    { MaxStdoutBytes = 16L
      MaxStderrBytes = 16L
      MaxBinlogBytes = 16L
      MaxTrxBytes = 16L
      MaxRunBytes = 64L
      MaxAggregateBytes = 4096L }

let private registryFor (workspace: TempWorkspace) quotas =
    new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), TimeSpan.FromHours 1.0, quotas = quotas)

let private evidenceFor (handle: RunHandle) =
    { Operation = VerificationOperation.Build
      Process = capturedProcess (ProcessStatus.Completed 0) (TimeSpan.FromSeconds 1.0) handle.Paths
      Diagnostics = []
      Tests = None
      MetadataPath = handle.Paths.Metadata
      ParsedEvidencePath = handle.Paths.ParsedEvidence }

let private validationTests =
    testList "artifact quota validation" [
        testCase "default artifact quotas validate"
        <| fun _ -> Expect.isOk (Budgets.validateArtifactQuotas Budgets.DefaultArtifactQuotas) "defaults must validate"

        testCase "non-positive artifact quotas are rejected"
        <| fun _ ->
            Budgets.validateArtifactQuotas { Budgets.DefaultArtifactQuotas with MaxStdoutBytes = 0L }
            |> expectErrorMatching "zero stdout" isInvalidInput
            |> ignore

        testCase "run quota below combined output quotas is rejected"
        <| fun _ ->
            Budgets.validateArtifactQuotas
                { MaxStdoutBytes = 2L
                  MaxStderrBytes = 2L
                  MaxBinlogBytes = 1L
                  MaxTrxBytes = 1L
                  MaxRunBytes = 3L
                  MaxAggregateBytes = 100L }
            |> expectErrorMatching "run < stdout + stderr" isInvalidInput
            |> ignore

        testCase "aggregate quota below the run quota is rejected"
        <| fun _ ->
            Budgets.validateArtifactQuotas
                { MaxStdoutBytes = 1L
                  MaxStderrBytes = 1L
                  MaxBinlogBytes = 1L
                  MaxTrxBytes = 1L
                  MaxRunBytes = 2L
                  MaxAggregateBytes = 1L }
            |> expectErrorMatching "aggregate < run" isInvalidInput
            |> ignore
    ]

let private enforcementTests =
    testList "artifact quota enforcement" [
        testCase "stdout quota is enforced on a run"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use registry = registryFor workspace smallQuotas
            let handle = startRun registry
            File.WriteAllBytes(handle.Paths.Stdout, Array.zeroCreate 32)

            registry.CheckQuota handle
            |> expectErrorMatching "stdout quota" isArtifactQuotaExceeded
            |> ignore

        testCase "per-run quota is enforced when auxiliary artifacts accumulate"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use registry = registryFor workspace smallQuotas
            let handle = startRun registry
            File.WriteAllBytes(handle.Paths.Stdout, Array.zeroCreate 8)
            File.WriteAllBytes(handle.Paths.Stderr, Array.zeroCreate 8)
            File.WriteAllBytes(handle.Paths.ParsedEvidence, Array.zeroCreate 80)

            registry.CheckQuota handle
            |> expectErrorMatching "run quota" isArtifactQuotaExceeded
            |> ignore

        testCase "aggregate quota blocks new runs before allocation"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let artifactRoot = Path.Combine(workspace.Root, "artifacts")
            Directory.CreateDirectory artifactRoot |> ignore
            File.WriteAllBytes(Path.Combine(artifactRoot, "seed.bin"), Array.zeroCreate 8192)
            use registry = new ArtifactRegistry(artifactRoot, TimeSpan.FromHours 1.0, quotas = smallQuotas)

            registry.Start VerificationOperation.Build
            |> expectErrorMatching "aggregate exhausted" isArtifactQuotaExceeded
            |> ignore

        testCase "completion rejects a run that exceeded its artifact quota"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use registry = registryFor workspace smallQuotas
            let handle = startRun registry
            File.WriteAllBytes(handle.Paths.Stdout, Array.zeroCreate 32)

            registry.Complete(handle, evidenceFor handle)
            |> expectErrorMatching "quota completion" isArtifactQuotaExceeded
            |> ignore
    ]

let tests = testList "artifact quotas" [ validationTests; enforcementTests ]
