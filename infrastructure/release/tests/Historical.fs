// WHY: the published-baseline regression scenarios are synthesized from the shared
//   canonical v2 fixture instead of a downloaded descriptor, so the same-archive,
//   changed-beyond-revision, and revision-only classifications survive forward-only
//   with no historical download prerequisite.

module Release.Tests.Historical

open Expecto
open Release.Tests

open System
open System.IO
open System.Text
open SharedFailure
open BuildProvenance
open ReleaseCompare

let mutable passed = 0
let mutable failed = 0
let failures = ResizeArray<string>()

let assertTrue name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(name)

let assertEqual name expected actual =
    if expected = actual then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(sprintf "%s: expected %A got %A" name expected actual)

let private contract = Fixture.canonicalContract

let private observeArchive path =
    async {
        match! observe path with
        | Ok value -> return value
        | Error error -> return failwithf "observe %s: %s" path (ReleaseError.message error)
    }

let private writeRevisionVariant (root: string) (label: string) (revision: string) =
    let archivePath = Path.Combine(root, label + ".zip")
    let manifestPath = Path.Combine(root, label + ".json")
    Fixture.writeRevisionVariantArchive archivePath manifestPath revision
    archivePath

// Two independent copies of one canonical archive classify as byte-identical.
let sameArchiveIdentical () =
    async {
        let root = Fixture.newTempDir "mcp-baseline-same"
        try
            let first = writeRevisionVariant root "first" Fixture.canonicalPublishedRevision
            let second = writeRevisionVariant root "second" Fixture.canonicalPublishedRevision
            let! a = observeArchive first
            let! b = observeArchive second

            match! compare contract a b Fixture.canonicalPublishedRevision with
            | Ok (ByteIdentical published) ->
                assertEqual "byte-identical carries the published archive sha" a.ArchiveSha256 published.ArchiveSha256
            | Ok other -> failed <- failed + 1; failures.Add(sprintf "same-archive: expected ByteIdentical got %A" other)
            | Error error -> failed <- failed + 1; failures.Add(sprintf "same-archive: %s" (ReleaseError.message error))
        finally
            Fixture.safeDeleteDir root
    }

// A candidate whose payload changed beyond the revision classifies as different.
let bumpedCandidateDifferent () =
    async {
        let root = Fixture.newTempDir "mcp-baseline-bumped"
        try
            let published = writeRevisionVariant root "published" Fixture.canonicalPublishedRevision
            let candidateArchive = Path.Combine(root, "candidate.zip")
            let candidateManifest = Path.Combine(root, "candidate.json")

            Fixture.writeModifiedDotnetDllArchive
                candidateArchive
                candidateManifest
                (Encoding.UTF8.GetBytes "modified-dotnet-dll-payload")
                Fixture.canonicalEvaluatedRevision

            let! publishedObs = observeArchive published
            let! candidateObs = observeArchive candidateArchive

            match! compare contract publishedObs candidateObs Fixture.canonicalEvaluatedRevision with
            | Ok (DifferentBeyondRevision (_, _, reason)) ->
                assertTrue (sprintf "changed candidate rejected (reason=%s)" reason) true
            | Ok other -> failed <- failed + 1; failures.Add(sprintf "bumped candidate: expected DifferentBeyondRevision got %A" other)
            | Error error -> failed <- failed + 1; failures.Add(sprintf "bumped candidate: %s" (ReleaseError.message error))
        finally
            Fixture.safeDeleteDir root
    }

// Consecutive distinct committed HEADs with identical payloads classify as revision-only.
let consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent () =
    async {
        let root = Fixture.newTempDir "mcp-baseline-consecutive"
        try
            let headA = "aaaa" + String('a', 36)
            let headB = "bbbb" + String('b', 36)
            let archiveA = writeRevisionVariant root "a" headA
            let archiveB = writeRevisionVariant root "b" headB
            let! obsA = observeArchive archiveA
            let! obsB = observeArchive archiveB

            for label, publishedObs, publishedRevision, candidateObs, evaluatedRevision in
                [ "A->B", obsA, headA, obsB, headB; "B->A", obsB, headB, obsA, headA ] do
                match! compare contract publishedObs candidateObs evaluatedRevision with
                | Ok (RevisionOnlyEquivalent (_, publishedRev, evaluatedRev)) ->
                    assertEqual (sprintf "%s publishedRev equals the published archive revision" label) publishedRevision publishedRev
                    assertEqual (sprintf "%s evaluatedRev equals the candidate HEAD" label) evaluatedRevision evaluatedRev
                | Ok other ->
                    failed <- failed + 1
                    failures.Add(sprintf "%s consecutive distinct HEAD fixture: expected RevisionOnlyEquivalent got %A" label other)
                | Error error ->
                    failed <- failed + 1
                    failures.Add(sprintf "%s consecutive distinct HEAD fixture: %s" label (ReleaseError.message error))
        finally
            Fixture.safeDeleteDir root
    }

let runSuite () =
    async {
        do! sameArchiveIdentical ()
        do! bumpedCandidateDifferent ()
        do! consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent ()

        printfn "canonical-baseline-tests summary: passed=%d failed=%d" passed failed
    }

let tests =
    testCaseAsync "Historical legacy regression assertions" <| async {
        do! runSuite ()
        Expect.equal 0 failed (String.concat Environment.NewLine failures)
    }
