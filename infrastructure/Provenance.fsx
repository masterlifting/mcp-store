// role: reusable module/helper
// Repo-shared build provenance and release-process mechanics loaded as the
// explicit BuildProvenance module. Owns the shared release error vocabulary and
// async process boundary so expected failures stay typed at that boundary.
module BuildProvenance

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
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

// Typed expected failures for the release flow. Cases carry structured context
// (operation, exit code, expected/actual) rather than only formatted text.
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

// Focused composition for the release flow only: it sequences Async<Result<_,ReleaseError>>
// steps without introducing a second effect vocabulary or a duplicate sync path.
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

// Hashes one release artifact asynchronously; the digest is the exact lowercase
// SHA-256 shape every manifest and consumer pin records.
let sha256FileAsync (path: string) : Async<Result<string, ReleaseError>> =
    async {
        try
            let! bytes = awaitOperational (File.ReadAllBytesAsync path)
            return Ok(SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant())
        with
        | :? OperationCanceledException as error -> return raise error
        | :? IOException as error -> return Error(FileFailure("hash", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("hash", path, error.Message))
    }

// Writes a JSON artifact with the shared indented formatting so manifests and
// consumer pins stay byte-stable across producers.
let writeJsonAsync (path: string) (value: JsonNode) : Async<Result<unit, ReleaseError>> =
    async {
        try
            do! awaitComplete (File.WriteAllTextAsync(path, value.ToJsonString(JsonSerializerOptions(WriteIndented = true))))
            return Ok()
        with
        | :? OperationCanceledException as error -> return raise error
        | :? IOException as error -> return Error(FileFailure("write", path, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("write", path, error.Message))
    }

// Reads one small text artifact asynchronously, classifying expected absence and
// read failure at the release boundary.
let readAllTextAsync (path: string) : Async<Result<string, ReleaseError>> =
    async {
        if not (File.Exists path) then
            return Error(MissingArtifact path)
        else
            try
                let! text = awaitOperational (File.ReadAllTextAsync path)
                return Ok text
            with
            | :? OperationCanceledException as error -> return raise error
            | :? IOException as error -> return Error(FileFailure("read", path, error.Message))
            | :? UnauthorizedAccessException as error -> return Error(FileFailure("read", path, error.Message))
    }

// Runs one external process asynchronously, draining both pipes concurrently so
// a full stderr pipe cannot deadlock the child, and returns its stdout.
let runProcess
    (workingDirectory: string)
    (executable: string)
    (arguments: string list)
    (cancellationToken: CancellationToken)
    : Async<Result<string, ReleaseError>> =
    async {
        let argumentText = String.concat " " arguments
        let operation = $"{executable} {argumentText}"
        let info = ProcessStartInfo(executable)
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        arguments |> List.iter info.ArgumentList.Add
        use childProcess = new Process(StartInfo = info)

        let terminate () =
            try
                if not childProcess.HasExited then childProcess.Kill(true)
            with
            | :? InvalidOperationException -> ()
            | :? System.ComponentModel.Win32Exception -> ()
            | :? NotSupportedException -> ()

        try
            if not (childProcess.Start()) then
                return Error(ProcessStartFailure(operation, "process could not be started"))
            else
                // Cancellation can abort the async without entering a try/with, so
                // termination is registered on the token itself.
                use registration = cancellationToken.Register(fun () -> terminate ())
                let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()
                let stderrTask = childProcess.StandardError.ReadToEndAsync()

                try
                    do! awaitComplete (childProcess.WaitForExitAsync cancellationToken)
                with :? OperationCanceledException as cancelled ->
                    terminate ()
                    do! awaitComplete (childProcess.WaitForExitAsync())

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

                if cancellationToken.IsCancellationRequested then
                    return raise (OperationCanceledException cancellationToken)
                elif childProcess.ExitCode <> 0 then
                    return Error(ProcessFailed(operation, childProcess.ExitCode, stderr.Trim()))
                else
                    return Ok(stdout.Trim())
        with
        | :? OperationCanceledException as error -> return raise error
        | :? System.ComponentModel.Win32Exception as error -> return Error(ProcessStartFailure(operation, error.Message))
        | :? InvalidOperationException as error -> return Error(ProcessStartFailure(operation, error.Message))
        | :? IOException as error -> return Error(ProcessStartFailure(operation, error.Message))
    }

let private gitRun (root: string) (args: string list) =
    async {
        let! cancellationToken = Async.CancellationToken
        return! runProcess root "git" args cancellationToken
    }

let committedHead (root: string) : Async<Result<string, ReleaseError>> =
    async {
        match! gitRun root [ "rev-parse"; "--verify"; "HEAD^{commit}" ] with
        | Error error -> return Error error
        | Ok sha when sha.Length = 40 -> return Ok sha
        | Ok sha -> return Error(UnresolvedRevision $"HEAD did not resolve to a commit SHA: '{sha}'")
    }

let assertCleanTree (root: string) : Async<Result<unit, ReleaseError>> =
    async {
        match! gitRun root [ "status"; "--porcelain" ] with
        | Error error -> return Error error
        | Ok "" -> return Ok()
        | Ok dirty -> return Error(DirtyTree dirty)
    }

// Validates that the on-disk manifest records the expected committed HEAD and
// returns that validated revision so callers can propagate the exact same value
// into consumer pins instead of generating a revision independently.
let assertManifestRevision (manifestPath: string) (expected: string) : Async<Result<string, ReleaseError>> =
    async {
        match! readAllTextAsync manifestPath with
        | Error error -> return Error error
        | Ok text ->
            let revision =
                try
                    let node = JsonNode.Parse text

                    if isNull node then
                        Error "the document is empty"
                    else
                        let document = node.AsObject()

                        if not (document.ContainsKey "revision") then
                            Error "the 'revision' field is missing"
                        else
                            let value = document["revision"]

                            if isNull value then
                                Error "the 'revision' field is missing"
                            else
                                Ok(value.GetValue<string>())
                with
                | :? System.Text.Json.JsonException as error -> Error $"the manifest is not valid JSON: {error.Message}"
                | :? FormatException as error -> Error $"the manifest is not valid JSON: {error.Message}"
                | :? InvalidOperationException as error -> Error $"the manifest is not valid JSON: {error.Message}"

            match revision with
            | Error detail -> return Error(MalformedArtifact(manifestPath, detail))
            | Ok actual when actual = expected -> return Ok actual
            | Ok actual -> return Error(RevisionMismatch(expected, actual))
    }
