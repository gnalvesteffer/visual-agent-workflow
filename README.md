# Visual Agent Workflow

A proposed C#/.NET platform for visually designing, executing, and observing agentic workflows built on Microsoft Agent Framework.

The product combines a standalone Blazor studio, embeddable Razor components, and an independent workflow runtime. Workflow authoring uses Blueprint-style visual scripting, with MCP tools for external operations and approved .NET extensions for specialized behavior.

See [the design document](docs/design.md) for the agreed direction, architecture, constraints, and proposed delivery milestones.

Contributors and coding assistants should read [AGENTS.md](AGENTS.md) and the [agent skill catalog](docs/agent-skills.md) before working on the project.

**Status:** early implementation. The core library supports a validated, versioned linear string pipeline. The broader design remains a roadmap, not a production-readiness claim. See [implementation status](docs/implementation.md).

## Development

Install the .NET SDK pinned in `global.json`, then run from the repository root:

```sh
dotnet restore --locked-mode
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release
```

No model credentials or external services are needed for core tests. Keep package lockfiles committed when adding dependencies.
