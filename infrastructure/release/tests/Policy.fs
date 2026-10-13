module Release.Tests.Policy

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Expecto
open BuildProvenance

let private repositoryRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../../"))

let private configPath componentId =
    Path.Combine(repositoryRoot, componentId, "release", "Config.fsx")

let private sha256Hex (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private captureExactlyOne label pattern source =
    let matches = Regex.Matches(source, pattern)

    if matches.Count <> 1 then
        failtestf "expected one authored %s expression, found %d" label matches.Count

    matches.[0].Groups.[1].Value

let private authoredPolicy path source =
    let literal name =
        captureExactlyOne name ($"(?m)^\\s*let\\s+{Regex.Escape name}\\s*=\\s*\"([^\"]*)\"\\s*$") source

    let componentId = literal "componentId"
    let version = literal "version"
    let entryDll = literal "entryDll"
    let sdkVersion = literal "sdkVersion"
    let runtimeVersion = literal "runtimeVersion"
    let platform = literal "platform"
    let archiveName = $"v{version}.zip"
    let assetUriTemplate =
        captureExactlyOne
            "assetUri interpolation"
            "(?ms)^\\s*let\\s+assetUri\\s*=\\s*\\$\"([^\"]*)\"\\s*$"
            source
    let assetUri =
        assetUriTemplate
            .Replace("{componentId}", componentId, StringComparison.Ordinal)
            .Replace("{version}", version, StringComparison.Ordinal)
    let filesExpression =
        captureExactlyOne "publishedFiles list" "(?s)let\\s+publishedFiles\\s*=\\s*\\[(.*?)\\]" source
    let publishedFiles =
        Regex.Matches(filesExpression, "\"([^\"]*)\"")
        |> Seq.cast<Match>
        |> Seq.map (fun item -> item.Groups.[1].Value)
        |> Seq.toList

    componentId, version, entryDll, sdkVersion, runtimeVersion, platform, archiveName, assetUri, publishedFiles, path

let private expectMalformed (name: string) (source: string) =
    testCase name (fun () ->
        let bytes = Encoding.UTF8.GetBytes source

        match ReleasePolicy.parseBytes "Config.fsx" bytes with
        | Error(MalformedArtifact("Config.fsx", _)) -> ()
        | Error error -> failtestf "expected a typed malformed-artifact error, got %A" error
        | Ok projection -> failtestf "expected malformed input to be rejected, got %A" projection)

let private canonicalComponentIds = [ "dotnet"; "workflow" ]

let private projectionTests =
    canonicalComponentIds
    |> List.map (fun componentId ->
        testCase ($"canonical {componentId} config projects a source-bound policy") (fun () ->
         let path = configPath componentId
         let bytes = File.ReadAllBytes path
         let source = Encoding.UTF8.GetString bytes
         let expected = authoredPolicy path source

         match ReleasePolicy.parseBytes path bytes with
         | Error error -> failtestf "canonical config was rejected: %A" error
         | Ok actual ->
             let expectedComponentId, expectedVersion, expectedEntryDll, expectedSdk, expectedRuntime, expectedPlatform, expectedArchive, expectedAssetUri, expectedFiles, expectedPath = expected
             Expect.equal actual.ComponentId expectedComponentId "component identity matches the authored config literal"
             Expect.equal actual.ComponentId componentId "component identity is bound to the roster path"
             Expect.equal actual.Version expectedVersion "version matches the authored config literal"
             Expect.equal actual.EntryDll expectedEntryDll "entry assembly matches the authored config literal"
             Expect.equal actual.SdkVersion expectedSdk "SDK policy matches the authored config literal"
             Expect.equal actual.RuntimeVersion expectedRuntime "runtime policy matches the authored config literal"
             Expect.equal actual.Platform expectedPlatform "platform matches the authored config literal"
             Expect.equal actual.ArchiveName expectedArchive "archive name follows the canonical authored version expression"
             Expect.equal actual.AssetUri expectedAssetUri "asset URI interpolates the independently read authored expression"
             Expect.equal actual.PublishedFiles expectedFiles "published file list matches authored literals"
             Expect.isTrue (List.contains actual.EntryDll actual.PublishedFiles) "published files include the entry assembly"
             Expect.isTrue (actual.PublishedFiles |> List.distinct |> List.length = actual.PublishedFiles.Length) "published file names are unique"
             Expect.equal actual.SourcePath expectedPath "source path"
             Expect.equal actual.SourceSha256 (sha256Hex bytes) "hash of exact canonical bytes"))

let private grammarTests =
    [ testCase "literal, prior-binding interpolation, whitespace, and list separators are accepted" (fun () ->
          let source =
              "module ReleaseConfig\n"
              + "let componentId = \"demo\"\n"
              + "let version = \"2.3\"\n"
              + "let entryDll = \"Demo.dll\"\n"
              + "let sdkVersion = \"sdk\"\n"
              + "let runtimeVersion = \"runtime\"\n"
              + "let platform = \"win-x64\"\n"
              + "let archiveName = $\"v{version}.zip\"\n"
              + "let assetUri = $\"https://example.invalid/{componentId}/{version}\"\n"
              + "let publishedFiles = [ \"one.dll\"\n \"two.dll\" ]\n"

          match ReleasePolicy.parseBytes "fixture" (Encoding.UTF8.GetBytes source) with
          | Error error -> failtestf "supported grammar was rejected: %A" error
          | Ok actual ->
              Expect.equal actual.ArchiveName "v2.3.zip" "archive interpolation"
              Expect.equal actual.AssetUri "https://example.invalid/demo/2.3" "URI interpolation"
              Expect.equal actual.PublishedFiles [ "one.dll"; "two.dll" ] "whitespace-separated list");
      expectMalformed "duplicate bindings are rejected" (File.ReadAllText(configPath "dotnet") + "\nlet version = \"9.9\"\n");
      expectMalformed "unknown bindings are rejected" (File.ReadAllText(configPath "dotnet") + "\nlet extraPolicy = \"unexpected\"\n");
      expectMalformed "executable top-level statements are rejected" (File.ReadAllText(configPath "dotnet") + "\ndo System.IO.File.WriteAllText(\"marker\", \"ran\")\n");
      expectMalformed "directives are rejected" (File.ReadAllText(configPath "dotnet") + "\n#load \"other.fsx\"\n");
      expectMalformed "function bindings are rejected" (File.ReadAllText(configPath "dotnet") + "\nlet helper () = \"value\"\n");
      expectMalformed "mutable bindings are rejected" (File.ReadAllText(configPath "dotnet") + "\nlet mutable mode = \"value\"\n");
      expectMalformed "unresolved interpolation references are rejected" (File.ReadAllText(configPath "dotnet").Replace("$\"v{version}.zip\"", "$\"v{missing}.zip\""));
      testCase "an executable fixture config is rejected without running it" (fun () ->
          let marker = Path.Combine(Path.GetTempPath(), $"release-policy-execution-{Guid.NewGuid():N}.txt")
          let source = File.ReadAllText(configPath "dotnet")
          let executable = source + $"{Environment.NewLine}do System.IO.File.WriteAllText(@\"{marker}\", \"executed\")"

          try
              match ReleasePolicy.parseBytes "executable-fixture" (Encoding.UTF8.GetBytes executable) with
              | Error(MalformedArtifact("executable-fixture", _)) -> ()
              | Error error -> failtestf "expected a malformed-artifact error, got %A" error
              | Ok projection -> failtestf "executable config was accepted: %A" projection
              Expect.isFalse (File.Exists marker) "parsing rejects executable syntax without evaluation"
          finally
              if File.Exists marker then File.Delete marker);
      testCase "malformed UTF-8 returns a typed malformed-artifact error" (fun () ->
          let bytes = File.ReadAllBytes(configPath "dotnet")
          let marker = Encoding.UTF8.GetBytes "dotnet"
          let markerIndex = Array.findIndex (fun index -> index + marker.Length <= bytes.Length && bytes.[index .. index + marker.Length - 1] = marker) [| 0 .. bytes.Length - marker.Length |]
          bytes.[markerIndex] <- 0xC3uy
          bytes.[markerIndex + 1] <- 0x28uy

          match ReleasePolicy.parseBytes "invalid-utf8" bytes with
          | Error(MalformedArtifact("invalid-utf8", _)) -> ()
          | Error error -> failtestf "expected typed malformed-artifact error, got %A" error
          | Ok projection -> failtestf "malformed UTF-8 was accepted: %A" projection);
      expectMalformed "embedded NUL returns a typed malformed-artifact error" (File.ReadAllText(configPath "dotnet").Replace("dotnet\"", "dotnet\u0000\""));
      testCase "oversized source returns a typed malformed-artifact error" (fun () ->
          let prefix = File.ReadAllText(configPath "dotnet")
          let source = prefix + "\n//" + String('x', 16 * 1024 * 1024)

          match ReleasePolicy.parseBytes "oversized" (Encoding.UTF8.GetBytes source) with
          | Error(MalformedArtifact("oversized", _)) -> ()
          | Error error -> failtestf "expected typed malformed-artifact error, got %A" error
          | Ok projection -> failtestf "oversized source was accepted: %A" projection) ]

let private snapshotTests =
    [ testCase "projection and source hash remain bound to the bytes observed before file mutation" (fun () ->
          let path = Path.Combine(Path.GetTempPath(), $"release-policy-{Guid.NewGuid():N}.fsx")
          let originalBytes = File.ReadAllBytes(configPath "dotnet")
          File.WriteAllBytes(path, originalBytes)
          let observedBytes = File.ReadAllBytes path
          let snapshot = Array.copy observedBytes
          let expectedHash = sha256Hex snapshot

          let parsed =
              match ReleasePolicy.parseBytes path snapshot with
              | Ok projection -> projection
              | Error error -> failtestf "canonical config was rejected: %A" error

          Array.fill snapshot 0 snapshot.Length 0uy
          let changedFile = Encoding.UTF8.GetBytes "module ReleaseConfig\nlet componentId = \"mutated\"\n"
          File.WriteAllBytes(path, changedFile)

          try
              let reparsed =
                  match ReleasePolicy.parseBytes path observedBytes with
                  | Ok projection -> projection
                  | Error error -> failtestf "original byte snapshot was rejected after file mutation: %A" error

              Expect.equal parsed.ComponentId "dotnet" "parsed projection captures original contents"
              Expect.equal parsed.SourceSha256 expectedHash "hash corresponds to original byte snapshot"
              Expect.equal reparsed.SourceSha256 expectedHash "file mutation does not alter hash of retained bytes"
              Expect.equal reparsed.ComponentId "dotnet" "file mutation does not alter parsed projection from retained bytes"
          finally
              File.Delete path) ]

let tests =
    testList "release policy parseBytes" [
        testList "canonical projections" projectionTests
        testList "supported grammar and fail-closed inputs" grammarTests
        testList "immutable byte snapshot" snapshotTests
    ]
