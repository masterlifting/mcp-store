module Release.Tests.Enrollment

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Expecto
open BuildProvenance
open ReleaseCompare
open ReleaseEvaluate
open ReleaseMetadata
open ReleaseEnrollment
open ReleaseTestProcess

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private currentParticipants =
    [ "dotnet", "dotnet/release/Config.fsx"
      "workflow", "workflow/release/Config.fsx" ]

let private withTemporaryRoot (action: string -> Async<unit>) : Async<unit> =
    async {
        let root = Path.Combine(Path.GetTempPath(), "mcp-release-enrollment-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore

        try
            do! action root
        finally
            if Directory.Exists root then Directory.Delete(root, true)
    }

let private renderRoster (participants: (string * string) list) (extraField: (string * string) option) =
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
    document.ToJsonString()

let private writeRoster root participants extraField =
    let path = ReleaseEnrollment.rosterPath root
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, renderRoster participants extraField)
    path

let private writeRosterText root (text: string) =
    let path = ReleaseEnrollment.rosterPath root
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)
    path

let private configText componentId version entryDll sdk runtime platform =
    let archiveName = $"v{version}.zip"
    let assetUri = $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/{archiveName}"

    String.concat
        Environment.NewLine
        [ "module ReleaseConfig"
          $"let componentId = \"{componentId}\""
          $"let version = \"{version}\""
          $"let entryDll = \"{entryDll}\""
          $"let sdkVersion = \"{sdk}\""
          $"let runtimeVersion = \"{runtime}\""
          $"let platform = \"{platform}\""
          $"let archiveName = \"{archiveName}\""
          $"let assetUri = \"{assetUri}\""
          $"let publishedFiles = [ \"{entryDll}\" ]" ]

let private writeConfig root configPath componentId version entryDll sdk runtime platform =
    let path = Path.Combine(root, configPath)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, configText componentId version entryDll sdk runtime platform)
    path

let private projectConfigEntries =
    [ "dotnet", "dotnet/release/Config.fsx", "Mcp.Dotnet.dll"
      "workflow", "workflow/release/Config.fsx", "Mcp.Workflow.dll" ]

let private writeProjectConfigs root sdk runtime platform participants =
    for componentId, configPath, entryDll in projectConfigEntries do
        if participants |> List.exists (fun (id, _) -> id = componentId) then
            writeConfig root configPath componentId "1.0.0" entryDll sdk runtime platform |> ignore

let private runUpdate root parameters =
    async {
        let outputPath = Path.Combine(root, "github-output-" + Guid.NewGuid().ToString("N") + ".txt")
        let info = ProcessStartInfo("pwsh")
        info.WorkingDirectory <- repoRoot
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add("-NoProfile")
        info.ArgumentList.Add("-File")
        info.ArgumentList.Add(Path.Combine(repoRoot, "infrastructure", "release", "Update.ps1"))
        parameters |> List.iter info.ArgumentList.Add
        info.EnvironmentVariables.Remove "GITHUB_ENV" |> ignore
        info.EnvironmentVariables["GITHUB_OUTPUT"] <- outputPath
        [ "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]
        |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

        match! ReleaseTestProcess.capture 60000 info with
        | Error error -> return failwith error
        | Ok (exitCode, stdout, stderr) ->
            let output = if File.Exists outputPath then File.ReadAllText outputPath else ""
            return exitCode, stdout, stderr, output
    }

let private containsDiagnostic (fragment: string) (text: string) =
    let withoutAnsi = Regex.Replace(text, "\u001b\\[[0-9;]*m", "")
    let withoutPowerShellBorders =
        withoutAnsi.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line -> Regex.Replace(line, @"^\s*\|\s?", ""))
        |> String.concat " "
    let normalized = Regex.Replace(withoutPowerShellBorders, @"\s+", " ")
    normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase)

let private updateProjectUsesRosterProjectionAndRejectsDrift =
    testCaseAsync "Update.ps1 -Project uses the controlled roster and rejects duplicate/config identity/toolchain drift" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    let reversed = List.rev currentParticipants
                    writeProjectConfigs root "fixture-sdk" "fixture-runtime" "fixture-platform" reversed
                    writeRoster root reversed None |> ignore

                    let! exitCode, _, _, output = runUpdate root [ "-Project"; "-Root"; root ]
                    Expect.equal exitCode 0 "the controlled roster projects without F# evaluation"
                    Expect.isTrue (output.Contains("components=[\"workflow\",\"dotnet\"]", StringComparison.Ordinal)) "component projection preserves authoritative roster order"

                    let duplicate = [ "dotnet", "dotnet/release/Config.fsx"; "dotnet", "dotnet/release/Config.fsx" ]
                    writeRoster root duplicate None |> ignore
                    let! duplicateResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root [ "dotnet", "dotnet/release/Config.fsx" ] None |> ignore
                    let! omittedResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root (currentParticipants @ [ "telegram", "telegram/release/Config.fsx" ]) None |> ignore
                    let! extraResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root currentParticipants (Some ("sdkVersion", "copied-policy")) |> ignore
                    let! policyResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root currentParticipants None |> ignore
                    writeConfig root "dotnet/release/Config.fsx" "workflow" "1.0.0" "Mcp.Workflow.dll" "fixture-sdk" "fixture-runtime" "fixture-platform" |> ignore
                    writeConfig root "workflow/release/Config.fsx" "workflow" "1.0.0" "Mcp.Workflow.dll" "fixture-sdk" "fixture-runtime" "fixture-platform" |> ignore
                    let! identityResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root currentParticipants None |> ignore
                    writeConfig root "dotnet/release/Config.fsx" "dotnet" "1.0.0" "Mcp.Dotnet.dll" "fixture-sdk" "fixture-runtime" "fixture-platform" |> ignore
                    writeConfig root "workflow/release/Config.fsx" "workflow" "1.0.0" "Mcp.Workflow.dll" "fixture-sdk" "runtime-drift" "fixture-platform" |> ignore
                    let! toolchainResult = runUpdate root [ "-Project"; "-Root"; root ]

                    let outputOf (_, stdout, stderr, _) = stdout + stderr
                    let assertRejected label (code, _, _, _) = Expect.isTrue (code <> 0) label

                    assertRejected "the PS boundary fails closed on duplicate identities" duplicateResult
                    Expect.isTrue
                        ((outputOf duplicateResult).Contains("duplicate componentId", StringComparison.Ordinal))
                        "duplicate PS diagnostic"
                    assertRejected "the PS boundary rejects an omitted participant" omittedResult
                    Expect.isTrue
                        (containsDiagnostic "missing required componentId(s): workflow" (outputOf omittedResult))
                        $"the missing PS participant is identified: {outputOf omittedResult}"
                    assertRejected "the PS boundary rejects an extra participant" extraResult
                    Expect.isTrue
                        ((outputOf extraResult).Contains("unsupported componentId 'telegram'", StringComparison.Ordinal))
                        "the extra PS participant is identified"
                    assertRejected "the PS boundary rejects copied policy before projection" policyResult
                    Expect.isTrue
                        ((outputOf policyResult).Contains("forbidden field 'sdkVersion'", StringComparison.Ordinal))
                        "the PS policy mirror field is identified"
                    assertRejected "the PS boundary rejects config/roster identity mismatch" identityResult
                    Expect.isTrue
                        (containsDiagnostic "does not match roster participant 'dotnet'" (outputOf identityResult))
                        $"config identity mismatch is actionable: {outputOf identityResult}"
                    assertRejected "the PS bootstrap projection retains common-runtime enforcement" toolchainResult
                    Expect.isTrue
                        ((outputOf toolchainResult).Contains("participants disagree on canonical 'runtimeVersion'", StringComparison.Ordinal))
                        "common bootstrap runtime mismatch is reported"
                })
    }

let private updateVerifyRejectsUnenrolledComponent =
    testCaseAsync "Update.ps1 -Verify rejects an unenrolled target before Bootstrap" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    writeProjectConfigs root Fixture.canonicalSdk Fixture.canonicalRuntime Fixture.canonicalPlatform currentParticipants
                    writeRoster root currentParticipants None |> ignore

                    let! exitCode, stdout, stderr, _ =
                        runUpdate root [ "-Verify"; "-Component"; "telegram"; "-Root"; root ]
                    let output = stdout + stderr
                    Expect.isTrue (exitCode <> 0) "an unenrolled PS target is rejected"
                    Expect.isTrue (output.Contains("not enrolled", StringComparison.Ordinal)) "PS reports the roster boundary"
                    Expect.isFalse (output.Contains("running toolchain verification", StringComparison.Ordinal)) "Bootstrap does not start for an unenrolled target"
                })
    }

let private cliRejectsUnenrolledComponentAndConfigPathDrift =
    testList
        "compiled CLI enrollment boundary"
        [ testCaseAsync "transaction mode rejects an unenrolled component before artifacts or transport" <| async {
              let runnerPath = ReleaseOrchestration.runnerPath repoRoot
              Expect.isTrue (File.Exists runnerPath) $"compiled runner exists at {runnerPath}"
              let missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
              let contract =
                  let document = JsonObject()
                  document["componentId"] <- JsonValue.Create "telegram"
                  document["version"] <- JsonValue.Create "9.9.9"
                  document["archiveName"] <- JsonValue.Create "v9.9.9.zip"
                  document["assetUri"] <- JsonValue.Create "https://github.com/masterlifting/mcp-store/releases/download/telegram/v9.9.9.zip"
                  let allowlist = JsonArray()
                  allowlist.Add(JsonValue.Create "distribution.json")
                  document["entryAllowlist"] <- allowlist
                  document.ToJsonString()

              let info = ProcessStartInfo(runnerPath)
              info.WorkingDirectory <- repoRoot
              info.UseShellExecute <- false
              info.RedirectStandardOutput <- true
              info.RedirectStandardError <- true
              [ Path.Combine(missing, "distribution.json"); Path.Combine(missing, "v9.9.9.zip"); contract ]
              |> List.iter info.ArgumentList.Add
              [ "GITHUB_TOKEN"; "GITHUB_ENV"; "GITHUB_OUTPUT"; "MCP_RELEASE_DO_UPLOAD"
                "MCP_RELEASE_GITHUB_OWNER"; "MCP_RELEASE_GITHUB_REPOSITORY" ]
              |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

              match! ReleaseTestProcess.capture 15000 info with
              | Error error -> failwith error
              | Ok (exitCode, stdout, _) ->
                  Expect.equal exitCode 1 "unregistered CLI component fails closed"
                  let diagnostic = JsonNode.Parse(stdout.TrimEnd()).AsObject()
                  Expect.equal (diagnostic["status"].GetValue<string>()) "failed" "structured status"
                  Expect.isTrue
                      ((diagnostic["reason"].GetValue<string>()).Contains("unsupported componentId 'telegram'", StringComparison.Ordinal))
                      "roster rejection is actionable"
                  Expect.isFalse (diagnostic.ContainsKey "publishedArchiveSha256") "unsupported identity has no published hash"
          }

          testCaseAsync "orchestrate mode rejects a config path outside the roster before child stages" <| async {
              do!
                  withTemporaryRoot (fun fixtureRoot ->
                      async {
                          let configPath =
                              writeConfig fixtureRoot "dotnet/release/Alternate.fsx" "dotnet" "1.0.4" "Mcp.Dotnet.dll" Fixture.canonicalSdk Fixture.canonicalRuntime Fixture.canonicalPlatform
                          let runnerPath = ReleaseOrchestration.runnerPath repoRoot
                          Expect.isTrue (File.Exists runnerPath) $"compiled runner exists at {runnerPath}"

                          let info = ProcessStartInfo(runnerPath)
                          info.WorkingDirectory <- repoRoot
                          info.UseShellExecute <- false
                          info.RedirectStandardOutput <- true
                          info.RedirectStandardError <- true
                          info.ArgumentList.Add "orchestrate"
                          info.ArgumentList.Add configPath
                          [ "GITHUB_TOKEN"; "GITHUB_ENV"; "GITHUB_OUTPUT"; "MCP_RELEASE_DO_UPLOAD"
                            "MCP_RELEASE_GITHUB_OWNER"; "MCP_RELEASE_GITHUB_REPOSITORY" ]
                          |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

                          match! ReleaseTestProcess.capture 30000 info with
                          | Error error -> failwith error
                          | Ok (exitCode, stdout, _) ->
                              Expect.equal exitCode 4 "config path drift blocks orchestration"
                              let diagnostic = JsonNode.Parse(stdout.TrimEnd()).AsObject()
                              Expect.equal (diagnostic["phase"].GetValue<string>()) "config" "drift is rejected during config validation"
                              Expect.isTrue
                                  ((diagnostic["reason"].GetValue<string>()).Contains("configPath drift", StringComparison.Ordinal))
                                  "the roster path mismatch is reported"
                      })
          } ]

let private expectErrorContaining (label: string) (fragment: string) (result: Result<'value, ReleaseError>) =
    match result with
    | Error error ->
        Expect.isTrue
            ((ReleaseError.message error).Contains(fragment, StringComparison.Ordinal))
            label
    | Ok _ -> Expect.isTrue false $"{label}: expected a typed enrollment failure"

let private contains (fragment: string) (text: string) =
    text.Contains(fragment, StringComparison.Ordinal)

let private checkedInRosterHasTheOnlySupportedParticipants =
    testCaseAsync "the authoritative roster contains only dotnet and workflow" <| async {
        match! ReleaseEnrollment.loadRoster repoRoot with
        | Error error -> failwith (ReleaseError.message error)
        | Ok roster ->
            Expect.equal
                (roster.Entries |> List.map (fun entry -> entry.ComponentId, entry.ConfigPath))
                currentParticipants
                "the checked-in roster is the exact current inventory"

            match ReleaseEnrollment.validateRoster roster with
            | Ok () -> ()
            | Error error -> failwith (ReleaseError.message error)
    }

let private rosterLoaderAndValidationRejectDuplicatesIncompleteMembershipAndPolicyMirrors =
    testCaseAsync "roster loading and validation reject duplicate identities, incomplete membership, and copied policy" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    let duplicate = [ "dotnet", "dotnet/release/Config.fsx"; "dotnet", "dotnet/release/Config.fsx" ]
                    writeRoster root duplicate None |> ignore

                    match! ReleaseEnrollment.loadRoster root with
                    | Error error ->
                        Expect.isTrue
                            ((ReleaseError.message error).Contains("duplicate componentId", StringComparison.Ordinal))
                            "duplicate identity is rejected"
                    | Ok _ -> Expect.isTrue false "duplicate identity must not load"

                    writeRoster root [ "dotnet", "dotnet/release/Config.fsx" ] None |> ignore
                    match! ReleaseEnrollment.loadRoster root with
                    | Error error -> failwith (ReleaseError.message error)
                    | Ok roster ->
                        expectErrorContaining
                            "the composed roster validation rejects an omitted required participant"
                            "missing required componentId(s): workflow"
                            (ReleaseEnrollment.validateRoster roster)

                    writeRoster root (currentParticipants @ [ "telegram", "telegram/release/Config.fsx" ]) None |> ignore
                    match! ReleaseEnrollment.loadRoster root with
                    | Error error ->
                        Expect.isTrue
                            ((ReleaseError.message error).Contains("unsupported componentId 'telegram'", StringComparison.Ordinal))
                            "an additional participant is rejected"
                    | Ok _ -> Expect.isTrue false "an unsupported inventory member must not load"

                    let forbiddenFields =
                        [ "version"
                          "sdkVersion"
                          "runtimeVersion"
                          "platform"
                          "archiveName"
                          "assetUri"
                          "entryAllowlist"
                          "publishedFiles"
                          "entryDll" ]

                    for field in forbiddenFields do
                        writeRoster root [ "dotnet", "dotnet/release/Config.fsx" ] (Some (field, "copied-policy")) |> ignore

                        match! ReleaseEnrollment.loadRoster root with
                        | Error error ->
                            Expect.isTrue
                                ((ReleaseError.message error).Contains($"forbidden field '{field}'", StringComparison.Ordinal))
                                $"policy field {field} is rejected"
                        | Ok _ -> Expect.isTrue false $"policy field {field} must not load"

                    writeRosterText root "{ \"participants\": [] }" |> ignore

                    match! ReleaseEnrollment.loadRoster root with
                    | Error error -> failwith (ReleaseError.message error)
                    | Ok roster ->
                        expectErrorContaining "empty inventory is rejected by roster validation" "empty" (ReleaseEnrollment.validateRoster roster)
                })
    }

let private projectionValidationRejectsOmissionsExtrasAndPathDrift =
    testCase "the production projection guard rejects omitted, extra, and drifted entries" <| fun _ ->
        let roster =
            { Entries =
                [ { ComponentId = "dotnet"; ConfigPath = "dotnet/release/Config.fsx" }
                  { ComponentId = "workflow"; ConfigPath = "workflow/release/Config.fsx" } ]
              SourcePath = "fixture/Participants.json" }

        let dotnetOnly = { roster with Entries = [ roster.Entries.Head ] }

        expectErrorContaining
            "direct validation rejects an omitted required inventory member"
            "missing required componentId(s): workflow"
            (ReleaseEnrollment.validateRoster dotnetOnly)

        let expanded =
            { roster with
                Entries = roster.Entries @ [ { ComponentId = "telegram"; ConfigPath = "telegram/release/Config.fsx" } ] }

        expectErrorContaining
            "direct validation rejects an unsupported inventory member"
            "unsupported componentId(s): telegram"
            (ReleaseEnrollment.validateRoster expanded)

        expectErrorContaining
            "an omitted workflow projection is rejected"
            "unsupported componentId 'workflow'"
            (ReleaseEnrollment.validateProjectionAgainstRoster "workflow" "workflow/release/Config.fsx" "fixture" dotnetOnly)

        expectErrorContaining
            "an extra Telegram projection is rejected"
            "unsupported componentId 'telegram'"
            (ReleaseEnrollment.validateProjectionAgainstRoster "telegram" "telegram/release/Config.fsx" "fixture" roster)

        expectErrorContaining
            "a component id paired with another config path is rejected"
            "configPath drift"
            (ReleaseEnrollment.validateProjectionAgainstRoster "dotnet" "workflow/release/Config.fsx" "fixture" roster)

        expectErrorContaining
            "an unregistered transaction component is rejected"
            "unsupported componentId 'telegram'"
            (ReleaseEnrollment.validateComponentIdInRoster "telegram" roster)

let private genericIdentityAndMetadataUseProducerOwnedToolchains =
    testCase "generic asset and metadata identity accepts independent producer expectations" <| fun _ ->
        let producers =
            [ "dotnet", "1.2.3", "Mcp.Dotnet.dll", "dotnet-sdk", "dotnet-runtime", "win-x64"
              "workflow", "4.5.6", "Mcp.Workflow.dll", "workflow-sdk", "workflow-runtime", "linux-x64" ]

        for componentId, version, entryDll, sdk, runtime, platform in producers do
            let archiveName = $"v{version}.zip"
            let assetUri = $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/{archiveName}"
            let contract: ComponentContract =
                { ComponentId = componentId
                  Version = version
                  ArchiveName = archiveName
                  EntryAllowlist = [] }
            let dto: CandidateDto =
                { Contract = contract
                  ManifestPath = "unused-manifest.json"
                  ArchivePath = "unused-archive.zip"
                  EvaluatedHead = String.replicate 40 "a"
                  AssetUri = assetUri
                  MetadataAssetName = ReleaseMetadata.metadataAssetName componentId version
                  MetadataLocalPath = "unused-metadata.json"
                  ProducerSdk = Some sdk
                  ProducerRuntime = Some runtime
                  ProducerPlatform = Some platform
                  RepositoryOwner = "masterlifting"
                  RepositoryName = "mcp-store" }

            Expect.equal (ReleaseEvaluate.validateCanonicalAssetUri dto contract) (Ok ()) $"{componentId} uses its own canonical identity"

            let projection =
                ReleaseMetadata.makeProjection componentId version assetUri archiveName (String.replicate 64 "b") (String.replicate 64 "c") (String.replicate 40 "d") (Some sdk) (Some runtime) (Some platform)

            match ReleaseMetadata.parse (ReleaseMetadata.render projection) with
            | Error error -> failwith (ReleaseError.message error)
            | Ok parsed ->
                Expect.equal parsed.ProducerSdk (Some sdk) $"{componentId} SDK remains producer-owned"
                Expect.equal parsed.ProducerRuntime (Some runtime) $"{componentId} runtime remains producer-owned"
                Expect.equal parsed.ProducerPlatform (Some platform) $"{componentId} platform remains producer-owned"

let private writeBootstrapSources root =
    let infrastructure = Path.Combine(root, "infrastructure")
    let release = Path.Combine(infrastructure, "release")
    Directory.CreateDirectory release |> ignore
    File.Copy(Path.Combine(repoRoot, "infrastructure", "Provenance.fs"), Path.Combine(infrastructure, "Provenance.fs"))
    File.Copy(Path.Combine(repoRoot, "infrastructure", "release", "Enrollment.fs"), Path.Combine(release, "Enrollment.fs"))
    File.Copy(Path.Combine(repoRoot, "infrastructure", "release", "Bootstrap.fsx"), Path.Combine(release, "Bootstrap.fsx"))
    Path.Combine(release, "Bootstrap.fsx")

let private runBootstrap root =
    async {
        let info = ProcessStartInfo("dotnet")
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        [ "fsi"; "--nologo"; Path.Combine(root, "infrastructure", "release", "Bootstrap.fsx") ]
        |> List.iter info.ArgumentList.Add
        [ "GITHUB_ENV"; "GITHUB_OUTPUT"; "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]
        |> List.iter (fun name -> info.EnvironmentVariables.Remove name |> ignore)

        match! ReleaseTestProcess.capture 120000 info with
        | Ok result -> return result
        | Error error -> return failwith error
    }

let private bootstrapRejectsConfigIdentityAndRuntimeDrift =
    testList
        "Bootstrap roster boundary"
        [ testCaseAsync "the real Bootstrap script rejects a roster/config id mismatch" <| async {
              do!
                  withTemporaryRoot (fun root ->
                      async {
                           writeBootstrapSources root |> ignore
                           writeRoster root currentParticipants None |> ignore
                           writeConfig root "dotnet/release/Config.fsx" "workflow" "1.0.0" "Mcp.Workflow.dll" Fixture.canonicalSdk Fixture.canonicalRuntime Fixture.canonicalPlatform |> ignore
                           writeConfig root "workflow/release/Config.fsx" "workflow" "1.0.0" "Mcp.Workflow.dll" Fixture.canonicalSdk Fixture.canonicalRuntime Fixture.canonicalPlatform |> ignore

                           let! exitCode, stdout, stderr = runBootstrap root
                           let output = stdout + stderr
                           Expect.isTrue (exitCode <> 0) "Bootstrap rejects the config identity mismatch"
                           Expect.isTrue
                               (containsDiagnostic "componentId does not match the release pin: expected dotnet, got workflow" output)
                               $"Bootstrap identifies the mismatched config id: {output}"
                      })
          }
          testCaseAsync "Bootstrap rejects copied producer policy in the roster" <| async {
              do!
                  withTemporaryRoot (fun root ->
                      async {
                          writeBootstrapSources root |> ignore
                          writeRoster root [ "dotnet", "dotnet/release/Config.fsx" ] (Some ("sdkVersion", Fixture.canonicalSdk)) |> ignore

                          let! exitCode, stdout, stderr = runBootstrap root
                          let output = stdout + stderr
                          Expect.isTrue (exitCode <> 0) "Bootstrap rejects a policy-mirror field before config loading"
                          Expect.isTrue (contains "forbidden field 'sdkVersion'" output) "Bootstrap names the forbidden roster field"
                      })
          }
          testCaseAsync "Bootstrap retains the common runtime restriction" <| async {
              do!
                  withTemporaryRoot (fun root ->
                      async {
                          writeBootstrapSources root |> ignore
                          writeRoster root currentParticipants None |> ignore
                          writeConfig root "dotnet/release/Config.fsx" "dotnet" "1.0.0" "Mcp.Dotnet.dll" Fixture.canonicalSdk Fixture.canonicalRuntime Fixture.canonicalPlatform |> ignore
                          writeConfig root "workflow/release/Config.fsx" "workflow" "1.0.0" "Mcp.Workflow.dll" Fixture.canonicalSdk "runtime-drift" Fixture.canonicalPlatform |> ignore

                          let! exitCode, stdout, stderr = runBootstrap root
                          let output = stdout + stderr
                          Expect.isTrue (exitCode <> 0) "Bootstrap rejects cross-producer runtime drift"
                          Expect.isTrue (contains "one canonical value" output) "Bootstrap reports the common runtime constraint"
                      })
          } ]

let private currentRosterAndProjectionTests =
    testList
        "compiled enrollment API"
        [ checkedInRosterHasTheOnlySupportedParticipants
          rosterLoaderAndValidationRejectDuplicatesIncompleteMembershipAndPolicyMirrors
          projectionValidationRejectsOmissionsExtrasAndPathDrift
          genericIdentityAndMetadataUseProducerOwnedToolchains ]

let tests =
    testList
        "enrollment"
        [ currentRosterAndProjectionTests
          updateProjectUsesRosterProjectionAndRejectsDrift
          updateVerifyRejectsUnenrolledComponent
          bootstrapRejectsConfigIdentityAndRuntimeDrift
          cliRejectsUnenrolledComponentAndConfigPathDrift ]
