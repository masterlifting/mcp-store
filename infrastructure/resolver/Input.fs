// role: reusable module/helper
// External inputs of the CLI: the trusted pin file and the launch-time values, none of which
// may live inside the immutable installation storage.
module ResolverInput

open System
open System.IO
open SharedFailure

let private pinBound = 64L * 1024L

let private rejected (name: string) (detail: string) = Error(LaunchInputRejected(name, detail))

let private invalid (path: string) (detail: string) = Error(FileFailure("pin", path, detail))

// Mutable state must not live in the immutable cache, so a supplied path is rejected when it
// names anything inside the storage root.
let private outside (root: string) (name: string) (path: string) : Result<unit, ReleaseError> =
    match ResolverDisk.under root path with
    | Ok _ -> rejected name $"'{path}' is inside the immutable storage root '{root}'"
    | Error _ -> Ok()

// A link anywhere in the chain would make the physical target differ from the path validated here,
// which is what keeps a link out of the immutable root and containment decided on text.
let private free (name: string) (path: string) : Result<unit, ReleaseError> =
    match ResolverDisk.chain path with
    | Ok () -> Ok()
    | Error error -> rejected name (ReleaseError.message error)

let file (root: string) (name: string) (path: string) : Result<unit, ReleaseError> =
    if not (ResolverPath.isAbsolute path) then
        rejected name $"'{path}' is not an absolute path"
    elif Directory.Exists path then
        rejected name $"'{path}' is a directory, not a file"
    elif not (File.Exists path) then
        rejected name $"the file '{path}' does not exist"
    else
        match free name path with
        | Error error -> Error error
        | Ok () ->
            // Kind is checked without opening, because opening a FIFO or device can block.
            match ResolverKind.classify path with
            | Error error -> rejected name (ReleaseError.message error)
            | Ok ResolverKind.Regular -> outside root name path
            | Ok kind -> rejected name $"'{path}' is a {ResolverKind.name kind}, not a regular file"

let directory (root: string) (name: string) (path: string) : Result<unit, ReleaseError> =
    if not (ResolverPath.isAbsolute path) then
        rejected name $"'{path}' is not an absolute path"
    elif File.Exists path then
        rejected name $"'{path}' is a file, not a directory"
    elif not (Directory.Exists path) then
        rejected name $"the directory '{path}' does not exist"
    else
        match free name path with
        | Error error -> Error error
        | Ok () ->
            match ResolverKind.classify path with
            | Error error -> rejected name (ReleaseError.message error)
            | Ok ResolverKind.Directory -> outside root name path
            | Ok kind -> rejected name $"'{path}' is a {ResolverKind.name kind}, not a directory"

// Manifest-declared inputs are read, never created, so only shape, kind, and location are
// decided here; a declared content hash of an input stays the product's own check.
let inputs
    (root: string)
    (descriptor: DistributionContract.LaunchDescriptor)
    (supplied: (string * string) list)
    : Result<unit, ReleaseError> =
    let mutable failure: ReleaseError option = None

    for name, value in supplied do
        if failure.IsNone then
            match descriptor.Arguments |> List.tryFind (fun argument -> argument.Input = name) with
            | Some argument ->
                let validation =
                    match argument.Type with
                    | DistributionContract.LaunchArgumentType.AbsoluteFile -> file root name value
                    | DistributionContract.LaunchArgumentType.AbsoluteDirectory -> directory root name value
                    | DistributionContract.LaunchArgumentType.Sha256 -> Ok()
                match validation with
                | Ok () -> ()
                | Error error -> failure <- Some error
            | None -> ()

    match failure with
    | Some error -> Error error
    | None -> Ok()

// Only readability and kind are decided here; the pin parser owns every identity and integrity rule.
let pin (path: string) : Result<string, ReleaseError> =
    if not (ResolverPath.isAbsolute path) then
        invalid path "the pin path is not absolute"
    elif Directory.Exists path then
        invalid path "the pin path is a directory"
    elif not (File.Exists path) then
        invalid path "the pin file does not exist"
    else
        match ResolverDisk.chain path with
        | Error error -> invalid path (ReleaseError.message error)
        | Ok () ->
            match ResolverKind.classify path with
            | Error error -> invalid path (ReleaseError.message error)
            | Ok ResolverKind.Regular ->
                try
                    let info = FileInfo path

                    if info.Length > pinBound then
                        invalid path $"the pin file is {info.Length} bytes which exceeds the {pinBound}-byte bound"
                    else
                        Ok(File.ReadAllText path)
                with
                | :? IOException as error -> invalid path error.Message
                | :? UnauthorizedAccessException as error -> invalid path error.Message
            | Ok kind -> invalid path $"the pin path is a {ResolverKind.name kind}, not a regular file"
