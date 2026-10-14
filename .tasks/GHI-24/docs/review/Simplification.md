# Approved forward-only simplification

The user explicitly approved this amendment after independent architect, challenger and security design assessment. It supersedes the intermediate custom publisher bootstrap, receipt, raw platform transport and source-closure requirements in `Boundary.md`; unchanged release invariants remain mandatory.

## Frozen contract

- Three isolated jobs: read-only publisher construction, read-only producer preparation/tests, and write-capable publication. No build, restore, tests or producer scripts execute in publication. Only reviewed release wiring and pinned supported actions execute before the verified publisher.
- One canonical `Config.json` per producer replaces `Config.fsx`. A strict bounded JSON policy reader replaces the F# subset interpreter. Pre-SDK projection reads the same JSON, not an independently authored policy table. Versions remain Dotnet 1.0.4 and Workflow 1.0.5; no automatic increment or unrelated assembly-version change.
- Remove custom `Bootstrap.ps1`, `Build.ps1`, `Resolve.ps1`, `Sources.json`, `Publisher.json`, `PublisherClosure.fs`, runtime envelopes and receipt schemas. No compatibility copies, aliases, dual readers or retired CLI modes remain.
- Build the existing release project independently in the read-only publisher job using its ordered compile inputs, isolated restore and disabled repository-wide MSBuild imports. Do not maintain a duplicate authored source graph.
- Use pinned supported Actions and exact artifact IDs from this run. Independently emit a minimal complete publisher file/SHA-256 inventory and verify every file, membership and path before execution. Fail closed; warning-only platform digest checks are not sufficient. No generic provenance/receipt framework.
- Candidates remain bounded data-only archives and manifests. Preserve exact event-SHA, canonical identity/toolchain/policy validation, strict byte equivalence, genuine remote digest observations, immutable assets, same verified-byte uploads and safe metadata recovery through the existing single release core.
- Preserve pre-publication ancestry/revision checks, queue retention, native failure propagation, twelve Workflow non-packaging gates and package-dependent gates before upload. Retain meaningful preflight, metadata identity, digest/readback and immutable recovery regressions; retire only custom-layer tests rendered obsolete by the explicit cutover.
- Delete the redundant closure abstraction rather than rename it. Keep required role directives and minimal one-line why comments; remove the remaining orchestration header narration. No unrelated churn.

The realistic isolation boundary is producer execution/artifacts versus independently built publisher code. Trusted reviewed main source is not protected from a malicious maintainer commit. Three jobs retain isolation without compiling/restoring in the write-capable job, where ambient action-token authority would otherwise require an explicit relaxation.

## Verification and authority

Implementation, local executable workflow/artifact smoke, focused regressions and independent reviews remain required. Prior evidence at `18146a7dca58a1f08ca58b6407ccbb2e244a9395` is the preserved baseline, not proof of the simplified implementation. Hosted publication remains separately unverified; no pre-merge upload is required. This approval authorizes source changes only, not commits, pushes, PR merging or thread resolution.
