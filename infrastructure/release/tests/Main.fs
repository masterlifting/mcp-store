module Release.Tests.Main

open Expecto

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
              Orchestration.tests ])

let private runPrepareAuthorityProbe (argv: string array) =
    if argv.Length = 3 && argv[0] = "--prepare-authority-probe" then
        Some(ReleaseRunner.main [| "prepare"; argv.[1]; argv.[2] |])
    else
        None

[<EntryPoint>]
let main argv =
    match runPrepareAuthorityProbe argv with
    | Some exitCode -> exitCode
    | None -> runTestsWithCLIArgs [] argv suites
