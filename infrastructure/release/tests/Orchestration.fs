module Release.Tests.Orchestration

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ReleaseTestProcess

type private StageFailure =
    | BuildFailure
    | PinsFailure
    | DotnetDistributionFailure
    | WorkflowDistributionFailure
    | WorkflowPackagedFailure

let private componentIds = [ "dotnet"; "workflow" ]

let private sha256 (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private clearReadOnlyGitObjects (root: string) =
    let objectDirectory = Path.Combine(root, ".git", "objects")

    if Directory.Exists objectDirectory then
        for path in Directory.EnumerateFiles(objectDirectory, "*", SearchOption.AllDirectories) do
            let file = FileInfo path

            if file.IsReadOnly then
                file.IsReadOnly <- false

let private withTemporaryRoot (action: string -> Async<unit>) =
    async {
        let root = Path.Combine(Path.GetTempPath(), "mcp-release-prepare-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            do! action root
        finally
            if Directory.Exists root then
                // Windows Git marks loose objects read-only, which blocks recursive deletion of this fixture.
                clearReadOnlyGitObjects root
                Directory.Delete(root, true)
    }

let private write (root: string) (relativePath: string) (contents: string) =
    let path = Path.Combine(root, relativePath)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, contents)
    path

let private policySource componentId =
    let productName = if componentId = "dotnet" then "Dotnet" else "Workflow"
    let entryDll = $"Mcp.{productName}.dll"
    String.concat
        Environment.NewLine
        [ "module ReleaseConfig"
          $"let componentId = \"{componentId}\""
          "let version = \"7.8.9\""
          $"let entryDll = \"{entryDll}\""
          "let sdkVersion = \"fixture-sdk\""
          "let runtimeVersion = \"fixture-runtime\""
          "let platform = \"fixture-platform\""
          "let archiveName = $\"v{version}.zip\""
          "let assetUri = $\"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/{archiveName}\""
          $"let publishedFiles = [ \"FSharp.Core.dll\"; \"Mcp.{productName}.deps.json\"; \"{entryDll}\"; \"Mcp.{productName}.runtimeconfig.json\" ]" ]

let private parsePolicy configPath =
    ReleasePolicy.parseBytes configPath (File.ReadAllBytes configPath)
    |> function
        | Ok policy -> policy
        | Error error -> failwithf "fixture policy failed to parse: %s" (BuildProvenance.ReleaseError.message error)

let private writeProducerArchive (root: string) (policy: ReleasePolicy.PolicyProjection) =
    let payloads =
        policy.PublishedFiles
        |> List.map (fun name -> name, Encoding.UTF8.GetBytes($"fixture:{policy.ComponentId}:{name}"))
        |> fun files -> files @ [ "NOTICE.txt", Encoding.UTF8.GetBytes "release preparation fixture" ]

    let manifest = JsonObject()
    manifest["schemaVersion"] <- JsonValue.Create 1
    manifest["id"] <- JsonValue.Create policy.ComponentId
    manifest["version"] <- JsonValue.Create policy.Version
    manifest["revision"] <- JsonValue.Create("0123456789abcdef0123456789abcdef01234567")
    manifest["sdk"] <- JsonValue.Create policy.SdkVersion
    manifest["entryDll"] <- JsonValue.Create policy.EntryDll
    manifest["archive"] <- JsonValue.Create policy.ArchiveName

    let files = JsonArray()
    for name, bytes in payloads do
        if name <> "NOTICE.txt" then
            let entry = JsonObject()
            entry["path"] <- JsonValue.Create name
            entry["sha256"] <- JsonValue.Create(sha256 bytes)
            files.Add entry
    manifest["files"] <- files

    let archiveFiles = JsonArray()
    for name in policy.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] do
        archiveFiles.Add(JsonValue.Create name)
    manifest["archiveFiles"] <- archiveFiles
    let manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
    let archivePath = Path.Combine(root, policy.ComponentId, "dist", policy.ArchiveName)
    let manifestPath = Path.Combine(root, policy.ComponentId, "dist", policy.ComponentId, "distribution.json")
    Directory.CreateDirectory(Path.GetDirectoryName archivePath) |> ignore
    Directory.CreateDirectory(Path.GetDirectoryName manifestPath) |> ignore
    File.WriteAllBytes(manifestPath, manifestBytes)

    use stream = File.Create archivePath
    use archive = new ZipArchive(stream, ZipArchiveMode.Create)
    for name, bytes in payloads @ [ "distribution.json", manifestBytes ] do
        let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        use entryStream = entry.Open()
        entryStream.Write(bytes, 0, bytes.Length)

let private stageScript (stage: string) (fails: bool) =
    let marker = "File.AppendAllText(\"prepare-stages.log\", \"" + stage + ";\")"
    let failure = if fails then "failwith \"controlled stage failure\" |> ignore" else "()"
    "open System.IO" + Environment.NewLine + marker + Environment.NewLine + failure + Environment.NewLine

let private stageFailure (producer: string) (stage: StageFailure) : string * string =
    match stage with
    | BuildFailure -> producer, "build"
    | PinsFailure -> producer, "pins"
    | DotnetDistributionFailure -> "dotnet", "distribution"
    | WorkflowDistributionFailure -> "workflow", "distribution"
    | WorkflowPackagedFailure -> "workflow", "packaged"

let private scriptFails selectedProducer selectedStage producer stage =
    selectedProducer = producer && selectedStage = stage

let private runGit root arguments =
    async {
        let info = ProcessStartInfo("git")
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        arguments |> List.iter info.ArgumentList.Add
        match! capture 30000 info with
        | Ok (0, _, _) -> return ()
        | Ok (code, stdout, stderr) -> return failwithf "git %A failed (%d): %s %s" arguments code stdout stderr
        | Error error -> return failwithf "git %A could not run: %s" arguments error
    }

let private writeFixture (root: string) (failure: StageFailure option) =
    async {
        let roster = JsonObject()
        let participants = JsonArray()
        for componentId in componentIds do
            let item = JsonObject()
            item["componentId"] <- JsonValue.Create componentId
            item["configPath"] <- JsonValue.Create $"{componentId}/release/Config.fsx"
            participants.Add item
        roster["participants"] <- participants
        write root "infrastructure/release/Participants.json" (roster.ToJsonString()) |> ignore

        for componentId in componentIds do
            let selectedProducer, selectedStage =
                failure
                |> Option.map (stageFailure componentId)
                |> Option.defaultValue ("", "")

            let configPath = write root $"{componentId}/release/Config.fsx" (policySource componentId)
            let policy = parsePolicy configPath
            writeProducerArchive root policy
            write root $"{componentId}/release/Build.fsx" (stageScript "build" (scriptFails selectedProducer selectedStage componentId "build")) |> ignore
            write root $"{componentId}/release/Pins.fsx" (stageScript "pins" (scriptFails selectedProducer selectedStage componentId "pins")) |> ignore
            write root $"{componentId}/tests/DistributionTests.fsx" (stageScript "distribution" (scriptFails selectedProducer selectedStage componentId "distribution")) |> ignore

            if componentId = "workflow" then
                write root "workflow/tests/PackagedRuntimeCoordinationTests.fsx" (stageScript "packaged" (scriptFails selectedProducer selectedStage componentId "packaged"))
                |> ignore

        do! runGit root [ "init"; "--quiet" ]
        do! runGit root [ "add"; "-A" ]
        do!
            runGit
                root
                [ "-c"; "user.name=ReleaseTests"; "-c"; "user.email=release-tests@example.invalid"; "commit"; "--quiet"; "-m"; "fixture"; "--allow-empty" ]
    }

let private runPrepare root configPath outputPath =
    async {
        let previous = Console.Out
        use output = new StringWriter()
        try
            Console.SetOut output
            let! exitCode = ReleaseOrchestration.prepare root configPath outputPath
            return exitCode, output.ToString()
        finally
            Console.SetOut previous
    }

let private prepareWritesPortableCandidateAndUsesExplicitProducerGates =
    testCaseAsync "prepare writes portable candidates after each enrolled producer's explicit test gates" <| async {
        for componentId in componentIds do
            do!
                withTemporaryRoot (fun root ->
                    async {
                        do! writeFixture root None
                        let outputPath = Path.Combine(root, "candidate-output")
                        let! exitCode, output = runPrepare root $"{componentId}/release/Config.fsx" outputPath
                        Expect.equal exitCode 0 $"{componentId} preparation succeeds: {output}"
                        let summary = JsonNode.Parse(output.Trim()).AsObject()
                        Expect.equal (summary["status"].GetValue<string>()) "prepared" $"{componentId} reports preparation only"
                        Expect.isTrue (File.Exists(Path.Combine(outputPath, "preparation.json"))) $"{componentId} emits the preparation receipt"
                        Expect.isTrue (File.Exists(Path.Combine(outputPath, "distribution.json"))) $"{componentId} emits its standalone manifest"
                        Expect.isTrue (File.Exists(Path.Combine(outputPath, "v7.8.9.zip"))) $"{componentId} emits the producer archive"
                        let stages = File.ReadAllText(Path.Combine(root, "prepare-stages.log"))
                        let expected = if componentId = "workflow" then "build;pins;distribution;packaged;" else "build;pins;distribution;"
                        Expect.equal stages expected $"{componentId} runs only its ordered producer gates"
                        Expect.isFalse (File.Exists(Path.Combine(root, "transaction-started"))) "preparation never starts publication"
                    })
    }

let private preparationFailureStopsBeforeLaterStages =
    testCaseAsync "build, pins, and both Workflow package-test failures block candidate output" <| async {
        let scenarios =
            [ BuildFailure, "dotnet", "build", "build;"
              PinsFailure, "dotnet", "pins", "build;pins;"
              WorkflowDistributionFailure, "workflow", "tests", "build;pins;distribution;"
              WorkflowPackagedFailure, "workflow", "tests[2]", "build;pins;distribution;packaged;" ]

        for failure, componentId, expectedPhase, expectedStages in scenarios do
            do!
                withTemporaryRoot (fun root ->
                    async {
                        do! writeFixture root (Some failure)
                        let outputPath = Path.Combine(root, "candidate-output")
                        let! exitCode, output = runPrepare root $"{componentId}/release/Config.fsx" outputPath
                        Expect.equal exitCode 4 $"{componentId}/{expectedPhase} returns the structured preparation failure"
                        let diagnostic = JsonNode.Parse(output.Trim()).AsObject()
                        Expect.equal (diagnostic["status"].GetValue<string>()) "failed" $"{componentId}/{expectedPhase} status"
                        Expect.equal (diagnostic["phase"].GetValue<string>()) expectedPhase $"{componentId}/{expectedPhase} phase"
                        Expect.isTrue
                            (diagnostic["reason"].GetValue<string>().Contains("failed (1)", StringComparison.Ordinal))
                            $"{componentId}/{expectedPhase} preserves the child failure code"
                        Expect.equal (File.ReadAllText(Path.Combine(root, "prepare-stages.log"))) expectedStages $"{componentId}/{expectedPhase} stops subsequent stages"
                        Expect.isFalse (Directory.Exists outputPath) $"{componentId}/{expectedPhase} writes no candidate output"
                        Expect.isFalse (File.Exists(Path.Combine(root, "transaction-started"))) $"{componentId}/{expectedPhase} reaches no publisher"
                    })
    }

let private prepareRefusesPublicationAuthority =
    testCaseAsync "the prepare CLI refuses to run when publication authority is present" <| async {
        let runnerAssembly = Assembly.GetEntryAssembly().Location
        let root = Path.Combine(Path.GetTempPath(), "mcp-release-prepare-authority-" + Guid.NewGuid().ToString("N"))
        let candidatePath = Path.Combine(root, "candidate")
        Directory.CreateDirectory root |> ignore

        try
            let info = ProcessStartInfo("dotnet")
            info.WorkingDirectory <- root
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            [ runnerAssembly; "--prepare-authority-probe"; "dotnet/release/Config.fsx"; candidatePath ]
            |> List.iter info.ArgumentList.Add
            [ "GITHUB_TOKEN"; "GH_TOKEN"; "MCP_RELEASE_DO_UPLOAD"; "MCP_RELEASE_GITHUB_OWNER"
              "MCP_RELEASE_GITHUB_REPOSITORY"; "GITHUB_ENV"; "GITHUB_OUTPUT" ]
            |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)
            info.EnvironmentVariables["GITHUB_TOKEN"] <- "controlled-test-sentinel"
            info.EnvironmentVariables["MCP_RELEASE_DO_UPLOAD"] <- "true"

            match! capture 15000 info with
            | Error error -> failtestf "prepare authority probe failed to run: %s" error
            | Ok (exitCode, stdout, _) ->
                Expect.equal exitCode 4 "prepare refuses publication authority"
                let diagnostic = JsonNode.Parse(stdout.Trim()).AsObject()
                Expect.equal (diagnostic["status"].GetValue<string>()) "failed" "authority rejection is structured"
                Expect.isTrue
                    (diagnostic["reason"].GetValue<string>().Contains("GITHUB_TOKEN", StringComparison.Ordinal))
                    "the refusing input name is identified without exposing its value"
                Expect.isFalse (Directory.Exists candidatePath) "refusal occurs before any preparation output"
        finally
            if Directory.Exists root then Directory.Delete(root, true)
    }

let tests =
    testList
        "modern preparation boundary"
        [ prepareWritesPortableCandidateAndUsesExplicitProducerGates
          preparationFailureStopsBeforeLaterStages
          prepareRefusesPublicationAuthority ]
