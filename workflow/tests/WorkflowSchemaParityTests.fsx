// Deterministic source-level parity guard for Workflow tools/list vs parser variants.
// This test intentionally validates the public contract without executing domain work.

open System
open System.IO

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let mcpSource = File.ReadAllText(Path.Combine(root, "WorkflowMcp.fs"))
let runtimeSource = File.ReadAllText(Path.Combine(root, "Workflow.fs"))

let between (startMarker: string) (endMarker: string) (text: string) =
    let startAt = text.IndexOf(startMarker, StringComparison.Ordinal)
    if startAt < 0 then failwithf "missing start marker: %s" startMarker
    let endAt = text.IndexOf(endMarker, startAt + startMarker.Length, StringComparison.Ordinal)
    if endAt < 0 then failwithf "missing end marker: %s" endMarker
    text.Substring(startAt, endAt - startAt)

let commandParser = between "let private parseCommand element =" "let private parseOperation" mcpSource
let patchParser = between "let private parseContractPatch element =" "let private parseCommand element =" mcpSource

let commandTypes =
    [ "start"; "complete-work"; "wait"; "block"; "resume"; "rebind-owner";
      "add-evidence"; "supersede-evidence"; "verify"; "add-guard";
      "mark-not-applicable"; "waive-guard"; "add-decision"; "add-question";
      "resolve-question"; "reopen"; "apply-contract-patch";
      "reconcile-contract-drift"; "reclassify-task"; "reconcile-profile-drift";
      "complete-task" ]

for commandType in commandTypes do
    assertTrue
        $"parser contains command {commandType}"
        (commandParser.Contains($"| \"{commandType}\" ->", StringComparison.Ordinal))

    assertTrue
        $"published schema contains command {commandType}"
        (mcpSource.Contains($"\"const\":\"{commandType}\"", StringComparison.Ordinal))

let patchTypes =
    [ "addAcceptanceCriterion"; "updateAcceptanceCriterion"; "removeAcceptanceCriterion";
      "addGuard"; "removeGuard"; "setObjective"; "setScope"; "setNonGoals" ]

for patchType in patchTypes do
    assertTrue
        $"parser contains patch {patchType}"
        (patchParser.Contains($"\"{patchType}\"", StringComparison.Ordinal))

    assertTrue
        $"published schema contains patch {patchType}"
        (mcpSource.Contains($"\"const\":\"{patchType}\"", StringComparison.Ordinal))

let parserBackedSchemaMarkers =
    [ "\"taskId\":{\"type\":\"string\",\"minLength\":1,\"pattern\":\"^[A-Za-z]+-[0-9]+$\"}"
      "\"workItemId\":{\"type\":\"string\",\"minLength\":1,\"pattern\":\"^W[0-9]+(\\\\.[0-9]+)*$\"}"
      "\"kind\":{\"type\":\"string\",\"minLength\":1,\"pattern\":\"^(build|test|review|observation|externalEffect|research|decisionEvidence|other:.*[^\\\\s].*)$\"}"
      "\"target\":{\"type\":\"string\",\"minLength\":1,\"pattern\":\"^(task|workItem:\\\\s*W[0-9]+(\\\\.[0-9]+)*\\\\s*)$\"}"
      "\"applicability\":{\"type\":\"string\",\"minLength\":1,\"pattern\":\"^(always|explicitDecision:(coordinator|user))$\"}"
      "\"waiver\":{\"type\":\"string\",\"minLength\":1,\"pattern\":\"^(notWaivable|waivableBy:(coordinator|user))$\"}"
      "\"impact\":{\"type\":\"string\",\"minLength\":1,\"pattern\":\"^(taskWide|workItems:\\\\s*W[0-9]+(\\\\.[0-9]+)*(\\\\s*,\\\\s*W[0-9]+(\\\\.[0-9]+)*)*\\\\s*)$\"}" ]

for marker in parserBackedSchemaMarkers do
    assertTrue $"published parser-backed schema marker {marker}" (mcpSource.Contains(marker, StringComparison.Ordinal))

for value in [ "build"; "test"; "review"; "observation"; "externalEffect"; "research"; "decisionEvidence" ] do
    assertTrue $"evidence parser contains {value}" (runtimeSource.Contains($"| \"{value}\" ->", StringComparison.Ordinal))

for value in [ "beforeStart"; "beforeComplete" ] do
    assertTrue $"guard checkpoint parser contains {value}" (runtimeSource.Contains($"| \"{value}\" ->", StringComparison.Ordinal))
    assertTrue $"guard checkpoint schema contains {value}" (mcpSource.Contains($"\"{value}\"", StringComparison.Ordinal))

for value in [ "userDecision"; "designDecision"; "assumption"; "policyApplication";
               "contractRevision"; "applicabilityDecision"; "waiverDecision" ] do
    assertTrue $"decision parser contains {value}" (runtimeSource.Contains($"| \"{value}\" ->", StringComparison.Ordinal))
    assertTrue $"decision schema contains {value}" (mcpSource.Contains($"\"{value}\"", StringComparison.Ordinal))

printfn "OK Workflow tools/list schema covers command/patch discriminators and parser-backed static constraints."
