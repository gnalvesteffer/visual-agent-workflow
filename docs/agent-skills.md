# Repository agent skills

Development-agent guidance lives in `.agents/skills/`, routed by [AGENTS.md](../AGENTS.md). These files guide contributors and coding assistants. They are separate from the versioned skills that product users will attach to workflow agents.

The structure follows `gnalvesteffer/ai-newspaper`: a project guide plus focused, task-triggered workflows. Instructions are adapted to this project; newspaper-specific rules and review/delegation requirements are not inherited.

| Skill | Use when |
| --- | --- |
| [Project guide](../.agents/skills/project-guide/SKILL.md) | Changing architecture, planning implementation, or orienting in the repository |
| [Microsoft Agent Framework](../.agents/skills/microsoft-agent-framework/SKILL.md) | Selecting/upgrading .NET framework packages, implementing the adapter, or verifying agent/workflow/tool APIs |
| [Blazor](../.agents/skills/blazor/SKILL.md) | Building or reviewing Razor components, lifecycle, binding, rendering, state, and UI tests; read the [project compatibility notes and license](../.agents/skills/blazor/SOURCE.md) too |
| [Run and validate](../.agents/skills/run-and-validate/SKILL.md) | Adding executable projects, starting services, testing, or diagnosing failures |
| [Capability security review](../.agents/skills/capability-security-review/SKILL.md) | Changing MCP grants, credentials, authorization, extension workers, or imports |
| [Workflow runtime review](../.agents/skills/workflow-runtime-review/SKILL.md) | Changing Agent Framework integration, scheduling, recovery, approvals, or external effects |
| [Audit and playback review](../.agents/skills/audit-playback-review/SKILL.md) | Changing events, audit records, payload capture, reconnect, or historical playback |
| [Graph experience review](../.agents/skills/graph-experience-review/SKILL.md) | Changing visual authoring, live observation, accessibility, or component embedding |
| [CSS](../.agents/skills/css/SKILL.md) | Writing or reviewing CSS layout, responsive behavior, typography, theming, specificity, or stylesheet organization; see [source and license](../.agents/skills/css/SOURCE.md) |
| [Version and compatibility review](../.agents/skills/version-compatibility-review/SKILL.md) | Changing definitions, prefabs, schemas, imports, collaboration, dependencies, or releases |

For repository-authored skills, keep `SKILL.md` frontmatter limited to a stable `name` and a descriptive `description` identifying when to use it. Preserve valid upstream metadata in vendored skills, along with their license and pinned provenance. Add scripts only when they exercise a real implemented workflow and have been tested. Update the catalog when skills are added or renamed.
