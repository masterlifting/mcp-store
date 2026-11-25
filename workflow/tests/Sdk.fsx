// role: test helper
// The expected .NET SDK is the single authored owner in the repository-root `global.json`; every suite that asserts the
// selected SDK reads it here instead of carrying its own literal and parser.
// The read is strict about the one value it returns: a missing, malformed, repeated or non-string `sdk.version` fails the
// suite at load, so a drifted owner is never reported as a passing check. The full owner shape (exactly `sdk`, and
// exactly `version`, `rollForward` and `allowPrerelease`) belongs to the release reader in
// `infrastructure/release/Toolchain.fs` and its PowerShell mirror, and is not restated here.

module Sdk

open System
open System.Collections.Generic
open System.IO
open System.Text.Json

let repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private ownerError (path: string) (detail: string) =
    raise (InvalidDataException(sprintf "the canonical SDK owner '%s' is not usable: %s" path detail))

let private uniqueMembers (path: string) (element: JsonElement) : unit =
    let names = HashSet<string>(StringComparer.Ordinal)

    for property in element.EnumerateObject() do
        if not (names.Add property.Name) then
            ownerError path (sprintf "the key '%s' is repeated" property.Name)

let private findMember (element: JsonElement) (name: string) : JsonElement option =
    match element.TryGetProperty name with
    | true, value -> Some value
    | _ -> None

// `sdk.version` from the repository-root `global.json`, which is what every SDK selection derives from.
let requiredSdk =
    let path = Path.Combine(repositoryRoot, "global.json")

    if not (File.Exists path) then
        ownerError path "the file does not exist"

    let options =
        JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 4)

    use document = JsonDocument.Parse(File.ReadAllText path, options)
    let root = document.RootElement

    if root.ValueKind <> JsonValueKind.Object then
        ownerError path "the root must be an object"

    uniqueMembers path root

    match findMember root "sdk" with
    | None -> ownerError path "the 'sdk' object is missing"
    | Some sdk when sdk.ValueKind <> JsonValueKind.Object -> ownerError path "the 'sdk' member must be an object"
    | Some sdk ->
        uniqueMembers path sdk

        match findMember sdk "version" with
        | None -> ownerError path "the 'sdk.version' value is missing"
        | Some version when version.ValueKind <> JsonValueKind.String -> ownerError path "the 'sdk.version' value must be a string"
        | Some version ->
            let value = version.GetString()

            if String.IsNullOrWhiteSpace value then
                ownerError path "the 'sdk.version' value is empty"

            value
