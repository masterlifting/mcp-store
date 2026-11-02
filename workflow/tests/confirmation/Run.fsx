// role: test entrypoint
// Native MCP coverage of the producer's protected-operation path over the real stdio boundary, limited to implemented behavior.
// The producer runs in a child process whose application-data and home directories are redirected to an isolated run root under the approved temporary base, so the real per-account integrity key is never read, created, or rotated here.
// Every answer to an elicitation is a deterministic JSON-RPC test double standing in for the native host form, so no case in this suite is evidence that a human approved any operation.

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks


let failWith message = raise (Exception message)


let normalizePath (value: string) =
    (Path.GetFullPath value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

let samePath (left: string) (right: string) =
    String.Equals(normalizePath left, normalizePath right, StringComparison.OrdinalIgnoreCase)

// Segment-aware containment: the candidate must live below the parent as a whole path segment.
let isInsideDirectory (parent: string) (candidate: string) =
    let parentPath = normalizePath parent
    let candidatePath = normalizePath candidate
    candidatePath.StartsWith(parentPath + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)


let jstr (value: string) : JsonNode = JsonValue.Create(value)
let jint (value: int) : JsonNode = JsonValue.Create(value)
let jbool (value: bool) : JsonNode = JsonValue.Create(value)

let jobj (fields: (string * JsonNode) list) : JsonNode =
    let node = JsonObject()

    for (key, value) in fields do
        node.[key] <- value

    node

let jarr (items: JsonNode list) : JsonNode =
    let node = JsonArray()

    for item in items do
        node.Add item |> ignore

    node

let strField (node: JsonNode) (name: string) =
    let value = node.[name]

    if isNull value then
        ""
    else
        value.GetValue<string>()

let intField (node: JsonNode) (name: string) =
    let value = node.[name]

    if isNull value then
        -1
    else
        value.GetValue<int>()

let boolField (node: JsonNode) (name: string) =
    let value = node.[name]

    if isNull value then
        false
    else
        value.GetValue<bool>()

// The raw JSON text of a JSON-RPC id, so a numeric and a string id are compared exactly as the producer parses them.
let rawId (message: JsonNode) =
    let id = message.["id"]

    if isNull id then
        None
    else
        Some(id.ToJsonString())


let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let releaseEntryDll = Path.Combine(repoRoot, "workflow", "bin", "Release", "net11.0", "Mcp.Workflow.dll")
let guardScript = Path.Combine(__SOURCE_DIRECTORY__, "Isolation.fsx")
let requiredSdk = "11.0.100-rc.1.26425.128"

if not (File.Exists releaseEntryDll) then
    failWith
        (sprintf
            "an authorized producer Release build is required before process-boundary testing: %s"
            releaseEntryDll)

if not (File.Exists guardScript) then
    failWith (sprintf "the isolation guard script is missing: %s" guardScript)

let approvedBase =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_TEMP_BASE" with
    | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
    | _ -> Path.Combine(Path.GetTempPath(), "opencode", "GHI-18", "tests")

let systemTemp = Path.GetFullPath(Path.GetTempPath())

if not (approvedBase.StartsWith(systemTemp, StringComparison.OrdinalIgnoreCase)) then
    failWith "the approved temporary base must live under the system temporary directory"

Directory.CreateDirectory approvedBase |> ignore

// The real per-account application data directory is only ever used as a comparison value; it is never read, created, or listed.
let realAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

if String.IsNullOrWhiteSpace realAppData then
    failWith "the real per-account application data directory must be resolvable"

let realKeyDirectory = Path.Combine(realAppData, "mcp-store", "workflow")

let resolveDotnetHost () =
    let names = if OperatingSystem.IsWindows() then [ "dotnet.exe" ] else [ "dotnet" ]
    let pathValue = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""

    let candidate =
        pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun directory ->
            names
            |> List.map (fun name -> Path.Combine(directory.Trim(), name))
            |> List.tryFind File.Exists)

    candidate
    |> Option.map Path.GetFullPath
    |> Option.defaultWith (fun () -> failWith (sprintf "the required .NET host '%s' is not on PATH" names.Head))

let dotnetHost = resolveDotnetHost ()

let assertRequiredSdk () : Async<unit> =
    async {
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- dotnetHost
        startInfo.ArgumentList.Add "--version"
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.WorkingDirectory <- repoRoot
        use child = Process.Start startInfo
        let! stdout = child.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
        let! stderr = child.StandardError.ReadToEndAsync() |> Async.AwaitTask
        do! child.WaitForExitAsync() |> Async.AwaitTask

        if child.ExitCode <> 0 then
            return failWith (sprintf "could not query the .NET SDK version: %s" (stderr.Trim()))

        let selected = stdout.Trim()

        if selected <> requiredSdk then
            return failWith (sprintf "the required SDK is '%s' but '%s' reports '%s'" requiredSdk dotnetHost selected)
    }


type McpHost =
    { Child: Process
      Responses: ConcurrentQueue<JsonNode>
      ServerRequests: ConcurrentQueue<JsonNode>
      Stderr: ConcurrentQueue<string>
      Unparsed: ConcurrentQueue<string> }

// A non-matching message is put back, so no server traffic is lost between assertions.
let takeMatching (queue: ConcurrentQueue<JsonNode>) (predicate: JsonNode -> bool) (timeoutMs: int) : Async<JsonNode option> =
    async {
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        let pending = ResizeArray<JsonNode>()
        let mutable result = None

        while result.IsNone && DateTime.UtcNow < deadline do
            match queue.TryDequeue() with
            | true, message ->
                if predicate message then
                    result <- Some message
                else
                    pending.Add message
            | _ -> do! Async.Sleep 25

        for message in pending do
            queue.Enqueue message

        return result
    }

let startHost
    (dllPath: string)
    (catalogPath: string)
    (catalogHash: string)
    (workingDirectory: string)
    (environment: (string * string) list)
    : McpHost =
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- dotnetHost
    startInfo.ArgumentList.Add "exec"
    startInfo.ArgumentList.Add dllPath
    startInfo.ArgumentList.Add "--profile-catalog"
    startInfo.ArgumentList.Add catalogPath
    startInfo.ArgumentList.Add "--profile-catalog-sha256"
    startInfo.ArgumentList.Add catalogHash
    startInfo.RedirectStandardInput <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.WorkingDirectory <- workingDirectory

    for (key, value) in environment do
        startInfo.Environment[key] <- value

    let child = Process.Start startInfo
    let responses = ConcurrentQueue<JsonNode>()
    let serverRequests = ConcurrentQueue<JsonNode>()
    let stderrLines = ConcurrentQueue<string>()
    let unparsedLines = ConcurrentQueue<string>()

    Task.Factory.StartNew(
        System.Action(fun () ->
            try
                let mutable running = true

                while running do
                    let line = child.StandardOutput.ReadLine()

                    if isNull line then
                        running <- false
                    else
                        try
                            let node = JsonNode.Parse line

                            if isNull node then
                                unparsedLines.Enqueue line
                            elif not (isNull (node.["method"])) then
                                serverRequests.Enqueue node
                            else
                                responses.Enqueue node
                        with _ ->
                            unparsedLines.Enqueue line
            with _ -> ()),
        TaskCreationOptions.LongRunning)
    |> ignore

    Task.Factory.StartNew(
        System.Action(fun () ->
            try
                let mutable running = true

                while running do
                    let line = child.StandardError.ReadLine()

                    if isNull line then
                        running <- false
                    else
                        stderrLines.Enqueue line
            with _ -> ()),
        TaskCreationOptions.LongRunning)
    |> ignore

    { Child = child
      Responses = responses
      ServerRequests = serverRequests
      Stderr = stderrLines
      Unparsed = unparsedLines }

let send (host: McpHost) (text: string) : Async<unit> =
    async {
        do! host.Child.StandardInput.WriteLineAsync(text) |> Async.AwaitTask
        do! host.Child.StandardInput.FlushAsync() |> Async.AwaitTask
    }

let closeInput (host: McpHost) =
    try
        host.Child.StandardInput.Close()
    with _ -> ()

let waitForExitAsync (host: McpHost) (timeoutMs: int) : Async<bool> =
    async {
        try
            do! host.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(float timeoutMs)) |> Async.AwaitTask
            return true
        with _ ->
            return false
    }

let kill (host: McpHost) =
    try
        if not host.Child.HasExited then
            host.Child.Kill true
    with _ -> ()


let mutable requestCounter = 0

let freshRequestId () =
    requestCounter <- requestCounter + 1
    requestCounter

let sendToolCall (host: McpHost) (requestId: int) (name: string) (arguments: JsonNode) : Async<unit> =
    let request =
        jobj
            [ "jsonrpc", jstr "2.0"
              "id", jint requestId
              "method", jstr "tools/call"
              "params", jobj [ "name", jstr name; "arguments", arguments ] ]

    send host (request.ToJsonString())

let awaitResponse (host: McpHost) (requestId: int) (timeoutMs: int) : Async<JsonNode option> =
    let expected = (jint requestId).ToJsonString()

    async {
        return! takeMatching host.Responses (fun message -> rawId message = Some expected) timeoutMs
    }

let awaitServerRequest (host: McpHost) (methodName: string) (timeoutMs: int) : Async<JsonNode option> =
    async {
        return! takeMatching host.ServerRequests (fun message -> strField message "method" = methodName) timeoutMs
    }

// The unsupported-client cases need a client that negotiates the accepted protocol version without the form capability, and one that negotiates the separately accepted older protocol version.
let initializeHostWith (host: McpHost) (protocolVersion: string) (supportsForm: bool) : Async<JsonNode> =
    async {
        let requestId = freshRequestId ()

        let capabilities =
            if supportsForm then
                jobj [ "elicitation", jobj [ "form", jobj [] ] ]
            else
                jobj []

        let request =
            jobj
                [ "jsonrpc", jstr "2.0"
                  "id", jint requestId
                  "method", jstr "initialize"
                  "params",
                  jobj
                      [ "protocolVersion", jstr protocolVersion
                        "capabilities", capabilities
                        "clientInfo", jobj [ "name", jstr "mcp-protected-tests"; "version", jstr "1" ] ] ]

        do! send host (request.ToJsonString())
        let! response = awaitResponse host requestId 60000

        let message =
            match response with
            | None -> failWith "the initialize request did not complete"
            | Some found -> found

        if not (isNull (message.["error"])) then
            failWith (sprintf "initialize returned a JSON-RPC error: %s" (message.ToJsonString()))

        let result = message.["result"]

        if isNull result then
            failWith "initialize returned no result"

        return result
    }

let initializeHost (host: McpHost) : Async<JsonNode> =
    initializeHostWith host "2025-11-25" true

let toolPayload (response: JsonNode) =
    let result = response.["result"]

    if isNull result then
        failWith "the JSON-RPC response carried no result"

    let payload = result.["structuredContent"]

    if isNull payload then
        failWith "the tool result carried no structured content"

    payload

let toolError (payload: JsonNode) =
    let error = payload.["error"]

    if isNull error then
        failWith "the tool result carried no error block"

    (strField error "code", strField error "message")

let acceptAnswer (elicitationId: string) =
    (jobj
        [ "jsonrpc", jstr "2.0"
          "id", jstr elicitationId
          "result", jobj [ "action", jstr "accept"; "content", jobj [ "confirm", jbool true ] ] ])
        .ToJsonString()


let createFixture (host: McpHost) (projectRoot: string) (taskId: string) (profile: string) : Async<JsonNode> =
    async {
        let requestId = freshRequestId ()

        let arguments =
            jobj
                [ "projectRoot", jstr projectRoot
                  "taskId", jstr taskId
                  "title", jstr "Protected operation fixture"
                  "kind", jstr "execution"
                  "profile", jstr profile
                  "acceptanceCriteria", jarr [ jobj [ "id", jstr "AC1"; "text", jstr "The fixture completes" ] ]
                  "workItems", jarr [ jobj [ "id", jstr "W1"; "title", jstr "Do the work"; "children", jarr [] ] ] ]

        do! sendToolCall host requestId "task_create" arguments
        let! response = awaitResponse host requestId 60000

        let message =
            match response with
            | None -> failWith (sprintf "the task_create call for '%s' did not complete" taskId)
            | Some found -> found

        let payload = toolPayload message

        if not (boolField payload "ok") then
            failWith (sprintf "the task_create call for '%s' failed: %s" taskId (payload.ToJsonString()))

        let task = payload.["task"]

        if isNull task then
            failWith "the created task carried no task payload"

        return task
    }

// The guard whose applicability also needs an explicit User decision is preferred among the user-waivable guards.
let pickUserWaivableGuard (task: JsonNode) : string =
    let guards = task.["guards"]

    if isNull guards then
        failWith "the created task carried no guards"

    let candidates =
        guards.AsArray()
        |> Seq.filter (fun guard -> strField guard "waiver" = "waivableBy:user")
        |> Seq.toList

    match candidates with
    | [] -> failWith "the created task carried no user-waivable guard"
    | _ ->
        let preferred =
            candidates
            |> List.tryFind (fun guard -> strField guard "applicability" = "explicitDecision:user")

        let chosen = defaultArg preferred (List.head candidates)
        strField chosen "id"

// Every user-waivable guard is a distinct User-authority target for the waiver command, so a task can carry more than one of them.
let userWaivableGuardIds (task: JsonNode) : string list =
    let guards = task.["guards"]

    if isNull guards then
        failWith "the created task carried no guards"

    guards.AsArray()
    |> Seq.filter (fun guard -> strField guard "waiver" = "waivableBy:user")
    |> Seq.map (fun guard -> strField guard "id")
    |> Seq.toList

// The other user-waivable guard of the same task, so a second protected operation is never a repeat of the first one.
let pickDistinctUserWaivableGuard (task: JsonNode) (excludeId: string) : string =
    let candidates =
        userWaivableGuardIds task
        |> List.filter (fun id -> id <> excludeId)

    match candidates with
    | [] -> failWith (sprintf "the created task carried no second user-waivable guard besides '%s'" excludeId)
    | id :: _ -> id

let sidecarPath (projectRoot: string) (taskId: string) =
    Path.Combine(projectRoot, ".tasks", taskId, "runtime.json")

let readPersisted (projectRoot: string) (taskId: string) : JsonNode =
    let path = sidecarPath projectRoot taskId

    if not (File.Exists path) then
        failWith (sprintf "the persisted sidecar is missing: %s" path)

    JsonNode.Parse(File.ReadAllText path)

// The authenticated boundary signs the persisted sidecar at task creation, so an answer that cannot authorize must leave the signed bytes and their receipt exactly as the snapshot found them; the tool response carries no receipt, so the snapshot reads the persisted bytes instead.
type SignedState =
    { Bytes: string
      Receipt: string }

let receiptFrame = Regex("^hmac-sha256-v1:[0-9a-f]{64}$", RegexOptions.CultureInvariant)

let signedState (projectRoot: string) (taskId: string) : SignedState =
    let path = sidecarPath projectRoot taskId

    if not (File.Exists path) then
        failWith (sprintf "the persisted sidecar is missing: %s" path)

    let bytes = File.ReadAllBytes path
    let persisted = JsonNode.Parse(Encoding.UTF8.GetString bytes)

    { Bytes = Convert.ToBase64String bytes
      Receipt = strField persisted "integrityReceipt" }

let persistedUserDecisions (persisted: JsonNode) : JsonNode list =
    let decisions = persisted.["decisions"]

    if isNull decisions then
        []
    else
        decisions.AsArray()
        |> Seq.filter (fun decision -> strField decision "authority" = "user")
        |> Seq.toList

let persistedGuard (persisted: JsonNode) (guardId: string) : JsonNode =
    let guards = persisted.["guards"]

    if isNull guards then
        failWith "the persisted task carried no guards"

    guards.AsArray()
    |> Seq.tryFind (fun guard -> strField guard "id" = guardId)
    |> Option.defaultWith (fun () -> failWith (sprintf "the persisted task carried no guard '%s'" guardId))

let waivedBy (task: JsonNode) (guardId: string) =
    let guards = task.["guards"]

    if isNull guards then
        false
    else
        guards.AsArray()
        |> Seq.exists (fun guard ->
            strField guard "id" = guardId
            && (strField guard "disposition").StartsWith("waived:", StringComparison.Ordinal))


let mutable passed = 0
let mutable failed = 0
let failures = ResizeArray<string>()

let check name condition detail =
    if condition then
        passed <- passed + 1
        printfn "CASE %s PASS" name
    else
        failed <- failed + 1
        failures.Add(sprintf "%s :: %s" name detail)
        printfn "CASE %s FAIL %s" name detail

let runCase name (body: Async<unit>) : Async<unit> =
    async {
        try
            do! body
        with error ->
            failed <- failed + 1
            failures.Add(sprintf "%s :: %s" name error.Message)
            printfn "CASE %s FAIL %s" name (error.Message)
    }

// The authenticated state of a task is signed at creation, so an answer that cannot authorize must leave the signed bytes and the receipt that binds them unchanged.
let checkSignedStateUnchanged (prefix: string) (before: SignedState) (after: SignedState) =
    check
        (prefix + ".preservesSignedBytes")
        (before.Bytes = after.Bytes)
        "the persisted signed bytes changed"

    check
        (prefix + ".unchangedReceipt")
        (before.Receipt = after.Receipt)
        (sprintf "the integrity receipt changed from '%s' to '%s'" before.Receipt after.Receipt)

    check
        (prefix + ".receiptFrame")
        (receiptFrame.IsMatch after.Receipt)
        (sprintf "the integrity receipt is not a framed HMAC-SHA256 tag: '%s'" after.Receipt)


let waiveArguments (projectRoot: string) (taskId: string) (revision: int) (guardId: string) =
    jobj
        [ "projectRoot", jstr projectRoot
          "taskId", jstr taskId
          "expectedStateRevision", jint revision
          "command", jobj [ "type", jstr "waive-guard"; "guardId", jstr guardId ] ]

let startArguments (projectRoot: string) (taskId: string) (revision: int) =
    jobj
        [ "projectRoot", jstr projectRoot
          "taskId", jstr taskId
          "expectedStateRevision", jint revision
          "command", jobj [ "type", jstr "start"; "workItemId", jstr "W1" ] ]

// One protected operation end to end, including the restart that proves the persisted state survives against the same application data.
let protectedScenario
    (host: McpHost)
    (projectRoot: string)
    (dllPath: string)
    (catalogPath: string)
    (catalogHash: string)
    (workspace: string)
    (environment: (string * string) list)
    : Async<unit> =
    async {
        let taskId = "MCP-101"
        let! fixture = createFixture host projectRoot taskId "harness"
        let guardId = pickUserWaivableGuard fixture
        let revision = intField fixture "stateRevision"

        // A routine apply on the protected fixture stays prompt-free.
        let routineId = freshRequestId ()
        do! sendToolCall host routineId "task_apply" (startArguments projectRoot taskId revision)
        let! routineResponse = awaitResponse host routineId 60000

        match routineResponse with
        | None -> check "routine.completes" false "the routine apply did not complete"
        | Some message ->
            let payload = toolPayload message
            check "routine.completes" true ""
            check "routine.succeeds" (boolField payload "ok") (sprintf "the routine apply failed: %s" (payload.ToJsonString()))
            let task = payload.["task"]
            check "routine.oneRevision" (intField task "stateRevision" = revision + 1) "the routine apply did not advance exactly one revision"

            let! emitted = awaitServerRequest host "elicitation/create" 750
            check "routine.noElicitation" emitted.IsNone "a routine apply emitted an elicitation request"

        let appliedRevision = revision + 1
        let waiveId = freshRequestId ()
        let command = jobj [ "type", jstr "waive-guard"; "guardId", jstr guardId ]

        do!
            sendToolCall
                host
                waiveId
                "task_apply"
                (jobj
                    [ "projectRoot", jstr projectRoot
                      "taskId", jstr taskId
                      "expectedStateRevision", jint appliedRevision
                      "command", command ])

        let! elicitation = awaitServerRequest host "elicitation/create" 30000

        match elicitation with
        | None -> check "protected.elicitationEmitted" false "no elicitation was emitted for the protected operation"
        | Some request ->
            check "protected.elicitationEmitted" true ""
            let elicitationId = strField request "id"
            check "protected.reservedId" (elicitationId.StartsWith("workflow-server:", StringComparison.Ordinal)) (sprintf "the elicitation id is '%s'" elicitationId)

            let parameters = request.["params"]
            let message = strField parameters "message"

            // The prompt names the decision kind and the decision target, but their wire encoding is not part of the prompt text contract, so each assertion reads the line that carries the value.
            let messageLine (label: string) =
                message.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryFind (fun line -> line.StartsWith(label + ":", StringComparison.Ordinal))
                |> Option.defaultValue ""

            let kindLine = messageLine "Decision kind"
            let targetLine = messageLine "Decision target"
            check "protected.messageHasTask" (message.Contains(taskId, StringComparison.Ordinal)) "the elicitation message does not name the task"
            check "protected.messageHasOperation" (message.Contains("Operation: task_apply.waive-guard", StringComparison.Ordinal)) "the elicitation message does not name the operation"
            check "protected.messageHasKind" (kindLine.Contains("waiverDecision", StringComparison.OrdinalIgnoreCase)) (sprintf "the elicitation message does not name the decision kind: %s" message)
            check "protected.messageHasTarget" (targetLine.Contains("guardDisposition", StringComparison.OrdinalIgnoreCase) && targetLine.Contains(guardId, StringComparison.Ordinal)) (sprintf "the elicitation message does not name the decision target: %s" message)
            check "protected.messageHasRevision" (message.Contains(sprintf "Expected state revision: %d" appliedRevision, StringComparison.Ordinal)) "the elicitation message does not carry the expected state revision"
            check "protected.messageHasRoot" (message.Contains(projectRoot, StringComparison.Ordinal)) "the elicitation message does not carry the project root"
            check "protected.messageHasCommand" (message.Contains(command.ToJsonString(), StringComparison.Ordinal)) "the elicitation message does not carry the complete validated command"

            let schema = parameters.["requestedSchema"]
            let properties = schema.["properties"]
            check "protected.schemaConfirmBoolean" (strField properties.["confirm"] "type" = "boolean") "the requested schema does not require a boolean confirm"

            let required =
                schema.["required"].AsArray()
                |> Seq.map (fun value -> value.GetValue<string>())
                |> Seq.toList

            check "protected.schemaRequiresConfirm" (List.contains "confirm" required) "the requested schema does not require confirm"

            let! early = awaitResponse host waiveId 500
            check "protected.pendingBeforeAnswer" early.IsNone "the protected call completed before an answer arrived"

            do! send host (acceptAnswer elicitationId)
            let! applied = awaitResponse host waiveId 60000

            match applied with
            | None -> check "protected.acceptCompletes" false "the protected call did not complete after the answer"
            | Some answer ->
                let payload = toolPayload answer
                check "protected.acceptCompletes" true ""
                check "protected.acceptSucceeds" (boolField payload "ok") (sprintf "the protected apply failed: %s" (payload.ToJsonString()))
                let task = payload.["task"]
                check "protected.oneRevision" (intField task "stateRevision" = appliedRevision + 1) "the protected apply did not advance exactly one revision"

                let persisted = readPersisted projectRoot taskId
                let userDecisions = persistedUserDecisions persisted
                check "protected.singleUserDecision" (userDecisions.Length = 1) (sprintf "the persisted task carries %d user decisions" userDecisions.Length)

                match userDecisions with
                | [ decision ] ->
                    check "protected.decisionKind" (strField decision "kind" = "waiverDecision") (sprintf "the persisted decision kind is '%s'" (strField decision "kind"))

                    let targets =
                        decision.["targets"].AsArray()
                        |> Seq.map (fun value -> value.GetValue<string>())
                        |> Seq.toList

                    check "protected.decisionTarget" (List.contains (sprintf "guardDisposition:%s" guardId) targets) "the persisted decision does not target the waived guard"
                    check "protected.decisionConfirmationRef" (strField decision "confirmationRef" = elicitationId) "the persisted decision does not carry the elicitation reference"
                | _ -> ()

                let guard = persistedGuard persisted guardId
                check "protected.guardWaived" ((strField guard "disposition").StartsWith("waived:", StringComparison.Ordinal)) (sprintf "the persisted guard disposition is '%s'" (strField guard "disposition"))
                check "protected.receiptPresent" (not (String.IsNullOrWhiteSpace (strField persisted "integrityReceipt"))) "the persisted task carries no integrity receipt"

        // The persisted state must survive a restart against the same application data, which also proves the receipt still verifies.
        kill host
        let restarted = startHost dllPath catalogPath catalogHash workspace environment

        try
            let! _ = initializeHost restarted
            let readId = freshRequestId ()
            do! sendToolCall restarted readId "task_get" (jobj [ "projectRoot", jstr projectRoot; "taskId", jstr taskId ])
            let! readResponse = awaitResponse restarted readId 60000

            match readResponse with
            | None -> check "restart.readsPersistedState" false "the restarted producer did not answer the read"
            | Some message ->
                let payload = toolPayload message
                check "restart.readsPersistedState" (boolField payload "ok") (sprintf "the restarted producer failed to read the persisted state: %s" (payload.ToJsonString()))
                let task = payload.["task"]
                check "restart.persistedRevision" (intField task "stateRevision" = appliedRevision + 1) "the restarted producer reported a different revision"
                check "restart.persistedWaiver" (waivedBy task guardId) "the restarted producer does not report the waived guard"

            let validateId = freshRequestId ()
            do! sendToolCall restarted validateId "task_validate" (jobj [ "projectRoot", jstr projectRoot; "taskId", jstr taskId ])
            let! validateResponse = awaitResponse restarted validateId 60000

            match validateResponse with
            | None -> check "restart.validates" false "the restarted producer did not answer the validation"
            | Some message ->
                let payload = toolPayload message
                check "restart.validates" (boolField payload "ok") (sprintf "the restarted producer failed to validate the persisted state: %s" (payload.ToJsonString()))
        finally
            kill restarted
    }

// A non-accept answer may never authorize the operation.
type AnswerVariant =
    { Name: string
      Answer: string -> string
      PendingSurvives: bool }

let answerVariants: AnswerVariant list =
    [ { Name = "decline"
        Answer =
            (fun id ->
                (jobj
                    [ "jsonrpc", jstr "2.0"
                      "id", jstr id
                      "result", jobj [ "action", jstr "decline" ] ])
                    .ToJsonString())
        PendingSurvives = false }
      { Name = "cancel"
        Answer =
            (fun id ->
                (jobj
                    [ "jsonrpc", jstr "2.0"
                      "id", jstr id
                      "result", jobj [ "action", jstr "cancel" ] ])
                    .ToJsonString())
        PendingSurvives = false }
      { Name = "confirmFalse"
        Answer =
            (fun id ->
                (jobj
                    [ "jsonrpc", jstr "2.0"
                      "id", jstr id
                      "result",
                      jobj
                          [ "action", jstr "accept"
                            "content", jobj [ "confirm", jbool false ] ] ])
                    .ToJsonString())
        PendingSurvives = false }
      { Name = "malformed"
        Answer = (fun _ -> "{\"jsonrpc\":\"2.0\",\"id\":")
        PendingSurvives = true }
      { Name = "errorResponse"
        Answer =
            (fun id ->
                (jobj
                    [ "jsonrpc", jstr "2.0"
                      "id", jstr id
                      "error", jobj [ "code", jint -32603; "message", jstr "synthetic client failure" ] ])
                    .ToJsonString())
        PendingSurvives = false }
      { Name = "unknownResponseId"
        Answer =
            (fun _ ->
                (jobj
                    [ "jsonrpc", jstr "2.0"
                      "id", jstr "workflow-server:unknown-0000"
                      "result",
                      jobj
                          [ "action", jstr "accept"
                            "content", jobj [ "confirm", jbool true ] ] ])
                    .ToJsonString())
        PendingSurvives = true } ]

let rejectionScenario (host: McpHost) (projectRoot: string) (variant: AnswerVariant) (taskId: string) : Async<unit> =
    async {
        let! fixture = createFixture host projectRoot taskId "harness"
        let guardId = pickUserWaivableGuard fixture
        let revision = intField fixture "stateRevision"

        // The canonical fixture is already authenticated at creation, so the signed state is captured before the protected call is even sent.
        let signedBefore = signedState projectRoot taskId
        let requestId = freshRequestId ()
        do! sendToolCall host requestId "task_apply" (waiveArguments projectRoot taskId revision guardId)
        let! elicitation = awaitServerRequest host "elicitation/create" 30000

        match elicitation with
        | None -> check (variant.Name + ".elicitationEmitted") false "no elicitation was emitted"
        | Some request ->
            check (variant.Name + ".elicitationEmitted") true ""
            let elicitationId = strField request "id"
            do! send host (variant.Answer elicitationId)

            if variant.PendingSurvives then
                let! early = awaitResponse host requestId 1200
                check (variant.Name + ".callStaysPending") early.IsNone "the protected call completed on an answer that cannot authorize it"
            else
                let! rejected = awaitResponse host requestId 20000

                match rejected with
                | None -> check (variant.Name + ".callRejected") false "the protected call did not complete after the rejected answer"
                | Some message ->
                    let payload = toolPayload message
                    check (variant.Name + ".callRejected") true ""
                    check (variant.Name + ".rejectedIsError") (not (boolField payload "ok")) "the rejected answer produced a successful result"
                    let (code, detail) = toolError payload
                    check (variant.Name + ".rejectedCode") (code = "INVALID_TRANSITION") (sprintf "the rejection code is '%s'" code)
                    check (variant.Name + ".rejectedMessage") (detail.Contains("rejected or cancelled", StringComparison.Ordinal)) (sprintf "the rejection message is '%s'" detail)

            let persisted = readPersisted projectRoot taskId
            check (variant.Name + ".noUserDecision") (persistedUserDecisions persisted).IsEmpty "an answer that cannot authorize persisted a User decision"
            check (variant.Name + ".revisionUnchanged") (intField persisted "stateRevision" = revision) "an answer that cannot authorize advanced the state revision"
            checkSignedStateUnchanged variant.Name signedBefore (signedState projectRoot taskId)
            let guard = persistedGuard persisted guardId
            check (variant.Name + ".guardNotWaived") (strField guard "disposition" = "applicable") (sprintf "the guard disposition is '%s'" (strField guard "disposition"))

            // A later strict answer may only authorize the call it belongs to.
            do! send host (acceptAnswer elicitationId)

            if variant.PendingSurvives then
                let! applied = awaitResponse host requestId 30000

                match applied with
                | None -> check (variant.Name + ".laterAcceptApplies") false "the later strict answer did not complete the pending call"
                | Some message ->
                    let payload = toolPayload message
                    check (variant.Name + ".laterAcceptApplies") (boolField payload "ok") (sprintf "the later strict answer failed: %s" (payload.ToJsonString()))
                    let appliedState = readPersisted projectRoot taskId
                    check (variant.Name + ".laterAcceptSingleDecision") ((persistedUserDecisions appliedState).Length = 1) "the later strict answer did not persist exactly one User decision"
            else
                let! extra = awaitResponse host requestId 1200
                check (variant.Name + ".consumedPendingNotReauthorized") extra.IsNone "a second answer completed an already rejected call"
                let persisted = readPersisted projectRoot taskId
                check (variant.Name + ".consumedPendingNoDecision") (persistedUserDecisions persisted).IsEmpty "a second answer persisted a User decision"
    }

// Ordinary traffic must complete while a form is pending.
let pendingTrafficScenario (host: McpHost) (projectRoot: string) : Async<unit> =
    async {
        let taskId = "MCP-300"
        let! fixture = createFixture host projectRoot taskId "harness"
        let guardId = pickUserWaivableGuard fixture
        let revision = intField fixture "stateRevision"
        let requestId = freshRequestId ()
        do! sendToolCall host requestId "task_apply" (waiveArguments projectRoot taskId revision guardId)
        let! elicitation = awaitServerRequest host "elicitation/create" 30000

        match elicitation with
        | None -> check "pending.elicitationEmitted" false "no elicitation was emitted"
        | Some request ->
            check "pending.elicitationEmitted" true ""
            let elicitationId = strField request "id"

            let pingId = freshRequestId ()

            do!
                send
                    host
                    ((jobj [ "jsonrpc", jstr "2.0"; "id", jint pingId; "method", jstr "ping" ]).ToJsonString())

            let! pingResponse = awaitResponse host pingId 20000
            check "pending.pingCompletes" pingResponse.IsSome "the ping did not complete while the form was pending"

            let readId = freshRequestId ()
            do! sendToolCall host readId "task_get" (jobj [ "projectRoot", jstr projectRoot; "taskId", jstr taskId ])
            let! readResponse = awaitResponse host readId 20000
            check "pending.readCompletes" readResponse.IsSome "the ordinary read did not complete while the form was pending"

            let! early = awaitResponse host requestId 300
            check "pending.formStillPending" early.IsNone "the protected call completed while other traffic was served"

            do! send host (acceptAnswer elicitationId)
            let! applied = awaitResponse host requestId 60000

            match applied with
            | None -> check "pending.acceptApplies" false "the protected call did not complete after the answer"
            | Some message ->
                let payload = toolPayload message
                check "pending.acceptApplies" (boolField payload "ok") (sprintf "the protected apply failed: %s" (payload.ToJsonString()))
    }

// A task that changes while the form is pending invalidates the answer.
let staleTaskScenario (host: McpHost) (projectRoot: string) : Async<unit> =
    async {
        let taskId = "MCP-350"
        let! fixture = createFixture host projectRoot taskId "harness"
        let guardId = pickUserWaivableGuard fixture
        let revision = intField fixture "stateRevision"
        let requestId = freshRequestId ()
        do! sendToolCall host requestId "task_apply" (waiveArguments projectRoot taskId revision guardId)
        let! elicitation = awaitServerRequest host "elicitation/create" 30000

        match elicitation with
        | None -> check "stale.elicitationEmitted" false "no elicitation was emitted"
        | Some request ->
            check "stale.elicitationEmitted" true ""
            let elicitationId = strField request "id"

            // The task changes underneath the pending form.
            let concurrentId = freshRequestId ()
            do! sendToolCall host concurrentId "task_apply" (startArguments projectRoot taskId revision)
            let! concurrent = awaitResponse host concurrentId 30000

            match concurrent with
            | None -> check "stale.concurrentChangeApplied" false "the concurrent change did not complete"
            | Some message ->
                let payload = toolPayload message
                check "stale.concurrentChangeApplied" (boolField payload "ok") (sprintf "the concurrent change failed: %s" (payload.ToJsonString()))

            // The concurrent ordinary change is the last legitimate write, so the signed state is captured after it invalidates the pending approval.
            let signedBefore = signedState projectRoot taskId

            do! send host (acceptAnswer elicitationId)
            let! applied = awaitResponse host requestId 60000

            match applied with
            | None -> check "stale.approvalRejected" false "the protected call did not complete after the answer"
            | Some message ->
                let payload = toolPayload message
                check "stale.approvalRejected" (not (boolField payload "ok")) "the stale approval was accepted"
                let (code, detail) = toolError payload
                check "stale.approvalCode" (code = "INVALID_TRANSITION") (sprintf "the stale approval code is '%s'" code)
                check "stale.approvalMessage" (detail.Contains("task state changed during trusted confirmation", StringComparison.Ordinal)) (sprintf "the stale approval message is '%s'" detail)

            let persisted = readPersisted projectRoot taskId
            check "stale.noUserDecision" (persistedUserDecisions persisted).IsEmpty "the stale approval persisted a User decision"
            check "stale.revisionUnchanged" (intField persisted "stateRevision" = revision + 1) "the stale approval advanced the state revision"
            checkSignedStateUnchanged "stale" signedBefore (signedState projectRoot taskId)
            let guard = persistedGuard persisted guardId
            check "stale.guardNotWaived" (strField guard "disposition" = "applicable") (sprintf "the guard disposition is '%s'" (strField guard "disposition"))
    }

// A duplicate answer may not authorize another request.
let duplicateScenario (host: McpHost) (projectRoot: string) : Async<unit> =
    async {
        let firstTaskId = "MCP-500"
        let! firstFixture = createFixture host projectRoot firstTaskId "harness"
        let firstGuard = pickUserWaivableGuard firstFixture
        let firstRevision = intField firstFixture "stateRevision"
        let firstRequest = freshRequestId ()
        do! sendToolCall host firstRequest "task_apply" (waiveArguments projectRoot firstTaskId firstRevision firstGuard)
        let! firstElicitation = awaitServerRequest host "elicitation/create" 30000
        let mutable consumedId = ""

        match firstElicitation with
        | None -> check "duplicate.firstElicitation" false "no elicitation was emitted for the first task"
        | Some request ->
            check "duplicate.firstElicitation" true ""
            consumedId <- strField request "id"
            do! send host (acceptAnswer consumedId)
            let! applied = awaitResponse host firstRequest 30000

            match applied with
            | None -> check "duplicate.firstApplied" false "the first protected call did not complete"
            | Some message ->
                let payload = toolPayload message
                check "duplicate.firstApplied" (boolField payload "ok") (sprintf "the first protected apply failed: %s" (payload.ToJsonString()))

        let secondTaskId = "MCP-501"
        let! secondFixture = createFixture host projectRoot secondTaskId "harness"
        let secondGuard = pickUserWaivableGuard secondFixture
        let secondRevision = intField secondFixture "stateRevision"
        let secondRequest = freshRequestId ()
        do! sendToolCall host secondRequest "task_apply" (waiveArguments projectRoot secondTaskId secondRevision secondGuard)
        let! secondElicitation = awaitServerRequest host "elicitation/create" 30000

        match secondElicitation with
        | None -> check "duplicate.secondElicitation" false "no elicitation was emitted for the second task"
        | Some request ->
            check "duplicate.secondElicitation" true ""
            let secondId = strField request "id"

            if not (String.IsNullOrWhiteSpace consumedId) then
                // The consumed answer is replayed while another form is pending.
                do! send host (acceptAnswer consumedId)
                let! early = awaitResponse host secondRequest 1200
                check "duplicate.replayDoesNotAuthorize" early.IsNone "a replayed answer completed another request"

                let persisted = readPersisted projectRoot secondTaskId
                check "duplicate.replayNoDecision" (persistedUserDecisions persisted).IsEmpty "a replayed answer persisted a User decision"

                do! send host (acceptAnswer secondId)
                let! applied = awaitResponse host secondRequest 30000

                match applied with
                | None -> check "duplicate.correctAnswerApplies" false "the correct answer did not complete the second call"
                | Some message ->
                    let payload = toolPayload message
                    check "duplicate.correctAnswerApplies" (boolField payload "ok") (sprintf "the correct answer failed: %s" (payload.ToJsonString()))
                    let appliedState = readPersisted projectRoot secondTaskId
                    check "duplicate.singleUserDecision" ((persistedUserDecisions appliedState).Length = 1) "the second task did not persist exactly one User decision"
    }

// A pending form must not keep the producer alive past stdin EOF.
let eofScenario
    (dllPath: string)
    (catalogPath: string)
    (catalogHash: string)
    (workspace: string)
    (projectRoot: string)
    (environment: (string * string) list)
    : Async<unit> =
    async {
        let host = startHost dllPath catalogPath catalogHash workspace environment

        try
            let! _ = initializeHost host
            let taskId = "MCP-400"
            let! fixture = createFixture host projectRoot taskId "harness"
            let guardId = pickUserWaivableGuard fixture
            let revision = intField fixture "stateRevision"
            let requestId = freshRequestId ()
            do! sendToolCall host requestId "task_apply" (waiveArguments projectRoot taskId revision guardId)
            let! elicitation = awaitServerRequest host "elicitation/create" 30000

            match elicitation with
            | None -> check "eof.elicitationEmitted" false "no elicitation was emitted"
            | Some _ ->
                check "eof.elicitationEmitted" true ""
                closeInput host
                let! exited = waitForExitAsync host 30000
                check "eof.exitsBounded" exited "the producer did not exit within the bound after stdin EOF"

                if exited then
                    check "eof.exitCode" (host.Child.ExitCode = 0) (sprintf "the producer exited with code %d" host.Child.ExitCode)

                let persisted = readPersisted projectRoot taskId
                check "eof.noUserDecision" (persistedUserDecisions persisted).IsEmpty "the EOF exit persisted a User decision"
                check "eof.revisionUnchanged" (intField persisted "stateRevision" = revision) "the EOF exit advanced the state revision"
        finally
            kill host
    }


// A second protected operation on the same task must be authorized by its own confirmation after a restart of the producer, and the re-signed state must verify against the same key after another restart.
let secondProtectedOperationScenario
    (dllPath: string)
    (catalogPath: string)
    (catalogHash: string)
    (workspace: string)
    (projectRoot: string)
    (environment: (string * string) list)
    : Async<unit> =
    async {
        let taskId = "MCP-600"
        let mutable firstGuard = ""
        let mutable secondGuard = ""
        let mutable revision = -1
        let mutable firstElicitationId = ""

        let first = startHost dllPath catalogPath catalogHash workspace environment

        try
            let! _ = initializeHost first
            let! fixture = createFixture first projectRoot taskId "harness"
            firstGuard <- pickUserWaivableGuard fixture
            secondGuard <- pickDistinctUserWaivableGuard fixture firstGuard
            revision <- intField fixture "stateRevision"

            let requestId = freshRequestId ()
            do! sendToolCall first requestId "task_apply" (waiveArguments projectRoot taskId revision firstGuard)
            let! elicitation = awaitServerRequest first "elicitation/create" 30000

            match elicitation with
            | None -> check "second.firstElicitation" false "no elicitation was emitted for the first protected operation"
            | Some request ->
                check "second.firstElicitation" true ""
                firstElicitationId <- strField request "id"
                do! send first (acceptAnswer firstElicitationId)
                let! applied = awaitResponse first requestId 60000

                match applied with
                | None -> check "second.firstApplied" false "the first protected operation did not complete"
                | Some message ->
                    let payload = toolPayload message
                    check "second.firstApplied" true ""
                    check "second.firstSucceeds" (boolField payload "ok") (sprintf "the first protected apply failed: %s" (payload.ToJsonString()))
                    let task = payload.["task"]
                    check "second.firstOneRevision" (intField task "stateRevision" = revision + 1) "the first protected apply did not advance exactly one revision"

                    let persisted = readPersisted projectRoot taskId
                    check "second.firstSingleUserDecision" ((persistedUserDecisions persisted).Length = 1) "the first protected apply did not persist exactly one User decision"
                    check "second.firstGuardWaived" (waivedBy task firstGuard) "the first protected apply did not waive the first guard"
        finally
            kill first

        // The confirmed state must survive a restart of the producer against the same application data and workspace.
        let second = startHost dllPath catalogPath catalogHash workspace environment

        try
            let! _ = initializeHost second
            let readId = freshRequestId ()
            do! sendToolCall second readId "task_get" (jobj [ "projectRoot", jstr projectRoot; "taskId", jstr taskId ])
            let! readResponse = awaitResponse second readId 60000

            match readResponse with
            | None -> check "second.restartReadsState" false "the restarted producer did not answer the read"
            | Some message ->
                let payload = toolPayload message
                check "second.restartReadsState" (boolField payload "ok") (sprintf "the restarted producer failed to read the persisted state: %s" (payload.ToJsonString()))
                let task = payload.["task"]
                check "second.restartRevision" (intField task "stateRevision" = revision + 1) "the restarted producer reported a different revision"
                check "second.restartFirstWaiver" (waivedBy task firstGuard) "the restarted producer does not report the first waived guard"

            // The second protected operation targets the other, still applicable, user-waivable guard of the same task.
            let secondRequest = freshRequestId ()
            do! sendToolCall second secondRequest "task_apply" (waiveArguments projectRoot taskId (revision + 1) secondGuard)
            let! elicitation = awaitServerRequest second "elicitation/create" 30000

            match elicitation with
            | None -> check "second.secondElicitation" false "no elicitation was emitted for the second protected operation"
            | Some request ->
                check "second.secondElicitation" true ""
                let secondElicitationId = strField request "id"
                check "second.distinctElicitation" (secondElicitationId <> firstElicitationId) "the second protected operation reused the first elicitation id"

                // The consumed answer of the first operation may not authorize the second one.
                let signedBefore = signedState projectRoot taskId

                do! send second (acceptAnswer firstElicitationId)
                let! early = awaitResponse second secondRequest 1500
                check "second.replayDoesNotAuthorize" early.IsNone "the replayed first answer completed the second protected operation"
                checkSignedStateUnchanged "second.replay" signedBefore (signedState projectRoot taskId)

                let replayed = readPersisted projectRoot taskId
                check "second.replaySingleUserDecision" ((persistedUserDecisions replayed).Length = 1) "the replayed answer persisted a second User decision"
                check "second.replaySecondGuardApplicable" (strField (persistedGuard replayed secondGuard) "disposition" = "applicable") "the replayed answer disposed the second guard"

                do! send second (acceptAnswer secondElicitationId)
                let! applied = awaitResponse second secondRequest 60000

                match applied with
                | None -> check "second.secondApplied" false "the second protected operation did not complete after its own answer"
                | Some message ->
                    let payload = toolPayload message
                    check "second.secondApplied" true ""
                    check "second.secondSucceeds" (boolField payload "ok") (sprintf "the second protected apply failed: %s" (payload.ToJsonString()))
                    let task = payload.["task"]
                    check "second.secondOneRevision" (intField task "stateRevision" = revision + 2) "the second protected apply did not advance exactly one revision"

                    let persisted = readPersisted projectRoot taskId
                    let userDecisions = persistedUserDecisions persisted
                    check "second.twoUserDecisions" (userDecisions.Length = 2) (sprintf "the persisted task carries %d user decisions" userDecisions.Length)
                    check "second.firstGuardWaived" ((strField (persistedGuard persisted firstGuard) "disposition").StartsWith("waived:", StringComparison.Ordinal)) "the first guard is no longer waived"
                    check "second.secondGuardWaived" ((strField (persistedGuard persisted secondGuard) "disposition").StartsWith("waived:", StringComparison.Ordinal)) "the second guard is not waived"

                    let receipt = strField persisted "integrityReceipt"
                    check "second.receiptFrame" (receiptFrame.IsMatch receipt) (sprintf "the integrity receipt is not a framed HMAC-SHA256 tag: '%s'" receipt)
                    check "second.receiptReissued" (receipt <> signedBefore.Receipt) "the second protected apply did not re-sign the persisted state"
        finally
            kill second

        // The re-signed state must verify against the same key after another restart, which is what the new receipt has to prove.
        let third = startHost dllPath catalogPath catalogHash workspace environment

        try
            let! _ = initializeHost third
            let readId = freshRequestId ()
            do! sendToolCall third readId "task_get" (jobj [ "projectRoot", jstr projectRoot; "taskId", jstr taskId ])
            let! readResponse = awaitResponse third readId 60000

            match readResponse with
            | None -> check "second.receiptSurvivesRestart" false "the restarted producer did not answer the read"
            | Some message ->
                let payload = toolPayload message
                check "second.receiptSurvivesRestart" (boolField payload "ok") (sprintf "the restarted producer failed to verify the re-signed state: %s" (payload.ToJsonString()))
                let task = payload.["task"]
                check "second.restartFinalRevision" (intField task "stateRevision" = revision + 2) "the restarted producer reported a different revision"
                check "second.restartBothWaivers" (waivedBy task firstGuard && waivedBy task secondGuard) "the restarted producer does not report both waived guards"

            let validateId = freshRequestId ()
            do! sendToolCall third validateId "task_validate" (jobj [ "projectRoot", jstr projectRoot; "taskId", jstr taskId ])
            let! validateResponse = awaitResponse third validateId 60000

            match validateResponse with
            | None -> check "second.restartValidates" false "the restarted producer did not answer the validation"
            | Some message ->
                let payload = toolPayload message
                check "second.restartValidates" (boolField payload "ok") (sprintf "the restarted producer failed to validate the re-signed state: %s" (payload.ToJsonString()))
        finally
            kill third
    }


// A native client that cannot answer the elicitation form must fail closed on a protected operation without changing any state, and its failure must be distinguishable from a human decline.
type UnsupportedClientVariant =
    { Name: string
      ProtocolVersion: string
      SupportsForm: bool
      ExpectedNegotiatedVersion: string }

let unsupportedClientVariants: UnsupportedClientVariant list =
    // The accepted protocol version without the elicitation form capability: the client cannot answer the form at all.
    [ { Name = "unsupportedNoForm"
        ProtocolVersion = "2025-11-25"
        SupportsForm = false
        ExpectedNegotiatedVersion = "2025-11-25" }
      // The separately accepted older protocol version, which declares the form capability, so only the negotiated version can disable the protected path.
      { Name = "unsupportedOldProtocol"
        ProtocolVersion = "2024-11-05"
        SupportsForm = true
        ExpectedNegotiatedVersion = "2024-11-05" } ]

let unsupportedClientScenario
    (dllPath: string)
    (catalogPath: string)
    (catalogHash: string)
    (workspace: string)
    (projectRoot: string)
    (environment: (string * string) list)
    (variant: UnsupportedClientVariant)
    (taskId: string)
    : Async<unit> =
    async {
        let host = startHost dllPath catalogPath catalogHash workspace environment

        try
            let! result = initializeHostWith host variant.ProtocolVersion variant.SupportsForm

            check
                (variant.Name + ".negotiatedProtocolVersion")
                (strField result "protocolVersion" = variant.ExpectedNegotiatedVersion)
                (sprintf "the negotiated protocol version is '%s'" (strField result "protocolVersion"))

            let! fixture = createFixture host projectRoot taskId "harness"
            let guardId = pickUserWaivableGuard fixture
            let revision = intField fixture "stateRevision"

            // An ordinary operation stays prompt-free for an unsupported client.
            let routineId = freshRequestId ()
            do! sendToolCall host routineId "task_apply" (startArguments projectRoot taskId revision)
            let! routineResponse = awaitResponse host routineId 60000

            match routineResponse with
            | None -> check (variant.Name + ".routineCompletes") false "the ordinary apply did not complete"
            | Some message ->
                let payload = toolPayload message
                check (variant.Name + ".routineCompletes") true ""
                check (variant.Name + ".routineSucceeds") (boolField payload "ok") (sprintf "the ordinary apply failed: %s" (payload.ToJsonString()))
                let task = payload.["task"]
                check (variant.Name + ".routineOneRevision") (intField task "stateRevision" = revision + 1) "the ordinary apply did not advance exactly one revision"

                let! emitted = awaitServerRequest host "elicitation/create" 750
                check (variant.Name + ".routineNoElicitation") emitted.IsNone "the ordinary apply emitted an elicitation request"

            // The protected operation fails closed for this client.
            let appliedRevision = revision + 1
            let signedBefore = signedState projectRoot taskId
            let protectedId = freshRequestId ()
            do! sendToolCall host protectedId "task_apply" (waiveArguments projectRoot taskId appliedRevision guardId)
            let! protectedResponse = awaitResponse host protectedId 30000

            match protectedResponse with
            | None -> check (variant.Name + ".protectedBoundedFailure") false "the protected apply did not complete"
            | Some message ->
                let payload = toolPayload message
                check (variant.Name + ".protectedBoundedFailure") true ""
                check (variant.Name + ".protectedFailed") (not (boolField payload "ok")) "the protected apply reported success for an unsupported client"

                let errorBlock = payload.["error"]
                check (variant.Name + ".protectedCarriesError") (not (isNull errorBlock)) "the protected apply carried no error block"

                if not (isNull errorBlock) then
                    let (code, detail) = toolError payload
                    check (variant.Name + ".protectedErrorCode") (code = "INVALID_TRANSITION") (sprintf "the protected failure code is '%s'" code)

                    // The unsupported-client failure must be distinguishable from a human decline, which the producer reports with its own wording.
                    check
                        (variant.Name + ".diagnosisDistinguishable")
                        (not (detail.Contains("rejected or cancelled", StringComparison.Ordinal)))
                        (sprintf "the unsupported-client failure is reported as '%s: %s', which is the same wording as a human decline" code detail)
                else
                    ()

                let! emitted = awaitServerRequest host "elicitation/create" 1500
                check (variant.Name + ".protectedNoElicitation") emitted.IsNone "the protected apply emitted an elicitation request to an unsupported client"

            let persisted = readPersisted projectRoot taskId
            check (variant.Name + ".noUserDecision") (persistedUserDecisions persisted).IsEmpty "the unsupported protected apply persisted a User decision"
            check (variant.Name + ".revisionUnchanged") (intField persisted "stateRevision" = appliedRevision) "the unsupported protected apply advanced the state revision"
            checkSignedStateUnchanged variant.Name signedBefore (signedState projectRoot taskId)
            let guard = persistedGuard persisted guardId
            check (variant.Name + ".guardNotWaived") (strField guard "disposition" = "applicable") (sprintf "the guard disposition is '%s'" (strField guard "disposition"))
        finally
            kill host
    }


// The producer ends a protected operation at its own two-minute deadline when the answer never arrives; the operation must fail without any state change, the pending confirmation must be consumed, and the transport must stay usable.
let timeoutScenario
    (dllPath: string)
    (catalogPath: string)
    (catalogHash: string)
    (workspace: string)
    (projectRoot: string)
    (environment: (string * string) list)
    : Async<unit> =
    async {
        let host = startHost dllPath catalogPath catalogHash workspace environment

        try
            let! _ = initializeHost host
            let taskId = "MCP-620"
            let! fixture = createFixture host projectRoot taskId "harness"
            let guardId = pickUserWaivableGuard fixture
            let revision = intField fixture "stateRevision"
            let signedBefore = signedState projectRoot taskId
            let requestId = freshRequestId ()
            do! sendToolCall host requestId "task_apply" (waiveArguments projectRoot taskId revision guardId)
            let! elicitation = awaitServerRequest host "elicitation/create" 30000

            match elicitation with
            | None -> check "timeout.elicitationEmitted" false "no elicitation was emitted"
            | Some request ->
                check "timeout.elicitationEmitted" true ""
                let elicitationId = strField request "id"

                // The answer is withheld past the producer's own two-minute deadline, so the harness bound must stay longer than that deadline.
                let timer = Stopwatch.StartNew()
                let! response = awaitResponse host requestId 140000
                timer.Stop()
                printfn "NOTE timeout.elapsedMs=%d" timer.ElapsedMilliseconds

                match response with
                | None -> check "timeout.boundedFailure" false "the protected call did not complete within the bounded timeout"
                | Some message ->
                    check "timeout.boundedFailure" true ""
                    check "timeout.actualDeadline" (timer.ElapsedMilliseconds >= 115000 && timer.ElapsedMilliseconds <= 140000) (sprintf "the protected call completed after %d ms" timer.ElapsedMilliseconds)

                    let payload = toolPayload message
                    check "timeout.failed" (not (boolField payload "ok")) "the timed-out protected call reported success"

                    let errorBlock = payload.["error"]
                    check "timeout.carriesError" (not (isNull errorBlock)) "the timed-out protected call carried no error block"

                    if not (isNull errorBlock) then
                        let (code, detail) = toolError payload
                        check "timeout.errorCode" (code = "INVALID_TRANSITION") (sprintf "the timeout error code is '%s'" code)
                        printfn "NOTE timeout.errorMessage=%s" detail
                    else
                        ()

                    // The late answer of the timed-out form may not mutate the state, and the pending confirmation is consumed.
                    do! send host (acceptAnswer elicitationId)
                    do! Async.Sleep 1500
                    let! extra = awaitResponse host requestId 1500
                    check "timeout.noSecondResponse" extra.IsNone "the timed-out protected call completed twice"
                    checkSignedStateUnchanged "timeout" signedBefore (signedState projectRoot taskId)

                    let persisted = readPersisted projectRoot taskId
                    check "timeout.noUserDecision" (persistedUserDecisions persisted).IsEmpty "the timed-out protected call persisted a User decision"
                    check "timeout.revisionUnchanged" (intField persisted "stateRevision" = revision) "the timed-out protected call advanced the state revision"
                    let guard = persistedGuard persisted guardId
                    check "timeout.guardNotWaived" (strField guard "disposition" = "applicable") (sprintf "the guard disposition is '%s'" (strField guard "disposition"))

                    // The transport stays usable: the ping completes and a new protected operation can be confirmed.
                    let pingId = freshRequestId ()
                    do! send host ((jobj [ "jsonrpc", jstr "2.0"; "id", jint pingId; "method", jstr "ping" ]).ToJsonString())
                    let! pingResponse = awaitResponse host pingId 20000
                    check "timeout.pingCompletes" pingResponse.IsSome "the ping did not complete after the timeout"

                    let nextId = freshRequestId ()
                    do! sendToolCall host nextId "task_apply" (waiveArguments projectRoot taskId revision guardId)
                    let! nextElicitation = awaitServerRequest host "elicitation/create" 30000

                    match nextElicitation with
                    | None -> check "timeout.newRequestElicitation" false "the new protected operation emitted no elicitation"
                    | Some next ->
                        check "timeout.newRequestElicitation" true ""
                        let nextElicitationId = strField next "id"
                        check "timeout.newRequestDistinct" (nextElicitationId <> elicitationId) "the new protected operation reused the timed-out elicitation id"

                        do! send host (acceptAnswer nextElicitationId)
                        let! applied = awaitResponse host nextId 60000

                        match applied with
                        | None -> check "timeout.newRequestApplies" false "the new protected operation did not complete"
                        | Some appliedMessage ->
                            let payload = toolPayload appliedMessage
                            check "timeout.newRequestApplies" (boolField payload "ok") (sprintf "the new protected apply failed: %s" (payload.ToJsonString()))
                            let task = payload.["task"]
                            check "timeout.newRequestOneRevision" (intField task "stateRevision" = revision + 1) "the new protected apply did not advance exactly one revision"
        finally
            kill host
    }


let runIsolationGuard (runRoot: string) (appData: string) (environment: (string * string) list) : Async<unit> =
    async {
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- dotnetHost
        startInfo.ArgumentList.Add "fsi"
        startInfo.ArgumentList.Add "--exec"
        startInfo.ArgumentList.Add "--quiet"
        startInfo.ArgumentList.Add guardScript
        startInfo.WorkingDirectory <- repoRoot
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true

        for (key, value) in environment do
            startInfo.Environment[key] <- value

        startInfo.Environment["MCP_WORKFLOW_TEST_ISOLATED_ROOT"] <- runRoot
        startInfo.Environment["MCP_WORKFLOW_TEST_EXPECTED_APP_DATA"] <- appData
        startInfo.Environment["MCP_WORKFLOW_TEST_REAL_APP_DATA"] <- realAppData
        startInfo.Environment["DOTNET_NOLOGO"] <- "1"
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"

        use guard = Process.Start startInfo
        let! stdout = guard.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
        let! stderr = guard.StandardError.ReadToEndAsync() |> Async.AwaitTask
        do! guard.WaitForExitAsync() |> Async.AwaitTask

        if guard.ExitCode <> 0 then
            failWith (sprintf "the isolation guard failed with code %d: %s" guard.ExitCode (stderr.Trim()))

        let lines = stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

        if not (lines |> Array.exists (fun line -> line = "GUARD OK")) then
            failWith (sprintf "the isolation guard did not confirm the redirected application data directory: %s" (stdout.Trim()))

        let keyDirectory =
            lines
            |> Array.tryFind (fun line -> line.StartsWith("KEYDIR ", StringComparison.Ordinal))
            |> Option.map (fun line -> line.Substring("KEYDIR ".Length).Trim())

        match keyDirectory with
        | None -> failWith "the isolation guard did not report its resolved key directory"
        | Some directory ->
            if not (isInsideDirectory runRoot directory) then
                failWith (sprintf "the isolation guard resolved an unexpected key directory: %s" directory)

            if samePath directory realKeyDirectory then
                failWith (sprintf "the isolation guard resolved the real per-account key directory: %s" directory)
    }


let runSuite () : Async<unit> =
    async {
        let runRoot = Path.Combine(approvedBase, "mcp-protected-" + Guid.NewGuid().ToString("N"))
        let appData = Path.Combine(runRoot, "appdata")
        let home = Path.Combine(runRoot, "home")
        let temp = Path.Combine(runRoot, "temp")
        let workspace = Path.Combine(runRoot, "workspace")
        let projectRoot = Path.Combine(workspace, "project")
        let catalogPath = Path.Combine(runRoot, "profile-catalog.json")

        // The run root belongs to this run only and is removed after every outcome unless a case failed and the artifacts are kept for diagnosis.
        let mutable retainRunRoot = false
        let mutable host = Unchecked.defaultof<McpHost>

        try
            do! assertRequiredSdk ()

            for directory in [ runRoot; appData; home; temp; workspace; projectRoot ] do
                Directory.CreateDirectory directory |> ignore

            // The canonical catalog fixture is an empty catalog; the hash is computed over the exact bytes this run wrote.
            File.WriteAllText(catalogPath, "{\"profiles\":[]}")

            let catalogHash =
                SHA256.HashData(File.ReadAllBytes catalogPath)
                |> Convert.ToHexString
                |> fun value -> value.ToLowerInvariant()

            let environment =
                [ "LOCALAPPDATA", appData
                  "APPDATA", appData
                  "ProgramData", appData
                  "USERPROFILE", home
                  "HOME", home
                  "TEMP", temp
                  "TMP", temp
                  "DOTNET_NOLOGO", "1"
                  "DOTNET_CLI_TELEMETRY_OPTOUT", "1" ]

            // The redirected application-data directory is proven before the producer starts, so the producer can never reach the real per-account key.
            do! runIsolationGuard runRoot appData environment
            check "isolation.guardConfirmed" true ""

            host <- startHost releaseEntryDll catalogPath catalogHash workspace environment
            let! result = initializeHost host
            check "initialize.negotiatedProtocolVersion" (strField result "protocolVersion" = "2025-11-25") (sprintf "the negotiated protocol version is '%s'" (strField result "protocolVersion"))
            check "initialize.acceptsFormElicitation" true ""

            do! runCase "protected" (protectedScenario host projectRoot releaseEntryDll catalogPath catalogHash workspace environment)

            // The protected scenario stops the shared host so the restart reads the persisted state from disk only; the remaining scenarios continue on a fresh host against the same application data.
            kill host
            host <- startHost releaseEntryDll catalogPath catalogHash workspace environment
            let! _ = initializeHost host

            let mutable variantIndex = 200

            for variant in answerVariants do
                variantIndex <- variantIndex + 1
                let taskId = sprintf "MCP-%d" variantIndex
                do! runCase variant.Name (rejectionScenario host projectRoot variant taskId)

            do! runCase "pending" (pendingTrafficScenario host projectRoot)
            do! runCase "stale" (staleTaskScenario host projectRoot)
            do! runCase "duplicate" (duplicateScenario host projectRoot)
            do! runCase "eof" (eofScenario releaseEntryDll catalogPath catalogHash workspace projectRoot environment)

            // The remaining scenarios own their own producer processes, so they never disturb the shared host.
            do! runCase "second" (secondProtectedOperationScenario releaseEntryDll catalogPath catalogHash workspace projectRoot environment)

            let mutable unsupportedIndex = 610

            for variant in unsupportedClientVariants do
                unsupportedIndex <- unsupportedIndex + 1
                let taskId = sprintf "MCP-%d" unsupportedIndex
                do! runCase variant.Name (unsupportedClientScenario releaseEntryDll catalogPath catalogHash workspace projectRoot environment variant taskId)

            // The timed case runs last, because it waits for the producer's own two-minute confirmation deadline.
            do! runCase "timeout" (timeoutScenario releaseEntryDll catalogPath catalogHash workspace projectRoot environment)
        with error ->
            failed <- failed + 1
            failures.Add("suite :: " + error.Message)
            printfn "CASE suite FAIL %s" (error.Message)
            retainRunRoot <- true

        if not (isNull (box host)) then
            kill host

        // Only the run root this invocation created is removed, and only when it lives inside the approved temporary base.
        if not retainRunRoot && Directory.Exists runRoot && isInsideDirectory approvedBase runRoot then
            try
                Directory.Delete(runRoot, true)
            with _ -> ()

        if retainRunRoot then
            printfn "NOTE the failed run root is kept for diagnosis: %s" runRoot
    }

// Standalone entry bridge: FSI needs one synchronous script entry, and the blocking checker allowlists this exact call.
runSuite () |> Async.RunSynchronously

printfn "SUMMARY passed=%d failed=%d" passed failed

if failed > 0 then
    for failure in failures do
        printfn "FAILURE %s" failure

    exit 1
