// Deterministic coverage for the platform-internal Workflow MCP boundary:
// the native stdio handshake and tool surface, transport parity with the retained library and
// CLI paths, structured results, bounded failures, workspace validation,
// expected-revision CAS, and the absence of authority/receipt/effect ingress.
// Every child process is a fixed repository script or producer artifact; there is no LLM, network, or model dependency.
// Plain FSI harness because the frozen solution forbids
// adding a project/package system.

#load "../domain/ComputationExpressions.fs"
#load "../domain/Workflow.fs"
#load "../protocol/Adapter.fs"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Workflow
open WorkflowAdapter

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let scriptDirectory = Path.Combine(repoRoot, "workflow", "tests")

// The frozen framework-dependent distribution has no apphost. Consumer
// semantics resolve the pinned dotnet host and invoke
// `dotnet exec <entry DLL>`.
let requiredSdk = "11.0.100-rc.1.26425.128"

let releaseEntryDll =
    Path.Combine(repoRoot, "workflow", "bin", "Release", "net11.0", "Mcp.Workflow.dll")

let resolveDotnetHost () =
    let names = if OperatingSystem.IsWindows() then [ "dotnet.exe" ] else [ "dotnet" ]

    let pathValue =
        match Environment.GetEnvironmentVariable "PATH" with
        | null -> ""
        | value -> value

    let candidate =
        pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun directory ->
            names
            |> List.map (fun name -> Path.Combine(directory.Trim(), name))
            |> List.tryFind File.Exists)

    match candidate with
    | Some host -> Path.GetFullPath host
    | None -> failwithf "the required .NET host '%s' is not on PATH" names.Head

let assertRequiredSdk (host: string) : Async<unit> =
    async {
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- host
        startInfo.ArgumentList.Add "--version"
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.WorkingDirectory <- repoRoot
        use child = Process.Start startInfo
        let stdoutTask = child.StandardOutput.ReadToEndAsync()
        let stderrTask = child.StandardError.ReadToEndAsync()
        do! child.WaitForExitAsync() |> Async.AwaitTask
        let! stdout = stdoutTask |> Async.AwaitTask
        let! stderr = stderrTask |> Async.AwaitTask
        if child.ExitCode <> 0 then return failwithf "could not query the .NET SDK version from '%s': %s" host (stderr.Trim())
        let selected = stdout.Trim()
        if selected <> requiredSdk then return failwithf "the required SDK is '%s' but '%s' reports '%s'" requiredSdk host selected
    }

let dotnetHost = resolveDotnetHost ()

let requireReleaseEntry () =
    if not (File.Exists releaseEntryDll) then
        failwithf "authorized producer Release build is required before process-boundary testing: %s" releaseEntryDll

let assertEqual name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let assertContains name (fragment: string) (text: string) =
    if not (text.Contains(fragment, StringComparison.Ordinal)) then
        failwithf "%s: expected '%s' in '%s'" name fragment text

let expectOk name result =
    match result with
    | Ok value -> value
    | Error error -> failwithf "%s: expected Ok, got Error %s" name (renderError error)

let jobj (fields: (string * JsonNode) list) =
    let node = JsonObject()
    fields |> List.iter (fun (key, value) -> node.[key] <- value)
    node

let jstr (value: string) : JsonNode = JsonValue.Create(value)
let jint (value: int) : JsonNode = JsonValue.Create(value)

let jarr (items: JsonNode list) : JsonNode =
    let array = JsonArray()
    items |> List.iter (fun item -> array.Add item)
    array

let nodeString (node: JsonNode) =
    if isNull node then failwith "expected a JSON string node, got null"
    node.GetValue<string>()

let isJsonNull (node: JsonNode) =
    isNull node || node.GetValueKind() = JsonValueKind.Null

let parseJson name (text: string) =
    try JsonNode.Parse text
    with error -> failwithf "%s: invalid JSON (%s): %s" name error.Message text

// --- fixed child-process runners -------------------------------------------

let runFsi name script arguments : Async<string * string> =
    async {
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- "dotnet"
    startInfo.ArgumentList.Add "fsi"
    startInfo.ArgumentList.Add "--nologo"
    startInfo.ArgumentList.Add script
    arguments |> List.iter startInfo.ArgumentList.Add
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.WorkingDirectory <- repoRoot
    use child = Process.Start startInfo
    let stdoutTask = child.StandardOutput.ReadToEndAsync()
    let stderrTask = child.StandardError.ReadToEndAsync()
    do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds 240000.0) |> Async.AwaitTask
    let! stdout = stdoutTask |> Async.AwaitTask
    let! stderr = stderrTask |> Async.AwaitTask
    if child.ExitCode <> 0 then return failwithf "%s: child FSI exit %d: %s" name child.ExitCode stderr
    return stdout, stderr
    }

// Variant for asserting TaskApply CLI fail-closed exit behavior: the spawn
// is the same process boundary, but the helper returns the exit code and
// stderr instead of failing so the test can pin the exact failure path
// (unknown flag, missing authority surface, etc.) at a genuine subprocess
// boundary rather than only at the in-process CLI parser layer.
let runFsiReturnExit name script arguments : Async<int * string * string> =
    async {
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- "dotnet"
    startInfo.ArgumentList.Add "fsi"
    startInfo.ArgumentList.Add "--nologo"
    startInfo.ArgumentList.Add script
    arguments |> List.iter startInfo.ArgumentList.Add
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.WorkingDirectory <- repoRoot
    use child = Process.Start startInfo
    let stdoutTask = child.StandardOutput.ReadToEndAsync()
    let stderrTask = child.StandardError.ReadToEndAsync()
    do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 60.0) |> Async.AwaitTask
    let! stdout = stdoutTask |> Async.AwaitTask
    let! stderr = stderrTask |> Async.AwaitTask
    return child.ExitCode, stdout, stderr
    }

// The host validates projectRoot against its process workspace; multiple consumer identities must share that producer instance.
let startMcp (workingDirectory: string) =
    requireReleaseEntry ()
    let catalog = Path.Combine(workingDirectory, "profiles.json")
    if not (File.Exists catalog) then File.WriteAllText(catalog, "{\"profiles\":[]}")
    let catalogHash = SHA256.HashData(File.ReadAllBytes catalog) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- dotnetHost
    startInfo.ArgumentList.Add "exec"
    startInfo.ArgumentList.Add releaseEntryDll
    startInfo.ArgumentList.Add "--profile-catalog"
    startInfo.ArgumentList.Add (Path.GetFullPath catalog)
    startInfo.ArgumentList.Add "--profile-catalog-sha256"
    startInfo.ArgumentList.Add catalogHash
    startInfo.RedirectStandardInput <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.WorkingDirectory <- workingDirectory
    Process.Start startInfo

// Protocol stdout must be newline-delimited JSON-RPC 2.0 only; any banner,
// diagnostic, or malformed line is a boundary failure.
let validateProtocolLine (line: string) =
    if String.IsNullOrWhiteSpace line then
        failwith "protocol stdout contained a blank line"

    let node =
        try JsonNode.Parse line
        with error ->
            failwithf "protocol stdout line is not JSON (%s): %s" error.Message line

    if isNull node || node.GetValueKind() <> JsonValueKind.Object then
        failwithf "protocol stdout line is not a JSON object: %s" line

    let jsonrpc = node.["jsonrpc"]

    if isNull jsonrpc || nodeString jsonrpc <> "2.0" then
        failwithf "protocol stdout line is not JSON-RPC 2.0: %s" line

    if isJsonNull node.["result"] && isJsonNull node.["error"] then
        failwithf "protocol stdout line has neither result nor error: %s" line

    node

let readProtocolLine (child: Process) name : Async<JsonNode> =
    async {
        let! line = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds 240000.0) |> Async.AwaitTask
        if isNull line then return failwithf "%s: protocol stdout closed before a response arrived" name
        return validateProtocolLine line
    }

let writeLine (child: Process) (line: string) : Async<unit> =
    async {
        do! child.StandardInput.WriteLineAsync line |> Async.AwaitTask
        do! child.StandardInput.FlushAsync() |> Async.AwaitTask
    }

let sendRaw (child: Process) name (line: string) : Async<JsonNode> =
    async {
        do! child.StandardInput.WriteLineAsync line |> Async.AwaitTask
        do! child.StandardInput.FlushAsync() |> Async.AwaitTask
        return! readProtocolLine child name
    }

let send (child: Process) name id methodName parameters : Async<JsonNode> =
    let request =
        jobj
            [ "jsonrpc", jstr "2.0"
              "id", jint id
              "method", jstr methodName
              "params", parameters ]

    sendRaw child name (request.ToJsonString())

let notify (child: Process) methodName parameters : Async<unit> =
    let request = jobj [ "jsonrpc", jstr "2.0"; "method", jstr methodName; "params", parameters ]
    writeLine child (request.ToJsonString())

let resultOf name (response: JsonNode) =
    let result = response.["result"]

    if isJsonNull result then
        failwithf "%s: expected a result envelope, got %s" name (response.ToJsonString())

    result

let structuredOf name (response: JsonNode) =
    let structured = (resultOf name response).["structuredContent"]

    if isJsonNull structured then
        failwithf "%s: result has no structuredContent: %s" name (response.ToJsonString())

    structured

let expectToolOk name (response: JsonNode) =
    let result = resultOf name response
    let structured = structuredOf name response
    assertEqual $"{name} isError" false (result.["isError"].GetValue<bool>())
    assertEqual $"{name} ok" true (structured.["ok"].GetValue<bool>())
    structured

let expectToolError name code (response: JsonNode) =
    let result = resultOf name response
    let structured = structuredOf name response
    assertEqual $"{name} isError" true (result.["isError"].GetValue<bool>())
    assertEqual $"{name} ok" false (structured.["ok"].GetValue<bool>())
    let error = structured.["error"]

    if isJsonNull error then
        failwithf "%s: error payload missing: %s" name (response.ToJsonString())

    assertEqual $"{name} code" code (nodeString error.["code"])
    error

let callTool (child: Process) name id tool arguments =
    send child name id "tools/call" (jobj [ "name", jstr tool; "arguments", arguments ])

let callToolWithMeta (child: Process) name id tool arguments =
    send child name id "tools/call" (jobj [ "name", jstr tool; "arguments", arguments; "_meta", jobj [ "progressToken", jstr "meta-regression" ] ])

// --- MCP stdio session ------------------------------------------------------

let createArgs root =
    jobj
        [ "projectRoot", jstr root
          "taskId", jstr "MCP-1"
          "title", jstr "MCP boundary task"
          "kind", jstr "execution"
          "acceptanceCriteria", jarr [ jobj [ "id", jstr "AC1"; "text", jstr "MCP boundary verified" ] ]
          "workItems", jarr [ jobj [ "id", jstr "W1"; "title", jstr "Do MCP work"; "children", jarr [] ] ] ]

let getArgs root =
    jobj [ "projectRoot", jstr root; "taskId", jstr "MCP-1" ]

let applyArgs root revision command =
    jobj
        [ "projectRoot", jstr root
          "taskId", jstr "MCP-1"
          "expectedStateRevision", jint revision
          "command", command ]

let tempRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"taskruntime-mcp-tests-{Guid.NewGuid():N}")

Directory.CreateDirectory tempRoot |> ignore
let fileRoot = Path.Combine(tempRoot, "not-a-directory.txt")
File.WriteAllText(fileRoot, "not a directory")

async {
    do! assertRequiredSdk dotnetHost
    let mcp = startMcp tempRoot
    let mcpStderr = mcp.StandardError.ReadToEndAsync()

    // Protocol handshake.
    let! initialized =
        send
            mcp
            "initialize"
            1
            "initialize"
            (jobj
                [ "protocolVersion", jstr "2024-11-05"
                  "capabilities", jobj []
                  "clientInfo", jobj [ "name", jstr "task-runtime-tests"; "version", jstr "1" ] ])

    assertEqual "initialize id" 1 (initialized.["id"].GetValue<int>())
    let initResult = resultOf "initialize" initialized
    assertEqual "initialize protocolVersion" "2024-11-05" (nodeString initResult.["protocolVersion"])
    assertEqual "initialize server name" "mcp-store-workflow" (nodeString initResult.["serverInfo"].["name"])
    let secondRoot = Path.Combine(tempRoot, "second-client-workspace")
    Directory.CreateDirectory secondRoot |> ignore
    let secondMcp = startMcp secondRoot
    let secondStderr = secondMcp.StandardError.ReadToEndAsync()
    let! secondInitialized =
        send
            secondMcp
            "independent client initialize"
            24
            "initialize"
            (jobj
                [ "protocolVersion", jstr "2024-11-05"
                  "capabilities", jobj []
                  "clientInfo", jobj [ "name", jstr "another-harness"; "version", jstr "9" ] ])
    assertEqual "second client initialize id" 24 (secondInitialized.["id"].GetValue<int>())
    let secondResult = resultOf "second client initialize" secondInitialized
    assertEqual "second client protocol" "2024-11-05" (nodeString secondResult.["protocolVersion"])
    assertEqual "same producer identity for independent process" "mcp-store-workflow" (nodeString secondResult.["serverInfo"].["name"])
    assertEqual "same producer version for independent process" (nodeString initResult.["serverInfo"].["version"]) (nodeString secondResult.["serverInfo"].["version"])
    do! notify secondMcp "notifications/initialized" (jobj [])
    assertEqual
        "initialize tools listChanged"
        false
        (initResult.["capabilities"].["tools"].["listChanged"].GetValue<bool>())

    // A notification must not desynchronize the request/response stream.
    do! notify mcp "notifications/initialized" (jobj [])

    let! listed = send mcp "tools/list" 2 "tools/list" (jobj [])
    assertEqual "tools/list id" 2 (listed.["id"].GetValue<int>())
    let tools = (resultOf "tools/list" listed).["tools"].AsArray()
    let toolNames = tools |> Seq.map (fun tool -> nodeString tool.["name"]) |> List.ofSeq
    assertEqual "tools/list names" [ "task_create"; "task_get"; "task_apply"; "task_validate" ] toolNames
    let applyTool = tools |> Seq.find (fun tool -> nodeString tool.["name"] = "task_apply")
    let toolsJson = (resultOf "tools/list" listed).["tools"].ToJsonString()
    let! secondListed = send secondMcp "independent client tools/list" 25 "tools/list" (jobj [])
    let secondTools = (resultOf "independent client tools/list" secondListed).["tools"]
    assertTrue "independent clients receive compatible tool schemas" (JsonNode.DeepEquals(JsonNode.Parse toolsJson, secondTools))
    assertEqual
        "task_apply inputSchema additionalProperties"
        false
        (applyTool.["inputSchema"].["additionalProperties"].GetValue<bool>())
    let applyProperties =
        applyTool.["inputSchema"].["properties"].AsObject()
        |> Seq.map (fun entry -> entry.Key)
        |> Set.ofSeq

    for forbidden in [ "authority"; "provenance"; "receipt"; "confirmationRef" ] do
        assertTrue $"task_apply schema excludes {forbidden}" (not (Set.contains forbidden applyProperties))

    assertTrue
        "task_apply schema enumerates command variants"
        (not (isJsonNull applyTool.["inputSchema"].["properties"].["command"].["oneOf"]))

    let createTool = tools |> Seq.find (fun tool -> nodeString tool.["name"] = "task_create")
    assertEqual "task_create schema requires acceptance criteria" true (createTool.["inputSchema"].["properties"].["acceptanceCriteria"].["minItems"].GetValue<int>() > 0)
    assertEqual "task_create schema requires work items" true (createTool.["inputSchema"].["properties"].["workItems"].["minItems"].GetValue<int>() > 0)

    let! ping = send mcp "ping" 3 "ping" (jobj [])
    assertEqual "ping id" 3 (ping.["id"].GetValue<int>())
    assertEqual "ping result is empty object" 0 ((resultOf "ping" ping).AsObject().Count)

    // Structured tool results and library/CLI parity on the same persisted state.
    let! created = callTool mcp "task_create" 4 "task_create" (createArgs tempRoot)
    let createdStructured = expectToolOk "task_create" created
    let createdTask = createdStructured.["task"]
    assertEqual "created id" "MCP-1" (nodeString createdTask.["id"])
    assertEqual "created schemaVersion" 1 (createdTask.["schemaVersion"].GetValue<int>())
    assertEqual "created stateRevision" 0 (createdTask.["stateRevision"].GetValue<int>())

    let! fetched = callToolWithMeta mcp "task_get" 5 "task_get" (getArgs tempRoot)
    let fetchedStructured = expectToolOk "task_get" fetched
    let! libraryResult = getTask tempRoot "MCP-1"
    let libraryTask = expectOk "library get parity" libraryResult
    let libraryJson = serialize libraryTask
    assertTrue
        "MCP task_get equals library serialize"
        (JsonNode.DeepEquals(fetchedStructured.["task"], JsonNode.Parse libraryJson))

    let! cliStdout, cliStderr =
        runFsi "CLI get parity" (Path.Combine(scriptDirectory, "TaskGet.fsx")) [ tempRoot; "MCP-1" ]

    assertTrue "CLI get has no diagnostics" (String.IsNullOrWhiteSpace cliStderr)
    let cliJson = cliStdout.Trim()
    assertTrue "CLI get equals library serialize" (JsonNode.DeepEquals(JsonNode.Parse libraryJson, JsonNode.Parse cliJson))
    assertTrue "CLI get equals MCP task_get" (JsonNode.DeepEquals(fetchedStructured.["task"], JsonNode.Parse cliJson))

    // Representative TaskApply CLI unknown-flag / authority boundary proof at
    // a genuine process boundary. The CLI exposes no surface to inject User
    // authority, rejects unknown flags with usage exit code 2, and stderr
    // carries the usage message instead of accepting the flag silently.
    let! cliAuthorityExit, _, cliAuthorityStderr =
        runFsiReturnExit
            "TaskApply authority flag"
            (Path.Combine(scriptDirectory, "TaskApply.fsx"))
            [ tempRoot; "MCP-1"; "0"; "start"; "W1"; "--authority"; "user" ]

    assertEqual "TaskApply rejects unknown authority flag" 2 cliAuthorityExit
    assertContains "TaskApply authority usage on stderr" "usage:" cliAuthorityStderr

    let! validated = callTool mcp "task_validate" 6 "task_validate" (getArgs tempRoot)
    expectToolOk "task_validate" validated |> ignore

    // Expected-revision CAS: a successful apply increments; a stale apply is a
    // bounded CONFLICT that leaves persisted state untouched.
    let! applied =
        callTool
            mcp
            "task_apply success"
            7
            "task_apply"
            (applyArgs
                tempRoot
                0
                (jobj
                    [ "type", jstr "add-evidence"
                      "id", jstr "E1"
                      "kind", jstr "observation"
                      "source", jstr "task-runtime-mcp-tests"
                      "summary", jstr "first bounded apply" ]))

    let appliedStructured = expectToolOk "task_apply success" applied
    assertEqual "apply increments revision" 1 (appliedStructured.["task"].["stateRevision"].GetValue<int>())

    let! conflicted =
        callTool
            mcp
            "task_apply conflict"
            8
            "task_apply"
            (applyArgs
                tempRoot
                0
                (jobj
                    [ "type", jstr "add-evidence"
                      "id", jstr "E2"
                      "kind", jstr "observation"
                      "source", jstr "task-runtime-mcp-tests"
                      "summary", jstr "stale apply" ]))

    let conflictError = expectToolError "task_apply conflict" "CONFLICT" conflicted
    assertContains "conflict message" "state revision conflict" (nodeString conflictError.["message"])

    let! afterConflict = callTool mcp "task_get after conflict" 9 "task_get" (getArgs tempRoot)
    let afterConflictTask = (expectToolOk "task_get after conflict" afterConflict).["task"]
    assertEqual "CAS conflict preserves revision" 1 (afterConflictTask.["stateRevision"].GetValue<int>())
    assertEqual "CAS conflict preserves evidence" 1 (afterConflictTask.["evidence"].AsArray().Count)

    // Invalid workspace is rejected before any runtime operation.
    let missingRoot = Path.Combine(tempRoot, "does-not-exist")
    let! missing =
        callTool mcp "task_get missing root" 10 "task_get" (jobj [ "projectRoot", jstr missingRoot; "taskId", jstr "MCP-1" ])

    let missingError = expectToolError "task_get missing root" "INVALID_INPUT" missing
    assertContains "missing root message" "project root does not exist" (nodeString missingError.["message"])

    let! fileWorkspace = callTool mcp "task_get file root" 11 "task_get" (jobj [ "projectRoot", jstr fileRoot; "taskId", jstr "MCP-1" ])
    let fileError = expectToolError "task_get file root" "INVALID_INPUT" fileWorkspace
    assertContains "file root message" "project root does not exist" (nodeString fileError.["message"])

    // Ordinary task_apply exposes no authority, provenance, or receipt ingress.
    let! argsAuthority =
        callTool
            mcp
            "task_apply args authority"
            12
            "task_apply"
            (jobj
                [ "projectRoot", jstr tempRoot
                  "taskId", jstr "MCP-1"
                  "expectedStateRevision", jint 1
                  "command", jobj [ "type", jstr "start"; "workItemId", jstr "W1" ]
                  "authority", jstr "user" ])

    let argsAuthorityError = expectToolError "task_apply args authority" "INVALID_INPUT" argsAuthority
    assertContains "args authority rejected" "unknown property 'authority'" (nodeString argsAuthorityError.["message"])

    let! commandAuthority =
        callTool
            mcp
            "task_apply command authority"
            13
            "task_apply"
            (applyArgs tempRoot 1 (jobj [ "type", jstr "start"; "workItemId", jstr "W1"; "authority", jstr "user" ]))

    let commandAuthorityError = expectToolError "task_apply command authority" "INVALID_INPUT" commandAuthority
    assertContains "command authority rejected" "unknown property 'authority'" (nodeString commandAuthorityError.["message"])

    let! commandProvenance =
        callTool
            mcp
            "task_apply command provenance"
            14
            "task_apply"
            (applyArgs tempRoot 1 (jobj [ "type", jstr "start"; "workItemId", jstr "W1"; "provenance", jstr "user" ]))

    let commandProvenanceError = expectToolError "task_apply command provenance" "INVALID_INPUT" commandProvenance
    assertContains "command provenance rejected" "unknown property 'provenance'" (nodeString commandProvenanceError.["message"])

    let! commandReceipt =
        callTool
            mcp
            "task_apply command receipt"
            15
            "task_apply"
            (applyArgs tempRoot 1 (jobj [ "type", jstr "start"; "workItemId", jstr "W1"; "receipt", jstr "r-1" ]))

    let commandReceiptError = expectToolError "task_apply command receipt" "INVALID_INPUT" commandReceipt
    assertContains "command receipt rejected" "unknown property 'receipt'" (nodeString commandReceiptError.["message"])

    let! commandConfirmation =
        callTool
            mcp
            "task_apply command confirmationRef"
            16
            "task_apply"
            (applyArgs tempRoot 1 (jobj [ "type", jstr "start"; "workItemId", jstr "W1"; "confirmationRef", jstr "c-1" ]))

    let commandConfirmationError = expectToolError "task_apply command confirmationRef" "INVALID_INPUT" commandConfirmation
    assertContains
        "command confirmationRef rejected"
        "unknown property 'confirmationRef'"
        (nodeString commandConfirmationError.["message"])

    // Arbitrary effects are not a command family.
    let! shellCommand =
        callTool mcp "task_apply arbitrary effect" 17 "task_apply" (applyArgs tempRoot 1 (jobj [ "type", jstr "shell"; "command", jstr "whoami" ]))

    let shellError = expectToolError "task_apply arbitrary effect" "INVALID_INPUT" shellCommand
    assertContains "arbitrary effect rejected" "not supported" (nodeString shellError.["message"])

    // Bounded argument and protocol failures.
    let! unknownTool = callTool mcp "unknown tool" 18 "task_exec" (jobj [])
    let unknownToolError = expectToolError "unknown tool" "INVALID_INPUT" unknownTool
    assertContains "unknown tool rejected" "tool 'task_exec' is not registered" (nodeString unknownToolError.["message"])

    let! missingArg = callTool mcp "task_get missing arg" 19 "task_get" (jobj [ "projectRoot", jstr tempRoot ])
    let missingArgError = expectToolError "task_get missing arg" "INVALID_INPUT" missingArg
    assertContains "missing arg rejected" "missing property 'taskId'" (nodeString missingArgError.["message"])

    let! unknownArg =
        callTool mcp "task_get unknown arg" 20 "task_get" (jobj [ "projectRoot", jstr tempRoot; "taskId", jstr "MCP-1"; "authority", jstr "user" ])

    let unknownArgError = expectToolError "task_get unknown arg" "INVALID_INPUT" unknownArg
    assertContains "unknown arg rejected" "unknown property 'authority'" (nodeString unknownArgError.["message"])

    let! unknownToolCallParameter = sendRaw mcp "task-call unknown parameter" """{"jsonrpc":"2.0","id":24,"method":"tools/call","params":{"name":"task_get","arguments":{"projectRoot":""},"_unexpected":true}}"""
    assertEqual "unknown tools/call parameter code" -32602 (unknownToolCallParameter.["error"].["code"].GetValue<int>())

    let! unknownMethod = send mcp "unknown method" 21 "tools/nonexistent" (jobj [])
    assertEqual "unknown method code" -32601 (unknownMethod.["error"].["code"].GetValue<int>())

    let! badVersion =
        send
            mcp
            "bad version"
            22
            "initialize"
            (jobj
                [ "protocolVersion", jstr "1999-01-01"
                  "capabilities", jobj []
                  "clientInfo", jobj [ "name", jstr "task-runtime-tests"; "version", jstr "1" ] ])

    assertEqual "bad version code" -32602 (badVersion.["error"].["code"].GetValue<int>())

    let! invalidJson = sendRaw mcp "invalid json" "this is not json"
    assertEqual "invalid json code" -32700 (invalidJson.["error"].["code"].GetValue<int>())
    assertTrue "invalid json id is null" (isJsonNull invalidJson.["id"])

    // The host remains usable after every bounded failure.
    let! finalPing = send mcp "final ping" 23 "ping" (jobj [])
    assertEqual "final ping id" 23 (finalPing.["id"].GetValue<int>())

    // Clean shutdown and residual stdout cleanliness.
    mcp.StandardInput.Close()

    do! mcp.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds 120000.0) |> Async.AwaitTask

    let! trailing = mcp.StandardOutput.ReadToEndAsync() |> Async.AwaitTask

    for line in trailing.Split('\n') do
        if not (String.IsNullOrWhiteSpace line) then
            validateProtocolLine (line.Trim()) |> ignore

    let! stderr = mcpStderr |> Async.AwaitTask
    assertTrue "MCP stderr carries no protocol responses" (not (stderr.Contains("\"jsonrpc\"", StringComparison.Ordinal)))
    secondMcp.StandardInput.Close()
    do! secondMcp.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds 120000.0) |> Async.AwaitTask
    let! secondTrailing = secondMcp.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
    for line in secondTrailing.Split('\n') do
        if not (String.IsNullOrWhiteSpace line) then validateProtocolLine (line.Trim()) |> ignore
    let! independentStderr = secondStderr |> Async.AwaitTask
    assertTrue "independent MCP stderr carries no protocol responses" (not (independentStderr.Contains("\"jsonrpc\"", StringComparison.Ordinal)))

    printfn
        "OK platform-internal task runtime MCP boundary: stdio handshake, tools/list, structured results, library/CLI parity, TaskApply CLI unknown-flag/authority fail-closed exit, workspace rejection, CAS preservation, authority/receipt/effect rejection, bounded failures, and stdout cleanliness"
    mcp.Dispose()
    secondMcp.Dispose()
    if Directory.Exists tempRoot && tempRoot.Contains("taskruntime-mcp-tests-", StringComparison.Ordinal) then
        Directory.Delete(tempRoot, true)
}
// Standalone entry bridge: FSI requires one synchronous top-level boundary.
|> Async.RunSynchronously
