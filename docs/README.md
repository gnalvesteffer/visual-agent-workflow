# Documentation

Start with the [user guide](user-guide.md) to create, organize, publish, run, and inspect workflows. For the current implementation and integration boundaries, read the [architecture guide](architecture.md).

| Document | Purpose |
| --- | --- |
| [User guide](user-guide.md) | Tasks through the actual Studio controls, including recovery and limitations |
| [Architecture](architecture.md) | Implemented components, dependency direction, persistence, runtime ownership, and trust boundaries |
| [Roadmap](roadmap.md) | Completed, active, next, and planned milestones with explicit scope |
| [Implementation history](implementation-history.md) | Detailed historical delivery notes, validation, and limitations |
| [Product design](design.md) | Agreed direction and proposed future capabilities; not a deployment description |
| [Service integration](service-integration.md) | User-owned service hosting, current headless APIs, and planned SDK acceptance criteria |
| [Embedding](embedding.md) | Public Razor components, host services, lifecycle, and theming |
| [Workflow API](workflow-api.md) | Local development API and concurrency contracts |
| [Reusable pipelines](reusable-pipelines.md) | Version-pinned expansion, provenance, package validation, and limitations |
| [Graph editor](graph-editor.md) | Canvas controls, layout schema, versioning, and research references |
| [Runtime configuration](runtime-configuration.md) | Providers, node-scoped MCP bindings, and development fixtures |
| [Editor design system](design-system.md) | Theme tokens, typography, visual states, and component boundaries |
| [Agent skills](agent-skills.md) | Contributor skills and their task triggers |
| [Applied skill review](skill-adherence.md) | Concrete improvements and validation evidence |

Documentation changes follow [documentation-maintenance](../.agents/skills/documentation-maintenance/SKILL.md). Diagrams describe the page's stated scope; planned features belong in the product design rather than the current architecture. Validate local links with `python3 scripts/check_docs.py`. For Mermaid validation, install the development-only renderer with `npm ci --prefix tools/documentation`, install the existing browser requirements, and run `python3 scripts/check_docs.py --mermaid` (or add `--browser-executable /path/to/chromium`).
