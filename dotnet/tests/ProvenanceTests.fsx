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

// The script composes one async pipeline of every assertion; the entry point
// applies exactly one Async.RunSynchronously at the bottom.
let private suite () : Async<unit> =
    async {
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
