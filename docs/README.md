# Documentation

Start with the [user guide](user-guide.md) to create, organize, publish, run, and inspect workflows. For the current implementation and integration boundaries, read the [architecture guide](architecture.md).

| Document | Purpose |
| --- | --- |
| [User guide](user-guide.md) | Tasks through the actual Studio controls, including recovery and limitations |
| [Architecture](architecture.md) | Implemented components, dependency direction, persistence, runtime ownership, and trust boundaries |
| [Roadmap](roadmap.md) | Completed, active, next, and planned milestones with explicit scope |
| [Implementation history](implementation-history.md) | Detailed historical delivery notes, validation, and limitations |
| [Product design](design.md) | Agreed direction and proposed future capabilities; not a deployment description |
| [Blueprint authoring guidance](blueprint-authoring-guidance.md) | Project-specific principles for adapting Unreal Blueprint authoring concepts |
| [Unreal Blueprint reference](unreal-blueprint-reference.md) | UE 5.7 Blueprint visual-scripting concepts translated into implementation guidance |
| [Exact-action approvals](approvals.md) | Exact MCP action review, nested call-path binding, attribution, controls and process ownership |
| [Conditional graphs](conditional-graphs.md) | Schema 1.4 predicates, typed string ports, exclusive merges, authoring and actual-path evidence |
| [Parallel graphs](parallel-graphs.md) | Schema 1.14 bounded two-branch fork/barrier join, JSON result, concurrency and failure limits |
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
| [Service integration](service-integration.md) | User-owned service hosting, current headless APIs, and planned SDK acceptance criteria |
| [Embedding](embedding.md) | Public Razor components, host services, lifecycle, and theming |
| [Pause and resume](run-control.md) | Cooperative boundaries, controls, audit, process ownership and restart limitations |
| [Workflow API](workflow-api.md) | Local development API and concurrency contracts |
| [Workflow parameters](workflow-parameters.md) | Schema 1.6 string parameters, run overrides, persisted evidence, and current limits |
| [Agent capability libraries](agent-capability-libraries.md) | Implemented text-only skill and Streamable HTTP MCP slice, plus proposed richer agent authoring |
| [Agent profiles and delegation](agent-profiles.md) | Versioned specialist profiles, bounded delegate calls, dependency packaging, and audit scope |
| [Reusable pipelines](reusable-pipelines.md) | Version-pinned expansion, provenance, package validation, and limitations |
| [Graph editor](graph-editor.md) | Canvas controls, layout schema, versioning, and research references |
| [Model providers](model-providers.md) | Opt-in host configuration, official SDK adapter, bounded evidence and live qualification limits |
| [Runtime configuration](runtime-configuration.md) | Providers, node-scoped MCP bindings, and development fixtures |
| [Editor design system](design-system.md) | Theme tokens, typography, visual states, and component boundaries |
| [Agent skills](agent-skills.md) | Contributor skills and their task triggers |
| [Applied skill review](skill-adherence.md) | Concrete improvements and validation evidence |

Documentation changes follow [documentation-maintenance](../.agents/skills/documentation-maintenance/SKILL.md). Diagrams describe the page's stated scope; planned features belong in the product design rather than the current architecture. Validate local links with `python3 scripts/check_docs.py`. For Mermaid validation, install the development-only renderer with `npm ci --prefix tools/documentation`, install the existing browser requirements, and run `python3 scripts/check_docs.py --mermaid` (or add `--browser-executable /path/to/chromium`).

## Architecture decisions

- [Initial graph execution decision](decisions/0001-initial-execution-subset.md) — why the first runtime used a deliberately small, validated graph subset.
- [Execution and data edges decision](decisions/0002-execution-and-data-edges.md) — separates scheduler activations from typed value bindings and defines compatibility for the forthcoming pure-node language.
