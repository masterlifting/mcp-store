namespace Mcp.Dotnet

open System
open System.IO

type ArtifactPaths =
    { Directory: string
      Metadata: string
      Stdout: string
      Stderr: string
      Binlog: string
      Trx: string
      ParsedEvidence: string }

type private InvocationData =
    { Operation: VerificationOperation
      Executable: string
      WorkspaceRoot: string
      Arguments: string list
      Timeout: TimeSpan
      AuthorizedPath: AuthorizedPath }

type AuthorizedInvocation = private AuthorizedInvocation of invocation: InvocationData

[<RequireQualifiedAccess>]
module AuthorizedInvocation =
    let private pathComparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    let private normalize (path: string) =
        let fullPath = Path.GetFullPath path
        let root = Path.GetPathRoot fullPath

        if String.IsNullOrEmpty root || fullPath.Length <= root.Length then
            fullPath
        else
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    let private pathIsWithin (root: string) (candidate: string) =
        let prefix =
            if root.EndsWith(string Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || root.EndsWith(string Path.AltDirectorySeparatorChar, StringComparison.Ordinal) then
                root
            else
                root + string Path.DirectorySeparatorChar

        candidate.Equals(root, pathComparison) || candidate.StartsWith(prefix, pathComparison)

    let private ensureNoReparseAncestors (path: string) =
        let mutable directory = DirectoryInfo(Path.GetDirectoryName path)
        let mutable valid = true

        while valid && not (isNull directory) do
            try
                valid <- not (File.GetAttributes(directory.FullName).HasFlag FileAttributes.ReparsePoint)
            with _ ->
                valid <- false

            directory <- directory.Parent

        valid

    let private validateExecutable workspaceRoot candidate : Result<string, VerificationError> =
        try
            if String.IsNullOrWhiteSpace candidate then
                Error(ProcessStartFailure "the verifier requires an injected .NET host")
            elif candidate.StartsWith("\\\\", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal) then
                Error(ProcessStartFailure "the injected .NET host must be a local absolute path")
            elif not (Path.IsPathFullyQualified candidate) then
                Error(ProcessStartFailure "the injected .NET host must be an absolute path")
            else
                let fullPath = normalize candidate
                let root = normalize workspaceRoot

                if not (String.Equals(candidate, fullPath, pathComparison)) then
                    Error(ProcessStartFailure "the injected .NET host must be a canonical absolute path")
                elif pathIsWithin root fullPath then
                    Error(ProcessStartFailure "the injected .NET host must be outside the workspace")
                elif not (File.Exists fullPath) || Directory.Exists fullPath then
                    Error(ProcessStartFailure "the injected .NET host does not exist as a regular file")
                elif (File.GetAttributes fullPath).HasFlag FileAttributes.ReparsePoint then
                    Error(ProcessStartFailure "the injected .NET host must not be a reparse point")
                elif not (ensureNoReparseAncestors fullPath) then
                    Error(ProcessStartFailure "the injected .NET host must not traverse a reparse point")
                elif OperatingSystem.IsWindows()
                     && not (String.Equals(Path.GetExtension fullPath, ".exe", StringComparison.OrdinalIgnoreCase)) then
                    Error(ProcessStartFailure "the injected .NET host must be an executable file")
                else
                    Ok fullPath
        with _ ->
            Error(ProcessStartFailure "the injected .NET host is invalid")

    let internal validateInjectedHost workspaceRoot candidate = validateExecutable workspaceRoot candidate

    let operation (AuthorizedInvocation invocation) = invocation.Operation
    let executable (AuthorizedInvocation invocation) = invocation.Executable
    let workingDirectory (AuthorizedInvocation invocation) = invocation.WorkspaceRoot
    let arguments (AuthorizedInvocation invocation) = invocation.Arguments
    let timeout (AuthorizedInvocation invocation) = invocation.Timeout

    let revalidate (AuthorizedInvocation invocation) =
        PathAuthorization.revalidate invocation.AuthorizedPath

module Invocation =
    let private targetArgument root target =
        target
        |> Option.map (fun path -> Path.GetRelativePath(root, path))
        |> Option.toList

    let private validateConfiguration configuration =
        match configuration with
        | None -> Ok "Release"
        | Some value when String.IsNullOrWhiteSpace value -> Error(InvalidInput "configuration must be non-empty")
        | Some value when value.Length > 64 -> Error(InvalidInput "configuration is too long")
        | Some value when
            value
            |> Seq.exists (fun character ->
                not (
                    Char.IsLetterOrDigit character
                    || character = '-'
                    || character = '_'
                    || character = '.'
                ))
            ->
            Error(InvalidInput "configuration contains unsupported characters")
        | Some value -> Ok value

    let private validateFilter filter =
        match filter with
        | None -> Ok None
        | Some value when String.IsNullOrWhiteSpace value ->
            Error(InvalidInput "filter must be non-empty when supplied")
        | Some value when value.Length > 512 || value.IndexOf('\u0000') >= 0 ->
            Error(InvalidInput "filter is invalid or too long")
        | Some value -> Ok(Some value)

    let build workspaceRoot (dotnetHost: string) budgets (options: BuildOptions) (paths: ArtifactPaths) =
        result {
            let! authorized = PathAuthorization.authorize workspaceRoot options.Target
            let! executable = AuthorizedInvocation.validateInjectedHost authorized.WorkspaceRoot dotnetHost
            let! configuration = validateConfiguration options.Configuration
            let! timeout = Budgets.validateTimeout budgets options.Timeout
            let noRestore = options.NoRestore |> Option.defaultValue false
            let target = targetArgument authorized.WorkspaceRoot authorized.Target

            let arguments =
                [ "build" ]
                @ target
                @ [ "--configuration"; configuration; "--nologo"; "--verbosity"; "minimal" ]
                @ (if noRestore then [ "--no-restore" ] else [])
                @ [ $"/bl:{paths.Binlog}" ]

            return
                AuthorizedInvocation
                    { Operation = VerificationOperation.Build
                      Executable = executable
                      WorkspaceRoot = authorized.WorkspaceRoot
                      Arguments = arguments
                      Timeout = timeout
                      AuthorizedPath = authorized }
        }

    let test workspaceRoot (dotnetHost: string) budgets (options: TestOptions) (paths: ArtifactPaths) =
        result {
            let! authorized = PathAuthorization.authorize workspaceRoot options.Target
            let! executable = AuthorizedInvocation.validateInjectedHost authorized.WorkspaceRoot dotnetHost
            let! configuration = validateConfiguration options.Configuration
            let! filter = validateFilter options.Filter
            let! timeout = Budgets.validateTimeout budgets options.Timeout
            let noBuild = options.NoBuild |> Option.defaultValue false
            let target = targetArgument authorized.WorkspaceRoot authorized.Target

            let arguments =
                [ "test" ]
                @ target
                @ [ "--configuration"; configuration; "--nologo"; "--verbosity"; "minimal" ]
                @ (if noBuild then [ "--no-build" ] else [])
                @ (filter
                   |> Option.map (fun value -> [ "--filter"; value ])
                   |> Option.defaultValue [])
                @ [ "--results-directory"
                    paths.Directory
                    "--logger"
                    $"trx;LogFileName={Path.GetFileName paths.Trx}" ]

            return
                AuthorizedInvocation
                    { Operation = VerificationOperation.Test
                      Executable = executable
                      WorkspaceRoot = authorized.WorkspaceRoot
                      Arguments = arguments
                      Timeout = timeout
                      AuthorizedPath = authorized }
        }
