// Repo-shared consumer-pin mechanism loaded as the explicit ReleasePins module.
// Single-sources clean-HEAD revision validation and the exact five-field pin
// shape so producer pins cannot drift.
module ReleasePins

#load "Provenance.fsx"

open System
open System.IO
open System.Text.Json.Nodes
open BuildProvenance

// The expected field list and diagnostics stay producer-local; only the
// parse/compare mechanics are shared.
let validateManifestIdentity
    (manifestPath: string)
    (expected: (string * string) list)
    : Async<Result<unit, ReleaseError>> =
    async {
        match! readAllTextAsync manifestPath with
        | Error error -> return Error error
        | Ok text ->
            try
                let node = JsonNode.Parse text

                if isNull node then
                    return Error(MalformedArtifact(manifestPath, "the document is empty"))
                else
                    let document = node.AsObject()

                    let mismatch =
                        expected
                        |> List.tryPick (fun (field, expectedValue) ->
                            if not (document.ContainsKey field) || isNull document[field] then
                                Some(MalformedArtifact(manifestPath, $"the '{field}' field is missing"))
                            else
                                let actual = document[field].GetValue<string>()

                                if actual = expectedValue then
                                    None
                                else
                                    Some(IdentityMismatch(field, expectedValue, actual)))

                    match mismatch with
                    | Some error -> return Error error
                    | None -> return Ok()
            with
            | :? System.Text.Json.JsonException as error ->
                return Error(MalformedArtifact(manifestPath, $"the manifest is not valid JSON: {error.Message}"))
            | :? FormatException as error ->
                return Error(MalformedArtifact(manifestPath, $"the manifest is not valid JSON: {error.Message}"))
            | :? InvalidOperationException as error ->
                return Error(MalformedArtifact(manifestPath, $"the manifest is not valid JSON: {error.Message}"))
    }

// Validates the packaged manifest against the committed clean HEAD, then writes
// the canonical consumer pin for one producer. Returns the archive hash,
// manifest hash, and validated revision for the caller's diagnostics.
let writeConsumerPins
    (root: string)
    (manifestPath: string)
    (archivePath: string)
    (outputPath: string)
    (componentId: string)
    (archiveName: string)
    (assetUri: string)
    : Async<Result<string * string * string, ReleaseError>> =
    releaseResult {
        do! BuildProvenance.assertCleanTree root
        let! head = BuildProvenance.committedHead root
        let! revision = BuildProvenance.assertManifestRevision manifestPath head
        let! archiveSha256 = BuildProvenance.sha256FileAsync archivePath
        let! manifestSha256 = BuildProvenance.sha256FileAsync manifestPath

        let pins = JsonObject()
        let value = JsonObject()
        value["assetName"] <- JsonValue.Create archiveName
        value["assetUri"] <- JsonValue.Create assetUri
        value["archiveSha256"] <- JsonValue.Create archiveSha256
        value["manifestSha256"] <- JsonValue.Create manifestSha256
        value["revision"] <- JsonValue.Create revision
        pins[componentId] <- value

        do! BuildProvenance.writeJsonAsync outputPath (pins :> JsonNode)
        return archiveSha256, manifestSha256, revision
    }
