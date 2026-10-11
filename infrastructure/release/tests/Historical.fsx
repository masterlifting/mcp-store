// role: test entrypoint
// Historical v1.0.3 verification: archive digests are recomputed, not trusted from docs; archive directory is `MCP_RELEASE_HISTORICAL_ROOT`-configurable (no hardcoded developer temp path).
#load "../../Provenance.fsx"
#load "../Compare.fsx"
#load "Fixture.fsx"

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

let private bootstrapDir =
    match Environment.GetEnvironmentVariable "MCP_RELEASE_HISTORICAL_ROOT" with
    | null -> ""
    | "" -> ""
    | value -> value

let private dotnetArchive103Path =
    Path.Combine(bootstrapDir, "dotnet-v1.0.3.zip")

let private workflowArchive103Path =
    Path.Combine(bootstrapDir, "workflow-v1.0.3.zip")

// Re-hash the historical v1.0.3 archives on disk and assert they
//    match the digests in `docs/GHI-24/packages.md` §7.
let historicalHashPinning () =
    if File.Exists dotnetArchive103Path then
        let bytes = File.ReadAllBytes dotnetArchive103Path
        let sha = SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()
        assertEqual "dotnet-v1.0.3.zip SHA-256 matches packages.md" "da3acb20007f8015e541ee7d68b48adf49d232c4eea41190a62073073d1a1614" sha
        assertEqual "dotnet-v1.0.3.zip size matches packages.md (1,289,835 B)" 1289835 (int bytes.Length)
    else
        failed <- failed + 1
        failures.Add(sprintf "historical v1.0.3 archive missing: %s" dotnetArchive103Path)

    if File.Exists workflowArchive103Path then
        let bytes = File.ReadAllBytes workflowArchive103Path
        let sha = SHA256.HashData bytes |> Convert.ToHexString |> fun v -> v.ToLowerInvariant()
        assertEqual "workflow-v1.0.3.zip SHA-256 matches packages.md" "8677413d830bd7fbc559e1847222ae4d3ee8582de4d9ba15a224a939dcc39db1" sha
        assertEqual "workflow-v1.0.3.zip size matches packages.md (1,482,675 B)" 1482675 (int bytes.Length)
    else
        failed <- failed + 1
        failures.Add(sprintf "historical v1.0.3 archive missing: %s" workflowArchive103Path)

// Helper: observe an archive and return its ArchiveObservation.
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

// Same archive observed twice → ByteIdentical.
let historicalSameArchiveIdentical () = async {
    if not (File.Exists dotnetArchive103Path) then
        failed <- failed + 1
        failures.Add("historicalSameArchiveIdentical: prerequisite v1.0.3 archive missing")
    else
        let firstCopy = writeTempCopy dotnetArchive103Path "first"
        let secondCopy = writeTempCopy dotnetArchive103Path "second"
        let! obsA = observe firstCopy
        let! obsB = observe secondCopy
        let contract =
            { ComponentId = "dotnet"
              Version = "1.0.3"
              ArchiveName = "v1.0.3.zip"
              EntryAllowlist =
                [ "FSharp.Core.dll"
                  "Mcp.Dotnet.deps.json"
                  "Mcp.Dotnet.dll"
                  "Mcp.Dotnet.runtimeconfig.json"
                  "NOTICE.txt"
                  "distribution.json" ] }
        // The historical archive's revision field is something like
        // `fc0aefa9db0fb3262ee6ca1254bd6bbb752b8b01`; for an
        // identical-archive test the caller passes that same revision.
        let manifestRevision = "fc0aefa9db0fb3262ee6ca1254bd6bbb752b8b01"
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

// Bumped candidate against historical v1.0.3: the canonical
//    v1.0.4-only files (`Mcp.Dotnet.dll` 256,000 B vs 225,792 B; the
//    F# compiled output changed between v1.0.3 and v1.0.4) →
//    DifferentBeyondRevision. The test pairs the historical v1.0.3
//    archive against a rebuilt v1.0.4 manifest body embedded inside a
//    new archive that contains a v1.0.3-compatible entry table for
//    every entry EXCEPT the binary `Mcp.Dotnet.dll` (which differs).
//    A real fixture reuses the historical archive's allowed list but
//    replaces `Mcp.Dotnet.dll` with the post-refactor bytes.

let private writeModifiedV104LikeArchive (sourcePath: string) (label: string) =
    let tempPath = Path.Combine(Path.GetTempPath(), "mcp-historical-" + label + "-" + Guid.NewGuid().ToString("N") + ".zip")

    // First pass: read every entry from the source archive into memory
    // so the source ZIP file handle can be closed before we open the
    // destination for writing.
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

    // Build a manifest with the bumped revision; declared `files` hashes
    // mirror the post-bump archive so the production self-consistency gate
    // accepts the modified archive.
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
        if name = "distribution.json" then () // written below as the canonical manifest
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
    if not (File.Exists dotnetArchive103Path) then
        failed <- failed + 1
        failures.Add("historicalBumpedCandidateDifferent: prerequisite v1.0.3 archive missing")
    else
        let candidatePath = writeModifiedV104LikeArchive dotnetArchive103Path "candidate"
        try
            let! publishedObs = observe dotnetArchive103Path
            let! candidateObs = observe candidatePath
            let contract =
                { ComponentId = "dotnet"
                  Version = "1.0.3"
                  ArchiveName = "v1.0.3.zip"
                  EntryAllowlist =
                    [ "FSharp.Core.dll"
                      "Mcp.Dotnet.deps.json"
                      "Mcp.Dotnet.dll"
                      "Mcp.Dotnet.runtimeconfig.json"
                      "NOTICE.txt"
                      "distribution.json" ] }
            // The candidate manifest's `revision` is the bumped one; we
            // pass it to `compare`. The historical archive's
            // `revision` field will differ; the comparison must
            // surface the diff (DifferentBeyondRevision) because the
            // Mcp.Dotnet.dll bytes differ.
            let manifestRevision = "3612e8e6dd4655543e6d4b9dc0edee9c24b82bf1"
            match! compare contract publishedObs candidateObs manifestRevision with
            | Ok (DifferentBeyondRevision (_, _, reason)) ->
                assertTrue (sprintf "v1.0.4-bumped candidate rejected (reason=%s)" reason) true
            | Ok other ->
                failed <- failed + 1
                failures.Add(sprintf "historical bumped candidate: expected DifferentBeyondRevision got %A" other)
            | Error error ->
                // The strict comparison rejects on the manifest `version`
                // field (v1.0.3 contract vs v1.0.4 candidate) or on the
                // different Mcp.Dotnet.dll payload bytes. Either failure
                // shape is a correct rejection of the bumped candidate.
                let m = ReleaseError.message error
                assertTrue (sprintf "historical bumped candidate: %s" m)
                    (m.Contains("version", StringComparison.Ordinal)
                     || m.Contains("differs", StringComparison.Ordinal)
                     || m.Contains("identity", StringComparison.Ordinal)
                     || m.Contains("Mcp.Dotnet", StringComparison.Ordinal))
        finally
            try File.Delete candidatePath with _ -> ()
}

// Consecutive distinct HEAD fixtures: a pair of archives whose only
//    difference is the embedded `revision` field (simulating a
//    docs-only or Telegram-only commit that does not change package
//    bytes). The comparison must classify both as
//    `RevisionOnlyEquivalent` so the workflow's no-op branch fires.
let consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent () = async {
    let tempRoot = Path.Combine(Path.GetTempPath(), "mcp-consecutive-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tempRoot |> ignore
    let writeWithManifestRevision (label: string) (revision: string) =
        let archivePath = Path.Combine(tempRoot, label + ".zip")
        let manifestPath = Path.Combine(tempRoot, label + ".json")
        // Reuse Fixture so the declared `files` SHA-256s match the actual
        // decompressed payloads and the F4 self-consistency gate accepts the
        // archive before it reaches the comparator.
        Fixture.writeRevisionVariantArchive archivePath manifestPath revision
        archivePath
    try
        // Two archives whose embedded `revision` differs but every
        // other byte (and the corresponding file payload SHA) is
        // identical. This simulates a docs/Telegram-only commit that
        // changes nothing in the package bytes.
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
            // For each pair, the "candidate" is the OTHER archive. The
            // `evaluatedRevision` argument is the candidate's HEAD.
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
    historicalHashPinning ()
    do! historicalSameArchiveIdentical ()
    do! historicalBumpedCandidateDifferent ()
    do! consecutiveDistinctHeadFixturesClassifiedRevisionOnlyEquivalent ()

    printfn "historical-bootstrap-tests summary: passed=%d failed=%d" passed failed
    if failed > 0 then
        for line in failures do
            eprintfn "FAIL: %s" line
        exit 1
}

// Standalone entry bridge: the optional historical FSI process owns this suite.
runSuite () |> Async.RunSynchronously
