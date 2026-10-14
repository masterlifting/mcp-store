// role: reusable module/helper
// Strict archive comparison over the raw ZIP bytes; only the embedded `revision` value may differ.
// Unsupported or inconclusive layouts fail closed instead of being normalized or repacked.
// Exact equality is re-validated, not trusted: allowlist, manifest identity, and payload hashes are cross-checked.
module ReleaseCompare

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open BuildProvenance

// `Raw` is the exact archive entry the comparison normalizes against, so no local/central metadata is discarded.
type EntryObservation =
    { Name: string
      Raw: ReleaseArchive.Entry
      PayloadSha256: string }

// `ManifestBody`/`ManifestSha256` retain the published manifest bytes and hash; the revision it carries stays distinct from the evaluated HEAD.
type ArchiveObservation =
    { ArchivePath: string
      ArchiveSha256: string
      Entries: EntryObservation list
      ManifestBody: string option
      ManifestSha256: string option }

// Carries the published values so a no-op report exposes published hashes/revision rather than candidate ones.
type Decision =
    | ByteIdentical of published: ArchiveObservation
    | RevisionOnlyEquivalent of published: ArchiveObservation * publishedRevision: string * evaluatedRevision: string
    | DifferentBeyondRevision of published: ArchiveObservation * candidate: ArchiveObservation * reason: string

// Allowlist and archive name come from the producer's canonical Config.json; the comparison never re-implements them.
type ComponentContract =
    { ComponentId: string
      Version: string
      ArchiveName: string
      EntryAllowlist: string list }

// A future schema change must update this constant and the parser together.
let private embeddedManifestEntryName = "distribution.json"
let private noticeEntryName = "NOTICE.txt"
let private supportedSchemaVersion = 1

type private ParsedManifest =
    { SchemaVersion: int
      Id: string
      Version: string
      Archive: string
      Revision: string
      ArchiveFiles: string list
      Files: (string * string) list }

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private readArchiveBytes (path: string) : Result<byte[], ReleaseError> =
    try
        let info = FileInfo path

        if not info.Exists then
            Error(MissingArtifact path)
        elif info.Length > ReleaseArchive.maxArchiveBytes then
            Error(
                MalformedArtifact(
                    path,
                    $"the archive is {info.Length} bytes which exceeds the {ReleaseArchive.maxArchiveBytes}-byte bound"))
        else
            use stream = File.OpenRead path
            let length = int info.Length
            let buffer = Array.zeroCreate<byte> length
            let mutable offset = 0
            let mutable truncated = false

            while offset < length do
                let read = stream.Read(buffer, offset, length - offset)

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

let private tryProperty (element: JsonElement) (name: string) : JsonElement option =
    let mutable found = None

    for property in element.EnumerateObject() do
        if property.Name = name && found.IsNone then
            found <- Some property.Value

    found

// A duplicate key anywhere in the document is rejected so two parses cannot silently disagree.
let private rejectDuplicateKeys (root: JsonElement) : Result<unit, string> =
    let rec walk (element: JsonElement) : Result<unit, string> =
        match element.ValueKind with
        | JsonValueKind.Object ->
            let seen = HashSet<string>(StringComparer.Ordinal)
            let mutable failure = None

            for property in element.EnumerateObject() do
                if failure.IsNone then
                    if not (seen.Add property.Name) then
                        failure <- Some $"duplicate JSON key '{property.Name}'"
                    else
                        match walk property.Value with
                        | Error message -> failure <- Some message
                        | Ok () -> ()

            match failure with
            | Some message -> Error message
            | None -> Ok()
        | JsonValueKind.Array ->
            let mutable failure = None

            for item in element.EnumerateArray() do
                if failure.IsNone then
                    match walk item with
                    | Error message -> failure <- Some message
                    | Ok () -> ()

            match failure with
            | Some message -> Error message
            | None -> Ok()
        | _ -> Ok()

    walk root

let private readString (root: JsonElement) (name: string) : Result<string, ReleaseError> =
    match tryProperty root name with
    | None ->
        Error(MalformedArtifact(embeddedManifestEntryName, $"the manifest is missing required field '{name}'"))
    | Some value when value.ValueKind <> JsonValueKind.String ->
        Error(MalformedArtifact(embeddedManifestEntryName, $"the manifest field '{name}' is not a string"))
    | Some value -> Ok(value.GetString())

let private readInt (root: JsonElement) (name: string) : Result<int, ReleaseError> =
    match tryProperty root name with
    | None ->
        Error(MalformedArtifact(embeddedManifestEntryName, $"the manifest is missing required field '{name}'"))
    | Some value ->
        try
            Ok(value.GetInt32())
        with
        | :? InvalidOperationException ->
            Error(MalformedArtifact(embeddedManifestEntryName, $"the manifest field '{name}' is not an integer"))
        | :? FormatException ->
            Error(MalformedArtifact(embeddedManifestEntryName, $"the manifest field '{name}' is not an integer"))

// A validated top-level revision is the only manifest value allowed to change between equivalent archives.
let private isLowerHexRevision (value: string) : bool =
    value.Length = 40
    && value |> Seq.forall (fun character -> (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

let private parseManifest (bytes: byte[]) : Result<ParsedManifest, ReleaseError> =
    try
        use document = JsonDocument.Parse(ReadOnlyMemory bytes)
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error(MalformedArtifact(embeddedManifestEntryName, "the manifest is not a JSON object"))
        else
            match rejectDuplicateKeys root with
            | Error message -> Error(MalformedArtifact(embeddedManifestEntryName, message))
            | Ok () ->
                match readInt root "schemaVersion", readString root "id", readString root "version", readString root "archive", readString root "revision" with
                | Error error, _, _, _, _ -> Error error
                | _, Error error, _, _, _ -> Error error
                | _, _, Error error, _, _ -> Error error
                | _, _, _, Error error, _ -> Error error
                | _, _, _, _, Error error -> Error error
                | Ok schemaVersion, Ok id, Ok version, Ok archive, Ok revision ->
                    if not (isLowerHexRevision revision) then
                        Error(
                            MalformedArtifact(
                                embeddedManifestEntryName,
                                "the manifest 'revision' is not a 40-character lowercase hex revision"))
                    else
                        match tryProperty root "archiveFiles", tryProperty root "files" with
                        | Some archiveFilesElement, Some filesElement
                            when archiveFilesElement.ValueKind = JsonValueKind.Array
                                 && filesElement.ValueKind = JsonValueKind.Array ->
                            let archiveFiles =
                                archiveFilesElement.EnumerateArray()
                                |> Seq.choose (fun item ->
                                    if item.ValueKind = JsonValueKind.String then Some(item.GetString()) else None)
                                |> Seq.toList

                            if archiveFiles.Length <> archiveFilesElement.GetArrayLength() then
                                Error(
                                    MalformedArtifact(
                                        embeddedManifestEntryName,
                                        "the manifest 'archiveFiles' must contain only strings"))
                            else
                                let mutable files = []
                                let mutable failure = None

                                for item in filesElement.EnumerateArray() do
                                    if failure.IsNone then
                                        if item.ValueKind <> JsonValueKind.Object then
                                            failure <- Some "the manifest 'files' entries must be JSON objects"
                                        else
                                            match tryProperty item "path", tryProperty item "sha256" with
                                            | Some pathElement, Some hashElement
                                                when pathElement.ValueKind = JsonValueKind.String
                                                     && hashElement.ValueKind = JsonValueKind.String ->
                                                files <- (pathElement.GetString(), hashElement.GetString()) :: files
                                            | _ ->
                                                failure <- Some "each manifest 'files' entry needs string 'path' and 'sha256'"

                                match failure with
                                | Some message -> Error(MalformedArtifact(embeddedManifestEntryName, message))
                                | None ->
                                    Ok
                                        { SchemaVersion = schemaVersion
                                          Id = id
                                          Version = version
                                          Archive = archive
                                          Revision = revision
                                          ArchiveFiles = archiveFiles
                                          Files = List.rev files }
                        | _ ->
                            Error(
                                MalformedArtifact(
                                    embeddedManifestEntryName,
                                    "the manifest is missing its 'archiveFiles' or 'files' array"))
    with
    | :? JsonException as error ->
        Error(MalformedArtifact(embeddedManifestEntryName, $"the embedded manifest is not valid JSON: {error.Message}"))

// Cross-checks the manifest against the actual payload bytes so declared hashes are never trusted on their own.
let private selfConsistency (observation: ArchiveObservation) : Result<unit, ReleaseError> =
    match observation.ManifestBody with
    | None -> Error(MalformedArtifact(observation.ArchivePath, "the canonical embedded manifest is missing"))
    | Some body ->
        match parseManifest (Encoding.UTF8.GetBytes body) with
        | Error error -> Error error
        | Ok manifest ->
            if manifest.SchemaVersion <> supportedSchemaVersion then
                Error(
                    MalformedArtifact(
                        embeddedManifestEntryName,
                        $"unsupported manifest schemaVersion {manifest.SchemaVersion}; expected {supportedSchemaVersion}"))
            else
                let actualNames = observation.Entries |> List.map _.Name
                let actualSorted = actualNames |> List.sort
                let archiveFilesSorted = manifest.ArchiveFiles |> List.sort

                if archiveFilesSorted <> actualSorted then
                    Error(
                        AllowlistMismatch(
                            "the manifest 'archiveFiles' does not match the archive entries",
                            actualSorted,
                            archiveFilesSorted))
                else
                    let byName = observation.Entries |> List.map (fun entry -> entry.Name, entry) |> Map.ofList
                    let mutable failure : string option = None

                    for path, declaredHash in manifest.Files do
                        if failure.IsNone then
                            match Map.tryFind path byName with
                            | None ->
                                failure <- Some $"the manifest declares payload '{path}' that is absent from the archive"
                            | Some entry when entry.PayloadSha256 <> declaredHash ->
                                failure <- Some $"the manifest hash for '{path}' does not match the archived payload"
                            | Some _ -> ()

                    match failure with
                    | Some message -> Error(MalformedArtifact(embeddedManifestEntryName, message))
                    | None ->
                        let declaredPaths = manifest.Files |> List.map fst |> Set.ofList

                        let expectedPaths =
                            actualNames
                            |> List.filter (fun name -> name <> embeddedManifestEntryName && name <> noticeEntryName)
                            |> Set.ofList

                        if declaredPaths <> expectedPaths then
                            Error(
                                MalformedArtifact(
                                    embeddedManifestEntryName,
                                    "the manifest 'files' entries do not cover exactly the archived payload files"))
                        else
                            Ok()

// Shared validation pipeline for both path-based and byte-based observations:
// extracts entries, computes payload sha256, parses the embedded manifest, and
// verifies self-consistency. The single source of validation truth lives here
// so the path-based and byte-based public entries share the exact same checks.
let private validateParsedArchive
    (parsed: ReleaseArchive.Archive)
    (path: string)
    (bytes: byte[])
    : Result<ArchiveObservation, ReleaseError> =
    let entries = ResizeArray<EntryObservation>()
    let mutable failure : ReleaseError option = None
    let mutable manifestBody : string option = None
    let mutable manifestSha256 : string option = None
    let mutable manifestCount = 0

    for raw in parsed.Entries do
        match failure with
        | Some _ -> ()
        | None ->
            match ReleaseArchive.decompress raw with
            | Error error -> failure <- Some error
            | Ok payload ->
                let payloadSha256 = sha256Hex payload

                if raw.Name = embeddedManifestEntryName then
                    manifestCount <- manifestCount + 1
                    manifestBody <- Some(Encoding.UTF8.GetString payload)
                    manifestSha256 <- Some payloadSha256

                entries.Add(
                    { Name = raw.Name
                      Raw = raw
                      PayloadSha256 = payloadSha256 }
                )

    match failure with
    | Some error -> Error error
    | None ->
        if manifestCount <> 1 then
            Error(
                MalformedArtifact(
                    path,
                    $"the archive must contain exactly one '{embeddedManifestEntryName}' entry"))
        else
            let observation =
                { ArchivePath = path
                  ArchiveSha256 = sha256Hex bytes
                  Entries = List.ofSeq entries
                  ManifestBody = manifestBody
                  ManifestSha256 = manifestSha256 }

            match selfConsistency observation with
            | Error error -> Error error
            | Ok () -> Ok observation

// Public entry: observe and fully validate one archive, surfacing a typed failure.
let observe (path: string) : Async<Result<ArchiveObservation, ReleaseError>> =
    async {
        match readArchiveBytes path with
        | Error error -> return Error error
        | Ok bytes ->
            match ReleaseArchive.parse path bytes with
            | Error error -> return Error error
            | Ok parsed -> return validateParsedArchive parsed path bytes
    }

// Byte-safe variant: the publisher has already read and verified the bytes;
// this entrypoint re-uses the exact same validation pipeline without ever
// touching the filesystem.
let observeBytes (path: string) (bytes: byte[]) : Result<ArchiveObservation, ReleaseError> =
    match ReleaseArchive.parse path bytes with
    | Error error -> Error error
    | Ok parsed -> validateParsedArchive parsed path bytes

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

// Usable before any remote publication: validates one observation's manifest identity and payload hashes against the contract.
let validate (contract: ComponentContract) (observation: ArchiveObservation) (label: string) : Result<unit, ReleaseError> =
    match precheck contract observation label with
    | Error error -> Error error
    | Ok () ->
        match observation.ManifestBody with
        | None -> Error(MalformedArtifact(observation.ArchivePath, "the canonical embedded manifest is missing"))
        | Some body ->
            match parseManifest (Encoding.UTF8.GetBytes body) with
            | Error error -> Error error
            | Ok manifest ->
                if manifest.SchemaVersion <> supportedSchemaVersion then
                    Error(
                        MalformedArtifact(
                            embeddedManifestEntryName,
                            $"unsupported manifest schemaVersion {manifest.SchemaVersion}; expected {supportedSchemaVersion}"))
                elif manifest.Id <> contract.ComponentId then
                    Error(IdentityMismatch("id", contract.ComponentId, manifest.Id))
                elif manifest.Version <> contract.Version then
                    Error(IdentityMismatch("version", contract.Version, manifest.Version))
                elif manifest.Archive <> contract.ArchiveName then
                    Error(IdentityMismatch("archive", contract.ArchiveName, manifest.Archive))
                else
                    Ok()

// Excludes the derived local-header offset; every stored byte, including the compressed payload, must match.
let private compareRawEntries (published: ReleaseArchive.Entry) (candidate: ReleaseArchive.Entry) : string option =
    if published.NameBytes <> candidate.NameBytes then
        Some $"entry '{published.Name}' name bytes differ"
    elif published.VersionMadeBy <> candidate.VersionMadeBy || published.VersionNeeded <> candidate.VersionNeeded then
        Some $"entry '{published.Name}' version metadata differs"
    elif published.Flags <> candidate.Flags then
        Some $"entry '{published.Name}' flags differ: published=0x{published.Flags:X4} candidate=0x{candidate.Flags:X4}"
    elif published.Method <> candidate.Method then
        Some $"entry '{published.Name}' compression method differs: published={published.Method} candidate={candidate.Method}"
    elif published.ModTime <> candidate.ModTime || published.ModDate <> candidate.ModDate then
        Some $"entry '{published.Name}' timestamp differs"
    elif published.Crc32 <> candidate.Crc32 then
        Some $"entry '{published.Name}' CRC32 differs: published=0x{published.Crc32:X8} candidate=0x{candidate.Crc32:X8}"
    elif published.CompressedSize <> candidate.CompressedSize then
        Some $"entry '{published.Name}' compressed size differs: published={published.CompressedSize} candidate={candidate.CompressedSize}"
    elif published.UncompressedSize <> candidate.UncompressedSize then
        Some $"entry '{published.Name}' uncompressed size differs: published={published.UncompressedSize} candidate={candidate.UncompressedSize}"
    elif published.LocalExtra <> candidate.LocalExtra || published.CentralExtra <> candidate.CentralExtra then
        Some $"entry '{published.Name}' extra field differs"
    elif published.Comment <> candidate.Comment then
        Some $"entry '{published.Name}' comment differs"
    elif published.DiskStart <> candidate.DiskStart then
        Some $"entry '{published.Name}' disk start differs"
    elif published.InternalAttributes <> candidate.InternalAttributes
         || published.ExternalAttributes <> candidate.ExternalAttributes then
        Some $"entry '{published.Name}' attributes differ"
    elif published.CompressedData <> candidate.CompressedData then
        Some $"entry '{published.Name}' compressed payload bytes differ"
    else
        None

let private compareNonManifestEntries (published: EntryObservation list) (candidate: EntryObservation list) : string option =
    if List.length published <> List.length candidate then
        Some "the candidate archive has a different number of non-manifest entries"
    else
        List.zip published candidate
        |> List.tryPick (fun (publishedEntry, candidateEntry) ->
            if publishedEntry.Name <> candidateEntry.Name then
                Some $"non-manifest entry order differs: published='{publishedEntry.Name}' candidate='{candidateEntry.Name}'"
            else
                match compareRawEntries publishedEntry.Raw candidateEntry.Raw with
                | Some reason -> Some reason
                | None ->
                    if publishedEntry.PayloadSha256 <> candidateEntry.PayloadSha256 then
                        Some $"entry '{publishedEntry.Name}' payload SHA-256 differs"
                    else
                        None)

// CRC, sizes, and the header offset are derived from the body; every other manifest-entry field must match.
let private compareManifestMetadata (published: EntryObservation) (candidate: EntryObservation) : string option =
    let publishedRaw = published.Raw
    let candidateRaw = candidate.Raw

    if publishedRaw.NameBytes <> candidateRaw.NameBytes then
        Some "manifest entry name differs"
    elif publishedRaw.VersionMadeBy <> candidateRaw.VersionMadeBy
         || publishedRaw.VersionNeeded <> candidateRaw.VersionNeeded then
        Some "manifest entry version metadata differs"
    elif publishedRaw.Flags <> candidateRaw.Flags then
        Some $"manifest entry flags differ: published=0x{publishedRaw.Flags:X4} candidate=0x{candidateRaw.Flags:X4}"
    elif publishedRaw.Method <> candidateRaw.Method then
        Some $"manifest entry compression method differs: published={publishedRaw.Method} candidate={candidateRaw.Method}"
    elif publishedRaw.ModTime <> candidateRaw.ModTime || publishedRaw.ModDate <> candidateRaw.ModDate then
        Some "manifest entry timestamp differs"
    elif publishedRaw.LocalExtra <> candidateRaw.LocalExtra || publishedRaw.CentralExtra <> candidateRaw.CentralExtra then
        Some "manifest entry extra field differs"
    elif publishedRaw.Comment <> candidateRaw.Comment then
        Some "manifest entry comment differs"
    elif publishedRaw.DiskStart <> candidateRaw.DiskStart then
        Some "manifest entry disk start differs"
    elif publishedRaw.InternalAttributes <> candidateRaw.InternalAttributes
         || publishedRaw.ExternalAttributes <> candidateRaw.ExternalAttributes then
        Some "manifest entry attributes differ"
    else
        None

// Re-proves that the stored manifest bytes are the canonical Deflate(Optimal) encoding, so a compressed difference is body-derived.
let private confirmCanonicalDeflate (entry: ReleaseArchive.Entry) (payload: byte[]) : Result<unit, ReleaseError> =
    use output = new MemoryStream()

    do
        use deflate = new DeflateStream(output, CompressionLevel.Optimal, true)
        deflate.Write(payload, 0, payload.Length)

    if output.ToArray() <> entry.CompressedData then
        Error(
            MalformedArtifact(
                embeddedManifestEntryName,
                "the embedded manifest compressed bytes are not the canonical Deflate representation for their content"))
    else
        Ok()

type private BodyComparison =
    | BodyEquivalent of publishedRevision: string * candidateRevision: string
    | BodyDiffers of reason: string

let private compareManifestBodies (publishedBytes: byte[]) (candidateBytes: byte[]) : Result<BodyComparison, ReleaseError> =
    match parseManifest publishedBytes, parseManifest candidateBytes with
    | Error error, _ -> Error error
    | _, Error error -> Error error
    | Ok published, Ok candidate ->
        let publishedText = Encoding.UTF8.GetString publishedBytes
        let candidateText = Encoding.UTF8.GetString candidateBytes

        let countOccurrences (needle: string) (haystack: string) =
            let mutable count = 0
            let mutable index = haystack.IndexOf(needle, StringComparison.Ordinal)

            while index >= 0 do
                count <- count + 1
                index <- haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)

            count

        if countOccurrences published.Revision publishedText <> 1 then
            Error(
                MalformedArtifact(
                    embeddedManifestEntryName,
                    "the published manifest revision value is not unique in its document"))
        elif countOccurrences candidate.Revision candidateText <> 1 then
            Error(
                MalformedArtifact(
                    embeddedManifestEntryName,
                    "the candidate manifest revision value is not unique in its document"))
        elif published.Revision = candidate.Revision then
            Ok(BodyDiffers "the published and candidate manifests share the same revision yet the archive bytes differ")
        elif publishedText.Replace(published.Revision, candidate.Revision) <> candidateText then
            // Byte substitution makes the revision the only permitted difference, covering every other manifest field.
            Ok(BodyDiffers "the manifests differ in more than the embedded revision value")
        else
            Ok(BodyEquivalent(published.Revision, candidate.Revision))

let private partitionManifestEntry (entries: EntryObservation list) : EntryObservation option * EntryObservation list =
    let manifest, others =
        entries |> List.partition (fun entry -> entry.Name = embeddedManifestEntryName)

    match manifest with
    | head :: _ -> Some head, others
    | [] -> None, others

let private compareInternal
    (contract: ComponentContract)
    (published: ArchiveObservation)
    (candidate: ArchiveObservation)
    (evaluatedRevision: string)
    : Result<Decision, ReleaseError> =
    match validate contract published "published" with
    | Error error -> Error error
    | Ok () ->
        match validate contract candidate "candidate" with
        | Error error -> Error error
        | Ok () ->
            // Byte-identity still passes validation above; it is not a shortcut past malformed input.
            if published.ArchiveSha256 = candidate.ArchiveSha256 then
                Ok(ByteIdentical published)
            else
                let publishedManifest, publishedOthers = partitionManifestEntry published.Entries
                let candidateManifest, candidateOthers = partitionManifestEntry candidate.Entries

                let differs reason =
                    Ok(DifferentBeyondRevision(published, candidate, reason))

                match publishedManifest, candidateManifest with
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
                    match compareNonManifestEntries publishedOthers candidateOthers with
                    | Some reason -> differs reason
                    | None ->
                        match compareManifestMetadata publishedEntry candidateEntry with
                        | Some reason -> differs reason
                        | None ->
                            match ReleaseArchive.decompress publishedEntry.Raw, ReleaseArchive.decompress candidateEntry.Raw with
                            | Error error, _ -> Error error
                            | _, Error error -> Error error
                            | Ok publishedBytes, Ok candidateBytes ->
                                match confirmCanonicalDeflate publishedEntry.Raw publishedBytes with
                                | Error error -> Error error
                                | Ok () ->
                                    match confirmCanonicalDeflate candidateEntry.Raw candidateBytes with
                                    | Error error -> Error error
                                    | Ok () ->
                                        match compareManifestBodies publishedBytes candidateBytes with
                                        | Error error -> Error error
                                        | Ok (BodyDiffers reason) -> differs reason
                                        | Ok (BodyEquivalent (publishedRevision, candidateRevision)) ->
                                            if candidateRevision <> evaluatedRevision then
                                                Error(RevisionMismatch(evaluatedRevision, candidateRevision))
                                            else
                                                Ok(
                                                    RevisionOnlyEquivalent(
                                                        published,
                                                        publishedRevision,
                                                        evaluatedRevision))

let compare
    (contract: ComponentContract)
    (published: ArchiveObservation)
    (candidate: ArchiveObservation)
    (evaluatedRevision: string)
    : Async<Result<Decision, ReleaseError>> =
    async { return compareInternal contract published candidate evaluatedRevision }

// Stable public header for the embedded distribution manifest; avoids leaking the private `ParsedManifest` shape.
type EmbeddedManifestHeader =
    { SchemaVersion: int
      Id: string
      Version: string
      Archive: string
      Revision: string
      ArchiveFiles: string list
      FileCount: int }

let parseEmbeddedManifestHeader (body: string) : Result<EmbeddedManifestHeader, ReleaseError> =
    match parseManifest (Encoding.UTF8.GetBytes body) with
    | Error error -> Error error
    | Ok parsed ->
        Ok
            { SchemaVersion = parsed.SchemaVersion
              Id = parsed.Id
              Version = parsed.Version
              Archive = parsed.Archive
              Revision = parsed.Revision
              ArchiveFiles = parsed.ArchiveFiles
              FileCount = parsed.Files.Length }

let parseEmbeddedManifestEntry
    (observation: ArchiveObservation)
    : Result<EmbeddedManifestHeader, ReleaseError> =
    match observation.ManifestBody with
    | None -> Error(MalformedArtifact(observation.ArchivePath, "the canonical embedded manifest entry is missing"))
    | Some body -> parseEmbeddedManifestHeader body
