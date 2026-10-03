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

type ArtifactRegistry(artifactRoot: string, workspaceNamespace: string, retention: TimeSpan, ?quotas: ArtifactQuotas) =
    let entries = ConcurrentDictionary<string, RegistryEntry>(StringComparer.Ordinal)
    let gate = obj ()
    let effectiveQuotas = quotas |> Option.defaultValue Budgets.DefaultArtifactQuotas
    let artifactRoot = Path.GetFullPath artifactRoot
    let workspaceDirectory = Path.Combine(artifactRoot, workspaceNamespace)
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

    // A reparse point inside an owned artifact directory is an expected denial,
    // returned directly as a typed error instead of raised and reclassified later.
    let rec sumFiles directory : Result<int64, VerificationError> =
        if isReparse directory then
            Error(ArtifactFailure "artifact root contains a reparse point")
        else
            try
                let mutable failure = None
                let mutable total = 0L

                for path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly) do
                    if failure.IsNone then
                        if isReparse path then
                            failure <- Some(ArtifactFailure "artifact root contains a reparse point")
                        else
                            total <- total + fileLength path

                if failure.IsNone then
                    for subdirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly) do
                        if failure.IsNone then
                            match sumFiles subdirectory with
                            | Ok value -> total <- total + value
                            | Error error -> failure <- Some error

                match failure with
                | Some error -> Error error
                | None -> Ok total
            with error ->
                Error(ArtifactFailure $"artifact size could not be measured: {error.Message}")

    let quotaFailure (handle: RunHandle) : Result<unit, VerificationError> =
        try
            let stdoutBytes = fileLength handle.Paths.Stdout
            let stderrBytes = fileLength handle.Paths.Stderr
            let binlogBytes = fileLength handle.Paths.Binlog
            let trxBytes = fileLength handle.Paths.Trx

            match sumFiles handle.Paths.Directory with
            | Error error -> Error error
            | Ok runBytes ->
                // Aggregate owns only this workspace namespace, never sibling workspaces.
                match sumFiles workspaceDirectory with
                | Error error -> Error error
                | Ok aggregateBytes ->
                    if stdoutBytes > effectiveQuotas.MaxStdoutBytes then
                        Error(ArtifactQuotaExceeded "stdout artifact quota exceeded")
                    elif stderrBytes > effectiveQuotas.MaxStderrBytes then
                        Error(ArtifactQuotaExceeded "stderr artifact quota exceeded")
                    elif binlogBytes > effectiveQuotas.MaxBinlogBytes then
                        Error(ArtifactQuotaExceeded "build binlog quota exceeded")
                    elif trxBytes > effectiveQuotas.MaxTrxBytes then
                        Error(ArtifactQuotaExceeded "TRX artifact quota exceeded")
                    elif runBytes > effectiveQuotas.MaxRunBytes then
                        Error(ArtifactQuotaExceeded "per-run artifact quota exceeded")
                    elif aggregateBytes > effectiveQuotas.MaxAggregateBytes then
                        Error(ArtifactQuotaExceeded "aggregate artifact quota exceeded")
                    else
                        Ok()
        with error ->
            Error(ArtifactFailure $"artifact quota could not be measured: {error.Message}")

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

    let removeRunDirectory (directory: string) =
        try Directory.Delete(directory, true) with _ -> ()

        // The namespace may be shared with sibling run state, so it is removed only
        // when empty and never recursively. The consumer-owned root is never removed.
        try
            if
                Directory.Exists workspaceDirectory
                && (Directory.EnumerateFileSystemEntries workspaceDirectory |> Seq.isEmpty)
            then
                Directory.Delete(workspaceDirectory, false)
        with _ ->
            ()

    let removeExpired now =
        for pair in entries do
            let entry = pair.Value

            match entry.CompletedAt with
            | Some completedAt when now - completedAt >= retention ->
                let mutable removed = Unchecked.defaultof<RegistryEntry>

                if entries.TryRemove(pair.Key, &removed) then
                    removeRunDirectory removed.Handle.Paths.Directory
            | _ -> ()

    do
        if
            String.IsNullOrWhiteSpace workspaceNamespace
            || workspaceNamespace.IndexOfAny([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]) >= 0
            || workspaceNamespace = "."
            || workspaceNamespace = ".."
        then
            invalidArg (nameof workspaceNamespace) "workspace namespace must be a single non-empty path segment"

        if retention <= TimeSpan.Zero then invalidArg (nameof retention) "retention must be positive"

        match Budgets.validateArtifactQuotas effectiveQuotas with
        | Ok _ -> ()
        | Error error -> invalidArg (nameof quotas) (VerificationError.message error)

    let allocateRun () : Result<RunHandle, VerificationError> =
        try
            match sumFiles workspaceDirectory with
            | Error error -> Error error
            | Ok currentBytes when currentBytes >= effectiveQuotas.MaxAggregateBytes ->
                Error(ArtifactQuotaExceeded "aggregate artifact quota is already exhausted")
            | Ok _ ->
                let mutable created = None
                let mutable attempt = 0

                while created.IsNone && attempt < 20 do
                    attempt <- attempt + 1
                    let runIdText = randomToken 32
                    let directoryName = randomToken 18
                    let directory = Path.GetFullPath(Path.Combine(workspaceDirectory, directoryName))

                    try
                        if not (pathIsWithin workspaceDirectory directory) then
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
                                    removeRunDirectory directory
                            else
                                removeRunDirectory directory
                    with _ -> ()

                match created with
                | Some handle -> Ok handle
                | None -> Error(ArtifactFailure "could not allocate an isolated verification artifact directory")
        with error -> Error(ArtifactFailure $"artifact quota could not be measured: {error.Message}")

    member _.Start(operation) : Result<RunHandle, VerificationError> =
        lock gate (fun () ->
            removeExpired DateTimeOffset.UtcNow

            // The workspace namespace under the configured root is created on the
            // first allocation, not at startup.
            match PathAuthorization.ensureArtifactRoot workspaceDirectory with
            | Error error -> Error error
            | Ok() -> allocateRun ())

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
                | Error error -> Error error
                | Ok() ->
                    let completed = { entry with Completed = Some evidence; CompletedAt = Some DateTimeOffset.UtcNow }
                    if entries.TryUpdate(key, completed, entry) then Ok() else Error(ArtifactFailure "verification run completion conflicted"))

    member _.Abort(handle: RunHandle) =
        lock gate (fun () ->
            let mutable removed = Unchecked.defaultof<RegistryEntry>
            if entries.TryRemove(RunId.value handle.RunId, &removed) then
                removeRunDirectory removed.Handle.Paths.Directory)

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
                | Error error -> Error error
                | Ok() -> Ok())

    member _.EndSession() =
        lock gate (fun () ->
            for pair in entries do
                let mutable removed = Unchecked.defaultof<RegistryEntry>
                if entries.TryRemove(pair.Key, &removed) then
                    removeRunDirectory removed.Handle.Paths.Directory)

    member _.Count = entries.Count

    interface IDisposable with
        member this.Dispose() = this.EndSession()

module ArtifactFiles =
    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let readAsync path : Async<Result<string, VerificationError>> =
        async {
            try
                let! text = File.ReadAllTextAsync path |> Async.AwaitTask
                return Ok text
            with error ->
                return Error(MissingArtifact $"artifact could not be read: {error.Message}")
        }

    let writeEvidence (handle: RunHandle) (evidence: RetainedEvidence) : Async<Result<unit, VerificationError>> =
        async {
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

                do!
                    File.WriteAllTextAsync(handle.Paths.Metadata, JsonSerializer.Serialize(metadata, jsonOptions), Encoding.UTF8)
                    |> Async.AwaitTask

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

                do!
                    File.WriteAllTextAsync(handle.Paths.ParsedEvidence, JsonSerializer.Serialize(parsed, jsonOptions), Encoding.UTF8)
                    |> Async.AwaitTask

                return Ok()
            with error ->
                return Error(ArtifactFailure $"verification evidence could not be persisted: {error.Message}")
        }
