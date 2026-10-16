// role: reusable module/helper
// Canonical immutable consumer pin, which is also the published release-metadata
// projection: one strict parser, one canonical renderer, and one whole-record
// comparison reused by producer pin emission, the release adapter, and consumers.
// Pure: no processes, filesystem writes, network, or credentials.
module DistributionPin

open System
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance

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

// Unknown fields, mismatched schema versions, missing fields, malformed JSON, and
// malformed hashes or revisions fail closed.
let parse (text: string) : Result<Pin, ReleaseError> =
    let mutable failure: ReleaseError option = None

    let fail error =
        failure <- Some error
        Error error

    let mutable result: Pin option = None

    try
        let document = JsonNode.Parse text

        if isNull document then
            failure <- Some(MalformedArtifact("metadata", "the document is empty"))
        else
            let root = document.AsObject()
            let unknown = root |> Seq.map (fun pair -> pair.Key) |> Seq.tryFind (fun name -> not (List.contains name (requiredFields @ optionalFields)))

            match unknown with
            | Some name -> failure <- Some(MalformedArtifact("metadata", $"the metadata projection contains unknown field '{name}'"))
            | None ->

                let readStr (name: string) : Result<string, ReleaseError> =
                    if not (root.ContainsKey name) then
                        fail(MalformedArtifact("metadata", $"the metadata projection is missing required field '{name}'"))
                    else
                        let valueNode = root.[name]

                        if isNull valueNode then
                            fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is null"))
                        else
                            try
                                Ok(valueNode.GetValue<string>())
                            with
                            | :? InvalidOperationException as error ->
                                fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is not a string: {error.Message}"))

                // An optional field may be omitted, but a present field must be a
                // non-empty string; null or a wrong type is malformed, not absent.
                let readOptional (name: string) : Result<string option, ReleaseError> =
                    if not (root.ContainsKey name) then
                        Ok None
                    else
                        let valueNode = root.[name]

                        if isNull valueNode then
                            fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is null"))
                        else
                            try
                                let value = valueNode.GetValue<string>()

                                if String.IsNullOrEmpty value then
                                    fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is empty"))
                                else
                                    Ok(Some value)
                            with
                            | :? InvalidOperationException as error ->
                                fail(MalformedArtifact("metadata", $"the metadata projection field '{name}' is not a string: {error.Message}"))

                let schemaResult =
                    try
                        Ok(root.["schemaVersion"].GetValue<int>())
                    with
                    | _ -> fail(MalformedArtifact("metadata", "the metadata projection 'schemaVersion' is not an integer"))

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
