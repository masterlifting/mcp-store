# Raw transport adapter review

## Implementation

`ReleaseRunner publish` now takes `<candidate-transport.zip>` as its second
positional argument. It accepts an existing file only; directories are rejected
as typed malformed input, with no extracted-directory compatibility mode. The
runner canonicalizes the path and uses its parent solely as the candidate root
for receipt overlap checks. Trusted publisher receipt, preparation diagnostics,
and FetchCandidate evidence remain three independent explicit paths.

The runner reads raw outer bytes through a 256 MiB bounded stream, compares the
actual lowercase SHA-256 with `--transport-sha256`, the FetchCandidate observed
download hash, API digest, and platform digest, and compares actual byte count
with the receipt's artifact size and downloaded byte count. It then passes those
same bytes and the observed outer digest through `ReleaseHandoff.readRawTransport`.
The enrolled `policy.ArchiveName` remains the expected inner ZIP entry name and
is not derived from or overridden by the raw transport filename. Fixed repository,
component, role, token, upload-opt-in, and HTTP authorization gates remain in the
existing publisher path.

## Bootstrap integration blocker / DevOps handoff

`Bootstrap.ps1 -Mode FetchCandidate` (`Bootstrap.ps1` around lines 1020-1065)
streams the raw artifact to a temporary `rawPath`, checks its hash and size, then
extracts it into `InstallRoot/candidate`. The bootstrap receipt records observed
hash and byte counts, but `rawPath` is not retained as an output and the isolated
install root is removed on script exit. Consequently, no genuine end-to-end
publisher invocation is possible from current FetchCandidate outputs. DevOps
must preserve/copy the original streamed raw ZIP to a fresh, isolated destination
outside extraction, workspace, and receipt paths and expose that exact file path
to `ReleaseRunner publish`; do not copy an extracted inner archive, recompress
the extraction, or relabel its digest as the outer/platform digest. A future
bootstrap contract should provide an explicit protected raw transport output path
and retain it while preserving cleanup of extraction/install state.

## Workspace assumption

`--workspace-root` remains the canonical trusted source package/repository root
for `Participants.json`, `Config.fsx`, and policy derivation. The existing
`BuildProvenance.committedHead` check runs Git with that root as its working
directory and requires HEAD to equal `--event-sha`; the root therefore must be a
Git worktree root or a Git-accepted path within that worktree. A detached source
package without Git metadata is not supported by this implementation, and the
trusted exact-HEAD boundary was not weakened.

## Validation status

The scoped `Release.fsproj` Release build succeeded with zero warnings/errors.
Manual CLI tests, live API checks, and publication were not run. No real fetched
artifact was available for a valid transport smoke test, so this change does not
establish publication readiness. `Bootstrap.ps1` was intentionally not modified.
