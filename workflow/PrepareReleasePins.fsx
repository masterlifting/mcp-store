open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let dist = Path.Combine(root, "workflow", "dist")
let output = Path.Combine(dist, "consumer-pins.json")

let sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let manifest id = Path.Combine(dist, id, "distribution.json")
let archive id = Path.Combine(dist, $"{id}-v0.1.0.zip")
let revision =
    let info = Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    use process = Diagnostics.Process.Start info
    let value = process.StandardOutput.ReadToEnd().Trim()
    process.WaitForExit()
    value

for id in [ "mcp-verifier"; "task-runtime" ] do
    if not (File.Exists(archive id)) || not (File.Exists(manifest id)) then
        failwithf "run BuildDistributions.fsx before preparing pins: %s" id

let pins = JsonObject()
for id in [ "mcp-verifier"; "task-runtime" ] do
    let value = JsonObject()
    value["assetName"] <- JsonValue.Create(Path.GetFileName(archive id))
    value["archiveSha256"] <- JsonValue.Create(sha256 (archive id))
    value["manifestSha256"] <- JsonValue.Create(sha256 (manifest id))
    pins[id] <- value

File.WriteAllText(output, pins.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
