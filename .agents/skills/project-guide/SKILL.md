---
name: project-guide
description: Use when changing, reviewing, or explaining this project, its architecture, implementation plans, or package boundaries.
---

# Project guide

Read [the design baseline](../../../docs/design.md), [roadmap and current scope](../../../docs/roadmap.md), and [README](../../../README.md). Inspect the actual tree before naming projects, commands, or APIs. Core supports conditional graphs, one schema-1.17 bounded linear-body loop, one bounded schema-1.14+ two-branch fork/barrier block with JSON output, schema-1.16 recursive JSON projection contracts with bounded arrays, a schema-1.9 JSON input-to-string-selector path, schema-1.5 exact-action MCP approvals, and schema-1.6+ workflow parameters with explicit getter nodes. Nested loops and nested/unbounded parallelism remain planned; see [workflow parameter contracts](../../../docs/workflow-parameters.md) and [parallel graph semantics](../../../docs/parallel-graphs.md).

## Preserve the architecture

- Treat the engine as a first-class library for user-owned services; Studio is an optional IDE. Engine embedding and designer embedding are independent. Do not introduce a mandatory Studio process, Blazor dependency, or authoring catalog into service execution. See [service integration](../../../docs/service-integration.md).
- Keep runtime execution independent of browser connections and Blazor circuits. The UI edits definitions and observes or controls backend-owned runs.
- Build embeddable Razor components first; the standalone studio must consume the same public APIs. Keep authorization, persistence, and backend access host-configurable.
- Isolate Microsoft Agent Framework APIs behind an adapter. Use [microsoft-agent-framework](../microsoft-agent-framework/SKILL.md) for version-specific API research, package selection, integration, and upgrades; distinguish confirmed API support from proposed mappings.
- User authoring is no-code visual composition. MCP supplies external operations; approved .NET extensions supply specialized behavior. Do not add general C# scripting or arbitrary URL tools by default.
- Apply [Blueprint-inspired workflow design](../blueprint-workflow-design/SKILL.md) when shaping graph interactions, variables/parameters, agent settings, and reusable workflows. Borrow typed, exposed variables and readable graphs without importing Unreal's execution semantics.
- Keep agent prompts role-separated. User-message interpolation is built from structured literal/input/parameter parts; any text editor syntax is only a codec over those stable references, never an expression language. Caller input and prior node output must not be elevated into system instructions implicitly.
- Keep published workflows and dependencies immutable. A run references its exact revision. Playback reconstructs recorded evidence without execution.
- Definitions use platform-owned JSON contracts, not CLR type names, framework instances, or canvas-library models.

## Work from evidence

Before selecting dependencies or implementing a public contract, identify the smallest representative proof and its failure cases. Record unresolved choices and compatibility tradeoffs in the design document or a focused linked decision document. Do not turn illustrative package names or JSON fragments into assumed supported APIs.

Use the [catalog](../../../docs/agent-skills.md) to select the relevant review. Update README and design documentation when behavior or architectural decisions change. Keep proposed work distinct from implemented and tested behavior.

For stylesheet work, read the vendored [CSS skill](../css/SKILL.md) and its [local integration guidance](../css/SOURCE.md), then use [graph-experience-review](../graph-experience-review/SKILL.md) for actual UI validation. Keep library styles and tokens scoped so embedding does not alter the host application's global styles.

For Razor/component work, read the vendored [Blazor skill](../blazor/SKILL.md) and its [.NET 10 project compatibility notes](../blazor/SOURCE.md). Preserve host-independent embedding and backend-owned execution; use verified framework APIs rather than assuming generic upstream examples match this repository's SDK.

For C# changes, read [modern C# coding standards](../modern-csharp-coding-standards/SKILL.md) with its [local notes](../modern-csharp-coding-standards/SOURCE.md), and [clean code](../clean-code/SKILL.md) with its [local notes](../clean-code/SOURCE.md). Write code that reads declaratively; follow the shared [declarative readability guidance](../clean-code/declarative-code.md), keeping effects and execution ordering explicit. For public contracts use [API design](../api-design/SKILL.md) and its [compatibility notes](../api-design/SOURCE.md). For dependency/build architecture use [.NET project structure](../dotnet-project-structure/SKILL.md) and its [local notes](../dotnet-project-structure/SOURCE.md). Keep package versions in `Directory.Packages.props`; architectural boundary tests guard inward dependencies. Upstream suggestions do not mandate unrelated frameworks, publishing, unsafe optimizations, or breaking contract rewrites.

For visual design or theme work, read [frontend-design](../frontend-design/SKILL.md) and [design-systems](../design-systems/SKILL.md) with their local SOURCE.md notes. Follow the [editor design system](../../../docs/design-system.md), preserve host overrides, and review real screenshots plus functional controls.

For focused interface work, also load the applicable specialist skill: [accessibility](../better-accessibility/SKILL.md), [colors](../better-colors/SKILL.md), [layout](../better-layout/SKILL.md), [typography](../better-typography/SKILL.md), [UI polish](../better-ui/SKILL.md), or [interface writing](../better-writing/SKILL.md). These upstream skills are vendored under MIT; see [provenance and license](../_third-party/jakubkrehel-skills/PROVENANCE.md). Keep their references beside each skill and adapt conflicts through project-specific guidance here.

Maintain docs with [documentation-maintenance](../documentation-maintenance/SKILL.md). Use the [documentation map](../../../docs/README.md), [current architecture](../../../docs/architecture.md), and [user guide](../../../docs/user-guide.md), with [technical-writing](../technical-writing/SKILL.md) and [mermaid-diagrams](../mermaid-diagrams/SKILL.md) plus their SOURCE.md notes when relevant.

For focused C# unit tests, use [unit testing](../unit-testing/SKILL.md). For verification planning and regression work, use [quality assurance](../quality-assurance/SKILL.md). For browser automation use [webapp testing](../webapp-testing/SKILL.md) with its [local guidance](../webapp-testing/SOURCE.md), reusing the existing Playwright suite.

For local runtime DI use `AddLocalWorkflowRuntime`; Studio and the standalone service sample share that registration. It creates no catalog or grants and preserves existing services. Verify hosted-worker/client identity and shutdown behavior. This helper is not a production durability or published NuGet contract; see [local hosting API](../../../docs/hosting-sdk.md).

Consumer observation uses Core's read-only `IWorkflowRunObserver`, a host-policy-protected opt-in route group, a separate Core-only HTTP client, and the reusable `WorkflowObservationPanel`. Studio connects only to operator-configured sources; browser users cannot supply server-side URLs or credentials. Preserve this separation from authoring and execution control. See [consumer observation](../../../docs/observation.md).

For routing use explicit port IDs and validated `GraphEditing` operations. `WorkflowGraph.OrderedNodes` is topological display/analysis order, never a runtime schedule. Keep linear-only editing/reuse guarded so routed graphs cannot be flattened. Use `ParallelBlockEditing` to preview/remove a whole parallel region. Loops are a single validated feedback region; use `GraphEditing.InsertBoundedLoopAfter` / `RemoveNode`, and never treat topological order as a runtime schedule. See [bounded loop semantics](../../../docs/loop-graphs.md). Read [conditional graph semantics](../../../docs/conditional-graphs.md) and [parallel graph semantics](../../../docs/parallel-graphs.md).

Approvals guard the exact next explicit MCP invocation, not an entire agent loop or host permission grant. Use optional approval client/gate capabilities; retain read-only observation and historical playback. Awaiting approvals release execution ownership but not admission capacity. See [approval contracts](../../../docs/approvals.md).

For run persistence use Hosting's local synchronous `IWorkflowRunStore`, preserving Core/observer contracts. `AddLocalWorkflowRuntime` honors an explicit store; pair custom stores with `JournalRunObserver.FromStore`. Preserve old file/runtime constructors, legacy history reads, atomic batches and expected-sequence conflicts. SQLite is evidence persistence, not a lease or checkpoint-recovery implementation. See [run storage](../../../docs/run-storage.md).

The opt-in `CheckpointWorkflowHost` admits deterministic-only native saved suspensions with exact checkpoint/cursor/version admission. It is separate from Studio's queue; do not start an ordinary local runtime merely to observe its database. Built-in hosts acquire an exclusive local owner handle before interruption/execution. See [checkpoint recovery](../../../docs/checkpoint-recovery.md).

Use Core's optional `IWorkflowRunReconciliationObserver` for metadata-only recovery disposition. Built-in journal observers support it; SQLite pairs evidence/frame reads in one transaction. The embedded assessment must match snapshot identity/cursor/status, remain independent of playback, and suppress stale eligibility on source failure. No reconciliation decision or execution belongs in the observation API. See [run reconciliation](../../../docs/run-reconciliation.md).
