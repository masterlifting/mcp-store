# R-01 — Requirements baseline, trust boundaries and research decision rules

**Research date:** 2026-11-05  
**Scope:** requirements and decision method only; no product or technology selection.  
**Repository snapshot:** [`masterlifting/mcp-store` at `3c71c37ec611032752455dc974d94560badb172a`](https://github.com/masterlifting/mcp-store/tree/3c71c37ec611032752455dc974d94560badb172a).  
**Public baseline:** [`../requirements/Overview.md`](../requirements/Overview.md).  
**Completion status:** supplied private requirements v1.1 reconciled against the public issue/repository baseline. The interview contains 50/50 numbered answers and post-interview clarifications; no private source content has been committed. **Owner confirmation that v1.1 is the latest approved revision and review of remaining open policies are still pending.**

## 1. Research question and result

**Question:** Which constraints are binding for the first version, which are hypotheses, and what evidence permits a defensible technology recommendation?

**Result:** Separate (a) binding research-process invariants, (b) first-version gates **documented by the supplied private v1.1 record**, (c) measurable evaluation criteria, and (d) open decisions. This avoids treating an issue alone or a researcher's interpretation as proof of a product requirement.

The accompanying public [requirements register](../requirements/Overview.md#requirements-register) introduces K-IDs to make downstream conclusions traceable. It defines the testable meanings of deterministic, offline, delete and open source, the assumed local trust group, and the external-model disclosure boundary.

**No database, protocol, search engine, ontology, connector framework, UI, programming language, producer, repository location or final architecture is selected.**

## 2. Evidence collected and limitations

| Evidence | Observed fact / contribution | Provenance and limitation |
| --- | --- | --- |
| E-01 | [#62](https://github.com/masterlifting/mcp-store/issues/62) demands a sanitized public baseline, deterministic core/retrieval, local/remote trust definitions, read-only sources, offline and OSS restrictions, and explicit technology-selection rules. | Current public issue as read 2026-11-05. Issue requirements do not replace a private interview. |
| E-02 | [#63–#76](https://github.com/masterlifting/mcp-store/issues?q=is%3Aissue+label%3Aknowledge) break research into existing products, representation, admission, ingestion, sources, privacy, lifecycle, benchmarking, search, ranking, interface, runtime, Store fit and final synthesis. | Issue texts define research questions and dependencies, **not completed findings** or owner-ratified release requirements. |
| E-03 | At snapshot SHA, [README.md](https://github.com/masterlifting/mcp-store/blob/3c71c37ec611032752455dc974d94560badb172a/README.md) lists Dotnet, Workflow and Telegram producers; [AGENTS.md](https://github.com/masterlifting/mcp-store/blob/3c71c37ec611032752455dc974d94560badb172a/AGENTS.md) defines producer and consumer boundaries. The `docs/knowledge` directory did not exist on `main` at this snapshot. | Direct read of public repository files and full tree. No Knowledge producer exists in this audited snapshot. This is **not** proof of suitability for a future service. |
| E-04 | The [Open Source Definition](https://opensource.org/osd) requires freedoms beyond mere source visibility; [OSI's approved license register](https://opensource.org/licenses) provides a reviewable license criterion; the [SPDX license list](https://spdx.org/licenses/) records identifiers and OSI status. | Primary standards/project documentation, checked 2026-11-05. Actual transitive licensing requires candidate-specific investigation. |
| E-05 | [OWASP LLM01:2025](https://genai.owasp.org/llmrisk/llm01-prompt-injection/) identifies indirect prompt-injection risk in retrieved content and tools; [OWASP LLM02:2025](https://genai.owasp.org/llmrisk/llm022025-sensitive-information-disclosure/) identifies disclosure through model context/outputs. | Published OWASP risk taxonomy consulted on 2026-11-05; it is risk guidance, **not** a validation of this system's security controls. |
| E-06 | [UK ICO erasure guidance](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/individual-rights/individual-rights/right-to-erasure/) distinguishes live-system removal from backup persistence and describes `beyond use` during scheduled replacement. | A practical reference illustrating backup-erasure tension, **not** a jurisdictional/legal conclusion for this private project. |
| E-07 | The operator supplied a private requirements record marked **v1.1 / 2026-11-05**, including all 50 numbered answers, amendments and subsequent clarified decisions; the companion private brief states the same effective revision. | Reviewed locally as private inputs in this research session. **Neither raw document nor personal source excerpts are included in this public PR.** Owner confirmation that no later revision exists remains open. |

No synthetic runtime experiment was necessary to classify requirements; **none was performed**. Consequently there are no measured search, privacy, sync, latency, disk or integration results in R-01. Later issues must not attribute such measurements to this report.

## 3. Classification: what is binding now?

### 3.1 Binding research invariants

- **Process:** research-only documentation PRs; no product code, new MCP producer, Resolver/CI/Harness changes, prototype merger or pre-approved implementation plan ([#62](https://github.com/masterlifting/mcp-store/issues/62), [#76](https://github.com/masterlifting/mcp-store/issues/76)).
- **Public boundary:** no private interview content, personal source records, credentials, session tokens, real exports or non-synthetic examples in the public repository.
- **Decision neutrality:** MCP, database, graph, vector/lexical search, UI and hosting are candidates, not prior commitments.
- **Honest evidence:** cite versioned/dated official specifications and actual source code; separate verified facts, measurements, inference and unknowns; do not silently promote hypotheses into requirements.

These are **currently applicable to the research work**, even before private product-requirement reconciliation.

### 3.2 Documented first-version gates (supplied v1.1)

The supplied private revision explicitly supports these gates (K-04–K-12, K-16, K-20–K-23). They are not derived solely from the issue network. The owner must still confirm that this is the most recent effective revision:

1. Single-instance operation for one trusted local agent group, without guaranteed per-agent isolation or identity checks.
2. Core local knowledge access, search and changes with the network disconnected after provisioning; no embeddings/vector search or LLM inference inside the knowledge engine.
3. Deterministic knowledge admission, state changes and retrieval for a fixed, versioned snapshot; an LLM may propose but cannot unilaterally establish authoritative truth or permissions.
4. Read-only **external** source integration, with separate controlled mutation of **local** accepted knowledge.
5. Provenance, full fact revision history, source-loss status and conflict semantics that can explain why a claim appears, without full computational replay auditing.
6. Defined user deletion and backup limitations; do not promise impossible retroactive erasure from already disclosed context.
7. OSS core/essential runtime components with no mandatory proprietary online processing for offline knowledge operations.
8. No unapproved outbound disclosure of raw sensitive values; explicit local-to-remote context boundary and research into minimal useful masking/projection/approval.
9. Working first-version agent integration, multiple useful read-only sources, deterministic quality-tested multilingual search and paginated context.
10. Portable single-instance core, Docker Compose target, local automatic backups, open-format export and user-triggered software updates/maintenance (independent of automated source synchronization).

Treat these as **supplied-source v1 gates**, and record violations as `v1-gate conflict` with supporting evidence; do not invent exemptions. Remaining ambiguity or unverified latest-version status must be raised for owner resolution, not silently converted to vendor acceptance.

### 3.3 Evaluation criteria, not hidden mandates

- Search quality and explainability for Russian and English, entities, relations, time, aliases and conflicting claims ([#70](https://github.com/masterlifting/mcp-store/issues/70), [#71](https://github.com/masterlifting/mcp-store/issues/71)).
- Connector availability, incremental-sync semantics, recovery and *which* several high-value sources form the first release ([#66](https://github.com/masterlifting/mcp-store/issues/66), [#67](https://github.com/masterlifting/mcp-store/issues/67)).
- Local operational burden, safe backup/restore, portability and maintainability within the documented single-instance, exportable and Docker Compose-capable boundary ([#64](https://github.com/masterlifting/mcp-store/issues/64), [#69](https://github.com/masterlifting/mcp-store/issues/69), [#74](https://github.com/masterlifting/mcp-store/issues/74)).
- Preference for F# in **new owned code** and C# for justified integrations; existing OSS language does not determine its fitness. Z#/Rust is a feasibility question, not a prerequisite ([#74](https://github.com/masterlifting/mcp-store/issues/74)).
- Context-size control, pagination and source-grounded result contracts. Protocol remains open ([#72](https://github.com/masterlifting/mcp-store/issues/72), [#73](https://github.com/masterlifting/mcp-store/issues/73)).

## 4. Causal trust-boundary analysis

**Boundary A — external sources → local ingestion.** Source content is data, not trusted instructions. Prompt injection can be carried by retrieved documents even when the account is legitimate; OWASP LLM01:2025 explicitly recognizes this risk (E-05). Read-only credentials constrain source-system side effects, not what a malicious document may tell an agent to do. A deterministic admission boundary must separately verify structure, provenance and policy. Factual truth is not established merely by deterministic parsing.

**Boundary B — agent proposals → authoritative local state.** An agent output is probabilistic. It may suggest facts, aliases, merges or deletions, but state mutation follows validated schema and explicit approval/precedence policies. Agent trust as a **user-facing local group** is not a substitute for data-integrity enforcement. Whether every ambiguous merge requires human confirmation is a question for [R-04](https://github.com/masterlifting/mcp-store/issues/65).

**Boundary C — local store → local agents.** Local storage protects neither against malicious local clients nor an agent with unconstrained access. The v1 group is assumed trusted for normal operation, **not isolated from each other**; this is a scoped threat-model assumption, not a system guarantee. Agent identity, permissions, remote-access endpoints and future multi-user needs are open in R-07/R-12.

**Boundary D — local agent → remote model.** Supplying a tool result to an agent can lead to sending it in an external model request. A knowledge service cannot revoke or filter content after that output leaves its control. Implementable measures are **before-disclosure selection, scope minimization and explicit policy**; any claim of preventing all onward forwarding would require an independently controlled egress boundary and evidence, not prompt instructions (E-05).

**Boundary E — operational backup/publication.** Backups, logs and search indexes may retain supposedly deleted material; public Git commits are an irreversible disclosure risk for ordinary operational purposes. Do not commit personal artifacts even temporarily. Backup-retention and restore-time deletion policies are R-08 questions; E-06 documents why live removal is not the same as instantaneous backup erasure.

### Minimal data-flow cases to carry forward

| Scenario | Required distinction | Failure / negative case |
| --- | --- | --- |
| Remote source changed; local machine offline | Locally cached search remains usable, live source sync is unavailable. | Do not report stale results as freshly synchronized. |
| Agent invents a person/entity relation | Proposal vs accepted fact; source support and ambiguous identity remain explicit. | Reject or require approval; do not take fluent model wording as proof. |
| Source record removed | Loss of evidence vs owner's request to erase derived knowledge. | Do not conflate these two operations or claim the external source is altered. |
| User deletes an accepted private fact | Active state, history-in-scope, index, caches, backup/restore are separate surfaces. | A UI disappearance is not sufficient evidence of full deletion. |
| Local agent forwards tool response to cloud LLM | Tool-return trust vs onward-disclosure trust. | Redaction performed **after** return cannot be guaranteed by the knowledge service. |
| Retrieved document includes malicious instructions | Content vs control plane. | Document must not gain authority to change policy or execute commands. |

## 5. Operational term decisions

R-01 establishes **test contracts, not implementation prescriptions**:

- **Deterministic:** same canonical input + same knowledge/index snapshot + same rule/schema/locale versions + supplied evaluation time → same validation decision, ordered result and tie-break. Wall-clock reads, implicit provider reranking or unordered set iteration without stable ties break the contract. Ground truth is a separate dimension.
- **Offline:** with already installed dependencies and synchronized local corpus, disable network and verify local startup, query and allowed updates; online sync, remote LLM and initial package acquisition are **not** assumed offline. Offline installation is an explicit owner question.
- **Delete:** specify removal scope covering active facts, applicable revisions, relation edges, derived indexes, caches and operational artifacts; separately document retained encrypted/unencrypted backups, their expiry and restore protections. Never equate logical deletion with physical secure erase or deletion at external providers.
- **Open source:** inspect the required component's license, source availability, SPDX identifier, OSI status and distribution/composition obligations. A source-available repository, no-cost tier or open API does **not** suffice by itself (E-04). Decide separately if optional external agent providers must satisfy the same rule.

These definitions are used by the public baseline; R-04, R-07, R-08, R-09 and R-10 must make their exact verification cases concrete.

## 6. Research decision procedure

Every later research PR should use this order.

1. **Map requirement and authority.** Name K-IDs and classify as confirmed invariant, candidate gate, criterion or open decision. Note any conflict with a current user decision; do not suppress uncertainty.
2. **Define the compared capability.** Product vs reusable library vs adapter vs architectural reference is not an interchangeable unit of comparison. Include no-change/reuse and at least one plausible alternative.
3. **Collect direct primary evidence.** Official documentation/specification, upstream source at immutable revision, release date/version, license/maintainer status and actual runtime requirements. Verify product edition and optional/mandatory cloud services. For a negative claim, identify the missing contract in the checked version, not just the absence of a marketing feature.
4. **Evaluate gates separately from trade-offs.** A violation of a **documented v1 gate** is non-compensable by speed, stars or convenience; record it and obtain owner resolution before excluding an approach by policy exception. An open decision remains open; unknowns are not passes.
5. **Compare on equivalent synthetic cases.** Use the same fixed corpus, exact queries, source permissions, failure injection and benchmark definitions where applicable. Report the host, versions, sample size, measurement procedure and limitations. Do not invent thresholds or infer p95 from a single query.
6. **Compare marginal costs.** Assess operational processes, necessary adapters, source sync/restore, licensing, implementation/maintenance, migration/exit and the code that can actually be **removed** by reuse. Prefer an existing solution when it satisfies the required behavior with lower total ownership cost; a custom core needs an evidenced gap, not language preference.
7. **Record outcome.** Choose `suitable`, `conditionally suitable`, `unsuitable` or `undetermined`; state sources, date, why alternatives were not preferred, assumptions, residual risks and a reproducible test or unanswered question that could change the outcome. **Only R-14 plus explicit owner approval** can authorize a final architectural direction.

### Evidence grades

| Grade | What it establishes | Example | Restriction |
| --- | --- | --- | --- |
| P — primary | Documented support or standard requirement as of a version/date. | Upstream API specification, OSI license text. | Documentation may omit bugs or operational gaps. |
| S — source-verified | Actual behavior/implementation boundary of a known revision. | Reviewed API/SDK source or release manifest. | Code inspection alone is not a performance measurement. |
| X — experiment | Reproducible behavior under recorded synthetic conditions. | Fixed multilingual queries, disconnected-network test, retry/delete matrix. | Scope conclusions to actual tested environment. |
| I — inference | Reasoned interpretation or recommended trade-off. | Lower anticipated maintenance if an existing SDK is reused. | Must link evidence and state uncertainty. |
| U — unknown | Missing, contradictory or inaccessible evidence. | Private interview decision not supplied; vendor behavior not tested. | **Not** a passed gate or proven absence of feature. |

Attach candidate versions and evidence dates to every claim likely to change with releases. For benchmarks or vulnerability cases, keep source fixtures synthetic. When a standard is versioned, cite that **specific edition**; do not silently assume the latest.

### Explicitly rejected research shortcuts

| Shortcut | Why it is invalid |
| --- | --- |
| “MCP Store hosts the research, therefore Knowledge must be an MCP producer.” | Docs/repo location, service boundary and delivery protocol are different decisions; see [#75](https://github.com/masterlifting/mcp-store/issues/75). |
| “Deterministic means no LLM may participate anywhere.” | An LLM can propose; the **authoritative validation and state/retrieval rule** must be deterministic. |
| “Offline means every remote source and cloud provider works disconnected.” | Local operation and external freshness require different contracts. |
| “Read-only data source means no local fact may ever be changed.” | Source-side authorization and local knowledge admission are independent. |
| “Deleting a DB row guarantees complete data erasure.” | Index, history, logs, backups and previously disclosed data are separate surfaces. |
| “Source visible on GitHub implies OSS.” | License rights and mandatory dependencies matter under the OSI definition. |
| “F# preference requires rewriting an existing mature engine.” | Owned-code preference is not proof of a technical requirement; measurable reuse comes first. |
| “A high feature count/benchmark score compensates for missing security or license evidence.” | Non-compensable gates and unknowns must remain visible. |

## 7. Unresolved decisions and risks

| ID | Unresolved matter | Why it matters | Routed research |
| --- | --- | --- | --- |
| U-01 | Private revision v1.1 was supplied and reconciled. A later effective amendment, if any, has not been ruled out by the owner. | **Blocks declaring this the final owner-confirmed current baseline.** No private source text may be published. | Owner confirmation of currency and review of any later private changes. |
| U-02 | Exact membership/authority of local agent group and whether remote clients count as trusted. | Determines whether extra process/user isolation is required. | [R-07](https://github.com/masterlifting/mcp-store/issues/68), [R-12](https://github.com/masterlifting/mcp-store/issues/73) |
| U-03 | User approval and data class policy for sending retrieved facts to remote models. | Defines where enforceable minimization ends. | [R-07](https://github.com/masterlifting/mcp-store/issues/68) |
| U-04 | Required v1 source types and their authorization/sync mechanics. | Defines integration viability, not just connector availability. | [R-05](https://github.com/masterlifting/mcp-store/issues/66), [R-06](https://github.com/masterlifting/mcp-store/issues/67) |
| U-05 | Source removal vs erase request vs history/conflict retention vs backups. | Incompatible naive interpretations cause data leakage or false provenance. | [R-04](https://github.com/masterlifting/mcp-store/issues/65), [R-08](https://github.com/masterlifting/mcp-store/issues/69) |
| U-06 | Required OS/platform matrix and whether first-install must be offline. | Can change package and deployment feasibility. | [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| U-07 | Actual multilingual accuracy/latency thresholds and evaluation corpus. | Without a shared benchmark, a search engine comparison is anecdotal. | [R-09](https://github.com/masterlifting/mcp-store/issues/70), [R-10](https://github.com/masterlifting/mcp-store/issues/71) |
| U-08 | Meaning of OSS constraint for optional LLM/provider integrations vs essential core. | Can incorrectly exclude legal useful adapters or allow mandatory closed dependencies. | Owner confirmation; [R-02](https://github.com/masterlifting/mcp-store/issues/63) |
| U-09 | Package/repository separation, mutable state and whether Resolver contracts suit a knowledge service. | Existing MCP packaging does not prove generic server delivery. | [R-15](https://github.com/masterlifting/mcp-store/issues/75) |

## 8. Research sequencing

After the **reviewed** R-01 baseline, the following can start independently: R-02 (#63), R-03 (#64), R-05 (#66), R-07 (#68), R-09 (#70), R-12 (#73) and the R-01-dependent portion of R-13 (#74).

Dependencies then constrain the rest:

- R-03 → R-04; R-03 + R-07 → R-08.
- R-05 → R-06; R-03 + R-09 → R-10; R-09 + R-10 → R-11.
- R-05 + R-01 → R-13 (as specified in #74); R-12 + R-13 → R-15.
- R-14 (#76) synthesizes **all** preceding research including R-15; any product/implementation plan requires **separate owner authorization** after synthesis.

The numbering R-15/R-14 is historical issue naming, **not** permission to synthesize before evaluating MCP Store fit. Do not duplicate ownership across reports: each downstream issue should cite the same K-IDs but investigate only its own surface.

## 9. Recommendation and exit criteria

**Recommendation:** use [`Overview.md`](../requirements/Overview.md) as the **sanitized, source-reconciled public comparison contract** based on supplied private v1.1, pending owner confirmation that no newer decisions supersede it. Keep the original private material outside GitHub, and publish only sanitized K-ID changes. Once reviewed, conduct product-first and component-first research in parallel under the gate/evidence rules above.

**R-01 acceptance is met only when:**

- All documented K-IDs are traceable to the supplied effective private requirements revision or an explicit research issue, with unresolved details visibly marked open and a responsible decision owner.
- The supplied private 50-question input and post-interview changes have been reconciled without publishing personal material; the owner has confirmed whether a later version supersedes it.
- Definitions and local/remote trust boundaries are approved as operationally testable.
- Downstream technology comparisons can cite stable K-IDs, evidence grades and issue dependencies without inventing hidden architectural commitments.
- The documentation-only PR has been reviewed; no prototypes or product changes are merged.

**Current verdict:** private input reconciliation complete **for the supplied v1.1 record**. The report and baseline are ready for owner review, but **do not close R-01 until current-version sign-off and outstanding policy decisions are accepted**. No implementation or speculative follow-up issues are justified.
