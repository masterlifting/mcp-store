// role: reusable module/helper
// Install identity and pin-versus-manifest identity. The whole canonical pin is hashed,
// so any field difference, including producer provenance, is a distinct installation.
module ResolverIdentity

open System.Text
open SharedFailure

let pinKey (pin: DistributionPin.Pin) : string =
    DistributionPin.render pin |> Encoding.UTF8.GetBytes |> ResolverDigest.sha256

// Optional pin provenance is matched when supplied; an omitted field asserts nothing.
let compare
    (pin: DistributionPin.Pin)
    (manifest: DistributionContract.DistributionManifest)
    : Result<unit, ReleaseError> =
    let failures = ResizeArray<string>()

    let check (field: string) (expected: string) (actual: string) =
        if expected <> actual then
            failures.Add($"{field} differs: pin={expected} manifest={actual}")

    let checkSupplied (field: string) (expected: string option) (actual: string) =
        match expected with
        | Some value -> check field value actual
        | None -> ()

    check "id" pin.ComponentId manifest.Id
    check "version" pin.Version manifest.Version
    check "revision" pin.PublishedRevision manifest.Revision
    check "archive" pin.ArchiveName manifest.Archive
    checkSupplied "producer.sdk" pin.ProducerSdk manifest.Producer.Sdk
    checkSupplied "producer.runtime" pin.ProducerRuntime manifest.Producer.Runtime
    checkSupplied "producer.platform" pin.ProducerPlatform manifest.Producer.Platform

    match DistributionContract.sourceRepositoryFromAssetUri pin.AssetUri with
    | Error _ ->
        failures.Add($"assetUri '{pin.AssetUri}' is not a '<repository>/releases/download/<tag>/<asset>' URL")
    | Ok repository -> check "sourceRepository" repository manifest.SourceRepository

    if failures.Count = 0 then
        Ok()
    else
        Error(MalformedArtifact(DistributionContract.manifestEntryName, String.concat "; " failures))
