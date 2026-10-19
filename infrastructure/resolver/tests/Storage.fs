// Root selection is a pure function of the declared platform, so the canonical root, the variable each platform consults, and the relative-environment fallback are decided without a host.
module Resolver.Tests.Storage

open System
open Expecto
open SharedFailure
open ResolverStorage

// A base is only admitted when the running host recognizes it as absolute, so the representative value follows the host while the composed root and the consulted variable stay platform-fixed.
let private hostBase (name: string) : string =
    if OperatingSystem.IsWindows() then
        $@"C:\{name}"
    else
        $"/{name}"

let private composedRoot (platform: Platform) (baseDirectory: string) : string =
    let separator = if platform = Platform.Windows then "\\" else "/"

    baseDirectory + separator + "mcp-store"

let private refused (result: Result<'a, ReleaseError>) : ReleaseError =
    match result with
    | Ok _ -> failtest "a base that is not absolute must be refused"
    | Error error -> error

let private accept (result: Result<string, ReleaseError>) : string =
    match result with
    | Ok value -> value
    | Error error -> failtestf "expected an accepted root, got %s" (ReleaseError.message error)

let tests =
    testList
        "root"
        [ testCase "each platform composes its root from the variable it declares"
          <| fun () ->
              let windows = hostBase "Users"
              let home = hostBase "home"
              let xdg = hostBase "xdg"

              Expect.equal
                  (accept (rootDirectory Platform.Windows (Some windows) None None))
                  (composedRoot Platform.Windows windows)
                  "Windows composes the root from LOCALAPPDATA"

              Expect.equal
                  (accept (rootDirectory Platform.Linux None (Some xdg) None))
                  (composedRoot Platform.Linux xdg)
                  "Linux prefers an absolute XDG_DATA_HOME"

              Expect.equal
                  (accept (rootDirectory Platform.Linux None None (Some home)))
                  (composedRoot Platform.Linux (home + "/.local/share"))
                  "Linux falls back to the specification default under HOME"

              Expect.equal
                  (accept (rootDirectory Platform.MacOS None (Some xdg) (Some home)))
                  (composedRoot Platform.MacOS (home + "/Library/Application Support"))
                  "macOS ignores XDG_DATA_HOME and composes under HOME"

          testCase "a relative base is refused, and a relative XDG value falls through to HOME"
          <| fun () ->
              let home = hostBase "home"

              match refused (rootDirectory Platform.Windows (Some "relative/appdata") None None) with
              | FileFailure (operation, name, _) ->
                  Expect.equal operation "storage-root" "the refusal is the storage-root decision"
                  Expect.equal name "LOCALAPPDATA" "the refused variable is named"
              | error -> failtestf "expected a storage-root failure, got %s" (ReleaseError.message error)

              Expect.equal
                  (accept (rootDirectory Platform.Linux None (Some "relative/xdg") (Some home)))
                  (composedRoot Platform.Linux (home + "/.local/share"))
                  "a relative XDG value is ignored rather than admitted as a base"

              match refused (rootDirectory Platform.Linux None (Some "relative/xdg") (Some "relative/home")) with
              | FileFailure (_, name, _) -> Expect.equal name "HOME" "the fallback variable is named"
              | error -> failtestf "expected a storage-root failure, got %s" (ReleaseError.message error)

              match refused (rootDirectory Platform.MacOS None (Some "relative/xdg") (Some "relative/home")) with
              | FileFailure (_, name, _) -> Expect.equal name "HOME" "macOS has no base but HOME"
              | error -> failtestf "expected a storage-root failure, got %s" (ReleaseError.message error)

          testCase "the canonical root follows the platform that runs the resolver"
          <| fun () ->
              let expected =
                  if OperatingSystem.IsWindows() then
                      Platform.Windows
                  elif OperatingSystem.IsMacOS() then
                      Platform.MacOS
                  else
                      Platform.Linux

              Expect.equal (hostPlatform ()) expected "the default root is selected for the running host" ]