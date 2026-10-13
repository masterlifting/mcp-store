// role: reusable module/helper
module ReleasePublisherClosure

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance

type PublisherClosure =
    { SchemaVersion: int
      Repository: string
      RosterPath: string
      SourcePath: string
      SourceSha256: string }

let publisherClosureSchemaVersion = 1

let private publisherClosureAllowedFieldNames : Set<string> =
    Set.ofList
        [ "schemaVersion"
          "repository"
          "rosterPath" ]

let private publisherClosureRequiredStringFields =
    [ "schemaVersion"
      "repository"
      "rosterPath" ]

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

let private isLowerHex64 (value: string) : bool =
    if String.IsNullOrEmpty value then false
    elif value.Length <> 64 then false
    else
        let mutable ok = true
        for ch in value do
            if not (Uri.IsHexDigit ch) then ok <- false
        ok

let private readRequiredString (root: JsonObject) (name: string) (sourcePath: string) : Result<string, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the publisher closure is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' is null"))
    else
        try
            let kind = root.[name].GetValueKind()
            if kind <> JsonValueKind.String then
                Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' is not a string"))
            else
                Ok(root.[name].GetValue<string>())
        with _ ->
            Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' could not be read as a string"))

let private readRequiredInt (root: JsonObject) (name: string) (sourcePath: string) : Result<int, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the publisher closure is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' is null"))
    else
        try
            let kind = root.[name].GetValueKind()
            if kind <> JsonValueKind.Number then
                Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' is not a number"))
            else
                Ok(root.[name].GetValue<int>())
        with _ ->
            Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' could not be read as an integer"))

let private readRequiredStringList (root: JsonObject) (name: string) (sourcePath: string) : Result<string list, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the publisher closure is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' is null"))
    else
        let node = root.[name]
        let kind = node.GetValueKind()
        if kind <> JsonValueKind.Array then
            Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' is not a JSON array"))
        else
            try
                let arr = node.AsArray()
                let mutable failure : ReleaseError option = None
                let mutable items : string list = []
                for item in arr do
                    match failure with
                    | Some _ -> ()
                    | None ->
                        if isNull item then
                            failure <- Some(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' contains a null entry"))
                        else
                            let itemKind = item.GetValueKind()
                            if itemKind <> JsonValueKind.String then
                                failure <- Some(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' contains a non-string entry"))
                            else
                                try
                                    items <- (item.GetValue<string>()) :: items
                                with _ ->
                                    failure <- Some(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' contains an unreadable entry"))
                match failure with
                | Some error -> Error error
                | None -> Ok(List.rev items)
            with _ ->
                Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' could not be read as a string list"))

let private readRequiredShaMap (root: JsonObject) (name: string) (sourcePath: string) : Result<Map<string, string>, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the publisher closure is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' is null"))
    else
        let node = root.[name]
        let kind = node.GetValueKind()
        if kind <> JsonValueKind.Object then
            Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' is not a JSON object"))
        else
            try
                let obj = node.AsObject()
                let dict = Dictionary<string, string>(StringComparer.Ordinal)
                let mutable failure : ReleaseError option = None
                for property in obj do
                    match failure with
                    | Some _ -> ()
                    | None ->
                        if isNull property.Value then
                            failure <- Some(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' has a null value at '{property.Key}'"))
                        else
                            let valueKind = property.Value.GetValueKind()
                            if valueKind <> JsonValueKind.String then
                                failure <- Some(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' value at '{property.Key}' is not a string"))
                            else
                                try
                                    let v = property.Value.GetValue<string>()
                                    if not (isLowerHex64 v) then
                                        failure <- Some(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' value at '{property.Key}' is not a 64-character lowercase hex SHA-256"))
                                    else
                                        dict.Add(property.Key, v) |> ignore
                                with _ ->
                                    failure <- Some(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' value at '{property.Key}' is unreadable"))
                match failure with
                | Some error -> Error error
                | None ->
                    if dict.Count = 0 then
                        Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' must contain at least one component entry"))
                    else
                        Ok(dict |> Seq.map (|KeyValue|) |> Map.ofSeq)
            with _ ->
                Error(MalformedArtifact(sourcePath, $"the publisher closure field '{name}' could not be read as an object map"))

let parse (text: string) (sourcePath: string) : Result<PublisherClosure, ReleaseError> =
    let docResult =
        try
            Ok(JsonNode.Parse text)
        with
        | :? JsonException as error ->
            Error(MalformedArtifact(sourcePath, $"the publisher closure is not valid JSON: {error.Message}"))
        | :? FormatException as error ->
            Error(MalformedArtifact(sourcePath, $"the publisher closure is not valid JSON: {error.Message}"))

    match docResult with
    | Error error -> Error error
    | Ok doc when isNull doc ->
        Error(MalformedArtifact(sourcePath, "the publisher closure document is empty"))
    | Ok doc ->
        let root = doc.AsObject()

        let mutable unknown : string list = []
        for property in root do
            if not (Set.contains property.Key publisherClosureAllowedFieldNames) then
                unknown <- property.Key :: unknown
        match unknown with
        | _ :: _ ->
            let detail = String.concat ", " (List.rev unknown)
            Error(MalformedArtifact(sourcePath, $"the publisher closure contains unknown field(s): {detail}"))
        | [] ->
            let mutable failure : ReleaseError option = None
            let mutable schemaVersion = 0
            let mutable repository = ""
            let mutable rosterPath = ""

            let bind label (r: Result<'a, ReleaseError>) : 'a option =
                match failure with
                | Some _ -> None
                | None ->
                    match r with
                    | Ok value -> Some value
                    | Error error ->
                        failure <- Some(MalformedArtifact(sourcePath, $"{label}: {ReleaseError.message error}"))
                        None

            match bind "schemaVersion" (readRequiredInt root "schemaVersion" sourcePath) with
            | Some v -> schemaVersion <- v
            | None -> ()

            match bind "repository" (readRequiredString root "repository" sourcePath) with
            | Some v -> repository <- v
            | None -> ()

            match bind "rosterPath" (readRequiredString root "rosterPath" sourcePath) with
            | Some v -> rosterPath <- v
            | None -> ()

            match failure with
            | Some error -> Error error
            | None ->
                if schemaVersion <> publisherClosureSchemaVersion then
                    Error(
                        MalformedArtifact(
                            sourcePath,
                            $"unsupported publisher closure schema version {schemaVersion}; expected {publisherClosureSchemaVersion}"))
                elif String.IsNullOrWhiteSpace repository then
                    Error(MalformedArtifact(sourcePath, "the publisher closure field 'repository' is empty"))
                elif String.IsNullOrWhiteSpace rosterPath then
                    Error(MalformedArtifact(sourcePath, "the publisher closure field 'rosterPath' is empty"))
                else
                    Ok
                        { SchemaVersion = schemaVersion
                          Repository = repository
                          RosterPath = rosterPath
                          SourcePath = sourcePath
                          SourceSha256 = "" }

let readFromFile (path: string) : Result<PublisherClosure * string, ReleaseError> =
    try
        if not (File.Exists path) then
            Error(MissingArtifact path)
        else
            let bytes = File.ReadAllBytes path
            let sha = sha256Hex bytes
            let text = Encoding.UTF8.GetString bytes
            match parse text path with
            | Error error -> Error error
            | Ok closure ->
                Ok(closure, sha)
    with
    | :? IOException as error -> Error(FileFailure("read", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))
