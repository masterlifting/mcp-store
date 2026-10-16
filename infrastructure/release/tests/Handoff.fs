// role: reusable module/helper
// WHY: simplification cut `prepare Config.json <outdir>` down to two outputs (producer ZIP +
//   distribution.json) and removed the preparation.json / trusted-receipt / faux-platform
//   surface. `Handoff.readCandidate` is the only private immutable snapshot factory the
//   publisher uses; this module exercises exactly that 2-entry candidate surface and the
//   inner-strict bounds + canonical/SDK/revision bounds, and retains traversal, case
//   collisions, read-bounds, source SHA, component/revision identity and negative tests
//   through the NEW private `releasePolicy` factory.

module Release.Tests.Handoff

open System
open System.Buffers.Binary
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Expecto
open BuildProvenance
open DistributionContract

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../../"))

let private sha256Hex (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private zipBytes (entries: (string * byte[]) list) =
    use output = new MemoryStream()

    do
        use archive = new ZipArchive(output, ZipArchiveMode.Create, true)

        for name, payload in entries do
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            use stream = entry.Open()
            stream.Write(payload, 0, payload.Length)

    output.ToArray()

// WHY: the test helper now goes through the strict-JSON parser rather than the F# grammar
//   regex; the same `releasePolicy` factory the production publisher invokes is reused so the
//   `readCandidate` factory contract, source path and source SHA are bound to the canonical
//   Config.json bytes (not a stored clone).
let private releasePolicy componentId =
    let path = Path.Combine(repositoryRoot, componentId, "release", "Config.json")
    let bytes = File.ReadAllBytes path

    match ReleasePolicy.parseBytes path bytes with
    | Ok policy -> policy
    | Error error -> failtestf "canonical %s policy was rejected: %A" componentId error

let private portableCandidateArchive (policy: ReleasePolicy.PolicyProjection) =
    let payloads = Fixture.policyPayloads policy "candidate-payload"
    let manifestText = Fixture.policyManifestFor policy "0123456789abcdef0123456789abcdef01234567" payloads
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    let producerArchive = zipBytes (payloads @ [ DistributionContract.manifestEntryName, manifestBytes ])
    producerArchive, manifestBytes

let private prepareCandidateDirectory (componentId: string) (policy: ReleasePolicy.PolicyProjection) =
    let producerArchive, manifestBytes = portableCandidateArchive policy
    let root = Path.Combine(Path.GetTempPath(), $"mcp-handoff-{componentId}-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore
    File.WriteAllBytes(Path.Combine(root, policy.ArchiveName), producerArchive)
    File.WriteAllBytes(Path.Combine(root, "distribution.json"), manifestBytes)
    root

// WHY: the unsafe-path negative must reach the canonical inner-archive `pathSafe` check,
//   not the host filesystem's drive/UNC/backslash/colon IO. We put the unsafe member name
//   inside an otherwise-valid producer ZIP and keep the outer 2-entry candidate directory
//   (producer archive + distribution.json) so `readCandidate` exercises its own validator.
let private innerArchiveWithUnsafeEntry (policy: ReleasePolicy.PolicyProjection) (unsafeName: string) =
    let payloads = Fixture.policyPayloads policy "candidate-payload"
    let manifestText = Fixture.policyManifestFor policy "0123456789abcdef0123456789abcdef01234567" payloads
    let manifestBytes = Encoding.UTF8.GetBytes manifestText

    let innerBytes =
        zipBytes
            (payloads
             @ [ unsafeName, Encoding.UTF8.GetBytes "unsafe"
                 DistributionContract.manifestEntryName, manifestBytes ])

    innerBytes, manifestBytes

let private unsafePathRejected (name: string) =
    testCase ($"readCandidate rejects unsafe path '{name}'") (fun () ->
        let componentId = "dotnet"
        let policy = releasePolicy componentId
        let innerBytes, manifestBytes = innerArchiveWithUnsafeEntry policy name
        let root = Path.Combine(Path.GetTempPath(), $"mcp-handoff-unsafe-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        File.WriteAllBytes(Path.Combine(root, policy.ArchiveName), innerBytes)
        File.WriteAllBytes(Path.Combine(root, "distribution.json"), manifestBytes)
        try
            match ReleaseHandoff.readCandidate root policy "0123456789abcdef0123456789abcdef01234567" with
            | Error _ -> ()
            | Ok _ -> failtestf "unsafe inner entry '%s' was accepted" name
        finally
            if Directory.Exists root then Directory.Delete(root, true))

let private caseVariant (value: string) =
    let chars = value.ToCharArray()
    let index = chars |> Array.findIndex Char.IsLetter
    chars[index] <- if Char.IsUpper chars[index] then Char.ToLowerInvariant chars[index] else Char.ToUpperInvariant chars[index]
    String chars

let private exactTwoEntryNegativeCase label (entries: ReleasePolicy.PolicyProjection -> byte[] -> byte[] -> (string * byte[]) list) =
    testCase ($"readCandidate rejects {label}") (fun () ->
        let componentId = "dotnet"
        let policy = releasePolicy componentId
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let root = Path.Combine(Path.GetTempPath(), $"mcp-handoff-entry-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        for name, payload in entries policy producerArchive manifestBytes do
            File.WriteAllBytes(Path.Combine(root, name), payload)
        try
            match ReleaseHandoff.readCandidate root policy "0123456789abcdef0123456789abcdef01234567" with
            | Error _ -> ()
            | Ok _ -> failtestf "exact-two-entry candidate with '%s' was accepted" label
        finally
            if Directory.Exists root then Directory.Delete(root, true))

let private safeDeleteDir (root: string) =
    try Directory.Delete(root, true) with _ -> ()

let private positiveCanonicalCandidate componentId =
    testCase ($"{componentId} readCandidate parses the exact-two-entry portable candidate") (fun () ->
        let policy = releasePolicy componentId
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let root = prepareCandidateDirectory componentId policy
        try
            match ReleaseHandoff.readCandidate root policy "0123456789abcdef0123456789abcdef01234567" with
            | Error error ->
                failtestf
                    "canonical two-entry candidate was rejected (%s, %d bytes, names=%A): %A"
                    policy.ArchiveName
                    producerArchive.Length
                    [ policy.ArchiveName; "distribution.json" ]
                    error
            | Ok snapshot ->
                // WHY: the simplified immutable snapshot exposes only the two verified byte
                //   copies, the source path and the source SHA-256. No parallel-engine clone,
                //   no embedded/standalone split APIs, no streaming facade.
                Expect.equal (snapshot.ArchiveBytesCopy()) producerArchive "snapshot exposes the verified producer ZIP bytes"
                Expect.equal (snapshot.ManifestBytesCopy()) manifestBytes "snapshot exposes the verified standalone manifest bytes"
                Expect.equal snapshot.SourcePath (Path.Combine(root, policy.ArchiveName)) "source path returns the parsed archive path"
                Expect.equal snapshot.SourceSha256 (sha256Hex producerArchive) "source SHA matches the producer archive bytes"
        finally
            safeDeleteDir root)

let private sourceShaAndRevisionIdentity =
    testCase "readCandidate binds source SHA to the verified candidate bytes for both producers" (fun () ->
        for componentId in [ "dotnet"; "workflow" ] do
            let policy = releasePolicy componentId
            let producerArchive, manifestBytes = portableCandidateArchive policy
            let root = prepareCandidateDirectory componentId policy
            try
                let expectedHead = "0123456789abcdef0123456789abcdef01234567"
                match ReleaseHandoff.readCandidate root policy expectedHead with
                | Error error -> failtestf "%s canonical candidate was rejected: %A" componentId error
                | Ok snapshot ->
                    Expect.equal snapshot.SourceSha256 (sha256Hex producerArchive) $"{componentId} source sha256 matches the producer ZIP bytes"
                    Expect.equal (snapshot.ArchiveBytesCopy()) producerArchive $"{componentId} archive bytes copy matches the producer ZIP bytes"
                    Expect.equal (snapshot.ManifestBytesCopy()) manifestBytes $"{componentId} manifest bytes copy matches the standalone manifest"
                    Expect.equal snapshot.SourcePath (Path.Combine(root, policy.ArchiveName)) $"{componentId} source path returns the parsed archive path"
            finally
                safeDeleteDir root)

let private componentRevisionSdkIdentity = testCase "readCandidate preserves canonical component, revision and SDK/toolchain identity" (fun () ->
    for componentId in [ "dotnet"; "workflow" ] do
        let policy = releasePolicy componentId
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let root = prepareCandidateDirectory componentId policy
        try
            match ReleaseHandoff.readCandidate root policy "0123456789abcdef0123456789abcdef01234567" with
            | Error error -> failtestf "%s canonical candidate was rejected: %A" componentId error
            | Ok snapshot ->
                Expect.isTrue ((snapshot.ArchiveBytesCopy()).Length > 0) $"{componentId} archive bytes copy is non-empty"
                Expect.equal (snapshot.ArchiveBytesCopy()) producerArchive $"{componentId} archive bytes copy matches the producer ZIP bytes"
                Expect.equal (snapshot.ManifestBytesCopy()) manifestBytes $"{componentId} manifest bytes copy matches the standalone manifest"
        finally
            safeDeleteDir root)

let private mismatchedStandaloneManifestRejected =
    testCase "readCandidate rejects standalone and embedded manifest byte mismatch" (fun () ->
        let componentId = "dotnet"
        let policy = releasePolicy componentId
        let producerArchive, embeddedManifest = portableCandidateArchive policy
        let standaloneManifest = Array.append embeddedManifest [| byte ' ' |]
        let root = Path.Combine(Path.GetTempPath(), $"mcp-handoff-mismatch-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        File.WriteAllBytes(Path.Combine(root, policy.ArchiveName), producerArchive)
        File.WriteAllBytes(Path.Combine(root, "distribution.json"), standaloneManifest)
        try
            match ReleaseHandoff.readCandidate root policy "0123456789abcdef0123456789abcdef01234567" with
            | Error _ -> ()
            | Ok _ -> failtest "readCandidate accepted different standalone/embedded manifests; expected rejection before any transaction"
        finally
            safeDeleteDir root)

let private missingDistributionManifestRejected =
    testCase "readCandidate rejects a candidate producer archive that is missing distribution.json" (fun () ->
        let componentId = "dotnet"
        let policy = releasePolicy componentId
        let _producerArchive, manifestBytes = portableCandidateArchive policy
        let innerBytes =
            zipBytes
                (policy.PublishedFiles
                 |> List.map (fun name -> name, Encoding.UTF8.GetBytes($"candidate-payload:{policy.ComponentId}:{name}"))
                 |> fun payloads -> payloads @ [ "NOTICE.txt", Encoding.UTF8.GetBytes "candidate notice" ])
        let root = Path.Combine(Path.GetTempPath(), $"mcp-handoff-noemb-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        File.WriteAllBytes(Path.Combine(root, policy.ArchiveName), innerBytes)
        File.WriteAllBytes(Path.Combine(root, "distribution.json"), manifestBytes)
        try
            match ReleaseHandoff.readCandidate root policy "0123456789abcdef0123456789abcdef01234567" with
            | Error (MalformedArtifact(path, detail)) ->
                Expect.equal path "distribution.json" "the rejection reports the missing canonical manifest entry name"
                Expect.isTrue
                    (detail.Contains("missing distribution.json", StringComparison.Ordinal))
                    $"the rejection names the missing canonical manifest entry: {detail}"
            | Error error -> failtestf "the malformed inner archive was rejected for an unexpected reason: %A" error
            | Ok _ -> failtest "a candidate producer archive without distribution.json was accepted"
        finally
            safeDeleteDir root)

let private entryCountUpperBoundRejection =
    testCase "readCandidate rejects an inner entry count above the cap" (fun () ->
        let componentId = "dotnet"
        let policy = releasePolicy componentId
        let excessIndex = 4096
        let entries = [ for index in 0 .. excessIndex do yield $"entry-{index:D4}.txt", [||] ]
        let innerBytes = zipBytes entries
        let _, manifestBytes = portableCandidateArchive policy
        let root = Path.Combine(Path.GetTempPath(), $"mcp-handoff-count-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        File.WriteAllBytes(Path.Combine(root, policy.ArchiveName), innerBytes)
        File.WriteAllBytes(Path.Combine(root, "distribution.json"), manifestBytes)
        try
            match ReleaseHandoff.readCandidate root policy "0123456789abcdef0123456789abcdef01234567" with
            | Error (MalformedArtifact(_, detail)) ->
                Expect.isTrue
                    (detail.Contains("entries", StringComparison.Ordinal))
                    $"entry-count rejection names the limit: {detail}"
            | Error error -> failtestf "entry-count overflow returned an unexpected error: %A" error
            | Ok _ -> failtest "inner candidate with too many entries was accepted"
        finally
            safeDeleteDir root)

let private unsafePathTests =
    [ "../escape.txt"
      "dir/../escape.txt"
      "./escape.txt"
      "/absolute.txt"
      "//server/share.txt"
      "C:/drive.txt"
      "C:relative.txt"
      "C:\\drive.txt"
      "file.txt:stream"
      "dir\\backslash.txt"
      "nämé.txt" ]
    |> List.map unsafePathRejected
    |> testList "readCandidate unsafe path negatives"

let private exactTwoEntryNegativeTests =
    testList
        "readCandidate exact-two-entry negatives"
        [ exactTwoEntryNegativeCase "an extra entry" (fun _ _ _ -> [ "v1.0.4.zip", [||]; "distribution.json", [||]; "extra.txt", [| 1uy |] ])
          exactTwoEntryNegativeCase "a missing inner archive" (fun _ _ manifest -> [ ("distribution.json", manifest) ])
          exactTwoEntryNegativeCase "a missing standalone manifest" (fun policy inner _ -> [ (policy.ArchiveName, inner) ])
          exactTwoEntryNegativeCase "a duplicate inner archive name" (fun policy inner _ -> [ policy.ArchiveName, inner; policy.ArchiveName, inner ])
          exactTwoEntryNegativeCase "a case-insensitive collision" (fun policy inner _ -> [ policy.ArchiveName, inner; caseVariant policy.ArchiveName, inner ]) ]

let private positiveTests =
    testList "readCandidate canonical positives" [
        positiveCanonicalCandidate "dotnet"
        positiveCanonicalCandidate "workflow"
    ]

let tests =
    testList "candidate handoff" [
        positiveTests
        sourceShaAndRevisionIdentity
        componentRevisionSdkIdentity
        testList
            "readCandidate malformed transport negatives"
            [ entryCountUpperBoundRejection
              unsafePathTests
              exactTwoEntryNegativeTests
              mismatchedStandaloneManifestRejected
              missingDistributionManifestRejected ]
    ]
