// Focused contract coverage for the repo-shared build provenance guard. Unlike the
// distribution tests this runs from a dirty tree because it exercises only manifest
// revision validation, not packaging or the clean-tree precondition.

#load "../../BuildProvenance.fsx"

open System
open System.IO
open BuildProvenance

let private assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let private assertAsyncOk name (action: Async<Result<'a, ReleaseError>>) =
    async {
        match! action with
        | Ok value -> return value
        | Error error -> return failwithf "%s: expected Ok, got Error %s" name (ReleaseError.message error)
    }

let private assertAsyncRejected name (fragment: string) (action: Async<Result<'a, ReleaseError>>) =
    async {
        match! action with
        | Ok value -> return failwithf "%s: expected rejection, got Ok %A" name value
        | Error error ->
            let message = ReleaseError.message error
            if not (message.Contains(fragment, StringComparison.Ordinal)) then
                return failwithf "%s: expected '%s', got '%s'" name fragment message
    }

let private revision = String.replicate 40 "a"

let private withManifest (json: string) (action: string -> Async<unit>) : Async<unit> =
    async {
        let path = Path.Combine(Path.GetTempPath(), "mcp-provenance-" + Guid.NewGuid().ToString("N") + ".json")
        File.WriteAllText(path, json)

        try
            do! action path
        finally
            if File.Exists path then
                File.Delete path
    }

let private releaseResultBuilderOrderingTest () : Async<unit> =
    async {
        let events = ResizeArray<string>()
        let step name result : Async<Result<unit, ReleaseError>> =
            async {
                events.Add name
                return result
            }

        let! ordered =
            releaseResult {
                events.Add "sync-before"
                do! step "async-before" (Ok())

                for item in [ 1; 2 ] do
                    do! step $"for-{item}" (Ok())

                events.Add "post-for"
                return ()
            }

        assertEqual "ordered release flow succeeds" (Ok()) ordered
        assertEqual "synchronous, async, for, and post-for effects preserve source order"
            [ "sync-before"; "async-before"; "for-1"; "for-2"; "post-for" ]
            (events |> Seq.toList)

        events.Clear()
        let failure = MalformedArtifact("failure-sentinel", "stop")

        let! failed =
            releaseResult {
                events.Add "failure-sync-before"
                do! step "failure-async" (Error failure)

                for item in [ 1; 2 ] do
                    do! step $"failure-for-{item}" (Ok())

                events.Add "failure-post-for"
                return ()
            }

        assertEqual "release flow returns the first failure" (Error failure) failed
        assertEqual "failure skips later async, for, and post-for effects"
            [ "failure-sync-before"; "failure-async" ]
            (events |> Seq.toList)
    }

// The script composes one async pipeline of every assertion; the entry point
// applies exactly one Async.RunSynchronously at the bottom.
let private suite () : Async<unit> =
    async {
        do! releaseResultBuilderOrderingTest ()

        do!
            withManifest
                (sprintf "{\"revision\":\"%s\"}" revision)
                (fun path -> async {
                    let! actual = assertAsyncOk "matching revision" (BuildProvenance.assertManifestRevision path revision)
                    assertEqual "matching revision is returned" revision actual
                })

        do!
            withManifest
                """{ "revision": "0000000000000000000000000000000000000000" }"""
                (fun path -> async {
                    do!
                        assertAsyncRejected
                            "mismatched revision is rejected"
                            "manifest revision"
                            (BuildProvenance.assertManifestRevision path revision)
                })

        do!
            withManifest
                "{}"
                (fun path -> async {
                    do!
                        assertAsyncRejected
                            "missing revision is rejected"
                            "the 'revision' field is missing"
                            (BuildProvenance.assertManifestRevision path revision)
                })

        do!
            withManifest
                "not json"
                (fun path -> async {
                    do!
                        assertAsyncRejected
                            "malformed manifest is rejected"
                            "the manifest is not valid JSON"
                            (BuildProvenance.assertManifestRevision path revision)
                })
    }

// Standalone entry bridge: the only synchronous wait in this script's flow.
suite () |> Async.RunSynchronously

printfn "provenance contract passed"
