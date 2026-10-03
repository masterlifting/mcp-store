module Mcp.Dotnet.Tests.McpHostTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

let private dotnetMcpDllPath () =
    let configuration = (DirectoryInfo AppContext.BaseDirectory).Parent.Name

    Path.Combine(
        repositoryRoot(),
        "dotnet",
        "bin",
        configuration,
        "net11.0",
        "Mcp.Dotnet.dll"
    )

let private runHostToCompletion (arguments: string list) (workingDirectory: string) =
    task {
        let dllPath = dotnetMcpDllPath ()

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
        let stdoutTask = child.StandardOutput.ReadToEndAsync()
        let stderrTask = child.StandardError.ReadToEndAsync()
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
        let mutable timedOut = false

        try
            do! child.WaitForExitAsync(timeout.Token)
        with :? OperationCanceledException ->
            try child.Kill true with _ -> ()
            timedOut <- true

        let! stdout = stdoutTask
        let! stderr = stderrTask
        if timedOut then
            return fail "runHostToCompletion" "dotnet MCP host did not exit after a rejected startup"
        else
            return child.ExitCode, stdout, stderr
    }

type private McpHostProcess(workingDirectory: string, artifactRoot: string, ?injectedHost: string) =
    let dllPath = dotnetMcpDllPath ()

    do
        if not (File.Exists dllPath) then
            fail "McpHostProcess" $"dotnet MCP host not found at {dllPath}"

    let startInfo = ProcessStartInfo()
    do
        startInfo.FileName <- "dotnet"
        startInfo.ArgumentList.Add dllPath
        startInfo.ArgumentList.Add "--dotnet-host"
        startInfo.ArgumentList.Add(defaultArg injectedHost (dotnetHost ()))
        startInfo.ArgumentList.Add "--artifact-root"
        startInfo.ArgumentList.Add artifactRoot
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true

    let hostProcess = new Process(StartInfo = startInfo)
    let allLines = ConcurrentQueue<string>()
    let pending = Channel.CreateUnbounded<string>()
    let stderrText = StringBuilder()

    do
        if not (hostProcess.Start()) then
            fail "McpHostProcess" "dotnet MCP host process could not be started"

    let stdoutReader =
        task {
            let mutable reading = true
            while reading do
                let! line = hostProcess.StandardOutput.ReadLineAsync()
                if isNull line then
                    reading <- false
                    pending.Writer.TryComplete() |> ignore
                else
                    allLines.Enqueue line
                    do! pending.Writer.WriteAsync(line).AsTask()
        }

    let stderrReader =
        task {
            let! output = hostProcess.StandardError.ReadToEndAsync()
            stderrText.Append output |> ignore
        }

    member _.Send(line: string) =
        task {
            do! hostProcess.StandardInput.WriteLineAsync line
            do! hostProcess.StandardInput.FlushAsync()
        }

    member _.ReadResponse(id: int, timeoutMs: int) =
        task {
            use timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(float timeoutMs))
            let! line = pending.Reader.ReadAsync(timeout.Token).AsTask()
            let node = JsonNode.Parse line
            let actual = node.["id"].GetValue<int>()

            if actual <> id then
                return fail "ReadResponse" $"expected response id {id} but got {actual}: {line}"
            else
                return node
        }

    member _.Received = allLines |> Seq.toArray
    member _.StandardError = stderrText.ToString()

    member _.AwaitExit(timeoutMs: int) =
        task {
            try
                do! hostProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(float timeoutMs))
                do! stdoutReader
                do! stderrReader
                return hostProcess.ExitCode
            with :? TimeoutException ->
                return fail "McpHostProcess" $"dotnet MCP host did not exit within {timeoutMs}ms"
        }

    interface IDisposable with
        member _.Dispose() =
            try
                if not hostProcess.HasExited then
                    hostProcess.Kill true
            with _ ->
                ()

            ()
            hostProcess.Dispose()
            pending.Writer.TryComplete() |> ignore

let private workspaceArtifactRoot (workspace: TempWorkspace) =
    workspace.ExternalArtifactRoot

let private initializeRequest =
    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"dotnet-mcp-tests","version":"1"}}}"""

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
    task {
        do! host.Send shutdownRequest
        let! _ = host.ReadResponse(99, 5000)
        do! host.Send exitNotification
        let! _ = host.AwaitExit 10000
        return ()
    }

let private hostTests =
    testSequenced
        (testList "MCP stdio host" [
            testCaseTask "the host requires both --dotnet-host and --artifact-root" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore

                    let! missingRootCode, missingRootStdout, missingRootStderr =
                        runHostToCompletion [ "--dotnet-host"; dotnetHost () ] workspace.Root

                    Expect.equal missingRootCode 1 "startup rejected without an artifact root"
                    Expect.isTrue (missingRootStderr.Contains("--artifact-root")) "startup names the missing flag"
                    Expect.isFalse (missingRootStdout.Contains("\"jsonrpc\"")) "no protocol traffic on rejected startup"

                    let! exitCode, stdout, stderr = runHostToCompletion [] workspace.Root
                    Expect.equal exitCode 1 "startup rejected without flags"
                    Expect.isTrue (stderr.Contains("requires --dotnet-host")) "actionable startup diagnostic"
                    Expect.isFalse (stdout.Contains("\"jsonrpc\"")) "no protocol traffic on rejected startup"
                })

            testCaseTask "a workspace-local injected host is rejected at startup" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    let decoy = workspace.Write("dotnet.exe", "decoy") |> Path.GetFullPath
                    let artifactRoot = workspaceArtifactRoot workspace

                    let! exitCode, stdout, stderr =
                        runHostToCompletion [ "--dotnet-host"; decoy; "--artifact-root"; artifactRoot ] workspace.Root

                    Expect.equal exitCode 1 "startup rejected"
                    Expect.isTrue (stderr.Contains("dotnet MCP startup failed")) "bounded startup failure"
                    Expect.isFalse (stdout.Contains("\"jsonrpc\"")) "no protocol traffic on rejected startup"
                })

            testCaseTask "an explicit external artifact root is accepted by the deployed host" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore

                    let externalRoot =
                        Path.Combine(Path.GetTempPath(), "mcp-dotnet-host-artifacts", Guid.NewGuid().ToString("N"))

                    try
                        use host = new McpHostProcess(workspace.Root, externalRoot)
                        do! host.Send initializeRequest
                        let! _ = host.ReadResponse(1, 5000)
                        do! host.Send initializedNotification
                        do! host.Send(toolCall 30 "build" "{\"target\":\"lib.csproj\"}")
                        let! response = host.ReadResponse(30, 120000)
                        Expect.equal (response.["result"].["isError"].GetValue<bool>()) false "configured artifact root starts the host"

                        // ArtifactRegistry.EndSession removes retained evidence when the host exits.
                        let retained =
                            Directory.Exists externalRoot
                            && Directory.EnumerateFiles(externalRoot, "*", SearchOption.AllDirectories) |> Seq.isEmpty |> not

                        Expect.isTrue retained "configured artifact root retains evidence"

                        do! stop host
                    finally
                        if Directory.Exists externalRoot then
                            Directory.Delete(externalRoot, true)
                })

            testCaseTask "the child build uses the injected host rather than PATH" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore

                    let outsideRoot =
                        Path.Combine(Path.GetTempPath(), "mcp-dotnet-host", Guid.NewGuid().ToString("N"))

                    Directory.CreateDirectory outsideRoot |> ignore
                    let fake = Path.Combine(outsideRoot, "not-dotnet.exe")
                    File.WriteAllText(fake, "not a portable executable")

                    try
                        use host = new McpHostProcess(workspace.Root, workspaceArtifactRoot workspace, injectedHost = fake)
                        do! host.Send initializeRequest
                        let! _ = host.ReadResponse(1, 5000)
                        do! host.Send initializedNotification

                        do! host.Send(toolCall 20 "build" """{"target":"lib.csproj"}""")
                        let! response = host.ReadResponse(20, 120000)
                        let payload = structured response

                        Expect.equal (response.["result"].["isError"].GetValue<bool>()) true "child launch failed"
                        Expect.equal
                            (payload.["error"].["code"].GetValue<string>())
                            "PROCESS_START_FAILURE"
                            "the injected host, not PATH dotnet, was launched"

                        do! stop host
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
                    use host = new McpHostProcess(workspace.Root, workspaceArtifactRoot workspace)

                    do! host.Send initializeRequest
                    let! initialized = host.ReadResponse(1, 5000)
                    Expect.equal (initialized.["result"].["serverInfo"].["name"].GetValue<string>()) "mcp-store-dotnet" "server name"

                    do! host.Send initializedNotification
                    do! host.Send toolsListRequest
                    let! listed = host.ReadResponse(2, 5000)
                    let names =
                        listed.["result"].["tools"].AsArray()
                        |> Seq.map (fun tool -> tool.["name"].GetValue<string>())
                        |> Seq.toList

                    Expect.equal
                        names
                        [ "build"; "test"; "details" ]
                        "exactly three capability-scoped tools"

                    do! stop host

                    for line in host.Received do
                        let parsed = JsonNode.Parse line
                        Expect.equal (parsed.["jsonrpc"].GetValue<string>()) "2.0" "stdout line is JSON-RPC"
                })

            testCaseTask "unknown tool and unknown run id return compact errors" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root, workspaceArtifactRoot workspace)

                    do! host.Send initializeRequest
                    let! _ = host.ReadResponse(1, 5000)
                    do! host.Send initializedNotification

                    do! host.Send(toolCall 3 "run_shell" """{"command":"rm -rf /"}""")
                    let! unknownTool = host.ReadResponse(3, 5000)
                    let unknownToolCode = (structured unknownTool).["error"].["code"].GetValue<string>()
                    Expect.equal (unknownTool.["result"].["isError"].GetValue<bool>()) true "unknown tool is an error"
                    Expect.equal unknownToolCode "INVALID_INPUT" "unknown tool code"

                    do! host.Send(toolCall 4 "details" """{"runId":"missing-run","kind":"errors"}""")
                    let! unknownRun = host.ReadResponse(4, 5000)
                    let unknownRunCode = (structured unknownRun).["error"].["code"].GetValue<string>()
                    Expect.equal unknownRunCode "UNKNOWN_RUN_ID" "unknown run id code"

                    do! stop host
                })

            testCaseTask "build tool returns semantic status without raw output" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root, workspaceArtifactRoot workspace)

                    do! host.Send initializeRequest
                    let! _ = host.ReadResponse(1, 5000)
                    do! host.Send initializedNotification

                    do! host.Send(toolCallWithMeta 4 "details" "{\"runId\":\"missing-run\",\"kind\":\"errors\"}")
                    let! metaResponse = host.ReadResponse(4, 5000)
                    Expect.equal (metaResponse.["result"].["isError"].GetValue<bool>()) true "optional MCP _meta is ignored"
                    Expect.equal ((structured metaResponse).["error"].["code"].GetValue<string>()) "UNKNOWN_RUN_ID" "_meta does not alter tool dispatch"

                    do! host.Send("""{"jsonrpc":"2.0","id":40,"method":"tools/call","params":{"name":"details","arguments":{"runId":"missing-run","kind":"errors"},"_unexpected":true}}""")
                    let! unknownParameter = host.ReadResponse(40, 5000)
                    Expect.equal (unknownParameter.["error"].["code"].GetValue<int>()) -32602 "other MCP tool-call metadata remains rejected"

                    do! host.Send(toolCall 5 "build" """{"target":"lib.csproj"}""")
                    let! response = host.ReadResponse(5, 120000)
                    let payload = structured response

                    Expect.equal (response.["result"].["isError"].GetValue<bool>()) false "transport succeeded"
                    Expect.equal (payload.["ok"].GetValue<bool>()) true "ok flag"
                    Expect.equal (payload.["status"].GetValue<string>()) "succeeded" "semantic status"
                    Expect.equal (payload.["errorCount"].GetValue<int>()) 0 "no errors"
                    Expect.isNotNull (payload.["runId"]) "opaque run id returned"

                    let text = textContent response
                    Expect.isFalse (text.Contains "Build succeeded") "raw log not leaked"
                    Expect.isTrue (text.Length < Budgets.Defaults.TotalToolResultSize) "compact result bounded"

                    do! stop host
                })

            testCaseTask "cancellation notification stops an in-flight run" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root, workspaceArtifactRoot workspace)

                    do! host.Send initializeRequest
                    let! _ = host.ReadResponse(1, 5000)
                    do! host.Send initializedNotification

                    do! host.Send(toolCall 10 "build" """{"target":"lib.csproj","timeoutMs":1800000}""")

                    do!
                        host.Send """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":10}}"""

                    let! response = host.ReadResponse(10, 60000)
                    let status = (structured response).["status"].GetValue<string>()
                    Expect.equal status "cancelled" "run cancelled"

                    do! stop host
                })

            testCaseTask "test tool returns counts, bounded failures, and retrievable details" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    let projectPath = workspace.CreateXunitTestProject("sample", "sample")
                    let target = Path.GetRelativePath(workspace.Root, projectPath).Replace('\\', '/')
                    use host = new McpHostProcess(workspace.Root, workspaceArtifactRoot workspace)

                    do! host.Send initializeRequest
                    let! _ = host.ReadResponse(1, 5000)
                    do! host.Send initializedNotification

                    do! host.Send(toolCall 6 "test" $"{{\"target\":\"{target}\"}}")
                    let! response = host.ReadResponse(6, 300000)
                    let payload = structured response

                    Expect.equal (response.["result"].["isError"].GetValue<bool>()) false "transport succeeded"
                    Expect.equal (payload.["ok"].GetValue<bool>()) true "ok flag"
                    Expect.equal (payload.["status"].GetValue<string>()) "failed" "one failing test maps to failed"
                    Expect.equal (payload.["trxAvailable"].GetValue<bool>()) true "trx retained"
                    Expect.isNull (payload.["trxUnavailableReason"]) "no TRX reason when retained"

                    let trxArtifacts =
                        Directory.EnumerateFiles(workspaceArtifactRoot workspace, "*.trx", SearchOption.AllDirectories)
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
                    do! host.Send(toolCall 7 "details" $"{{\"runId\":\"{runId}\",\"kind\":\"failed-tests\"}}")
                    let! detailsResponse = host.ReadResponse(7, 30000)
                    let details = structured detailsResponse
                    Expect.equal (details.["ok"].GetValue<bool>()) true "details ok"
                    Expect.equal (details.["total"].GetValue<int>()) 1 "one failed test detail"

                    do! stop host
                })

            testCaseTask "failed-test details on a build run return a compact unavailable error" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", validClassSource) |> ignore
                    use host = new McpHostProcess(workspace.Root, workspaceArtifactRoot workspace)

                    do! host.Send initializeRequest
                    let! _ = host.ReadResponse(1, 5000)
                    do! host.Send initializedNotification

                    do! host.Send(toolCall 8 "build" """{"target":"lib.csproj"}""")
                    let! response = host.ReadResponse(8, 120000)
                    let runId = (structured response).["runId"].GetValue<string>()

                    do! host.Send(toolCall 9 "details" $"{{\"runId\":\"{runId}\",\"kind\":\"failed-tests\"}}")
                    let! detailsResponse = host.ReadResponse(9, 30000)
                    let payload = structured detailsResponse

                    Expect.equal (detailsResponse.["result"].["isError"].GetValue<bool>()) true "unavailable is an error"
                    Expect.equal (payload.["error"].["code"].GetValue<string>()) "TEST_DETAIL_UNAVAILABLE" "actionable code"
                    Expect.isFalse ((textContent detailsResponse).Contains "stdout.log") "no physical artifact path leaked"

                    do! stop host
                })

            testCaseTask "detail responses stay within the total tool-result budget" (fun () ->
                task {
                    use workspace = new TempWorkspace()
                    workspace.CreateClassLibrary("lib", manyErrorSource 120 300) |> ignore
                    use host = new McpHostProcess(workspace.Root, workspaceArtifactRoot workspace)

                    do! host.Send initializeRequest
                    let! _ = host.ReadResponse(1, 5000)
                    do! host.Send initializedNotification

                    do! host.Send(toolCall 11 "build" """{"target":"lib.csproj"}""")
                    let! buildResponse = host.ReadResponse(11, 120000)
                    let payload = structured buildResponse
                    Expect.equal (payload.["status"].GetValue<string>()) "failed" "many-error build failed"
                    let runId = payload.["runId"].GetValue<string>()

                    do! host.Send(
                        toolCall
                            12
                            "details"
                            $"{{\"runId\":\"{runId}\",\"kind\":\"errors\",\"offset\":0,\"limit\":128}}"
                    )

                    let! detailsResponse = host.ReadResponse(12, 60000)
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

                    do! stop host
                })
        ])

let tests = hostTests
