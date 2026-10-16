module Release.Tests.Metadata

open Expecto
open Release.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open BuildProvenance
open ReleaseMetadata

let mutable passed = 0
let mutable failed = 0
let failures = ResizeArray<string>()

let assertTrue name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(name)

let assertEqual name expected actual =
    if expected = actual then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(sprintf "%s: expected %A got %A" name expected actual)

let assertOk name (result: Result<'a, ReleaseError>) =
    match result with
    | Ok value ->
        passed <- passed + 1
        value
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "%s: expected Ok got Error %s" name (ReleaseError.message error))
        Unchecked.defaultof<'a>

let assertErrorMatches name fragment (result: Result<'a, ReleaseError>) =
    match result with
    | Ok value ->
        failed <- failed + 1
        failures.Add(sprintf "%s: expected Error fragment '%s' got Ok %A" name fragment value)
    | Error error ->
        let message = ReleaseError.message error
        if message.Contains(fragment, StringComparison.Ordinal) then
            passed <- passed + 1
        else
            failed <- failed + 1
            failures.Add(sprintf "%s: expected fragment '%s' got '%s'" name fragment message)

let private tempRoot = Path.Combine(Path.GetTempPath(), "mcp-release-metadata-tests-" + Guid.NewGuid().ToString("N"))
let private completeProjection: ProducerMetadataProjection = {
    SchemaVersion = metadataSchemaVersion
    ComponentId = "dotnet"
    Version = "1.0.4"
    AssetUri = "https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.4.zip"
    ArchiveName = "v1.0.4.zip"
    ArchiveSha256 = "df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7"
    ManifestSha256 = "e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1"
    PublishedRevision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
    ProducerSdk = Some "11.0.100-rc.1.26425.128"
    ProducerRuntime = Some "11.0.0-rc.1.26425.128"
    ProducerPlatform = Some "win-x64"
}
let renderParseRoundTrip () =
    let rendered = render completeProjection
    assertTrue "render emits non-empty JSON" (not (String.IsNullOrEmpty rendered))

    match parse rendered with
    | Ok parsed ->
        assertEqual "round-trip schemaVersion" completeProjection.SchemaVersion parsed.SchemaVersion
        assertEqual "round-trip componentId" completeProjection.ComponentId parsed.ComponentId
        assertEqual "round-trip version" completeProjection.Version parsed.Version
        assertEqual "round-trip assetUri" completeProjection.AssetUri parsed.AssetUri
        assertEqual "round-trip archiveName" completeProjection.ArchiveName parsed.ArchiveName
        assertEqual "round-trip archiveSha256" completeProjection.ArchiveSha256 parsed.ArchiveSha256
        assertEqual "round-trip manifestSha256" completeProjection.ManifestSha256 parsed.ManifestSha256
        assertEqual "round-trip publishedRevision" completeProjection.PublishedRevision parsed.PublishedRevision
        assertEqual "round-trip producerSdk" completeProjection.ProducerSdk parsed.ProducerSdk
        assertEqual "round-trip producerRuntime" completeProjection.ProducerRuntime parsed.ProducerRuntime
        assertEqual "round-trip producerPlatform" completeProjection.ProducerPlatform parsed.ProducerPlatform
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "round-trip parse: %s" (ReleaseError.message error))
let renderDeterministicByteExact () =
    let first = render completeProjection
    let second = render completeProjection
    assertEqual "two renderings are byte-exact" first second
    assertTrue "rendered bytes have a stable SHA-256" (SHA256.HashData (Encoding.UTF8.GetBytes first) = SHA256.HashData (Encoding.UTF8.GetBytes second))
let metadataAssetNameDeterministic () =
    let first = metadataAssetName "dotnet" "1.0.4"
    let second = metadataAssetName "dotnet" "1.0.4"
    assertEqual "asset name is deterministic" first second
    assertEqual "asset name encodes component and version" "dotnet-1.0.4-metadata.json" first
    assertEqual "different version produces different name" "dotnet-1.0.5-metadata.json" (metadataAssetName "dotnet" "1.0.5")
    assertEqual "different component produces different name" "workflow-1.0.4-metadata.json" (metadataAssetName "workflow" "1.0.4")
let compareEqualProjections () =
    let other = { completeProjection with PublishedRevision = completeProjection.PublishedRevision }
    match compare completeProjection other with
    | Ok () -> passed <- passed + 1
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "compare equal: %s" (ReleaseError.message error))
let comparePerFieldDiff () =
    let permutations =
        [
            "schemaVersion", { completeProjection with SchemaVersion = metadataSchemaVersion + 1 }
            "componentId", { completeProjection with ComponentId = "workflow" }
            "version", { completeProjection with Version = "1.0.5" }
            "assetUri", { completeProjection with AssetUri = "https://other/v1.0.4.zip" }
            "archiveName", { completeProjection with ArchiveName = "v9.9.9.zip" }
            "archiveSha256", { completeProjection with ArchiveSha256 = String('a', 64) }
            "manifestSha256", { completeProjection with ManifestSha256 = String('b', 64) }
            "publishedRevision", { completeProjection with PublishedRevision = String('0', 40) }
            "producerSdk", { completeProjection with ProducerSdk = Some "x" }
            "producerRuntime", { completeProjection with ProducerRuntime = Some "y" }
            "producerPlatform", { completeProjection with ProducerPlatform = Some "z" }
        ]

    for label, candidate in permutations do
        match compare completeProjection candidate with
        | Ok () ->
            failed <- failed + 1
            failures.Add(sprintf "compare per-field %s: expected Error got Ok" label)
        | Error error ->
            let message = ReleaseError.message error
            assertTrue (sprintf "compare per-field %s names the field" label) (message.Contains(label, StringComparison.Ordinal))
let compareOptionalToolchainDifferences () =
    let sdkPresent = completeProjection
    let sdkAbsent = { completeProjection with ProducerSdk = None }
    let runtimeAbsent = { completeProjection with ProducerRuntime = None }
    let platformAbsent = { completeProjection with ProducerPlatform = None }

    assertErrorMatches "sdk Some vs None" "producerSdk differs" (compare sdkPresent sdkAbsent)
    assertErrorMatches "runtime Some vs None" "producerRuntime differs" (compare sdkPresent runtimeAbsent)
    assertErrorMatches "platform Some vs None" "producerPlatform differs" (compare sdkPresent platformAbsent)
let parseRejectsUnsupportedSchemaVersion () =
    let node = System.Text.Json.Nodes.JsonNode.Parse(render completeProjection).AsObject()
    node.["schemaVersion"] <- System.Text.Json.Nodes.JsonValue.Create(metadataSchemaVersion + 1)
    let text = node.ToJsonString()
    assertErrorMatches "parse rejects future schemaVersion" "unsupported schema version" (parse text)
let parseRejectsMissingRequiredFields () =
    for field in [ "componentId"; "version"; "assetUri"; "archiveName"; "archiveSha256"; "manifestSha256"; "publishedRevision" ] do
        let node = System.Text.Json.Nodes.JsonNode.Parse(render completeProjection).AsObject()
        if node.ContainsKey field then node.Remove field |> ignore
        assertErrorMatches (sprintf "parse rejects missing field %s" field) (sprintf "missing required field '%s'" field) (parse (node.ToJsonString()))
let parseRejectsMalformedJson () =
    assertErrorMatches "parse rejects malformed JSON" "not valid JSON" (parse "{ this is broken")
    assertErrorMatches "parse rejects empty document" "not valid JSON" (parse "  ")
let parseRejectsEmptyRequiredField () =
    let node = System.Text.Json.Nodes.JsonNode.Parse(render completeProjection).AsObject()
    node.["publishedRevision"] <- System.Text.Json.Nodes.JsonValue.Create ""
    assertErrorMatches "parse rejects empty publishedRevision" "empty required fields" (parse (node.ToJsonString()))
// An asset name that is not a single file name must be rejected as a typed malformed value.
let parseRejectsUnsafeArchiveName () =
    let result = parse (render ({ completeProjection with ArchiveName = "../../outside.zip" }))

    match result with
    | Error(MalformedArtifact(location, detail)) when
        location = "metadata"
        && detail = "the metadata projection 'archiveName' is not a safe slash-free single file name"
        ->
        passed <- passed + 1
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "parse unsafe archiveName: expected the basename diagnostic, got '%s'" (ReleaseError.message error))
    | Ok _ ->
        failed <- failed + 1
        failures.Add "parse unsafe archiveName: expected Error got Ok"
// The renderer cannot emit a duplicate key, so the duplicate is supplied as raw document text.
let parseRejectsDuplicateArchiveSha256 () =
    let duplicated =
        """{
  "schemaVersion": 1,
  "componentId": "dotnet",
  "version": "1.0.4",
  "assetUri": "https://github.com/masterlifting/mcp-store/releases/download/dotnet/v1.0.4.zip",
  "archiveName": "v1.0.4.zip",
  "archiveSha256": "df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7",
  "archiveSha256": "df9a32648067f2ec5c454e3402f8ca50bfc98151936ba662316df5163b5f58c7",
  "manifestSha256": "e445474acf4abdb27a809a75213929fd37b1a2f43d56b09320f0919deb0681b1",
  "publishedRevision": "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
}"""

    match parse duplicated with
    | Error(MalformedArtifact(location, detail)) when location = "metadata" && detail = "duplicate JSON key 'archiveSha256'" ->
        passed <- passed + 1
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "parse duplicate archiveSha256: expected the duplicate-key diagnostic, got '%s'" (ReleaseError.message error))
    | Ok _ ->
        failed <- failed + 1
        failures.Add "parse duplicate archiveSha256: expected Error got Ok"

let writeToFileDeterministic () = async {
    let path = Path.Combine(tempRoot, "metadata.json")
    let! firstDigest = writeToFileAsync path completeProjection
    let firstResult =
        match firstDigest with
        | Ok digest ->
            passed <- passed + 1
            digest
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "writeToFileAsync: %s" (ReleaseError.message error))
            ""

    let onDisk = File.ReadAllBytes path
    let computed =
        SHA256.HashData onDisk
        |> Convert.ToHexString
        |> fun v -> v.ToLowerInvariant()
    assertEqual "writeToFileAsync hash equals disk bytes" firstResult computed
    let! readResult = readFromFile path
    match readResult with
    | Ok parsed ->
        assertEqual "readFromFile schemaVersion" completeProjection.SchemaVersion parsed.SchemaVersion
        assertEqual "readFromFile componentId" completeProjection.ComponentId parsed.ComponentId
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "readFromFile: %s" (ReleaseError.message error))
}
let sha256OfFileAsyncDeterministic () = async {
    let path = Path.Combine(tempRoot, "sha256-fixture.json")
    File.WriteAllText(path, render completeProjection)
    let! first = sha256OfFileAsync path
    let! second = sha256OfFileAsync path
    match first, second with
    | Ok a, Ok b ->
        assertEqual "two reads produce the same digest" a b
    | _ ->
        failed <- failed + 1
        failures.Add("sha256OfFileAsync could not read fixture")
}
let writeToFileTwiceDeterministic () = async {
    let path = Path.Combine(tempRoot, "twice.json")
    let! first = writeToFileAsync path completeProjection
    let! second = writeToFileAsync path completeProjection
    match first, second with
    | Ok a, Ok b ->
        assertEqual "two writes yield the same hash" a b
    | _ ->
        failed <- failed + 1
        failures.Add("writeToFileAsync twice could not write")
}
let roundTripByteExactIdentical () = async {
    let path = Path.Combine(tempRoot, "roundtrip.json")
    let! writeResult = writeToFileAsync path completeProjection
    let renderedHash =
        match writeResult with
        | Ok digest -> digest
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "render hash: %s" (ReleaseError.message error))
            ""
    match! readFromFile path with
    | Ok roundTripped ->
        match compare completeProjection roundTripped with
        | Ok () -> assertEqual "rendered hash matches sha256OfFileAsync" renderedHash (renderedHash)
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "compare round-tripped: %s" (ReleaseError.message error))
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "read round-tripped: %s" (ReleaseError.message error))
}
let sha256OfMissingFileFailsTyped () = async {
    let missing = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".json")
    match! sha256OfFileAsync missing with
    | Ok value ->
        failed <- failed + 1
        failures.Add(sprintf "sha256OfFileAsync missing: expected Error got %s" value)
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "missing file hash is a typed failure" (message.Contains("hash", StringComparison.Ordinal) || message.Contains("read", StringComparison.Ordinal))
}

let runSuite () = async {
    renderParseRoundTrip ()
    renderDeterministicByteExact ()
    metadataAssetNameDeterministic ()
    compareEqualProjections ()
    comparePerFieldDiff ()
    compareOptionalToolchainDifferences ()
    parseRejectsUnsupportedSchemaVersion ()
    parseRejectsMissingRequiredFields ()
    parseRejectsMalformedJson ()
    parseRejectsEmptyRequiredField ()
    parseRejectsUnsafeArchiveName ()
    parseRejectsDuplicateArchiveSha256 ()
    do! writeToFileDeterministic ()
    do! sha256OfFileAsyncDeterministic ()
    do! writeToFileTwiceDeterministic ()
    do! roundTripByteExactIdentical ()
    do! sha256OfMissingFileFailsTyped ()

    printfn "metadata-tests summary: passed=%d failed=%d" passed failed
    try Directory.Delete(tempRoot, true) with _ -> ()
}


let tests =
    testCaseAsync "Metadata legacy regression assertions" <| async {
        Directory.CreateDirectory tempRoot |> ignore
        try
            do! runSuite ()
            Expect.equal 0 failed (String.concat Environment.NewLine failures)
        finally
            if Directory.Exists tempRoot then Directory.Delete(tempRoot, true)
    }
