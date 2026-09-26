namespace Mcp.Dotnet

open System
open System.IO

type AuthorizedPath =
    { WorkspaceRoot: string
      Target: string option }

module PathAuthorization =
    let private comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let private normalize (path: string) =
        let full = Path.GetFullPath path
        let root = Path.GetPathRoot full

        if not (isNull root) && full.Length > root.Length then
            full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        else
            full

    let private within (root: string) (candidate: string) =
        let prefix =
            if
                root.EndsWith(string Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || root.EndsWith(string Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            then
                root
            else
                root + string Path.DirectorySeparatorChar

        candidate.Equals(root, comparison) || candidate.StartsWith(prefix, comparison)

    let private isReparse (path: string) =
        try
            (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> false
        | _ -> true

    let private validateAncestors containmentRoot target =
        let mutable current =
            if File.Exists target then
                DirectoryInfo(target).Parent
            else
                DirectoryInfo target

        let mutable failure = None

        while failure.IsNone && not (isNull current) do
            let currentPath = normalize current.FullName

            match containmentRoot with
            | Some root when not (within root currentPath) ->
                failure <- Some(UnauthorizedPath "target ancestor is outside the trusted workspace")
            | _ when isReparse currentPath ->
                failure <- Some(UnauthorizedPath "target or an ancestor is a reparse point")
            | Some root when currentPath.Equals(root, comparison) -> current <- null
            | _ -> current <- current.Parent

        match failure with
        | Some error -> Error error
        | None -> Ok()

    let validateWorkspace workspaceRoot : Result<string, VerificationError> =
        try
            if String.IsNullOrWhiteSpace workspaceRoot then
                Error(InvalidInput "workspace root must be non-empty")
            else
                let root = normalize workspaceRoot

                if not (Directory.Exists root) then
                    Error(UnauthorizedPath "trusted workspace does not exist")
                elif isReparse root then
                    Error(UnauthorizedPath "trusted workspace must not be a reparse point")
                else
                    Ok root
        with error ->
            Error(InvalidInput $"workspace root is invalid: {error.Message}")

    let validateArtifactRoot workspaceRoot artifactRoot : Result<string, VerificationError> =
        try
            result {
                let! root = validateWorkspace workspaceRoot

                if String.IsNullOrWhiteSpace artifactRoot then
                    return! Error(InvalidInput "artifact root must be non-empty")
                elif artifactRoot.IndexOf('\u0000') >= 0 then
                    return! Error(InvalidInput "artifact root contains an invalid character")

                let candidate =
                    if Path.IsPathRooted artifactRoot then
                        artifactRoot
                    else
                        Path.Combine(root, artifactRoot)

                let normalized = normalize candidate

                let external = not (within root normalized)

                if external && not (Path.IsPathFullyQualified artifactRoot) then
                    return! Error(UnauthorizedPath "an external artifact root must be an absolute path")
                elif external
                     && OperatingSystem.IsWindows()
                     && (artifactRoot.StartsWith("\\\\", StringComparison.Ordinal)
                         || artifactRoot.StartsWith("//", StringComparison.Ordinal)) then
                    return! Error(UnauthorizedPath "an external artifact root must be a local path")
                elif external && not (Path.IsPathRooted artifactRoot) then
                    return! Error(UnauthorizedPath "artifact root is outside the trusted workspace")
                elif File.Exists normalized then
                    return! Error(UnauthorizedPath "artifact root must be a directory")

                // Check existing hops so a missing path below a junction is never
                // materialized outside the root when the root is created lazily.
                do! validateAncestors (if external then None else Some root) normalized
                return normalized
            }
        with error ->
            Error(InvalidInput $"artifact root is invalid: {error.Message}")

    let ensureArtifactRoot (artifactRoot: string) : Result<unit, VerificationError> =
        try
            result {
                let normalized = normalize artifactRoot

                if File.Exists normalized then
                    return! Error(UnauthorizedPath "artifact root must be a directory")
                else
                    // Re-check ancestors at creation so a reparse point introduced
                    // after startup validation cannot redirect the root.
                    do! validateAncestors None normalized
                    Directory.CreateDirectory normalized |> ignore
                    do! validateAncestors None normalized
                    return ()
            }
        with error ->
            Error(ArtifactFailure $"artifact root could not be created: {error.Message}")

    let private authorizeTarget root value : Result<AuthorizedPath, VerificationError> =
        try
            result {
                if String.IsNullOrWhiteSpace value then
                    return! Error(InvalidInput "target must be non-empty when supplied")
                elif Path.IsPathRooted value then
                    return! Error(UnauthorizedPath "target must be relative to the trusted workspace")
                elif value.IndexOf('\u0000') >= 0 then
                    return! Error(InvalidInput "target contains an invalid character")
                else
                    let fullTarget = normalize (Path.Combine(root, value))
                    let extension = Path.GetExtension fullTarget

                    let allowedExtension =
                        [ ".sln"; ".slnx"; ".csproj"; ".fsproj"; ".vbproj" ]
                        |> List.exists (fun item -> item.Equals(extension, StringComparison.OrdinalIgnoreCase))

                    if not allowedExtension then
                        return! Error(InvalidInput "target must be a .NET project or solution file")
                    elif not (within root fullTarget) then
                        return! Error(UnauthorizedPath "target is outside the trusted workspace")
                    elif not (File.Exists fullTarget) || Directory.Exists fullTarget then
                        return! Error(UnauthorizedPath "target file does not exist")
                    elif isReparse fullTarget then
                        return! Error(UnauthorizedPath "target must not be a reparse point")
                    else
                        do! validateAncestors (Some root) fullTarget

                        return
                            { WorkspaceRoot = root
                              Target = Some fullTarget }
            }
        with error ->
            Error(InvalidInput $"target is invalid: {error.Message}")

    let authorize workspaceRoot target : Result<AuthorizedPath, VerificationError> =
        result {
            let! root = validateWorkspace workspaceRoot

            match target with
            | None -> return { WorkspaceRoot = root; Target = None }
            | Some value -> return! authorizeTarget root value
        }

    let revalidate authorized =
        let target =
            authorized.Target
            |> Option.map (fun path -> Path.GetRelativePath(authorized.WorkspaceRoot, path))

        authorize authorized.WorkspaceRoot target
        |> Result.bind (fun current ->
            if current = authorized then
                Ok()
            else
                Error(UnauthorizedPath "authorized workspace or target changed before launch"))
