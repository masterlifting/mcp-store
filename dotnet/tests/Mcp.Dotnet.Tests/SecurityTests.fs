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
        testCase "an external absolute local root is canonicalized without eager creation"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let outside = Path.Combine(Path.GetTempPath(), "mcp-dotnet-artifacts", Guid.NewGuid().ToString("N"))

            try
                let root = PathAuthorization.validateArtifactRoot workspace.Root outside |> expectOk "external artifact root"
                Expect.isFalse (Directory.Exists root) "validation does not create the external root"
                Expect.isFalse (root.StartsWith(workspace.Root, StringComparison.OrdinalIgnoreCase)) "external root is not workspace-local"
                Expect.equal root (Path.GetFullPath outside) "external root is canonical"
                Expect.isOk (PathAuthorization.ensureArtifactRoot root) "the root is materialized on first allocation"
                Expect.isTrue (Directory.Exists root) "external root created lazily"
            finally
                if Directory.Exists outside then
                    Directory.Delete(outside, true)

        testCase "relative artifact roots are rejected without resolution"
        <| fun _ ->
            use workspace = new TempWorkspace()

            for candidate in [ ".mcp-store/dotnet"; "artifacts"; "..\\outside" ] do
                PathAuthorization.validateArtifactRoot workspace.Root candidate
                |> expectErrorMatching $"relative artifact root {candidate}" isUnauthorizedPath
                |> ignore

        testCase "relative traversal cannot opt into an external artifact root"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
            let relativeOutside = Path.GetRelativePath(workspace.Root, outside)

            PathAuthorization.validateArtifactRoot workspace.Root relativeOutside
            |> expectErrorMatching "relative external artifact root" isUnauthorizedPath
            |> ignore

        testCase "empty and whitespace artifact roots are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()

            for candidate in [ ""; "   " ] do
                PathAuthorization.validateArtifactRoot workspace.Root candidate
                |> expectErrorMatching "blank artifact root" isInvalidInput
                |> ignore

        testCase "the workspace root and its descendants are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()

            PathAuthorization.validateArtifactRoot workspace.Root workspace.Root
            |> expectErrorMatching "workspace root" isUnauthorizedPath
            |> ignore

            PathAuthorization.validateArtifactRoot workspace.Root (Path.Combine(workspace.Root, "artifacts"))
            |> expectErrorMatching "workspace descendant" isUnauthorizedPath
            |> ignore

        testCase "an existing file is rejected as an artifact root"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let directory = Path.Combine(Path.GetTempPath(), "mcp-dotnet-file-root")
            Directory.CreateDirectory directory |> ignore
            let file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt")
            File.WriteAllText(file, "not a directory")

            try
                PathAuthorization.validateArtifactRoot workspace.Root file
                |> expectErrorMatching "file artifact root" isUnauthorizedPath
                |> ignore
            finally
                if File.Exists file then
                    File.Delete file

        testCase "a Windows UNC or network path is rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()

            if OperatingSystem.IsWindows() then
                for candidate in [ "\\\\server\\share\\artifacts"; "//server/share/artifacts" ] do
                    PathAuthorization.validateArtifactRoot workspace.Root candidate
                    |> expectErrorMatching "network artifact root" isUnauthorizedPath
                    |> ignore

        testCase "external artifact ancestors must not be reparse points"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let parent = Path.Combine(Path.GetTempPath(), "mcp-dotnet-reparse", Guid.NewGuid().ToString("N"))
            let real = Path.Combine(parent, "real")
            Directory.CreateDirectory real |> ignore
            let link = Path.Combine(parent, "link")
            createDirectoryLink link real |> ignore

            try
                PathAuthorization.validateArtifactRoot workspace.Root (Path.Combine(link, "artifacts"))
                |> expectErrorMatching "external reparse ancestor" isUnauthorizedPath
                |> ignore
            finally
                if Directory.Exists link then
                    try
                        Directory.Delete(link, false)
                    with _ ->
                        ()

                if Directory.Exists parent then
                    try
                        Directory.Delete(parent, true)
                    with _ ->
                        ()

        testCase "reparse artifact ancestors are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let parent = Path.Combine(Path.GetTempPath(), "mcp-dotnet-reparse", Guid.NewGuid().ToString("N"))
            let real = Path.Combine(parent, "real")
            Directory.CreateDirectory real |> ignore
            let link = Path.Combine(parent, "link")
            createDirectoryLink link real |> ignore

            try
                PathAuthorization.validateArtifactRoot workspace.Root link
                |> expectErrorMatching "reparse artifact root" isUnauthorizedPath
                |> ignore
            finally
                if Directory.Exists link then
                    try
                        Directory.Delete(link, false)
                    with _ ->
                        ()

                if Directory.Exists parent then
                    try
                        Directory.Delete(parent, true)
                    with _ ->
                        ()

        testCase "a missing artifact root below a reparse point is rejected before creation"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let parent = Path.Combine(Path.GetTempPath(), "mcp-dotnet-reparse", Guid.NewGuid().ToString("N"))
            let real = Path.Combine(parent, "real")
            Directory.CreateDirectory real |> ignore
            let link = Path.Combine(parent, "link")
            createDirectoryLink link real |> ignore

            try
                PathAuthorization.validateArtifactRoot workspace.Root (Path.Combine(link, "new", "artifacts"))
                |> expectErrorMatching "reparse pre-creation" isUnauthorizedPath
                |> ignore

                Expect.isFalse
                    (Directory.Exists(Path.Combine(real, "new")))
                    "no directory was materialized through the junction"
            finally
                if Directory.Exists link then
                    try
                        Directory.Delete(link, false)
                    with _ ->
                        ()

                if Directory.Exists parent then
                    try
                        Directory.Delete(parent, true)
                    with _ ->
                        ()

        testCase "artifact root with an invalid character returns typed invalid input"
        <| fun _ ->
            use workspace = new TempWorkspace()

            PathAuthorization.validateArtifactRoot workspace.Root "bad\u0000root"
            |> expectErrorMatching "nul artifact root" isInvalidInput
            |> ignore
    ]

let private namespaceTests =
    testList "workspace namespace" [
        testCase "the same workspace maps to a stable single-segment identifier"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let first = PathAuthorization.workspaceNamespace workspace.Root
            let second = PathAuthorization.workspaceNamespace workspace.Root

            Expect.equal first second "namespace is stable for the same workspace"
            Expect.equal first.Length 64 "namespace is a full SHA-256 hex digest"
            Expect.isTrue (first |> Seq.forall Uri.IsHexDigit) "namespace is hexadecimal"
            Expect.equal first (first.ToLowerInvariant()) "namespace is lowercase"
            Expect.isFalse (first.Contains(workspace.Root, StringComparison.OrdinalIgnoreCase)) "raw workspace path is not embedded"
            Expect.isFalse (first.Contains '/') "namespace has no directory separator"
            Expect.isFalse (first.Contains '\\') "namespace has no alternate separator"

        testCase "distinct workspaces map to distinct namespaces"
        <| fun _ ->
            use firstWorkspace = new TempWorkspace()
            use secondWorkspace = new TempWorkspace()

            Expect.notEqual
                (PathAuthorization.workspaceNamespace firstWorkspace.Root)
                (PathAuthorization.workspaceNamespace secondWorkspace.Root)
                "distinct workspace roots"

        testCase "case variants of one Windows workspace map to one namespace"
        <| fun _ ->
            use workspace = new TempWorkspace()

            if OperatingSystem.IsWindows() then
                Expect.equal
                    (PathAuthorization.workspaceNamespace (workspace.Root.ToUpperInvariant()))
                    (PathAuthorization.workspaceNamespace workspace.Root)
                    "Windows path comparison is case-insensitive"

        testCase "the registry rejects a namespace that is not a single path segment"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let artifactRoot = Path.Combine(workspace.Root, "artifacts")

            let segmented = [ "a/b" ] @ (if OperatingSystem.IsWindows() then [ "a\\b" ] else [])

            for invalid in [ ""; "   "; "."; ".." ] @ segmented do
                Expect.throws
                    (fun () -> new ArtifactRegistry(artifactRoot, invalid, TimeSpan.FromHours 1.0) |> ignore)
                    $"namespace '{invalid}' is rejected"
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
            use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), workspace.Namespace, TimeSpan.FromHours 1.0)
            let handle = startRun registry
            let runIdText = RunId.value handle.RunId
            let directoryName = Path.GetFileName handle.Paths.Directory

            Expect.equal runIdText.Length 43 "256-bit base64url length"
            Expect.isTrue (Regex.IsMatch(runIdText, "^[A-Za-z0-9_-]+$")) "base64url alphabet"
            Expect.notEqual directoryName runIdText "directory name is independent"
            Expect.isFalse (handle.Paths.Directory.Contains(runIdText, StringComparison.Ordinal)) "path does not embed run id"

        testCase "each run is namespaced under the workspace identity"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let artifactRoot = workspace.ExternalArtifactRoot
            use registry = new ArtifactRegistry(artifactRoot, workspace.Namespace, TimeSpan.FromHours 1.0)
            let handle = startRun registry
            let namespaceDirectory = Path.Combine(artifactRoot, workspace.Namespace)

            Expect.isTrue (Directory.Exists namespaceDirectory) "workspace namespace exists"
            Expect.equal (Path.GetDirectoryName handle.Paths.Directory) namespaceDirectory "run directory is one level under the namespace"
            Expect.isFalse (handle.Paths.Directory.Contains(workspace.Root, StringComparison.OrdinalIgnoreCase)) "raw workspace path is not exposed"

        testCase "each run receives a unique isolated directory"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let artifactRoot = Path.Combine(workspace.Root, "artifacts")
            use registry = new ArtifactRegistry(artifactRoot, workspace.Namespace, TimeSpan.FromHours 1.0)
            let first = startRun registry
            let second = startRun registry

            Expect.notEqual (RunId.value first.RunId) (RunId.value second.RunId) "unique run ids"
            Expect.notEqual first.Paths.Directory second.Paths.Directory "unique directories"
            Expect.isTrue (first.Paths.Directory.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase)) "first contained"
            Expect.isTrue (second.Paths.Directory.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase)) "second contained"

        testCase "unknown and incomplete run ids resolve to actionable errors"
        <| fun _ ->
            use workspace = new TempWorkspace()
            use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), workspace.Namespace, TimeSpan.FromHours 1.0)
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
            use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), workspace.Namespace, TimeSpan.FromHours 1.0)
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
            use registry = new ArtifactRegistry(Path.Combine(workspace.Root, "artifacts"), workspace.Namespace, TimeSpan.FromHours 1.0)
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

        testCase "cleanup removes only owned run state and preserves siblings and unrelated entries"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let artifactRoot = Path.Combine(workspace.Root, "artifacts")
            let sibling = Path.Combine(artifactRoot, "sibling-workspace")
            Directory.CreateDirectory sibling |> ignore
            let siblingFile = Path.Combine(sibling, "keep.txt")
            File.WriteAllText(siblingFile, "sibling state")
            let unrelated = Path.Combine(artifactRoot, "consumer-data.txt")
            File.WriteAllText(unrelated, "consumer data")

            use registry = new ArtifactRegistry(artifactRoot, workspace.Namespace, TimeSpan.FromHours 1.0)
            let handle = startRun registry
            let runDirectory = handle.Paths.Directory
            let namespaceDirectory = Path.Combine(artifactRoot, workspace.Namespace)
            File.WriteAllText(Path.Combine(runDirectory, "stdout.log"), "output")

            registry.EndSession()

            Expect.isFalse (Directory.Exists runDirectory) "owned run directory removed"
            Expect.isFalse (Directory.Exists namespaceDirectory) "emptied owned namespace removed"
            Expect.isTrue (Directory.Exists sibling) "sibling workspace namespace preserved"
            Expect.isTrue (File.Exists siblingFile) "sibling content preserved"
            Expect.isTrue (File.Exists unrelated) "unrelated root entry preserved"
            Expect.isTrue (Directory.Exists artifactRoot) "shared consumer root preserved"

        testCase "a non-empty owned namespace is not removed"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let artifactRoot = Path.Combine(workspace.Root, "artifacts")
            use registry = new ArtifactRegistry(artifactRoot, workspace.Namespace, TimeSpan.FromHours 1.0)
            let handle = startRun registry
            let namespaceDirectory = Path.Combine(artifactRoot, workspace.Namespace)
            let retained = Path.Combine(namespaceDirectory, "retained.txt")
            File.WriteAllText(retained, "retained")

            registry.EndSession()

            Expect.isFalse (Directory.Exists handle.Paths.Directory) "owned run directory removed"
            Expect.isTrue (File.Exists retained) "unowned namespace content preserved"
            Expect.isTrue (Directory.Exists namespaceDirectory) "non-empty namespace preserved"

        testCase "the namespace is retained while another owned run remains"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let artifactRoot = Path.Combine(workspace.Root, "artifacts")
            use registry = new ArtifactRegistry(artifactRoot, workspace.Namespace, TimeSpan.FromHours 1.0)
            let namespaceDirectory = Path.Combine(artifactRoot, workspace.Namespace)
            let first = startRun registry
            let second = startRun registry

            registry.Abort first

            Expect.isFalse (Directory.Exists first.Paths.Directory) "aborted run directory removed"
            Expect.isTrue (Directory.Exists second.Paths.Directory) "sibling owned run retained"
            Expect.isTrue (Directory.Exists namespaceDirectory) "namespace retained while an owned run remains"

            registry.Abort second

            Expect.isFalse (Directory.Exists namespaceDirectory) "emptied namespace removed after the last run"

        testCaseTask "concurrent runs allocate unique isolated artifacts" (fun () ->
            task {
                use workspace = new TempWorkspace()
                let artifactRoot = Path.Combine(workspace.Root, "artifacts")
                use registry = new ArtifactRegistry(artifactRoot, workspace.Namespace, TimeSpan.FromHours 1.0)

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
          namespaceTests
          noLaunchTests
          registryTests ]
