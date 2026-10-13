# Focused release-fixture diagnosis

## Handoff rejection

The recorded full-suite run in `Execution.md` had a single Handoff failure:
`handoff rejects an inner producer archive without its embedded manifest`.
The filtered baseline reproduced that assertion failure against the existing
`Release.Tests.dll`
(`9908BDA69D61754237C687B4FF9A29BA3A8ECBE0ECD232CCC853DE3466B5E2E6`). The
test expected the phrase `embedded manifest`; that was too narrow for the
production validator's actual typed failure.

The isolated filter for this case reported 1 failed, 0 errored. The two
independent preparation filters each reported 1 errored at the same cleanup
stack in `tests/Orchestration.fs:34`; neither was part of a repeated full-suite
run.

`readRawTransport` passes the verified inner archive bytes to
`ReleaseCompare.observeBytes`. With no inner `distribution.json`, that parser
returns `MalformedArtifact` explaining that the archive must contain exactly
one `distribution.json` entry. The rejection is fail-closed and occurs before a
candidate snapshot is built. The test now requires this typed malformed-artifact
reason and checks its missing-entry meaning without pinning the error location;
any accepted archive still fails the test.

## Preparation fixture cleanup

Both filtered preparation cases independently reproduced
`UnauthorizedAccessException` from `Directory.Delete(root, true)` in the
test-owned `withTemporaryRoot` cleanup. The denied paths were Git loose objects
under each fixture's `.git/objects` directory. In both roots the implicated
object had `FileAttributes.ReadOnly`; inspection found 25 read-only loose object
files per fixture. As a minimal falsification experiment, only those attributes
were cleared inside one reproduction fixture's own object store, after which
recursive deletion of that exact fixture root succeeded. The second fixture
showed the same attribute and cleanup evidence. No process was killed, and no
pre-existing temporary roots were modified.

The test cleanup now clears the read-only bit only on files under its own
fixture's `.git/objects` before deleting that fixture root. No production code
changed. The original focused runs exercised the existing compiled assembly, so
they diagnose the baseline only; this source change requires an engineer-owned
test-project build before its focused cases can be verified. Do not treat the
stale assembly as post-fix evidence.

## Retained readiness status

The full run's 153-case inventory matched its executed count and the provenance
entrypoint passed. Its five Coherence and one Telegram workflow-integration
failures remain the expected RED readiness finding. The workflow was edited
after that run; the current `publish` job is explicitly closed with `if: false`,
but the later workflow state has not had a full-suite verification. This work
does not change or relax those checks, enable publication, or claim end-to-end
integration readiness. No second full-suite run, live GitHub call, or upload was
performed.
