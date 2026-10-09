// role: release/build entrypoint
// Repo-shared release bootstrap. The workflow installs an SDK via
// `actions/setup-dotnet`; this script verifies the install against each
// producer's canonical Config.fsx and emits a stable line-oriented summary
// the workflow appends to GITHUB_ENV. The bootstrap is the gate every
// release transaction runs through before any build, test, or publish step.
//
// Each producer's Config.fsx declares `module ReleaseConfig` (script-level),
// so the two producer configs cannot share an FSI session. This script
// evaluates each Config.fsx in its own child `dotnet fsi` process and parses
// the key=value output; that keeps the two producer modules isolated and
// avoids re-implementing producer policy in a duplicate projection.
//
// Run with:
//   dotnet fsi infrastructure/release/Bootstrap.fsx

#load "../Provenance.fsx"

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open BuildProvenance

// The summary the bootstrap emits; one record per loaded producer. SDK
// policy lives in the producer's Config.fsx and is read through a child
// `dotnet fsi` invocation so this script does not duplicate the source of
// truth.
type ProducerSummary =
    { ComponentId: string
      Version: string
      EntryDll: string
      SdkVersion: string
      RuntimeVersion: string
      Platform: string
      ArchiveName: string
      AssetUri: string }

// The executing identity the workflow's `actions/setup-dotnet` is checked
// against. A mismatch anywhere in this record is reported as a single
// actionable diagnostic.
type ExecutingIdentity =
    { SdkVersion: string
      RuntimeMoniker: string
      RuntimeVersion: string
      Platform: string
      Architecture: string }

// The shared-framework moniker every producer distribution runs on. The exact
// runtime version is not duplicated here; it is read from each producer's
// canonical Config.fsx and compared against `dotnet --list-runtimes` so a
// different runtime patch cannot quietly reach the build path.
let private expectedRuntimeMoniker = "Microsoft.NETCore.App"

// The list of producers this bootstrap verifies. Adding a producer requires
// adding a row here; the canonical Config.fsx path is the single source of
// truth and the script's role header forbids the analyzer from following
// any other source.
let private producerConfigPaths : (string * string) list =
    [ "dotnet", "dotnet/release/Config.fsx"
      "workflow", "workflow/release/Config.fsx" ]

let private readSdkVersion (root: string) : Async<Result<string, ReleaseError>> =
    async {
        let! cancellationToken = Async.CancellationToken
        return! runProcess root "dotnet" [ "--version" ] cancellationToken
    }

let private readExecutingIdentity (root: string) (canonicalRuntimeVersion: string) : Async<Result<ExecutingIdentity, ReleaseError>> =
    async {
        let! cancellationToken = Async.CancellationToken

        match! runProcess root "dotnet" [ "--list-runtimes" ] cancellationToken with
        | Error error -> return Error error
        | Ok output ->
            let runtimeVersions =
                output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose (fun line ->
                    let parts = line.Split([| ' '; '[' |], StringSplitOptions.RemoveEmptyEntries)

                    if parts.Length >= 2
                       && String.Equals(parts.[0], expectedRuntimeMoniker, StringComparison.Ordinal) then
                        Some parts.[1]
                    else
                        None)

            // Exact canonical runtime version is mandatory. A prefix-only
            // fallback would accept a different runtime patch and silently
            // break reproducibility, so the exact match is required.
            let matched =
                runtimeVersions
                |> Array.tryFind (fun version -> String.Equals(version, canonicalRuntimeVersion, StringComparison.Ordinal))

            match matched with
            | Some runtimeVersion ->
                let arch =
                    try
                        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
                    with
                    | :? PlatformNotSupportedException -> "unknown"

                let os =
                    try
                        if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
                            "linux"
                        elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
                            "osx"
                        elif RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                            "win"
                        else
                            "unknown"
                    with
                    | :? PlatformNotSupportedException -> "unknown"

                let! sdkResult = readSdkVersion root

                match sdkResult with
                | Error error -> return Error error
                | Ok sdkVersion ->
                    return
                        Ok
                            { SdkVersion = sdkVersion
                              RuntimeMoniker = expectedRuntimeMoniker
                              RuntimeVersion = runtimeVersion
                              Platform = $"{os}-{arch}"
                              Architecture = arch }
            | None ->
                return
                    Error(
                        ProcessFailed(
                            "dotnet --list-runtimes",
                            0,
                            $"the canonical runtime {expectedRuntimeMoniker} {canonicalRuntimeVersion} was not reported by 'dotnet --list-runtimes'; observed:\n{output}")
                    )
    }

// Generate a one-shot wrapper script that loads a single producer's
// Config.fsx and prints its key=value summary. The wrapper lives in a
// fresh temp directory for the duration of the child process so concurrent
// runs cannot race on the same file.
let private writeConfigWrapper (root: string) (configPath: string) : string =
    let tempDir = Path.Combine(Path.GetTempPath(), $"mcp-bootstrap-{Guid.NewGuid():N}")
    Directory.CreateDirectory tempDir |> ignore
    let wrapperPath = Path.Combine(tempDir, "Wrapper.fsx")
    let absoluteConfig = Path.GetFullPath(Path.Combine(root, configPath))
    // Script-level `module ReleaseConfig` definitions in Config.fsx are
    // pulled into the wrapper's FSI session by `#load`; the wrapper then
    // prints the canonical fields. The wrapper never redefines the module.
    let content =
        sprintf "#load @\"%s\"\n" absoluteConfig
        + "printfn \"COMPONENT_ID=%s\" ReleaseConfig.componentId\n"
        + "printfn \"VERSION=%s\" ReleaseConfig.version\n"
        + "printfn \"ENTRY_DLL=%s\" ReleaseConfig.entryDll\n"
        + "printfn \"SDK=%s\" ReleaseConfig.sdkVersion\n"
        + "printfn \"RUNTIME=%s\" ReleaseConfig.runtimeVersion\n"
        + "printfn \"PLATFORM=%s\" ReleaseConfig.platform\n"
        + "printfn \"ARCHIVE=%s\" ReleaseConfig.archiveName\n"
        + "printfn \"ASSET_URI=%s\" ReleaseConfig.assetUri\n"
    File.WriteAllText(wrapperPath, content)
    wrapperPath

let private cleanupWrapper (wrapperPath: string) =
    let dir = Path.GetDirectoryName wrapperPath
    if not (String.IsNullOrEmpty dir) && Directory.Exists dir then
        Directory.Delete(dir, true)

let private loadProducerConfig (root: string) (componentId: string) (configPath: string) : Async<Result<ProducerSummary, ReleaseError>> =
    async {
        let wrapperPath = writeConfigWrapper root configPath
        let mutable cleanupNeeded = true

        try
            let! cancellationToken = Async.CancellationToken

            match! runProcess root "dotnet" [ "fsi"; "--nologo"; wrapperPath ] cancellationToken with
            | Error error -> return Error error
            | Ok output ->
                let pairs =
                    output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.choose (fun line ->
                        let idx = line.IndexOf('=')

                        if idx <= 0 then
                            None
                        else
                            Some(line.Substring(0, idx), line.Substring(idx + 1)))
                    |> Map.ofArray

                let requireAll (names: string list) : Result<Map<string, string>, ReleaseError> =
                    let rec loop acc remaining =
                        match remaining with
                        | [] -> Ok(Map.ofList acc)
                        | name :: rest ->
                            match Map.tryFind name pairs with
                            | Some value -> loop ((name, value) :: acc) rest
                            | None ->
                                Error(
                                    MalformedArtifact(
                                        configPath,
                                        $"bootstrap could not read producer field '{name}' from Config.fsx output; observed:\n{output}")
                                )

                    loop [] names

                match requireAll [ "COMPONENT_ID"; "VERSION"; "ENTRY_DLL"; "SDK"; "RUNTIME"; "PLATFORM"; "ARCHIVE"; "ASSET_URI" ] with
                | Error error -> return Error error
                | Ok fields ->
                    let id = fields.["COMPONENT_ID"]

                    if id <> componentId then
                        return Error(IdentityMismatch("componentId", componentId, id))
                    else
                        return
                            Ok
                                { ComponentId = id
                                  Version = fields.["VERSION"]
                                  EntryDll = fields.["ENTRY_DLL"]
                                  SdkVersion = fields.["SDK"]
                                  RuntimeVersion = fields.["RUNTIME"]
                                  Platform = fields.["PLATFORM"]
                                  ArchiveName = fields.["ARCHIVE"]
                                  AssetUri = fields.["ASSET_URI"] }
        finally
            if cleanupNeeded then cleanupWrapper wrapperPath
    }

// Optional expected values the workflow projects from the canonical Config.fsx
// through the non-F# `Update.ps1 -Project` projection before any SDK install.
// When present they must equal both the evaluated Config.fsx and the executing
// toolchain, completing the projection -> evaluation -> execution cross-check.
let private expectedFromEnvironment (name: string) : string option =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

let private checkExpected (name: string) (expected: string option) (actual: string) : ReleaseError option =
    match expected with
    | Some value when value <> actual -> Some(IdentityMismatch(name, value, actual))
    | _ -> None

// Cross-validate executing identity against every loaded producer. SDK,
// runtime, and platform policy lives in each producer's Config.fsx; the script
// reads it through a child `dotnet fsi` invocation so this module never
// duplicates the source of truth. The canonical runtime is resolved from the
// loaded producers before the executing runtime is read so the exact runtime
// patch can be required.
let private verify (root: string) : Async<Result<ExecutingIdentity * ProducerSummary list, ReleaseError>> =
    async {
        let mutable failures : ReleaseError list = []
        let mutable loaded : ProducerSummary list = []

        for componentId, configPath in producerConfigPaths do
            match! loadProducerConfig root componentId configPath with
            | Error error -> failures <- error :: failures
            | Ok producer -> loaded <- producer :: loaded

        let loaded = loaded |> List.rev

        match failures with
        | first :: _ -> return Error first
        | [] ->
            let canonicalRuntime =
                loaded
                |> List.map (fun producer -> producer.RuntimeVersion)
                |> List.distinct

            match canonicalRuntime with
            | [ runtime ] ->
                let! identity = readExecutingIdentity root runtime

                match identity with
                | Error error -> return Error error
                | Ok executing ->
                    let mutable mismatches : ReleaseError list = []

                    for producer in loaded do
                        if producer.SdkVersion <> executing.SdkVersion then
                            mismatches <-
                                IdentityMismatch($"{producer.ComponentId}.sdkVersion", producer.SdkVersion, executing.SdkVersion) :: mismatches

                        if producer.RuntimeVersion <> executing.RuntimeVersion then
                            mismatches <-
                                IdentityMismatch($"{producer.ComponentId}.runtimeVersion", producer.RuntimeVersion, executing.RuntimeVersion) :: mismatches

                        if producer.Platform <> executing.Platform then
                            mismatches <-
                                IdentityMismatch($"{producer.ComponentId}.platform", producer.Platform, executing.Platform) :: mismatches

                    let projected =
                        [ checkExpected "RELEASE_EXPECTED_SDK" (expectedFromEnvironment "RELEASE_EXPECTED_SDK") executing.SdkVersion
                          checkExpected "RELEASE_EXPECTED_RUNTIME" (expectedFromEnvironment "RELEASE_EXPECTED_RUNTIME") executing.RuntimeVersion
                          checkExpected "RELEASE_EXPECTED_PLATFORM" (expectedFromEnvironment "RELEASE_EXPECTED_PLATFORM") executing.Platform ]
                        |> List.choose id

                    match mismatches @ projected with
                    | first :: _ -> return Error first
                    | [] -> return Ok(executing, loaded)
            | _ ->
                return
                    Error(
                        IdentityMismatch(
                            "producer runtimeVersion",
                            "one canonical value",
                            String.concat ", " canonicalRuntime))
    }

// Stable line-oriented KEY=VALUE summary the workflow appends to
// GITHUB_ENV. The format is additive: existing keys are stable, new keys
// are appended at the end so older workflow versions keep parsing.
let private renderSummary (executing: ExecutingIdentity) (producers: ProducerSummary list) : string =
    let builder = System.Text.StringBuilder()
    builder.AppendLine("# release bootstrap summary") |> ignore
    builder.AppendLine($"RELEASE_SDK={executing.SdkVersion}") |> ignore
    builder.AppendLine($"RELEASE_RUNTIME={executing.RuntimeMoniker} {executing.RuntimeVersion}") |> ignore
    builder.AppendLine($"RELEASE_PLATFORM={executing.Platform}") |> ignore
    builder.AppendLine($"RELEASE_ARCHITECTURE={executing.Architecture}") |> ignore

    for producer in producers do
        builder.AppendLine($"RELEASE_{producer.ComponentId.ToUpperInvariant()}_VERSION={producer.Version}") |> ignore
        builder.AppendLine($"RELEASE_{producer.ComponentId.ToUpperInvariant()}_ARCHIVE={producer.ArchiveName}") |> ignore
        builder.AppendLine($"RELEASE_{producer.ComponentId.ToUpperInvariant()}_ASSET_URI={producer.AssetUri}") |> ignore
        builder.AppendLine($"RELEASE_{producer.ComponentId.ToUpperInvariant()}_ENTRY={producer.EntryDll}") |> ignore

    builder.ToString()

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let result =
    match verify root |> Async.RunSynchronously with
    | Ok(executing, producers) -> renderSummary executing producers
    | Error error -> failwith (ReleaseError.message error)

printfn "%s" result
