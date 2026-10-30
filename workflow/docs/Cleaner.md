# Workflow cleanup and archive

`task_clean` accepts exactly `projectRoot`, `taskId`, and `operation` (`cleanup`
or `archive`). Cleanup is limited to complete or aborted tasks and removes only
the contents of `temp/`, preserving the empty directory when present. Archive
requires a complete task, removes `temp/`, then moves the whole remaining task
directory to `.tasks/archives/<TASK-ID>` without replacing an existing entry.
Neither operation changes runtime bytes, lifecycle, or revision; archive is not
looked up by active task operations.

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
manufacture lifecycle state. Verify cleanup and repeat cleanup preserve runtime,
scripts, and docs bytes; verify absent `temp/` succeeds. Verify archive moves the
complete durable tree without `temp/`, preserves file hashes, rejects non-complete
lifecycle and any occupied destination, and reports filesystem/access failures.
Exercise unsafe nested links and observed file-in-use errors only where they can
be created safely. Record unrun cases as unqualified; this document is guidance,
not execution evidence. Aborted-task fixture coverage remains blocked until an
eligible fixture is available through authorized ingress.
