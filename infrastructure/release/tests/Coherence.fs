module Release.Tests.Coherence

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open Expecto
open ReleaseTestProcess

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

let tests =
    testList
        "cross-stage coherence"
        [ coherenceGuardChecksActualRosterAndWorkflowMarkers
          coherenceGuardRejectsRosterPolicyAndInventoryDrift
          coherenceGuardUsesExplicitProjectionAcrossStepOutputs
          releaseWorkflowStopsBeforePublicationWhenWorkflowTestsFail
          releaseWorkflowRequiresFreshPreparationBuilderAndPublisherJobs ]
