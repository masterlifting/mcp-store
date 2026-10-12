module Release.Tests.Orchestration

open System
open System.IO
open System.Text.Json.Nodes
open Expecto
open BuildProvenance

let private producers =
    [ "dotnet", "1.2.3", "Mcp.Dotnet.dll", "dotnet-test-sdk", "dotnet-test-runtime", "win-x64"
      "workflow", "4.5.6", "Mcp.Workflow.dll", "workflow-test-sdk", "workflow-test-runtime", "linux-x64" ]

let private writeRoster root =
    let path = ReleaseEnrollment.rosterPath root
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let participants = JsonArray()
    for producerId, _, _, _, _, _ in producers do
        let entry = JsonObject()
        entry["componentId"] <- JsonValue.Create producerId
        entry["configPath"] <- JsonValue.Create(Path.Combine(producerId, "release", "Config.fsx").Replace('\\', '/'))
        participants.Add entry
    let document = JsonObject()
    document["participants"] <- participants
    File.WriteAllText(path, document.ToJsonString())

let private writeConfig root producerId version entryDll sdk runtime platform =
    let componentRoot = Path.Combine(root, producerId)
    let releaseDir = Path.Combine(componentRoot, "release")
    Directory.CreateDirectory releaseDir |> ignore
    Directory.CreateDirectory(Path.Combine(componentRoot, "tests")) |> ignore

    let archiveName = $"v{version}.zip"
    let assetUri = $"https://github.com/masterlifting/mcp-store/releases/download/{producerId}/{archiveName}"
    let configPath = Path.Combine(releaseDir, "Config.fsx")
    let config =
        String.concat
            Environment.NewLine
            [ "module ReleaseConfig"
              $"let componentId = \"{producerId}\""
              $"let version = \"{version}\""
              $"let entryDll = \"{entryDll}\""
              $"let sdkVersion = \"{sdk}\""
              $"let runtimeVersion = \"{runtime}\""
              $"let platform = \"{platform}\""
              $"let archiveName = \"{archiveName}\""
              $"let assetUri = \"{assetUri}\""
              $"let publishedFiles = [ \"{entryDll}\" ]" ]
    File.WriteAllText(configPath, config)
    Path.GetRelativePath(root, configPath).Replace('\\', '/')

let private writeStepScript scriptPath marker stage shouldFail =
    let source =
        $"open System.IO{Environment.NewLine}"
        + $"File.AppendAllText(@\"{marker}\", \"{stage};\"){Environment.NewLine}"
        + if shouldFail then "failwith \"controlled validation failure\" |> ignore" + Environment.NewLine else ""
    File.WriteAllText(scriptPath, source)

let private parseDiagnostic (text: string) =
    JsonNode.Parse(text).AsObject()

let private propertyNames (node: JsonObject) =
    node |> Seq.map (fun pair -> pair.Key) |> Set.ofSeq

let private requiredString (node: JsonObject) (key: string) : string =
    node[key].GetValue<string>()

let private runValidationFailure (producerId, version, entryDll, sdk, runtime, platform) =
    async {
        let root = Path.Combine(Path.GetTempPath(), "mcp-release-orchestration-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore

        try
            let configPath = writeConfig root producerId version entryDll sdk runtime platform
            writeRoster root
            let releaseDir = Path.Combine(root, producerId, "release")
            let testsDir = Path.Combine(root, producerId, "tests")
            let marker = Path.Combine(root, "sequence.txt")
            writeStepScript (Path.Combine(releaseDir, "Build.fsx")) marker "build" false
            writeStepScript (Path.Combine(releaseDir, "Pins.fsx")) marker "pins" false
            writeStepScript (Path.Combine(testsDir, "DistributionTests.fsx")) marker "tests" true

            let previousOutput = Console.Out
            use output = new StringWriter()

            let! exitCode =
                async {
                    try
                        Console.SetOut output
                        return! ReleaseOrchestration.orchestrate root configPath
                    finally
                        Console.SetOut previousOutput
                }

            let diagnostic = parseDiagnostic (output.ToString())
            return exitCode, File.ReadAllText marker, diagnostic
        finally
            if Directory.Exists root then Directory.Delete(root, true)
    }

let private producerValidationFailureStopsBeforeTransaction () =
    async {
        let diagnostics = ResizeArray<string * string * string * JsonObject>()

        for producer in producers do
            let! exitCode, steps, diagnostic = runValidationFailure producer
            let producerId, version, entryDll, sdk, runtime, platform = producer
            let archiveName = $"v{version}.zip"
            let assetUri = $"https://github.com/masterlifting/mcp-store/releases/download/{producerId}/{archiveName}"

            Expect.equal exitCode 1 $"{producerId} validation failure exit code"
            Expect.equal
                steps
                "build;pins;tests;"
                $"{producerId} child steps run in order; diagnostic: {diagnostic.ToJsonString()}"
            Expect.equal (requiredString diagnostic "status") "failed" $"{producerId} structured status"
            Expect.equal (requiredString diagnostic "phase") "tests" $"{producerId} failure phase"
            Expect.equal (diagnostic["childExitCode"].GetValue<int>()) 1 $"{producerId} child exit code"
            Expect.isTrue
                ((requiredString diagnostic "childStderr").Contains("controlled validation failure", StringComparison.Ordinal))
                $"{producerId} child stderr"
            Expect.equal (requiredString diagnostic "componentId") producerId $"{producerId} context id"
            Expect.equal (requiredString diagnostic "version") version $"{producerId} context version"
            Expect.equal (requiredString diagnostic "archiveName") archiveName $"{producerId} context archive"
            Expect.equal (requiredString diagnostic "assetUri") assetUri $"{producerId} context URI"
            Expect.equal
                (propertyNames diagnostic)
                (Set.ofList [ "status"; "reason"; "phase"; "childExitCode"; "childStderr"; "componentId"; "version"; "archiveName"; "assetUri" ])
                $"{producerId} failure keeps the shared diagnostic shape"

            diagnostics.Add(producerId, requiredString diagnostic "reason", requiredString diagnostic "phase", diagnostic)

        let dotnet = diagnostics |> Seq.find (fun (producerId, _, _, _) -> producerId = "dotnet")
        let workflow = diagnostics |> Seq.find (fun (producerId, _, _, _) -> producerId = "workflow")
        let _, dotnetReason, dotnetPhase, dotnetDiagnostic = dotnet
        let _, workflowReason, workflowPhase, workflowDiagnostic = workflow
        Expect.equal dotnetReason workflowReason "the shared failure path emits equivalent reasons"
        Expect.equal dotnetPhase workflowPhase "the shared failure path names the same phase"
        Expect.equal (propertyNames dotnetDiagnostic) (propertyNames workflowDiagnostic) "the producers share a diagnostic schema"
    }

let private missingCredentialsCannotReachNetworkObserver () =
    async {
        let locator: ReleasePublish.ReleaseLocator =
            { Owner = "owner"
              Repository = "repo"
              Tag = "dotnet" }
        let observer = ReleaseTransaction.buildRemoteObserver locator true None (Path.GetTempPath())

        match! observer.ObserveArchive("dotnet", "v1.2.3.zip") with
        | Ok _ -> Expect.isFalse true "archive observation without auth cannot succeed"
        | Error error ->
            Expect.isTrue
                ((ReleaseError.message error).Contains("GITHUB_TOKEN", StringComparison.Ordinal))
                "archive observation fails before transport"

        match! observer.ObserveMetadata("dotnet", "dotnet-1.2.3-metadata.json") with
        | Ok _ -> Expect.isFalse true "metadata observation without auth cannot succeed"
        | Error error ->
            Expect.isTrue
                ((ReleaseError.message error).Contains("GITHUB_TOKEN", StringComparison.Ordinal))
                "metadata observation fails before transport"
    }

let tests =
    testList
        "shared orchestration"
        [ testCaseAsync
              "both producer validation failures are structured and block transaction"
              (producerValidationFailureStopsBeforeTransaction ())
          testCaseAsync "missing credentials stop before any network observer" (missingCredentialsCannotReachNetworkObserver ()) ]
