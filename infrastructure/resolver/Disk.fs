// role: reusable module/helper
// Link-free paths and rename-published writes keep operations within the validated storage tree.
module ResolverDisk

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open SharedFailure

// Bounds keep a damaged or hostile tree from turning a scan into unbounded work.
let private maxEntries = 8192
let private maxScanBytes = 512L * 1024L * 1024L
let private maxDepth = 64

// Aborts a walk of a damaged tree; the public entry points map it to a typed failure.
exception private WalkAbort of string

let private abort (detail: string) : 'T = raise (WalkAbort detail)

let exists (path: string) : bool = File.Exists path || Directory.Exists path

let tryExists (operation: string) (path: string) : Result<bool, ReleaseError> =
    try
        File.GetAttributes path |> ignore
        Ok true
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> Ok false
    | :? IOException as error -> Error(FileFailure(operation, path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure(operation, path, error.Message))

// LinkTarget detects dangling links that existence checks miss.
let linked (path: string) : bool =
    try
        let info = FileInfo path :> FileSystemInfo

        if not (isNull info.LinkTarget) then
            true
        else
            (File.GetAttributes info.FullName).HasFlag FileAttributes.ReparsePoint
    with
    | :? FileNotFoundException -> false
    | :? DirectoryNotFoundException -> false
    // An unreadable path is refused rather than treated as a safe regular entry.
    | :? IOException -> true
    | :? UnauthorizedAccessException -> true

let private full (value: string) = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

// Textual containment prevents callers from composing a target outside the supplied root.
let under (root: string) (path: string) : Result<string, ReleaseError> =
    try
        let rootFull = full (Path.GetFullPath root)
        let pathFull = full (Path.GetFullPath path)

        let comparison =
            // macOS volumes may fold case, so ordinal matching could alias one physical cache path.
            if OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        if pathFull.Equals(rootFull, comparison)
           || pathFull.StartsWith(rootFull + string Path.DirectorySeparatorChar, comparison) then
            Ok pathFull
        else
            Error(FileFailure("containment", path, $"the path is outside the resolver storage root '{rootFull}'"))
    with
    | :? ArgumentException as error -> Error(FileFailure("containment", path, error.Message))
    | :? NotSupportedException as error -> Error(FileFailure("containment", path, error.Message))
    | :? PathTooLongException as error -> Error(FileFailure("containment", path, error.Message))

// Checking every component prevents a planted link from redirecting an operation.
let private walk (start: string) (segments: string list) : Result<unit, ReleaseError> =
    let rec step (current: string) (remaining: string list) =
        if linked current then
            Error(FileFailure("link", current, "the path is a link or reparse point"))
        else
            match remaining with
            | [] -> Ok()
            | segment :: rest -> step (Path.Combine(current, segment)) rest

    if List.length segments > maxDepth then
        Error(FileFailure("link", start, $"the path is deeper than the {maxDepth}-component bound"))
    else
        step start segments

let rejectLinks (root: string) (target: string) : Result<unit, ReleaseError> =
    match under root target with
    | Error error -> Error error
    | Ok targetFull ->
        let rootFull = full (Path.GetFullPath root)

        let relative =
            targetFull.Substring(rootFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        let segments = if relative = "" then [] else List.ofArray (relative.Split Path.DirectorySeparatorChar)

        walk rootFull segments

// A link in an external path can make its physical target differ from validated path text.
let chain (path: string) : Result<unit, ReleaseError> =
    try
        let absolute = Path.GetFullPath path
        let root = Path.GetPathRoot absolute

        if String.IsNullOrEmpty root then
            Error(FileFailure("link", path, "the path has no filesystem root"))
        else
            let segments =
                absolute.Substring(root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                |> List.ofArray
                |> List.filter (fun segment -> segment <> "")

            walk root segments
    with
    | :? ArgumentException as error -> Error(FileFailure("link", path, error.Message))
    | :? NotSupportedException as error -> Error(FileFailure("link", path, error.Message))
    | :? PathTooLongException as error -> Error(FileFailure("link", path, error.Message))

let private create (path: string) : Result<unit, ReleaseError> =
    try
        Directory.CreateDirectory path |> ignore
        Ok()
    with
    | :? IOException as error -> Error(FileFailure("directory", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("directory", path, error.Message))
    | :? NotSupportedException as error -> Error(FileFailure("directory", path, error.Message))

let ensureRoot (path: string) : Result<unit, ReleaseError> =
    match chain path with
    | Error error -> Error error
    | Ok () -> create path

let ensureDirectory (root: string) (path: string) : Result<unit, ReleaseError> =
    match rejectLinks root path with
    | Error error -> Error error
    | Ok () -> create path

let read (root: string) (path: string) (bound: int64) : Async<Result<byte[], ReleaseError>> =
    async {
        match rejectLinks root path with
        | Error error -> return Error error
        | Ok () ->
            match! ResolverKind.classify path with
            | Error error -> return Error error
            | Ok ResolverKind.Regular ->
                try
                    let info = FileInfo path
                    if info.Length > bound then
                        return Error(FileFailure("read", path, $"the file is {info.Length} bytes which exceeds the {bound}-byte bound"))
                    else
                        return Ok(File.ReadAllBytes path)
                with
                | :? IOException as error -> return Error(FileFailure("read", path, error.Message))
                | :? UnauthorizedAccessException as error -> return Error(FileFailure("read", path, error.Message))
            | Ok kind -> return Error(FileFailure("read", path, $"the path is a {ResolverKind.name kind}, not a regular file"))
    }

// Only a plain-file partial is replaced because other entry kinds are unowned content.
let private discardPartial (partial: string) : Async<Result<unit, ReleaseError>> =
    async {
        if linked partial then return Error(FileFailure("write", partial, "a leftover partial is a link or reparse point"))
        else
            match tryExists "write" partial with
            | Error error -> return Error error
            | Ok false -> return Ok()
            | Ok true ->
                match! ResolverKind.classify partial with
                | Error error -> return Error error
                | Ok ResolverKind.Regular ->
                    try
                        File.Delete partial
                        return Ok()
                    with
                    | :? IOException as error -> return Error(FileFailure("write", partial, error.Message))
                    | :? UnauthorizedAccessException as error -> return Error(FileFailure("write", partial, error.Message))
                | Ok kind -> return Error(FileFailure("write", partial, $"a leftover partial is a {ResolverKind.name kind}, which this resolver did not write"))
    }

let write (root: string) (path: string) (bytes: byte[]) : Async<Result<unit, ReleaseError>> =
    let partial = path + ".partial"

    async {
        match rejectLinks root path, rejectLinks root partial with
        | Error error, _ -> return Error error
        | _, Error error -> return Error error
        | Ok (), Ok () ->
            match! discardPartial partial with
            | Error error -> return Error error
            | Ok () ->
                try
                    let create () =
                        // CreateNew refuses to follow an existing entry of any kind.
                        use stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        stream.Write(bytes, 0, bytes.Length)
                        stream.Flush true

                    create ()
                    File.Move(partial, path, true)
                    return Ok()
                with
                | :? IOException as error -> return Error(FileFailure("write", path, error.Message))
                | :? UnauthorizedAccessException as error -> return Error(FileFailure("write", path, error.Message))
    }

let move (root: string) (source: string) (destination: string) : Result<unit, ReleaseError> =
    match rejectLinks root source, rejectLinks root destination with
    | Error error, _ -> Error error
    | _, Error error -> Error error
    | Ok (), Ok () ->
        if exists destination then
            Error(FileFailure("publish", destination, "the destination already exists; an immutable installation is never overwritten"))
        else
            try
                // Same-volume rename; every caller keeps both paths under one storage root.
                Directory.Move(source, destination)
                Ok()
            with
            | :? IOException as error -> Error(FileFailure("publish", source, error.Message))
            | :? UnauthorizedAccessException as error -> Error(FileFailure("publish", source, error.Message))

let entries (directory: string) : Result<string list, ReleaseError> =
    if linked directory then
        Error(FileFailure("entries", directory, "the directory is a link or reparse point"))
    else
        try
            if not (Directory.Exists directory) then
                Error(FileFailure("entries", directory, "the directory does not exist"))
            else
                Ok(Directory.GetFileSystemEntries directory |> Array.toList)
        with
        | :? IOException as error -> Error(FileFailure("entries", directory, error.Message))
        | :? UnauthorizedAccessException as error -> Error(FileFailure("entries", directory, error.Message))

// The remaining scan budget caps reads if an entry changes after classification.
let private digest (path: string) (budget: int64) (started: Stopwatch) : string * int64 =
    use stream = File.OpenRead path
    use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
    let chunk = Array.zeroCreate<byte> 65536
    let mutable read = 0L
    let mutable finished = false

    while not finished do
        if started.Elapsed > TimeSpan.FromSeconds 60. then abort "the inventory exceeded its 60-second deadline"

        let count = stream.Read(chunk, 0, chunk.Length)

        if count = 0 then
            finished <- true
        else
            read <- read + int64 count

            if read > budget then
                abort $"the entry '{path}' exceeds the remaining {maxScanBytes}-byte scan budget"

            hash.AppendData(chunk, 0, count)

    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), read

// Special entries fail closed before open because inventories support only files and directories.
let inventory (tree: string) : Async<Result<(string * string) list, ReleaseError>> =
    async {
      let started = Stopwatch.StartNew()
    if linked tree then
        return Error(FileFailure("scan", tree, "the tree is a link or reparse point"))
    else
        try
            if not (Directory.Exists tree) then
                return Error(FileFailure("scan", tree, "the tree does not exist"))
            else
                let files = ResizeArray<string * string>()
                let mutable count = 0
                let mutable scanned = 0L

                let rec step (relative: string) (directory: string) = async {
                    for entry in Directory.GetFileSystemEntries directory do
                        if started.Elapsed > TimeSpan.FromSeconds 60. then abort "the inventory exceeded its 60-second deadline"
                        count <- count + 1

                        if count > maxEntries then
                            abort $"the tree exceeds the {maxEntries}-entry bound"

                        let name = Path.GetFileName entry
                        let nested = if relative = "" then name else relative + "/" + name

                        let remaining = TimeSpan.FromSeconds 60. - started.Elapsed

                        if remaining <= TimeSpan.Zero then abort "the inventory exceeded its 60-second deadline"

                        match! ResolverKind.classifyWithin remaining entry with
                        | Error error -> abort (ReleaseError.message error)
                        | Ok ResolverKind.Directory -> do! step nested entry
                        | Ok ResolverKind.Regular ->
                            let info = FileInfo entry

                            if info.Length > maxScanBytes - scanned then
                                abort $"the tree exceeds the {maxScanBytes}-byte scan bound"

                            let digestValue, read = digest entry (maxScanBytes - scanned) started
                            scanned <- scanned + read
                            files.Add(nested, digestValue)
                        | Ok kind -> abort $"the entry '{entry}' is a {ResolverKind.name kind}, not a regular file or directory"
                    return ()
                }

                do! step "" tree

                return Ok(files |> List.ofSeq |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right)))
        with
        | WalkAbort detail -> return Error(FileFailure("scan", tree, detail))
        | :? IOException as error -> return Error(FileFailure("scan", tree, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("scan", tree, error.Message))
    }

// Only plain files and directories are deleted, so links and special entries remain untouched.
let remove (root: string) (target: string) : Async<Result<unit, ReleaseError>> = async {
    let started = Stopwatch.StartNew()

    match rejectLinks root target with
    | Error error -> return Error error
    | Ok () ->
        try
            if linked target then
                return Error(FileFailure("delete", target, "the target is a link or reparse point"))
            else
                match tryExists "delete" target with
                | Error error -> return Error error
                | Ok false -> return Ok()
                | Ok true ->
                    let remaining = TimeSpan.FromSeconds 60. - started.Elapsed

                    if remaining <= TimeSpan.Zero then abort "the cleanup exceeded its 60-second deadline"

                    match! ResolverKind.classifyWithin remaining target with
                    | Error error -> return Error error
                    | Ok ResolverKind.Directory ->
                        let mutable count = 0

                        let rec clear (directory: string) = async {
                            for entry in Directory.GetFileSystemEntries directory do
                                if started.Elapsed > TimeSpan.FromSeconds 60. then abort "the cleanup exceeded its 60-second deadline"

                                count <- count + 1

                                if count > maxEntries then
                                    abort $"the tree exceeds the {maxEntries}-entry bound"

                                let remaining = TimeSpan.FromSeconds 60. - started.Elapsed

                                if remaining <= TimeSpan.Zero then abort "the cleanup exceeded its 60-second deadline"

                                match! ResolverKind.classifyWithin remaining entry with
                                | Error error -> abort (ReleaseError.message error)
                                | Ok ResolverKind.Directory ->
                                    do! clear entry
                                    Directory.Delete(entry, false)
                                | Ok ResolverKind.Regular -> File.Delete entry
                                | Ok kind -> abort $"the entry '{entry}' is a {ResolverKind.name kind}"
                            return ()
                        }

                        do! clear target
                        Directory.Delete(target, false)
                        return Ok()
                    | Ok ResolverKind.Regular ->
                        File.Delete target
                        return Ok()
                    | Ok kind -> return Error(FileFailure("delete", target, $"the target is a {ResolverKind.name kind}, which this resolver did not create"))
        with
        | WalkAbort detail -> return Error(FileFailure("delete", target, detail))
        | :? IOException as error -> return Error(FileFailure("delete", target, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("delete", target, error.Message))
}
