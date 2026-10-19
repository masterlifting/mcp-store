// role: reusable module/helper
// Strict archive comparison over the raw ZIP bytes; only the embedded `revision` value may differ.
// Unsupported or inconclusive layouts fail closed instead of being normalized or repacked.
// Exact equality is re-validated, not trusted: allowlist, manifest identity, declared member
// hashes, launch/runtime metadata, and payload hashes are cross-checked against the trusted
// producer contract and the archived runtime configuration.
module ReleaseCompare

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open SharedFailure
open BuildProvenance
open DistributionContract

// `Raw` is the exact archive entry the comparison normalizes against, so no local/central metadata is discarded.
type EntryObservation =
    { Name: string
      Raw: DistributionArchive.Entry
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

// Identity, member allowlist, launch descriptor, source repository, and build
// provenance come from the producer's canonical Config.json; the comparison never
// re-implements them.
type ComponentContract =
    { ComponentId: string
      Version: string
      ArchiveName: string
      SourceRepository: string
      Producer: ProducerProvenance
      Launch: LaunchDescriptor
      EntryAllowlist: string list }

// Derives the trusted contract from one producer's canonical policy so identity,
// member allowlist, source repository, launch descriptor, and build provenance are
// never re-implemented at a comparison call site.
let contractFromPolicy
    (policy: ReleasePolicy.PolicyProjection)
    (archiveName: string)
    : Result<ComponentContract, ReleaseError> =
    sourceRepositoryFromAssetUri policy.AssetUri
    |> Result.map (fun repository ->
        { ComponentId = policy.ComponentId
          Version = policy.Version
          ArchiveName = archiveName
          SourceRepository = repository
          Producer =
            { Sdk = policy.SdkVersion
              Runtime = policy.RuntimeVersion
              Platform = policy.Platform }
          Launch =
            { Kind = launchKindDotnetExec
              Entrypoint = policy.EntryDll
              Arguments = policy.LaunchArguments }
          EntryAllowlist = trustedArchiveMemberNames policy.PublishedFiles })

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private readArchiveBytes (path: string) : Result<byte[], ReleaseError> =
    try
        let info = FileInfo path

        if not info.Exists then
            Error(MissingArtifact path)
        elif info.Length > DistributionArchive.maxArchiveBytes then
            Error(
                MalformedArtifact(
                    path,
                    $"the archive is {info.Length} bytes which exceeds the {DistributionArchive.maxArchiveBytes}-byte bound"))
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

let private renderProducer (producer: ProducerProvenance) : string =
    $"{producer.Sdk} {producer.Runtime} {producer.Platform}"

let private renderLaunch (launch: LaunchDescriptor) : string =
    let arguments =
        launch.Arguments
        |> List.map (fun argument -> $"{argument.Flag} {argument.Input} {LaunchArgumentType.wireName argument.Type}")
        |> String.concat "; "

    $"{launch.Kind} {launch.Entrypoint} [{arguments}]"

// The declared runtime prerequisite must match the packaged runtime configuration
// bytes that `files` hashes, so the manifest never states an unverified runtime.
let private validateRuntimeConfiguration
    (manifest: DistributionManifest)
    (observation: ArchiveObservation)
    : Result<unit, ReleaseError> =
    match observation.Entries |> List.tryFind (fun entry -> entry.Name = manifest.Runtime.Config) with
    | None ->
        Error(
            MalformedArtifact(
                manifestEntryName,
                $"the manifest 'runtime.config' '{manifest.Runtime.Config}' is absent from the archive"))
    | Some entry ->
        match DistributionArchive.decompress entry.Raw with
        | Error error -> Error error
        | Ok payload ->
            match runtimeRequirementFromConfig manifest.Runtime.Config payload with
            | Error error -> Error error
            | Ok derived when derived = manifest.Runtime -> Ok()
            | Ok derived ->
                Error(
                    IdentityMismatch(
                        "runtime",
                        $"{manifest.Runtime.Framework} {manifest.Runtime.Version}",
                        $"{derived.Framework} {derived.Version}"))

// Cross-checks the manifest against the actual payload bytes so declared hashes and
// the declared runtime are never trusted on their own.
let private selfConsistency (observation: ArchiveObservation) : Result<unit, ReleaseError> =
    match observation.ManifestBody with
    | None -> Error(MalformedArtifact(observation.ArchivePath, "the canonical embedded manifest is missing"))
    | Some body ->
        match parseBytes (Encoding.UTF8.GetBytes body) with
        | Error error -> Error error
        | Ok manifest ->
            let actualSorted = observation.Entries |> List.map _.Name |> List.sort
            let expectedSorted = archiveMemberNames manifest

            if expectedSorted <> actualSorted then
                Error(
                    AllowlistMismatch(
                        "the manifest 'files' entries do not cover exactly the archive members",
                        expectedSorted,
                        actualSorted))
            else
                let byName = observation.Entries |> List.map (fun entry -> entry.Name, entry) |> Map.ofList
                let mutable failure: string option = None

                for file in manifest.Files do
                    if failure.IsNone then
                        match Map.tryFind file.Path byName with
                        | None ->
                            failure <- Some $"the manifest declares payload '{file.Path}' that is absent from the archive"
                        | Some entry when entry.PayloadSha256 <> file.Sha256 ->
                            failure <- Some $"the manifest hash for '{file.Path}' does not match the archived payload"
                        | Some _ -> ()

                match failure with
                | Some message -> Error(MalformedArtifact(manifestEntryName, message))
                | None -> validateRuntimeConfiguration manifest observation

// Shared validation pipeline for both path-based and byte-based observations:
// extracts entries, computes payload sha256, parses the embedded manifest, and
// verifies self-consistency. The single source of validation truth lives here
// so the path-based and byte-based public entries share the exact same checks.
let private validateParsedArchive
    (parsed: DistributionArchive.Archive)
    (path: string)
    (bytes: byte[])
    : Result<ArchiveObservation, ReleaseError> =
    let entries = ResizeArray<EntryObservation>()
    let mutable failure: ReleaseError option = None
    let mutable manifestBody: string option = None
    let mutable manifestSha256: string option = None
    let mutable manifestCount = 0

    for raw in parsed.Entries do
        match failure with
        | Some _ -> ()
        | None ->
            match DistributionArchive.decompress raw with
            | Error error -> failure <- Some error
            | Ok payload ->
                let payloadSha256 = sha256Hex payload

                if raw.Name = manifestEntryName then
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
                    $"the archive must contain exactly one '{manifestEntryName}' entry"))
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
            match DistributionArchive.parse path bytes with
            | Error error -> return Error error
            | Ok parsed -> return validateParsedArchive parsed path bytes
    }

// Byte-safe variant: the publisher has already read and verified the bytes;
// this entrypoint re-uses the exact same validation pipeline without ever
// touching the filesystem.
let observeBytes (path: string) (bytes: byte[]) : Result<ArchiveObservation, ReleaseError> =
    match DistributionArchive.parse path bytes with
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

// Usable before any remote publication: validates one observation's manifest identity,
// member hashes, and launch/runtime/provenance metadata against the trusted contract.
let validate (contract: ComponentContract) (observation: ArchiveObservation) (label: string) : Result<unit, ReleaseError> =
    match precheck contract observation label with
    | Error error -> Error error
    | Ok () ->
        match observation.ManifestBody with
        | None -> Error(MalformedArtifact(observation.ArchivePath, "the canonical embedded manifest is missing"))
        | Some body ->
            match parseBytes (Encoding.UTF8.GetBytes body) with
            | Error error -> Error error
            | Ok manifest ->
                if manifest.Id <> contract.ComponentId then
                    Error(IdentityMismatch("id", contract.ComponentId, manifest.Id))
                elif manifest.Version <> contract.Version then
                    Error(IdentityMismatch("version", contract.Version, manifest.Version))
                elif manifest.Archive <> contract.ArchiveName then
                    Error(IdentityMismatch("archive", contract.ArchiveName, manifest.Archive))
                elif manifest.SourceRepository <> contract.SourceRepository then
                    Error(IdentityMismatch("sourceRepository", contract.SourceRepository, manifest.SourceRepository))
                elif manifest.Producer <> contract.Producer then
                    Error(IdentityMismatch("producer", renderProducer contract.Producer, renderProducer manifest.Producer))
                elif manifest.Launch <> contract.Launch then
                    Error(IdentityMismatch("launch", renderLaunch contract.Launch, renderLaunch manifest.Launch))
                else
                    Ok()

// Excludes the derived local-header offset; every stored byte, including the compressed payload, must match.
let private compareRawEntries (published: DistributionArchive.Entry) (candidate: DistributionArchive.Entry) : string option =
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
let private confirmCanonicalDeflate (entry: DistributionArchive.Entry) (payload: byte[]) : Result<unit, ReleaseError> =
    use output = new MemoryStream()

    do
        use deflate = new DeflateStream(output, CompressionLevel.Optimal, true)
        deflate.Write(payload, 0, payload.Length)

    if output.ToArray() <> entry.CompressedData then
        Error(
            MalformedArtifact(
                manifestEntryName,
                "the embedded manifest compressed bytes are not the canonical Deflate representation for their content"))
    else
        Ok()

type private BodyComparison =
    | BodyEquivalent of publishedRevision: string * candidateRevision: string
    | BodyDiffers of reason: string

let private compareManifestBodies (publishedBytes: byte[]) (candidateBytes: byte[]) : Result<BodyComparison, ReleaseError> =
    match parseBytes publishedBytes, parseBytes candidateBytes with
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
                    manifestEntryName,
                    "the published manifest revision value is not unique in its document"))
        elif countOccurrences candidate.Revision candidateText <> 1 then
            Error(
                MalformedArtifact(
                    manifestEntryName,
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
        entries |> List.partition (fun entry -> entry.Name = manifestEntryName)

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
                            match DistributionArchive.decompress publishedEntry.Raw, DistributionArchive.decompress candidateEntry.Raw with
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

// Stable public accessors for the embedded distribution manifest.
let parseEmbeddedManifest (body: string) : Result<DistributionManifest, ReleaseError> =
    parse body

let parseEmbeddedManifestEntry
    (observation: ArchiveObservation)
    : Result<DistributionManifest, ReleaseError> =
    match observation.ManifestBody with
    | None -> Error(MalformedArtifact(observation.ArchivePath, "the canonical embedded manifest entry is missing"))
    | Some body -> parse body
