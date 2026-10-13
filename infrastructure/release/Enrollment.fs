// role: reusable module/helper
// Single authoritative enrollment roster for the release transaction; rejects any drift between the roster and a per-component projection.
// Versions, SDKs, allowlists, and URIs stay in each producer's canonical Config.fsx; this module never mirrors them.
module ReleaseEnrollment

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open BuildProvenance

// One roster entry: only the identity needed to look up the canonical Config.fsx. No policy values.
type RosterEntry =
    { ComponentId: string
      ConfigPath: string }

type Roster =
    { Entries: RosterEntry list
      SourcePath: string }

// Any field beyond componentId/configPath would turn the roster into a policy mirror and is rejected at load time.
let private forbiddenFields =
    [ "version"
      "sdkVersion"
      "runtimeVersion"
      "platform"
      "archiveName"
      "assetUri"
      "entryAllowlist"
      "publishedFiles"
      "entryDll" ]

// Mandatory AC-06: the current release transaction supports exactly Dotnet and Workflow. The supported set is an explicit validation constraint, not a roster mirror, and is not versioned, mirrored, or duplicated into the roster file. A roster that contains a different componentId (or omits one) is rejected as drift.
let supportedComponentIds : Set<string> =
    Set.ofList [ "dotnet"; "workflow" ]

// Human-readable form for diagnostics.
let private supportedComponentIdsList : string =
    String.concat ", " (Set.toList supportedComponentIds)

let rosterPath (root: string) : string =
    Path.Combine(root, "infrastructure", "release", "Participants.json")

let private requireStringField (obj: JsonObject) (field: string) (sourcePath: string) : Result<string, ReleaseError> =
    if not (obj.ContainsKey field) then
        Error(MalformedArtifact(sourcePath, $"the enrollment roster entry is missing required field '{field}'"))
    else
        let node = obj.[field]

        if isNull node then
            Error(MalformedArtifact(sourcePath, $"the enrollment roster entry field '{field}' is null"))
        else
            try
                Ok(node.GetValue<string>())
            with _ ->
                Error(MalformedArtifact(sourcePath, $"the enrollment roster entry field '{field}' is not a string"))

let private parseRoster (text: string) (sourcePath: string) : Result<Roster, ReleaseError> =
    let doc =
        try
            JsonNode.Parse text
        with _ ->
            JsonValue.Create null

    if isNull doc then
        Error(MalformedArtifact(sourcePath, "the enrollment roster is not valid JSON"))
    else
        let rootObj = doc.AsObject()

        if not (rootObj.ContainsKey "participants") then
            Error(MalformedArtifact(sourcePath, "the enrollment roster is missing the 'participants' array"))
        else
            let participantsNode = rootObj.["participants"]

            if isNull participantsNode then
                Error(MalformedArtifact(sourcePath, "the enrollment roster 'participants' field is null"))
            else
                let array =
                    try
                        participantsNode.AsArray()
                    with _ ->
                        JsonArray()

                let mutable failure : ReleaseError option = None
                let mutable entries : RosterEntry list = []
                let mutable seenIds : Set<string> = Set.empty
                let mutable index = 0

                for node in array do
                    if failure.IsNone then
                        index <- index + 1

                        if isNull node then
                            failure <- Some(MalformedArtifact(sourcePath, $"the enrollment roster entry at index {index - 1} is null"))
                        else
                            let entry = node.AsObject()
                            let forbidden = forbiddenFields |> List.tryFind (fun f -> entry.ContainsKey f)

                            match forbidden with
                            | Some field ->
                                failure <- Some(MalformedArtifact(sourcePath, $"the enrollment roster entry at index {index - 1} has forbidden field '{field}'; versions, SDKs, allowlists, and URIs must stay in each producer's Config.fsx"))
                            | None ->
                                match requireStringField entry "componentId" sourcePath, requireStringField entry "configPath" sourcePath with
                                | Error error, _ -> failure <- Some error
                                | _, Error error -> failure <- Some error
                                | Ok componentId, Ok configPath ->
                                    if String.IsNullOrWhiteSpace componentId then
                                        failure <- Some(MalformedArtifact(sourcePath, $"the enrollment roster entry at index {index - 1} has an empty componentId"))
                                    elif String.IsNullOrWhiteSpace configPath then
                                        failure <- Some(MalformedArtifact(sourcePath, $"the enrollment roster entry at index {index - 1} has an empty configPath"))
                                    elif seenIds.Contains componentId then
                                        failure <- Some(MalformedArtifact(sourcePath, $"the enrollment roster has a duplicate componentId '{componentId}'"))
                                    elif not (Set.contains componentId supportedComponentIds) then
                                        failure <- Some(MalformedArtifact(sourcePath, $"the enrollment roster entry at index {index - 1} has unsupported componentId '{componentId}'; the mandatory supported set is [{supportedComponentIdsList}]"))
                                    else
                                        seenIds <- seenIds.Add componentId
                                        entries <- { ComponentId = componentId; ConfigPath = configPath } :: entries

                match failure with
                | Some error -> Error error
                | None -> Ok { Entries = entries |> List.rev; SourcePath = sourcePath }

// Absolute-path form: cross-checks the projected config path against the roster's config path so a moved Config.fsx cannot silently route to the wrong component.
let private normalizePath (root: string) (path: string) : string =
    let full =
        if Path.IsPathRooted path then
            path
        else
            Path.Combine(root, path)
    Path.GetFullPath full

// Reject any roster that resolves two entries to the same canonical configPath; without this, `validateConfigPathAgainstRoster` could never guarantee an exact-one match, and a CLI configPath could mask which component it routes to.
let private validateCanonicalConfigPaths (root: string) (roster: Roster) : Result<unit, ReleaseError> =
    let mutable seen : Set<string> = Set.empty
    let mutable failure : ReleaseError option = None

    for entry in roster.Entries do
        match failure with
        | None ->
            let resolved = normalizePath root entry.ConfigPath

            if seen.Contains resolved then
                failure <-
                    Some(
                        MalformedArtifact(
                            roster.SourcePath,
                            $"the enrollment roster entry for componentId '{entry.ComponentId}' resolves to the same canonical configPath '{resolved}' as another entry (path '{entry.ConfigPath}'); refusing to admit an ambiguous configPath projection so authorize-before-FSI can match exactly one"))
            else
                seen <- seen.Add resolved
        | Some _ -> ()

    match failure with
    | Some error -> Error error
    | None -> Ok()

let loadRoster (root: string) : Async<Result<Roster, ReleaseError>> =
    async {
        let path = rosterPath root

        if not (File.Exists path) then
            return Error(MissingArtifact path)
        else
            match! BuildProvenance.readAllTextAsync path with
            | Error error -> return Error error
            | Ok text ->
                match parseRoster text path with
                | Error error -> return Error error
                | Ok roster ->
                    match validateCanonicalConfigPaths root roster with
                    | Error error -> return Error error
                    | Ok () -> return Ok roster
    }

// Structural validation: non-empty, no duplicate componentIds, and the componentId set must match `supportedComponentIds` exactly (AC-06 mandatory scope). The loader already rejects duplicates and unsupported componentIds; this is a defense-in-depth check for callers that construct a Roster directly.
let validateRoster (roster: Roster) : Result<unit, ReleaseError> =
    if List.isEmpty roster.Entries then
        Error(MalformedArtifact(roster.SourcePath, "the enrollment roster is empty"))
    else
        let mutable seen : Set<string> = Set.empty
        let mutable failure : ReleaseError option = None

        for entry in roster.Entries do
            if failure.IsNone then
                if seen.Contains entry.ComponentId then
                    failure <- Some(MalformedArtifact(roster.SourcePath, $"the enrollment roster has a duplicate componentId '{entry.ComponentId}'"))
                else
                    seen <- seen.Add entry.ComponentId

        if failure.IsNone then
            let seenMatchesSupported = seen |> Set.forall (fun id -> Set.contains id supportedComponentIds)
            let supportedAllSeen = supportedComponentIds |> Set.forall (fun id -> Set.contains id seen)

            if not (seenMatchesSupported && supportedAllSeen) then
                let unexpected = seen |> Set.toList |> List.filter (fun id -> not (Set.contains id supportedComponentIds))
                let missing = supportedComponentIds |> Set.toList |> List.filter (fun id -> not (Set.contains id seen))

                let unsupportedText = String.concat ", " unexpected
                let missingText = String.concat ", " missing
                let detail =
                    [ if not (List.isEmpty unexpected) then yield "unsupported componentId(s): " + unsupportedText
                      if not (List.isEmpty missing) then yield "missing required componentId(s): " + missingText ]
                    |> String.concat "; "

                failure <- Some(MalformedArtifact(roster.SourcePath, "the enrollment roster componentId set does not match the mandatory supported set [" + supportedComponentIdsList + "]; " + detail))

        match failure with
        | Some error -> Error error
        | None -> Ok()

// Validates a per-component projection against the roster. Rejects: unsupported componentId, configID mismatch, path drift, omission.
let validateProjectionAgainstRoster
    (componentId: string)
    (projectedConfigPath: string)
    (root: string)
    (roster: Roster)
    : Result<unit, ReleaseError> =
    let projectedFull = normalizePath root projectedConfigPath
    let mutable failure : ReleaseError option = None
    let mutable matched = false

    for entry in roster.Entries do
        if not matched && failure.IsNone then
            let rosterFull = normalizePath root entry.ConfigPath

            if String.Equals(entry.ComponentId, componentId, StringComparison.Ordinal) then
                if not (String.Equals(rosterFull, projectedFull, StringComparison.Ordinal)) then
                    failure <- Some(MalformedArtifact(roster.SourcePath, $"participant '{componentId}' configPath drift: projected '{projectedConfigPath}' (resolved '{projectedFull}') vs roster '{entry.ConfigPath}' (resolved '{rosterFull}')"))
                else
                    matched <- true

    if failure.IsSome then
        Error failure.Value
    elif not matched then
        Error(MalformedArtifact(roster.SourcePath, $"unsupported componentId '{componentId}'; not present in the enrollment roster"))
    else
        Ok()

// Validates a contract componentId (transaction mode) against the roster; the contract carries no config path, so only the componentId is checked.
let validateComponentIdInRoster (componentId: string) (roster: Roster) : Result<unit, ReleaseError> =
    let mutable found = false

    for entry in roster.Entries do
        if not found && String.Equals(entry.ComponentId, componentId, StringComparison.Ordinal) then
            found <- true

    if found then
        Ok()
    else
        Error(MalformedArtifact(roster.SourcePath, $"unsupported componentId '{componentId}'; not present in the enrollment roster"))

// Path-only authorization: counts how many enrolled entries resolve to the same canonical configPath as the CLI path and rejects anything other than exactly one, before any Config.fsx evaluation runs. Run this once before `loadProducerConfig` so an unregistered or ambiguous file never reaches `dotnet fsi`.
let validateConfigPathAgainstRoster
    (configPath: string)
    (root: string)
    (roster: Roster)
    : Result<unit, ReleaseError> =
    let projectedFull = normalizePath root configPath
    let mutable matches : (string * string) list = []

    for entry in roster.Entries do
        let rosterFull = normalizePath root entry.ConfigPath

        if String.Equals(rosterFull, projectedFull, StringComparison.Ordinal) then
            matches <- (entry.ComponentId, entry.ConfigPath) :: matches

    match matches with
    | [ (componentId, _) ] -> Ok()
    | [] ->
        let known =
            roster.Entries
            |> List.map (fun e -> sprintf "'%s' (%s)" e.ComponentId e.ConfigPath)
            |> String.concat "; "

        Error(
            MalformedArtifact(
                roster.SourcePath,
                $"configPath '{configPath}' (resolved '{projectedFull}') is not registered in the enrollment roster; refusing to launch a child FSI process. Known entries: {known}"))
    | many ->
        let ambiguous =
            many
            |> List.rev
            |> List.map (fun (id, path) -> sprintf "'%s' (%s)" id path)
            |> String.concat "; "

        Error(
            MalformedArtifact(
                roster.SourcePath,
                $"configPath '{configPath}' (resolved '{projectedFull}') matches {List.length many} enrolled entries: {ambiguous}; the authorization must resolve to exactly one and the roster has duplicate canonical configPaths. Refusing to launch a child FSI process."))
