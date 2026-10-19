// Child probe contract: the parent starts this same test assembly with `--probe <mode>` so the
// real cross-process guard, first-install race, crash recovery, and product launch run untouched.
// Standalone entry bridge: an independent child must turn its result into a process exit code.
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
            // The canonical transport reaches the GitHub release only, so locally packed bytes are
            // served in place; every other boundary in this path stays the real resolver code.
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

let run (argv: string array) : int option =
    match List.ofArray argv with
    | "--probe" :: "install" :: rest -> Some(Async.RunSynchronously(install rest))
    | "--probe" :: "guard" :: rest -> Some(Async.RunSynchronously(hold rest))
    | "--probe" :: "run" :: rest -> Some(Async.RunSynchronously(launch rest))
    | _ -> None

let private assemblyPath = Assembly.GetEntryAssembly().Location

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

// A launched product inherits this probe's own standard handles, so the parent that owns the
// conversation must also own the handles this process was started with.
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
