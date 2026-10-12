// role: release/build entrypoint
// Verifies the installed SDK/runtime/platform against each producer's canonical Config.fsx and emits the workflow summary.
// Each producer Config.fsx declares `module ReleaseConfig`, so the two are evaluated in isolated child `dotnet fsi` processes.
// Requires the exact canonical runtime patch so a different toolchain cannot quietly reach the build path.

#load "../Provenance.fs"
#load "Enrollment.fs"

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open BuildProvenance

// SDK policy lives in the producer's Config.fsx and is read through a child `dotnet fsi` so this script does not duplicate it.
type ProducerSummary =
    { ComponentId: string
      Version: string
      EntryDll: string
      SdkVersion: string
      RuntimeVersion: string
      Platform: string
      ArchiveName: string
      AssetUri: string }

type ExecutingIdentity =
    { SdkVersion: string
      RuntimeMoniker: string
      RuntimeVersion: string
      Platform: string
      Architecture: string }

// The exact runtime is read from each producer's Config.fsx; the moniker is the only shared constant.
let private expectedRuntimeMoniker = "Microsoft.NETCore.App"

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

            // A prefix-only match would accept a different runtime patch and break reproducibility.
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

// The wrapper lives in a fresh temp directory so concurrent runs cannot race on the same file.
let private writeConfigWrapper (root: string) (configPath: string) : string =
    let tempDir = Path.Combine(Path.GetTempPath(), $"mcp-bootstrap-{Guid.NewGuid():N}")
    Directory.CreateDirectory tempDir |> ignore
    let wrapperPath = Path.Combine(tempDir, "Wrapper.fsx")
    let absoluteConfig = Path.GetFullPath(Path.Combine(root, configPath))
    // The wrapper `#load`s the config; it never redefines the module.
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

// The workflow projects these from Config.fsx before SDK install; when present they must equal both the evaluated config and the executing toolchain.
let private expectedFromEnvironment (name: string) : string option =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

let private checkExpected (name: string) (expected: string option) (actual: string) : ReleaseError option =
    match expected with
    | Some value when value <> actual -> Some(IdentityMismatch(name, value, actual))
    | _ -> None

// The remaining cross-producer equality check stays scoped to the bootstrap toolchain guard.
let private continueVerify
    (root: string)
    (loaded: ProducerSummary list)
    : Async<Result<ExecutingIdentity * ProducerSummary list, ReleaseError>> =
    async {
        match loaded with
        | [] -> return Error(MalformedArtifact("Participants.json", "no producers loaded from the enrollment roster"))
        | _ ->
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

// Resolves the canonical runtime from the loaded producers so the exact patch can be required.
let private verify (root: string) : Async<Result<ExecutingIdentity * ProducerSummary list, ReleaseError>> =
    async {
        let! rosterResult = ReleaseEnrollment.loadRoster root

        match rosterResult with
        | Error error -> return Error error
        | Ok roster ->
            match ReleaseEnrollment.validateRoster roster with
            | Error error -> return Error error
            | Ok () ->
                let mutable failures : ReleaseError list = []
                let mutable loaded : ProducerSummary list = []

                for entry in roster.Entries do
                    match! loadProducerConfig root entry.ComponentId entry.ConfigPath with
                    | Error error -> failures <- error :: failures
                    | Ok producer ->
                        match ReleaseEnrollment.validateProjectionAgainstRoster producer.ComponentId entry.ConfigPath root roster with
                        | Error rosterError -> failures <- rosterError :: failures
                        | Ok () -> loaded <- producer :: loaded

                let loaded = loaded |> List.rev

                match failures with
                | first :: _ -> return Error first
                | [] -> return! continueVerify root loaded
    }

// The format is additive so older workflow versions keep parsing.
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

// Standalone entry bridge: fsi can only drive a top-level async pipeline
// through one synchronous wait at the host boundary; without it the
// workflow summary print races the spawned `dotnet fsi` child.
let result =
    match verify root |> Async.RunSynchronously with
    | Ok(executing, producers) -> renderSummary executing producers
    | Error error -> failwith (ReleaseError.message error)

printfn "%s" result
