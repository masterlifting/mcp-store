# Workflow cleanup and archive

`task_clean` accepts exactly `projectRoot`, `taskId`, and `operation` (`cleanup`
or `archive`). Before either operation can inspect or mutate task content, it
must validate the requested project/task identity and authenticate the strict
schema-v3 runtime receipt with the existing account key. Require the DTO task ID
to equal the requested task-directory ID. Missing/invalid receipts, destination
mismatch, and schema v2 fail closed without mutation; schema v2 is not migrated,
rewritten, or treated as unsigned. Existing-key verification does not create,
repair, or rotate the key. Never inspect or expose key bytes.

Cleanup is limited to complete or aborted tasks and removes only
the contents of `temp/`, preserving the empty directory when present. Archive
requires a complete task, removes `temp/`, then moves the whole remaining task
directory to `.tasks/archives/<TASK-ID>` without replacing an existing entry.
Neither operation changes runtime bytes, lifecycle, or revision; archive is not
looked up by active task operations.

Authentication is against the original logical project-root and task-ID
identity, not the archive path. Verify that identity and receipt before archive;
the move retains the signed runtime bytes and logical identity. Do not rebind an
archive as a task under its new filesystem location. Cleanup/archive are not
recovery, migration, or installation procedures.

The consumer must stop or await its own task processes before requesting cleanup.
The existing Workflow mutex coordinates participating Workflow operations only.
Traversal uses managed `System.IO`, checks entries for links/reparse points, and
deletes ordinary files and empty directories explicitly. Filesystem and access
errors are returned as failures; temporary cleanup may be partial if interrupted,
but retained files are not selected for deletion. Archive uses a normal whole-
directory move on the same volume; it is not a copy/rollback transaction and
does not claim protection against unrelated hostile filesystem writers.

## Manual qualification

Use an authorized disposable project root, never real task data. Create eligible
fixtures through supported Workflow operations; do not edit runtime files to
manufacture lifecycle state. Isolate all account-data and temporary environment
paths as described in [manual verification](Verification.md#schema-v3-integrity-and-native-user-confirmation-disposable-only),
and guard the resolved application-data path before creating/persisting tasks.
Verify valid receipt authentication and requested identity precede cleanup and
archive. Verify schema-v2 and wrong-task/wrong-project copies reject without any
byte changes. Then verify cleanup and repeat cleanup preserve runtime, scripts,
and docs bytes; verify absent `temp/` succeeds. Verify archive moves the complete
durable tree without `temp/`, preserves file hashes and the logical
project/task identity, rejects non-complete lifecycle and any occupied
destination, and reports filesystem/access failures. Exercise unsafe nested
links and observed file-in-use errors only where they can be created safely.
Record unrun cases as unqualified; this document is guidance, not execution
evidence. Aborted-task fixture coverage remains blocked until an eligible
fixture is available through authorized ingress.
