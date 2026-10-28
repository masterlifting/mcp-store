// role: test entrypoint

#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Text
open Workflow

[<DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)>]
extern int createFifo(string path, uint32 mode)

let assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let evidenceKind value =
    match parseEvidenceKind value with
    | Ok kind -> kind
    | Error error -> failwithf "invalid evidence-kind test fixture '%s': %s" value (renderError error)

let expectOk name (result: Async<Result<'a, RuntimeError>>) : Async<'a> =
    async {
        match! result with
        | Ok value -> return value
        | Error error -> return failwithf "%s: expected Ok, got Error %s" name (renderError error)
    }

let expectRejected (name: string) (fragment: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ -> return failwithf "%s: expected rejection" name
        | Error error ->
            let message = renderError error
            if message.Contains(fragment, StringComparison.Ordinal) then return ()
            else return failwithf "%s: expected '%s', got '%s'" name fragment message
    }

let expectAnyError (name: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ -> return failwithf "%s: expected a safe rejection" name
        | Error error ->
            printfn "%s -> rejected: %s" name (renderError error)
            return ()
    }

let expectRejectedWithin (name: string) (fragment: string) (timeout: TimeSpan) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        let pending = Async.StartAsTask result
        let! outcome = pending.WaitAsync(timeout) |> Async.AwaitTask

        match outcome with
        | Ok _ -> return failwithf "%s: expected rejection" name
        | Error error ->
            let message = renderError error
            if message.Contains(fragment, StringComparison.Ordinal) then return ()
            else return failwithf "%s: expected '%s', got '%s'" name fragment message
    }

let request id =
    { Id = id
      Title = "Supporting artifact contract"
      Kind = Execution
      AcceptanceCriteria = [ ("AC1", "Keep supporting material outside runtime state") ]
      WorkItems = [ { Id = "W1"; Title = "Verify the task"; DependsOn = []; Children = [] } ] }

let taskDirectory root id = Path.Combine(root, ".tasks", id)
let sidecar root id = Path.Combine(taskDirectory root id, SidecarFileName)

let createDirectoryLink (link: string) (target: string) : Async<unit> =
    async {
        if OperatingSystem.IsWindows() then
            let startInfo = ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            use child = Process.Start startInfo
            let stdoutTask = child.StandardOutput.ReadToEndAsync()
            let stderrTask = child.StandardError.ReadToEndAsync()

            try
                do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 30.0) |> Async.AwaitTask
            with :? TimeoutException ->
                if not child.HasExited then child.Kill true
                do! child.WaitForExitAsync() |> Async.AwaitTask
                return failwithf "junction creation timed out: %s" link

            let! stdout = stdoutTask |> Async.AwaitTask
            let! stderr = stderrTask |> Async.AwaitTask
            if child.ExitCode <> 0 then failwithf "could not create junction '%s': %s %s" link stdout stderr
        else
            Directory.CreateSymbolicLink(link, target) |> ignore
    }

let removeLink (path: string) =
    if Directory.Exists path then Directory.Delete(path, false)
    elif File.Exists path then File.Delete path

let rejectExistingOperations name root id fragment : Async<unit> =
    async {
        do! expectRejected $"{name}: get" fragment (getTask root id)
        do! expectRejected $"{name}: apply" fragment (applyTask root id 0 (StartWorkItem "W1"))
        do! expectRejected $"{name}: validate" fragment (validateTask root id)
    }

let rejectSpecialFileOperations name root id fragment timeout : Async<unit> =
    async {
        do! expectRejectedWithin $"{name}: get" fragment timeout (getTask root id)
        do! expectRejectedWithin $"{name}: apply" fragment timeout (applyTask root id 0 (StartWorkItem "W1"))
        do! expectRejectedWithin $"{name}: validate" fragment timeout (validateTask root id)
    }

let tempRoot = Path.Combine(Path.GetTempPath(), "opencode", $"workflow-supporting-artifacts-{Guid.NewGuid():N}")
Directory.CreateDirectory tempRoot |> ignore
let reparseEntries = ResizeArray<string>()
let activeId = "SUP-1"
let activeSpecPath = Path.Combine(taskDirectory tempRoot activeId, "docs", "SPEC.md")

async {
    try
        let! _ = expectOk "create active task" (createTask tempRoot (request activeId))
        let activeDirectory = taskDirectory tempRoot activeId
        let docsDirectory = Path.Combine(activeDirectory, "docs")
        let scriptsDirectory = Path.Combine(activeDirectory, "scripts")
        let tempDirectory = Path.Combine(activeDirectory, "temp")
        let specPath = activeSpecPath
        let referenceDirectory = Path.Combine(docsDirectory, "references")
        let referencePath = Path.Combine(referenceDirectory, "issue.md")
        let nestedScriptDirectory = Path.Combine(scriptsDirectory, "helpers")
        let nestedScriptPath = Path.Combine(nestedScriptDirectory, "check.fsx")
        let tempNotePath = Path.Combine(tempDirectory, "scratch.txt")
        let specBytes = Encoding.UTF8.GetBytes("task specification\r\n")
        let referenceBytes = Encoding.UTF8.GetBytes("reference document\r\n")
        let nestedScriptBytes = Encoding.UTF8.GetBytes("printfn \"support script\"\r\n")
        let tempNoteBytes = Encoding.UTF8.GetBytes("temporary note\r\n")
        Directory.CreateDirectory docsDirectory |> ignore
        Directory.CreateDirectory tempDirectory |> ignore
        File.WriteAllBytes(specPath, specBytes)
        if OperatingSystem.IsWindows() then
            File.SetAttributes(specPath, FileAttributes.ReadOnly ||| FileAttributes.Hidden)
        else
            File.SetUnixFileMode(specPath, UnixFileMode.UserRead ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
        Directory.CreateDirectory referenceDirectory |> ignore
        Directory.CreateDirectory nestedScriptDirectory |> ignore
        File.WriteAllBytes(referencePath, referenceBytes)
        File.WriteAllBytes(nestedScriptPath, nestedScriptBytes)
        File.WriteAllBytes(tempNotePath, tempNoteBytes)

        let opaqueTarget = Path.Combine(tempRoot, "opaque-target")
        Directory.CreateDirectory opaqueTarget |> ignore
        File.WriteAllText(Path.Combine(opaqueTarget, "outside.txt"), "outside the task tree")
        let nestedLink = Path.Combine(docsDirectory, "opaque-link")
        do! createDirectoryLink nestedLink opaqueTarget
        reparseEntries.Add nestedLink
        assertTrue "nested link is a reparse point" ((File.GetAttributes nestedLink &&& FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint)

        let! initial = expectOk "get task with docs/references/scripts/temp" (getTask tempRoot activeId)
        assertEqual "initial revision" 0 initial.StateRevision

        let decision =
            { Kind = DesignDecision
              Targets = [ OtherDecisionTarget "supporting-artifact-contract" ]
              Rationale = "Record a coordinator decision for supporting-artifact invariance coverage" }

        let! decided = expectOk "add decision" (applyTask tempRoot activeId 0 (AddDecision decision))
        assertEqual "decision increments revision" 1 decided.StateRevision
        assertEqual "fixture has a coordinator-authority decision" Coordinator decided.Decisions.Head.Authority

        let guard =
            { Id = "G1"
              Target = TaskTarget
              Checkpoint = BeforeComplete
              Requirement =
                EvidenceRequired
                    { Kind = evidenceKind "test"
                      MinimumCount = 1
                      ProducerRole = None
                      RequireIndependentProducer = false }
              Applicability = Always
              Waiver = NotWaivable }

        let! guarded = expectOk "add guard" (applyTask tempRoot activeId 1 (AddGuard guard))
        assertEqual "guard increments revision" 2 guarded.StateRevision
        assertEqual "fixture has a task-design guard" TaskDesign guarded.Guards.Head.Origin

        let! applied = expectOk "apply task with opaque nested link" (applyTask tempRoot activeId 2 (StartWorkItem "W1"))
        assertEqual "apply revision" 3 applied.StateRevision
        let! afterApply = expectOk "get task after apply" (getTask tempRoot activeId)
        assertEqual "get returns the applied task model" applied afterApply
        let! validated = expectOk "validate task with all supporting entries" (validateTask tempRoot activeId)
        assertEqual "validate revision" 3 validated.StateRevision
        assertEqual "validate returns the applied task model" applied validated
        assertEqual "spec bytes survive apply" specBytes (File.ReadAllBytes specPath)
        assertEqual "nested reference bytes survive apply" referenceBytes (File.ReadAllBytes referencePath)
        assertEqual "nested script bytes survive apply" nestedScriptBytes (File.ReadAllBytes nestedScriptPath)
        assertEqual "temp note bytes survive apply" tempNoteBytes (File.ReadAllBytes tempNotePath)
        if OperatingSystem.IsWindows() then
            assertTrue "read-only and hidden spec attributes survive reads" ((File.GetAttributes specPath &&& (FileAttributes.ReadOnly ||| FileAttributes.Hidden)) = (FileAttributes.ReadOnly ||| FileAttributes.Hidden))
            File.SetAttributes(specPath, FileAttributes.Normal)
        else
            let specMode = File.GetUnixFileMode specPath
            let writeBits = UnixFileMode.UserWrite ||| UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite
            assertTrue "read-only spec remains non-writable" ((specMode &&& writeBits) = enum<UnixFileMode> 0)
        assertTrue "nested link target remains untouched" (File.Exists(Path.Combine(opaqueTarget, "outside.txt")))
        let sidecarBytesBeforeCreate = File.ReadAllBytes(sidecar tempRoot activeId)
        do!
            expectRejected
                "re-create with supporting entries does not overwrite the active task"
                "contains runtime state 'runtime.json'"
                (createTask tempRoot (request activeId))
        assertEqual "re-create preserves runtime bytes" sidecarBytesBeforeCreate (File.ReadAllBytes(sidecar tempRoot activeId))
        assertEqual "re-create preserves spec bytes" specBytes (File.ReadAllBytes specPath)
        assertEqual "re-create preserves nested reference bytes" referenceBytes (File.ReadAllBytes referencePath)
        assertEqual "re-create preserves nested script bytes" nestedScriptBytes (File.ReadAllBytes nestedScriptPath)
        assertEqual "re-create preserves temp note bytes" tempNoteBytes (File.ReadAllBytes tempNotePath)

        match! applyTask tempRoot activeId 0 (StartWorkItem "W1") with
        | Error(Conflict(expected, actual)) ->
            assertEqual "stale CAS expected revision" 0 expected
            assertEqual "stale CAS observes the committed revision" 3 actual
        | Error error -> failwithf "stale CAS returned an unexpected error: %s" (renderError error)
        | Ok _ -> failwith "stale CAS unexpectedly succeeded"
        let! afterStaleApply = expectOk "get after stale CAS" (getTask tempRoot activeId)
        assertEqual "stale CAS leaves the managed task model unchanged" applied afterStaleApply
        assertEqual "stale CAS preserves spec bytes" specBytes (File.ReadAllBytes specPath)
        assertEqual "stale CAS preserves nested reference bytes" referenceBytes (File.ReadAllBytes referencePath)
        assertEqual "stale CAS preserves nested script bytes" nestedScriptBytes (File.ReadAllBytes nestedScriptPath)
        assertEqual "stale CAS preserves temp note bytes" tempNoteBytes (File.ReadAllBytes tempNotePath)

        let missingId = "MIS-1"
        let missingDirectory = taskDirectory tempRoot missingId
        Directory.CreateDirectory(Path.Combine(missingDirectory, "docs", "references")) |> ignore
        Directory.CreateDirectory(Path.Combine(missingDirectory, "scripts")) |> ignore
        Directory.CreateDirectory(Path.Combine(missingDirectory, "temp")) |> ignore
        let missingSpec = Path.Combine(missingDirectory, "docs", "spec.md")
        let missingDoc = Path.Combine(missingDirectory, "docs", "references", "keep.md")
        File.WriteAllText(missingSpec, "pre-existing specification")
        File.WriteAllText(missingDoc, "pre-existing document")
        do! expectRejected "allowlisted prepopulation get remains missing" "runtime sidecar does not exist" (getTask tempRoot missingId)
        do! expectRejected "allowlisted prepopulation apply remains missing" "runtime sidecar does not exist" (applyTask tempRoot missingId 0 (StartWorkItem "W1"))
        do! expectRejected "allowlisted prepopulation validate remains missing" "runtime sidecar does not exist" (validateTask tempRoot missingId)
        do! expectRejected "create refuses allowlisted-only prepopulation" "task directory is already occupied" (createTask tempRoot (request missingId))
        assertTrue "occupied create creates no sidecar" (not (File.Exists(sidecar tempRoot missingId)))
        assertEqual "occupied spec bytes preserved" "pre-existing specification" (File.ReadAllText missingSpec)
        assertEqual "occupied document bytes preserved" "pre-existing document" (File.ReadAllText missingDoc)

        let missingArtifactCases = [ "docs"; "scripts"; "temp" ]

        for index, name in List.indexed missingArtifactCases do
            let id = $"MSO-{index + 1}"
            let directory = taskDirectory tempRoot id
            Directory.CreateDirectory directory |> ignore
            let entry = Path.Combine(directory, name)
            Directory.CreateDirectory entry |> ignore

            let originalEntries = Directory.GetFileSystemEntries(directory)
            do! expectRejected $"{name}-only get remains missing" "runtime sidecar does not exist" (getTask tempRoot id)
            do! expectRejected $"{name}-only apply remains missing" "runtime sidecar does not exist" (applyTask tempRoot id 0 (StartWorkItem "W1"))
            do! expectRejected $"{name}-only validate remains missing" "runtime sidecar does not exist" (validateTask tempRoot id)
            do! expectRejected $"create refuses {name}-only prepopulation" "task directory is already occupied" (createTask tempRoot (request id))

            assertTrue $"{name}-only create adds no sidecar" (not (File.Exists(sidecar tempRoot id)))
            assertEqual $"{name}-only directory entries remain unchanged" originalEntries (Directory.GetFileSystemEntries(directory))
            assertTrue $"{name}-only directory remains empty" (Seq.isEmpty (Directory.EnumerateFileSystemEntries entry))

        let emptyArtifactsId = "EMP-2"
        let! _ = expectOk "create before empty supporting directories" (createTask tempRoot (request emptyArtifactsId))
        let emptyArtifactsDirectory = taskDirectory tempRoot emptyArtifactsId
        let emptyDocs = Path.Combine(emptyArtifactsDirectory, "docs")
        let emptyScripts = Path.Combine(emptyArtifactsDirectory, "scripts")
        let emptyTemp = Path.Combine(emptyArtifactsDirectory, "temp")
        Directory.CreateDirectory emptyDocs |> ignore
        Directory.CreateDirectory emptyScripts |> ignore
        Directory.CreateDirectory emptyTemp |> ignore
        let! emptyArtifactsInitial = expectOk "get task with empty docs/scripts/temp" (getTask tempRoot emptyArtifactsId)
        let! emptyArtifactsApplied =
            applyTask tempRoot emptyArtifactsId emptyArtifactsInitial.StateRevision (StartWorkItem "W1")
            |> expectOk "apply task with empty docs/scripts/temp"
        let! emptyArtifactsValidated = expectOk "validate task with empty docs/scripts/temp" (validateTask tempRoot emptyArtifactsId)
        assertEqual "empty docs/scripts/temp apply advances revision" 1 emptyArtifactsApplied.StateRevision
        assertEqual "empty docs/scripts/temp validate returns applied revision" emptyArtifactsApplied.StateRevision emptyArtifactsValidated.StateRevision
        assertTrue "empty docs remains empty" (Seq.isEmpty (Directory.EnumerateFileSystemEntries emptyDocs))
        assertTrue "empty scripts remains empty" (Seq.isEmpty (Directory.EnumerateFileSystemEntries emptyScripts))
        assertTrue "empty temp remains empty" (Seq.isEmpty (Directory.EnumerateFileSystemEntries emptyTemp))

        let emptyId = "EMP-1"
        Directory.CreateDirectory(taskDirectory tempRoot emptyId) |> ignore
        let! _ = expectOk "existing empty directory remains reusable" (createTask tempRoot (request emptyId))
        assertTrue "empty directory reuse creates runtime sidecar" (File.Exists(sidecar tempRoot emptyId))

        let unknownCases =
            [ "SPEC.md", false
              "TASK.md", false
              "references", true
              "runtime.lock", false
              "unknown.bin", false ]

        for index, (name, isDirectory) in List.indexed unknownCases do
            let id = $"UNK-{index + 1}"
            let directory = taskDirectory tempRoot id
            Directory.CreateDirectory directory |> ignore
            let entry = Path.Combine(directory, name)
            if isDirectory then
                Directory.CreateDirectory entry |> ignore
                File.WriteAllText(Path.Combine(entry, "keep.md"), "legacy data")
            else
                File.WriteAllText(entry, "unknown data")
            do! expectRejected $"create rejects root {name}" "unsupported entry" (createTask tempRoot (request id))
            assertTrue $"create preserves root {name}" (Directory.Exists entry || File.Exists entry)
            assertTrue $"create does not add sidecar beside {name}" (not (File.Exists(sidecar tempRoot id)))

            let existingId = $"BAD-{index + 1}"
            let! _ = expectOk $"create existing-task case for {name}" (createTask tempRoot (request existingId))
            let existingDirectory = taskDirectory tempRoot existingId
            let existingEntry = Path.Combine(existingDirectory, name)
            if isDirectory then
                Directory.CreateDirectory existingEntry |> ignore
                File.WriteAllText(Path.Combine(existingEntry, "keep.md"), "must remain untouched")
            else
                File.WriteAllText(existingEntry, "must remain untouched")
            do! rejectExistingOperations $"existing task with root {name}" tempRoot existingId "unsupported entry"
            if isDirectory then
                assertEqual $"failed operations preserve {name} child" "must remain untouched" (File.ReadAllText(Path.Combine(existingEntry, "keep.md")))
            else
                assertEqual $"failed operations preserve {name} bytes" "must remain untouched" (File.ReadAllText existingEntry)

        let invalidTypes =
            [ "SPEC.md", true
              "docs", false
              "scripts", false
              "temp", false ]

        for index, (name, isDirectory) in List.indexed invalidTypes do
            let id = $"TYP-{index + 1}"
            let! _ = expectOk $"create before invalid {name} type" (createTask tempRoot (request id))
            let entry = Path.Combine(taskDirectory tempRoot id, name)
            if isDirectory then Directory.CreateDirectory entry |> ignore
            else File.WriteAllText(entry, "wrong entry type")
            do! rejectExistingOperations $"invalid {name} root type" tempRoot id "unsupported entry"
            if not isDirectory then assertEqual $"wrong-type {name} preserved" "wrong entry type" (File.ReadAllText entry)

        let sidecarDirectoryId = "TYP-9"
        Directory.CreateDirectory(sidecar tempRoot sidecarDirectoryId) |> ignore
        do! expectRejected "runtime.json directory rejected by get" "unsupported entry" (getTask tempRoot sidecarDirectoryId)
        do! expectRejected "runtime.json directory rejected by apply" "unsupported entry" (applyTask tempRoot sidecarDirectoryId 0 (StartWorkItem "W1"))
        do! expectRejected "runtime.json directory rejected by validate" "unsupported entry" (validateTask tempRoot sidecarDirectoryId)

        let caseId = "CAS-1"
        let! _ = expectOk "create for case comparison" (createTask tempRoot (request caseId))
        Directory.CreateDirectory(Path.Combine(taskDirectory tempRoot caseId, "DOCS")) |> ignore
        if OperatingSystem.IsWindows() then
            let! _ = expectOk "Windows allowlist comparison ignores case" (getTask tempRoot caseId)
            printfn "Windows root allowlist comparison: ordinal-ignore-case"
        else
            do! expectRejected "non-Windows allowlist comparison is ordinal" "unsupported entry" (getTask tempRoot caseId)
            printfn "Non-Windows root allowlist comparison: ordinal"

        let docsLinkId = "LNK-1"
        let! _ = expectOk "create before docs junction" (createTask tempRoot (request docsLinkId))
        let docsTarget = Path.Combine(tempRoot, "docs-link-target")
        Directory.CreateDirectory docsTarget |> ignore
        let docsLink = Path.Combine(taskDirectory tempRoot docsLinkId, "docs")
        do! createDirectoryLink docsLink docsTarget
        reparseEntries.Add docsLink
        do! rejectExistingOperations "allowlisted docs root reparse" tempRoot docsLinkId "reparse point"

        let scriptsLinkId = "LNK-2"
        let! _ = expectOk "create before scripts junction" (createTask tempRoot (request scriptsLinkId))
        let scriptsTarget = Path.Combine(tempRoot, "scripts-link-target")
        Directory.CreateDirectory scriptsTarget |> ignore
        let scriptsLink = Path.Combine(taskDirectory tempRoot scriptsLinkId, "scripts")
        do! createDirectoryLink scriptsLink scriptsTarget
        reparseEntries.Add scriptsLink
        do! rejectExistingOperations "allowlisted scripts root reparse" tempRoot scriptsLinkId "reparse point"

        let tempLinkId = "LNK-3"
        let! _ = expectOk "create before temp junction" (createTask tempRoot (request tempLinkId))
        let tempTarget = Path.Combine(tempRoot, "temp-link-target")
        Directory.CreateDirectory tempTarget |> ignore
        let tempLink = Path.Combine(taskDirectory tempRoot tempLinkId, "temp")
        do! createDirectoryLink tempLink tempTarget
        reparseEntries.Add tempLink
        do! rejectExistingOperations "allowlisted temp root reparse" tempRoot tempLinkId "reparse point"

        if OperatingSystem.IsWindows() then
            printfn "Windows reparse coverage: task-root and sidecar links plus docs/scripts/temp junctions"
        else
            printfn "Unix reparse coverage: task-root and sidecar links plus docs/scripts/temp symbolic links"

        if OperatingSystem.IsWindows() then
            printfn "SKIP Unix special-file cases: this test host is Windows"
        else
            let specialFileTimeout = TimeSpan.FromSeconds 5.0
            let fifoId = "FIF-1"
            let! _ = expectOk "create before FIFO" (createTask tempRoot (request fifoId))
            let fifoPath = Path.Combine(taskDirectory tempRoot fifoId, "docs")
            if createFifo(fifoPath, 384u) <> 0 then
                failwithf "mkfifo failed with errno %d" (Marshal.GetLastWin32Error())
            do! rejectSpecialFileOperations "FIFO at allowlisted docs" tempRoot fifoId "unsupported entry" specialFileTimeout

            let runtimeFifoId = "FIF-2"
            let! _ = expectOk "create before runtime.json FIFO" (createTask tempRoot (request runtimeFifoId))
            File.Delete(sidecar tempRoot runtimeFifoId)
            let runtimeFifo = sidecar tempRoot runtimeFifoId
            if createFifo(runtimeFifo, 384u) <> 0 then
                failwithf "mkfifo for runtime.json failed with errno %d" (Marshal.GetLastWin32Error())
            do! rejectSpecialFileOperations "FIFO at runtime.json" tempRoot runtimeFifoId "unsupported entry" specialFileTimeout

            let socketId = "SOC-1"
            let! _ = expectOk "create before Unix socket" (createTask tempRoot (request socketId))
            let socketPath = Path.Combine(taskDirectory tempRoot socketId, "docs")
            use listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            listener.Bind(UnixDomainSocketEndPoint socketPath)
            do! rejectSpecialFileOperations "Unix socket at allowlisted docs" tempRoot socketId "unsupported entry" specialFileTimeout
            listener.Dispose()
            File.Delete socketPath

            let runtimeSocketId = "SOC-2"
            let! _ = expectOk "create before runtime.json socket" (createTask tempRoot (request runtimeSocketId))
            File.Delete(sidecar tempRoot runtimeSocketId)
            let runtimeSocket = sidecar tempRoot runtimeSocketId
            use runtimeListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            runtimeListener.Bind(UnixDomainSocketEndPoint runtimeSocket)
            do! rejectSpecialFileOperations "Unix socket at runtime.json" tempRoot runtimeSocketId "unsupported entry" specialFileTimeout
            runtimeListener.Dispose()
            File.Delete runtimeSocket
            printfn "Unix special-file coverage: bounded FIFO and Unix-domain socket classification at docs and runtime.json"

        printfn "OK supporting-artifact layout: runtime.json plus docs/scripts/temp roots work, occupied creation fails closed, and nested links remain opaque"
    finally
        for link in reparseEntries |> Seq.rev do
            removeLink link

        // The Windows read-only attribute blocks deletion, so the fixture file is made writable again before the tree is removed.
        if OperatingSystem.IsWindows() && File.Exists activeSpecPath then
            File.SetAttributes(activeSpecPath, FileAttributes.Normal)

        if Directory.Exists tempRoot && tempRoot.Contains("workflow-supporting-artifacts-", StringComparison.Ordinal) then
            Directory.Delete(tempRoot, true)
}
// Standalone entry bridge: FSI owns the process entry for this asynchronous script.
|> Async.RunSynchronously
