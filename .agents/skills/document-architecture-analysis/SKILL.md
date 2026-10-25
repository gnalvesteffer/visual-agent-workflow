---
name: document-architecture-analysis
description: Use when studying a large technical document or product reference to extract its architecture, concepts, capabilities, design philosophy, runtime model, and implementation implications into evidence-grounded project documentation.
---

# Document architecture analysis

Use this skill when a source is a large manual, product reference, specification, book, or collection of technical documents and the goal is to understand how the system is designed rather than merely summarize how to use it. The output should help an implementation agent reproduce the source system's useful capabilities under the project's own constraints.

Read [the analysis template](../../../docs/research/document-analysis-template.md), [project guide](../project-guide/SKILL.md), and [documentation maintenance](../documentation-maintenance/SKILL.md). Add [Blueprint-inspired workflow design](../blueprint-workflow-design/SKILL.md) when the source concerns visual workflow authoring, typed graphs, reusable nodes, agent configuration, or related concepts.

## Establish evidence boundaries

Separate every conclusion into one of three categories:

- **Source fact:** directly supported by a named source section, page, code path, screenshot, or tested behavior.
- **Interpretation:** a reasoned explanation of why the source may use a pattern or what capability the pattern enables.
- **Project decision:** an adaptation chosen for Visual Agent Workflow, including deliberate differences from the source.

Do not present a project interpretation as a vendor guarantee. Do not infer runtime semantics from an editor screenshot, layout convention, or tutorial sequence alone. Record uncertainty and conflicting source statements instead of silently choosing one.

## Analyze the system in layers

Extract findings in this order, adapting the depth to the source:

1. **Purpose and users:** what problems the system solves, who authors content, and what kinds of artifacts they create.
2. **Mental model:** the nouns, verbs, boundaries, and abstraction levels users are expected to understand.
3. **Asset and composition model:** reusable units, ownership, identity, inheritance, instancing, libraries, and versioning.
4. **Type and data model:** values, references, schemas, containers, defaults, conversions, nullability, and serialization.
5. **Execution model:** events, dataflow, control flow, scheduling, concurrency, latent work, retries, cancellation, and terminal states.
6. **Extension and communication model:** functions, macros, interfaces, dispatchers, plugins, host capabilities, and external effects.
7. **Authoring model:** discovery, validation, graph navigation, organization, undo, compile/save/publish lifecycle, and error recovery.
8. **Runtime and evidence model:** state, persistence, debugging, observability, replay, determinism, and provenance.
9. **Quality and boundaries:** performance, limits, security, trust boundaries, unsupported cases, and failure modes.

For each layer, identify the source concepts, the capability they provide, the invariants an implementation must preserve, and the safe project adaptation.

## Produce implementation-useful documentation

Prefer capability matrices, concept mappings, lifecycle diagrams, state tables, and short examples that expose contracts. Explain what must be true for a feature to be complete: schema, validation, execution, persistence, UI, evidence, and tests. Call out where a visually similar feature would have different semantics in this project.

For visual systems, distinguish executable semantics from presentation. Record stable node/port identities, typed edges, graph scope, layout-only metadata, and the rule that screen position never schedules runtime work. For agent systems, distinguish definition defaults, per-run inputs, run state, host capabilities, approvals, credentials, and recorded evidence.

When the document is large, use targeted extraction and search rather than loading the entire source into context. Preserve a source map of section titles and local page or line references. Keep raw extracted text and generated caches out of the repository unless the project explicitly needs them as fixtures.

## Apply the findings

Translate the source into the project's vocabulary only after the source model is clear. Link the detailed analysis from `docs/README.md`. Update architecture or user-guide documents only when the analysis changes a shipped contract; keep research, future design, and current implementation claims visibly separate. Use the existing project skills for security, runtime, compatibility, graph experience, and audit concerns when the source findings touch those areas.

Validate local links and Mermaid diagrams. If the analysis produces a proposed capability, identify its contract, trust boundary, persistence behavior, failure semantics, and verification plan before describing it as implementation work.
