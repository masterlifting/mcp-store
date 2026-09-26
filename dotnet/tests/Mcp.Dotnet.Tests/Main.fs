module Mcp.Dotnet.Tests.Main

open Expecto

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs
        []
        argv
        (testList
            "Mcp.Dotnet"
            [ DomainTests.tests
              InvocationTests.tests
              SecurityTests.tests
              ProcessIntegrationTests.tests
              McpHostTests.tests
              QuotaTests.tests
              DotnetSchemaParityTests.tests ])
