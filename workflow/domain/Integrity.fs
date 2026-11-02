module WorkflowIntegrity

open System
open System.IO
open System.Security.Cryptography

// Keeping the key outside task state prevents model-controlled task operations from supplying it.
let private keyDirectory =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mcp-store",
        "workflow"
    )

let private keyPath = Path.Combine(keyDirectory, "integrity.key")

let private rejectReparsePath (path: string) =
    if (File.Exists path || Directory.Exists path)
       && (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint then
        raise (IOException "integrity key path must not traverse a reparse point")

let private validateExistingDirectory () =
    if String.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) then
        raise (IOException "per-account application data directory is unavailable")

    if not (Directory.Exists keyDirectory) then
        raise (DirectoryNotFoundException "workflow integrity key directory does not exist")

    rejectReparsePath keyDirectory
    rejectReparsePath keyPath

let private readExistingKey () =
    let attributes = File.GetAttributes keyPath

    if attributes.HasFlag FileAttributes.Directory || attributes.HasFlag FileAttributes.ReparsePoint then
        raise (IOException "integrity key must be a regular file")

    let bytes = File.ReadAllBytes keyPath

    if bytes.Length <> 32 then
        CryptographicOperations.ZeroMemory bytes
        raise (CryptographicException "integrity key has an invalid length")

    bytes

// CreateNew preserves established trust; malformed existing keys fail closed rather than rotate.
let loadOrCreateKey () =
    if String.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) then
        raise (IOException "per-account application data directory is unavailable")

    Directory.CreateDirectory keyDirectory |> ignore
    rejectReparsePath keyDirectory
    rejectReparsePath keyPath

    if File.Exists keyPath then
        readExistingKey ()
    else
        let generated = RandomNumberGenerator.GetBytes 32

        try
            use stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)
            stream.Write(generated, 0, generated.Length)
            stream.Flush(true)
            generated
        with
        | :? IOException when File.Exists keyPath ->
            CryptographicOperations.ZeroMemory generated
            readExistingKey ()
        | error ->
            CryptographicOperations.ZeroMemory generated
            raise error

// Verification cannot bootstrap trust from a missing key.
let loadExistingKey () =
    validateExistingDirectory ()

    if not (File.Exists keyPath) then
        raise (FileNotFoundException("workflow integrity key does not exist", keyPath))

    readExistingKey ()

let authenticate (key: byte array) (payload: byte array) =
    if isNull key || key.Length <> 32 then
        invalidArg (nameof key) "integrity key must be 32 bytes"

    if isNull payload then
        nullArg (nameof payload)

    use hmac = new HMACSHA256(key)
    hmac.ComputeHash payload

let verify (key: byte array) (payload: byte array) (tag: byte array) =
    if isNull tag || tag.Length <> 32 then
        false
    else
        let expected = authenticate key payload

        try
            CryptographicOperations.FixedTimeEquals(expected, tag)
        finally
            CryptographicOperations.ZeroMemory expected
