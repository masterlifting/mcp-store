// role: entrypoint/command
// Process entry for the documented CLI; the resolve snapshot is the only thing this program
// writes to stdout, so a child never competes with resolver output for that channel.
module ResolverMain

open System
open SharedFailure

// 1 marks a resolver failure and 2 a command line the CLI refused; a launched child's own
// status is reported unchanged and is documented as indistinguishable from these in `run`.
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
