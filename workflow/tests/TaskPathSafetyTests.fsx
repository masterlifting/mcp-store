// Focused deterministic coverage for the runtime path-containment boundary: a
// project root, a traversed ancestor directory, or a leaf sidecar/lock that is a
// reparse point must fail closed before any read or write. Directory junctions
// are used on Windows so the fixtures never depend on symbolic-link privilege;
// symlinks are used elsewhere. Plain FSI harness because the solution contract
// forbids adding a project/package system. All fixtures live under one fresh GUID
// temp root and only that root is removed.

#load "../ComputationExpressions.fs"
#load "../Workflow.fs"

open System
open System.Diagnostics
open System.IO
open Workflow

let expectRejected (name: string) (fragment: string) result =
    match result with
    | Ok _ -> failwithf "%s: expected rejection, got Ok" name
    | Error error ->
        let message = renderError error

        if not (message.Contains(fragment, StringComparison.Ordinal)) then
            failwithf "%s: expected '%s', got '%s'" name fragment message

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let tempRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"taskpathsafety-tests-{Guid.NewGuid():N}")

Directory.CreateDirectory tempRoot |> ignore

// A directory reparse point. Junctions are used on Windows so the test never
// depends on symbolic-link privilege.
let createDirectoryLink (link: string) (target: string) =
    if OperatingSystem.IsWindows() then
        let startInfo = ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        use proc = Process.Start startInfo
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwithf "could not create junction '%s': %s %s" link stdout stderr
    else
        Directory.CreateSymbolicLink(link, target) |> ignore

// Remove a link without following it into its target.
let removeLink (path: string) =
    if Directory.Exists path then Directory.Delete(path, false)
    elif File.Exists path then File.Delete path

let sidecarPath root id =
    Path.Combine(root, ".tasks", id, SidecarFileName)

let createRequest id title =
    { Id = id
      Title = title
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "Create" ]
      WorkItems =
          [ { Id = "W1"
              Title = "Create"
              DependsOn = []
              Children = [] } ] }

try
    // A reparse point used as the project root is rejected before any read.
    let realRoot = Path.Combine(tempRoot, "real-root")
    Directory.CreateDirectory realRoot |> ignore
    let rootLink = Path.Combine(tempRoot, "root-link")
    createDirectoryLink rootLink realRoot

    expectRejected
        "project root reparse point rejected"
        "project root must not be a reparse point"
        (getTask rootLink "TASK-1")

    // A reparse point planted at .tasks is rejected as a traversed ancestor.
    let traversalRoot = Path.Combine(tempRoot, "traversal-root")
    let traversalTarget = Path.Combine(traversalRoot, "real-tasks")
    Directory.CreateDirectory traversalTarget |> ignore
    createDirectoryLink (Path.Combine(traversalRoot, ".tasks")) traversalTarget

    expectRejected
        "traversed reparse point rejected"
        "path must not traverse a reparse point"
        (getTask traversalRoot "TASK-1")

    if OperatingSystem.IsWindows() then
        // Evidence bootstrap also rejects a reparse point anywhere below the
        // pre-existing task directory instead of following it during enumeration.
        let evidenceRoot = Path.Combine(tempRoot, "evidence-root")
        let evidenceTaskDirectory = Path.Combine(evidenceRoot, ".tasks", "TASK-1")
        Directory.CreateDirectory evidenceTaskDirectory |> ignore
        let evidenceTarget = Path.Combine(tempRoot, "evidence-target")
        Directory.CreateDirectory evidenceTarget |> ignore
        let evidenceLink = Path.Combine(evidenceTaskDirectory, "references")
        createDirectoryLink evidenceLink evidenceTarget

        expectRejected
            "evidence reparse point rejected during create"
            "task directory evidence must not contain a reparse point"
            (createTask evidenceRoot (createRequest "TASK-1" "Reparse evidence"))

        // A failed evidence validation must remove only the lock created by this
        // attempt, leaving the rejected directory recoverable.
        let rejectedRoot = Path.Combine(tempRoot, "rejected-root")
        let rejectedTaskDirectory = Path.Combine(rejectedRoot, ".tasks", "TASK-1")
        let rejectedReferences = Path.Combine(rejectedTaskDirectory, "references")
        Directory.CreateDirectory rejectedReferences |> ignore
        File.WriteAllText(Path.Combine(rejectedReferences, "issue.md"), "evidence")
        File.WriteAllText(Path.Combine(rejectedTaskDirectory, "unsupported.txt"), "not evidence")

        expectRejected
            "rejected evidence create cleans its lock"
            "unsupported pre-existing entry"
            (createTask rejectedRoot (createRequest "TASK-1" "Rejected evidence"))

        assertTrue
            "rejected evidence create leaves no orphan lock"
            (not (File.Exists(Path.Combine(rejectedTaskDirectory, LockFileName))))
    else
        // Unix does not have the complete descriptor-relative boundary required
        // to inspect and claim an existing evidence directory.
        let unixEvidenceRoot = Path.Combine(tempRoot, "unix-evidence-root")
        let unixEvidenceDirectory = Path.Combine(unixEvidenceRoot, ".tasks", "TASK-1", "references")
        Directory.CreateDirectory unixEvidenceDirectory |> ignore
        let unixEvidencePath = Path.Combine(unixEvidenceDirectory, "issue.md")
        File.WriteAllText(unixEvidencePath, "immutable evidence")

        expectRejected
            "Unix evidence bootstrap rejects before mutation"
            "evidence-only task bootstrap requires Windows directory-handle boundaries"
            (createTask unixEvidenceRoot (createRequest "TASK-1" "Unix evidence"))

        assertTrue "Unix evidence remains unchanged" (File.ReadAllText unixEvidencePath = "immutable evidence")
        assertTrue
            "Unix evidence bootstrap creates no state"
            (not (File.Exists(Path.Combine(unixEvidenceRoot, ".tasks", "TASK-1", SidecarFileName)))
             && not (File.Exists(Path.Combine(unixEvidenceRoot, ".tasks", "TASK-1", LockFileName))))

    // A reparse point at the sidecar leaf is rejected.
    let leafRoot = Path.Combine(tempRoot, "leaf-root")
    let leafTaskDirectory = Path.Combine(leafRoot, ".tasks", "TASK-1")
    Directory.CreateDirectory leafTaskDirectory |> ignore
    let leafTarget = Path.Combine(tempRoot, "leaf-target")
    Directory.CreateDirectory leafTarget |> ignore
    createDirectoryLink (sidecarPath leafRoot "TASK-1") leafTarget

    expectRejected
        "sidecar reparse leaf rejected"
        "file must not be a reparse point"
        (getTask leafRoot "TASK-1")

    // A reparse point at the lock leaf is rejected during lock acquisition.
    let lockRoot = Path.Combine(tempRoot, "lock-root")
    let lockTaskDirectory = Path.Combine(lockRoot, ".tasks", "TASK-1")
    Directory.CreateDirectory lockTaskDirectory |> ignore
    // The lock is only reached when the sidecar exists; without it the
    // runtime must fail closed on the missing sidecar before touching a lock.
    File.WriteAllText(sidecarPath lockRoot "TASK-1", "{}")
    let lockTarget = Path.Combine(tempRoot, "lock-target")
    Directory.CreateDirectory lockTarget |> ignore
    createDirectoryLink (Path.Combine(lockTaskDirectory, LockFileName)) lockTarget

    expectRejected
        "lock reparse leaf rejected"
        "could not acquire runtime lock: lock file is a reparse point"
        (getTask lockRoot "TASK-1")

    // A hostile reparse planted at the task collection is rejected before
    // creation, and no task state appears in the junction target.
    let hostileRoot = Path.Combine(tempRoot, "hostile-root")
    let hostileTarget = Path.Combine(tempRoot, "hostile-target")
    Directory.CreateDirectory hostileRoot |> ignore
    Directory.CreateDirectory hostileTarget |> ignore
    let hostileTasks = Path.Combine(hostileRoot, ".tasks")
    createDirectoryLink hostileTasks hostileTarget

    expectRejected
        "hostile task-collection reparse rejected during create"
        "path must not traverse a reparse point"
        (createTask hostileRoot (createRequest "TASK-1" "Hostile reparse"))

    assertTrue
        "hostile reparse target receives no task state"
        (not (File.Exists(Path.Combine(hostileTarget, "TASK-1", SidecarFileName))))

    printfn "OK task path safety: project-root, traversed-ancestor, evidence-tree, sidecar-leaf, and lock-leaf reparse points fail closed"
finally
    // Remove links before the recursive delete so a link target is never followed.
    let links =
        [ Path.Combine(tempRoot, "root-link")
          Path.Combine(tempRoot, "traversal-root", ".tasks")
          Path.Combine(tempRoot, "evidence-root", ".tasks", "TASK-1", "references")
          sidecarPath (Path.Combine(tempRoot, "leaf-root")) "TASK-1"
          Path.Combine(tempRoot, "lock-root", ".tasks", "TASK-1", LockFileName)
          Path.Combine(tempRoot, "hostile-root", ".tasks") ]

    links |> List.iter removeLink

    if Directory.Exists tempRoot && tempRoot.Contains("taskpathsafety-tests-", StringComparison.Ordinal) then
        Directory.Delete(tempRoot, true)
