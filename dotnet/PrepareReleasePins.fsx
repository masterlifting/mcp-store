#load "ReleaseConfig.fsx"
#load "../ReleasePins.fsx"

open System
open System.IO
open System.Text.Json.Nodes

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let dist = Path.Combine(root, "dotnet", "dist")
let output = Path.Combine(dist, "consumer-pins.json")
let componentId = ReleaseConfig.componentId
let version = ReleaseConfig.version
let archiveName = ReleaseConfig.archiveName
let assetUri = ReleaseConfig.assetUri

let manifestPath = Path.Combine(dist, componentId, "distribution.json")
let archivePath = Path.Combine(dist, archiveName)

if not (File.Exists archivePath) || not (File.Exists manifestPath) then
    failwith "run dotnet/BuildDistribution.fsx before preparing dotnet v1 pins"

let manifest: JsonObject = JsonNode.Parse(File.ReadAllText manifestPath).AsObject()

let requiredManifestValue (name: string) =
    match manifest[name] with
    | null -> failwithf "manifest is missing '%s'" name
    | value -> value.GetValue<string>()

if requiredManifestValue "id" <> componentId
   || requiredManifestValue "version" <> version
   || requiredManifestValue "archive" <> archiveName then
    failwith "dotnet v1 manifest identity does not match the release pin"

let archiveSha256, manifestSha256, revision =
    ReleasePins.writeConsumerPins root manifestPath archivePath output componentId archiveName assetUri

printfn
    "dotnet asset=%s archiveSha256=%s manifestSha256=%s revision=%s"
    archiveName
    archiveSha256
    manifestSha256
    revision
