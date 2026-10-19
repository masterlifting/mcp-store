// role: reusable module/helper
// Capture exit, stdout, and stderr for one child process without blocking on `WaitForExit`/`ReadToEnd`.
// Build/pins/tests/config-evaluation callers scrub publication credentials here so the runner keeps the only auth boundary.
module ReleaseCapture

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open SharedFailure
open BuildProvenance

// Bound so a hung child cannot deadlock the wrapper or the compiled runner
// past the publishing window; the wrapper's ambient token still wins if shorter.
let defaultDeadline = TimeSpan.FromMinutes 30.0

// Stdout and stderr are read concurrently so a full stderr pipe cannot stall the
// child; the exit code is captured before `Dispose` clears it, and a cancellation
// kills the child and awaits both drains without blocking the calling thread.
let runProcessCapture
    (workingDirectory: string)
    (executable: string)
    (arguments: string list)
    (scrubEnvVars: string list)
    (cancellationToken: CancellationToken)
    : Async<Result<int * string * string, ReleaseError>> =
    async {
        let argumentText = String.concat " " arguments
        let operation = $"{executable} {argumentText}"
        let info = ProcessStartInfo(executable)
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        arguments |> List.iter info.ArgumentList.Add

        // Scrub credentials the caller names (by environment variable name only) so pre-publication stages never inherit publication authority.
        for name in scrubEnvVars do
            info.EnvironmentVariables.Remove name |> ignore

        let exitCodePlaceholder = -1
        use child = new Process(StartInfo = info)
        let mutable exitCode = exitCodePlaceholder

        use deadline =
            new CancellationTokenSource(defaultDeadline)
        use linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token)

        let token = linked.Token
        let mutable killAttempted = false
        let terminate () =
            if not killAttempted then
                killAttempted <- true
                try
                    if not child.HasExited then child.Kill(true)
                with
                | :? InvalidOperationException -> ()
                | :? System.ComponentModel.Win32Exception -> ()
                | :? NotSupportedException -> ()

        try
            if not (child.Start()) then
                return Error(ProcessStartFailure(operation, "process could not be started"))
            else
                use registration = token.Register(fun () -> terminate ())
                let stdoutTask = child.StandardOutput.ReadToEndAsync()
                let stderrTask = child.StandardError.ReadToEndAsync()

                try
                    do! awaitComplete (child.WaitForExitAsync token)
                    // Captured BEFORE the implicit `use` Dispose; do not reorder.
                    exitCode <- child.ExitCode
                with :? OperationCanceledException as cancelled ->
                    terminate ()
                    do! awaitComplete (child.WaitForExitAsync())
                    exitCode <- child.ExitCode

                    try
                        let! _ = awaitOperational stdoutTask
                        let! _ = awaitOperational stderrTask
                        ()
                    with
                    | :? IOException -> ()
                    | :? UnauthorizedAccessException -> ()

                    return raise cancelled

                let! stdout = awaitOperational stdoutTask
                let! stderr = awaitOperational stderrTask

                if token.IsCancellationRequested then
                    return raise (OperationCanceledException token)
                else
                    return Ok(exitCode, stdout, stderr)
        with
        | :? OperationCanceledException as error -> return raise error
        | :? System.ComponentModel.Win32Exception as error -> return Error(ProcessStartFailure(operation, error.Message))
        | :? InvalidOperationException as error -> return Error(ProcessStartFailure(operation, error.Message))
        | :? IOException as error -> return Error(ProcessStartFailure(operation, error.Message))
    }
