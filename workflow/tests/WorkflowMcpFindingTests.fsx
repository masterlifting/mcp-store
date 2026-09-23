// Targeted regression coverage for the frozen accepted findings:
// INFRA-005-D1 (validate JSON-RPC 2.0 and request IDs),
// INFRA-005-D2 (reject cross-variant tagged DTO fields),
// INFRA-005-D3 (reject negative expectedStateRevision without writing),
// ARCH-INFRA005-001 (bind projectRoot to the trusted workspace and reject
// reparse traversal).
// Deterministic newline-delimited stdio only; no LLM, network, or model call.
// Plain FSI harness because the frozen solution has no project/package system.

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

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

let assertRequiredSdk (host: string) =
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- host
    startInfo.ArgumentList.Add "--version"
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.WorkingDirectory <- repoRoot
    use child = Process.Start startInfo
    let stdout = child.StandardOutput.ReadToEnd()
    let stderr = child.StandardError.ReadToEnd()
    child.WaitForExit()

    if child.ExitCode <> 0 then
        failwithf "could not query the .NET SDK version from '%s': %s" host (stderr.Trim())

    let selected = stdout.Trim()

    if selected <> requiredSdk then
        failwithf "the required SDK is '%s' but '%s' reports '%s'" requiredSdk host selected

let dotnetHost =
    let host = resolveDotnetHost ()
    assertRequiredSdk host
    host

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

let jobj (fields: (string * JsonNode) list) =
    let node = JsonObject()
    fields |> List.iter (fun (key, value) -> node.[key] <- value)
    node

let jstr (value: string) : JsonNode = JsonValue.Create(value)
let jint (value: int) : JsonNode = JsonValue.Create(value)
let jlong (value: int64) : JsonNode = JsonValue.Create(value)
let jdouble (value: float) : JsonNode = JsonValue.Create(value)
let jbool (value: bool) : JsonNode = JsonValue.Create(value)

let jarr (items: JsonNode list) : JsonNode =
    let array = JsonArray()
    items |> List.iter (fun item -> array.Add item)
    array

let nodeString (node: JsonNode) =
    if isNull node then failwith "expected a JSON string node, got null"
    node.GetValue<string>()

let isJsonNull (node: JsonNode) =
    isNull node || node.GetValueKind() = JsonValueKind.Null

// --- MCP host over stdio ----------------------------------------------------

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

let readProtocolLine (child: Process) name =
    let task = child.StandardOutput.ReadLineAsync()

    if not (task.Wait(240000)) then
        failwithf "%s: timed out waiting for a protocol response" name

    if isNull task.Result then
        failwithf "%s: protocol stdout closed before a response arrived" name

    let node = JsonNode.Parse task.Result

    if isNull node || isNull node.["jsonrpc"] || nodeString node.["jsonrpc"] <> "2.0" then
        failwithf "%s: protocol stdout line is not JSON-RPC 2.0: %s" name task.Result

    node

let sendRequest (child: Process) name (request: JsonNode) =
    child.StandardInput.WriteLine(request.ToJsonString())
    child.StandardInput.Flush()
    readProtocolLine child name

let callTool (child: Process) name id tool arguments =
    sendRequest child name (jobj [ "jsonrpc", jstr "2.0"; "id", jint id; "method", jstr "tools/call"; "params", jobj [ "name", jstr tool; "arguments", arguments ] ])

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

let expectProtocolError name code (response: JsonNode) =
    let error = response.["error"]

    if isJsonNull error then
        failwithf "%s: expected a JSON-RPC error, got %s" name (response.ToJsonString())

    assertEqual $"{name} code" code (error.["code"].GetValue<int>())
    assertTrue $"{name} error id is null" (isJsonNull response.["id"])
    nodeString error.["message"]

// --- workspace fixtures -----------------------------------------------------

let suiteRoot =
    Path.Combine(Path.GetTempPath(), "opencode", $"taskruntime-findings-{Guid.NewGuid():N}")

let workspaceRoot = Path.Combine(suiteRoot, "workspace")
let outsideRoot = Path.Combine(suiteRoot, "outside")
let subWorkspace = Path.Combine(workspaceRoot, "sub")

Directory.CreateDirectory workspaceRoot |> ignore
Directory.CreateDirectory outsideRoot |> ignore
Directory.CreateDirectory subWorkspace |> ignore
let workspaceFile = Path.Combine(workspaceRoot, "not-a-directory.txt")
File.WriteAllText(workspaceFile, "not a directory")

// Junctions are used on Windows so the fixtures never depend on symbolic-link
// privilege; symlinks are used elsewhere.
let createDirectoryLink (link: string) (target: string) =
    if OperatingSystem.IsWindows() then
        let startInfo = ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        use proc = Process.Start startInfo
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwithf "could not create junction '%s': %s %s" link stdout stderr
    else
        Directory.CreateSymbolicLink(link, target) |> ignore

let removeLink (path: string) =
    if Directory.Exists path then Directory.Delete(path, false)
    elif File.Exists path then File.Delete path

let traversalLink = Path.Combine(workspaceRoot, "link")
let trustedLink = Path.Combine(suiteRoot, "trusted-link")
createDirectoryLink traversalLink outsideRoot
createDirectoryLink trustedLink workspaceRoot

let createArgs root id =
    jobj
        [ "projectRoot", jstr root
          "taskId", jstr id
          "title", jstr "Finding task"
          "kind", jstr "execution"
          "acceptanceCriteria", jarr [ jobj [ "id", jstr "AC1"; "text", jstr "verified" ] ]
          "workItems", jarr [ jobj [ "id", jstr "W1"; "title", jstr "work"; "children", jarr [] ] ] ]

let getArgs root id =
    jobj [ "projectRoot", jstr root; "taskId", jstr id ]

let applyArgs root id revision command =
    jobj
        [ "projectRoot", jstr root
          "taskId", jstr id
          "expectedStateRevision", revision
          "command", command ]

let evidenceCommand id =
    jobj
        [ "type", jstr "add-evidence"
          "id", jstr id
          "kind", jstr "observation"
          "source", jstr "task-runtime-finding-tests"
          "summary", jstr "finding evidence" ]

let mcp = startMcp workspaceRoot

try
    // --- INFRA-005-D1: JSON-RPC 2.0 and request ID validation --------------
    let wrongVersion =
        sendRequest mcp "D1 jsonrpc 1.0" (jobj [ "jsonrpc", jstr "1.0"; "id", jint 1; "method", jstr "ping"; "params", jobj [] ])

    let wrongVersionMessage = expectProtocolError "D1 jsonrpc 1.0" -32600 wrongVersion
    assertContains "D1 jsonrpc message" "jsonrpc must be exactly '2.0'" wrongVersionMessage

    let missingVersion =
        sendRequest mcp "D1 missing jsonrpc" (jobj [ "id", jint 2; "method", jstr "ping"; "params", jobj [] ])

    let missingVersionMessage = expectProtocolError "D1 missing jsonrpc" -32600 missingVersion
    assertContains "D1 missing jsonrpc message" "missing property 'jsonrpc'" missingVersionMessage

    let nonStringVersion =
        sendRequest mcp "D1 non-string jsonrpc" (jobj [ "jsonrpc", jdouble 2.0; "id", jint 3; "method", jstr "ping"; "params", jobj [] ])

    let nonStringVersionMessage = expectProtocolError "D1 non-string jsonrpc" -32600 nonStringVersion
    assertContains "D1 non-string jsonrpc message" "property 'jsonrpc' must be a string" nonStringVersionMessage

    for label, idNode in
        [ "bool", jbool true
          "array", jarr [ jint 1 ]
          "object", jobj [ "a", jint 1 ] ] do
        let invalidId =
            sendRequest mcp $"D1 invalid id {label}" (jobj [ "jsonrpc", jstr "2.0"; "id", idNode; "method", jstr "ping"; "params", jobj [] ])

        let message = expectProtocolError $"D1 invalid id {label}" -32600 invalidId
        assertContains $"D1 invalid id {label} message" "id must be a string, number, or null" message

    let nullId =
        sendRequest mcp "D1 null id" (jobj [ "jsonrpc", jstr "2.0"; "id", JsonValue.Create(null: string); "method", jstr "ping"; "params", jobj [] ])

    assertTrue "D1 null id returns a result" (not (isJsonNull nullId.["result"]))
    assertTrue "D1 null id echoed as null" (isJsonNull nullId.["id"])

    let stringId =
        sendRequest mcp "D1 string id" (jobj [ "jsonrpc", jstr "2.0"; "id", jstr "abc"; "method", jstr "ping"; "params", jobj [] ])

    assertTrue "D1 string id returns a result" (not (isJsonNull stringId.["result"]))
    assertEqual "D1 string id echoed" "abc" (nodeString stringId.["id"])

    let numberId =
        sendRequest mcp "D1 number id" (jobj [ "jsonrpc", jstr "2.0"; "id", jint 7; "method", jstr "ping"; "params", jobj [] ])

    assertTrue "D1 number id returns a result" (not (isJsonNull numberId.["result"]))
    assertEqual "D1 number id echoed" 7 (numberId.["id"].GetValue<int>())

    // --- INFRA-005-D2: strict tagged DTO fields ----------------------------
    // Each variant must reject a field that belongs to a sibling variant. These
    // fail during parsing, before any runtime lookup.
    let crossVariantCases =
        [ "patch addAcceptanceCriterion + target",
          jobj
              [ "type", jstr "apply-contract-patch"
                "patch", jobj [ "type", jstr "addAcceptanceCriterion"; "id", jstr "AC1"; "text", jstr "x"; "target", jstr "task" ] ],
          "unknown property 'target'"
          "patch removeGuard + text",
          jobj
              [ "type", jstr "apply-contract-patch"
                "patch", jobj [ "type", jstr "removeGuard"; "id", jstr "G1"; "text", jstr "x" ] ],
          "unknown property 'text'"
          "patch setObjective + id",
          jobj
              [ "type", jstr "apply-contract-patch"
                "patch", jobj [ "type", jstr "setObjective"; "text", jstr "x"; "id", jstr "X" ] ],
          "unknown property 'id'"
          "plan acceptExternalContract + objective",
          jobj
              [ "type", jstr "reconcile-contract-drift"
                "plan", jobj [ "type", jstr "acceptExternalContract"; "objective", jstr "o" ] ],
          "unknown property 'objective'"
          "command start + result",
          jobj [ "type", jstr "start"; "workItemId", jstr "W1"; "result", jstr "x" ],
          "unknown property 'result'" ]

    for label, command, expected in crossVariantCases do
        let response = callTool mcp $"D2 {label}" 20 "task_apply" (applyArgs workspaceRoot "FND-999" (jint 0) command)
        let error = expectToolError $"D2 {label}" "INVALID_INPUT" response
        assertContains $"D2 {label} message" expected (nodeString error.["message"])

    // Positive control: a well-formed single-variant patch parses and reaches
    // the runtime (missing task), proving the strict check is not over-broad.
    let validPatch =
        callTool
            mcp
            "D2 valid patch control"
            21
            "task_apply"
            (applyArgs workspaceRoot "FND-999" (jint 0) (jobj [ "type", jstr "apply-contract-patch"; "patch", jobj [ "type", jstr "setObjective"; "text", jstr "x" ] ]))

    let validPatchError = structuredOf "D2 valid patch control" validPatch
    assertEqual "D2 valid patch control ok" false (validPatchError.["ok"].GetValue<bool>())
    let validPatchMessage = nodeString validPatchError.["error"].["message"]
    assertTrue "D2 valid patch control has no unknown-property error" (not (validPatchMessage.Contains("unknown property", StringComparison.Ordinal)))

    // --- INFRA-005-D3: negative expectedStateRevision ----------------------
    let created = callTool mcp "D3 create" 30 "task_create" (createArgs workspaceRoot "FND-1")
    let createdTask = (expectToolOk "D3 create" created).["task"]
    assertEqual "D3 created revision" 0 (createdTask.["stateRevision"].GetValue<int>())

    let negative = callTool mcp "D3 negative" 31 "task_apply" (applyArgs workspaceRoot "FND-1" (jint -1) (evidenceCommand "E1"))
    let negativeError = expectToolError "D3 negative" "INVALID_INPUT" negative
    assertContains "D3 negative message" "must be non-negative" (nodeString negativeError.["message"])

    let outOfRange = callTool mcp "D3 out of range" 32 "task_apply" (applyArgs workspaceRoot "FND-1" (jlong -2147483649L) (evidenceCommand "E2"))
    let outOfRangeError = expectToolError "D3 out of range" "INVALID_INPUT" outOfRange
    assertContains "D3 out of range message" "must be a 32-bit integer" (nodeString outOfRangeError.["message"])

    let fractional = callTool mcp "D3 fractional" 33 "task_apply" (applyArgs workspaceRoot "FND-1" (jdouble 1.5) (evidenceCommand "E3"))
    let fractionalError = expectToolError "D3 fractional" "INVALID_INPUT" fractional
    assertContains "D3 fractional message" "must be a 32-bit integer" (nodeString fractionalError.["message"])

    let afterRejections = callTool mcp "D3 get after rejections" 34 "task_get" (getArgs workspaceRoot "FND-1")
    let afterRejectionsTask = (expectToolOk "D3 get after rejections" afterRejections).["task"]
    assertEqual "D3 rejections preserve revision" 0 (afterRejectionsTask.["stateRevision"].GetValue<int>())
    assertEqual "D3 rejections preserve evidence" 0 (afterRejectionsTask.["evidence"].AsArray().Count)

    let positive = callTool mcp "D3 positive" 35 "task_apply" (applyArgs workspaceRoot "FND-1" (jint 0) (evidenceCommand "E4"))
    assertEqual "D3 positive revision" 1 ((expectToolOk "D3 positive" positive).["task"].["stateRevision"].GetValue<int>())

    // --- ARCH-INFRA005-001: trusted workspace containment ------------------
    let outside =
        callTool mcp "ARCH outside" 40 "task_get" (getArgs outsideRoot "FND-1")

    let outsideError = expectToolError "ARCH outside" "INVALID_INPUT" outside
    assertContains "ARCH outside message" "outside the trusted workspace" (nodeString outsideError.["message"])

    let escaped =
        callTool mcp "ARCH escaped" 41 "task_get" (getArgs (Path.Combine(workspaceRoot, "..", "outside")) "FND-1")

    let escapedError = expectToolError "ARCH escaped" "INVALID_INPUT" escaped
    assertContains "ARCH escaped message" "outside the trusted workspace" (nodeString escapedError.["message"])

    let atRoot = callTool mcp "ARCH trusted root" 42 "task_get" (getArgs workspaceRoot "FND-1")
    expectToolOk "ARCH trusted root accepted" atRoot |> ignore

    let subCreated = callTool mcp "ARCH sub create" 43 "task_create" (createArgs subWorkspace "FND-2")
    expectToolOk "ARCH sub create" subCreated |> ignore
    let atSub = callTool mcp "ARCH sub get" 44 "task_get" (getArgs subWorkspace "FND-2")
    expectToolOk "ARCH sub workspace accepted" atSub |> ignore

    let missing =
        callTool mcp "ARCH missing" 45 "task_get" (getArgs (Path.Combine(workspaceRoot, "missing")) "FND-1")

    let missingError = expectToolError "ARCH missing" "INVALID_INPUT" missing
    assertContains "ARCH missing message" "project root does not exist" (nodeString missingError.["message"])

    let fileWorkspace = callTool mcp "ARCH file" 46 "task_get" (getArgs workspaceFile "FND-1")
    let fileError = expectToolError "ARCH file" "INVALID_INPUT" fileWorkspace
    assertContains "ARCH file message" "project root does not exist" (nodeString fileError.["message"])

    let traversed = callTool mcp "ARCH traversal" 47 "task_get" (getArgs traversalLink "FND-1")
    let traversedError = expectToolError "ARCH traversal" "INVALID_INPUT" traversed
    assertContains "ARCH traversal message" "must not traverse a reparse point" (nodeString traversedError.["message"])

    printfn
        "OK task runtime MCP findings: D1 JSON-RPC 2.0/ID validation, D2 strict tagged DTO fields, D3 negative revision rejected without write, ARCH-INFRA005-001 trusted workspace containment and reparse rejection"
finally
    try
        if not mcp.HasExited then mcp.Kill true
    with _ ->
        ()

    mcp.Dispose()

// The trusted workspace itself being a reparse point must fail closed. A second
// host is anchored at a junction to the real workspace.
let mcpLinked = startMcp trustedLink

try
    let response = callTool mcpLinked "ARCH trusted reparse" 50 "task_get" (getArgs trustedLink "FND-1")
    let error = expectToolError "ARCH trusted reparse" "INVALID_INPUT" response
    assertContains "ARCH trusted reparse message" "trusted workspace must not be a reparse point" (nodeString error.["message"])
finally
    try
        if not mcpLinked.HasExited then mcpLinked.Kill true
    with _ ->
        ()

    mcpLinked.Dispose()

    [ traversalLink; trustedLink ] |> List.iter removeLink

    if Directory.Exists suiteRoot && suiteRoot.Contains("taskruntime-findings-", StringComparison.Ordinal) then
        Directory.Delete(suiteRoot, true)
