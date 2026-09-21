open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let dist = Path.Combine(root, "dotnet", "dist")
let output = Path.Combine(dist, "consumer-pins.json")
let componentId = "dotnet"
let version = "1.0.0"
let archiveName = $"{componentId}-v{version}.zip"

let sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

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

let pins = JsonObject()
let value = JsonObject()
value["assetName"] <- JsonValue.Create archiveName
value["archiveSha256"] <- JsonValue.Create(sha256 archivePath)
value["manifestSha256"] <- JsonValue.Create(sha256 manifestPath)
pins[componentId] <- value

File.WriteAllText(output, pins.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

printfn "dotnet asset=%s archiveSha256=%s manifestSha256=%s" archiveName (sha256 archivePath) (sha256 manifestPath)
