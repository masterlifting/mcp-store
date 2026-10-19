// role: reusable module/helper
// Portable filesystem admission: members must be extractable on Windows and unambiguous
// case-insensitively, so unsafe or colliding names fail before any write.
module ResolverPath

open System
open System.Collections.Generic
open System.IO
open SharedFailure

let maxSegmentLength = 255
let maxRelativeLength = 1024

let isAbsolute (value: string) : bool =
    if String.IsNullOrWhiteSpace value then
        false
    else
        try
            Path.IsPathFullyQualified value
        with :? ArgumentException ->
            false

// Windows resolves a device name even with an extension, so a member that merely looks
// like a path segment can still name a device outside the install directory.
let private deviceNames =
    let numbered prefix = [ for index in 0 .. 9 -> $"{prefix}{index}" ]

    HashSet<string>(
        [ "CON"; "PRN"; "AUX"; "NUL" ] @ numbered "COM" @ numbered "LPT",
        StringComparer.OrdinalIgnoreCase
    )

let private invalidCharacters = [| '"'; '*'; '<'; '>'; '?'; '|' |]

let private segmentProblem (segment: string) : string option =
    if segment.Length > maxSegmentLength then
        Some $"the segment exceeds the {maxSegmentLength}-character bound"
    elif segment.EndsWith(".", StringComparison.Ordinal) then
        Some "the segment ends with a trailing dot"
    elif segment.EndsWith(" ", StringComparison.Ordinal) then
        Some "the segment ends with a trailing space"
    elif segment |> Seq.exists (fun character -> Array.contains character invalidCharacters) then
        Some "the segment contains a character that is invalid on Windows"
    else
        let dot = segment.IndexOf '.'

        let stem = if dot < 0 then segment else segment.Substring(0, dot)

        if deviceNames.Contains stem then
            Some $"the segment name '{stem}' is a reserved Windows device name"
        else
            None

let portablePath (relativePath: string) : Result<unit, ReleaseError> =
    let malformed detail = Error(MalformedArtifact(relativePath, detail))

    if not (DistributionContract.isSafeRelativePath relativePath) then
        malformed "the member is not a safe slash-separated relative path"
    elif relativePath.Length > maxRelativeLength then
        malformed $"the member path exceeds the {maxRelativeLength}-character bound"
    else
        match relativePath.Split '/' |> Array.tryPick segmentProblem with
        | Some detail -> malformed detail
        | None -> Ok()

// No member may also be the directory of another member; the check is case-insensitive
// because two such names collide on a case-folding host.
let portableSet (paths: string list) : Result<unit, ReleaseError> =
    let members = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let directories = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let rec collect (remaining: string list) =
        match remaining with
        | [] -> Ok()
        | path :: rest ->
            match portablePath path with
            | Error error -> Error error
            | Ok () ->
                let segments = path.Split '/'

                for index in 0 .. segments.Length - 2 do
                    directories.Add(String.concat "/" segments.[0..index]) |> ignore

                if not (members.Add path) then
                    Error(MalformedArtifact(path, "the member repeats another member case-insensitively"))
                else
                    collect rest

    match collect paths with
    | Error error -> Error error
    | Ok () ->
        match members |> Seq.tryFind (fun memberName -> directories.Contains memberName) with
        | Some conflict ->
            Error(MalformedArtifact(conflict, "the member is both a file and a directory of another member"))
        | None -> Ok()
