---
name: blueprint-workflow-design
description: Use when designing or changing workflow node contracts, graph authoring, typed pins, workflow parameters, agent configuration, skills, reusable workflows, or Blueprint-inspired interactions.
---

# Blueprint-inspired workflow design

Read the [Unreal Blueprint design reference](../../../docs/unreal-blueprint-reference.md), [local adaptation guide](../../../docs/blueprint-authoring-guidance.md), [product design](../../../docs/design.md), and the [current roadmap](../../../docs/roadmap.md). The reference distinguishes summarized Unreal source concepts from Visual Agent Workflow recommendations; preserve that distinction and report its source scope and confidence accurately.

Use the standalone [Blueprint design reference](../../../docs/unreal-blueprint-reference.md) for source concepts, adaptation boundaries, and representative acceptance scenarios. Its capability targets do not override the current schema or roadmap limits.

Use Blueprint concepts as authoring inspiration, not as permission to copy Unreal's runtime semantics. Keep definitions framework-independent and versioned; separate executable contracts from layout; distinguish data values, execution order, node configuration, and host permissions. Never let canvas position or topological display order define run order.

When adding configurable workflow values, define stable typed parameter IDs, defaults, descriptions, validation, per-run/host override rules, serialization, and secret handling first. Keep immutable workflow defaults separate from invocation values. Expose graph values through explicit typed getters/pins; treat mutable run-scoped variables as a separate feature requiring defined ordering, loops, and parallel-write semantics. A model profile resolves through a host-authorized catalog, not a workflow URL or credential.

When adding agent tools or skills, assign them explicitly to the specific agent/node and preserve exact version/contract identities. Catalog visibility is not an execution grant; the host must authorize every invocation. Skill text cannot widen tool permissions.

Prefer focused nodes, clear names, visible inputs/outputs, useful comments/sections, and reusable components with explicit contracts. Avoid hidden prompt substitution, implicit cross-node state, decorative execution animation, or an overgrown canvas. Verify the real graph editor and recorded runtime evidence, not only the rendered page; report which live behaviors and browser actions were checked. Keep proposed Blueprint-like features distinct from what current schema, package, and runner support.


For conditional-block edits, use `ConditionalBlockEditing` to preview and remove a single-entry/single-exit region. A matching merge must lie on every downstream route; reachability from both arms alone is insufficient. Reject outside incoming paths, list all affected nodes before confirmation, and commit definition/layout as one undo unit. Verify nested and sequential boundaries plus shared-merge rejection.

For the bounded parallel region, use explicit named branch pins and anchors and `ParallelBlockEditing` to preview/remove the full fork-to-join region. Do not infer concurrent execution from node placement. Verify Agent Framework fan-out/barrier behavior, concurrent branch calls, exact result payloads and evidence; document that completed external side effects cannot be rolled back if another branch fails. The current contract excludes nested forks, loops and conditional routing inside parallel branches.
