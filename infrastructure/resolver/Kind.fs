// role: reusable module/helper
// Entry kind without opening the entry: .NET reports every Unix special file as Normal, and an
// open of a FIFO or device can block forever, so kind comes from a stat-level classification.
module ResolverKind

open System
open System.IO
open System.Runtime.InteropServices
open SharedFailure

type Kind =
    | Regular
    | Directory
    | Link
    | Other

let name (kind: Kind) : string =
    match kind with
    | Regular -> "regular file"
    | Directory -> "directory"
    | Link -> "link or reparse point"
    | Other -> "special file"

// Windows decides kind from the file attributes, where a junction and a symlink both carry the
// reparse flag and a directory flag can be present at the same time.
let private windows (path: string) : Result<Kind, ReleaseError> =
    try
        let attributes = File.GetAttributes path

        if attributes.HasFlag FileAttributes.ReparsePoint then
            Ok Link
        elif attributes.HasFlag FileAttributes.Directory then
            Ok Directory
        else
            Ok Regular
    with
    | :? IOException as error -> Error(FileFailure("kind", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("kind", path, error.Message))

// Mode bits of the runtime's own stat shim; only the file-type mask and the four kinds the resolver
// distinguishes are read.
[<Literal>]
let private typeMask = 0xF000

[<Literal>]
let private regularMask = 0x8000

[<Literal>]
let private directoryMask = 0x4000

[<Literal>]
let private linkMask = 0xA000

// The field order is the runtime's own FileStatus; a mismatch would let the shim write past this
// struct, so the declaration is re-validated whenever the .NET runtime baseline changes.
[<StructLayout(LayoutKind.Sequential)>]
type private FileStatus =
    struct
        val Flags: int32
        val Mode: int32
        val Uid: uint32
        val Gid: uint32
        val Size: int64
        val ATime: int64
        val ATimeNsec: int64
        val MTime: int64
        val MTimeNsec: int64
        val CTime: int64
        val CTimeNsec: int64
        val BirthTime: int64
        val BirthTimeNsec: int64
        val Dev: int64
        val RDev: int64
        val Ino: int64
        val UserFlags: uint32
        val HardLinkCount: int32
    end

[<DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", CallingConvention = CallingConvention.Cdecl)>]
extern int private lstat ([<MarshalAs(UnmanagedType.LPUTF8Str)>] string path, FileStatus& output)

// A missing shim or a layout mismatch is a runtime defect, so it is reported as unclassifiable
// rather than as a safe non-regular entry.
let private unix (path: string) : Result<Kind, ReleaseError> =
    try
        let mutable status = Unchecked.defaultof<FileStatus>

        if lstat (path, &status) <> 0 then
            Error(FileFailure("kind", path, "the entry could not be classified by the platform stat call"))
        else
            let kind = status.Mode &&& typeMask

            if kind = regularMask then
                Ok Regular
            elif kind = directoryMask then
                Ok Directory
            elif kind = linkMask then
                Ok Link
            else
                Ok Other
    with
    | :? DllNotFoundException as error -> Error(FileFailure("kind", path, $"the runtime stat shim is unavailable: {error.Message}"))
    | :? EntryPointNotFoundException as error -> Error(FileFailure("kind", path, $"the runtime stat shim is unavailable: {error.Message}"))
    | :? TypeLoadException as error -> Error(FileFailure("kind", path, $"the runtime stat shim layout does not match: {error.Message}"))

let classify (path: string) : Result<Kind, ReleaseError> =
    if OperatingSystem.IsWindows() then windows path else unix path
