// role: release/build entrypoint
module ReleaseRunner

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance

let private emitPreContractFailure (message: string) : unit =
    let node = JsonObject()
    node["status"] <- JsonValue.Create "failed"
    node["reason"] <- JsonValue.Create message
    let payload = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))
    let buffer = Encoding.UTF8.GetBytes(payload + "\n")
    try
        use stdout = Console.OpenStandardOutput()
        stdout.Write(buffer, 0, buffer.Length)
        stdout.Flush()
    with
    | _ -> ()

let private emitResolution (resolution: ReleaseEvaluate.Resolution) : unit =
    let payload = ReleaseOrchestration.renderResolution resolution
    let buffer = Encoding.UTF8.GetBytes(payload + "\n")
    try
        use stdout = Console.OpenStandardOutput()
        stdout.Write(buffer, 0, buffer.Length)
        stdout.Flush()
    with
    | _ -> ()

let private hashBytes (bytes: byte[]) : string =
    SHA256.HashData bytes
    |> Convert.ToHexString
    |> fun v -> v.ToLowerInvariant()

let private isLowerHex40 (value: string) : bool =
    if String.IsNullOrEmpty value then false
    elif value.Length <> 40 then false
    else
        let mutable ok = true
        for ch in value do
            if not (Uri.IsHexDigit ch) then ok <- false
        ok

let private isLowerHex64 (value: string) : bool =
    if String.IsNullOrEmpty value then false
    elif value.Length <> 64 then false
    else
        let mutable ok = true
        for ch in value do
            if not (Uri.IsHexDigit ch) then ok <- false
        ok

let private publicationAuthorityNames : string list =
    [ "GITHUB_TOKEN"
      "MCP_RELEASE_DO_UPLOAD"
      "MCP_RELEASE_GITHUB_OWNER"
      "MCP_RELEASE_GITHUB_REPOSITORY" ]

let private hasPublicationAuthority () : string option =
    let mutable found = None
    for name in publicationAuthorityNames do
        match found with
        | Some _ -> ()
        | None ->
            match Environment.GetEnvironmentVariable name with
            | null | "" -> ()
            | _ -> found <- Some name
    found

let private canonicalRepository () : string * string = "masterlifting", "mcp-store"

let private buildRootFromSourceDirectory () : string =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private runPrepareMode (argv: string[]) : Async<int> =
    async {
        if argv.Length < 3 then
            eprintfn "usage: ReleaseRunner prepare <config-path> <candidate-directory>"
            return 2
        else
            let configPath = argv.[1]
            let candidateDirectory = argv.[2]
            let root = buildRootFromSourceDirectory ()

            match hasPublicationAuthority () with
            | Some name ->
                emitPreContractFailure
                    ($"the preparation job refuses to start because '{name}' is set; preparation has no write authority and must never inherit publication credentials")
                return 4
            | None ->
                return! ReleaseOrchestration.prepare root configPath candidateDirectory
    }


type private PublishCli =
    { ClosurePath: string
      CandidateTransportPath: string
      Repo: string
      RunId: string
      Attempt: string
      EventSha: string
      Role: string
      ProducerComponent: string
      ArtifactId: string
      ArtifactName: string
      TransportSha256: string
      BuilderSdk: string
      BuilderRuntime: string
      BuilderPlatform: string
      WorkspaceRoot: string
      TrustedReceiptPath: string
      TransportEvidence: string
      Trigger: string option }

let private allowedPublishFlagNames : Set<string> =
    Set.ofList
        [ "--repo"
          "--run-id"
          "--attempt"
          "--event-sha"
          "--role"
          "--component"
          "--artifact-id"
          "--artifact-name"
          "--transport-sha256"
          "--builder-sdk"
          "--builder-runtime"
          "--builder-platform"
          "--workspace-root"
          "--trusted-receipt"
          "--transport-evidence"
          "--trigger" ]

let private allowedPublishFlagsText =
    String.concat ", " allowedPublishFlagNames

let private allowedRoles : Set<string> = Set.ofList [ "candidate" ]

let private parsePublishCli (argv: string[]) : Result<PublishCli, ReleaseError> =
    if argv.Length < 3 then
        Error(MalformedArtifact("publish-cli", "the publish entrypoint expects a publisher-closure-path, a raw candidate transport ZIP file, and the required --key=value flags"))
    else
        let closurePath = argv.[1]
        let candidateTransportPath = argv.[2]

        let mutable failure : ReleaseError option = None
        let mutable index = 3
        let mutable seen : Set<string> = Set.empty

        let mutable repo = ""
        let mutable runId = ""
        let mutable attempt = ""
        let mutable eventSha = ""
        let mutable role = ""
        let mutable producerComponent = ""
        let mutable artifactId = ""
        let mutable artifactName = ""
        let mutable transportSha256 = ""
        let mutable builderSdk = ""
        let mutable builderRuntime = ""
        let mutable builderPlatform = ""
        let mutable workspaceRoot = ""
        let mutable trustedReceiptPath = ""
        let mutable transportEvidence = ""
        let mutable trigger : string option = None

        while index < argv.Length && failure.IsNone do
            let arg = argv.[index]

            if String.IsNullOrEmpty arg then
                failure <- Some(MalformedArtifact("publish-cli", $"the publish CLI argument at position {index} is empty"))
            elif not (arg.StartsWith "--") then
                failure <- Some(MalformedArtifact("publish-cli", $"the publish CLI rejects the unexpected positional argument '{arg}'; only --key=value flags are accepted after the two positional paths"))
            else
                let eq = arg.IndexOf '='
                if eq <= 0 then
                    failure <- Some(MalformedArtifact("publish-cli", $"the publish CLI flag '{arg}' is missing the required '=' separator"))
                else
                    let key = arg.Substring(0, eq)
                    let value = arg.Substring(eq + 1)

                    if not (Set.contains key allowedPublishFlagNames) then
                        let detail = "the publish CLI rejects the unknown flag '" + key + "'; allowed flags are " + allowedPublishFlagsText
                        failure <- Some(MalformedArtifact("publish-cli", detail))
                    elif Set.contains key seen then
                        failure <- Some(MalformedArtifact("publish-cli", $"the publish CLI rejects the duplicate flag '{key}'"))
                    else
                        seen <- seen.Add key

                        match key with
                        | "--repo" -> repo <- value
                        | "--run-id" -> runId <- value
                        | "--attempt" -> attempt <- value
                        | "--event-sha" -> eventSha <- value
                        | "--role" -> role <- value
                        | "--component" -> producerComponent <- value
                        | "--artifact-id" -> artifactId <- value
                        | "--artifact-name" -> artifactName <- value
                        | "--transport-sha256" -> transportSha256 <- value
                        | "--builder-sdk" -> builderSdk <- value
                        | "--builder-runtime" -> builderRuntime <- value
                        | "--builder-platform" -> builderPlatform <- value
                        | "--workspace-root" -> workspaceRoot <- value
                        | "--trusted-receipt" -> trustedReceiptPath <- value
                        | "--transport-evidence" -> transportEvidence <- value
                        | "--trigger" -> trigger <- Some value
                        | _ ->
                            failure <- Some(MalformedArtifact("publish-cli", $"the publish CLI flag '{key}' is in the allowlist but has no binding"))

            index <- index + 1

        let required : (string * string) list =
            [ ("--repo", repo)
              ("--run-id", runId)
              ("--attempt", attempt)
              ("--event-sha", eventSha)
              ("--role", role)
              ("--component", producerComponent)
              ("--artifact-id", artifactId)
              ("--artifact-name", artifactName)
              ("--transport-sha256", transportSha256)
              ("--builder-sdk", builderSdk)
              ("--builder-runtime", builderRuntime)
              ("--builder-platform", builderPlatform)
              ("--workspace-root", workspaceRoot)
              ("--trusted-receipt", trustedReceiptPath)
              ("--transport-evidence", transportEvidence) ]

        let mutable missing : string list = []
        for name, value in required do
            if String.IsNullOrWhiteSpace value then
                missing <- name :: missing
        if not (List.isEmpty missing) then
            let detail = String.concat ", " (List.rev missing)
            failure <- Some(MalformedArtifact("publish-cli", $"the publish CLI is missing required flag(s): {detail}"))

        match failure with
        | Some _ -> ()
        | None ->
            if not (Set.contains role allowedRoles) then
                let detail = "the publish CLI --role '" + role + "' is not 'candidate'; the publish entrypoint only accepts the candidate role, not publisher/runtime (Bootstrap.ps1 InstallRuntime outputs are protected BOOTSTRAP installation evidence, not candidate publish input)"
                failure <- Some(MalformedArtifact("publish-cli", detail))

        match failure with
        | Some error -> Error error
        | None ->
            Ok
                { ClosurePath = closurePath
                  CandidateTransportPath = candidateTransportPath
                  Repo = repo
                  RunId = runId
                  Attempt = attempt
                  EventSha = eventSha
                  Role = role
                  ProducerComponent = producerComponent
                  ArtifactId = artifactId
                  ArtifactName = artifactName
                  TransportSha256 = transportSha256
                  BuilderSdk = builderSdk
                  BuilderRuntime = builderRuntime
                  BuilderPlatform = builderPlatform
                  WorkspaceRoot = workspaceRoot
                  TrustedReceiptPath = trustedReceiptPath
                  TransportEvidence = transportEvidence
                  Trigger = trigger }

let private allowedComponents : Set<string> = Set.ofList [ "dotnet"; "workflow" ]

let private splitRepo (value: string) : (string * string) option =
    let parts = value.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
    if parts.Length <> 2 then None
    else Some(parts.[0], parts.[1])

let private loadCandidateBytes (path: string) : Result<byte[], ReleaseError> =
    try
        if not (File.Exists path) then
            Error(MissingArtifact path)
        else
            Ok(File.ReadAllBytes path)
    with
    | :? IOException as error -> Error(FileFailure("read", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))

let private loadCandidateText (path: string) : Result<string, ReleaseError> =
    match loadCandidateBytes path with
    | Ok bytes -> Ok(Encoding.UTF8.GetString bytes)
    | Error error -> Error error

let private boundedTransportPath (path: string) : Result<string * string, ReleaseError> =
    try
        if String.IsNullOrWhiteSpace path then
            Error(MalformedArtifact("publish", "the publisher received an empty raw candidate transport file path"))
        elif Directory.Exists path then
            Error(MalformedArtifact("publish", "the candidate transport positional argument must be a raw ZIP file, not a directory"))
        elif not (File.Exists path) then
            Error(MissingArtifact path)
        else
            let canonicalPath = Path.GetFullPath path
            Ok(canonicalPath, Path.GetDirectoryName canonicalPath)
    with
    | :? IOException as error -> Error(FileFailure("resolve", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("resolve", path, error.Message))
    | :? ArgumentException as error -> Error(FileFailure("resolve", path, error.Message))
    | :? NotSupportedException as error -> Error(FileFailure("resolve", path, error.Message))

let private crossCheckStrings (field: string) (expected: string) (observed: string) : Result<unit, ReleaseError> =
    if expected = observed then Ok ()
    else Error(IdentityMismatch(field, expected, observed))

let private receiptReadLimit = 4L * 1024L * 1024L

let private loadReceiptBytes (path: string) : Result<byte[], ReleaseError> =
    try
        let info = FileInfo path
        if not info.Exists then
            Error(MissingArtifact path)
        elif info.Length > receiptReadLimit then
            let detail = "the receipt file exceeds the " + (string receiptReadLimit) + "-byte bounded read"
            Error(MalformedArtifact(path, detail))
        else
            Ok(File.ReadAllBytes path)
    with
    | :? IOException as error -> Error(FileFailure("read", path, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", path, error.Message))

let private loadReceiptText (path: string) : Result<string, ReleaseError> =
    match loadReceiptBytes path with
    | Ok bytes -> Ok(Encoding.UTF8.GetString bytes)
    | Error error -> Error error


type private BootstrapReceipt =
    { SchemaVersion: int
      Mode: string
      Status: string
      Repository: string
      RunId: string
      RunAttempt: string
      EventSha: string
      ObservedEvent: string
      Role: string
      Component: string option
      ArtifactId: string
      ArtifactName: string
      ArtifactApiDigest: string
      ArtifactPlatformDigest: string
      ArtifactSizeInBytes: int64
      DownloadRedirectHost: string
      DownloadRedirectCode: int
      DownloadBytesRead: int64
      DownloadObservedSha256: string
      UploaderJobId: string
      UploaderJobName: string
      UploaderJobConclusion: string
      UploaderJobRunAttempt: string
      HeadSha: string
      SourcePath: string }

let private bootstrapAllowedFieldNames : Set<string> =
    Set.ofList
        [ "schemaVersion"
          "mode"
          "status"
          "transport"
          "apiOrigin"
          "repository"
          "repositoryId"
          "runId"
          "runAttempt"
          "eventSha"
          "observedEvent"
          "role"
          "component"
          "artifact"
          "runAttemptObservation"
          "uploaderJob"
          "download"
          "outerArchive"
          "envelope"
          "runtime"
          "failures" ]

let private bootstrapArtifactAllowedFieldNames : Set<string> =
    Set.ofList
        [ "id"
          "name"
          "role"
          "component"
          "sizeInBytes"
          "expired"
          "apiDigestRaw"
          "apiDigest"
          "platformDigest"
          "digestSource" ]

let private bootstrapDownloadAllowedFieldNames : Set<string> =
    Set.ofList
        [ "redirectHost"
          "redirectCode"
          "bytesRead"
          "platformBytes"
          "observedSha256" ]

let private bootstrapUploaderJobAllowedFieldNames : Set<string> =
    Set.ofList
        [ "id"
          "name"
          "runId"
          "runAttempt"
          "headSha"
          "status"
          "conclusion"
          "listingUri"
          "artifactLinkLimit" ]

let private bootstrapRunAttemptAllowedFieldNames : Set<string> =
    Set.ofList
        [ "uri"
          "id"
          "runAttempt"
          "headSha"
          "event"
          "repositoryId"
          "repositoryFullName" ]

let private bootstrapSchemaVersion = 2

let private requireStringField
    (object: JsonNode)
    (field: string)
    (source: string)
    : Result<string, ReleaseError> =
    let node = object.[field]
    if isNull node then
        Error(MalformedArtifact(source, $"the bootstrap receipt is missing the required string field '{field}'"))
    else
        try
            let value = node.AsValue().GetValue<string>()
            if String.IsNullOrWhiteSpace value then
                Error(MalformedArtifact(source, $"the bootstrap receipt field '{field}' is empty"))
            else
                Ok value
        with
        | _ ->
            Error(MalformedArtifact(source, $"the bootstrap receipt field '{field}' is not a non-empty string"))

let private requireIntField
    (object: JsonNode)
    (field: string)
    (source: string)
    : Result<int, ReleaseError> =
    let node = object.[field]
    if isNull node then
        Error(MalformedArtifact(source, $"the bootstrap receipt is missing the required integer field '{field}'"))
    else
        try
            Ok(node.AsValue().GetValue<int>())
        with
        | _ ->
            try
                Ok(int (node.AsValue().GetValue<int64>()))
            with
            | _ ->
                Error(MalformedArtifact(source, $"the bootstrap receipt field '{field}' is not an integer"))

let private optionalIntField
    (object: JsonNode)
    (field: string)
    : int =
    let node = object.[field]
    if isNull node then 0
    else
        try
            node.AsValue().GetValue<int>()
        with
        | _ ->
            try
                int (node.AsValue().GetValue<int64>())
            with
            | _ -> 0

let private optionalStringField
    (object: JsonNode)
    (field: string)
    : string =
    let node = object.[field]
    if isNull node then ""
    else
        try
            node.AsValue().GetValue<string>()
        with
        | _ -> ""

let private rejectUnknownFields
    (object: JsonNode)
    (allowed: Set<string>)
    (source: string)
    (subject: string)
    : Result<unit, ReleaseError> =
    let mutable unknown : string list = []
    for property in object.AsObject() do
        if not (Set.contains property.Key allowed) then
            unknown <- property.Key :: unknown
    match unknown with
    | _ :: _ ->
        let detail = String.concat ", " (List.rev unknown)
        Error(MalformedArtifact(source, $"the bootstrap receipt {subject} contains unknown field(s): {detail}"))
    | [] -> Ok ()

let private parseBootstrapReceipt
    (text: string)
    (sourcePath: string)
    : Result<BootstrapReceipt, ReleaseError> =
    let docResult =
        try
            Ok(JsonNode.Parse text)
        with
        | :? JsonException as error ->
            Error(MalformedArtifact(sourcePath, $"the bootstrap receipt is not valid JSON: {error.Message}"))
        | :? FormatException as error ->
            Error(MalformedArtifact(sourcePath, $"the bootstrap receipt is not valid JSON: {error.Message}"))

    match docResult with
    | Error error -> Error error
    | Ok doc when isNull doc ->
        Error(MalformedArtifact(sourcePath, "the bootstrap receipt document is empty"))
    | Ok doc ->
        let root = doc.AsObject()
        match rejectUnknownFields (root :> JsonNode) bootstrapAllowedFieldNames sourcePath "root" with
        | Error error -> Error error
        | Ok () ->
            match requireIntField (root :> JsonNode) "schemaVersion" sourcePath with
            | Error error -> Error error
            | Ok schemaValue ->
                if schemaValue <> bootstrapSchemaVersion then
                    Error(MalformedArtifact(sourcePath, $"the bootstrap receipt schemaVersion '{schemaValue}' does not match the expected {bootstrapSchemaVersion}"))
                else
                    match requireStringField (root :> JsonNode) "mode" sourcePath with
                    | Error error -> Error error
                    | Ok modeValue ->
                        match requireStringField (root :> JsonNode) "status" sourcePath with
                        | Error error -> Error error
                        | Ok statusValue ->
                            if statusValue <> "fetched" then
                                Error(MalformedArtifact(sourcePath, $"the bootstrap receipt status '{statusValue}' is not 'fetched'; the publish entrypoint only accepts Bootstrap.ps1 FetchCandidate outputs, not InstallRuntime (status='installed') outputs which are protected BOOTSTRAP installation evidence"))
                            else
                                match requireStringField (root :> JsonNode) "repository" sourcePath with
                                | Error error -> Error error
                                | Ok repositoryValue ->
                                    match requireStringField (root :> JsonNode) "runId" sourcePath with
                                    | Error error -> Error error
                                    | Ok runIdValue ->
                                        match requireStringField (root :> JsonNode) "runAttempt" sourcePath with
                                        | Error error -> Error error
                                        | Ok runAttemptValue ->
                                            match requireStringField (root :> JsonNode) "eventSha" sourcePath with
                                            | Error error -> Error error
                                            | Ok eventShaValue ->
                                                let observedEvent = optionalStringField (root :> JsonNode) "observedEvent"
                                                match requireStringField (root :> JsonNode) "role" sourcePath with
                                                | Error error -> Error error
                                                | Ok roleValue ->
                                                    let componentResult : Result<string option, ReleaseError> =
                                                        let componentNode = root.["component"]
                                                        if isNull componentNode then Ok None
                                                        else
                                                            try
                                                                let raw = componentNode.AsValue().GetValue<string>()
                                                                if String.IsNullOrWhiteSpace raw then Ok None
                                                                else Ok(Some raw)
                                                            with
                                                            | _ ->
                                                                Error(MalformedArtifact(sourcePath, "the bootstrap receipt field 'component' is not a string"))
                                                    match componentResult with
                                                    | Error error -> Error error
                                                    | Ok componentValue ->
                                                    let artifactNode = root.["artifact"]
                                                    if isNull artifactNode then
                                                        Error(MalformedArtifact(sourcePath, "the bootstrap receipt is missing the required 'artifact' object"))
                                                    else
                                                        match rejectUnknownFields artifactNode bootstrapArtifactAllowedFieldNames sourcePath "artifact" with
                                                        | Error error -> Error error
                                                        | Ok () ->
                                                            match requireStringField artifactNode "id" sourcePath with
                                                            | Error error -> Error error
                                                            | Ok artifactIdValue ->
                                                                match requireStringField artifactNode "name" sourcePath with
                                                                | Error error -> Error error
                                                                | Ok artifactNameValue ->
                                                                    match requireStringField artifactNode "apiDigest" sourcePath with
                                                                    | Error error -> Error error
                                                                    | Ok apiDigestValue ->
                                                                        match requireStringField artifactNode "platformDigest" sourcePath with
                                                                        | Error error -> Error error
                                                                        | Ok platformDigestValue ->
                                                                            match requireIntField artifactNode "sizeInBytes" sourcePath with
                                                                            | Error error -> Error error
                                                                            | Ok sizeValue ->
                                                                                let downloadNode = root.["download"]
                                                                                if isNull downloadNode then
                                                                                    Error(MalformedArtifact(sourcePath, "the bootstrap receipt is missing the required 'download' object"))
                                                                                else
                                                                                    match rejectUnknownFields downloadNode bootstrapDownloadAllowedFieldNames sourcePath "download" with
                                                                                    | Error error -> Error error
                                                                                    | Ok () ->
                                                                                        let redirectHost = optionalStringField downloadNode "redirectHost"
                                                                                        let redirectCode = optionalIntField downloadNode "redirectCode"
                                                                                        match requireIntField downloadNode "bytesRead" sourcePath with
                                                                                        | Error error -> Error error
                                                                                        | Ok bytesReadValue ->
                                                                                            match requireStringField downloadNode "observedSha256" sourcePath with
                                                                                            | Error error -> Error error
                                                                                            | Ok downloadShaValue ->
                                                                                                let uploaderNode = root.["uploaderJob"]
                                                                                                if isNull uploaderNode then
                                                                                                    Error(MalformedArtifact(sourcePath, "the bootstrap receipt is missing the required 'uploaderJob' object"))
                                                                                                else
                                                                                                    match rejectUnknownFields uploaderNode bootstrapUploaderJobAllowedFieldNames sourcePath "uploaderJob" with
                                                                                                    | Error error -> Error error
                                                                                                    | Ok () ->
                                                                                                        match requireStringField uploaderNode "id" sourcePath with
                                                                                                        | Error error -> Error error
                                                                                                        | Ok uploaderIdValue ->
                                                                                                            let uploaderName = optionalStringField uploaderNode "name"
                                                                                                            let uploaderAttempt = optionalStringField uploaderNode "runAttempt"
                                                                                                            match requireStringField uploaderNode "conclusion" sourcePath with
                                                                                                            | Error error -> Error error
                                                                                                            | Ok uploaderConclusionValue ->
                                                                                                                let observationNode = root.["runAttemptObservation"]
                                                                                                                let observationHeadSha =
                                                                                                                    if isNull observationNode then ""
                                                                                                                    else
                                                                                                                        try
                                                                                                                            observationNode.AsObject().["headSha"].AsValue().GetValue<string>()
                                                                                                                        with
                                                                                                                        | _ -> ""
                                                                                                                if observationHeadSha <> "" && observationHeadSha <> eventShaValue then
                                                                                                                    Error(MalformedArtifact(sourcePath, $"the bootstrap receipt runAttemptObservation.headSha '{observationHeadSha}' does not equal the receipt eventSha '{eventShaValue}'"))
                                                                                                                else
                                                                                                                    Ok
                                                                                                                        { SchemaVersion = schemaValue
                                                                                                                          Mode = modeValue
                                                                                                                          Status = statusValue
                                                                                                                          Repository = repositoryValue
                                                                                                                          RunId = runIdValue
                                                                                                                          RunAttempt = runAttemptValue
                                                                                                                          EventSha = eventShaValue
                                                                                                                          ObservedEvent = observedEvent
                                                                                                                          Role = roleValue
                                                                                                                          Component = componentValue
                                                                                                                          ArtifactId = artifactIdValue
                                                                                                                          ArtifactName = artifactNameValue
                                                                                                                          ArtifactApiDigest = apiDigestValue
                                                                                                                          ArtifactPlatformDigest = platformDigestValue
                                                                                                                          ArtifactSizeInBytes = int64 sizeValue
                                                                                                                          DownloadRedirectHost = redirectHost
                                                                                                                          DownloadRedirectCode = redirectCode
                                                                                                                          DownloadBytesRead = int64 bytesReadValue
                                                                                                                          DownloadObservedSha256 = downloadShaValue
                                                                                                                          UploaderJobId = uploaderIdValue
                                                                                                                          UploaderJobName = uploaderName
                                                                                                                          UploaderJobConclusion = uploaderConclusionValue
                                                                                                                          UploaderJobRunAttempt = uploaderAttempt
                                                                                                                          HeadSha = observationHeadSha
                                                                                                                          SourcePath = sourcePath }

let private loadBootstrapReceipt (path: string) : Result<BootstrapReceipt, ReleaseError> =
    match loadReceiptBytes path with
    | Error error -> Error error
    | Ok bytes ->
        let text = Encoding.UTF8.GetString bytes
        match parseBootstrapReceipt text path with
        | Error error -> Error error
        | Ok receipt -> Ok receipt

let private isolateBootstrapEvidencePath
    (evidencePath: string)
    (candidateRoot: string)
    (workspaceRoot: string)
    : Result<string, ReleaseError> =
    let canonicalEvidence = Path.GetFullPath evidencePath
    let canonicalCandidate = Path.GetFullPath candidateRoot
    let canonicalWorkspace = Path.GetFullPath workspaceRoot
    let sep = string Path.DirectorySeparatorChar
    let inside (inner: string) (outer: string) : bool =
        let comparison = StringComparison.OrdinalIgnoreCase
        if inner = outer then true
        elif inner.StartsWith(outer + sep, comparison) then true
        else false
    if inside canonicalEvidence canonicalCandidate then
        Error(MalformedArtifact("publish-cli", $"the publisher rejects --transport-evidence '{evidencePath}': the bootstrap receipt must NOT be inside the candidate directory; a candidate cannot author its own observed metadata"))
    elif inside canonicalEvidence canonicalWorkspace then
        Error(MalformedArtifact("publish-cli", $"the publisher rejects --transport-evidence '{evidencePath}': the bootstrap receipt must NOT be inside the workspace root; a single checked-out path cannot host both source and observed transport evidence"))
    elif canonicalEvidence = Path.GetFullPath(Path.Combine(canonicalCandidate, "receipt.json")) then
        Error(MalformedArtifact("publish-cli", $"the publisher rejects --transport-evidence '{evidencePath}': the bootstrap receipt path collides with the candidate's 'receipt.json'; the trusted publisher receipt and the bootstrap receipt are separate documents with separate paths"))
    else
        Ok canonicalEvidence

let private isolateTrustedReceiptPath
    (receiptPath: string)
    (candidateRoot: string)
    (transportEvidencePath: string)
    : Result<string, ReleaseError> =
    let canonicalReceipt = Path.GetFullPath receiptPath
    let canonicalCandidate = Path.GetFullPath candidateRoot
    let canonicalTransport = Path.GetFullPath transportEvidencePath
    let canonicalCandidateReceipt = Path.GetFullPath(Path.Combine(canonicalCandidate, "receipt.json"))
    let sep = string Path.DirectorySeparatorChar
    let inside (inner: string) (outer: string) : bool =
        let comparison = StringComparison.OrdinalIgnoreCase
        if inner = outer then true
        elif inner.StartsWith(outer + sep, comparison) then true
        else false
    if canonicalReceipt = canonicalCandidateReceipt then
        Error(MalformedArtifact("publish-cli", $"the publisher rejects --trusted-receipt '{receiptPath}': the trusted receipt path collides with the candidate's 'receipt.json'"))
    elif inside canonicalReceipt canonicalCandidate then
        Error(MalformedArtifact("publish-cli", $"the publisher rejects --trusted-receipt '{receiptPath}': the trusted receipt path must NOT be inside the candidate directory; a candidate cannot author its own trusted provenance"))
    elif canonicalReceipt = canonicalTransport then
        Error(MalformedArtifact("publish-cli", $"the publisher rejects --trusted-receipt '{receiptPath}': the trusted receipt path equals --transport-evidence"))
    else
        Ok canonicalReceipt

let private crossCheckComponent (cliComponent: string) (receiptPath: string) (receiptSha: string) : Result<unit, ReleaseError> =
    if not (Set.contains cliComponent allowedComponents) then
        let detail = "the publish CLI --component '" + cliComponent + "' is not one of dotnet|workflow"
        Error(MalformedArtifact("publish-cli", detail))
    elif String.IsNullOrEmpty receiptPath then
        let detail = "the trusted publisher receipt declares no canonical Config.fsx path for component '" + cliComponent + "'; the receipt MUST carry a non-empty componentConfigPaths entry for every supported component"
        Error(MalformedArtifact("trusted-receipt", detail))
    elif String.IsNullOrEmpty receiptSha then
        let detail = "the trusted publisher receipt declares no canonical Config.fsx SHA-256 for component '" + cliComponent + "'; the receipt MUST carry a non-empty componentConfigShas entry for every supported component"
        Error(MalformedArtifact("trusted-receipt", detail))
    else
        Ok ()

let private reconcilePolicyWithReceipt
    (componentId: string)
    (workspaceRoot: string)
    (receipt: ReleaseHandoff.TrustedPublisherReceipt)
    : Result<ReleasePolicy.PolicyProjection * string * string, ReleaseError> =
    let lookupPath =
        match Map.tryFind componentId receipt.ComponentConfigPaths with
        | Some v -> v
        | None -> ""
    let lookupSha =
        match Map.tryFind componentId receipt.ComponentConfigShas with
        | Some v -> v.Value
        | None -> ""
    match crossCheckComponent componentId lookupPath lookupSha with
    | Error error -> Error error
    | Ok () ->
        let absoluteConfig = Path.GetFullPath(Path.Combine(workspaceRoot, lookupPath))
        match loadCandidateBytes absoluteConfig with
        | Error error -> Error error
        | Ok configBytes ->
            let configSha = hashBytes configBytes
            if configSha <> lookupSha then
                Error(IdentityMismatch("componentConfigSha", lookupSha, configSha))
            else
                match ReleasePolicy.parseBytes absoluteConfig configBytes with
                | Error error -> Error error
                | Ok policy ->
                    if policy.ComponentId <> componentId then
                        Error(IdentityMismatch("componentId", componentId, policy.ComponentId))
                    else
                        Ok(policy, absoluteConfig, lookupPath)

let private loadRoster (workspaceRoot: string) (rosterRelativePath: string) : Result<string * byte[], ReleaseError> =
    let absolute = Path.GetFullPath(Path.Combine(workspaceRoot, rosterRelativePath))
    match loadCandidateBytes absolute with
    | Error error -> Error error
    | Ok bytes -> Ok(absolute, bytes)

let private crossCheckReceipt
    (cli: PublishCli)
    (closure: ReleasePublisherClosure.PublisherClosure)
    (receipt: ReleaseHandoff.TrustedPublisherReceipt)
    : Result<unit, ReleaseError> =
    let mutable failure : ReleaseError option = None

    let tryCheck (label: string) (next: unit -> Result<unit, ReleaseError>) =
        match failure with
        | Some _ -> ()
        | None ->
            match next () with
            | Ok () -> ()
            | Error error ->
                failure <- Some(MalformedArtifact("publish-cli", label + ": " + ReleaseError.message error))

    let eventShaOk () =
        if isLowerHex40 cli.EventSha then Ok ()
        else
            let detail = "the publish CLI --event-sha '" + cli.EventSha + "' is not a 40-character lowercase hex SHA"
            Error(MalformedArtifact("publish-cli", detail))

    tryCheck "publisherClosure.repository" (fun () -> crossCheckStrings "publisherClosure.repository" closure.Repository cli.Repo)
    tryCheck "publisherClosure.rosterPath" (fun () -> crossCheckStrings "publisherClosure.rosterPath" closure.RosterPath receipt.RosterPath)
    tryCheck "trustedReceipt.repository" (fun () -> crossCheckStrings "trustedReceipt.repository" receipt.Repository cli.Repo)
    tryCheck "event-sha shape" eventShaOk
    tryCheck "trustedReceipt.eventRevision" (fun () -> crossCheckStrings "trustedReceipt.eventRevision" receipt.EventRevision cli.EventSha)
    tryCheck "trustedReceipt.runId" (fun () -> crossCheckStrings "trustedReceipt.runId" receipt.RunId cli.RunId)
    tryCheck "trustedReceipt.attempt" (fun () -> crossCheckStrings "trustedReceipt.attempt" receipt.Attempt cli.Attempt)
    tryCheck "trustedReceipt.role" (fun () -> crossCheckStrings "trustedReceipt.role" receipt.Role "publisher")
    tryCheck "trustedReceipt.builderSdk" (fun () -> crossCheckStrings "trustedReceipt.builderSdk" receipt.BuilderSdk cli.BuilderSdk)
    tryCheck "trustedReceipt.builderRuntime" (fun () -> crossCheckStrings "trustedReceipt.builderRuntime" receipt.BuilderRuntime cli.BuilderRuntime)
    tryCheck "trustedReceipt.builderPlatform" (fun () -> crossCheckStrings "trustedReceipt.builderPlatform" receipt.BuilderPlatform cli.BuilderPlatform)

    match failure with
    | Some error -> Error error
    | None -> Ok ()

let private crossCheckBootstrapReceipt
    (cli: PublishCli)
    (bootstrap: BootstrapReceipt)
    : Result<unit, ReleaseError> =
    let mutable failure : ReleaseError option = None
    let tryCheck (label: string) (next: unit -> Result<unit, ReleaseError>) =
        match failure with
        | Some _ -> ()
        | None ->
            match next () with
            | Ok () -> ()
            | Error error ->
                failure <- Some(MalformedArtifact("publish-cli", label + ": " + ReleaseError.message error))

    tryCheck "bootstrap.repository" (fun () -> crossCheckStrings "bootstrap.repository" bootstrap.Repository cli.Repo)
    tryCheck "bootstrap.runId" (fun () -> crossCheckStrings "bootstrap.runId" bootstrap.RunId cli.RunId)
    tryCheck "bootstrap.runAttempt" (fun () -> crossCheckStrings "bootstrap.runAttempt" bootstrap.RunAttempt cli.Attempt)
    tryCheck "bootstrap.eventSha" (fun () -> crossCheckStrings "bootstrap.eventSha" bootstrap.EventSha cli.EventSha)
    tryCheck "bootstrap.role" (fun () -> crossCheckStrings "bootstrap.role" bootstrap.Role cli.Role)
    tryCheck "bootstrap.component" (fun () ->
        match bootstrap.Component with
        | Some v when v = cli.ProducerComponent -> Ok ()
        | Some v -> Error(IdentityMismatch("bootstrap.component", cli.ProducerComponent, v))
        | None -> Error(MalformedArtifact("publish-cli", "the bootstrap receipt declares no component for the candidate role; the publisher refuses to publish (a null component cannot match the CLI --component and would silently bypass producer identity)")))
    tryCheck "bootstrap.artifact.id" (fun () -> crossCheckStrings "bootstrap.artifact.id" bootstrap.ArtifactId cli.ArtifactId)
    tryCheck "bootstrap.artifact.name" (fun () -> crossCheckStrings "bootstrap.artifact.name" bootstrap.ArtifactName cli.ArtifactName)
    tryCheck "bootstrap.download.observedSha256" (fun () -> crossCheckStrings "bootstrap.download.observedSha256" bootstrap.DownloadObservedSha256 cli.TransportSha256)

    match failure with
    | Some error -> Error error
    | None -> Ok ()

let private verifyBootstrapUploaderJob
    (bootstrap: BootstrapReceipt)
    : Result<unit, ReleaseError> =
    if bootstrap.UploaderJobConclusion <> "success" then
        let detail = "the bootstrap receipt uploaderJob.conclusion is '" + bootstrap.UploaderJobConclusion + "', not 'success'; the platform did not observe a successful upload for this artifact and the publisher refuses to publish"
        Error(MalformedArtifact("publish", detail))
    elif String.IsNullOrWhiteSpace bootstrap.UploaderJobId then
        Error(MalformedArtifact("publish", "the bootstrap receipt uploaderJob.id is empty; the platform did not observe an uploader job for this artifact"))
    elif not (System.Text.RegularExpressions.Regex.IsMatch(bootstrap.UploaderJobId, "^[0-9]+$")) then
        Error(MalformedArtifact("publish", $"the bootstrap receipt uploaderJob.id '{bootstrap.UploaderJobId}' is not a numeric platform job id"))
    else
        Ok ()

let private buildEvidence
    (cli: PublishCli)
    (bootstrap: BootstrapReceipt)
    (candidateArchiveName: string)
    (outerDigestValue: string)
    (outerLength: int64)
    (outerBytes: byte[])
    : ReleaseHandoff.ArtifactEvidence =
    let expected : ReleaseHandoff.ExpectedMetadata =
        { ArtifactId = cli.ArtifactId
          ArtifactName = cli.ArtifactName
          Role = cli.Role
          Component = cli.ProducerComponent
          Repository = cli.Repo
          RunId = cli.RunId
          Attempt = cli.Attempt
          Sha = cli.EventSha }
    let observedComponent =
        match bootstrap.Component with
        | Some v -> v
        | None -> failwithf "internal invariant violated: the candidate-role bootstrap receipt must carry a non-null component (verified earlier; crossCheckBootstrapReceipt must run before buildEvidence)"
    let observed : ReleaseHandoff.ObservedPlatformMetadata =
        { ArtifactId = bootstrap.ArtifactId
          ArtifactName = bootstrap.ArtifactName
          Role = bootstrap.Role
          Component = observedComponent
          Repository = bootstrap.Repository
          RunId = bootstrap.RunId
          Attempt = bootstrap.RunAttempt
          Sha = bootstrap.EventSha }
    { Expected = expected
      Observed = observed
      ActualOuterDigest =
        match ReleaseHandoff.makeOuterDigest outerDigestValue with
        | Ok v -> v
        | Error error -> failwithf "the publisher observed outer digest is invalid: %s" (ReleaseError.message error)
      TransportSourcePath = candidateArchiveName
      TransportLength = outerLength
      RawTransportBytes = outerBytes }

let private protectedDownloadDirectory
    (cli: PublishCli)
    : Result<string, ReleaseError> =
    try
        let canonicalPath (path: string) = Path.GetFullPath path
        let candidatePath = canonicalPath cli.CandidateTransportPath
        let candidateParent = Path.GetDirectoryName candidatePath
        let downloadDirectory = canonicalPath (Path.Combine(candidateParent, "downloads"))
        let workspace = canonicalPath cli.WorkspaceRoot
        let trustedReceipt = canonicalPath cli.TrustedReceiptPath
        let transportEvidence = canonicalPath cli.TransportEvidence
        let separator = string Path.DirectorySeparatorChar
        let overlaps (left: string) (right: string) =
            let left = left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            let right = right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            let contains (inner: string) (outer: string) =
                String.Equals(inner, outer, StringComparison.OrdinalIgnoreCase)
                || inner.StartsWith(outer + separator, StringComparison.OrdinalIgnoreCase)
            contains left right || contains right left

        if [ workspace; trustedReceipt; transportEvidence ]
           |> List.exists (overlaps downloadDirectory) then
            Error(
                MalformedArtifact(
                    "publish-cache",
                    "the publisher download cache overlaps the trusted workspace, trusted receipt, or transport evidence"))
        elif File.Exists downloadDirectory then
            Error(MalformedArtifact("publish-cache", "the publisher download cache path is an existing file"))
        else
            let mutable current = DirectoryInfo downloadDirectory
            let mutable unsafeAncestor = false
            while not (isNull current) && not unsafeAncestor do
                if current.Exists && (current.Attributes &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0 then
                    unsafeAncestor <- true
                current <- current.Parent

            if unsafeAncestor then
                Error(MalformedArtifact("publish-cache", "the publisher download cache has a reparse-point ancestor"))
            else
                Ok downloadDirectory
    with
    | :? IOException as error -> Error(FileFailure("resolve-cache", cli.CandidateTransportPath, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("resolve-cache", cli.CandidateTransportPath, error.Message))
    | :? ArgumentException as error -> Error(FileFailure("resolve-cache", cli.CandidateTransportPath, error.Message))
    | :? NotSupportedException as error -> Error(FileFailure("resolve-cache", cli.CandidateTransportPath, error.Message))

let private runPublishPipelineWithTransportFactory
    (createTransport: unit -> ReleasePublish.HttpTransport)
    (cli: PublishCli)
    (policy: ReleasePolicy.PolicyProjection)
    (snapshot: ReleaseHandoff.CandidateArchiveSnapshot)
    (candidateArchiveName: string)
    (metadataLocalPath: string)
    (repositoryOwner: string)
    (repositoryName: string)
    : Async<int> =
    async {
        match! ReleaseEvaluate.VerifiedCandidate.createFromSnapshot
                    snapshot policy candidateArchiveName cli.EventSha metadataLocalPath repositoryOwner repositoryName with
        | Error error ->
            emitPreContractFailure (ReleaseError.message error)
            return 4
        | Ok verified ->
                let locator : ReleasePublish.ReleaseLocator =
                    { Owner = repositoryOwner
                      Repository = repositoryName
                      Tag = policy.ComponentId }

                // Require both independent publication authorities before creating transport or filesystem state.
                if Environment.GetEnvironmentVariable "MCP_RELEASE_DO_UPLOAD" <> "true" then
                    let error =
                        FileFailure(
                            "auth",
                            "MCP_RELEASE_DO_UPLOAD",
                            "publication requires the exact value 'true'; local-mirror and implicit modes are not supported")
                    emitPreContractFailure (ReleaseError.message error)
                    return 4
                else
                    match ReleasePublish.environmentToken () with
                    | Error error ->
                        emitPreContractFailure (ReleaseError.message error)
                        return 4
                    | Ok auth ->
                        match protectedDownloadDirectory cli with
                        | Error error ->
                            emitPreContractFailure (ReleaseError.message error)
                            return 4
                        | Ok downloadDir ->
                            // Keep transport construction and cache creation behind path and authority checks.
                            let transport = createTransport ()
                            if not (Directory.Exists downloadDir) then
                                Directory.CreateDirectory downloadDir |> ignore

                            let! result =
                                ReleaseTransaction.run
                                    transport
                                    (Some auth)
                                    true
                                    downloadDir
                                    locator
                                    verified
                            match result with
                            | Error error ->
                                emitPreContractFailure (ReleaseError.message error)
                                return 4
                            | Ok (resolution, exitCode) ->
                                emitResolution resolution
                                return exitCode
    }

let private runPublishPipeline
    (cli: PublishCli)
    (policy: ReleasePolicy.PolicyProjection)
    (snapshot: ReleaseHandoff.CandidateArchiveSnapshot)
    (candidateArchiveName: string)
    (metadataLocalPath: string)
    (repositoryOwner: string)
    (repositoryName: string)
    : Async<int> =
    runPublishPipelineWithTransportFactory
        (fun () -> ReleasePublish.defaultHttpTransport ())
        cli
        policy
        snapshot
        candidateArchiveName
        metadataLocalPath
        repositoryOwner
        repositoryName

type private VerifiedWorkspace =
    { Cli: PublishCli
      Closure: ReleasePublisherClosure.PublisherClosure
      Receipt: ReleaseHandoff.TrustedPublisherReceipt
      Bootstrap: BootstrapReceipt
      Policy: ReleasePolicy.PolicyProjection
      CandidateRoot: string
      CandidateArchiveName: string
      OuterBytes: byte[]
      OuterDigestValue: string
      Snapshot: ReleaseHandoff.CandidateArchiveSnapshot
      MetadataLocalPath: string
      Owner: string
      Name: string }

let private loadClosure
    (cli: PublishCli)
    : Result<ReleasePublisherClosure.PublisherClosure, ReleaseError> =
    match ReleasePublisherClosure.readFromFile cli.ClosurePath with
    | Error error -> Error error
    | Ok (closure, _) -> Ok closure

let private splitCanonical (cli: PublishCli) : Result<string * string, ReleaseError> =
    match splitRepo cli.Repo with
    | None ->
        let detail = "the publish CLI --repo '" + cli.Repo + "' is not in '<owner>/<name>' form"
        Error(MalformedArtifact("publish-cli", detail))
    | Some (cliOwner, cliName) ->
        let canonicalOwner, canonicalName = canonicalRepository ()
        if cliOwner <> canonicalOwner || cliName <> canonicalName then
            let catalog = canonicalOwner + "/" + canonicalName
            Error(IdentityMismatch("catalog", catalog, cli.Repo))
        else
            Ok(cliOwner, cliName)

let private loadTrustedReceipt
    (cli: PublishCli)
    (candidateRoot: string)
    : Result<ReleaseHandoff.TrustedPublisherReceipt, ReleaseError> =
    match isolateTrustedReceiptPath cli.TrustedReceiptPath candidateRoot cli.TransportEvidence with
    | Error error -> Error error
    | Ok canonicalPath ->
        match loadReceiptText canonicalPath with
        | Error error -> Error error
        | Ok text -> ReleaseHandoff.parseTrustedPublisherReceipt text canonicalPath

let private loadReceipts
    (cli: PublishCli)
    (candidateRoot: string)
    : Result<ReleaseHandoff.TrustedPublisherReceipt, ReleaseError> =
    match loadTrustedReceipt cli candidateRoot with
    | Error error -> Error error
    | Ok receipt -> Ok receipt

let private loadAndVerifyBootstrapReceipt
    (cli: PublishCli)
    (candidateRoot: string)
    : Result<BootstrapReceipt, ReleaseError> =
    match isolateBootstrapEvidencePath cli.TransportEvidence candidateRoot cli.WorkspaceRoot with
    | Error error -> Error error
    | Ok isolatedPath ->
        match loadBootstrapReceipt isolatedPath with
        | Error error -> Error error
        | Ok receipt ->
            match verifyBootstrapUploaderJob receipt with
            | Error error -> Error error
            | Ok () ->
                match crossCheckBootstrapReceipt cli receipt with
                | Error error -> Error error
                | Ok () -> Ok receipt

let private verifyRosterHash
    (cli: PublishCli)
    (closure: ReleasePublisherClosure.PublisherClosure)
    (receipt: ReleaseHandoff.TrustedPublisherReceipt)
    : Result<unit, ReleaseError> =
    match loadRoster cli.WorkspaceRoot closure.RosterPath with
    | Error error -> Error error
    | Ok (_, rosterBytes) ->
        let rosterSha = hashBytes rosterBytes
        if rosterSha <> receipt.RosterSha256.Value then
            Error(IdentityMismatch("trustedReceipt.rosterSha256", receipt.RosterSha256.Value, rosterSha))
        else
            Ok ()

let private verifyOuterTransport
    (candidateTransportPath: string)
    (bootstrap: BootstrapReceipt)
    (cli: PublishCli)
    : Result<byte[] * string, ReleaseError> =
    try
        let info = FileInfo candidateTransportPath
        if not info.Exists then Error(MissingArtifact candidateTransportPath)
        elif info.Length > ReleaseHandoff.outerTransportMaxBytes then
            Error(MalformedArtifact(candidateTransportPath, $"the raw candidate transport exceeds the {ReleaseHandoff.outerTransportMaxBytes}-byte bounded read"))
        elif not (isLowerHex64 cli.TransportSha256) then
            let detail = "the publish CLI --transport-sha256 '" + cli.TransportSha256 + "' is not a 64-character lowercase hex SHA-256"
            Error(MalformedArtifact("publish-cli", detail))
        else
            use input = new FileStream(candidateTransportPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 81920
            let mutable total = 0L
            let mutable finished = false
            let mutable oversized = false
            while not finished && not oversized do
                let read = input.Read(buffer, 0, buffer.Length)
                if read = 0 then finished <- true
                else
                    total <- total + int64 read
                    if total > ReleaseHandoff.outerTransportMaxBytes then oversized <- true
                    else output.Write(buffer, 0, read)
            if oversized then
                Error(MalformedArtifact(candidateTransportPath, $"the raw candidate transport exceeds the {ReleaseHandoff.outerTransportMaxBytes}-byte bounded read"))
            else
                let outerBytes = output.ToArray()
                let observedDigest = hashBytes outerBytes
                let observedSize = int64 outerBytes.Length
                let sizeChecks =
                    [ crossCheckStrings "bootstrap.artifact.sizeInBytes" (string bootstrap.ArtifactSizeInBytes) (string observedSize)
                      crossCheckStrings "bootstrap.download.bytesRead" (string bootstrap.DownloadBytesRead) (string observedSize) ]
                let digestChecks =
                    [ crossCheckStrings "transport-sha256" cli.TransportSha256 observedDigest
                      crossCheckStrings "bootstrap.download.observedSha256" bootstrap.DownloadObservedSha256 observedDigest
                      crossCheckStrings "bootstrap.artifact.apiDigest" bootstrap.ArtifactApiDigest observedDigest
                      crossCheckStrings "bootstrap.artifact.platformDigest" bootstrap.ArtifactPlatformDigest observedDigest ]
                match List.tryPick (function Error error -> Some error | Ok () -> None) (sizeChecks @ digestChecks) with
                | Some error -> Error error
                | None ->
                    Ok(outerBytes, observedDigest)
    with
    | :? IOException as error -> Error(FileFailure("read", candidateTransportPath, error.Message))
    | :? UnauthorizedAccessException as error -> Error(FileFailure("read", candidateTransportPath, error.Message))

let private verifyCommittedHead
    (cli: PublishCli)
    : Async<Result<unit, ReleaseError>> =
    async {
        match! BuildProvenance.committedHead cli.WorkspaceRoot with
        | Error error -> return Error error
        | Ok committedHead ->
            if committedHead <> cli.EventSha then
                return Error(IdentityMismatch("workspaceHead", cli.EventSha, committedHead))
            else
                return Ok ()
    }

let private readSnapshot
    (policy: ReleasePolicy.PolicyProjection)
    (cli: PublishCli)
    (bootstrap: BootstrapReceipt)
    (candidateArchiveName: string)
    (outerBytes: byte[])
    (outerDigestValue: string)
    : Result<ReleaseHandoff.CandidateArchiveSnapshot, ReleaseError> =
    let evidence = buildEvidence cli bootstrap cli.CandidateTransportPath outerDigestValue (int64 outerBytes.Length) outerBytes
    ReleaseHandoff.readRawTransport cli.CandidateTransportPath outerBytes evidence policy

let private verifyWorkspace (cli: PublishCli) : Async<Result<VerifiedWorkspace, ReleaseError>> =
    async {
        match loadClosure cli with
        | Error error -> return Error error
        | Ok closure ->
            match splitCanonical cli with
            | Error error -> return Error error
            | Ok (cliOwner, cliName) ->
                match boundedTransportPath cli.CandidateTransportPath with
                | Error error -> return Error error
                | Ok (candidateTransportPath, candidateRoot) ->
                    let cli = { cli with CandidateTransportPath = candidateTransportPath }
                    match loadAndVerifyBootstrapReceipt cli candidateRoot with
                    | Error error -> return Error error
                    | Ok bootstrap ->
                        match loadReceipts cli candidateRoot with
                        | Error error -> return Error error
                        | Ok receipt ->
                            match crossCheckReceipt cli closure receipt with
                            | Error error -> return Error error
                            | Ok () ->
                                match verifyRosterHash cli closure receipt with
                                | Error error -> return Error error
                                | Ok () ->
                                    match reconcilePolicyWithReceipt cli.ProducerComponent cli.WorkspaceRoot receipt with
                                    | Error error -> return Error error
                                    | Ok (policy, _, _) ->
                                        match verifyOuterTransport candidateTransportPath bootstrap cli with
                                        | Error error -> return Error error
                                        | Ok (outerBytes, outerDigestValue) ->
                                            match! verifyCommittedHead cli with
                                            | Error error -> return Error error
                                            | Ok () ->
                                                let candidateArchiveName = policy.ArchiveName
                                                match readSnapshot policy cli bootstrap candidateArchiveName outerBytes outerDigestValue with
                                                | Error error -> return Error error
                                                | Ok snapshot ->
                                                    let metadataAssetName = ReleaseMetadata.metadataAssetName policy.ComponentId policy.Version
                                                    let metadataLocalPath =
                                                        Path.Combine
                                                            (cli.WorkspaceRoot,
                                                             policy.ComponentId,
                                                             "dist",
                                                             policy.ComponentId,
                                                             metadataAssetName)
                                                    return Ok
                                                                { Cli = cli
                                                                  Closure = closure
                                                                  Receipt = receipt
                                                                  Bootstrap = bootstrap
                                                                  Policy = policy
                                                                  CandidateRoot = candidateRoot
                                                                  CandidateArchiveName = candidateArchiveName
                                                                  OuterBytes = outerBytes
                                                                  OuterDigestValue = outerDigestValue
                                                                  Snapshot = snapshot
                                                                  MetadataLocalPath = metadataLocalPath
                                                                  Owner = cliOwner
                                                                  Name = cliName }
    }

let private runPublishMode (argv: string[]) : Async<int> =
    async {
        if argv.Length < 3 then
            eprintfn "usage: ReleaseRunner publish <publisher-closure-path> <candidate-transport.zip> --repo=<owner>/<name> --run-id=<id> --attempt=<n> --event-sha=<40-hex> --role=<candidate> --component=<dotnet|workflow> --artifact-id=<id> --artifact-name=<name> --transport-sha256=<actual-64-hex> --builder-sdk=<x> --builder-runtime=<y> --builder-platform=<z> --workspace-root=<source-root> --trusted-receipt=<builder-emitted-receipt.json> --transport-evidence=<fetchcandidate-bootstrap-receipt.json> [--trigger=<event>]"
            return 2
        else
            match parsePublishCli argv with
            | Error error ->
                emitPreContractFailure (ReleaseError.message error)
                return 4
            | Ok cli ->
                match! verifyWorkspace cli with
                | Error error ->
                    emitPreContractFailure (ReleaseError.message error)
                    return 4
                | Ok verified ->
                    return!
                        runPublishPipeline
                            verified.Cli
                            verified.Policy
                            verified.Snapshot
                            verified.CandidateArchiveName
                            verified.MetadataLocalPath
                            verified.Owner
                            verified.Name
    }

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        eprintfn "usage: ReleaseRunner prepare <config-path> <candidate-directory>"
        eprintfn "usage: ReleaseRunner publish <publisher-closure-path> <candidate-transport.zip> --repo=<owner>/<name> --run-id=<id> --attempt=<n> --event-sha=<40-hex> --role=<candidate> --component=<dotnet|workflow> --artifact-id=<id> --artifact-name=<name> --transport-sha256=<actual-64-hex> --builder-sdk=<x> --builder-runtime=<y> --builder-platform=<z> --workspace-root=<source-root> --trusted-receipt=<builder-emitted-receipt.json> --transport-evidence=<fetchcandidate-bootstrap-receipt.json> [--trigger=<event>]"
        2
    elif argv.[0] = "prepare" then
        // the compiled CLI must finish its asynchronous operation before exiting
        runPrepareMode argv |> Async.RunSynchronously
    elif argv.[0] = "publish" then
        runPublishMode argv |> Async.RunSynchronously
    else
        eprintfn "usage: ReleaseRunner prepare <config-path> <candidate-directory>"
        eprintfn "usage: ReleaseRunner publish <publisher-closure-path> <candidate-transport.zip> --repo=<owner>/<name> --run-id=<id> --attempt=<n> --event-sha=<40-hex> --role=<candidate> --component=<dotnet|workflow> --artifact-id=<id> --artifact-name=<name> --transport-sha256=<actual-64-hex> --builder-sdk=<x> --builder-runtime=<y> --builder-platform=<z> --workspace-root=<source-root> --trusted-receipt=<builder-emitted-receipt.json> --transport-evidence=<fetchcandidate-bootstrap-receipt.json> [--trigger=<event>]"
        2
