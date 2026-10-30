# Workflow manual verification

These are operator instructions, not producer execution evidence. Use only a
disposable project root and candidate build. Do not use an active consumer
runtime, production task root, release output, or live credentials.

## Isolated setup and recovery

Choose absolute paths for the repository, disposable root, candidate output,
and backups. Before writing, identify the actual runtime/configuration paths and
task-data paths involved. Back up the disposable root's `.tasks` and
`.workflow` data using your protected backup procedure, with backups outside
the root and candidate directory. Do not copy `.env`, credentials, sessions,
or unrelated secrets; handle any necessary secret separately with your
approved protected-secret procedure. Preserve the backup unchanged. If a
verification fails, stop the candidate and restore only the identified
disposable task/config data from backup after confirming it has no intervening
changes; do not blindly overwrite changed data. Remove only the candidate
directory you created when rolling back. Never remove or overwrite a
pre-existing installation.

Prepare the disposable project's profile catalog from an existing pinned
consumer/project catalog and its published digest. Do not assume a new root
contains `.workflow/profiles/catalog.json`, invent a profile, or calculate a
new digest as a substitute for the pinned value. Build from the repository
into an isolated candidate directory; do not invoke release scripts:

```powershell
$repo = 'D:\repos\private\mcp-store'
$root = 'D:\workflow-check\project'
$candidate = 'D:\workflow-check\candidate'
New-Item -ItemType Directory -Force $root,$candidate | Out-Null
Push-Location $repo
dotnet build workflow/Mcp.Workflow.fsproj -c Release -o $candidate
Pop-Location
```

Configure an MCP client to launch the candidate with the disposable root as its
working directory (the server trusts `Environment.CurrentDirectory` as the
workspace boundary). Use the actual pinned catalog path and lowercase digest:

```text
dotnet exec <absolute-candidate-path>/Mcp.Workflow.dll --profile-catalog <absolute-pinned-catalog-path> --profile-catalog-sha256 <pinned-lowercase-sha256>
```

Keep the server attached to that client. The examples below are **MCP tool
arguments**, not JSON-RPC envelopes. Replace `<root>` and IDs as appropriate.
Call `task_get` to read the current `stateRevision` before each `task_apply`;
use that returned value as `expectedStateRevision`, never a hardcoded stale
revision. `task_create` has no expected revision. A minimal disposable task can
be created with:

```json
{"projectRoot":"<root>","taskId":"DEMO-1","title":"Manual workflow check","kind":"execution","acceptanceCriteria":[{"id":"AC1","text":"Manual check complete"}],"workItems":[{"id":"W1","title":"Baseline work","children":[]}]}
```

Omit `profile` only if the producer's default is intended and available; to
test a selected profile, supply its exact ID from the pinned catalog.

## Compact projections

On the disposable task, call `task_status` and verify it returns only
`validation: notChecked`, `taskId`, `stateRevision`, `contractRevision`, `kind`,
`profile`, `lifecycle`, `contractState`, and fixed WorkItem progress counts.
Confirm no title/prose, history, evidence, decisions, or WorkItem tree is
included. This status read is lenient and must not mutate the task.

Call `task_ready` with no pagination arguments and verify strict registry-backed
validation plus a tree-order `pending` list. Each item contains `workItemId`,
`startable`, and typed blockers; confirm dependencies, guards, open questions,
lifecycle, and ancestor state/question cases as available in disposable
fixtures. Missing, unknown, or drifted profiles must reject the read even when
the task contract is Draft. A valid Draft task with a valid profile remains
startable; contract fingerprint drift is meaningful only after baselining.
There is no Draft profile state. Add enough pending items to verify offset/limit pagination
(default 0/25, maximum limit 100), `total`, `nextOffset`, revisions, and the
20-blocker per-item cap with total/truncation indicators. A later valid
`task_apply` start is authoritative; the projection does not reserve eligibility.
Compare task bytes and revisions before/after both reads to verify no persistence
or event/revision side effects. These steps are candidate smoke instructions, not
execution evidence.

## Decision target boundary

On a fresh Draft fixture with only `W1`, capture the task bytes and
`stateRevision`, then submit this public command:

```json
{"type":"add-decision","kind":"userDecision","targets":["workItemPatch:W2"],"rationale":"Check runtime-only target rejection"}
```

Expect rejection because `workItemPatch` is runtime-generated audit provenance,
not an `add-decision` input. Verify no Decision was added, the revision is
unchanged, and the persisted task bytes exactly match the captured bytes. Then
add `W2` using the Draft `addWorkItem` command from the next section and retry a
public decision using an ordinary public target such as `other:operator-review`;
that input remains accepted. Do not use `workItemPatch` as caller input even
after `W2` exists.

On a separate disposable Baselined fixture, use a normal authorized WorkItem
patch flow to generate its audit Decision. Read, restart/reload, and read again
to confirm the runtime-generated `workItemPatch` target and audit Decision are
preserved by history decoding and replay. These are operator steps, not executed
verification evidence.

## Contract patch checks

For every call below, obtain the latest state revision with `task_get` first.
In `task_apply` arguments, wrap the command shown as
`{"type":"...",...}` in `command`, alongside `projectRoot`, `taskId`, and
`expectedStateRevision`.

- Create a Draft task as above, then add a fresh root item and an immediate
  child under eligible pending/active ancestry. Verify generated hierarchy,
  inherited acceptance references, pending state, and empty result/evidence/
  owner. For example: `{"type":"apply-contract-patch","patch":{"type":"addWorkItem","id":"W2","title":"Additional work","dependsOn":[]}}`, then add child `W1.1` with `parentId":"W1"`.
- On a fresh Draft fixture, use the authorized Coordinator `start` command on
  its pending work item; the first material start establishes the contract
  baseline. Read with `task_get` to confirm Baselined state and current
  revisions before patching.
  Apply `addWorkItem` without `decisionRef`; it should succeed as a
  Coordinator-authorized patch. A dependency-only strengthening should also
  succeed without a reference. `updateWorkItem`, eligible `removeWorkItem`, and
  any dependency edit that drops an old prerequisite (including mixed add/drop)
  must fail closed for missing trusted User authority. `add-decision` cannot
  manufacture User authority. Where an authorized User ingress exists, verify
  the successful patch advances both revisions once and retains the exact patch
  hash and WorkItem target in readable rationale with before/after snapshots;
  reusing a decision should preserve it and append an audit Decision linked by
  `authorizedBy`.
- Reject duplicate/malformed/non-immediate IDs, terminal parent, non-pending
  rename, no-op title, duplicate dependencies, equal dependency sets supplied in
  different orders, unknown/self/ancestor/descendant
  dependency and a cycle. Reject removal of the last, active/done/owned/
  evidenced/result-bearing, non-leaf, incoming-dependency, or
  history-referenced item. Verify no rejected request persists a revision or
  payload change.
- For stale CAS, submit a valid patch using a previously observed revision
  after another successful write. Expect conflict and unchanged task bytes.

## Lifecycle, history, and authority

Complete a disposable task through normal commands, retaining its terminal
handoff and history. The first material start also establishes the contract
baseline. Record actual evidence and verify the acceptance criterion before
completion (`add-evidence` requires `id`, `kind`, `source`, and `summary`;
`verify` takes the criterion ID and evidence refs). The command shapes are
`{"type":"start","workItemId":"W1"}`,
`{"type":"complete-work","workItemId":"W1","result":"Done","evidenceRefs":[]}`,
and `{"type":"complete-task","state":"complete","evidenceSummary":"Manual fixture complete","next":"None"}`;
read current state between writes. Once task lifecycle is `complete`, apply the
singleton task reopen without `decisionRef` (it auto-mints the matching
Coordinator decision):

```json
{"projectRoot":"<root>","taskId":"DEMO-1","expectedStateRevision":<current-stateRevision>,"command":{"type":"reopen","reason":"Resume coordinator lifecycle","targets":["task"]}}
```

Expect lifecycle `open`, previous handoff archived, all records/history
retained, `stateRevision` +1, and unchanged `contractRevision`. Then add
remediation and re-verification work using two separate `addWorkItem` patches;
make the verification item depend on the remediation item. Confirm both are
pending, completed evidence/ownership/history remain unchanged, and the
`contractRevision` is +1 per successful patch while the profile fingerprint is
unchanged. A task-only reopen does not invalidate a completion requirement;
Baselined remains Baselined. Reject mixed, duplicate, empty, nonterminal,
aborted-task, and repeated reopen requests without persistence. Targeted reopen
is still subject to its invalidation requirement and authorization. Read and
validate the reopened record, including historical reopen decision target
decoding.

Attempt an obligation/scope amendment and a Baselined WorkItem weakening without
trusted User authority; expect a fail-closed structured authority error with
required authority/remediation details. Test a mixed dependency add/drop to
confirm it is weakening. On an aborted fixture, verify singleton `task` reopen
is rejected structurally before authorization; targeted remediation followed
by re-verification is the supported composition. Do not perform a live cutover
or treat these instructions as executed verification evidence.
