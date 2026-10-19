// role: reusable module/helper
// Strict reader for a producer's canonical Config.json policy plus the trusted
// distribution contract derived from it. Pure: no processes or filesystem writes.
module ReleasePolicy

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open SharedFailure
open BuildProvenance
open DistributionContract

type PolicyProjection =
    { ComponentId: string
      Version: string
      EntryDll: string
      SdkVersion: string
      RuntimeVersion: string
      Platform: string
      ArchiveName: string
      AssetUri: string
      PublishedFiles: string list
      LaunchArguments: LaunchArgument list
      SourcePath: string
      SourceSha256: string }

let private requiredFields =
    [ "componentId"
      "version"
      "entryDll"
      "sdkVersion"
      "runtimeVersion"
      "platform"
      "archiveName"
      "assetUri"
      "publishedFiles"
      "launchArguments" ]

let private maxSourceBytes = 16 * 1024 * 1024

let private sha256Hex (bytes: byte array) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private malformed sourcePath detail = Error(MalformedArtifact(sourcePath, detail))

let parseBytes (sourcePath: string) (boundedBytes: byte array) : Result<PolicyProjection, ReleaseError> =
    if isNull boundedBytes then
        malformed sourcePath "the canonical Config.json bytes are null"
    elif boundedBytes.Length > maxSourceBytes then
        malformed sourcePath $"the canonical Config.json exceeds the bounded maximum of {maxSourceBytes} bytes (got {boundedBytes.Length})"
    else
        match boundedBytes |> Array.tryFindIndex ((=) 0uy) with
        | Some offset -> malformed sourcePath $"the canonical Config.json contains an embedded NUL byte at offset {offset}"
        | None ->
            try
                use document =
                    JsonDocument.Parse(
                        ReadOnlyMemory<byte>(boundedBytes),
                        JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8)
                    )

                let root = document.RootElement

                if root.ValueKind <> JsonValueKind.Object then
                    malformed sourcePath "the canonical Config.json root must be an object"
                else
                    let properties = Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    let mutable failure: string option = None

                    for property in root.EnumerateObject() do
                        if properties.ContainsKey property.Name then
                            failure <- Some $"the canonical Config.json contains duplicate field '{property.Name}'"
                        elif not (List.contains property.Name requiredFields) then
                            failure <- Some $"the canonical Config.json contains unknown field '{property.Name}'"
                        else
                            properties.Add(property.Name, property.Value)

                    for field in requiredFields do
                        if not (properties.ContainsKey field) && failure.IsNone then
                            failure <- Some $"the canonical Config.json is missing required field '{field}'"

                    let readString field =
                        match properties.TryGetValue field with
                        | true, element when element.ValueKind = JsonValueKind.String ->
                            let value = element.GetString()

                            if isNull value then Error $"field '{field}' must be a non-null string"
                            elif value.Contains('\000') then Error $"field '{field}' contains a NUL character"
                            else Ok value
                        | true, _ -> Error $"field '{field}' must be a string"
                        | _ -> Error $"field '{field}' is missing"

                    let readPublishedFiles () =
                        match properties.TryGetValue "publishedFiles" with
                        | true, element when element.ValueKind = JsonValueKind.Array ->
                            let values = ResizeArray<string>()

                            for item in element.EnumerateArray() do
                                if item.ValueKind <> JsonValueKind.String then
                                    failure <- Some "field 'publishedFiles' must contain only strings"
                                else
                                    let value = item.GetString()

                                    if isNull value || value.Contains('\000') then
                                        failure <- Some "field 'publishedFiles' contains a null or NUL string"
                                    else
                                        values.Add value

                            values |> Seq.toList
                        | _ ->
                            failure <- Some "field 'publishedFiles' must be an array of strings"
                            []

                    // The launch descriptors are authored policy: an ordered list of
                    // required flag/input/type triples with a closed value-kind set.
                    let readLaunchArguments () =
                        match properties.TryGetValue "launchArguments" with
                        | true, element when element.ValueKind = JsonValueKind.Array ->
                            let values = ResizeArray<LaunchArgument>()

                            for item in element.EnumerateArray() do
                                if item.ValueKind <> JsonValueKind.Object then
                                    failure <- Some "field 'launchArguments' entries must be JSON objects"
                                else
                                    let readStringMember (name: string) =
                                        match item.TryGetProperty name with
                                        | true, memberValue when memberValue.ValueKind = JsonValueKind.String ->
                                            let value = memberValue.GetString()

                                            if String.IsNullOrWhiteSpace value then
                                                failure <- Some $"field 'launchArguments' entry '{name}' must be a non-empty string"
                                                None
                                            else
                                                Some value
                                        | _ ->
                                            failure <- Some $"field 'launchArguments' entry '{name}' must be a non-empty string"
                                            None

                                    let known =
                                        [ "flag"; "input"; "type" ]
                                        |> List.forall (fun name ->
                                            item.EnumerateObject() |> Seq.exists (fun property -> property.Name = name))

                                    let unknown =
                                        item.EnumerateObject()
                                        |> Seq.map (fun property -> property.Name)
                                        |> Seq.tryFind (fun name -> not (List.contains name [ "flag"; "input"; "type" ]))

                                    if not known then
                                        failure <- Some "field 'launchArguments' entries need a 'flag', 'input', and 'type'"
                                    elif unknown.IsSome then
                                        failure <- Some $"field 'launchArguments' entry contains unknown field '{unknown.Value}'"
                                    else
                                        match readStringMember "flag", readStringMember "input", readStringMember "type" with
                                        | Some flag, Some input, Some typeName ->
                                            match LaunchArgumentType.tryParse typeName with
                                            | Some argumentType -> values.Add { Flag = flag; Input = input; Type = argumentType }
                                            | None ->
                                                failure <-
                                                    Some $"field 'launchArguments' type '{typeName}' is not one of absolute-file, absolute-directory, sha256"
                                        | _ -> ()

                            let parsed = values |> Seq.toList

                            if parsed.Length <> (parsed |> List.map (fun argument -> argument.Flag) |> List.distinct |> List.length) then
                                failure <- Some "field 'launchArguments' repeats an argument flag"

                            if parsed.Length <> (parsed |> List.map (fun argument -> argument.Input) |> List.distinct |> List.length) then
                                failure <- Some "field 'launchArguments' repeats an argument input name"

                            if parsed.IsEmpty && failure.IsNone then
                                failure <- Some "field 'launchArguments' must declare at least one required argument"

                            parsed
                        | _ ->
                            failure <- Some "field 'launchArguments' must be an array of objects"
                            []

                    let scalarFields =
                        [ "componentId"; "version"; "entryDll"; "sdkVersion"; "runtimeVersion"; "platform"; "archiveName"; "assetUri" ]

                    let scalars = scalarFields |> List.map (fun field -> field, readString field)

                    let scalarFailure =
                        scalars
                        |> List.tryPick (function
                            | _, Error detail -> Some detail
                            | _ -> None)

                    match failure, scalarFailure with
                    | Some detail, _ -> malformed sourcePath detail
                    | None, Some detail -> malformed sourcePath detail
                    | None, None ->
                        let valueOf field =
                            scalars
                            |> List.pick (function
                                | name, Ok value when name = field -> Some value
                                | _ -> None)

                        let publishedFiles = readPublishedFiles ()
                        let launchArguments = readLaunchArguments ()

                        match failure with
                        | Some detail -> malformed sourcePath detail
                        | None ->
                            Ok
                                { ComponentId = valueOf "componentId"
                                  Version = valueOf "version"
                                  EntryDll = valueOf "entryDll"
                                  SdkVersion = valueOf "sdkVersion"
                                  RuntimeVersion = valueOf "runtimeVersion"
                                  Platform = valueOf "platform"
                                  ArchiveName = valueOf "archiveName"
                                  AssetUri = valueOf "assetUri"
                                  PublishedFiles = publishedFiles
                                  LaunchArguments = launchArguments
                                  SourcePath = sourcePath
                                  SourceSha256 = sha256Hex boundedBytes }
            with
            | :? JsonException as error -> malformed sourcePath $"the canonical Config.json is not valid strict JSON: {error.Message}"

let private readBounded (path: string) : Result<byte array, ReleaseError> =
    try
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        if stream.Length > int64 maxSourceBytes then
            Error(MalformedArtifact(path, $"the canonical Config.json exceeds the bounded maximum of {maxSourceBytes} bytes (got at least {stream.Length})"))
        else
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 8192
            let mutable count = stream.Read(chunk, 0, chunk.Length)
            while count > 0 do
                buffer.Write(chunk, 0, count)
                count <- stream.Read(chunk, 0, chunk.Length)
            Ok(buffer.ToArray())
    with
    | :? IOException as error -> Error(FileFailure("read", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))

let parse (sourcePath: string) : Result<PolicyProjection, ReleaseError> =
    try
        let absolute = Path.GetFullPath sourcePath
        if not (File.Exists absolute) then Error(MissingArtifact absolute)
        else readBounded absolute |> Result.bind (parseBytes absolute)
    with
    | :? IOException as error -> Error(FileFailure("read", sourcePath, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", sourcePath, error.Message))

// The policy projection into the trusted distribution contract is derived by
// `ReleaseCompare.contractFromPolicy`; this module owns only the
// canonical Config.json parse so the emission scripts never depend on comparison.
