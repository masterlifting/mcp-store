// role: reusable module/helper
// Repo-shared strict archive-comparison mechanics loaded as the explicit
// ReleaseCompare module. Implements the per-component release contract from
// the GHI-24 issue: every entry's CRC, length, compressed length, last-write
// timestamp, compression method, and payload SHA-256 must match exactly
// between a published versioned archive and a freshly built candidate, except
// for the embedded `distribution.json` whose CRC/length/compressed-length/
// payload-hash differences are derived from the manifest text changing. The
// manifest body is compared as a parsed JSON document with a strict
// field-by-field check that allows only `revision` to differ; any other field
// difference or hash mismatch fails closed.
//
// The comparison never normalizes JSON, never unpacks/repacks the archive, and
// never discards archive metadata; equality of the archive as built is the
// authoritative gate. An unsupported or inconclusive representation (for
// example an entry with an unrecognized compression method) fails closed so a
// future ZIP encoding change cannot silently drift past the gate.
//
// This module is compiled into the Release.fsproj assembly together with
// Provenance.fsx; the `#load` directive is honored by the analyzer
// (which typechecks reusable roots) and ignored by the project compiler
// (which compiles all sources in dependency order).
module ReleaseCompare

#load "../Provenance.fsx"

open System
open System.Globalization
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance

// One observation of a ZIP entry, captured from the parsed central
// directory. CompressionMethod is reported so an unsupported encoder fails
// closed instead of being treated as if it were Deflate.
type EntryObservation =
    { Name: string
      Crc32: uint32
      Length: int64
      CompressedLength: int64
      LastWriteTime: DateTimeOffset
      CompressionMethod: System.IO.Compression.ZipCompressionMethod
      PayloadSha256: string }

// The complete observation of one archive, captured once and consumed by
// every decision branch. `ManifestBody` is the raw embedded `distribution.json`
// text so the manifest comparison is byte-exact on the text and JSON-exact
// on the parsed document. `ManifestSha256` is the lowercase-hex SHA-256 of
// those exact manifest bytes so the consumer-neutral release-metadata
// projection can include a verified manifest hash instead of inventing one
// at publish time.
type ArchiveObservation =
    { ArchivePath: string
      ArchiveSha256: string
      Entries: EntryObservation list
      ManifestBody: string option
      ManifestSha256: string option }

// Per-component release-archive decision produced by `compare`. The
// decision carries the published values so the caller can emit them in a
// no-op report; the candidate is reported separately to keep evaluated
// HEAD distinct from published revision (issue req 26).
//
// `RevisionOnlyEquivalent` carries both the actual published revision
// (parsed from the published manifest body) and the evaluated revision
// (the candidate's embedded `revision` field). Reporting both keeps the
// per-component summary grounded in the published values, not the
// candidate ones.
type Decision =
    | ByteIdentical of published: ArchiveObservation
    | RevisionOnlyEquivalent of published: ArchiveObservation * publishedRevision: string * evaluatedRevision: string
    | DifferentBeyondRevision of published: ArchiveObservation * candidate: ArchiveObservation * reason: string

// The producer-side release contract the comparison enforces. Allowlist
// and archive name come from the producer's canonical Config.fsx; the
// comparison never re-implements them.
type ComponentContract =
    { ComponentId: string
      Version: string
      ArchiveName: string
      EntryAllowlist: string list }

// The current schema's canonical manifest entry name. A future schema
// change must either update this constant or add a new allowlist branch.
let private embeddedManifestEntryName = "distribution.json"

// One-time, IO-bound capture of an archive. Result-valued so a malformed
// archive (truncated, missing central directory, unreadable) is reported
// as a typed failure rather than a thrown exception. The payload hash is
// computed per entry so the manifest's `files` table is checked
// independently of the local-header CRC.
let private readArchiveBytes (path: string) : Result<byte[], ReleaseError> =
    try
        use readStream = File.OpenRead path
        let length = int readStream.Length
        let buffer = Array.zeroCreate<byte> length
        let mutable offset = 0
        let mutable truncated = false

        while offset < length do
            let read = readStream.Read(buffer, offset, length - offset)
            if read = 0 then
                truncated <- true
            else
                offset <- offset + read

        if truncated then
            Error(MalformedArtifact(path, "the archive ended before the declared length was read"))
        else
            Ok buffer
    with
    | :? IOException as error -> Error(FileFailure("read", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))

let private observeArchive (path: string) : Async<Result<ArchiveObservation, ReleaseError>> =
    async {
        match readArchiveBytes path with
        | Error error -> return Error error
        | Ok archiveBytes ->
        try
            let archiveSha256 =
                SHA256.HashData archiveBytes
                |> Convert.ToHexString
                |> fun value -> value.ToLowerInvariant()

            use archive = ZipFile.OpenRead path
            let entries = ResizeArray<EntryObservation>()
            let mutable manifestBody : string option = None
            let mutable manifestSha256 : string option = None

            for entry in archive.Entries do
                use payload = entry.Open()
                let payloadBuffer =
                    use memory = new MemoryStream()
                    payload.CopyTo(memory)
                    memory.ToArray()
                let payloadHash = SHA256.HashData payloadBuffer
                let payloadSha256 =
                    Convert.ToHexString payloadHash
                    |> fun value -> value.ToLowerInvariant()

                let normalizedName = entry.FullName.Replace('\\', '/')

                if normalizedName = embeddedManifestEntryName then
                    manifestBody <- Some(Encoding.UTF8.GetString(payloadBuffer))
                    manifestSha256 <- Some payloadSha256

                entries.Add(
                    { Name = normalizedName
                      Crc32 = uint32 entry.Crc32
                      Length = entry.Length
                      CompressedLength = entry.CompressedLength
                      LastWriteTime = entry.LastWriteTime
                      CompressionMethod = entry.CompressionMethod
                      PayloadSha256 = payloadSha256 }
                )

            return
                Ok
                    { ArchivePath = path
                      ArchiveSha256 = archiveSha256
                      Entries = entries |> Seq.sortBy _.Name |> Seq.toList
                      ManifestBody = manifestBody
                      ManifestSha256 = manifestSha256 }
        with
        | :? IOException as error -> return Error(FileFailure("observe", path, error.Message))
        | :? InvalidDataException as error ->
            return Error(MalformedArtifact(path, $"the archive is not a valid ZIP container: {error.Message}"))
        | :? NotSupportedException as error ->
            return Error(MalformedArtifact(path, $"the archive uses an unsupported ZIP encoding: {error.Message}"))
        | :? UnauthorizedAccessException as error ->
            return Error(FileFailure("observe", path, error.Message))
    }

// Public entry: observe one archive and surface a typed failure.
let observe (path: string) : Async<Result<ArchiveObservation, ReleaseError>> =
    observeArchive path

// Pre-checks the comparison enforces before any byte comparison. The
// allowlist comes from the producer contract so a configuration drift is
// caught at the gate.
let private precheck (contract: ComponentContract) (observation: ArchiveObservation) (label: string) : Result<unit, ReleaseError> =
    let actualNames = observation.Entries |> List.map _.Name |> List.sort
    let expectedNames = contract.EntryAllowlist |> List.sort

    if actualNames <> expectedNames then
        Error(
            AllowlistMismatch(
                $"{label} archive entry allowlist does not match producer contract",
                expectedNames,
                actualNames))
    else
        Ok()

// Parse the embedded manifest body captured during observation. A
// missing or malformed body fails closed so the comparison never falls
// back to a hash-only or unpack-and-repack representation.
let private parsedManifest (observation: ArchiveObservation) : Result<JsonObject, ReleaseError> =
    match observation.ManifestBody with
    | None -> Error(MissingArtifact embeddedManifestEntryName)
    | Some text ->
        try
            let document = JsonNode.Parse(text)
            if isNull document then
                Error(MalformedArtifact(embeddedManifestEntryName, "the document is empty"))
            else
                Ok(document.AsObject())
        with
        | :? JsonException as error ->
            Error(MalformedArtifact(embeddedManifestEntryName, $"the embedded manifest is not valid JSON: {error.Message}"))
        | :? FormatException as error ->
            Error(MalformedArtifact(embeddedManifestEntryName, $"the embedded manifest is not valid JSON: {error.Message}"))
        | :? InvalidOperationException as error ->
            Error(MalformedArtifact(embeddedManifestEntryName, $"the embedded manifest is not valid JSON: {error.Message}"))

// Read the `revision` field from a parsed manifest body, failing closed
// when the field is missing or unparseable. Carrying the result on
// `Decision.RevisionOnlyEquivalent` keeps the per-component summary
// grounded in the actual published revision.
let private readManifestRevision (label: string) (document: JsonObject) : Result<string, ReleaseError> =
    if not (document.ContainsKey "revision") then
        Error(MalformedArtifact(embeddedManifestEntryName, $"the {label} manifest is missing field 'revision'"))
    else
        try
            Ok(document["revision"].GetValue<string>())
        with
        | :? InvalidOperationException as error ->
            Error(MalformedArtifact(embeddedManifestEntryName, $"the {label} manifest 'revision' field could not be read: {error.Message}"))

// Compare the embedded manifest field-by-field, allowing only the
// `revision` field to differ. Every other declared field is checked
// exactly. A missing or unexpected field fails closed. Returns the
// published and candidate revisions when the comparison succeeds so
// the caller can surface them separately in the no-op report.
let private compareManifests
    (contract: ComponentContract)
    (published: JsonObject)
    (candidate: JsonObject)
    : Result<string * string, ReleaseError> =
    let required =
        [ "schemaVersion"; "id"; "version"; "revision"; "sdk"; "entryDll"; "archive"; "files"; "archiveFiles" ]

    let mutable failure : ReleaseError option = None

    let fail error = failure <- Some error
    let setFailure () = ()

    for field in required do
        match failure with
        | Some _ -> ()
        | None ->
            let publishedHas = published.ContainsKey field
            let candidateHas = candidate.ContainsKey field

            if not publishedHas then
                fail(MalformedArtifact(embeddedManifestEntryName, $"the published manifest is missing field '{field}'"))
            elif not candidateHas then
                fail(MalformedArtifact(embeddedManifestEntryName, $"the candidate manifest is missing field '{field}'"))
            elif field = "revision" then
                () // Strict field-by-field check below covers `revision` last so the typed surfaces the candidate value, not the published one.
            else
                let publishedValue = published.[field].ToJsonString()
                let candidateValue = candidate.[field].ToJsonString()

                if publishedValue <> candidateValue then
                    fail(IdentityMismatch(field, publishedValue, candidateValue))

    match failure with
    | Some error -> Error error
    | None ->
        let idNode = candidate["id"]

        if idNode.GetValue<string>() <> contract.ComponentId then
            Error(IdentityMismatch("id", contract.ComponentId, idNode.GetValue<string>()))
        else
            let versionNode = candidate["version"]

            if versionNode.GetValue<string>() <> contract.Version then
                Error(IdentityMismatch("version", contract.Version, versionNode.GetValue<string>()))
            else
                let archiveNode = candidate["archive"]

                if archiveNode.GetValue<string>() <> contract.ArchiveName then
                    Error(IdentityMismatch("archive", contract.ArchiveName, archiveNode.GetValue<string>()))
                else
                    let publishedRevisionResult = readManifestRevision "published" published
                    let candidateRevisionResult = readManifestRevision "candidate" candidate

                    match publishedRevisionResult, candidateRevisionResult with
                    | Error error, _ -> Error error
                    | _, Error error -> Error error
                    | Ok publishedRevision, Ok candidateRevision ->
                        if publishedRevision = candidateRevision then
                            Error(
                                MalformedArtifact(
                                    embeddedManifestEntryName,
                                    "the published and candidate manifests share the same revision, yet the archive bytes differ; entry-level comparison should have classified this as ByteIdentical"))
                        else
                            Ok(publishedRevision, candidateRevision)

// Compare non-manifest entries by CRC, length, compressed length,
// last-write timestamp, compression method, and payload SHA-256.
// Mismatches fail closed with the offending entry and field. The
// embedded manifest entry is not compared here; it is consumed by
// `compareManifests` instead because its CRC/length/compressed-length/
// payload-hash are derived from the body text and would change even on
// a strictly revision-only-equivalent archive.
let private compareNonManifestEntries
    (published: EntryObservation list)
    (candidate: EntryObservation list)
    : Result<unit, ReleaseError> =
    let publishedByName = published |> List.map (fun entry -> entry.Name, entry) |> Map.ofList
    let candidateByName = candidate |> List.map (fun entry -> entry.Name, entry) |> Map.ofList
    let mutable failure : ReleaseError option = None

    let fail error = failure <- Some error

    for name, publishedEntry in
        (publishedByName |> Map.toList |> List.sortBy fst) do
        match failure with
        | Some _ -> ()
        | None ->
            match Map.tryFind name candidateByName with
            | None ->
                fail(
                    MalformedArtifact(
                        "candidate archive",
                        $"the candidate archive is missing entry '{name}' that the published archive declared"))
            | Some candidateEntry ->
                if publishedEntry.Crc32 <> candidateEntry.Crc32 then
                    fail(
                        MalformedArtifact(
                            "candidate archive",
                            $"entry '{name}' CRC32 differs: published=0x{publishedEntry.Crc32:X8} candidate=0x{candidateEntry.Crc32:X8}"))
                elif publishedEntry.Length <> candidateEntry.Length then
                    fail(
                        MalformedArtifact(
                            "candidate archive",
                            $"entry '{name}' length differs: published={publishedEntry.Length} candidate={candidateEntry.Length}"))
                elif publishedEntry.CompressedLength <> candidateEntry.CompressedLength then
                    fail(
                        MalformedArtifact(
                            "candidate archive",
                            $"entry '{name}' compressed length differs: published={publishedEntry.CompressedLength} candidate={candidateEntry.CompressedLength}"))
                elif publishedEntry.LastWriteTime <> candidateEntry.LastWriteTime then
                    fail(
                        MalformedArtifact(
                            "candidate archive",
                            $"entry '{name}' last-write time differs: published={publishedEntry.LastWriteTime:O} candidate={candidateEntry.LastWriteTime:O}"))
                elif publishedEntry.CompressionMethod <> candidateEntry.CompressionMethod then
                    fail(
                        MalformedArtifact(
                            "candidate archive",
                            $"entry '{name}' compression method differs: published={publishedEntry.CompressionMethod} candidate={candidateEntry.CompressionMethod}"))
                elif publishedEntry.PayloadSha256 <> candidateEntry.PayloadSha256 then
                    fail(
                        MalformedArtifact(
                            "candidate archive",
                            $"entry '{name}' payload SHA-256 differs: published={publishedEntry.PayloadSha256} candidate={candidateEntry.PayloadSha256}"))

    match failure with
    | Some error -> Error error
    | None ->
        let publishedNames = published |> List.map _.Name |> Set.ofList
        let extraNames =
            candidate
            |> List.map _.Name
            |> List.filter (fun name -> not (Set.contains name publishedNames))

        if not extraNames.IsEmpty then
            Error(
                AllowlistMismatch(
                    "candidate archive introduced entries not in the published archive",
                    published |> List.map _.Name,
                    extraNames))
        else
            Ok()

// Split the published/candidate entry lists into the embedded manifest
// entry (if any) and the remaining entries. The manifest entry is
// handed to the manifest-body comparison path; the rest is handed to the
// non-manifest entry comparison path.
let private partitionManifestEntry (entries: EntryObservation list) : EntryObservation option * EntryObservation list =
    let manifest, others =
        entries
        |> List.partition (fun entry -> entry.Name = embeddedManifestEntryName)

    match manifest with
    | head :: _ -> Some head, others
    | [] -> None, others

// Top-level comparison. The published observation is required even when
// the published archive is byte-identical to the candidate; the caller
// always supplies it so the decision can report the published values
// without re-reading. The evaluated revision is the manifest's `revision`
// field on the candidate, supplied after the candidate manifest is
// validated against the committed clean HEAD by the caller.
let private compareInternal
    (contract: ComponentContract)
    (published: ArchiveObservation)
    (candidate: ArchiveObservation)
    (evaluatedRevision: string)
    : Result<Decision, ReleaseError> =
    match precheck contract published "published" with
    | Error error -> Error error
    | Ok() ->
        match precheck contract candidate "candidate" with
        | Error error -> Error error
        | Ok() ->
            // Step 1: byte-identity shortcut. Strict equality includes
            // the embedded revision string; the manifest comparison is
            // skipped because the archives are byte-identical.
            if published.ArchiveSha256 = candidate.ArchiveSha256 then
                Ok(ByteIdentical published)
            else
                let publishedManifestEntry, publishedOthers = partitionManifestEntry published.Entries
                let candidateManifestEntry, candidateOthers = partitionManifestEntry candidate.Entries

                match publishedManifestEntry, candidateManifestEntry with
                | None, _ ->
                    Error(
                        MalformedArtifact(
                            published.ArchivePath,
                            "the published archive is missing the canonical embedded manifest entry"))
                | _, None ->
                    Error(
                        MalformedArtifact(
                            candidate.ArchivePath,
                            "the candidate archive is missing the canonical embedded manifest entry"))
                | Some publishedEntry, Some candidateEntry ->
                    // Step 2: every non-manifest entry must match exactly.
                    // The manifest entry is compared via its body, not its
                    // header, because its CRC/length/payload-hash are derived
                    // from the body text.
                    match compareNonManifestEntries publishedOthers candidateOthers with
                    | Error reason ->
                        Ok(
                            DifferentBeyondRevision(
                                published,
                                candidate,
                                ReleaseError.message reason))
                    | Ok() ->
                        // The manifest entry's own CRC/length/payload-hash
                        // differences are derived from the manifest body
                        // changing. We check only the metadata that is
                        // independent of the payload content
                        // (`CompressionMethod` and `LastWriteTime`) and
                        // route the body comparison through
                        // `compareManifests` instead of treating the
                        // derived differences as a hard mismatch.
                        if publishedEntry.CompressionMethod <> candidateEntry.CompressionMethod then
                            Ok(
                                DifferentBeyondRevision(
                                    published,
                                    candidate,
                                    $"manifest entry compression method differs: published={publishedEntry.CompressionMethod} candidate={candidateEntry.CompressionMethod}"))
                        elif publishedEntry.LastWriteTime <> candidateEntry.LastWriteTime then
                            Ok(
                                DifferentBeyondRevision(
                                    published,
                                    candidate,
                                    $"manifest entry last-write time differs: published={publishedEntry.LastWriteTime:O} candidate={candidateEntry.LastWriteTime:O}"))
                        else
                            // Step 3: manifest-body comparison. Only the
                            // `revision` field is permitted to differ; every
                            // other declared field, including `sdk`,
                            // `entryDll`, `archive`, and the `files`
                            // hash table, must match exactly.
                            match parsedManifest published with
                            | Error error -> Error error
                            | Ok publishedManifest ->
                                match parsedManifest candidate with
                                | Error error -> Error error
                                | Ok candidateManifest ->
                                    match compareManifests contract publishedManifest candidateManifest with
                                    | Error reason ->
                                        Ok(
                                            DifferentBeyondRevision(
                                                published,
                                                candidate,
                                                ReleaseError.message reason))
                                    | Ok (publishedRevision, candidateRevision) ->
                                        // The evaluated revision comes from
                                        // the candidate manifest by contract
                                        // (the candidate was built from the
                                        // committed HEAD); assert it here so
                                        // a manifest revision that differs
                                        // from the caller's HEAD observation
                                        // is reported as a typed failure
                                        // rather than being silently
                                        // absorbed into the revision-only
                                        // exception.
                                        if candidateRevision <> evaluatedRevision then
                                            Error(
                                                RevisionMismatch(evaluatedRevision, candidateRevision))
                                        else
                                            Ok(
                                                RevisionOnlyEquivalent(
                                                    published,
                                                    publishedRevision,
                                                    evaluatedRevision))

// Async wrapper for the synchronous compare internal. Async return type
// keeps the call site consistent with the rest of the release flow.
let compare
    (contract: ComponentContract)
    (published: ArchiveObservation)
    (candidate: ArchiveObservation)
    (evaluatedRevision: string)
    : Async<Result<Decision, ReleaseError>> =
    async { return compareInternal contract published candidate evaluatedRevision }
