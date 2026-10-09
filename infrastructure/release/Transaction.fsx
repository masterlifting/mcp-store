// role: reusable module/helper
// Repo-shared per-component release-transaction orchestration loaded as the
// explicit ReleaseTransaction module. Composes the strict-archive
// comparison and the decision layer into a single per-component
// transaction the workflow runs independently for each producer. The
// transaction is pure orchestration: it consumes a candidate archive path,
// a transport-supplied remote observation, and a producer contract, and
// returns a typed `Outcome` the workflow can dispatch on.
//
// The remote transport is provided by the caller; this seam keeps GitHub
// effects out of the transaction and makes the per-component flow
// testable without a real remote. The candidate archive path is supplied
// by the caller too so the transaction does not assume a particular
// dist-layout.
//
// Outcomes are split so the comparison decision ("what the strict
// comparison says") and the upload result ("what the transport did")
// never get conflated:
//
//   * `PublishPending` — the remote asset was absent; the comparison
//     decision is to publish, but no actual upload has happened. The
//     transport performs the upload in a follow-up step. This is
//     distinct from a completed `Published` outcome.
//   * `Unchanged` — the remote asset is byte-identical or strictly
//     revision-only-equivalent. The existing published archive must
//     remain unchanged.
//   * `BumpRequired` — the remote asset differs beyond the narrow
//     revision-only exception. The workflow fails closed and reports
//     the typed version-bump diagnostic.
//   * `Failed` — any other typed failure, including missing artifacts,
//     malformed archives, transport errors, or internal inconsistencies.
//
// This module is compiled into the Release.fsproj assembly together with
// Provenance.fsx, Compare.fsx, and Decide.fsx; the `#load` directives
// are honored by the analyzer and ignored by the project compiler.
module ReleaseTransaction

#load "../Provenance.fsx"
#load "Compare.fsx"
#load "Decide.fsx"

open System
open System.IO
open BuildProvenance

// Transport seam the workflow provides. Returns `Ok None` when the
// remote asset is absent (the comparison says "publish"), `Ok Some
// obs` when the remote asset was observed (the comparison either says
// "no-op" or "bump required"), and `Error` for any transport or
// validation failure including immutable-release configuration.
type RemoteObserver =
    { ObserveRemote: (string * string) -> Async<Result<ReleaseCompare.ArchiveObservation option, ReleaseError>> }

// The per-component transaction outcome. `PublishPending` carries an
// optional published observation (None when the remote asset was absent;
// the transport performs the upload after the transaction returns).
// `Unchanged` is a clean no-op. `BumpRequired` is a typed failure the
// workflow surfaces to the operator. `Failed` wraps any other release
// failure so the caller can render it.
type Outcome =
    | PublishPending of remoteAbsent: bool
    | Unchanged of published: ReleaseCompare.ArchiveObservation
    | BumpRequired of currentVersion: string * reason: string
    | Failed of error: ReleaseError

type TransactionRequest =
    { Contract: ReleaseCompare.ComponentContract
      Tag: string
      CandidateArchivePath: string
      CandidateManifestPath: string }

// Parse the candidate manifest to read its `revision` field. The
// candidate is built by the producer flow (Build.fsx + Pins.fsx) which
// already enforces this against HEAD, so the transaction re-asserts the
// invariant under a fresh observation before any publish decision is
// made.
let private readCandidateRevision (candidateManifestPath: string) : Async<Result<string, ReleaseError>> =
    async {
        match! readAllTextAsync candidateManifestPath with
        | Error error -> return Error error
        | Ok text ->
            try
                let node = System.Text.Json.Nodes.JsonNode.Parse text
                if isNull node then
                    return Error(MalformedArtifact(candidateManifestPath, "the document is empty"))
                else
                    let document = node.AsObject()
                    if not (document.ContainsKey "revision") then
                        return Error(MalformedArtifact(candidateManifestPath, "the 'revision' field is missing"))
                    else
                        return Ok(document["revision"].GetValue<string>())
            with
            | :? System.Text.Json.JsonException as error ->
                return Error(MalformedArtifact(candidateManifestPath, $"the manifest is not valid JSON: {error.Message}"))
            | :? FormatException as error ->
                return Error(MalformedArtifact(candidateManifestPath, $"the manifest is not valid JSON: {error.Message}"))
            | :? InvalidOperationException as error ->
                return Error(MalformedArtifact(candidateManifestPath, $"the manifest is not valid JSON: {error.Message}"))
    }

// One-time, IO-bound capture of the candidate archive.
let private observeCandidate (request: TransactionRequest) : Async<Result<ReleaseCompare.ArchiveObservation, ReleaseError>> =
    async {
        let mutable failure : ReleaseError option = None
        let mutable observed : ReleaseCompare.ArchiveObservation option = None

        if not (File.Exists request.CandidateArchivePath) then
            failure <- Some(MissingArtifact request.CandidateArchivePath)
        elif not (File.Exists request.CandidateManifestPath) then
            failure <- Some(MissingArtifact request.CandidateManifestPath)
        else
            let! observation = ReleaseCompare.observe request.CandidateArchivePath
            match observation with
            | Error error -> failure <- Some error
            | Ok value -> observed <- Some value

        match observed, failure with
        | Some value, None -> return Ok value
        | _, Some error -> return Error error
        | None, None -> return Error(MalformedArtifact(request.CandidateArchivePath, "no observation result was produced"))
    }

// The complete per-component transaction. Returns an `Outcome` the
// workflow can dispatch on; transport and observation failures become
// `Failed`, comparison and decision failures become `BumpRequired` or
// `Unchanged` per the strict-archive contract, and absent remote assets
// become `PublishPending` (the workflow performs the upload in a
// follow-up step).
let run (remote: RemoteObserver) (request: TransactionRequest) : Async<Outcome> =
    async {
        let! revisionResult = readCandidateRevision request.CandidateManifestPath
        match revisionResult with
        | Error error -> return Failed error
        | Ok evaluatedRevision ->
            let! candidateResult = observeCandidate request
            match candidateResult with
            | Error error -> return Failed error
            | Ok candidateObs ->
                let! publishedResult = remote.ObserveRemote(request.Tag, request.Contract.ArchiveName)
                match publishedResult with
                | Error error -> return Failed error
                | Ok None ->
                    // Asset absent: the comparison decision is to publish;
                    // no upload has been performed yet. The transport
                    // performs the upload in a follow-up step.
                    return PublishPending(true)
                | Ok (Some publishedObs) ->
                    let! decisionResult = ReleaseCompare.compare request.Contract publishedObs candidateObs evaluatedRevision
                    match decisionResult with
                    | Error error -> return Failed error
                    | Ok decision ->
                        let action = ReleaseDecide.decide request.Contract decision
                        match action with
                        | ReleaseDecide.Unchanged _ -> return Unchanged publishedObs
                        | ReleaseDecide.Publish reason ->
                            // A `Publish` action is not produced by the
                            // current comparison path. When it does
                            // surface (for example, because a future
                            // comparison replaces the absent-asset branch
                            // with a `Publish` action), the transaction
                            // surfaces it as a typed failure so the
                            // workflow fails closed rather than treating
                            // the absent-asset case as a successful
                            // publish.
                            return Failed(FileFailure("publish", request.Contract.ComponentId, reason))
                        | ReleaseDecide.BumpRequired (version, reason) -> return BumpRequired(version, reason)
    }

// Render a per-component Outcome as a per-component summary. The
// renderer exposes published hashes, candidate hash, evaluated HEAD,
// and the reason for the decision so the operator can act on it
// without re-running the transaction. The outcome distinguishes
// publish-pending from a confirmed published archive: a publish-
// pending outcome means the comparison decision is to publish but the
// upload has not yet been performed; the workflow exits non-zero so
// the absence is not mistaken for a successful publication.
let renderStepSummary
    (componentId: string)
    (outcome: Outcome)
    (candidateSha256: string option)
    (candidateRevision: string option)
    : string =
    let builder = System.Text.StringBuilder()
    builder.AppendLine($"## {componentId} release") |> ignore

    let status, reason =
        match outcome with
        | PublishPending _ ->
            "publish-pending (asset absent, upload not yet performed)",
            "the remote release has no asset named in the producer contract; the upload must run before this state can advance to published"
        | Unchanged published ->
            "unchanged",
            $"the published archive sha256={published.ArchiveSha256} matches the candidate under the strict comparison"
        | BumpRequired (version, reason) -> $"failed (version bump required to {version})", reason
        | Failed error -> "failed", ReleaseError.message error

    builder.AppendLine($"status={status}") |> ignore
    builder.AppendLine($"reason={reason}") |> ignore

    match candidateSha256 with
    | Some value -> builder.AppendLine($"candidate archive sha256={value}") |> ignore
    | None -> builder.AppendLine("candidate archive sha256=<unknown>") |> ignore

    match candidateRevision with
    | Some value -> builder.AppendLine($"evaluated head={value}") |> ignore
    | None -> builder.AppendLine("evaluated head=<unknown>") |> ignore

    builder.ToString()
