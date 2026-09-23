module WorkflowAdapter

open Workflow

// This is a typed library boundary over the existing runtime operations. It does
// not add semantics, persistence, authority, or effect execution; callers still
// receive the same TaskModel/RuntimeError results as the underlying runtime.
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

type Operation =
    | CreateTask of CreateOperation
    | GetTask of GetOperation
    | ApplyTask of ApplyOperation
    | ValidateTask of ValidateOperation

let execute operation : Result<TaskModel, RuntimeError> =
    match operation with
    | CreateTask request -> createTaskWithProfile request.Root request.ProfileId request.Request
    | GetTask request -> getTask request.Root request.TaskId
    | ApplyTask request ->
        applyTask request.Root request.TaskId request.ExpectedStateRevision request.Command
    | ValidateTask request -> validateTask request.Root request.TaskId
