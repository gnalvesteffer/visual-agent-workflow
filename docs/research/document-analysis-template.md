# Technical document architecture analysis template

Use this template for a large product manual, technical reference, specification, book, or multi-document source. It is an analysis scaffold, not a requirement to fill every field mechanically.

## Source record

| Field | Value |
| --- | --- |
| Source title |  |
| Version/date |  |
| Local path or URL |  |
| Sections/pages inspected |  |
| Extraction method |  |
| Analyst/date |  |
| Known source limitations |  |

## Evidence rules

Mark each finding as one of:

| Mark | Meaning |
| --- | --- |
| Source fact | Directly supported by the source. Include a section/page/line reference. |
| Interpretation | Reasoned explanation or synthesis. State the evidence that supports it. |
| Project decision | Deliberate adaptation for this repository. Link the owning design or contract. |

## System purpose and mental model

Describe the users, authoring goals, primary artifacts, and the nouns and verbs the source uses. Identify the abstraction ladder: what users build directly, what they reuse, and what the runtime executes.

## Capability inventory

| Capability | Source concept | User value | Required contract | Project adaptation | Evidence |
| --- | --- | --- | --- | --- | --- |
|  |  |  |  |  |  |

## Architecture and lifecycle

Describe asset creation, editing, validation/compile, save/publish, instancing, execution, debugging, persistence, and replay. Use a Mermaid lifecycle or sequence diagram when it clarifies ownership and state transitions.

## Data and type model

Record primitive types, structured types, collections, references, defaults, conversions, nullability, schema evolution, and serialization. Identify which values are definition-scoped, invocation-scoped, run-scoped, host-owned, or presentation-only.

## Execution model

Document entry points, execution edges, data edges, branch semantics, loops, concurrency, waits, external effects, retries, cancellation, failure, and terminal states. Explicitly distinguish documented behavior from inferred behavior.

## Reuse and communication

Compare functions, macros/templates, libraries, interfaces, inheritance/specialization, events/dispatchers, and child workflows. Record identity, versioning, scope, side effects, override rules, and debugging/provenance behavior.

## Authoring and debugging model

Describe discovery, context-sensitive creation, graph navigation, grouping, annotations, undo, validation feedback, breakpoints, watches, traces, call stacks, and evidence playback. State which interactions are presentation-only.

## Limits and quality attributes

Record performance guidance, resource budgets, security/trust boundaries, deterministic portions, unsupported cases, error modes, and operational assumptions.

## Open questions and verification plan

| Question or uncertainty | Why it matters | Evidence needed | Verification method | Owner/status |
| --- | --- | --- | --- | --- |
|  |  |  |  |  |

## Implementation handoff

Summarize the minimum contracts an implementation agent must preserve, the capabilities that are in scope, the deliberate deviations from the source, the documents that own each decision, and the tests or authoring flows required before the capability can be called complete.
