// role: reusable module/helper
// Narrow filesystem adapter over the resolver-owned storage tree: regular files only, no
// reparse point on a traversed path, and every write published by rename.
module ResolverDisk

open System
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

// A reparse point is refused on an ancestor and on a descendant alike; LinkTarget also
// reports a link whose target is missing, which an existence test cannot.
let linked (path: string) : bool =
    try
        let info =
            if Directory.Exists path then
                DirectoryInfo path :> FileSystemInfo
            else
                FileInfo path :> FileSystemInfo

        not (isNull info.LinkTarget)
        || (exists path && (File.GetAttributes info.FullName).HasFlag FileAttributes.ReparsePoint)
    with
    | :? FileNotFoundException -> false
    | :? DirectoryNotFoundException -> false
    // An unreadable path is refused rather than treated as a safe regular entry.
    | :? IOException -> true
    | :? UnauthorizedAccessException -> true

let private full (value: string) = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

// Containment is decided on full paths under an explicit root, so no caller can compose a
// target that escapes the tree it was given.
let under (root: string) (path: string) : Result<string, ReleaseError> =
    try
        let rootFull = full (Path.GetFullPath root)
        let pathFull = full (Path.GetFullPath path)

        if pathFull = rootFull || pathFull.StartsWith(rootFull + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
            Ok pathFull
        else
            Error(FileFailure("containment", path, $"the path is outside the resolver storage root '{rootFull}'"))
    with
    | :? ArgumentException as error -> Error(FileFailure("containment", path, error.Message))
    | :? NotSupportedException as error -> Error(FileFailure("containment", path, error.Message))
    | :? PathTooLongException as error -> Error(FileFailure("containment", path, error.Message))

// Rejects a link on the start and on every existing component beneath it, so a planted link cannot
// redirect an operation away from the tree the caller named.
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

// An external path is admitted only when its whole chain is link-free: containment is decided on
// text, so one link anywhere lets the physical target differ from the path that was validated.
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

let read (root: string) (path: string) (bound: int64) : Result<byte[], ReleaseError> =
    match rejectLinks root path with
    | Error error -> Error error
    | Ok () ->
        // Kind comes before the open, because opening a FIFO or device can block.
        match ResolverKind.classify path with
        | Error error -> Error error
        | Ok ResolverKind.Regular ->
            try
                let info = FileInfo path

                if info.Length > bound then
                    Error(FileFailure("read", path, $"the file is {info.Length} bytes which exceeds the {bound}-byte bound"))
                else
                    Ok(File.ReadAllBytes path)
            with
            | :? IOException as error -> Error(FileFailure("read", path, error.Message))
            | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))
        | Ok kind -> Error(FileFailure("read", path, $"the path is a {ResolverKind.name kind}, not a regular file"))

// A crashed attempt leaves at most one partial per destination, and only a plain file at that name
// is replaced: any other kind is content this resolver cannot account for.
let private discardPartial (partial: string) : Result<unit, ReleaseError> =
    if not (exists partial) then
        Ok()
    else
        match ResolverKind.classify partial with
        | Error error -> Error error
        | Ok ResolverKind.Regular ->
            try
                File.Delete partial
                Ok()
            with
            | :? IOException as error -> Error(FileFailure("write", partial, error.Message))
            | :? UnauthorizedAccessException as error -> Error(FileFailure("write", partial, error.Message))
        | Ok kind -> Error(FileFailure("write", partial, $"a leftover partial is a {ResolverKind.name kind}, which this resolver did not write"))

let write (root: string) (path: string) (bytes: byte[]) : Result<unit, ReleaseError> =
    let partial = path + ".partial"

    match rejectLinks root path, rejectLinks root partial with
    | Error error, _ -> Error error
    | _, Error error -> Error error
    | Ok (), Ok () ->
        match discardPartial partial with
        | Error error -> Error error
        | Ok () ->
            try
                let create () =
                    // CreateNew refuses to follow an existing entry of any kind.
                    use stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    stream.Write(bytes, 0, bytes.Length)
                    stream.Flush true

                create ()
                File.Move(partial, path, true)
                Ok()
            with
            | :? IOException as error -> Error(FileFailure("write", path, error.Message))
            | :? UnauthorizedAccessException as error -> Error(FileFailure("write", path, error.Message))

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

// Reading is capped by the remaining scan budget, so an entry that changed after classification
// cannot make the scan unbounded.
let private digest (path: string) (budget: int64) : string * int64 =
    use stream = File.OpenRead path
    use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
    let chunk = Array.zeroCreate<byte> 65536
    let mutable read = 0L
    let mutable finished = false

    while not finished do
        let count = stream.Read(chunk, 0, chunk.Length)

        if count = 0 then
            finished <- true
        else
            read <- read + int64 count

            if read > budget then
                abort $"the entry '{path}' exceeds the remaining {maxScanBytes}-byte scan budget"

            hash.AppendData(chunk, 0, count)

    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), read

// Relative slash paths and SHA-256 of every regular file in the tree; a directory is descended,
// and any other kind fails closed before it is opened.
let inventory (tree: string) : Result<(string * string) list, ReleaseError> =
    if linked tree then
        Error(FileFailure("scan", tree, "the tree is a link or reparse point"))
    else
        try
            if not (Directory.Exists tree) then
                Error(FileFailure("scan", tree, "the tree does not exist"))
            else
                let files = ResizeArray<string * string>()
                let mutable count = 0
                let mutable scanned = 0L

                let rec step (relative: string) (directory: string) =
                    for entry in Directory.GetFileSystemEntries directory do
                        count <- count + 1

                        if count > maxEntries then
                            abort $"the tree exceeds the {maxEntries}-entry bound"

                        let name = Path.GetFileName entry
                        let nested = if relative = "" then name else relative + "/" + name

                        match ResolverKind.classify entry with
                        | Error error -> abort (ReleaseError.message error)
                        | Ok ResolverKind.Directory -> step nested entry
                        | Ok ResolverKind.Regular ->
                            let info = FileInfo entry

                            if info.Length > maxScanBytes - scanned then
                                abort $"the tree exceeds the {maxScanBytes}-byte scan bound"

                            let digestValue, read = digest entry (maxScanBytes - scanned)
                            scanned <- scanned + read
                            files.Add(nested, digestValue)
                        | Ok kind -> abort $"the entry '{entry}' is a {ResolverKind.name kind}, not a regular file or directory"

                step "" tree

                Ok(files |> List.ofSeq |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right)))
        with
        | WalkAbort detail -> Error(FileFailure("scan", tree, detail))
        | :? IOException as error -> Error(FileFailure("scan", tree, error.Message))
        | :? UnauthorizedAccessException as error -> Error(FileFailure("scan", tree, error.Message))

// Deletes resolver-owned content only: anything that is not a plain file or directory aborts the
// delete, so no link or special entry is followed or removed.
let remove (root: string) (target: string) : Result<unit, ReleaseError> =
    match rejectLinks root target with
    | Error error -> Error error
    | Ok () ->
        try
            if not (exists target) then
                Ok()
            else
                match ResolverKind.classify target with
                | Error error -> Error error
                | Ok ResolverKind.Directory ->
                    let mutable count = 0

                    let rec clear (directory: string) =
                        for entry in Directory.GetFileSystemEntries directory do
                            count <- count + 1

                            if count > maxEntries then
                                abort $"the tree exceeds the {maxEntries}-entry bound"

                            match ResolverKind.classify entry with
                            | Error error -> abort (ReleaseError.message error)
                            | Ok ResolverKind.Directory ->
                                clear entry
                                Directory.Delete(entry, false)
                            | Ok ResolverKind.Regular -> File.Delete entry
                            | Ok kind -> abort $"the entry '{entry}' is a {ResolverKind.name kind}"

                    clear target
                    Directory.Delete(target, false)
                    Ok()
                | Ok ResolverKind.Regular ->
                    File.Delete target
                    Ok()
                | Ok kind -> Error(FileFailure("delete", target, $"the target is a {ResolverKind.name kind}, which this resolver did not create"))
        with
        | WalkAbort detail -> Error(FileFailure("delete", target, detail))
        | :? IOException as error -> Error(FileFailure("delete", target, error.Message))
        | :? UnauthorizedAccessException as error -> Error(FileFailure("delete", target, error.Message))
