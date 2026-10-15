// WHY: published-baseline regression gate reads one descriptor so the path/SHA/size pin table
//   is single-sourced, and exercises the same-archive, changed, and revision-only comparison
//   scenarios over the dotnet fixture without an obsolete workflow v1.0.3 network prerequisite.

module Release.Tests.Historical

open Expecto
open Release.Tests

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance
open ReleaseCompare

let mutable passed = 0
let mutable failed = 0
let failures = ResizeArray<string>()

let assertTrue name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(name)

let assertEqual name expected actual =
    if expected = actual then passed <- passed + 1
    else
        failed <- failed + 1
        failures.Add(sprintf "%s: expected %A got %A" name expected actual)

// WHY: descriptor reads REPO-authored Fixtures.json so HistoricalRoot never carries a self-authored override.
let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let private repoDescriptorPath = Path.Combine(repoRoot, "infrastructure", "release", "tests", "Fixtures.json")

let private fixtureRoot =
    match Environment.GetEnvironmentVariable "MCP_RELEASE_HISTORICAL_ROOT" with
    | null -> ""
    | "" -> ""
    | value -> value

type FixtureEntry =
    { ComponentId: string
      ArchiveFile: string
      AssetName: string
      AssetUri: string
      Sha256: string
      Size: int64
      VerifiedManifestRevision: string }

let private descriptorSchemaVersion = 1
let private expectedEntryCount = 2

// WHY: archiveFile must stay a contained basename so Path.Combine with the fixture root cannot
//   resolve outside the historical root via '..' segments or absolute paths.
let private isContainedBasename (name: string) =
    not (String.IsNullOrEmpty name)
    && not (Path.IsPathRooted name)
    && name = Path.GetFileName name
    && not (name.Contains "..")

let private isLowerHex (value: string) (length: int) =
    value.Length = length
    && value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

let private fixturePath (entry: FixtureEntry) =
    Path.Combine(fixtureRoot, entry.ArchiveFile)

let private readFileSha256 (path: string) =
    use stream = File.OpenRead path
    use sha = SHA256.Create()
    sha.ComputeHash stream |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()

let private tryString (element: JsonElement) (name: string) : string option =
    let mutable found = None
    for property in element.EnumerateObject() do
        if property.Name = name && found.IsNone then
            match property.Value.ValueKind with
            | JsonValueKind.String ->
                let value = property.Value.GetString()
                if not (isNull value) then found <- Some value
            | _ -> ()
    found

let private tryInt (element: JsonElement) (name: string) : int64 option =
    let mutable found = None
    for property in element.EnumerateObject() do
        if property.Name = name && found.IsNone then
            match property.Value.ValueKind with
            | JsonValueKind.Number -> found <- Some (property.Value.GetInt64())
            | _ -> ()
    found

let private tryRootProperty (root: JsonElement) (name: string) : JsonElement option =
    let mutable found = None
    for property in root.EnumerateObject() do
        if property.Name = name && found.IsNone then
            found <- Some property.Value
    found

let private loadDescriptor () : Result<FixtureEntry list, string> =
    if not (File.Exists repoDescriptorPath) then
        Error(sprintf "descriptor missing: %s" repoDescriptorPath)
    else
        try
            use stream = File.OpenRead repoDescriptorPath
            let doc = JsonDocument.Parse stream
            let root = doc.RootElement
            if root.ValueKind <> JsonValueKind.Object then
                Error "descriptor root must be an object"
            else
                let schemaOk =
                    match tryRootProperty root "schemaVersion" with
                    | Some v when v.ValueKind = JsonValueKind.Number && v.GetInt32() = descriptorSchemaVersion -> true
                    | _ -> false
                if not schemaOk then
                    Error (sprintf "descriptor schemaVersion must equal %d" descriptorSchemaVersion)
                else
                    let fixturesOpt = tryRootProperty root "fixtures"
                    match fixturesOpt with
                    | None -> Error "descriptor must contain a 'fixtures' array"
                    | Some fixtures when fixtures.ValueKind = JsonValueKind.Array ->
                        if fixtures.GetArrayLength() <> expectedEntryCount then
                            Error(sprintf "descriptor must contain exactly %d entries; observed %d" expectedEntryCount (fixtures.GetArrayLength()))
                        else
                            let mutable errors : string list = []
                            let mutable entries : FixtureEntry list = []
                            let mutable ix = 0
                            for element in fixtures.EnumerateArray() do
                                let prefix = sprintf "fixtures[%d]" ix
                                let compId = tryString element "componentId"
                                let archiveFile = tryString element "archiveFile"
                                let assetName = tryString element "assetName"
                                let assetUri = tryString element "assetUri"
                                let sha = tryString element "sha256"
                                let size = tryInt element "size"
                                let rev = tryString element "verifiedManifestRevision"
                                match compId, archiveFile, assetName, assetUri, sha, size, rev with
                                | Some c, Some a, Some n, Some u, Some h, Some s, Some r ->
                                    if not (isContainedBasename a) then
                                        errors <- (sprintf "%s: archiveFile %A must be a contained basename (no path, no '..')" prefix a) :: errors
                                    if not (isLowerHex h 64) then
                                        errors <- (sprintf "%s: sha256 %A must be 64 lowercase hex chars" prefix h) :: errors
                                    if s <= 0L then
                                        errors <- (sprintf "%s: size must be a positive integer" prefix) :: errors
                                    if not (u.Contains(sprintf "/%s/" c, StringComparison.OrdinalIgnoreCase)) then
                                        errors <- (sprintf "%s: assetUri %A must contain the fixed component tag /%s/" prefix u c) :: errors
                                    if not (u.EndsWith(sprintf "/%s" n, StringComparison.Ordinal)) then
                                        errors <- (sprintf "%s: assetUri %A must end with /%s" prefix u n) :: errors
                                    if not (isLowerHex r 40) then
                                        errors <- (sprintf "%s: verifiedManifestRevision %A must be 40 lowercase hex chars" prefix r) :: errors
                                    entries <- { ComponentId = c
                                                 ArchiveFile = a
                                                 AssetName = n
                                                 AssetUri = u
                                                 Sha256 = h
                                                 Size = s
                                                 VerifiedManifestRevision = r } :: entries
                                | _ ->
                                    errors <- (sprintf "%s: missing required field(s) componentId/archiveFile/assetName/assetUri/sha256/size/verifiedManifestRevision" prefix) :: errors
                                ix <- ix + 1
                            if not (List.isEmpty errors) then
                                Error(String.concat "; " (List.rev errors))
                            else
                                let result = List.rev entries
                                let duplicateKeys f entries =
                                    entries
                                    |> List.groupBy f
                                    |> List.filter (fun (_, g) -> List.length g > 1)
                                    |> List.map (fun (k, _) -> string k)
                                let compIdDups = duplicateKeys (fun e -> e.ComponentId.ToLowerInvariant()) result
                                let shaDups = duplicateKeys (fun e -> e.Sha256) result
                                let allDups = compIdDups @ shaDups
                                if not (List.isEmpty allDups) then
                                    Error(sprintf "duplicate descriptor values: %s" (String.concat ", " allDups))
                                else
                                    Ok result
                    | Some _ -> Error "descriptor 'fixtures' must be an array"
        with ex ->
            Error(sprintf "descriptor parse error: %s" ex.Message)

let private findEntry (entries: FixtureEntry list) (componentId: string) =
    entries |> List.tryFind (fun e -> e.ComponentId.Equals(componentId, StringComparison.OrdinalIgnoreCase))

let historicalDescriptorSchema () =
    match loadDescriptor () with
    | Error e ->
        failed <- failed + 1
        failures.Add(sprintf "historicalDescriptorSchema: %s" e)
    | Ok entries ->
        assertTrue "descriptor parses without error" true
        for entry in entries do
            let path = fixturePath entry
            if not (File.Exists path) then
                failed <- failed + 1
                failures.Add(sprintf "historicalDescriptorSchema: archive missing for %s at %s" entry.ComponentId path)
            else
                let actualSize = (FileInfo path).Length
                assertEqual (sprintf "%s archive size matches descriptor" entry.ComponentId) entry.Size actualSize
                let actualSha = readFileSha256 path
                assertEqual (sprintf "%s archive SHA-256 matches descriptor" entry.ComponentId) entry.Sha256 actualSha
                assertTrue (sprintf "%s archiveFile is a contained basename" entry.ComponentId) (isContainedBasename entry.ArchiveFile)
                assertTrue (sprintf "%s assetUri embeds the fixed component tag" entry.ComponentId) (entry.AssetUri.Contains(sprintf "/%s/" entry.ComponentId, StringComparison.OrdinalIgnoreCase))
                assertTrue (sprintf "%s assetUri ends with the asset name" entry.ComponentId) (entry.AssetUri.EndsWith(sprintf "/%s" entry.AssetName, StringComparison.Ordinal))
                assertTrue (sprintf "%s sha256 is lowercase hex" entry.ComponentId) (isLowerHex entry.Sha256 64)
                assertTrue (sprintf "%s verifiedManifestRevision is lowercase hex" entry.ComponentId) (isLowerHex entry.VerifiedManifestRevision 40)

let private observe path =
    async {
        match! observe path with
        | Ok v -> return v
        | Error e -> return failwithf "observe %s: %s" path (ReleaseError.message e)
    }

let private writeTempCopy (sourcePath: string) (label: string) =
    let tempPath = Path.Combine(Path.GetTempPath(), "mcp-historical-" + label + "-" + Guid.NewGuid().ToString("N") + ".zip")
    File.Copy(sourcePath, tempPath)
    tempPath
let historicalSameArchiveIdentical () = async {
    match loadDescriptor () with
    | Error e ->
        failed <- failed + 1
        failures.Add(sprintf "historicalSameArchiveIdentical: %s" e)
    | Ok entries ->
        match findEntry entries "dotnet" with
        | None ->
            failed <- failed + 1
            failures.Add("historicalSameArchiveIdentical: no dotnet entry in descriptor")
        | Some dotnetEntry ->
            let dotnetPath = fixturePath dotnetEntry
            if not (File.Exists dotnetPath) then
                failed <- failed + 1
                failures.Add("historicalSameArchiveIdentical: prerequisite dotnet archive missing")
            else
                let firstCopy = writeTempCopy dotnetPath "first"
                let secondCopy = writeTempCopy dotnetPath "second"
                let! obsA = observe firstCopy
                let! obsB = observe secondCopy
                let contract =
                    { ComponentId = "dotnet"
                      Version = "1.0.3"
                      ArchiveName = dotnetEntry.AssetName
                      EntryAllowlist =
                        [ "FSharp.Core.dll"
                          "Mcp.Dotnet.deps.json"
                          "Mcp.Dotnet.dll"
                          "Mcp.Dotnet.runtimeconfig.json"
                          "NOTICE.txt"
                          "distribution.json" ] }
                let manifestRevision = dotnetEntry.VerifiedManifestRevision
                match! compare contract obsA obsB manifestRevision with
                | Ok (ByteIdentical published) ->
                    assertEqual "byte-identical carries the published archive sha" obsA.ArchiveSha256 published.ArchiveSha256
                | Ok other ->
                    failed <- failed + 1
                    failures.Add(sprintf "historical same-archive: expected ByteIdentical got %A" other)
                | Error error ->
                    failed <- failed + 1
                    failures.Add(sprintf "historical same-archive: %s" (ReleaseError.message error))
}

let private writeModifiedV104LikeArchive (sourcePath: string) (label: string) =
    let tempPath = Path.Combine(Path.GetTempPath(), "mcp-historical-" + label + "-" + Guid.NewGuid().ToString("N") + ".zip")
    let snapshots = ResizeArray<string * byte[]>()
    let mutable manifestText = ""
    do
        use src = ZipFile.OpenRead(sourcePath)
        for entry in src.Entries do
            let name = entry.FullName.Replace('\\', '/')
            let buf = new MemoryStream()
            entry.Open().CopyTo(buf)
            let bytes = buf.ToArray()
            snapshots.Add(name, bytes)
            if name = "distribution.json" then
                manifestText <- Encoding.UTF8.GetString(bytes)
    let bogus = Encoding.UTF8.GetBytes "v1.0.4-built-dll-fixture-payload"
    let bumpedRevision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
    let payloadHashes =
        snapshots
        |> Seq.toList
        |> List.filter (fun (n, _) -> n <> "distribution.json" && n <> "NOTICE.txt")
        |> List.map (fun (name, bytes) ->
            if name = "Mcp.Dotnet.dll" then name, Fixture.sha256Hex bogus
            else name, Fixture.sha256Hex bytes)
    let manifestTextNew = Fixture.buildManifestText bumpedRevision Fixture.canonicalSdk payloadHashes
    let manifestBytesNew = Encoding.UTF8.GetBytes manifestTextNew

    use dst = ZipFile.Open(tempPath, ZipArchiveMode.Create)
    for (name, bytes) in snapshots do
        if name = "distribution.json" then ()
        else
            let entry = dst.CreateEntry(name, CompressionLevel.Optimal)
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use outStream = entry.Open()
            let payload = if name = "Mcp.Dotnet.dll" then bogus else bytes
            outStream.Write(payload, 0, payload.Length)
    let manifestEntry = dst.CreateEntry("distribution.json", CompressionLevel.Optimal)
    manifestEntry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
    use outStream = manifestEntry.Open()
    outStream.Write(manifestBytesNew, 0, manifestBytesNew.Length)
    tempPath

let historicalBumpedCandidateDifferent () = async {
    match loadDescriptor () with
    | Error e ->
        failed <- failed + 1
        failures.Add(sprintf "historicalBumpedCandidateDifferent: %s" e)
    | Ok entries ->
        match findEntry entries "dotnet" with
        | None ->
            failed <- failed + 1
            failures.Add("historicalBumpedCandidateDifferent: no dotnet entry in descriptor")
        | Some dotnetEntry ->
            let dotnetPath = fixturePath dotnetEntry
            if not (File.Exists dotnetPath) then
                failed <- failed + 1
                failures.Add("historicalBumpedCandidateDifferent: prerequisite dotnet archive missing")
            else
                let candidatePath = writeModifiedV104LikeArchive dotnetPath "candidate"
                try
                    let! publishedObs = observe dotnetPath
                    let! candidateObs = observe candidatePath
                    let contract =
                        { ComponentId = "dotnet"
                          Version = "1.0.3"
                          ArchiveName = dotnetEntry.AssetName
                          EntryAllowlist =
                            [ "FSharp.Core.dll"
                              "Mcp.Dotnet.deps.json"
                              "Mcp.Dotnet.dll"
                              "Mcp.Dotnet.runtimeconfig.json"
                              "NOTICE.txt"
                              "distribution.json" ] }
                    let manifestRevision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
                    match! compare contract publishedObs candidateObs manifestRevision with
                    | Ok (DifferentBeyondRevision (_, _, reason)) ->
                        assertTrue (sprintf "v1.0.4-bumped candidate rejected (reason=%s)" reason) true
                    | Ok other ->
                        failed <- failed + 1
                        failures.Add(sprintf "historical bumped candidate: expected DifferentBeyondRevision got %A" other)
                    | Error error ->
                        let m = ReleaseError.message error
                        assertTrue (sprintf "historical bumped candidate: %s" m)
                            (m.Contains("version", StringComparison.Ordinal)
                             || m.Contains("differs", StringComparison.Ordinal)
                             || m.Contains("identity", StringComparison.Ordinal)
                             || m.Contains("Mcp.Dotnet", StringComparison.Ordinal))
                finally
                    try File.Delete candidatePath with _ -> ()
}
let consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent () = async {
    let tempRoot = Path.Combine(Path.GetTempPath(), "mcp-consecutive-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tempRoot |> ignore
    let writeWithManifestRevision (label: string) (revision: string) =
        let archivePath = Path.Combine(tempRoot, label + ".zip")
        let manifestPath = Path.Combine(tempRoot, label + ".json")
        Fixture.writeRevisionVariantArchive archivePath manifestPath revision
        archivePath
    try
        let headA = "aaaa" + String('a', 36)
        let headB = "bbbb" + String('b', 36)
        let archiveA = writeWithManifestRevision "a" headA
        let archiveB = writeWithManifestRevision "b" headB
        let! obsA = observe archiveA
        let! obsB = observe archiveB
        let contract =
            { ComponentId = "dotnet"
              Version = "1.0.4"
              ArchiveName = "v1.0.4.zip"
              EntryAllowlist =
                [ "FSharp.Core.dll"
                  "Mcp.Dotnet.deps.json"
                  "Mcp.Dotnet.dll"
                  "Mcp.Dotnet.runtimeconfig.json"
                  "NOTICE.txt"
                  "distribution.json" ] }
        let pair = ref 0
        for (label, pubObs, pubRev) in [ ("A->B", obsA, headA); ("B->A", obsB, headB) ] do
            let candObs, candRev = if label = "A->B" then obsB, headB else obsA, headA
            match! compare contract pubObs candObs candRev with
            | Ok (RevisionOnlyEquivalent (_, publishedRev, evaluatedRev)) ->
                assertEqual (sprintf "%s publishedRev equals pub archive revision" label) pubRev publishedRev
                assertEqual (sprintf "%s evaluatedRev equals candidate HEAD" label) candRev evaluatedRev
            | Ok other ->
                failed <- failed + 1
                failures.Add(sprintf "%s consecutive distinct HEAD fixture: expected RevisionOnlyEquivalent got %A" label other)
            | Error error ->
                failed <- failed + 1
                failures.Add(sprintf "%s consecutive distinct HEAD fixture: %s" label (ReleaseError.message error))
    finally
        try Directory.Delete(tempRoot, true) with _ -> ()

}

let runSuite () = async {
    historicalDescriptorSchema ()
    do! historicalSameArchiveIdentical ()
    do! historicalBumpedCandidateDifferent ()
    do! consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent ()

    printfn "historical-bootstrap-tests summary: passed=%d failed=%d" passed failed
}


let tests =
    testCaseAsync "Historical legacy regression assertions" <| async {
        do! runSuite ()
        Expect.equal 0 failed (String.concat Environment.NewLine failures)
    }
