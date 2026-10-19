// role: reusable module/helper
// The only process boundary: a shell-free argument list that inherits the parent's standard
// handles, so the child owns stdout and its own exit status is reported unchanged.
module ResolverChild

open System
open System.ComponentModel
open System.Diagnostics
open System.Runtime.InteropServices
open SharedFailure

// 128 + signal number, the conventional report for a process ended by a signal.
let interruptCode = 130
let terminateCode = 143

type Outcome =
    | Exited of int
    | Signalled of int

let start
    (host: string)
    (workspace: string)
    (entrypoint: string)
    (arguments: string list)
    : Result<Process, ReleaseError> =
    if not (ResolverPath.isAbsolute host) then
        Error(LaunchInputRejected("host", $"the .NET host path '{host}' is not absolute"))
    elif not (ResolverPath.isAbsolute workspace) then
        Error(LaunchInputRejected("workspace", $"the working directory '{workspace}' is not absolute"))
    elif not (ResolverPath.isAbsolute entrypoint) then
        Error(MalformedArtifact(entrypoint, "the resolved entrypoint is not an absolute path"))
    else
        try
            let info = ProcessStartInfo()
            info.FileName <- host
            info.WorkingDirectory <- workspace
            info.UseShellExecute <- false
            info.ArgumentList.Add "exec"
            info.ArgumentList.Add entrypoint

            for argument in arguments do
                info.ArgumentList.Add argument

            let started = Process.Start info

            if isNull started then
                Error(ProcessStartFailure(host, "the operating system did not start a process"))
            else
                Ok started
        with
        | :? Win32Exception as error -> Error(ProcessStartFailure(host, error.Message))
        | :? InvalidOperationException as error -> Error(ProcessStartFailure(host, error.Message))

// A signal terminates the child rather than being forwarded, because a resolver that is going
// away must not leave the child running.
let await (child: Process) : Async<Outcome> =
    async {
        let mutable signalled = 0

        let terminate (code: int) =
            signalled <- code

            try
                if not child.HasExited then child.Kill true
            with
            | :? InvalidOperationException -> ()
            | :? NotSupportedException -> ()
            | :? Win32Exception -> ()

        let intercept (context: PosixSignalContext) =
            context.Cancel <- true
            terminate (if context.Signal = PosixSignal.SIGINT then interruptCode else terminateCode)

        use _interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, intercept)
        use _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, intercept)
        do! child.WaitForExitAsync() |> awaitComplete

        if signalled = 0 then
            return Exited child.ExitCode
        else
            return Signalled signalled
    }
