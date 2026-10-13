# SEC1 publication boundary amendment

## Status and authority

GHR-36 coordinator synthesis of independent architect `ses_ecec1e8ceffeouGyj5GdYWxsOT` and challenger `ses_ecec1a42fffeOcJp5ioWHzl3gV`. This additive amendment preserves `Solution.md`, original issue #24, all twelve acceptance criteria, and completed verification history. SEC1 remains open until implemented and independently verified.

## Frozen decisions

1. Use fresh hosted jobs for producer preparation, trusted publisher construction, and publication. Producer FSI/config/build/pins/tests never execute in the publication job or on its filesystem. Preparation has no release-write authority. Do not use shared self-hosted storage, preparation caches, or a producer-supplied publisher.
2. Keep the existing compiled release application and `ReleaseTransaction.run` as the single engine. Add explicit preparation and data-only publication entrypoints. Do not retain credential-bearing orchestration as an alternative write path; no compatibility aliases or environment-mode switches.
3. Independently build the publisher in a read-only job from the exact accepted event SHA and an explicit audited release-source/build closure. Exclude producer scripts/projects and unaudited repository-wide build hooks. Transfer its complete runtime closure separately from candidate data. No build, restore, FSI, or tests run in the write-authority job.
4. Derive publication policy independently from canonical `Config.fsx` bytes and `Participants.json` at that SHA using a pure fail-closed parser for the existing declarative syntax. Reject unsupported executable syntax and duplicate bindings. No second authored versions, toolchain values, allowlists, or URLs. This replaces the FSI-only projection decision solely at the protected publisher boundary.
5. Treat preparation receipts, contract projections, manifests, and archives as untrusted data. Bind downloaded publisher and candidate artifacts to exact repository/run/attempt/SHA/component and explicit artifact IDs; verify platform transport digests and actual bytes. Missing or conflicting evidence fails before any POST. Do not select latest artifacts or trust a receipt's revision as authorization.
6. Validate bounded transport archives before extraction: exact names, entry and expansion limits, no traversal/absolute/drive/UNC/alternate-stream paths, links, duplicates or case collisions. Candidate data stays outside publisher/runtime/source directories and is never executed. Reuse existing inner producer archive checks.
7. Independently verify policy, toolchain, candidate identity, standalone/embedded manifest equality, exact event revision, raw hashes/sizes, and main ancestry. Preserve immutable assets, metadata preflight, complete metadata identity, API digest checks, strict equivalence and safe recovery. Upload a verified snapshot of the same bytes, not a later unchecked path reopening.
8. Preserve explicit Dotnet/Workflow test selection, all existing cases, count guards and native failures. A failed preparation/test gate prevents publication. Keep job-local write credentials solely on the final verified publisher invocation.

## Synthesis and exclusions

Both proposals agree on job isolation, independent publisher provenance, non-executing policy projection, and strict handoff verification. Adopt the primary proposal's separate trusted builder rather than compiling inside the write-authority job. Reject the challenger's compatibility retention and standalone PowerShell transaction-policy verifier: validation remains compiled, production-referencing code in the existing application, with workflow tooling limited to transport and job wiring. Do not add generic registration, plugins, extra producers, consumer/Telegram changes, or a second release engine.

## Delivery gates

Implement in reviewable batches: compiled policy/handoff and safe modes; fresh-job workflow/artifact boundaries; production-referencing negative regressions; builds and actual complete gates; independent security, architecture, correctness and acceptance reviews. Engineers own builds, tester owns test execution, DevOps owns workflow wiring. Preserve current 33/33 exact-wrapper evidence as historical coverage, not proof of this amendment.

Required negatives cover publisher replacement/persistence in preparation, arbitrary config syntax, wrong artifact ID/run/attempt/SHA/component/policy/digest, unsafe extraction, manifest disagreement, mutated bytes, and failed producer tests. Prove preparation cannot publish and publication cannot execute producer code. Real hosted publication remains unverified; local executable regressions and source wiring can establish pre-merge acceptance without claiming an upload.

Commits, pushes, external comments/review requests and thread-status changes still require their applicable confirmation gates. Do not merge.

## Frozen implementation contracts after architecture checkpoint

Independent checkpoint `ses_ecec1e8ceffeouGyj5GdYWxsOT` found C1–C7 in the first corrective batch; its build successes are not correctness evidence. No downstream migration is authorized against those interfaces.

- Use one privately constructed verified candidate containing bounded archive/manifest bytes, computed hashes/sizes, parsed candidate observation, canonical policy/toolchain and event revision. One classifier and executor consume it. Remote observations remain genuine remote HTTP observations; never substitute candidate bytes. Upload consumes the verified snapshot. Remove parallel byte/path execution implementations rather than maintaining compatibility.
- Separate an authored audited source/build/runtime closure from the independently generated trusted publisher receipt. The receipt binds repository, event SHA, run/attempt, source/config/roster hashes, derived policy, observed toolchain and runtime-output hashes. Preparation cannot manufacture these expectations.
- Resolve canonical config paths through the verified roster. Hash and parse the same bounded bytes via a pure `parseBytes` interface. Support current configs without changing their syntax; reject unsupported executable syntax.
- Each publisher/candidate platform evidence record carries artifact ID, expected name/role/component, repository, run/attempt/event SHA, platform transport SHA-256, and downloaded transport bytes. Compare independently observed platform metadata with workflow-derived expectations. Toolchain identity is separate from transport digest. Reject duplicate/unknown CLI flags; remove misleading digest aliases and archive-name overrides.
- A compiled outer-handoff validator verifies digest, exact names, limits, paths, links and case collisions before any extraction. Candidate and publisher destinations are new, isolated and non-overlapping. Use bounded reads and typed errors. Inner producer ZIP validation is not outer transport validation.
- Publisher installation needs a non-circular trusted bootstrap: validation by the downloaded executable cannot authenticate that executable. The concrete transport/bootstrap path must be settled with DevOps before implementation or workflow wiring; no unaudited extraction shortcut or second transaction/policy engine.
- Bind repository/component/roster/policy and candidate toolchain independently. Use explicit trusted package/context paths, not embedded build-source directories. Recheck exact SHA and safe-main ancestry immediately before publication through the workflow/platform adapter.

Delivery order: settle trusted publisher transfer/bootstrap; fix pure snapshot policy/types; consolidate the canonical transaction with genuine remote observations; implement bounded handoff; wire fresh jobs; migrate all retained tests and add negatives; run independent reviews. Do not patch around failed assertions or claim the prior 33-case result covers these changes.

## Trusted bootstrap transport decisions

Coordinator adopts the bounded DevOps proposal `ses_ece9d0ba2ffeC933yJa47TOaKG` with these corrections:

1. Reviewed exact-event workflow/bootstrap source, pinned actions and fresh hosted runners anchor installation before the publisher executable exists. A transport-only PowerShell bootstrap fetches raw platform artifact archives by explicit ID, validates metadata/digests and bounded archive structure, and installs only the independently built runtime. It contains no producer-policy or transaction logic. Authentication must not follow cross-origin redirects; signed artifact URLs receive no GitHub credential.
2. Use a self-contained publisher runtime so publication requires no SDK/build/restore/FSI. This does not change producer runtime/platform policy. The trusted builder stages only its authored audited input closure outside the producer checkout, with explicit safe MSBuild/NuGet import and restore resolution. Do not introduce a second authored SDK value or reject unrelated files globally; exclude them from the staged build closure instead.
3. Accept independently trusted builder job outputs for runtime manifest/content digests, bound to explicit publisher artifact ID and exact run/attempt/event SHA. Retain the GitHub control plane and reviewed builder source as trust anchors; do not claim cryptographic independence from that control plane. No committed runtime digest placeholder, nullable compatibility field, or preparation-generated trusted expectations.
4. Distinguish platform-generated outer artifact digest from the builder-generated inner runtime-envelope digest. The builder cannot predict the outer archive bytes subsequently produced by upload-artifact. Verify outer bytes against platform metadata and upload outputs, then inner bytes against the builder's independent manifest/envelope hashes. Candidate transport follows the same separation after trusted publisher installation.
5. Separate source paths and hashes in generated receipts. Resolve config paths from the verified roster; no zero hash placeholders. Runtime and candidate artifact IDs, names, role/component, run/attempt/SHA and digest are separate typed fields. Attempt evidence must come from corresponding job outputs and attempt-specific platform observations; do not infer creation attempt solely from an artifact object or the latest run response.
6. Remove misleading toolchain-as-digest flags and archive overrides without aliases. Toolchain observations and source-policy agreement remain distinct from SHA-256 transport integrity. Bootstrap/digest failure prevents executable installation and token-bearing invocation.

This narrowly permits unavoidable bootstrap transport validation outside the compiled application; it does not authorize another policy or release engine. Independent security review must verify the non-circular bootstrap and redirect credential boundary as well as compiled candidate validation.
