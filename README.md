# Visual Agent Workflow

A proposed C#/.NET platform for visually designing, executing, and observing agentic workflows built on Microsoft Agent Framework.

The product combines a standalone Blazor studio, embeddable Razor components, and an independent workflow runtime. Workflow authoring uses Blueprint-style visual scripting, with MCP tools for external operations and approved .NET extensions for specialized behavior.

See [the design document](docs/design.md) for the agreed direction, architecture, constraints, and proposed delivery milestones.

Contributors and coding assistants should read [AGENTS.md](AGENTS.md) and the [agent skill catalog](docs/agent-skills.md) before working on the project.

**Status:** early implementation. The local Blazor studio visually edits linear pipelines with approved MCP text tools and deterministic demo agents, persists drafts/published versions, executes through Microsoft Agent Framework, and records model/tool evidence for playback. The editor and run viewer are reusable Razor components, with freely positioned nodes, edge sockets, reroutable wires, labels/comments, and named sections. See [implementation status](docs/implementation.md) for verified scope and limitations, and the [editor design system](docs/design-system.md) for visual conventions.

## Development

Install the .NET SDK pinned in `global.json`, then run from the repository root:

```sh
dotnet restore --locked-mode
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release
```

No model credentials or external services are needed for core tests. Keep package lockfiles committed when adding dependencies.

## Run the development studio

```sh
dotnet run --project src/VisualAgentWorkflow.Studio --configuration Release --no-build -- --environment Development
```

The console reports the local listening address on port 5099. The prototype requires Development mode and restricts requests to loopback peers/hosts and same-origin browser access. Do not expose it through a public reverse proxy. It has no workspace authentication and records plaintext input/output under `src/VisualAgentWorkflow.Studio/.local/runs/`, with the workflow catalog in `.local/workflows.db` (both ignored by Git). Use disposable, non-sensitive inputs.

With the studio running, exercise the actual API:

```sh
python3 scripts/studio_smoke.py
```

Optional real-browser checks require Python Playwright and its Chromium browser (or `--browser-executable` pointing to an installed Chromium):

```sh
python3 scripts/studio_smoke.py --browser
```

The built-in MCP tools run in a trusted local stdio fixture with no external I/O or model credentials. The demo agent uses the real Agent Framework function loop with scripted responses; it does not interpret instructions or call an LLM. Workflows cannot supply executable paths or grant themselves capabilities. See [component embedding](docs/embedding.md), [runtime provider configuration](docs/runtime-configuration.md), and the [saved workflow API](docs/workflow-api.md). Free-form graph editing, branching, live-provider validation, remote MCP provisioning, and production durability remain ahead.

For the [graph canvas](docs/graph-editor.md), run `python3 scripts/graph_canvas_smoke.py` against the development Studio with Playwright/Chromium installed. Pass `--browser-executable` for an existing Chromium or `--screenshots DIRECTORY` for review images.
