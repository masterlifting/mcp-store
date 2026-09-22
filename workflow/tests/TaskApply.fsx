#load "../ComputationExpressions.fs"
#load "../Workflow.fs"
#load "../WorkflowAdapter.fs"

open System
open Workflow
open WorkflowAdapter

let usage () =
    eprintfn "usage: TaskApply.fsx <project-root> <TASK-ID> <expected-revision> <command> [command arguments]"
    eprintfn "  start <W-id>"
    eprintfn "  complete-work <W-id> <result> [evidence-id...]"
    eprintfn "  wait <W-id> <resume-condition>"
    eprintfn "  block <W-id> <blocker>"
    eprintfn "  resume <W-id> [observation-ref]"
    eprintfn "  add-evidence <E-id> <kind> <source> <summary> [--subject <text>] [--producer-role <role>] [--producer-id <id>] [--reference <ref>]"
    eprintfn "  supersede-evidence <E-id> <reason>"
    eprintfn "  verify <AC-id> <evidence-id...>"
    eprintfn "  add-guard <G-id> --target <task|workItem:W-id> --checkpoint <beforeStart|beforeComplete> --kind <kind> [--minimum-count <n>] [--producer-role <role>] [--independent] [--applicability <always|explicitDecision:coordinator|explicitDecision:user>] [--waiver <notWaivable|waivableBy:coordinator|waivableBy:user>]"
    eprintfn "  mark-not-applicable <G-id> [decision-ref]"
    eprintfn "  waive-guard <G-id> [decision-ref]"
    eprintfn "  add-decision <kind> --target <target> [--target <target>...] --rationale <text>"
    eprintfn "  add-question <Q-id> <text> --impact <taskWide|workItems:W-id,...>"
    eprintfn "  resolve-question <Q-id> <D-id>"
    eprintfn "  rebind-owner <W-id> <role> <reason> [--agent-id <id>]"
    eprintfn "  reopen <reason> --target <target> [--target <target>...] [--decision <D-id>]"
    eprintfn "  complete-task <state> <evidence-summary> <next>"
    exit 2

let addEvidence (parts: string array) =
    if parts.Length < 4 then usage ()

    match parseEvidenceKind parts.[1] with
    | Error error ->
        eprintfn "%s" (renderError error)
        exit 1
    | Ok kind ->
        let mutable subject = None
        let mutable producerRole = None
        let mutable producerId = None
        let mutable reference = None
        let mutable index = 4

        while index < parts.Length do
            match parts.[index] with
            | "--subject" when index + 1 < parts.Length ->
                subject <- Some parts.[index + 1]
                index <- index + 2
            | "--producer-role" when index + 1 < parts.Length ->
                producerRole <- Some parts.[index + 1]
                index <- index + 2
            | "--producer-id" when index + 1 < parts.Length ->
                producerId <- Some parts.[index + 1]
                index <- index + 2
            | "--reference" when index + 1 < parts.Length ->
                reference <- Some parts.[index + 1]
                index <- index + 2
            | _ -> usage ()

        AddEvidence
            { Id = parts.[0]
              Kind = kind
              Source = EvidenceSource parts.[2]
              Subject = subject
              ProducerRole = producerRole
              ProducerId = producerId
              Reference = reference
              Summary = parts.[3] }

let addGuard (parts: string array) =
    if parts.Length < 1 then usage ()

    let mutable targetText = None
    let mutable checkpointText = None
    let mutable kindText = None
    let mutable minimumCount = 1
    let mutable producerRole = None
    let mutable independent = false
    let mutable applicability = Always
    let mutable waiver = NotWaivable
    let mutable index = 1

    let consumeValue () =
        if index + 1 >= parts.Length then usage ()
        let value = parts.[index + 1]
        index <- index + 2
        value

    while index < parts.Length do
        match parts.[index] with
        | "--target" -> targetText <- Some(consumeValue ())
        | "--checkpoint" -> checkpointText <- Some(consumeValue ())
        | "--kind" -> kindText <- Some(consumeValue ())
        | "--minimum-count" ->
            match Int32.TryParse(consumeValue ()) with
            | true, value -> minimumCount <- value
            | _ -> usage ()
        | "--producer-role" -> producerRole <- Some(consumeValue ())
        | "--independent" ->
            independent <- true
            index <- index + 1
        | "--applicability" ->
            match parseApplicability (consumeValue ()) with
            | Ok value -> applicability <- value
            | Error error ->
                eprintfn "%s" (renderError error)
                exit 1
        | "--waiver" ->
            match parseWaiver (consumeValue ()) with
            | Ok value -> waiver <- value
            | Error error ->
                eprintfn "%s" (renderError error)
                exit 1
        | _ -> usage ()

    match targetText, checkpointText, kindText with
    | Some targetText, Some checkpointText, Some kindText ->
        match parseGuardTarget targetText, parseGuardCheckpoint checkpointText, parseEvidenceKind kindText with
        | Ok target, Ok checkpoint, Ok kind ->
            AddGuard
                { Id = parts.[0]
                  Target = target
                  Checkpoint = checkpoint
                  Requirement =
                    EvidenceRequired
                        { Kind = kind
                          MinimumCount = minimumCount
                          ProducerRole = producerRole
                          RequireIndependentProducer = independent }
                  Applicability = applicability
                  Waiver = waiver }
        | Error error, _, _
        | _, Error error, _
        | _, _, Error error ->
            eprintfn "%s" (renderError error)
            exit 1
    | _ -> usage ()

let addDecision (parts: string array) =
    if parts.Length < 1 then usage ()

    let mutable rationale = None
    let mutable targets = []
    let mutable index = 1

    let consumeValue () =
        if index + 1 >= parts.Length then usage ()
        let value = parts.[index + 1]
        index <- index + 2
        value

    while index < parts.Length do
        match parts.[index] with
        | "--target" ->
            match parseDecisionTarget (consumeValue ()) with
            | Ok value -> targets <- targets @ [ value ]
            | Error error ->
                eprintfn "%s" (renderError error)
                exit 1
        | "--rationale" -> rationale <- Some(consumeValue ())
        | _ -> usage ()

    match parseDecisionKind parts.[0], targets, rationale with
    | Ok kind, _ :: _, Some rationale -> AddDecision { Kind = kind; Targets = targets; Rationale = rationale }
    | Error error, _, _ ->
        eprintfn "%s" (renderError error)
        exit 1
    | _ -> usage ()

let addQuestion (parts: string array) =
    if parts.Length < 2 then usage ()

    let mutable impactText = None
    let mutable index = 2

    while index < parts.Length do
        match parts.[index] with
        | "--impact" ->
            if index + 1 >= parts.Length then usage ()
            impactText <- Some parts.[index + 1]
            index <- index + 2
        | _ -> usage ()

    match impactText with
    | Some text ->
        match parseQuestionImpact text with
        | Ok impact ->
            AddQuestion
                { Id = parts.[0]
                  Text = parts.[1]
                  Impact = impact }
        | Error error ->
            eprintfn "%s" (renderError error)
            exit 1
    | None -> usage ()

let rebindOwner (parts: string array) =
    if parts.Length < 3 then usage ()

    let mutable agentId = None
    let mutable index = 3

    while index < parts.Length do
        match parts.[index] with
        | "--agent-id" when index + 1 < parts.Length ->
            agentId <- Some parts.[index + 1]
            index <- index + 2
        | _ -> usage ()

    RebindOwner(parts.[0], { Role = parts.[1]; AgentId = agentId }, parts.[2])

let reopenTask (parts: string array) =
    if parts.Length < 1 then usage ()

    let mutable targets = []
    let mutable decisionRef = None
    let mutable index = 1

    let consumeValue () =
        if index + 1 >= parts.Length then usage ()
        let value = parts.[index + 1]
        index <- index + 2
        value

    while index < parts.Length do
        match parts.[index] with
        | "--target" ->
            match parseReopenTarget (consumeValue ()) with
            | Ok value -> targets <- targets @ [ value ]
            | Error error ->
                eprintfn "%s" (renderError error)
                exit 1
        | "--decision" -> decisionRef <- Some(DecisionRef(consumeValue ()))
        | _ -> usage ()

    if targets.IsEmpty then usage ()

    ReopenTask
        { Reason = parts.[0]
          DecisionRef = decisionRef
          Targets = targets }

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length < 4 then usage ()

let expectedRevision =
    match Int32.TryParse args.[2] with
    | true, value when value >= 0 -> value
    | _ -> usage ()

let command =
    match args.[3] with
    | "start" when args.Length = 5 -> StartWorkItem args.[4]
    | "complete-work" when args.Length >= 6 ->
        CompleteWorkItem(
            args.[4],
            { Result = args.[5]
              EvidenceRefs = args |> Array.skip 6 |> Array.toList }
        )
    | "wait" when args.Length = 6 -> WaitWorkItem(args.[4], ResumeCondition args.[5])
    | "block" when args.Length = 6 -> BlockWorkItem(args.[4], Blocker args.[5])
    | "resume" when args.Length = 5 -> ResumeWorkItem(args.[4], None)
    | "resume" when args.Length = 6 -> ResumeWorkItem(args.[4], Some(ObservationRef args.[5]))
    | "add-evidence" -> addEvidence (args |> Array.skip 4)
    | "supersede-evidence" when args.Length = 6 -> SupersedeEvidence(args.[4], args.[5])
    | "verify" when args.Length >= 6 ->
        VerifyAcceptanceCriterion(args.[4], args |> Array.skip 5 |> Array.toList)
    | "add-guard" -> addGuard (args |> Array.skip 4)
    | "mark-not-applicable" when args.Length = 5 -> MarkGuardNotApplicable(args.[4], None)
    | "mark-not-applicable" when args.Length = 6 ->
        MarkGuardNotApplicable(args.[4], Some(DecisionRef args.[5]))
    | "waive-guard" when args.Length = 5 -> WaiveGuard(args.[4], None)
    | "waive-guard" when args.Length = 6 -> WaiveGuard(args.[4], Some(DecisionRef args.[5]))
    | "add-decision" -> addDecision (args |> Array.skip 4)
    | "add-question" -> addQuestion (args |> Array.skip 4)
    | "resolve-question" when args.Length = 6 -> ResolveQuestion(args.[4], DecisionRef args.[5])
    | "rebind-owner" -> rebindOwner (args |> Array.skip 4)
    | "reopen" -> reopenTask (args |> Array.skip 4)
    | "complete-task" when args.Length = 7 ->
        CompleteTask
            { State = args.[4]
              EvidenceSummary = args.[5]
              Next = args.[6] }
    | _ -> usage ()

match
    execute
        (ApplyTask
            { Root = args.[0]
              TaskId = args.[1]
              ExpectedStateRevision = expectedRevision
              Command = command }) with
| Ok task -> printfn "%s" (serialize task)
| Error error ->
    eprintfn "%s" (renderError error)
    exit 1
