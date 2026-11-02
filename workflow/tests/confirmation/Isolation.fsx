// role: test entrypoint
// Isolation guard for the native MCP protected-operation suite.
// The guard resolves the per-account application-data directory in its own process and refuses to report success unless it is exactly the isolated directory it was given, so the producer child that follows with the same environment can never read, create, or rotate the real per-account integrity key.
// The supported entry point is confirmation/Run.fsx, which owns the redirection and spawns this guard.

open System
open System.IO

let fail message = raise (Exception message)

let isolatedRoot =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_ISOLATED_ROOT" with
    | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
    | _ -> fail "MCP_WORKFLOW_TEST_ISOLATED_ROOT is required; run this guard through confirmation/Run.fsx so the per-account application data directory is redirected"

let expectedAppData =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_EXPECTED_APP_DATA" with
    | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
    | _ -> fail "MCP_WORKFLOW_TEST_EXPECTED_APP_DATA is required"

let samePath (left: string) (right: string) =
    String.Equals(
        left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase)

let resolvedAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

if String.IsNullOrWhiteSpace resolvedAppData then
    fail "per-account application data directory is unavailable"

if not (samePath resolvedAppData expectedAppData) then
    failwithf
        "resolved application data directory '%s' is not the isolated test root '%s'"
        resolvedAppData
        expectedAppData

match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_REAL_APP_DATA" with
| value when not (String.IsNullOrWhiteSpace value) ->
    if samePath resolvedAppData value then
        fail "the isolated guard resolved the real per-account application data directory"
| _ -> ()

let keyDirectory = Path.Combine(resolvedAppData, "mcp-store", "workflow")

// The resolved key directory is reported for containment comparison only; no key material is read, created, printed, or rotated here.
printfn "GUARD OK"
printfn "KEYDIR %s" keyDirectory
