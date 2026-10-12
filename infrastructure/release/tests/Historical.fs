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

let private bootstrapDir =
    match Environment.GetEnvironmentVariable "MCP_RELEASE_HISTORICAL_ROOT" with
    | null -> ""
    | "" -> ""
    | value -> value

let private dotnetArchive103Path =
    Path.Combine(bootstrapDir, "dotnet-v1.0.3.zip")

let private workflowArchive103Path =
    Path.Combine(bootstrapDir, "workflow-v1.0.3.zip")
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
    historicalHashPinning ()
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
