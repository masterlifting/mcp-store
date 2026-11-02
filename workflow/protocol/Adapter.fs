module WorkflowAdapter

open Workflow

// Task operations remain a typed library boundary over the existing runtime.
type CreateOperation =
    { Root: string
      ProfileId: string option
      Request: CreateRequest }

type GetOperation =
    { Root: string
      TaskId: string }

type ApplyOperation =
    { Root: string
      TaskId: string
      ExpectedStateRevision: int
      Command: TaskCommand }

type ValidateOperation =
    { Root: string
      TaskId: string }

type ProjectionOperation =
    { Root: string
      TaskId: string
      Ready: bool
      Offset: int
      Limit: int }

type CleanRequest =
    { Root: string
      TaskId: string
      Operation: CleanOperation }

type Operation =
    | CreateTask of CreateOperation
    | GetTask of GetOperation
    | ApplyTask of ApplyOperation
    | ValidateTask of ValidateOperation

// Effectful dispatch is asynchronous end-to-end; the transport host is the only
// place that bridges this to its native Task-based loop.
let execute operation : Async<Result<TaskModel, RuntimeError>> =
    match operation with
    | CreateTask request -> createTaskWithProfile request.Root request.ProfileId request.Request
    | GetTask request -> getTask request.Root request.TaskId
    | ApplyTask request ->
        applyTask request.Root request.TaskId request.ExpectedStateRevision request.Command
    | ValidateTask request -> validateTask request.Root request.TaskId

// Limiting prompts to canonical denials avoids changing routine Coordinator applies.
let internal executeApplyWithTrustedConfirmation
    (request: ApplyOperation)
    (nativeConfirmationSupported: bool)
    (confirm: TrustedConfirmationCallback)
    : Async<Result<TaskModel, RuntimeError>> =
    async {
        let! ordinary = applyTask request.Root request.TaskId request.ExpectedStateRevision request.Command

        match ordinary with
        | Error(AuthorityDenied (metadata, _))
            when metadata.RequiredAuthority = MinimumAuthority.UserAuthority
                 && metadata.Target.IsSome
                 && metadata.DecisionKind.IsSome ->
            if not nativeConfirmationSupported then
                return
                    Error(
                        InvalidTransition
                            "native User confirmation is unsupported: the connected client must negotiate protocol 2025-11-25 and form elicitation"
                    )
            else
                return!
                    applyTrustedTask
                        request.Root
                        request.TaskId
                        request.ExpectedStateRevision
                        request.Command
                        confirm
        | result -> return result
    }

let executeProjection (request: ProjectionOperation) =
    projectTask request.Root request.TaskId request.Ready request.Offset request.Limit

let executeClean (request: CleanRequest) : Async<Result<CleanResult, RuntimeError>> =
    // Cleanup has an operation-specific result rather than a task snapshot.
    cleanTask request.Root request.TaskId request.Operation
