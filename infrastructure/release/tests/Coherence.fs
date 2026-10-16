module Release.Tests.Coherence

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open Expecto
open ReleaseTestProcess
open BuildProvenance

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let private currentParticipants = [ "dotnet", "dotnet/release/Config.json"; "workflow", "workflow/release/Config.json" ]

let private withRoot (action: string -> Async<unit>) =
    async {
        let root = Path.Combine(Path.GetTempPath(), "mcp-release-coherence-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore

        try
            do! action root
        finally
            if Directory.Exists root then Directory.Delete(root, true)
    }

let private writeRoster (root: string) (participants: (string * string) list) (extraField: (string * string) option) =
    let path = Path.Combine(root, "infrastructure", "release", "Participants.json")
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let entries = JsonArray()

    for index, (componentId, configPath) in List.indexed participants do
        let entry = JsonObject()
        entry["componentId"] <- JsonValue.Create componentId
        entry["configPath"] <- JsonValue.Create configPath

        match extraField with
        | Some (name, value) when index = 0 -> entry[name] <- JsonValue.Create value
        | _ -> ()

        entries.Add entry

    let document = JsonObject()
    document["participants"] <- entries
    File.WriteAllText(path, document.ToJsonString())

let private baselineWorkflow () =
    Path.Combine(repoRoot, ".github", "workflows", "release.yml") |> File.ReadAllText

let private runCoherenceWithOutput (root: string) (workflowText: string) (components: string) (outputPath: string option) =
    async {
        let workflowPath = Path.Combine(root, ".github", "workflows", "release.yml")
        Directory.CreateDirectory(Path.GetDirectoryName workflowPath) |> ignore
        File.WriteAllText(workflowPath, workflowText)

        let info = ProcessStartInfo("pwsh")
        info.WorkingDirectory <- repoRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        [ "-NoProfile"
          "-File"
          Path.Combine(repoRoot, "infrastructure", "release", "Coherence.ps1")
          "-Root"
          root
          "-Workflow"
          workflowPath
          "-Components"
          components ]
        |> List.iter info.ArgumentList.Add
        [ "GITHUB_ENV"; "GITHUB_OUTPUT"; "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]
        |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)
        outputPath |> Option.iter (fun path -> info.EnvironmentVariables["GITHUB_OUTPUT"] <- path)

        match! ReleaseTestProcess.capture 60000 info with
        | Ok result -> return result
        | Error error -> return failwith error
    }

let private runCoherence root workflowText components = runCoherenceWithOutput root workflowText components None

let private runProject (outputPath: string) =
    async {
        let info = ProcessStartInfo("pwsh")
        info.WorkingDirectory <- repoRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        [ "-NoProfile"
          "-File"
          Path.Combine(repoRoot, "infrastructure", "release", "Update.ps1")
          "-Project" ]
        |> List.iter info.ArgumentList.Add
        [ "GITHUB_ENV"; "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]
        |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)
        info.EnvironmentVariables["GITHUB_OUTPUT"] <- outputPath

        match! ReleaseTestProcess.capture 60000 info with
        | Ok result -> return result
        | Error error -> return failwith error
    }

let private extractWorkflowStep (workflow: string) (stepName: string) =
    let lines = workflow.Split([| '\n' |], StringSplitOptions.None)
    let header = $"      - name: {stepName}"
    let start = lines |> Array.tryFindIndex (fun line -> line.TrimEnd('\r') = header)

    match start with
    | None -> failwith $"workflow step not found: {stepName}"
    | Some index ->
        let runIndex =
            [| index + 1 .. lines.Length - 1 |]
            |> Array.tryFind (fun lineIndex -> lines[lineIndex].TrimEnd('\r') = "        run: |")
            |> Option.defaultWith (fun () -> failwith $"workflow step has no literal run block: {stepName}")

        [| runIndex + 1 .. lines.Length - 1 |]
        |> Array.takeWhile (fun lineIndex -> not (lines[lineIndex].StartsWith("      - name:", StringComparison.Ordinal)))
        |> Array.map (fun lineIndex ->
            let line = lines[lineIndex]
            if line.StartsWith("          ", StringComparison.Ordinal) then line.Substring(10) else line)
        |> String.concat Environment.NewLine

let private runPowerShellScript (workingDirectory: string) (scriptPath: string) (pathPrefix: string) (logPath: string) =
    async {
        let info = ProcessStartInfo("pwsh")
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        [ "-NoProfile"; "-File"; scriptPath ] |> List.iter info.ArgumentList.Add
        [ "GITHUB_TOKEN"; "GH_TOKEN"; "GITHUB_OUTPUT"; "GITHUB_ENV" ]
        |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)
        info.EnvironmentVariables["PATH"] <- pathPrefix + string Path.PathSeparator + info.EnvironmentVariables["PATH"]
        info.EnvironmentVariables["DOTNET_STUB_LOG"] <- logPath

        match! ReleaseTestProcess.capture 60000 info with
        | Ok result -> return result
        | Error error -> return failwith error
    }

// The workflow record step consumes the runner's canonical summary; the fixture writes exactly the field
// names the step reads so the step's own parsing and comparison run instead of a stub.
let private writeReleaseResolution
    (temp: string)
    (componentId: string)
    (version: string)
    (metadataLocalPath: string)
    (assetUri: string)
    (archiveName: string)
    (archiveSha: string)
    (manifestSha: string)
    (publishedRevision: string) =
    let document = JsonObject()
    document["status"] <- JsonValue.Create "published"
    document["reason"] <- JsonValue.Create "fixture resolution"
    document["componentId"] <- JsonValue.Create componentId
    document["version"] <- JsonValue.Create version
    document["archiveName"] <- JsonValue.Create archiveName
    document["assetUri"] <- JsonValue.Create assetUri
    document["metadataLocalPath"] <- JsonValue.Create metadataLocalPath
    document["publishedArchiveSha256"] <- JsonValue.Create archiveSha
    document["publishedManifestSha256"] <- JsonValue.Create manifestSha
    document["publishedRevision"] <- JsonValue.Create publishedRevision
    document["publishedMetadataSha256"] <- JsonValue.Create (String.replicate 64 "0")
    File.WriteAllText(Path.Combine(temp, $"publish-stdout-{componentId}.json"), document.ToJsonString())
    File.WriteAllText(Path.Combine(temp, $"publish-exit-{componentId}.txt"), "0")

// The record step starts no external process, so it needs only its own runner environment, not the stub PATH
// the Workflow CI step uses.
let private runRecordStep
    (workingDirectory: string)
    (scriptPath: string)
    (runnerTemp: string)
    (workspace: string)
    (summaryPath: string)
    (outputPath: string) =
    async {
        let info = ProcessStartInfo("pwsh")
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        [ "-NoProfile"; "-File"; scriptPath ] |> List.iter info.ArgumentList.Add
        [ "GITHUB_TOKEN"; "GH_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]
        |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)
        info.EnvironmentVariables["RUNNER_TEMP"] <- runnerTemp
        info.EnvironmentVariables["GITHUB_WORKSPACE"] <- workspace
        info.EnvironmentVariables["GITHUB_STEP_SUMMARY"] <- summaryPath
        info.EnvironmentVariables["GITHUB_OUTPUT"] <- outputPath
        info.EnvironmentVariables["RELEASE_COMPONENTS"] <- "[\"dotnet\",\"workflow\"]"
        info.EnvironmentVariables["RELEASE_DOTNET_VERSION"] <- "1.0.4"
        info.EnvironmentVariables["RELEASE_WORKFLOW_VERSION"] <- "1.0.5"

        match! ReleaseTestProcess.capture 60000 info with
        | Ok result -> return result
        | Error error -> return failwith error
    }

let private readRecordJson (path: string) =
    JsonNode.Parse(File.ReadAllText path).AsObject()

let private expectTypedFileFailure (label: string) (expectedOperation: string) (expectedPath: string) (result: Result<'a, ReleaseError>) =
    match result with
    | Ok _ -> failtestf "%s returned Ok where the absent publication directory must stay a typed failure" label
    | Error(FileFailure(operation, path, detail)) ->
        Expect.equal operation expectedOperation $"{label} names its operation"
        Expect.equal path expectedPath $"{label} names the file it could not use"
        Expect.isTrue (not (String.IsNullOrWhiteSpace detail)) $"{label} carries the underlying diagnostic"
    | Error error -> failtestf "%s returned an unexpected error case: %s" label (ReleaseError.message error)

let private containsDiagnostic (fragment: string) (text: string) =
    let withoutAnsi = Regex.Replace(text, "\u001b\\[[0-9;]*m", "")
    let withoutPowerShellBorders =
        withoutAnsi.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line -> Regex.Replace(line, @"^\s*\|\s?", ""))
        |> String.concat " "
    let normalized = Regex.Replace(withoutPowerShellBorders, @"\s+", " ")
    normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase)

let private workflowJobs (workflow: string) =
    Regex.Matches(workflow, "(?ms)^  (?<name>[A-Za-z0-9_-]+):\\r?\\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\\r?\\n|\\z)")
    |> Seq.cast<Match>
    |> Seq.map (fun matched -> matched.Groups.["name"].Value, matched.Groups.["body"].Value)
    |> Seq.toList

let private modernWorkflowGaps workflow =
    let jobs = workflowJobs workflow
    let prepare =
        jobs
        |> List.tryFind (fun (_, body) -> Regex.IsMatch(body, "(?i)&\\s+\\$\\w+\\s+prepare\\b"))
    let publisher =
        jobs
        |> List.tryFind (fun (_, body) -> Regex.IsMatch(body, "(?i)&\\s+\\$\\w+\\s+publish\\b"))
    let gaps = ResizeArray<string>()

    match prepare with
    | None -> gaps.Add "no fresh job invokes ReleaseRunner prepare"
    | Some (_, body) when not (Regex.IsMatch(body, "(?m)^\\s*runs-on:")) -> gaps.Add "the preparation job is not a hosted job"
    | Some (_, body) when Regex.IsMatch(body, "(?i)contents:\\s*write") -> gaps.Add "the preparation job has release-write authority"
    | _ -> ()

    match publisher with
    | None -> gaps.Add "no separate data-only job invokes ReleaseRunner publish"
    | Some (_, body) when not (Regex.IsMatch(body, "(?m)^\\s*runs-on:")) -> gaps.Add "the publisher job is not a hosted job"
    | Some (_, body) when not (Regex.IsMatch(body, "(?i)contents:\\s*write")) -> gaps.Add "the publisher job does not declare its write authority"
    | Some (_, body) when Regex.IsMatch(body, "(?i)dotnet\\s+(build|restore|fsi|test)\\b")
                          || body.Contains("Build.fsx", StringComparison.Ordinal)
                          || body.Contains("Pins.fsx", StringComparison.Ordinal) ->
        gaps.Add "the publication job still executes build/test/toolchain work"
    | _ -> ()

    match prepare, publisher with
    | Some (prepareName, _), Some (publisherName, publisherBody) ->
        if prepareName = publisherName then
            gaps.Add "preparation and publication are not isolated in fresh jobs"

        if not (publisherBody.Contains($"- {prepareName}", StringComparison.Ordinal)) then
            gaps.Add $"publication job does not depend on the {prepareName} job"
    | _ -> ()

    List.ofSeq gaps

let private publishReadinessGaps workflow =
    let publishBody =
        workflowJobs workflow
        |> List.tryFind (fun (name, _) -> name = "publication")
        |> Option.map snd

    match publishBody with
    | None -> [ "the publication job is missing" ]
    | Some body ->
        let executableBody =
            body.Split([| '\n' |], StringSplitOptions.None)
            |> Array.filter (fun line -> not (line.TrimStart().StartsWith("#", StringComparison.Ordinal)))
            |> String.concat Environment.NewLine
        let gaps = ResizeArray<string>()

        if Regex.IsMatch(body, "(?im)^ {4}if:\\s*(?:\\$\\{\\{\\s*)?false\\b") then
            gaps.Add "the publication job is disabled"

        let runnerIsExecutable =
            executableBody.Contains("ReleaseRunner.exe", StringComparison.OrdinalIgnoreCase)
            || executableBody.Contains("ReleaseRunner.dll", StringComparison.OrdinalIgnoreCase)
        let publishIsInvoked = Regex.IsMatch(executableBody, "(?im)^\\s*&\\s*.+?\\s+publish\\b")

        if not (runnerIsExecutable && publishIsInvoked) then
            gaps.Add "the publication job has no executable ReleaseRunner publish invocation"

        // trust-receipt flag is retired: the publisher reads the runtime inventory and verifies it locally before the native call.

        List.ofSeq gaps

let private releaseWorkflowRequiresFreshPreparationBuilderAndPublisherJobs =
    testCase "the release workflow isolates preparation, trusted building, and data-only publication" (fun () ->
        let workflowPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml")
        let workflow = File.ReadAllText workflowPath
        let gaps = modernWorkflowGaps workflow @ publishReadinessGaps workflow
        Expect.isEmpty gaps (String.concat Environment.NewLine gaps))

let private coherenceGuardChecksActualRosterAndWorkflowMarkers =
    testCaseAsync "the production Coherence.ps1 accepts roster wiring and rejects marker drift" <| async {
        do!
            withRoot (fun root ->
                async {
                    writeRoster root currentParticipants None |> ignore
                    let correctProjection = "[\"dotnet\",\"workflow\"]"
                    let workflowFixture = baselineWorkflow ()

                    let! baselineCode, baselineOut, baselineErr = runCoherence root workflowFixture correctProjection
                    Expect.equal baselineCode 0 "the coherent workflow and projected roster pass"
                    Expect.isTrue
                        ((baselineOut + baselineErr).Contains("cross-stage coherence ok", StringComparison.Ordinal))
                        "the actual PS guard reports its checked invariants"

                    let hardcodedMatrix =
                            workflowFixture.Replace(
                            "          $components = @(ConvertFrom-Json $env:RELEASE_COMPONENTS)",
                            "          $components = @(\"dotnet\")"
                        )
                    let! matrixCode, matrixOut, matrixErr = runCoherence root hardcodedMatrix correctProjection
                    Expect.isTrue (matrixCode <> 0) "a hardcoded workflow matrix fails the actual guard"
                    Expect.isTrue
                        ((matrixOut + matrixErr).Contains("publisher reads the projected components", StringComparison.Ordinal))
                        "the hardcoded projection is identified by the publisher's projection-source check"

                    let renamedBootstrapOutput =
                            workflowFixture.Replace(
                            "          PROJECTED_COMPONENTS: ${{ steps.project.outputs.components }}",
                            "          PROJECTED_COMPONENTS: ${{ steps.project.outputs.participants }}"
                        )
                    let! outputMarkerCode, outputMarkerOut, outputMarkerErr = runCoherence root renamedBootstrapOutput correctProjection
                    Expect.isTrue (outputMarkerCode <> 0) "a renamed projection output marker fails the actual guard"
                    Expect.isTrue
                        ((outputMarkerOut + outputMarkerErr).Contains("the coherence guard receives the projection value", StringComparison.Ordinal))
                        "the projection-output marker mismatch is identified"

                    let mismatchedProjection = "[\"dotnet\",\"telegram\"]"
                    let! projectionCode, projectionOut, projectionErr = runCoherence root workflowFixture mismatchedProjection
                    Expect.isTrue (projectionCode <> 0) "a mismatched emitted component list fails the actual guard"
                    Expect.isTrue
                        ((projectionOut + projectionErr).Contains("projected participants", StringComparison.Ordinal))
                        "the roster/output mismatch is identified"

                    let hardcodedCli =
                            workflowFixture.Replace(
                            "            $configPath = Join-Path (Get-Location) \"$component/release/Config.json\"",
                            "            $configPath = Join-Path (Get-Location) \"dotnet/release/Config.json\""
                        )
                    let! cliCode, cliOut, cliErr = runCoherence root hardcodedCli correctProjection
                    Expect.isTrue (cliCode <> 0) "a hardcoded CLI config path fails the actual guard"
                    Expect.isTrue
                        ((cliOut + cliErr).Contains("no participant literal in a CLI or policy path", StringComparison.Ordinal))
                        "the CLI path drift is identified"
                })
    }

let private coherenceGuardRejectsRosterPolicyAndInventoryDrift =
    testCaseAsync "the production Coherence.ps1 rejects roster policy and inventory drift" <| async {
        do!
            withRoot (fun root ->
                async {
                    let output = "[\"dotnet\",\"workflow\"]"
                    let workflowFixture = baselineWorkflow ()
                    let invokeCurrentWorkflow () = runCoherence root workflowFixture output

                    writeRoster root currentParticipants (Some ("version", "9.9.9")) |> ignore
                    let! policyResult = invokeCurrentWorkflow ()

                    writeRoster root [ "dotnet", "dotnet/release/Config.json"; "dotnet", "dotnet/release/Config.json" ] None |> ignore
                    let! duplicateResult = invokeCurrentWorkflow ()

                    writeRoster root [ "dotnet", "dotnet/release/Config.json"; "telegram", "telegram/release/Config.json" ] None |> ignore
                    let! extraResult = invokeCurrentWorkflow ()

                    writeRoster root [ "dotnet", "dotnet/release/Config.json" ] None |> ignore
                    let! omittedResult = invokeCurrentWorkflow ()

                    writeRoster root [ "dotnet", "dotnet/release/Alternate.json"; "workflow", "workflow/release/Config.json" ] None |> ignore
                    let! pathResult = invokeCurrentWorkflow ()

                    let outputOf (_, stdout, stderr) = stdout + stderr
                    let assertRejected label (code, _, _) = Expect.isTrue (code <> 0) label

                    assertRejected "a policy mirror in the roster fails before stages run" policyResult
                    Expect.isTrue
                        ((outputOf policyResult).Contains("forbidden field 'version'", StringComparison.Ordinal))
                        $"the forbidden policy field is identified: {outputOf policyResult}"
                    assertRejected "duplicate roster ids fail the actual guard" duplicateResult
                    Expect.isTrue
                        ((outputOf duplicateResult).Contains("duplicate componentId", StringComparison.Ordinal))
                        $"the duplicate roster id is identified: {outputOf duplicateResult}"
                    assertRejected "an extra roster participant fails the actual guard" extraResult
                    Expect.isTrue
                        ((outputOf extraResult).Contains("unsupported componentId 'telegram'", StringComparison.Ordinal))
                        $"the unsupported participant is identified: {outputOf extraResult}"
                    assertRejected "an omitted required roster participant fails the actual guard" omittedResult
                    Expect.isTrue
                        (containsDiagnostic "missing required componentId(s): workflow" (outputOf omittedResult))
                        $"the omitted participant is identified: {outputOf omittedResult}"
                    assertRejected "config path drift fails the actual guard" pathResult
                    Expect.isTrue
                        ((outputOf pathResult).Contains("CLI entry registration", StringComparison.Ordinal))
                        $"the registered CLI path mismatch is identified: {outputOf pathResult}"
                })
    }

let private coherenceGuardUsesExplicitProjectionAcrossStepOutputs =
    testCaseAsync "the coherence guard consumes the project step value, not its own output file" <| async {
        do!
            withRoot (fun root ->
                async {
                    writeRoster root currentParticipants None |> ignore
                    let workflowFixture = baselineWorkflow ()
                    let projectOutput = Path.Combine(root, "project-output.txt")
                    let guardOutput = Path.Combine(root, "guard-output.txt")

                    let! projectCode, projectOut, projectErr = runProject projectOutput
                    Expect.equal projectCode 0 $"Update.ps1 -Project succeeds: {projectOut}{projectErr}"
                    let projectedComponents =
                        File.ReadAllLines projectOutput
                        |> Array.tryFind (fun line -> line.StartsWith("components=", StringComparison.Ordinal))
                        |> Option.map (fun line -> line.Substring("components=".Length))
                        |> Option.defaultValue ""
                    Expect.equal projectedComponents "[\"dotnet\",\"workflow\"]" "the project step emits the roster component JSON"

                    let! guardCode, guardOut, guardErr = runCoherenceWithOutput root workflowFixture projectedComponents (Some guardOutput)
                    Expect.equal guardCode 0 $"the guard accepts the explicit projected value: {guardOut}{guardErr}"
                    Expect.isFalse (File.Exists guardOutput) "the guard process has no step-local GITHUB_OUTPUT file"

                    let! missingCode, missingOut, missingErr = runCoherence root workflowFixture ""
                    Expect.notEqual missingCode 0 "a missing projected component value fails closed"
                    Expect.isTrue
                        ((missingOut + missingErr).Contains("-Components must carry the projection value", StringComparison.Ordinal))
                        "the missing explicit projection is diagnosed"
                })
    }

let private releaseWorkflowStopsBeforePublicationWhenWorkflowTestsFail =
    testCaseAsync "the actual Workflow CI step preserves a failing native exit before later entrypoints" <| async {
        do!
            withRoot (fun root ->
                async {
                    let workflowPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml")
                    let workflow = File.ReadAllText workflowPath
                    let paths =
                        [ "workflow/tests/AuthorityMetadataTests.fsx"
                          "workflow/tests/EphemeralLockInvariantTests.fsx"
                          "workflow/tests/SchemaParityGuardTests.fsx"
                          "workflow/tests/TaskContractTests.fsx"
                          "workflow/tests/TaskPathSafetyTests.fsx"
                          "workflow/tests/TaskProfileTests.fsx"
                          "workflow/tests/TaskScratchCompatibilityTests.fsx"
                          "workflow/tests/TaskSupportingArtifactTests.fsx"
                          "workflow/tests/WorkflowMcpFindingTests.fsx"
                          "workflow/tests/WorkflowMcpNativeCompatTests.fsx"
                          "workflow/tests/WorkflowMcpTests.fsx"
                          "workflow/tests/WorkflowTests.fsx" ]
                    let stepName = "run the non-packaging Workflow test entrypoints (native exit preserved)"
                    let script = extractWorkflowStep workflow stepName
                    let listedPaths =
                        Regex.Matches(script, "\"(workflow/tests/[^\"]+\\.fsx)\"")
                        |> Seq.cast<Match>
                        |> Seq.map (fun matched -> matched.Groups[1].Value)
                        |> Seq.toList
                    Expect.equal listedPaths paths "CI retains the explicit, deterministic twelve-entrypoint set"
                    let publisherDependencies =
                        workflowJobs workflow
                        |> List.tryFind (fun (name, _) -> name = "publication")
                        |> Option.bind (fun (_, body) ->
                            let needs =
                                Regex.Match(
                                    body,
                                    "(?m)^    needs:[ \\t]*\\r?\\n(?<items>(?:      - [A-Za-z0-9_-]+[ \\t]*\\r?\\n)+)"
                                )

                            if not needs.Success then
                                None
                            else
                                Regex.Matches(needs.Groups["items"].Value, "(?m)^      - ([A-Za-z0-9_-]+)")
                                |> Seq.cast<Match>
                                |> Seq.map (fun matched -> matched.Groups[1].Value)
                                |> Seq.toList
                                |> Some)
                        |> Option.defaultValue []
                    let expectedPublisherDependencies = set [ "publisher"; "preparation" ]
                    Expect.equal publisherDependencies.Length expectedPublisherDependencies.Count "publication has exactly two gating jobs"
                    Expect.equal (Set.ofList publisherDependencies) expectedPublisherDependencies "publication depends on publisher and preparation"

                    let bin = Path.Combine(root, "stub-bin")
                    Directory.CreateDirectory bin |> ignore
                    let stub = Path.Combine(bin, "dotnet.cmd")
                    File.WriteAllText(stub, "@echo off\r\necho %*>>\"%DOTNET_STUB_LOG%\"\r\nexit /b 7\r\n")
                    let scriptPath = Path.Combine(root, "workflow-step.ps1")
                    File.WriteAllText(scriptPath, script)
                    let logPath = Path.Combine(root, "dotnet-calls.log")

                    let! code, stdout, stderr = runPowerShellScript repoRoot scriptPath bin logPath
                    let calls = if File.Exists logPath then File.ReadAllLines logPath else [||]
                    Expect.equal code 7 $"the step returns the first failing native code: {stdout}{stderr}"
                    Expect.equal calls.Length 1 "later entrypoints do not run after the first failed test"
                    Expect.isTrue (calls[0].Contains(paths.Head, StringComparison.Ordinal)) "the stub failed the first selected entrypoint"
                })
    }

// One trusted publication path is shared by the runner's public ensurePublicationDirectory seam, its metadata
// boundary, and the workflow record step: the runner creates the runner-temp publication directory, and every
// later writer, reader, and consumer of the projection must resolve exactly that path.
let private metadataBoundaryAndRecordStepShareTheTrustedPublicationPath =
    testCaseAsync "the record step verifies only the runner's trusted publication projection" <| async {
        do!
            withRoot (fun root ->
                async {
                    let runnerTemp = Path.Combine(root, "runner-temp")
                    let workspace = Path.Combine(root, "workspace")
                    let publication = Path.Combine(runnerTemp, "publication")
                    Directory.CreateDirectory runnerTemp |> ignore
                    Directory.CreateDirectory workspace |> ignore

                    let assetUri = "https://example.test/releases/download/dotnet/v1.0.4.zip"
                    let archiveName = "v1.0.4.zip"
                    let archiveSha = String.replicate 64 "1"
                    let manifestSha = String.replicate 64 "2"
                    let publishedRevision = String.replicate 40 "4"
                    let projection =
                        ReleaseMetadata.makeProjection
                            "dotnet" "1.0.4" assetUri archiveName archiveSha manifestSha publishedRevision
                            (Some "11.0.100-rc.1.26425.128") (Some "11.0.0-rc.1.26425.128") (Some "win-x64")
                    let metadataPath = Path.Combine(publication, ReleaseMetadata.metadataAssetName "dotnet" "1.0.4")

                    // While the private publication directory is absent, every metadata file operation must stay
                    // the typed FileFailure instead of escaping as an AggregateException.
                    let! absentWrite = ReleaseMetadata.writeToFileAsync metadataPath projection
                    let! absentRead = ReleaseMetadata.readFromFile metadataPath
                    let! absentHash = ReleaseMetadata.sha256OfFileAsync metadataPath
                    Expect.isFalse (Directory.Exists publication) "the private publication directory starts absent"
                    expectTypedFileFailure "the metadata write" "write" metadataPath absentWrite
                    expectTypedFileFailure "the metadata read" "read" metadataPath absentRead
                    expectTypedFileFailure "the metadata hash" "hash" metadataPath absentHash

                    // The production runner owns the creation of its private directory; the candidate root is a
                    // fresh path inside RUNNER_TEMP, so the case exercises that creation instead of pre-creating it.
                    match ReleaseRunner.ensurePublicationDirectory (Path.Combine(runnerTemp, "candidate-dotnet")) with
                    | Ok directory ->
                        Expect.equal directory publication "the runner creates and returns its runner-temp publication directory"
                        Expect.isTrue (Directory.Exists directory) "the runner's publication directory exists after creation"
                    | Error error -> failtestf "the runner failed to create its publication directory: %s" (ReleaseError.message error)

                    let! writeResult = ReleaseMetadata.writeToFileAsync metadataPath projection
                    Expect.isTrue (Result.isOk writeResult) $"the production metadata writer emits the trusted projection: {writeResult}"

                    // dotnet reports the trusted runner-temp path; workflow reports a checkout path, which the
                    // retired workspace rule accepted and the corrected step must refuse.
                    let workflowMetadataPath = Path.Combine(workspace, "publication", ReleaseMetadata.metadataAssetName "workflow" "1.0.5")
                    writeReleaseResolution runnerTemp "dotnet" "1.0.4" metadataPath assetUri archiveName archiveSha manifestSha publishedRevision
                    writeReleaseResolution runnerTemp "workflow" "1.0.5" workflowMetadataPath "https://example.test/releases/download/workflow/v1.0.5.zip" "v1.0.5.zip" archiveSha manifestSha publishedRevision

                    let scriptPath = Path.Combine(root, "record-step.ps1")
                    File.WriteAllText(scriptPath, extractWorkflowStep (baselineWorkflow ()) "record the structured release results")
                    let summaryPath = Path.Combine(root, "step-summary.md")
                    let outputPath = Path.Combine(root, "step-output.txt")
                    File.WriteAllText(summaryPath, "")
                    File.WriteAllText(outputPath, "")

                    let! code, stdout, stderr = runRecordStep root scriptPath runnerTemp workspace summaryPath outputPath
                    Expect.equal code 0 $"the record step completes on every enrolled component: {stdout}{stderr}"

                    let evidenceRoot = Path.Combine(runnerTemp, "release-evidence")
                    let dotnetRecord = readRecordJson (Path.Combine(evidenceRoot, "release-status-dotnet.json"))
                    Expect.equal (dotnetRecord["verified"].GetValue<bool>()) true "the trusted publication projection is verified evidence"
                    Expect.equal (dotnetRecord["projectionKind"].GetValue<string>()) "verified-projection" "the trusted publication projection is the verified kind"
                    Expect.equal (dotnetRecord["diagnostics"].AsArray().Count) 0 "a verified projection records no diagnostic"
                    let copied = Path.Combine(evidenceRoot, "published-metadata-dotnet.json")
                    Expect.isTrue (File.Exists copied) "the verified projection is copied into the evidence set"
                    Expect.equal (Convert.ToHexString (File.ReadAllBytes metadataPath)) (Convert.ToHexString (File.ReadAllBytes copied)) "the copied projection is byte-equal to the runner's written projection"
                    let copiedProjection = readRecordJson copied
                    Expect.equal (copiedProjection["schemaVersion"].GetValue<int>()) 1 "the copied projection carries schemaVersion 1"
                    Expect.equal (copiedProjection["componentId"].GetValue<string>()) "dotnet" "the copied projection carries the looped component"
                    Expect.equal (copiedProjection["version"].GetValue<string>()) "1.0.4" "the copied projection carries the trusted version"
                    Expect.equal (copiedProjection["assetUri"].GetValue<string>()) assetUri "the copied projection carries the resolution assetUri"
                    Expect.equal (copiedProjection["archiveName"].GetValue<string>()) archiveName "the copied projection carries the resolution archiveName"
                    Expect.equal (copiedProjection["archiveSha256"].GetValue<string>()) archiveSha "the copied projection carries the published archive hash"
                    Expect.equal (copiedProjection["manifestSha256"].GetValue<string>()) manifestSha "the copied projection carries the published manifest hash"
                    Expect.equal (copiedProjection["publishedRevision"].GetValue<string>()) publishedRevision "the copied projection carries the published revision"

                    let workflowRecord = readRecordJson (Path.Combine(evidenceRoot, "release-status-workflow.json"))
                    Expect.equal (workflowRecord["verified"].GetValue<bool>()) false "a checkout projection path is not verified evidence"
                    Expect.equal (workflowRecord["projectionKind"].GetValue<string>()) "status-evidence-only" "an unexpected location stays unverified"
                    let workflowDiagnostics =
                        workflowRecord["diagnostics"].AsArray()
                        |> Seq.map (fun node -> node.GetValue<string>())
                        |> String.concat " "
                    Expect.isTrue
                        (workflowDiagnostics.Contains("inside the checkout", StringComparison.Ordinal))
                        $"the checkout location is named as the refusal: {workflowDiagnostics}"

                    // D1: the trusted location alone is not proof; the projection must also carry the declared JSON
                    // types. Re-run the same record block over the still-trusted path, reusing the runner-written
                    // projection bytes and flipping only schemaVersion to a JSON string, so the type guard is the
                    // single faulting check while every hash, revision and other field stays exact.
                    let malformed = readRecordJson metadataPath
                    malformed["schemaVersion"] <- JsonValue.Create "1"
                    File.WriteAllText(metadataPath, malformed.ToJsonString())
                    let rereadMalformed = readRecordJson metadataPath
                    Expect.equal (rereadMalformed["schemaVersion"].GetValue<string>()) "1" "the malformed variant carries schemaVersion as a JSON string at the trusted path"

                    // The positive run above already copied this test-owned evidence file; clear exactly that one
                    // copy so the absence assertion below proves the refused projection was not re-copied.
                    if File.Exists copied then File.Delete copied

                    let! malformedCode, malformedStdout, malformedStderr = runRecordStep root scriptPath runnerTemp workspace summaryPath outputPath
                    Expect.equal malformedCode 0 $"the record step completes on a type-invalid projection: {malformedStdout}{malformedStderr}"

                    let malformedRecord = readRecordJson (Path.Combine(evidenceRoot, "release-status-dotnet.json"))
                    Expect.equal (malformedRecord["verified"].GetValue<bool>()) false "a JSON string schemaVersion is not verified evidence"
                    Expect.equal (malformedRecord["projectionKind"].GetValue<string>()) "status-evidence-only" "a type-invalid projection stays status-only evidence"
                    let malformedDiagnostics =
                        malformedRecord["diagnostics"].AsArray()
                        |> Seq.map (fun node -> node.GetValue<string>())
                        |> String.concat " "
                    Expect.isTrue
                        (malformedDiagnostics.Contains("the verified projection 'schemaVersion' is a JSON string, not the JSON integer '1'", StringComparison.Ordinal))
                        $"the type fault is named exactly: {malformedDiagnostics}"
                    Expect.isFalse (File.Exists copied) "the refused projection is not copied into the evidence set"

                    // The same public seam fails closed when the publication path is already occupied by a plain
                    // file: the runner's typed mkdir refusal, which the record step never sees.
                    let blockedRoot = Path.Combine(root, "blocked-runner-temp")
                    Directory.CreateDirectory blockedRoot |> ignore
                    let blockedPublication = Path.Combine(blockedRoot, "publication")
                    File.WriteAllText(blockedPublication, "occupied")
                    expectTypedFileFailure
                        "the blocked publication creation"
                        "mkdir"
                        blockedPublication
                        (ReleaseRunner.ensurePublicationDirectory (Path.Combine(blockedRoot, "candidate-dotnet")))
                })
    }

let tests =
    testList
        "cross-stage coherence"
        [ coherenceGuardChecksActualRosterAndWorkflowMarkers
          coherenceGuardRejectsRosterPolicyAndInventoryDrift
          coherenceGuardUsesExplicitProjectionAcrossStepOutputs
          releaseWorkflowStopsBeforePublicationWhenWorkflowTestsFail
          releaseWorkflowRequiresFreshPreparationBuilderAndPublisherJobs
          metadataBoundaryAndRecordStepShareTheTrustedPublicationPath ]
