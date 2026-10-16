// role: reusable module/helper
module ReleaseHandoff

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open BuildProvenance

let private maxArchiveBytes = 256L * 1024L * 1024L
let private maxEntries = 4096

let private sha256 (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

type CandidateArchiveSnapshot internal (archiveBytes: byte[], manifestBytes: byte[], sourcePath: string, sourceSha256: string) =
    member _.ArchiveBytesCopy() = Array.copy archiveBytes
    member _.ManifestBytesCopy() = Array.copy manifestBytes
    member _.SourcePath = sourcePath
    member _.SourceSha256 = sourceSha256

let private readBounded (stream: Stream) (name: string) =
    try
        use output = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 81920
        let mutable total = 0L
        let mutable doneReading = false
        let mutable tooLarge = false
        while not doneReading && not tooLarge do
            let count = stream.Read(buffer, 0, buffer.Length)
            if count = 0 then doneReading <- true
            else
                total <- total + int64 count
                if total > maxArchiveBytes then tooLarge <- true
                else output.Write(buffer, 0, count)
        if tooLarge then Error(MalformedArtifact(name, "archive member exceeds the bounded read"))
        else Ok(output.ToArray())
    with
    | :? IOException as error -> Error(FileFailure("read", name, error.Message))

let private readFileBounded (path: string) =
    try
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        readBounded stream path
    with
    | :? IOException as error -> Error(FileFailure("read", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))

let private pathSafe (name: string) =
    DistributionContract.isSafeRelativePath name

let readCandidate (candidatePath: string) (policy: ReleasePolicy.PolicyProjection) (eventSha: string) : Result<CandidateArchiveSnapshot, ReleaseError> =
    try
        let canonicalPath = Path.GetFullPath candidatePath
        let archivePath = Path.Combine(canonicalPath, policy.ArchiveName)
        let manifestPath = Path.Combine(canonicalPath, DistributionContract.manifestEntryName)
        let info = FileInfo archivePath
        let candidateRootInfo = DirectoryInfo canonicalPath
        let candidateEntries = if candidateRootInfo.Exists then Directory.EnumerateFileSystemEntries canonicalPath |> Seq.map Path.GetFileName |> Set.ofSeq else Set.empty
        let unsafeCandidatePath =
            let mutable current = candidateRootInfo
            let mutable unsafePath = false
            while not (isNull current) && not unsafePath do
                if current.Exists && (current.Attributes &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0 then unsafePath <- true
                current <- current.Parent
            unsafePath
        let unsafeCandidateMember =
            if not candidateRootInfo.Exists then false
            else
                Directory.EnumerateFileSystemEntries canonicalPath
                |> Seq.exists (fun path -> (File.GetAttributes path &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0)
        if unsafeCandidatePath || unsafeCandidateMember then Error(MalformedArtifact(canonicalPath, "candidate directory or a member traverses a reparse point"))
        elif candidateEntries <> Set.ofList [ policy.ArchiveName; DistributionContract.manifestEntryName ] then Error(AllowlistMismatch($"candidate directory must contain exactly the producer archive and {DistributionContract.manifestEntryName}", [ policy.ArchiveName; DistributionContract.manifestEntryName ], Set.toList candidateEntries))
        elif not info.Exists then Error(MissingArtifact canonicalPath)
        elif info.Length > maxArchiveBytes then Error(MalformedArtifact(canonicalPath, "candidate archive exceeds the bounded read"))
        else
            use candidateStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read)
            match readBounded candidateStream canonicalPath with
            | Error error -> Error error
            | Ok bytes ->
                // Keep the standalone manifest as a separate bounded filesystem read; the archive bytes are the producer ZIP itself.
                match readFileBounded manifestPath with
                | Error error -> Error error
                | Ok manifestBytes ->
                    use input = new MemoryStream(bytes, false)
                    use archive = new ZipArchive(input, ZipArchiveMode.Read, false)
                    if archive.Entries.Count > maxEntries then Error(MalformedArtifact(canonicalPath, "candidate archive has too many entries"))
                    else
                        let names = archive.Entries |> Seq.map (fun entry -> entry.FullName) |> Set.ofSeq
                        let duplicateNames = archive.Entries.Count <> names.Count
                        let totalUncompressed = archive.Entries |> Seq.sumBy (fun entry -> max 0L entry.Length)
                        let unsafeEntry = archive.Entries |> Seq.tryFind (fun entry -> not (pathSafe entry.FullName) || entry.Length < 0L || entry.Length > maxArchiveBytes || (entry.CompressedLength > 0L && entry.Length > entry.CompressedLength * 200L) || ((entry.ExternalAttributes >>> 16) &&& 0xF000) = 0xA000 || (entry.ExternalAttributes &&& 0x400) <> 0)
                        if duplicateNames then Error(MalformedArtifact(canonicalPath, "candidate archive contains duplicate entry names"))
                        elif totalUncompressed > maxArchiveBytes then Error(MalformedArtifact(canonicalPath, "candidate archive declares excessive total uncompressed data"))
                        elif unsafeEntry.IsSome then Error(MalformedArtifact(canonicalPath, "candidate archive contains an unsafe, linked, or oversized member"))
                        else
                            let readMember name =
                                match archive.GetEntry name with
                                | null -> Error(MissingArtifact name)
                                | entry -> use stream = entry.Open() in readBounded stream name
                            match readMember DistributionContract.manifestEntryName with
                            | Error error ->
                                match error with
                                | MissingArtifact name -> Error(MalformedArtifact(name, $"candidate producer archive is missing {DistributionContract.manifestEntryName}"))
                                | _ -> Error error
                            | Ok embeddedManifestBytes ->
                                match ReleaseArchive.parse policy.ArchiveName bytes with
                                | Error error -> Error error
                                | Ok _ ->
                                    match ReleaseCompare.observeBytes policy.ArchiveName bytes with
                                    | Error error -> Error error
                                    | Ok observation ->
                                        match ReleaseCompare.contractFromPolicy policy policy.ArchiveName with
                                        | Error error -> Error error
                                        | Ok contract ->
                                            match ReleaseCompare.validate contract observation "candidate" with
                                            | Error error -> Error error
                                            | Ok () ->
                                                match ReleaseCompare.parseEmbeddedManifestEntry observation with
                                                | Error error -> Error error
                                                | Ok manifest ->
                                                    let identityChecks =
                                                        [ manifest.Id = policy.ComponentId, "id"
                                                          manifest.Version = policy.Version, "version"
                                                          manifest.Archive = policy.ArchiveName, "archive"
                                                          manifest.Revision = eventSha, "revision"
                                                          embeddedManifestBytes = manifestBytes, "manifest bytes" ]

                                                    match identityChecks |> List.tryFind (fun (valid, _) -> not valid) with
                                                    | Some (_, field) -> Error(MalformedArtifact(canonicalPath, $"candidate {field} does not match trusted release policy"))
                                                    | None ->
                                                        match DistributionContract.parse (Encoding.UTF8.GetString manifestBytes) with
                                                        | Error error -> Error error
                                                        | Ok standalone when standalone.Id <> policy.ComponentId || standalone.Version <> policy.Version || standalone.Archive <> policy.ArchiveName || standalone.Revision <> eventSha -> Error(MalformedArtifact(canonicalPath, "standalone manifest identity does not match trusted release policy"))
                                                        | Ok _ -> Ok(CandidateArchiveSnapshot(Array.copy bytes, Array.copy manifestBytes, archivePath, sha256 bytes))
    with
    | :? IOException as error -> Error(FileFailure("read", candidatePath, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", candidatePath, error.Message))
    | :? InvalidDataException as error -> Error(MalformedArtifact(candidatePath, $"candidate archive is invalid: {error.Message}"))
