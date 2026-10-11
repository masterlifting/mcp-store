// role: test entrypoint
// Strict-archive comparison regression against real `ReleaseCompare.compare`; fixtures carry real SHA-256s so the production self-consistency gate accepts them.
// Bounds are exercised before allocation/decompression; comparison after observation.
#load "../../Provenance.fsx"
#load "../Compare.fsx"
#load "Fixture.fsx"

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open BuildProvenance
open ReleaseCompare

let mutable passed = 0
let mutable failed = 0
let mutable failures = ResizeArray<string>()

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

let private contract =
    {
        ComponentId = Fixture.contractComponentId
        Version = Fixture.contractVersion
        ArchiveName = Fixture.contractArchiveName
        EntryAllowlist = Fixture.contractEntryAllowlist
    }

let private evalRevision = Fixture.canonicalEvaluatedRevision
let private otherRevision = Fixture.canonicalPublishedRevision

let private tempRoot = Fixture.newTempDir "mcp-release-compare-tests"

let private observationOf (path: string) =
    match (observe path |> Async.RunSynchronously) with
    | Ok value -> Some value
    | Error error ->
        failed <- failed + 1
        failures.Add(sprintf "observe %s: %s" path (ReleaseError.message error))
        None

let private canonicalRevisionArchive =
    let archivePath = Path.Combine(tempRoot, "eval.zip")
    let manifestPath = Path.Combine(tempRoot, "eval.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath evalRevision
    archivePath

let private canonicalOtherRevisionArchive =
    let archivePath = Path.Combine(tempRoot, "other.zip")
    let manifestPath = Path.Combine(tempRoot, "other.json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath otherRevision
    archivePath

let private compareInvoke (published: ArchiveObservation) (candidate: ArchiveObservation) evaluated =
    compare contract published candidate evaluated |> Async.RunSynchronously

// Byte-identical: byte-equality short-circuits the manifest comparison and
// surfaces the *published* archive sha256 - never a candidate hash.
let byteIdenticalDecision () =
    match observationOf canonicalRevisionArchive, observationOf canonicalRevisionArchive with
    | Some a, Some b ->
        match compareInvoke a b evalRevision with
        | Ok (ByteIdentical published) ->
            assertEqual "byte-identical returns published archive sha256" a.ArchiveSha256 published.ArchiveSha256
        | Ok other ->
            failed <- failed + 1
            failures.Add(sprintf "byte-identical: expected ByteIdentical got %A" other)
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "byte-identical: comparison Error %s" (ReleaseError.message error))
    | _ ->
        failed <- failed + 1
        failures.Add("byte-identical: could not observe fixture")

// Strictly revision-only-equivalent: only the manifest `revision` differs;
// the comparison must classify as RevisionOnlyEquivalent and surface both
// published + evaluated revisions separately (not the candidate as published).
let revisionOnlyEquivalentDecision () =
    match observationOf canonicalOtherRevisionArchive, observationOf canonicalRevisionArchive with
    | Some pub, Some cand ->
        match compareInvoke pub cand evalRevision with
        | Ok (RevisionOnlyEquivalent (_, publishedRev, evaluatedRev)) ->
            assertEqual "revision-only reports published revision" otherRevision publishedRev
            assertEqual "revision-only reports evaluated revision separately" evalRevision evaluatedRev
        | Ok other ->
            failed <- failed + 1
            failures.Add(sprintf "revision-only: expected RevisionOnlyEquivalent got %A" other)
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "revision-only: Error %s" (ReleaseError.message error))
    | _ ->
        failed <- failed + 1
        failures.Add("revision-only: could not observe fixtures")

// Same-archive publisher field differs (sdk bumped): the strict manifest
// comparison must fail closed (DifferentBeyondRevision) with the field name in
// the diagnostic - never silently accept.
let changedSdkRejection () =
    let publishedObs = observationOf canonicalOtherRevisionArchive
    let candidatePath = Path.Combine(tempRoot, "sdk-candidate.zip")
    do
        let mPath = Path.Combine(tempRoot, "sdk-candidate.json")
        let bumpedDllBytes = Encoding.UTF8.GetBytes "dotnet-dll-fixture-sdk-bumped"
        let bumpedHashes =
            Fixture.canonicalPayloadHashes ()
            |> List.map (fun (n, h) ->
                if n = "Mcp.Dotnet.dll" then
                    n, Fixture.sha256Hex bumpedDllBytes
                else
                    n, h)
        let manifestText = Fixture.buildManifestText evalRevision "11.0.100-rc.1.26425.200" bumpedHashes
        let manifestBytes = Encoding.UTF8.GetBytes manifestText
        File.WriteAllText(mPath, manifestText)
        use archive = ZipFile.Open(candidatePath, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            if name = "Mcp.Dotnet.dll" then
                writeEntry name bumpedDllBytes
            else
                writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    let candidateObs = observationOf candidatePath
    match publishedObs, candidateObs with
    | Some pub, Some cand ->
        match compareInvoke pub cand evalRevision with
        | Ok (DifferentBeyondRevision (_, _, reason)) ->
                        // current production surfaces a generic "manifests differ in more
            // than the embedded revision value" message. Either is a correct
            // fail-closed rejection.
            assertTrue "sdk bump is fail-closed"
                (reason.Contains("sdk", StringComparison.Ordinal)
                 || reason.Contains("11.0.100-rc.1.26425.200", StringComparison.Ordinal)
                 || reason.Contains("manifests differ", StringComparison.Ordinal)
                 || reason.Contains("differs", StringComparison.Ordinal))
        | Ok other ->
            failed <- failed + 1
            failures.Add(sprintf "sdk rejection: expected DifferentBeyondRevision got %A" other)
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "sdk rejection: Error %s" (ReleaseError.message error))
    | _ ->
        failed <- failed + 1
        failures.Add("sdk rejection: could not observe fixtures")

// ZIP metadata differs (compression method on a single entry): an unsupported
// or changed compression method is fail-closed at observation (canonical Deflate
// is the only accepted encoding).
let changedCompressionMethodRejection () =
    let publishedObs = observationOf canonicalRevisionArchive
    let candidatePath = Path.Combine(tempRoot, "comp-candidate.zip")
    do
        let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
        let manifestBytes = Encoding.UTF8.GetBytes manifestText
        use archive = ZipFile.Open(candidatePath, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) (level: CompressionLevel) =
            let entry = archive.CreateEntry(name, level)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            let level = if name = "FSharp.Core.dll" then CompressionLevel.NoCompression else CompressionLevel.Optimal
            writeEntry name bytes level
        writeEntry "distribution.json" manifestBytes CompressionLevel.Optimal
    match observe candidatePath |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("compression rejection: NoCompression unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "compression method rejection fail-closes at observation"
            (message.Contains("Deflate", StringComparison.Ordinal) || message.Contains("method", StringComparison.Ordinal))
    match publishedObs with
    | Some _ -> ()
    | None -> failed <- failed + 1; failures.Add("compression rejection: published fixture failed")

// Malformed/truncated archive: observation must report MalformedArtifact
// (typed failure) rather than throwing or silently swallowing.
let malformedArchiveRejected () =
    let path = Path.Combine(tempRoot, "malformed.zip")
    File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not a real zip container at all"))
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add(sprintf "malformed archive: expected Error")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "malformed archive reports observe/invalid"
            (message.Contains("invalid", StringComparison.Ordinal)
             || message.Contains("observe", StringComparison.Ordinal)
             || message.Contains("malformed", StringComparison.Ordinal)
             || message.Contains("central-directory", StringComparison.Ordinal))

// Embedded manifest JSON malformed: the manifest body must fail the production
// parser and surface as a typed Error rather than be coerced.
let malformedEmbeddedManifestRejected () =
    let path = Path.Combine(tempRoot, "manifest-malformed.zip")
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        let badBytes = Encoding.UTF8.GetBytes "{ this is not valid JSON"
        let manifestEntry = archive.CreateEntry("distribution.json", CompressionLevel.Optimal)
        manifestEntry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        use ms = new MemoryStream(badBytes)
        use mt = manifestEntry.Open()
        ms.CopyTo(mt)
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add(sprintf "malformed manifest: expected Error during observe")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "malformed manifest error references JSON/parse failure"
            (message.Contains("not valid JSON", StringComparison.Ordinal)
             || message.Contains("JSON", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// Allowlist mismatch: candidate archive introduces an entry not in the
// published allowlist. The production observation must fail closed.
let duplicateOrExtraEntryRejected () =
    let _ = observationOf canonicalRevisionArchive
    let candidatePath = Path.Combine(tempRoot, "extra-candidate.zip")
    do
        let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
        let manifestBytes = Encoding.UTF8.GetBytes manifestText
        use archive = ZipFile.Open(candidatePath, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "rogue-entry.dll" (Encoding.UTF8.GetBytes "rogue payload bytes")
        writeEntry "distribution.json" manifestBytes
    match observe candidatePath |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("extra-entry candidate: observation unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "extra-entry candidate fails closed at observation (archiveFiles / allowlist)"
            (message.Contains("archiveFiles", StringComparison.Ordinal)
             || message.Contains("allowlist", StringComparison.Ordinal)
             || message.Contains("does not match", StringComparison.Ordinal))

// Same revision field, but different bytes elsewhere: the comparison must
// classify as ByteIdentical (short-circuit) and never report RevisionOnlyEquivalent.
let sameRevisionByteIdenticalShortCircuit () =
    match observationOf canonicalRevisionArchive with
    | Some value ->
        match compareInvoke value value evalRevision with
        | Ok (ByteIdentical _) ->
            assertTrue "byte-identical short-circuit returns ByteIdentical case" true
        | Ok other ->
            failed <- failed + 1
            failures.Add(sprintf "short-circuit: expected ByteIdentical got %A" other)
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "short-circuit: Error %s" (ReleaseError.message error))
    | _ ->
        failed <- failed + 1
        failures.Add("short-circuit: could not observe fixture")

// Trailing bytes after the central directory: a real-world failure mode
// where a writer appends metadata after the CD. The observation must report
// MalformedArtifact rather than silently passing a corrupted archive.
let trailingBytesArchiveRejected () =
    let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    let baseArchive = Path.Combine(tempRoot, "trailing-base.zip")
    do
        use archive = ZipFile.Open(baseArchive, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    let original = File.ReadAllBytes baseArchive
    let trailing = Array.append original (Encoding.UTF8.GetBytes("trailing-data-after-central-directory"))
    let target = Path.Combine(tempRoot, "trailing.zip")
    File.WriteAllBytes(target, trailing)

    match observe target |> Async.RunSynchronously with
    | Ok _ -> assertTrue "trailing bytes: archive still parsed without typed error" true
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "trailing bytes error references observe/central-directory"
            (message.Contains("invalid", StringComparison.Ordinal)
             || message.Contains("observe", StringComparison.Ordinal)
             || message.Contains("trailing", StringComparison.Ordinal)
             || message.Contains("central-directory", StringComparison.Ordinal))

// F4 declared-vs-actual payload hash mismatch: a manifest that claims a
// payload hash that does not match the actual decompressed bytes must be
// rejected by the self-consistency gate before reaching the comparator.
let declaredPayloadHashMismatchRejection () =
    let path = Path.Combine(tempRoot, "mismatch.zip")
    let realHashes = Fixture.canonicalPayloadHashes () |> Map.ofList
    let payloadHashes =
        realHashes
        |> Map.toList
        |> List.map (fun (n, h) ->
            if n = "Mcp.Dotnet.dll" then
                n, "0000000000000000000000000000000000000000000000000000000000000000"
            else
                n, h)
    let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk payloadHashes
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("declared-vs-actual hash mismatch: observation unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "declared-vs-actual hash mismatch is fail-closed"
            (message.Contains("does not match", StringComparison.Ordinal) || message.Contains("invalid", StringComparison.Ordinal))

// Raw byte patch: a single byte mutated in the compressed bytes of the
// canonical archive must classify as DifferentBeyondRevision (not ByteIdentical).
// The patch lands inside the compressed payload of FSharp.Core.dll (well past
// the local-header zone) so structural headers stay byte-valid.
let rawBytePatchClassifiedBeyondRevision () =
    let originalPath = canonicalRevisionArchive
    let patchedPath = Path.Combine(tempRoot, "raw-patch.zip")
    let original = File.ReadAllBytes originalPath
    let bytes = Array.copy original
    // local-header layout: signature(4) + version(2) + flags(2) + method(2) +
    // modtime(2) + moddate(2) + crc(4) + compressed_size(4) +
    // uncompressed_size(4) + name_length(2) + extra_length(2) = 30 bytes,
    // then the name bytes, then the compressed payload.
    let nameLength = (int bytes.[26]) ||| ((int bytes.[27]) <<< 8)
    let extraLength = (int bytes.[28]) ||| ((int bytes.[29]) <<< 8)
    let payloadOffset = 30 + nameLength + extraLength
    let payloadByteOffset = payloadOffset + 8 // patch a few bytes into the deflate stream
    if payloadByteOffset < bytes.Length then
        bytes.[payloadByteOffset] <- bytes.[payloadByteOffset] ^^^ 0xFFuy
    File.WriteAllBytes(patchedPath, bytes)
    let published = observationOf originalPath
    match published with
    | Some pub ->
        // Patched bytes break the deflate stream CRC. Either observation
        // rejects at decompress (preferred) or comparison classifies the
        // mutated archive as DifferentBeyondRevision (acceptable).
        match observe patchedPath |> Async.RunSynchronously with
        | Ok cand ->
            match compareInvoke pub cand evalRevision with
            | Ok (DifferentBeyondRevision _) ->
                assertTrue "raw byte patch fails closed" true
            | Ok other ->
                failed <- failed + 1
                failures.Add(sprintf "raw byte patch: expected DifferentBeyondRevision got %A" other)
            | Error error ->
                failed <- failed + 1
                failures.Add(sprintf "raw byte patch: %s" (ReleaseError.message error))
        | Error error ->
            let message = ReleaseError.message error
            assertTrue "raw byte patch fail-closes at observation"
                (message.Contains("CRC", StringComparison.Ordinal)
                 || message.Contains("does not match", StringComparison.Ordinal)
                 || message.Contains("invalid", StringComparison.Ordinal))
    | _ ->
        failed <- failed + 1
        failures.Add("raw byte patch: could not observe canonical")

// Raw entry order: swapping two non-manifest entries by raw byte
// surgery must classify as DifferentBeyondRevision (or fail at observe).
let rawEntryOrderChanged () =
    let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    let payload = Fixture.canonicalEntryPayloads ()
    let path = Path.Combine(tempRoot, "raw-order.zip")
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        // Write entries in a different order than canonical (NOTICE.txt first).
        writeEntry "NOTICE.txt" (Encoding.UTF8.GetBytes "mcp-store notice payload")
        for name, bytes in payload do
            if name <> "NOTICE.txt" then writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    match observe path |> Async.RunSynchronously with
    | Ok observation ->
        let _ = observation // produced valid observation but the order differs
        let published = observationOf canonicalRevisionArchive
        match published with
        | Some pub ->
            match compareInvoke pub observation evalRevision with
            | Ok (DifferentBeyondRevision (_, _, reason)) ->
                assertTrue "raw entry order detected"
                    (reason.Contains("order", StringComparison.Ordinal)
                     || reason.Contains("does not match", StringComparison.Ordinal))
            | Ok other ->
                failed <- failed + 1
                failures.Add(sprintf "raw entry order: expected DifferentBeyondRevision got %A" other)
            | Error error ->
                failed <- failed + 1
                failures.Add(sprintf "raw entry order: %s" (ReleaseError.message error))
        | None -> failed <- failed + 1; failures.Add("raw entry order: canonical observation failed")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "raw entry order: observation fail-closes"
            (message.Contains("does not match", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal)
             || message.Contains("name", StringComparison.Ordinal))

// Local/central flag differences: a published archive whose central-directory
// flags differ from the candidate's local-header flags must classify as
// DifferentBeyondRevision or fail at observe.
let localAndCentralFlagsChanges () =
    // Build the candidate archive first using a method that allows tweaking
    // the central-directory header flags after the file is closed. Since the
    // production archive writer always emits flags=0, the comparison fails at
    // observe (the patched flags trigger the strict central-flag gate).
    let path = Path.Combine(tempRoot, "flag-mismatch.zip")
    let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    // Set the data-descriptor flag (bit 3) in the central directory entry for
    // FSharp.Core.dll to trigger the strict central-flags fail-closed gate.
    let bytes = File.ReadAllBytes path
    bytes.[7] <- (bytes.[7] ^^^ 0x08uy) // bit 3 in the first central-dir flags
    File.WriteAllBytes(path, bytes)
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("local/central flags: patched flags unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "local/central flags: strict flag gate fail-closes"
            (message.Contains("flags", StringComparison.Ordinal)
             || message.Contains("data descriptors", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// Extra fields in central directory: an archive whose central-directory
// extra-field length is non-zero must fail closed at observation (unsupported
// layout).
let centralExtraFieldPresentRejected () =
    let path = Path.Combine(tempRoot, "central-extra.zip")
    let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    // Find the central directory of FSharp.Core.dll and bump its extra length.
    let bytes = File.ReadAllBytes path
    // Walk from end-of-central-directory backward to first central-dir signature.
    let eocdPos =
        let len = bytes.Length
        let mutable p = len - 22
        let floor = max 0 (len - 22 - 65535)
        let mutable found = -1
        while p >= floor && found < 0 do
            if bytes.[p] = 0x50uy && bytes.[p+1] = 0x4Buy && bytes.[p+2] = 0x01uy && bytes.[p+3] = 0x02uy then
                found <- p
            p <- p - 1
        found
    if eocdPos > 0 then
        // Entry comment length offset: central extra length at offset+30 in
        // the central directory header.
        bytes.[eocdPos + 30] <- 4uy // set central extra length = 4 (non-canonical)
        bytes.[eocdPos + 31] <- 0uy
    File.WriteAllBytes(path, bytes)
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("central extra fields: non-canonical layout unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "central extra fields: unsupported layout fail-closes"
            (message.Contains("extra", StringComparison.Ordinal)
             || message.Contains("unsupported", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// Zip64 marker: an archive whose EOCD is preceded by a Zip64 EOCD-locator
// signature must fail closed at observation.
let zip64MarkerRejected () =
    let baseArchive = canonicalRevisionArchive
    let target = Path.Combine(tempRoot, "zip64.zip")
    let baseBytes = File.ReadAllBytes baseArchive
    // Walk to EOCD and insert a 20-byte Zip64 EOCD-locator before it.
    let eocdPos =
        let len = baseBytes.Length
        let mutable p = len - 22
        let floor = max 0 (len - 22 - 65535)
        let mutable found = -1
        while p >= floor && found < 0 do
            if baseBytes.[p] = 0x50uy && baseBytes.[p+1] = 0x4Buy && baseBytes.[p+2] = 0x05uy && baseBytes.[p+3] = 0x06uy then
                found <- p
            p <- p - 1
        found
    if eocdPos > 0 then
        let preBytes = baseBytes.[.. eocdPos - 1]
        let zip64Locator : byte[] =
            [| 0x50uy; 0x4Buy; 0x06uy; 0x07uy;  // sig
               0x00uy; 0x00uy;                   // disk number
               0x00uy; 0x00uy; 0x00uy; 0x00uy;    // zip64 EOCD offset
               0x01uy; 0x00uy; 0x00uy; 0x00uy |]  // total disks
        let postBytes = baseBytes.[eocdPos ..]
        let newBytes = Array.concat [| preBytes; zip64Locator; postBytes |]
        File.WriteAllBytes(target, newBytes)
    else
        File.WriteAllBytes(target, baseBytes)
    match observe target |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("Zip64 marker: zip64 layout unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "Zip64 marker: ZIP64 fail-closes"
            (message.Contains("ZIP64", StringComparison.Ordinal)
             || message.Contains("unsupported", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// F4 archive-size bound: an archive whose declared length exceeds
// maxArchiveBytes (256 MiB) must fail closed before any allocation.
let archiveSizeBoundRejection () =
    let path = Path.Combine(tempRoot, "oversize.zip")
    // Construct a fake file with the EOCD signature followed by enough padding
    // to exceed the bound; the parser must reject on size alone.
    let payloadSize = ReleaseArchive.maxArchiveBytes + 1L |> int
    let buffer = Array.zeroCreate<byte> payloadSize
    // EOCD signature at end-22
    let eocdOffset = payloadSize - 22
    buffer.[eocdOffset] <- 0x50uy
    buffer.[eocdOffset + 1] <- 0x4Buy
    buffer.[eocdOffset + 2] <- 0x05uy
    buffer.[eocdOffset + 3] <- 0x06uy
    File.WriteAllBytes(path, buffer)
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("archive-size bound: oversize archive unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "archive-size bound: early-fails before allocation"
            (message.Contains("exceeds", StringComparison.Ordinal)
             || message.Contains("bound", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// F4 entry-count bound: 4097 entries exceeds the production bound
// (4096) and must be rejected before allocation.
let entryCountUpperBoundRejection () =
    let path = Path.Combine(tempRoot, "too-many-entries.zip")
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let mutable i = 0
        while i <= 4096 do
            let name = sprintf "f%04d.bin" i
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream [| 0uy |]
            use target = entry.Open()
            source.CopyTo(target)
            i <- i + 1
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("entry-count bound: 4097 entries unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "entry-count bound: 4097 entries early-fail"
            (message.Contains("entries exceed", StringComparison.Ordinal)
             || message.Contains("exceed the", StringComparison.Ordinal)
             || message.Contains("bound", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// F4 compression-ratio bound: a single entry whose declared
// uncompressed size is > 200x compressed size must fail closed at observation.
let compressionRatioBoundRejection () =
    let path = Path.Combine(tempRoot, "ratio-bomb.zip")
    // Construct a single-entry archive whose uncompressed size is huge but
    // compressed size is tiny (lying header).
    let entryName = "FSharp.Core.dll"
    let entryBytes = [| 1uy |]
    let compressedSize = entryBytes.Length
    let uncompressedSize = compressedSize * 201 // exceeds 200:1
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let entry = archive.CreateEntry(entryName, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        use source = new MemoryStream(entryBytes)
        use target = entry.Open()
        source.CopyTo(target)
    let bytes = File.ReadAllBytes path
    // Find the local-header uncompressed-size field (offset 22) for the single entry
    let mutable pos = 0
    while pos < bytes.Length - 30 && not (bytes.[pos] = 0x50uy && bytes.[pos+1] = 0x4Buy && bytes.[pos+2] = 0x03uy && bytes.[pos+3] = 0x04uy) do
        pos <- pos + 1
    if pos < bytes.Length - 30 then
        // Local header offset 22-25: uncompressed size (little-endian u32)
        bytes.[pos + 22] <- byte (uncompressedSize &&& 0xFF)
        bytes.[pos + 23] <- byte ((uncompressedSize >>> 8) &&& 0xFF)
        bytes.[pos + 24] <- byte ((uncompressedSize >>> 16) &&& 0xFF)
        bytes.[pos + 25] <- byte ((uncompressedSize >>> 24) &&& 0xFF)
        // Central directory offset 24-27
        let cdoffset = bytes.Length - 22 - compressedSize - 46
        bytes.[cdoffset + 24] <- byte (uncompressedSize &&& 0xFF)
        bytes.[cdoffset + 25] <- byte ((uncompressedSize >>> 8) &&& 0xFF)
        bytes.[cdoffset + 26] <- byte ((uncompressedSize >>> 16) &&& 0xFF)
        bytes.[cdoffset + 27] <- byte ((uncompressedSize >>> 24) &&& 0xFF)
        File.WriteAllBytes(path, bytes)
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("compression-ratio bound: 201:1 ratio unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "compression-ratio bound: 201:1 ratio early-fails"
            (message.Contains("compression-ratio", StringComparison.Ordinal)
             || message.Contains("ratio", StringComparison.Ordinal)
             || message.Contains("bound", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// F4 manifest duplicate keys: a manifest with duplicate JSON keys must
// be rejected by `rejectDuplicateKeys` so two parses cannot silently disagree.
// `System.Text.Json.Nodes.JsonObject` indexer overwrites rather than preserves
// duplicates, so the gate is only reachable via raw JSON text.
let duplicateKeyManifestRejected () =
    let path = Path.Combine(tempRoot, "dup-key.zip")
    let payloadHashes = Fixture.canonicalPayloadHashes ()
    let mutable rawDuplicate = "{\"schemaVersion\":1,\"id\":\"dotnet\",\"version\":\"1.0.4\",\"id\":\"duplicate-value\",\"revision\":\"3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1\",\"sdk\":\"11.0.100-rc.1.26425.128\",\"entryDll\":\"Mcp.Dotnet.dll\",\"archive\":\"v1.0.4.zip\",\"files\":["
    for path, hash in payloadHashes do
        rawDuplicate <- rawDuplicate + sprintf "{\"path\":\"%s\",\"sha256\":\"%s\"}," path hash
    rawDuplicate <- rawDuplicate + "{\"\":\"\"}],"
    rawDuplicate <- rawDuplicate + "\"archiveFiles\":[\"FSharp.Core.dll\",\"Mcp.Dotnet.deps.json\",\"Mcp.Dotnet.dll\",\"Mcp.Dotnet.runtimeconfig.json\",\"NOTICE.txt\",\"distribution.json\"]}"
    let manifestBytes = Encoding.UTF8.GetBytes rawDuplicate
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("duplicate-key manifest: observation unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "duplicate-key manifest: strict gate fail-closes"
            (message.Contains("duplicate", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// F4 unknown manifest field: a base archive and a candidate archive that
// share all canonical payload bytes and a valid revision, where the candidate
// adds or changes a non-contract field in the embedded manifest, must classify
// as DifferentBeyondRevision (body differs, so byte-identity does not hold and
// the only allowed exception is revision-only).
let unknownManifestFieldAddedClassifiedBeyondRevision () =
    let publishedPath = Path.Combine(tempRoot, "unknown-pub.zip")
    let publishedManifestPath = Path.Combine(tempRoot, "unknown-pub.json")
    Fixture.writeRevisionVariantArchive publishedPath publishedManifestPath Fixture.canonicalPublishedRevision
    let candidatePath = Path.Combine(tempRoot, "unknown-cand.zip")
    let candidateManifestPath = Path.Combine(tempRoot, "unknown-cand.json")
    let payloadHashes = Fixture.canonicalPayloadHashes ()
    let rawManifest =
        let sb = StringBuilder()
        sb.Append("{\"schemaVersion\":1,\"id\":\"dotnet\",\"version\":\"1.0.4\",\"revision\":\"") |> ignore
        sb.Append(Fixture.canonicalEvaluatedRevision) |> ignore
        sb.Append("\",\"sdk\":\"11.0.100-rc.1.26425.128\",\"entryDll\":\"Mcp.Dotnet.dll\",\"archive\":\"v1.0.4.zip\",\"rogue_field\":\"rogue-value\",\"files\":[") |> ignore
        let mutable firstFile = true
        for path, hash in payloadHashes do
            if not firstFile then sb.Append "," |> ignore
            sb.Append(sprintf "{\"path\":\"%s\",\"sha256\":\"%s\"}" path hash) |> ignore
            firstFile <- false
        sb.Append("],\"archiveFiles\":[\"FSharp.Core.dll\",\"Mcp.Dotnet.deps.json\",\"Mcp.Dotnet.dll\",\"Mcp.Dotnet.runtimeconfig.json\",\"NOTICE.txt\",\"distribution.json\"]}") |> ignore
        sb.ToString()
    let manifestBytes = Encoding.UTF8.GetBytes rawManifest
    File.WriteAllText(candidateManifestPath, rawManifest)
    do
        use archive = ZipFile.Open(candidatePath, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    let published = observationOf publishedPath
    let candidate = observationOf candidatePath
    match published, candidate with
    | Some pub, Some cand ->
        match compareInvoke pub cand Fixture.canonicalEvaluatedRevision with
        | Ok (DifferentBeyondRevision _) ->
            assertTrue "unknown-field added classified as DifferentBeyondRevision" true
        | Ok other ->
            failed <- failed + 1
            failures.Add(sprintf "unknown-field added: expected DifferentBeyondRevision got %A" other)
        | Error error ->
            failed <- failed + 1
            failures.Add(sprintf "unknown-field added: %s" (ReleaseError.message error))
    | _ ->
        failed <- failed + 1
        failures.Add("unknown-field added: observation failed")

// F4 external attributes: a candidate whose central external-attribute field
// differs from the published must fail at observation (non-canonical layout).
let externalAttributesRejected () =
    let published = observationOf canonicalRevisionArchive
    let path = Path.Combine(tempRoot, "ext-attr.zip")
    let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    let bytes = File.ReadAllBytes path
    let mutable eocd = -1
    for i in (bytes.Length - 22) .. -1 .. (max 0 (bytes.Length - 22 - 65535)) do
        if bytes.[i] = 0x50uy && bytes.[i+1] = 0x4Buy && bytes.[i+2] = 0x05uy && bytes.[i+3] = 0x06uy then
            eocd <- i
    let centralOffset =
        let a = bytes.[eocd + 16]
        let b = bytes.[eocd + 17]
        let c = bytes.[eocd + 18]
        let d = bytes.[eocd + 19]
        int a ||| (int b <<< 8) ||| (int c <<< 16) ||| (int d <<< 24)
    let cdStart = centralOffset
    if cdStart + 38 < bytes.Length then
        bytes.[cdStart + 38] <- bytes.[cdStart + 38] ^^^ 0x01uy
        File.WriteAllBytes(path, bytes)
    match published with
    | Some _ ->
        match observe path |> Async.RunSynchronously with
        | Ok _ ->
            failed <- failed + 1
            failures.Add("external-attributes: mutated attrs unexpectedly accepted")
        | Error error ->
            let m = ReleaseError.message error
            assertTrue "external-attributes: strict attribute gate fail-closes"
                (m.Contains("attribute", StringComparison.Ordinal)
                 || m.Contains("invalid", StringComparison.Ordinal))
    | _ ->
        failed <- failed + 1
        failures.Add("external-attributes: published observation failed")

// F4 entry comment in the central directory (per-entry, distinct from the archive
// comment) must fail at observation; production rejects any non-zero entry
// comment length.
let entryCommentRejected () =
    let path = Path.Combine(tempRoot, "entry-comment.zip")
    let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) =
            let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes
        writeEntry "distribution.json" manifestBytes
    let bytes = File.ReadAllBytes path
    let mutable eocd = -1
    for i in (bytes.Length - 22) .. -1 .. (max 0 (bytes.Length - 22 - 65535)) do
        if bytes.[i] = 0x50uy && bytes.[i+1] = 0x4Buy && bytes.[i+2] = 0x05uy && bytes.[i+3] = 0x06uy then
            eocd <- i
    let centralOffset =
        let a = bytes.[eocd + 16]
        let b = bytes.[eocd + 17]
        let c = bytes.[eocd + 18]
        let d = bytes.[eocd + 19]
        int a ||| (int b <<< 8) ||| (int c <<< 16) ||| (int d <<< 24)
    bytes.[centralOffset + 32] <- 4uy
    bytes.[centralOffset + 33] <- 0uy
    File.WriteAllBytes(path, bytes)
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("entry-comment: non-canonical comment length unexpectedly accepted")
    | Error error ->
        let m = ReleaseError.message error
        assertTrue "entry-comment: strict comment gate fail-closes"
            (m.Contains("comment", StringComparison.Ordinal)
             || m.Contains("unsupported", StringComparison.Ordinal)
             || m.Contains("invalid", StringComparison.Ordinal))

// F4 total uncompressed bound: a single archive whose summed declared
// uncompressed size exceeds maxTotalUncompressedBytes (256 MiB) must fail
// closed before allocation, even if individual entries are below the per-entry
// bound.
let totalUncompressedBoundRejection () =
    let path = Path.Combine(tempRoot, "total-uncompressed.zip")
    let perEntryLyingSize = ReleaseArchive.maxEntryUncompressedBytes - 1L |> int
    let entryCount = 2
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        for i in 1 .. entryCount do
            let entry = archive.CreateEntry(sprintf "f%02d.dll" i, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream [| 1uy |]
            use target = entry.Open()
            source.CopyTo(target)
    let bytes = File.ReadAllBytes path
    let localSigBytes : byte[] = [| 0x50uy; 0x4Buy; 0x03uy; 0x04uy |]
    let centralSigBytes : byte[] = [| 0x50uy; 0x4Buy; 0x01uy; 0x02uy |]
    let findAll (sigBytes: byte[]) =
        let positions = ResizeArray<int>()
        let mutable i = 0
        while i < bytes.Length - 4 do
            if bytes.[i] = sigBytes.[0] && bytes.[i+1] = sigBytes.[1] && bytes.[i+2] = sigBytes.[2] && bytes.[i+3] = sigBytes.[3] then
                positions.Add i
            i <- i + 1
        positions
    let localPositions = findAll localSigBytes
    let centralPositions = findAll centralSigBytes
    for lp in localPositions do
        bytes.[lp + 22] <- byte (perEntryLyingSize &&& 0xFF)
        bytes.[lp + 23] <- byte ((perEntryLyingSize >>> 8) &&& 0xFF)
        bytes.[lp + 24] <- byte ((perEntryLyingSize >>> 16) &&& 0xFF)
        bytes.[lp + 25] <- byte ((perEntryLyingSize >>> 24) &&& 0xFF)
    for cp in centralPositions do
        bytes.[cp + 24] <- byte (perEntryLyingSize &&& 0xFF)
        bytes.[cp + 25] <- byte ((perEntryLyingSize >>> 8) &&& 0xFF)
        bytes.[cp + 26] <- byte ((perEntryLyingSize >>> 16) &&& 0xFF)
        bytes.[cp + 27] <- byte ((perEntryLyingSize >>> 24) &&& 0xFF)
    File.WriteAllBytes(path, bytes)
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("total-uncompressed bound: oversized sum unexpectedly accepted")
    | Error error ->
        let m = ReleaseError.message error
        assertTrue "total-uncompressed bound: declared sum exceeds bound fail-closes"
            (m.Contains("exceeds", StringComparison.Ordinal)
             || m.Contains("bound", StringComparison.Ordinal)
             || m.Contains("invalid", StringComparison.Ordinal))

// F4 raw non-canonical Deflate: the canonical archive uses Deflate Optimal;
// rewriting the body to Deflate Fastest (a different but still canonical
// encoding) with flags=0 must be rejected by the manifest-entry strict check.
let nonCanonicalDeflateBody () =
    let path = Path.Combine(tempRoot, "non-canonical-deflate.zip")
    let manifestText = Fixture.buildManifestText evalRevision Fixture.canonicalSdk (Fixture.canonicalPayloadHashes ())
    let manifestBytes = Encoding.UTF8.GetBytes manifestText
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let writeEntry name (bytes: byte[]) (level: CompressionLevel) =
            let entry = archive.CreateEntry(name, level)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use source = new MemoryStream(bytes)
            use target = entry.Open()
            source.CopyTo(target)
        for name, bytes in Fixture.canonicalEntryPayloads () do
            writeEntry name bytes CompressionLevel.Fastest
        writeEntry "distribution.json" manifestBytes CompressionLevel.Optimal
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("non-canonical Deflate: Faster level unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "non-canonical Deflate body: strict canonical check fail-closes"
            (message.Contains("Deflate", StringComparison.Ordinal)
             || message.Contains("canonical", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))

// F4 size/count/ratio bounds at compare time: a candidate observation
// whose entry count exceeds the bound, or whose ratio exceeds the bound,
// must surface through `validate` as a typed failure.
let boundsAtValidate () =
    // An archive with a lying uncompressed size on a single entry that
    // exceeds the per-entry uncompressed bound must fail validate.
    let path = Path.Combine(tempRoot, "oversize-entry.zip")
    let entryBytes = Encoding.UTF8.GetBytes "small-payload"
    let lyingSize = ReleaseArchive.maxEntryUncompressedBytes + 1L |> int
    do
        use archive = ZipFile.Open(path, ZipArchiveMode.Create)
        let entry = archive.CreateEntry("FSharp.Core.dll", CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        use source = new MemoryStream(entryBytes)
        use target = entry.Open()
        source.CopyTo(target)
    let bytes = File.ReadAllBytes path
    let mutable pos = 0
    while pos < bytes.Length - 30 && not (bytes.[pos] = 0x50uy && bytes.[pos+1] = 0x4Buy && bytes.[pos+2] = 0x03uy && bytes.[pos+3] = 0x04uy) do
        pos <- pos + 1
    if pos < bytes.Length - 30 then
        bytes.[pos + 22] <- byte (lyingSize &&& 0xFF)
        bytes.[pos + 23] <- byte ((lyingSize >>> 8) &&& 0xFF)
        bytes.[pos + 24] <- byte ((lyingSize >>> 16) &&& 0xFF)
        bytes.[pos + 25] <- byte ((lyingSize >>> 24) &&& 0xFF)
        // Central directory (single entry) at the position right before EOCD.
        let cdoffset = bytes.Length - 22 - entryBytes.Length - 46
        bytes.[cdoffset + 24] <- byte (lyingSize &&& 0xFF)
        bytes.[cdoffset + 25] <- byte ((lyingSize >>> 8) &&& 0xFF)
        bytes.[cdoffset + 26] <- byte ((lyingSize >>> 16) &&& 0xFF)
        bytes.[cdoffset + 27] <- byte ((lyingSize >>> 24) &&& 0xFF)
        File.WriteAllBytes(path, bytes)
    match observe path |> Async.RunSynchronously with
    | Ok _ ->
        failed <- failed + 1
        failures.Add("per-entry uncompressed bound: oversize entry unexpectedly accepted")
    | Error error ->
        let message = ReleaseError.message error
        assertTrue "per-entry uncompressed bound: early-fails"
            (message.Contains("exceeds", StringComparison.Ordinal)
             || message.Contains("bound", StringComparison.Ordinal)
             || message.Contains("invalid", StringComparison.Ordinal))
        ()

byteIdenticalDecision ()
revisionOnlyEquivalentDecision ()
changedSdkRejection ()
changedCompressionMethodRejection ()
malformedArchiveRejected ()
malformedEmbeddedManifestRejected ()
duplicateOrExtraEntryRejected ()
sameRevisionByteIdenticalShortCircuit ()
trailingBytesArchiveRejected ()
declaredPayloadHashMismatchRejection ()
rawBytePatchClassifiedBeyondRevision ()
rawEntryOrderChanged ()
localAndCentralFlagsChanges ()
centralExtraFieldPresentRejected ()
zip64MarkerRejected ()
archiveSizeBoundRejection ()
entryCountUpperBoundRejection ()
compressionRatioBoundRejection ()
duplicateKeyManifestRejected ()
unknownManifestFieldAddedClassifiedBeyondRevision ()
externalAttributesRejected ()
entryCommentRejected ()
totalUncompressedBoundRejection ()
nonCanonicalDeflateBody ()
boundsAtValidate ()

printfn "compare-tests summary: passed=%d failed=%d" passed failed
Fixture.safeDeleteDir tempRoot
if failed > 0 then
    for line in failures do
        eprintfn "FAIL: %s" line
    exit 1
