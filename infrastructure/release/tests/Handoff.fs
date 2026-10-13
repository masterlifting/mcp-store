module Release.Tests.Handoff

open System
open System.Buffers.Binary
open System.IO
open System.IO.Compression
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Expecto
open BuildProvenance

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

let private zipBytesWithExternalAttributes (entries: (string * byte[] * int) list) =
    use output = new MemoryStream()

    do
        use archive = new ZipArchive(output, ZipArchiveMode.Create, true)

        for name, payload, externalAttributes in entries do
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.ExternalAttributes <- externalAttributes
            use stream = entry.Open()
            stream.Write(payload, 0, payload.Length)

    output.ToArray()

let private canonicalPolicy componentId =
    let path = Path.Combine(repositoryRoot, componentId, "release", "Config.fsx")
    let bytes = File.ReadAllBytes path

    match ReleasePolicy.parseBytes path bytes with
    | Ok policy -> policy
    | Error error -> failtestf "canonical %s policy was rejected: %A" componentId error

let private portableCandidateArchive (policy: ReleasePolicy.PolicyProjection) =
    let payloads =
        policy.PublishedFiles
        |> List.map (fun name -> name, Encoding.UTF8.GetBytes($"candidate-payload:{policy.ComponentId}:{name}"))

    let manifest = JsonObject()
    manifest["schemaVersion"] <- JsonValue.Create 1
    manifest["id"] <- JsonValue.Create policy.ComponentId
    manifest["version"] <- JsonValue.Create policy.Version
    manifest["revision"] <- JsonValue.Create "0123456789abcdef0123456789abcdef01234567"
    manifest["sdk"] <- JsonValue.Create policy.SdkVersion
    manifest["entryDll"] <- JsonValue.Create policy.EntryDll
    manifest["archive"] <- JsonValue.Create policy.ArchiveName

    let files = JsonArray()

    for name, payload in payloads do
        let file = JsonObject()
        file["path"] <- JsonValue.Create name
        file["sha256"] <- JsonValue.Create(sha256Hex payload)
        files.Add file

    manifest["files"] <- files

    let archiveFiles = JsonArray()

    for name in policy.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] do
        archiveFiles.Add(JsonValue.Create name)

    manifest["archiveFiles"] <- archiveFiles
    let manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString())
    let producerArchive =
        zipBytes
            (payloads
             @ [ "NOTICE.txt", Encoding.UTF8.GetBytes "candidate notice"
                 "distribution.json", manifestBytes ])

    producerArchive, manifestBytes

let private preparationReceipt producerArchive manifestBytes =
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create 1
    node["innerArchiveSha256"] <- JsonValue.Create(sha256Hex producerArchive)
    node["innerArchiveSize"] <- JsonValue.Create(int64 producerArchive.Length)
    node["manifestSha256"] <- JsonValue.Create(sha256Hex manifestBytes)
    node["manifestSize"] <- JsonValue.Create(int64 manifestBytes.Length)
    Encoding.UTF8.GetBytes(node.ToJsonString())

let private outerDigest (value: string) =
    let unionType = typeof<ReleaseHandoff.OuterDigest>
    let bindingFlags = BindingFlags.Public ||| BindingFlags.NonPublic
    let unionCase = Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(unionType, bindingFlags) |> Array.exactlyOne

    Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(unionCase, [| box value |], bindingFlags)
    :?> ReleaseHandoff.OuterDigest

let private expectedMetadata componentId : ReleaseHandoff.ExpectedMetadata =
    { ArtifactId = "artifact-123"
      ArtifactName = "candidate-" + componentId
      Role = "candidate"
      Component = componentId
      Repository = "masterlifting/mcp-store"
      RunId = "123456"
      Attempt = "1"
      Sha = "0123456789abcdef0123456789abcdef01234567" }

let private observedMetadata componentId : ReleaseHandoff.ObservedPlatformMetadata =
    { ArtifactId = "artifact-123"
      ArtifactName = "candidate-" + componentId
      Role = "candidate"
      Component = componentId
      Repository = "masterlifting/mcp-store"
      RunId = "123456"
      Attempt = "1"
      Sha = "0123456789abcdef0123456789abcdef01234567" }

let private artifactEvidence
    (outerBytes: byte[])
    (expected: ReleaseHandoff.ExpectedMetadata)
    (observed: ReleaseHandoff.ObservedPlatformMetadata)
    (digest: string)
    (length: int64)
    : ReleaseHandoff.ArtifactEvidence =
    { Expected = expected
      Observed = observed
      ActualOuterDigest = outerDigest digest
      TransportSourcePath = "candidate-artifact.zip"
      TransportLength = length
      RawTransportBytes = outerBytes }

let private readOuter policy outerBytes evidence =
    ReleaseHandoff.readRawTransport "candidate-artifact.zip" outerBytes evidence policy

let private expectRejected label result =
    match result with
    | Error _ -> ()
    | Ok _ -> failtestf "%s was accepted" label

let private expectMalformedContaining (label: string) (expectedDetail: string) (result: Result<'a, ReleaseError>) =
    match result with
    | Error (MalformedArtifact(_, detail)) ->
        Expect.isTrue (detail.Contains(expectedDetail, StringComparison.Ordinal)) $"{label} rejection includes '{expectedDetail}', observed '{detail}'"
    | Error error -> failtestf "%s was rejected with an unexpected error: %A" label error
    | Ok _ -> failtestf "%s was accepted" label

let private parsePreparationNode (node: JsonObject) =
    ReleaseHandoff.parsePreparationReceipt (node.ToJsonString()) "receipt.json"

let private preparationNode producerArchive manifestBytes =
    JsonNode.Parse(Encoding.UTF8.GetString(preparationReceipt producerArchive manifestBytes)).AsObject()

let private validTrustedReceiptNode () =
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create 1
    node["repository"] <- JsonValue.Create "masterlifting/mcp-store"
    node["eventRevision"] <- JsonValue.Create "0123456789abcdef0123456789abcdef01234567"
    node["runId"] <- JsonValue.Create "123456"
    node["attempt"] <- JsonValue.Create "1"
    node["trigger"] <- JsonValue.Create "workflow_dispatch"
    node["role"] <- JsonValue.Create "candidate"
    node["rosterPath"] <- JsonValue.Create "infrastructure/release/Participants.json"
    node["rosterSha256"] <- JsonValue.Create(String.replicate 64 "a")

    let componentPaths = JsonObject()
    componentPaths["dotnet"] <- JsonValue.Create "dotnet/release/Config.fsx"
    componentPaths["workflow"] <- JsonValue.Create "workflow/release/Config.fsx"
    node["componentConfigPaths"] <- componentPaths

    let componentShas = JsonObject()
    componentShas["dotnet"] <- JsonValue.Create(String.replicate 64 "b")
    componentShas["workflow"] <- JsonValue.Create(String.replicate 64 "c")
    node["componentConfigShas"] <- componentShas

    let runtimePaths = JsonArray()
    runtimePaths.Add(JsonValue.Create "dotnet.exe")
    node["runtimeFilePaths"] <- runtimePaths

    let runtimeDigests = JsonObject()
    runtimeDigests["dotnet.exe"] <- JsonValue.Create(String.replicate 64 "d")
    node["runtimeFileDigests"] <- runtimeDigests

    let runtimeSizes = JsonObject()
    runtimeSizes["dotnet.exe"] <- JsonValue.Create 123
    node["runtimeFileSizes"] <- runtimeSizes
    node["builderSdk"] <- JsonValue.Create "11.0.100"
    node["builderRuntime"] <- JsonValue.Create "11.0.0"
    node["builderPlatform"] <- JsonValue.Create "win-x64"
    node

let private parseTrustedNode (node: JsonObject) =
    ReleaseHandoff.parseTrustedPublisherReceipt (node.ToJsonString()) "trusted-receipt.json"

let private setJsonNull (node: JsonObject) (name: string) =
    node[name] <- (null: JsonNode)

let private patchCentralSizes (entryName: string) (compressedSize: uint32) (uncompressedSize: uint32) (archiveBytes: byte[]) =
    let signature (offset: int) (value: uint32) =
        BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(offset, 4)) = value

    let mutable eocd = archiveBytes.Length - 22

    while eocd >= 0 && not (signature eocd 0x06054B50u) do
        eocd <- eocd - 1

    if eocd < 0 then
        failtest "the generated ZIP has an end-of-central-directory record"

    let centralOffset = int (BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(eocd + 16, 4)))
    let mutable recordOffset = centralOffset
    let mutable patched = false

    while recordOffset <= eocd - 46 && not patched do
        if not (signature recordOffset 0x02014B50u) then
            failtestf "the generated ZIP central directory is valid at byte %d" recordOffset

        let nameLength = int (BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(recordOffset + 28, 2)))
        let extraLength = int (BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(recordOffset + 30, 2)))
        let commentLength = int (BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(recordOffset + 32, 2)))
        let currentName = Encoding.UTF8.GetString(archiveBytes, recordOffset + 46, nameLength)

        if currentName = entryName then
            BinaryPrimitives.WriteUInt32LittleEndian(archiveBytes.AsSpan(recordOffset + 20, 4), compressedSize)
            BinaryPrimitives.WriteUInt32LittleEndian(archiveBytes.AsSpan(recordOffset + 24, 4), uncompressedSize)
            patched <- true
        else
            recordOffset <- recordOffset + 46 + nameLength + extraLength + commentLength

    if not patched then
        failtestf "the generated ZIP central directory contains '%s'" entryName

let private unsafePathRejected (name: string) =
    testCase ($"outer ZIP rejects unsafe path '{name}'") (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let outerBytes =
            zipBytes
                [ policy.ArchiveName, producerArchive
                  "distribution.json", manifestBytes
                  name, [| 1uy |] ]
        let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
        expectRejected $"unsafe outer entry '{name}'" (readOuter policy outerBytes evidence))

let private attributeCase name attributes expectedDetail =
    testCase ($"outer ZIP rejects {name} attributes") (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let outerBytes =
            zipBytesWithExternalAttributes
                [ policy.ArchiveName, producerArchive, attributes
                  "distribution.json", manifestBytes, 0 ]
        let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
        expectMalformedContaining name expectedDetail (readOuter policy outerBytes evidence))

let private metadataMismatchCase field mutateObserved =
    testCase ($"outer ZIP rejects observed metadata mismatch for {field}") (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let outerBytes = zipBytes [ policy.ArchiveName, producerArchive; "distribution.json", manifestBytes ]
        let evidence =
            artifactEvidence
                outerBytes
                (expectedMetadata policy.ComponentId)
                (mutateObserved (observedMetadata policy.ComponentId))
                (sha256Hex outerBytes)
                (int64 outerBytes.Length)
        expectRejected $"metadata field '{field}' mismatch" (readOuter policy outerBytes evidence))

let private trustedReceiptMutationCase label mutate =
    testCase ($"trusted receipt rejects {label}") (fun () ->
        let node = validTrustedReceiptNode ()
        mutate node
        expectRejected label (parseTrustedNode node))

let private preparationReceiptMutationCase label mutate =
    testCase ($"preparation receipt rejects {label}") (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let node = preparationNode producerArchive manifestBytes
        mutate node
        expectRejected label (parsePreparationNode node))

let private symlinkOuterEntryRejected =
    testCase "outer ZIP rejects a canonical entry marked as a symbolic link" (fun () ->
        let componentId = "dotnet"
        let policy = canonicalPolicy componentId
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let symlinkAttributes = 0xA000 <<< 16
        let outerBytes =
            zipBytesWithExternalAttributes
                [ policy.ArchiveName, producerArchive, symlinkAttributes
                  "distribution.json", manifestBytes, 0 ]

        use outerStream = new MemoryStream(outerBytes)
        use outerArchive = new ZipArchive(outerStream, ZipArchiveMode.Read)
        let innerEntry = outerArchive.GetEntry policy.ArchiveName
        let unixFileType = (innerEntry.ExternalAttributes >>> 16) &&& 0xF000
        Expect.equal unixFileType 0xA000 "the ZIP central directory records the canonical entry as a Unix symbolic link"

        let evidence: ReleaseHandoff.ArtifactEvidence =
            { Expected = expectedMetadata componentId
              Observed = observedMetadata componentId
              ActualOuterDigest = outerDigest (sha256Hex outerBytes)
              TransportSourcePath = "candidate-artifact.zip"
              TransportLength = int64 outerBytes.Length
              RawTransportBytes = outerBytes }

        match ReleaseHandoff.readRawTransport "candidate-artifact.zip" outerBytes evidence policy with
        | Error _ -> ()
        | Ok _ -> failtest "a ZIP entry carrying Unix symbolic-link file type was accepted")

let private trustedReceiptValid =
    testCase "trusted receipt accepts a populated cross-consistent runtime triplet" (fun () ->
        match parseTrustedNode (validTrustedReceiptNode ()) with
        | Error error -> failtestf "valid trusted receipt was rejected: %A" error
        | Ok receipt ->
            Expect.equal receipt.SchemaVersion 1 "trusted receipt uses the current schema"
            Expect.equal receipt.RuntimeFilePaths [ "dotnet.exe" ] "trusted runtime path is retained"
            Expect.equal (Map.keys receipt.RuntimeFileDigests |> Seq.toList) [ "dotnet.exe" ] "trusted runtime digest is paired with its path"
            Expect.equal (Map.keys receipt.RuntimeFileSizes |> Seq.toList) [ "dotnet.exe" ] "trusted runtime size is paired with its path")

let private trustedReceiptMutationTests =
    let mutations: (string * (JsonObject -> unit)) list =
        [ "an unknown field", (fun node -> node["untrusted"] <- JsonValue.Create true)
          "a missing required repository", (fun node -> node.Remove "repository" |> ignore)
          "a null required repository", (fun node -> setJsonNull node "repository")
          "an unsupported schema version", (fun node -> node["schemaVersion"] <- JsonValue.Create 2)
          "a malformed event revision", (fun node -> node["eventRevision"] <- JsonValue.Create "G123")
          "an uppercase event revision", (fun node -> node["eventRevision"] <- JsonValue.Create(String.replicate 40 "A"))
          "a malformed roster SHA-256", (fun node -> node["rosterSha256"] <- JsonValue.Create "AB")
          "an uppercase component SHA-256", (fun node -> node["componentConfigShas"].AsObject()["dotnet"] <- JsonValue.Create(String.replicate 64 "A"))
          "a missing runtime paths field", (fun node -> node.Remove "runtimeFilePaths" |> ignore)
          "a null runtime paths field", (fun node -> setJsonNull node "runtimeFilePaths")
          "an empty runtime paths field", (fun node -> node["runtimeFilePaths"] <- JsonArray())
          "a missing runtime digests field", (fun node -> node.Remove "runtimeFileDigests" |> ignore)
          "a null runtime digests field", (fun node -> setJsonNull node "runtimeFileDigests")
          "an empty runtime digests field", (fun node -> node["runtimeFileDigests"] <- JsonObject())
          "a missing runtime sizes field", (fun node -> node.Remove "runtimeFileSizes" |> ignore)
          "a null runtime sizes field", (fun node -> setJsonNull node "runtimeFileSizes")
          "an empty runtime sizes field", (fun node -> node["runtimeFileSizes"] <- JsonObject())
          "a runtime path without a digest", (fun node -> node["runtimeFileDigests"].AsObject().Remove "dotnet.exe" |> ignore)
          "a runtime path without a size", (fun node -> node["runtimeFileSizes"].AsObject().Remove "dotnet.exe" |> ignore)
          "an extra runtime digest without a path", (fun node -> node["runtimeFileDigests"].AsObject().Add("extra.dll", JsonValue.Create(String.replicate 64 "e")))
          "an extra runtime size without a path", (fun node -> node["runtimeFileSizes"].AsObject().Add("extra.dll", JsonValue.Create 7))
          "a component path without a hash", (fun node -> node["componentConfigShas"].AsObject().Remove "dotnet" |> ignore)
          "a component hash without a path", (fun node -> node["componentConfigPaths"].AsObject().Remove "dotnet" |> ignore)
          "a null runtime digest", (fun node -> setJsonNull (node["runtimeFileDigests"].AsObject()) "dotnet.exe")
          "a null runtime size", (fun node -> setJsonNull (node["runtimeFileSizes"].AsObject()) "dotnet.exe") ]

    testList "trusted receipt malformed and incomplete negatives" (mutations |> List.map (fun (label, mutate) -> trustedReceiptMutationCase label mutate))

let private preparationReceiptValid =
    testCase "preparation receipt binds producer-known hashes, sizes, and schema only" (fun () ->
        for componentId in [ "dotnet"; "workflow" ] do
            let policy = canonicalPolicy componentId
            let producerArchive, manifestBytes = portableCandidateArchive policy
            let receipt =
                match ReleaseHandoff.parsePreparationReceipt (Encoding.UTF8.GetString(preparationReceipt producerArchive manifestBytes)) "receipt.json" with
                | Ok value -> value
                | Error error -> failtestf "producer-known preparation receipt for %s was rejected: %A" componentId error

            Expect.equal receipt.SchemaVersion 1 $"{componentId} receipt schema is current"
            Expect.equal receipt.InnerArchiveSha256.Value (sha256Hex producerArchive) $"{componentId} receipt binds producer ZIP bytes"
            Expect.equal receipt.InnerArchiveSize (int64 producerArchive.Length) $"{componentId} receipt binds producer ZIP length"
            Expect.equal receipt.ManifestSha256.Value (sha256Hex manifestBytes) $"{componentId} receipt binds standalone manifest bytes"
            Expect.equal receipt.ManifestSize (int64 manifestBytes.Length) $"{componentId} receipt binds standalone manifest length"

            let fields =
                JsonNode.Parse(Encoding.UTF8.GetString(preparationReceipt producerArchive manifestBytes)).AsObject()
                |> Seq.map (fun property -> property.Key)
                |> Set.ofSeq

            Expect.equal fields (set [ "schemaVersion"; "innerArchiveSha256"; "innerArchiveSize"; "manifestSha256"; "manifestSize" ]) $"{componentId} preparation receipt has no outer transport, runtime, or toolchain claims")

let private preparationReceiptMutationTests =
    let forbiddenFields =
        [ "candidateOuterDigest"
          "candidateOuterSize"
          "actualOuterDigest"
          "outerTransportSha256"
          "runtimeFilePaths"
          "runtimeFileDigests"
          "runtimeFileSizes"
          "builderSdk"
          "builderRuntime"
          "builderPlatform" ]

    let mutations: (string * (JsonObject -> unit)) list =
        [ "a missing schema version", (fun node -> node.Remove "schemaVersion" |> ignore)
          "a missing inner archive digest", (fun node -> node.Remove "innerArchiveSha256" |> ignore)
          "a missing inner archive size", (fun node -> node.Remove "innerArchiveSize" |> ignore)
          "a missing manifest digest", (fun node -> node.Remove "manifestSha256" |> ignore)
          "a missing manifest size", (fun node -> node.Remove "manifestSize" |> ignore)
          "a null schema version", (fun node -> setJsonNull node "schemaVersion")
          "a null inner archive digest", (fun node -> setJsonNull node "innerArchiveSha256")
          "a null inner archive size", (fun node -> setJsonNull node "innerArchiveSize")
          "a null manifest digest", (fun node -> setJsonNull node "manifestSha256")
          "a null manifest size", (fun node -> setJsonNull node "manifestSize")
          "an unsupported schema version", (fun node -> node["schemaVersion"] <- JsonValue.Create 2)
          "a malformed inner archive SHA-256", (fun node -> node["innerArchiveSha256"] <- JsonValue.Create "not-a-sha")
          "an uppercase manifest SHA-256", (fun node -> node["manifestSha256"] <- JsonValue.Create(String.replicate 64 "A"))
          "a negative inner archive size", (fun node -> node["innerArchiveSize"] <- JsonValue.Create -1L)
          "a negative manifest size", (fun node -> node["manifestSize"] <- JsonValue.Create -1L) ]
        @ (forbiddenFields |> List.map (fun field -> ($"forbidden field '{field}'", (fun node -> node[field] <- JsonValue.Create "forbidden"))))

    let ordinaryCases = mutations |> List.map (fun (label, mutate) -> preparationReceiptMutationCase label mutate)
    let malformedJson = testCase "preparation receipt rejects malformed JSON" (fun () -> expectRejected "malformed JSON" (ReleaseHandoff.parsePreparationReceipt "{" "receipt.json"))
    testList "preparation receipt malformed and forbidden-field negatives" (malformedJson :: ordinaryCases)

let private outerEvidenceMutationCase label mutateEvidence =
    testCase ($"outer ZIP rejects {label}") (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let outerBytes = zipBytes [ policy.ArchiveName, producerArchive; "distribution.json", manifestBytes ]
        let baseline = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
        expectRejected label (readOuter policy outerBytes (mutateEvidence baseline)))

let private outerEvidenceNegativeTests =
    let metadataMutations: (string * (ReleaseHandoff.ObservedPlatformMetadata -> ReleaseHandoff.ObservedPlatformMetadata)) list =
        [ "artifactId", (fun observed -> { observed with ArtifactId = "wrong-id" })
          "artifactName", (fun observed -> { observed with ArtifactName = "wrong-name" })
          "role", (fun observed -> { observed with Role = "runtime" })
          "component", (fun observed -> { observed with Component = "workflow" })
          "repository", (fun observed -> { observed with Repository = "other/repository" })
          "runId", (fun observed -> { observed with RunId = "654321" })
          "attempt", (fun observed -> { observed with Attempt = "2" })
          "sha", (fun observed -> { observed with Sha = "fedcba9876543210fedcba9876543210fedcba98" }) ]

    let metadataCases = metadataMutations |> List.map (fun (field, mutate) -> metadataMismatchCase field mutate)
    let evidenceCases =
        [ outerEvidenceMutationCase "a malformed outer digest shape" (fun evidence -> { evidence with ActualOuterDigest = outerDigest "abc" })
          outerEvidenceMutationCase "an uppercase outer digest" (fun evidence -> { evidence with ActualOuterDigest = outerDigest((sha256Hex evidence.RawTransportBytes).ToUpperInvariant()) })
          outerEvidenceMutationCase "an outer digest that does not match the bytes" (fun evidence -> { evidence with ActualOuterDigest = outerDigest(String.replicate 64 "0") })
          outerEvidenceMutationCase "a declared transport length that does not match the bytes" (fun evidence -> { evidence with TransportLength = evidence.TransportLength + 1L }) ]

    testList "transport metadata, digest, and length negatives" (metadataCases @ evidenceCases)

let private exactOuterEntryNegativeTests =
    let makeCase label entries =
        testCase ($"outer ZIP rejects {label}") (fun () ->
            let policy = canonicalPolicy "dotnet"
            let producerArchive, manifestBytes = portableCandidateArchive policy
            let outerBytes = zipBytes (entries policy producerArchive manifestBytes)
            let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
            expectRejected label (readOuter policy outerBytes evidence))

    let caseVariant (value: string) =
        let chars = value.ToCharArray()
        let index = chars |> Array.findIndex Char.IsLetter
        chars[index] <- if Char.IsUpper chars[index] then Char.ToLowerInvariant chars[index] else Char.ToUpperInvariant chars[index]
        String chars

    testList
        "outer exact entry-set negatives"
        [ makeCase "an extra entry" (fun policy inner manifest -> [ policy.ArchiveName, inner; "distribution.json", manifest; "extra.txt", [| 1uy |] ])
          makeCase "a missing inner archive" (fun _ _ manifest -> [ ("distribution.json", manifest) ])
          makeCase "a missing standalone manifest" (fun policy inner _ -> [ (policy.ArchiveName, inner) ])
          makeCase "a duplicate inner archive name" (fun policy inner manifest -> [ policy.ArchiveName, inner; policy.ArchiveName, inner; "distribution.json", manifest ])
          makeCase "a duplicate standalone manifest name" (fun policy inner manifest -> [ policy.ArchiveName, inner; "distribution.json", manifest; "distribution.json", manifest ])
          makeCase "a case-insensitive collision" (fun policy inner manifest -> [ policy.ArchiveName, inner; caseVariant policy.ArchiveName, inner; "distribution.json", manifest ]) ]

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
    |> testList "outer unsafe path negatives"

let private zipAttributeNegativeTests =
    testList
        "outer ZIP file-type and reparse negatives"
        [ attributeCase "DOS reparse-point" 0x400 "DOS reparse-point attribute"
          attributeCase "Unix directory" (0x4000 <<< 16) "file type 0x4000"
          attributeCase "Unix FIFO" (0x1000 <<< 16) "file type 0x1000"
          attributeCase "Unix character device" (0x2000 <<< 16) "file type 0x2000"
          attributeCase "Unix block device" (0x3000 <<< 16) "file type 0x3000"
          attributeCase "Unix socket" (0xC000 <<< 16) "file type 0xC000"
          attributeCase "unsupported Unix type" (0x5000 <<< 16) "unsupported Unix file type" ]

let private ordinaryUnixRegularEntryAccepted =
    testCase "outer ZIP accepts an ordinary Unix regular file attribute" (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let outerBytes = zipBytesWithExternalAttributes [ policy.ArchiveName, producerArchive, (0x8000 <<< 16); "distribution.json", manifestBytes, 0 ]
        let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
        match readOuter policy outerBytes evidence with
        | Error error -> failtestf "ordinary Unix regular-file entry was rejected: %A" error
        | Ok _ -> ())

let private outerLimitsNegativeTests =
    let entryCount =
        testCase "outer ZIP rejects an entry count above the cap" (fun () ->
            let policy = canonicalPolicy "dotnet"
            let entries = [ for index in 0 .. ReleaseHandoff.outerEntryCountMax do yield $"entry-{index:D4}.txt", [||] ]
            let outerBytes = zipBytes entries
            let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
            expectMalformedContaining "outer entry count" $"exceeds the {ReleaseHandoff.outerEntryCountMax}-entry cap" (readOuter policy outerBytes evidence))

    let entrySize =
        testCase "outer ZIP rejects declared uncompressed entry size above the cap without allocating it" (fun () ->
            let policy = canonicalPolicy "dotnet"
            let producerArchive, manifestBytes = portableCandidateArchive policy
            let outerBytes = zipBytes [ policy.ArchiveName, producerArchive; "distribution.json", manifestBytes ]
            patchCentralSizes policy.ArchiveName 1u (uint32 (ReleaseHandoff.outerEntryUncompressedMax + 1L)) outerBytes
            let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
            expectMalformedContaining "outer entry size" "uncompressed cap" (readOuter policy outerBytes evidence))

    let compressionRatio =
        testCase "outer ZIP rejects declared expansion ratio above the cap without expanding it" (fun () ->
            let policy = canonicalPolicy "dotnet"
            let producerArchive, manifestBytes = portableCandidateArchive policy
            let outerBytes = zipBytes [ policy.ArchiveName, producerArchive; "distribution.json", manifestBytes ]
            patchCentralSizes policy.ArchiveName 1u 201u outerBytes
            let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
            expectMalformedContaining "outer compression ratio" "compression ratio cap" (readOuter policy outerBytes evidence))

    let totalSize =
        testCase "outer ZIP rejects declared total uncompressed size above the cap without allocating it" (fun () ->
            let policy = canonicalPolicy "dotnet"
            let _, manifestBytes = portableCandidateArchive policy
            let outerBytes = zipBytes [ policy.ArchiveName, [| 1uy |]; "distribution.json", manifestBytes ]
            patchCentralSizes "distribution.json" (uint32 ((uint64 ReleaseHandoff.outerEntryUncompressedMax + uint64 ReleaseHandoff.outerEntryCompressionRatioMax - 1UL) / uint64 ReleaseHandoff.outerEntryCompressionRatioMax)) (uint32 ReleaseHandoff.outerEntryUncompressedMax) outerBytes
            let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
            expectMalformedContaining "outer total uncompressed size" "total" (readOuter policy outerBytes evidence))

    let malformedZip =
        testCase "outer ZIP rejects non-ZIP transport bytes" (fun () ->
            let policy = canonicalPolicy "dotnet"
            let outerBytes = Encoding.UTF8.GetBytes "not a zip"
            let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
            expectRejected "non-ZIP transport bytes" (readOuter policy outerBytes evidence))

    testList "bounded outer ZIP limits and format negatives" [ entryCount; entrySize; compressionRatio; totalSize; malformedZip ]

let private mismatchedStandaloneManifestRejected =
    testCase "handoff rejects standalone and embedded manifest byte mismatch" (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, embeddedManifest = portableCandidateArchive policy
        let standaloneManifest = Array.append embeddedManifest [| byte ' ' |]
        let outerBytes = zipBytes [ policy.ArchiveName, producerArchive; "distribution.json", standaloneManifest ]
        let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)

        match readOuter policy outerBytes evidence with
        | Error _ -> ()
        | Ok snapshot ->
            let standaloneBytes = snapshot.ManifestBytesCopy()
            let embeddedBytes = snapshot.EmbeddedManifestBytesCopy()
            Expect.notEqual standaloneBytes embeddedBytes "fixture carries different standalone and embedded manifest bytes"
            failtestf "readRawTransport accepted different standalone/embedded manifests (standalone sha=%s, embedded sha=%s); expected rejection before any transaction" (sha256Hex standaloneBytes) (sha256Hex embeddedBytes))

let private missingEmbeddedManifestRejected =
    testCase "handoff rejects an inner producer archive without its embedded manifest" (fun () ->
        let policy = canonicalPolicy "dotnet"
        let _, manifestBytes = portableCandidateArchive policy
        let innerBytes =
            zipBytes
                (policy.PublishedFiles
                 |> List.map (fun name -> name, Encoding.UTF8.GetBytes($"candidate-payload:{policy.ComponentId}:{name}"))
                 |> fun payloads -> payloads @ [ "NOTICE.txt", Encoding.UTF8.GetBytes "candidate notice" ])
        let outerBytes = zipBytes [ policy.ArchiveName, innerBytes; "distribution.json", manifestBytes ]
        let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)

        match readOuter policy outerBytes evidence with
        | Error (MalformedArtifact(_, detail)) ->
            Expect.isTrue
                (detail.Contains("exactly one", StringComparison.Ordinal)
                 && detail.Contains("distribution.json", StringComparison.Ordinal))
                $"the inner archive rejection identifies its missing canonical manifest entry: {detail}"
        | Error error -> failtestf "the malformed inner archive was rejected for an unexpected reason: %A" error
        | Ok _ -> failtest "an inner producer archive without distribution.json was accepted")

let private snapshotStorageIsNotExposed =
    testCase "handoff snapshot copies and read-only streams do not expose verified backing arrays" (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let outerBytes = zipBytes [ policy.ArchiveName, producerArchive; "distribution.json", manifestBytes ]
        let evidence = artifactEvidence outerBytes (expectedMetadata policy.ComponentId) (observedMetadata policy.ComponentId) (sha256Hex outerBytes) (int64 outerBytes.Length)
        let snapshot =
            match readOuter policy outerBytes evidence with
            | Error error -> failtestf "canonical candidate was rejected: %A" error
            | Ok value -> value

        let mutateFirst (bytes: byte[]) =
            if bytes.Length > 0 then bytes[0] <- bytes[0] ^^^ 0xFFuy

        let innerCopy = snapshot.InnerArchiveBytesCopy()
        mutateFirst innerCopy
        Expect.equal (snapshot.InnerArchiveBytesCopy()) producerArchive "mutating an inner archive copy does not alter the snapshot"

        let manifestCopy = snapshot.ManifestBytesCopy()
        mutateFirst manifestCopy
        Expect.equal (snapshot.ManifestBytesCopy()) manifestBytes "mutating a standalone manifest copy does not alter the snapshot"

        let embeddedCopy = snapshot.EmbeddedManifestBytesCopy()
        mutateFirst embeddedCopy
        Expect.equal (snapshot.EmbeddedManifestBytesCopy()) manifestBytes "mutating an embedded manifest copy does not alter the snapshot"

        let entry = snapshot.Entries |> List.find (fun value -> value.Name.Value = policy.ArchiveName)
        let entryPayloadCopy = entry.PayloadBytesCopy()
        mutateFirst entryPayloadCopy
        Expect.equal (entry.PayloadBytesCopy()) producerArchive "mutating a verified-entry payload copy does not alter the snapshot"

        let entryNameCopy = entry.NameBytes
        mutateFirst entryNameCopy
        Expect.equal (entry.NameBytes) (Encoding.ASCII.GetBytes policy.ArchiveName) "mutating a verified-entry name copy does not alter the snapshot"

        let assertProtectedStream (label: string) (expected: byte[]) (createStream: unit -> Stream) =
            use stream = createStream ()
            Expect.isFalse stream.CanWrite $"{label} stream is read-only"

            match stream with
            | :? MemoryStream as memoryStream ->
                let mutable segment = Unchecked.defaultof<ArraySegment<byte>>
                Expect.isFalse (memoryStream.TryGetBuffer(&segment)) $"{label} stream does not expose a backing buffer"
            | _ -> failtestf "%s stream is not backed by the expected bounded memory stream" label

            use copied = new MemoryStream()
            stream.CopyTo copied
            Expect.equal (copied.ToArray()) expected $"{label} stream yields the verified bytes"

        assertProtectedStream "inner archive" producerArchive snapshot.InnerArchiveStream
        assertProtectedStream "standalone manifest" manifestBytes snapshot.ManifestStream
        assertProtectedStream "embedded manifest" manifestBytes snapshot.EmbeddedManifestStream
        assertProtectedStream "verified entry" producerArchive entry.PayloadStream

        use freshStream = snapshot.InnerArchiveStream()
        use freshCopy = new MemoryStream()
        freshStream.CopyTo freshCopy
        Expect.equal (freshCopy.ToArray()) producerArchive "disposing an earlier stream does not affect a later stream" )

let private preparationOuterDigestIsIndependent =
    testCase "preparation receipt does not claim the later platform outer digest" (fun () ->
        let policy = canonicalPolicy "dotnet"
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let receipt =
            match ReleaseHandoff.parsePreparationReceipt (Encoding.UTF8.GetString(preparationReceipt producerArchive manifestBytes)) "receipt.json" with
            | Ok value -> value
            | Error error -> failtestf "valid preparation receipt was rejected: %A" error
        let outerBytes = zipBytes [ policy.ArchiveName, producerArchive; "distribution.json", manifestBytes ]
        Expect.notEqual receipt.InnerArchiveSha256.Value (sha256Hex outerBytes) "producer receipt digest is distinct from the post-upload outer digest")

let private portableCandidate componentId =
    testCase ($"{componentId} portable outer ZIP snapshots the producer archive and standalone manifest") (fun () ->
        let policy = canonicalPolicy componentId
        let producerArchive, manifestBytes = portableCandidateArchive policy
        let prepReceiptBytes = preparationReceipt producerArchive manifestBytes
        let outerBytes =
            zipBytes
                [ policy.ArchiveName, producerArchive
                  "distribution.json", manifestBytes ]

        let prepReceipt =
            match ReleaseHandoff.parsePreparationReceipt (Encoding.UTF8.GetString prepReceiptBytes) "receipt.json" with
            | Ok receipt -> receipt
            | Error error -> failtestf "producer-known preparation receipt was rejected: %A" error

        Expect.equal prepReceipt.InnerArchiveSha256.Value (sha256Hex producerArchive) "receipt binds the producer ZIP SHA-256"
        Expect.equal prepReceipt.InnerArchiveSize (int64 producerArchive.Length) "receipt binds the producer ZIP size"
        Expect.equal prepReceipt.ManifestSha256.Value (sha256Hex manifestBytes) "receipt binds the standalone manifest SHA-256"
        Expect.equal prepReceipt.ManifestSize (int64 manifestBytes.Length) "receipt binds the standalone manifest size"

        let contract: ReleaseCompare.ComponentContract =
            { ComponentId = policy.ComponentId
              Version = policy.Version
              ArchiveName = policy.ArchiveName
              EntryAllowlist = policy.PublishedFiles @ [ "NOTICE.txt"; "distribution.json" ] }

        let observation =
            match ReleaseCompare.observeBytes policy.ArchiveName producerArchive with
            | Ok value -> value
            | Error error -> failtestf "inner producer archive was rejected: %A" error

        match ReleaseCompare.validate contract observation "inner producer archive" with
        | Error error -> failtestf "inner producer archive failed canonical comparison validation: %A" error
        | Ok () -> ()

        Expect.equal observation.ManifestBody (Some(Encoding.UTF8.GetString manifestBytes)) "embedded manifest bytes match the standalone manifest bytes"

        let expected = expectedMetadata componentId
        let observed = observedMetadata componentId

        let evidence: ReleaseHandoff.ArtifactEvidence =
            { Expected = expected
              Observed = observed
              ActualOuterDigest = outerDigest (sha256Hex outerBytes)
              TransportSourcePath = "candidate-artifact.zip"
              TransportLength = int64 outerBytes.Length
              RawTransportBytes = outerBytes }

        let snapshot =
            match ReleaseHandoff.readRawTransport "candidate-artifact.zip" outerBytes evidence policy with
            | Error error ->
                failtestf
                    "canonical two-entry outer ZIP was rejected (%s, %d bytes, names=%A): %A"
                    policy.ArchiveName
                    outerBytes.Length
                    [ policy.ArchiveName; "distribution.json" ]
                    error
            | Ok value -> value

        Expect.equal snapshot.OuterDigest.Value (sha256Hex outerBytes) "outer digest binds the platform ZIP bytes"
        Expect.equal snapshot.OuterSize (int64 outerBytes.Length) "outer size binds the platform ZIP length"
        Expect.equal snapshot.InnerArchiveSha256.Value (sha256Hex producerArchive) "inner digest binds the producer ZIP bytes"
        Expect.equal snapshot.InnerArchiveSize (int64 producerArchive.Length) "inner size binds the producer ZIP bytes"
        Expect.equal (snapshot.InnerArchiveBytesCopy()) producerArchive "snapshot contains the producer ZIP, not outer transport bytes"
        Expect.equal (snapshot.ManifestBytesCopy()) manifestBytes "standalone manifest remains distinct candidate data"
        Expect.equal (snapshot.EmbeddedManifestBytesCopy()) manifestBytes "verified embedded manifest matches the standalone manifest"
        Expect.equal (snapshot.Entries |> List.map (fun entry -> entry.Name.Value) |> Set.ofList) (set [ policy.ArchiveName; "distribution.json" ]) "outer archive has exactly the canonical two entries"

        use innerStream = snapshot.InnerArchiveStream()
        use manifestStream = snapshot.ManifestStream()
        use embeddedManifestStream = snapshot.EmbeddedManifestStream()
        use innerCopy = new MemoryStream()
        use manifestCopy = new MemoryStream()
        use embeddedManifestCopy = new MemoryStream()
        innerStream.CopyTo innerCopy
        manifestStream.CopyTo manifestCopy
        embeddedManifestStream.CopyTo embeddedManifestCopy
        Expect.equal (innerCopy.ToArray()) producerArchive "inner archive stream yields only the verified producer ZIP"
        Expect.equal (manifestCopy.ToArray()) manifestBytes "manifest stream yields the standalone manifest"
        Expect.equal (embeddedManifestCopy.ToArray()) manifestBytes "embedded manifest stream yields the verified inner manifest")

let positiveTests =
    testList "canonical portable outer positives" [
        portableCandidate "dotnet"
        portableCandidate "workflow"
    ]

let tests =
    testList "candidate handoff" [
        positiveTests
        preparationReceiptValid
        preparationOuterDigestIsIndependent
        trustedReceiptValid
        testList
            "malformed transport negatives"
            [ symlinkOuterEntryRejected
              trustedReceiptMutationTests
              preparationReceiptMutationTests
              outerEvidenceNegativeTests
              exactOuterEntryNegativeTests
              unsafePathTests
              zipAttributeNegativeTests
              ordinaryUnixRegularEntryAccepted
              outerLimitsNegativeTests
              mismatchedStandaloneManifestRejected
              missingEmbeddedManifestRejected
              snapshotStorageIsNotExposed ]
    ]
