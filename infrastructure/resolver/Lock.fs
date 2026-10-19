// role: reusable module/helper
// Same-pin cross-process coordination on never-unlinked OS files: the exclusive guard
// serializes installation and repair, and the shared lease is held for a consumer lifetime.
module ResolverLock

open System
open System.IO
open SharedFailure

// A held coordination file. Releasing closes the handle, which is the whole protocol: the
// file is neither unlinked nor rewritten, so no unlink race and no stale-lock deletion exists.
type Lock =
    private { Handle: FileStream }

    member this.Release() = this.Handle.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Handle.Dispose()

let private retryDelay = 100
let private attemptLimit = 600

type private Attempt =
    | Acquired of FileStream
    | Busy of string
    | Refused of ReleaseError

let private openFile (path: string) (access: FileAccess) (share: FileShare) : Attempt =
    try
        Acquired(new FileStream(path, FileMode.OpenOrCreate, access, share, 1, FileOptions.None))
    with
    // A sharing conflict is the expected contention signal; the message is retained because
    // an unrelated IO failure is only distinguishable by the operator from its text.
    | :? IOException as error -> Busy error.Message
    | :? UnauthorizedAccessException as error -> Refused(FileFailure("lock", path, error.Message))
    | :? NotSupportedException as error -> Refused(FileFailure("lock", path, error.Message))
    | :? ArgumentException as error -> Refused(FileFailure("lock", path, error.Message))

// Enforcement is proven instead of assumed: a second exclusive open of a file this process already
// holds exclusively is refused only where the platform enforces sharing, so a lock that is silently
// absent (advisory locking skipped on the filesystem, or file locking disabled) is not trusted.
let private proven (path: string) : Result<unit, string> =
    try
        use probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        Error "a second exclusive open succeeded, so no lock is enforced here"
    with
    | :? IOException -> Ok()
    | error -> Error $"the exclusivity probe failed: {error.GetType().Name}: {error.Message}"

// Waiting stays bounded and cancellable, and a crashed holder releases its handle through
// the OS, so no process ever has to reclaim another process's coordination file.
let private acquire
    (path: string)
    (role: string)
    (access: FileAccess)
    (share: FileShare)
    (proof: bool)
    : Async<Result<Lock, ReleaseError>> =
    let rec step (remaining: int) (last: string) =
        async {
            match openFile path access share with
            | Acquired stream ->
                let verdict = if proof then proven path else Ok()

                match verdict with
                | Ok () -> return Ok { Handle = stream }
                | Error detail ->
                    stream.Dispose()
                    return Error(CoordinationUnsupported(path, detail))
            | Refused error -> return Error error
            | Busy message ->
                if remaining <= 0 then
                    let bound = attemptLimit * retryDelay
                    return Error(InstallInUse(path, $"{role} was not available within {bound} ms and is held by another process: {last}"))
                else
                    do! Async.Sleep retryDelay
                    return! step (remaining - 1) message
        }

    // A missing parent is reported at once; retrying it would only delay the same answer.
    if not (Directory.Exists(Path.GetDirectoryName path)) then
        async { return Error(FileFailure("lock", path, "the directory of the coordination file does not exist")) }
    else
        step attemptLimit ""

let exclusive (path: string) : Async<Result<Lock, ReleaseError>> =
    acquire path "the exclusive install guard" FileAccess.ReadWrite FileShare.None true

// ReadWrite/ReadWrite is maximally permissive among concurrent consumers while still
// conflicting with the exclusive holder's FileShare.None in either direction.
let shared (path: string) : Async<Result<Lock, ReleaseError>> =
    acquire path "the shared usage lease" FileAccess.ReadWrite FileShare.ReadWrite false
