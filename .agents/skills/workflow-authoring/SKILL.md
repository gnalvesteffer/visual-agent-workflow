---
name: workflow-authoring
description: Use when an agent is asked to create, modify, or explain a Visual Agent Workflow from a user's natural-language request.
---

# Workflow authoring

Turn the user's request into a reviewable graph contract. Do not answer with a hidden prompt rewrite or claim that a workflow exists until the definition has been authored, validated, and saved through the host's workflow-authoring surface.

## Plan the graph

- Restate the requested outcome and identify the workflow's input, output, side effects, and success criteria.
- Ask only the clarifying questions that change the graph: missing input shape, expected output, required tools, approval needs, model choice, failure behavior, or whether work is sequential, conditional, or parallel.
- Choose the smallest supported node set. Prefer explicit typed data pins for values and explicit execution pins for control flow.
- Keep node labels and comments useful to a person inspecting the graph. Put workflow-specific instructions, repository paths, tool choices, and input guidance in the workflow's nodes, parameters, comments, templates, or assigned skills.

## Build safely

- Use the host's canonical node and tool descriptors; never invent a node type, port ID, tool schema, or model connection ID.
- Connect every required input exactly once, connect control-flow paths explicitly, and preserve the distinction between data dependencies and execution order.
- Assign tools and skills to the intended Agent node or profile explicitly. A visible tool in a catalog is not permission to invoke it.
- Do not put credentials, endpoints, grants, approvals, or host policy into portable workflow data.
- Treat caller input as data. Never silently replace it with a workflow-specific prompt. If guidance is needed, use an input-node description/comment or an explicit template node.
- Keep drafts saveable while they are incomplete, but surface every validation issue and do not claim the workflow can run until publish/run validation passes.

## Verify and report

- Validate the definition and layout after each structural edit, then run a deterministic test input when the host permits it.
- Inspect the recorded run evidence for the requested paths, tool calls, failures, outputs, and costs; do not infer execution from node placement or a successful save.
- Report the graph shape, configured inputs/tools, validation state, test result, and any unsupported requirement. Distinguish authored behavior from host configuration and from proposed future work.
- When a requested capability is unsupported, explain the nearest explicit graph design and ask before substituting behavior.

This is repository-specific guidance for Visual Agent Workflow. It intentionally does not prescribe a particular user workflow or seed project-specific prompts into the platform.
