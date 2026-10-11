// role: test helper
// Shared deterministic fixture writer: real-byte canonical archives whose embedded `files` hashes match the actual decompressed payloads.
// Caller loads Provenance.fsx and the production Compare.fsx so every shared type comes from one FSI module.

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let canonicalComponentId = "dotnet"
let canonicalVersion = "1.0.4"
let canonicalArchiveName = "v1.0.4.zip"
let canonicalEvaluatedRevision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
let canonicalPublishedRevision = "fc0aefa9db0fb7262ee6ca1b54bd6bbb752b8b01"
let canonicalSdk = "11.0.100-rc.1.26425.128"
let canonicalRuntime = "11.0.0-rc.1.26425.128"
let canonicalPlatform = "win-x64"
let canonicalAssetUri = "https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.4.zip"

let canonicalEntryAllowlist =
    [
        "FSharp.Core.dll"
        "Mcp.Dotnet.deps.json"
        "Mcp.Dotnet.dll"
        "Mcp.Dotnet.runtimeconfig.json"
        "NOTICE.txt"
        "distribution.json"
    ]

let canonicalEntryPayloads () : (string * byte[]) list =
    [
        "FSharp.Core.dll", Encoding.UTF8.GetBytes "fsharp-core-fixture-payload"
        "Mcp.Dotnet.deps.json", Encoding.UTF8.GetBytes "{\"runtimeOptions\":{\"tfm\":\"net11.0\"}}"
        "Mcp.Dotnet.dll", Encoding.UTF8.GetBytes "dotnet-dll-fixture-payload"
        "Mcp.Dotnet.runtimeconfig.json", Encoding.UTF8.GetBytes "{\"runtimeOptions\":{\"tfm\":\"net11.0\"}}"
        "NOTICE.txt", Encoding.UTF8.GetBytes "mcp-store notice payload"
    ]

let sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

// ComponentContract fields; tests instantiate their own contract with these values.
let contractComponentId = canonicalComponentId
let contractVersion = canonicalVersion
let contractArchiveName = canonicalArchiveName
let contractEntryAllowlist = canonicalEntryAllowlist

let buildManifestText (revision: string) (sdk: string) (payloadHashes: (string * string) list) =
    let manifest = JsonObject()
    manifest.["schemaVersion"] <- JsonValue.Create 1
    manifest.["id"] <- JsonValue.Create canonicalComponentId
    manifest.["version"] <- JsonValue.Create canonicalVersion
    manifest.["revision"] <- JsonValue.Create revision
    manifest.["sdk"] <- JsonValue.Create sdk
    manifest.["entryDll"] <- JsonValue.Create "Mcp.Dotnet.dll"
    manifest.["archive"] <- JsonValue.Create canonicalArchiveName
    let files = JsonArray()
    for path, hash in payloadHashes do
        let entry = JsonObject()
        entry.["path"] <- JsonValue.Create path
        entry.["sha256"] <- JsonValue.Create hash
        files.Add entry
    manifest.["files"] <- files
    let archiveFiles = JsonArray()
    for path in canonicalEntryAllowlist do
        archiveFiles.Add(JsonValue.Create path)
    manifest.["archiveFiles"] <- archiveFiles
    manifest.ToJsonString(JsonSerializerOptions(WriteIndented = true))

// Canonical payload SHA-256s computed from the canonical payload bytes.
// The manifest `files` table covers only the runtime payload files (not NOTICE.txt
// or distribution.json, which are excluded by the production self-consistency gate).
let canonicalPayloadHashes () : (string * string) list =
    canonicalEntryPayloads ()
    |> List.filter (fun (name, _) -> name <> "NOTICE.txt")
    |> List.map (fun (name, bytes) -> name, sha256Hex bytes)

let private writeArchiveInternal (archivePath: string) (manifestBytes: byte[]) (extraEntries: (string * byte[]) list) =
    let canonical = canonicalEntryPayloads ()
    let extrasMap = extraEntries |> Map.ofList
    let canonicalByName = canonical |> Map.ofList
    let merged =
        canonical
        |> List.map (fun (name, defaultBytes) ->
            match Map.tryFind name extrasMap with
            | Some replacement -> name, replacement
            | None -> name, defaultBytes)
    use archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)
    for name, bytes in merged do
        let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        use source = new MemoryStream(bytes)
        use target = entry.Open()
        source.CopyTo(target)
    let manifestEntry = archive.CreateEntry("distribution.json", CompressionLevel.Optimal)
    manifestEntry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
    use manifestSource = new MemoryStream(manifestBytes)
    use manifestTarget = manifestEntry.Open()
    manifestSource.CopyTo(manifestTarget)

// Writes a single canonical archive; manifest's `files` SHA-256s match the actual payloads.
let writeCanonicalArchive (archivePath: string) (manifestPath: string) (revision: string) (sdk: string) =
    let payloadHashes = canonicalPayloadHashes ()
    let manifestText = buildManifestText revision sdk payloadHashes
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    File.WriteAllText(manifestPath, manifestText)
    writeArchiveInternal archivePath manifestBytes []

// Manifest with `files` SHA-256s matching the actual payload bytes; used by every
// archive test that requires self-consistency to pass.
let writeRevisionVariantArchive (archivePath: string) (manifestPath: string) (revision: string) =
    writeCanonicalArchive archivePath manifestPath revision canonicalSdk

// Replaces the canonical `Mcp.Dotnet.dll` payload with caller-supplied bytes; the
// manifest is rewritten so the declared SHA-256 matches the new payload, preserving
// self-consistency under the production gate.
let writeModifiedDotnetDllArchive (archivePath: string) (manifestPath: string) (replacementBytes: byte[]) (revision: string) =
    let payloadHashes =
        canonicalEntryPayloads ()
        |> List.filter (fun (name, _) -> name <> "NOTICE.txt")
        |> List.map (fun (name, bytes) ->
            if name = "Mcp.Dotnet.dll" then
                name, sha256Hex replacementBytes
            else
                name, sha256Hex bytes)
    let manifestText = buildManifestText revision canonicalSdk payloadHashes
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    File.WriteAllText(manifestPath, manifestText)
    let extra = [ "Mcp.Dotnet.dll", replacementBytes ]
    writeArchiveInternal archivePath manifestBytes extra

let newTempDir (prefix: string) =
    let path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory path |> ignore
    path

let safeDeleteDir (dir: string) =
    try Directory.Delete(dir, true) with _ -> ()
