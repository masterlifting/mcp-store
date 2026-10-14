module Release.Tests.Policy

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open BuildProvenance
open Release.Tests.FixtureHelpers

let private repositoryRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../../"))

let private configPath componentId =
    Path.Combine(repositoryRoot, componentId, "release", "Config.json")

let private sha256Hex (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

// WHY: the strict JSON grammar has exactly nine required fields; the canonical Config.json
//   supplies them with no interpolation, no extra policies, and no overlapping keys. Each
//   canonical projection test reads the bytes once via the Snapshots (FixtureHelpers)
//   helper so the bound SourceSha256 is computed from the same bytes the parser observes.
let private requiredFields =
    [ "componentId"; "version"; "entryDll"; "sdkVersion"; "runtimeVersion"
      "platform"; "archiveName"; "assetUri"; "publishedFiles" ]

let private expectMalformed (name: string) (sourcePath: string) (input: string) =
    testCase name (fun () ->
        let bytes = Encoding.UTF8.GetBytes input

        match ReleasePolicy.parseBytes sourcePath bytes with
        | Error(MalformedArtifact(path, _)) ->
            Expect.equal path sourcePath "error path labels the supplied source name"
        | Error error -> failtestf "expected a typed malformed-artifact error, got %A" error
        | Ok projection -> failtestf "expected malformed input to be rejected, got %A" projection)

let private expectMalformedBytes (name: string) (sourcePath: string) (bytes: byte[]) =
    testCase name (fun () ->
        match ReleasePolicy.parseBytes sourcePath bytes with
        | Error(MalformedArtifact(path, _)) ->
            Expect.equal path sourcePath "error path labels the supplied source name"
        | Error error -> failtestf "expected a typed malformed-artifact error, got %A" error
        | Ok projection -> failtestf "expected malformed bytes to be rejected, got %A" projection)

let private canonicalComponentIds = [ "dotnet"; "workflow" ]

let private projectionTests =
    canonicalComponentIds
    |> List.map (fun componentId ->
        testCase ($"canonical {componentId} Config.json projects a source-bound policy") (fun () ->
         let snapshot = readCanonicalConfig repositoryRoot componentId
         let actual = snapshot.Parsed
         let node = JsonNode.Parse(Encoding.UTF8.GetString snapshot.ConfigBytes).AsObject()

         for field in requiredFields do
             Expect.isTrue (node.ContainsKey field) $"canonical Config.json contains required field {field}"
             Expect.isTrue (node[field].GetValueKind() <> JsonValueKind.Null) $"canonical field {field} is not null"

         Expect.equal actual.ComponentId componentId $"{componentId} component identity is bound to its canonical path"
         Expect.equal actual.ComponentId (node["componentId"].GetValue<string>()) $"{componentId} component identity matches the canonical literal"
         Expect.equal actual.Version (node["version"].GetValue<string>()) $"{componentId} version matches the canonical literal"
         Expect.equal actual.EntryDll (node["entryDll"].GetValue<string>()) $"{componentId} entry assembly matches the canonical literal"
         Expect.equal actual.SdkVersion (node["sdkVersion"].GetValue<string>()) $"{componentId} SDK policy matches the canonical literal"
         Expect.equal actual.RuntimeVersion (node["runtimeVersion"].GetValue<string>()) $"{componentId} runtime policy matches the canonical literal"
         Expect.equal actual.Platform (node["platform"].GetValue<string>()) $"{componentId} platform matches the canonical literal"
         Expect.equal actual.ArchiveName (node["archiveName"].GetValue<string>()) $"{componentId} archive name matches the canonical literal"
         Expect.equal actual.AssetUri (node["assetUri"].GetValue<string>()) $"{componentId} asset URI matches the canonical literal"

         let published =
             node["publishedFiles"].AsArray()
             |> Seq.map (fun value -> value.GetValue<string>())
             |> Seq.toList
         Expect.equal actual.PublishedFiles published $"{componentId} published files match the canonical array"
         Expect.isTrue (List.contains actual.EntryDll actual.PublishedFiles) $"{componentId} published files include the entry assembly"
         Expect.isTrue (actual.PublishedFiles |> List.distinct |> List.length = actual.PublishedFiles.Length) $"{componentId} published file names are unique"
         Expect.equal actual.SourcePath snapshot.ConfigPath $"{componentId} source path returns the canonical file path"
         Expect.equal actual.SourceSha256 snapshot.ConfigSha256 $"{componentId} hash of exact canonical bytes returns the helper observation"
         let mutable parsedVersion: Version = Unchecked.defaultof<Version>
         Expect.isTrue (Version.TryParse(actual.Version, &parsedVersion)) $"canonical {componentId} version parses as semver"
         Expect.isTrue (actual.Version = "1.0.4" || actual.Version = "1.0.5") $"canonical {componentId} version is the current pinned release value"))

let private strictJsonGrammarTests =
    let canonical = readCanonicalConfig repositoryRoot "dotnet"
    let baseline = Encoding.UTF8.GetString canonical.ConfigBytes
    // WHY: each negative must actually change the canonical bytes; a no-op `String.Replace`
    //   returns the baseline unchanged and exercises no rejection path. We therefore parse
    //   the canonical Config.json into a `JsonObject`, mutate one well-defined property or
    //   array element, and re-serialize with compact options so the bytes differ in exactly
    //   the spot the test names. Trailing commas are an actual text mutation since the
    //   serializer would otherwise strip them.
    let mutated (action: JsonObject -> unit) =
        let node = JsonNode.Parse(baseline).AsObject()
        action node
        node.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    let bytes (text: string) = Encoding.UTF8.GetBytes text

    // JsonObject cannot carry duplicate keys, so the duplicate-field negative stays as a
    //   textual post-process: serialize the canonical Config.json to compact JSON, then
    //   splice a second `"version"` key immediately after the opening `{` so the parser
    //   observes two `version` properties without the canonical version literal being
    //   hard-coded into the fixture. Construction and assertions run inside the test
    //   action so the module cctor cannot crash on whitespace drift between canonical
    //   and compact representations.
    let buildDuplicate () =
        let serialized =
            JsonNode.Parse(baseline)
                .AsObject()
                .ToJsonString(JsonSerializerOptions(WriteIndented = false))
        let openBrace = serialized.IndexOf '{'
        if openBrace < 0 then failtest "canonical config serialization did not start with '{'"
        serialized.Insert(openBrace + 1, "\"version\":\"9.9\",")

    [ testCase "duplicate fields are rejected" (fun () ->
          let duplicate = buildDuplicate ()
          let versionCount =
              duplicate.Split([|"\"version\":"|], StringSplitOptions.None).Length - 1
          Expect.isTrue (duplicate.Contains "\"version\":\"9.9\"") "duplicate fixture carries the inserted version literal"
          Expect.equal versionCount 2 "duplicate fixture contains exactly two version keys"
          match ReleasePolicy.parseBytes "Config.json" (Encoding.UTF8.GetBytes duplicate) with
          | Error(MalformedArtifact(path, _)) ->
              Expect.equal path "Config.json" "duplicate-version path labels the supplied source name"
          | Error error -> failtestf "expected a typed malformed-artifact error, got %A" error
          | Ok projection -> failtestf "expected duplicate version to be rejected, got %A" projection)
      expectMalformedBytes "missing required field componentId is rejected" "Config.json" (
          mutated (fun node -> node.Remove "componentId" |> ignore) |> bytes)
      expectMalformedBytes "missing required field version is rejected" "Config.json" (
          mutated (fun node -> node.Remove "version" |> ignore) |> bytes)
      expectMalformedBytes "missing required field entryDll is rejected" "Config.json" (
          mutated (fun node -> node.Remove "entryDll" |> ignore) |> bytes)
      expectMalformedBytes "missing required field sdkVersion is rejected" "Config.json" (
          mutated (fun node -> node.Remove "sdkVersion" |> ignore) |> bytes)
      expectMalformedBytes "missing required field runtimeVersion is rejected" "Config.json" (
          mutated (fun node -> node.Remove "runtimeVersion" |> ignore) |> bytes)
      expectMalformedBytes "missing required field platform is rejected" "Config.json" (
          mutated (fun node -> node.Remove "platform" |> ignore) |> bytes)
      expectMalformedBytes "missing required field archiveName is rejected" "Config.json" (
          mutated (fun node -> node.Remove "archiveName" |> ignore) |> bytes)
      expectMalformedBytes "missing required field assetUri is rejected" "Config.json" (
          mutated (fun node -> node.Remove "assetUri" |> ignore) |> bytes)
      expectMalformedBytes "missing required field publishedFiles is rejected" "Config.json" (
          mutated (fun node -> node.Remove "publishedFiles" |> ignore) |> bytes)
      expectMalformedBytes "an unknown field is rejected" "Config.json" (
          mutated (fun node -> node["rogueField"] <- JsonValue.Create "rogue") |> bytes)
      expectMalformedBytes "a non-string componentId is rejected" "Config.json" (
          mutated (fun node -> node["componentId"] <- JsonValue.Create 7) |> bytes)
      expectMalformedBytes "a number publishedFiles entry is rejected" "Config.json" (
          mutated (fun node ->
              let array = node["publishedFiles"].AsArray()
              array[0] <- JsonValue.Create 7) |> bytes)
      expectMalformedBytes "a null required field is rejected" "Config.json" (
          mutated (fun node -> node["version"] <- null) |> bytes)
      expectMalformedBytes "malformed UTF-8 bytes return a typed malformed-artifact error" "invalid-utf8" ([| 0xC3uy; 0x28uy; 0x00uy |])
      // WHY: the canonical Config.json is re-emitted as compact JSON then post-processed to
      //   actually append a trailing comma before the closing array brace. A bare
      //   serialization round-trip would silently normalize the trailing comma away.
      expectMalformedBytes "trailing commas are rejected as strict JSON" "Config.json" (
          let compact = JsonNode.Parse(baseline).AsObject().ToJsonString(JsonSerializerOptions(WriteIndented = false))
          let trailing = compact.Replace("\"Mcp.Dotnet.runtimeconfig.json\"]", "\"Mcp.Dotnet.runtimeconfig.json\",]", StringComparison.Ordinal)
          bytes trailing)
      expectMalformedBytes "embedded NUL returns a typed malformed-artifact error" "Config.json" (Encoding.UTF8.GetBytes("{\"componentId\":\u0000\"dotnet\"}"))
      testCase "oversized source returns a typed malformed-artifact error" (fun () ->
          let oversized =
              "{\"componentId\":\"dotnet\"" + String(',', 16 * 1024 * 1024)
          match ReleasePolicy.parseBytes "oversized" (Encoding.UTF8.GetBytes oversized) with
          | Error(MalformedArtifact("oversized", _)) -> ()
          | Error error -> failtestf "expected typed malformed-artifact error, got %A" error
          | Ok projection -> failtestf "oversized source was accepted: %A" projection)
      testCase "a malformed JSON document returns a typed malformed-artifact error" (fun () ->
          match ReleasePolicy.parseBytes "not-json" (Encoding.UTF8.GetBytes "{ this is not valid JSON") with
          | Error(MalformedArtifact("not-json", _)) -> ()
          | Error error -> failtestf "expected typed malformed-artifact error, got %A" error
          | Ok projection -> failtestf "malformed JSON was accepted: %A" projection) ]

let private semanticGrammarTests =
    [ testCase "canonical two-component projections both satisfy the strict schema" (fun () ->
          let projections = canonicalComponentIds |> List.map (fun id -> (id, readCanonicalConfig repositoryRoot id))
          Expect.isTrue (projections.Length = 2) "exactly two canonical projections were observed"
          for componentId, snapshot in projections do
              Expect.isTrue (snapshot.Parsed.Version.Length > 0) $"{componentId} version is non-empty"
              Expect.isTrue (snapshot.Parsed.SdkVersion.Contains "11.0") $"{componentId} SDK version names the canonical SDK family"
              Expect.isTrue (snapshot.Parsed.RuntimeVersion.Contains "11.0") $"{componentId} runtime version names the canonical runtime family"
              Expect.isTrue (snapshot.Parsed.Platform = "win-x64") $"{componentId} platform is the canonical win-x64"
              Expect.isTrue (snapshot.Parsed.AssetUri.Contains componentId) $"{componentId} asset URI names the component id"
              Expect.isTrue (snapshot.Parsed.ArchiveName.StartsWith "v") $"{componentId} archive name is versioned")
      testCase "every published file is a contained bare name with no directory traversal" (fun () ->
          for componentId in canonicalComponentIds do
              let snapshot = readCanonicalConfig repositoryRoot componentId
              for name in snapshot.Parsed.PublishedFiles do
                  Expect.isFalse (name.Contains "/") $"{componentId} published file {name} contains no directory separator"
                  Expect.isFalse (name.Contains "\\") $"{componentId} published file {name} contains no backslash"
                  Expect.isTrue (name = Path.GetFileName name) $"{componentId} published file {name} is a bare name"
                  Expect.isFalse (name.Contains "..") $"{componentId} published file {name} contains no relative-traversal segment")
      testCase "the canonical Config.json contains exactly the nine required fields and no others" (fun () ->
          for componentId in canonicalComponentIds do
              let snapshot = readCanonicalConfig repositoryRoot componentId
              let node = JsonNode.Parse(Encoding.UTF8.GetString snapshot.ConfigBytes).AsObject()
              let keys = node |> Seq.map (fun property -> property.Key) |> Seq.toList |> List.sort
              Expect.equal keys (requiredFields |> List.sort) $"{componentId} Config.json has exactly the nine required fields") ]

let private snapshotTests =
    [ testCase "projection and source hash remain bound to the bytes observed before file mutation" (fun () ->
          let sourcePath = Path.Combine(Path.GetTempPath(), $"release-policy-{Guid.NewGuid():N}.json")
          let originalBytes = readCanonicalConfig repositoryRoot "dotnet"
          File.WriteAllBytes(sourcePath, originalBytes.ConfigBytes)
          let observedBytes = File.ReadAllBytes sourcePath
          let snapshot = Array.copy observedBytes
          let expectedHash = sha256Hex snapshot

          let parsed =
              match ReleasePolicy.parseBytes sourcePath snapshot with
              | Ok projection -> projection
              | Error error -> failtestf "canonical config was rejected: %A" error

          Array.fill snapshot 0 snapshot.Length 0uy
          let changedFile = Encoding.UTF8.GetBytes "{\"componentId\":\"mutated\"}"
          File.WriteAllBytes(sourcePath, changedFile)

          try
              let reparsed =
                  match ReleasePolicy.parseBytes sourcePath observedBytes with
                  | Ok projection -> projection
                  | Error error -> failtestf "original byte snapshot was rejected after file mutation: %A" error

              Expect.equal parsed.ComponentId "dotnet" "parsed projection captures original contents"
              Expect.equal parsed.SourceSha256 expectedHash "hash corresponds to original byte snapshot"
              Expect.equal reparsed.SourceSha256 expectedHash "file mutation does not alter hash of retained bytes"
              Expect.equal reparsed.ComponentId "dotnet" "file mutation does not alter parsed projection from retained bytes"
          finally
              File.Delete sourcePath) ]

let tests =
    testList "release policy parseBytes" [
        testList "canonical projections" projectionTests
        testList "strict JSON grammar and fail-closed inputs" strictJsonGrammarTests
        testList "semantic schema relations" semanticGrammarTests
        testList "immutable byte snapshot" snapshotTests
    ]
