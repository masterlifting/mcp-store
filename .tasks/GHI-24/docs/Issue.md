# Issue #24 reread — 2026-10-13

Coordinator fetched and read the complete current issue body from https://github.com/masterlifting/mcp-store/issues/24. GitHub reports `updated_at: 2026-10-13T13:41:42Z`. Historical `original.md` remains unchanged; it is not a snapshot of this newer issue body.

## Authoritative development-stage migration policy

The current issue explicitly requires forward-only implementation of one canonical contract, removing superseded internal code, legacy paths, aliases, dual schemas, fallbacks and legacy-only tests. It preserves user-owned state/documents/archives, security/authority, path safety, CAS/concurrency, immutable releases and external contracts. Breaking internal contracts require a documented deliberate fail-closed cutover, not silent reinterpretation or permanent dual readers.

It also requires risk-based tests and rapid feedback: focused happy paths, high-impact failures, actual smoke/integration validation and regressions for discovered defects. Avoid exhaustive permutations, redundant fixtures, legacy matrices and trivial wiring tests. Existing meaningful safety tests stay. Oversized matrices are prioritized examples, not mandatory exhaustive enumeration. Never claim unexecuted tests passed.

This policy supersedes conflicting obsolete compatibility or exhaustive low-value testing demands; it does not waive safety, user data or artifact integrity. Explain breaking changes and safe rollout in the PR.

## Coordination consequences

- Modern-only preparation/trusted-builder/publication remains the intended direction. Do not restore retired orchestration or transaction CLI interfaces to satisfy obsolete fixtures.
- Keep the completed 101-case handoff and 50-scenario bootstrap evidence, but stop growing combinatorial matrices. Prioritize one actual two-producer end-to-end smoke, genuine remote-observation behavior through controlled transport, failed validation/no upload, strict no-op/version-bump/recovery, protected trust boundaries and current-main integration.
- Preserve original automation goal: one root push-to-main workflow, exactly Dotnet/Workflow, authored versions and canonical configs, exact event SHA and actual SDK, fixed component tags, immutable assets, genuine published hashes/revision, serialized `queue: max`, no consumer/Telegram changes and no generic CI/CD framework.
- Protected builder and handoff work must remain the smallest release-specific mechanism needed to close the demonstrated executable-replacement threat, not become a general artifact/security framework.
- Independent final reviews and clean-source verification remain required, with evidence explicitly separating local execution, simulations and unverified hosted publication.

Complete current-body retrieval evidence: managed tool output `tool_1358884de001f8bP4oE5CfUJVF` (302 formatted lines, fully read). Current issue authority takes precedence over older task snapshots when they conflict.
