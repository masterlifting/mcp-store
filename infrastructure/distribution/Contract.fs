// role: reusable module/helper
// Keep consumer lifecycle and side effects outside the immutable producer contract.
module DistributionContract

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open SharedFailure

// A future schema change must update this constant, the parser, and the renderer together.
let schemaVersion = 2
let manifestEntryName = "distribution.json"
let noticeEntryName = "NOTICE.txt"
let launchKindDotnetExec = "dotnet-exec"
let runtimeFramework = "Microsoft.NETCore.App"

// The launch argument value kinds are a closed set: a consumer can resolve every
// descriptor without producer knowledge, and no shell template is admitted.
type LaunchArgumentType =
    | AbsoluteFile
    | AbsoluteDirectory
    | Sha256

module LaunchArgumentType =
    let wireName (value: LaunchArgumentType) : string =
        match value with
        | AbsoluteFile -> "absolute-file"
        | AbsoluteDirectory -> "absolute-directory"
        | Sha256 -> "sha256"

    let private names = [ AbsoluteFile; AbsoluteDirectory; Sha256 ]

    let tryParse (value: string) : LaunchArgumentType option =
        names |> List.tryFind (fun candidate -> wireName candidate = value)

type LaunchArgument =
    { Flag: string
      Input: string
      Type: LaunchArgumentType }

type LaunchDescriptor =
    { Kind: string
      Entrypoint: string
      Arguments: LaunchArgument list }

// Build provenance only: it is not a consumer SDK requirement or a supported-platform claim.
type ProducerProvenance =
    { Sdk: string
      Runtime: string
      Platform: string }

// The packaged runtime prerequisite; framework and version are derived from the
// hashed runtime configuration rather than assumed from the build toolchain.
type RuntimeRequirement =
    { Framework: string
      Version: string
      Config: string }

type DistributionFile =
    { Path: string
      Sha256: string }

type DistributionManifest =
    { SchemaVersion: int
      Id: string
      Version: string
      Revision: string
      SourceRepository: string
      Producer: ProducerProvenance
      Archive: string
      Launch: LaunchDescriptor
      Runtime: RuntimeRequirement
      Files: DistributionFile list }

// F# generic string comparison is ordinal; the contract states the ordering
// explicitly so emission and validation cannot drift from it.
let sortOrdinal (values: string list) : string list =
    List.sortWith (fun left right -> String.CompareOrdinal(left, right)) values

// Archive membership is exactly the declared member files plus the canonical manifest itself.
let archiveMemberNames (manifest: DistributionManifest) : string list =
    let paths = manifest.Files |> List.map (fun file -> file.Path)
    sortOrdinal (paths @ [ manifestEntryName ])

// The trusted-policy counterpart of `archiveMemberNames`: the policy allowlist,
// the always-present NOTICE, and the manifest entry.
let trustedArchiveMemberNames (publishedFiles: string list) : string list =
    sortOrdinal (publishedFiles @ [ noticeEntryName; manifestEntryName ])

// Safe slash-separated relative member paths only: no rooted, drive-qualified,
// backslash, empty, current, parent, control, or non-ASCII segment is admitted.
let isSafeRelativePath (value: string) : bool =
    not (String.IsNullOrWhiteSpace value)
    && not (value.Contains '\\')
    && not (value.Contains ':')
    && not (value.StartsWith("/", StringComparison.Ordinal))
    && not (value.EndsWith("/", StringComparison.Ordinal))
    && (value.Split '/'
        |> Array.forall (fun segment ->
            segment <> ""
            && segment <> "."
            && segment <> ".."
            && (segment |> Seq.forall (fun character -> int character > 31 && int character < 127))))

let isLowerHexDigits (length: int) (value: string) : bool =
    not (isNull value)
    && value.Length = length
    && value |> Seq.forall (fun character -> (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

let isSha256 (value: string) : bool = isLowerHexDigits 64 value
let isRevision (value: string) : bool = isLowerHexDigits 40 value

let private lower (value: string) : string = if isNull value then "" else value.ToLowerInvariant()

// The packaged runtime configuration of the entry assembly; it is the authoritative
// source of the runtime prerequisite and stays hashed by `files`.
let runtimeConfigNameFor (entrypoint: string) : string =
    Path.ChangeExtension(entrypoint, ".runtimeconfig.json")

let private tryProperty (element: JsonElement) (name: string) : JsonElement option =
    let mutable found = None

    for property in element.EnumerateObject() do
        if property.Name = name && found.IsNone then
            found <- Some property.Value

    found

let private stringMemberOrNull (element: JsonElement) (name: string) : string =
    match tryProperty element name with
    | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
    | _ -> null

// A duplicate key anywhere in the document is rejected so two readers cannot silently disagree; the pin parser reuses this routine.
let rejectDuplicateKeys (root: JsonElement) : Result<unit, string> =
    let rec walk (element: JsonElement) : Result<unit, string> =
        match element.ValueKind with
        | JsonValueKind.Object ->
            let seen = HashSet<string>(StringComparer.Ordinal)
            let mutable failure = None

            for property in element.EnumerateObject() do
                if failure.IsNone then
                    if not (seen.Add property.Name) then
                        failure <- Some $"duplicate JSON key '{property.Name}'"
                    else
                        match walk property.Value with
                        | Error message -> failure <- Some message
                        | Ok () -> ()

            match failure with
            | Some message -> Error message
            | None -> Ok()
        | JsonValueKind.Array ->
            let mutable failure = None

            for item in element.EnumerateArray() do
                if failure.IsNone then
                    match walk item with
                    | Error message -> failure <- Some message
                    | Ok () -> ()

            match failure with
            | Some message -> Error message
            | None -> Ok()
        | _ -> Ok()

    walk root

// Derives the runtime prerequisite from the packaged runtime configuration bytes,
// so the manifest never assumes an SDK-to-runtime correspondence.
let runtimeRequirementFromConfig (configName: string) (bytes: byte[]) : Result<RuntimeRequirement, ReleaseError> =
    let malformed detail = Error(MalformedArtifact(configName, detail))

    try
        use document =
            JsonDocument.Parse(
                ReadOnlyMemory<byte> bytes,
                JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8)
            )

        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            malformed "the packaged runtime configuration is not a JSON object"
        else
            // A duplicate key would otherwise make the derived prerequisite depend on property order.
            match rejectDuplicateKeys root with
            | Error detail -> malformed detail
            | Ok () ->
                match tryProperty root "runtimeOptions" with
                | Some options when options.ValueKind = JsonValueKind.Object ->
                    match tryProperty options "framework" with
                    | Some framework when framework.ValueKind = JsonValueKind.Object ->
                        let name = stringMemberOrNull framework "name"
                        let version = stringMemberOrNull framework "version"

                        if String.IsNullOrWhiteSpace name then
                            malformed "the packaged runtime configuration is missing 'runtimeOptions.framework.name'"
                        elif String.IsNullOrWhiteSpace version then
                            malformed "the packaged runtime configuration is missing 'runtimeOptions.framework.version'"
                        else
                            Ok
                                { Framework = name
                                  Version = version
                                  Config = configName }
                    | _ -> malformed "the packaged runtime configuration declares no 'runtimeOptions.framework' object"
                | _ -> malformed "the packaged runtime configuration declares no 'runtimeOptions' object"
    with :? JsonException as error ->
        malformed $"the packaged runtime configuration is not valid JSON: {error.Message}"

// Bounded arbitrary-root resolution: a validated relative member is composed
// beneath an explicitly supplied root and must stay inside it. This resolves
// metadata only; installation-root selection is outside this contract.
let resolveUnderRoot (root: string) (relativePath: string) : Result<string, ReleaseError> =
    if String.IsNullOrWhiteSpace relativePath || not (isSafeRelativePath relativePath) then
        Error(MalformedArtifact(relativePath, "the distribution member is not a safe slash-separated relative path"))
    elif String.IsNullOrWhiteSpace root then
        Error(MalformedArtifact(root, "the extraction root is empty"))
    else
        try
            let rootFull = Path.GetFullPath root
            let composed = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            let prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + string Path.DirectorySeparatorChar

            if composed.StartsWith(prefix, StringComparison.Ordinal) then
                Ok composed
            else
                Error(MalformedArtifact(relativePath, $"the distribution member resolves outside the extraction root '{rootFull}'"))
        with
        | :? ArgumentException as error -> Error(MalformedArtifact(relativePath, error.Message))
        | :? NotSupportedException as error -> Error(MalformedArtifact(relativePath, error.Message))
        | :? PathTooLongException as error -> Error(MalformedArtifact(relativePath, error.Message))

// Canonical asset URIs encode the single repository a release is published from; a
// source-repository claim is only meaningful when it names that repository.
let sourceRepositoryFromAssetUri (assetUri: string) : Result<string, ReleaseError> =
    let separator = "/releases/download/"

    if String.IsNullOrWhiteSpace assetUri then
        Error(MalformedArtifact("assetUri", "field 'assetUri' must be a non-empty string"))
    else
        let index = assetUri.IndexOf(separator, StringComparison.Ordinal)

        if index <= 0 then
            Error(MalformedArtifact("assetUri", $"assetUri '{assetUri}' is not a '<repository>/releases/download/<tag>/<asset>' URL"))
        else
            Ok(assetUri.Substring(0, index))

// Semantic validation of a parsed or directly constructed manifest: the same rules
// a consumer relies on, independent of the JSON document that produced it.
let validate (manifest: DistributionManifest) : Result<unit, ReleaseError> =
    let malformed detail = Error(MalformedArtifact(manifestEntryName, detail))
    let mutable failure: string option = None
    let fail detail = if failure.IsNone then failure <- Some detail
    let requireNonEmpty name (value: string) =
        if String.IsNullOrWhiteSpace value then fail $"the manifest '{name}' must be a non-empty string"

    if manifest.SchemaVersion <> schemaVersion then
        fail $"unsupported manifest schemaVersion {manifest.SchemaVersion}; expected {schemaVersion}"

    requireNonEmpty "id" manifest.Id
    requireNonEmpty "version" manifest.Version
    requireNonEmpty "archive" manifest.Archive
    requireNonEmpty "sourceRepository" manifest.SourceRepository

    if not (isRevision manifest.Revision) then
        fail "the manifest 'revision' is not a 40-character lowercase hex revision"

    if not (isSafeRelativePath manifest.Archive) then
        fail "the manifest 'archive' is not a safe slash-free single file name"
    elif manifest.Archive.Contains '/' then
        fail "the manifest 'archive' must be a single slash-free file name"

    requireNonEmpty "producer.sdk" manifest.Producer.Sdk
    requireNonEmpty "producer.runtime" manifest.Producer.Runtime
    requireNonEmpty "producer.platform" manifest.Producer.Platform

    if manifest.Launch.Kind <> launchKindDotnetExec then
        fail $"the manifest 'launch.kind' '{manifest.Launch.Kind}' is not '{launchKindDotnetExec}'"

    if not (isSafeRelativePath manifest.Launch.Entrypoint) then
        fail "the manifest 'launch.entrypoint' is not a safe slash-separated relative path"

    if List.isEmpty manifest.Launch.Arguments then
        fail "the manifest 'launch.arguments' must declare at least one required argument"

    if manifest.Launch.Arguments |> List.map (fun argument -> lower argument.Flag) |> List.distinct |> List.length <> manifest.Launch.Arguments.Length then
        fail "the manifest 'launch.arguments' repeats an argument flag"

    if manifest.Launch.Arguments |> List.map (fun argument -> lower argument.Input) |> List.distinct |> List.length <> manifest.Launch.Arguments.Length then
        fail "the manifest 'launch.arguments' repeats an argument input name"

    for argument in manifest.Launch.Arguments do
        if String.IsNullOrWhiteSpace argument.Flag then
            fail "the manifest 'launch.arguments' declares an empty flag"
        elif not (argument.Flag.StartsWith("--", StringComparison.Ordinal)) then
            fail $"the manifest 'launch.arguments' flag '{argument.Flag}' is not a '--'-prefixed flag"

        if String.IsNullOrWhiteSpace argument.Input then
            fail "the manifest 'launch.arguments' declares an empty input name"

    requireNonEmpty "runtime.framework" manifest.Runtime.Framework
    requireNonEmpty "runtime.version" manifest.Runtime.Version

    if not (String.IsNullOrWhiteSpace manifest.Runtime.Framework) && manifest.Runtime.Framework <> runtimeFramework then
        fail $"the manifest 'runtime.framework' '{manifest.Runtime.Framework}' is not '{runtimeFramework}'"

    if not (isSafeRelativePath manifest.Runtime.Config) then
        fail "the manifest 'runtime.config' is not a safe slash-separated relative path"
    elif manifest.Runtime.Config <> runtimeConfigNameFor manifest.Launch.Entrypoint then
        fail "the manifest 'runtime.config' is not the packaged runtime configuration of 'launch.entrypoint'"

    if List.isEmpty manifest.Files then
        fail "the manifest 'files' must declare at least one member"

    let paths = manifest.Files |> List.map _.Path

    if paths |> List.distinct |> List.length <> paths.Length then
        fail "the manifest 'files' repeats a member path"

    if paths |> List.map lower |> List.distinct |> List.length <> paths.Length then
        fail "the manifest 'files' repeats a member path case-insensitively"

    for file in manifest.Files do
        if not (isSafeRelativePath file.Path) then
            fail $"the manifest 'files' path '{file.Path}' is not a safe slash-separated relative path"
        elif lower file.Path = lower manifestEntryName then
            fail $"the manifest 'files' must not declare the '{manifestEntryName}' entry"

        if not (isSha256 file.Sha256) then
            fail $"the manifest 'files' entry '{file.Path}' is not a lowercase 64-character SHA-256"

    if sortOrdinal paths <> paths then
        fail "the manifest 'files' must be sorted by path"

    if paths |> List.forall (fun path -> path <> manifest.Launch.Entrypoint) then
        fail "the manifest 'launch.entrypoint' is not a member of 'files'"

    if paths |> List.forall (fun path -> path <> manifest.Runtime.Config) then
        fail "the manifest 'runtime.config' is not a member of 'files'"

    if paths |> List.forall (fun path -> path <> noticeEntryName) then
        fail $"the manifest 'files' must include the '{noticeEntryName}' member"

    match failure with
    | Some detail -> malformed detail
    | None -> Ok()

// One typed reader over a JSON object; the first failure wins so a malformed
// document reports exactly one deterministic diagnostic.
type private MemberReader(ownerName: string) =
    let mutable failure: string option = None

    member _.Failure = failure
    member _.Fail(detail: string) = if failure.IsNone then failure <- Some detail

    member _.String(owner: JsonElement, name: string) : string =
        match tryProperty owner name with
        | None ->
            failure <- Some $"{ownerName} is missing required field '{name}'"
            ""
        | Some value when value.ValueKind <> JsonValueKind.String ->
            failure <- Some $"{ownerName} field '{name}' is not a string"
            ""
        | Some value ->
            let text = value.GetString()

            if isNull text then
                failure <- Some $"{ownerName} field '{name}' is null"
                ""
            else
                text

    member _.Int(owner: JsonElement, name: string) : int =
        let notAnInteger () =
            failure <- Some $"{ownerName} field '{name}' is not an integer"
            0

        match tryProperty owner name with
        | None ->
            failure <- Some $"{ownerName} is missing required field '{name}'"
            0
        // TryGetInt32 throws instead of returning false for a non-Number kind, so the kind guard comes first.
        | Some value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, parsed -> parsed
            | _ -> notAnInteger ()
        | Some _ -> notAnInteger ()

    member _.Object(owner: JsonElement, name: string) : JsonElement option =
        match tryProperty owner name with
        | None ->
            failure <- Some $"{ownerName} is missing required field '{name}'"
            None
        | Some value when value.ValueKind <> JsonValueKind.Object ->
            failure <- Some $"{ownerName} field '{name}' is not a JSON object"
            None
        | Some value -> Some value

    member _.Array(owner: JsonElement, name: string) : JsonElement option =
        match tryProperty owner name with
        | None ->
            failure <- Some $"{ownerName} is missing required field '{name}'"
            None
        | Some value when value.ValueKind <> JsonValueKind.Array ->
            failure <- Some $"{ownerName} field '{name}' is not a JSON array"
            None
        | Some value -> Some value

    member _.KnownFields(owner: JsonElement, allowed: string list) =
        for property in owner.EnumerateObject() do
            if not (List.contains property.Name allowed) then
                failure <- Some $"{ownerName} contains unknown field '{property.Name}'"

let private knownTopLevelFields =
    [ "schemaVersion"
      "id"
      "version"
      "revision"
      "sourceRepository"
      "producer"
      "archive"
      "launch"
      "runtime"
      "files" ]

let private argumentTypeNames =
    [ AbsoluteFile; AbsoluteDirectory; Sha256 ] |> List.map LaunchArgumentType.wireName |> String.concat ", "

let private parseObject (root: JsonElement) : Result<DistributionManifest, ReleaseError> =
    let reader = MemberReader "the manifest"
    reader.KnownFields(root, knownTopLevelFields)
    let schemaVersion = reader.Int(root, "schemaVersion")
    let id = reader.String(root, "id")
    let version = reader.String(root, "version")
    let revision = reader.String(root, "revision")
    let sourceRepository = reader.String(root, "sourceRepository")
    let archive = reader.String(root, "archive")

    let producer =
        match reader.Object(root, "producer") with
        | None ->
            { Sdk = ""
              Runtime = ""
              Platform = "" }
        | Some element ->
            let producerReader = MemberReader "the manifest 'producer'"
            producerReader.KnownFields(element, [ "sdk"; "runtime"; "platform" ])
            let produced =
                { Sdk = producerReader.String(element, "sdk")
                  Runtime = producerReader.String(element, "runtime")
                  Platform = producerReader.String(element, "platform") }

            match producerReader.Failure with
            | Some detail -> reader.Fail detail
            | None -> ()

            produced

    let launch =
        match reader.Object(root, "launch") with
        | None ->
            { Kind = ""
              Entrypoint = ""
              Arguments = [] }
        | Some element ->
            let launchReader = MemberReader "the manifest 'launch'"
            launchReader.KnownFields(element, [ "kind"; "entrypoint"; "arguments" ])
            let kind = launchReader.String(element, "kind")
            let entrypoint = launchReader.String(element, "entrypoint")

            let arguments =
                match launchReader.Array(element, "arguments") with
                | None -> []
                | Some array ->
                    [ for item in array.EnumerateArray() ->
                          let argumentReader = MemberReader "each manifest 'launch.arguments' entry"

                          if item.ValueKind <> JsonValueKind.Object then
                              argumentReader.Fail "each manifest 'launch.arguments' entry must be a JSON object"

                              { Flag = ""
                                Input = ""
                                Type = LaunchArgumentType.AbsoluteFile }
                          else
                              argumentReader.KnownFields(item, [ "flag"; "input"; "type" ])
                              let flag = argumentReader.String(item, "flag")
                              let input = argumentReader.String(item, "input")
                              let typeName = argumentReader.String(item, "type")

                              let argumentType =
                                  match LaunchArgumentType.tryParse typeName with
                                  | Some parsed -> parsed
                                  | None ->
                                      argumentReader.Fail
                                          $"the manifest 'launch.arguments' type '{typeName}' is not one of {argumentTypeNames}"

                                      LaunchArgumentType.AbsoluteFile

                              match argumentReader.Failure with
                              | Some detail -> launchReader.Fail detail
                              | None -> ()

                              { Flag = flag
                                Input = input
                                Type = argumentType } ]

            match launchReader.Failure with
            | Some detail -> reader.Fail detail
            | None -> ()

            { Kind = kind
              Entrypoint = entrypoint
              Arguments = arguments }

    let runtime =
        match reader.Object(root, "runtime") with
        | None ->
            { Framework = ""
              Version = ""
              Config = "" }
        | Some element ->
            let runtimeReader = MemberReader "the manifest 'runtime'"
            runtimeReader.KnownFields(element, [ "framework"; "version"; "config" ])
            let requirement =
                { Framework = runtimeReader.String(element, "framework")
                  Version = runtimeReader.String(element, "version")
                  Config = runtimeReader.String(element, "config") }

            match runtimeReader.Failure with
            | Some detail -> reader.Fail detail
            | None -> ()

            requirement

    let files =
        match reader.Array(root, "files") with
        | None -> []
        | Some array ->
            [ for item in array.EnumerateArray() ->
                  let fileReader = MemberReader "each manifest 'files' entry"

                  if item.ValueKind <> JsonValueKind.Object then
                      fileReader.Fail "each manifest 'files' entry must be a JSON object"

                      { Path = ""
                        Sha256 = "" }
                  else
                      fileReader.KnownFields(item, [ "path"; "sha256" ])
                      let file =
                          { Path = fileReader.String(item, "path")
                            Sha256 = fileReader.String(item, "sha256") }

                      match fileReader.Failure with
                      | Some detail -> reader.Fail detail
                      | None -> ()

                      file ]

    let manifest =
        { SchemaVersion = schemaVersion
          Id = id
          Version = version
          Revision = revision
          SourceRepository = sourceRepository
          Producer = producer
          Archive = archive
          Launch = launch
          Runtime = runtime
          Files = files }

    match reader.Failure with
    | Some detail -> Error(MalformedArtifact(manifestEntryName, detail))
    | None -> validate manifest |> Result.map (fun () -> manifest)

// The embedded manifest is strict JSON; a duplicate key, unknown field, missing
// field, wrong type, or semantic mismatch fails closed.
let parseBytes (bytes: byte[]) : Result<DistributionManifest, ReleaseError> =
    let malformed detail = Error(MalformedArtifact(manifestEntryName, detail))

    try
        use document =
            JsonDocument.Parse(
                ReadOnlyMemory<byte> bytes,
                JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8)
            )

        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            malformed "the manifest is not a JSON object"
        else
            match rejectDuplicateKeys root with
            | Error detail -> malformed detail
            | Ok () -> parseObject root
    with :? JsonException as error ->
        malformed $"the embedded manifest is not valid JSON: {error.Message}"

let parse (text: string) : Result<DistributionManifest, ReleaseError> =
    if isNull text then
        Error(MalformedArtifact(manifestEntryName, "the manifest is empty"))
    else
        parseBytes (Encoding.UTF8.GetBytes text)

// Canonical rendering: fixed key order, sorted member files, and the shared
// indented formatting keep the standalone and embedded bytes deterministic.
let toJsonNode (manifest: DistributionManifest) : JsonNode =
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create manifest.SchemaVersion
    node["id"] <- JsonValue.Create manifest.Id
    node["version"] <- JsonValue.Create manifest.Version
    node["revision"] <- JsonValue.Create manifest.Revision
    node["sourceRepository"] <- JsonValue.Create manifest.SourceRepository
    let producer = JsonObject()
    producer["sdk"] <- JsonValue.Create manifest.Producer.Sdk
    producer["runtime"] <- JsonValue.Create manifest.Producer.Runtime
    producer["platform"] <- JsonValue.Create manifest.Producer.Platform
    node["producer"] <- producer
    node["archive"] <- JsonValue.Create manifest.Archive
    let launch = JsonObject()
    launch["kind"] <- JsonValue.Create manifest.Launch.Kind
    launch["entrypoint"] <- JsonValue.Create manifest.Launch.Entrypoint
    let arguments = JsonArray()

    for argument in manifest.Launch.Arguments do
        let entry = JsonObject()
        entry["flag"] <- JsonValue.Create argument.Flag
        entry["input"] <- JsonValue.Create argument.Input
        entry["type"] <- JsonValue.Create(LaunchArgumentType.wireName argument.Type)
        arguments.Add entry

    launch["arguments"] <- arguments
    node["launch"] <- launch
    let runtime = JsonObject()
    runtime["framework"] <- JsonValue.Create manifest.Runtime.Framework
    runtime["version"] <- JsonValue.Create manifest.Runtime.Version
    runtime["config"] <- JsonValue.Create manifest.Runtime.Config
    node["runtime"] <- runtime
    let files = JsonArray()

    for file in manifest.Files do
        let entry = JsonObject()
        entry["path"] <- JsonValue.Create file.Path
        entry["sha256"] <- JsonValue.Create file.Sha256
        files.Add entry

    node["files"] <- files
    node

let render (manifest: DistributionManifest) : string =
    (toJsonNode manifest).ToJsonString(JsonSerializerOptions(WriteIndented = true))
