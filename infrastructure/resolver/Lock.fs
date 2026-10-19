// role: reusable module/helper
// Never-unlinked files keep coordination stable across holder crashes.
module ResolverLock

open System
open System.IO
open SharedFailure

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

let private admit (path: string) : Async<Result<unit, ReleaseError>> =
    async {
    match ResolverDisk.chain path with
    | Error error -> return Error error
    | Ok () ->
        let full = Path.GetFullPath path
        let root = Path.GetPathRoot full
        let parent = Path.GetDirectoryName full

        let ancestors =
            parent.Substring(root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            |> Array.filter (fun segment -> segment <> "")
            |> Array.fold (fun (current, paths) segment ->
                let next = Path.Combine(current, segment)
                next, paths @ [ next ]) (root, [])
            |> snd

        let mutable failure: ReleaseError option = None

        for ancestor in ancestors do
            if failure.IsNone then
                match! ResolverKind.classify ancestor with
                | Ok ResolverKind.Directory -> ()
                | Ok kind -> failure <- Some(FileFailure("lock", ancestor, $"an ancestor is a {ResolverKind.name kind}, not a directory"))
                | Error error -> failure <- Some error

        match failure with
        | Some error -> return Error error
        | None ->
            match ResolverDisk.tryExists "lock" full with
            | Error error -> return Error error
            | Ok false -> return Ok()
            | Ok true ->
                match! ResolverKind.classify full with
                | Ok ResolverKind.Regular -> return Ok()
                | Ok kind -> return Error(FileFailure("lock", full, $"the coordination entry is a {ResolverKind.name kind}, not a regular file"))
                | Error error -> return Error error
    }

let private openFile (path: string) (access: FileAccess) (share: FileShare) : Async<Attempt> =
    async {
    match! admit path with
    | Error error -> return Refused error
    | Ok () ->
        try
            return Acquired(new FileStream(path, FileMode.OpenOrCreate, access, share, 1, FileOptions.None))
        with
        // Preserve the IO detail because unrelated failures otherwise resemble contention.
        | :? IOException as error -> return Busy error.Message
        | :? UnauthorizedAccessException as error -> return Refused(FileFailure("lock", path, error.Message))
        | :? NotSupportedException as error -> return Refused(FileFailure("lock", path, error.Message))
        | :? ArgumentException as error -> return Refused(FileFailure("lock", path, error.Message))
    }

// The second open detects filesystems that silently skip the exclusivity contract.
let private proven (path: string) : Result<unit, string> =
    try
        use probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        Error "a second exclusive open succeeded, so no lock is enforced here"
    with
    | :? IOException -> Ok()
    | error -> Error $"the exclusivity probe failed: {error.GetType().Name}: {error.Message}"

let private acquire
    (path: string)
    (role: string)
    (access: FileAccess)
    (share: FileShare)
    (proof: bool)
    : Async<Result<Lock, ReleaseError>> =
    let rec step (remaining: int) (last: string) =
        async {
            match! openFile path access share with
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

    // A missing parent cannot become available by retrying the lock.
    if not (Directory.Exists(Path.GetDirectoryName path)) then
        async { return Error(FileFailure("lock", path, "the directory of the coordination file does not exist")) }
    else
        step attemptLimit ""

let exclusive (path: string) : Async<Result<Lock, ReleaseError>> =
    acquire path "the exclusive install guard" FileAccess.ReadWrite FileShare.None true

// Shared consumers conflict with the exclusive rebuild holder.
let shared (path: string) : Async<Result<Lock, ReleaseError>> =
    acquire path "the shared usage lease" FileAccess.ReadWrite FileShare.ReadWrite false
