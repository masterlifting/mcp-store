// role: reusable module/helper
// Hashing the whole canonical pin makes every field difference a distinct installation.
module ResolverIdentity

open System.Text
open SharedFailure

let pinKey (pin: DistributionPin.Pin) : string =
    DistributionPin.render pin |> Encoding.UTF8.GetBytes |> ResolverDigest.sha256

let compare
    (pin: DistributionPin.Pin)
    (manifest: DistributionContract.DistributionManifest)
    : Result<unit, ReleaseError> =
    let failures = ResizeArray<string>()

    let check (field: string) (expected: string) (actual: string) =
        if expected <> actual then
            failures.Add($"{field} differs: pin={expected} manifest={actual}")

    check "id" pin.Id manifest.Id
    check "version" pin.Version manifest.Version
    check "archive" pin.ArchiveName manifest.Archive
    check "sourceRepository" DistributionContract.storeRepository manifest.SourceRepository

    if failures.Count = 0 then
        Ok()
    else
        Error(MalformedArtifact(DistributionContract.manifestEntryName, String.concat "; " failures))
