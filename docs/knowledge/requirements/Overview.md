# Knowledge — public requirements baseline

**Status:** sanitized baseline reconciled with the supplied private interview v1.1 (dated 2026-11-05); awaiting owner confirmation that this is the latest approved revision. Not a product architecture or implementation approval.  
**Reviewed against:** MCP Store `main` at `3c71c37ec611032752455dc974d94560badb172a`, 2026-11-05.  
**Owner review required:** the supplied private interview includes all 50 numbered answers, post-interview decisions and a recorded requirements revision. Only sanitized implications are captured here; the owner must still confirm that no later private decisions supersede version 1.1.

## Purpose and authority

Knowledge is being investigated as a **private, locally operable, agent-accessible knowledge system** for structured, source-grounded facts. The goal is to evaluate existing open-source products and components before considering custom implementation. This document is a **sanitized research input**, not a specification of an already chosen product.

The public sources for this baseline are [R-01, issue #62](https://github.com/masterlifting/mcp-store/issues/62), the related [Knowledge research issues #63–#76](https://github.com/masterlifting/mcp-store/issues?q=is%3Aissue+label%3Aknowledge), and current [repository guidance](../../../AGENTS.md) and [README](../../../README.md). The supplied private interview v1.1 is the latest available requirements record, while current published issues define the latest research-task contracts. An issue may refine research scope but cannot silently override a private product decision.

Read the classifications as follows:

- **Research invariant:** binding on research and any recommendation under the current issues; cannot be traded away through a weighted score.
- **Documented v1 gate:** a requirement explicitly supported by the latest *supplied* private requirements revision (v1.1), still subject to the owner's confirmation that no newer decision supersedes it.
- **Evaluation criterion:** a quality or cost dimension to compare, not an automatic rejection rule.
- **Open decision:** intentionally undecided; must be resolved with evidence and an explicit decision, not inferred by an implementer.

Authority order for a future conflict: explicit, current owner decision → reconciled private interview → approved public baseline → issue-specific research scope → researcher inference. Never publish the private source while recording the outcome.

## Requirements register

| ID | Class | Public statement | Research owner |
| --- | --- | --- | --- |
| K-01 | Research invariant | Research only until architecture synthesis and separate approval; no product, producer, migration, distribution or Harness changes. | [R-01](https://github.com/masterlifting/mcp-store/issues/62), [R-14](https://github.com/masterlifting/mcp-store/issues/76) |
| K-02 | Research invariant | Only sanitized requirements, synthetic examples and public references may enter this public repository; do not publish the private interview, personal records, source exports or secrets. | [R-01](https://github.com/masterlifting/mcp-store/issues/62) |
| K-03 | Research invariant | Do not select MCP, REST, CLI, a database, a search engine, an ontology, or the hosting repository in advance. | [R-03](https://github.com/masterlifting/mcp-store/issues/64), [R-12](https://github.com/masterlifting/mcp-store/issues/73), [R-15](https://github.com/masterlifting/mcp-store/issues/75) |
| K-04 | Documented v1 gate | One local instance for a single trusted operator and local agent group, with common privileges. Per-agent proof of identity or isolation is **not guaranteed**. Agents still cannot bypass knowledge validation. | [R-07](https://github.com/masterlifting/mcp-store/issues/68) |
| K-05 | Documented v1 gate | Core knowledge access and retrieval must work with network access disabled after setup, against data already synchronized locally. Remote source freshness is not promised while offline. | [R-08](https://github.com/masterlifting/mcp-store/issues/69), [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-06 | Documented v1 gate | Admission, validation, identity resolution where unambiguous, state transitions and retrieval/ranking must have reproducible deterministic rules independent of LLM judgment. | [R-04](https://github.com/masterlifting/mcp-store/issues/65), [R-10](https://github.com/masterlifting/mcp-store/issues/71), [R-11](https://github.com/masterlifting/mcp-store/issues/72) |
| K-07 | Documented v1 gate | Connectors must not write to external source systems. Writing accepted knowledge **inside** the local store is a distinct operation requiring its own contract. | [R-05](https://github.com/masterlifting/mcp-store/issues/66), [R-04](https://github.com/masterlifting/mcp-store/issues/65) |
| K-08 | Documented v1 gate | Accepted claims have identifiable source provenance, full fact-value revision history, source-loss status and inspectable conflict handling; agent assertions are proposals. Full replay auditing of each computation/search is explicitly **not required**. | [R-03](https://github.com/masterlifting/mcp-store/issues/64), [R-04](https://github.com/masterlifting/mcp-store/issues/65) |
| K-09 | Documented v1 gate | User-driven deletion must cover the active local knowledge state and its derived searchable projections, with explicit and truthful limitations for backups and externally held copies. | [R-08](https://github.com/masterlifting/mcp-store/issues/69) |
| K-10 | Documented v1 gate | The required knowledge core and essential dependencies must qualify as open-source software and must not depend on a mandatory proprietary service for offline retrieval. Exact license compatibility is evaluated per candidate. | [R-02](https://github.com/masterlifting/mcp-store/issues/63), [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-11 | Documented v1 gate | Remote models are outside the trusted local boundary. Raw sensitive personal values must **not** be disclosed to external processors by default; selective disclosure, masking and pseudonymization remain research questions. A retrieval tool cannot police data after handing it to an agent. | [R-07](https://github.com/masterlifting/mcp-store/issues/68) |
| K-12 | Documented v1 gate | First usable release must support working agent integration, quality-tested deterministic retrieval and several useful read-only source integrations. Exact 2–3-source shortlist and feasibility are research outcomes, not predetermined. | [R-06](https://github.com/masterlifting/mcp-store/issues/67) |
| K-13 | Evaluation criterion | Evaluate Russian/English search, aliases, cross-language limitations, conflicting facts, recency, explainable rankings and useful agent context under a synthetic benchmark. | [R-09](https://github.com/masterlifting/mcp-store/issues/70), [R-10](https://github.com/masterlifting/mcp-store/issues/71) |
| K-14 | Evaluation criterion | Prefer minimal single-instance operational complexity, local backup/recovery, portability, open-format export and reuse of mature OSS over bespoke frameworks. | [R-03](https://github.com/masterlifting/mcp-store/issues/64), [R-08](https://github.com/masterlifting/mcp-store/issues/69), [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-15 | Evaluation criterion | For **new owned code**, prefer F# where appropriate and C# when there is an evidence-backed integration advantage. Do not reject an existing OSS component merely because it is written in another language. Native Z#/Rust is unselected. | [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-16 | Documented v1 gate | Agent access supports task-relevant context, paginated summaries, selective details and bounded responses; interface and protocol remain undecided. | [R-11](https://github.com/masterlifting/mcp-store/issues/72), [R-12](https://github.com/masterlifting/mcp-store/issues/73) |
| K-17 | Open decision | Exact trusted-agent membership, remote-context consent granularity, and any independent local-client authorization boundary. | [R-07](https://github.com/masterlifting/mcp-store/issues/68), [R-12](https://github.com/masterlifting/mcp-store/issues/73) |
| K-18 | Open decision | What source deletion means for derived facts; backup retention, restore-time suppression, and whether retained revisions contain deleted personal values. | [R-08](https://github.com/masterlifting/mcp-store/issues/69) |
| K-19 | Open decision | Exact qualified first-version OS matrix, offline first-install requirement, UI delivery order, storage engine, sync framework, client protocol and MCP Store/product code boundary remain unselected. | [R-13](https://github.com/masterlifting/mcp-store/issues/74), [R-15](https://github.com/masterlifting/mcp-store/issues/75) |
| K-20 | Documented v1 gate | No embeddings, vector retrieval or LLM inference **inside the knowledge engine or its retrieval path**; agent-side reasoning is outside that boundary. | [R-09](https://github.com/masterlifting/mcp-store/issues/70), [R-10](https://github.com/masterlifting/mcp-store/issues/71) |
| K-21 | Documented v1 gate | Target one instance with portable core and Docker Compose as an intended deployment model; this does not mandate microservices or identical binaries on all systems. | [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-22 | Documented v1 gate | Operational software upgrades, migrations and maintenance are owner-triggered; source-data synchronization may independently use events, scheduled polling and manual triggers. | [R-05](https://github.com/masterlifting/mcp-store/issues/66), [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-23 | Documented v1 gate | Store facts and provenance links without requiring a duplicate archive of original source content; support export of facts, relationships and metadata in open formats. | [R-03](https://github.com/masterlifting/mcp-store/issues/64), [R-06](https://github.com/masterlifting/mcp-store/issues/67) |

**Interpretation:** K-01–K-03 are research-process invariants. K-04–K-12, K-16, and K-20–K-23 are **documented in supplied private requirements v1.1**, pending confirmation of currency; K-13–K-15 are evaluation criteria; K-17–K-19 are open decisions. Record unresolved details without inventing a technology selection.

## Trust and data-flow boundaries

| Boundary | Trust assumption | Allowed flow and limitation |
| --- | --- | --- |
| Operator and local host | Single trusted operator; host compromise is out of the proposed v1 isolation scope, not proof of security. | Configures source access, consent and local approvals. |
| External accounts, documents, API responses | Source bytes are **untrusted instructions** even if their data is relevant. | Read-only connectors collect candidate records and source identifiers. Neither source content nor an LLM may directly grant permissions. |
| Admission and policy enforcement | Deterministic, inspectable authority for accepted changes. | Validated proposals → accepted/rejected knowledge changes; ambiguous identity merges and conflicts require an explicit policy or approval. |
| Local knowledge, indexes, cache, backup | Private local state, including potentially sensitive material. | Query and update through controlled interfaces; deletions must account for dependent state and restore. Storage locality is not an exfiltration defense by itself. |
| Local agents | A **selected** trusted group for usage, but probabilistic reasoning and retrieved instructions are not enforcement mechanisms. | May receive authorized, bounded context and submit proposals. Assumed group membership does not imply blanket permission for arbitrary data disclosure. |
| Remote LLM/model provider | Separate external trust and retention boundary. | A request containing facts is an outbound disclosure. Once a tool returns content to an agent, this system cannot guarantee the agent will not forward it. |
| Public GitHub repository and CI | Explicitly **not** a personal-data sink. | Only sanitized documentation, public metadata and fully synthetic fixtures. |

Representative flows:

1. **Ingest:** source → read-only connector → typed proposal with source reference → deterministic checks / optional owner approval → local facts and index.
2. **Retrieve:** agent → bounded search and detail selection → context policy → response containing provenance → agent; optional forwarding to a remote model is a separate, explicit boundary crossing.
3. **Delete:** operator → local deletion operation → dependent relations, search projections and caches; backup/restore handling follows a documented retention policy. The external source is not altered.

No claim is made that a shared local-agent trust model is safe for untrusted plugins, remote callers or multi-tenant use. If those are required, reassess K-04 and K-17.

## Operational definitions to test

| Term | Testable meaning | Explicit non-guarantee |
| --- | --- | --- |
| **Deterministic** | With the same canonical input, accepted state snapshot, schema, index, rule/config version, locale and supplied evaluation time, a repeated admission or retrieval produces the same accepted/rejected result, ordering and tie-breaks. | Does not prove a claim is true, that an LLM is deterministic, or that changing data/config never changes answers. |
| **Offline** | With already provisioned software and locally synchronized data, disconnect all network access; startup, local knowledge reads, queries and relevant state operations remain usable. External sync and remote-model features must fail clearly or remain unavailable. | Does not mean every source can be synchronized offline, every dependency can be installed offline, or external data remains fresh. Those are distinct decisions. |
| **Delete** | On an authorized deletion request, the specified data is no longer returned from active facts, history that is in deletion scope, relationship projections, indexes or accessible caches after completion. Define verification and restore-time behavior. | Does not mean secure erasure of physical media, immediate erasure from retained backup copies, erasure from the original external source or revocation of data already sent to an agent/provider. |
| **Open source** | Mandatory software components and runtimes have inspectable source and licenses that meet the Open Source Definition (operational default: OSI-approved license, recorded by SPDX identifier), with distribution/combination obligations reviewed. | Public source visibility, a no-cost hosted tier, an API client SDK, or a noncommercial/source-available license alone is not sufficient. |

The deterministic and offline definitions are **proposed test contracts**, not claims about existing code. The deletion definition is intentionally narrower than physical eradication. Research must explicitly quantify any unresolved backup gap.

## Provisional first-version validation gates

The following are **proposed verification cases** for the documented first-version gates; exact acceptance thresholds and owner sign-off remain open:

1. A synthetic local fact can be ingested/updated with source provenance, rejected when invalid, and retrieved with stable ordering in repeated fixed-state runs.
2. Existing local knowledge remains queryable and locally editable with network access disabled; unsupported remote operations produce explicit errors without corrupting local state. No embeddings or LLM processing take place in the kernel.
3. An external source connector produces no mutation against its source API, including during retries or failure recovery.
4. Agent-proposed mutations cannot bypass deterministic validation and an ambiguous entity merge is not silently accepted.
5. Sensitive facts are not placed in a remote-model context implicitly; the disclosure policy and its residual risks are inspectable.
6. User deletion no longer exposes targeted facts in active query surfaces, with documented backup retention/restore limitations.
7. Essential components pass verified OSS license and mandatory-dependency checks; retrieval quality is evaluated against a shared, synthetic multilingual benchmark. An agent can retrieve source-grounded, paginated results from multiple verified read-only sources.

These checks do **not** approve a particular implementation, mandate a full IAM/encryption stack, or imply that source connectors, UI, protocols or the storage engine have been selected.

## Private reconciliation and changes

The supplied private 50-question interview (version 1.1, 2026-11-05) and its post-interview decisions were reviewed **outside this public repository**. All 50 IDs were present. Only sanitized, category-level requirements are recorded here; no original interview, personally identifying examples, private source text or interview attachments are committed. The owner should confirm this is the latest revision and publish only amended K-ID dispositions: `confirmed`, `revised`, `deferred`, or `not-approved`.

High-impact details still open in the supplied revision: outbound disclosure/consent implementation; exact first-release source shortlist and operating systems; whether offline includes first install; backup retention and restore-time suppression of deleted facts. The group trust model and fact-history/no-replay-audit policy are *already resolved in the supplied version*, so do not re-open them without a newer owner decision.

All subsequent research documents should reference stable K-IDs and propose public diffs instead of silently reinterpreting the baseline.
