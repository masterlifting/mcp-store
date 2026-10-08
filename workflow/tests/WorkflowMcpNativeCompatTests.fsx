// role: test entrypoint
open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let requiredSdk = "11.0.100-rc.1.26425.128"
let releaseEntryDll = Path.Combine(repoRoot, "workflow", "bin", "Release", "net11.0", "Mcp.Workflow.dll")

let resolveDotnetHost () =
    let names = if OperatingSystem.IsWindows() then [ "dotnet.exe" ] else [ "dotnet" ]
    let pathValue = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
    let candidate =
        pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun directory -> names |> List.map (fun name -> Path.Combine(directory.Trim(), name)) |> List.tryFind File.Exists)
    candidate |> Option.map Path.GetFullPath |> Option.defaultWith (fun () -> failwithf "the required .NET host '%s' is not on PATH" names.Head)

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

let requireReleaseEntry () =
    if not (File.Exists releaseEntryDll) then
        failwithf "authorized producer Release build is required before process-boundary testing: %s" releaseEntryDll

let jstr (value: string) : JsonNode = JsonValue.Create(value)
let jint (value: int) : JsonNode = JsonValue.Create(value)

let jobj (fields: (string * JsonNode) list) =
    let node = JsonObject()
    fields |> List.iter (fun (key, value) -> node.[key] <- value)
    node

let startMcp (host: string) =
    requireReleaseEntry ()
    let catalog = Path.Combine(Path.GetTempPath(), $"task-runtime-profile-catalog-{Guid.NewGuid():N}.json")
    File.WriteAllText(catalog, "{\"profiles\":[]}")
    let catalogHash = SHA256.HashData(File.ReadAllBytes catalog) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- host
    startInfo.ArgumentList.Add "exec"
    startInfo.ArgumentList.Add releaseEntryDll
    startInfo.ArgumentList.Add "--profile-catalog"
    startInfo.ArgumentList.Add catalog
    startInfo.ArgumentList.Add "--profile-catalog-sha256"
    startInfo.ArgumentList.Add catalogHash
    startInfo.RedirectStandardInput <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.WorkingDirectory <- repoRoot
    Process.Start startInfo

let readLine (child: Process) : Async<string> =
    async {
        try
            let! line = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds 240000.0) |> Async.AwaitTask
            if isNull line then return failwith "MCP stdout closed before the initialize response"
            return line
        with :? TimeoutException ->
            return failwith "timed out waiting for the initialize response"
    }

let requestedVersion = "2025-11-25"
let dotnetHost = resolveDotnetHost ()
let mutable child: Process = null

// The single script entry bridge keeps protocol and child-process waits asynchronous.
async {
    do! assertRequiredSdk dotnetHost
    child <- startMcp dotnetHost

    let! outcome =
        async {
        let request =
            jobj
                [ "jsonrpc", jstr "2.0"
                  "id", jint 1
                  "method", jstr "initialize"
                  "params",
                  jobj
                      [ "protocolVersion", jstr requestedVersion
                        "capabilities", jobj []
                        "clientInfo", jobj [ "name", jstr "native-compat-test"; "version", jstr "1" ] ] ]

        do! child.StandardInput.WriteLineAsync(request.ToJsonString()) |> Async.AwaitTask
        do! child.StandardInput.FlushAsync() |> Async.AwaitTask
        let! responseLine = readLine child
        let response = JsonNode.Parse responseLine
        let error = response.["error"]

        if not (isNull error) then
            failwithf "initialize with '%s' returned JSON-RPC error %s: %s" requestedVersion (error.["code"].ToJsonString()) (error.["message"].GetValue<string>())

        let result = response.["result"]
        if isNull result then failwithf "initialize with '%s' returned no result" requestedVersion

        let negotiated = result.["protocolVersion"]
        if isNull negotiated || String.IsNullOrWhiteSpace(negotiated.GetValue<string>()) then
            failwithf "initialize with '%s' returned no protocolVersion" requestedVersion

        printfn "OK MCP protocol negotiation: initialize accepted '%s' and negotiated '%s'" requestedVersion (negotiated.GetValue<string>())
        return ()
        }
        |> Async.Catch

    if not (isNull child) then
        try
            if not child.HasExited then child.Kill true
        with _ -> ()

        try
            do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 30.0) |> Async.AwaitTask
        with _ -> ()
        try
            let! _ = child.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
            ()
        with _ -> ()
        try
            let! _ = child.StandardError.ReadToEndAsync() |> Async.AwaitTask
            ()
        with _ -> ()
        child.Dispose()

    match outcome with
    | Choice1Of2 () -> ()
    | Choice2Of2 error -> raise error
}
// Standalone entry bridge: FSI requires one synchronous top-level boundary.
|> Async.RunSynchronously
