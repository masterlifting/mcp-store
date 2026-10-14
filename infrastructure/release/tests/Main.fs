// role: compiled test entry point
// scope: dispatch --prepare-authority-probe through the modern ReleaseOrchestration.prepare
//   and --authority-probe through the test host's internal probeCase; test suites run under
//   testSequenced so the fixture-using child probes do not race the shared temporary roots.

module Release.Tests.Main

open System.IO
open Expecto
open Release.Tests.Authority

let private suites =
    testSequenced
        (testList
            "compiled release regressions"
            [ Enrollment.tests
              Coherence.tests
              Bootstrap.tests
              Compare.tests
              Evaluate.tests
              Historical.tests
              Invocation.tests
              Metadata.tests
              Publish.tests
              Integrity.tests
              Policy.tests
              Handoff.tests
              Revision.tests
              Runner.tests
              Telegram.tests
              Orchestration.tests
              Authority.tests ])

// WHY: --prepare-authority-probe routes the probe through the real ReleaseRunner.main entry
//   point so the publication-authority guard in runPrepareMode runs in this owned child.
//   The parent stripped every credential name by NAME and set dummy values before launch;
//   the child therefore reads only those dummies and never touches the host's env. Returning
//   the native int directly avoids the Expecto ANSI wrapper that previously corrupted the
//   JSON stdout the parent parses; ReleaseRunner.main already bridges its own Async.
let private runPrepareAuthorityProbe (argv: string array) =
    if argv.Length = 3 && argv.[0] = "--prepare-authority-probe" then
        Some (ReleaseRunner.main [| "prepare"; argv.[1]; argv.[2] |])
    else
        None

let private runAuthorityProbe (argv: string array) =
    match argv with
    | [| "--authority-probe"; mode; componentId |] ->
        Some (probeCase mode componentId)
    | _ -> None

[<EntryPoint>]
let main argv =
    match runPrepareAuthorityProbe argv with
    | Some exitCode -> exitCode
    | None ->
        match runAuthorityProbe argv with
        | Some probe ->
            runTestsWithCLIArgs [] [||] (testList "authority probe dispatch" [ probe ])
        | None ->
            runTestsWithCLIArgs [] argv suites
