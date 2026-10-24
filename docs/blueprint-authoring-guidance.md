# Blueprint-inspired workflow authoring guidance

This project borrows interaction and authoring ideas from Unreal Engine Blueprints while keeping workflow execution semantics explicit and portable. This is an adaptation guide, not an implementation claim or a promise of Blueprint compatibility.

Epic's guidance is spread across several documentation pages rather than one product-neutral design specification. Start with the official [Blueprint visual scripting overview](https://dev.epicgames.com/documentation/en-us/unreal-engine/blueprints-visual-scripting-in-unreal-engine), [Blueprint variables](https://dev.epicgames.com/documentation/en-us/unreal-engine/blueprint-variables-in-unreal-engine), [Blueprint functions](https://dev.epicgames.com/documentation/en-us/unreal-engine/blueprint-functions-in-unreal-engine), and [Blueprint best practices](https://dev.epicgames.com/documentation/en-us/unreal-engine/blueprint-best-practices-in-unreal-engine). Direct retrieval from Epic returned HTTP 403 in this environment on 2026-10-24. The principles below are a local design interpretation informed by established Blueprint concepts; verify exact terminology and current Epic recommendations when those pages are reachable. Do not quote these notes as Epic's wording.

## Concepts to adapt

| Blueprint concept | Workflow Studio adaptation |
| --- | --- |
| A Blueprint asset defines reusable behavior and defaults | A workflow definition is a portable, versioned asset; layout stays separate from executable semantics |
| Typed variables have names, types, defaults, and Get/Set access | A workflow declares stable, typed parameters; getter nodes make values visible in the graph. Mutable Set/Get variables require explicit per-run and ordering semantics |
| Instance-editable or exposed-on-spawn values configure an instance | Hosts and run callers can override declared values without mutating a published workflow. Defaults remain safe and shareable |
| Function/event inputs and outputs appear as pins | Workflow entry inputs and reusable workflow inputs/outputs have explicit typed contracts |
| Execution and value pins have different meanings | Define edge semantics before rendering them. The current graph is a bounded, single-path string dataflow with explicit conditional nodes; screen position and topological display order never schedule execution |
| Functions, macros, comments, categories, and graph layout organize behavior | Prefer focused nodes, named reusable workflows with pinned versions, meaningful labels/comments/sections, and readable wire routing over one sprawling canvas |
| Blueprint debugging shows runtime behavior in graph context | Color or animate only recorded execution evidence; retain payload/event inspection and historical playback without invoking live dependencies |

Blueprint's object-instance memory and Unreal-specific execution flow are not copied automatically. Platform definitions remain framework-independent JSON contracts, and Microsoft Agent Framework mappings must be verified against the pinned package before execution features are committed.

## Workflow parameters

See [product design: workflow parameters and variables](design.md#workflow-parameters-and-variables) for the proposed parameter contract. Keep four concepts distinct:

- **Definition defaults** are non-secret starting values stored with a draft/publication/package.
- **Invocation inputs** are values supplied by a service or operator for one run and do not rewrite a published definition.
- **Graph variables** are explicit typed getters or, only after run-state semantics exist, mutable values scoped to one run.
- **Host capabilities and secrets** remain outside workflow artifacts. A model-profile parameter resolves only through the host's approved provider catalog; it cannot provide a URL or credential. Assigning a parameter or skill never grants an MCP tool.

Use visible pins or named bindings for values that affect agent instructions, provider/model choice, tool arguments, conditions, or transformations. Avoid hidden placeholder substitution in arbitrary node fields. Share parameter schemas and safe defaults with the workflow, resolve run overrides separately, and record effective non-secret values or redacted references according to sensitivity policy.

## Review each authoring change

Before changing a node, graph contract, or canvas gesture:

1. Name the user's task and the value/control flow involved. Distinguish data delivery from execution order, node configuration, host permission, and visual grouping.
2. Define stable IDs, data types, defaults, input/output pins, validation, versioning, failure behavior, cancellation, and serialization before rendering controls.
3. Keep important behavior visible in the graph or selected-node inspector. Use labels and comments to explain intent; extract repeated behavior into an explicitly contracted and version-pinned reusable workflow.
4. Treat workflow packages as untrusted declarations. They can require a tool or skill but cannot supply credentials, authorize tools, start MCP servers, or bypass host policy.
5. Verify the graph through actual authoring controls and backend execution. Confirm the published revision and dependencies, inspect recorded inputs/outputs/routes, and ensure playback makes no live calls. Keep screenshots as visual evidence, not proof of runtime semantics.
6. Update [architecture](architecture.md) for shipped contract/runtime changes, the [user guide](user-guide.md) for shipped user behavior, and the [roadmap](roadmap.md) for planned capabilities. Keep Blueprint-inspired proposals explicitly marked as future design until implemented and verified.

