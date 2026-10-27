# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Developers and technical operators who design, configure, test, and maintain AI-enabled workflows. They use Visual Agent Workflow Studio locally as an IDE and integrate portable workflow artifacts into services and applications they own.

## Product Purpose

Visual Agent Workflow lets developers compose workflows as visual graphs, combine deterministic logic with agent and tool operations, run them, and inspect recorded execution evidence. Success means a developer can create and validate a workflow in Studio, then deploy or invoke it from a consumer host without requiring the Studio UI at runtime.

## Positioning

It combines an IDE-style project workspace with Blueprint-style visual scripting for agent workflows. Graphs can express explicit data and control flow around model and tool calls, and project assets are intended to remain portable source files.

## Operating Context

Studio is designed first for one local operator. Projects are ordinary folders that developers may manage with Git themselves. Authors use an explorer, editors, configuration libraries, and run inspection in one workspace. Consumer applications and services own execution policy and may host workflows independently of Studio.

## Capabilities and Constraints

- Workflow graphs combine deterministic nodes, agent nodes, control-flow nodes, and explicitly assigned MCP tools.
- Agent model connections, MCP endpoints, credentials, and runtime policy belong to the Studio or consumer host environment; credentials must not be written into project definitions.
- Workflow project files are portable authoring inputs. Run history and audit data belong to a host-selected store outside project source.
- Users own source control and may use Git to share, branch, and revert project files.
- Invalid drafts may be saved for continued editing, while publishing and execution require validation to pass.
- Studio is an authoring and observation client; workflow execution must remain consumable by host applications without a Studio browser session.

## Brand Commitments

The product is Visual Agent Workflow Studio. Its main experience is a developer IDE, with familiar Visual Studio project navigation and an Unreal Engine Blueprint-inspired graph editor.

## Product Principles

- Keep graph behavior explicit and inspectable.
- Combine AI operations with deterministic programming constructs.
- Keep authoring portable and runtime policy host-owned.
- Show validation and execution state where developers can act on it.
- Keep the graph editor central to the workflow-development experience.
