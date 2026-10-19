// role: reusable module/helper
// Mutable launch inputs must stay outside the immutable installation storage.
module ResolverInput

open System
open System.IO
open SharedFailure

let private pinBound = 64L * 1024L

let private rejected (name: string) (detail: string) = Error(LaunchInputRejected(name, detail))

let private invalid (path: string) (detail: string) = Error(FileFailure("pin", path, detail))

let private outside (root: string) (name: string) (path: string) : Result<unit, ReleaseError> =
    match ResolverDisk.under root path with
    | Ok _ -> rejected name $"'{path}' is inside the immutable storage root '{root}'"
    | Error _ -> Ok()

// Containment is textual, so links could redirect a validated path.
let private free (name: string) (path: string) : Result<unit, ReleaseError> =
    match ResolverDisk.chain path with
    | Ok () -> Ok()
    | Error error -> rejected name (ReleaseError.message error)

let file (root: string) (name: string) (path: string) : Async<Result<unit, ReleaseError>> = async {
    if not (ResolverPath.isAbsolute path) then
        return rejected name $"'{path}' is not an absolute path"
    else
        match outside root name path with
        | Error error -> return Error error
        | Ok () ->
            match free name path with
            | Error error -> return Error error
            | Ok () ->
                if Directory.Exists path then
                    return rejected name $"'{path}' is a directory, not a file"
                elif not (File.Exists path) then
                    return rejected name $"the file '{path}' does not exist"
                else
                    // Kind is checked without opening, because opening a FIFO or device can block.
                    match! ResolverKind.classify path with
                    | Error error -> return rejected name (ReleaseError.message error)
                    | Ok ResolverKind.Regular -> return Ok()
                    | Ok kind -> return rejected name $"'{path}' is a {ResolverKind.name kind}, not a regular file"
}

let directory (root: string) (name: string) (path: string) : Async<Result<unit, ReleaseError>> = async {
    if not (ResolverPath.isAbsolute path) then
        return rejected name $"'{path}' is not an absolute path"
    else
        match outside root name path with
        | Error error -> return Error error
        | Ok () ->
            match free name path with
            | Error error -> return Error error
            | Ok () ->
                if File.Exists path then
                    return rejected name $"'{path}' is a file, not a directory"
                elif not (Directory.Exists path) then
                    return rejected name $"the directory '{path}' does not exist"
                else
                    match! ResolverKind.classify path with
                    | Error error -> return rejected name (ReleaseError.message error)
                    | Ok ResolverKind.Directory -> return Ok()
                    | Ok kind -> return rejected name $"'{path}' is a {ResolverKind.name kind}, not a directory"
}

let inputs
    (root: string)
    (descriptor: DistributionContract.LaunchDescriptor)
    (supplied: (string * string) list)
    : Async<Result<unit, ReleaseError>> =
    async {
        let rec validate remaining = async {
            match remaining with
            | [] -> return Ok()
            | (name, value) :: rest ->
                match descriptor.Arguments |> List.tryFind (fun argument -> argument.Input = name) with
                | None -> return! validate rest
                | Some argument ->
                    let! result =
                        match argument.Type with
                        | DistributionContract.LaunchArgumentType.AbsoluteFile -> file root name value
                        | DistributionContract.LaunchArgumentType.AbsoluteDirectory -> directory root name value
                        | DistributionContract.LaunchArgumentType.Sha256 -> async { return Ok() }
                    match result with
                    | Error error -> return Error error
                    | Ok () -> return! validate rest
        }
        return! validate supplied
    }

let pin (path: string) : Async<Result<string, ReleaseError>> = async {
    if not (ResolverPath.isAbsolute path) then
        return invalid path "the pin path is not absolute"
    else
        match ResolverDisk.chain path with
        | Error error -> return invalid path (ReleaseError.message error)
        | Ok () ->
            if Directory.Exists path then
                return invalid path "the pin path is a directory"
            elif not (File.Exists path) then
                return invalid path "the pin file does not exist"
            else
                match! ResolverKind.classify path with
                | Error error -> return invalid path (ReleaseError.message error)
                | Ok ResolverKind.Regular ->
                    try
                        let info = FileInfo path

                        if info.Length > pinBound then
                            return invalid path $"the pin file is {info.Length} bytes which exceeds the {pinBound}-byte bound"
                        else
                            return Ok(File.ReadAllText path)
                    with
                    | :? IOException as error -> return invalid path error.Message
                    | :? UnauthorizedAccessException as error -> return invalid path error.Message
                | Ok kind -> return invalid path $"the pin path is a {ResolverKind.name kind}, not a regular file"
}
