// Focused deterministic coverage for the runtime path-containment boundary: a
// project root, a traversed ancestor directory, or a leaf sidecar/lock that is a
// reparse point must fail closed before any read or write. Directory junctions
// are used on Windows so the fixtures never depend on symbolic-link privilege;
// symlinks are used elsewhere. Plain FSI harness because the solution contract
// forbids adding a project/package system. All fixtures live under one fresh GUID
// temp root and only that root is removed.

#load "../ComputationExpressions.fs"
#load "../TaskRuntime.fs"

open System
open System.Diagnostics
open System.IO
open TaskRuntime

let expectRejected (name: string) (fragment: string) result =
    match result with
    | Ok _ -> failwithf "%s: expected rejection, got Ok" name
    | Error error ->
        let message = renderError error

        if not (message.Contains(fragment, StringComparison.Ordinal)) then
            failwithf "%s: expected '%s', got '%s'" name fragment message

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

    printfn "OK task path safety: project-root, traversed-ancestor, sidecar-leaf, and lock-leaf reparse points fail closed"
finally
    // Remove links before the recursive delete so a link target is never followed.
    let links =
        [ Path.Combine(tempRoot, "root-link")
          Path.Combine(tempRoot, "traversal-root", ".tasks")
          sidecarPath (Path.Combine(tempRoot, "leaf-root")) "TASK-1"
          Path.Combine(tempRoot, "lock-root", ".tasks", "TASK-1", LockFileName) ]

    links |> List.iter removeLink

    if Directory.Exists tempRoot && tempRoot.Contains("taskpathsafety-tests-", StringComparison.Ordinal) then
        Directory.Delete(tempRoot, true)
