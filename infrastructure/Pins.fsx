// role: reusable module/helper
// Repo-shared consumer-pin mechanism loaded as the explicit ReleasePins module.
// Single-sources clean-HEAD revision validation, the strict distribution parse, and
// the canonical unwrapped pin record so producer pins cannot drift from the
// published release-metadata projection.
module ReleasePins

#load "Failure.fs"
#load "Provenance.fs"
#load "distribution/Contract.fs"
#load "distribution/Pin.fs"
#load "release/Policy.fs"

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

// Validates the packaged manifest against the producer's canonical policy and the
// committed clean HEAD, then writes the canonical consumer pin for one producer.
// Callers pass the canonical Config.json path, never a parsed projection: FSI gives
// each #load chain its own PolicyProjection, an incompatible type across helpers.
// Returns the archive hash, manifest hash, and validated revision for the caller's
// diagnostics.
let writeConsumerPins
    (root: string)
    (configPath: string)
    (manifestPath: string)
    (archivePath: string)
    (outputPath: string)
    : Async<Result<string * string * string, ReleaseError>> =
    releaseResult {
        let! policy = ReleasePolicy.parse configPath
        do! BuildProvenance.assertCleanTree root
        let! head = BuildProvenance.committedHead root
        let! manifestText = BuildProvenance.readAllTextAsync manifestPath
        let! manifest = DistributionContract.parse manifestText
        let! sourceRepository = DistributionContract.sourceRepositoryFromAssetUri policy.AssetUri
        do! assertIdentity "id" policy.ComponentId manifest.Id
        do! assertIdentity "version" policy.Version manifest.Version
        do! assertIdentity "archive" policy.ArchiveName manifest.Archive
        do! assertIdentity "sourceRepository" sourceRepository manifest.SourceRepository

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
              AssetUri = policy.AssetUri
              ArchiveName = policy.ArchiveName
              ArchiveSha256 = archiveSha256
              ManifestSha256 = manifestSha256
              PublishedRevision = manifest.Revision
              ProducerSdk = Some policy.SdkVersion
              ProducerRuntime = Some policy.RuntimeVersion
              ProducerPlatform = Some policy.Platform }

        do! BuildProvenance.writeJsonAsync outputPath (DistributionPin.toJsonNode pin)
        return archiveSha256, manifestSha256, manifest.Revision
    }
