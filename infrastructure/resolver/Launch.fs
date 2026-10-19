// role: reusable module/helper
// Typed launch composition: manifest arguments become an argument list, never a shell
// string, and supplied values stay launch-time rather than entering the immutable install.
module ResolverLaunch

open System
open SharedFailure

let absoluteFile (input: string) (value: string) : Result<string, ReleaseError> =
    if ResolverPath.isAbsolute value then
        Ok value
    else
        Error(LaunchInputRejected(input, "the value is not an absolute file path"))

// Existence and content are verified at launch; this is the typed shape check.
let absoluteDirectory (input: string) (value: string) : Result<string, ReleaseError> =
    if ResolverPath.isAbsolute value then
        Ok value
    else
        Error(LaunchInputRejected(input, "the value is not an absolute directory path"))

let digest (input: string) (value: string) : Result<string, ReleaseError> =
    if DistributionContract.isSha256 value then
        Ok value
    else
        Error(LaunchInputRejected(input, "the value is not a lowercase 64-character SHA-256"))

let private validate
    (argument: DistributionContract.LaunchArgument)
    (value: string)
    : Result<string, ReleaseError> =
    match argument.Type with
    | DistributionContract.LaunchArgumentType.AbsoluteFile -> absoluteFile argument.Input value
    | DistributionContract.LaunchArgumentType.AbsoluteDirectory -> absoluteDirectory argument.Input value
    | DistributionContract.LaunchArgumentType.Sha256 -> digest argument.Input value

// Values are matched by declared input name in declared order, so an undeclared, repeated,
// or missing input is rejected instead of reaching the child as a partial argument list.
let compose
    (descriptor: DistributionContract.LaunchDescriptor)
    (supplied: (string * string) list)
    : Result<string list, ReleaseError> =
    let declared = descriptor.Arguments |> List.map (fun argument -> argument.Input)
    let values = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
    let mutable failure: ReleaseError option = None

    for name, value in supplied do
        if failure.IsNone then
            if declared |> List.forall (fun declaredName -> declaredName <> name) then
                failure <- Some(LaunchInputRejected(name, "the value is not a launch input declared by the manifest"))
            elif values.ContainsKey name then
                failure <- Some(LaunchInputRejected(name, "the value was supplied more than once"))
            else
                values.Add(name, value)

    match failure with
    | Some error -> Error error
    | None ->
        let arguments = ResizeArray<string>()

        for argument in descriptor.Arguments do
            if failure.IsNone then
                match values.TryGetValue argument.Input with
                | true, value ->
                    match validate argument value with
                    | Ok validated ->
                        arguments.Add argument.Flag
                        arguments.Add validated
                    | Error error -> failure <- Some error
                | _ ->
                    failure <- Some(LaunchInputRejected(argument.Input, "no value was supplied for this launch input"))

        match failure with
        | Some error -> Error error
        | None -> Ok(List.ofSeq arguments)

let entrypoint
    (installDirectory: string)
    (manifest: DistributionContract.DistributionManifest)
    : Result<string, ReleaseError> =
    DistributionContract.resolveUnderRoot installDirectory manifest.Launch.Entrypoint
