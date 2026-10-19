// role: reusable module/helper
// Paths use only the derived pin hash and validated archive name to prevent pin-based escape.
module ResolverStorage

open System
open System.IO
open SharedFailure

type Platform =
    | Windows
    | Linux
    | MacOS

type Storage =
    { Root: string
      Key: string
      Install: string
      Staging: string
      Retained: string
      Guard: string
      Lease: string }

let private applicationName = "mcp-store"

// Explicit separators make root selection depend on the declared platform, not the host.
let private join (platform: Platform) (parts: string list) : string =
    let separator = if platform = Platform.Windows then "\\" else "/"

    parts
    |> List.map (fun part -> part.TrimEnd('\\', '/'))
    |> String.concat separator

let private absolute (name: string) (value: string option) : Result<string, ReleaseError> =
    match value with
    | Some text when ResolverPath.isAbsolute text -> Ok text
    | _ -> Error(FileFailure("storage-root", name, "the environment does not provide an absolute directory"))

let rootDirectory
    (platform: Platform)
    (localAppData: string option)
    (xdgDataHome: string option)
    (homeDirectory: string option)
    : Result<string, ReleaseError> =
    let baseDirectory =
        match platform with
        | Platform.Windows -> absolute "LOCALAPPDATA" localAppData
        | Platform.Linux ->
            // XDG_DATA_HOME is honored only when absolute; otherwise the specification default applies.
            match absolute "XDG_DATA_HOME" xdgDataHome with
            | Ok value -> Ok value
            | Error _ ->
                match absolute "HOME" homeDirectory with
                | Ok home -> Ok(join platform [ home; ".local"; "share" ])
                | Error error -> Error error
        | Platform.MacOS ->
            match absolute "HOME" homeDirectory with
            | Ok home -> Ok(join platform [ home; "Library"; "Application Support" ])
            | Error error -> Error error

    baseDirectory |> Result.map (fun platformRoot -> join platform [ platformRoot; applicationName ])

// The default root follows the running host, unlike layouts composed from a declared platform.
let hostPlatform () : Platform =
    if OperatingSystem.IsWindows() then
        Platform.Windows
    elif OperatingSystem.IsMacOS() then
        Platform.MacOS
    else
        Platform.Linux

let private environment (name: string) : string option =
    let value = Environment.GetEnvironmentVariable name
    if String.IsNullOrWhiteSpace value then None else Some value

let canonicalRoot () : Result<string, ReleaseError> =
    let home =
        match environment "HOME" with
        | Some value -> Some value
        | None ->
            let profile = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
            if String.IsNullOrWhiteSpace profile then None else Some profile

    rootDirectory (hostPlatform ()) (environment "LOCALAPPDATA") (environment "XDG_DATA_HOME") home

let layout (root: string) (pin: DistributionPin.Pin) : Result<Storage, ReleaseError> =
    if not (ResolverPath.isAbsolute root) then
        Error(FileFailure("storage-root", root, "the canonical storage root is not an absolute directory path"))
    else
        match ResolverPath.portablePath pin.ArchiveName with
        | Error error -> Error error
        | Ok () ->
            let key = ResolverIdentity.pinKey pin

            Ok
                { Root = root
                  Key = key
                  Install = Path.Combine(root, "installs", key)
                  Staging = Path.Combine(root, "staging", key)
                  Retained = Path.Combine(root, "archives", key, pin.ArchiveName)
                  Guard = Path.Combine(root, "locks", key + ".guard")
                  Lease = Path.Combine(root, "locks", key + ".lease") }
