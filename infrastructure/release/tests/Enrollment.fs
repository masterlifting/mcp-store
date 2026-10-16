module Release.Tests.Enrollment

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Expecto
open BuildProvenance
open DistributionContract
open ReleaseCompare
open ReleaseEvaluate
open ReleaseMetadata
open ReleaseEnrollment
open ReleaseTestProcess

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private currentParticipants =
    [ "dotnet", "dotnet/release/Config.json"
      "workflow", "workflow/release/Config.json" ]

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

let private configText (componentId: string) (version: string) (entryDll: string) (sdk: string) (runtime: string) (platform: string) =
    let archiveName = $"v{version}.zip"
    let assetUri = $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/{archiveName}"
    // WHY: the production policy reader consumes strict JSON over the canonical 9 fields;
    //   fixture Config.json files share the same shape so the roster toolchain checks
    //   exercise the same projection path as the canonical configs.
    let node = System.Text.Json.Nodes.JsonObject()
    node["componentId"] <- System.Text.Json.Nodes.JsonValue.Create componentId
    node["version"] <- System.Text.Json.Nodes.JsonValue.Create version
    node["entryDll"] <- System.Text.Json.Nodes.JsonValue.Create entryDll
    node["sdkVersion"] <- System.Text.Json.Nodes.JsonValue.Create sdk
    node["runtimeVersion"] <- System.Text.Json.Nodes.JsonValue.Create runtime
    node["platform"] <- System.Text.Json.Nodes.JsonValue.Create platform
    node["archiveName"] <- System.Text.Json.Nodes.JsonValue.Create archiveName
    node["assetUri"] <- System.Text.Json.Nodes.JsonValue.Create assetUri
    let files = System.Text.Json.Nodes.JsonArray()
    files.Add(System.Text.Json.Nodes.JsonValue.Create entryDll) |> ignore
    node["publishedFiles"] <- files
    let launchArguments = System.Text.Json.Nodes.JsonArray()
    let argument = System.Text.Json.Nodes.JsonObject()
    argument["flag"] <- System.Text.Json.Nodes.JsonValue.Create "--fixture-input"
    argument["input"] <- System.Text.Json.Nodes.JsonValue.Create "fixtureInput"
    argument["type"] <- System.Text.Json.Nodes.JsonValue.Create "absolute-file"
    launchArguments.Add argument
    node["launchArguments"] <- launchArguments
    node.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))

let private writeConfig root configPath componentId version entryDll sdk runtime platform =
    let path = Path.Combine(root, configPath)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, configText componentId version entryDll sdk runtime platform)
    path

let private projectConfigEntries =
    [ "dotnet", "dotnet/release/Config.json", "Mcp.Dotnet.dll"
      "workflow", "workflow/release/Config.json", "Mcp.Workflow.dll" ]

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

                    let duplicate = [ "dotnet", "dotnet/release/Config.json"; "dotnet", "dotnet/release/Config.json" ]
                    writeRoster root duplicate None |> ignore
                    let! duplicateResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root [ "dotnet", "dotnet/release/Config.json" ] None |> ignore
                    let! omittedResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root (currentParticipants @ [ "telegram", "telegram/release/Config.json" ]) None |> ignore
                    let! extraResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root currentParticipants (Some ("sdkVersion", "copied-policy")) |> ignore
                    let! policyResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root currentParticipants None |> ignore
                    writeConfig root "dotnet/release/Config.json" "workflow" "1.0.0" "Mcp.Workflow.dll" "fixture-sdk" "fixture-runtime" "fixture-platform" |> ignore
                    writeConfig root "workflow/release/Config.json" "workflow" "1.0.0" "Mcp.Workflow.dll" "fixture-sdk" "fixture-runtime" "fixture-platform" |> ignore
                    let! identityResult = runUpdate root [ "-Project"; "-Root"; root ]

                    writeRoster root currentParticipants None |> ignore
                    writeConfig root "dotnet/release/Config.json" "dotnet" "1.0.0" "Mcp.Dotnet.dll" "fixture-sdk" "fixture-runtime" "fixture-platform" |> ignore
                    writeConfig root "workflow/release/Config.json" "workflow" "1.0.0" "Mcp.Workflow.dll" "fixture-sdk" "runtime-drift" "fixture-platform" |> ignore
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

let private prepareRejectsUnregisteredConfigBeforeAnyChild =
    testCaseAsync "prepare rejects an unregistered executable config before any child starts" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    writeRoster root currentParticipants None |> ignore
                    let configPath =
                        writeConfig root "dotnet/release/Alternate.fsx" "dotnet" "1.0.4" "Mcp.Dotnet.dll" Fixture.canonicalSdk Fixture.canonicalRuntime Fixture.canonicalPlatform
                    let marker = Path.Combine(root, "config-executed.txt")
                    File.AppendAllText(configPath, $"{Environment.NewLine}do System.IO.File.WriteAllText(@\"{marker}\", \"ran\")")
                    let candidateDirectory = Path.Combine(root, "candidate-output")

                    let! exitCode = ReleaseOrchestration.prepare root "dotnet/release/Alternate.fsx" candidateDirectory

                    Expect.equal exitCode 4 "an unregistered config fails closed"
                    Expect.isFalse (File.Exists marker) "path authorization runs before Config.json evaluation"
                    Expect.isFalse (Directory.Exists candidateDirectory) "rejected config writes no candidate output"
                })
    }

let private expectErrorContaining (label: string) (fragment: string) (result: Result<'value, ReleaseError>) =
    match result with
    | Error error ->
        Expect.isTrue
            ((ReleaseError.message error).Contains(fragment, StringComparison.Ordinal))
            label
    | Ok _ -> Expect.isTrue false $"{label}: expected a typed enrollment failure"

let private canonicalConfigPathAuthorizationIsExactAndLexical =
    testCaseAsync "config path authorization accepts canonical lexical forms and rejects drift" <| async {
        match! ReleaseEnrollment.loadRoster repoRoot with
        | Error error -> failwith (ReleaseError.message error)
        | Ok roster ->
            for _, configPath in currentParticipants do
                let lexical = Path.Combine(".", configPath).Replace('\\', '/')
                Expect.isOk
                    (ReleaseEnrollment.validateConfigPathAgainstRoster lexical repoRoot roster)
                    $"the lexical equivalent of {configPath} is authorized"
                Expect.isOk
                    (ReleaseEnrollment.validateConfigPathAgainstRoster (Path.GetFullPath(Path.Combine(repoRoot, configPath))) repoRoot roster)
                    $"the absolute canonical form of {configPath} is authorized"

            let drifted = Path.Combine(repoRoot, "dotnet", "release", "Alternate.fsx")
            expectErrorContaining
                "a different path is rejected even when it is inside the repository"
                "not registered"
                (ReleaseEnrollment.validateConfigPathAgainstRoster drifted repoRoot roster)
    }

let private rosterLoaderRejectsCanonicalConfigPathCollisions =
    testCaseAsync "roster loading rejects different entries with the same canonical config path" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    writeRoster
                        root
                        [ "dotnet", "dotnet/release/Config.json"
                          "workflow", "./dotnet/release/Config.json" ]
                        None
                    |> ignore
                    match! ReleaseEnrollment.loadRoster root with
                    | Error error ->
                        Expect.isTrue
                            ((ReleaseError.message error).Contains("same canonical configPath", StringComparison.Ordinal))
                            "the canonical collision is reported"
                    | Ok _ -> Expect.isTrue false "canonical config path collisions must fail closed"
                })
    }

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
                    let duplicate = [ "dotnet", "dotnet/release/Config.json"; "dotnet", "dotnet/release/Config.json" ]
                    writeRoster root duplicate None |> ignore

                    match! ReleaseEnrollment.loadRoster root with
                    | Error error ->
                        Expect.isTrue
                            ((ReleaseError.message error).Contains("duplicate componentId", StringComparison.Ordinal))
                            "duplicate identity is rejected"
                    | Ok _ -> Expect.isTrue false "duplicate identity must not load"

                    writeRoster root [ "dotnet", "dotnet/release/Config.json" ] None |> ignore
                    match! ReleaseEnrollment.loadRoster root with
                    | Error error -> failwith (ReleaseError.message error)
                    | Ok roster ->
                        expectErrorContaining
                            "the composed roster validation rejects an omitted required participant"
                            "missing required componentId(s): workflow"
                            (ReleaseEnrollment.validateRoster roster)

                    writeRoster root (currentParticipants @ [ "telegram", "telegram/release/Config.json" ]) None |> ignore
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
                          "entryDll"
                          "launchArguments" ]

                    for field in forbiddenFields do
                        writeRoster root [ "dotnet", "dotnet/release/Config.json" ] (Some (field, "copied-policy")) |> ignore

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
                [ { ComponentId = "dotnet"; ConfigPath = "dotnet/release/Config.json" }
                  { ComponentId = "workflow"; ConfigPath = "workflow/release/Config.json" } ]
              SourcePath = "fixture/Participants.json" }

        let dotnetOnly = { roster with Entries = [ roster.Entries.Head ] }

        expectErrorContaining
            "direct validation rejects an omitted required inventory member"
            "missing required componentId(s): workflow"
            (ReleaseEnrollment.validateRoster dotnetOnly)

        let expanded =
            { roster with
                Entries = roster.Entries @ [ { ComponentId = "telegram"; ConfigPath = "telegram/release/Config.json" } ] }

        expectErrorContaining
            "direct validation rejects an unsupported inventory member"
            "unsupported componentId(s): telegram"
            (ReleaseEnrollment.validateRoster expanded)

        expectErrorContaining
            "an omitted workflow projection is rejected"
            "unsupported componentId 'workflow'"
            (ReleaseEnrollment.validateProjectionAgainstRoster "workflow" "workflow/release/Config.json" "fixture" dotnetOnly)

        expectErrorContaining
            "an extra Telegram projection is rejected"
            "unsupported componentId 'telegram'"
            (ReleaseEnrollment.validateProjectionAgainstRoster "telegram" "telegram/release/Config.json" "fixture" roster)

        expectErrorContaining
            "a component id paired with another config path is rejected"
            "configPath drift"
            (ReleaseEnrollment.validateProjectionAgainstRoster "dotnet" "workflow/release/Config.json" "fixture" roster)

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
                  SourceRepository = "https://github.com/masterlifting/mcp-store"
                  Producer =
                    { Sdk = sdk
                      Runtime = runtime
                      Platform = platform }
                  Launch =
                    { Kind = launchKindDotnetExec
                      Entrypoint = entryDll
                      Arguments = [ { Flag = "--fixture-input"; Input = "fixtureInput"; Type = AbsoluteFile } ] }
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
          prepareRejectsUnregisteredConfigBeforeAnyChild
          canonicalConfigPathAuthorizationIsExactAndLexical
          rosterLoaderRejectsCanonicalConfigPathCollisions ]
