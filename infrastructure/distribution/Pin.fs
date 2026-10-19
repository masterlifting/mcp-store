// role: reusable module/helper
// Canonical immutable consumer pin, which is also the published release-metadata
// projection: one strict parser, one canonical renderer, and one whole-record
// comparison reused by producer pin emission, the release adapter, and consumers.
// Pure: no processes, filesystem writes, network, or credentials.
module DistributionPin

open System
open System.Text.Json
open System.Text.Json.Nodes
open SharedFailure

// A mismatched schema version is rejected so a schema change cannot bypass the strict field check.
let schemaVersion = 1

// A future schema change must update these lists together with the parser and renderer.
let private requiredFields =
    [ "schemaVersion"
      "componentId"
      "version"
      "assetUri"
      "archiveName"
      "archiveSha256"
      "manifestSha256"
      "publishedRevision" ]

// Producer toolchain context is informational; consumer pinning uses the immutable
// archive/manifest hashes and the published revision.
let private optionalFields = [ "producerSdk"; "producerRuntime"; "producerPlatform" ]

type Pin =
    { SchemaVersion: int
      ComponentId: string
      Version: string
      AssetUri: string
      ArchiveName: string
      ArchiveSha256: string
      ManifestSha256: string
      PublishedRevision: string
      ProducerSdk: string option
      ProducerRuntime: string option
      ProducerPlatform: string option }

// Fixed key order and lowercase-hex hashes keep the bytes stable for the
// existing-asset check and for consumer pin comparison.
let toJsonNode (pin: Pin) : JsonNode =
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create pin.SchemaVersion
    node["componentId"] <- JsonValue.Create pin.ComponentId
    node["version"] <- JsonValue.Create pin.Version
    node["assetUri"] <- JsonValue.Create pin.AssetUri
    node["archiveName"] <- JsonValue.Create pin.ArchiveName
    node["archiveSha256"] <- JsonValue.Create pin.ArchiveSha256
    node["manifestSha256"] <- JsonValue.Create pin.ManifestSha256
    node["publishedRevision"] <- JsonValue.Create pin.PublishedRevision

    match pin.ProducerSdk with
    | Some value -> node["producerSdk"] <- JsonValue.Create value
    | None -> ()

    match pin.ProducerRuntime with
    | Some value -> node["producerRuntime"] <- JsonValue.Create value
    | None -> ()

    match pin.ProducerPlatform with
    | Some value -> node["producerPlatform"] <- JsonValue.Create value
    | None -> ()

    node

let render (pin: Pin) : string =
    (toJsonNode pin).ToJsonString(JsonSerializerOptions(WriteIndented = true))

// Unknown fields, duplicate keys, mismatched schema versions, missing fields,
// malformed JSON, and malformed hashes or revisions fail closed.
let parse (text: string) : Result<Pin, ReleaseError> =
    let mutable failure: ReleaseError option = None

    let fail error =
        failure <- Some error
        Error error

    let mutable result: Pin option = None

    try
        use document =
            JsonDocument.Parse(
                text,
                JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8)
            )

        let root = document.RootElement

        if root.ValueKind = JsonValueKind.Null then
            failure <- Some(MalformedArtifact("metadata", "the document is empty"))
        elif root.ValueKind <> JsonValueKind.Object then
            failure <- Some(MalformedArtifact("metadata", "the metadata projection is not a JSON object"))
        else
            let tryField (name: string) : JsonElement option =
                let mutable value = Unchecked.defaultof<JsonElement>

                if root.TryGetProperty(name, &value) then Some value else None

            let unknown =
                root.EnumerateObject()
                |> Seq.map (fun property -> property.Name)
                |> Seq.tryFind (fun name -> not (List.contains name (requiredFields @ optionalFields)))

            // Duplicate keys surface only when the object is materialized, so the shared strict check runs first.
            match DistributionContract.rejectDuplicateKeys root, unknown with
            | Error detail, _ -> failure <- Some(MalformedArtifact("metadata", detail))
            | Ok (), Some name -> failure <- Some(MalformedArtifact("metadata", $"the metadata projection contains unknown field '{name}'"))
            | Ok (), None ->

                let readStr (name: string) : Result<string, ReleaseError> =
                    match tryField name with
                    | None -> fail(MalformedArtifact("metadata", $"the metadata projection is missing required field '{name}'"))
                    | Some value when value.ValueKind = JsonValueKind.Null ->
                        fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is null"))
                    | Some value when value.ValueKind <> JsonValueKind.String ->
                        fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is not a string"))
                    | Some value -> Ok(value.GetString())

                // An optional field may be omitted, but a present field must be a
                // non-empty string; null or a wrong type is malformed, not absent.
                let readOptional (name: string) : Result<string option, ReleaseError> =
                    match tryField name with
                    | None -> Ok None
                    | Some value when value.ValueKind = JsonValueKind.Null ->
                        fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is null"))
                    | Some value when value.ValueKind <> JsonValueKind.String ->
                        fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is not a string"))
                    | Some value ->
                        let fieldText = value.GetString()

                        if String.IsNullOrEmpty fieldText then
                            fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is empty"))
                        else
                            Ok(Some fieldText)

                let schemaResult =
                    let notAnInteger () =
                        fail(MalformedArtifact("metadata", "the metadata projection 'schemaVersion' is not an integer"))

                    match tryField "schemaVersion" with
                    // TryGetInt32 throws instead of returning false for a non-Number kind, so the kind guard comes first.
                    | Some value when value.ValueKind = JsonValueKind.Number ->
                        match value.TryGetInt32() with
                        | true, parsed -> Ok parsed
                        | _ -> notAnInteger ()
                    | _ -> notAnInteger ()

                match schemaResult with
                | Error _ -> ()
                | Ok schemaVersionValue when schemaVersionValue <> schemaVersion ->
                    failure <-
                        Some(
                            MalformedArtifact(
                                "metadata",
                                $"unsupported schema version {schemaVersionValue}; expected {schemaVersion}"))
                | Ok schemaVersionValue ->
                    match
                        readStr "componentId",
                        readStr "version",
                        readStr "assetUri",
                        readStr "archiveName",
                        readStr "archiveSha256",
                        readStr "manifestSha256",
                        readStr "publishedRevision",
                        readOptional "producerSdk",
                        readOptional "producerRuntime",
                        readOptional "producerPlatform"
                        with
                    | Ok componentId,
                      Ok version,
                      Ok assetUri,
                      Ok archiveName,
                      Ok archiveSha256,
                      Ok manifestSha256,
                      Ok publishedRevision,
                      Ok producerSdk,
                      Ok producerRuntime,
                      Ok producerPlatform when
                        not (String.IsNullOrEmpty componentId)
                        && not (String.IsNullOrEmpty version)
                        && not (String.IsNullOrEmpty assetUri)
                        && not (String.IsNullOrEmpty archiveName)
                        && not (String.IsNullOrEmpty archiveSha256)
                        && not (String.IsNullOrEmpty manifestSha256)
                        && not (String.IsNullOrEmpty publishedRevision) ->
                        if not (DistributionContract.isSha256 archiveSha256) then
                            failure <-
                                Some(MalformedArtifact("metadata", "the metadata projection 'archiveSha256' is not a lowercase 64-character SHA-256"))
                        elif not (DistributionContract.isSha256 manifestSha256) then
                            failure <-
                                Some(MalformedArtifact("metadata", "the metadata projection 'manifestSha256' is not a lowercase 64-character SHA-256"))
                        elif not (DistributionContract.isRevision publishedRevision) then
                            failure <-
                                Some(MalformedArtifact("metadata", "the metadata projection 'publishedRevision' is not a 40-character lowercase hex revision"))
                        elif not (DistributionContract.isSafeRelativePath archiveName) || archiveName.Contains '/' then
                            failure <-
                                Some(MalformedArtifact("metadata", "the metadata projection 'archiveName' is not a safe slash-free single file name"))
                        else
                            result <-
                                Some
                                    { SchemaVersion = schemaVersionValue
                                      ComponentId = componentId
                                      Version = version
                                      AssetUri = assetUri
                                      ArchiveName = archiveName
                                      ArchiveSha256 = archiveSha256
                                      ManifestSha256 = manifestSha256
                                      PublishedRevision = publishedRevision
                                      ProducerSdk = producerSdk
                                      ProducerRuntime = producerRuntime
                                      ProducerPlatform = producerPlatform }
                    | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, _, _, _ ->
                        failure <- Some(MalformedArtifact("metadata", "the metadata projection contains empty required fields"))
                    | _ -> ()
    with
    | :? JsonException as error ->
        failure <- Some(MalformedArtifact("metadata", $"the metadata is not valid JSON: {error.Message}"))
    | :? FormatException as error ->
        failure <- Some(MalformedArtifact("metadata", $"the metadata is not valid JSON: {error.Message}"))
    | :? InvalidOperationException as error ->
        failure <- Some(MalformedArtifact("metadata", $"the metadata is not valid JSON: {error.Message}"))

    match failure, result with
    | Some error, _ -> Error error
    | None, Some value -> Ok value
    | None, None ->
        Error(MalformedArtifact("metadata", "the metadata projection did not produce a parseable result"))

// Applied to an existing remote asset or an existing pin before an idempotent
// re-upload is allowed; every field participates in identity.
let compare (existing: Pin) (expected: Pin) : Result<unit, ReleaseError> =
    let failures = ResizeArray<string>()

    if existing.SchemaVersion <> expected.SchemaVersion then
        failures.Add($"schemaVersion differs: existing={existing.SchemaVersion} expected={expected.SchemaVersion}")

    if existing.ComponentId <> expected.ComponentId then
        failures.Add($"componentId differs: existing={existing.ComponentId} expected={expected.ComponentId}")

    if existing.Version <> expected.Version then
        failures.Add($"version differs: existing={existing.Version} expected={expected.Version}")

    if existing.AssetUri <> expected.AssetUri then
        failures.Add($"assetUri differs: existing={existing.AssetUri} expected={expected.AssetUri}")

    if existing.ArchiveName <> expected.ArchiveName then
        failures.Add($"archiveName differs: existing={existing.ArchiveName} expected={expected.ArchiveName}")

    if existing.ArchiveSha256 <> expected.ArchiveSha256 then
        failures.Add($"archiveSha256 differs: existing={existing.ArchiveSha256} expected={expected.ArchiveSha256}")

    if existing.ManifestSha256 <> expected.ManifestSha256 then
        failures.Add($"manifestSha256 differs: existing={existing.ManifestSha256} expected={expected.ManifestSha256}")

    if existing.PublishedRevision <> expected.PublishedRevision then
        failures.Add($"publishedRevision differs: existing={existing.PublishedRevision} expected={expected.PublishedRevision}")

    if existing.ProducerSdk <> expected.ProducerSdk then
        failures.Add($"producerSdk differs: existing={existing.ProducerSdk} expected={expected.ProducerSdk}")

    if existing.ProducerRuntime <> expected.ProducerRuntime then
        failures.Add($"producerRuntime differs: existing={existing.ProducerRuntime} expected={expected.ProducerRuntime}")

    if existing.ProducerPlatform <> expected.ProducerPlatform then
        failures.Add($"producerPlatform differs: existing={existing.ProducerPlatform} expected={expected.ProducerPlatform}")

    if failures.Count > 0 then
        Error(MalformedArtifact("metadata", String.concat "; " failures))
    else
        Ok()
