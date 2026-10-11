// role: test entrypoint

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Text.Json
open System.Threading.Tasks

type private Producer =
    { Name: string
      ProjectPath: string
      BuiltAssemblyPath: string }

type private AssemblyInfo =
    { IncludesSourceRevision: bool
      InformationalVersion: string }

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private producers =
    [ { Name = "dotnet"
        ProjectPath = Path.Combine(repoRoot, "dotnet", "Mcp.Dotnet.fsproj")
        BuiltAssemblyPath = Path.Combine(repoRoot, "dotnet", "bin", "Release", "net11.0", "Mcp.Dotnet.dll") }
      { Name = "workflow"
        ProjectPath = Path.Combine(repoRoot, "workflow", "Mcp.Workflow.fsproj")
        BuiltAssemblyPath = Path.Combine(repoRoot, "workflow", "bin", "Release", "net11.0", "Mcp.Workflow.dll") } ]

let private revisionA = String.replicate 40 "1"
let private revisionB = String.replicate 40 "2"
let mutable private passed = 0
let mutable private failed = 0
let mutable private pePassed = 0
let mutable private peFailed = 0
let mutable private peNotRun = 0

let private checkEqual name expected actual =
    if expected = actual then
        passed <- passed + 1
    else
        failed <- failed + 1
        eprintfn "FAIL %s: expected %A, got %A" name expected actual

let private checkTrue name condition =
    if condition then
        passed <- passed + 1
    else
        failed <- failed + 1
        eprintfn "FAIL %s" name

let private checkPeEqual name expected actual =
    if expected = actual then
        pePassed <- pePassed + 1
        printfn "PE integration PASS %s" name
    else
        peFailed <- peFailed + 1
        eprintfn "PE integration FAIL %s: expected %A, got %A" name expected actual

let private parseAssemblyInfo (output: string) : Result<AssemblyInfo, string> =
    try
        use document = JsonDocument.Parse output
        let root = document.RootElement
        let sourceRevisionSetting =
            root.GetProperty("Properties").GetProperty("IncludeSourceRevisionInInformationalVersion").GetString()
        let attributes =
            root.GetProperty("Items").GetProperty("AssemblyAttribute").EnumerateArray()
            |> Seq.tryFind (fun attribute ->
                attribute.GetProperty("Identity").GetString() = "System.Reflection.AssemblyInformationalVersionAttribute")

        match attributes with
        | None -> Error "SDK metadata did not contain AssemblyInformationalVersionAttribute"
        | Some attribute ->
            let informationalVersion = attribute.GetProperty("_Parameter1").GetString()
            Ok
                { IncludesSourceRevision = Boolean.Parse sourceRevisionSetting
                  InformationalVersion = informationalVersion }
    with
    | :? JsonException as error -> Error(sprintf "MSBuild metadata output was not JSON: %s" error.Message)
    | :? InvalidOperationException as error -> Error(sprintf "MSBuild metadata omitted an expected field: %s" error.Message)
    | :? KeyNotFoundException as error -> Error(sprintf "MSBuild metadata omitted an expected field: %s" error.Message)
    | :? FormatException as error -> Error(sprintf "MSBuild returned an invalid property value: %s" error.Message)

let private evaluateAssemblyInfo (producer: Producer) (sourceRevision: string) (includeSourceRevision: bool) : Async<Result<AssemblyInfo, string>> =
    async {
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.WorkingDirectory <- repoRoot
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true

        [ "msbuild"
          producer.ProjectPath
          "-t:GetAssemblyAttributes"
          "-getProperty:IncludeSourceRevisionInInformationalVersion"
          "-getItem:AssemblyAttribute"
          "-p:Configuration=Release"
          "-p:SourceRevisionId=" + sourceRevision ]
        |> List.iter startInfo.ArgumentList.Add

        if includeSourceRevision then
            startInfo.ArgumentList.Add "-p:IncludeSourceRevisionInInformationalVersion=true"

        startInfo.ArgumentList.Add "-nologo"

        try
            use child = new Process(StartInfo = startInfo)

            if not (child.Start()) then
                return Error(sprintf "%s MSBuild metadata process did not start" producer.Name)
            else
                let outputTask = child.StandardOutput.ReadToEndAsync()
                let errorTask = child.StandardError.ReadToEndAsync()
                do! child.WaitForExitAsync() |> Async.AwaitTask
                let! output = outputTask |> Async.AwaitTask
                let! errorOutput = errorTask |> Async.AwaitTask

                if child.ExitCode <> 0 then
                    let details = if String.IsNullOrWhiteSpace errorOutput then output.Trim() else errorOutput.Trim()
                    return Error(sprintf "%s GetAssemblyAttributes failed (%d): %s" producer.Name child.ExitCode details)
                else
                    return parseAssemblyInfo output
        with
        | :? System.ComponentModel.Win32Exception as error ->
            return Error(sprintf "could not start dotnet msbuild for %s: %s" producer.Name error.Message)
        | :? IOException as error ->
            return Error(sprintf "could not read MSBuild metadata for %s: %s" producer.Name error.Message)
        | :? InvalidOperationException as error ->
            return Error(sprintf "MSBuild metadata process failed for %s: %s" producer.Name error.Message)
    }

let private requireMetadata producer revision includeSourceRevision result =
    match result with
    | Ok info -> info
    | Error error -> failwithf "%s SourceRevisionId=%s IncludeSourceRevisionInInformationalVersion=%b: %s" producer.Name revision includeSourceRevision error

let private inspectBuiltAssembly (path: string) : Result<string, string> =
    try
        // Avoid loading producer code because this integration check must not run startup.
        use stream = File.OpenRead path
        use pe = new PEReader(stream)

        if not pe.HasMetadata then
            Error "built PE did not contain CLI metadata"
        else
            let reader = pe.GetMetadataReader()

            let attributeTypeName (handle: EntityHandle) =
                match handle.Kind with
                | HandleKind.TypeReference ->
                    let typeRef = reader.GetTypeReference(MetadataTokens.TypeReferenceHandle(MetadataTokens.GetRowNumber handle))
                    reader.GetString(typeRef.Namespace) + "." + reader.GetString(typeRef.Name)
                | HandleKind.TypeDefinition ->
                    let typeDef = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(MetadataTokens.GetRowNumber handle))
                    reader.GetString(typeDef.Namespace) + "." + reader.GetString(typeDef.Name)
                | _ -> ""

            let informationalAttribute =
                reader.GetAssemblyDefinition().GetCustomAttributes()
                |> Seq.tryFind (fun handle ->
                    let attribute = reader.GetCustomAttribute handle

                    match attribute.Constructor.Kind with
                    | HandleKind.MemberReference ->
                        let memberRef = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle(MetadataTokens.GetRowNumber attribute.Constructor))
                        attributeTypeName memberRef.Parent = "System.Reflection.AssemblyInformationalVersionAttribute"
                    | _ -> false)

            match informationalAttribute with
            | None -> Error "PE metadata did not contain AssemblyInformationalVersionAttribute"
            | Some handle ->
                let attribute = reader.GetCustomAttribute handle
                let blob = reader.GetBlobReader attribute.Value

                if blob.ReadUInt16() <> 1us then
                    Error "PE attribute blob had an invalid custom-attribute prolog"
                else
                    match blob.ReadSerializedString() with
                    | null -> Error "PE attribute argument was null"
                    | value -> Ok value
    with
    | :? IOException as error -> Error(sprintf "could not read the built PE: %s" error.Message)
    | :? BadImageFormatException as error -> Error(sprintf "built output was not a readable PE: %s" error.Message)

let private runSuite () : Async<unit> =
    async {
        for producer in producers do
            let! defaultAResult = evaluateAssemblyInfo producer revisionA false
            let! defaultBResult = evaluateAssemblyInfo producer revisionB false
            let defaultA = requireMetadata producer revisionA false defaultAResult
            let defaultB = requireMetadata producer revisionB false defaultBResult
            printfn "%s generated AssemblyInformationalVersion=%s" producer.Name defaultA.InformationalVersion

            let beforeDefault = failed
            checkEqual (producer.Name + " default source revision property for input A") false defaultA.IncludesSourceRevision
            checkEqual (producer.Name + " default source revision property for input B") false defaultB.IncludesSourceRevision
            checkTrue (producer.Name + " default informational version omits a SHA for input A") (not (defaultA.InformationalVersion.Contains("+", StringComparison.Ordinal)))
            checkTrue (producer.Name + " default informational version omits a SHA for input B") (not (defaultB.InformationalVersion.Contains("+", StringComparison.Ordinal)))
            checkTrue (producer.Name + " default informational version is nonempty") (not (String.IsNullOrWhiteSpace defaultA.InformationalVersion))
            checkEqual (producer.Name + " default informational version is independent of SourceRevisionId") defaultA.InformationalVersion defaultB.InformationalVersion

            let defaultId = if producer.Name = "dotnet" then "R14" else "R15"
            if failed = beforeDefault then
                printfn "PASS %s %s::defaultInformationalVersionIgnoresSourceRevision" defaultId producer.Name
            else
                eprintfn "FAIL %s %s::defaultInformationalVersionIgnoresSourceRevision" defaultId producer.Name

            let! counterfactualAResult = evaluateAssemblyInfo producer revisionA true
            let! counterfactualBResult = evaluateAssemblyInfo producer revisionB true
            let counterfactualA = requireMetadata producer revisionA true counterfactualAResult
            let counterfactualB = requireMetadata producer revisionB true counterfactualBResult

            let beforeCounterfactual = failed
            checkEqual (producer.Name + " explicit source revision property for input A") true counterfactualA.IncludesSourceRevision
            checkEqual (producer.Name + " explicit source revision property for input B") true counterfactualB.IncludesSourceRevision
            checkEqual (producer.Name + " explicit SDK behavior appends input A") (defaultA.InformationalVersion + "+" + revisionA) counterfactualA.InformationalVersion
            checkEqual (producer.Name + " explicit SDK behavior appends input B") (defaultA.InformationalVersion + "+" + revisionB) counterfactualB.InformationalVersion
            checkTrue (producer.Name + " explicit counterfactual versions differ") (counterfactualA.InformationalVersion <> counterfactualB.InformationalVersion)

            let counterfactualId = if producer.Name = "dotnet" then "R16" else "R17"
            if failed = beforeCounterfactual then
                printfn "PASS %s %s::explicitSdkDefaultCounterfactualWouldAppendSourceRevision" counterfactualId producer.Name
            else
                eprintfn "FAIL %s %s::explicitSdkDefaultCounterfactualWouldAppendSourceRevision" counterfactualId producer.Name

            if File.Exists producer.BuiltAssemblyPath then
                match inspectBuiltAssembly producer.BuiltAssemblyPath with
                | Ok actual ->
                    checkPeEqual (producer.Name + " AssemblyInformationalVersion matches SDK metadata") defaultA.InformationalVersion actual
                    printfn "%s owner-built PE AssemblyInformationalVersion=%s" producer.Name actual
                | Error error ->
                    peFailed <- peFailed + 1
                    eprintfn "PE integration FAIL %s owner-built metadata: %s" producer.Name error
            else
                peNotRun <- peNotRun + 1
                printfn "PE integration NOT RUN %s: no built DLL at %s" producer.Name producer.BuiltAssemblyPath

        printfn "revision metadata summary: passed=%d failed=%d skipped=0" passed failed
        printfn "producer PE integration summary: passed=%d failed=%d notRun=%d" pePassed peFailed peNotRun
        if failed > 0 || peFailed > 0 then exit 1
    }

// Standalone entry bridge: the only synchronous wait in this script's flow.
runSuite () |> Async.RunSynchronously
