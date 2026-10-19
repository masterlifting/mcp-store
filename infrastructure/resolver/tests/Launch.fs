// role: test
// Real neutral consumer launch: the locally packed canonical Dotnet and Workflow distributions are
// installed and started through the resolver run path, so packaging, integrity, typed inputs,
// inherited handles, and child lifecycle are proven on product bytes; a local pack is no evidence
// that a published immutable release resolves.
module Resolver.Tests.Launch

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json.Nodes
open Expecto
open SharedFailure
open DistributionContract
open Resolver.Tests.Fixture
open Resolver.Tests.Probe

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private producers = [ "dotnet"; "workflow" ]

// The workspace pin must be unsatisfiable, otherwise a successful launch would prove nothing about
// a consumer workspace that cannot select an SDK.
let private unavailableSdkVersion = "99.99.99"

let private canonicalProtocolVersion = "2024-11-05"

let private initializeRequest =
    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"resolver-launch-smoke","version":"1"}}}"""

// A space in every path segment is the consumer-environment hazard a shell command string would lose.
let private baseDirectory = Path.Combine(Path.GetTempPath(), "mcp-store resolver launch smoke")

let private jsonString (owner: JsonNode) (name: string) : string =
    let value = if isNull owner then null else owner.[name]

    if isNull value then
        failwithf
            "the initialize response is missing '%s': %s"
            name
            (if isNull owner then "<no node>" else owner.ToJsonString())

    value.GetValue<string>()

// The Dotnet producer validates the injected host before it starts, so the consumer supplies the
// canonical local muxer rather than a bare command name.
let private discoverMuxer () : string =
    let executable = if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"

    let regular (path: string option) =
        path
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value) && File.Exists value)
        |> Option.map Path.GetFullPath

    let hostProcess =
        Environment.ProcessPath
        |> Option.ofObj
        |> Option.filter (fun path ->
            Path.GetFileName(path).Equals(executable, StringComparison.OrdinalIgnoreCase)
        )

    let searched =
        let separator = if OperatingSystem.IsWindows() then ';' else ':'

        match Environment.GetEnvironmentVariable("PATH") with
        | null -> None
        | value ->
            value.Split(separator, StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun directory ->
                let candidate = Path.Combine(directory, executable)
                if File.Exists candidate then Some candidate else None)

    match
        regular (Option.ofObj (Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"))
        |> Option.orElse (regular hostProcess)
        |> Option.orElse searched
    with
    | Some path -> path
    | None -> failwith "the .NET muxer was not found for the resolver launch smoke"

type private Capture =
    { ExitCode: int
      Stdout: string
      Stderr: string }

type private Layout =
    { Root: string
      Workspace: string
      ArtifactRoot: string
      Catalog: string
      Muxer: string }

let private layout (componentId: string) : Layout =
    let base' = Path.Combine(baseDirectory, $"{componentId}-{Guid.NewGuid():N}")
    let root = Path.Combine(base', "immutable storage root")
    let workspace = Path.Combine(base', "hostile workspace")
    let artifactRoot = Path.Combine(base', "mutable artifact root")
    let catalog = Path.Combine(base', "mutable state", "profile catalog.json")

    for directory in [ root; workspace; artifactRoot; Path.GetDirectoryName catalog ] do
        Directory.CreateDirectory directory |> ignore

    File.WriteAllText(
        Path.Combine(workspace, "global.json"),
        $"{{\"sdk\":{{\"version\":\"{unavailableSdkVersion}\",\"rollForward\":\"disable\"}}}}"
    )
    File.WriteAllText(catalog, """{"profiles":[]}""")

    { Root = root
      Workspace = workspace
      ArtifactRoot = artifactRoot
      Catalog = catalog
      Muxer = discoverMuxer () }

let private runMuxer (muxer: string) (workingDirectory: string) (arguments: string list) : Async<Capture> =
    async {
        let info = ProcessStartInfo(muxer)
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        arguments |> List.iter info.ArgumentList.Add
        use child = Process.Start info
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        do! child.WaitForExitAsync() |> Async.AwaitTask
        let! out = stdout |> Async.AwaitTask
        let! err = stderr |> Async.AwaitTask
        return { ExitCode = child.ExitCode; Stdout = out; Stderr = err }
    }

let private producerArtifacts (producer: string) =
    let pinPath = Path.Combine(repositoryRoot, producer, "dist", "consumer-pins.json")
    let prerequisite =
        $"`dotnet fsi --nologo {producer}/release/Build.fsx` and `dotnet fsi --nologo {producer}/release/Pins.fsx`"

    // Fail closed on an unpacked producer so an absent distribution can never read as a passed launch.
    if not (File.Exists pinPath) then
        failwithf "the canonical %s consumer pin is missing at %s; run %s" producer pinPath prerequisite

    let pin =
        match DistributionPin.parse (File.ReadAllText pinPath) with
        | Ok value -> value
        | Error error -> failwithf "%s: %s" pinPath (ReleaseError.message error)

    let archivePath = Path.Combine(Path.GetDirectoryName pinPath, pin.ArchiveName)

    if not (File.Exists archivePath) then
        failwithf "the canonical %s archive is missing at %s; run %s" producer archivePath prerequisite

    pin, archivePath, pinPath

let private packagedManifest (archivePath: string) : DistributionManifest =
    use archive = ZipFile.OpenRead archivePath
    let entry = archive.GetEntry manifestEntryName

    if isNull entry then
        failwithf "%s carries no %s" archivePath manifestEntryName

    use stream = entry.Open()
    use buffer = new MemoryStream()
    stream.CopyTo buffer

    match DistributionContract.parse (Encoding.UTF8.GetString(buffer.ToArray())) with
    | Ok value -> value
    | Error error -> failwithf "%s: %s" archivePath (ReleaseError.message error)

// Declared inputs come from the packaged manifest, so an input this consumer does not bind is a
// coverage gap rather than a silently skipped argument.
let private consumerInputs (manifest: DistributionManifest) (place: Layout) =
    let bindings =
        [ "dotnetHost", place.Muxer
          "artifactRoot", place.ArtifactRoot
          "profileCatalog", place.Catalog
          "profileCatalogSha256", sha256 (File.ReadAllBytes place.Catalog) ]

    manifest.Launch.Arguments
    |> List.map (fun argument ->
        match bindings |> List.tryFind (fun (name, _) -> name = argument.Input) with
        | Some(_, value) -> argument.Input, value
        | None ->
            failwithf
                "the packaged manifest declares launch input '%s' that this consumer does not bind"
                argument.Input)

// A path-based input is chosen without naming a producer, so the same boundary holds for both.
let private externalInput (manifest: DistributionManifest) =
    manifest.Launch.Arguments
    |> List.tryFind (fun argument ->
        argument.Type = DistributionContract.LaunchArgumentType.AbsoluteDirectory
        || argument.Type = DistributionContract.LaunchArgumentType.AbsoluteFile
    )
    |> Option.defaultWith (fun () ->
        failwith "the packaged manifest declares no path-based launch input"
    )

let private launchArguments
    (place: Layout)
    (archivePath: string)
    (pinPath: string)
    (inputs: (string * string) list)
    =
    [ place.Root; pinPath; archivePath; place.Workspace; place.Muxer ]
    @ (inputs |> List.map (fun (name, value) -> $"{name}={value}"))

// stdin closes after the single request, so an entry that only answers initialize still terminates
// and the exit status the resolver reports stays observable.
let private exchange (arguments: string list) : Async<Capture> =
    async {
        let child = startInteractive "run" arguments
        let stderr = child.StandardError.ReadToEndAsync()

        try
            do! child.StandardInput.WriteLineAsync initializeRequest |> Async.AwaitTask
            do! child.StandardInput.FlushAsync() |> Async.AwaitTask
            let mutable stdout = ""
            let mutable readError = ""

            try
                let! line =
                    child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds 180.0)
                    |> Async.AwaitTask

                if isNull line then
                    readError <- "the launched product closed stdout before answering initialize"
                else
                    stdout <- line
            with :? TimeoutException ->
                readError <- "the launched product did not answer initialize within 180s"

            try child.StandardInput.Close() with _ -> ()

            if not child.HasExited then
                try
                    do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 60.0) |> Async.AwaitTask
                with _ ->
                    if not child.HasExited then child.Kill true
                    do! child.WaitForExitAsync() |> Async.AwaitTask

            let! err = stderr |> Async.AwaitTask

            if readError <> "" then
                return failwith readError

            return { ExitCode = child.ExitCode; Stdout = stdout; Stderr = err }
        finally
            child.Dispose()
    }

// A launch refusal happens before any child exists, so that path only reads the resolver's own
// outcome; writing to a standard input no child will read would only race its exit.
let private refuse (arguments: string list) : Async<Capture> =
    async {
        use child = startInteractive "run" arguments
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        do! child.WaitForExitAsync() |> Async.AwaitTask
        let! out = stdout |> Async.AwaitTask
        let! err = stderr |> Async.AwaitTask
        return { ExitCode = child.ExitCode; Stdout = out; Stderr = err }
    }

let private launchCase (producer: string) =
    testCaseAsync $"the packed {producer} distribution installs and launches through the resolver" <| async {
        let pin, archivePath, pinPath = producerArtifacts producer
        let manifest = packagedManifest archivePath
        let place = layout pin.ComponentId
        let inputs = consumerInputs manifest place

        Expect.equal manifest.Id pin.ComponentId "the packaged manifest carries the pinned component identity"

        let! pinned = runMuxer place.Muxer place.Workspace [ "--version" ]
        Expect.notEqual pinned.ExitCode 0 "the consumer workspace cannot select an SDK"
        Expect.stringContains unavailableSdkVersion pinned.Stderr "the workspace failure names the unavailable pin"
        Expect.stringContains "global.json" pinned.Stderr "the workspace failure names the workspace pin"
        let! unpinned = runMuxer place.Muxer (Path.GetDirectoryName place.Workspace) [ "--version" ]
        Expect.equal unpinned.ExitCode 0 "the same host selects an SDK outside the pinned workspace"

        let! launched = exchange (launchArguments place archivePath pinPath inputs)

        Expect.isFalse
            (launched.Stderr.Contains("startup failed", StringComparison.Ordinal))
            $"the launched entry reported no startup failure (stderr: {launched.Stderr})"
        Expect.equal launched.ExitCode 0 "the resolver reports the launched product's own clean exit"

        let node = JsonNode.Parse launched.Stdout
        Expect.equal (jsonString node "jsonrpc") "2.0" "the initialize response is JSON-RPC 2.0"
        Expect.equal (node.["id"].GetValue<int>()) 1 "the initialize response carries the request id"
        let result = node.["result"]
        Expect.isTrue (not (isNull result)) "the initialize response carries a result"
        Expect.equal (jsonString result "protocolVersion") canonicalProtocolVersion "initialize negotiates the canonical protocol version"
        Expect.equal (jsonString result.["serverInfo"] "name") ("mcp-store-" + manifest.Id) "initialize names the launched product"

        // Mutable consumer state must stay outside the immutable store the launch just published.
        let declared = externalInput manifest
        let insideStore = Path.Combine(place.Root, "consumer state", "declared input")

        if declared.Type = DistributionContract.LaunchArgumentType.AbsoluteDirectory then
            Directory.CreateDirectory insideStore |> ignore
        else
            Directory.CreateDirectory(Path.GetDirectoryName insideStore) |> ignore
            File.WriteAllText(insideStore, """{"profiles":[]}""")

        let misplaced =
            inputs
            |> List.map (fun (name, value) ->
                if name = declared.Input then name, insideStore else name, value
            )

        let! refused = refuse (launchArguments place archivePath pinPath misplaced)

        Expect.notEqual refused.ExitCode 0 "a launch input inside the immutable storage root never launches"
        Expect.stringContains "immutable storage root" refused.Stderr "the refusal names the immutable storage root"
    }

let private typedInputTests =
    testList
        "typed inputs"
        [ testCase "declared inputs compose into the manifest's own flag order" <| fun _ ->
            let descriptor = (manifest dotnetSpec dotnetSpec.Revision (bodies dotnetSpec)).Launch
            let arguments =
                [ "artifactRoot", @"C:\artifacts"
                  "dotnetHost", @"C:\host\dotnet.exe" ]
                |> ResolverLaunch.compose descriptor

            Expect.equal
                arguments
                (Ok [ "--dotnet-host"; @"C:\host\dotnet.exe"; "--artifact-root"; @"C:\artifacts" ])
                "values follow the declared descriptor order, not the supplied order"

          testCase "an undeclared, repeated, or missing input is refused before any launch" <| fun _ ->
            let descriptor = (manifest dotnetSpec dotnetSpec.Revision (bodies dotnetSpec)).Launch

            let refused (inputs: (string * string) list) =
                Expect.isFalse (ResolverLaunch.compose descriptor inputs |> Result.isOk) "the composition is refused"

            refused [ "dotnetHost", @"C:\host\dotnet.exe"; "artifactRoot", @"C:\artifacts"; "unknown", @"C:\x" ]
            refused [ "dotnetHost", @"C:\host\dotnet.exe"; "dotnetHost", @"C:\other\dotnet.exe" ]
            refused [ "dotnetHost", @"C:\host\dotnet.exe" ]

          testCase "a relative path and a non-digest value are refused by their declared type" <| fun _ ->
            let dotnet = (manifest dotnetSpec dotnetSpec.Revision (bodies dotnetSpec)).Launch

            // The shared fixture gives every spec the Dotnet descriptor, so the digest declaration
            // is stated here rather than inferred from a producer identity.
            let workflow =
                { dotnet with
                    Arguments =
                        [ { Flag = "--profile-catalog"
                            Input = "profileCatalog"
                            Type = DistributionContract.LaunchArgumentType.AbsoluteFile }
                          { Flag = "--profile-catalog-sha256"
                            Input = "profileCatalogSha256"
                            Type = DistributionContract.LaunchArgumentType.Sha256 } ] }

            match ResolverLaunch.compose dotnet [ "dotnetHost", "dotnet.exe"; "artifactRoot", @"C:\artifacts" ] with
            | Error(LaunchInputRejected(input, _)) -> Expect.equal input "dotnetHost" "the relative host names its input"
            | other -> failtestf "expected a rejected relative host, got %A" other

            match ResolverLaunch.compose workflow [ "profileCatalog", @"C:\catalog.json"; "profileCatalogSha256", "NOT-A-DIGEST" ] with
            | Error(LaunchInputRejected(input, _)) -> Expect.equal input "profileCatalogSha256" "the non-digest names its input"
            | other -> failtestf "expected a rejected non-digest, got %A" other

          testCase "the command line keeps every path absolute, single-valued, and typed" <| fun _ ->
            let refused (arguments: string list) =
                match ResolverCli.parse arguments with
                | Error(UsageRejected _) -> ()
                | other -> failtestf "expected a rejected command line, got %A" other

            refused [ "run"; "--pin"; "pin.json"; "--workspace"; @"C:\w"; "--host"; @"C:\h" ]
            refused [ "resolve"; "--pin"; @"C:\pin.json"; "--host"; @"C:\h" ]
            refused [ "run"; "--pin"; @"C:\pin.json"; "--pin"; @"C:\other.json"; "--workspace"; @"C:\w"; "--host"; @"C:\h" ]
            refused [ "run"; "--pin"; @"C:\pin.json"; "--workspace"; @"C:\w"; "--host"; @"C:\h"; "--input"; "no-equals-sign" ]
            refused [ "run"; "--pin"; @"C:\pin.json"; "--workspace"; @"C:\w"; "--host"; @"C:\h"; "--unknown"; "x" ]

            match ResolverCli.parse [ "run"; "--pin"; @"C:\pin.json"; "--workspace"; @"C:\w"; "--host"; @"C:\h"; "--input"; "dotnetHost=C:\dotnet.exe" ] with
            | Ok(ResolverCli.Run options) ->
                Expect.equal options.Inputs [ "dotnetHost", @"C:\dotnet.exe" ] "a well-formed input pair is accepted"
                Expect.isNone options.Root "an omitted root stays unclaimed"
            | other -> failtestf "expected a parsed run command, got %A" other ]

let tests =
    testList
        "launch"
        [ yield! producers |> List.map launchCase
          typedInputTests ]