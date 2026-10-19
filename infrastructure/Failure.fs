// role: reusable module/helper
// Repo-shared expected-failure vocabulary and its async-result composition.
// The module name avoids the F# core `Failure` exception, which shadows it in referencing assemblies.
// Pure: no filesystem, process, network, or credential access.
module SharedFailure

open System
open System.Threading.Tasks

// Async.AwaitTask reports a faulted task as AggregateException; surface its
// single operational cause so focused catches can classify it.
let awaitOperational (work: Task<'T>) : Async<'T> =
    async {
        try
            return! Async.AwaitTask work
        with
        | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
            return raise aggregate.InnerExceptions.[0]
        | error -> return raise error
    }

let awaitComplete (work: Task) : Async<unit> =
    async {
        try
            do! Async.AwaitTask work
        with
        | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
            return raise aggregate.InnerExceptions.[0]
        | error -> return raise error
    }

// Typed expected failures. Cases carry structured context (operation, exit code,
// expected/actual, transport location) rather than only formatted text.
type ReleaseError =
    | ProcessStartFailure of operation: string * detail: string
    | ProcessFailed of operation: string * exitCode: int * detail: string
    | DirtyTree of detail: string
    | UnresolvedRevision of detail: string
    | MissingArtifact of path: string
    | MalformedArtifact of path: string * detail: string
    | IdentityMismatch of field: string * expected: string * actual: string
    | RevisionMismatch of expected: string * actual: string
    | AllowlistMismatch of description: string * expected: string list * actual: string list
    | FileFailure of operation: string * path: string * detail: string
    | TransportRejected of uri: string * detail: string
    | TransportFailure of uri: string * detail: string
    | InstallInUse of path: string * detail: string
    | CoordinationUnsupported of path: string * detail: string
    | CorruptInstall of path: string * detail: string
    | RuntimeUnavailable of framework: string * version: string * detail: string
    | LaunchInputRejected of input: string * detail: string
    | UsageRejected of detail: string

module ReleaseError =
    let message error =
        match error with
        | ProcessStartFailure (operation, detail) -> $"{operation} could not be started: {detail}"
        | ProcessFailed (operation, exitCode, detail) -> $"{operation} failed ({exitCode}): {detail}"
        | DirtyTree detail -> $"refusing to package from a dirty source tree:\n{detail}"
        | UnresolvedRevision detail -> $"producer revision could not be resolved from a committed HEAD: {detail}"
        | MissingArtifact path -> $"required release artifact does not exist: {path}"
        | MalformedArtifact (path, detail) -> $"release artifact '{path}' is invalid: {detail}"
        | IdentityMismatch (field, expected, actual) -> $"{field} does not match the release pin: expected {expected}, got {actual}"
        | RevisionMismatch (expected, actual) ->
            $"manifest revision {actual} does not match clean HEAD {expected}; rebuild the distribution"
        | AllowlistMismatch (description, expected, actual) -> $"{description}: expected exactly {expected}, got {actual}"
        | FileFailure (operation, path, detail) -> $"{operation} failed for '{path}': {detail}"
        | TransportRejected (uri, detail) -> $"the release transport rejected '{uri}': {detail}"
        | TransportFailure (uri, detail) -> $"the release request to '{uri}' failed: {detail}"
        | InstallInUse (path, detail) -> $"the distribution coordination file '{path}' is not available: {detail}"
        | CoordinationUnsupported (path, detail) -> $"exclusive coordination cannot be enforced for '{path}': {detail}"
        | CorruptInstall (path, detail) -> $"the installed distribution '{path}' is not verifiable: {detail}"
        | RuntimeUnavailable (framework, version, detail) -> $"the required runtime {framework} {version} is not available: {detail}"
        | LaunchInputRejected (input, detail) -> $"the launch input '{input}' is not valid: {detail}"
        | UsageRejected detail -> $"the command line is invalid: {detail}"

// Focused composition over Async<Result<_,ReleaseError>> steps without introducing a
// second effect vocabulary or a duplicate synchronous path.
type ReleaseResultBuilder() =
    member _.Bind(work: Async<Result<'a, ReleaseError>>, next: 'a -> Async<Result<'b, ReleaseError>>) =
        async {
            match! work with
            | Ok value -> return! next value
            | Error error -> return Error error
        }

    member _.Bind(value: Result<'a, ReleaseError>, next: 'a -> Async<Result<'b, ReleaseError>>) =
        match value with
        | Ok value -> next value
        | Error error -> async { return Error error }

    member _.Return(value: 'a) : Async<Result<'a, ReleaseError>> = async { return Ok value }
    member _.ReturnFrom(work: Async<Result<'a, ReleaseError>>) = work
    member _.ReturnFrom(value: Result<'a, ReleaseError>) = async { return value }
    member _.Zero() : Async<Result<unit, ReleaseError>> = async { return Ok() }

    // Deferring the remainder is required for source-order effects; an eager delay
    // evaluated later synchronous steps before an earlier for/async step completed.
    member _.Delay(generator: unit -> Async<Result<'a, ReleaseError>>) = async { return! generator () }

    member _.Combine(first: Async<Result<unit, ReleaseError>>, second: Async<Result<'a, ReleaseError>>) =
        async {
            match! first with
            | Ok() -> return! second
            | Error error -> return Error error
        }

    member this.For(sequence: seq<'a>, body: 'a -> Async<Result<unit, ReleaseError>>) =
        this.Bind(
            async {
                use enumerator = sequence.GetEnumerator()
                let mutable failure = None

                while failure.IsNone && enumerator.MoveNext() do
                    match! body enumerator.Current with
                    | Ok() -> ()
                    | Error error -> failure <- Some error

                return
                    match failure with
                    | Some error -> Error error
                    | None -> Ok()
            },
            fun () -> this.Zero()
        )

let releaseResult = ReleaseResultBuilder()
