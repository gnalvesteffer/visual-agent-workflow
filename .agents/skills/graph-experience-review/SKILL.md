---
name: graph-experience-review
description: Use when changing the Blazor graph designer, live run viewer, interaction design, accessibility, visual behavior, or component embedding.
---

# Graph experience review

Read [project-guide](../project-guide/SKILL.md). Establish the user task: author, configure, debug live, inspect history, or embed components. Test the smallest coherent path through actual controls when a runnable UI exists.

## Review authoring

Check searchable node creation, typed connections, inline diagnostics, configuration, selection, keyboard navigation, undo/redo, subgraph orientation, and save/conflict feedback. Distinguish data links, execution dependencies, and tool bindings. Invalid configurations must explain how to recover.

Keep canvas implementation models out of execution contracts. The standalone studio and sample consuming app must use the public component APIs; verify configuration, styles/assets, dependency injection, and remote-backend access without hidden studio services.

## Review observation and playback

Show the selected revision/run, live versus recorded mode, distinct invocations, actual path evidence, failures, approvals, and uncertain outcomes. Do not animate an inferred route as a recorded fact. Preserve manual navigation; offer reduced motion and avoid auto-follow that traps users.

Check accessible names, focus visibility/order, keyboard alternatives to dragging, non-color status cues, zoom/reflow, contrast, and readable payload inspectors. Include an accessible structured alternative for graph execution details.

Use matched screenshots for visual comparisons plus real interaction checks for behavior. Test representative graph sizes and event volume; report observed responsiveness and environment. Screenshots alone do not prove execution, accessibility, or embedding. In this design-only repository, report UI checks as unrun rather than constructing a fake passing demo.
