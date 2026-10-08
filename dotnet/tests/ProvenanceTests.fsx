// Focused contract coverage for the repo-shared build provenance guard. Unlike the
// distribution tests this runs from a dirty tree because it exercises only manifest
// revision validation, not packaging or the clean-tree precondition.

#load "../../infrastructure/Provenance.fsx"

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
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

let private processCancellationTest () : Async<unit> =
    async {
        let root = Path.Combine(Path.GetTempPath(), "mcp-provenance-cancel-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let scriptPath = Path.Combine(root, "WaitForCancellation.fsx")
        let markerPath = Path.Combine(root, "child.pid")
        File.WriteAllText(
            scriptPath,
            "open System\nopen System.IO\nopen System.Threading\nFile.WriteAllText(Array.last fsi.CommandLineArgs, string Environment.ProcessId)\nThread.Sleep Timeout.Infinite\n"
        )

        use cancellation = new CancellationTokenSource()
        let mutable childTask: Task<Result<string, ReleaseError>> option = None
        let mutable childIdentity: (int * DateTime) option = None

        let! result =
            async {
                let task =
                    Async.StartAsTask(
                        runProcess root "dotnet" [ "fsi"; "--nologo"; scriptPath; markerPath ] cancellation.Token
                    )

                childTask <- Some task
                let deadline = DateTime.UtcNow.AddSeconds 15.0
                let mutable childPid = None

                while childPid.IsNone && not task.IsCompleted && DateTime.UtcNow < deadline do
                    if File.Exists markerPath then
                        try
                            childPid <- Some(int (File.ReadAllText markerPath))
                        with :? IOException -> ()

                    if childPid.IsNone then do! Async.Sleep 20

                let pid = childPid |> Option.defaultWith (fun () -> failwith "cancellation child did not write a readable PID marker")
                use child = Process.GetProcessById pid
                child.Refresh()
                if child.HasExited then return failwith "cancellation child exited before cancellation"
                childIdentity <- Some(pid, child.StartTime.ToUniversalTime())

                cancellation.Cancel()
                let! cancellationOutcome =
                    Async.Catch(
                        task.WaitAsync(TimeSpan.FromSeconds 10.0)
                        |> Async.AwaitTask
                    )

                let rec isCancellation (error: exn) =
                    match error with
                    | :? OperationCanceledException -> true
                    | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
                        isCancellation aggregate.InnerExceptions.[0]
                    | _ -> false

                match cancellationOutcome with
                | Choice2Of2 error when isCancellation error -> ()
                | Choice2Of2 (:? TimeoutException) -> return failwith "runProcess did not reap its child after cancellation"
                | Choice2Of2 error -> return failwithf "runProcess cancellation raised an unexpected error: %s" error.Message
                | Choice1Of2 value -> return failwithf "runProcess returned after cancellation: %A" value

                match childIdentity with
                | Some(ownedPid, ownedStartTime) ->
                    let stillOwnedProcessExists =
                        try
                            use ownedProcess = Process.GetProcessById ownedPid
                            ownedProcess.Refresh()
                            not ownedProcess.HasExited && ownedProcess.StartTime.ToUniversalTime() = ownedStartTime
                        with :? ArgumentException -> false

                    assertEqual "owned child is terminated after runProcess returns" false stillOwnedProcessExists
                | None -> failwith "cancellation child identity was not recorded"

                return ()
            }
            |> Async.Catch

        cancellation.Cancel()

        match childTask with
        | Some task when not task.IsCompleted ->
            match childIdentity with
            | Some(pid, _) ->
                try
                    use child = Process.GetProcessById pid
                    if not child.HasExited then child.Kill true
                with :? ArgumentException -> ()
            | None -> ()

            let! _ = Async.Catch(task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask)
            ()
        | _ -> ()

        if Directory.Exists root then Directory.Delete(root, true)

        match result with
        | Choice1Of2 () -> return ()
        | Choice2Of2 error -> return raise error
    }

// The script composes one async pipeline of every assertion; the entry point
// applies exactly one Async.RunSynchronously at the bottom.
let private suite () : Async<unit> =
    async {
        do! releaseResultBuilderOrderingTest ()
        do! processCancellationTest ()

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
