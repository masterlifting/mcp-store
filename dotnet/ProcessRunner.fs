namespace Mcp.Dotnet

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

module ProcessRunner =
    let private copyToFile (source: Stream) path maximumBytes =
        task {
            use output =
                new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous
                )

            let buffer = Array.zeroCreate<byte> 81920
            let mutable written = 0L
            let mutable reading = true

            // Continue draining after the quota is reached so the child cannot
            // block on a full redirected pipe. Bytes beyond the cap are discarded.
            while reading do
                let! read = source.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)

                if read = 0 then
                    reading <- false
                else
                    let remaining = maximumBytes - written
                    let copyCount = int (min (int64 read) (max 0L remaining))

                    if copyCount > 0 then
                        do! output.WriteAsync(buffer, 0, copyCount, CancellationToken.None)
                        written <- written + int64 copyCount

            do! output.FlushAsync(CancellationToken.None)
            return written
        }

    let private terminate (child: Process) =
        try
            if not child.HasExited then
                child.Kill(true)
        with _ ->
            ()

    let private createSdkIsolationDirectory () : Result<string, VerificationError> =
        try
            let directory =
                Path.Combine(
                    Path.GetTempPath(),
                    "mcp-dotnet-sdk-isolation",
                    Guid.NewGuid().ToString("N")
                )

            Directory.CreateDirectory directory |> ignore

            // The nearest global.json wins. An empty file in a private launch
            // directory prevents a workspace global.json from selecting the SDK.
            File.WriteAllText(Path.Combine(directory, "global.json"), "{}")
            Ok directory
        with error ->
            Error(ProcessStartFailure $"dotnet SDK isolation could not be prepared: {error.Message}")

    let private removeSdkIsolationDirectory directory =
        try
            Directory.Delete(directory, true)
        with _ ->
            ()

    let runWithQuotas invocation paths cancellationToken quotas (quotaCheck: unit -> Result<unit, VerificationError>) : Task<Result<CapturedProcess, VerificationError>> =
        task {
            match AuthorizedInvocation.revalidate invocation with
            | Error error -> return Error error
            | Ok() ->
                match createSdkIsolationDirectory () with
                | Error error -> return Error error
                | Ok sdkIsolationDirectory ->
                    try
                        let startInfo = ProcessStartInfo()
                        startInfo.FileName <- AuthorizedInvocation.executable invocation
                        startInfo.WorkingDirectory <- sdkIsolationDirectory
                        startInfo.UseShellExecute <- false
                        startInfo.CreateNoWindow <- true
                        startInfo.RedirectStandardOutput <- true
                        startInfo.RedirectStandardError <- true

                        for argument in AuthorizedInvocation.arguments invocation do
                            startInfo.ArgumentList.Add argument

                        use child = new Process(StartInfo = startInfo)
                        let stopwatch = Stopwatch.StartNew()

                        try
                            match AuthorizedInvocation.revalidate invocation with
                            | Error error -> return Error error
                            | Ok() ->
                                if not (child.Start()) then
                                    return Error(ProcessStartFailure "dotnet process could not be started")
                                else
                                    let stdoutTask = copyToFile child.StandardOutput.BaseStream paths.Stdout quotas.MaxStdoutBytes
                                    let stderrTask = copyToFile child.StandardError.BaseStream paths.Stderr quotas.MaxStderrBytes

                                    use timeoutSource =
                                        new CancellationTokenSource(AuthorizedInvocation.timeout invocation)

                                    use linkedSource =
                                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token)

                                    let mutable status = None
                                    let mutable quotaFailure = None
                                    use monitorStop = new CancellationTokenSource()

                                    let monitorQuota () =
                                        task {
                                            let mutable finished = false

                                            while not finished && not monitorStop.IsCancellationRequested do
                                                if child.HasExited then
                                                    finished <- true
                                                else
                                                    match quotaCheck () with
                                                    | Error error ->
                                                        quotaFailure <- Some error
                                                        terminate child
                                                        finished <- true
                                                    | Ok() -> do! Task.Delay(25)
                                        }

                                    let waitTask = child.WaitForExitAsync(linkedSource.Token)
                                    let quotaTask = monitorQuota ()
                                    let! winner = Task.WhenAny(waitTask, quotaTask)

                                    if obj.ReferenceEquals(winner, quotaTask) && quotaFailure.IsSome then
                                        try
                                            do! waitTask
                                        with _ ->
                                            ()
                                    else
                                        try
                                            do! waitTask
                                            match quotaCheck () with
                                            | Error error ->
                                                quotaFailure <- Some error
                                                terminate child
                                            | Ok() -> ()
                                        with
                                        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                                            terminate child
                                            status <- Some ProcessStatus.Cancelled
                                        | :? OperationCanceledException ->
                                            terminate child
                                            status <- Some ProcessStatus.TimedOut

                                    monitorStop.Cancel()

                                    try
                                        do! quotaTask
                                    with _ ->
                                        ()

                                    if quotaFailure.IsSome then
                                        let! _ = Task.WhenAll [| stdoutTask; stderrTask |]
                                        return Error quotaFailure.Value

                                    else
                                        if status.IsNone then
                                            status <- Some(ProcessStatus.Completed child.ExitCode)

                                        let! byteCounts = Task.WhenAll [| stdoutTask; stderrTask |]
                                        stopwatch.Stop()

                                        return
                                            Ok
                                                { Status = status.Value
                                                  Duration = stopwatch.Elapsed
                                                  StdoutPath = paths.Stdout
                                                  StderrPath = paths.Stderr
                                                  StdoutBytes = byteCounts.[0]
                                                  StderrBytes = byteCounts.[1] }
                        with
                        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                            terminate child
                            return Error(ProcessStartFailure "dotnet process was cancelled before output capture completed")
                        | error ->
                            terminate child
                            return Error(ProcessStartFailure $"dotnet process execution failed: {error.Message}")
                    finally
                        removeSdkIsolationDirectory sdkIsolationDirectory
        }

    let run invocation paths cancellationToken =
        runWithQuotas invocation paths cancellationToken Budgets.DefaultArtifactQuotas (fun () -> Ok())
