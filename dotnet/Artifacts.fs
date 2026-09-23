namespace Mcp.Dotnet

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading

type RunHandle = { RunId: RunId; Paths: ArtifactPaths }

type private RegistryEntry =
    { Handle: RunHandle
      Completed: RetainedEvidence option
      CreatedAt: DateTimeOffset
      CompletedAt: DateTimeOffset option }

type ArtifactRegistry(artifactRoot: string, retention: TimeSpan, ?quotas: ArtifactQuotas) =
    let entries = ConcurrentDictionary<string, RegistryEntry>(StringComparer.Ordinal)
    let gate = obj ()
    let effectiveQuotas = quotas |> Option.defaultValue Budgets.DefaultArtifactQuotas
    let artifactRoot = Path.GetFullPath artifactRoot
    let pathComparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    let pathIsWithin (root: string) (candidate: string) =
        let prefix =
            if root.EndsWith(string Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || root.EndsWith(string Path.AltDirectorySeparatorChar, StringComparison.Ordinal) then
                root
            else
                root + string Path.DirectorySeparatorChar

        candidate.Equals(root, pathComparison) || candidate.StartsWith(prefix, pathComparison)

    let isReparse (path: string) =
        try
            (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> false
        | _ -> true

    let fileLength path =
        if File.Exists path then FileInfo(path).Length else 0L

    let rec sumFiles directory =
        if isReparse directory then
            raise (IOException "artifact root contains a reparse point")

        let files =
            Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            |> Seq.sumBy (fun path ->
                if isReparse path then
                    raise (IOException "artifact root contains a reparse point")

                fileLength path)

        let directories =
            Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
            |> Seq.sumBy sumFiles

        files + directories

    let quotaFailure (handle: RunHandle) =
        try
            let stdoutBytes = fileLength handle.Paths.Stdout
            let stderrBytes = fileLength handle.Paths.Stderr
            let binlogBytes = fileLength handle.Paths.Binlog
            let trxBytes = fileLength handle.Paths.Trx
            let runBytes = sumFiles handle.Paths.Directory
            let aggregateBytes = sumFiles artifactRoot

            if stdoutBytes > effectiveQuotas.MaxStdoutBytes then Some(ArtifactQuotaExceeded "stdout artifact quota exceeded")
            elif stderrBytes > effectiveQuotas.MaxStderrBytes then Some(ArtifactQuotaExceeded "stderr artifact quota exceeded")
            elif binlogBytes > effectiveQuotas.MaxBinlogBytes then Some(ArtifactQuotaExceeded "build binlog quota exceeded")
            elif trxBytes > effectiveQuotas.MaxTrxBytes then Some(ArtifactQuotaExceeded "TRX artifact quota exceeded")
            elif runBytes > effectiveQuotas.MaxRunBytes then Some(ArtifactQuotaExceeded "per-run artifact quota exceeded")
            elif aggregateBytes > effectiveQuotas.MaxAggregateBytes then Some(ArtifactQuotaExceeded "aggregate artifact quota exceeded")
            else None
        with error ->
            Some(ArtifactFailure $"artifact quota could not be measured: {error.Message}")

    let randomToken byteCount =
        let bytes = Array.zeroCreate<byte> byteCount
        RandomNumberGenerator.Fill bytes
        Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=')

    let createPaths directory =
        { Directory = directory
          Metadata = Path.Combine(directory, "metadata.json")
          Stdout = Path.Combine(directory, "stdout.log")
          Stderr = Path.Combine(directory, "stderr.log")
          Binlog = Path.Combine(directory, "build.binlog")
          Trx = Path.Combine(directory, "test-results.trx")
          ParsedEvidence = Path.Combine(directory, "parsed-evidence.json") }

    let removeExpired now =
        for pair in entries do
            let entry = pair.Value

            match entry.CompletedAt with
            | Some completedAt when now - completedAt >= retention ->
                let mutable removed = Unchecked.defaultof<RegistryEntry>

                if entries.TryRemove(pair.Key, &removed) then
                    try Directory.Delete(removed.Handle.Paths.Directory, true) with _ -> ()
            | _ -> ()

    do
        if retention <= TimeSpan.Zero then invalidArg (nameof retention) "retention must be positive"

        match Budgets.validateArtifactQuotas effectiveQuotas with
        | Ok _ -> ()
        | Error error -> invalidArg (nameof quotas) (VerificationError.message error)

        Directory.CreateDirectory(artifactRoot) |> ignore

    member _.Start(operation) : Result<RunHandle, VerificationError> =
        lock gate (fun () ->
            removeExpired DateTimeOffset.UtcNow
            try
                if sumFiles artifactRoot >= effectiveQuotas.MaxAggregateBytes then
                    Error(ArtifactQuotaExceeded "aggregate artifact quota is already exhausted")
                else
                    let mutable created = None
                    let mutable attempt = 0

                    while created.IsNone && attempt < 20 do
                        attempt <- attempt + 1
                        let runIdText = randomToken 32
                        let directoryName = randomToken 18
                        let directory = Path.GetFullPath(Path.Combine(artifactRoot, directoryName))

                        try
                            if not (pathIsWithin artifactRoot directory) then
                                ()
                            else
                                Directory.CreateDirectory directory |> ignore

                                if not (isReparse directory) then
                                    let runId = RunId.create runIdText
                                    let handle = { RunId = runId; Paths = createPaths directory }
                                    let entry =
                                        { Handle = handle
                                          Completed = None
                                          CreatedAt = DateTimeOffset.UtcNow
                                          CompletedAt = None }

                                    if entries.TryAdd(runIdText, entry) then
                                        created <- Some handle
                                    else
                                        Directory.Delete(directory, true)
                                else
                                    Directory.Delete(directory, true)
                        with _ -> ()

                    match created with
                    | Some handle -> Ok handle
                    | None -> Error(ArtifactFailure "could not allocate an isolated verification artifact directory")
            with error -> Error(ArtifactFailure $"artifact quota could not be measured: {error.Message}"))

    member _.Complete(handle: RunHandle, evidence: RetainedEvidence) : Result<unit, VerificationError> =
        lock gate (fun () ->
            let key = RunId.value handle.RunId

            match entries.TryGetValue key with
            | false, _ -> Error(UnknownRunId "verification run is no longer available")
            | true, entry when entry.Completed.IsSome -> Error(ArtifactFailure "verification run has already completed")
            | true, entry when
                not (String.Equals(evidence.Process.StdoutPath, entry.Handle.Paths.Stdout, StringComparison.Ordinal))
                || not (String.Equals(evidence.Process.StderrPath, entry.Handle.Paths.Stderr, StringComparison.Ordinal))
                || not (String.Equals(evidence.MetadataPath, entry.Handle.Paths.Metadata, StringComparison.Ordinal))
                || not (String.Equals(evidence.ParsedEvidencePath, entry.Handle.Paths.ParsedEvidence, StringComparison.Ordinal)) ->
                Error(ArtifactFailure "verification evidence is not owned by its registry run")
            | true, entry ->
                match quotaFailure handle with
                | Some error -> Error error
                | None ->
                    let completed = { entry with Completed = Some evidence; CompletedAt = Some DateTimeOffset.UtcNow }
                    if entries.TryUpdate(key, completed, entry) then Ok() else Error(ArtifactFailure "verification run completion conflicted"))

    member _.Abort(handle: RunHandle) =
        lock gate (fun () ->
            let mutable removed = Unchecked.defaultof<RegistryEntry>
            if entries.TryRemove(RunId.value handle.RunId, &removed) then
                try Directory.Delete(removed.Handle.Paths.Directory, true) with _ -> ())

    member _.Get(runId: string) : Result<RetainedEvidence, VerificationError> =
        lock gate (fun () ->
            removeExpired DateTimeOffset.UtcNow

            if String.IsNullOrWhiteSpace runId then Error(UnknownRunId "runId must be non-empty")
            else
                match entries.TryGetValue runId with
                | false, _ -> Error(UnknownRunId "unknown or expired verification runId")
                | true, entry ->
                    match entry.Completed with
                    | Some evidence -> Ok evidence
                    | None -> Error(MissingArtifact "verification evidence is not complete yet"))

    member _.CheckQuota(handle: RunHandle) : Result<unit, VerificationError> =
        lock gate (fun () ->
            let key = RunId.value handle.RunId

            match entries.TryGetValue key with
            | false, _ -> Error(UnknownRunId "verification run is no longer available")
            | true, entry when entry.Handle <> handle -> Error(ArtifactFailure "verification run handle is not owned by its registry")
            | true, _ ->
                match quotaFailure handle with
                | Some error -> Error error
                | None -> Ok())

    member _.EndSession() =
        lock gate (fun () ->
            for pair in entries do
                let mutable removed = Unchecked.defaultof<RegistryEntry>
                if entries.TryRemove(pair.Key, &removed) then
                    try Directory.Delete(removed.Handle.Paths.Directory, true) with _ -> ())

    member _.Count = entries.Count

    interface IDisposable with
        member this.Dispose() = this.EndSession()

module ArtifactFiles =
    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let read path =
        try Ok(File.ReadAllText path) with error -> Error(MissingArtifact $"artifact could not be read: {error.Message}")

    let writeEvidence (handle: RunHandle) (evidence: RetainedEvidence) =
        try
            let metadata =
                {| operation = evidence.Operation.ToString().ToLowerInvariant()
                   status = VerificationStatus.ofProcessStatus evidence.Process.Status |> string
                   exitCode =
                    match evidence.Process.Status with
                    | ProcessStatus.Completed code -> Some code
                    | _ -> None
                   durationMs = int64 evidence.Process.Duration.TotalMilliseconds
                   stdoutBytes = evidence.Process.StdoutBytes
                   stderrBytes = evidence.Process.StderrBytes
                   stdoutTruncated = evidence.Process.StdoutBytes >= Budgets.DefaultArtifactQuotas.MaxStdoutBytes
                   stderrTruncated = evidence.Process.StderrBytes >= Budgets.DefaultArtifactQuotas.MaxStderrBytes
                   binlog = File.Exists handle.Paths.Binlog
                   binlogUnavailableReason = if File.Exists handle.Paths.Binlog then None else Some "binlog was not produced by the selected invocation"
                   trx = File.Exists handle.Paths.Trx
                   trxUnavailableReason = if File.Exists handle.Paths.Trx then None else Some "TRX was not produced by the selected invocation"
                   artifactQuotas =
                    {| maxStdoutBytes = Budgets.DefaultArtifactQuotas.MaxStdoutBytes
                       maxStderrBytes = Budgets.DefaultArtifactQuotas.MaxStderrBytes
                       maxBinlogBytes = Budgets.DefaultArtifactQuotas.MaxBinlogBytes
                       maxTrxBytes = Budgets.DefaultArtifactQuotas.MaxTrxBytes
                       maxRunBytes = Budgets.DefaultArtifactQuotas.MaxRunBytes
                       maxAggregateBytes = Budgets.DefaultArtifactQuotas.MaxAggregateBytes |} |}

            File.WriteAllText(handle.Paths.Metadata, JsonSerializer.Serialize(metadata, jsonOptions), Encoding.UTF8)

            let diagnostics =
                evidence.Diagnostics
                |> List.map (fun item ->
                    {| severity = item.Severity.ToString().ToLowerInvariant()
                       code = item.Code
                       file = item.File
                       line = item.Line
                       column = item.Column
                       message = item.Message |})

            let tests =
                evidence.Tests
                |> Option.map (fun testEvidence ->
                    {| counts = testEvidence.Counts
                       trxAvailable = testEvidence.TrxAvailable
                       trxUnavailableReason = testEvidence.TrxUnavailableReason
                       cases =
                        testEvidence.Cases
                        |> List.map (fun item ->
                            {| name = item.Name
                               outcome = item.Outcome.ToString().ToLowerInvariant()
                               message = item.Message |}) |})

            let parsed = {| diagnostics = diagnostics; tests = tests |}
            File.WriteAllText(handle.Paths.ParsedEvidence, JsonSerializer.Serialize(parsed, jsonOptions), Encoding.UTF8)
            Ok()
        with error ->
            Error(ArtifactFailure $"verification evidence could not be persisted: {error.Message}")
