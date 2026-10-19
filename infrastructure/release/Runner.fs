// role: release/build entrypoint
module ReleaseRunner

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open SharedFailure
open BuildProvenance

let private emitFailure (reason: string) =
    let result = JsonObject()
    result["status"] <- JsonValue.Create "failed"
    result["reason"] <- JsonValue.Create reason
    Console.Out.WriteLine(result.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

let private emitResolution (resolution: ReleaseEvaluate.Resolution) =
    Console.Out.WriteLine(ReleaseOrchestration.renderResolution resolution)

let private publicationAuthorityNames = [ "GITHUB_TOKEN"; "MCP_RELEASE_DO_UPLOAD" ]

let private hasPublicationAuthority () =
    publicationAuthorityNames
    |> List.tryFind (fun name -> not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable name)))

let private buildRoot () = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private runPrepareMode (argv: string[]) =
    async {
        if argv.Length <> 3 then
            eprintfn "usage: ReleaseRunner prepare <Config.json> <outdir>"
            return 2
        else
            match hasPublicationAuthority () with
            | Some name ->
                emitFailure $"preparation refuses to start because '{name}' is set"
                return 4
            | None -> return! ReleaseOrchestration.prepare (buildRoot ()) argv[1] argv[2]
    }

type private PublishCli =
    { ConfigPath: string
      CandidateDirectory: string
      WorkspaceRoot: string
      EventSha: string
      Repo: string }

type private PublishCliParseError =
    | MissingInput of string
    | InvalidInput of string

let private parsePublishCli (argv: string[]) =
    if argv.Length < 3 then Error(InvalidInput "publish requires Config.json, candidate-directory, and the three required flags")
    else
        let flags = [ "--workspace-root"; "--event-sha"; "--repo" ]
        let values = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        let mutable error = None
        for arg in argv |> Array.skip 3 do
            let separator = arg.IndexOf('=')
            if separator <= 2 || not (arg.StartsWith("--", StringComparison.Ordinal)) then
                error <- Some(InvalidInput "publish accepts only --key=value flags")
            else
                let key, value = arg.Substring(0, separator), arg.Substring(separator + 1)
                if not (List.contains key flags) then error <- Some(InvalidInput $"unknown publish flag '{key}'")
                elif values.ContainsKey key then error <- Some(InvalidInput $"duplicate publish flag '{key}'")
                elif String.IsNullOrWhiteSpace value then error <- Some(InvalidInput $"empty publish flag '{key}'")
                else values.Add(key, value)
        match error with
        | Some reason -> Error reason
        | None when flags |> List.exists (fun key -> not (values.ContainsKey key)) ->
            flags
            |> List.tryFind (fun key -> not (values.ContainsKey key))
            |> Option.map MissingInput
            |> Option.defaultValue (InvalidInput "publish required-input validation failed")
            |> Error
        | None ->
            Ok
                { ConfigPath = argv[1]
                  CandidateDirectory = argv[2]
                  WorkspaceRoot = values["--workspace-root"]
                  EventSha = values["--event-sha"]
                  Repo = values["--repo"] }

// The private publication directory is the runner-temp parent of the candidate directory; it holds the
// uploaded projection and every transport download, so it must exist before the first remote observation.
let ensurePublicationDirectory (candidateRoot: string) : Result<string, ReleaseError> =
    match Path.GetDirectoryName candidateRoot with
    | parent when String.IsNullOrEmpty parent ->
        Error(FileFailure("mkdir", candidateRoot, "the candidate directory has no parent directory for the private publication directory"))
    | parent ->
        let directory = Path.Combine(parent, "publication")

        try
            Directory.CreateDirectory directory |> ignore
            Ok directory
        with
        | :? IOException as error -> Error(FileFailure("mkdir", directory, error.Message))
        | :? UnauthorizedAccessException as error -> Error(FileFailure("mkdir", directory, error.Message))
        | :? ArgumentException as error -> Error(FileFailure("mkdir", directory, error.Message))
        | :? NotSupportedException as error -> Error(FileFailure("mkdir", directory, error.Message))

let private runPublishPipelineWithTransportFactory (createTransport: unit -> ReleasePublish.HttpTransport) (policy: ReleasePolicy.PolicyProjection) (snapshot: ReleaseHandoff.CandidateArchiveSnapshot) (eventSha: string) (metadataLocalPath: string) (owner: string) (repo: string) =
    async {
        match! ReleaseEvaluate.VerifiedCandidate.createFromSnapshot snapshot policy policy.ArchiveName eventSha metadataLocalPath owner repo with
        | Error error -> emitFailure (ReleaseError.message error); return 4
        | Ok candidate ->
            if Environment.GetEnvironmentVariable("MCP_RELEASE_DO_UPLOAD") <> "true" then
                emitFailure "publication requires MCP_RELEASE_DO_UPLOAD=true; local-mirror fallback is disabled"
                return 4
            else
                match ReleasePublish.environmentToken () with
                | Error error -> emitFailure (ReleaseError.message error); return 4
                | Ok token ->
                    let directory = Path.GetDirectoryName metadataLocalPath
                    let transport = createTransport ()
                    let locator: ReleasePublish.ReleaseLocator = { Owner = owner; Repository = repo; Tag = policy.ComponentId }
                    match! ReleaseTransaction.run transport (Some token) true directory locator candidate with
                    | Error error -> emitFailure (ReleaseError.message error); return 4
                    | Ok (resolution, exitCode) -> emitResolution resolution; return exitCode
    }

let private runPublishMode (argv: string[]) =
    async {
        match parsePublishCli argv with
        | Error(MissingInput flag) -> emitFailure $"publish is missing required flag '{flag}'"; return 4
        | Error(InvalidInput reason) -> emitFailure reason; return 4
        | Ok cli ->
            let root = Path.GetFullPath cli.WorkspaceRoot
            let canonicalRepo = "masterlifting/mcp-store"
            if cli.Repo <> canonicalRepo then
                emitFailure $"--repo must be '{canonicalRepo}'"
                return 4
            elif String.IsNullOrWhiteSpace cli.EventSha || cli.EventSha.Length <> 40 || (cli.EventSha |> Seq.exists (fun c -> not (Uri.IsHexDigit c) || Char.IsUpper c)) then
                emitFailure "--event-sha must be a 40-character lowercase hexadecimal SHA"
                return 4
            else
                let configPath = Path.GetFullPath(Path.Combine(root, cli.ConfigPath))
                let! rosterResult = ReleaseEnrollment.loadRoster root
                match rosterResult with
                | Error error -> emitFailure (ReleaseError.message error); return 4
                | Ok roster ->
                    match ReleaseEnrollment.validateRoster roster, ReleaseEnrollment.validateConfigPathAgainstRoster configPath root roster with
                    | Error error, _ | _, Error error -> emitFailure (ReleaseError.message error); return 4
                    | Ok (), Ok () ->
                        match File.ReadAllBytes configPath |> fun bytes -> ReleasePolicy.parseBytes configPath bytes with
                        | Error error -> emitFailure (ReleaseError.message error); return 4
                        | Ok policy ->
                            match ReleaseEnrollment.validateProjectionAgainstRoster policy.ComponentId configPath root roster with
                            | Error error -> emitFailure (ReleaseError.message error); return 4
                            | Ok () ->
                                match! BuildProvenance.committedHead root with
                                | Error error -> emitFailure (ReleaseError.message error); return 4
                                | Ok head when head <> cli.EventSha -> emitFailure "trusted checkout HEAD does not equal --event-sha"; return 4
                                | Ok _ ->
                                    match! BuildProvenance.assertCleanTree root with
                                    | Error error -> emitFailure (ReleaseError.message error); return 4
                                    | Ok () ->
                                        let candidateRoot = Path.GetFullPath cli.CandidateDirectory
                                        let separator = string Path.DirectorySeparatorChar
                                        let rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + separator
                                        if candidateRoot.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || candidateRoot = root then
                                            emitFailure "candidate directory must be outside the trusted checkout"
                                            return 4
                                        else
                                            match ReleaseHandoff.readCandidate candidateRoot policy cli.EventSha with
                                            | Error error -> emitFailure (ReleaseError.message error); return 4
                                            | Ok snapshot ->
                                                match ensurePublicationDirectory candidateRoot with
                                                | Error error -> emitFailure (ReleaseError.message error); return 4
                                                | Ok privateDirectory ->
                                                    let metadataPath = Path.Combine(privateDirectory, ReleaseMetadata.metadataAssetName policy.ComponentId policy.Version)
                                                    return! runPublishPipelineWithTransportFactory (fun () -> ReleasePublish.defaultHttpTransport ()) policy snapshot cli.EventSha metadataPath "masterlifting" "mcp-store"
    }

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        eprintfn "usage: ReleaseRunner prepare <Config.json> <outdir>"
        eprintfn "usage: ReleaseRunner publish <Config.json> <candidate-directory> --workspace-root=<trusted-checkout> --event-sha=<exactHEAD> --repo=<masterlifting/mcp-store>"
        2
    elif argv[0] = "prepare" then
        // the compiled CLI must finish its asynchronous operation before exiting
        runPrepareMode argv |> Async.RunSynchronously
    elif argv[0] = "publish" then
        runPublishMode argv |> Async.RunSynchronously
    else
        eprintfn "usage: ReleaseRunner prepare <Config.json> <outdir>"
        eprintfn "usage: ReleaseRunner publish <Config.json> <candidate-directory> --workspace-root=<trusted-checkout> --event-sha=<exactHEAD> --repo=<masterlifting/mcp-store>"
        2
