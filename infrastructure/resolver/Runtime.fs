// role: reusable module/helper
// The host enforces the packaged runtime prerequisite, so the resolver needs no selector.
module ResolverRuntime

open SharedFailure

// The manifest must match the hashed runtime configuration to avoid conflicting prerequisites.
let requirement
    (manifest: DistributionContract.DistributionManifest)
    (configBytes: byte[])
    : Result<DistributionContract.RuntimeRequirement, ReleaseError> =
    match DistributionContract.runtimeRequirementFromConfig manifest.Runtime.Config configBytes with
    | Error error -> Error error
    | Ok derived ->
        if derived.Framework <> manifest.Runtime.Framework then
            Error(IdentityMismatch("manifest runtime.framework", manifest.Runtime.Framework, derived.Framework))
        elif derived.Version <> manifest.Runtime.Version then
            Error(IdentityMismatch("manifest runtime.version", manifest.Runtime.Version, derived.Version))
        else
            Ok derived
