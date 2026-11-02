// role: test helper
// The expected receipt comes from an independent framing oracle, so a suite never confirms the producer's framing with the producer's own framing.
// The helper reads no file and no key, so an expected frame never depends on the environment that produced it.

module WorkflowReceiptOracle

open System
open System.IO
open System.Security.Cryptography
open System.Text

let prefix = "Mcp.Workflow.TaskIntegrity\000v1\000"

let platformName = if OperatingSystem.IsWindows() then "windows" else "unix"

// The canonical root is the authenticated identity of a project, so the documented normalization is applied once here and reused by every frame.
let canonicalProjectRoot (root: string) =
    let full = Path.GetFullPath root
    let filesystemRoot = Path.GetPathRoot full

    let normalized =
        if String.Equals(full, filesystemRoot, StringComparison.OrdinalIgnoreCase) then filesystemRoot
        else full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    if OperatingSystem.IsWindows() then
        if normalized.StartsWith("\\\\?\\", StringComparison.Ordinal)
           || normalized.StartsWith("\\\\.\\", StringComparison.Ordinal) then
            invalidArg (nameof root) "device-prefixed project roots are unsupported for authenticated task identity"

        normalized.ToUpperInvariant()
    else
        normalized

let private frameField (stream: MemoryStream) (value: byte array) =
    let length = uint32 value.Length
    stream.WriteByte(byte (length >>> 24))
    stream.WriteByte(byte (length >>> 16))
    stream.WriteByte(byte (length >>> 8))
    stream.WriteByte(byte length)
    stream.Write(value, 0, value.Length)

let expectedFrameBytes (platform: string) (canonicalRoot: string) (requestedId: string) (canonicalPayloadJson: string) =
    use stream = new MemoryStream()
    let prefixBytes = Encoding.UTF8.GetBytes prefix
    stream.Write(prefixBytes, 0, prefixBytes.Length)
    frameField stream (Encoding.UTF8.GetBytes platform)
    frameField stream (Encoding.UTF8.GetBytes canonicalRoot)
    frameField stream (Encoding.UTF8.GetBytes requestedId)
    frameField stream (Encoding.UTF8.GetBytes canonicalPayloadJson)
    stream.ToArray()

let expectedReceipt (key: byte array) (platform: string) (canonicalRoot: string) (requestedId: string) (canonicalPayloadJson: string) =
    if isNull key || key.Length <> 32 then
        invalidArg (nameof key) "integrity key must be 32 bytes"

    use hmac = new HMACSHA256(key)
    let tag = hmac.ComputeHash(expectedFrameBytes platform canonicalRoot requestedId canonicalPayloadJson)
    "hmac-sha256-v1:" + Convert.ToHexString(tag).ToLowerInvariant()

// The golden frames pin the byte layout of the frame itself, so a change to the prefix or to the length-prefixed field order stays visible even when the payload under test moves.
let goldenRequestedId = "TRU-GOLDEN"
let goldenPayloadJson = "{\"schemaVersion\":3}"
let goldenWindowsRoot = "C:\\GOLDEN-ROOT"
let goldenWindowsFrameHex = "4d63702e576f726b666c6f772e5461736b496e74656772697479007631000000000777696e646f77730000000e433a5c474f4c44454e2d524f4f540000000a5452552d474f4c44454e000000137b22736368656d6156657273696f6e223a337d"
let goldenUnixRoot = "/golden-root"
let goldenUnixFrameHex = "4d63702e576f726b666c6f772e5461736b496e746567726974790076310000000004756e69780000000c2f676f6c64656e2d726f6f740000000a5452552d474f4c44454e000000137b22736368656d6156657273696f6e223a337d"
