// role: reusable module/helper
// Runtime prerequisite derived from the packaged runtime configuration, never from build
// provenance; the host enforces the prerequisite, so no resolver-side selector exists.
module ResolverRuntime

open SharedFailure

// The packaged configuration is authoritative, so a manifest that declares a different
// prerequisite than the hashed member it names is rejected rather than resolved.
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
