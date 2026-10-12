module ReleaseTestProcess

open System
open System.Diagnostics
open System.Threading

let capture (timeoutMs: int) (startInfo: ProcessStartInfo) : Async<Result<int * string * string, string>> =
    async {
        use child = new Process(StartInfo = startInfo)
        try
            if not (child.Start()) then
                return Error "process did not start"
            else
                use timeout = new CancellationTokenSource(timeoutMs)
                let stdoutTask = child.StandardOutput.ReadToEndAsync()
                let stderrTask = child.StandardError.ReadToEndAsync()

                try
                    do! child.WaitForExitAsync(timeout.Token) |> Async.AwaitTask
                    let! stdout = stdoutTask |> Async.AwaitTask
                    let! stderr = stderrTask |> Async.AwaitTask
                    return Ok(child.ExitCode, stdout, stderr)
                with :? OperationCanceledException ->
                    try
                        if not child.HasExited then child.Kill(true)
                    with
                    | :? InvalidOperationException -> ()
                    | :? System.ComponentModel.Win32Exception -> ()
                    | :? NotSupportedException -> ()

                    do! child.WaitForExitAsync() |> Async.AwaitTask
                    let! _ = stdoutTask |> Async.AwaitTask
                    let! stderr = stderrTask |> Async.AwaitTask
                    return Error(sprintf "process timed out after %d ms: %s" timeoutMs stderr)
        with
        | :? System.ComponentModel.Win32Exception as error -> return Error(sprintf "could not start process: %s" error.Message)
        | :? InvalidOperationException as error -> return Error(sprintf "process operation failed: %s" error.Message)
    }
