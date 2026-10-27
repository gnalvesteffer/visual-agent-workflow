---
name: workflow-ownership
description: Use when changing workflow-specific prompts, input guidance, agent behavior, tools, repository paths, or sample/user project workflows so project policy stays in workflow assets rather than shared VAW code.
---

# Workflow ownership

Keep the boundary clear between the VAW platform and workflows authored in user projects.

- VAW code may provide generic editing, rendering, validation, and execution behavior.
- A workflow owns its prompts, research instructions, repository scope, tool selection, node comments, parameters, and text templates.
- Never special-case a workflow ID or display name in shared Studio, Blazor, runtime, or API code to inject project behavior.
- Never hardcode a sample workflow's prompt, repository path, MCP policy, or agent role into a reusable platform component.
- Use the workflow definition and layout metadata for project-owned behavior: agent instructions, text-template nodes, parameters, and input-node comments are appropriate places for it.
- Run input is caller data. A comment may explain what input is expected, but UI guidance must not silently overwrite the caller's text. If a transformation is required, make it explicit in the graph with a visible, reviewable node or template.
- If a project workflow needs a default, store it as workflow data and preserve it through import/export, revisioning, and execution evidence; do not derive it from an ID lookup in platform code.

For a UI change, verify both the generic behavior with a workflow that has no guidance and the project-owned behavior with a workflow that has an input-node comment. Add a focused regression test for the ownership boundary.

This is project-specific guidance based on the Repository Knowledgebase incident and the persisted workflow/layout model in `VisualAgentWorkflow.Core`; no external source is required.
