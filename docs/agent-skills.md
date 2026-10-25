# Repository agent skills

Development-agent guidance lives in `.agents/skills/`, routed by [AGENTS.md](../AGENTS.md). These files guide contributors and coding assistants. They are separate from the versioned skills that product users will attach to workflow agents.

The structure follows `gnalvesteffer/ai-newspaper`: a project guide plus focused, task-triggered workflows. Instructions are adapted to this project; newspaper-specific rules and review/delegation requirements are not inherited.

| Skill | Use when |
| --- | --- |
| [Documentation maintenance](../.agents/skills/documentation-maintenance/SKILL.md) | Behavior/architecture/API/UI changes requiring current architecture, user-guide, link, and Mermaid updates |
| [Technical writing](../.agents/skills/technical-writing/SKILL.md) | Clear developer documentation and explanations; see [local guidance and provenance](../.agents/skills/technical-writing/SOURCE.md) |
| [Document architecture analysis](../.agents/skills/document-architecture-analysis/SKILL.md) | Extract architecture, capabilities, design philosophy, runtime models, evidence, and implementation implications from large technical documents |
| [Mermaid diagrams](../.agents/skills/mermaid-diagrams/SKILL.md) | Architecture, lifecycle, sequence, and user-flow visualizations; see [local guidance and provenance](../.agents/skills/mermaid-diagrams/SOURCE.md) |
| [Project guide](../.agents/skills/project-guide/SKILL.md) | Changing architecture, planning implementation, or orienting in the repository |
| [Microsoft Agent Framework](../.agents/skills/microsoft-agent-framework/SKILL.md) | Selecting/upgrading .NET framework packages, implementing the adapter, or verifying agent/workflow/tool APIs |
| [Blazor](../.agents/skills/blazor/SKILL.md) | Building or reviewing Razor components, lifecycle, binding, rendering, state, and UI tests; read the [project compatibility notes and license](../.agents/skills/blazor/SOURCE.md) too |
| [Modern C# coding standards](../.agents/skills/modern-csharp-coding-standards/SKILL.md) | C# implementation, declarative readability, async/cancellation, immutable contracts, composition; read [local notes](../.agents/skills/modern-csharp-coding-standards/SOURCE.md) |
| [API design](../.agents/skills/api-design/SKILL.md) | Public APIs and source/binary/wire compatibility; read [local notes](../.agents/skills/api-design/SOURCE.md) |
| [.NET project structure](../.agents/skills/dotnet-project-structure/SKILL.md) | Solution layering, SDK/package pins, central build configuration; read [local notes](../.agents/skills/dotnet-project-structure/SOURCE.md) |
| [Clean code](../.agents/skills/clean-code/SKILL.md) | Declarative intent, naming, responsibilities, duplication, and avoiding unnecessary abstraction; read [local guidance and provenance](../.agents/skills/clean-code/SOURCE.md) |
| [Quality assurance](../.agents/skills/quality-assurance/SKILL.md) | Risk-based test selection, deterministic concurrency checks, integration boundaries, and honest verification evidence |
| [Unit testing](../.agents/skills/unit-testing/SKILL.md) | Focused C# unit-test design, readability, test doubles, determinism, and the repository's test boundaries |
| [Webapp testing](../.agents/skills/webapp-testing/SKILL.md) | Playwright browser testing, rendered controls, screenshots and browser logs; read [local guidance and pinned source](../.agents/skills/webapp-testing/SOURCE.md) |
| [Run and validate](../.agents/skills/run-and-validate/SKILL.md) | Adding executable projects, starting services, testing, or diagnosing failures |
| [Capability security review](../.agents/skills/capability-security-review/SKILL.md) | Changing MCP grants, credentials, authorization, extension workers, or imports |
| [Workflow runtime review](../.agents/skills/workflow-runtime-review/SKILL.md) | Changing Agent Framework integration, scheduling, recovery, approvals, or external effects |
| [Audit and playback review](../.agents/skills/audit-playback-review/SKILL.md) | Changing events, audit records, payload capture, reconnect, or historical playback |
| [Graph experience review](../.agents/skills/graph-experience-review/SKILL.md) | Changing visual authoring, live observation, accessibility, or component embedding |
| [Blueprint-inspired workflow design](../.agents/skills/blueprint-workflow-design/SKILL.md) | Changing node contracts, typed pins, parameters, agent configuration, reusable workflows, or Blueprint-inspired interactions |
| [Frontend design](../.agents/skills/frontend-design/SKILL.md) | Visual direction, typography, layout, and screenshot critique; see [local notes and license](../.agents/skills/frontend-design/SOURCE.md) |
| [Design systems](../.agents/skills/design-systems/SKILL.md) | Theme tokens, component states, visual consistency, and embedding; see [local notes and license](../.agents/skills/design-systems/SOURCE.md) |
| [CSS](../.agents/skills/css/SKILL.md) | Writing or reviewing CSS layout, responsive behavior, typography, theming, specificity, or stylesheet organization; see [source and license](../.agents/skills/css/SOURCE.md) |
| [Accessibility](../.agents/skills/better-accessibility/SKILL.md) | WCAG-based keyboard, focus, forms, screen-reader, motion, and zoom review; see [upstream provenance](../.agents/skills/_third-party/jakubkrehel-skills/PROVENANCE.md) |
| [Color systems](../.agents/skills/better-colors/SKILL.md) | Palette structure, semantic color tokens, color usage, and measured contrast |
| [Responsive layout](../.agents/skills/better-layout/SKILL.md) | Grouping, alignment, reading order, responsive behavior, and overflow review |
| [Typography](../.agents/skills/better-typography/SKILL.md) | Type scale, font rendering, line spacing, wrapping, and truncation |
| [UI polish](../.agents/skills/better-ui/SKILL.md) | Surfaces, shadows, icons, state transitions, and motion details |
| [Interface writing](../.agents/skills/better-writing/SKILL.md) | Labels, errors, empty states, confirmations, and concise product language |
| [Version and compatibility review](../.agents/skills/version-compatibility-review/SKILL.md) | Changing definitions, prefabs, schemas, imports, collaboration, dependencies, or releases |

For repository-authored skills, keep `SKILL.md` frontmatter limited to a stable `name` and a descriptive `description` identifying when to use it. Preserve valid upstream metadata in vendored skills, along with their license and pinned provenance. Add scripts only when they exercise a real implemented workflow and have been tested. Update the catalog when skills are added or renamed.
