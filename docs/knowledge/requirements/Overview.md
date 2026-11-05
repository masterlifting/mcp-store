# Knowledge — public requirements baseline

**Status:** provisional research baseline; not a product architecture or an implementation approval.  
**Reviewed against:** MCP Store `main` at `3c71c37ec611032752455dc974d94560badb172a`, 2026-11-05.  
**Owner review required:** reconciliation with the private 50-question interview and later decisions is incomplete because the source interview was not available to this research pass.

## Purpose and authority

Knowledge is being investigated as a **private, locally operable, agent-accessible knowledge system** for structured, source-grounded facts. The goal is to evaluate existing open-source products and components before considering custom implementation. This document is a **sanitized research input**, not a specification of an already chosen product.

The public sources for this baseline are [R-01, issue #62](https://github.com/masterlifting/mcp-store/issues/62), the related [Knowledge research issues #63–#76](https://github.com/masterlifting/mcp-store/issues?q=is%3Aissue+label%3Aknowledge), and current [repository guidance](../../../AGENTS.md) and [README](../../../README.md). A research issue may describe a candidate policy without proving that the owner approved it as a v1 release gate.

Read the classifications as follows:

- **Research invariant:** binding on research and any recommendation under the current issues; cannot be traded away through a weighted score.
- **Candidate v1 gate:** a proposed non-negotiable requirement for the first usable version; requires private interview reconciliation and explicit owner confirmation before becoming binding.
- **Evaluation criterion:** a quality or cost dimension to compare, not an automatic rejection rule.
- **Open decision:** intentionally undecided; must be resolved with evidence and an explicit decision, not inferred by an implementer.

Authority order for a future conflict: explicit, current owner decision → reconciled private interview → approved public baseline → issue-specific research scope → researcher inference. Never publish the private source while recording the outcome.

## Requirements register

| ID | Class | Public statement | Research owner |
| --- | --- | --- | --- |
| K-01 | Research invariant | Research only until architecture synthesis and separate approval; no product, producer, migration, distribution or Harness changes. | [R-01](https://github.com/masterlifting/mcp-store/issues/62), [R-14](https://github.com/masterlifting/mcp-store/issues/76) |
| K-02 | Research invariant | Only sanitized requirements, synthetic examples and public references may enter this public repository; do not publish the private interview, personal records, source exports or secrets. | [R-01](https://github.com/masterlifting/mcp-store/issues/62) |
| K-03 | Research invariant | Do not select MCP, REST, CLI, a database, a search engine, an ontology, or the hosting repository in advance. | [R-03](https://github.com/masterlifting/mcp-store/issues/64), [R-12](https://github.com/masterlifting/mcp-store/issues/73), [R-15](https://github.com/masterlifting/mcp-store/issues/75) |
| K-04 | Candidate v1 gate | One trusted local operator and a specified local agent group are the assumed audience; separate hostile tenants and per-agent isolation are **not assumed**. Agents are not thereby authorized to bypass validation. | [R-07](https://github.com/masterlifting/mcp-store/issues/68) |
| K-05 | Candidate v1 gate | Core knowledge access and retrieval must work with network access disabled after setup, against data already synchronized locally. Remote source freshness is not promised while offline. | [R-08](https://github.com/masterlifting/mcp-store/issues/69), [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-06 | Candidate v1 gate | Admission, validation, identity resolution where unambiguous, state transitions and retrieval/ranking must have reproducible deterministic rules independent of LLM judgment. | [R-04](https://github.com/masterlifting/mcp-store/issues/65), [R-10](https://github.com/masterlifting/mcp-store/issues/71), [R-11](https://github.com/masterlifting/mcp-store/issues/72) |
| K-07 | Candidate v1 gate | Connectors must not write to external source systems. Writing accepted knowledge **inside** the local store is a distinct operation requiring its own contract. | [R-05](https://github.com/masterlifting/mcp-store/issues/66), [R-04](https://github.com/masterlifting/mcp-store/issues/65) |
| K-08 | Candidate v1 gate | Accepted claims need identifiable source provenance, provenance status, revision semantics and inspectable conflict handling; an agent assertion is a proposal, not automatically a fact. | [R-03](https://github.com/masterlifting/mcp-store/issues/64), [R-04](https://github.com/masterlifting/mcp-store/issues/65) |
| K-09 | Candidate v1 gate | User-driven deletion must cover the active local knowledge state and its derived searchable projections, with explicit and truthful limitations for backups and externally held copies. | [R-08](https://github.com/masterlifting/mcp-store/issues/69) |
| K-10 | Candidate v1 gate | The required knowledge core and essential dependencies must qualify as open-source software and must not depend on a mandatory proprietary service for offline retrieval. Exact license compatibility is evaluated per candidate. | [R-02](https://github.com/masterlifting/mcp-store/issues/63), [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-11 | Candidate v1 gate | Remote model calls are outside the local trust boundary. Before any remotely consumed context is disclosed, data minimization and an explicit disclosure policy must be assessed; a retrieval tool cannot police data after returning it to an agent. | [R-07](https://github.com/masterlifting/mcp-store/issues/68) |
| K-12 | Evaluation criterion | Prioritize two or three useful, verifiable first-version source integrations; their identities and feasibility remain open. | [R-06](https://github.com/masterlifting/mcp-store/issues/67) |
| K-13 | Evaluation criterion | Evaluate Russian/English search, aliases, cross-language limitations, conflicting facts, recency, explainable rankings and useful agent context under a synthetic benchmark. | [R-09](https://github.com/masterlifting/mcp-store/issues/70), [R-10](https://github.com/masterlifting/mcp-store/issues/71) |
| K-14 | Evaluation criterion | Prefer minimal single-instance operational complexity, local backup/recovery, portability, open-format export and reuse of mature OSS over bespoke frameworks. | [R-03](https://github.com/masterlifting/mcp-store/issues/64), [R-08](https://github.com/masterlifting/mcp-store/issues/69), [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-15 | Evaluation criterion | For **new owned code**, prefer F# where appropriate and C# when there is an evidence-backed integration advantage. Do not reject an existing OSS component merely because it is written in another language. Native Z#/Rust is unselected. | [R-13](https://github.com/masterlifting/mcp-store/issues/74) |
| K-16 | Evaluation criterion | Use structured, bounded agent contracts with pagination, targeted detail retrieval, observable failures and portable integration; no need to create a custom protocol if standards suffice. | [R-11](https://github.com/masterlifting/mcp-store/issues/72), [R-12](https://github.com/masterlifting/mcp-store/issues/73) |
| K-17 | Open decision | Exact trusted-agent membership, remote-context consent granularity, and any independent local-client authorization boundary. | [R-07](https://github.com/masterlifting/mcp-store/issues/68), [R-12](https://github.com/masterlifting/mcp-store/issues/73) |
| K-18 | Open decision | What source deletion means for derived facts; backup retention, restore-time suppression, and whether retained revisions contain deleted personal values. | [R-08](https://github.com/masterlifting/mcp-store/issues/69) |
| K-19 | Open decision | Required first-version operating systems, offline installation guarantees, UI necessity, storage, sync engine, client protocol and MCP Store/product repository boundary. | [R-13](https://github.com/masterlifting/mcp-store/issues/74), [R-15](https://github.com/masterlifting/mcp-store/issues/75) |

**Interpretation:** K-01–K-03 are current research-process constraints. K-04–K-11 are **candidate** product gates, not verified interview decisions. K-12–K-16 should influence evidence-based comparison. K-17–K-19 must not be silently decided by a researcher.

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

The following are **proposed** checks for R-14 to accept or amend after the private interview is reconciled:

1. A synthetic local fact can be ingested/updated with source provenance, rejected when invalid, and retrieved with stable ordering in repeated fixed-state runs.
2. Existing local knowledge remains queryable with network access physically or programmatically disabled; unsupported remote operations produce explicit errors without corrupting local state.
3. An external source connector produces no mutation against its source API, including during retries or failure recovery.
4. Agent-proposed mutations cannot bypass deterministic validation and an ambiguous entity merge is not silently accepted.
5. Sensitive facts are not placed in a remote-model context implicitly; the disclosure policy and its residual risks are inspectable.
6. User deletion no longer exposes targeted facts in active query surfaces, with documented backup retention/restore limitations.
7. Essential components pass verified OSS license and mandatory-dependency checks; retrieval quality is evaluated against a shared, synthetic multilingual benchmark.

These checks do **not** approve a particular implementation, mandate a full IAM/encryption stack, or imply that source connectors, UI, protocols or the storage engine have been selected.

## Private reconciliation and changes

The original 50-question interview and any sensitive later decisions must be reviewed **outside this public repository**. For each candidate requirement, the owner should record **only** one public disposition: `confirmed`, `revised`, `deferred`, or `not-approved`; publish no personal motivation, examples or source excerpts. A missing interview must remain marked `unverified`, never reconstructed from model memory.

High-impact questions requiring owner confirmation: scope of the trusted agent group; exact remote-data disclosure model; which v1 sources are essential; whether "offline" includes first install; desired deletion/history/backup semantics; and required first-release operating systems. These are gates to final baseline approval, not permission to begin implementation.

All subsequent research documents should reference stable K-IDs and propose public diffs instead of silently reinterpreting the baseline.
