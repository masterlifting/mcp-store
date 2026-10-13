# Bootstrap comment normalization

Status: comments-only edits for the assigned surface are complete and statically
verified. No build, CLI invocation, upload or smoke run was performed, so this is
not an approval and not post-change test evidence.

## Scope

Assigned surface: `infrastructure/release/Bootstrap.ps1`, `Build.ps1`, `Suite.ps1`,
`.github/workflows/release.yml`, `infrastructure/release/Release.fsproj`, and
`infrastructure/release/README.md`. The `.fs` sources owned by the parallel F#
engineer were not opened for edit; only the two stale-mode comment references in
`Suite.ps1` were corrected.

## Changes

`Bootstrap.ps1` was the bulk of the remaining work. Comment lines went from 79 to
26 (1195 -> 1140 file lines) with no code change:

- Deleted the numbered phase labels `# 1.` .. `# 8.` in the main block.
- Deleted restatements of code (`# Reads the bounded response body of a platform
  metadata request.`, the `Get-ArtifactRawArchive` summary, and the duplicate
  cross-origin Authorization note).
- Deleted the duplicated role-map invariant that appeared three times; the map
  now carries one comment next to `Resolve-RoleBinding`.
- Collapsed every remaining multi-line block to one line, keeping the strongest
  reason (case policy, `-Value` non-Mandatory trap, `ExtractToDirectory`
  avoidance, redirect handling, receipt-destination ordering, inner/outer digest
  namespaces).
- Removed the banned wording (`deliberately`) and the banner separator lines that
  survived the earlier pass.

Other files:

- `Build.ps1`: unchanged. Its 17 comments are already one-line why/trap comments
  and its header is role plus two points.
- `Suite.ps1`: two comments still named the removed `orchestrate` mode; both now
  name the `prepare` tests phase, which is what runs the packaging suite today.
- `release.yml`: 44 -> 33 comment lines. The ten-suite selection comment lost its
  pending-integration narration; the `NOT ENABLED` job note was collapsed to one
  line; the trailing six-line "intended wiring" narration was deleted.
- `Release.fsproj`: dropped the second sentence of the `Bootstrap.fsx` comment,
  which restated the producer script layout instead of the exclusion trap.
- `README.md`: 535 -> 555 lines, additions only.

## Relocated contract text

The removed Bootstrap comments carried three external values, now documented in
the new `### Bootstrap CLI values` section of `infrastructure/release/README.md`:
the exact `-RoleMapJson` JSON shape with its per-role `component` rule, the
`-UploaderJobId` source (`job.check_run_id`), and the two accepted
`-PlatformDigest` forms with their bare-lowercase-hex normalization. The text
states only what the script does; it claims no validation that is not performed.

## Verification

Comments were located with the PowerShell parser, not with text greps, so no
comment text was taken from a string or here-string.

- Code identity (comments excluded): `Bootstrap.ps1`, `Build.ps1` and `Suite.ps1`
  token streams are byte-identical to `.../Temp/opencode/orig/*.orig` after line
  endings are normalized (`Bootstrap.ps1` 6646 tokens, 17 line continuations;
  `Build.ps1` 1953; `Suite.ps1` 582). Comment tokens are the only difference.
- `Bootstrap.ps1` parses with zero errors; the file stays uniformly CRLF with no
  lone LF or lone CR.
- `release.yml`: every non-comment line is byte-identical before and after (402
  lines); both revisions parse with PyYAML and the parsed object trees are
  identical once comment lines are stripped from the block scalars.
- `Release.fsproj`: XML element and attribute structure is identical before and
  after; only a comment node changed.
- `README.md`: the delta against the pre-change file is 20 added lines, zero
  removed.
- Post-change scan of all six files finds no `deliberately`, `intentionally`,
  `Slice N`, `TODO`, `DEBUG-REMOVE` or `# ----` banner text. The only remaining
  task-doc citation is inside a `Write-Host` string in `release.yml`, which is
  frozen because strings were out of scope.

## Gap: the standalone bootstrap test still keys on a removed banner

`infrastructure/release/tests/Bootstrap.ps1:556` delimits the transport source
with `$bootstrapText.IndexOf("# ---- runtime manifest")`. That banner was removed
by the earlier banner cleanup, so the lookup now returns `-1` and the assertion at
line 557 ("production transport functions could not be delimited") cannot pass:

```text
metadataStart=24523  downloadStart=27911  manifestStart(banner)=-1
function Read-RuntimeManifest index=34157
```

A banner-free `Bootstrap.ps1` cannot satisfy this, and re-adding the banner would
violate the comment contract. Test source was not edited, so the test owner needs
to re-anchor the delimiter, for example to `function Read-RuntimeManifest`
(index 34157, still after `downloadStart`). The 50/50 and 26/26 numbers in
`Smoke.md` therefore no longer describe the current `Bootstrap.ps1` source.

## Out of scope observations

- `README.md` prose uses "deliberately" in two places (the role-field and
  producer-test paragraphs). These are prose, not comments, and the README scope
  for this pass was relocated contract text only, so they were left alone.

## Evidence commands

```text
[System.Management.Automation.PSParser]::Tokenize(content, [ref]$errors)   # comment inventory, zero parse errors
[System.Management.Automation.Language.Parser]::ParseFile(path, ...)       # Bootstrap.ps1 AST parse
Compare-Object <code tokens without Comment/NewLine/Whitespace> -SyncWindow 0
python yamlcheck.py orig/release.yml.orig .github/workflows/release.yml     # PyYAML tree compare
Compare-Object (non-comment lines of release.yml) -SyncWindow 0
[xml] SelectNodes('//*') shape compare for Release.fsproj
```
