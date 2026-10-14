// role: reusable module/helper
module ReleaseHandoff

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open BuildProvenance


type OuterDigest = private OuterDigest of string with
    member this.Value = let (OuterDigest v) = this in v
    override this.ToString() = this.Value

type InnerArchiveSha256 = private InnerArchiveSha256 of string with
    member this.Value = let (InnerArchiveSha256 v) = this in v
    override this.ToString() = this.Value

type VerifiedEntryName = private VerifiedEntryName of string with
    member this.Value = let (VerifiedEntryName v) = this in v
    override this.ToString() = this.Value

let trustedPublisherReceiptSchemaVersion = 1
let preparationReceiptSchemaVersion = 1

let manifestEntryName = "distribution.json"
let noticeEntryName = "NOTICE.txt"

let outerTransportMaxBytes = 256L * 1024L * 1024L
let outerEntryCountMax = 4096
let outerEntryUncompressedMax = 256L * 1024L * 1024L
let outerTotalUncompressedMax = 256L * 1024L * 1024L
let outerEntryCompressionRatioMax = 200L

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

let private lowerHex40Pattern =
    Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)

let private lowerHex64Pattern =
    Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

let private isLowerHex40 (value: string) : bool =
    not (String.IsNullOrEmpty value) && lowerHex40Pattern.IsMatch value

let private isLowerHex64 (value: string) : bool =
    not (String.IsNullOrEmpty value) && lowerHex64Pattern.IsMatch value

let makeOuterDigest (value: string) : Result<OuterDigest, ReleaseError> =
    if not (isLowerHex64 value) then
        Error(MalformedArtifact("publish-cli", $"the candidate outer digest '{value}' is not a 64-character lowercase hex SHA-256"))
    else
        Ok(OuterDigest value)

let makeInnerArchiveSha256 (value: string) : Result<InnerArchiveSha256, ReleaseError> =
    if not (isLowerHex64 value) then
        Error(MalformedArtifact("publish", $"the inner archive SHA-256 '{value}' is not a 64-character lowercase hex SHA-256"))
    else
        Ok(InnerArchiveSha256 value)

let private isPathSafe (name: string) : bool =
    if String.IsNullOrEmpty name then false
    elif name.Contains "\\" then false
    elif name.Contains ":" then false
    elif name.StartsWith "/" then false
    else
        let segments = name.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
        let mutable ok = true
        let mutable idx = 0
        while ok && idx < segments.Length do
            let seg = segments.[idx]
            if seg = "" || seg = "." || seg = ".." then
                ok <- false
            elif seg |> Seq.exists (fun c -> int c > 127) then
                ok <- false
            idx <- idx + 1
        ok

let private pathLengthOk (name: string) : bool =
    name.Length <= 256

let private isBoundedRelativeRuntimePath (name: string) : bool =
    pathLengthOk name && isPathSafe name

let private tryGetString (node: JsonNode) : string option =
    try
        if isNull node then None
        else
            match node.GetValueKind() with
            | JsonValueKind.String ->
                let v = node.GetValue<string>()
                if String.IsNullOrWhiteSpace v then None else Some v
            | _ -> None
    with _ -> None

let private tryGetInt (node: JsonNode) : int option =
    try
        if isNull node then None
        else
            match node.GetValueKind() with
            | JsonValueKind.Number ->
                let v = node.GetValue<int>()
                if v < 0 then None else Some v
            | _ -> None
    with _ -> None

let private readRequiredString (label: string) (root: JsonObject) (name: string) (sourcePath: string) : Result<string, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the {label} is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is null"))
    else
        match tryGetString root.[name] with
        | Some v -> Ok v
        | None -> Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is missing, null, or not a non-empty string"))

let private readRequiredInt (label: string) (root: JsonObject) (name: string) (sourcePath: string) : Result<int, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the {label} is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is null"))
    else
        match tryGetInt root.[name] with
        | Some v -> Ok v
        | None -> Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is missing, null, or not a non-negative integer"))

let private readRequiredStringList (label: string) (root: JsonObject) (name: string) (sourcePath: string) : Result<string list, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the {label} is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is null"))
    else
        let node = root.[name]
        let kind = node.GetValueKind()
        if kind <> JsonValueKind.Array then
            Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is not a JSON array"))
        else
            let mutable failure : ReleaseError option = None
            let mutable items : string list = []
            for item in node.AsArray() do
                if failure.IsNone then
                    match tryGetString item with
                    | Some s -> items <- s :: items
                    | None -> failure <- Some(MalformedArtifact(sourcePath, $"the {label} field '{name}' contains a null or non-string entry"))
            match failure with
            | Some error -> Error error
            | None -> Ok(List.rev items)

let private readRequiredStringMap (label: string) (root: JsonObject) (name: string) (sourcePath: string) (allowEmpty: bool) : Result<Map<string, string>, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the {label} is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is null"))
    else
        let node = root.[name]
        let kind = node.GetValueKind()
        if kind <> JsonValueKind.Object then
            Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is not a JSON object"))
        else
            let dict = Dictionary<string, string>(StringComparer.Ordinal)
            let mutable failure : ReleaseError option = None
            for property in node.AsObject() do
                if failure.IsNone then
                    match tryGetString property.Value with
                    | Some s -> dict.Add(property.Key, s) |> ignore
                    | None -> failure <- Some(MalformedArtifact(sourcePath, $"the {label} field '{name}' value at '{property.Key}' is missing, null, or not a non-empty string"))
            match failure with
            | Some error -> Error error
            | None ->
                if not allowEmpty && dict.Count = 0 then
                    Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' must contain at least one entry"))
                else
                    Ok(dict |> Seq.map (|KeyValue|) |> Map.ofSeq)

let private readRequiredIntMap (label: string) (root: JsonObject) (name: string) (sourcePath: string) (allowEmpty: bool) : Result<Map<string, int>, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the {label} is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is null"))
    else
        let node = root.[name]
        let kind = node.GetValueKind()
        if kind <> JsonValueKind.Object then
            Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is not a JSON object"))
        else
            let dict = Dictionary<string, int>(StringComparer.Ordinal)
            let mutable failure : ReleaseError option = None
            for property in node.AsObject() do
                if failure.IsNone then
                    match tryGetInt property.Value with
                    | Some n -> dict.Add(property.Key, n) |> ignore
                    | None -> failure <- Some(MalformedArtifact(sourcePath, $"the {label} field '{name}' value at '{property.Key}' is missing, null, or not a non-negative integer"))
            match failure with
            | Some error -> Error error
            | None ->
                if not allowEmpty && dict.Count = 0 then
                    Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' must contain at least one entry"))
                else
                    Ok(dict |> Seq.map (|KeyValue|) |> Map.ofSeq)

let private parseOuterDigest (label: string) (root: JsonObject) (name: string) (sourcePath: string) : Result<OuterDigest, ReleaseError> =
    match readRequiredString label root name sourcePath with
    | Error e -> Error e
    | Ok v ->
        if not (isLowerHex64 v) then
            Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' '{v}' is not a 64-character lowercase hex SHA-256"))
        else
            Ok(OuterDigest v)

let private readRequiredOuterDigestMap (label: string) (root: JsonObject) (name: string) (sourcePath: string) (allowEmpty: bool) : Result<Map<string, OuterDigest>, ReleaseError> =
    match readRequiredStringMap label root name sourcePath allowEmpty with
    | Error e -> Error e
    | Ok raw ->
        let result = Dictionary<string, OuterDigest>(StringComparer.Ordinal)
        let mutable failure : ReleaseError option = None
        for kv in raw do
            if failure.IsNone then
                if not (isLowerHex64 kv.Value) then
                    failure <- Some(MalformedArtifact(sourcePath, $"the {label} field '{name}' value at '{kv.Key}' is not a 64-character lowercase hex SHA-256"))
                else
                    result.Add(kv.Key, OuterDigest kv.Value) |> ignore
        match failure with
        | Some e -> Error e
        | None -> Ok(result |> Seq.map (|KeyValue|) |> Map.ofSeq)


let private trustedReceiptAllowedFieldNames : Set<string> =
    Set.ofList
        [ "schemaVersion"
          "repository"
          "eventRevision"
          "runId"
          "attempt"
          "trigger"
          "role"
          "rosterPath"
          "rosterSha256"
          "componentConfigPaths"
          "componentConfigShas"
          "runtimeFilePaths"
          "runtimeFileDigests"
          "runtimeFileSizes"
          "builderSdk"
          "builderRuntime"
          "builderPlatform" ]

let private trustedReceiptLabel = "trusted publisher receipt"

type TrustedPublisherReceipt =
    { SchemaVersion: int
      Repository: string
      EventRevision: string
      RunId: string
      Attempt: string
      Trigger: string
      Role: string
      RosterPath: string
      RosterSha256: OuterDigest
      ComponentConfigPaths: Map<string, string>
      ComponentConfigShas: Map<string, OuterDigest>
      RuntimeFilePaths: string list
      RuntimeFileDigests: Map<string, OuterDigest>
      RuntimeFileSizes: Map<string, int>
      BuilderSdk: string
      BuilderRuntime: string
      BuilderPlatform: string
      SourcePath: string
      SourceSha256: string }

let parseTrustedPublisherReceipt (text: string) (sourcePath: string) : Result<TrustedPublisherReceipt, ReleaseError> =
    let docResult =
        try
            Ok(JsonNode.Parse text)
        with
        | :? JsonException as error ->
            Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} is not valid JSON: {error.Message}"))
        | :? FormatException as error ->
            Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} is not valid JSON: {error.Message}"))

    match docResult with
    | Error error -> Error error
    | Ok doc when isNull doc ->
        Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} document is empty"))
    | Ok doc ->
        let root = doc.AsObject()
        let mutable unknown : string list = []
        for property in root do
            if not (Set.contains property.Key trustedReceiptAllowedFieldNames) then
                unknown <- property.Key :: unknown
        match unknown with
        | _ :: _ ->
            let detail = String.concat ", " (List.rev unknown)
            Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} contains unknown field(s): {detail}"))
        | [] ->
            let schemaVersion = readRequiredInt trustedReceiptLabel root "schemaVersion" sourcePath
            let repository = readRequiredString trustedReceiptLabel root "repository" sourcePath
            let eventRevision = readRequiredString trustedReceiptLabel root "eventRevision" sourcePath
            let runId = readRequiredString trustedReceiptLabel root "runId" sourcePath
            let attempt = readRequiredString trustedReceiptLabel root "attempt" sourcePath
            let trigger = readRequiredString trustedReceiptLabel root "trigger" sourcePath
            let role = readRequiredString trustedReceiptLabel root "role" sourcePath
            let rosterPath = readRequiredString trustedReceiptLabel root "rosterPath" sourcePath
            let rosterSha256 = parseOuterDigest trustedReceiptLabel root "rosterSha256" sourcePath
            let componentConfigPaths = readRequiredStringMap trustedReceiptLabel root "componentConfigPaths" sourcePath false
            let componentConfigShas = readRequiredOuterDigestMap trustedReceiptLabel root "componentConfigShas" sourcePath false
            let runtimeFilePaths = readRequiredStringList trustedReceiptLabel root "runtimeFilePaths" sourcePath
            let runtimeFileDigests = readRequiredOuterDigestMap trustedReceiptLabel root "runtimeFileDigests" sourcePath true
            let runtimeFileSizes = readRequiredIntMap trustedReceiptLabel root "runtimeFileSizes" sourcePath true
            let builderSdk = readRequiredString trustedReceiptLabel root "builderSdk" sourcePath
            let builderRuntime = readRequiredString trustedReceiptLabel root "builderRuntime" sourcePath
            let builderPlatform = readRequiredString trustedReceiptLabel root "builderPlatform" sourcePath

            let mutable failure : ReleaseError option = None
            let mutable schemaVersionValue = 0
            let mutable repositoryValue = ""
            let mutable eventRevisionValue = ""
            let mutable runIdValue = ""
            let mutable attemptValue = ""
            let mutable triggerValue = ""
            let mutable roleValue = ""
            let mutable rosterPathValue = ""
            let mutable rosterSha256Value : OuterDigest option = None
            let mutable componentConfigPathsValue : Map<string, string> = Map.empty
            let mutable componentConfigShasValue : Map<string, OuterDigest> = Map.empty
            let mutable runtimeFilePathsValue : string list = []
            let mutable runtimeFileDigestsValue : Map<string, OuterDigest> = Map.empty
            let mutable runtimeFileSizesValue : Map<string, int> = Map.empty
            let mutable builderSdkValue = ""
            let mutable builderRuntimeValue = ""
            let mutable builderPlatformValue = ""

            let bind (r: Result<'a, ReleaseError>) (setter: 'a -> unit) =
                match failure with
                | Some _ -> ()
                | None ->
                    match r with
                    | Ok value -> setter value
                    | Error error -> failure <- Some error

            bind schemaVersion (fun v -> schemaVersionValue <- v)
            bind repository (fun v -> repositoryValue <- v)
            bind eventRevision (fun v -> eventRevisionValue <- v)
            bind runId (fun v -> runIdValue <- v)
            bind attempt (fun v -> attemptValue <- v)
            bind trigger (fun v -> triggerValue <- v)
            bind role (fun v -> roleValue <- v)
            bind rosterPath (fun v -> rosterPathValue <- v)
            bind rosterSha256 (fun v -> rosterSha256Value <- Some v)
            bind componentConfigPaths (fun v -> componentConfigPathsValue <- v)
            bind componentConfigShas (fun v -> componentConfigShasValue <- v)
            bind runtimeFilePaths (fun v -> runtimeFilePathsValue <- v)
            bind runtimeFileDigests (fun v -> runtimeFileDigestsValue <- v)
            bind runtimeFileSizes (fun v -> runtimeFileSizesValue <- v)
            bind builderSdk (fun v -> builderSdkValue <- v)
            bind builderRuntime (fun v -> builderRuntimeValue <- v)
            bind builderPlatform (fun v -> builderPlatformValue <- v)

            match failure with
            | Some error -> Error error
            | None ->
                if schemaVersionValue <> trustedPublisherReceiptSchemaVersion then
                    Error(MalformedArtifact(sourcePath, $"unsupported {trustedReceiptLabel} schema version {schemaVersionValue}; expected {trustedPublisherReceiptSchemaVersion}"))
                elif not (isLowerHex40 eventRevisionValue) then
                    Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} field 'eventRevision' '{eventRevisionValue}' is not a 40-character lowercase hex SHA"))
                else
                    let rosterSha =
                        match rosterSha256Value with
                        | Some v -> v
                        | None -> OuterDigest ""
                    // Missing runtime evidence would weaken the trusted install attestation.
                    let runtimePathsEmpty = List.isEmpty runtimeFilePathsValue
                    let runtimeDigestsEmpty = Map.isEmpty runtimeFileDigestsValue
                    let runtimeSizesEmpty = Map.isEmpty runtimeFileSizesValue
                    if runtimePathsEmpty then
                        Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} declares no runtimeFilePaths; the trusted publisher receipt MUST carry a non-empty cross-consistent runtime triplet"))
                    elif runtimeDigestsEmpty then
                        Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} declares runtimeFilePaths but no runtimeFileDigests"))
                    elif runtimeSizesEmpty then
                        Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} declares runtimeFilePaths but no runtimeFileSizes"))
                    else
                        let mutable invalid : string list = []
                        // Duplicate paths would make the path-to-digest/size mapping ambiguous.
                        let pathSet = HashSet<string>(StringComparer.Ordinal)
                        for path in runtimeFilePathsValue do
                            if not (pathSet.Add path) then
                                invalid <- ("runtimeFilePaths:" + path + " duplicate entry") :: invalid
                        // Absolute or traversing paths must not enter the runtime install attestation.
                        for path in runtimeFilePathsValue do
                            if not (isBoundedRelativeRuntimePath path) then
                                invalid <- ("runtimeFilePaths:" + path + " is not a bounded relative path (no absolute / drive / UNC / backslash / traversal / non-ASCII / oversize)") :: invalid
                        for kv in componentConfigShasValue do
                            if not (Map.containsKey kv.Key componentConfigPathsValue) then
                                invalid <- ("componentConfigShas:" + kv.Key + " missing path") :: invalid
                        for kv in componentConfigPathsValue do
                            if not (Map.containsKey kv.Key componentConfigShasValue) then
                                invalid <- ("componentConfigPaths:" + kv.Key + " missing sha") :: invalid
                        // A size without a matching path is an orphaned runtime entry.
                        for kv in runtimeFileSizesValue do
                            if not (List.contains kv.Key runtimeFilePathsValue) then
                                invalid <- ("runtimeFileSizes:" + kv.Key + " missing path") :: invalid
                        for kv in runtimeFileDigestsValue do
                            if not (List.contains kv.Key runtimeFilePathsValue) then
                                invalid <- ("runtimeFileDigests:" + kv.Key + " missing path") :: invalid
                        for path in runtimeFilePathsValue do
                            if not (Map.containsKey path runtimeFileDigestsValue) then
                                invalid <- ("runtimeFilePaths:" + path + " missing digest") :: invalid
                            elif not (Map.containsKey path runtimeFileSizesValue) then
                                invalid <- ("runtimeFilePaths:" + path + " missing size") :: invalid
                        match invalid with
                        | _ :: _ ->
                            let detail = String.concat ", " (List.rev invalid)
                            Error(MalformedArtifact(sourcePath, $"the {trustedReceiptLabel} has mismatched or malformed entries: {detail}"))
                        | [] ->
                            Ok
                                { SchemaVersion = schemaVersionValue
                                  Repository = repositoryValue
                                  EventRevision = eventRevisionValue
                                  RunId = runIdValue
                                  Attempt = attemptValue
                                  Trigger = triggerValue
                                  Role = roleValue
                                  RosterPath = rosterPathValue
                                  RosterSha256 = rosterSha
                                  ComponentConfigPaths = componentConfigPathsValue
                                  ComponentConfigShas = componentConfigShasValue
                                  RuntimeFilePaths = runtimeFilePathsValue
                                  RuntimeFileDigests = runtimeFileDigestsValue
                                  RuntimeFileSizes = runtimeFileSizesValue
                                  BuilderSdk = builderSdkValue
                                  BuilderRuntime = builderRuntimeValue
                                  BuilderPlatform = builderPlatformValue
                                  SourcePath = sourcePath
                                  SourceSha256 = "" }

let readTrustedPublisherReceiptFromFile (path: string) : Result<TrustedPublisherReceipt, ReleaseError> =
    try
        if not (File.Exists path) then
            Error(MissingArtifact path)
        else
            let bytes = File.ReadAllBytes path
            let sha = sha256Hex bytes
            let text = Encoding.UTF8.GetString bytes
            match parseTrustedPublisherReceipt text path with
            | Error error -> Error error
            | Ok receipt -> Ok { receipt with SourceSha256 = sha }
    with
    | :? IOException as error -> Error(FileFailure("read", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))

let renderTrustedPublisherReceipt (receipt: TrustedPublisherReceipt) : string =
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create receipt.SchemaVersion
    node["repository"] <- JsonValue.Create receipt.Repository
    node["eventRevision"] <- JsonValue.Create receipt.EventRevision
    node["runId"] <- JsonValue.Create receipt.RunId
    node["attempt"] <- JsonValue.Create receipt.Attempt
    node["trigger"] <- JsonValue.Create receipt.Trigger
    node["role"] <- JsonValue.Create receipt.Role
    node["rosterPath"] <- JsonValue.Create receipt.RosterPath
    node["rosterSha256"] <- JsonValue.Create receipt.RosterSha256.Value

    let paths = JsonObject()
    for kv in receipt.ComponentConfigPaths do
        paths[kv.Key] <- JsonValue.Create kv.Value
    node["componentConfigPaths"] <- paths

    let shas = JsonObject()
    for kv in receipt.ComponentConfigShas do
        shas[kv.Key] <- JsonValue.Create kv.Value.Value
    node["componentConfigShas"] <- shas

    let runtimePaths = JsonArray()
    for path in receipt.RuntimeFilePaths do
        runtimePaths.Add(JsonValue.Create path) |> ignore
    node["runtimeFilePaths"] <- runtimePaths

    let runtimeDigests = JsonObject()
    for kv in receipt.RuntimeFileDigests do
        runtimeDigests[kv.Key] <- JsonValue.Create kv.Value.Value
    node["runtimeFileDigests"] <- runtimeDigests

    let runtimeSizes = JsonObject()
    for kv in receipt.RuntimeFileSizes do
        runtimeSizes[kv.Key] <- JsonValue.Create kv.Value
    node["runtimeFileSizes"] <- runtimeSizes

    node["builderSdk"] <- JsonValue.Create receipt.BuilderSdk
    node["builderRuntime"] <- JsonValue.Create receipt.BuilderRuntime
    node["builderPlatform"] <- JsonValue.Create receipt.BuilderPlatform

    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

let writeTrustedPublisherReceiptToFile (path: string) (receipt: TrustedPublisherReceipt) : Result<string, ReleaseError> =
    try
        let rendered = renderTrustedPublisherReceipt receipt
        let bytes = Encoding.UTF8.GetBytes rendered
        let directory = Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (Directory.Exists directory) then
            Directory.CreateDirectory directory |> ignore
        let tempPath = path + ".tmp"
        File.WriteAllBytes(tempPath, bytes)
        if File.Exists path then
            File.Delete path
        File.Move(tempPath, path)
        Ok(sha256Hex bytes)
    with
    | :? IOException as error -> Error(FileFailure("write", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("write", path, error.Message))


let private preparationReceiptAllowedFieldNames : Set<string> =
    Set.ofList
        [ "schemaVersion"
          "innerArchiveSha256"
          "innerArchiveSize"
          "manifestSha256"
          "manifestSize" ]

let private preparationReceiptLabel = "preparation receipt"

type PreparationReceipt =
    { SchemaVersion: int
      InnerArchiveSha256: OuterDigest
      InnerArchiveSize: int64
      ManifestSha256: OuterDigest
      ManifestSize: int64
      SourcePath: string
      SourceSha256: string }

let private readRequiredInt64Field
    (label: string) (root: JsonObject) (name: string) (sourcePath: string)
    : Result<int64, ReleaseError> =
    if not (root.ContainsKey name) then
        Error(MalformedArtifact(sourcePath, $"the {label} is missing required field '{name}'"))
    elif isNull root.[name] then
        Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is null"))
    else
        try
            match root.[name].GetValueKind() with
            | JsonValueKind.Number ->
                let v = root.[name].GetValue<int64>()
                if v < 0L then
                    Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is negative"))
                else
                    Ok v
            | _ -> Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is not a non-negative integer"))
        with _ ->
            Error(MalformedArtifact(sourcePath, $"the {label} field '{name}' is not a non-negative integer"))

let parsePreparationReceipt (text: string) (sourcePath: string) : Result<PreparationReceipt, ReleaseError> =
    let docResult =
        try
            Ok(JsonNode.Parse text)
        with
        | :? JsonException as error ->
            Error(MalformedArtifact(sourcePath, $"the {preparationReceiptLabel} is not valid JSON: {error.Message}"))
        | :? FormatException as error ->
            Error(MalformedArtifact(sourcePath, $"the {preparationReceiptLabel} is not valid JSON: {error.Message}"))

    match docResult with
    | Error error -> Error error
    | Ok doc when isNull doc ->
        Error(MalformedArtifact(sourcePath, $"the {preparationReceiptLabel} document is empty"))
    | Ok doc ->
        let root = doc.AsObject()
        let mutable unknown : string list = []
        for property in root do
            if not (Set.contains property.Key preparationReceiptAllowedFieldNames) then
                unknown <- property.Key :: unknown
        match unknown with
        | _ :: _ ->
            let detail = String.concat ", " (List.rev unknown)
            // The candidate cannot know the platform's outer digest before upload.
            Error(MalformedArtifact(sourcePath, $"the {preparationReceiptLabel} contains unknown field(s): {detail}; the preparation receipt carries NO runtime fields, NO toolchain values, and NO platform outer transport digest or size"))
        | [] ->
            let schemaVersion = readRequiredInt preparationReceiptLabel root "schemaVersion" sourcePath
            let innerArchiveSha = parseOuterDigest preparationReceiptLabel root "innerArchiveSha256" sourcePath
            let innerArchiveSize = readRequiredInt64Field preparationReceiptLabel root "innerArchiveSize" sourcePath
            let manifestSha = parseOuterDigest preparationReceiptLabel root "manifestSha256" sourcePath
            let manifestSize = readRequiredInt64Field preparationReceiptLabel root "manifestSize" sourcePath

            let mutable failure : ReleaseError option = None
            let mutable schemaVersionValue = 0
            let mutable innerArchiveShaValue : OuterDigest option = None
            let mutable innerArchiveSizeValue = 0L
            let mutable manifestShaValue : OuterDigest option = None
            let mutable manifestSizeValue = 0L

            let bind (r: Result<'a, ReleaseError>) (setter: 'a -> unit) =
                match failure with
                | Some _ -> ()
                | None ->
                    match r with
                    | Ok value -> setter value
                    | Error error -> failure <- Some error

            bind schemaVersion (fun v -> schemaVersionValue <- v)
            bind innerArchiveSha (fun v -> innerArchiveShaValue <- Some v)
            bind innerArchiveSize (fun v -> innerArchiveSizeValue <- v)
            bind manifestSha (fun v -> manifestShaValue <- Some v)
            bind manifestSize (fun v -> manifestSizeValue <- v)

            match failure with
            | Some error -> Error error
            | None ->
                if schemaVersionValue <> preparationReceiptSchemaVersion then
                    Error(MalformedArtifact(sourcePath, $"unsupported {preparationReceiptLabel} schema version {schemaVersionValue}; expected {preparationReceiptSchemaVersion}"))
                else
                    let inner =
                        match innerArchiveShaValue with
                        | Some v -> v
                        | None -> OuterDigest ""
                    let manifest =
                        match manifestShaValue with
                        | Some v -> v
                        | None -> OuterDigest ""
                    Ok
                        { SchemaVersion = schemaVersionValue
                          InnerArchiveSha256 = inner
                          InnerArchiveSize = innerArchiveSizeValue
                          ManifestSha256 = manifest
                          ManifestSize = manifestSizeValue
                          SourcePath = sourcePath
                          SourceSha256 = "" }

let private parsePreparationReceiptBytes (bytes: byte[]) (sourcePath: string) : Result<PreparationReceipt, ReleaseError> =
    if int64 bytes.Length > 4L * 1024L * 1024L then
        Error(MalformedArtifact(sourcePath, "the preparation receipt exceeds the 4194304-byte bounded read"))
    else
        try
            let text = UTF8Encoding(false, true).GetString bytes
            match parsePreparationReceipt text sourcePath with
            | Error error -> Error error
            | Ok receipt -> Ok { receipt with SourceSha256 = sha256Hex bytes }
        with
        | :? DecoderFallbackException as error ->
            Error(MalformedArtifact(sourcePath, $"the preparation receipt is not strict UTF-8: {error.Message}"))


type ExpectedMetadata =
    { ArtifactId: string
      ArtifactName: string
      Role: string
      Component: string
      Repository: string
      RunId: string
      Attempt: string
      Sha: string }

type ObservedPlatformMetadata =
    { ArtifactId: string
      ArtifactName: string
      Role: string
      Component: string
      Repository: string
      RunId: string
      Attempt: string
      Sha: string }

type ArtifactEvidence =
    { Expected: ExpectedMetadata
      Observed: ObservedPlatformMetadata
      ActualOuterDigest: OuterDigest
      TransportSourcePath: string
      TransportLength: int64
      RawTransportBytes: byte[] }

let private crossCheckMetadata
    (rawOuter: string)
    (expected: ExpectedMetadata)
    (observed: ObservedPlatformMetadata)
    : Result<unit, ReleaseError> =
    let check field (e: string) (o: string) =
        if e <> o then
            Error(IdentityMismatch(field, e, o))
        else
            Ok()
    let checkResults =
        [ check "artifactId" expected.ArtifactId observed.ArtifactId
          check "artifactName" expected.ArtifactName observed.ArtifactName
          check "role" expected.Role observed.Role
          check "component" expected.Component observed.Component
          check "repository" expected.Repository observed.Repository
          check "runId" expected.RunId observed.RunId
          check "attempt" expected.Attempt observed.Attempt
          check "sha" expected.Sha observed.Sha ]
    let mutable firstError : ReleaseError option = None
    for r in checkResults do
        match r with
        | Error e ->
            match firstError with
            | None -> firstError <- Some e
            | Some _ -> ()
        | Ok () -> ()
    match firstError with
    | Some (IdentityMismatch (field, e, o)) ->
        Error(MalformedArtifact(rawOuter, $"the artifact metadata field '{field}' is mismatched between expected '{e}' and observed '{o}'; refusing to publish"))
    | Some e -> Error e
    | None -> Ok()


type VerifiedEntry internal
    (
        name: VerifiedEntryName,
        nameBytes: byte[],
        payloadSha256: OuterDigest,
        payloadSize: int64,
        payloadBytes: byte[]
    ) =
    member _.Name = name
    member _.NameBytes with get () = Array.copy nameBytes
    member _.PayloadSha256 = payloadSha256
    member _.PayloadSize = payloadSize
    member _.PayloadBytesCopy () = Array.copy payloadBytes
    member _.PayloadStream () = new MemoryStream(payloadBytes, false) :> Stream

type CandidateArchiveSnapshot internal
    (
        outerDigest: OuterDigest,
        outerSize: int64,
        innerArchiveBytes: byte[],
        innerArchiveSha256: InnerArchiveSha256,
        innerArchiveSize: int64,
        manifestEntryName: VerifiedEntryName option,
        manifestBytes: byte[] option,
        manifestSha256: OuterDigest option,
        manifestSize: int64 option,
        embeddedManifestBytes: byte[] option,
        embeddedRevision: string option,
        entries: VerifiedEntry list,
        preparation: PreparationReceipt,
        sourcePath: string,
        sourceSha256: string
    ) =
    member _.OuterDigest = outerDigest
    member _.OuterSize = outerSize
    member _.InnerArchiveSha256 = innerArchiveSha256
    member _.InnerArchiveSize = innerArchiveSize
    member _.ManifestEntryName = manifestEntryName
    member _.ManifestSha256 = manifestSha256
    member _.ManifestSize = manifestSize
    member _.EmbeddedRevision = embeddedRevision
    member _.Entries = entries
    member _.Preparation = preparation
    member _.SourcePath = sourcePath
    member _.SourceSha256 = sourceSha256
    member _.HasManifest = manifestEntryName.IsSome
    member _.InnerArchiveBytesCopy () = Array.copy innerArchiveBytes
    member _.ManifestBytesCopy () =
        match manifestBytes with
        | Some b -> Array.copy b
        | None -> [||]
    member _.EmbeddedManifestBytesCopy () =
        match embeddedManifestBytes with
        | Some b -> Array.copy b
        | None -> [||]
    member _.InnerArchiveStream () = new MemoryStream(innerArchiveBytes, false) :> Stream
    member _.ManifestStream () =
        match manifestBytes with
        | Some b -> new MemoryStream(b, false) :> Stream
        | None -> new MemoryStream([||], false) :> Stream
    member _.EmbeddedManifestStream () =
        match embeddedManifestBytes with
        | Some b -> new MemoryStream(b, false) :> Stream
        | None -> new MemoryStream([||], false) :> Stream

let private readBoundedBytes (source: Stream) (cap: int64) (label: string) : Result<byte[], ReleaseError> =
    try
        use m = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 81920
        let mutable total = 0L
        let mutable finished = false
        let mutable oversize = false
        while not finished && not oversize do
            let read = source.Read(buffer, 0, buffer.Length)
            if read = 0 then
                finished <- true
            else
                total <- total + int64 read
                if total > cap then
                    oversize <- true
                else
                    m.Write(buffer, 0, read)
        if oversize then
            Error(MalformedArtifact(label, $"the bounded stream read exceeds the {cap}-byte cap"))
        else
            Ok(m.ToArray())
    with
    | :? IOException as error -> Error(FileFailure("read", label, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", label, error.Message))

let private canonicalEntrySet (policy: ReleasePolicy.PolicyProjection) : Set<string> =
    Set.ofList [ policy.ArchiveName; manifestEntryName; "preparation.json" ]


let readRawTransport
    (rawOuter: string)
    (boundedBytes: byte[])
    (evidence: ArtifactEvidence)
    (trustedPolicy: ReleasePolicy.PolicyProjection)
    : Result<CandidateArchiveSnapshot, ReleaseError> =

    if isNull boundedBytes then
        Error(MalformedArtifact(rawOuter, "the bounded transport bytes are null"))
    elif int64 boundedBytes.Length > outerTransportMaxBytes then
        Error(MalformedArtifact(rawOuter, $"the bounded transport bytes exceed the {outerTransportMaxBytes}-byte cap"))
    elif int64 boundedBytes.Length <> evidence.TransportLength then
        Error(IdentityMismatch("transportLength", string evidence.TransportLength, string (int64 boundedBytes.Length)))
    elif not (isLowerHex64 evidence.ActualOuterDigest.Value) then
        Error(MalformedArtifact(rawOuter, "the evidence 'actualOuterDigest' is not a 64-character lowercase hex SHA-256"))
    else
        let observedOuterDigestValue = sha256Hex boundedBytes
        if observedOuterDigestValue <> evidence.ActualOuterDigest.Value then
            Error(IdentityMismatch("outerTransportSha256", evidence.ActualOuterDigest.Value, observedOuterDigestValue))
        else
            match crossCheckMetadata rawOuter evidence.Expected evidence.Observed with
            | Error e -> Error e
            | Ok () ->
                try
                    use memoryStream = new MemoryStream(boundedBytes, false)
                    use archive = new ZipArchive(memoryStream, ZipArchiveMode.Read, false)

                    if archive.Entries.Count > outerEntryCountMax then
                        Error(MalformedArtifact(rawOuter, $"the archive declares {archive.Entries.Count} entries which exceeds the {outerEntryCountMax}-entry cap"))
                    else
                        let canonicalNames = canonicalEntrySet trustedPolicy
                        let seenNames = HashSet<string>(StringComparer.Ordinal)
                        let seenNamesOrdinalIgnoreCase = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        let mutable failure : ReleaseError option = None
                        let mutable totalUncompressed = 0L
                        let entryAcc = ResizeArray<VerifiedEntry>()
                        let mutable manifestEntry : ZipArchiveEntry option = None

                        for entry in archive.Entries do
                            if failure.IsNone then
                                let name = entry.FullName
                                let nameBytes = Encoding.ASCII.GetBytes name
                                let payloadCap =
                                    if name = "preparation.json" then 4L * 1024L * 1024L
                                    else outerEntryUncompressedMax

                                if not (pathLengthOk name) then
                                    failure <- Some(MalformedArtifact(rawOuter, $"entry name '{name}' exceeds the 256-character path cap"))
                                elif not (isPathSafe name) then
                                    failure <- Some(MalformedArtifact(rawOuter, $"entry name '{name}' has an unsafe path (absolute / drive / UNC / backslash / alternate-stream / dot-segment / non-ASCII)"))
                                elif not (seenNames.Add name) then
                                    failure <- Some(MalformedArtifact(rawOuter, $"duplicate entry name '{name}' (case-sensitive)"))
                                elif not (seenNamesOrdinalIgnoreCase.Add name) then
                                    failure <- Some(MalformedArtifact(name, $"duplicate entry name '{name}' (case-insensitive collision with a previous entry in the same central directory)"))
                                elif entry.Length < 0L || entry.CompressedLength < 0L then
                                    failure <- Some(MalformedArtifact(rawOuter, $"entry '{name}' declares a negative size"))
                                else
                                    if entry.Length > payloadCap then
                                        failure <- Some(MalformedArtifact(rawOuter, $"entry '{name}' exceeds the {payloadCap}-byte uncompressed cap"))
                                    elif entry.CompressedLength > 0L && entry.Length > entry.CompressedLength * outerEntryCompressionRatioMax then
                                        failure <- Some(MalformedArtifact(rawOuter, $"entry '{name}' exceeds the {outerEntryCompressionRatioMax}:1 compression ratio cap"))
                                    else
                                        totalUncompressed <- totalUncompressed + entry.Length
                                        if totalUncompressed > outerTotalUncompressedMax then
                                            failure <- Some(MalformedArtifact(rawOuter, $"entries declare more than {outerTotalUncompressedMax} bytes total uncompressed"))
                                        else
                                            if name = manifestEntryName then
                                                if manifestEntry.IsSome then
                                                    failure <- Some(MalformedArtifact(rawOuter, $"the archive contains more than one '{manifestEntryName}' entry"))
                                                else
                                                    manifestEntry <- Some entry

                                            let externalAttrs = entry.ExternalAttributes
                                            let unixFileType = (externalAttrs >>> 16) &&& 0xF000
                                            let dosAttrs = externalAttrs &&& 0xFFFF
                                            let forbiddenFileTypeDetail =
                                                match unixFileType with
                                                | 0xA000 -> Some $"the central directory marks '{name}' as a Unix symbolic link (file type 0xA000); refusing to follow a symlink entry"
                                                | 0x4000 -> Some $"the central directory marks '{name}' as a directory (file type 0x4000); the outer transport allows exactly three file entries and forbids directories"
                                                | 0x1000 -> Some $"the central directory marks '{name}' as a Unix FIFO / named pipe (file type 0x1000)"
                                                | 0x2000 -> Some $"the central directory marks '{name}' as a Unix character device (file type 0x2000)"
                                                | 0x3000 -> Some $"the central directory marks '{name}' as a Unix block device (file type 0x3000)"
                                                | 0xC000 -> Some $"the central directory marks '{name}' as a Unix socket (file type 0xC000)"
                                                | t when t = 0x0000 || t = 0x8000 -> None
                                                | _ -> Some $"the central directory marks '{name}' with an unsupported Unix file type 0x{unixFileType:X4}; only S_IFREG (0x8000) and zero attributes (ordinary Windows platform ZIP) are accepted"
                                            match forbiddenFileTypeDetail, (dosAttrs &&& 0x400) <> 0 with
                                            | Some detail, _ ->
                                                failure <- Some(MalformedArtifact(name, $"{detail}; only S_IFREG (0x8000) and zero attributes (ordinary Windows platform ZIP) are accepted, and DOS reparse-point attribute 0x400 is rejected"))
                                            | None, true ->
                                                failure <- Some(MalformedArtifact(name, $"the central directory marks '{name}' with a DOS reparse-point attribute (0x400); refusing to follow a symlink / reparse entry"))
                                            | None, false ->
                                                use entryStream = entry.Open()
                                                let payloadResult = readBoundedBytes entryStream payloadCap name
                                                match payloadResult with
                                                | Error e -> failure <- Some e
                                                | Ok payload ->
                                                    let payloadSha = sha256Hex payload
                                                    let entryName = VerifiedEntryName name
                                                    let entryDigest = OuterDigest payloadSha
                                                    entryAcc.Add(
                                                        VerifiedEntry(
                                                            entryName,
                                                            Array.copy nameBytes,
                                                            entryDigest,
                                                            entry.Length,
                                                            Array.copy payload))

                        match failure with
                        | Some e -> Error e
                        | None ->
                            let actualNames = seenNames |> Set.ofSeq
                            if actualNames <> canonicalNames then
                                let expected = canonicalNames |> Set.toList |> List.sort
                                let actual = actualNames |> Set.toList |> List.sort
                                Error(AllowlistMismatch("the candidate archive entry set does not match the trusted canonical policy", expected, actual))
                            else
                                match manifestEntry with
                                | None ->
                                    Error(MalformedArtifact(rawOuter, $"the verified candidate transport is missing the canonical standalone '{manifestEntryName}' entry"))
                                | Some entry ->
                                    let prepPath = sprintf "%s/%s" rawOuter "preparation.json"
                                    match entryAcc |> Seq.tryFind (fun verified -> verified.Name.Value = "preparation.json") with
                                    | None -> Error(MalformedArtifact(rawOuter, "the verified candidate transport is missing 'preparation.json'"))
                                    | Some prepEntry ->
                                        match parsePreparationReceiptBytes (prepEntry.PayloadBytesCopy()) prepPath with
                                        | Error error -> Error error
                                        | Ok preparation ->
                                            match readBoundedBytes (entry.Open()) outerEntryUncompressedMax (sprintf "%s/%s" rawOuter manifestEntryName) with
                                            | Error e -> Error e
                                            | Ok manifestPayload ->
                                                let manifestSha = sha256Hex manifestPayload
                                                let manifestName = VerifiedEntryName manifestEntryName
                                                let entries = List.ofSeq entryAcc
                                                match entries |> List.tryFind (fun verified -> verified.Name.Value = trustedPolicy.ArchiveName) with
                                                | None ->
                                                    Error(MalformedArtifact(rawOuter, $"the verified outer entry set is missing the canonical inner archive entry '{trustedPolicy.ArchiveName}'"))
                                                | Some innerEntry ->
                                                    let innerBytes = innerEntry.PayloadBytesCopy()
                                                    let innerSha = InnerArchiveSha256 (sha256Hex innerBytes)
                                                    let innerSize = int64 innerBytes.Length
                                                    match ReleaseCompare.observeBytes trustedPolicy.ArchiveName innerBytes with
                                                    | Error e -> Error e
                                                    | Ok observation ->
                                                        match observation.ManifestBody with
                                                        | None ->
                                                            Error(MalformedArtifact(trustedPolicy.ArchiveName, $"the verified inner archive contains no embedded manifest; canonical publish must embed a '{manifestEntryName}' entry inside the inner archive"))
                                                        | Some embeddedManifestText ->
                                                            let embeddedManifestBytes = Encoding.UTF8.GetBytes embeddedManifestText
                                                            let actualArchiveSha = sha256Hex innerBytes
                                                            let actualArchiveSize = int64 innerBytes.Length
                                                            let actualManifestSha = sha256Hex manifestPayload
                                                            let actualManifestSize = int64 manifestPayload.Length
                                                            if preparation.InnerArchiveSha256.Value <> actualArchiveSha
                                                               || preparation.InnerArchiveSize <> actualArchiveSize
                                                               || preparation.ManifestSha256.Value <> actualManifestSha
                                                               || preparation.ManifestSize <> actualManifestSize then
                                                                Error(MalformedArtifact(prepPath, "the preparation receipt's inner archive and standalone manifest hashes or sizes do not match the verified transport member bytes"))
                                                            elif manifestPayload <> embeddedManifestBytes then
                                                                let standaloneSha = sha256Hex manifestPayload
                                                                let embeddedSha = sha256Hex embeddedManifestBytes
                                                                Error(IdentityMismatch("standaloneEmbeddedManifestBytes", embeddedSha, standaloneSha))
                                                            else
                                                                let outerDigestTyped = OuterDigest observedOuterDigestValue
                                                                Ok(
                                                                    CandidateArchiveSnapshot(
                                                                        outerDigestTyped,
                                                                        int64 boundedBytes.Length,
                                                                        innerBytes,
                                                                        innerSha,
                                                                        innerSize,
                                                                        Some manifestName,
                                                                        Some(Array.copy manifestPayload),
                                                                        Some(OuterDigest manifestSha),
                                                                        Some(int64 manifestPayload.Length),
                                                                        Some(Array.copy embeddedManifestBytes),
                                                                        Some embeddedManifestText,
                                                                        entries,
                                                                        preparation,
                                                                        rawOuter,
                                                                        sha256Hex innerBytes))
                with
                | :? InvalidDataException as ex ->
                    Error(MalformedArtifact(rawOuter, $"the bounded transport bytes are not a valid standard-platform ZIP: {ex.Message}"))
                | :? IOException as ex ->
                    Error(FileFailure("read", rawOuter, ex.Message))
                | :? UnauthorizedAccessException as ex ->
                    Error(FileFailure("read", rawOuter, ex.Message))
