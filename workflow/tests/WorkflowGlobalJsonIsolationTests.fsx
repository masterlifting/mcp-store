// Process-boundary regression: Workflow host startup must not depend on the
// SDK selected by a consumer workspace global.json.

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let releaseEntryDll =
    Path.Combine(repoRoot, "workflow", "bin", "Release", "net11.0", "Mcp.Workflow.dll")

let resolveDotnetHost () =
    let names =
        if OperatingSystem.IsWindows() then
            [ "dotnet.exe" ]
        else
            [ "dotnet" ]

    let pathValue =
        match Environment.GetEnvironmentVariable "PATH" with
        | null -> ""
        | value -> value

    pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
    |> Array.tryPick (fun directory ->
        names
        |> List.map (fun name -> Path.Combine(directory.Trim(), name))
        |> List.tryFind File.Exists)
    |> Option.map Path.GetFullPath
    |> Option.defaultWith (fun () -> failwith "dotnet host is not on PATH")

let dotnetHost = resolveDotnetHost ()

let jstr (value: string) : JsonNode = JsonValue.Create value
let jint (value: int) : JsonNode = JsonValue.Create value

let jobj (fields: (string * JsonNode) list) =
    let node = JsonObject()
    fields |> List.iter (fun (key, value) -> node.[key] <- value)
    node

let readLine (child: Process) =
    let pending = child.StandardOutput.ReadLineAsync()

    if not (pending.Wait 30000) then
        child.Kill true
        failwith "timed out waiting for Workflow initialize response"

    if isNull pending.Result then
        failwithf
            "Workflow stdout closed before initialize response: %s"
            (child.StandardError.ReadToEnd())

    pending.Result

let assertStartsWithGlobalJson (sdkVersion: string) =
    let workspace =
        Path.Combine(
            Path.GetTempPath(),
            "mcp-workflow-global-json",
            Guid.NewGuid().ToString("N")
        )

    let catalog =
        Path.Combine(
            Path.GetTempPath(),
            $"workflow-profile-catalog-{Guid.NewGuid():N}.json"
        )

    Directory.CreateDirectory workspace |> ignore

    try
        let globalJson =
            sprintf "{\"sdk\":{\"version\":\"%s\",\"rollForward\":\"disable\"}}" sdkVersion

        File.WriteAllText(Path.Combine(workspace, "global.json"), globalJson)
        File.WriteAllText(catalog, "{\"profiles\":[]}")

        let catalogHash =
            SHA256.HashData(File.ReadAllBytes catalog)
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()

        let startInfo = ProcessStartInfo()
        startInfo.FileName <- dotnetHost
        startInfo.ArgumentList.Add "exec"
        startInfo.ArgumentList.Add releaseEntryDll
        startInfo.ArgumentList.Add "--profile-catalog"
        startInfo.ArgumentList.Add catalog
        startInfo.ArgumentList.Add "--profile-catalog-sha256"
        startInfo.ArgumentList.Add catalogHash
        startInfo.WorkingDirectory <- workspace
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true

        use child = Process.Start startInfo

        try
            let request =
                jobj
                    [ "jsonrpc", jstr "2.0"
                      "id", jint 1
                      "method", jstr "initialize"
                      "params",
                      jobj
                          [ "protocolVersion", jstr "2025-11-25"
                            "capabilities", jobj []
                            "clientInfo",
                            jobj
                                [ "name", jstr "global-json-isolation"
                                  "version", jstr "1" ] ] ]

            child.StandardInput.WriteLine(request.ToJsonString())
            child.StandardInput.Flush()

            let response = JsonNode.Parse(readLine child)

            if not (isNull response.["error"]) then
                failwithf
                    "Workflow startup was coupled to workspace SDK %s: %s"
                    sdkVersion
                    (response.["error"].ToJsonString())

            let serverName =
                response.["result"].["serverInfo"].["name"].GetValue<string>()

            if serverName <> "mcp-store-workflow" then
                failwithf
                    "unexpected Workflow server name for SDK %s: %s"
                    sdkVersion
                    serverName
        finally
            try
                if not child.HasExited then
                    child.Kill true
            with _ ->
                ()
    finally
        if Directory.Exists workspace then
            Directory.Delete(workspace, true)

        if File.Exists catalog then
            File.Delete catalog

for sdkVersion in [ "8.0.100"; "99.0.100" ] do
    assertStartsWithGlobalJson sdkVersion

printfn "OK Workflow host startup is independent of workspace global.json SDK selection."
