// Repo-shared consumer-pin mechanism. Each producer keeps its release policy,
// paths, and diagnostics local; this module single-sources the clean-HEAD
// revision validation and the exact five-field pin shape so the producers
// cannot drift. The file name supplies the implicit module name, so `#load`
// exposes these as ReleasePins.*.
#load "BuildProvenance.fsx"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

let private sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

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
    =
    BuildProvenance.assertCleanTree root
    let revision = BuildProvenance.assertManifestRevision manifestPath (BuildProvenance.committedHead root)
    let archiveSha256 = sha256 archivePath
    let manifestSha256 = sha256 manifestPath

    let pins = JsonObject()
    let value = JsonObject()
    value["assetName"] <- JsonValue.Create archiveName
    value["assetUri"] <- JsonValue.Create assetUri
    value["archiveSha256"] <- JsonValue.Create archiveSha256
    value["manifestSha256"] <- JsonValue.Create manifestSha256
    value["revision"] <- JsonValue.Create revision
    pins[componentId] <- value

    File.WriteAllText(outputPath, pins.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
    archiveSha256, manifestSha256, revision
