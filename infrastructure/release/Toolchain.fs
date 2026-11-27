// role: reusable module/helper
// The shared .NET toolchain facts. The SDK, target framework, language version and the unique `Microsoft.NETCore.App`
// framework reference are evaluated by the native MSBuild resolver on the release project, so no SDK root, listing or
// bundled-versions file is discovered or parsed here. The only authored owner read is the SDK version in the root
// `global.json`, used to confirm that the evaluated SDK is the exact stable selection.
// Evaluation is a process-spawning step that only credential-free entry points call; the publication entry point
// consumes the verified handoff and has no reachable path to a subprocess.
module ReleaseToolchain

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.RegularExpressions
open SharedFailure
open BuildProvenance

// The resolved toolchain every dependent reference derives from: the policy projection, the contract and the manifest.
// The platform is the RID the host observed, never an authored value.
type Toolchain =
    { SdkVersion: string
      RuntimeVersion: string
      Platform: string }

// The values read from one native evaluation of the release project.
type Evaluation =
    { EvaluatedSdkVersion: string
      TargetFramework: string
      LangVersion: string
      BundledTargetFramework: string
      DefaultRuntimeFrameworkVersion: string }

let globalJsonName = "global.json"

// Written only by the credential-free verification step after its checks; both are required for publication.
let verifiedSdkVariable = "MCP_RELEASE_VERIFIED_SDK"
let verifiedRuntimeVariable = "MCP_RELEASE_VERIFIED_RUNTIME"
let verifiedPlatformVariable = "MCP_RELEASE_VERIFIED_PLATFORM"

// The release project that native evaluation reads, relative to the trusted repository root.
let evaluatedProjectPath = "infrastructure/release/Release.fsproj"

let evaluationArguments : string list =
    [ "msbuild"
      evaluatedProjectPath
      "-nologo"
      "-getProperty:NETCoreSdkVersion,TargetFramework,LangVersion,BundledNETCoreAppTargetFramework"
      "-getItem:KnownFrameworkReference" ]

// Native evaluation must never run beside a publication credential.
let private publicationAuthorityNames = [ "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]

let maxOwnerBytes = 16 * 1024
let maxEvaluationBytes = 4 * 1024 * 1024

let private versionToken =
    Regex(@"\A[0-9]+(\.[0-9]+)*(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?\z", RegexOptions.CultureInvariant)

let private stableVersion = Regex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)
let private frameworkToken = Regex(@"\Anet[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)
let private languageToken = Regex(@"\A[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)
let private platformToken = Regex(@"\A[a-z0-9]+([.-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)

// The RID the executing runtime reports; the manifest provenance and the publisher qualification both read it.
let hostPlatform () : string = RuntimeInformation.RuntimeIdentifier

let private malformed path detail = Error(MalformedArtifact(path, detail))

// A version that is handed over or compared is accepted only in this exact shape: digits separated by dots with an
// optional dot-separated alphanumeric prerelease label. `..`, either separator, an empty component, a trailing dot, a
// drive prefix and a bare prerelease label never match.
let isVersionToken (value: string) : bool =
    not (isNull value) && versionToken.IsMatch value

let private readBounded (path: string) (limit: int) : Result<byte[], ReleaseError> =
    try
        let info = FileInfo path

        if not info.Exists then
            Error(MissingArtifact path)
        elif info.Length > int64 limit then
            malformed path $"the file exceeds the bounded maximum of {limit} bytes (got {info.Length})"
        else
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 8192
            let mutable count = stream.Read(chunk, 0, chunk.Length)

            while count > 0 do
                buffer.Write(chunk, 0, count)
                count <- stream.Read(chunk, 0, chunk.Length)

            if buffer.Length > int64 limit then
                malformed path $"the file exceeds the bounded maximum of {limit} bytes"
            else
                Ok(buffer.ToArray())
    with
    | :? IOException as error -> Error(FileFailure("read", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))

let private checkBounded (path: string) (limit: int) (bytes: byte[]) : Result<unit, ReleaseError> =
    if isNull bytes then
        malformed path "the file bytes are null"
    elif bytes.Length > limit then
        malformed path $"the file exceeds the bounded maximum of {limit} bytes (got {bytes.Length})"
    else
        match bytes |> Array.tryFindIndex ((=) 0uy) with
        | Some offset -> malformed path $"the file contains an embedded NUL byte at offset {offset}"
        | None -> Ok()

// Only the fields that select the exact stable SDK are authoritative: one `sdk` object holding one `version`, `rollForward`
// and `allowPrerelease`. A repeated authoritative key is ambiguous and rejected; unrelated valid fields are allowed.
// `JsonDocument` keeps repeated members, so the count is taken by enumeration.
let private singleMember (element: JsonElement) (name: string) : Result<JsonElement, string> =
    let found =
        element.EnumerateObject()
        |> Seq.filter (fun property -> String.Equals(property.Name, name, StringComparison.Ordinal))
        |> Seq.map (fun property -> property.Value)
        |> Seq.toList

    match found with
    | [ value ] -> Ok value
    | [] -> Error $"the '{name}' member is missing"
    | _ -> Error $"the authoritative key '{name}' is repeated"

let private selectSdk (root: JsonElement) : Result<string, string> =
    if root.ValueKind <> JsonValueKind.Object then
        Error "the root must be an object"
    else
        singleMember root "sdk"
        |> Result.bind (fun sdk ->
            if sdk.ValueKind <> JsonValueKind.Object then
                Error "the 'sdk' member must be an object"
            else
                singleMember sdk "version"
                |> Result.bind (fun version ->
                    singleMember sdk "rollForward"
                    |> Result.bind (fun rollForward ->
                        singleMember sdk "allowPrerelease"
                        |> Result.bind (fun allowPrerelease ->
                            if version.ValueKind <> JsonValueKind.String then
                                Error "the 'sdk.version' value must be a string"
                            elif not (stableVersion.IsMatch(version.GetString())) then
                                Error $"the 'sdk.version' value '{version.GetString()}' is not a stable three-part version"
                            elif rollForward.ValueKind <> JsonValueKind.String || rollForward.GetString() <> "disable" then
                                Error "the 'sdk.rollForward' value must be exactly \"disable\""
                            elif allowPrerelease.ValueKind <> JsonValueKind.False then
                                Error "the 'sdk.allowPrerelease' value must be exactly false"
                            else
                                Ok(version.GetString())))))

let parseGlobalJson (path: string) (bytes: byte[]) : Result<string, ReleaseError> =
    match checkBounded path maxOwnerBytes bytes with
    | Error error -> Error error
    | Ok() ->
        try
            use document =
                JsonDocument.Parse(
                    ReadOnlyMemory<byte>(bytes),
                    JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16)
                )

            match selectSdk document.RootElement with
            | Ok version -> Ok version
            | Error detail -> malformed path detail
        with :? JsonException as error ->
            malformed path $"the file is not valid strict JSON: {error.Message}"

let globalJsonPath (repositoryRoot: string) = Path.Combine(repositoryRoot, globalJsonName)

// The authored SDK version, read without any SDK or process.
let readSdkVersion (repositoryRoot: string) : Result<string, ReleaseError> =
    let path = globalJsonPath repositoryRoot
    readBounded path maxOwnerBytes |> Result.bind (parseGlobalJson path)

// Decodes the JSON that `dotnet msbuild -getProperty -getItem` prints. Exactly one `KnownFrameworkReference` for
// `Microsoft.NETCore.App` at the evaluated framework must exist and carry a plain `DefaultRuntimeFrameworkVersion`; the
// SDK-bundled framework must equal the evaluated one, so a drifted framework fails closed.
let parseEvaluation (source: string) (output: string) : Result<Evaluation, ReleaseError> =
    if String.IsNullOrWhiteSpace output then
        malformed source "the evaluation produced no output"
    elif output.Length > maxEvaluationBytes then
        malformed source $"the evaluation output exceeds the bounded maximum of {maxEvaluationBytes} characters"
    else
        try
            use document = JsonDocument.Parse output
            let root = document.RootElement

            let text (element: JsonElement) (name: string) =
                match element.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | _ -> None

            let objectMember (element: JsonElement) (name: string) =
                match element.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.Object -> Some value
                | _ -> None

            match (if root.ValueKind = JsonValueKind.Object then objectMember root "Properties" else None) with
            | None -> malformed source "the evaluation output has no Properties object"
            | Some properties ->
                let property name = text properties name |> Option.filter (String.IsNullOrWhiteSpace >> not)

                match property "NETCoreSdkVersion", property "TargetFramework", property "LangVersion", property "BundledNETCoreAppTargetFramework" with
                | Some sdk, Some framework, Some language, Some bundled ->
                    let references =
                        match objectMember root "Items" with
                        | None -> []
                        | Some items ->
                            match items.TryGetProperty "KnownFrameworkReference" with
                            | true, list when list.ValueKind = JsonValueKind.Array ->
                                list.EnumerateArray()
                                |> Seq.filter (fun item ->
                                    item.ValueKind = JsonValueKind.Object
                                    && text item "Identity" = Some "Microsoft.NETCore.App"
                                    && text item "TargetFramework" = Some framework)
                                |> Seq.toList
                            | _ -> []

                    if not (isVersionToken sdk) then
                        malformed source $"the evaluated SDK version '{sdk}' is not a plain version token"
                    elif not (frameworkToken.IsMatch framework) then
                        malformed source $"the evaluated TargetFramework '{framework}' is not a plain netX.Y moniker"
                    elif not (languageToken.IsMatch language) then
                        malformed source $"the evaluated LangVersion '{language}' is not a plain X.Y version"
                    elif bundled <> framework then
                        malformed source $"the SDK's BundledNETCoreAppTargetFramework '{bundled}' differs from the evaluated target framework '{framework}'"
                    else
                        match references with
                        | [] -> malformed source $"no KnownFrameworkReference for Microsoft.NETCore.App and '{framework}' exists"
                        | _ :: _ :: _ ->
                            malformed source $"more than one KnownFrameworkReference for Microsoft.NETCore.App and '{framework}' exists"
                        | [ reference ] ->
                            match text reference "DefaultRuntimeFrameworkVersion" with
                            | None -> malformed source "the matching KnownFrameworkReference carries no DefaultRuntimeFrameworkVersion"
                            | Some value when not (isVersionToken value) ->
                                malformed source $"the DefaultRuntimeFrameworkVersion value '{value}' is not a plain version token"
                            | Some value ->
                                Ok
                                    { EvaluatedSdkVersion = sdk
                                      TargetFramework = framework
                                      LangVersion = language
                                      BundledTargetFramework = bundled
                                      DefaultRuntimeFrameworkVersion = value }
                | _ -> malformed source "the evaluation output lacks NETCoreSdkVersion, TargetFramework, LangVersion or BundledNETCoreAppTargetFramework"
        with :? JsonException as error ->
            malformed source $"the evaluation output is not valid JSON: {error.Message}"

// The toolchain from one evaluation: the evaluated SDK must be the exact version the owner selects.
let toolchainFromEvaluation (ownerSdkVersion: string) (evaluation: Evaluation) : Result<Toolchain, ReleaseError> =
    if not (String.Equals(evaluation.EvaluatedSdkVersion, ownerSdkVersion, StringComparison.Ordinal)) then
        Error(IdentityMismatch("SDK version", ownerSdkVersion, evaluation.EvaluatedSdkVersion))
    else
        Ok
            { SdkVersion = evaluation.EvaluatedSdkVersion
              RuntimeVersion = evaluation.DefaultRuntimeFrameworkVersion
              Platform = hostPlatform () }

let private readHandoff (isToken: string -> bool) (kind: string) (name: string) : Result<string option, ReleaseError> =
    match Environment.GetEnvironmentVariable name with
    | null -> Ok None
    | value when not (isToken value) -> Error(UsageRejected $"{name} must be a plain {kind}")
    | value -> Ok(Some value)

let private readVerified = readHandoff isVersionToken "version token"
let private readVerifiedPlatform = readHandoff (fun value -> platformToken.IsMatch value) "platform identifier"

// Publication resolution: the verified handoff only. A missing, partial or invalid handoff is an error and never a
// fallback to evaluation, so no subprocess can run beside the publication credential. The owner SDK version is read from
// the file and must equal the handed-over one.
let resolvePublication (repositoryRoot: string) : Result<Toolchain, ReleaseError> =
    match readVerified verifiedSdkVariable, readVerified verifiedRuntimeVariable, readVerifiedPlatform verifiedPlatformVariable with
    | Error error, _, _
    | _, Error error, _
    | _, _, Error error -> Error error
    | Ok(Some sdk), Ok(Some runtime), Ok(Some platform) ->
        match readSdkVersion repositoryRoot with
        | Error error -> Error error
        | Ok owner when not (String.Equals(owner, sdk, StringComparison.Ordinal)) ->
            Error(IdentityMismatch(verifiedSdkVariable, owner, sdk))
        | Ok _ ->
            Ok
                { SdkVersion = sdk
                  RuntimeVersion = runtime
                  Platform = platform }
    | Ok None, _, _ -> Error(UsageRejected $"{verifiedSdkVariable} is not set; the verification step must hand over the verified toolchain")
    | _, Ok None, _ -> Error(UsageRejected $"{verifiedRuntimeVariable} is not set; the verification step must hand over the verified toolchain")
    | _, _, Ok None -> Error(UsageRejected $"{verifiedPlatformVariable} is not set; the verification step must hand over the verified toolchain")

// Runs the native evaluation of the release project from the trusted repository root, with no build target.
let evaluate (repositoryRoot: string) : Async<Result<Evaluation, ReleaseError>> =
    async {
        match publicationAuthorityNames |> List.tryFind (fun name -> not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable name))) with
        | Some name -> return Error(UsageRejected $"native evaluation refuses to start because '{name}' is set")
        | None ->
            let! token = Async.CancellationToken

            match! BuildProvenance.runProcess repositoryRoot "dotnet" evaluationArguments token with
            | Error error -> return Error error
            | Ok output -> return parseEvaluation (Path.Combine(repositoryRoot, evaluatedProjectPath)) output
    }

// The evaluated facts once the native SDK is confirmed to be the exact version the owner selects.
type Verified =
    { Toolchain: Toolchain
      TargetFramework: string
      LangVersion: string }

// Credential-free verification: one native evaluation compared with the on-disk owner.
let verifyLocal (repositoryRoot: string) : Async<Result<Verified, ReleaseError>> =
    async {
        match readSdkVersion repositoryRoot with
        | Error error -> return Error error
        | Ok owner ->
            match! evaluate repositoryRoot with
            | Error error -> return Error error
            | Ok evaluation ->
                return
                    toolchainFromEvaluation owner evaluation
                    |> Result.map (fun toolchain ->
                        { Toolchain = toolchain
                          TargetFramework = evaluation.TargetFramework
                          LangVersion = evaluation.LangVersion })
    }

// Local resolution for every entry point except publication. A verified handoff, when either value is present, must be
// complete and agree with the evaluation.
let resolveLocal (repositoryRoot: string) : Async<Result<Toolchain, ReleaseError>> =
    async {
        match readVerified verifiedSdkVariable, readVerified verifiedRuntimeVariable, readVerifiedPlatform verifiedPlatformVariable with
        | Error error, _, _
        | _, Error error, _
        | _, _, Error error -> return Error error
        | Ok handoffSdk, Ok handoffRuntime, Ok handoffPlatform ->
            match handoffSdk, handoffRuntime with
            | Some _, None -> return Error(UsageRejected $"{verifiedRuntimeVariable} is not set while {verifiedSdkVariable} is")
            | None, Some _ -> return Error(UsageRejected $"{verifiedSdkVariable} is not set while {verifiedRuntimeVariable} is")
            | _ ->
                match! verifyLocal repositoryRoot with
                | Error error -> return Error error
                | Ok verified ->
                    let toolchain = verified.Toolchain

                    match handoffSdk, handoffRuntime, handoffPlatform with
                    | Some sdk, _, _ when not (String.Equals(sdk, toolchain.SdkVersion, StringComparison.Ordinal)) ->
                        return Error(IdentityMismatch(verifiedSdkVariable, toolchain.SdkVersion, sdk))
                    | _, Some runtime, _ when not (String.Equals(runtime, toolchain.RuntimeVersion, StringComparison.Ordinal)) ->
                        return Error(IdentityMismatch(verifiedRuntimeVariable, toolchain.RuntimeVersion, runtime))
                    | _, _, Some platform when not (String.Equals(platform, toolchain.Platform, StringComparison.Ordinal)) ->
                        return Error(IdentityMismatch(verifiedPlatformVariable, toolchain.Platform, platform))
                    | _ -> return Ok toolchain
    }
