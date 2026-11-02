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
open DistributionContract

type private StageFailure =
    | BuildFailure
    | PinsFailure
    | DotnetDistributionFailure
    | WorkflowDistributionFailure
    | WorkflowPackagedFailure

let private componentIds = [ "dotnet"; "workflow" ]

// The canonical root helper the coordination gate is routed through lives in the repository root.
let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

// The fixture child must resolve the same .NET SDK as the repository root, because the isolation
// contract depends on the runtime's known-folder resolution: SDKs before .NET 10 ignore the
// LOCALAPPDATA redirection the root helper performs, so an unpinned fixture would fail for a
// reason that has nothing to do with the coordination gate.
let private repositorySdkVersion =
    lazy
        (let info = ProcessStartInfo("dotnet")
         info.WorkingDirectory <- repoRoot
         info.UseShellExecute <- false
         info.RedirectStandardOutput <- true
         info.RedirectStandardError <- true
         info.ArgumentList.Add("--version")
         use child = new Process(StartInfo = info)

         if not (child.Start()) then
             failwith "the repository SDK version could not be resolved"

         let output = child.StandardOutput.ReadToEnd()
         child.WaitForExit()
         let version = output.Trim()

         if String.IsNullOrWhiteSpace version then
             failwith "the repository SDK version could not be resolved"

         version)

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

// The fixture root receives the repository's SDK pin and a verbatim copy of the canonical root
// helper, so the coordination gate exercises the trusted redirection instead of a second,
// test-owned implementation of it.
let private writeFixtureRuntime (root: string) =
    let sdk = JsonObject()
    sdk["version"] <- JsonValue.Create repositorySdkVersion.Value
    let document = JsonObject()
    document["sdk"] <- sdk
    write root "global.json" (document.ToJsonString()) |> ignore

    let source = Path.Combine(repoRoot, "Run.ps1")

    if not (File.Exists source) then
        failwithf "the canonical root helper is missing: %s" source

    let target = Path.Combine(root, "Run.ps1")
    File.Copy(source, target, true)
    target

// WHY: the strict Config.json reader requires the ordered launch descriptors; the
//   fixture supplies one generic flag/input pair so the projection path matches the
//   canonical Configs without restating producer identity.
let private policyJson componentId =
    let productName = if componentId = "dotnet" then "Dotnet" else "Workflow"
    let entryDll = $"Mcp.{productName}.dll"
    let node = JsonObject()
    node["componentId"] <- JsonValue.Create componentId
    node["version"] <- JsonValue.Create "7.8.9"
    node["entryDll"] <- JsonValue.Create entryDll
    node["sdkVersion"] <- JsonValue.Create "fixture-sdk"
    node["runtimeVersion"] <- JsonValue.Create "fixture-runtime"
    node["platform"] <- JsonValue.Create "fixture-platform"
    node["archiveName"] <- JsonValue.Create "v7.8.9.zip"
    node["assetUri"] <- JsonValue.Create $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/v7.8.9.zip"
    let files = JsonArray()
    files.Add(JsonValue.Create "FSharp.Core.dll") |> ignore
    files.Add(JsonValue.Create $"Mcp.{productName}.deps.json") |> ignore
    files.Add(JsonValue.Create entryDll) |> ignore
    files.Add(JsonValue.Create $"Mcp.{productName}.runtimeconfig.json") |> ignore
    node["publishedFiles"] <- files
    let launchArguments = JsonArray()
    let argument = JsonObject()
    argument["flag"] <- JsonValue.Create "--fixture-input"
    argument["input"] <- JsonValue.Create "fixtureInput"
    argument["type"] <- JsonValue.Create "absolute-file"
    launchArguments.Add argument
    node["launchArguments"] <- launchArguments
    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

// WHY: test fixture generation now goes through the strict JSON policy template so the same
//   canonical 9-field projection path is exercised as the canonical Config.json files.
let private policySource componentId = policyJson componentId

let private parsePolicy configPath =
    ReleasePolicy.parseBytes configPath (File.ReadAllBytes configPath)
    |> function
        | Ok policy -> policy
        | Error error -> failwithf "fixture policy failed to parse: %s" (SharedFailure.ReleaseError.message error)

let private writeProducerArchive (root: string) (policy: ReleasePolicy.PolicyProjection) (revision: string) =
    let payloads = Fixture.policyPayloads policy "fixture"
    let manifestText = Fixture.policyManifestFor policy revision payloads
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    let archivePath = Path.Combine(root, policy.ComponentId, "dist", policy.ArchiveName)
    let manifestPath = Path.Combine(root, policy.ComponentId, "dist", policy.ComponentId, DistributionContract.manifestEntryName)
    Directory.CreateDirectory(Path.GetDirectoryName archivePath) |> ignore
    Directory.CreateDirectory(Path.GetDirectoryName manifestPath) |> ignore
    File.WriteAllBytes(manifestPath, manifestBytes)

    use stream = File.Create archivePath
    use archive = new ZipArchive(stream, ZipArchiveMode.Create)

    for name, bytes in payloads @ [ DistributionContract.manifestEntryName, manifestBytes ] do
        let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        use entryStream = entry.Open()
        entryStream.Write(bytes, 0, bytes.Length)

// WHY: the ordinary producer gates must stay unredirected, so every stage records the isolation
//   marker it observed; the coordination gate is the only gate that may see a redirected child.
let private stageScript (stage: string) (fails: bool) =
    [
        "open System"
        "open System.IO"
        ""
        "let observedIsolation ="
        "    match Environment.GetEnvironmentVariable \"MCP_WORKFLOW_TEST_EXPECTED_APP_DATA\" with"
        "    | null -> \"<unset>\""
        "    | value -> value"
        ""
        sprintf "File.WriteAllText(\"stage-%s-isolation.txt\", observedIsolation)" stage
        sprintf "File.AppendAllText(\"prepare-stages.log\", \"%s;\")" stage
        ""
        // WHY: an untyped final failure becomes the script's own value and is rejected by the
        //   value restriction before the stage can write anything, so the failure is typed as unit.
        (if fails then "(failwith \"controlled stage failure\" : unit)" else "()")
        ""
    ]
    |> String.concat Environment.NewLine

// The coordination gate is the only producer gate that starts a fresh child environment, so the
// fixture script proves the redirection the canonical root helper performs: the expected
// per-account directory exists, the resolved one is exactly that directory and never the real
// one, the redirected home and temporary directories share one sandbox root, and the per-account
// key path stays inside the sandbox without ever reaching the real key. The script reads no key
// material and writes only its own report and stage marker.
let private coordinationScriptText =
    """
open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

let samePath (left: string) (right: string) =
    String.Equals(
        (Path.GetFullPath left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        (Path.GetFullPath right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase)

let contained (parent: string) (child: string) =
    let parentPath = (Path.GetFullPath parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    let childPath = (Path.GetFullPath child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    childPath.StartsWith(parentPath + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

let ancestors (path: string) =
    let full = (Path.GetFullPath path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    let segments = full.Split([| Path.DirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
    let mutable current = Path.GetPathRoot full

    [ for segment in segments do
          current <- Path.Combine(current, segment)
          yield current ]

let commonAncestor (paths: string list) =
    let narrow (left: string list) (right: string list) =
        let rec loop (l: string list) (r: string list) =
            match l, r with
            | a :: at, b :: bt when samePath a b -> a :: loop at bt
            | _ -> []

        loop left right

    match paths |> List.map ancestors with
    | [] -> ""
    | first :: rest ->
        let narrowed = rest |> List.fold narrow first

        match List.rev narrowed with
        | deepest :: _ -> deepest
        | [] -> ""

let expectedRaw = Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_EXPECTED_APP_DATA"

if String.IsNullOrWhiteSpace expectedRaw then
    failwith "the coordination gate requires an isolated child: MCP_WORKFLOW_TEST_EXPECTED_APP_DATA is missing"

let expected = Path.GetFullPath expectedRaw
let resolved = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

if String.IsNullOrWhiteSpace resolved then
    failwith "the coordination gate could not resolve a per-account application data directory"

if not (samePath resolved expected) then
    failwith (sprintf "the resolved application data directory '%s' is not the isolated test root '%s'" resolved expected)

let realRaw = Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_REAL_APP_DATA"

let differsFromReal =
    if String.IsNullOrWhiteSpace realRaw then
        true
    else
        not (samePath resolved (Path.GetFullPath realRaw))

if not differsFromReal then
    failwith "the isolated guard resolved the real per-account application data directory"

let home = Environment.GetEnvironmentVariable "HOME"
let userProfile = Environment.GetEnvironmentVariable "USERPROFILE"
let temp = Environment.GetEnvironmentVariable "TEMP"
let tmp = Environment.GetEnvironmentVariable "TMP"
let cliHome = Environment.GetEnvironmentVariable "DOTNET_CLI_HOME"
let redirected = [ home; userProfile; temp; tmp; cliHome ]

if redirected |> List.exists (fun value -> String.IsNullOrWhiteSpace value) then
    failwith "the coordination gate requires redirected home and temporary directories"

let sandboxRoot = commonAncestor (expected :: redirected)

if String.IsNullOrWhiteSpace sandboxRoot then
    failwith "the coordination gate could not resolve a common sandbox root"

if not (contained sandboxRoot resolved) then
    failwith "the coordination gate resolved a per-account application data directory outside its sandbox"

let isolatedKeyPath = Path.Combine(resolved, "mcp-store", "workflow")

let realKeyPath =
    if String.IsNullOrWhiteSpace realRaw then
        Path.Combine(resolved, "mcp-store", "workflow")
    else
        Path.Combine(Path.GetFullPath realRaw, "mcp-store", "workflow")

if not (contained sandboxRoot isolatedKeyPath) then
    failwith "the coordination gate resolved a per-account key path outside its sandbox"

if samePath isolatedKeyPath realKeyPath then
    failwith "the coordination gate resolved the real per-account key path"

if contained sandboxRoot realKeyPath then
    failwith "the coordination gate sandbox contains the real per-account key path"

let report = JsonObject()
report["expectedAppDataPresent"] <- JsonValue.Create true
report["resolvedEqualsExpected"] <- JsonValue.Create(samePath resolved expected)
report["resolvedDiffersFromReal"] <- JsonValue.Create differsFromReal
report["redirectedDirectoriesShareOneSandboxRoot"] <- JsonValue.Create(redirected |> List.forall (fun value -> contained sandboxRoot value))
report["isolatedKeyPathUnderSandboxRoot"] <- JsonValue.Create(contained sandboxRoot isolatedKeyPath)
report["isolatedKeyPathDiffersFromRealKeyPath"] <- JsonValue.Create(not (samePath isolatedKeyPath realKeyPath))
report["sandboxRoot"] <- JsonValue.Create sandboxRoot
File.WriteAllText("coordination-isolation.json", report.ToJsonString())
File.AppendAllText("prepare-stages.log", "packaged;")
CONTROLLED_FAILURE
"""

let private coordinationScript (fails: bool) =
    let controlledFailure =
        if fails then
            // WHY: an untyped final failure becomes the script's own value and is rejected by the
            //   value restriction before the gate can report, so the failure is typed as unit.
            "(failwith \"controlled packaged coordination failure\" : unit)"
        else
            "()"

    coordinationScriptText.Replace("CONTROLLED_FAILURE", controlledFailure)

// The ordinary producer gates inherit the ambient environment, so their isolation marker records
// whatever the host already carried; only the coordination gate receives a redirected child.
let private ambientIsolationMarker =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_EXPECTED_APP_DATA" with
    | null -> "<unset>"
    | value -> value

// The coordination gate writes one flat JSON report, so the fixture reads that report directly
// instead of introducing a general parser.
let private readCoordinationReport (root: string) =
    let path = Path.Combine(root, "coordination-isolation.json")

    if not (File.Exists path) then
        failwithf "the coordination gate wrote no isolation report: %s" path

    JsonNode.Parse(File.ReadAllText path).AsObject()

// A typed failure that is not a child process failure is reported twice: the producer-gate runner
// names the failing gate and the prepare entry point names its own phase. Only the first document
// carries the gate phase, so the fixture reads that bounded document.
let private firstDiagnostic (output: string) =
    let start = output.IndexOf '{'

    if start < 0 then
        failwithf "the prepare output carries no JSON diagnostic: %s" output

    let finish = output.IndexOf("\n}", start)

    if finish < 0 then
        failwithf "the prepare output carries no complete JSON diagnostic: %s" output

    JsonNode.Parse(output.Substring(start, finish - start + 2)).AsObject()

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

let private runGitHead root =
    async {
        let info = ProcessStartInfo("git")
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        [ "rev-parse"; "HEAD" ] |> List.iter info.ArgumentList.Add
        match! capture 30000 info with
        | Ok (0, stdout, _) -> return stdout.Trim()
        | Ok (code, stdout, stderr) -> return failwithf "git rev-parse HEAD failed (%d): %s %s" code stdout stderr
        | Error error -> return failwithf "git rev-parse HEAD could not run: %s" error
    }

let private writeFixture (root: string) (failure: StageFailure option) =
    async {
        let roster = JsonObject()
        let participants = JsonArray()
        for componentId in componentIds do
            let item = JsonObject()
            item["componentId"] <- JsonValue.Create componentId
            item["configPath"] <- JsonValue.Create $"{componentId}/release/Config.json"
            participants.Add item
        roster["participants"] <- participants
        write root "infrastructure/release/Participants.json" (roster.ToJsonString()) |> ignore
        writeFixtureRuntime root |> ignore

        for componentId in componentIds do
            let selectedProducer, selectedStage =
                failure
                |> Option.map (stageFailure componentId)
                |> Option.defaultValue ("", "")

            write root $"{componentId}/release/Config.json" (policySource componentId) |> ignore
            write root $"{componentId}/release/Build.fsx" (stageScript "build" (scriptFails selectedProducer selectedStage componentId "build")) |> ignore
            write root $"{componentId}/release/Pin.fsx" (stageScript "pins" (scriptFails selectedProducer selectedStage componentId "pins")) |> ignore
            write root $"{componentId}/tests/distribution/Run.fsx" (stageScript "distribution" (scriptFails selectedProducer selectedStage componentId "distribution")) |> ignore

            if componentId = "workflow" then
                // The packaged gate is the coordination gate: it runs through the canonical root
                // helper, so the fixture proves the isolated child instead of an ordinary stage.
                write
                    root
                    "workflow/tests/coordination/Run.fsx"
                    (coordinationScript (scriptFails selectedProducer selectedStage componentId "packaged"))
                |> ignore

        do! runGit root [ "init"; "--quiet" ]
        do! runGit root [ "add"; "-A" ]
        do!
            runGit
                root
                [ "-c"; "user.name=ReleaseTests"; "-c"; "user.email=release-tests@example.invalid"; "commit"; "--quiet"; "-m"; "fixture"; "--allow-empty" ]

        // The packaged manifest must carry the committed HEAD, so the data-only dist
        // output is generated after the source commit, mirroring production order.
        let! head = runGitHead root

        for componentId in componentIds do
            let configPath = Path.Combine(root, componentId, "release", "Config.json")
            writeProducerArchive root (parsePolicy configPath) head
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

// The coordination gate must refuse to run outside the canonical wrapper. The fixture proves that
// refusal with a direct child that carries no isolation contract; the gate reads no key material.
let private runCoordinationScriptDirectly (root: string) =
    async {
        let scriptPath = Path.Combine(root, "workflow", "tests", "coordination", "Run.fsx")
        let info = ProcessStartInfo("dotnet")
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        [ "fsi"; "--exec"; "--quiet"; scriptPath ] |> List.iter info.ArgumentList.Add
        info.EnvironmentVariables.Remove "MCP_WORKFLOW_TEST_EXPECTED_APP_DATA" |> ignore
        info.EnvironmentVariables.Remove "MCP_WORKFLOW_TEST_REAL_APP_DATA" |> ignore
        return! capture 180000 info
    }

let private prepareWritesPortableCandidateAndUsesExplicitProducerGates =
    testCaseAsync "prepare writes portable candidates after each enrolled producer's explicit test gates" <| async {
        for componentId in componentIds do
            do!
                withTemporaryRoot (fun root ->
                    async {
                        do! writeFixture root None
                        let outputPath = Path.Combine(root, "candidate-output")
                        let! exitCode, output = runPrepare root $"{componentId}/release/Config.json" outputPath
                        Expect.equal exitCode 0 $"{componentId} preparation succeeds: {output}"
                        let summary = JsonNode.Parse(output.Trim()).AsObject()
                        Expect.equal (summary["status"].GetValue<string>()) "prepared" $"{componentId} reports preparation only"
                        Expect.isTrue (File.Exists(Path.Combine(outputPath, "distribution.json"))) $"{componentId} emits its standalone manifest"
                        Expect.isTrue (File.Exists(Path.Combine(outputPath, "v7.8.9.zip"))) $"{componentId} emits the producer archive"
                        Expect.isFalse (File.Exists(Path.Combine(outputPath, "preparation.json"))) $"{componentId} does not emit a preparation receipt"
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
                        let! exitCode, output = runPrepare root $"{componentId}/release/Config.json" outputPath
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
            [ runnerAssembly; "--prepare-authority-probe"; "dotnet/release/Config.json"; candidatePath ]
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

// The coordination gate is the only producer gate that starts a fresh child environment, so the
// fixture proves the redirection the canonical root helper performs while the ordinary gates keep
// their unredirected behavior.
let private prepareRunsTheCoordinationGateThroughTheCanonicalIsolationWrapper =
    testCaseAsync "prepare runs the Workflow coordination gate through the canonical isolation wrapper" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    do! writeFixture root None
                    let outputPath = Path.Combine(root, "candidate-output")
                    let! exitCode, output = runPrepare root "workflow/release/Config.json" outputPath
                    Expect.equal exitCode 0 $"the workflow preparation succeeds: {output}"

                    for stage in [ "build"; "pins"; "distribution" ] do
                        let marker = Path.Combine(root, sprintf "stage-%s-isolation.txt" stage)
                        Expect.isTrue (File.Exists marker) $"the {stage} gate records its isolation marker"
                        Expect.equal (File.ReadAllText marker) ambientIsolationMarker $"the {stage} gate keeps the ambient environment"

                    Expect.isFalse
                        (File.Exists(Path.Combine(root, "stage-packaged-isolation.txt")))
                        "the packaged gate runs as the coordination gate, not as an ordinary producer stage"

                    // The canonical helper is used verbatim, so the fixture never keeps a second copy.
                    let helperScripts =
                        Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories) |> Seq.toList

                    Expect.equal helperScripts.Length 1 "the fixture holds exactly one PowerShell helper"
                    Expect.equal helperScripts.[0] (Path.Combine(root, "Run.ps1")) "the fixture helper is the root helper"

                    Expect.isTrue
                        (File.ReadAllBytes helperScripts.[0] = File.ReadAllBytes(Path.Combine(repoRoot, "Run.ps1")))
                        "the fixture helper is the canonical root helper, byte for byte"

                    let report = readCoordinationReport root
                    Expect.isTrue (report["expectedAppDataPresent"].GetValue<bool>()) "the coordination gate observed the expected isolated directory"
                    Expect.isTrue (report["resolvedEqualsExpected"].GetValue<bool>()) "the coordination gate resolved exactly the isolated directory"
                    Expect.isTrue (report["resolvedDiffersFromReal"].GetValue<bool>()) "the coordination gate never resolved the real per-account directory"
                    Expect.isTrue (report["redirectedDirectoriesShareOneSandboxRoot"].GetValue<bool>()) "the redirected home and temporary directories share one sandbox root"
                    Expect.isTrue (report["isolatedKeyPathUnderSandboxRoot"].GetValue<bool>()) "the isolated key path stays under the sandbox root"
                    Expect.isTrue (report["isolatedKeyPathDiffersFromRealKeyPath"].GetValue<bool>()) "the isolated key path differs from the real per-account key path"

                    let sandboxRoot = report["sandboxRoot"].GetValue<string>()
                    let realTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    Expect.isTrue (sandboxRoot.StartsWith(realTemp, StringComparison.OrdinalIgnoreCase)) "the sandbox root is a temporary directory"
                    Expect.isFalse (sandboxRoot = realTemp) "the sandbox root is its own directory under the temporary root"
                    Expect.isFalse (Directory.Exists sandboxRoot) "the canonical helper removed the sandbox root after the child"

                    Expect.equal
                        (File.ReadAllText(Path.Combine(root, "prepare-stages.log")))
                        "build;pins;distribution;packaged;"
                        "the workflow gates run in their canonical order"
                })
    }

// A failing coordination gate is a typed failure of the second producer gate: the wrapper
// propagates the child's exit code and no later gate runs.
let private coordinationGateFailureIsATypedProducerGateFailure =
    testCaseAsync "a failing coordination gate is a typed failure of the second producer gate" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    do! writeFixture root (Some WorkflowPackagedFailure)
                    let outputPath = Path.Combine(root, "candidate-output")
                    let! exitCode, output = runPrepare root "workflow/release/Config.json" outputPath
                    Expect.equal exitCode 4 "the failing coordination gate fails the preparation"
                    let diagnostic = firstDiagnostic output
                    Expect.equal (diagnostic["status"].GetValue<string>()) "failed" "the coordination gate failure is structured"
                    Expect.equal (diagnostic["phase"].GetValue<string>()) "tests[2]" "the coordination gate failure names the second producer gate"
                    Expect.isTrue
                        (diagnostic["reason"].GetValue<string>().Contains("failed (1)", StringComparison.Ordinal))
                        "the coordination gate failure preserves the child failure code"
                    Expect.equal
                        (File.ReadAllText(Path.Combine(root, "prepare-stages.log")))
                        "build;pins;distribution;packaged;"
                        "the coordination gate ran last and no later gate followed"
                    Expect.isTrue
                        (File.Exists(Path.Combine(root, "coordination-isolation.json")))
                        "the coordination gate reported its isolation before it failed"
                    Expect.isFalse (Directory.Exists outputPath) "the failing coordination gate writes no candidate output"
                    Expect.isFalse (File.Exists(Path.Combine(root, "transaction-started"))) "the failing coordination gate reaches no publisher"
                })
    }

// The canonical wrapper is the only way into the coordination gate: without it the gate is a typed
// missing-artifact failure and never runs unisolated.
let private missingCanonicalHelperIsATypedMissingArtifactFailure =
    testCaseAsync "a missing canonical isolation helper is a typed missing-artifact failure" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    do! writeFixture root None
                    let helperPath = Path.Combine(root, "Run.ps1")
                    File.Delete helperPath
                    let outputPath = Path.Combine(root, "candidate-output")
                    let! exitCode, output = runPrepare root "workflow/release/Config.json" outputPath
                    Expect.equal exitCode 4 "the missing helper fails the preparation"
                    let diagnostic = firstDiagnostic output
                    Expect.equal (diagnostic["status"].GetValue<string>()) "failed" "the missing helper failure is structured"
                    Expect.equal (diagnostic["phase"].GetValue<string>()) "tests[2]" "the missing helper failure names the second producer gate"
                    Expect.isTrue
                        (diagnostic["reason"].GetValue<string>().Contains("required release artifact does not exist", StringComparison.Ordinal))
                        "the missing helper failure is a missing-artifact failure"
                    Expect.isTrue
                        (diagnostic["reason"].GetValue<string>().Contains(helperPath, StringComparison.OrdinalIgnoreCase))
                        "the missing helper failure names the absent artifact"
                    Expect.isFalse
                        (File.Exists(Path.Combine(root, "coordination-isolation.json")))
                        "the coordination gate never ran without the canonical helper"
                    Expect.equal
                        (File.ReadAllText(Path.Combine(root, "prepare-stages.log")))
                        "build;pins;distribution;"
                        "the coordination gate never started"
                    Expect.isFalse (Directory.Exists outputPath) "the missing helper writes no candidate output"
                })
    }

// The gate refuses to start outside the canonical wrapper, and it refuses before any key path is
// used; the fixture never reads key material.
let private coordinationGateRefusesToRunOutsideTheCanonicalWrapper =
    testCaseAsync "the coordination gate refuses to run outside the canonical isolation wrapper" <| async {
        do!
            withTemporaryRoot (fun root ->
                async {
                    do! writeFixture root None
                    let! result = runCoordinationScriptDirectly root

                    match result with
                    | Error error -> failtestf "the unisolated coordination gate could not run: %s" error
                    | Ok (exitCode, stdout, stderr) ->
                        Expect.isFalse (exitCode = 0) "the unisolated coordination gate refuses to start"
                        Expect.isTrue
                            ((stdout + stderr).Contains("requires an isolated child", StringComparison.Ordinal))
                            "the refusal names the missing isolation contract"
                        Expect.isFalse
                            (File.Exists(Path.Combine(root, "coordination-isolation.json")))
                            "the refusing gate reports no isolation evidence"
                        Expect.isFalse
                            (File.Exists(Path.Combine(root, "prepare-stages.log")))
                            "the refusing gate writes no stage marker"
                })
    }

let tests =
    testList
        "modern preparation boundary"
        [ prepareWritesPortableCandidateAndUsesExplicitProducerGates
          preparationFailureStopsBeforeLaterStages
          prepareRefusesPublicationAuthority
          prepareRunsTheCoordinationGateThroughTheCanonicalIsolationWrapper
          coordinationGateFailureIsATypedProducerGateFailure
          missingCanonicalHelperIsATypedMissingArtifactFailure
          coordinationGateRefusesToRunOutsideTheCanonicalWrapper ]
