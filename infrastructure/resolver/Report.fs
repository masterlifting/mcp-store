// role: reusable module/helper
// Versioned resolution snapshot: the trusted pin identity, the installed revision, the launch
// descriptor, and the derived runtime prerequisite, with no build provenance.
module ResolverReport

open System.Text.Json
open System.Text.Json.Nodes
open SharedFailure

// The snapshot is a consumer contract, so a field change is a schema change.
let schemaVersion = 1

let render (pin: DistributionPin.Pin) (resolution: ResolverInstall.Resolution) : string =
    let manifest = resolution.Manifest
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create schemaVersion
    node["key"] <- JsonValue.Create resolution.Key
    node["root"] <- JsonValue.Create resolution.Root
    node["install"] <- JsonValue.Create resolution.Install
    node["entrypoint"] <- JsonValue.Create resolution.Entrypoint
    node["componentId"] <- JsonValue.Create pin.ComponentId
    node["version"] <- JsonValue.Create pin.Version
    node["publishedRevision"] <- JsonValue.Create pin.PublishedRevision
    node["assetUri"] <- JsonValue.Create pin.AssetUri
    node["archiveName"] <- JsonValue.Create pin.ArchiveName
    node["archiveSha256"] <- JsonValue.Create pin.ArchiveSha256
    node["manifestSha256"] <- JsonValue.Create pin.ManifestSha256
    node["kind"] <- JsonValue.Create manifest.Launch.Kind

    let runtime = JsonObject()
    runtime["framework"] <- JsonValue.Create resolution.Runtime.Framework
    runtime["version"] <- JsonValue.Create resolution.Runtime.Version
    runtime["config"] <- JsonValue.Create resolution.Runtime.Config
    node["runtime"] <- runtime

    let arguments = JsonArray()

    for argument in manifest.Launch.Arguments do
        let item = JsonObject()
        item["flag"] <- JsonValue.Create argument.Flag
        item["input"] <- JsonValue.Create argument.Input
        item["type"] <- JsonValue.Create(DistributionContract.LaunchArgumentType.wireName argument.Type)
        arguments.Add item

    node["arguments"] <- arguments
    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
