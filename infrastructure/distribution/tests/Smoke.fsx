// role: test entrypoint
// Every producer launches through one metadata-driven routine, so launch composition is proven producer-independent.
// Composition only: the produced distributions must already exist and every consumer binding is supplied explicitly.

#load "../../Distribution.fsx"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text.Json.Nodes
open BuildProvenance
open DistributionBuild
open DistributionContract

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let assertContains label (fragment: string) (text: string) =
    if not (text.Contains(fragment, StringComparison.Ordinal)) then
        failwithf "%s: expected '%s' in '%s'" label fragment text

let jsonString (owner: JsonNode) (name: string) =
    let value = if isNull owner then null else owner.[name]

    if isNull value then
        failwithf
            "the protocol response is missing '%s': %s"
            name
            (if isNull owner then "<no node>" else owner.ToJsonString())

    value.GetValue<string>()

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

// Participating producers are named only by their release directory; component identity, archive, entrypoint, and launch arguments all come from emitted metadata.
let producers = [ "dotnet"; "workflow" ]

// The injected host must be the canonical local muxer path: the Dotnet producer rejects a relative, aliased, missing, or reparse-point host before it starts.
let dotnetHost () =
    let executable = if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"

    let fromEnvironment =
        Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
        |> Option.ofObj
        |> Option.filter (fun path -> not (String.IsNullOrWhiteSpace path) && File.Exists path)

    let fromProcess =
        Environment.ProcessPath
        |> Option.ofObj
        |> Option.filter (fun path ->
            Path.GetFileName(path).Equals(executable, StringComparison.OrdinalIgnoreCase))

    let fromPath =
        let separator = if OperatingSystem.IsWindows() then ';' else ':'

        Environment.GetEnvironmentVariable("PATH").Split(separator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun directory ->
            let candidate = Path.Combine(directory, executable)
            if File.Exists candidate then Some candidate else None)

    match fromEnvironment |> Option.orElse fromProcess |> Option.orElse fromPath with
    | Some path -> Path.GetFullPath path
    | None -> failwith "the .NET muxer was not found for the packaged launch smoke"

let muxer = dotnetHost ()
let mkdir (path: string) = Directory.CreateDirectory path |> ignore

// The unavailable pin is the isolation probe: a packed entry must start although the workspace it runs from cannot select an SDK for operational commands.
let unavailableSdkVersion = "99.99.99"

let globalJson =
    sprintf "{\"sdk\":{\"version\":\"%s\",\"rollForward\":\"disable\"}}" unavailableSdkVersion

let profileCatalogText = """{"profiles":[]}"""
let canonicalProtocolVersion = "2024-11-05"

let initializeRequest =
    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"distribution-smoke","version":"1"}}}"""

let resolveMember (extractionRoot: string) (relativePath: string) =
    match DistributionContract.resolveUnderRoot extractionRoot relativePath with
    | Ok path -> path
    | Error error -> failwith (ReleaseError.message error)

// The entrypoint and ordered argument descriptors come from the extracted manifest, so launch never branches on producer identity.
let launchCommand
    (manifest: DistributionManifest)
    (extractionRoot: string)
    (inputs: Map<string, string>)
    =
    let validate (argument: LaunchArgument) (value: string) =
        match argument.Type with
        | AbsoluteFile ->
            assertTrue
                $"launch input '{argument.Input}' is an absolute existing file"
                (Path.IsPathFullyQualified value && File.Exists value)
        | AbsoluteDirectory ->
            assertTrue
                $"launch input '{argument.Input}' is an absolute existing directory"
                (Path.IsPathFullyQualified value && Directory.Exists value)
        | Sha256 ->
            assertTrue $"launch input '{argument.Input}' is an exact lowercase SHA-256" (isSha256 value)

        value

    let pairs =
        manifest.Launch.Arguments
        |> List.map (fun argument ->
            match Map.tryFind argument.Input inputs with
            | Some value -> argument.Flag, validate argument value
            | None ->
                failwithf
                    "the manifest declares launch input '%s' that the consumer environment does not supply"
                    argument.Input)

    assertEqual "launch kind selects the structured dotnet exec form" launchKindDotnetExec manifest.Launch.Kind
    let entrypoint = resolveMember extractionRoot manifest.Launch.Entrypoint
    assertTrue $"the entrypoint resolves to an extracted file: {manifest.Launch.Entrypoint}" (File.Exists entrypoint)

    assertTrue
        $"the packaged runtime configuration resolves under the extraction root: {manifest.Runtime.Config}"
        (File.Exists(resolveMember extractionRoot manifest.Runtime.Config))

    entrypoint, [ "exec"; entrypoint ] @ (pairs |> List.collect (fun (flag, value) -> [ flag; value ]))

let extractDistribution (archivePath: string) (extractionRoot: string) =
    if not (File.Exists archivePath) then
        failwithf
            "the produced distribution archive is missing: %s (run the producer package gate first: dotnet fsi --nologo <producer>/tests/DistributionTests.fsx)"
            archivePath

    if Directory.Exists extractionRoot then Directory.Delete(extractionRoot, true)
    ZipFile.ExtractToDirectory(archivePath, extractionRoot)

let readExtractedManifest (extractionRoot: string) : DistributionManifest =
    let path = Path.Combine(extractionRoot, manifestEntryName)

    if not (File.Exists path) then
        failwithf "the extracted distribution carries no %s: %s" manifestEntryName extractionRoot

    match DistributionContract.parse (File.ReadAllText path) with
    | Ok manifest -> manifest
    | Error error -> failwith (ReleaseError.message error)

type Capture =
    { ExitCode: int
      Stdout: string
      Stderr: string }

let runCaptured (workingDirectory: string) (arguments: string list) : Async<Capture> =
    async {
        let info = ProcessStartInfo muxer
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        arguments |> List.iter info.ArgumentList.Add
        use child = Process.Start info
        let stdoutTask = child.StandardOutput.ReadToEndAsync()
        let stderrTask = child.StandardError.ReadToEndAsync()
        do! child.WaitForExitAsync() |> Async.AwaitTask
        let! stdout = stdoutTask |> Async.AwaitTask
        let! stderr = stderrTask |> Async.AwaitTask
        return { ExitCode = child.ExitCode; Stdout = stdout; Stderr = stderr }
    }

// stdin closes after the single request so an entry that only answers initialize still terminates deterministically.
let smokeInitialize (workspace: string) (command: string list) (serverName: string) : Async<unit> =
    async {
        let info = ProcessStartInfo muxer
        info.WorkingDirectory <- workspace
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        command |> List.iter info.ArgumentList.Add
        use child = Process.Start info
        let stderrTask = child.StandardError.ReadToEndAsync()
        do! child.StandardInput.WriteLineAsync initializeRequest |> Async.AwaitTask
        do! child.StandardInput.FlushAsync() |> Async.AwaitTask
        let mutable response = ""
        let mutable readError = ""

        try
            let! line =
                child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds 180.0)
                |> Async.AwaitTask

            if isNull line then readError <- "the packed entry closed stdout before answering initialize"
            else response <- line
        with :? TimeoutException ->
            readError <- "the packed entry did not answer initialize within 180s"

        try child.StandardInput.Close() with _ -> ()

        if not child.HasExited then
            try
                do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 60.0) |> Async.AwaitTask
            with _ ->
                if not child.HasExited then child.Kill true
                do! child.WaitForExitAsync() |> Async.AwaitTask

        let! stderr = stderrTask |> Async.AwaitTask

        if readError <> "" then failwith readError

        assertTrue
            "the packed entry must not report a startup failure"
            (not (stderr.Contains("startup failed", StringComparison.Ordinal)))

        assertEqual "the packed entry exited cleanly after stdin closed" 0 child.ExitCode
        let node = JsonNode.Parse response
        assertEqual "initialize response jsonrpc" "2.0" (jsonString node "jsonrpc")
        assertEqual "initialize response id" 1 (node.["id"].GetValue<int>())
        let result = node.["result"]
        assertTrue "initialize response carries a result" (not (isNull result))
        assertEqual "initialize negotiates the canonical protocol version" canonicalProtocolVersion (jsonString result "protocolVersion")
        assertEqual "initialize names the packed server" serverName (jsonString result.["serverInfo"] "name")
    }

// The same host honors the workspace pin for operational commands, so an entry starting under that pin proves runtime entry is not SDK selection.
let assertWorkspaceSdkIsHonored (workspace: string) : Async<unit> =
    async {
        let! pinned = runCaptured workspace [ "--version" ]
        assertTrue "an operational SDK command honors the unavailable workspace pin" (pinned.ExitCode <> 0)
        assertContains "the SDK failure names the unavailable pin" unavailableSdkVersion pinned.Stderr
        assertContains "the SDK failure names the workspace global.json" "global.json" pinned.Stderr
        let! unpinned = runCaptured (Path.GetDirectoryName workspace) [ "--version" ]
        assertEqual "the same host selects an SDK outside the pinned workspace" 0 unpinned.ExitCode

        assertTrue
            "the unpinned workspace reports an SDK version"
            (not (String.IsNullOrWhiteSpace unpinned.Stdout))
    }

let runProducer (runRoot: string) (producer: string) : Async<unit> =
    async {
        let policy = loadConfig (Path.Combine(repoRoot, producer, "release", "Config.json"))
        let archivePath = Path.Combine(repoRoot, producer, "dist", policy.ArchiveName)
        let extractionRoot = Path.Combine(runRoot, "extraction root", policy.ComponentId)
        let workspace = Path.Combine(runRoot, "workspace root", policy.ComponentId)
        let artifactRoot = Path.Combine(runRoot, "artifact root", policy.ComponentId)
        let catalogPath = Path.Combine(runRoot, "profile catalog", policy.ComponentId + ".json")
        printfn "%s: extracting %s beneath an arbitrary root with spaces" policy.ComponentId archivePath
        extractDistribution archivePath extractionRoot
        mkdir workspace
        File.WriteAllText(Path.Combine(workspace, "global.json"), globalJson)
        mkdir artifactRoot
        mkdir (Path.GetDirectoryName catalogPath)
        File.WriteAllText(catalogPath, profileCatalogText)

        let! catalogHash =
            async {
                match! sha256FileAsync catalogPath with
                | Ok value -> return value
                | Error error -> return failwith (ReleaseError.message error)
            }

        let manifest = readExtractedManifest extractionRoot

        // The declared inputs come from this one consumer environment; no binding holds credentials or a live dependency.
        let inputs =
            Map.ofList
                [ "profileCatalog", catalogPath
                  "profileCatalogSha256", catalogHash
                  "dotnetHost", muxer
                  "artifactRoot", artifactRoot ]

        let entrypoint, command = launchCommand manifest extractionRoot inputs
        printfn "%s: metadata command is [%s]" manifest.Id (String.concat " " command)
        do! smokeInitialize workspace command ("mcp-store-" + manifest.Id)
        do! assertWorkspaceSdkIsHonored workspace

        printfn
            "%s: OK launched %s under the unavailable workspace SDK pin %s"
            manifest.Id
            entrypoint
            unavailableSdkVersion
    }

async {
    let runRoot =
        Path.Combine(Path.GetTempPath(), "opencode", "distribution-smoke-" + Guid.NewGuid().ToString("N"))

    try
        for producer in producers do
            do! runProducer runRoot producer

        printfn "OK distribution launch smoke: %d producers launched from extracted manifests" producers.Length
    finally
        if Directory.Exists runRoot then
            try
                Directory.Delete(runRoot, true)
            with _ ->
                ()
}
// FSI needs one synchronous top-level entry for the asynchronous script.
|> Async.RunSynchronously
