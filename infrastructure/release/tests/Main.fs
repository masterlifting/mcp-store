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
              Revision.tests
              Runner.tests
              Telegram.tests
              Orchestration.tests ])

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv suites
