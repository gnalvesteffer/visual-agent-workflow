# Documentation

Start with the [user guide](user-guide.md) to create, organize, publish, run, and inspect workflows. For the current implementation and integration boundaries, read the [architecture guide](architecture.md).

| Document | Purpose |
| --- | --- |
| [User guide](user-guide.md) | Tasks through the actual Studio controls, including recovery and limitations |
| [Architecture](architecture.md) | Implemented components, dependency direction, persistence, runtime ownership, and trust boundaries |
| [Roadmap](roadmap.md) | Completed, active, next, and planned milestones with explicit scope |
| [Implementation history](implementation-history.md) | Detailed historical delivery notes, validation, and limitations |
| [Product design](design.md) | Agreed direction and proposed future capabilities; not a deployment description |
| [Project folders and workspaces](project-workspaces.md) | Folder-backed authoring, source transport, local restore points, selected `.vawpkg` releases, and catalog cutover |
| [Blueprint authoring guidance](blueprint-authoring-guidance.md) | Project-specific principles for adapting Unreal Blueprint authoring concepts |
| [Unreal Blueprint reference](unreal-blueprint-reference.md) | UE 5.7 Blueprint visual-scripting concepts translated into implementation guidance |
| [Blueprint capability matrix](blueprint-capability-matrix.md) | Audited comparison of Blueprint capability targets, shipped workflow behavior, limits, and next proofs |
| [Blueprint contract inventory](blueprint-contract-inventory.md) | Stable identity, portability, host authority, evidence, and compatibility boundaries for Blueprint-inspired constructs |
| [Exact-action approvals](approvals.md) | Exact MCP action review, nested call-path binding, attribution, controls and process ownership |
| [Conditional graphs](conditional-graphs.md) | Schema 1.4 predicates, typed string ports, exclusive merges, authoring and actual-path evidence |
| [Parallel graphs](parallel-graphs.md) | Schema 1.14–1.35 parallel regions, JSON joins, collaboration, branch-local routing, concurrency and failure limits |
| [Parallel agent collaboration](parallel-agent-collaboration.md) | Schema-1.33 contract for one branch-scoped rendezvous, snapshots, execution, audit, playback, and authoring |
| [Two-phase parallel Agent collaboration](parallel-agent-collaboration-phases.md) | Schema-1.35 data layout, ordered barriers, phase/role grants, bounds, and compatibility contract |
| [Branch-local parallel routing](parallel-branch-routing.md) | Schema 1.34 Boolean routes inside static 1.32 fork branches, validation, evidence, and explicit 1.33 collaboration deferral |
| [Bounded loop graphs](loop-graphs.md) | Schema 1.17 single-loop body, predicate, hard iteration cap, audit events, and authoring limits |
| [Workflow termination](workflow-termination.md) | Schema 1.18 successful no-result exits and runtime/audit behavior |
| [Nested workflow calls](nested-workflows.md) | Schema 1.19 pinned child workflows, scoped approvals/audit events, limits, and authoring behavior |
| [Consumer observation](observation.md) | Opt-in authorized consumer endpoint, remote Studio attachment and reusable read-only graph |
| [Run reconciliation](run-reconciliation.md) | Read-only recovery dispositions, unsettled work, call correlation and observation UI |
| [Checkpoint recovery](checkpoint-recovery.md) | Opt-in deterministic native checkpoints, saved-suspension recovery and exclusive local ownership |
| [Run storage](run-storage.md) | Atomic batches, expected-sequence conflicts, file compatibility and SQLite persistence |
| [Local hosting API](hosting-sdk.md) | DI registration, host ownership, current project-reference consumption and future package-release boundaries |
| [Containerized MCP workers](extension-workers.md) | Digest-pinned host registration, no-network container limits, trust assumptions, and Linux isolation proof |
| [Isolated .NET extensions](extension-assemblies.md) | Extension interface, version/hash pinning, typed MCP operations, and host capability context |
| [Service integration](service-integration.md) | User-owned service hosting, selected workflow bundles, current headless APIs, and planned SDK acceptance criteria |
| [Embedding](embedding.md) | Public Razor components, host services, lifecycle, and theming |
| [Pause and resume](run-control.md) | Cooperative boundaries, controls, audit, process ownership and restart limitations |
| [Workflow API](workflow-api.md) | Local development API and concurrency contracts |
| [Workflow parameters](workflow-parameters.md) | Schema 1.6 string parameters, run overrides, persisted evidence, and current limits |
| [Agent capability libraries](agent-capability-libraries.md) | Implemented text-only skills and Streamable HTTP/stdio MCP connections, plus richer agent authoring |
| [Agent profiles and delegation](agent-profiles.md) | Versioned specialist profiles, bounded delegate calls, dependency packaging, and audit scope |
| [Reusable workflows](reusable-workflows.md) | Version-pinned expansion, provenance, package validation, and limitations |
| [Graph editor](graph-editor.md) | Canvas controls, layout schema, versioning, and research references |
| [Graph values and functions](graph-values.md) | Typed deterministic values, pure graph function constraints, and future visual-language design |
| [Graph values and functions](graph-values.md) | Typed deterministic values, pure graph function constraints, and future visual-language design |
| [Model providers](model-providers.md) | Opt-in host configuration, official SDK adapter, bounded evidence and live qualification limits |
| [Runtime configuration](runtime-configuration.md) | Providers, node-scoped MCP bindings, and development fixtures |
| [Editor design system](design-system.md) | Theme tokens, typography, visual states, and component boundaries |
| [Agent skills](agent-skills.md) | Contributor skills and their task triggers |
| [Applied skill review](skill-adherence.md) | Concrete improvements and validation evidence |

Documentation changes follow [documentation-maintenance](../.agents/skills/documentation-maintenance/SKILL.md). Diagrams describe the page's stated scope; planned features belong in the product design rather than the current architecture. Validate local links with `python3 scripts/check_docs.py`. For Mermaid validation, install the development-only renderer with `npm ci --prefix tools/documentation`, install the existing browser requirements, and run `python3 scripts/check_docs.py --mermaid` (or add `--browser-executable /path/to/chromium`).

## Architecture decisions

- [Initial graph execution decision](decisions/0001-initial-execution-subset.md) — why the first runtime used a deliberately small, validated graph subset.
- [Execution and data edges decision](decisions/0002-execution-and-data-edges.md) — separates scheduler activations from typed value bindings and defines compatibility for the forthcoming pure-node language.
- [Bounded Sequence dispatch boundary](decisions/0003-bounded-sequence-dispatch-boundary.md) — defers a Blueprint-style Sequence node until scheduler and playback semantics are proven.
- [Sequence-to-Agent path proof gate](decisions/0011-sequence-agent-path-proof.md) — records the non-public Agent descendant proof and the gate for a future schema 1.44.
- [Dynamic pin descriptor resolution](decisions/0012-dynamic-pin-descriptor-resolution.md) — defines the schema-neutral Core projection for configured dynamic pins and transient MCP discovery.
- [Static descriptor validation boundary](decisions/0013-static-descriptor-validation-boundary.md) — validates registered static descriptor metadata without making dynamic authoring projections an admission contract.
- [Schema capability catalog](decisions/0014-schema-capability-catalog.md) — centralizes exact supported schema identities and selected feature flags without claiming migration support.
- [Schema promotion inventory](decisions/0015-schema-promotion-inventory.md) — records individually named, non-executable promotion operations without implying composable migration.
- [Dynamic descriptor conformance](decisions/0016-dynamic-descriptor-conformance.md) — applies schema-neutral pin metadata checks to resolved dynamic node projections without making discovery or incomplete drafts executable.
- [Static Boolean Branch descriptor](decisions/0017-static-boolean-branch-descriptor.md) — routes the typed Boolean Branch pin projections through one canonical static descriptor without changing schema admission or runtime behavior.
- [Explicit conversion insertion](decisions/0018-explicit-conversion-insertion.md) — bounds the opt-in schema-1.39 string-to-integer insertion action to unconnected typed pins.
- [Node capability projection](decisions/0019-node-capability-projection.md) — a versioned, non-serialized composition of canonical node contracts and resolved pins.
- [MCP schema descriptor](decisions/0020-mcp-schema-descriptor.md) — exposes bounded normalized tool-schema metadata without changing workflow pins or runtime authority.
- [Sequence Agent topology analyzer](decisions/0021-sequence-agent-topology-analyzer.md) — analyzes a future bounded terminal-Agent path without promoting schema 1.44 or changing runtime admission.
- [Node effect and authoring metadata](decisions/0022-node-effect-and-authoring-metadata.md) — adds descriptive, non-serialized effect and editor-capability metadata to the canonical node catalog.
- [Pure graph-template proof seam](decisions/0023-pure-graph-template-proof.md) — bounds authoring-only pure template extraction and expansion before Studio exposure.
