// role: reusable module/helper
// Build provenance is omitted because it is not a consumer requirement.
module ResolverReport

open System.Text.Json
open System.Text.Json.Nodes
open SharedFailure

// The snapshot is a consumer contract, so a field change is a schema change.
let schemaVersion = 2

// The revision is read from the authenticated manifest, never asserted by the pin.
let render (pin: DistributionPin.Pin) (resolution: ResolverInstall.Resolution) : string =
    let manifest = resolution.Manifest
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create schemaVersion
    node["key"] <- JsonValue.Create resolution.Key
    node["root"] <- JsonValue.Create resolution.Root
    node["install"] <- JsonValue.Create resolution.Install
    node["entrypoint"] <- JsonValue.Create resolution.Entrypoint
    node["id"] <- JsonValue.Create pin.Id
    node["version"] <- JsonValue.Create pin.Version
    node["digest"] <- JsonValue.Create pin.Digest
    node["revision"] <- JsonValue.Create manifest.Revision
    node["kind"] <- JsonValue.Create(DistributionContract.LaunchKind.wireName manifest.Launch.Kind)

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
