module Mcp.Verifier.Tests.Support

open System
open System.Diagnostics
open System.IO
open System.Text
open Mcp.Verifier

// Shared fixtures and assertions for the deterministic producer test suite.
// Fixtures stay under one GUID temp root per workspace and clean up only that root.

let fail name (message: string) : 'a = failwithf "%s: %s" name message

let expectOk name result =
    match result with
    | Ok value -> value
    | Error error -> fail name (VerificationError.message error)

let expectError name result =
    match result with
    | Ok _ -> fail name "expected Error but got Ok"
    | Error error -> error

let expectErrorMatching name predicate result =
    let error = expectError name result

    if not (predicate error) then
        fail name (sprintf "unexpected error: %A" error)

    error

let isInvalidInput = function
    | VerificationError.InvalidInput _ -> true
    | _ -> false

let isUnauthorizedPath = function
    | VerificationError.UnauthorizedPath _ -> true
    | _ -> false

let isUnknownRunId = function
    | VerificationError.UnknownRunId _ -> true
    | _ -> false

let isMissingArtifact = function
    | VerificationError.MissingArtifact _ -> true
    | _ -> false

let isInvalidPagination = function
    | VerificationError.InvalidPagination _ -> true
    | _ -> false

let isUnsupportedDetailKind = function
    | VerificationError.UnsupportedDetailKind _ -> true
    | _ -> false

let isArtifactFailure = function
    | VerificationError.ArtifactFailure _ -> true
    | _ -> false

let isProcessStartFailure = function
    | VerificationError.ProcessStartFailure _ -> true
    | _ -> false

let isUnavailableTestDetail = function
    | VerificationError.UnavailableTestDetail _ -> true
    | _ -> false

let isArtifactQuotaExceeded = function
    | VerificationError.ArtifactQuotaExceeded _ -> true
    | _ -> false

// Junctions on Windows and directory symlinks elsewhere keep reparse coverage
// portable without requiring symbolic-link privileges on Windows.
let createDirectoryLink (link: string) (target: string) =
    if OperatingSystem.IsWindows() then
        let startInfo = ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        use child = Process.Start startInfo
        child.WaitForExit()

        if child.ExitCode <> 0 then
            fail "createDirectoryLink" $"junction creation failed: {child.StandardError.ReadToEnd()}"
    else
        Directory.CreateSymbolicLink(link, target) |> ignore

    link

type TempWorkspace() =
    let root =
        Path.Combine(Path.GetTempPath(), "mcp-verifier-tests", Guid.NewGuid().ToString("N"))

    do Directory.CreateDirectory root |> ignore

    member _.Root = root

    member _.Write(relativePath: string, content: string) =
        let path = Path.Combine(root, relativePath)
        let directory = Path.GetDirectoryName path

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

        File.WriteAllText(path, content)
        path

    member this.CreateClassLibrary(name: string, source: string) =
        let projectPath =
            this.Write(
                $"{name}.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>"
            )

        this.Write($"{name}.cs", source) |> ignore
        projectPath

    member this.CreateClassLibraryIn(relativeDirectory: string, name: string, source: string) =
        let projectPath =
            this.Write(
                Path.Combine(relativeDirectory, $"{name}.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>"
            )

        this.Write(Path.Combine(relativeDirectory, $"{name}.cs"), source) |> ignore
        projectPath

    member this.CreateXunitTestProject(relativeDirectory: string, name: string) =
        let directory = Path.Combine(root, relativeDirectory)
        Directory.CreateDirectory directory |> ignore

        // Cleared sources force restore to resolve the cached test packages without network.
        File.WriteAllText(
            Path.Combine(directory, "NuGet.config"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear /></packageSources></configuration>"
        )

        let projectPath = Path.Combine(directory, $"{name}.csproj")

        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>"
            + "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />"
            + "<PackageReference Include=\"xunit\" Version=\"2.9.3\" />"
            + "<PackageReference Include=\"xunit.runner.visualstudio\" Version=\"2.5.7\" /></ItemGroup></Project>"
        )

        File.WriteAllText(
            Path.Combine(directory, "SampleTests.cs"),
            "using Xunit;\n"
            + "public class SampleTests {\n"
            + "    [Fact] public void Passes() { Assert.Equal(1, 1); }\n"
            + "    [Fact] public void Fails() { Assert.Equal(1, 2); }\n"
            + "}\n"
        )

        projectPath

    member this.CreateJunction(linkName: string, targetRelative: string) =
        createDirectoryLink (Path.Combine(root, linkName)) (Path.Combine(root, targetRelative))

    interface IDisposable with
        member _.Dispose() =
            try
                Directory.Delete(root, true)
            with _ ->
                ()

let validClassSource = "public class Class1 { public int Value => 1; }"
let invalidClassSource = "public class Class1 { public int Value() { return NotDefined; } }"

let manyErrorSource count nameLength =
    let builder = StringBuilder()
    builder.AppendLine("public class ManyErrors {") |> ignore

    for index in 1..count do
        let identifier = String('u', nameLength) + string index
        builder.AppendLine($"    public int M{index}() {{ return {identifier}; }}") |> ignore

    builder.AppendLine("}") |> ignore
    builder.ToString()

let repositoryRoot () =
    let rec walk (directory: DirectoryInfo) =
        if isNull directory then
            fail "repositoryRoot" "could not locate repository root"
        elif File.Exists(Path.Combine(directory.FullName, "dotnet", "verifier", "Mcp.Verifier.fsproj"))
        then
            directory.FullName
        else
            walk directory.Parent

    walk (DirectoryInfo AppContext.BaseDirectory)

// Test fixture only: production injects the trusted .NET host from the
// validated machine-local binding. Tests resolve the real muxer so they can
// exercise real builds and tests; they never feed this into production paths
// except as an explicitly injected host.
let dotnetHost () =
    let executable = if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"

    let fromEnvironment =
        Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
        |> Option.ofObj
        |> Option.filter (fun path -> not (String.IsNullOrWhiteSpace path) && File.Exists path)

    let fromProcess =
        Environment.ProcessPath
        |> Option.ofObj
        |> Option.filter (fun path ->
             Path.GetFileName(path).Equals(executable, StringComparison.OrdinalIgnoreCase))

    let fromPath =
        let separator = if OperatingSystem.IsWindows() then ';' else ':'

        (Environment.GetEnvironmentVariable "PATH").Split(separator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun directory ->
             let candidate = Path.Combine(directory, executable)
             if File.Exists candidate then Some candidate else None)

    match fromEnvironment |> Option.orElse fromProcess |> Option.orElse fromPath with
    | Some path -> Path.GetFullPath path
    | None -> fail "dotnetHost" "the .NET muxer was not found for test execution"

let startRun (registry: ArtifactRegistry) =
    match registry.Start VerificationOperation.Build with
    | Ok handle -> handle
    | Error error -> fail "startRun" (VerificationError.message error)

let capturedProcess status duration (paths: ArtifactPaths) =
    { Status = status
      Duration = duration
      StdoutPath = paths.Stdout
      StderrPath = paths.Stderr
      StdoutBytes = 0L
      StderrBytes = 0L }

let errorDiagnostic message =
    { Severity = DiagnosticSeverity.ErrorDiagnostic
      Code = Some "CS0001"
      File = Some "a.cs"
      Line = Some 1
      Column = Some 2
      Message = message }

let warningDiagnostic message =
    { Severity = DiagnosticSeverity.WarningDiagnostic
      Code = None
      File = None
      Line = None
      Column = None
      Message = message }

let trxDocument (results: (string * string * string option) list) =
    let builder = StringBuilder()
    builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>") |> ignore
    builder.AppendLine("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">") |> ignore
    builder.AppendLine("  <Results>") |> ignore

    for (name, outcome, message) in results do
        builder.AppendLine($"    <UnitTestResult testName=\"{name}\" outcome=\"{outcome}\">") |> ignore

        match message with
        | Some value -> builder.AppendLine($"      <Message>{value}</Message>") |> ignore
        | None -> ()

        builder.AppendLine("    </UnitTestResult>") |> ignore

    builder.AppendLine("  </Results>") |> ignore
    builder.AppendLine("</TestRun>") |> ignore
    builder.ToString()
