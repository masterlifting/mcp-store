// role: test entrypoint
// Deterministic coverage for the compact Workflow projections (task_status and
// task_ready): lenient status reads, strict readiness reads, dependency
// blockers, ancestor state and question blockers, unmet guard blockers,
// terminal lifecycle, blocker truncation,
// pagination bounds, applied-state tracking, the
// lenient/strict split on drifted state, read-only guarantees, and start
// eligibility parity. Uses the repository's plain FSI test harness rather than
// Expecto because the solution contract forbids adding a project/package
// system. All fixtures live under one fresh GUID temp root and only that root
// is removed.

#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"
#load "../protocol/Adapter.fs"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Workflow
open WorkflowAdapter

let assertEqual name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let expectOkAsync name (result: Async<Result<'a, RuntimeError>>) : Async<'a> =
    async {
        match! result with
        | Ok value -> return value
        | Error error -> return failwithf "%s: expected Ok, got Error %s" name (renderError error)
    }

let expectRejectedAsync (name: string) (fragment: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ -> return failwithf "%s: expected rejection, got Ok" name
        | Error error ->
            let message = renderError error
            if message.Contains(fragment, StringComparison.OrdinalIgnoreCase) then return ()
            else return failwithf "%s: expected '%s', got '%s'" name fragment message
    }

let expectErrorAsync name (result: Async<Result<'a, RuntimeError>>) : Async<string> =
    async {
        match! result with
        | Ok _ -> return failwithf "%s: expected Error, got Ok" name
        | Error error -> return renderError error
    }

let text (node: JsonNode) (name: string) = node.[name].GetValue<string>()
let number (node: JsonNode) (name: string) = node.[name].GetValue<int>()
let boolean (node: JsonNode) (name: string) = node.[name].GetValue<bool>()
let child (node: JsonNode) (name: string) = node.[name].AsObject()
let array (node: JsonNode) (name: string) = node.[name].AsArray()

// Field tables keep the repeated projection assertions on one screen.
let expectTextFields name (node: JsonNode) (fields: (string * string) list) =
    for (key, expected) in fields do
        assertEqual (sprintf "%s %s" name key) expected (text node key)

let expectNumberFields name (node: JsonNode) (fields: (string * int) list) =
    for (key, expected) in fields do
        assertEqual (sprintf "%s %s" name key) expected (number node key)

let optionalNumber (node: JsonNode) (name: string) =
    let value = node.[name]
    if isNull value then None
    elif value.GetValueKind() = JsonValueKind.Null then None
    else Some(value.GetValue<int>())

let spec id title =
    { Id = id; Title = title; DependsOn = []; Children = [] }

let specWith id title dependsOn children =
    { Id = id; Title = title; DependsOn = dependsOn; Children = children }

let request id title workItems =
    { Id = id
      Title = title
      Kind = Execution
      AcceptanceCriteria = [ ("AC1", "Execution completes") ]
      WorkItems = workItems }

let evidenceKind value =
    match parseEvidenceKind value with
    | Ok kind -> kind
    | Error error -> failwithf "invalid test evidence kind %A: %s" value (renderError error)

let guardSpec id target checkpoint =
    { Id = id
      Target = target
      Checkpoint = checkpoint
      Requirement =
        EvidenceRequired
            { Kind = evidenceKind "test"
              MinimumCount = 1
              ProducerRole = None
              RequireIndependentProducer = false }
      Applicability = Always
      Waiver = NotWaivable }

let tempRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"projections-tests-{Guid.NewGuid():N}")

Directory.CreateDirectory tempRoot |> ignore

let createTaskOk id title workItems =
    createTask tempRoot (request id title workItems)

let project root taskId ready offset limit =
    executeProjection { Root = root; TaskId = taskId; Ready = ready; Offset = offset; Limit = limit }

let sidecarPath root taskId =
    Path.Combine(root, ".tasks", taskId, "runtime.json")

let snapshot root =
    Directory.GetFiles(root, "*", SearchOption.AllDirectories)
    |> Array.map (fun path -> path.Substring(root.Length), File.ReadAllText path)
    |> Array.sortBy fst

let pendingItems node =
    array node "pending" |> Seq.map (fun item -> item.AsObject()) |> Seq.toList

// Each scenario is independent so one failure cannot mask the rest, and every
// failure is reported with the production location that raised it.
let mutable failures: (string * string) list = []

let topFrame (ex: exn) =
    match ex.StackTrace with
    | null -> ""
    | trace ->
        let frames =
            trace.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun line -> line.Trim())
            |> Array.filter (fun line -> line.StartsWith("at ", StringComparison.Ordinal))
        frames |> Array.tryFind (fun frame -> frame.Contains("Workflow.fs"))
        |> Option.orElse (Array.tryHead frames)
        |> Option.defaultValue ""

let scenario name (body: Async<unit>) : Async<unit> =
    async {
        try
            do! body
            printfn "PASS %s" name
        with ex ->
            let frame = topFrame ex
            let detail = if String.IsNullOrWhiteSpace frame then ex.Message else $"{ex.Message} @ {frame}"
            failures <- failures @ [ (name, detail) ]
            printfn "FAIL %s: %s" name detail
    }

async {
    try
        do!
            scenario "status projection is lenient and reports notChecked" (async {
                let! _ = expectOkAsync "create PRJ-1" (createTaskOk "PRJ-1" "Status projection" [ spec "W1" "Do the work" ])
                let! status = expectOkAsync "status PRJ-1" (project tempRoot "PRJ-1" false 0 10)
                expectTextFields
                    "status"
                    status
                    [ "validation", "notChecked"
                      "taskId", "PRJ-1"
                      "kind", "execution"
                      "profile", "general"
                      "lifecycle", "open"
                      "contractState", "draft" ]
                expectNumberFields "status" status [ "stateRevision", 0; "contractRevision", 1 ]
                let progress = child status "workItemProgress"
                expectNumberFields
                    "status progress"
                    progress
                    [ "pending", 1; "active", 0; "waiting", 0; "blocked", 0; "done", 0; "skipped", 0 ]
            })

        do!
            scenario "status projection counts nested work items" (async {
                let! _ =
                    expectOkAsync
                        "create PRJ-2"
                        (createTaskOk "PRJ-2" "Nested projection" [ specWith "W1" "Parent" [] [ spec "W1.1" "Child" ] ])
                let! status = expectOkAsync "status PRJ-2" (project tempRoot "PRJ-2" false 0 10)
                assertEqual "nested pending count" 2 (number (child status "workItemProgress") "pending")
                let! ready = expectOkAsync "ready PRJ-2" (project tempRoot "PRJ-2" true 0 10)
                assertEqual "nested ready total" 2 (number ready "total")
                assertTrue "nested children are startable" (pendingItems ready |> List.forall (fun item -> boolean item "startable"))
            })

        do!
            scenario "ready projection is registry-validated" (async {
                let! _ = expectOkAsync "create PRJ-3" (createTaskOk "PRJ-3" "Ready projection" [ spec "W1" "Do the work" ])
                let! ready = expectOkAsync "ready PRJ-3" (project tempRoot "PRJ-3" true 0 10)
                expectTextFields "ready" ready [ "validation", "valid"; "taskId", "PRJ-3" ]
                expectNumberFields "ready" ready [ "total", 1; "offset", 0; "limit", 10 ]
                assertEqual "ready nextOffset absent" None (optionalNumber ready "nextOffset")
                let items = pendingItems ready
                assertEqual "ready pending length" 1 items.Length
                let first = items.Head
                assertEqual "ready workItemId" "W1" (text first "workItemId")
                assertEqual "ready startable" true (boolean first "startable")
                assertEqual "ready blockers empty" 0 (array first "blockers").Count
                assertEqual "ready blockersTotal" 0 (number first "blockersTotal")
                assertEqual "ready blockersTruncated" false (boolean first "blockersTruncated")
            })

        do!
            scenario "ready projection reports dependency blockers" (async {
                let! _ =
                    expectOkAsync
                        "create PRJ-4"
                        (createTaskOk "PRJ-4" "Dependency projection" [ spec "W1" "First"; specWith "W2" "Second" [ "W1" ] [] ])
                let! ready = expectOkAsync "ready PRJ-4" (project tempRoot "PRJ-4" true 0 10)
                assertEqual "dependency ready total" 2 (number ready "total")
                let items = pendingItems ready
                let first = items |> List.find (fun item -> text item "workItemId" = "W1")
                let second = items |> List.find (fun item -> text item "workItemId" = "W2")
                assertEqual "dependency-free item startable" true (boolean first "startable")
                assertEqual "dependent item not startable" false (boolean second "startable")
                assertEqual "dependent blockersTotal" 1 (number second "blockersTotal")
                let blocker = (array second "blockers").[0].AsObject()
                expectTextFields "dependency blocker" blocker [ "type", "dependency"; "id", "W1" ]
            })

        do!
            scenario "ready projection paginates pending work" (async {
                let! _ =
                    expectOkAsync
                        "create PRJ-5"
                        (createTaskOk "PRJ-5" "Paged projection" [ spec "W1" "First"; spec "W2" "Second" ])
                let! page1 = expectOkAsync "ready PRJ-5 page one" (project tempRoot "PRJ-5" true 0 1)
                assertEqual "page one total" 2 (number page1 "total")
                assertEqual "page one length" 1 (pendingItems page1).Length
                assertEqual "page one nextOffset" (Some 1) (optionalNumber page1 "nextOffset")
                assertEqual "page one workItemId" "W1" (text (pendingItems page1).Head "workItemId")
                let! page2 = expectOkAsync "ready PRJ-5 page two" (project tempRoot "PRJ-5" true 1 1)
                assertEqual "page two length" 1 (pendingItems page2).Length
                assertEqual "page two nextOffset absent" None (optionalNumber page2 "nextOffset")
                assertEqual "page two workItemId" "W2" (text (pendingItems page2).Head "workItemId")
            })

        do!
            scenario "projection input bounds are enforced" (async {
                let! _ = expectOkAsync "create PRJ-6" (createTaskOk "PRJ-6" "Bounds projection" [ spec "W1" "Do the work" ])
                do! expectRejectedAsync "negative offset" "offset must be non-negative" (project tempRoot "PRJ-6" true -1 10)
                do! expectRejectedAsync "zero limit" "limit must be between 1 and 100" (project tempRoot "PRJ-6" true 0 0)
                do! expectRejectedAsync "limit above range" "limit must be between 1 and 100" (project tempRoot "PRJ-6" true 0 101)
                do! expectRejectedAsync "negative offset on status" "offset must be non-negative" (project tempRoot "PRJ-6" false -1 10)
            })

        do!
            scenario "projections track applied work state" (async {
                let! _ = expectOkAsync "create PRJ-7" (createTaskOk "PRJ-7" "State projection" [ spec "W1" "Do the work" ])
                let! _ = expectOkAsync "start PRJ-7 W1" (applyTask tempRoot "PRJ-7" 0 (StartWorkItem "W1"))
                let! status = expectOkAsync "status PRJ-7" (project tempRoot "PRJ-7" false 0 10)
                assertEqual "active stateRevision" 1 (number status "stateRevision")
                let progress = child status "workItemProgress"
                expectNumberFields "status progress" progress [ "active", 1; "pending", 0 ]
                let! ready = expectOkAsync "ready PRJ-7" (project tempRoot "PRJ-7" true 0 10)
                assertEqual "ready total after start" 0 (number ready "total")
                assertEqual "ready pending empty after start" 0 (pendingItems ready).Length
            })

        do!
            scenario "ready projection truncates long blocker lists" (async {
                let manyDependencies = [ 1 .. 21 ] |> List.map (fun index -> sprintf "W%d" index)
                let manyItems =
                    manyDependencies |> List.map (fun id -> spec id "Dependency") |> fun items -> items @ [ specWith "W22" "Truncated" manyDependencies [] ]
                let! _ = expectOkAsync "create PRJ-8" (createTaskOk "PRJ-8" "Truncation projection" manyItems)
                let! ready = expectOkAsync "ready PRJ-8" (project tempRoot "PRJ-8" true 0 100)
                let target = pendingItems ready |> List.find (fun item -> text item "workItemId" = "W22")
                assertEqual "truncated blockersTotal" 21 (number target "blockersTotal")
                assertEqual "visible blockers" 20 (array target "blockers").Count
                assertEqual "truncated flag" true (boolean target "blockersTruncated")
            })

        do!
            scenario "status stays lenient where ready is strict" (async {
                let! _ = expectOkAsync "create PRJ-9" (createTaskOk "PRJ-9" "Drift projection" [ spec "W1" "Do the work" ])
                let sidecar = sidecarPath tempRoot "PRJ-9"
                let drifted =
                    match JsonNode.Parse(File.ReadAllText sidecar) with
                    | :? JsonObject as node ->
                        node.["profileFingerprint"] <- JsonValue.Create "drifted-fingerprint"
                        node.ToJsonString()
                    | _ -> failwith "sidecar is not a JSON object"
                File.WriteAllText(sidecar, drifted)
                let! status = expectOkAsync "status PRJ-9 after drift" (project tempRoot "PRJ-9" false 0 10)
                assertEqual "drifted status stays lenient" "notChecked" (text status "validation")
                let! strict = expectErrorAsync "ready PRJ-9 after drift" (project tempRoot "PRJ-9" true 0 10)
                let lowered = strict.ToLowerInvariant()
                assertTrue "strict ready rejects drift" (lowered.Contains "profile" || lowered.Contains "fingerprint")
            })

        do!
            scenario "projection reads do not mutate bytes, revision, or root" (async {
                let! _ = expectOkAsync "create PRJ-10" (createTaskOk "PRJ-10" "Read-only projection" [ spec "W1" "Do the work" ])
                let before = snapshot tempRoot
                let! status = expectOkAsync "status PRJ-10" (project tempRoot "PRJ-10" false 0 10)
                let! ready = expectOkAsync "ready PRJ-10" (project tempRoot "PRJ-10" true 0 10)
                assertEqual "projection reads leave the root untouched" before (snapshot tempRoot)
                assertEqual "status revision unchanged" 0 (number status "stateRevision")
                assertEqual "ready revision unchanged" 0 (number ready "stateRevision")
            })

        do!
            scenario "start semantics refuse a dependency-blocked item" (async {
                let! _ =
                    expectOkAsync
                        "create PRJ-11"
                        (createTaskOk "PRJ-11" "Start semantics" [ spec "W1" "First"; specWith "W2" "Second" [ "W1" ] [] ])
                let! started = expectOkAsync "start PRJ-11 W1" (applyTask tempRoot "PRJ-11" 0 (StartWorkItem "W1"))
                assertEqual "start bumps revision" 1 started.StateRevision
                do! expectRejectedAsync "blocked item cannot start" "not ready" (applyTask tempRoot "PRJ-11" 1 (StartWorkItem "W2"))
            })

        do!
            scenario "projection startability matches start semantics" (async {
                let! _ =
                    expectOkAsync
                        "create PRJ-12"
                        (createTaskOk "PRJ-12" "Eligibility parity" [ spec "W1" "First"; specWith "W2" "Second" [ "W1" ] [] ])
                let! ready = expectOkAsync "ready PRJ-12" (project tempRoot "PRJ-12" true 0 10)
                let items = pendingItems ready
                let startableOf taskId =
                    let item = items |> List.find (fun item -> text item "workItemId" = taskId)
                    boolean item "startable"
                assertEqual "parity W1 startable" true (startableOf "W1")
                assertEqual "parity W2 startable" false (startableOf "W2")
                let! started = expectOkAsync "start PRJ-12 W1" (applyTask tempRoot "PRJ-12" 0 (StartWorkItem "W1"))
                assertEqual "parity start bumps revision" 1 started.StateRevision
                do! expectRejectedAsync "blocked item cannot start" "not ready" (applyTask tempRoot "PRJ-12" 1 (StartWorkItem "W2"))
            })

        do!
            scenario "ready projection reports ancestor state blockers" (async {
                let! _ =
                    expectOkAsync
                        "create PRJ-13"
                        (createTaskOk "PRJ-13" "Ancestor state projection" [ specWith "W1" "Parent" [] [ spec "W1.1" "Child" ] ])
                let! _ = expectOkAsync "start PRJ-13 W1" (applyTask tempRoot "PRJ-13" 0 (StartWorkItem "W1"))
                let! _ =
                    expectOkAsync
                        "wait PRJ-13 W1"
                        (applyTask tempRoot "PRJ-13" 1 (WaitWorkItem("W1", ResumeCondition "external")))
                let! ready = expectOkAsync "ready PRJ-13" (project tempRoot "PRJ-13" true 0 10)
                let child = pendingItems ready |> List.find (fun item -> text item "workItemId" = "W1.1")
                assertEqual "ancestor-state child not startable" false (boolean child "startable")
                assertEqual "ancestor-state blockersTotal" 1 (number child "blockersTotal")
                let blocker = (array child "blockers").[0].AsObject()
                expectTextFields "ancestor state blocker" blocker [ "type", "ancestorState"; "id", "W1"; "state", "waiting" ]
                do!
                    expectRejectedAsync
                        "child cannot start under waiting ancestor"
                        "cannot start while ancestor 'W1' is waiting"
                        (applyTask tempRoot "PRJ-13" 2 (StartWorkItem "W1.1"))
            })

        do!
            scenario "ready projection reports ancestor question blockers" (async {
                let! _ =
                    expectOkAsync
                        "create PRJ-14"
                        (createTaskOk "PRJ-14" "Ancestor question projection" [ specWith "W1" "Parent" [] [ spec "W1.1" "Child" ] ])
                let! _ =
                    expectOkAsync
                        "add question PRJ-14"
                        (applyTask
                            tempRoot
                            "PRJ-14"
                            0
                            (AddQuestion { Id = "Q1"; Text = "Parent needs a decision"; Impact = WorkItems [ "W1" ] }))
                let! ready = expectOkAsync "ready PRJ-14" (project tempRoot "PRJ-14" true 0 10)
                let parent = pendingItems ready |> List.find (fun item -> text item "workItemId" = "W1")
                let child = pendingItems ready |> List.find (fun item -> text item "workItemId" = "W1.1")
                assertEqual "question-blocked parent not startable" false (boolean parent "startable")
                let parentBlocker = (array parent "blockers").[0].AsObject()
                expectTextFields "question blocker" parentBlocker [ "type", "question"; "id", "Q1" ]
                assertEqual "ancestor-question child not startable" false (boolean child "startable")
                assertEqual "ancestor-question blockersTotal" 1 (number child "blockersTotal")
                let childBlocker = (array child "blockers").[0].AsObject()
                expectTextFields "ancestor question blocker" childBlocker [ "type", "ancestorQuestion"; "id", "Q1"; "ancestorId", "W1" ]
                do!
                    expectRejectedAsync
                        "child cannot start under question-blocked ancestor"
                        "cannot start while ancestor 'W1' is blocked by an open question"
                        (applyTask tempRoot "PRJ-14" 1 (StartWorkItem "W1.1"))
            })

        do!
            scenario "ready projection reports unmet guard blockers" (async {
                let! _ = expectOkAsync "create PRJ-15" (createTaskOk "PRJ-15" "Guard projection" [ spec "W1" "Do the work" ])
                let! _ =
                    expectOkAsync
                        "add guard PRJ-15"
                        (applyTask tempRoot "PRJ-15" 0 (AddGuard(guardSpec "G1" (WorkItemTarget "W1") BeforeStart)))
                let! ready = expectOkAsync "ready PRJ-15" (project tempRoot "PRJ-15" true 0 10)
                let item = pendingItems ready |> List.find (fun item -> text item "workItemId" = "W1")
                assertEqual "guard-blocked item not startable" false (boolean item "startable")
                assertEqual "guard blockersTotal" 1 (number item "blockersTotal")
                let blocker = (array item "blockers").[0].AsObject()
                expectTextFields "guard blocker" blocker [ "type", "guard"; "id", "G1" ]
                do!
                    expectRejectedAsync
                        "guarded item cannot start"
                        "guard 'G1' is not satisfied"
                        (applyTask tempRoot "PRJ-15" 1 (StartWorkItem "W1"))
            })

        do!
            scenario "terminal lifecycle task reports no startable work" (async {
                let! _ = expectOkAsync "create PRJ-16" (createTaskOk "PRJ-16" "Terminal projection" [ spec "W1" "Do the work" ])
                let sidecar = sidecarPath tempRoot "PRJ-16"
                // The canonical completion path requires every WorkItem to be terminal,
                // so the terminal fixture is written directly to reach a terminal task
                // that still owns pending work.
                let terminal =
                    match JsonNode.Parse(File.ReadAllText sidecar) with
                    | :? JsonObject as node ->
                        node.["lifecycle"] <- JsonValue.Create "complete"
                        node.ToJsonString()
                    | _ -> failwith "sidecar is not a JSON object"
                File.WriteAllText(sidecar, terminal)
                let! status = expectOkAsync "status PRJ-16" (project tempRoot "PRJ-16" false 0 10)
                expectTextFields "terminal status" status [ "validation", "notChecked"; "lifecycle", "complete" ]
                assertEqual "terminal pending count" 1 (number (child status "workItemProgress") "pending")
                let! ready = expectOkAsync "ready PRJ-16" (project tempRoot "PRJ-16" true 0 10)
                let item = pendingItems ready |> List.find (fun item -> text item "workItemId" = "W1")
                assertEqual "terminal item not startable" false (boolean item "startable")
                assertEqual "terminal blockersTotal" 1 (number item "blockersTotal")
                let blocker = (array item "blockers").[0].AsObject()
                expectTextFields "terminal blocker" blocker [ "type", "lifecycle"; "state", "complete" ]
                do!
                    expectRejectedAsync
                        "terminal task cannot start"
                        "a complete task cannot start work"
                        (applyTask tempRoot "PRJ-16" 0 (StartWorkItem "W1"))
            })

        if failures.IsEmpty then
            printfn "OK workflow projections: lenient status, strict ready, dependency blockers, ancestor state and question blockers, unmet guard blockers, terminal lifecycle, blocker truncation, pagination bounds, applied-state tracking, drift split, read-only guarantee, and start-eligibility parity"
    finally
        if Directory.Exists tempRoot && tempRoot.Contains("projections-tests-", StringComparison.Ordinal) then
            Directory.Delete(tempRoot, true)
}
|> Async.RunSynchronously

if not failures.IsEmpty then
    printfn "%d scenario(s) failed" failures.Length
    exit 1
