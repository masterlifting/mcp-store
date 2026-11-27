// role: reusable module/helper
// Sharing pin validation keeps producer release metadata consistent.
module ReleasePins

#load "Failure.fs"
#load "Provenance.fs"
#load "distribution/Contract.fs"
#load "distribution/Archive.fs"
#load "distribution/Pin.fs"
#load "release/Toolchain.fs"
#load "release/Policy.fs"

open System
open System.IO
open System.Security.Cryptography
open SharedFailure
open BuildProvenance
open DistributionContract
open DistributionPin
open ReleasePolicy

let private assertIdentity (field: string) (expected: string) (actual: string) : Result<unit, ReleaseError> =
    if actual = expected then
        Ok()
    else
        Error(IdentityMismatch(field, expected, actual))

// FileShare.Read denies concurrent writers while the bounded read runs.
let private readArchiveSnapshot (path: string) : Async<Result<byte[], ReleaseError>> =
    async {
        try
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)

            if stream.Length > DistributionArchive.maxArchiveBytes then
                return
                    Error(
                        MalformedArtifact(path, $"archive is {stream.Length} bytes which exceeds the {DistributionArchive.maxArchiveBytes}-byte bound")
                    )
            else
                let bytes = Array.zeroCreate<byte> (int stream.Length)
                do! awaitComplete (stream.ReadExactlyAsync(bytes, 0, bytes.Length).AsTask())
                return Ok bytes
        with
        | :? OperationCanceledException as error -> return raise error
        | :? EndOfStreamException as error -> return Error(FileFailure("read", path, error.Message))
        | :? IOException as error -> return Error(FileFailure("read", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("read", path, error.Message))
    }

let private embeddedManifest
    (archivePath: string)
    (archive: DistributionArchive.Archive)
    : Result<DistributionContract.DistributionManifest, ReleaseError> =
    match archive.Entries |> List.filter (fun entry -> entry.Name = DistributionContract.manifestEntryName) with
    | [ entry ] ->
        DistributionArchive.decompress entry
        |> Result.bind DistributionContract.parseBytes
    | [] -> Error(MalformedArtifact(archivePath, $"the archive is missing the canonical {DistributionContract.manifestEntryName} entry"))
    | _ ->
        Error(MalformedArtifact(archivePath, $"the archive contains more than one {DistributionContract.manifestEntryName} entry"))

let writeConsumerPins
    (root: string)
    (configPath: string)
    (distRoot: string)
    (outputPath: string)
    (missingArtifactHint: string)
    : Async<Result<Pin, ReleaseError>> =
    releaseResult {
        // The SDK and runtime versions come from their owners, never from the component policy.
        let! toolchain = ReleaseToolchain.resolveLocal root
        let! policy = ReleasePolicy.parse toolchain configPath
        let archivePath = Path.Combine(distRoot, policy.ArchiveName)

        do!
            (if File.Exists archivePath then
                 Ok()
             else
                 Error(MissingArtifact missingArtifactHint))

        do! BuildProvenance.assertCleanTree root
        let! head = BuildProvenance.committedHead root
        // The manifest and the digest both come from this one snapshot, so they describe the same ZIP bytes.
        let! archiveBytes = readArchiveSnapshot archivePath
        let! archive = DistributionArchive.parse archivePath archiveBytes
        let! manifest = embeddedManifest archivePath archive
        do! assertIdentity "id" policy.ComponentId manifest.Id
        do! assertIdentity "version" policy.Version manifest.Version
        do! assertIdentity "archive" policy.ArchiveName manifest.Archive
        do! assertIdentity "sourceRepository" DistributionContract.storeRepository manifest.SourceRepository

        do!
            (if manifest.Revision = head then
                 Ok()
             else
                 Error(RevisionMismatch(head, manifest.Revision)))

        let archiveSha256 =
            SHA256.HashData archiveBytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

        let pin: Pin =
            { Id = policy.ComponentId
              Version = policy.Version
              Digest = DistributionPin.digestFor archiveSha256 }

        do! BuildProvenance.writeJsonAsync outputPath (DistributionPin.toJsonNode pin)
        return pin
    }
