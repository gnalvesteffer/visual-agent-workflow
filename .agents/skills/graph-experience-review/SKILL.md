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

Use matched screenshots for visual comparisons plus real interaction checks for behavior. Test representative graph sizes and event volume; report observed responsiveness and environment. Screenshots alone do not prove execution, accessibility, or embedding. The local studio supports visual linear-pipeline editing, catalog versions, and recorded viewing, including free placement, reroutes, and annotation/section metadata; explicit validated conditional blocks are supported; arbitrary rewiring and nested controls remain unsupported; one bounded loop with a linear body is implemented. Use the browser option in `scripts/studio_smoke.py` for implemented controls and report unsupported graph-editor checks as unrun.

Prerendered Blazor markup can appear before event handlers attach. Keep execution controls disabled until interactive rendering is ready, and wait for that state before filling inputs in browser tests. Verify actual recorded output; text presence alone can reflect prerendered or stale state.

When a select's options change after saving a new workflow, assert its selected value still matches the editor's workflow identity. Key dynamic options and reset selector identity when needed; a correct in-memory definition does not establish a correct displayed selection. Give dropdowns explicit accessible names that do not absorb their option text. For server-rendered text fields, wait for the expected value after an action before reading it in a browser test.

For embedding fixes, run `scripts/ui_skill_review.py` against the running Studio. Buttons must declare `type="button"` to avoid submitting consumer forms. Check narrow component containers independently of viewport width. Large server-bound fields can exceed SignalR limits before domain validation because event payloads may duplicate field text; verify both valid-boundary input and rejected oversized input without circuit disconnection.

Keep the editor’s responsive query container on the stable designer wrapper. During theme validation in Chromium 154, size containment on the dynamically updated pipeline inside the fieldset left new-workflow controls with zero layout size until a forced paint. The `studio_smoke.py --browser` new-workflow → choose operation → insert/reorder flow covers this regression; do not replace it with screenshot-only checks.

For canvas changes run `scripts/graph_canvas_smoke.py`. Geometry must connect actual edge socket centers, account for scroll/zoom and variable card heights, and remain separate from execution. Commit one undoable edit per completed gesture; cancel previews without saving. Verify node/section/waypoint keyboard alternatives, layout reload and version restore, escaped comments, and read-only recorded layouts. Section membership is explicit; overlapping a frame does not grant membership or capabilities.

Give section frames an initial position from their member coordinates before browser measurement; otherwise a newly created group header can briefly sit at the origin and receive gestures at the wrong location. Use actual measured card bounds for final frame sizes. Nudge/drag deltas must be relative to stored positions so imported off-grid coordinates do not jump on release.

For remote observation run `scripts/observation_smoke.py`: the reusable panel must clear data on source replacement, ignore stale old-source results, cancel observation waits on disposal, retain explicitly marked last received evidence on connection errors, and recover on reconnect. Never add execution/control actions to a read-only observer. Use exclusive temporary Studio data roots and child-process ports so a browser test cannot interrupt another host's journal.

For conditional authoring run `scripts/conditional_graph_smoke.py`. Distinct true/false ports must drive measured edge endpoints; never route both through a hard-coded out pin. Decisions and delivered edges are separate evidence. Test both outcomes, unvisited arms, version pinning and scrubbing before the decision. ARIA Boolean attributes such as aria-pressed must explicitly render the strings true/false: Razor Boolean attributes otherwise render empty/absent values and fail selection styling/accessibility.

For canvas-first creation run `scripts/workbench_smoke.py`. Start new drafts with connected terminals; test actual library clicks/drop placement, contextual search/cancel, exact-port output-pin creation, guarded deletion, one-step definition/layout undo, native fullscreen/focus/denial and fit geometry. Fullscreen must be requested from a trusted browser click, not after a server roundtrip. Keep editing and playback controls inside the expanded root. Browser tests must select **Run inspector** before quick-run/history controls and select the target node before its inspector fields. Use `workbench_browser.py` helpers for existing configuration fixtures; exercise blank creation independently.

Browser-local Delete/undo shortcuts can remove a node before Blazor receives that element's bubbled keydown. Ignore unrelated keys before looking up geometry and tolerate stale node/waypoint IDs. The workbench deletion → next selection check covers the observed failure; don't log payload-bearing exceptions to diagnose it.

For the project IDE document layout, run `scripts/ide_shell_smoke.py` and `scripts/folder_workflow_ide_smoke.py` with isolated Studio data. Verify document-tab and Explorer selection, compact inspector controls, test input → run → output → inspection → return, invalid-draft save/run gating, and folder-tab replacement after save. Settings can temporarily hide a canvas: do not consume pending centering while its viewport has zero dimensions. Reapply the pending view from the measured draw after it becomes visible. Folder workflow tabs follow the logical workflow ID when a save changes its indexed source path; adding another tab for the new path creates duplicate documents.

For recorded graph layout, assert the run header's captured layout as well as the visible node labels. A correct editor layout does not prove direct-start execution persisted it. Catalog-backed direct starts capture a matching saved layout independently of publishing; layout-only snapshots carry no folder provenance. Browser suites must wait for the hidden workflow selector to become interactive and bring pins into the visible canvas before deriving drag coordinates. Allow subpixel tolerance in viewport-ratio assertions.

Pane layout controllers may be attached to nested designer, canvas, and playback roots. Mark each controller's root and let it mutate and handle gestures only for panes whose nearest controller is itself; otherwise parent/child MutationObservers can continually restore competing splitter values and freeze the page. Merge one changed pane into the latest localStorage layout so independent nested controllers cannot erase each other's preferences. For node-event dialogs, use fixed readable column widths and a horizontally scrollable table; keep the payload column wide and payload text wrapping within its cell, and assert the rendered column geometry in `ide_shell_smoke.py`.

The run inspector view displays invocation count badges (e.g., "3×") on nodes invoked more than once, computed from `NodeStarted` events in `VisibleEvents`. Per-node cost badges (e.g., "$0.0012") appear on agent nodes using the same calculation as the accumulated header cost — iterate events, extract provider pricing from AgentStarted events, and compute costs from ModelResponded usage data with cache read/write rates. Both badges use inline-flex pill styling with accent/success colors and negative margin to sit flush with the node status chip.

Workflow admission errors must name specific missing dependencies rather than showing generic failure text. Catch `WorkflowAdmissionException` (in Core) and format the report's issues into actionable messages: list missing agent providers by ID and missing MCP tools by binding ID. The API error filter should return structured JSON with the issue list; the Blazor designer should display a human-readable summary. This applies to both UI runs and programmatic API calls.
