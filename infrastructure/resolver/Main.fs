// role: entrypoint/command
// Only resolve snapshots use stdout; run leaves it to the child.
module ResolverMain

open System
open SharedFailure

// Child exit codes are preserved, even when they match resolver failure codes.
let private failure (error: ReleaseError) : int =
    Console.Error.WriteLine(ReleaseError.message error)

    match error with
    | UsageRejected _ -> 2
    | _ -> 1

[<EntryPoint>]
let main arguments =
    match ResolverCli.parse (List.ofArray arguments) with
    | Error error -> failure error
    | Ok command ->
        let transport = ResolverTransport.defaultTransport ()

        let work =
            async {
                match command with
                | ResolverCli.Resolve target ->
                    match! ResolverRun.resolve transport target with
                    | Error error -> return Error error
                    | Ok snapshot ->
                        Console.Out.WriteLine snapshot
                        return Ok 0
                | ResolverCli.Run options ->
                    match! ResolverRun.run transport options with
                    | Error error -> return Error error
                    | Ok(ResolverChild.Exited code) -> return Ok code
                    | Ok(ResolverChild.Signalled code) ->
                        Console.Error.WriteLine $"the child was terminated after signal {code - 128}"
                        return Ok code
            }

        // the compiled CLI must finish its asynchronous operation before exiting
        match Async.RunSynchronously work with
        | Ok code -> code
        | Error error -> failure error
