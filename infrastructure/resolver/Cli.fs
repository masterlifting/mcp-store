// role: reusable module/helper
// Strict argv parsing for the two subcommands; paths are declared absolute here so no
// repository- or harness-relative state can be inherited from the caller.
module ResolverCli

open System
open SharedFailure

type Target =
    { Pin: string
      Root: string option }

type Run =
    { Pin: string
      Root: string option
      Workspace: string
      Host: string
      Inputs: (string * string) list }

type Command =
    | Resolve of Target
    | Run of Run

let private absolutePath (name: string) (value: string) : Result<string, ReleaseError> =
    if ResolverPath.isAbsolute value then
        Ok value
    else
        Error(UsageRejected $"option '{name}' must be an absolute path, but '{value}' is not")

let private optionalPath (name: string) (value: string option) : Result<string option, ReleaseError> =
    match value with
    | None -> Ok None
    | Some text -> absolutePath name text |> Result.map Some

let parse (arguments: string list) : Result<Command, ReleaseError> =
    let mutable failure: ReleaseError option = None
    let mutable pin = None
    let mutable root = None
    let mutable workspace = None
    let mutable host = None
    let mutable inputs: (string * string) list = []

    let fail detail = if failure.IsNone then failure <- Some(UsageRejected detail)

    // Options are single-valued and a repeat is a mistake rather than a last-wins override.
    let store (name: string) (slot: string option) (raw: string) (assign: string -> unit) =
        if slot.IsSome then fail $"option '{name}' was supplied more than once" else assign raw

    // A leading '--' means a missing value, so a mistyped flag cannot be read as an operand.
    let rec loop (remaining: string list) =
        if failure.IsNone then
            match remaining with
            | [] -> ()
            | flag :: rest when flag = "--input" ->
                match rest with
                | raw :: tail when not (String.IsNullOrWhiteSpace raw) && not (raw.StartsWith("--", StringComparison.Ordinal)) ->
                    match raw.IndexOf '=' with
                    | -1 -> fail "option '--input' requires a name=value argument"
                    | index ->
                        let name = raw.Substring(0, index)
                        let value = raw.Substring(index + 1)

                        if String.IsNullOrWhiteSpace name || String.IsNullOrWhiteSpace value then
                            fail "option '--input' requires a name=value argument"
                        elif inputs |> List.exists (fun (declared, _) -> declared = name) then
                            fail $"the launch input '{name}' was supplied more than once"
                        else
                            inputs <- inputs @ [ name, value ]
                            loop tail
                | _ -> fail "option '--input' requires a name=value argument"
            | flag :: rest when flag = "--pin" || flag = "--root" || flag = "--workspace" || flag = "--host" ->
                match rest with
                | raw :: tail when not (String.IsNullOrWhiteSpace raw) && not (raw.StartsWith("--", StringComparison.Ordinal)) ->
                    match flag with
                    | "--pin" -> store "--pin" pin raw (fun value -> pin <- Some value)
                    | "--root" -> store "--root" root raw (fun value -> root <- Some value)
                    | "--workspace" -> store "--workspace" workspace raw (fun value -> workspace <- Some value)
                    | _ -> store "--host" host raw (fun value -> host <- Some value)

                    if failure.IsNone then loop tail
                | _ -> fail $"option '{flag}' requires a value"
            | flag :: _ -> fail $"unknown option '{flag}'"

    let build (subcommand: string) : Result<Command, ReleaseError> =
        match pin with
        | None -> Error(UsageRejected "option '--pin' is required")
        | Some pinPath ->
            match absolutePath "--pin" pinPath, optionalPath "--root" root with
            | Error error, _ -> Error error
            | _, Error error -> Error error
            | Ok pinPath, Ok rootPath ->
                if subcommand = "resolve" then
                    if workspace.IsSome || host.IsSome || not (List.isEmpty inputs) then
                        Error(UsageRejected "'resolve' accepts only '--pin' and '--root'")
                    else
                        Ok(Resolve { Pin = pinPath; Root = rootPath })
                else
                    match workspace, host with
                    | None, _ -> Error(UsageRejected "option '--workspace' is required")
                    | _, None -> Error(UsageRejected "option '--host' is required")
                    | Some workspacePath, Some hostPath ->
                        match absolutePath "--workspace" workspacePath, absolutePath "--host" hostPath with
                        | Error error, _ -> Error error
                        | _, Error error -> Error error
                        | Ok workspacePath, Ok hostPath ->
                            Ok(
                                Run
                                    { Pin = pinPath
                                      Root = rootPath
                                      Workspace = workspacePath
                                      Host = hostPath
                                      Inputs = inputs }
                            )

    match arguments with
    | [] -> Error(UsageRejected "no subcommand was supplied; expected 'resolve' or 'run'")
    | subcommand :: rest when subcommand = "resolve" || subcommand = "run" ->
        loop rest

        match failure with
        | Some error -> Error error
        | None -> build subcommand
    | subcommand :: _ -> Error(UsageRejected $"unknown subcommand '{subcommand}'; expected 'resolve' or 'run'")
