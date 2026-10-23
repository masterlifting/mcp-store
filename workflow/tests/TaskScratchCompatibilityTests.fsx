// role: test entrypoint

#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open Workflow

let assertEqual name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertContains name (fragment: string) (text: string) =
    if not (text.Contains(fragment, StringComparison.Ordinal)) then
        failwithf "%s: expected '%s' in '%s'" name fragment text

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

let request id =
    { Id = id
      Title = "TaskScratch compatibility"
      Kind = Execution
      AcceptanceCriteria = [ ("AC1", "Keep promoted files outside managed task state") ]
      WorkItems =
          [ { Id = "W1"; Title = "Establish the contract"; DependsOn = []; Children = [] }
            { Id = "W2"; Title = "Update after promotion"; DependsOn = []; Children = [] } ] }

let removeLink path =
    if Directory.Exists path then Directory.Delete(path, false)
    elif File.Exists path then File.Delete path

let createDirectoryLink (link: string) (target: string) : Async<unit> =
    async {
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
    }

let runConsumer (consumerPath: string) (workingDirectory: string) (arguments: string list) : Async<int * string> =
    async {
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "dotnet"
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.ArgumentList.Add "fsi"
        startInfo.ArgumentList.Add "--exec"
        startInfo.ArgumentList.Add "--quiet"
        startInfo.ArgumentList.Add consumerPath
        arguments |> List.iter startInfo.ArgumentList.Add

        use child = Process.Start startInfo
        let stdoutTask = child.StandardOutput.ReadToEndAsync()
        let stderrTask = child.StandardError.ReadToEndAsync()

        try
            do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 90.0) |> Async.AwaitTask
        with :? TimeoutException ->
            if not child.HasExited then child.Kill true
            do! child.WaitForExitAsync() |> Async.AwaitTask
            return failwithf "TaskScratch command timed out: %s" (String.concat " " arguments)

        let! stdout = stdoutTask |> Async.AwaitTask
        let! stderr = stderrTask |> Async.AwaitTask
        return child.ExitCode, (stdout + Environment.NewLine + stderr).Trim()
    }

let run () : Async<unit> =
    async {
        let profile = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        let defaultConsumer =
            Path.Combine(profile, ".config", "opencode", "skills", "task", "scripts", "TaskScratch.fsx")

        let consumerPath =
            match Environment.GetEnvironmentVariable "OPENCODE_TASKSCRATCH_FSX" with
            | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
            | _ -> defaultConsumer

        let explicitConsumer = not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable "OPENCODE_TASKSCRATCH_FSX"))

        if not (File.Exists consumerPath) then
            if explicitConsumer then failwithf "OPENCODE_TASKSCRATCH_FSX does not exist: %s" consumerPath
            else printfn "SKIP TaskScratch promote smoke: consumer script not found at %s" consumerPath
        else
            let consumerHash =
                SHA256.HashData(File.ReadAllBytes consumerPath)
                |> Convert.ToHexString
                |> fun value -> value.ToLowerInvariant()

            printfn "TaskScratch consumer: %s (sha256=%s)" consumerPath consumerHash

            if not (OperatingSystem.IsWindows()) then
                printfn "SKIP TaskScratch promote smoke: the current consumer fails closed outside Windows"
            else
                let projectRoot =
                    Path.Combine(Path.GetTempPath(), "opencode", $"workflow-task-scratch-compat-{Guid.NewGuid():N}")

                let taskId = $"GHI-{DateTime.UtcNow:yyyyMMddHHmmssfff}"
                let runId = $"ghi36-{Guid.NewGuid():N}"
                let scratchRoot = Path.Combine(Path.GetTempPath(), "opencode", "tasks", taskId, runId)
                let cleanupLinks = ResizeArray<string>()
                Directory.CreateDirectory projectRoot |> ignore

                try
                    let! _ = expectOk "create active task" (createTask projectRoot (request taskId))
                    let! created = expectOk "read initial task" (getTask projectRoot taskId)

                    let decision =
                        { Kind = DesignDecision
                          Targets = [ OtherDecisionTarget "task-scratch-compatibility" ]
                          Rationale = "Record a coordinator decision for artifact invariance coverage" }

                    let! decided = applyTask projectRoot taskId created.StateRevision (AddDecision decision) |> expectOk "add decision"
                    assertEqual "decision increments revision" (created.StateRevision + 1) decided.StateRevision

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

                    let! guarded = applyTask projectRoot taskId decided.StateRevision (AddGuard guard) |> expectOk "add guard"
                    let! started = applyTask projectRoot taskId guarded.StateRevision (StartWorkItem "W1") |> expectOk "baseline contract"
                    assertEqual "contract is baselined" Baselined started.ContractState
                    assertTrue "contract fingerprint is populated" (not (String.IsNullOrWhiteSpace started.ContractFingerprint))
                    assertTrue "profile fingerprint is populated" (not (String.IsNullOrWhiteSpace started.ProfileFingerprint))
                    assertEqual "fixture has a task-design guard" TaskDesign started.Guards.Head.Origin
                    assertEqual "fixture has a coordinator-authority decision" Coordinator started.Decisions.Head.Authority
                    assertEqual "fixture lifecycle is open" "open" started.Lifecycle

                    let! baseline = expectOk "read semantic baseline" (getTask projectRoot taskId)
                    assertEqual "baseline retains metadata" started baseline

                    assertTrue "unique scratch root is not occupied" (not (Directory.Exists scratchRoot))
                    let! createCode, createOutput = runConsumer consumerPath projectRoot [ "create"; taskId; "--run"; runId ]
                    assertEqual "TaskScratch create exit code" 0 createCode
                    assertTrue "TaskScratch created the expected root" (Directory.Exists scratchRoot)
                    assertContains "TaskScratch create output" scratchRoot createOutput

                    let documentationName = "promoted-evidence.md"
                    let scriptName = "promoted-check.fsx"
                    let documentationBytes = Encoding.UTF8.GetBytes("promoted document — original bytes\r\n")
                    let scriptBytes = Encoding.UTF8.GetBytes("printfn \"promoted script\"\r\n")
                    File.WriteAllBytes(Path.Combine(scratchRoot, documentationName), documentationBytes)
                    File.WriteAllBytes(Path.Combine(scratchRoot, scriptName), scriptBytes)

                    let! registerCode, registerOutput =
                        runConsumer consumerPath projectRoot [ "register"; scratchRoot; documentationName; scriptName ]

                    assertEqual "TaskScratch register exit code" 0 registerCode
                    assertContains "TaskScratch register output" "registered 2 path(s)" registerOutput

                    let! docsCode, docsOutput =
                        runConsumer consumerPath projectRoot [ "promote"; scratchRoot; documentationName; "--destination"; "docs" ]

                    assertEqual "TaskScratch docs promote exit code" 0 docsCode
                    assertContains "TaskScratch docs promote result" $".tasks/{taskId}/docs/{documentationName}" docsOutput

                    let! scriptsCode, scriptsOutput =
                        runConsumer consumerPath projectRoot [ "promote"; scratchRoot; scriptName; "--destination"; "scripts" ]

                    assertEqual "TaskScratch scripts promote exit code" 0 scriptsCode
                    assertContains "TaskScratch scripts promote result" $".tasks/{taskId}/scripts/{scriptName}" scriptsOutput

                    let taskDirectory = Path.Combine(projectRoot, ".tasks", taskId)
                    let specPath = Path.Combine(taskDirectory, "SPEC.md")
                    let docsPath = Path.Combine(taskDirectory, "docs", documentationName)
                    let scriptsPath = Path.Combine(taskDirectory, "scripts", scriptName)
                    let docsReferenceDirectory = Path.Combine(taskDirectory, "docs", "references")
                    let docsReferencePath = Path.Combine(docsReferenceDirectory, "nested-reference.md")
                    let nestedDocsTarget = Path.Combine(projectRoot, "nested-docs-target")
                    let nestedDocsLink = Path.Combine(taskDirectory, "docs", "opaque-link")
                    File.WriteAllText(specPath, "SPEC before edits")
                    Directory.CreateDirectory docsReferenceDirectory |> ignore
                    Directory.CreateDirectory nestedDocsTarget |> ignore
                    File.WriteAllText(docsReferencePath, "nested reference before edits")
                    File.WriteAllText(Path.Combine(nestedDocsTarget, "outside.md"), "opaque target")
                    do! createDirectoryLink nestedDocsLink nestedDocsTarget
                    cleanupLinks.Add nestedDocsLink

                    assertEqual "consumer-promoted document bytes" documentationBytes (File.ReadAllBytes docsPath)
                    assertEqual "consumer-promoted script bytes" scriptBytes (File.ReadAllBytes scriptsPath)

                    let! afterPromotion = expectOk "get after direct promotions and nested reference" (getTask projectRoot taskId)
                    assertEqual "promotions and nested documents do not change managed task" baseline afterPromotion
                    let! afterPromotionValidation = expectOk "validate after direct promotions" (validateTask projectRoot taskId)
                    assertEqual "promotion validation returns unchanged task" baseline afterPromotionValidation

                    let! applied =
                        applyTask projectRoot taskId baseline.StateRevision (StartWorkItem "W2")
                        |> expectOk "apply after direct promotions"

                    assertEqual "apply advances only normal task revision" (baseline.StateRevision + 1) applied.StateRevision
                    assertEqual "contract revision survives supporting artifacts" baseline.ContractRevision applied.ContractRevision
                    assertEqual "contract fingerprint survives supporting artifacts" baseline.ContractFingerprint applied.ContractFingerprint
                    assertEqual "profile survives supporting artifacts" baseline.Profile applied.Profile
                    assertEqual "profile fingerprint survives supporting artifacts" baseline.ProfileFingerprint applied.ProfileFingerprint
                    assertEqual "guards survive supporting artifacts" baseline.Guards applied.Guards
                    assertEqual "guard-key provenance survives supporting artifacts" baseline.ProfileGuardKeys applied.ProfileGuardKeys
                    assertEqual "decisions and authority survive supporting artifacts" baseline.Decisions applied.Decisions
                    assertEqual "lifecycle survives supporting artifacts" baseline.Lifecycle applied.Lifecycle
                    assertEqual "docs promotion survives task apply" documentationBytes (File.ReadAllBytes docsPath)
                    assertEqual "scripts promotion survives task apply" scriptBytes (File.ReadAllBytes scriptsPath)

                    let editedSpec = Encoding.UTF8.GetBytes("SPEC edited after promotion")
                    let editedDocument = Encoding.UTF8.GetBytes("documentation edited after promotion\r\n")
                    let editedReference = Encoding.UTF8.GetBytes("nested reference edited after promotion\r\n")
                    let editedScript = Encoding.UTF8.GetBytes("printfn \"edited script\"\r\n")
                    File.WriteAllBytes(specPath, editedSpec)
                    File.WriteAllBytes(docsPath, editedDocument)
                    File.WriteAllBytes(docsReferencePath, editedReference)
                    File.WriteAllBytes(scriptsPath, editedScript)

                    let! afterEdits = expectOk "get after document edits" (getTask projectRoot taskId)
                    assertEqual "document edits leave entire task model unchanged" applied afterEdits
                    let! afterEditValidation = expectOk "validate after document edits" (validateTask projectRoot taskId)
                    assertEqual "document edits leave validation model unchanged" applied afterEditValidation

                    match! applyTask projectRoot taskId baseline.StateRevision (StartWorkItem "W2") with
                    | Error(Conflict(expected, actual)) ->
                        assertEqual "stale CAS expected revision after document edits" baseline.StateRevision expected
                        assertEqual "stale CAS observes only committed Workflow apply" applied.StateRevision actual
                    | Error error -> failwithf "stale CAS after document edits returned unexpected error: %s" (renderError error)
                    | Ok _ -> failwith "stale CAS after document edits unexpectedly succeeded"

                    let! afterStaleApply = expectOk "get after stale CAS" (getTask projectRoot taskId)
                    assertEqual "stale CAS leaves managed state unchanged" applied afterStaleApply
                    assertEqual "edited SPEC remains untouched" editedSpec (File.ReadAllBytes specPath)
                    assertEqual "edited document remains untouched" editedDocument (File.ReadAllBytes docsPath)
                    assertEqual "edited nested reference remains untouched" editedReference (File.ReadAllBytes docsReferencePath)
                    assertEqual "edited script remains untouched" editedScript (File.ReadAllBytes scriptsPath)
                    assertTrue "nested task-document link target is not removed" (File.Exists(Path.Combine(nestedDocsTarget, "outside.md")))

                    let consumerLinkTarget = Path.Combine(projectRoot, "scratch-link-target")
                    Directory.CreateDirectory consumerLinkTarget |> ignore
                    File.WriteAllText(Path.Combine(consumerLinkTarget, "outside.txt"), "consumer boundary sentinel")
                    let consumerLink = Path.Combine(scratchRoot, "linked-source")
                    do! createDirectoryLink consumerLink consumerLinkTarget
                    cleanupLinks.Add consumerLink

                    let! linkedRegisterCode, linkedRegisterOutput =
                        runConsumer consumerPath projectRoot [ "register"; scratchRoot; "linked-source"; "outside.txt" ]

                    assertEqual "TaskScratch rejects a reparse ancestor" 1 linkedRegisterCode
                    assertContains "TaskScratch reparse diagnostic" "reparse point" linkedRegisterOutput
                    assertEqual "consumer traversal does not alter outside sentinel" "consumer boundary sentinel" (File.ReadAllText(Path.Combine(consumerLinkTarget, "outside.txt")))

                    printfn "OK TaskScratch compatibility: direct docs/scripts promotion, byte preservation, opaque nested documents, state/CAS invariance, and no-follow consumer traversal"
                finally
                    for link in cleanupLinks |> Seq.rev do
                        removeLink link

                    if Directory.Exists scratchRoot
                       && Path.GetFullPath(scratchRoot).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "opencode", "tasks")) + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) then
                        Directory.Delete(scratchRoot, true)

                    let scratchTaskDirectory = Path.GetDirectoryName scratchRoot
                    if Directory.Exists scratchTaskDirectory && Seq.isEmpty (Directory.EnumerateFileSystemEntries scratchTaskDirectory) then
                        Directory.Delete scratchTaskDirectory

                    if Directory.Exists projectRoot && projectRoot.Contains("workflow-task-scratch-compat-", StringComparison.Ordinal) then
                        Directory.Delete(projectRoot, true)
    }

if OperatingSystem.IsWindows() then
    // Standalone entry bridge: FSI owns the process entry for this asynchronous script.
    run () |> Async.RunSynchronously
else
    printfn "SKIP TaskScratch promote smoke: the current consumer is Windows-only"
