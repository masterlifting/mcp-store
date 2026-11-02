// role: test entrypoint
// Every authority denial publishes the structured `error.authority` block, while non-authority errors stay envelope-identical.
// The suite persists a task and reaches the producer-owned per-account integrity key, so it must run in an isolated child process with redirected per-account application data.
// The isolation guard loads before the domain sources so the key path is never captured outside that sandbox; end-to-end authorization over the native MCP boundary is covered by protocol/Run.fsx.

#load "../Isolation.fsx"
#load "../../domain/Computation.fs"
#load "../../domain/Integrity.fs"
#load "../../domain/Workflow.fs"

open System
open System.IO
open System.Text.Json.Nodes
open Workflow

let private failWith message = raise (System.Exception message)

let assertEqual name expected actual =
    if expected <> actual then
        failWith (sprintf "%s: expected %A, got %A" name expected actual)

let assertTrue name condition =
    if not condition then failWith (sprintf "%s: expected true" name)

let assertSome name value =
    match value with
    | Some _ -> ()
    | None -> failWith (sprintf "%s: expected Some, got None" name)

let assertNone name value =
    match value with
    | None -> ()
    | Some _ -> failWith (sprintf "%s: expected None, got Some" name)

let evidenceKind value =
    match parseEvidenceKind value with
    | Ok kind -> kind
    | Error error -> failWith (sprintf "invalid evidence-kind test fixture '%s': %s" value (renderError error))

let expectOk name (result: Async<Result<'a, RuntimeError>>) : Async<'a> =
    async {
        match! result with
        | Ok value -> return value
        | Error error -> return failwithf "%s: expected Ok, got Error %s" name (renderError error)
    }

let tempRoot =
    Path.Combine(
        Path.GetTempPath(),
        "mcp-workflow-authority-metadata",
        Guid.NewGuid().ToString("N")
    )

Directory.CreateDirectory tempRoot |> ignore

// Standalone entry bridge: the only synchronous wait in this script's flow;
// the entire suite composes asynchronously above.
async {
    try
        let request =
            { Id = "AUTH-1"
              Title = "Workflow authority metadata contract"
              Kind = Execution
              AcceptanceCriteria = [ ("AC19", "Structured authority remediation") ]
              WorkItems =
                  [ { Id = "W1"
                      Title = "Exercise authority contract"
                      DependsOn = []
                      Children = [] } ] }

        let! task = expectOk "createTask" (createTask tempRoot request)
        let! profiles = expectOk "resolveProfiles" (resolveProfiles tempRoot)

        let now = DateTimeOffset.UtcNow

        // Case 1: end-to-end via applyTask, waiving a User-waivable guard without any DecisionRef, which
        // cannot be authorized by an ordinary apply and must publish the structured authority metadata.
        let guardSpec : GuardSpec =
            { Id = "G1"
              Target = GuardTarget.TaskTarget
              Checkpoint = GuardCheckpoint.BeforeStart
              Requirement = EvidenceRequired { Kind = evidenceKind "review"; MinimumCount = 1; ProducerRole = None; RequireIndependentProducer = false }
              Applicability = ApplicabilityPolicy.Always
              Waiver = WaiverPolicy.WaivableBy MinimumAuthority.UserAuthority }

        let! afterGuard =
            expectOk
                "addGuard"
                (applyTask tempRoot task.Id task.StateRevision (TaskCommand.AddGuard guardSpec))

        let! waiveResult =
            applyTask
                tempRoot
                afterGuard.Id
                afterGuard.StateRevision
                (TaskCommand.WaiveGuard ("G1", None))

        match waiveResult with
        | Error (AuthorityDenied (metadata, _)) ->
            assertEqual "Case 1 RequiredAuthority"
                MinimumAuthority.UserAuthority
                metadata.RequiredAuthority
            assertEqual "Case 1 Operation" "task_apply.waive-guard" metadata.Operation
            assertEqual "Case 1 DecisionRefStatus"
                DecisionRefStatus.Absent
                metadata.DecisionRefStatus
            match metadata.DecisionKind with
            | Some kind ->
                assertEqual "Case 1 DecisionKind value" WaiverDecision kind
            | None -> failWith "Case 1 DecisionKind expected Some"
            match metadata.Target with
            | Some target ->
                assertEqual "Case 1 Target value" (GuardDispositionTarget "G1") target
            | None -> failWith "Case 1 Target expected Some"
        | Error other ->
            failWith (sprintf "Case 1: expected AuthorityDenied, got %s" (renderError other))
        | Ok _ ->
            failWith "Case 1: expected authority rejection"

        // Case 1b: an explicit unknown DecisionRef is rejected as a not-found decision, so it carries no
        // authority metadata and must leave the persisted task bytes, revision, and decisions unchanged.
        let sidecarPath =
            Path.Combine(Path.GetFullPath tempRoot, ".tasks", afterGuard.Id, "runtime.json")

        if not (File.Exists sidecarPath) then
            failWith (sprintf "Case 1b: persisted sidecar is missing at %s" sidecarPath)

        let persistedBefore =
            JsonNode.Parse(File.ReadAllText sidecarPath) :?> JsonObject

        let sidecarBytesBefore = File.ReadAllBytes sidecarPath

        let! unknownReferenceResult =
            applyTask
                tempRoot
                afterGuard.Id
                afterGuard.StateRevision
                (TaskCommand.WaiveGuard ("G1", Some (DecisionRef "D999")))

        let unknownReferenceError =
            match unknownReferenceResult with
            | Error error -> error
            | Ok _ -> failWith "Case 1b: expected the unknown decision reference to be rejected"

        // The domain reports the unknown reference as NotFound, and the ordinary apply envelope carries
        // every non-authority rejection as InvalidInput with the same canonical reason.
        match unknownReferenceError with
        | InvalidInput message ->
            assertEqual "Case 1b unknown reference message" "Decision 'D999' was not found" message
        | other ->
            failWith (sprintf "Case 1b: expected InvalidInput, got %s" (renderError other))

        assertNone
            "Case 1b unknown reference carries no authority metadata"
            (tryAuthorityMetadata unknownReferenceError)

        let sidecarBytesAfter = File.ReadAllBytes sidecarPath

        assertEqual "Case 1b persisted bytes unchanged" sidecarBytesBefore sidecarBytesAfter

        let persistedAfter =
            JsonNode.Parse(File.ReadAllText sidecarPath) :?> JsonObject

        assertEqual
            "Case 1b persisted revision unchanged"
            afterGuard.StateRevision
            (persistedAfter.["stateRevision"].GetValue<int>())

        assertEqual
            "Case 1b persisted revision matches the pre-rejection revision"
            (persistedBefore.["stateRevision"].GetValue<int>())
            (persistedAfter.["stateRevision"].GetValue<int>())

        assertEqual
            "Case 1b persisted decision count unchanged"
            (persistedBefore.["decisions"].AsArray().Count)
            (persistedAfter.["decisions"].AsArray().Count)

        // Case 2: tryAuthorityMetadata round-trip
        let case2Error =
            AuthorityDenied
                ({ RequiredAuthority = MinimumAuthority.UserAuthority
                   Operation = "task_apply.waive-guard"
                   DecisionKind = Some WaiverDecision
                   Target = Some (GuardDispositionTarget "G1")
                   DecisionRefStatus = DecisionRefStatus.Absent },
                 "test message")

        let extracted = tryAuthorityMetadata case2Error
        assertSome "Case 2 tryAuthorityMetadata returns Some" extracted

        let renderedObj =
            renderAuthorityMetadata (extracted |> (fun o -> Option.get o))
            |> (fun n -> n.AsObject())

        assertTrue "Case 2 rendered has required key" (renderedObj.ContainsKey "required")
        assertTrue "Case 2 rendered has operation key" (renderedObj.ContainsKey "operation")
        assertTrue "Case 2 rendered has decisionKind key" (renderedObj.ContainsKey "decisionKind")
        assertTrue "Case 2 rendered has target key" (renderedObj.ContainsKey "target")
        assertTrue "Case 2 rendered has decisionRefStatus key"
            (renderedObj.ContainsKey "decisionRefStatus")

        assertEqual
            "Case 2 required wire"
            "user"
            (renderedObj.["required"] |> (fun n -> n.ToString()))
        assertEqual
            "Case 2 operation wire"
            "task_apply.waive-guard"
            (renderedObj.["operation"] |> (fun n -> n.ToString()))
        assertEqual
            "Case 2 decisionRefStatus wire"
            "absent"
            (renderedObj.["decisionRefStatus"] |> (fun n -> n.ToString()))

        // Case 3: non-authority errors must NOT carry authority metadata
        let parseErr = InvalidInput "deliberate non-authority error"
        assertNone "Case 3 non-authority error yields None" (tryAuthorityMetadata parseErr)

        let persistErr = PersistenceFailure "deliberate non-authority persistence error"
        assertNone "Case 3b non-authority persistence error yields None" (tryAuthorityMetadata persistErr)

        // Case 4: renderAuthorityMetadata handles None fields explicitly
        let rendered4Obj =
            renderAuthorityMetadata
                { RequiredAuthority = MinimumAuthority.CoordinatorAuthority
                  Operation = "task_apply.add-decision"
                  DecisionKind = None
                  Target = None
                  DecisionRefStatus = DecisionRefStatus.Absent }
            |> (fun n -> n.AsObject())

        let decisionKindNode = rendered4Obj.["decisionKind"]
        let targetNode = rendered4Obj.["target"]
        let requiredNode = rendered4Obj.["required"]

        assertTrue "Case 4 required is coordinator" (requiredNode.ToString() = "coordinator")
        assertTrue "Case 4 decisionKind is null" (isNull decisionKindNode)
        assertTrue "Case 4 target is null" (isNull targetNode)

        // Case 6: the published `task_apply` schema description must warn about the fail-closed authority contract.
        // The inline `tools` JSON literal is read from protocol/Mcp.fs without modifying the source.
        let toolsSource = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "protocol", "Mcp.fs"))

        assertTrue
            "Case 6 schema description mentions fail closed"
            (toolsSource.Contains("fail closed"))
        assertTrue
            "Case 6 schema description mentions error.authority"
            (toolsSource.Contains("error.authority"))
        assertTrue
            "Case 6 schema description disclaims User authority manufacturing"
            (toolsSource.Contains("cannot manufacture User authority"))

        printfn "OK Workflow authority metadata contract: an omitted reference denies with structured authority metadata, an explicit unknown reference is a not-found error without authority metadata or a persisted change, AuthorityDenied round-trips, non-authority errors stay envelope-clean, render handles None fields, schema description documents the contract"
    finally
        if Directory.Exists tempRoot then
            Directory.Delete(tempRoot, true)
}
// Standalone entry bridge: FSI needs one synchronous script entry.
|> Async.RunSynchronously
