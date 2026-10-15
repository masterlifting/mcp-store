# Emission

## Root cause

`Build.ps1 -Phase Emit` exited 1 with `Argument types do not match` while
evaluating the manifest `Set-Content` at the pre-fix line 262, so
`runtime-manifest.json`, the inner envelope and `receipt.json` were never
written. The failing construct is the array subexpression around a list built by
`New-Object`:

| Line | Construct | Effect |
| --- | --- | --- |
| 251 | `$manifestEntries = New-Object System.Collections.Generic.List[object]` | `New-Object` emits the list inside a `[psobject]` wrapper |
| 262 | `files = @($manifestEntries)` | array-subexpression binder throws `System.ArgumentException: Argument types do not match` |

The throw is raised by the binder, not by JSON: upstream PowerShell issue 27558
records the same minimal repro (`New-Object System.Collections.Generic.List[object]`
then `@($list)`) with the stack `PSToObjectArrayBinder.Bind` →
`PSEnumerableBinder.MaybeDebase` → `System.Linq.Expressions.Expression.Condition`,
and the matrix `New-Object List[object]` + `@()` FAIL / `[List[object]]::new()` +
`@()` OK / `ToArray()` OK. The focused serialization probe reproduced the same
failure on PowerShell 7.6.6 and 7.5.3. `System.Linq.Expressions.dll` is the only
binary in `$PSHOME` carrying that message literal. The message is a runtime binder
failure, so the parser cannot see it and the focused tester's captured
`mcp-store-ghi24-builder-smoke-*/run/emit.log` carries only the message, because
the harness recorded child stderr.

## Fix and verification

One production line changed, no other value touched: `files = $manifestEntries.ToArray()`
replaces `@($manifestEntries)`. `ToArray()` is the list's own typed conversion, so
the same `object[]` in the same insertion order reaches the unchanged
`ConvertTo-Json -Depth 8`; `Set-Content -LiteralPath/-Value/-Encoding utf8`, the
`schemaVersion`/`files` key set, and the `path`/`sha256`/`bytes` entries from line
258 are untouched, as are the roster/config hashes, builder SDK/runtime/platform
stamps and the strict minimal receipt. One WHY comment line documents the
rejection, so the statement now starts at line 263.

Engineer-owned static check only (no Emit execution, no build, no test):
`[System.Management.Automation.Language.Parser]::ParseFile` → 0 parse errors,
2458 tokens; AST assertions → manifest `Set-Content` parameters
`LiteralPath, Value, Encoding`, `files = $manifestEntries.ToArray()`,
`ConvertTo-Json -Depth 8`, `-Encoding utf8`, zero array subexpressions wrapping
`$manifestEntries` (12 array subexpressions remain in the file), receipt
`ConvertTo-Json -Depth 8` unchanged. Repo scan: this was the only
`New-Object List[object]` + `@($variable)` site, and `Bootstrap.ps1` line 1135
`@(Read-RuntimeManifest -Path …)` is a command pipeline, not a wrapped-variable
subexpression.

Emit stays unproven and no authentic receipt exists: rerunning `Build.ps1 -Phase
Emit` and parsing its actual `receipt.json` is the tester-owned smoke that must
close this, and the manifest/envelope digests remain unverified until then.

## Role

- Emit stamps the trusted publisher receipt `role` as `publisher` (one changed
  line: `role = "publisher"`); the `candidate` role belongs only to the
  preparation/candidate artifact transport and must never authorise the trusted
  publisher (GHI-24).
- No receipt schema version, role-map alias, candidate-trust path or runtime
  role changed; the `publisher` InstallRuntime role-map entry stays consistent
  with the Pipeline wiring.
- Static check: `Parser::ParseFile` → 0 parse errors on the edited source; the
  emitted `receipt.json` role stays unproven until the tester-owned Emit smoke
  runs.
