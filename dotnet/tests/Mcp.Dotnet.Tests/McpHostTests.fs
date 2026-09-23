module Mcp.Dotnet.Tests.McpHostTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

// The host is exercised as a real stdio subprocess so stdout protocol cleanliness,
// tool listing, semantic status, and cancellation are verified at the transport boundary.
let private verifierDllPath () =
    let configuration = (DirectoryInfo AppContext.BaseDirectory).Parent.Name

    Path.Combine(
        repositoryRoot(),
        "dotnet",
        "bin",
        configuration,
        "net11.0",
        "Mcp.Dotnet.dll"
    )

// Runs the host to completion to assert bounded startup rejection for missing or
// invalid injected hosts; the MCP path never reaches stdio in these cases.
let private runHostToCompletion (arguments: string list) (workingDirectory: string) =
    let dllPath = verifierDllPath ()

    let startInfo = ProcessStartInfo()
    startInfo.FileName <- "dotnet"
    startInfo.ArgumentList.Add dllPath
    arguments |> List.iter startInfo.ArgumentList.Add
    startInfo.WorkingDirectory <- workingDirectory
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    use child = Process.Start startInfo
    let stdout = child.StandardOutput.ReadToEnd()
    let stderr = child.StandardError.ReadToEnd()

    if not (child.WaitForExit 30000) then
        child.Kill true
        fail "runHostToCompletion" "verifier host did not exit after a rejected startup"

    child.ExitCode, stdout, stderr

type private McpHostProcess(workingDirectory: string, ?injectedHost: string, ?artifactRoot: string) =
    let dllPath = verifierDllPath ()

    do
        if not (File.Exists dllPath) then
            fail "McpHostProcess" $"verifier host not found at {dllPath}"

    let startInfo = ProcessStartInfo()
    do
        startInfo.FileName <- "dotnet"
        startInfo.ArgumentList.Add dllPath
        startInfo.ArgumentList.Add "--dotnet-host"
        startInfo.ArgumentList.Add(defaultArg injectedHost (dotnetHost ()))

        match artifactRoot with
        | Some root ->
            startInfo.ArgumentList.Add "--artifact-root"
            startInfo.ArgumentList.Add root
        | None -> ()

        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true

    let hostProcess = new Process(StartInfo = startInfo)
    let allLines = ConcurrentQueue<string>()
    let pending = new BlockingCollection<string>()
    let stderrText = StringBuilder()

    do
        if not (hostProcess.Start()) then
            fail "McpHostProcess" "verifier host process could not be started"

    let stdoutReader =
        Task.Run(fun () ->
            let rec loop () =
                let line = hostProcess.StandardOutput.ReadLine()

                if not (isNull line) then
                    allLines.Enqueue line
                    pending.Add line
                    loop ()

            loop ())

    let stderrReader =
        Task.Run(fun () -> stderrText.Append(hostProcess.StandardError.ReadToEnd()) |> ignore)

    member _.Send(line: string) =
        hostProcess.StandardInput.WriteLine line
        hostProcess.StandardInput.Flush()

    member _.TryReadLine(timeoutMs: int) =
        let mutable line = null

        if pending.TryTake(&line, timeoutMs) then
            Some line
        else
            None

    member this.ReadResponse(id: int, timeoutMs: int) =
        match this.TryReadLine timeoutMs with
        | None -> fail "ReadResponse" $"no response for id {id}; stderr: {stderrText}"
        | Some line ->
            let node = JsonNode.Parse line
            let actual = node.["id"].GetValue<int>()

            if actual <> id then
                fail "ReadResponse" $"expected response id {id} but got {actual}: {line}"

            node

    member _.Received = allLines |> Seq.toArray
    member _.StandardError = stderrText.ToString()

    member _.WaitForExit(timeoutMs: int) =
        if not (hostProcess.WaitForExit timeoutMs) then
            fail "McpHostProcess" $"verifier host did not exit within {timeoutMs}ms"

        hostProcess.ExitCode

    interface IDisposable with
        member _.Dispose() =
            try
                if not hostProcess.HasExited then
                    hostProcess.Kill true
            with _ ->
                ()

            stdoutReader.Wait 2000 |> ignore
            stderrReader.Wait 2000 |> ignore
            hostProcess.Dispose()
            pending.Dispose()

let private initializeRequest =
    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"verifier-tests","version":"1"}}}"""

let private initializedNotification =
    """{"jsonrpc":"2.0","method":"notifications/initialized"}"""

let private toolsListRequest = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""
let private shutdownRequest = """{"jsonrpc":"2.0","id":99,"method":"shutdown"}"""
let private exitNotification = """{"jsonrpc":"2.0","method":"exit"}"""

let private toolCall id name arguments =
    $"""{{"jsonrpc":"2.0","id":{id},"method":"tools/call","params":{{"name":"{name}","arguments":{arguments}}}}}"""

let private toolCallWithMeta id name arguments =
    $"""{{"jsonrpc":"2.0","id":{id},"method":"tools/call","params":{{"name":"{name}","arguments":{arguments},"_meta":{{"progressToken":"meta-regression"}}}}}}"""

let private structured (response: JsonNode) = response.["result"].["structuredContent"]
let private textContent (response: JsonNode) = response.["result"].["content"].[0].["text"].GetValue<string>()

let private stop (host: McpHostProcess) =
    host.Send shutdownRequest
    host.ReadResponse(99, 5000) |> ignore
    host.Send exitNotification
    host.WaitForExit 10000 |> ignore

let private hostTests =
    testSequenced
        (testList "MCP stdio host" [
            testCase "the host requires exactly one injected --dotnet-host"
            <| fun _ ->
                use workspace = new TempWorkspace()
                workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                let exitCode, stdout, stderr = runHostToCompletion [] workspace.Root

                Expect.equal exitCode 1 "startup rejected"
                Expect.isTrue (stderr.Contains("requires exactly one injected --dotnet-host")) "actionable startup diagnostic"
                Expect.isFalse (stdout.Contains("\"jsonrpc\"")) "no protocol traffic on rejected startup"

            testCase "a workspace-local injected host is rejected at startup"
            <| fun _ ->
                use workspace = new TempWorkspace()
                let decoy = workspace.Write("dotnet.exe", "decoy") |> Path.GetFullPath

                let exitCode, stdout, stderr =
                    runHostToCompletion [ "--dotnet-host"; decoy ] workspace.Root

                Expect.equal exitCode 1 "startup rejected"
                Expect.isTrue (stderr.Contains("dotnet verifier startup failed")) "bounded startup failure"
                Expect.isFalse (stdout.Contains("\"jsonrpc\"")) "no protocol traffic on rejected startup"

            testCaseTask "an explicit external artifact root is accepted by the deployed host" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore

                    let externalRoot =
                        Path.Combine(Path.GetTempPath(), "mcp-verifier-host-artifacts", Guid.NewGuid().ToString("N"))

                    try
                        use host = new McpHostProcess(workspace.Root, artifactRoot = externalRoot)
                        host.Send initializeRequest
                        host.ReadResponse(1, 5000) |> ignore
                        host.Send initializedNotification
                        host.Send(toolCall 30 "verify_dotnet_build" "{\"target\":\"lib.csproj\"}")
                        let response = host.ReadResponse(30, 120000)
                        Expect.equal (response.["result"].["isError"].GetValue<bool>()) false "configured artifact root starts the host"

                        // Evidence under the configured root is observable only while the
                        // MCP session is alive: disposing the host service runs
                        // ArtifactRegistry.EndSession, which removes the per-run
                        // directories by documented session-cleanup design.
                        let retained =
                            Directory.Exists externalRoot
                            && Directory.EnumerateFiles(externalRoot, "*", SearchOption.AllDirectories) |> Seq.isEmpty |> not

                        Expect.isTrue retained "configured artifact root retains evidence"

                        stop host
                    finally
                        if Directory.Exists externalRoot then
                            Directory.Delete(externalRoot, true)
                })

            testCaseTask "the child build uses the injected host rather than PATH" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore

                    let outsideRoot =
                        Path.Combine(Path.GetTempPath(), "mcp-verifier-host", Guid.NewGuid().ToString("N"))

                    Directory.CreateDirectory outsideRoot |> ignore
                    let fake = Path.Combine(outsideRoot, "not-dotnet.exe")
                    File.WriteAllText(fake, "not a portable executable")

                    try
                        use host = new McpHostProcess(workspace.Root, injectedHost = fake)
                        host.Send initializeRequest
                        host.ReadResponse(1, 5000) |> ignore
                        host.Send initializedNotification

                        host.Send(toolCall 20 "verify_dotnet_build" """{"target":"lib.csproj"}""")
                        let response = host.ReadResponse(20, 120000)
                        let payload = structured response

                        Expect.equal (response.["result"].["isError"].GetValue<bool>()) true "child launch failed"
                        Expect.equal
                            (payload.["error"].["code"].GetValue<string>())
                            "PROCESS_START_FAILURE"
                            "the injected host, not PATH dotnet, was launched"

                        stop host
                    finally
                        try
                            Directory.Delete(outsideRoot, true)
                        with _ ->
                            ()
                })

            testCaseTask "handshake and tool list are protocol-clean" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root)

                    host.Send initializeRequest
                    let initialized = host.ReadResponse(1, 5000)
                    Expect.equal (initialized.["result"].["serverInfo"].["name"].GetValue<string>()) "mcp-store-dotnet" "server name"

                    host.Send initializedNotification
                    host.Send toolsListRequest
                    let listed = host.ReadResponse(2, 5000)
                    let names =
                        listed.["result"].["tools"].AsArray()
                        |> Seq.map (fun tool -> tool.["name"].GetValue<string>())
                        |> Seq.toList

                    Expect.equal
                        names
                        [ "verify_dotnet_build"; "verify_dotnet_test"; "verification_details" ]
                        "exactly three capability-scoped tools"

                    stop host

                    for line in host.Received do
                        let parsed = JsonNode.Parse line
                        Expect.equal (parsed.["jsonrpc"].GetValue<string>()) "2.0" "stdout line is JSON-RPC"
                })

            testCaseTask "unknown tool and unknown run id return compact errors" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root)

                    host.Send initializeRequest
                    host.ReadResponse(1, 5000) |> ignore
                    host.Send initializedNotification

                    host.Send(toolCall 3 "verify_dotnet_bash" """{"command":"rm -rf /"}""")
                    let unknownTool = host.ReadResponse(3, 5000)
                    let unknownToolCode = (structured unknownTool).["error"].["code"].GetValue<string>()
                    Expect.equal (unknownTool.["result"].["isError"].GetValue<bool>()) true "unknown tool is an error"
                    Expect.equal unknownToolCode "INVALID_INPUT" "unknown tool code"

                    host.Send(toolCall 4 "verification_details" """{"runId":"missing-run","kind":"errors"}""")
                    let unknownRun = host.ReadResponse(4, 5000)
                    let unknownRunCode = (structured unknownRun).["error"].["code"].GetValue<string>()
                    Expect.equal unknownRunCode "UNKNOWN_RUN_ID" "unknown run id code"

                    stop host
                })

            testCaseTask "build tool returns semantic status without raw output" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root)

                    host.Send initializeRequest
                    host.ReadResponse(1, 5000) |> ignore
                    host.Send initializedNotification

                    host.Send(toolCallWithMeta 4 "verification_details" "{\"runId\":\"missing-run\",\"kind\":\"errors\"}")
                    let metaResponse = host.ReadResponse(4, 5000)
                    Expect.equal (metaResponse.["result"].["isError"].GetValue<bool>()) true "optional MCP _meta is ignored"
                    Expect.equal ((structured metaResponse).["error"].["code"].GetValue<string>()) "UNKNOWN_RUN_ID" "_meta does not alter tool dispatch"

                    host.Send("""{"jsonrpc":"2.0","id":40,"method":"tools/call","params":{"name":"verification_details","arguments":{"runId":"missing-run","kind":"errors"},"_unexpected":true}}""")
                    let unknownParameter = host.ReadResponse(40, 5000)
                    Expect.equal (unknownParameter.["error"].["code"].GetValue<int>()) -32602 "other MCP tool-call metadata remains rejected"

                    host.Send(toolCall 5 "verify_dotnet_build" """{"target":"lib.csproj"}""")
                    let response = host.ReadResponse(5, 120000)
                    let payload = structured response

                    Expect.equal (response.["result"].["isError"].GetValue<bool>()) false "transport succeeded"
                    Expect.equal (payload.["ok"].GetValue<bool>()) true "ok flag"
                    Expect.equal (payload.["status"].GetValue<string>()) "succeeded" "semantic status"
                    Expect.equal (payload.["errorCount"].GetValue<int>()) 0 "no errors"
                    Expect.isNotNull (payload.["runId"]) "opaque run id returned"

                    let text = textContent response
                    Expect.isFalse (text.Contains "Build succeeded") "raw log not leaked"
                    Expect.isTrue (text.Length < Budgets.Defaults.TotalToolResultSize) "compact result bounded"

                    stop host
                })

            testCaseTask "cancellation notification stops an in-flight run" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root)

                    host.Send initializeRequest
                    host.ReadResponse(1, 5000) |> ignore
                    host.Send initializedNotification

                    host.Send(toolCall 10 "verify_dotnet_build" """{"target":"lib.csproj","timeoutMs":1800000}""")

                    host.Send
                        """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":10}}"""

                    let response = host.ReadResponse(10, 60000)
                    let status = (structured response).["status"].GetValue<string>()
                    Expect.equal status "cancelled" "run cancelled"

                    stop host
                })

            testCaseTask "test tool returns counts, bounded failures, and retrievable details" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    let projectPath = workspace.CreateXunitTestProject("sample", "sample")
                    let target = Path.GetRelativePath(workspace.Root, projectPath).Replace('\\', '/')
                    use host = new McpHostProcess(workspace.Root)

                    host.Send initializeRequest
                    host.ReadResponse(1, 5000) |> ignore
                    host.Send initializedNotification

                    host.Send(toolCall 6 "verify_dotnet_test" $"{{\"target\":\"{target}\"}}")
                    let response = host.ReadResponse(6, 300000)
                    let payload = structured response

                    Expect.equal (response.["result"].["isError"].GetValue<bool>()) false "transport succeeded"
                    Expect.equal (payload.["ok"].GetValue<bool>()) true "ok flag"
                    Expect.equal (payload.["status"].GetValue<string>()) "failed" "one failing test maps to failed"
                    Expect.equal (payload.["trxAvailable"].GetValue<bool>()) true "trx retained"
                    Expect.isNull (payload.["trxUnavailableReason"]) "no TRX reason when retained"

                    let trxArtifacts =
                        Directory.EnumerateFiles(
                            Path.Combine(workspace.Root, ".opencode", "dotnet-verification"),
                            "*.trx",
                            SearchOption.AllDirectories
                        )
                        |> Seq.toList

                    Expect.isTrue (trxArtifacts.Length >= 1) "a TRX artifact was retained"
                    Expect.isTrue (trxArtifacts |> List.forall (fun path -> FileInfo(path).Length > 0L)) "TRX is non-empty"

                    let counts = payload.["counts"]
                    Expect.equal (counts.["total"].GetValue<int>()) 2 "total tests"
                    Expect.equal (counts.["passed"].GetValue<int>()) 1 "passed tests"
                    Expect.equal (counts.["failed"].GetValue<int>()) 1 "failed tests"

                    let failedTests = payload.["failedTests"].AsArray()
                    Expect.equal failedTests.Count 1 "bounded failed subset"
                    Expect.isTrue ((failedTests.[0].GetValue<string>()).Contains "Fails") "failing test named"

                    let runId = payload.["runId"].GetValue<string>()
                    host.Send(toolCall 7 "verification_details" $"{{\"runId\":\"{runId}\",\"kind\":\"failed-tests\"}}")
                    let detailsResponse = host.ReadResponse(7, 30000)
                    let details = structured detailsResponse
                    Expect.equal (details.["ok"].GetValue<bool>()) true "details ok"
                    Expect.equal (details.["total"].GetValue<int>()) 1 "one failed test detail"

                    stop host
                })

            testCaseTask "failed-test details on a build run return a compact unavailable error" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root)

                    host.Send initializeRequest
                    host.ReadResponse(1, 5000) |> ignore
                    host.Send initializedNotification

                    host.Send(toolCall 8 "verify_dotnet_build" """{"target":"lib.csproj"}""")
                    let response = host.ReadResponse(8, 120000)
                    let runId = (structured response).["runId"].GetValue<string>()

                    host.Send(toolCall 9 "verification_details" $"{{\"runId\":\"{runId}\",\"kind\":\"failed-tests\"}}")
                    let detailsResponse = host.ReadResponse(9, 30000)
                    let payload = structured detailsResponse

                    Expect.equal (detailsResponse.["result"].["isError"].GetValue<bool>()) true "unavailable is an error"
                    Expect.equal (payload.["error"].["code"].GetValue<string>()) "TEST_DETAIL_UNAVAILABLE" "actionable code"
                    Expect.isFalse ((textContent detailsResponse).Contains "stdout.log") "no physical artifact path leaked"

                    stop host
                })

            testCaseTask "detail responses stay within the total tool-result budget" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", manyErrorSource 120 300) |> ignore
                    use host = new McpHostProcess(workspace.Root)

                    host.Send initializeRequest
                    host.ReadResponse(1, 5000) |> ignore
                    host.Send initializedNotification

                    host.Send(toolCall 11 "verify_dotnet_build" """{"target":"lib.csproj"}""")
                    let buildResponse = host.ReadResponse(11, 120000)
                    let payload = structured buildResponse
                    Expect.equal (payload.["status"].GetValue<string>()) "failed" "many-error build failed"
                    let runId = payload.["runId"].GetValue<string>()

                    host.Send(
                        toolCall
                            12
                            "verification_details"
                            $"{{\"runId\":\"{runId}\",\"kind\":\"errors\",\"offset\":0,\"limit\":128}}"
                    )

                    let detailsResponse = host.ReadResponse(12, 60000)
                    let details = structured detailsResponse
                    let text = textContent detailsResponse

                    if not (details.["ok"].GetValue<bool>()) then
                        fail "detail budget" $"large detail response must stay bounded, got: {text}"

                    Expect.isTrue
                        (Encoding.UTF8.GetByteCount text <= Budgets.Defaults.TotalToolResultSize)
                        "serialized detail result within the centralized total budget"

                    let items = details.["items"].AsArray()
                    let total = details.["total"].GetValue<int>()
                    Expect.isTrue (total >= 50) "large diagnostic set was retained"
                    Expect.isTrue (items.Count <= Budgets.Defaults.DetailsMaxPageSize) "detail page bounded by the max page size"

                    if items.Count < total then
                        Expect.equal (details.["hasMore"].GetValue<bool>()) true "trimmed page signals more results"

                    stop host
                })
        ])

let tests = hostTests
