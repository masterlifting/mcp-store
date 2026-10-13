// role: reusable module/helper
module ReleasePolicy

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open BuildProvenance

type PolicyProjection =
    { ComponentId: string
      Version: string
      EntryDll: string
      SdkVersion: string
      RuntimeVersion: string
      Platform: string
      ArchiveName: string
      AssetUri: string
      PublishedFiles: string list
      SourcePath: string
      SourceSha256: string }

let private requiredFields =
    [ "componentId"
      "version"
      "entryDll"
      "sdkVersion"
      "runtimeVersion"
      "platform"
      "archiveName"
      "assetUri"
      "publishedFiles" ]

let private requiredFieldSet = Set.ofList requiredFields

let private maxSourceBytes = 16 * 1024 * 1024

let private strictUtf8 =
    UTF8Encoding(encoderShouldEmitUTF8Identifier = false, throwOnInvalidBytes = true)

exception private PolicyParse of detail: string
let private bad detail = raise (PolicyParse detail)

type private Cursor =
    { mutable Index: int
      Text: string
      Length: int }

let private peek (c: Cursor) : char option =
    if c.Index < c.Length then Some c.Text.[c.Index] else None

let private isIdentStart (ch: char) =
    (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch = '_'

let private isIdentChar (ch: char) =
    isIdentStart ch || (ch >= '0' && ch <= '9')

let private readIdent (c: Cursor) : string =
    if c.Index >= c.Length || not (isIdentStart c.Text.[c.Index]) then
        bad $"expected an identifier at offset {c.Index}"

    let startIdx = c.Index
    c.Index <- c.Index + 1

    while c.Index < c.Length && isIdentChar c.Text.[c.Index] do
        c.Index <- c.Index + 1

    c.Text.Substring(startIdx, c.Index - startIdx)

let private skipTrivia (c: Cursor) : unit =
    let mutable keepGoing = true

    while c.Index < c.Length && keepGoing do
        let ch = c.Text.[c.Index]

        if ch = ' ' || ch = '\t' || ch = '\r' || ch = '\n' then
            c.Index <- c.Index + 1
        elif ch = '/' && c.Index + 1 < c.Length && c.Text.[c.Index + 1] = '/' then
            c.Index <- c.Index + 2

            while c.Index < c.Length && c.Text.[c.Index] <> '\n' do
                c.Index <- c.Index + 1
        else
            keepGoing <- false

let private appendEscaped (sb: StringBuilder) (escape: char) : unit =
    match escape with
    | '"' -> sb.Append '"' |> ignore
    | '\\' -> sb.Append '\\' |> ignore
    | '/' -> sb.Append '/' |> ignore
    | 'n' -> sb.Append '\n' |> ignore
    | 'r' -> sb.Append '\r' |> ignore
    | 't' -> sb.Append '\t' |> ignore
    | 'b' -> sb.Append '\b' |> ignore
    | 'f' -> sb.Append '\012' |> ignore
    | _ -> bad $"unsupported escape sequence '\\{escape}' in string literal"

let private readStringLiteral (c: Cursor) : string =
    if c.Index >= c.Length || c.Text.[c.Index] <> '"' then
        bad $"expected '\"' at offset {c.Index}"

    c.Index <- c.Index + 1
    let sb = StringBuilder()

    let mutable continueLoop = true

    while c.Index < c.Length && continueLoop do
        let ch = c.Text.[c.Index]

        if ch = '"' then
            c.Index <- c.Index + 1
            continueLoop <- false
        elif ch = '\\' then
            if c.Index + 1 >= c.Length then bad "unterminated escape sequence in string literal"
            let escape = c.Text.[c.Index + 1]
            c.Index <- c.Index + 2
            appendEscaped sb escape
        else
            if ch = '\n' || ch = '\r' then bad "raw newline inside a string literal"
            sb.Append ch |> ignore
            c.Index <- c.Index + 1

    if continueLoop then
        bad "unterminated string literal"
    else
        sb.ToString()

let private readInterpolatedString (c: Cursor) (bindings: IDictionary<string, string>) : string =
    if c.Index + 1 >= c.Length || c.Text.[c.Index] <> '$' || c.Text.[c.Index + 1] <> '"' then
        bad $"expected '$\"' at offset {c.Index}"

    c.Index <- c.Index + 2
    let sb = StringBuilder()

    let mutable continueLoop = true

    while c.Index < c.Length && continueLoop do
        let ch = c.Text.[c.Index]

        if ch = '"' then
            c.Index <- c.Index + 1
            continueLoop <- false
        elif ch = '\\' then
            if c.Index + 1 >= c.Length then
                bad "unterminated escape sequence in interpolated string"

            let escape = c.Text.[c.Index + 1]
            c.Index <- c.Index + 2
            appendEscaped sb escape
        elif ch = '{' then
            if c.Index + 1 < c.Length && c.Text.[c.Index + 1] = '{' then
                sb.Append '{' |> ignore
                c.Index <- c.Index + 2
            else
                c.Index <- c.Index + 1
                let ident = readIdent c

                if ident.Length = 0 then
                    bad "empty interpolation reference"

                if c.Index >= c.Length || c.Text.[c.Index] <> '}' then
                    bad $"unclosed interpolation reference '{{{ident}'"

                c.Index <- c.Index + 1

                match bindings.TryGetValue ident with
                | true, value -> sb.Append value |> ignore
                | _ -> bad $"interpolation references undeclared binding '{ident}'"
        elif ch = '}' then
            if c.Index + 1 < c.Length && c.Text.[c.Index + 1] = '}' then
                sb.Append '}' |> ignore
                c.Index <- c.Index + 2
            else
                bad "unmatched '}' in interpolated string"
        else
            if ch = '\n' || ch = '\r' then
                bad "raw newline inside an interpolated string"

            sb.Append ch |> ignore
            c.Index <- c.Index + 1

    if continueLoop then
        bad "unterminated interpolated string"
    else
        sb.ToString()

let private readListElement (c: Cursor) (bindings: IDictionary<string, string>) : string =
    skipTrivia c

    if c.Index >= c.Length then
        bad "expected a list element before end of input"

    match peek c with
    | Some '$' -> readInterpolatedString c bindings
    | Some '"' -> readStringLiteral c
    | Some ch -> bad $"unsupported list element at offset {c.Index}: '{ch}'"
    | None -> bad "expected a list element before end of input"

let private readListLiteral (c: Cursor) (bindings: IDictionary<string, string>) : string =
    // c.Index points at '['.
    if c.Index >= c.Length || c.Text.[c.Index] <> '[' then
        bad $"expected '[' at offset {c.Index}"

    c.Index <- c.Index + 1
    let items = List<string>()

    let mutable continueLoop = true

    skipTrivia c

    if c.Index < c.Length && c.Text.[c.Index] = ']' then
        c.Index <- c.Index + 1
        continueLoop <- false
    else
        while continueLoop do
            let element = readListElement c bindings
            items.Add element |> ignore
            skipTrivia c

            if c.Index >= c.Length then
                continueLoop <- false
            elif c.Text.[c.Index] = ';' then
                c.Index <- c.Index + 1
                skipTrivia c
            elif c.Text.[c.Index] = ']' then
                c.Index <- c.Index + 1
                continueLoop <- false
            elif c.Text.[c.Index] = '"' || c.Text.[c.Index] = '$' then
                () // whitespace separates the next element; no ';' required
            else
                bad $"expected ';' or ']' inside list at offset {c.Index}"

    String.concat ";" items

let private readLetBinding (c: Cursor) (bindings: IDictionary<string, string>) : unit =
    // Caller must have just consumed the `let` keyword.
    let name = readIdent c

    if bindings.ContainsKey name then
        bad $"duplicate let binding '{name}' in canonical Config.fsx"

    if not (Set.contains name requiredFieldSet) then
        let requiredFieldsList = String.concat ", " requiredFields
        bad $"unsupported let binding '{name}' in canonical Config.fsx; the canonical schema is exactly [{requiredFieldsList}]"

    skipTrivia c

    if c.Index >= c.Length || c.Text.[c.Index] <> '=' then
        bad $"expected '=' after let binding name '{name}' at offset {c.Index}"

    c.Index <- c.Index + 1
    skipTrivia c

    if c.Index >= c.Length then
        bad $"expected a value for let binding '{name}'"

    let resolved : string =
        match peek c with
        | Some '"' -> readStringLiteral c
        | Some '$' -> readInterpolatedString c bindings
        | Some '[' -> readListLiteral c bindings
        | Some ch -> bad $"unsupported expression after let binding '{name}' at offset {c.Index}: '{ch}'"
        | None -> bad $"expected a value for let binding '{name}'"

    bindings.Add(name, resolved) |> ignore

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

let private parseAll (text: string) (sourcePath: string) : Dictionary<string, string> =
    let cursor = { Index = 0; Text = text; Length = text.Length }
    skipTrivia cursor

    if cursor.Index + 6 > cursor.Length || cursor.Text.Substring(cursor.Index, 6) <> "module" then
        bad "the canonical Config.fsx must start with `module <Ident>`"

    cursor.Index <- cursor.Index + 6
    skipTrivia cursor
    let moduleName = readIdent cursor

    if moduleName <> "ReleaseConfig" then
        bad $"unsupported module name '{moduleName}'; only `module ReleaseConfig` is parsed"

    skipTrivia cursor

    let bindings = Dictionary<string, string>(StringComparer.Ordinal)
    let mutable continueLoop = true

    while cursor.Index < cursor.Length && continueLoop do
        skipTrivia cursor

        if cursor.Index >= cursor.Length then
            continueLoop <- false
        else
            if cursor.Index + 3 > cursor.Length
               || cursor.Text.Substring(cursor.Index, 3) <> "let" then
                let preview = text.Substring(cursor.Index, min 16 (text.Length - cursor.Index))
                bad $"unsupported top-level syntax at offset {cursor.Index}: '{preview}'"

            cursor.Index <- cursor.Index + 3
            skipTrivia cursor
            readLetBinding cursor bindings

    let mutable missing : string list = []

    for field in requiredFields do
        if not (bindings.ContainsKey field) then
            missing <- field :: missing

    match missing with
    | _ :: _ ->
        let detail = String.concat ", " (List.rev missing)
        bad $"canonical Config.fsx is missing required field(s): {detail}"
    | [] -> bindings

let parseBytes (sourcePath: string) (boundedBytes: byte[]) : Result<PolicyProjection, ReleaseError> =
    if isNull boundedBytes then
        Error(MalformedArtifact(sourcePath, "the canonical Config.fsx bytes are null"))
    elif boundedBytes.Length > maxSourceBytes then
        Error(MalformedArtifact(sourcePath, $"the canonical Config.fsx exceeds the bounded maximum of {maxSourceBytes} bytes (got {boundedBytes.Length})"))
    else
        match boundedBytes |> Array.tryFindIndex (fun byte -> byte = 0uy) with
        | Some nulOffset ->
            Error(MalformedArtifact(sourcePath, $"the canonical Config.fsx contains an embedded NUL byte at offset {nulOffset}"))
        | None ->
            try
                let sha = sha256Hex boundedBytes
                let text = strictUtf8.GetString boundedBytes
                let bindings = parseAll text sourcePath

                Ok
                    { ComponentId = bindings.["componentId"]
                      Version = bindings.["version"]
                      EntryDll = bindings.["entryDll"]
                      SdkVersion = bindings.["sdkVersion"]
                      RuntimeVersion = bindings.["runtimeVersion"]
                      Platform = bindings.["platform"]
                      ArchiveName = bindings.["archiveName"]
                      AssetUri = bindings.["assetUri"]
                      PublishedFiles =
                        (bindings.["publishedFiles"]).Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.toList
                      SourcePath = sourcePath
                      SourceSha256 = sha }
            with
            | PolicyParse detail -> Error(MalformedArtifact(sourcePath, detail))
            | :? DecoderFallbackException as error ->
                Error(MalformedArtifact(sourcePath, $"the canonical Config.fsx is not valid UTF-8: {error.Message}"))

let parse (sourcePath: string) : Result<PolicyProjection, ReleaseError> =
    try
        let absolute = Path.GetFullPath sourcePath
        if not (File.Exists absolute) then
            Error(MissingArtifact absolute)
        else
            parseBytes absolute (File.ReadAllBytes absolute)
    with
    | :? IOException as error -> Error(FileFailure("read", sourcePath, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", sourcePath, error.Message))
    | PolicyParse detail -> Error(MalformedArtifact(sourcePath, detail))
