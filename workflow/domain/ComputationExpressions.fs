namespace Common

open System

module CE =
    type ResultBuilder() =
        member _.Bind(m, f) = Result.bind f m
        member _.Return(x) = Ok x
        member _.ReturnFrom(m: Result<_, _>) = m
        member _.Zero() = Ok()
        member _.Delay(f: unit -> Result<'a, 'e>) = f
        member _.Run(f: unit -> Result<'a, 'e>) = f ()

        member _.Combine(a: Result<unit, 'e>, b: unit -> Result<'b, 'e>) =
            match a with
            | Ok() -> b ()
            | Error e -> Error e

    let result = ResultBuilder()

    // Sequences Async<Result<_, RuntimeError>> steps with scoped resources; the
    // effectful boundary composition, not a second runtime implementation.
    type AsyncResultBuilder() =
        member _.Bind(m: Async<Result<'a, 'e>>, f: 'a -> Async<Result<'b, 'e>>) =
            async {
                match! m with
                | Ok x -> return! f x
                | Error e -> return Error e
            }

        member _.Bind(m: Result<'a, 'e>, f: 'a -> Async<Result<'b, 'e>>) =
            async {
                match m with
                | Ok x -> return! f x
                | Error e -> return Error e
            }

        member _.Return(x) = async { return Ok x }
        member _.ReturnFrom(m: Async<Result<'a, 'e>>) = m
        member _.ReturnFrom(m: Result<'a, 'e>) = async { return m }
        member _.Zero() = async { return Ok() }
        member _.Delay(f: unit -> Async<Result<'a, 'e>>) = async { return! f () }

        member _.Combine(a: Async<Result<unit, 'e>>, b: Async<Result<'b, 'e>>) =
            async {
                match! a with
                | Ok() -> return! b
                | Error e -> return Error e
            }

        member _.Using(resource: #IDisposable, binder: _ -> Async<Result<'a, 'e>>) : Async<Result<'a, 'e>> =
            async {
                try
                    return! binder resource
                finally
                    if not (isNull (box resource)) then
                        resource.Dispose()
            }

        member _.TryFinally(body: Async<Result<'a, 'e>>, compensation: unit -> unit) : Async<Result<'a, 'e>> =
            async {
                try
                    return! body
                finally
                    compensation ()
            }

        member _.TryWith(body: Async<Result<'a, 'e>>, handler: exn -> Async<Result<'a, 'e>>) : Async<Result<'a, 'e>> =
            async {
                try
                    return! body
                with error ->
                    return! handler error
            }

    let asyncResult = AsyncResultBuilder()
