# Visual Agent Workflow

A proposed C#/.NET platform for visually designing, executing, and observing agentic workflows built on Microsoft Agent Framework.

The product combines an independently consumable workflow engine for user-owned services, embeddable Razor components, and a standalone Blazor Studio that acts as an optional IDE. Services should be able to load approved workflow artifacts and invoke them from their own APIs, workers, jobs, or application code without the designer running. See [service integration](docs/service-integration.md) for the goal and current library APIs. Workflow authoring uses Blueprint-style visual scripting, with MCP tools for external operations and approved .NET extensions for specialized behavior.

See the [user guide](docs/user-guide.md), [current architecture](docs/architecture.md), and [documentation map](docs/README.md).

See [the design document](docs/design.md) for the agreed direction, architecture, constraints, and proposed delivery milestones.

Contributors and coding assistants should read [AGENTS.md](AGENTS.md) and the [agent skill catalog](docs/agent-skills.md) before working on the project.

**Status:** early implementation. The local Blazor studio visually edits linear/conditional string workflows with approved MCP text tools and deterministic demo agents, persists drafts/published versions, executes through Microsoft Agent Framework, and records model/tool evidence for playback. The editor and run viewer are reusable Razor components, with freely positioned nodes, edge sockets, reroutable wires, labels/comments, and named sections. Published pipelines can be reused with fresh node identities; complete designs can be exported/imported as validated packages. See [roadmap and current scope](docs/roadmap.md) for verified scope and limitations, and the [editor design system](docs/design-system.md) for visual conventions.

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

The built-in MCP tools run in a trusted local stdio fixture with no external I/O or model credentials. The demo agent uses the real Agent Framework function loop with scripted responses; it does not interpret instructions or call an LLM. Workflows cannot supply executable paths or grant themselves capabilities. See [component embedding](docs/embedding.md), [runtime provider configuration](docs/runtime-configuration.md), [run storage](docs/run-storage.md), [deterministic checkpoint recovery](docs/checkpoint-recovery.md), [read-only reconciliation](docs/run-reconciliation.md), [opt-in model providers](docs/model-providers.md), and the [saved workflow API](docs/workflow-api.md). Arbitrary socket rewiring, parallel execution, live-provider validation, remote MCP provisioning, and production durability remain ahead.

The [graph workbench](docs/graph-editor.md) provides a searchable node library, contextual creation, compact nodes with a property inspector, and fullscreen editing/playback. New workflows start with connected Input/Output nodes. Run `python3 scripts/workbench_smoke.py` for creation and fullscreen checks.

For the [graph canvas](docs/graph-editor.md), run `python3 scripts/graph_canvas_smoke.py` against the development Studio with Playwright/Chromium installed. Pass `--browser-executable` for an existing Chromium or `--screenshots DIRECTORY` for review images.

For reusable pipelines and sharing, `python3 scripts/pipeline_reuse_smoke.py` exercises exact-version insertion, section extraction, full-package import/export, and missing-tool denial in the browser.

## Run in your own service

The [standalone service sample](samples/WorkflowService/README.md) loads a pinned package and demonstrates direct/queued execution without Studio, Blazor or an authoring catalog. Run `python3 scripts/service_smoke.py` after the Release build to verify it with deterministic fixtures and real local MCP.

Consumers can opt into an authorized read-only observation endpoint. Studio can attach to a named consumer source, and Blazor apps can embed the same observation panel. See [consumer observation](docs/observation.md) for setup and current limits.

Schema-1.4 [conditional graphs](docs/conditional-graphs.md) add approved ordinal predicates, true/false string outputs and exclusive merges. The designer and recorded viewer preserve actual chosen-path evidence.

Schema-1.5 [approval steps](docs/approvals.md) hold an exact explicit MCP action for review, preserve request/decision evidence and remain independent of browser lifetime. Remote observation stays read-only.

CI restores, builds and runs the .NET tests on Windows and Linux. Service, provider-protocol, observation, browser and documentation integration checks run on Linux. A separate Linux job runs the real Agent Framework/provider path against a locally hosted Qwen3 0.6B model in the prebuilt llama.cpp server image; it needs no external model credentials and does not compile llama.cpp in CI. See [model provider verification](docs/model-providers.md#verification-and-remaining-qualification) to run the opt-in test locally.
