// role: reusable module/helper
// Stat-level classification avoids opening Unix special files, which .NET reports as Normal.
module ResolverKind

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open System.ComponentModel
open SharedFailure

type Kind =
    | Regular
    | Directory
    | Link
    | Other

let name (kind: Kind) : string =
    match kind with
    | Regular -> "regular file"
    | Directory -> "directory"
    | Link -> "link or reparse point"
    | Other -> "special file"

let private windows (path: string) : Result<Kind, ReleaseError> =
    try
        let attributes = File.GetAttributes path

        if attributes.HasFlag FileAttributes.ReparsePoint then Ok Link
        elif attributes.HasFlag FileAttributes.Directory then Ok Directory
        else Ok Regular
    with
    | :? IOException as error -> Error(FileFailure("kind", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("kind", path, error.Message))

[<Literal>]
let private typeMask = 0xF000UL

[<Literal>]
let private regularMask = 0x8000UL

[<Literal>]
let private directoryMask = 0x4000UL

[<Literal>]
let private linkMask = 0xA000UL

let internal parseMode (radix: int) (text: string) : Result<uint64, string> =
    let value =
        if isNull text then ""
        elif text.EndsWith("\r\n", StringComparison.Ordinal) then text.Substring(0, text.Length - 2)
        elif text.EndsWith("\n", StringComparison.Ordinal) then text.Substring(0, text.Length - 1)
        else text

    if (radix <> 8 && radix <> 16) || String.IsNullOrEmpty value || value.Contains('\n') || value.Contains('\r') then
        Error "stat returned malformed mode output"
    else
        let validDigit character =
            if radix = 16 then
                (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F')
            else
                character >= '0' && character <= '7'

        if value |> Seq.exists (validDigit >> not) then
            Error "stat returned malformed mode output"
        else
            let mutable parsed = 0UL
            let mutable overflow = false

            for character in value do
                let digit =
                    if character >= '0' && character <= '9' then uint64 (int character - int '0')
                    elif character >= 'a' && character <= 'f' then uint64 (int character - int 'a' + 10)
                    else uint64 (int character - int 'A' + 10)

                let baseValue = uint64 radix

                if parsed > (UInt64.MaxValue - digit) / baseValue then
                    overflow <- true
                elif not overflow then
                    parsed <- parsed * baseValue + digit

            if overflow then Error "stat returned a malformed or overflowing mode" else Ok parsed

let internal classifyMode mode =
    match mode &&& typeMask with
    | value when value = regularMask -> Regular
    | value when value = directoryMask -> Directory
    | value when value = linkMask -> Link
    | _ -> Other

let private readBounded (stream: Stream) (limit: int) (kill: unit -> unit) =
    task {
        use output = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 512
        let mutable finished = false

        while not finished do
            let remaining = limit - int output.Length
            let readLength = min buffer.Length (remaining + 1)
            let! count = stream.ReadAsync(buffer.AsMemory(0, readLength))

            if count = 0 then
                finished <- true
            elif output.Length + int64 count > int64 limit then
                kill ()
                raise (InvalidDataException "stat output exceeded its bounded limit")
            else
                output.Write(buffer, 0, count)

        return output.ToArray()
    }

type internal StatResult = { Kind: Kind; Arguments: string list }

let internal runStat (program: string) (arguments: string list) (radix: int) (path: string) (timeout: TimeSpan) : Async<Result<StatResult, ReleaseError>> =
    async {
        let failure detail = Error(FileFailure("kind", path, detail))

        if timeout <= TimeSpan.Zero then
            return failure "the stat classification budget must be positive"
        else
            let info = ProcessStartInfo(program)
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.Environment.Clear()
            info.Environment["LC_ALL"] <- "C"

            for argument in arguments do
                info.ArgumentList.Add argument

            use childProcess = new Process()
            childProcess.StartInfo <- info
            let mutable started = false
            let mutable stdout: Task<byte[]> = Task.FromResult(Array.empty)
            let mutable stderr: Task<byte[]> = Task.FromResult(Array.empty)
            let mutable exit: Task = Task.CompletedTask

            let kill () =
                try
                    if started && not childProcess.HasExited then childProcess.Kill(true)
                with _ -> ()

            let! result =
                async {
                    try
                        if not (childProcess.Start()) then
                            return failure "the stat utility did not start"
                        else
                            started <- true
                            stdout <- readBounded childProcess.StandardOutput.BaseStream 64 kill
                            stderr <- readBounded childProcess.StandardError.BaseStream 4096 kill
                            exit <- childProcess.WaitForExitAsync()
                            let complete = Task.WhenAll(exit, stdout :> Task, stderr :> Task)
                            use timeoutCancellation = new CancellationTokenSource()
                            let timeoutTask = Task.Delay(timeout, timeoutCancellation.Token)
                            let! finished = Task.WhenAny(complete, timeoutTask) |> awaitOperational
                            timeoutCancellation.Cancel()

                            if finished <> complete then
                                kill ()
                                return failure "the stat utility timed out"
                            else
                                do! awaitComplete complete
                                if childProcess.ExitCode <> 0 then
                                    return failure $"the stat utility exited with code {childProcess.ExitCode}"
                                else
                                    let! output = awaitOperational stdout
                                    let text = Text.Encoding.ASCII.GetString output

                                    match parseMode radix text with
                                    | Error detail -> return failure detail
                                    | Ok mode -> return Ok { Kind = classifyMode mode; Arguments = arguments }
                    with
                    | :? Win32Exception as error -> return failure $"the stat utility is unavailable: {error.Message}"
                    | :? IOException as error -> return failure error.Message
                    | :? UnauthorizedAccessException as error -> return failure error.Message
                    | :? InvalidOperationException as error -> return failure error.Message
                    // Bounded pipe overflow is an expected classification failure, not a resolver fault.
                    | :? InvalidDataException as error -> return failure error.Message
                    | :? AggregateException as error when error.InnerExceptions.Count > 0 -> return failure error.InnerExceptions.[0].Message
                    | :? OperationCanceledException ->
                        kill ()
                        return failure "the stat utility classification was cancelled"
                }

            if started then
                kill ()
                use cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds 500.)

                try
                    do! awaitComplete (childProcess.WaitForExitAsync(cleanup.Token))
                with _ -> ()

                let readers = Task.WhenAll(stdout :> Task, stderr :> Task)
                let! drained = Task.WhenAny(readers, Task.Delay(TimeSpan.FromMilliseconds 500.)) |> awaitOperational

                if drained = readers then
                    try
                        do! awaitComplete readers
                    with _ -> ()

            return result
    }

let private unix (path: string) (timeout: TimeSpan) : Async<Result<Kind, ReleaseError>> =
    async {
        if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
            return Error(FileFailure("kind", path, "Unix stat classification is unsupported on this operating system"))
        else
            let arguments = ResizeArray<string>()

            if OperatingSystem.IsLinux() then
                arguments.Add "--printf=%f"
                arguments.Add "--"
            else
                arguments.Add "-f"
                arguments.Add "%Op"

            let absolute = Path.GetFullPath path
            let root = Path.GetPathRoot absolute
            let entryPath = absolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            arguments.Add(if entryPath.Length < root.Length then root else entryPath)

            let! result = runStat "/usr/bin/stat" (List.ofSeq arguments) (if OperatingSystem.IsLinux() then 16 else 8) path timeout
            return Result.map (fun stat -> stat.Kind) result
    }

let classifyWithin (budget: TimeSpan) (path: string) : Async<Result<Kind, ReleaseError>> =
    let timeout = min budget (TimeSpan.FromSeconds 2.)

    if budget <= TimeSpan.Zero then
        async { return Error(FileFailure("kind", path, "the stat classification budget must be positive")) }
    elif OperatingSystem.IsWindows() then async { return windows path }
    else unix path timeout

let classify (path: string) : Async<Result<Kind, ReleaseError>> =
    classifyWithin (TimeSpan.FromSeconds 2.) path
