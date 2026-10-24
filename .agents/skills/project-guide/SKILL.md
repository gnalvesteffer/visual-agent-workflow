---
name: project-guide
description: Use when changing, reviewing, or explaining this project, its architecture, implementation plans, or package boundaries.
---

# Project guide

Read [the design baseline](../../../docs/design.md), [implementation status](../../../docs/implementation.md), and [README](../../../README.md). Inspect the actual tree before naming projects, commands, or APIs. The initial core library supports linear string pipelines; broader graph capabilities remain planned.

## Preserve the architecture

- Keep runtime execution independent of browser connections and Blazor circuits. The UI edits definitions and observes or controls backend-owned runs.
- Build embeddable Razor components first; the standalone studio must consume the same public APIs. Keep authorization, persistence, and backend access host-configurable.
- Isolate Microsoft Agent Framework APIs behind an adapter. Use [microsoft-agent-framework](../microsoft-agent-framework/SKILL.md) for version-specific API research, package selection, integration, and upgrades; distinguish confirmed API support from proposed mappings.
- User authoring is no-code visual composition. MCP supplies external operations; approved .NET extensions supply specialized behavior. Do not add general C# scripting or arbitrary URL tools by default.
- Keep published workflows and dependencies immutable. A run references its exact revision. Playback reconstructs recorded evidence without execution.
- Definitions use platform-owned JSON contracts, not CLR type names, framework instances, or canvas-library models.

## Work from evidence

Before selecting dependencies or implementing a public contract, identify the smallest representative proof and its failure cases. Record unresolved choices and compatibility tradeoffs in the design document or a focused linked decision document. Do not turn illustrative package names or JSON fragments into assumed supported APIs.

Use the [catalog](../../../docs/agent-skills.md) to select the relevant review. Update README and design documentation when behavior or architectural decisions change. Keep proposed work distinct from implemented and tested behavior.

For stylesheet work, read the vendored [CSS skill](../css/SKILL.md) and its [local integration guidance](../css/SOURCE.md), then use [graph-experience-review](../graph-experience-review/SKILL.md) for actual UI validation. Keep library styles and tokens scoped so embedding does not alter the host application's global styles.

For Razor/component work, read the vendored [Blazor skill](../blazor/SKILL.md) and its [.NET 10 project compatibility notes](../blazor/SOURCE.md). Preserve host-independent embedding and backend-owned execution; use verified framework APIs rather than assuming generic upstream examples match this repository's SDK.

For C# changes, read [modern C# coding standards](../modern-csharp-coding-standards/SKILL.md) with its [local notes](../modern-csharp-coding-standards/SOURCE.md), and [clean code](../clean-code/SKILL.md). For public contracts use [API design](../api-design/SKILL.md) and its [compatibility notes](../api-design/SOURCE.md). For dependency/build architecture use [.NET project structure](../dotnet-project-structure/SKILL.md) and its [local notes](../dotnet-project-structure/SOURCE.md). Keep package versions in `Directory.Packages.props`; architectural boundary tests guard inward dependencies. Upstream suggestions do not mandate unrelated frameworks, publishing, unsafe optimizations, or breaking contract rewrites.

For visual design or theme work, read [frontend-design](../frontend-design/SKILL.md) and [design-systems](../design-systems/SKILL.md) with their local SOURCE.md notes. Follow the [editor design system](../../../docs/design-system.md), preserve host overrides, and review real screenshots plus functional controls.
