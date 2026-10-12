module Release.Tests.Coherence

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open Expecto
open ReleaseTestProcess

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let private currentParticipants = [ "dotnet", "dotnet/release/Config.fsx"; "workflow", "workflow/release/Config.fsx" ]

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

let private baselineWorkflow =
    String.concat
        Environment.NewLine
        [ "jobs:"
          "      components: ${{ steps.project.outputs.components }}"
          "        component: ${{ fromJSON(needs.bootstrap.outputs.components) }}"
          "    needs: bootstrap"
          "          RELEASE_COMPONENTS: ${{ needs.bootstrap.outputs.components }}"
          "  run: pwsh infrastructure/release/Update.ps1 -Verify"
          "  run: pwsh infrastructure/release/Update.ps1 -Verify -Component ${{ matrix.component }}"
          "  run: ReleaseRunner.exe orchestrate \"${{ matrix.component }}/release/Config.fsx\""
          "  uses: actions/setup-dotnet@fixture"
          "    dotnet-version: ${{ steps.project.outputs.sdk }}" ]

let private runCoherence (root: string) (workflowText: string) (projectionLine: string) =
    async {
        let workflowPath = Path.Combine(root, ".github", "workflows", "release.yml")
        Directory.CreateDirectory(Path.GetDirectoryName workflowPath) |> ignore
        File.WriteAllText(workflowPath, workflowText)

        let outputPath = Path.Combine(root, "projected-output.txt")
        File.WriteAllText(outputPath, projectionLine)

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
          "-OutputFile"
          outputPath ]
        |> List.iter info.ArgumentList.Add
        [ "GITHUB_ENV"; "GITHUB_OUTPUT"; "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]
        |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

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

let private coherenceGuardChecksActualRosterAndWorkflowMarkers =
    testCaseAsync "the production Coherence.ps1 accepts roster wiring and rejects marker drift" <| async {
        do!
            withRoot (fun root ->
                async {
                    writeRoster root currentParticipants None |> ignore
                    let correctProjection = "components=[\"dotnet\",\"workflow\"]"

                    let! baselineCode, baselineOut, baselineErr = runCoherence root baselineWorkflow correctProjection
                    Expect.equal baselineCode 0 "the coherent workflow and projected roster pass"
                    Expect.isTrue
                        ((baselineOut + baselineErr).Contains("cross-stage coherence ok", StringComparison.Ordinal))
                        "the actual PS guard reports its checked invariants"

                    let hardcodedMatrix =
                        baselineWorkflow.Replace(
                            "        component: ${{ fromJSON(needs.bootstrap.outputs.components) }}",
                            "        component: dotnet"
                        )
                    let! matrixCode, matrixOut, matrixErr = runCoherence root hardcodedMatrix correctProjection
                    Expect.isTrue (matrixCode <> 0) "a hardcoded workflow matrix fails the actual guard"
                    Expect.isTrue
                        ((matrixOut + matrixErr).Contains("release matrix from roster projection", StringComparison.Ordinal))
                        "the matrix marker mismatch is identified"

                    let renamedBootstrapOutput =
                        baselineWorkflow.Replace(
                            "      components: ${{ steps.project.outputs.components }}",
                            "      participants: ${{ steps.project.outputs.components }}"
                        )
                    let! outputMarkerCode, outputMarkerOut, outputMarkerErr = runCoherence root renamedBootstrapOutput correctProjection
                    Expect.isTrue (outputMarkerCode <> 0) "a renamed bootstrap output marker fails the actual guard"
                    Expect.isTrue
                        ((outputMarkerOut + outputMarkerErr).Contains("bootstrap components output", StringComparison.Ordinal))
                        "the bootstrap output mismatch is identified"

                    let mismatchedProjection = "components=[\"dotnet\",\"telegram\"]"
                    let! projectionCode, projectionOut, projectionErr = runCoherence root baselineWorkflow mismatchedProjection
                    Expect.isTrue (projectionCode <> 0) "a mismatched emitted component list fails the actual guard"
                    Expect.isTrue
                        ((projectionOut + projectionErr).Contains("projected participants", StringComparison.Ordinal))
                        "the roster/output mismatch is identified"

                    let hardcodedCli =
                        baselineWorkflow.Replace(
                            "ReleaseRunner.exe orchestrate \"${{ matrix.component }}/release/Config.fsx\"",
                            "ReleaseRunner.exe orchestrate \"dotnet/release/Config.fsx\""
                        )
                    let! cliCode, cliOut, cliErr = runCoherence root hardcodedCli correctProjection
                    Expect.isTrue (cliCode <> 0) "a hardcoded CLI config path fails the actual guard"
                    Expect.isTrue
                        ((cliOut + cliErr).Contains("transaction CLI path derives from the matrix participant", StringComparison.Ordinal))
                        "the CLI path drift is identified"
                })
    }

let private coherenceGuardRejectsRosterPolicyAndInventoryDrift =
    testCaseAsync "the production Coherence.ps1 rejects roster policy and inventory drift" <| async {
        do!
            withRoot (fun root ->
                async {
                    let output = "components=[\"dotnet\",\"workflow\"]"
                    let invokeCurrentWorkflow () = runCoherence root baselineWorkflow output

                    writeRoster root currentParticipants (Some ("version", "9.9.9")) |> ignore
                    let! policyResult = invokeCurrentWorkflow ()

                    writeRoster root [ "dotnet", "dotnet/release/Config.fsx"; "dotnet", "dotnet/release/Config.fsx" ] None |> ignore
                    let! duplicateResult = invokeCurrentWorkflow ()

                    writeRoster root [ "dotnet", "dotnet/release/Config.fsx"; "telegram", "telegram/release/Config.fsx" ] None |> ignore
                    let! extraResult = invokeCurrentWorkflow ()

                    writeRoster root [ "dotnet", "dotnet/release/Config.fsx" ] None |> ignore
                    let! omittedResult = invokeCurrentWorkflow ()

                    writeRoster root [ "dotnet", "dotnet/release/Alternate.fsx"; "workflow", "workflow/release/Config.fsx" ] None |> ignore
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

let tests =
    testList
        "cross-stage coherence"
        [ coherenceGuardChecksActualRosterAndWorkflowMarkers
          coherenceGuardRejectsRosterPolicyAndInventoryDrift ]
