// role: reusable module/helper
// Repo-shared release-decision mechanics loaded as the explicit ReleaseDecide
// module. Translates a strict-archive `Decision` from `ReleaseCompare` into a
// typed `Action` the per-component transaction can dispatch on, and renders
// both Decision and Action as the per-component summary the workflow
// reports. The decision logic is pure: it consumes a Decision and returns
// either an Action or a typed failure without performing any I/O.
//
// This module is compiled into the Release.fsproj assembly together with
// Provenance.fsx and Compare.fsx; the `#load` directives are honored by
// the analyzer and ignored by the project compiler.
module ReleaseDecide

#load "../Provenance.fsx"
#load "Compare.fsx"

open System
open System.Text
open BuildProvenance

// The workflow-level action derived from a Compare decision. The
// transaction maps each Action to a per-component outcome; `Publish` is
// emitted only when the strict comparison says so (currently: never —
// the comparison only emits ByteIdentical, RevisionOnlyEquivalent, or
// DifferentBeyondRevision; an absence-of-published-asset case is encoded
// at the transport layer as a separate `PublishPending` outcome). The
// `Publish` case is kept for symmetry and so a future transport that
// consumes a `Publish` action has a stable signature to dispatch on.
type Action =
    | Publish of reason: string
    | Unchanged of reason: string
    | BumpRequired of currentVersion: string * reason: string

// Map a strict-archive Decision to a workflow Action. The function is
// total: every Decision case maps to exactly one Action, and the
// `BumpRequired` case carries the producer version so the workflow
// surfaces the typed version-bump diagnostic.
let decide (contract: ReleaseCompare.ComponentContract) (decision: ReleaseCompare.Decision) : Action =
    match decision with
    | ReleaseCompare.ByteIdentical published ->
        Unchanged(
            $"published archive {contract.ArchiveName} is byte-identical (sha256={published.ArchiveSha256}); no upload")
    | ReleaseCompare.RevisionOnlyEquivalent (published, publishedRevision, evaluatedRevision) ->
        // Report the actual published revision and the evaluated HEAD
        // separately so the per-component summary exposes the published
        // values, not the candidate ones (issue requirement 26).
        Unchanged(
            $"published archive {contract.ArchiveName} is strictly revision-only equivalent; published sha256={published.ArchiveSha256} published revision={publishedRevision} evaluated head={evaluatedRevision}")
    | ReleaseCompare.DifferentBeyondRevision (published, candidate, reason) ->
        BumpRequired(
            contract.Version,
            $"candidate {contract.ArchiveName} differs from the published archive beyond the narrow revision-only exception: {reason}; published sha256={published.ArchiveSha256} candidate sha256={candidate.ArchiveSha256}; review the difference and author a new version in {contract.ComponentId}/release/Config.fsx")

// Render a per-component summary the workflow appends to the job summary
// and `$GITHUB_STEP_SUMMARY`. Status is one of the three the issue
// requires (published | unchanged | failed); evaluated HEAD is reported
// separately from published revision.
let renderSummary
    (componentId: string)
    (action: Action)
    (evaluatedRevision: string option)
    (publishedRevision: string option)
    (publishedSha256: string option)
    (candidateSha256: string option)
    : string =
    let builder = StringBuilder()
    builder.AppendLine($"## {componentId} release") |> ignore

    let status, reason =
        match action with
        | Publish reason -> "published", reason
        | Unchanged reason -> "unchanged", reason
        | BumpRequired (version, reason) -> $"failed (version bump required to {version})", reason

    builder.AppendLine($"status={status}") |> ignore
    builder.AppendLine($"reason={reason}") |> ignore

    match publishedRevision with
    | Some value -> builder.AppendLine($"published revision={value}") |> ignore
    | None -> builder.AppendLine("published revision=<absent>") |> ignore

    match publishedSha256 with
    | Some value -> builder.AppendLine($"published archive sha256={value}") |> ignore
    | None -> builder.AppendLine("published archive sha256=<absent>") |> ignore

    match candidateSha256 with
    | Some value -> builder.AppendLine($"candidate archive sha256={value}") |> ignore
    | None -> builder.AppendLine("candidate archive sha256=<absent>") |> ignore

    match evaluatedRevision with
    | Some value -> builder.AppendLine($"evaluated head={value}") |> ignore
    | None -> builder.AppendLine("evaluated head=<unknown>") |> ignore

    builder.ToString()
