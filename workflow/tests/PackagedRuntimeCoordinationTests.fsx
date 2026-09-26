// Process-boundary proof for the packaged Workflow distribution (AC20/AC21).
//
// Coverage:
//   1. Resolves a packaged Mcp.Workflow.dll. A fresh local install is used when
//      present; otherwise the repository package is published with the exact
//      BuildDistributions.fsx publish profile. MCP_WORKFLOW_PACKAGED_DLL can
//      override the target explicitly.
//   2. Verifies the packaged binary publishes the OS mutex name and contains no
//      runtime.lock artifact string or legacy file-lock diagnostic.
//   3. Spawns two real `dotnet exec <packaged-dll>` MCP processes that race a
//      task_create and then a non-overlapping task_apply (start vs block) on the
//      same projectRoot/taskId, polling the task directory for transient locks.
//   4. Recovers from an abandoned mutex owner: a helper subprocess acquires the
//      named runtime mutex and is killed while holding it; a packaged Workflow
//      process must then acquire the abandoned mutex and complete the CAS.
//
// The test never mutates the installed MCP; it only reads it.

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Threading

// --- assertions -------------------------------------------------------------

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertEqual name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual

let assertContains name (fragment: string) (text: string) =
    if not (text.Contains(fragment, StringComparison.Ordinal)) then
        failwithf "%s: expected '%s' in '%s'" name fragment text

// --- byte-level string scanning --------------------------------------------

// .NET stores user string literals in the metadata #US heap as UTF-16LE, so a
// naive ASCII scan misses them. Search the raw bytes for both encodings.
let countBytes (haystack: byte[]) (needle: byte[]) =
    if needle.Length = 0 || haystack.Length < needle.Length then
        0
    else
        let mutable count = 0
        let limit = haystack.Length - needle.Length

        for i in 0 .. limit do
            if haystack.[i] = needle.[0] then
                let mutable j = 1
                while j < needle.Length && haystack.[i + j] = needle.[j] do
                    j <- j + 1

                if j = needle.Length then count <- count + 1

        count

let occurrencesUtf16 (text: string) (bytes: byte[]) = countBytes bytes (Encoding.Unicode.GetBytes text)
let occurrencesUtf8 (text: string) (bytes: byte[]) = countBytes bytes (Encoding.UTF8.GetBytes text)

let sha256File path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

// --- packaged entry resolution ---------------------------------------------

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let userProfile = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
let installedDirectory = Path.Combine(userProfile, ".config", "opencode", "mcp", "workflow")
let installedEntry = Path.Combine(installedDirectory, "Mcp.Workflow.dll")
let helperScript = Path.Combine(__SOURCE_DIRECTORY__, "MutexAbandonmentHelper.fsx")

let mutexNamePattern = "Mcp.Workflow.Runtime."
let lockArtifact = "runtime.lock"

let legacyLockMessages =
    [ "could not acquire runtime lock: lock file is a reparse point"
      "runtime lock already exists and its ownership cannot be established safely" ]

let hasFreshCoordination (path: string) =
    try
        File.Exists path
        && (let bytes = File.ReadAllBytes path
            occurrencesUtf16 mutexNamePattern bytes >= 1
            && occurrencesUtf16 lockArtifact bytes = 0)
    with _ ->
        false

let runDotnet (arguments: string list) =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- repoRoot
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    arguments |> List.iter info.ArgumentList.Add
    use child = Process.Start info
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()

    if child.ExitCode <> 0 then
        failwithf "dotnet %s failed (%d): %s" (String.concat " " arguments) child.ExitCode (stderr.Result.Trim())

    stdout.Result

let publishRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"workflow-packaged-{Guid.NewGuid():N}")

let publishPackage () =
    Directory.CreateDirectory publishRoot |> ignore

    runDotnet
        [ "publish"
          "workflow/Mcp.Workflow.fsproj"
          "--configuration"
          "Release"
          "--framework"
          "net11.0"
          "--self-contained"
          "false"
          "-p:UseAppHost=false"
          "-p:DebugType=None"
          "-p:DebugSymbols=false"
          "-p:SatelliteResourceLanguages=none"
          "--output"
          publishRoot ]
    |> ignore

let tryPublishPackage () =
    try
        publishPackage ()
        Some(Path.Combine(publishRoot, "Mcp.Workflow.dll"))
    with error ->
        printfn "WARNING: publishing the current tree failed; falling back to an existing fresh package."
        printfn "  publish error: %s" error.Message
        None

// Prebuilt package locations used only when the current tree cannot publish
// (for example, a concurrent uncommitted edit). A fresh fallback still carries
// the final ephemeral-mutex coordination implementation, and the chosen source
// is printed so a stale fallback is visible.
let fallbackEntryDlls =
    [ Path.Combine(repoRoot, "workflow", "dist", "workflow", "Mcp.Workflow.dll")
      Path.Combine(repoRoot, "workflow", "bin", "Release", "net11.0", "Mcp.Workflow.dll") ]

let entryDll, entrySource =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_PACKAGED_DLL" with
    | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value, "env:MCP_WORKFLOW_PACKAGED_DLL"
    | _ when hasFreshCoordination installedEntry -> installedEntry, "installed"
    | _ ->
        match tryPublishPackage () with
        | Some path -> path, "repo-publish"
        | None ->
            match fallbackEntryDlls |> List.tryFind hasFreshCoordination with
            | Some path -> path, "repo-package-fallback"
            | None ->
                failwith
                    "no fresh packaged Workflow binary is available: the current tree does not publish and no fresh package exists"

let cleanupDirectories = ResizeArray<string>()
cleanupDirectories.Add publishRoot

// --- packaged binary string verification -----------------------------------

let verifyPackagedBinary (path: string) =
    if not (File.Exists path) then
        failwithf "packaged entry DLL does not exist: %s" path

    let bytes = File.ReadAllBytes path
    let mutexCount = occurrencesUtf16 mutexNamePattern bytes
    let lockUtf16 = occurrencesUtf16 lockArtifact bytes
    let lockUtf8 = occurrencesUtf8 lockArtifact bytes

    printfn "binary string scan: %s" path
    printfn "  \"%s\" utf16 occurrences: %d" mutexNamePattern mutexCount
    printfn "  \"%s\" utf16=%d utf8=%d" lockArtifact lockUtf16 lockUtf8

    for message in legacyLockMessages do
        printfn "  legacy message utf16 occurrences: %d (%s)" (occurrencesUtf16 message bytes) message

    assertTrue "packaged binary publishes the OS mutex name" (mutexCount >= 1)
    assertTrue "packaged binary must not contain runtime.lock" (lockUtf16 = 0 && lockUtf8 = 0)

    for message in legacyLockMessages do
        assertTrue
            (sprintf "packaged binary must not contain legacy lock message '%s'" message)
            (occurrencesUtf16 message bytes = 0)

// --- stdio MCP transport ----------------------------------------------------

let jstr (value: string) : JsonNode = JsonValue.Create value
let jint (value: int) : JsonNode = JsonValue.Create value

let jobj (fields: (string * JsonNode) list) =
    let node = JsonObject()
    fields |> List.iter (fun (key, value) -> node.[key] <- value)
    node

let jarr (items: JsonNode list) =
    let array = JsonArray()
    items |> List.iter (fun item -> array.Add item)
    array

let readProtocolLine (child: Process) name =
    let task = child.StandardOutput.ReadLineAsync()

    if not (task.Wait 240000) then
        failwithf "%s: timed out waiting for a protocol response" name

    if isNull task.Result then
        failwithf "%s: protocol stdout closed before a response arrived" name

    let node = JsonNode.Parse task.Result

    if isNull node || isNull node.["jsonrpc"] || node.["jsonrpc"].GetValue<string>() <> "2.0" then
        failwithf "%s: protocol stdout line is not JSON-RPC 2.0: %s" name task.Result

    node

let startMcpProcess (root: string) (catalog: string) (catalogHash: string) =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add "exec"
    startInfo.ArgumentList.Add entryDll
    startInfo.ArgumentList.Add "--profile-catalog"
    startInfo.ArgumentList.Add (Path.GetFullPath catalog)
    startInfo.ArgumentList.Add "--profile-catalog-sha256"
    startInfo.ArgumentList.Add catalogHash
    startInfo.RedirectStandardInput <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.WorkingDirectory <- root
    Process.Start startInfo

type McpSession(host: Process) =
    let stderrTask = host.StandardError.ReadToEndAsync()
    let mutable closed = false
    let mutable exitCode = -1
    let mutable stderrText = ""

    member _.Id = host.Id
    member _.ExitCode = exitCode
    member _.Stderr = stderrText

    member _.Write(request: JsonNode) =
        host.StandardInput.WriteLine(request.ToJsonString())
        host.StandardInput.Flush()

    member _.Read(name: string) = readProtocolLine host name

    member this.Call(name: string, id: int, tool: string, arguments: JsonNode) =
        this.Write(jobj [ "jsonrpc", jstr "2.0"; "id", jint id; "method", jstr "tools/call"; "params", jobj [ "name", jstr tool; "arguments", arguments ] ])
        this.Read name

    member _.Close() =
        if not closed then
            closed <- true

            if not host.HasExited then
                try host.StandardInput.Close() with _ -> ()

                if not (host.WaitForExit 30000) then
                    try host.Kill() with _ -> ()
                    host.WaitForExit 5000 |> ignore

            exitCode <- host.ExitCode
            stderrText <- try stderrTask.Result with _ -> ""
            host.Dispose()

let isToolError (response: JsonNode) = response.["result"].["isError"].GetValue<bool>()
let toolStructured (response: JsonNode) = response.["result"].["structuredContent"]
let toolErrorCode (response: JsonNode) = (toolStructured response).["error"].["code"].GetValue<string>()
let toolErrorMessage (response: JsonNode) = (toolStructured response).["error"].["message"].GetValue<string>()
let taskOf (response: JsonNode) = (toolStructured response).["task"]

// --- task directory snapshots ----------------------------------------------

let enumerateEntries directory =
    try
        if Directory.Exists directory then
            Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path -> Path.GetRelativePath(directory, path))
            |> Seq.sort
            |> Seq.toList
        else
            []
    with
    | :? IOException -> []
    | :? UnauthorizedAccessException -> []

type DirectorySampler(directory: string) =
    let snapshots = ConcurrentQueue<string list>()
    let cancellation = new CancellationTokenSource()
    let mutable thread = Unchecked.defaultof<Thread>

    member _.Start() =
        let loop () =
            while not cancellation.IsCancellationRequested do
                snapshots.Enqueue(enumerateEntries directory)
                Thread.Sleep 1

        thread <- Thread(ThreadStart loop)
        thread.IsBackground <- true
        thread.Start()

    member _.Stop() =
        cancellation.Cancel()
        if not (isNull thread) then thread.Join()
        snapshots.ToArray() |> Array.toList

// --- request payloads -------------------------------------------------------

let createArgs root id =
    jobj
        [ "projectRoot", jstr root
          "taskId", jstr id
          "title", jstr "Packaged coordination proof"
          "kind", jstr "execution"
          "acceptanceCriteria", jarr [ jobj [ "id", jstr "AC1"; "text", jstr "The task is serialized" ] ]
          "workItems", jarr [ jobj [ "id", jstr "W1"; "title", jstr "Coordinate"; "children", jarr [] ] ] ]

let taskGetArgs root id = jobj [ "projectRoot", jstr root; "taskId", jstr id ]

let applyArgs root id revision command =
    jobj
        [ "projectRoot", jstr root
          "taskId", jstr id
          "expectedStateRevision", jint revision
          "command", command ]

let startCommand () = jobj [ "type", jstr "start"; "workItemId", jstr "W1" ]

let blockCommand () =
    jobj [ "type", jstr "block"; "workItemId", jstr "W1"; "blocker", jstr "process-boundary race" ]

let prepareWorkspace prefix =
    let root = Path.Combine(Path.GetTempPath(), "opencode", $"{prefix}-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore
    let catalog = Path.Combine(root, "profiles.json")
    File.WriteAllText(catalog, "{\"profiles\":[]}")
    cleanupDirectories.Add root
    root, catalog, sha256File catalog

let capture (label: string) (taskDirectory: string) =
    let entries = enumerateEntries taskDirectory
    printfn "directory %-24s -> [%s]" label (String.concat ", " entries)
    entries

// --- coverage 3: two packaged processes race create + apply ----------------

let runRaceTest () =
    let root, catalog, catalogHash = prepareWorkspace "workflow-packaged-race"
    let taskId = "PKG-1"
    let taskDirectory = Path.Combine(root, ".tasks", taskId)
    let sampler = DirectorySampler taskDirectory
    let mutable first = Unchecked.defaultof<McpSession>
    let mutable second = Unchecked.defaultof<McpSession>

    try
        sampler.Start()
        first <- McpSession(startMcpProcess root catalog catalogHash)
        second <- McpSession(startMcpProcess root catalog catalogHash)
        printfn "race processes: A pid=%d B pid=%d" first.Id second.Id

        // Both processes receive task_create before either response is read, so
        // the two creates genuinely race through the runtime mutex.
        first.Write(jobj [ "jsonrpc", jstr "2.0"; "id", jint 1; "method", jstr "tools/call"; "params", jobj [ "name", jstr "task_create"; "arguments", createArgs root taskId ] ])
        second.Write(jobj [ "jsonrpc", jstr "2.0"; "id", jint 1; "method", jstr "tools/call"; "params", jobj [ "name", jstr "task_create"; "arguments", createArgs root taskId ] ])
        let createA = first.Read "task_create A"
        let createB = second.Read "task_create B"
        printfn "create A: isError=%b code=%s" (isToolError createA) (if isToolError createA then toolErrorCode createA else "-")
        printfn "create B: isError=%b code=%s" (isToolError createB) (if isToolError createB then toolErrorCode createB else "-")
        capture "after task_create" taskDirectory |> ignore

        let createErrors = [ createA; createB ] |> List.filter isToolError
        assertEqual "exactly one create wins" 1 createErrors.Length
        assertEqual "losing create code" "INVALID_INPUT" (toolErrorCode createErrors.Head)
        assertContains "losing create message" "runtime state" (toolErrorMessage createErrors.Head)

        // Non-overlapping mutating commands against revision 0: one start, one
        // block. The CAS must let exactly one win.
        first.Write(jobj [ "jsonrpc", jstr "2.0"; "id", jint 2; "method", jstr "tools/call"; "params", jobj [ "name", jstr "task_apply"; "arguments", applyArgs root taskId 0 (startCommand ()) ] ])
        second.Write(jobj [ "jsonrpc", jstr "2.0"; "id", jint 2; "method", jstr "tools/call"; "params", jobj [ "name", jstr "task_apply"; "arguments", applyArgs root taskId 0 (blockCommand ()) ] ])
        let applyStart = first.Read "task_apply start"
        let applyBlock = second.Read "task_apply block"
        printfn "apply start: isError=%b code=%s" (isToolError applyStart) (if isToolError applyStart then toolErrorCode applyStart else "-")
        printfn "apply block: isError=%b code=%s" (isToolError applyBlock) (if isToolError applyBlock then toolErrorCode applyBlock else "-")
        capture "after task_apply" taskDirectory |> ignore

        let applyErrors = [ applyStart; applyBlock ] |> List.filter isToolError
        assertEqual "exactly one apply wins" 1 applyErrors.Length
        assertEqual "losing apply code" "CONFLICT" (toolErrorCode applyErrors.Head)
        assertContains "losing apply message" "state revision conflict" (toolErrorMessage applyErrors.Head)

        let finalGet = first.Call("task_get", 3, "task_get", taskGetArgs root taskId)
        let finalTask = taskOf finalGet
        assertEqual "final state revision" 1 (finalTask.["stateRevision"].GetValue<int>())

        let startWon = not (isToolError applyStart)
        let expectedState = if startWon then "active" else "blocked"
        let actualState = finalTask.["workItems"].AsArray().[0].["state"].GetValue<string>()
        assertEqual "winning operation is reflected in final state" expectedState actualState
        capture "after task_get" taskDirectory |> ignore

        let finalEntries = capture "final" taskDirectory
        assertEqual "task directory contains only runtime.json" [ "runtime.json" ] finalEntries

        let observed = sampler.Stop()
        let observedEntries = observed |> List.collect id |> List.distinct |> List.sort

        let observedLocks =
            observedEntries
            |> List.filter (fun entry -> Path.GetFileName(entry).Equals(lockArtifact, StringComparison.OrdinalIgnoreCase))

        assertTrue
            (sprintf "sampler never observed %s (observed: %A)" lockArtifact observedEntries)
            observedLocks.IsEmpty

        first.Close()
        second.Close()
        let stderrA = first.Stderr
        let stderrB = second.Stderr
        assertEqual "race process A exit code" 0 first.ExitCode
        assertEqual "race process B exit code" 0 second.ExitCode

        let combinedStderr = (stderrA + "\n" + stderrB).ToLowerInvariant()
        assertTrue "packaged stderr must not mention a file lock" (not (combinedStderr.Contains "file lock"))
        assertTrue "packaged stderr must not mention runtime.lock" (not (combinedStderr.Contains lockArtifact))

        printfn "OK packaged race: one create and one apply serialized; directory stayed ephemeral"
        printfn "observed task-directory snapshots (%d samples, %d distinct): %A" observed.Length observedEntries.Length observedEntries
    finally
        try sampler.Stop() |> ignore with _ -> ()
        try first.Close() with _ -> ()
        try second.Close() with _ -> ()

// --- coverage 4: abandoned-owner mutex recovery -----------------------------

let runAbandonedOwnerTest () =
    let root, catalog, catalogHash = prepareWorkspace "workflow-packaged-abandoned"
    let taskId = "ABN-1"
    let taskDirectory = Path.Combine(root, ".tasks", taskId)
    let sidecar = Path.Combine(taskDirectory, "runtime.json")
    let readyMarker = Path.Combine(root, "mutex-held.marker")

    // Seed the task so the reclaimer has something to mutate.
    let creator = McpSession(startMcpProcess root catalog catalogHash)

    try
        let created = creator.Call("task_create", 1, "task_create", createArgs root taskId)
        assertTrue "abandoned-test seed create succeeded" (not (isToolError created))
        assertEqual "abandoned-test seed revision" 0 ((taskOf created).["stateRevision"].GetValue<int>())
    finally
        creator.Close()

    // A helper subprocess acquires the exact named runtime mutex for this
    // sidecar path and then blocks. Killing it abandons the mutex.
    let helperInfo = ProcessStartInfo("dotnet")
    helperInfo.WorkingDirectory <- repoRoot
    helperInfo.UseShellExecute <- false
    helperInfo.RedirectStandardOutput <- true
    helperInfo.RedirectStandardError <- true
    helperInfo.CreateNoWindow <- true

    for argument in [ "fsi"; "--nologo"; helperScript; sidecar; readyMarker ] do
        helperInfo.ArgumentList.Add argument

    use helper = Process.Start helperInfo
    let helperStdout = helper.StandardOutput.ReadToEndAsync()
    let helperStderr = helper.StandardError.ReadToEndAsync()

    let deadline = DateTime.UtcNow.AddSeconds 60.0

    while not (File.Exists readyMarker) && DateTime.UtcNow < deadline && not helper.HasExited do
        Thread.Sleep 25

    assertTrue "mutex holder signalled acquisition" (File.Exists readyMarker)

    printfn "abandoned-owner helper pid=%d acquired the runtime mutex" helper.Id
    try helper.Kill() with _ -> ()
    helper.WaitForExit 30000 |> ignore

    let reclaimer = McpSession(startMcpProcess root catalog catalogHash)

    try
        let applied = reclaimer.Call("task_apply", 1, "task_apply", applyArgs root taskId 0 (startCommand ()))
        printfn "reclaimer: isError=%b code=%s" (isToolError applied) (if isToolError applied then toolErrorCode applied else "-")
        assertTrue "packaged process acquired the abandoned mutex and applied" (not (isToolError applied))
        assertEqual "reclaimer revision" 1 ((taskOf applied).["stateRevision"].GetValue<int>())
        assertEqual "abandoned run directory contents" [ "runtime.json" ] (enumerateEntries taskDirectory)
        assertTrue "abandoned run leaves no runtime.lock" (not (File.Exists(Path.Combine(taskDirectory, lockArtifact))))
        reclaimer.Close()
        assertEqual "reclaimer exit code" 0 reclaimer.ExitCode
        printfn "OK abandoned-owner recovery: packaged process acquired the abandoned OS mutex"
    finally
        reclaimer.Close()

    printfn "helper stdout: %s" (try helperStdout.Result.Trim() with _ -> "")
    printfn "helper stderr: %s" (try helperStderr.Result.Trim() with _ -> "")

// --- main -------------------------------------------------------------------

printfn "packaged entry: %s (source=%s)" entryDll entrySource

let installedStatus =
    if File.Exists installedEntry then
        let bytes = File.ReadAllBytes installedEntry
        let mutexCount = occurrencesUtf16 mutexNamePattern bytes
        let lockCount = occurrencesUtf16 lockArtifact bytes
        printfn "installed package: %s" installedEntry
        printfn "  mutex-name utf16=%d runtime.lock utf16=%d" mutexCount lockCount

        if mutexCount = 0 || lockCount > 0 then
            printfn "WARNING STALE-INSTALL: the local install predates the ephemeral-mutex coordination."
            printfn "  W6 owns installing the regenerated v1.0.2 distribution; this run verified the repo-built package instead."
            Some false
        else
            Some true
    else
        printfn "installed package: not present at %s" installedEntry
        None

verifyPackagedBinary entryDll

try
    runRaceTest ()
    runAbandonedOwnerTest ()
    printfn "OK packaged runtime coordination proof: ephemeral mutex, no runtime.lock, cross-process serialization, abandoned-owner recovery"

    match installedStatus with
    | Some false -> printfn "NOTE: installed-package verification deferred to W6 (stale local install)."
    | _ -> ()
finally
    for directory in cleanupDirectories do
        if Directory.Exists directory
           && (directory.Contains("workflow-packaged-", StringComparison.Ordinal)) then
            try Directory.Delete(directory, true) with _ -> ()
