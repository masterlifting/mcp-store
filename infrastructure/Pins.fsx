// role: reusable module/helper
// Sharing pin validation keeps producer release metadata consistent.
module ReleasePins

#load "Failure.fs"
#load "Provenance.fs"
#load "distribution/Contract.fs"
#load "distribution/Pin.fs"
#load "release/Toolchain.fs"
#load "release/Policy.fs"

open System.IO
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

let writeConsumerPins
    (root: string)
    (configPath: string)
    (distRoot: string)
    (outputPath: string)
    (missingArtifactHint: string)
    : Async<Result<string * string * string * string, ReleaseError>> =
    releaseResult {
        // The SDK and runtime versions come from their owners, never from the component policy.
        let! toolchain = ReleaseToolchain.resolveLocal root
        let! policy = ReleasePolicy.parse toolchain configPath
        let manifestPath = Path.Combine(distRoot, policy.ComponentId, "distribution.json")
        let archivePath = Path.Combine(distRoot, policy.ArchiveName)

        do!
            (if File.Exists archivePath && File.Exists manifestPath then
                 Ok()
             else
                 Error(MissingArtifact missingArtifactHint))

        do! BuildProvenance.assertCleanTree root
        let! head = BuildProvenance.committedHead root
        let! manifestText = BuildProvenance.readAllTextAsync manifestPath
        let! manifest = DistributionContract.parse manifestText
        do! assertIdentity "id" policy.ComponentId manifest.Id
        do! assertIdentity "version" policy.Version manifest.Version
        do! assertIdentity "archive" policy.ArchiveName manifest.Archive
        do! assertIdentity "sourceRepository" DistributionContract.storeRepository manifest.SourceRepository

        do!
            (if manifest.Revision = head then
                 Ok()
             else
                 Error(RevisionMismatch(head, manifest.Revision)))

        let! archiveSha256 = BuildProvenance.sha256FileAsync archivePath
        let! manifestSha256 = BuildProvenance.sha256FileAsync manifestPath

        let pin: Pin =
            { SchemaVersion = DistributionPin.schemaVersion
              ComponentId = policy.ComponentId
              Version = policy.Version
              ArchiveSha256 = archiveSha256
              ManifestSha256 = manifestSha256
              PublishedRevision = manifest.Revision }

        do! BuildProvenance.writeJsonAsync outputPath (DistributionPin.toJsonNode pin)
        return policy.ArchiveName, archiveSha256, manifestSha256, manifest.Revision
    }
