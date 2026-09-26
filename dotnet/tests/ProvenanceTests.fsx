// Focused contract coverage for the repo-shared build provenance guard. Unlike the
// distribution tests this runs from a dirty tree because it exercises only manifest
// revision validation, not packaging or the clean-tree precondition.

#load "../../BuildProvenance.fsx"

open System
open System.IO

let private assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let private assertThrows name (action: unit -> unit) =
    let mutable threw = false

    try
        action ()
    with _ ->
        threw <- true

    if not threw then
        failwithf "%s: expected the guard to reject the manifest" name

let private revision = String.replicate 40 "a"

let private withManifest (json: string) (action: string -> unit) =
    let path = Path.Combine(Path.GetTempPath(), "mcp-provenance-" + Guid.NewGuid().ToString("N") + ".json")
    File.WriteAllText(path, json)

    try
        action path
    finally
        if File.Exists path then
            File.Delete path

withManifest (sprintf "{\"revision\":\"%s\"}" revision) (fun path ->
    assertEqual "matching revision is returned" revision (BuildProvenance.assertManifestRevision path revision))

withManifest """{ "revision": "0000000000000000000000000000000000000000" }""" (fun path ->
    assertThrows "mismatched revision is rejected" (fun () -> BuildProvenance.assertManifestRevision path revision |> ignore))

withManifest "{}" (fun path ->
    assertThrows "missing revision is rejected" (fun () -> BuildProvenance.assertManifestRevision path revision |> ignore))

withManifest "not json" (fun path ->
    assertThrows "malformed manifest is rejected" (fun () -> BuildProvenance.assertManifestRevision path revision |> ignore))

printfn "provenance contract passed"
