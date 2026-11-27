// role: reusable module/helper
// Canonical immutable pin, which is also the published release metadata: one strict parser, one
// canonical renderer, and one whole-record comparison shared by producer pin emission, the
// release adapter, and consumers. Pure: no processes, filesystem writes, network, or credentials.
module DistributionPin

open System
open System.Text.Json
open System.Text.Json.Nodes
open SharedFailure

let private fields = [ "id"; "version"; "digest" ]
let private digestPrefix = "sha256:"

// The archive name and asset location derive from the identity and the trusted Store repository.
type Pin =
    { Id: string
      Version: string
      Digest: string }

    member pin.ArchiveName: string = DistributionContract.archiveNameFor pin.Version
    member pin.AssetUri: string = DistributionContract.assetUriFor pin.Id pin.Version
    member pin.Sha256: string = pin.Digest.Substring digestPrefix.Length

let digestFor (sha256: string) : string = digestPrefix + sha256

let isDigest (value: string) : bool =
    not (isNull value)
    && value.StartsWith(digestPrefix, StringComparison.Ordinal)
    && DistributionContract.isSha256 (value.Substring digestPrefix.Length)

// Fixed key order keeps the bytes stable for the existing-asset check and for consumer pin comparison.
let toJsonNode (pin: Pin) : JsonNode =
    let node = JsonObject()
    node["id"] <- JsonValue.Create pin.Id
    node["version"] <- JsonValue.Create pin.Version
    node["digest"] <- JsonValue.Create pin.Digest
    node

let render (pin: Pin) : string =
    (toJsonNode pin).ToJsonString(JsonSerializerOptions(WriteIndented = true))

let private malformed (detail: string) : ReleaseError = MalformedArtifact("pin", detail)

let private parseRoot (root: JsonElement) : Result<Pin, ReleaseError> =
    let field (name: string) : Result<string, ReleaseError> =
        match root.TryGetProperty name with
        | false, _ -> Error(malformed $"the pin is missing required field '{name}'")
        | true, value when value.ValueKind = JsonValueKind.Null -> Error(malformed $"the pin field '{name}' is null")
        | true, value when value.ValueKind <> JsonValueKind.String -> Error(malformed $"the pin field '{name}' is not a string")
        | true, value -> Ok(value.GetString())

    let unknown =
        root.EnumerateObject()
        |> Seq.map _.Name
        |> Seq.tryFind (fun name -> not (List.contains name fields))

    match DistributionContract.rejectDuplicateKeys root, unknown with
    | Error detail, _ -> Error(malformed detail)
    | Ok (), Some name -> Error(malformed $"the pin contains unknown field '{name}'")
    | Ok (), None ->
        match field "id", field "version", field "digest" with
        | Error error, _, _
        | _, Error error, _
        | _, _, Error error -> Error error
        | Ok id, Ok version, Ok digest ->
            match DistributionContract.validateIdentity id version with
            | Error error -> Error error
            | Ok () when not (isDigest digest) ->
                Error(malformed $"the pin 'digest' is not '{digestPrefix}' followed by 64 lowercase hexadecimal characters")
            | Ok () ->
                Ok
                    { Id = id
                      Version = version
                      Digest = digest }

// Unknown fields, duplicate keys, missing or null fields, malformed JSON, and malformed identities
// or digests fail closed.
let parse (text: string) : Result<Pin, ReleaseError> =
    try
        use document =
            JsonDocument.Parse(
                text,
                JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8)
            )

        if document.RootElement.ValueKind <> JsonValueKind.Object then
            Error(malformed "the pin is not a JSON object")
        else
            parseRoot document.RootElement
    with
    | :? JsonException as error -> Error(malformed $"the pin is not valid JSON: {error.Message}")
    | :? FormatException as error -> Error(malformed $"the pin is not valid JSON: {error.Message}")
    | :? InvalidOperationException as error -> Error(malformed $"the pin is not valid JSON: {error.Message}")

// Applied to an existing remote asset or an existing pin before an idempotent
// re-upload is allowed; every field participates in identity.
let compare (existing: Pin) (expected: Pin) : Result<unit, ReleaseError> =
    let differences =
        [ "id", existing.Id, expected.Id
          "version", existing.Version, expected.Version
          "digest", existing.Digest, expected.Digest ]
        |> List.filter (fun (_, left, right) -> left <> right)
        |> List.map (fun (name, left, right) -> $"{name} differs: existing={left} expected={right}")

    if List.isEmpty differences then
        Ok()
    else
        Error(malformed (String.concat "; " differences))
