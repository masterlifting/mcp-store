module Release.Tests.Runner

open Expecto
open Release.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open BuildProvenance
open ReleaseTestProcess

let private repositoryRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private runnerPath =
    Path.Combine(repositoryRoot, "infrastructure", "release", "bin", "Release", "net11.0", "ReleaseRunner.exe")

let private readCanonicalPolicy componentId =
    let path = Path.Combine(repositoryRoot, componentId, "release", "Config.fsx")
    ReleasePolicy.parseBytes path (File.ReadAllBytes path)
    |> function
        | Ok policy -> policy
        | Error error -> failwithf "%s canonical policy failed to parse: %s" componentId (ReleaseError.message error)

let private runnerBinaryExists =
    testCase "the compiled runner exists at the canonical output path" (fun () ->
        Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}")

let private metadataAssetNameUsesCanonicalPolicy =
    testCase "metadata asset names follow each canonical producer version" (fun () ->
        for componentId in [ "dotnet"; "workflow" ] do
            let policy = readCanonicalPolicy componentId
            Expect.equal policy.ComponentId componentId $"{componentId} config identity"
            Expect.equal
                (ReleaseMetadata.metadataAssetName policy.ComponentId policy.Version)
                $"{policy.ComponentId}-{policy.Version}-metadata.json"
                $"{componentId} versioned metadata name")

let private cleanTreeGuardFailsClosed =
    testCaseAsync "the committed-source guard never reports dirty source as clean" <| async {
        match! BuildProvenance.assertCleanTree repositoryRoot with
        | Ok () -> ()
        | Error error ->
            let message = ReleaseError.message error
            Expect.isTrue
                (message.Contains("dirty", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("refusing", StringComparison.OrdinalIgnoreCase))
                "dirty-tree refusal is actionable"
    }

let private committedHeadIsResolved =
    testCaseAsync "the repository HEAD resolves to a commit identifier" <| async {
        match! BuildProvenance.committedHead repositoryRoot with
        | Ok head ->
            Expect.equal head.Length 40 "commit SHA length"
            Expect.isTrue (head |> Seq.forall Uri.IsHexDigit) "commit SHA is hexadecimal"
        | Error error -> failtestf "committedHead failed: %s" (ReleaseError.message error)
    }

let private runnerEmptyArgumentsReturnUsage =
    testCaseAsync "empty runner arguments return the usage exit code" <| async {
        Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}"
        let info = ProcessStartInfo(runnerPath)
        info.WorkingDirectory <- repositoryRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true

        match! capture 10000 info with
        | Error error -> failtestf "empty-argv runner invocation failed: %s" error
        | Ok (exitCode, _, stderr) ->
            Expect.equal exitCode 2 "usage exit code"
            Expect.isTrue (stderr.Contains("usage: ReleaseRunner prepare", StringComparison.Ordinal)) "prepare usage is shown"
            Expect.isTrue (stderr.Contains("usage: ReleaseRunner publish", StringComparison.Ordinal)) "publish usage is shown"
    }

let private missingIndependentClosure =
    testCaseAsync "missingIndependentClosure" <| async {
        Expect.isTrue (File.Exists runnerPath) $"runner binary exists at {runnerPath}"
        let fixtureRoot = Fixture.newTempDir "mcp-runner-publisher-closure"
        // The publisher closure must be the FIRST verification failure; the candidate transport
        // is a zero-byte raw-file stub that satisfies boundedTransportPath (directory-or-file +
        // exists check) without containing a valid outer ZIP. Transport-byte verification never
        // runs because loadClosure fails before verifyOuterTransport; the missing closure
        // remains the typed and actual precondition that blocks publication.
        let closurePath = Path.Combine(fixtureRoot, "missing-publisher.json")
        let candidateTransportPath = Path.Combine(fixtureRoot, "candidate-transport.zip")
        File.WriteAllBytes(candidateTransportPath, Array.empty)
        let trustedReceiptPath = Path.Combine(fixtureRoot, "trusted-publisher-receipt.json")
        let transportEvidencePath = Path.Combine(fixtureRoot, "fetch-candidate-bootstrap-receipt.json")
        let transportSha256 = String.replicate 64 "a"
        let eventSha = Fixture.canonicalEvaluatedRevision

        let bootstrapReceipt = JsonObject()
        bootstrapReceipt["schemaVersion"] <- JsonValue.Create 2
        bootstrapReceipt["mode"] <- JsonValue.Create "FetchCandidate"
        bootstrapReceipt["status"] <- JsonValue.Create "fetched"
        bootstrapReceipt["repository"] <- JsonValue.Create "masterlifting/mcp-store"
        bootstrapReceipt["repositoryId"] <- JsonValue.Create "123456789"
        bootstrapReceipt["runId"] <- JsonValue.Create "controlled-run"
        bootstrapReceipt["runAttempt"] <- JsonValue.Create "1"
        bootstrapReceipt["eventSha"] <- JsonValue.Create eventSha
        bootstrapReceipt["observedEvent"] <- JsonValue.Create "workflow_dispatch"
        bootstrapReceipt["role"] <- JsonValue.Create "candidate"
        bootstrapReceipt["component"] <- JsonValue.Create "dotnet"

        let artifact = JsonObject()
        artifact["id"] <- JsonValue.Create "controlled-artifact"
        artifact["name"] <- JsonValue.Create "controlled-candidate"
        artifact["role"] <- JsonValue.Create "candidate"
        artifact["component"] <- JsonValue.Create "dotnet"
        artifact["sizeInBytes"] <- JsonValue.Create 0
        artifact["expired"] <- JsonValue.Create false
        artifact["apiDigestRaw"] <- JsonValue.Create ($"sha256:{transportSha256}")
        artifact["apiDigest"] <- JsonValue.Create transportSha256
        artifact["platformDigest"] <- JsonValue.Create transportSha256
        artifact["digestSource"] <- JsonValue.Create "controlled-fixture"
        bootstrapReceipt["artifact"] <- artifact

        let download = JsonObject()
        download["redirectHost"] <- JsonValue.Create "objects.githubusercontent.com"
        download["redirectCode"] <- JsonValue.Create 302
        download["bytesRead"] <- JsonValue.Create 0
        download["platformBytes"] <- JsonValue.Create 0
        download["observedSha256"] <- JsonValue.Create transportSha256
        bootstrapReceipt["download"] <- download

        let uploaderJob = JsonObject()
        uploaderJob["id"] <- JsonValue.Create "1234567890"
        uploaderJob["name"] <- JsonValue.Create "prepare dotnet"
        uploaderJob["runId"] <- JsonValue.Create "controlled-run"
        uploaderJob["runAttempt"] <- JsonValue.Create "1"
        uploaderJob["headSha"] <- JsonValue.Create eventSha
        uploaderJob["status"] <- JsonValue.Create "completed"
        uploaderJob["conclusion"] <- JsonValue.Create "success"
        uploaderJob["listingUri"] <- JsonValue.Create "https://api.github.com/repos/masterlifting/mcp-store/actions/runs/controlled-run/attempts/1/jobs"
        uploaderJob["artifactLinkLimit"] <- JsonValue.Create "platform does not expose artifact-to-job links"
        bootstrapReceipt["uploaderJob"] <- uploaderJob

        let runAttemptObservation = JsonObject()
        runAttemptObservation["uri"] <- JsonValue.Create "https://api.github.com/repos/masterlifting/mcp-store/actions/runs/controlled-run/attempts/1"
        runAttemptObservation["id"] <- JsonValue.Create "controlled-run"
        runAttemptObservation["runAttempt"] <- JsonValue.Create "1"
        runAttemptObservation["headSha"] <- JsonValue.Create eventSha
        runAttemptObservation["event"] <- JsonValue.Create "workflow_dispatch"
        runAttemptObservation["repositoryId"] <- JsonValue.Create "123456789"
        runAttemptObservation["repositoryFullName"] <- JsonValue.Create "masterlifting/mcp-store"
        bootstrapReceipt["runAttemptObservation"] <- runAttemptObservation

        try
            // Valid transport-evidence keeps this closure regression independent of later FetchCandidate validation.
            let workspaceRoot = Path.GetFullPath repositoryRoot
            let workspacePrefix =
                workspaceRoot.TrimEnd([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
                + string Path.DirectorySeparatorChar
            let fullFixtureRoot = Path.GetFullPath fixtureRoot
            Expect.isFalse
                (fullFixtureRoot.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                 || fullFixtureRoot.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
                "receipt fixture paths are outside the publisher workspace"
            File.WriteAllText(transportEvidencePath, bootstrapReceipt.ToJsonString())
            let args =
                [ "publish"
                  closurePath
                  candidateTransportPath
                  "--repo=masterlifting/mcp-store"
                  "--run-id=controlled-run"
                  "--attempt=1"
                  $"--event-sha={eventSha}"
                  "--role=candidate"
                  "--component=dotnet"
                  "--artifact-id=controlled-artifact"
                  "--artifact-name=controlled-candidate"
                  $"--transport-sha256={transportSha256}"
                  "--builder-sdk=controlled-sdk"
                  "--builder-runtime=controlled-runtime"
                  "--builder-platform=controlled-platform"
                  $"--workspace-root={repositoryRoot}"
                  $"--trusted-receipt={trustedReceiptPath}"
                  $"--transport-evidence={transportEvidencePath}" ]
            let info = ProcessStartInfo(runnerPath)
            info.WorkingDirectory <- repositoryRoot
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.CreateNoWindow <- true
            args |> List.iter info.ArgumentList.Add
            [ "GITHUB_TOKEN"; "GH_TOKEN"; "MCP_RELEASE_DO_UPLOAD"; "MCP_RELEASE_GITHUB_OWNER"
              "MCP_RELEASE_GITHUB_REPOSITORY"; "GITHUB_ENV"; "GITHUB_OUTPUT" ]
            |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

            match! capture 15000 info with
            | Error error -> failtestf "publish closure preflight failed to run: %s" error
            | Ok (exitCode, stdout, _) ->
                Expect.equal exitCode 4 "missing trusted closure blocks publication"
                let result = JsonNode.Parse(stdout.TrimEnd()).AsObject()
                Expect.equal (result["status"].GetValue<string>()) "failed" "structured failure status"
                Expect.equal
                    (result["reason"].GetValue<string>())
                    $"required release artifact does not exist: {closurePath}"
                    "typed missing publisher closure is the first verification failure"
                for name in [ "publishedArchiveSha256"; "publishedManifestSha256"; "publishedRevision"; "publishedMetadataSha256" ] do
                    Expect.isFalse (result.ContainsKey name) $"{name} is absent without a trusted closure"
        finally
            Fixture.safeDeleteDir fixtureRoot
    }

let tests =
    testList
        "runner publication boundary"
        [ runnerBinaryExists
          metadataAssetNameUsesCanonicalPolicy
          cleanTreeGuardFailsClosed
          committedHeadIsResolved
          runnerEmptyArgumentsReturnUsage
          missingIndependentClosure ]
