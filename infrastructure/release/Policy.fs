// role: reusable module/helper
module ReleasePolicy

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open BuildProvenance

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
      "publishedFiles" ]

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

                    match failure with
                    | Some detail -> malformed sourcePath detail
                    | None ->
                        let fields = requiredFields |> List.filter ((<>) "publishedFiles") |> List.map (fun field -> field, readString field)
                        match fields |> List.tryPick (fun (field, result) -> match result with Error detail -> Some(field, detail) | Ok _ -> None) with
                        | Some(_, detail) -> malformed sourcePath detail
                        | None ->
                            let values = fields |> List.map (fun (_, result) -> match result with Ok value -> value | Error _ -> "") |> List.toArray
                            let publishedFiles = readPublishedFiles ()
                            match failure with
                            | Some detail -> malformed sourcePath detail
                            | None ->
                                Ok
                                    { ComponentId = values.[0]
                                      Version = values.[1]
                                      EntryDll = values.[2]
                                      SdkVersion = values.[3]
                                      RuntimeVersion = values.[4]
                                      Platform = values.[5]
                                      ArchiveName = values.[6]
                                      AssetUri = values.[7]
                                      PublishedFiles = publishedFiles
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
