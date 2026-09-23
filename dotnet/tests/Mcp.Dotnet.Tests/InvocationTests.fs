module Mcp.Dotnet.Tests.InvocationTests

open System
open System.IO
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

let private pathsFor (workspace: TempWorkspace) =
    let directory = Path.Combine(workspace.Root, "artifacts", "run")

    { Directory = directory
      Metadata = Path.Combine(directory, "metadata.json")
      Stdout = Path.Combine(directory, "stdout.log")
      Stderr = Path.Combine(directory, "stderr.log")
      Binlog = Path.Combine(directory, "build.binlog")
      Trx = Path.Combine(directory, "test-results.trx")
      ParsedEvidence = Path.Combine(directory, "parsed-evidence.json") }

let private defaultBuildOptions =
    { Target = None
      Configuration = None
      NoRestore = None
      Timeout = None }

let private defaultTestOptions =
    { Target = None
      Configuration = None
      Filter = None
      NoBuild = None
      Timeout = None }

let private buildInvocation workspace options =
    let paths = pathsFor workspace

    Invocation.build workspace.Root (dotnetHost ()) Budgets.Defaults options paths
    |> expectOk "build invocation"

let private testInvocation workspace options =
    let paths = pathsFor workspace

    Invocation.test workspace.Root (dotnetHost ()) Budgets.Defaults options paths
    |> expectOk "test invocation"

let private argumentTests =
    testList "controlled argument arrays" [
        testCase "build uses a fixed executable and discrete arguments"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore
            let paths = pathsFor workspace
            let invocation = buildInvocation workspace defaultBuildOptions

            let executable = AuthorizedInvocation.executable invocation
            Expect.isTrue (Path.IsPathRooted executable) "absolute executable"
            Expect.isTrue (File.Exists executable) "executable exists"
            Expect.equal (Path.GetFileNameWithoutExtension executable) "dotnet" "fixed dotnet host"

            Expect.equal
                (AuthorizedInvocation.arguments invocation)
                [ "build"
                  workspace.Root
                  "--configuration"
                  "Release"
                  "--nologo"
                  "--verbosity"
                  "minimal"
                  $"/bl:{paths.Binlog}" ]
                "default build arguments"

            Expect.equal (AuthorizedInvocation.workingDirectory invocation) workspace.Root "workspace working directory"

        testCase "build target is relative and noRestore is explicit"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let projectPath = workspace.CreateClassLibrary("lib", validClassSource)
            let paths = pathsFor workspace

            let invocation =
                buildInvocation
                    workspace
                    { defaultBuildOptions with
                        Target = Some "lib.csproj"
                        NoRestore = Some true }

            let arguments = AuthorizedInvocation.arguments invocation
            Expect.equal (arguments |> List.item 1) (Path.GetFullPath projectPath) "canonical absolute target"
            Expect.isTrue (List.contains "--no-restore" arguments) "no-restore flag"
            Expect.isTrue (arguments |> List.forall (fun item -> not (item.Contains "dotnet "))) "no shell fragment arguments"

        testCase "test arguments carry filter and TRX logger without shell text"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let projectPath = workspace.CreateClassLibrary("lib", validClassSource)
            let paths = pathsFor workspace

            let invocation =
                testInvocation
                    workspace
                    { defaultTestOptions with
                        Target = Some "lib.csproj"
                        Filter = Some "Category=Fast"
                        NoBuild = Some true }

            let arguments = AuthorizedInvocation.arguments invocation

            Expect.equal
                arguments
                [ "test"
                  Path.GetFullPath projectPath
                  "--configuration"
                  "Release"
                  "--nologo"
                  "--verbosity"
                  "minimal"
                  "--no-build"
                  "--filter"
                  "Category=Fast"
                  "--results-directory"
                  paths.Directory
                  "--logger"
                  "trx;LogFileName=test-results.trx" ]
                "test arguments"

            Expect.isTrue (arguments |> List.forall (fun item -> not (item.Contains "dotnet "))) "no command string"

        testCase "configuration default is Release and invalid values are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore

            let invocation = buildInvocation workspace { defaultBuildOptions with Configuration = Some "Debug" }
            Expect.isTrue (AuthorizedInvocation.arguments invocation |> List.contains "Debug") "explicit configuration"

            Invocation.build
                workspace.Root
                (dotnetHost ())
                Budgets.Defaults
                { defaultBuildOptions with Configuration = Some "Release; rm -rf /" }
                (pathsFor workspace)
            |> expectErrorMatching "configuration injection" isInvalidInput
            |> ignore

            Invocation.build
                workspace.Root
                (dotnetHost ())
                Budgets.Defaults
                { defaultBuildOptions with Configuration = Some(String('a', 65)) }
                (pathsFor workspace)
            |> expectErrorMatching "configuration length" isInvalidInput
            |> ignore

        testCase "invalid filters are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore

            Invocation.test
                workspace.Root
                (dotnetHost ())
                Budgets.Defaults
                { defaultTestOptions with Filter = Some " " }
                (pathsFor workspace)
            |> expectErrorMatching "blank filter" isInvalidInput
            |> ignore

            Invocation.test
                workspace.Root
                (dotnetHost ())
                Budgets.Defaults
                { defaultTestOptions with Filter = Some(String('a', 513)) }
                (pathsFor workspace)
            |> expectErrorMatching "long filter" isInvalidInput
            |> ignore
    ]

let private authorizationTests =
    testList "invocation authorization" [
        testCase "absolute targets are rejected before any argument is built"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore

            Invocation.build
                workspace.Root
                (dotnetHost ())
                Budgets.Defaults
                { defaultBuildOptions with Target = Some(Path.Combine(workspace.Root, "lib.csproj")) }
                (pathsFor workspace)
            |> expectErrorMatching "absolute target" isUnauthorizedPath
            |> ignore

        testCase "parent traversal and unsupported extensions are rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()

            Invocation.build
                workspace.Root
                (dotnetHost ())
                Budgets.Defaults
                { defaultBuildOptions with Target = Some "../outside.csproj" }
                (pathsFor workspace)
            |> expectErrorMatching "traversal" isUnauthorizedPath
            |> ignore

            workspace.Write("notes.txt", "not a project") |> ignore

            Invocation.build
                workspace.Root
                (dotnetHost ())
                Budgets.Defaults
                { defaultBuildOptions with Target = Some "notes.txt" }
                (pathsFor workspace)
            |> expectErrorMatching "extension" isInvalidInput
            |> ignore

        testCase "revalidation rejects a target removed after authorization"
        <| fun _ ->
            use workspace = new TempWorkspace()
            let projectPath = workspace.CreateClassLibrary("lib", validClassSource)
            let invocation = buildInvocation workspace { defaultBuildOptions with Target = Some "lib.csproj" }

            File.Delete projectPath
            AuthorizedInvocation.revalidate invocation
            |> expectErrorMatching "removed target" isUnauthorizedPath
            |> ignore

        testCase "revalidation succeeds for an unchanged target"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore
            let invocation = buildInvocation workspace { defaultBuildOptions with Target = Some "lib.csproj" }
            Expect.isOk (AuthorizedInvocation.revalidate invocation) "unchanged target revalidates"
    ]

let private trustedHostTests =
    testList "injected trusted host" [
        testCase "the child invocation uses the injected absolute host verbatim"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore
            let host = dotnetHost ()
            let invocation = buildInvocation workspace defaultBuildOptions
            let executable = AuthorizedInvocation.executable invocation
            let workspacePrefix = workspace.Root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

            Expect.equal executable host "child invocation uses the injected host"
            Expect.isTrue (Path.IsPathRooted executable) "absolute host"
            Expect.isTrue (File.Exists executable) "host exists"
            Expect.isFalse (executable.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase)) "host outside workspace"
            Expect.isFalse ((File.GetAttributes executable).HasFlag FileAttributes.ReparsePoint) "host is not a reparse point"

        testCase "the test invocation uses the injected absolute host verbatim"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore
            let host = dotnetHost ()

            let invocation =
                testInvocation workspace { defaultTestOptions with Target = Some "lib.csproj" }

            Expect.equal (AuthorizedInvocation.executable invocation) host "test invocation uses the injected host"

        testCase "a workspace-local dotnet decoy is rejected as the injected host"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore
            let decoy = workspace.Write("dotnet.exe", "decoy") |> Path.GetFullPath

            Invocation.build workspace.Root decoy Budgets.Defaults defaultBuildOptions (pathsFor workspace)
            |> expectErrorMatching "workspace decoy" isProcessStartFailure
            |> ignore

        testCase "a bare PATH name is rejected as the injected host"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore

            Invocation.build workspace.Root "dotnet" Budgets.Defaults defaultBuildOptions (pathsFor workspace)
            |> expectErrorMatching "bare path name" isProcessStartFailure
            |> ignore

        testCase "a blank or missing injected host is rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore

            Invocation.build workspace.Root "" Budgets.Defaults defaultBuildOptions (pathsFor workspace)
            |> expectErrorMatching "blank host" isProcessStartFailure
            |> ignore

            Invocation.build
                workspace.Root
                (Path.Combine(Path.GetTempPath(), "mcp-verifier-host", "missing-dotnet.exe"))
                Budgets.Defaults
                defaultBuildOptions
                (pathsFor workspace)
            |> expectErrorMatching "missing host" isProcessStartFailure
            |> ignore

        testCase "a non-canonical injected host path is rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore
            let host = dotnetHost ()

            let nonCanonical =
                Path.Combine(Path.GetDirectoryName host, "sub", "..", Path.GetFileName host)

            Invocation.build workspace.Root nonCanonical Budgets.Defaults defaultBuildOptions (pathsFor workspace)
            |> expectErrorMatching "non-canonical host" isProcessStartFailure
            |> ignore

        testCase "a reparse-point host path is rejected"
        <| fun _ ->
            use workspace = new TempWorkspace()
            workspace.CreateClassLibrary("lib", validClassSource) |> ignore
            let host = dotnetHost ()
            let outsideRoot = Path.Combine(Path.GetTempPath(), "mcp-verifier-host", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory outsideRoot |> ignore
            let link = Path.Combine(outsideRoot, "host-link")

            try
                createDirectoryLink link (Path.GetDirectoryName host) |> ignore
                let linkedHost = Path.Combine(link, Path.GetFileName host)

                Invocation.build workspace.Root linkedHost Budgets.Defaults defaultBuildOptions (pathsFor workspace)
                |> expectErrorMatching "reparse host" isProcessStartFailure
                |> ignore
            finally
                if Directory.Exists link then
                    try
                        Directory.Delete(link, false)
                    with _ ->
                        ()

                try
                    Directory.Delete(outsideRoot, true)
                with _ ->
                    ()
    ]

let tests = testList "invocation" [ argumentTests; authorizationTests; trustedHostTests ]
