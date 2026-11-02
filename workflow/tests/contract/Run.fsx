// role: test entrypoint
// The transport DTO and the persisted sidecar are different records, so only the signed sidecar is an authenticated task load.
// The suite persists tasks and reaches the producer-owned per-account integrity key, so it must run in an isolated child process with redirected per-account application data.
// The isolation guard loads before the domain sources so the key path is never captured outside that sandbox.

#load "../Isolation.fsx"
#load "../../domain/Computation.fs"
#load "../../domain/Integrity.fs"
#load "../../domain/Workflow.fs"

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Workflow

let assertEqual name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual

let assertTrue name condition =
    if not condition then failwithf "%s: expected true" name

let expectOk name (result: Async<Result<'a, RuntimeError>>) : Async<'a> =
    async {
        match! result with
        | Ok value -> return value
        | Error error -> return failwithf "%s: expected Ok, got Error %s" name (renderError error)
    }

let expectRejected (name: string) (fragment: string) (result: Async<Result<'a, RuntimeError>>) : Async<unit> =
    async {
        match! result with
        | Ok _ -> return failwithf "%s: expected rejection" name
        | Error error when (renderError error).Contains(fragment, StringComparison.Ordinal) -> ()
        | Error error -> return failwithf "%s: expected '%s', got '%s'" name fragment (renderError error)
    }

// `deserialize` is sync; lift it into the async helpers via a thin wrapper.
let deserializeAsync json : Async<Result<TaskModel, RuntimeError>> = async { return deserialize json }

let request id title =
    { Id = id
      Title = title
      Kind = Execution
      AcceptanceCriteria = [ "AC1", "Execution completes" ]
      WorkItems =
          [ { Id = "W1"
              Title = "Do the work"
              DependsOn = []
              Children = [] } ] }

let tempRoot = Path.Combine(Path.GetTempPath(), "opencode", $"workflow-contract-v3-{Guid.NewGuid():N}")
Directory.CreateDirectory tempRoot |> ignore

let sidecar root id = Path.Combine(root, ".tasks", id, SidecarFileName)
let runtimeLock root id = Path.Combine(root, ".tasks", id, "runtime.lock")

// The canonical receipt shape is a framed `hmac-sha256-v1:` tag over 64
// lowercase hexadecimal characters.
let receiptPattern = Regex("^hmac-sha256-v1:[0-9a-f]{64}$", RegexOptions.CultureInvariant)

async {
    try
        let! task = expectOk "create task" (createTask tempRoot (request "CON-1" "Contract"))
        let json = serialize task
        let raw = JsonNode.Parse(json).AsObject()

        // The transport DTO is not the authenticated record: it carries the
        // canonical schema3 version and deliberately omits the persistence
        // receipt that the persisted sidecar owns.
        assertEqual "transport schema" 3 (raw["schemaVersion"].GetValue<int>())
        assertTrue "transport DTO omits the persistence receipt" (not (raw.ContainsKey "integrityReceipt"))

        // The unsigned transport DTO is not a task load. The bounded DTO
        // error for a record without a receipt is the accepted outcome.
        do!
            expectRejected
                "unsigned transport DTO is rejected"
                "missing property 'integrityReceipt'"
                (deserializeAsync json)

        // The persisted sidecar is the canonical authenticated record. It
        // carries the receipt and parses back to the same domain model, and
        // the authenticated read path returns the same model.
        let persistedText = File.ReadAllText(sidecar tempRoot "CON-1")
        let persisted = JsonNode.Parse(persistedText).AsObject()
        assertEqual "persisted schema" 3 (persisted["schemaVersion"].GetValue<int>())
        assertTrue "persisted record carries a receipt" (persisted.ContainsKey "integrityReceipt")
        assertTrue
            "persisted receipt is a framed hmac-sha256-v1 tag"
            (receiptPattern.IsMatch(persisted["integrityReceipt"].GetValue<string>()))

        let! parsedPersisted = expectOk "deserialize persisted task" (deserializeAsync persistedText)
        assertEqual "persisted serializer/parser parity" task parsedPersisted

        let! authenticatedRead = expectOk "authenticated read of the persisted task" (getTask tempRoot "CON-1")
        assertEqual "authenticated read parity" task authenticatedRead

        assertTrue "serializer emits no schema alias" (not (raw.ContainsKey "version"))
        assertTrue "serializer emits no legacy task document" (not (File.Exists(Path.Combine(Path.GetDirectoryName(sidecar tempRoot "CON-1"), "TASK.md"))))

        // Only the canonical schema3 version is accepted. The legacy schema2
        // version is rejected with the typed unsupported-schema error.
        for version in [ 0; 1; 2; 4 ] do
            let changed = JsonNode.Parse(raw.ToJsonString()).AsObject()
            changed["schemaVersion"] <- JsonValue.Create version
            do!
                expectRejected
                    ($"schema {version} rejected")
                    ($"unsupported schemaVersion {version}")
                    (deserializeAsync (changed.ToJsonString()))

        let aliased = JsonNode.Parse(raw.ToJsonString()).AsObject()
        aliased["version"] <- JsonValue.Create 1
        do! expectRejected "persisted version alias rejected" "unknown property 'version'" (deserializeAsync (aliased.ToJsonString()))

        // A legacy schema2 record is rejected with the typed unsupported-schema
        // error and is never rewritten or adopted. The schema gate is
        // evaluated before any other property, so the remaining fields only
        // make the fixture a recognisable legacy record.
        let legacyDirectory = Path.Combine(tempRoot, ".tasks", "OLD-1")
        Directory.CreateDirectory legacyDirectory |> ignore
        let legacyPath = Path.Combine(legacyDirectory, SidecarFileName)

        File.WriteAllText(
            legacyPath,
            """{"schemaVersion":2,"id":"OLD-1","title":"Legacy schema2 record","created":"2026-01-01T00:00:00.0000000+00:00","kind":"execution","profile":"general","profileFingerprint":"","objective":"","scope":"","nonGoals":"","contractState":"draft","contractFingerprint":"","contractRevision":0,"stateRevision":0,"lifecycle":"open","evidence":[],"acceptanceCriteria":[{"id":"AC1","text":"Legacy acceptance criterion"}],"guards":[],"profileGuardKeys":{},"decisions":[],"questions":[],"workItems":[{"id":"W1","title":"Legacy work item","state":"pending","result":"","acceptanceRefs":["AC1"],"dependsOn":[],"evidenceRefs":[],"children":[]}],"completionHistory":[]}"""
        )

        let legacyBytes = File.ReadAllBytes legacyPath
        do! expectRejected "legacy schema2 record rejected" "unsupported schemaVersion 2" (getTask tempRoot "OLD-1")
        assertTrue "legacy schema2 record is not rewritten" (legacyBytes = File.ReadAllBytes legacyPath)
        assertTrue "legacy schema2 record keeps its recorded bytes" (legacyBytes.Length > 0)

        let missingSidecarDirectory = Path.Combine(tempRoot, ".tasks", "MIS-1")
        Directory.CreateDirectory missingSidecarDirectory |> ignore
        do! expectRejected "missing sidecar remains absent" "runtime sidecar does not exist" (getTask tempRoot "MIS-1")
        do! expectRejected "missing sidecar apply remains absent" "runtime sidecar does not exist" (applyTask tempRoot "MIS-1" 0 (StartWorkItem "W1"))
        assertTrue "missing sidecar creates no lock" (not (File.Exists(runtimeLock tempRoot "MIS-1")))

        let taskDocumentDirectory = Path.Combine(tempRoot, ".tasks", "DOC-1")
        Directory.CreateDirectory taskDocumentDirectory |> ignore
        File.WriteAllText(Path.Combine(taskDocumentDirectory, "TASK.md"), "legacy task")
        do! expectRejected "TASK.md layout rejected" "unsupported entry" (getTask tempRoot "DOC-1")

        let referencesDirectory = Path.Combine(tempRoot, ".tasks", "REF-1", "references")
        Directory.CreateDirectory referencesDirectory |> ignore
        File.WriteAllText(Path.Combine(referencesDirectory, "issue.md"), "legacy evidence")
        do! expectRejected "references layout rejected" "unsupported entry" (createTask tempRoot (request "REF-1" "References are not runtime state"))

        let lockDirectory = Path.Combine(tempRoot, ".tasks", "LCK-1")
        Directory.CreateDirectory lockDirectory |> ignore
        File.WriteAllText(Path.Combine(lockDirectory, "runtime.lock"), "legacy lock")
        do! expectRejected "persisted lock layout rejected" "unsupported entry" (createTask tempRoot (request "LCK-1" "Locks are ephemeral"))

        let! _ = expectOk "apply task" (applyTask tempRoot "CON-1" 0 (StartWorkItem "W1"))
        let appliedPersisted = JsonNode.Parse(File.ReadAllText(sidecar tempRoot "CON-1")).AsObject()
        assertEqual "updated persisted schema" 3 (appliedPersisted["schemaVersion"].GetValue<int>())
        assertTrue
            "updated persisted record carries a receipt"
            (receiptPattern.IsMatch(appliedPersisted["integrityReceipt"].GetValue<string>()))
        assertTrue "apply leaves no lock" (not (File.Exists(runtimeLock tempRoot "CON-1")))

        let! _ = expectOk "get leaves persisted state" (getTask tempRoot "CON-1")
        let! _ = expectOk "validate leaves persisted state" (validateTask tempRoot "CON-1")
        assertTrue "get leaves no lock" (not (File.Exists(runtimeLock tempRoot "CON-1")))
        assertTrue "validate leaves no lock" (not (File.Exists(runtimeLock tempRoot "CON-1")))

        printfn "OK schema-v3 contract: strict version, authenticated receipt, serializer/parser parity, legacy-schema rejection without rewrite, legacy-layout rejection, and ephemeral locking"
    finally
        if Directory.Exists tempRoot && tempRoot.Contains("workflow-contract-v3-", StringComparison.Ordinal) then
            Directory.Delete(tempRoot, true)
}
// Standalone entry bridge: FSI needs one synchronous script entry.
|> Async.RunSynchronously
