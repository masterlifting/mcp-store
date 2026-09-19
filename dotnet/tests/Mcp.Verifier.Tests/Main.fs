module Mcp.Verifier.Tests.Main

open Expecto

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs
        []
        argv
        (testList
            "Mcp.Verifier"
            [ DomainTests.tests
              InvocationTests.tests
              SecurityTests.tests
              ProcessIntegrationTests.tests
              McpHostTests.tests
              QuotaTests.tests ])
