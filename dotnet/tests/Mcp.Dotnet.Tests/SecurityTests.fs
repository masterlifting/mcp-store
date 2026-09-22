module Mcp.Dotnet.Tests.SecurityTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

let private classLibraryProject =
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>"

let private pathsFor (workspace: TempWorkspace) =
    let directory = Path.Combine(workspace.Root, "artifacts", "run")

    { Directory = directory
      Metadata = Path.Combine(directory, "metadata.json")
      Stdout = Path.Combine(directory, "stdout.log")
      Stderr = Path.Combine(directory, "stderr.log")
      Binlog = Path.Combine(directory, "build.binlog")
      Trx = Path.Combine(directory, "test-results.trx")
      ParsedEvidence = Path.Combine(directory, "parsed-evidence.json") }

let private evidenceFor (handle: RunHandle) =
    { Operation = VerificationOperation.Build
      Process = capturedProcess (ProcessStatus.Completed 0) (TimeSpan.FromSeconds 1.0) handle.Paths
      Diagnostics = []
      Tests = None
      MetadataPath = handle.Paths.Metadata
      ParsedEvidencePath = handle.Paths.ParsedEvidence }

let private workspaceTests =
    testList "workspace validation" [
        testCase "valid workspace root resolves"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let root = PathAuthorization.validateWorkspace workspace.Root |> expectOk "valid root"
            Expect.isTrue (String.Equals(root, workspace.Root, StringComparison.OrdinalIgnoreCase)) "normalized root"

        testCase "missing and blank workspace roots are rejected"
        <| fun _ ->
            PathAuthorization.validateWorkspace (Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
            |> expectErrorMatching "missing root" isUnauthorizedPath
            |> ignore

            PathAuthorization.validateWorkspace "  "
            |> expectErrorMatching "blank root" isInvalidInput
            |> ignore

        testCase "reparse-point workspace roots are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            Directory.CreateDirectory(Path.Combine(workspace.Root, "real")) |> ignore
            let link = workspace.CreateJunction("jlink", "real")

            PathAuthorization.validateWorkspace link
            |> expectErrorMatching "junction root" isUnauthorizedPath
            |> ignore
    ]

let private targetTests =
    testList "target authorization" [
        testCase "absent target authorizes the workspace root only"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let authorized = PathAuthorization.authorize workspace.Root None |> expectOk "no target"
            Expect.equal authorized.Target None "no target"
            Expect.isTrue (String.Equals(authorized.WorkspaceRoot, workspace.Root, StringComparison.OrdinalIgnoreCase)) "root"

        testCase "existing relative project authorizes and normalizes"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let projectPath = workspace.CreateClassLibrary("lib", validClassSource)
            let authorized = PathAuthorization.authorize workspace.Root (Some "lib.csproj") |> expectOk "valid target"
            Expect.equal authorized.Target (Some projectPath) "normalized target"

        testCase "absolute, traversal, and unsupported targets are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore

            PathAuthorization.authorize workspace.Root (Some(Path.Combine(workspace.Root, "lib.csproj")))
            |> expectErrorMatching "absolute" isUnauthorizedPath
            |> ignore

            PathAuthorization.authorize workspace.Root (Some "../outside.csproj")
            |> expectErrorMatching "traversal" isUnauthorizedPath
            |> ignore

            workspace.Write("notes.txt", "text") |> ignore

            PathAuthorization.authorize workspace.Root (Some "notes.txt")
            |> expectErrorMatching "extension" isInvalidInput
            |> ignore

        testCase "missing target, directory target, and NUL are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()

            PathAuthorization.authorize workspace.Root (Some "missing.csproj")
            |> expectErrorMatching "missing" isUnauthorizedPath
            |> ignore

            Directory.CreateDirectory(Path.Combine(workspace.Root, "dir.csproj")) |> ignore

            PathAuthorization.authorize workspace.Root (Some "dir.csproj")
            |> expectErrorMatching "directory target" isUnauthorizedPath
            |> ignore

            PathAuthorization.authorize workspace.Root (Some "lib\u0000.csproj")
            |> expectErrorMatching "nul" isInvalidInput
            |> ignore

        testCase "blank target normalization is a typed invalid input"
        <| fun _ ->
            use workspace = new TempWorkspace()

            PathAuthorization.authorize workspace.Root (Some "   ")
            |> expectErrorMatching "blank target" isInvalidInput
            |> ignore

        testCase "reparse-point ancestors are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.Write("real/lib.csproj", classLibraryProject) |> ignore
            workspace.Write("real/lib.cs", validClassSource) |> ignore
            workspace.CreateJunction("link", "real") |> ignore

            PathAuthorization.authorize workspace.Root (Some "link/lib.csproj")
            |> expectErrorMatching "reparse ancestor" isUnauthorizedPath
            |> ignore

        testCase "revalidation detects a workspace target changed after authorization"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let projectPath = workspace.CreateClassLibrary("lib", validClassSource)
            let authorized = PathAuthorization.authorize workspace.Root (Some "lib.csproj") |> expectOk "authorized"
            Expect.isOk (PathAuthorization.revalidate authorized) "unchanged revalidates"
            File.Delete projectPath

            PathAuthorization.revalidate authorized
            |> expectErrorMatching "changed target" isUnauthorizedPath
            |> ignore
    ]

let private artifactRootTests =
    testList "artifact root authorization" [
        testCase "relative artifact root is rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()

            PathAuthorization.validateArtifactRoot workspace.Root ".mcp-store/dotnet-state"
            |> expectErrorMatching "relative artifact root" isInvalidInput
            |> ignore

        testCase "absolute artifact root outside the workspace is accepted without eager creation"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use artifactWorkspace = new TempWorkspace()
            let requested = Path.Combine(artifactWorkspace.Root, "state")

            let actual =
                PathAuthorization.validateArtifactRoot workspace.Root requested
                |> expectOk "external artifact root"

            Expect.equal actual (Path.GetFullPath requested) "canonical external artifact root"
            Expect.isFalse (Directory.Exists requested) "validation remains lazy"

        testCase "external reparse artifact ancestors are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use artifactWorkspace = new TempWorkspace()
            Directory.CreateDirectory(Path.Combine(artifactWorkspace.Root, "artreal")) |> ignore
            artifactWorkspace.CreateJunction("artlink", "artreal") |> ignore
            let requested = Path.Combine(artifactWorkspace.Root, "artlink", "artifacts")

            PathAuthorization.validateArtifactRoot workspace.Root requested
            |> expectErrorMatching "reparse artifact root" isUnauthorizedPath
            |> ignore

        testCase "a missing external artifact root below a reparse point is rejected before creation"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use artifactWorkspace = new TempWorkspace()
            Directory.CreateDirectory(Path.Combine(artifactWorkspace.Root, "real")) |> ignore
            artifactWorkspace.CreateJunction("jlink", "real") |> ignore
            let requested = Path.Combine(artifactWorkspace.Root, "jlink", "new", "artifacts")

            PathAuthorization.validateArtifactRoot workspace.Root requested
            |> expectErrorMatching "reparse pre-creation" isUnauthorizedPath
            |> ignore

            Expect.isFalse
                (Directory.Exists(Path.Combine(artifactWorkspace.Root, "real", "new")))
                "no directory was materialized through the junction"

        testCase "artifact root with an invalid character returns typed invalid input"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let invalid = Path.Combine(Path.GetTempPath(), "bad\u0000root")

            PathAuthorization.validateArtifactRoot workspace.Root invalid
            |> expectErrorMatching "nul artifact root" isInvalidInput
            |> ignore
    ]

let private noLaunchTests =
    testList "no-launch rejection" [
        testCaseTask "a target invalidated after authorization is rejected before process start" (fun () ->
            task {
                use workspace = new TempWorkspace()
                let projectPath = workspace.CreateClassLibrary("lib", validClassSource)
                let paths = pathsFor workspace

                let invocation =
                    Invocation.build
                        workspace.Root
                        (dotnetHost ())
                        Budgets.Defaults
                        { Target = Some "lib.csproj"
                          Configuration = None
                          NoRestore = None
                          Timeout = None }
                        paths
                    |> expectOk "build invocation"

                File.Delete projectPath

                let! result = ProcessRunner.run invocation paths CancellationToken.None

                result
                |> expectErrorMatching "pre-launch rejection" isUnauthorizedPath
                |> ignore

                Expect.isFalse (File.Exists paths.Stdout) "no stdout artifact"
                Expect.isFalse (File.Exists paths.Stderr) "no stderr artifact"
            })
    ]

let private registryTests =
    testList "artifact registry" [
        testCase "run identifiers are opaque base64url and independent of directory names"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), TimeSpan.FromHours 1.0)
            let handle = startRun registry
            let runIdText = RunId.value handle.RunId
            let directoryName = Path.GetFileName handle.Paths.Directory

            Expect.equal runIdText.Length 43 "256-bit base64url length"
            Expect.isTrue (Regex.IsMatch(runIdText, "^[A-Za-z0-9_-]+$")) "base64url alphabet"
            Expect.notEqual directoryName runIdText "directory name is independent"
            Expect.isFalse (handle.Paths.Directory.Contains(runIdText, StringComparison.Ordinal)) "path does not embed run id"

        testCase "each run receives a unique isolated directory"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let artifactRoot = Path.Combine(workspace.Root, "artifacts")
            use registry = new ArtifactRegistry(artifactRoot, TimeSpan.FromHours 1.0)
            let first = startRun registry
            let second = startRun registry

            Expect.notEqual (RunId.value first.RunId) (RunId.value second.RunId) "unique run ids"
            Expect.notEqual first.Paths.Directory second.Paths.Directory "unique directories"
            Expect.isTrue (first.Paths.Directory.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase)) "first contained"
            Expect.isTrue (second.Paths.Directory.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase)) "second contained"

        testCase "unknown and incomplete run ids resolve to actionable errors"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), TimeSpan.FromHours 1.0)
            let handle = startRun registry

            registry.Get "not-a-run-id"
            |> expectErrorMatching "unknown run id" isUnknownRunId
            |> ignore

            registry.Get(RunId.value handle.RunId)
            |> expectErrorMatching "incomplete run" isMissingArtifact
            |> ignore

            registry.Get "  "
            |> expectErrorMatching "blank run id" isUnknownRunId
            |> ignore

        testCase "completion rejects foreign evidence paths and double completion"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), TimeSpan.FromHours 1.0)
            let handle = startRun registry

            let foreign =
                { evidenceFor handle with
                    Process =
                        { (evidenceFor handle).Process with
                            StdoutPath = Path.Combine(workspace.Root, "elsewhere.log") } }

            registry.Complete(handle, foreign)
            |> expectErrorMatching "foreign evidence" isArtifactFailure
            |> ignore

            registry.Complete(handle, evidenceFor handle) |> expectOk "first completion" |> ignore

            registry.Complete(handle, evidenceFor handle)
            |> expectErrorMatching "double completion" isArtifactFailure
            |> ignore

            registry.Get(RunId.value handle.RunId) |> expectOk "completed evidence" |> ignore

        testCase "abort and session end remove owned artifacts"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), TimeSpan.FromHours 1.0)
            let first = startRun registry
            let firstDirectory = first.Paths.Directory
            registry.Abort first
            Expect.isFalse (Directory.Exists firstDirectory) "aborted directory removed"

            let second = startRun registry
            let secondDirectory = second.Paths.Directory
            registry.Complete(second, evidenceFor second) |> expectOk "complete" |> ignore
            registry.EndSession()
            Expect.equal registry.Count 0 "registry cleared"
            Expect.isFalse (Directory.Exists secondDirectory) "session directory removed"

            registry.Get(RunId.value second.RunId)
            |> expectErrorMatching "expired run id" isUnknownRunId
            |> ignore

        testCaseTask "concurrent runs allocate unique isolated artifacts" (fun () ->
            task {
                use workspace = new TempWorkspace()
                let artifactRoot = Path.Combine(workspace.Root, "artifacts")
                use registry = new ArtifactRegistry(artifactRoot, TimeSpan.FromHours 1.0)

                let! handles =
                    [ for _ in 1..64 -> Task.Run(fun () -> startRun registry) ]
                    |> Task.WhenAll

                let runIds = handles |> Array.map (fun handle -> RunId.value handle.RunId)
                let directories = handles |> Array.map (fun handle -> handle.Paths.Directory)

                Expect.equal (Set.ofArray runIds).Count 64 "unique run ids"
                Expect.equal (Set.ofArray directories).Count 64 "unique directories"

                Expect.isTrue
                    (directories
                     |> Array.forall (fun directory ->
                         directory.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase)))
                    "all directories contained"
            })
    ]

let tests =
    testList
        "security"
        [ workspaceTests
          targetTests
          artifactRootTests
          noLaunchTests
          registryTests ]
