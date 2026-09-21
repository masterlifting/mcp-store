open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let workflow = Path.Combine(root, "workflow")
let outputRoot = Path.Combine(workflow, "dist")
let sdk = "11.0.100-rc.1.26425.128"
let version = "0.1.0"

let run arguments =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    arguments |> List.iter info.ArgumentList.Add
    use process = Process.Start info
    let output = process.StandardOutput.ReadToEndAsync()
    let error = process.StandardError.ReadToEndAsync()
    process.WaitForExit()
    if process.ExitCode <> 0 then failwithf "dotnet %s failed: %s" (String.concat " " arguments) (error.Result.Trim())

let revision =
    let info = ProcessStartInfo("git", "rev-parse HEAD")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    use process = Process.Start info
    let value = process.StandardOutput.ReadToEnd().Trim()
    process.WaitForExit()
    if process.ExitCode <> 0 || value.Length <> 40 then failwith "producer revision could not be resolved"
    value

let sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let writeJson path (value: JsonNode) =
    File.WriteAllText(path, value.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

let publish id project (entryDll: string) =
    let destination = Path.Combine(outputRoot, id)
    if Directory.Exists destination then Directory.Delete(destination, true)
    Directory.CreateDirectory destination |> ignore
    let publishRoot = Path.Combine(destination, "publish")
    run [ "publish"; project; "--configuration"; "Release"; "--framework"; "net11.0"; "--self-contained"; "false"; "-p:UseAppHost=false"; "-p:DebugType=None"; "-p:DebugSymbols=false"; "--output"; publishRoot ]

    for file in Directory.EnumerateFiles publishRoot do
        let name = Path.GetFileName file
        if name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) then
            File.Delete file
        else
            File.Move(file, Path.Combine(destination, name))
    Directory.Delete(publishRoot, true)

    File.WriteAllText(Path.Combine(destination, "NOTICE.txt"), "mcp-store Task Runtime MCP distribution\n")
    let archiveName = $"{id}-v{version}.zip"
    let manifest = JsonObject()
    manifest["schemaVersion"] <- JsonValue.Create 1
    manifest["id"] <- JsonValue.Create id
    manifest["version"] <- JsonValue.Create version
    manifest["revision"] <- JsonValue.Create revision
    manifest["sdk"] <- JsonValue.Create sdk
    manifest["entryDll"] <- JsonValue.Create entryDll
    manifest["archive"] <- JsonValue.Create archiveName
    let files = JsonArray()
    for file in Directory.EnumerateFiles(destination) do files.Add(JsonValue.Create(Path.GetFileName file))
    manifest["files"] <- files
    writeJson (Path.Combine(destination, "distribution.json")) manifest
    let archivePath = Path.Combine(outputRoot, archiveName)
    if File.Exists archivePath then File.Delete archivePath
    ZipFile.CreateFromDirectory(destination, archivePath)
    archiveName, sha256 archivePath, sha256 (Path.Combine(destination, "distribution.json"))

Directory.CreateDirectory outputRoot |> ignore
let verifier = publish "mcp-verifier" "dotnet/Mcp.Verifier.fsproj" "Mcp.Verifier.dll"
let workflowDistribution = publish "task-runtime" "workflow/Task.Runtime.fsproj" "Task.Runtime.dll"
printfn "mcp-verifier archive=%s sha256=%s manifest=%s" (let (name, _, _) = verifier in name) (let (_, hash, _) = verifier in hash) (let (_, _, hash) = verifier in hash)
printfn "task-runtime archive=%s sha256=%s manifest=%s" (let (name, _, _) = workflowDistribution in name) (let (_, hash, _) = workflowDistribution in hash) (let (_, _, hash) = workflowDistribution in hash)
