// Repo-shared build provenance guard. Every producer packages only from a clean
// committed tree and stamps the manifest with the exact HEAD it built from, so a
// distribution can never claim a revision it did not come from. The file name
// supplies the implicit module name, so `#load` exposes these as BuildProvenance.*.
open System
open System.Diagnostics

let private gitRun (root: string) (args: string list) =
    let info = ProcessStartInfo("git")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    args |> List.iter info.ArgumentList.Add
    use p = Process.Start info
    let so = p.StandardOutput.ReadToEnd()
    let se = p.StandardError.ReadToEnd()
    p.WaitForExit()
    (p.ExitCode, so.Trim(), se.Trim())

let committedHead root =
    match gitRun root [ "rev-parse"; "--verify"; "HEAD^{commit}" ] with
    | 0, sha, _ when sha.Length = 40 -> sha
    | _ -> failwith "producer revision could not be resolved from a committed HEAD"

let assertCleanTree root =
    match gitRun root [ "status"; "--porcelain" ] with
    | 0, "", _ -> ()
    | 0, dirty, _ -> failwithf "refusing to package from a dirty source tree:\n%s" dirty
    | c, _, e -> failwithf "git status failed (%d): %s" c e

let assertManifestRevision manifestPath expected =
    let rev =
        (System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText manifestPath))
            .AsObject().["revision"].GetValue<string>()

    if rev <> expected then
        failwithf "manifest revision %s does not match clean HEAD %s; rebuild the distribution" rev expected
