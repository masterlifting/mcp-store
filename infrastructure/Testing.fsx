// role: test helper
// Repo-shared distribution-contract assertions loaded as the explicit
// DistributionTestHelper module. Assertion failure stays a test-framework
// exception while only effectful acquisition is asynchronous and Result-valued.
module DistributionTestHelper

#load "Provenance.fs"

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open BuildProvenance

// Producer-supplied identity for the shared v2 distribution contract. The launch
// descriptor is asserted structurally rather than re-declared here.
type Producer =
    { ComponentId: string
      Version: string
      SdkVersion: string
      RuntimeVersion: string
      Platform: string
      EntryDll: string
      ArchiveName: string
      AssetUri: string
      PublishedFiles: string list }

type Layout =
    { RepoRoot: string
      DistributionDirectory: string
      ArchivePath: string
      ManifestPath: string
      PinsPath: string }

let manifestEntryName = "distribution.json"
let noticeEntryName = "NOTICE.txt"

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let assertExactFiles (name: string) (expected: string list) (actual: string list) =
    assertEqual $"{name} has no duplicate entries" actual.Length (actual |> Set.ofList |> Set.count)
    assertEqual name expected (actual |> List.sort)

let assertExactKeys (name: string) (expected: string list) (actual: string list) =
    assertEqual name (expected |> List.sort) (actual |> List.sort)

let assertNoCaseInsensitiveDuplicates name (values: string list) =
    let duplicates =
        values
        |> List.groupBy _.ToLowerInvariant()
        |> List.filter (fun (_, entries) -> entries.Length > 1)

    assertTrue name duplicates.IsEmpty

let assertConsumerRelativePath label (path: string) =
    let normalized = path.Replace('\\', '/')
    let segments = normalized.Split('/')
    let driveQualified = normalized.Length >= 2 && Char.IsLetter(normalized.[0]) && normalized.[1] = ':'

    assertEqual $"{label} uses slash separators" normalized path
    assertTrue $"{label} is not rooted" (not (normalized.StartsWith("/", StringComparison.Ordinal)))
    assertTrue $"{label} is not drive-qualified" (not driveQualified)
    assertTrue $"{label} has no traversal or empty segments"
        (segments |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> ".."))

let private hashStreamAsync (stream: Stream) : Async<string> =
    async {
        let! bytes = SHA256.HashDataAsync(stream, CancellationToken.None).AsTask() |> Async.AwaitTask
        return bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
    }

let sha256Async (path: string) : Async<string> =
    async {
        use stream = File.OpenRead path
        return! hashStreamAsync stream
    }

let private isLowerHex (length: int) (value: string) =
    not (isNull value)
    && value.Length = length
    && value |> Seq.forall (fun character -> (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

let private keysOf (node: JsonObject) = node |> Seq.map (fun pair -> pair.Key) |> Seq.toList

// Converts the typed provenance failure into the test-framework assertion
// exception that this helper reports to its callers.
let private headRevisionAsync (repoRoot: string) : Async<string> =
    async {
        match! BuildProvenance.committedHead repoRoot with
        | Ok value -> return value
        | Error error -> return failwith (ReleaseError.message error)
    }

let private assertString (label: string) (expected: string) (node: JsonNode) =
    assertTrue $"{label} is a JSON string" (not (isNull node) && node.GetValueKind() = JsonValueKind.String)
    assertEqual label expected (node.GetValue<string>())

let private launchArgumentTypes = [ "absolute-file"; "absolute-directory"; "sha256" ]

let assertDistributionContract (producer: Producer) (layout: Layout) : Async<unit> =
    async {
        let manifestMembers = producer.PublishedFiles @ [ noticeEntryName ] |> List.sort
        let archiveMembers = manifestMembers @ [ manifestEntryName ] |> List.sort
        let runtimeConfigName = Path.ChangeExtension(producer.EntryDll, ".runtimeconfig.json")

        for path in [ layout.DistributionDirectory; layout.ArchivePath; layout.ManifestPath; layout.PinsPath ] do
            assertTrue ($"required release artifact exists: {path}") (File.Exists path || Directory.Exists path)

        let! manifestText = File.ReadAllTextAsync layout.ManifestPath |> Async.AwaitTask
        let manifest = JsonNode.Parse(manifestText).AsObject()

        assertExactKeys
            "manifest declares exactly the schema-v2 fields"
            [ "schemaVersion"; "id"; "version"; "revision"; "sourceRepository"; "producer"; "archive"; "launch"; "runtime"; "files" ]
            (keysOf manifest)

        assertEqual "manifest schemaVersion" 2 (manifest["schemaVersion"].GetValue<int>())
        assertString "manifest id" producer.ComponentId manifest["id"]
        assertString "manifest version" producer.Version manifest["version"]
        assertString "manifest archive" producer.ArchiveName manifest["archive"]

        let manifestRevision = manifest["revision"].GetValue<string>()

        assertTrue "manifest revision is a full lowercase commit SHA" (isLowerHex 40 manifestRevision)

        let! headRevision = headRevisionAsync layout.RepoRoot
        assertEqual "manifest revision equals the committed HEAD" headRevision manifestRevision

        let producerNode = manifest["producer"].AsObject()
        assertExactKeys "manifest producer fields" [ "sdk"; "runtime"; "platform" ] (keysOf producerNode)
        assertString "manifest producer sdk" producer.SdkVersion producerNode["sdk"]
        assertString "manifest producer runtime" producer.RuntimeVersion producerNode["runtime"]
        assertString "manifest producer platform" producer.Platform producerNode["platform"]

        let launchNode = manifest["launch"].AsObject()
        assertExactKeys "manifest launch fields" [ "kind"; "entrypoint"; "arguments" ] (keysOf launchNode)
        assertString "manifest launch kind" "dotnet-exec" launchNode["kind"]
        assertString "manifest launch entrypoint" producer.EntryDll launchNode["entrypoint"]

        let arguments = launchNode["arguments"].AsArray()

        assertTrue "manifest launch declares at least one required argument" (arguments.Count > 0)

        for value in arguments do
            let argument = value.AsObject()
            assertExactKeys "launch argument fields" [ "flag"; "input"; "type" ] (keysOf argument)

            let flag = argument["flag"].GetValue<string>()

            assertTrue $"launch argument flag is '--'-prefixed: {flag}" (flag.StartsWith("--", StringComparison.Ordinal))
            assertTrue $"launch argument input is non-empty: {flag}" (not (String.IsNullOrWhiteSpace(argument["input"].GetValue<string>())))
            assertTrue
                $"launch argument type is a closed value kind: {flag}"
                (List.contains (argument["type"].GetValue<string>()) launchArgumentTypes)

        let runtimeNode = manifest["runtime"].AsObject()
        assertExactKeys "manifest runtime fields" [ "framework"; "version"; "config" ] (keysOf runtimeNode)
        assertString "manifest runtime framework" "Microsoft.NETCore.App" runtimeNode["framework"]
        assertString "manifest runtime config is the entrypoint runtime configuration" runtimeConfigName runtimeNode["config"]

        let declaredRuntimeVersion = runtimeNode["version"].GetValue<string>()
        assertTrue "manifest runtime version is non-empty" (not (String.IsNullOrWhiteSpace declaredRuntimeVersion))

        let manifestEntries =
            manifest["files"].AsArray()
            |> Seq.map (fun value ->
                let entry = value.AsObject()

                assertExactKeys "manifest file entry fields" [ "path"; "sha256" ] (keysOf entry)

                entry["path"].GetValue<string>(), entry["sha256"].GetValue<string>())
            |> Seq.toList

        let manifestFileNames = manifestEntries |> List.map fst
        assertExactFiles "manifest hashed members are the payload allowlist plus NOTICE" manifestMembers manifestFileNames
        assertNoCaseInsensitiveDuplicates "manifest paths are unique case-insensitively" manifestFileNames
        assertTrue "manifest no longer carries a redundant archiveFiles list" (not (manifest.ContainsKey "archiveFiles"))

        for file, hash in manifestEntries do
            assertConsumerRelativePath $"manifest path {file}" file

            assertTrue
                $"manifest path {file} is not the manifest entry"
                (not (file.Equals(manifestEntryName, StringComparison.OrdinalIgnoreCase)))

            assertTrue
                $"manifest path {file} is a member of the payload allowlist"
                (producer.PublishedFiles |> List.contains file || file = noticeEntryName)

            let! fileHash = sha256Async (Path.Combine(layout.DistributionDirectory, file))
            assertEqual $"manifest SHA-256 for {file}" fileHash hash
            assertTrue $"manifest SHA-256 is lowercase hexadecimal for {file}" (isLowerHex 64 hash)

        // The runtime prerequisite is derived from the hashed runtime configuration, so
        // the packaged bytes must declare exactly the version the manifest states.
        let! runtimeConfigText =
            File.ReadAllTextAsync(Path.Combine(layout.DistributionDirectory, runtimeConfigName)) |> Async.AwaitTask

        let runtimeConfigRoot = JsonNode.Parse(runtimeConfigText).AsObject()
        let runtimeOptions = runtimeConfigRoot.["runtimeOptions"].AsObject()
        let frameworkNode = runtimeOptions.["framework"].AsObject()

        assertString "packaged runtime configuration framework" "Microsoft.NETCore.App" frameworkNode.["name"]
        assertString "packaged runtime configuration version matches the manifest runtime" declaredRuntimeVersion frameworkNode.["version"]

        let archive = ZipFile.OpenRead layout.ArchivePath
        let entries = archive.Entries |> Seq.map (fun entry -> entry.FullName.Replace('\\', '/')) |> Seq.toList
        assertExactFiles "archive entries are exactly the hashed members plus the manifest" archiveMembers entries
        assertNoCaseInsensitiveDuplicates "archive entries are unique case-insensitively" entries

        for entry in entries do
            assertConsumerRelativePath $"archive entry {entry}" entry
            let lower = entry.ToLowerInvariant()
            let segments = lower.Split('/')

            assertTrue
                ($"archive entry is not a source/build artifact: {entry}")
                (not (
                    segments |> Array.exists (fun segment -> segment = "src" || segment = "bin" || segment = "obj")
                    || lower.StartsWith("src/")
                    || lower.StartsWith("bin/")
                    || lower.StartsWith("obj/")
                    || lower.EndsWith(".fs")
                    || lower.EndsWith(".fsproj")
                    || lower.EndsWith(".pdb")
                    || lower.EndsWith(".exe")
                    || lower.Contains("apphost")
                ))

        let manifestHashes = manifestEntries |> Map.ofList

        for entry in archive.Entries do
            match manifestHashes |> Map.tryFind (entry.FullName.Replace('\\', '/')) with
            | Some expectedHash ->
                use payload = entry.Open()
                let! payloadHash = hashStreamAsync payload
                assertEqual $"archive payload hash for {entry.FullName}" expectedHash payloadHash
            | None -> ()

        archive.Dispose()

        // The consumer pin and the published release-metadata projection share one
        // unwrapped canonical schema; no keyed five-field inner record exists.
        let! pinsText = File.ReadAllTextAsync layout.PinsPath |> Async.AwaitTask
        let pin = JsonNode.Parse(pinsText).AsObject()

        assertExactKeys
            "pin declares exactly the canonical release-metadata fields"
            [ "schemaVersion"; "componentId"; "version"; "assetUri"; "archiveName"; "archiveSha256"; "manifestSha256"; "publishedRevision" ]
            (keysOf pin)

        assertEqual "pin schemaVersion" 1 (pin["schemaVersion"].GetValue<int>())
        assertString "pin componentId" producer.ComponentId pin["componentId"]
        assertString "pin version" producer.Version pin["version"]
        assertString "pin assetUri" producer.AssetUri pin["assetUri"]
        assertString "pin archiveName" producer.ArchiveName pin["archiveName"]

        let! archiveSha256 = sha256Async layout.ArchivePath
        let! manifestSha256 = sha256Async layout.ManifestPath
        assertEqual "pin archiveSha256" archiveSha256 (pin["archiveSha256"].GetValue<string>())
        assertEqual "pin manifestSha256" manifestSha256 (pin["manifestSha256"].GetValue<string>())
        assertEqual "pin publishedRevision equals the validated manifest revision" manifestRevision (pin["publishedRevision"].GetValue<string>())
        assertEqual "pin publishedRevision equals the committed HEAD" headRevision (pin["publishedRevision"].GetValue<string>())
        assertString "pin producerSdk" producer.SdkVersion pin["producerSdk"]
        assertString "pin producerRuntime" producer.RuntimeVersion pin["producerRuntime"]
        assertString "pin producerPlatform" producer.Platform pin["producerPlatform"]
    }
