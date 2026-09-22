// Regression pin for the MCP protocol-negotiation boundary. MCP requires the
// server to answer initialize with a supported protocolVersion rather than a
// JSON-RPC error when the client requests a version the server does not
// implement. Native OpenCode 1.18.30 requests "2025-11-25"; the current host
// accepts the native version. Deterministic; no LLM or network. The test uses
// the producer's Release entry DLL through the pinned dotnet host so it never
// restores or builds source during the boundary check.
open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
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

// The version native OpenCode 1.18.30 sends in initialize.
let requestedVersion = "2025-11-25"

let jobj (fields: (string * JsonNode) list) =
    let node = JsonObject()
    fields |> List.iter (fun (key, value) -> node.[key] <- value)
    node

let jstr (value: string) : JsonNode = JsonValue.Create(value)
let jint (value: int) : JsonNode = JsonValue.Create(value)

let startMcp () =
    requireReleaseEntry ()
    let catalog = Path.Combine(Path.GetTempPath(), $"workflow-profile-catalog-{Guid.NewGuid():N}.json")
    File.WriteAllText(catalog, "{\"profiles\":[]}")
    let catalogHash = SHA256.HashData(File.ReadAllBytes catalog) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- dotnetHost
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

let readLine (child: Process) =
    let task = child.StandardOutput.ReadLineAsync()

    if not (task.Wait(240000)) then
        failwith "timed out waiting for the initialize response"

    if isNull task.Result then
        failwith "MCP stdout closed before the initialize response"

    task.Result

let mcp = startMcp ()

try
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

    mcp.StandardInput.WriteLine(request.ToJsonString())
    mcp.StandardInput.Flush()
    let response = JsonNode.Parse(readLine mcp)
    let error = response.["error"]

    if not (isNull error) then
        failwithf
            "initialize with '%s' returned JSON-RPC error %s: %s"
            requestedVersion
            (error.["code"].ToJsonString())
            (error.["message"].GetValue<string>())

    let result = response.["result"]

    if isNull result then
        failwithf "initialize with '%s' returned no result" requestedVersion

    let negotiated = result.["protocolVersion"]

    if isNull negotiated || String.IsNullOrWhiteSpace(negotiated.GetValue<string>()) then
        failwithf "initialize with '%s' returned no protocolVersion" requestedVersion

    printfn "OK MCP protocol negotiation: initialize accepted '%s' and negotiated '%s'" requestedVersion (negotiated.GetValue<string>())
finally
    if not (isNull mcp) then
        try
            if not mcp.HasExited then mcp.Kill true
        with _ ->
            ()

        mcp.Dispose()
