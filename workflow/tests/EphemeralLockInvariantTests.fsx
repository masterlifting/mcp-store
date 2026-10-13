// role: test entrypoint
// This fixture has no supporting artifacts. A background sampler watches for
// any sub-second runtime.lock appearance while task operations run; coexistence
// with supporting artifacts is covered separately.

#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Workflow

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertEqual name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual

let expectOk name (result: Async<Result<'a, RuntimeError>>) : Async<'a> =
    async {
        match! result with
        | Ok value -> return value
        | Error error -> return failwithf "%s: expected Ok, got Error %s" name (renderError error)
    }

let request id =
    { Id = id
      Title = "Ephemeral lock invariant"
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "The task is complete" ]
      WorkItems =
          [ { Id = "W1"
              Title = "Perform the work"
              DependsOn = []
              Children = [] } ] }

let taskDirectory root id = Path.Combine(root, ".tasks", id)
let lockPath root id = Path.Combine(taskDirectory root id, "runtime.lock")

let verifyCancelledWaiterReleasesMutex root id : Async<unit> =
    async {
        let sidecar = Path.Combine(taskDirectory root id, SidecarFileName)
        // Contention must use the exact path-derived mutex name used by Workflow.
        let canonical = Path.GetFullPath sidecar
        let canonical = if OperatingSystem.IsWindows() then canonical.ToUpperInvariant() else canonical
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes canonical) |> Convert.ToHexString
        let mutexName = if OperatingSystem.IsWindows() then $"Local\\Mcp.Workflow.Runtime.{digest}" else $"Mcp.Workflow.Runtime.{digest}"
        let holderAcquired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let holderReleased = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let cancelledActionPath = Path.Combine(taskDirectory root id, "cancelled-waiter-action.txt")
        use releaseHolder = new ManualResetEvent(false)
        let holderThread =
            Thread(ThreadStart(fun () ->
                let mutex = new Mutex(false, mutexName)
                let mutable held = false
                try
                    mutex.WaitOne() |> ignore
                    held <- true
                    holderAcquired.TrySetResult(()) |> ignore
                    releaseHolder.WaitOne() |> ignore
                finally
                    try
                        if held then mutex.ReleaseMutex()
                    finally
                        mutex.Dispose()
                        holderReleased.TrySetResult(()) |> ignore))
        holderThread.IsBackground <- true
        holderThread.Start()
        let baselineOwnerCount = activeLockOwnerThreads ()
        let mutable waitingTask: Task<Result<unit, RuntimeError>> option = None
        let cancellationWatch = Stopwatch()

        let! outcome =
            async {
                try
                    do! holderAcquired.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
                with :? TimeoutException -> return failwith "mutex-holder acquisition timed out"
                use cancellation = new CancellationTokenSource()
                let waiting =
                    Async.StartAsTask(
                        withLock sidecar (fun () ->
                            async {
                                File.WriteAllText(cancelledActionPath, "executed")
                                return Ok ()
                            }),
                        cancellationToken = cancellation.Token
                    )
                waitingTask <- Some waiting

                let awaitOwnerCount predicate : Async<bool> =
                    async {
                        let deadline = DateTime.UtcNow.AddSeconds 5.0
                        let mutable matched = predicate (activeLockOwnerThreads ())
                        while not matched && DateTime.UtcNow < deadline do
                            do! Task.Delay 10 |> Async.AwaitTask
                            matched <- predicate (activeLockOwnerThreads ())
                        return matched
                    }

                let! contenderStarted = awaitOwnerCount (fun count -> count > baselineOwnerCount)
                assertTrue "cancelled operation started its native mutex owner" contenderStarted
                cancellationWatch.Start()
                cancellation.Cancel()

                let! ownerReturned = awaitOwnerCount ((=) baselineOwnerCount)
                assertTrue "cancelled waiter releases its native owner while the external holder still owns the mutex" ownerReturned
                assertTrue "external mutex holder remains unreleased until the owner exits" (not holderReleased.Task.IsCompleted)

                let! cancellationResult =
                    Async.Catch(waiting.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask)

                let rec isCancellation (error: exn) =
                    match error with
                    | :? OperationCanceledException -> true
                    | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
                        isCancellation aggregate.InnerExceptions.[0]
                    | _ -> false

                let cancelled =
                    match cancellationResult with
                    | Choice2Of2 error -> isCancellation error
                    | Choice1Of2 _ -> false

                assertTrue "contended lock operation preserves caller cancellation" cancelled
                cancellationWatch.Stop()
                assertTrue "cancelled waiter does not execute or mutate its protected action" (not (File.Exists cancelledActionPath))
                if not (OperatingSystem.IsWindows()) then
                    assertTrue
                        (sprintf "Unix cancellation completes within 500ms (elapsed %O)" cancellationWatch.Elapsed)
                        (cancellationWatch.Elapsed <= TimeSpan.FromMilliseconds 500.0)
                    printfn "Unix cancelled waiter released its owner and skipped the protected action in %O" cancellationWatch.Elapsed

                releaseHolder.Set() |> ignore
                do! holderReleased.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
                let nextTask = Async.StartAsTask(getTask root id)
                let! next =
                    async {
                        try return! nextTask.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
                        with :? TimeoutException -> return failwith "cancelled caller stranded the mutex owner; next getTask timed out"
                    }
                match next with
                | Ok _ -> return ()
                | Error error -> return failwithf "getTask after caller cancellation: %s" (renderError error)
            }
            |> Async.Catch

        releaseHolder.Set() |> ignore
        do! holderReleased.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
        match waitingTask with
        | Some waiting when not waiting.IsCompleted ->
            let! _ = Async.Catch(waiting.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask)
            ()
        | _ -> ()
        match outcome with
        | Choice1Of2 () -> return ()
        | Choice2Of2 error -> return raise error
    }

let coordinationMutexName (path: string) =
    let canonical = Path.GetFullPath path
    let canonical = if OperatingSystem.IsWindows() then canonical.ToUpperInvariant() else canonical
    let digest = SHA256.HashData(Encoding.UTF8.GetBytes canonical) |> Convert.ToHexString
    if OperatingSystem.IsWindows() then $"Local\\Mcp.Workflow.Runtime.{digest}" else $"Mcp.Workflow.Runtime.{digest}"

let createAbandonedMutexHandle (path: string) : Async<Mutex> =
    async {
        let acquired = TaskCompletionSource<Result<Mutex, exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let name = coordinationMutexName path
        let owner =
            Thread(ThreadStart(fun () ->
                try
                    let mutex = new Mutex(false, name)
                    if mutex.WaitOne(TimeSpan.FromSeconds 5.0) then
                        acquired.TrySetResult(Ok mutex) |> ignore
                    else
                        acquired.TrySetResult(Error(TimeoutException "abandonment owner did not acquire the named mutex")) |> ignore
                with error ->
                    acquired.TrySetResult(Error error) |> ignore))

        owner.IsBackground <- true
        owner.Start()

        let! ownership = acquired.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
        assertTrue "abandonment owner thread exits while retaining its open mutex handle" (owner.Join(TimeSpan.FromSeconds 5.0))

        match ownership with
        | Ok mutex -> return mutex
        | Error error -> return raise error
    }

let verifyUnixCancellationAfterNativeAcquire root id abandoned : Async<unit> =
    async {
        if OperatingSystem.IsWindows() then
            printfn "SKIP Unix native-acquisition cancellation (%s): Windows uses the atomic WaitAny path" id
        else
            let taskDir = taskDirectory root id
            Directory.CreateDirectory taskDir |> ignore
            let sidecar = Path.Combine(taskDir, SidecarFileName)
            let originalBytes = Encoding.UTF8.GetBytes $"preserved-{id}"
            File.WriteAllBytes(sidecar, originalBytes)

            use gate = new ManualResetEvent(false)
            use cancellation = new CancellationTokenSource()
            let beforeGuard = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let acquisitionPublished = TaskCompletionSource<Result<unit, exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseAttempted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseSucceeded = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let observations = ConcurrentQueue<LockObservation * int>()
            let observerFailures = ConcurrentQueue<string>()
            let actionCalls = ConcurrentQueue<unit>()
            let mutable ownerThread: Thread option = None
            let mutable abandonedHandle: Mutex = null
            let mutable operation: Task<Result<unit, RuntimeError>> option = None

            let observe observation =
                try
                    let thread = Thread.CurrentThread
                    observations.Enqueue((observation, thread.ManagedThreadId))

                    match observation with
                    | BeforeUnixGuard wasAbandoned ->
                        ownerThread <- Some thread
                        beforeGuard.TrySetResult wasAbandoned |> ignore
                        if not (gate.WaitOne(TimeSpan.FromSeconds 10.0)) then
                            observerFailures.Enqueue "owner did not leave the pre-publication gate within 10 seconds"
                    | AcquisitionPublished result -> acquisitionPublished.TrySetResult result |> ignore
                    | ReleaseAttempt -> releaseAttempted.TrySetResult() |> ignore
                    | ReleaseSucceeded -> releaseSucceeded.TrySetResult() |> ignore
                with error ->
                    observerFailures.Enqueue error.Message

            let! outcome =
                async {
                    try
                        if abandoned then
                            let! retained = createAbandonedMutexHandle sidecar
                            abandonedHandle <- retained

                        let running =
                            Async.StartAsTask(
                                withLockCore
                                    observe
                                    sidecar
                                    (fun () ->
                                        async {
                                            actionCalls.Enqueue(())
                                            return Ok ()
                                        }),
                                cancellationToken = cancellation.Token
                            )

                        operation <- Some running

                        let! actualAbandonment = beforeGuard.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
                        assertEqual "native owner reached the Unix post-acquisition gate" abandoned actualAbandonment

                        cancellation.Cancel()
                        assertTrue "caller cancellation completed before releasing the native owner gate" cancellation.IsCancellationRequested
                        gate.Set() |> ignore

                        let! published = acquisitionPublished.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask

                        match published with
                        | Error (:? OperationCanceledException) -> ()
                        | Error error -> return failwithf "native acquisition published an unexpected error: %s" error.Message
                        | Ok () ->
                            return failwith "native acquisition published Ok after cancellation completed while its Unix owner was gated"

                        let! callerResult =
                            Async.Catch(running.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask)

                        let rec isCancellation (error: exn) =
                            match error with
                            | :? OperationCanceledException -> true
                            | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
                                isCancellation aggregate.InnerExceptions.[0]
                            | _ -> false

                        match callerResult with
                        | Choice2Of2 error when isCancellation error -> ()
                        | Choice2Of2 error -> return failwithf "cancelled acquisition raised an unexpected error: %s" error.Message
                        | Choice1Of2 value -> return failwithf "cancelled acquisition unexpectedly returned %A" value

                        return ()
                    with error ->
                        return raise error
                }
                |> Async.Catch

            gate.Set() |> ignore

            match operation with
            | Some running ->
                let! _ = Async.Catch(running.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask)
                ()
            | None -> ()

            match ownerThread with
            | Some owner -> assertTrue "native mutex owner thread terminates" (owner.Join(TimeSpan.FromSeconds 5.0))
            | None -> ()

            if not (isNull abandonedHandle) then abandonedHandle.Dispose()

            match outcome with
            | Choice2Of2 error -> return raise error
            | Choice1Of2 () -> ()

            assertTrue "observation callback completed without fixture errors" observerFailures.IsEmpty
            do! releaseAttempted.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
            do! releaseSucceeded.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask

            let observed = observations.ToArray() |> Array.toList
            let events = observed |> List.map fst
            let releaseAttempts = events |> List.filter ((=) ReleaseAttempt)
            let releaseSuccesses = events |> List.filter ((=) ReleaseSucceeded)
            let publications = events |> List.choose (function AcquisitionPublished result -> Some result | _ -> None)
            let preGuards = events |> List.choose (function BeforeUnixGuard value -> Some value | _ -> None)
            let owner = ownerThread |> Option.defaultWith (fun () -> failwith "native owner thread was not captured")

            assertEqual "one native acquisition publication" 1 publications.Length
            assertEqual "one successful or abandoned native acquisition" [ abandoned ] preGuards
            assertEqual "one native mutex release attempt" 1 releaseAttempts.Length
            assertEqual "one successful native mutex release" 1 releaseSuccesses.Length
            assertTrue
                "publication and release observations stay on the native owner thread"
                (observed |> List.forall (fun (_, threadId) -> threadId = owner.ManagedThreadId))
            assertTrue "cancelled protected action is never invoked" actionCalls.IsEmpty
            assertEqual "cancelled operation leaves protected sidecar bytes unchanged" originalBytes (File.ReadAllBytes sidecar)
            assertTrue "cancelled operation leaves no runtime.lock" (not (File.Exists(lockPath root id)))

            let! next =
                expectOk
                    "normal acquisition after cancelled native acquisition"
                    (withLock sidecar (fun () -> async { return Ok "next" }))

            assertEqual "next normal acquisition executes" "next" next
            assertTrue "subsequent acquisition leaves no runtime.lock" (not (File.Exists(lockPath root id)))
            printfn "OK Unix cancelled native acquisition: abandoned=%b, one same-thread release, next acquisition succeeds" abandoned
    }

let verifyPostAcquisitionCancellationKeepsMutexLeased root id : Async<unit> =
    async {
        let sidecar = Path.Combine(taskDirectory root id, SidecarFileName)
        let firstEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let firstGate = TaskCompletionSource<Result<string, RuntimeError>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let secondEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let baselineOwnerCount = activeLockOwnerThreads ()
        use cancellation = new CancellationTokenSource()
        let mutable firstTask: Task<Result<string, RuntimeError>> option = None
        let mutable secondTask: Task<Result<string, RuntimeError>> option = None

        let! outcome =
            async {
                let first =
                    Async.StartAsTask(
                        withLock sidecar (fun () ->
                            async {
                                firstEntered.TrySetResult(()) |> ignore
                                // AwaitTask keeps the protected action gated after caller cancellation.
                                return! Async.AwaitTask firstGate.Task
                            }),
                        cancellationToken = cancellation.Token
                    )

                firstTask <- Some first
                do! firstEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
                let firstOwnerCount = activeLockOwnerThreads ()
                assertTrue "first action owns a native lock thread" (firstOwnerCount > baselineOwnerCount)
                cancellation.Cancel()

                let secondScheduled = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let second =
                    Async.StartAsTask(async {
                        secondScheduled.TrySetResult(()) |> ignore
                        return!
                            withLock sidecar (fun () ->
                                async {
                                    secondEntered.TrySetResult(()) |> ignore
                                    return Ok "second"
                                })
                    })

                secondTask <- Some second
                do! secondScheduled.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask

                let deadline = DateTime.UtcNow.AddSeconds 5.0
                while not secondEntered.Task.IsCompleted
                      && not second.IsCompleted
                      && activeLockOwnerThreads () <= firstOwnerCount
                      && DateTime.UtcNow < deadline do
                    do! Task.Delay 10 |> Async.AwaitTask

                assertTrue "second contender starts its owner thread" (activeLockOwnerThreads () > firstOwnerCount || secondEntered.Task.IsCompleted)
                assertTrue "second protected action remains outside while first action is gated" (not secondEntered.Task.IsCompleted)
                assertTrue "second contender remains incomplete while first action is gated" (not second.IsCompleted)

                let observationWindow = Task.Delay 200
                let! raced =
                    Task.WhenAny([| secondEntered.Task :> Task; second :> Task; observationWindow |])
                    |> Async.AwaitTask

                assertTrue "second contender stays blocked for the bounded observation window" (obj.ReferenceEquals(raced, observationWindow))

                firstGate.TrySetResult(Ok "first") |> ignore
                let! firstOutcome = Async.Catch(first.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask)

                let rec isCancellation (error: exn) =
                    match error with
                    | :? OperationCanceledException -> true
                    | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
                        isCancellation aggregate.InnerExceptions.[0]
                    | _ -> false

                match firstOutcome with
                | Choice2Of2 error when isCancellation error -> ()
                | Choice2Of2 error -> return failwithf "cancelled first lease raised an unexpected error: %s" error.Message
                | Choice1Of2(Error(PersistenceFailure message)) ->
                    return failwithf "first cancellation became a normal persistence failure: %s" message
                | Choice1Of2(Ok _) -> ()
                | Choice1Of2(Error error) -> return failwithf "first lease returned an unexpected error: %s" (renderError error)

                do! secondEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
                let! secondResult = second.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
                assertEqual "second lease completes after the first unwinds" (Ok "second") secondResult
                return ()
            }
            |> Async.Catch

        firstGate.TrySetResult(Ok "cleanup") |> ignore

        for task in [ firstTask |> Option.map (fun pending -> pending :> Task); secondTask |> Option.map (fun pending -> pending :> Task) ] do
            match task with
            | Some pending ->
                let! _ = Async.Catch(pending.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask)
                ()
            | None -> ()

        match outcome with
        | Choice1Of2 () -> return ()
        | Choice2Of2 error -> return raise error
    }

// Recursive listing relative to the task directory. Tolerant of a directory
// that does not exist yet or is being created concurrently.
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

let assertOnlyRuntimeJson label root id =
    assertTrue $"{label}: runtime.lock must not exist" (not (File.Exists(lockPath root id)))
    assertEqual $"{label}: task directory contents" [ "runtime.json" ] (enumerateEntries (taskDirectory root id))

// Polls the task directory at one-millisecond intervals on a background thread
// so a transient runtime.lock that appears and is deleted inside a normal
// operation is still observed and reported.
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

let tempRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"workflow-ephemeral-lock-{Guid.NewGuid():N}")

Directory.CreateDirectory tempRoot |> ignore
let taskId = "EPH-1"
let sampler = DirectorySampler(taskDirectory tempRoot taskId)

// Standalone entry bridge: FSI requires one synchronous top-level boundary.
async {
    try
        sampler.Start()

        let! _ = expectOk "create" (createTask tempRoot (request taskId))
        assertOnlyRuntimeJson "after task_create" tempRoot taskId

        let taskSidecar = Path.Combine(taskDirectory tempRoot taskId, SidecarFileName)
        let persistedTask = File.ReadAllText taskSidecar
        File.WriteAllText(taskSidecar, "{")
        let! malformedRead = getTask tempRoot taskId

        match malformedRead with
        | Error _ -> ()
        | Ok _ -> failwith "malformed task read unexpectedly succeeded"

        File.WriteAllText(taskSidecar, persistedTask)
        let! _ = expectOk "valid read after failed read releases mutex lease" (getTask tempRoot taskId)
        assertOnlyRuntimeJson "after failed read" tempRoot taskId
        do! verifyCancelledWaiterReleasesMutex tempRoot taskId
        do! verifyUnixCancellationAfterNativeAcquire tempRoot "EPH-2" false
        do! verifyUnixCancellationAfterNativeAcquire tempRoot "EPH-3" true
        do! verifyPostAcquisitionCancellationKeepsMutexLeased tempRoot taskId

        let! _ = expectOk "get" (getTask tempRoot taskId)
        assertOnlyRuntimeJson "after task_get" tempRoot taskId

        let! applied = expectOk "apply" (applyTask tempRoot taskId 0 (StartWorkItem "W1"))
        assertEqual "apply revision" 1 applied.StateRevision
        assertOnlyRuntimeJson "after task_apply" tempRoot taskId

        let! _ = expectOk "validate" (validateTask tempRoot taskId)
        assertOnlyRuntimeJson "after task_validate" tempRoot taskId

        let observed = sampler.Stop()

        let observedEntries =
            observed |> List.collect id |> List.distinct |> List.sort

        let observedLockFiles =
            observedEntries
            |> List.filter (fun entry ->
                Path.GetFileName(entry).Equals("runtime.lock", StringComparison.OrdinalIgnoreCase))

        assertTrue
            (sprintf "sampler never observed runtime.lock (observed: %A)" observedEntries)
            observedLockFiles.IsEmpty

        assertOnlyRuntimeJson "final" tempRoot taskId

        printfn "OK ephemeral lock invariant: create/get/apply/validate leave only runtime.json"
        printfn "observed task-directory snapshots (%d samples, %d distinct): %A" observed.Length observedEntries.Length observedEntries
    finally
        try sampler.Stop() |> ignore with _ -> ()

        if Directory.Exists tempRoot
           && tempRoot.Contains("workflow-ephemeral-lock-", StringComparison.Ordinal) then
            Directory.Delete(tempRoot, true)
}
// Standalone entry bridge: FSI needs one synchronous script entry.
|> Async.RunSynchronously
