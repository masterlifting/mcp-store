// role: test helper
// Every persisted task is signed with the producer-owned per-account integrity key.
// A suite that reaches the persistence API must run with its per-account directories redirected.
// The guard compares resolved paths only; it never reads, creates, prints, or rotates key material.

open System
open System.IO

let private isolationGuardFail message = raise (Exception message)

let private isolationGuardSamePath (left: string) (right: string) =
    String.Equals(
        (Path.GetFullPath left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        (Path.GetFullPath right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase)

let isolationGuardExpectedAppData =
    match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_EXPECTED_APP_DATA" with
    | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
    | _ ->
        isolationGuardFail
            "MCP_WORKFLOW_TEST_EXPECTED_APP_DATA is required; run this script in a fresh child process whose per-account application data directory is redirected to an isolated sandbox"

let isolationGuardResolvedAppData =
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

if String.IsNullOrWhiteSpace isolationGuardResolvedAppData then
    isolationGuardFail "per-account application data directory is unavailable"

if not (isolationGuardSamePath isolationGuardResolvedAppData isolationGuardExpectedAppData) then
    isolationGuardFail
        (sprintf
            "resolved application data directory '%s' is not the isolated test root '%s'"
            isolationGuardResolvedAppData
            isolationGuardExpectedAppData)

match Environment.GetEnvironmentVariable "MCP_WORKFLOW_TEST_REAL_APP_DATA" with
| value when not (String.IsNullOrWhiteSpace value) ->
    if isolationGuardSamePath isolationGuardResolvedAppData value then
        isolationGuardFail "the isolated guard resolved the real per-account application data directory"
| _ -> ()

printfn "ISOLATION GUARD OK"
