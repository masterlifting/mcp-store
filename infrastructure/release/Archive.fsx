// role: reusable module/helper
// Raw ZIP32 reader for the deterministic producer archives.
// It validates the exact local/central layout and exposes every byte a strict comparison must preserve.
// Only the canonical Deflate32 shape the producer emits is accepted; any other layout fails closed.
module ReleaseArchive

#load "../Provenance.fsx"

open System
open System.IO
open System.IO.Compression
open BuildProvenance

// Untrusted-input caps applied before any allocation or decompression.
let maxArchiveBytes = 256L * 1024L * 1024L
let maxEntryCount = 4096
let maxEntryUncompressedBytes = 256L * 1024L * 1024L
let maxTotalUncompressedBytes = 256L * 1024L * 1024L
let maxCompressionRatio = 200L

let private localHeaderSignature = 0x04034B50u
let private centralHeaderSignature = 0x02014B50u
let private endOfCentralDirectorySignature = 0x06054B50u
let private zip64LocatorSignature = 0x07064B50u

// The producer emits Deflate with version 20, zero flags, and no extras, comments, or data descriptors.
let private deflateMethod = 8us
let private canonicalVersion = 20us

// Every raw field a strict comparison may need; `LocalHeaderOffset` is derived layout, the rest are stored bytes.
type Entry =
    { Name: string
      NameBytes: byte[]
      LocalHeaderOffset: int64
      VersionMadeBy: uint16
      VersionNeeded: uint16
      Flags: uint16
      Method: uint16
      ModTime: uint16
      ModDate: uint16
      Crc32: uint32
      CompressedSize: int64
      UncompressedSize: int64
      LocalExtra: byte[]
      CentralExtra: byte[]
      Comment: byte[]
      DiskStart: uint16
      InternalAttributes: uint16
      ExternalAttributes: uint32
      CompressedData: byte[] }

type Archive =
    { Path: string
      Length: int64
      CentralDirectoryOffset: int64
      CentralDirectorySize: int64
      Entries: Entry list }

// Aborts a parse of untrusted bytes; `parse` maps it to a typed MalformedArtifact at that boundary.
exception private ArchiveFormat of string

let private bad (detail: string) : 'T = raise (ArchiveFormat detail)

let private crcTable =
    let table = Array.zeroCreate<uint32> 256

    for index in 0 .. 255 do
        let mutable value = uint32 index

        for _ in 1 .. 8 do
            value <- if (value &&& 1u) = 1u then 0xEDB88320u ^^^ (value >>> 1) else value >>> 1

        table[index] <- value

    table

let crc32 (bytes: byte[]) : uint32 =
    let mutable value = 0xFFFFFFFFu

    for byte in bytes do
        value <- crcTable[int ((value ^^^ uint32 byte) &&& 0xFFu)] ^^^ (value >>> 8)

    value ^^^ 0xFFFFFFFFu

let private parseBytes (path: string) (bytes: byte[]) : Archive =
    let length = int64 bytes.Length

    if length > maxArchiveBytes then
        bad $"archive is {length} bytes which exceeds the {maxArchiveBytes}-byte bound"

    if length < 22L then
        bad "archive is too short to contain an end-of-central-directory record"

    let u16 (offset: int64) =
        if offset < 0L || offset + 2L > length then
            bad "truncated header field"

        BitConverter.ToUInt16(bytes, int offset)

    let u32 (offset: int64) =
        if offset < 0L || offset + 4L > length then
            bad "truncated header field"

        BitConverter.ToUInt32(bytes, int offset)

    let slice (offset: int64) (count: int64) =
        if offset < 0L || count < 0L || offset + count > length then
            bad "truncated header field"

        if count = 0L then [||] else bytes[int offset .. int (offset + count) - 1]

    // The EOCD must be the final record; a trailing byte is rejected rather than tolerated.
    let mutable eocd = -1L
    let mutable probe = length - 22L
    let searchFloor = max 0L (length - 22L - 65535L)

    while eocd < 0L && probe >= searchFloor do
        if u32 probe = endOfCentralDirectorySignature then eocd <- probe else probe <- probe - 1L

    if eocd < 0L then
        bad "the end-of-central-directory record is missing"

    if u16 (eocd + 4L) <> 0us then
        bad "multi-disk archives are unsupported"

    if u16 (eocd + 6L) <> 0us then
        bad "multi-disk archives are unsupported"

    let recordsOnDisk = u16 (eocd + 8L)
    let totalRecords = u16 (eocd + 10L)
    let centralSize = u32 (eocd + 12L)
    let centralOffset = u32 (eocd + 16L)
    let commentLength = u16 (eocd + 20L)

    if commentLength <> 0us then
        bad "archive comments are unsupported"

    if eocd + 22L <> length then
        bad "trailing bytes follow the end-of-central-directory record"

    if recordsOnDisk <> totalRecords then
        bad "multi-disk archive record counts disagree"

    if recordsOnDisk = 0xFFFFus || centralSize = 0xFFFFFFFFu || centralOffset = 0xFFFFFFFFu then
        bad "ZIP64 archives are unsupported"

    if eocd >= 20L && u32 (eocd - 20L) = zip64LocatorSignature then
        bad "ZIP64 archives are unsupported"

    if int64 centralOffset + int64 centralSize <> eocd then
        bad "the central directory does not abut the end-of-central-directory record"

    let count = int totalRecords

    if count > maxEntryCount then
        bad $"{count} entries exceed the {maxEntryCount}-entry bound"

    let names = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
    let central = ResizeArray<Entry>()
    let mutable cursor = int64 centralOffset
    let mutable totalUncompressed = 0L

    for _ in 1 .. count do
        if u32 cursor <> centralHeaderSignature then
            bad "a central directory header has an unexpected signature"

        let versionMadeBy = u16 (cursor + 4L)
        let versionNeeded = u16 (cursor + 6L)
        let flags = u16 (cursor + 8L)
        let method = u16 (cursor + 10L)
        let modTime = u16 (cursor + 12L)
        let modDate = u16 (cursor + 14L)
        let crc = u32 (cursor + 16L)
        let compressedSize = u32 (cursor + 20L)
        let uncompressedSize = u32 (cursor + 24L)
        let nameLength = u16 (cursor + 28L)
        let extraLength = u16 (cursor + 30L)
        let entryCommentLength = u16 (cursor + 32L)
        let diskStart = u16 (cursor + 34L)
        let internalAttributes = u16 (cursor + 36L)
        let externalAttributes = u32 (cursor + 38L)
        let localOffset = u32 (cursor + 42L)
        let nameOffset = cursor + 46L
        let nameBytes = slice nameOffset (int64 nameLength)
        let centralExtra = slice (nameOffset + int64 nameLength) (int64 extraLength)
        let comment = slice (nameOffset + int64 nameLength + int64 extraLength) (int64 entryCommentLength)
        cursor <- nameOffset + int64 nameLength + int64 extraLength + int64 entryCommentLength

        if flags <> 0us then
            bad "entry flags are not the canonical value; data descriptors, encryption, and UTF-8 name flags are unsupported"

        if method <> deflateMethod then
            bad "only the canonical Deflate method is supported"

        if versionNeeded <> canonicalVersion || versionMadeBy <> canonicalVersion then
            bad "entry version metadata is not the canonical value"

        if extraLength <> 0us || entryCommentLength <> 0us then
            bad "entry extra fields and comments are unsupported"

        if diskStart <> 0us then
            bad "multi-disk entries are unsupported"

        if internalAttributes <> 0us || externalAttributes <> 0u then
            bad "entry attributes are not the canonical value"

        if nameBytes.Length = 0 then
            bad "an entry has an empty name"

        if nameBytes |> Array.exists (fun byte -> byte < 0x20uy || byte >= 0x80uy) then
            bad "non-ASCII entry names are unsupported"

        let name = Text.Encoding.ASCII.GetString nameBytes

        if not (names.Add name) then
            bad $"duplicate archive entry '{name}'"

        if int64 uncompressedSize > maxEntryUncompressedBytes then
            bad $"entry '{name}' declares more than the {maxEntryUncompressedBytes}-byte uncompressed bound"

        if compressedSize = 0u then
            if uncompressedSize > 0u then
                bad $"entry '{name}' declares an uncompressed payload with no compressed bytes"
        elif int64 uncompressedSize > int64 compressedSize * maxCompressionRatio then
            bad $"entry '{name}' exceeds the {maxCompressionRatio}:1 compression-ratio bound"

        totalUncompressed <- totalUncompressed + int64 uncompressedSize

        if totalUncompressed > maxTotalUncompressedBytes then
            bad $"entries declare more than the {maxTotalUncompressedBytes}-byte total uncompressed bound"

        central.Add(
            { Name = name
              NameBytes = nameBytes
              LocalHeaderOffset = int64 localOffset
              VersionMadeBy = versionMadeBy
              VersionNeeded = versionNeeded
              Flags = flags
              Method = method
              ModTime = modTime
              ModDate = modDate
              Crc32 = crc
              CompressedSize = int64 compressedSize
              UncompressedSize = int64 uncompressedSize
              LocalExtra = [||]
              CentralExtra = centralExtra
              Comment = comment
              DiskStart = diskStart
              InternalAttributes = internalAttributes
              ExternalAttributes = externalAttributes
              CompressedData = [||] }
        )

    if cursor <> eocd then
        bad "the central directory records do not fill the declared central directory size"

    // Local headers must run contiguously from byte zero and agree with their central copies.
    let verified = ResizeArray<Entry>()
    let mutable expectedOffset = 0L

    for entry in central do
        if entry.LocalHeaderOffset <> expectedOffset then
            bad $"entry '{entry.Name}' is not contiguous with the previous entry"

        let localOffset = entry.LocalHeaderOffset

        if u32 localOffset <> localHeaderSignature then
            bad $"entry '{entry.Name}' local header has an unexpected signature"

        let localVersionNeeded = u16 (localOffset + 4L)
        let localFlags = u16 (localOffset + 6L)
        let localMethod = u16 (localOffset + 8L)
        let localModTime = u16 (localOffset + 10L)
        let localModDate = u16 (localOffset + 12L)
        let localCrc = u32 (localOffset + 14L)
        let localCompressed = u32 (localOffset + 18L)
        let localUncompressed = u32 (localOffset + 22L)
        let localNameLength = u16 (localOffset + 26L)
        let localExtraLength = u16 (localOffset + 28L)

        if localExtraLength <> 0us then
            bad $"entry '{entry.Name}' has an unsupported local extra field"

        if localVersionNeeded <> entry.VersionNeeded
           || localFlags <> entry.Flags
           || localMethod <> entry.Method
           || localModTime <> entry.ModTime
           || localModDate <> entry.ModDate
           || localCrc <> entry.Crc32
           || localCompressed <> uint32 entry.CompressedSize
           || localUncompressed <> uint32 entry.UncompressedSize
           || int64 localNameLength <> int64 entry.NameBytes.Length then
            bad $"entry '{entry.Name}' local and central headers disagree"

        let localNameBytes = slice (localOffset + 30L) (int64 localNameLength)

        if localNameBytes <> entry.NameBytes then
            bad $"entry '{entry.Name}' local and central names disagree"

        let dataOffset = localOffset + 30L + int64 localNameLength
        let dataEnd = dataOffset + entry.CompressedSize

        if dataEnd > eocd then
            bad $"entry '{entry.Name}' payload overlaps the central directory"

        let data = slice dataOffset entry.CompressedSize
        verified.Add({ entry with CompressedData = data })
        expectedOffset <- dataEnd

    if expectedOffset <> int64 centralOffset then
        bad "gaps or overlaps exist between entry payloads and the central directory"

    { Path = path
      Length = length
      CentralDirectoryOffset = int64 centralOffset
      CentralDirectorySize = int64 centralSize
      Entries = List.ofSeq verified }

let parse (path: string) (bytes: byte[]) : Result<Archive, ReleaseError> =
    try
        Ok(parseBytes path bytes)
    with
    | ArchiveFormat detail -> Error(MalformedArtifact(path, detail))
    | :? InvalidDataException as error -> Error(MalformedArtifact(path, error.Message))

// Decompresses one entry under its declared size, then verifies size and CRC so a lying header cannot expand past the bound.
let decompress (entry: Entry) : Result<byte[], ReleaseError> =
    if entry.Method <> deflateMethod then
        Error(MalformedArtifact(entry.Name, "only the canonical Deflate method is supported"))
    elif entry.UncompressedSize > maxEntryUncompressedBytes then
        Error(MalformedArtifact(entry.Name, "the declared uncompressed size exceeds the bounded maximum"))
    else
        try
            use source = new MemoryStream(entry.CompressedData, false)
            use deflate = new DeflateStream(source, CompressionMode.Decompress)
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 81920
            let mutable total = 0L
            let mutable exceeded = false
            let mutable finished = false

            while not finished && not exceeded do
                let read = deflate.Read(buffer, 0, buffer.Length)

                if read = 0 then
                    finished <- true
                else
                    total <- total + int64 read

                    if total > entry.UncompressedSize then
                        exceeded <- true
                    else
                        output.Write(buffer, 0, read)

            if exceeded then
                Error(MalformedArtifact(entry.Name, "the compressed payload expands beyond its declared uncompressed size"))
            else
                let payload = output.ToArray()

                if int64 payload.Length <> entry.UncompressedSize then
                    Error(MalformedArtifact(entry.Name, "the decompressed size does not match the declared size"))
                elif crc32 payload <> entry.Crc32 then
                    Error(MalformedArtifact(entry.Name, "the decompressed payload CRC-32 does not match the declared CRC-32"))
                else
                    Ok payload
        with
        | :? InvalidDataException as error ->
            Error(MalformedArtifact(entry.Name, $"the compressed payload is not valid Deflate data: {error.Message}"))
