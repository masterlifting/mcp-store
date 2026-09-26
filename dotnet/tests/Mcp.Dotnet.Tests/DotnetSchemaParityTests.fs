module Mcp.Dotnet.Tests.DotnetSchemaParityTests

open System
open System.IO
open System.Text.Json.Nodes
open Expecto
open Mcp.Dotnet
open Mcp.Dotnet.Tests.Support

// The published tools/list schema must encode the same bounds the runtime parser
// enforces. This suite extracts the literal from Host.fs so the shipped schema is
// the only schema under test, then compares budget-derived bounds to
// Budgets.Defaults so a future budget change makes the schema test fail.

// Mirrors the literal bound in dotnet/Invocation.fs validateConfiguration; the
// published schema and the runtime check must move together.
let private configurationMaxLength = 64
let private configurationPattern = "^[A-Za-z0-9_.-]+$"

// Mirrors the literal bound in dotnet/Invocation.fs validateFilter.
let private filterMaxLength = 512

let private expectedTimeoutMinimum = int Budgets.Defaults.MinimumTimeout.TotalMilliseconds
let private expectedTimeoutMaximum = int Budgets.Defaults.MaximumTimeout.TotalMilliseconds
let private expectedTimeoutDefault = int Budgets.Defaults.DefaultTimeout.TotalMilliseconds
let private expectedLimitDefault = Budgets.Defaults.DetailsPageSize
let private expectedLimitMaximum = Budgets.Defaults.DetailsMaxPageSize

let private extractToolsJson () =
    let hostPath = Path.Combine(repositoryRoot (), "dotnet", "Host.fs")
    let source = File.ReadAllText hostPath
    let marker = "let private toolsJson ="
    let markerIndex = source.IndexOf(marker, StringComparison.Ordinal)

    if markerIndex < 0 then
        fail "DotnetSchemaParityTests" "toolsJson marker was not found in Host.fs"

    let openIndex = source.IndexOf("\"\"\"", markerIndex, StringComparison.Ordinal)

    if openIndex < 0 then
        fail "DotnetSchemaParityTests" "toolsJson literal opening delimiter was not found"

    let contentStart = openIndex + 3
    let closeIndex = source.IndexOf("\"\"\"", contentStart, StringComparison.Ordinal)

    if closeIndex < 0 then
        fail "DotnetSchemaParityTests" "toolsJson literal closing delimiter was not found"

    source.Substring(contentStart, closeIndex - contentStart)

let private tools = lazy (JsonNode.Parse(extractToolsJson ()).AsArray())

let private toolByName name =
    tools.Value
    |> Seq.map (fun tool -> tool.AsObject())
    |> Seq.find (fun tool -> tool.["name"].GetValue<string>() = name)

let private properties name = (toolByName name).["inputSchema"].["properties"].AsObject()

let private memberNode (node: JsonNode) (key: string) (label: string) : JsonNode =
    let value = node.[key]

    if isNull value then
        fail "DotnetSchemaParityTests" $"{label}.{key} must be published"

    value

let private expectInt (node: JsonNode) (key: string) (expected: int) (label: string) =
    Expect.equal ((memberNode node key label).GetValue<int>()) expected $"{label}.{key}"

let private expectBool (node: JsonNode) (key: string) (expected: bool) (label: string) =
    Expect.equal ((memberNode node key label).GetValue<bool>()) expected $"{label}.{key}"

let private expectString (node: JsonNode) (key: string) (expected: string) (label: string) =
    Expect.equal ((memberNode node key label).GetValue<string>()) expected $"{label}.{key}"

let private schemaTests =
    testList "Dotnet tools/list schema parity" [
        testCase "the literal parses as JSON with exactly the three capability-scoped tools"
        <| fun _ ->
            let names =
                tools.Value
                |> Seq.map (fun tool -> tool.["name"].GetValue<string>())
                |> Seq.toList

            Expect.equal names [ "build"; "test"; "details" ] "published tool names"

        testCase "configuration publishes the runtime pattern, length, and default"
        <| fun _ ->
            for toolName in [ "build"; "test" ] do
                let configuration = memberNode (properties toolName) "configuration" $"{toolName}.properties"
                expectString configuration "type" "string" $"{toolName}.configuration"
                expectInt configuration "minLength" 1 $"{toolName}.configuration"
                expectInt configuration "maxLength" configurationMaxLength $"{toolName}.configuration"
                expectString configuration "pattern" configurationPattern $"{toolName}.configuration"
                expectString configuration "default" "Release" $"{toolName}.configuration"

        testCase "filter publishes a non-empty bounded string"
        <| fun _ ->
            let filter = memberNode (properties "test") "filter" "test.properties"
            expectString filter "type" "string" "test.filter"
            expectInt filter "minLength" 1 "test.filter"
            expectInt filter "maxLength" filterMaxLength "test.filter"

        testCase "boolean noBuild and noRestore default to false"
        <| fun _ ->
            let noRestore = memberNode (properties "build") "noRestore" "build.properties"
            let noBuild = memberNode (properties "test") "noBuild" "test.properties"
            expectString noRestore "type" "boolean" "build.noRestore"
            expectBool noRestore "default" false "build.noRestore"
            expectString noBuild "type" "boolean" "test.noBuild"
            expectBool noBuild "default" false "test.noBuild"

        testCase "timeoutMs publishes Budgets.Defaults bounds for build and test"
        <| fun _ ->
            for toolName in [ "build"; "test" ] do
                let timeout = memberNode (properties toolName) "timeoutMs" $"{toolName}.properties"
                expectString timeout "type" "integer" $"{toolName}.timeoutMs"
                expectInt timeout "minimum" expectedTimeoutMinimum $"{toolName}.timeoutMs"
                expectInt timeout "maximum" expectedTimeoutMaximum $"{toolName}.timeoutMs"
                expectInt timeout "default" expectedTimeoutDefault $"{toolName}.timeoutMs"

        testCase "details runId, kind, offset, and limit publish runtime bounds"
        <| fun _ ->
            let details = properties "details"
            let runId = memberNode details "runId" "details.properties"
            expectString runId "type" "string" "details.runId"
            expectInt runId "minLength" 1 "details.runId"

            let kind = memberNode details "kind" "details.properties"
            expectString kind "type" "string" "details.kind"

            let kindEnum =
                kind.["enum"].AsArray()
                |> Seq.map (fun value -> value.GetValue<string>())
                |> Seq.toList

            Expect.equal kindEnum [ "errors"; "warnings"; "failed-tests"; "output" ] "details.kind enum"

            let offset = memberNode details "offset" "details.properties"
            expectString offset "type" "integer" "details.offset"
            expectInt offset "minimum" 0 "details.offset"
            expectInt offset "default" 0 "details.offset"

            let limit = memberNode details "limit" "details.properties"
            expectString limit "type" "integer" "details.limit"
            expectInt limit "minimum" 1 "details.limit"
            expectInt limit "maximum" expectedLimitMaximum "details.limit"
            expectInt limit "default" expectedLimitDefault "details.limit"
    ]

let tests = schemaTests
