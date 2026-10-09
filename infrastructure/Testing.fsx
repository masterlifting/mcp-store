// role: test helper
// Repo-shared distribution-contract assertions loaded as the explicit
// DistributionTestHelper module. Assertion failure stays a test-framework
// exception while only effectful acquisition is asynchronous and Result-valued.
module DistributionTestHelper

#load "Provenance.fsx"

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading
open BuildProvenance

type Producer =
    { ComponentId: string
      Version: string
      SdkVersion: string
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

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let assertExactFiles (name: string) (expected: string list) (actual: string list) =
    assertEqual $"{name} has no duplicate entries" actual.Length (actual |> Set.ofList |> Set.count)
    assertEqual name expected (actual |> List.sort)

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

// Converts the typed provenance failure into the test-framework assertion
// exception that this helper reports to its callers.
let private headRevisionAsync (repoRoot: string) : Async<string> =
    async {
        match! BuildProvenance.committedHead repoRoot with
        | Ok value -> return value
        | Error error -> return failwith (ReleaseError.message error)
    }

let assertDistributionContract (producer: Producer) (layout: Layout) : Async<unit> =
    async {
        let archiveFiles = producer.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] |> List.sort

        for path in [ layout.DistributionDirectory; layout.ArchivePath; layout.ManifestPath; layout.PinsPath ] do
            assertTrue ($"required release artifact exists: {path}") (File.Exists path || Directory.Exists path)

        let! manifestText = File.ReadAllTextAsync layout.ManifestPath |> Async.AwaitTask
        let manifest = JsonNode.Parse(manifestText).AsObject()
        assertEqual "manifest id" producer.ComponentId (manifest["id"].GetValue<string>())
        assertEqual "manifest version" producer.Version (manifest["version"].GetValue<string>())
        assertEqual "manifest sdk" producer.SdkVersion (manifest["sdk"].GetValue<string>())
        assertEqual "manifest archive" producer.ArchiveName (manifest["archive"].GetValue<string>())
        assertEqual "manifest entry DLL" producer.EntryDll (manifest["entryDll"].GetValue<string>())

        let manifestRevision = manifest["revision"].GetValue<string>()

        assertTrue "manifest revision is a full commit SHA"
            (manifestRevision.Length = 40 && manifestRevision |> Seq.forall Uri.IsHexDigit)

        let! headRevision = headRevisionAsync layout.RepoRoot
        assertEqual "manifest revision equals the committed HEAD" headRevision manifestRevision

        let manifestEntries =
            manifest["files"].AsArray()
            |> Seq.map (fun value ->
                let entry = value.AsObject()

                assertExactFiles
                    "manifest file entry properties"
                    [ "path"; "sha256" ]
                    (entry |> Seq.map (fun pair -> pair.Key) |> Seq.toList)

                entry["path"].GetValue<string>(), entry["sha256"].GetValue<string>())
            |> Seq.toList

        let manifestFileNames = manifestEntries |> List.map fst
        assertExactFiles "manifest runtime files are the deterministic allowlist" producer.PublishedFiles manifestFileNames
        assertNoCaseInsensitiveDuplicates "manifest paths are unique case-insensitively" manifestFileNames

        assertEqual
            "manifest archive files are the deterministic allowlist"
            archiveFiles
            (manifest["archiveFiles"].AsArray() |> Seq.map (fun value -> value.GetValue<string>()) |> Seq.toList)

        assertNoCaseInsensitiveDuplicates "archive allowlist paths are unique case-insensitively" archiveFiles

        for file, hash in manifestEntries do
            assertConsumerRelativePath $"manifest path {file}" file

            assertTrue
                $"manifest path {file} is not distribution.json"
                (not (file.Equals("distribution.json", StringComparison.OrdinalIgnoreCase)))

            assertEqual $"manifest path is the exact runtime allowlist entry {file}" file (producer.PublishedFiles |> List.find ((=) file))
            let! fileHash = sha256Async (Path.Combine(layout.DistributionDirectory, file))
            assertEqual $"manifest SHA-256 for {file}" fileHash hash

            assertTrue
                $"manifest SHA-256 is lowercase hexadecimal for {file}"
                (hash.Length = 64 && hash = hash.ToLowerInvariant() && hash |> Seq.forall Uri.IsHexDigit)

        let archive = ZipFile.OpenRead layout.ArchivePath
        let entries = archive.Entries |> Seq.map (fun entry -> entry.FullName.Replace('\\', '/')) |> Seq.toList
        assertExactFiles "archive entries are the deterministic allowlist" archiveFiles entries
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

        let! pinsText = File.ReadAllTextAsync layout.PinsPath |> Async.AwaitTask
        let pins = JsonNode.Parse(pinsText).AsObject()
        let pinKeys = pins |> Seq.map (fun pair -> pair.Key) |> Set.ofSeq
        assertEqual $"consumer pins contain only {producer.ComponentId}" (Set.singleton producer.ComponentId) pinKeys
        let pin = pins[producer.ComponentId].AsObject()

        assertExactFiles
            "pin properties"
            ([ "assetName"; "assetUri"; "archiveSha256"; "manifestSha256"; "revision" ] |> List.sort)
            (pin |> Seq.map (fun pair -> pair.Key) |> Seq.toList)

        let! archiveSha256 = sha256Async layout.ArchivePath
        let! manifestSha256 = sha256Async layout.ManifestPath
        assertEqual "pin asset name" producer.ArchiveName (pin["assetName"].GetValue<string>())
        assertEqual "pin asset uri" producer.AssetUri (pin["assetUri"].GetValue<string>())
        assertEqual "pin archive SHA-256" archiveSha256 (pin["archiveSha256"].GetValue<string>())
        assertEqual "pin manifest SHA-256" manifestSha256 (pin["manifestSha256"].GetValue<string>())
        assertEqual "pin revision equals validated manifest revision" manifestRevision (pin["revision"].GetValue<string>())
        assertEqual "pin revision equals the committed HEAD" headRevision (pin["revision"].GetValue<string>())
    }
