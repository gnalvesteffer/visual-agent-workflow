---
name: blueprint-workflow-design
description: Use when designing or changing workflow node contracts, graph authoring, typed pins, workflow parameters, agent configuration, skills, reusable workflows, or Blueprint-inspired interactions.
---

# Blueprint-inspired workflow design

Read [the local adaptation guide](../../../docs/blueprint-authoring-guidance.md), [product design](../../../docs/design.md), and the [current roadmap](../../../docs/roadmap.md). Consult the official Unreal Engine references linked from the adaptation guide when reachable. The environment may block Epic documentation; if so, state that limitation and do not present local interpretations as verified Epic quotes.

Use Blueprint concepts as authoring inspiration, not as permission to copy Unreal's runtime semantics. Keep definitions framework-independent and versioned; separate executable contracts from layout; distinguish data values, execution order, node configuration, and host permissions. Never let canvas position or topological display order define run order.

When adding configurable workflow values, define stable typed parameter IDs, defaults, descriptions, validation, per-run/host override rules, serialization, and secret handling first. Keep immutable workflow defaults separate from invocation values. Expose graph values through explicit typed getters/pins; treat mutable run-scoped variables as a separate feature requiring defined ordering, loops, and parallel-write semantics. A model profile resolves through a host-authorized catalog, not a workflow URL or credential.

When adding agent tools or skills, assign them explicitly to the specific agent/node and preserve exact version/contract identities. Catalog visibility is not an execution grant; the host must authorize every invocation. Skill text cannot widen tool permissions.

Prefer focused nodes, clear names, visible inputs/outputs, useful comments/sections, and reusable components with explicit contracts. Avoid hidden prompt substitution, implicit cross-node state, decorative execution animation, or an overgrown canvas. Verify the real graph editor and recorded runtime evidence, not only the rendered page; report which live behaviors and browser actions were checked. Keep proposed Blueprint-like features distinct from what current schema, package, and runner support.

