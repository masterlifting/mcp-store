// Child probe contract: the parent starts this same test assembly with `--probe <mode>` so the real cross-process guard, first-install race, crash recovery, product launch, and blocking pipe refusal run untouched.
// Standalone entry bridge: an independent child must turn its result into a process exit code, so every mode shares this one bridge.
module Resolver.Tests.Probe

open System
open System.Diagnostics
open System.IO
open System.Reflection
open Expecto
open SharedFailure
open Resolver.Tests.Fixture

let private pinAt (path: string) : DistributionPin.Pin =
    match DistributionPin.parse (File.ReadAllText path) with
    | Ok value -> value
    | Error error -> failwithf "probe pin parse failed: %s" (ReleaseError.message error)

let private storageAt (root: string) (pin: DistributionPin.Pin) : ResolverStorage.Storage =
    match ResolverStorage.layout root pin with
    | Ok storage -> storage
    | Error error -> failwithf "probe storage layout failed: %s" (ReleaseError.message error)

let private install (arguments: string list) : Async<int> =
    async {
        match arguments with
        | [ root; pinPath; archivePath; delayMs ] ->
            let pinValue = pinAt pinPath
            let downloads = ref 0

            match! ResolverInstall.resolve (servingFile archivePath (int delayMs) downloads) root pinValue with
            | Ok resolution ->
                printfn "ok %s" resolution.Install
                return 0
            | Error error ->
                eprintfn "%s" (ReleaseError.message error)
                return 1
        | _ ->
            eprintfn "probe install expects <root> <pin> <archive> <delayMs>"
            return 2
    }

let private hold (arguments: string list) : Async<int> =
    async {
        match arguments with
        | [ root; pinPath; marker ] ->
            let pinValue = pinAt pinPath
            let storage = storageAt root pinValue

            match ResolverDisk.ensureRoot storage.Root with
            | Error error ->
                eprintfn "%s" (ReleaseError.message error)
                return 1
            | Ok () ->
                match ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Lease) with
                | Error error ->
                    eprintfn "%s" (ReleaseError.message error)
                    return 1
                | Ok () ->
                    match! ResolverLock.exclusive storage.Guard with
                    | Error error ->
                        eprintfn "%s" (ReleaseError.message error)
                        return 1
                    | Ok guard ->
                        use held = guard
                        File.WriteAllText(marker, "held")
                        // The parent kills this process; the OS closing the handle is the release protocol.
                        do! Async.Sleep 30000
                        return 0
        | _ ->
            eprintfn "probe guard expects <root> <pin> <marker>"
            return 2
    }

let private launch (arguments: string list) : Async<int> =
    async {
        match arguments with
        | root :: pinPath :: archivePath :: workspace :: host :: supplied ->
            // The canonical transport reaches the GitHub release only, so locally packed bytes are served in place; every other boundary in this path stays the real resolver code.
            let argv =
                [ "run"; "--pin"; pinPath; "--root"; root; "--workspace"; workspace; "--host"; host ]
                @ (supplied |> List.collect (fun entry -> [ "--input"; entry ]))

            match ResolverCli.parse argv with
            | Error error ->
                eprintfn "%s" (ReleaseError.message error)
                return 2
            | Ok(ResolverCli.Resolve _) ->
                eprintfn "probe run must not receive the resolve subcommand"
                return 2
            | Ok(ResolverCli.Run options) ->
                match! ResolverRun.run (servingFile archivePath 0 (ref 0)) options with
                | Ok(ResolverChild.Exited code) -> return code
                | Ok(ResolverChild.Signalled code) ->
                    eprintfn $"the launched child was terminated after signal {code - 128}"
                    return code
                | Error error ->
                    eprintfn "%s" (ReleaseError.message error)
                    return 1
        | _ ->
            eprintfn "probe run expects <root> <pin> <archive> <workspace> <host> [name=value ...]"
            return 2
    }

// A named pipe has no peer, so a caller that opens it instead of classifying it never returns and only the parent's watchdog can end this child.
let private special (arguments: string list) : Async<int> =
    async {
        match arguments with
        | [ root; pipe; tree ] ->
            let expected = ResolverKind.name ResolverKind.Other
            let failures = ResizeArray<string>()

            let refuse label (outcome: Result<unit, ReleaseError>) =
                match outcome with
                | Ok () -> failures.Add(sprintf "%s admitted the pipe instead of refusing it" label)
                | Error error ->
                    let reported = ReleaseError.message error

                    if not (reported.Contains(expected, StringComparison.Ordinal)) then
                        failures.Add(sprintf "%s expected '%s', got %s" label expected reported)

            let! inputOutcome = ResolverInput.file root "host" pipe
            refuse "the launch input" inputOutcome

            let! pinOutcome = ResolverInput.pin pipe
            refuse "the pin" (Result.map ignore pinOutcome)

            let! readOutcome = ResolverDisk.read root pipe 1024L
            refuse "the read" (Result.map ignore readOutcome)

            let! guardOutcome = ResolverLock.exclusive pipe
            refuse "the exclusive guard" (Result.map ignore guardOutcome)

            let! leaseOutcome = ResolverLock.shared pipe
            refuse "the shared lease" (Result.map ignore leaseOutcome)

            let! inventoryOutcome = ResolverDisk.inventory tree
            refuse "the inventory" (Result.map ignore inventoryOutcome)

            let! cleanupOutcome = ResolverDisk.remove root pipe
            refuse "the cleanup" cleanupOutcome

            if not (File.Exists pipe) then
                failures.Add "a refused cleanup removed the pipe"

            if failures.Count > 0 then
                for detail in failures do
                    eprintfn "%s" detail

                return 1
            else
                printfn "ok 7 refusals"
                return 0

        | _ ->
            eprintfn "probe special expects <root> <pipe> <tree>"
            return 2
    }

// Each mode answers one runner-owned shape, so the bounded process path is exercised against a child whose every byte is controlled here.
let private statRecords =
    [ "hex-regular", "81a4"
      "hex-directory", "41ed"
      "hex-link", "a1ff"
      "hex-fifo", "11a4"
      "hex-socket", "c1a4"
      "hex-character", "21b6"
      "octal-regular", "100644"
      "octal-directory", "40755"
      "octal-link", "120777"
      "malformed", "81a4 not-a-mode"
      "signed", "-81a4"
      "prefixed", "0x81a4"
      "padded", " 81a4"
      "empty", ""
      "extra-record", "81a4\n81a4\n"
      "overflow-hex", String('f', 24)
      "overflow-octal", String('7', 22) ]

let private stat (arguments: string list) : Async<int> =
    async {
        match arguments with
        | [ mode; receipt; target ] ->
            let emit (text: string) =
                Console.Out.Write text
                Console.Out.Flush()

            let complain (text: string) =
                Console.Error.Write text
                Console.Error.Flush()

            // The receipt carries what the child actually received and its own process id, so the parent can prove exact forwarding and a reaped child.
            File.WriteAllText(receipt, String.Join("\u001f", [ mode; receipt; target ]))
            File.WriteAllText(receipt + ".pid", string Environment.ProcessId)

            match mode with
            | "nonzero" ->
                complain "the controlled child refused the classification"
                return 3
            | "stdout-flood" ->
                // The bounded reader owns this decision; buffering past the limit is the defect the mode exists to expose.
                emit (String('a', 256))
                return 0
            | "stderr-flood" ->
                complain (String('b', 8192))
                return 0
            | "stall" ->
                // Only an outer watchdog can end this child, so the runner's own deadline is what has to stop it.
                do! Async.Sleep 30000
                emit "81a4"
                return 0
            | _ ->
                match statRecords |> List.tryFind (fun (name, _) -> name = mode) with
                | Some(_, text) ->
                    emit (text + Environment.NewLine)
                    return 0
                | None ->
                    complain ("probe stat has no mode " + mode)
                    return 2

        | _ ->
            eprintfn "probe stat expects <mode> <receipt> <target>"
            return 2
    }

let run (argv: string array) : int option =
    let dispatched =
        match List.ofArray argv with
        | "--probe" :: "install" :: rest -> Some(install rest)
        | "--probe" :: "guard" :: rest -> Some(hold rest)
        | "--probe" :: "run" :: rest -> Some(launch rest)
        | "--probe" :: "special" :: rest -> Some(special rest)
        | "--probe" :: "stat" :: rest -> Some(stat rest)
        | _ -> None

    dispatched |> Option.map Async.RunSynchronously

let private assemblyPath = Assembly.GetEntryAssembly().Location

let statAssembly = assemblyPath

// The classification runner clears the environment, so a bare host name could not be resolved and no PATH lookup may be assumed.
let statHost =
    let rec ascend (directory: string) (levels: int) =
        if levels = 0 then
            directory
        else
            ascend (Path.GetDirectoryName directory) (levels - 1)

    let runtime = Path.TrimEndingDirectorySeparator(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
    Path.Combine(ascend runtime 3, if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet")

let statArguments (mode: string) (receipt: string) (target: string) : string list =
    [ "exec"; assemblyPath; "--probe"; "stat"; mode; receipt; target ]

let startProbe (mode: string) (arguments: string list) : Process =
    let info = ProcessStartInfo("dotnet")
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true

    for argument in ([ assemblyPath; "--probe"; mode ] @ arguments) do
        info.ArgumentList.Add argument

    let child = new Process(StartInfo = info)

    if not (child.Start()) then
        failtest "the probe process did not start"

    child

let awaitProbe (child: Process) : Async<int * string * string> =
    async {
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        do! child.WaitForExitAsync() |> Async.AwaitTask
        let! out = stdout |> Async.AwaitTask
        let! err = stderr |> Async.AwaitTask
        let code = child.ExitCode
        child.Dispose()
        return code, out, err
    }

// A child that blocks on a pipe with no peer never exits on its own, so the parent bounds the wait, kills the whole tree, and reports the hang as a failure instead of a skip.
let awaitProbeWithin (bound: TimeSpan) (child: Process) : Async<int * string * string> =
    async {
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        let mutable blocked = false

        try
            do! child.WaitForExitAsync().WaitAsync bound |> Async.AwaitTask
        with :? TimeoutException ->
            blocked <- true

            try
                child.Kill true
            with :? InvalidOperationException ->
                ()

            do! child.WaitForExitAsync() |> Async.AwaitTask

        let! out = stdout |> Async.AwaitTask
        let! err = stderr |> Async.AwaitTask
        let code = child.ExitCode
        child.Dispose()

        if blocked then
            failtestf "the child exceeded its %O watchdog, so a caller opened an entry instead of refusing it" bound

        return code, out, err
    }

// A launched product inherits this probe's own standard handles, so the parent that owns the conversation must also own the handles this process was started with.
let startInteractive (mode: string) (arguments: string list) : Process =
    let info = ProcessStartInfo("dotnet")
    info.UseShellExecute <- false
    info.RedirectStandardInput <- true
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true

    for argument in ([ assemblyPath; "--probe"; mode ] @ arguments) do
        info.ArgumentList.Add argument

    let child = new Process(StartInfo = info)

    if not (child.Start()) then
        failtest "the interactive probe process did not start"

    child
