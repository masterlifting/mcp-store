module Release.Tests.Fixture

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open SharedFailure
open BuildProvenance
open DistributionContract

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

// Canonical v2 fixture identity. Emission goes through the shared renderer so the
// synthetic bytes are the exact contract the producers emit, never a parallel shape.
let canonicalComponentId = "dotnet"
let canonicalVersion = "1.0.5"
let canonicalArchiveName = "v1.0.5.zip"
let canonicalEvaluatedRevision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
let canonicalPublishedRevision = "fc0aefa9db0fb7262ee6ca1b54bd6bbb752b8b01"
let canonicalSdk = "11.0.100-rc.1.26425.128"
let canonicalRuntime = "11.0.0-rc.1.26425.128"
let canonicalPlatform = "win-x64"
let canonicalAssetUri = "https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.5.zip"
let canonicalSourceRepository = "https://github.com/masterlifting/mcp-store"
let canonicalEntrypoint = "Mcp.Dotnet.dll"
let canonicalRuntimeConfig = "Mcp.Dotnet.runtimeconfig.json"

let canonicalLaunchArguments : LaunchArgument list =
    [ { Flag = "--dotnet-host"; Input = "dotnetHost"; Type = AbsoluteFile }
      { Flag = "--artifact-root"; Input = "artifactRoot"; Type = AbsoluteDirectory } ]

// Exact archive membership: payload allowlist plus the always-hashed NOTICE and the manifest.
let canonicalEntryAllowlist =
    [ "FSharp.Core.dll"
      "Mcp.Dotnet.deps.json"
      "Mcp.Dotnet.dll"
      "Mcp.Dotnet.runtimeconfig.json"
      "NOTICE.txt"
      "distribution.json" ]

let sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

// The packaged runtime configuration is the authoritative runtime prerequisite, so its
// fixture bytes must actually declare the framework the manifest derives.
let private runtimeConfigNode (version: string) : JsonObject =
    let framework = JsonObject()
    framework["name"] <- JsonValue.Create "Microsoft.NETCore.App"
    framework["version"] <- JsonValue.Create version
    let options = JsonObject()
    options["framework"] <- framework
    let root = JsonObject()
    root["runtimeOptions"] <- options
    root

let runtimeConfigPayloadFor (version: string) : byte[] =
    Encoding.UTF8.GetBytes((runtimeConfigNode version).ToJsonString())

let private runtimeConfigPayload () : byte[] = runtimeConfigPayloadFor canonicalRuntime


let canonicalEntryPayloads () : (string * byte[]) list =
    [
        "FSharp.Core.dll", Encoding.UTF8.GetBytes "fsharp-core-fixture-payload"
        "Mcp.Dotnet.deps.json", Encoding.UTF8.GetBytes "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v11.0\"}}"
        "Mcp.Dotnet.dll", Encoding.UTF8.GetBytes "dotnet-dll-fixture-payload"
        "Mcp.Dotnet.runtimeconfig.json", runtimeConfigPayload ()
        "NOTICE.txt", Encoding.UTF8.GetBytes "mcp-store notice payload"
    ]

// Every hashed manifest member (all archive members except the manifest entry).
let canonicalManifestPayloads () : (string * byte[]) list = canonicalEntryPayloads ()

let contractComponentId = canonicalComponentId
let contractVersion = canonicalVersion
let contractArchiveName = canonicalArchiveName
let contractEntryAllowlist = canonicalEntryAllowlist

// One canonical manifest record; `render` owns the byte layout.
let canonicalManifest (revision: string) (sdk: string) (payloadHashes: (string * string) list) : DistributionManifest =
    { SchemaVersion = DistributionContract.schemaVersion
      Id = canonicalComponentId
      Version = canonicalVersion
      Revision = revision
      SourceRepository = canonicalSourceRepository
      Producer =
        { Sdk = sdk
          Runtime = canonicalRuntime
          Platform = canonicalPlatform }
      Archive = canonicalArchiveName
      Launch =
        { Kind = launchKindDotnetExec
          Entrypoint = canonicalEntrypoint
          Arguments = canonicalLaunchArguments }
      Runtime =
        { Framework = runtimeFramework
          Version = canonicalRuntime
          Config = canonicalRuntimeConfig }
      Files = payloadHashes |> List.map (fun (path, hash) -> { Path = path; Sha256 = hash }) }

let buildManifestText (revision: string) (sdk: string) (payloadHashes: (string * string) list) =
    canonicalManifest revision sdk payloadHashes |> DistributionContract.render

let canonicalPayloadHashes () : (string * string) list =
    canonicalManifestPayloads ()
    |> List.map (fun (name, bytes) -> name, sha256Hex bytes)

// The trusted contract the synthetic archive is validated against; sourced from the
// same fixture identity so the comparison call sites never restate policy.
let canonicalContract : ReleaseCompare.ComponentContract =
    { ComponentId = canonicalComponentId
      Version = canonicalVersion
      ArchiveName = canonicalArchiveName
      SourceRepository = canonicalSourceRepository
      Producer =
        { Sdk = canonicalSdk
          Runtime = canonicalRuntime
          Platform = canonicalPlatform }
      Launch =
        { Kind = launchKindDotnetExec
          Entrypoint = canonicalEntrypoint
          Arguments = canonicalLaunchArguments }
      EntryAllowlist = canonicalEntryAllowlist }

// Policy-driven fixtures for the release-boundary tests: the manifest is rendered by
// the shared contract so the synthetic bytes satisfy the trusted contract exactly.
let policyPayloads (policy: ReleasePolicy.PolicyProjection) (prefix: string) : (string * byte[]) list =
    let runtimeConfig = runtimeConfigNameFor policy.EntryDll

    let files =
        policy.PublishedFiles
        |> List.map (fun name ->
            let bytes =
                if name = runtimeConfig then
                    runtimeConfigPayloadFor policy.RuntimeVersion
                else
                    Encoding.UTF8.GetBytes $"{prefix}:{policy.ComponentId}:{name}"

            name, bytes)

    files @ [ noticeEntryName, Encoding.UTF8.GetBytes $"{prefix} notice" ]

let policyManifestText
    (policy: ReleasePolicy.PolicyProjection)
    (revision: string)
    (payloadHashes: (string * string) list)
    : string =
    let repository =
        match sourceRepositoryFromAssetUri policy.AssetUri with
        | Ok value -> value
        | Error error -> failwith (ReleaseError.message error)

    let manifest : DistributionManifest =
        { SchemaVersion = DistributionContract.schemaVersion
          Id = policy.ComponentId
          Version = policy.Version
          Revision = revision
          SourceRepository = repository
          Producer =
            { Sdk = policy.SdkVersion
              Runtime = policy.RuntimeVersion
              Platform = policy.Platform }
          Archive = policy.ArchiveName
          Launch =
            { Kind = launchKindDotnetExec
              Entrypoint = policy.EntryDll
              Arguments = policy.LaunchArguments }
          Runtime =
            { Framework = runtimeFramework
              Version = policy.RuntimeVersion
              Config = runtimeConfigNameFor policy.EntryDll }
          Files = payloadHashes |> List.map (fun (path, hash) -> { Path = path; Sha256 = hash }) }

    DistributionContract.render manifest

let policyManifestFor (policy: ReleasePolicy.PolicyProjection) (revision: string) (payloads: (string * byte[]) list) : string =
    policyManifestText policy revision (payloads |> List.map (fun (name, bytes) -> name, sha256Hex bytes))

let private writeArchiveInternal (archivePath: string) (manifestBytes: byte[]) (extraEntries: (string * byte[]) list) =
    let canonical = canonicalEntryPayloads ()
    let extrasMap = extraEntries |> Map.ofList
    use archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)

    for name, defaultBytes in canonical do
        let bytes =
            match Map.tryFind name extrasMap with
            | Some replacement -> replacement
            | None -> defaultBytes

        let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        use source = new MemoryStream(bytes)
        use target = entry.Open()
        source.CopyTo(target)

    let manifestEntry = archive.CreateEntry(manifestEntryName, CompressionLevel.Optimal)
    manifestEntry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
    use manifestSource = new MemoryStream(manifestBytes)
    use manifestTarget = manifestEntry.Open()
    manifestSource.CopyTo(manifestTarget)

let writeCanonicalArchive (archivePath: string) (manifestPath: string) (revision: string) (sdk: string) =
    let manifestText = buildManifestText revision sdk (canonicalPayloadHashes ())
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    File.WriteAllText(manifestPath, manifestText)
    writeArchiveInternal archivePath manifestBytes []

// Writes the canonical members around caller-supplied manifest bytes, so a mutation
// case varies only the manifest field it targets.
let writeManifestArchive (archivePath: string) (manifestText: string) =
    writeArchiveInternal archivePath (Encoding.UTF8.GetBytes manifestText) []

let writeRevisionVariantArchive (archivePath: string) (manifestPath: string) (revision: string) =
    writeCanonicalArchive archivePath manifestPath revision canonicalSdk

let writeModifiedDotnetDllArchive (archivePath: string) (manifestPath: string) (replacementBytes: byte[]) (revision: string) =
    let payloadHashes =
        canonicalManifestPayloads ()
        |> List.map (fun (name, bytes) ->
            if name = canonicalEntrypoint then
                name, sha256Hex replacementBytes
            else
                name, sha256Hex bytes)

    let manifestText = buildManifestText revision canonicalSdk payloadHashes
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    File.WriteAllText(manifestPath, manifestText)
    writeArchiveInternal archivePath manifestBytes [ canonicalEntrypoint, replacementBytes ]

let newTempDir (prefix: string) =
    let path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory path |> ignore
    path

let safeDeleteDir (dir: string) =
    try Directory.Delete(dir, true) with _ -> ()
