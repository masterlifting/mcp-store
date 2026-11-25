// role: reusable module/helper
// Strict reader for the shared .NET toolchain owners: the SDK version authored in the root `global.json`, the target
// framework, F# language version and Expecto version authored in the root `Directory.Build.props`, and the runtime
// version derived from the pinned SDK's own `Microsoft.NETCoreSdk.BundledVersions.props`.
// The reader takes the SDK root as an explicit input and never discovers it. Discovery is a separate, process-spawning
// step (`discoverSdkRoot`) that only credential-free entry points call; the publication entry point reads the root
// handed over in `MCP_RELEASE_SDK_ROOT` and has no reachable path to the muxer listing.
// The PowerShell mirror `Toolchain.ps1` applies the same rules; both must change together.
module ReleaseToolchain

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml
open System.Xml.Linq
open SharedFailure
open BuildProvenance

// The resolved toolchain every dependent reference derives from: the policy projection, the contract and the manifest.
type Toolchain =
    { SdkVersion: string
      RuntimeVersion: string }

// The closed property set of the root `Directory.Build.props`.
type BuildProperties =
    { TargetFramework: string
      LangVersion: string
      ExpectoVersion: string }

// One `dotnet --list-sdks` line.
type SdkInstall = { Version: string; Root: string }

// The facts read from one SDK's `Microsoft.NETCoreSdk.BundledVersions.props`.
type BundledFacts =
    { BundledTargetFramework: string
      DefaultRuntimeFrameworkVersion: string }

let globalJsonName = "global.json"
let buildPropsName = "Directory.Build.props"
let bundledVersionsName = "Microsoft.NETCoreSdk.BundledVersions.props"

// The single named channel through which the publication entry point receives the SDK root.
let sdkRootVariable = "MCP_RELEASE_SDK_ROOT"

// The closed `Directory.Build.props` property set. `Toolchain.ps1` carries the one PowerShell copy.
let buildPropertyNames = [ "TargetFramework"; "LangVersion"; "ExpectoVersion" ]

let maxOwnerBytes = 16 * 1024
let maxBundledVersionsBytes = 4 * 1024 * 1024

let private versionToken =
    Regex(@"\A[0-9]+(\.[0-9]+)*(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?\z", RegexOptions.CultureInvariant)

let private frameworkToken = Regex(@"\Anet[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)
let private languageToken = Regex(@"\A[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)

let private malformed path detail = Error(MalformedArtifact(path, detail))

// A version that is joined into a path under the SDK root is accepted only in this exact shape: digits separated by
// dots with an optional dot-separated alphanumeric prerelease label. `..`, either separator, an empty component, a
// trailing dot, a drive prefix and a bare prerelease label never match.
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

// `global.json` is read strictly: one `sdk` object with exactly `version`, `rollForward` and `allowPrerelease`, no
// repeated key at any level, no unknown property, and the strict values. `JsonDocument` keeps repeated members, so the
// duplicate rule is enforced here by enumeration.
let parseGlobalJson (path: string) (bytes: byte[]) : Result<string, ReleaseError> =
    match checkBounded path maxOwnerBytes bytes with
    | Error error -> Error error
    | Ok() ->
        try
            use document =
                JsonDocument.Parse(
                    ReadOnlyMemory<byte>(bytes),
                    JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4)
                )

            let root = document.RootElement

            let members (element: JsonElement) =
                let seen = Dictionary<string, JsonElement>(StringComparer.Ordinal)
                let mutable failure: string option = None

                for property in element.EnumerateObject() do
                    if seen.ContainsKey property.Name then
                        failure <- Some $"the key '{property.Name}' is repeated"
                    else
                        seen.Add(property.Name, property.Value)

                match failure with
                | Some detail -> Error detail
                | None -> Ok seen

            if root.ValueKind <> JsonValueKind.Object then
                malformed path "the root must be an object"
            else
                match members root with
                | Error detail -> malformed path detail
                | Ok rootMembers ->
                    match rootMembers.Keys |> Seq.tryFind (fun name -> name <> "sdk") with
                    | Some name -> malformed path $"the unknown property '{name}' is not allowed"
                    | None ->
                        match rootMembers.TryGetValue "sdk" with
                        | false, _ -> malformed path "the 'sdk' object is missing"
                        | true, sdk when sdk.ValueKind <> JsonValueKind.Object -> malformed path "the 'sdk' member must be an object"
                        | true, sdk ->
                            match members sdk with
                            | Error detail -> malformed path detail
                            | Ok sdkMembers ->
                                let expected = [ "version"; "rollForward"; "allowPrerelease" ]

                                match sdkMembers.Keys |> Seq.tryFind (fun name -> not (List.contains name expected)) with
                                | Some name -> malformed path $"the unknown 'sdk' property '{name}' is not allowed"
                                | None ->
                                    match expected |> List.tryFind (fun name -> not (sdkMembers.ContainsKey name)) with
                                    | Some name -> malformed path $"the 'sdk' property '{name}' is missing"
                                    | None ->
                                        let version = sdkMembers["version"]
                                        let rollForward = sdkMembers["rollForward"]
                                        let allowPrerelease = sdkMembers["allowPrerelease"]

                                        if version.ValueKind <> JsonValueKind.String then
                                            malformed path "the 'sdk.version' value must be a string"
                                        elif not (isVersionToken (version.GetString())) then
                                            malformed path $"the 'sdk.version' value '{version.GetString()}' is not a plain version token"
                                        elif rollForward.ValueKind <> JsonValueKind.String || rollForward.GetString() <> "disable" then
                                            malformed path "the 'sdk.rollForward' value must be exactly \"disable\""
                                        elif allowPrerelease.ValueKind <> JsonValueKind.False then
                                            malformed path "the 'sdk.allowPrerelease' value must be exactly false"
                                        else
                                            Ok(version.GetString())
        with :? JsonException as error ->
            malformed path $"the file is not valid strict JSON: {error.Message}"

let private loadXml (path: string) (bytes: byte[]) : Result<XDocument, ReleaseError> =
    try
        let settings = XmlReaderSettings()
        settings.DtdProcessing <- DtdProcessing.Prohibit
        settings.IgnoreComments <- true
        settings.IgnoreProcessingInstructions <- true
        settings.MaxCharactersInDocument <- 8000000L
        use stream = new MemoryStream(bytes, false)
        use reader = XmlReader.Create(stream, settings)
        Ok(XDocument.Load reader)
    with :? XmlException as error ->
        malformed path $"the file is not well-formed XML: {error.Message}"

// `Directory.Build.props` is the owner of exactly {TargetFramework, LangVersion, ExpectoVersion}: one property group of
// plain text elements, no attribute, no repeated or unknown property and a well-formed value for each.
let parseBuildProps (path: string) (bytes: byte[]) : Result<BuildProperties, ReleaseError> =
    match checkBounded path maxOwnerBytes bytes with
    | Error error -> Error error
    | Ok() ->
        match loadXml path bytes with
        | Error error -> Error error
        | Ok document ->
            let root = document.Root

            if isNull root || root.Name <> XName.Get "Project" then
                malformed path "the root element must be an un-namespaced <Project>"
            elif root.HasAttributes then
                malformed path "the <Project> element must carry no attribute"
            else
                let groups = root.Elements() |> Seq.toList

                match groups with
                | [ group ] when group.Name = XName.Get "PropertyGroup" && not group.HasAttributes ->
                    let properties = group.Elements() |> Seq.toList
                    let values = Dictionary<string, string>(StringComparer.Ordinal)
                    let mutable failure: string option = None

                    for property in properties do
                        let name = property.Name.LocalName

                        if failure.IsNone then
                            if property.Name.Namespace <> XNamespace.None || not (List.contains name buildPropertyNames) then
                                failure <- Some $"the unknown property '{name}' is not allowed"
                            elif values.ContainsKey name then
                                failure <- Some $"the property '{name}' is repeated"
                            elif property.HasAttributes || property.HasElements then
                                failure <- Some $"the property '{name}' must be plain text"
                            elif property.Value <> property.Value.Trim() || property.Value.Length = 0 then
                                failure <- Some $"the property '{name}' must be a non-empty value without surrounding whitespace"
                            else
                                values.Add(name, property.Value)

                    match failure with
                    | Some detail -> malformed path detail
                    | None ->
                        match buildPropertyNames |> List.tryFind (fun name -> not (values.ContainsKey name)) with
                        | Some name -> malformed path $"the property '{name}' is missing"
                        | None ->
                            let framework = values["TargetFramework"]
                            let language = values["LangVersion"]
                            let expecto = values["ExpectoVersion"]

                            if not (frameworkToken.IsMatch framework) then
                                malformed path $"the TargetFramework value '{framework}' is not a plain netX.Y moniker"
                            elif not (languageToken.IsMatch language) then
                                malformed path $"the LangVersion value '{language}' is not a plain X.Y version"
                            elif not (isVersionToken expecto) then
                                malformed path $"the ExpectoVersion value '{expecto}' is not a plain version token"
                            else
                                Ok
                                    { TargetFramework = framework
                                      LangVersion = language
                                      ExpectoVersion = expecto }
                | _ -> malformed path "the <Project> must contain exactly one attribute-free <PropertyGroup>"

// The SDK-bundled facts. `KnownFrameworkReference` is matched by exact element name, `Include` and `TargetFramework`
// together, because `KnownRuntimePack` carries the same `Include` and `TargetFramework` and a loose filter would match
// twice. Exactly one reference must match and it must name a plain `DefaultRuntimeFrameworkVersion`. The SDK's own
// `BundledNETCoreAppTargetFramework` must equal the owned framework, so a drifted framework fails closed instead of
// silently selecting a different reference.
let parseBundledVersions (path: string) (targetFramework: string) (bytes: byte[]) : Result<BundledFacts, ReleaseError> =
    match checkBounded path maxBundledVersionsBytes bytes with
    | Error error -> Error error
    | Ok() ->
        match loadXml path bytes with
        | Error error -> Error error
        | Ok document ->
            let elements (name: string) =
                document.Descendants() |> Seq.filter (fun element -> element.Name.LocalName = name) |> Seq.toList

            let attribute (element: XElement) (name: string) =
                match element.Attribute(XName.Get name) with
                | null -> None
                | value -> Some value.Value

            let references =
                elements "KnownFrameworkReference"
                |> List.filter (fun element ->
                    attribute element "Include" = Some "Microsoft.NETCore.App"
                    && attribute element "TargetFramework" = Some targetFramework)

            let bundledFrameworks = elements "BundledNETCoreAppTargetFramework"

            match references, bundledFrameworks with
            | [], _ ->
                malformed path $"no KnownFrameworkReference for Microsoft.NETCore.App and '{targetFramework}' exists"
            | _ :: _ :: _, _ ->
                malformed path $"more than one KnownFrameworkReference for Microsoft.NETCore.App and '{targetFramework}' exists"
            | [ reference ], [ bundled ] ->
                match attribute reference "DefaultRuntimeFrameworkVersion" with
                | None -> malformed path "the matching KnownFrameworkReference carries no DefaultRuntimeFrameworkVersion"
                | Some value when not (isVersionToken value) ->
                    malformed path $"the DefaultRuntimeFrameworkVersion value '{value}' is not a plain version token"
                | Some value when bundled.Value <> targetFramework ->
                    malformed path $"the SDK's BundledNETCoreAppTargetFramework '{bundled.Value}' differs from the owned target framework '{targetFramework}'"
                | Some value ->
                    Ok
                        { BundledTargetFramework = bundled.Value
                          DefaultRuntimeFrameworkVersion = value }
            | [ _ ], _ ->
                malformed path "the SDK must declare exactly one BundledNETCoreAppTargetFramework"

let globalJsonPath (repositoryRoot: string) = Path.Combine(repositoryRoot, globalJsonName)
let buildPropsPath (repositoryRoot: string) = Path.Combine(repositoryRoot, buildPropsName)

// The authored SDK version, read without any SDK or process.
let readSdkVersion (repositoryRoot: string) : Result<string, ReleaseError> =
    let path = globalJsonPath repositoryRoot
    readBounded path maxOwnerBytes |> Result.bind (parseGlobalJson path)

let readBuildProperties (repositoryRoot: string) : Result<BuildProperties, ReleaseError> =
    let path = buildPropsPath repositoryRoot
    readBounded path maxOwnerBytes |> Result.bind (parseBuildProps path)

// The toolchain from the owners and one explicit SDK root: the SDK version from `global.json`, the framework from
// `Directory.Build.props`, and the runtime from the pinned SDK's bundled facts under that root. No process is spawned.
let resolveWithRoot (repositoryRoot: string) (sdkRoot: string) : Result<Toolchain, ReleaseError> =
    match readSdkVersion repositoryRoot, readBuildProperties repositoryRoot with
    | Error error, _ -> Error error
    | _, Error error -> Error error
    | Ok sdkVersion, Ok properties ->
        if String.IsNullOrWhiteSpace sdkRoot || not (Path.IsPathRooted sdkRoot) then
            Error(UsageRejected $"the SDK root '{sdkRoot}' must be an absolute path")
        else
            // The version token is validated by the global.json reader, so this join cannot leave the SDK root.
            let propsPath = Path.Combine(sdkRoot, sdkVersion, bundledVersionsName)

            readBounded propsPath maxBundledVersionsBytes
            |> Result.bind (parseBundledVersions propsPath properties.TargetFramework)
            |> Result.map (fun facts ->
                { SdkVersion = sdkVersion
                  RuntimeVersion = facts.DefaultRuntimeFrameworkVersion })

let private normalizeRoot (root: string) =
    Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

let private sameRoot (left: string) (right: string) =
    let comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    String.Equals(normalizeRoot left, normalizeRoot right, comparison)

let private listingLine = Regex(@"^(?<version>\S+)\s+\[(?<root>.+)\]\s*$", RegexOptions.CultureInvariant)

// Parses `dotnet --list-sdks` output; lines that are not an installation entry are ignored.
let parseSdkListing (output: string) : SdkInstall list =
    if String.IsNullOrEmpty output then
        []
    else
        output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let found = listingLine.Match(line.Trim())

            if found.Success then
                Some { Version = found.Groups["version"].Value; Root = found.Groups["root"].Value }
            else
                None)
        |> Array.toList

// The pinned version must resolve to exactly one root. Appearing under no root or under more than one distinct root
// fails closed: there is no preference order that could silently pick a different installation.
let selectRoot (version: string) (installs: SdkInstall list) : Result<string, ReleaseError> =
    let roots =
        installs
        |> List.filter (fun install -> String.Equals(install.Version, version, StringComparison.Ordinal))
        |> List.map (fun install -> install.Root)
        |> List.fold
            (fun accumulated root -> if accumulated |> List.exists (sameRoot root) then accumulated else accumulated @ [ root ])
            []

    match roots with
    | [ root ] -> Ok(normalizeRoot root)
    | [] -> Error(RuntimeUnavailable("Microsoft.NET.Sdk", version, "the pinned SDK is not installed"))
    | many ->
        let listed = String.concat "; " many

        Error(
            RuntimeUnavailable(
                "Microsoft.NET.Sdk",
                version,
                $"the pinned SDK is installed under more than one root [{listed}]; refusing to choose one"
            )
        )

// The root handed over by the credential-free verification step; the only channel the publication entry point reads.
let sdkRootFromEnvironment () : Result<string, ReleaseError> =
    match Environment.GetEnvironmentVariable sdkRootVariable with
    | null -> Error(UsageRejected $"{sdkRootVariable} is not set; the verification step must hand over the SDK root")
    | value when String.IsNullOrWhiteSpace value -> Error(UsageRejected $"{sdkRootVariable} is empty")
    | value when not (Path.IsPathRooted value) -> Error(UsageRejected $"{sdkRootVariable} must be an absolute path")
    | value -> Ok(normalizeRoot value)

// Publication resolution: the explicit handoff only. There is no muxer listing on this path, so a workflow edit that
// drops the variable is an error here and never a silent fallback to discovery.
let resolvePublication (repositoryRoot: string) : Result<Toolchain, ReleaseError> =
    sdkRootFromEnvironment () |> Result.bind (resolveWithRoot repositoryRoot)

// Local resolution from an already captured listing. When the handoff is present it must agree with the local result.
let resolveFromListing (repositoryRoot: string) (listing: string) (handoff: string option) : Result<Toolchain, ReleaseError> =
    match readSdkVersion repositoryRoot with
    | Error error -> Error error
    | Ok version ->
        match selectRoot version (parseSdkListing listing) with
        | Error error -> Error error
        | Ok root ->
            match handoff with
            | Some value when not (String.IsNullOrWhiteSpace value) && not (sameRoot value root) ->
                Error(IdentityMismatch(sdkRootVariable, root, normalizeRoot value))
            | _ -> resolveWithRoot repositoryRoot root

// The only function in this module that spawns a process. It is called by credential-free entry points only (prepare,
// the producer scripts, the compiled tests and the Unix lane); the publication entry point never reaches it.
let discoverSdkListing (repositoryRoot: string) : Async<Result<string, ReleaseError>> =
    async {
        let! token = Async.CancellationToken
        return! BuildProvenance.runProcess repositoryRoot "dotnet" [ "--list-sdks" ] token
    }

// Local resolution for every entry point except publication.
let resolveLocal (repositoryRoot: string) : Async<Result<Toolchain, ReleaseError>> =
    async {
        match! discoverSdkListing repositoryRoot with
        | Error error -> return Error error
        | Ok listing ->
            let handoff =
                match Environment.GetEnvironmentVariable sdkRootVariable with
                | null -> None
                | value -> Some value

            return resolveFromListing repositoryRoot listing handoff
    }

// Blocking convenience for the FSI scripts, whose top level is synchronous.
let resolveLocalSynchronously (repositoryRoot: string) : Result<Toolchain, ReleaseError> =
    resolveLocal repositoryRoot |> Async.RunSynchronously
