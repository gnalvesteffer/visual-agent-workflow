# Blueprint-style graph editing

## Reference research

Epic's current documentation pages (Connecting Nodes, Nodes, and the Blueprint Editor Cheat Sheet) were blocked by this environment's proxy during research. The following accessible references were inspected instead:

- [Archived Epic Wiki: Blueprint Fundamentals](https://github.com/MichaelJCole/wiki.unrealengine.com/blob/5f5d1dfc7542ba3e826f959986cf540d288091ed/docs/Blueprint_Fundamentals/README.md): direction and type of pins, left-to-right connected flow, and named comment blocks around selected nodes. Linked screenshot hosts were also blocked; this is a historical UE4 reference, not a visual verification of current UE5.
- [Unreal SGraphPin reference](https://github.com/folgerwang/UnrealEngine/blob/release/Engine/Source/Editor/GraphEditor/Private/SGraphPin.cpp): input pin before its label on the left; output pin after its label on the right; connected/unconnected and type-specific pin styling.
- [Unreal reroute-knot contract](https://github.com/folgerwang/UnrealEngine/blob/release/Engine/Source/Editor/BlueprintGraph/Classes/K2Node_Knot.h): pass-through control points used to organize wires, not compiler-relevant executable work.

These inform interaction conventions. No Unreal source code or artwork is incorporated into this project. Our string graphs have data links only. Schema 1.4 conditions expose explicit true/false string outputs; the UI must not invent separate execution pulses or imply support for arbitrary fan-out.

## Portable presentation model

`WorkflowLayout` schema `1.0` is a separate presentation artifact. It stores node coordinates, user labels and comments, wire style and ordered waypoints with optional pinned tangent angles, and named sections with explicit node membership. Moving nodes, adding comments, and routing wires do not change executable definitions, capability grants, event evidence, or `WorkflowJson` content hashes.

Positions are finite and bounded to 0–32768 logical canvas units. Limits are 160 characters per label, 4000 per comment, 32 waypoints per wire, and 64 sections. A node belongs to at most one section. Sections are organizational only, not runnable subgraphs or prefabs. Curved-wire tangent angles are clockwise screen degrees [0,360); orthogonal wires accept multiples of 90 only. Duplicate/unknown JSON properties and invalid references are rejected. Layout edits never become executable nodes.

`IWorkflowDesignCatalog` extends the optional host contract without changing existing `IWorkflowCatalog` methods. Its design save updates definition and layout under one expected generation and SQLite transaction. An explicit save records `draft.saved` and `layout.saved`; the latter identifies the presentation hash. Publishing snapshots layout with the immutable revision; restoring restores both. Older catalogs/revisions without layout use deterministic default positions. Definition-only API saves preserve annotations for surviving node IDs and prune routes/sections whose references disappear.

SQLite schema 2 adds presentation tables to schema 1 without changing existing definitions, generations, revisions, or hashes. The development database remains single-host and unauthenticated, as documented in [roadmap and current scope](roadmap.md).

## Canvas controls

The reusable `WorkflowGraphCanvas` renders left-edge input sockets and right-edge output sockets, measured from the actual rendered nodes. Wires use SVG geometry and keep following the pins when cards change size. The supported string links are data pins; conditions expose distinct true/false sockets backed by real exclusive routing. There are no decorative execution pins.

- Drag a node header to place it freely. Arrow keys move by 8 units; Shift moves by 40. Numeric X/Y fields provide an alternative. Placing nodes does not change the connected execution order; the existing move-up/down operations still reorder execution.
- Scroll or drag empty graph background to pan. Zoom controls change the view, not stored coordinates. Coordinate conversion accounts for zoom and scroll. Pointer cancellation restores the preview rather than saving an accidental move.
- Double-click a wire to add a reroute waypoint, or use **Add reroute point** in the inspector. Drag or keyboard-nudge the point. Curved routes support automatic or pinned tangent angles (presets and custom degrees); right-angle routes constrain tangents to multiples of 90. Resetting a route removes its presentation waypoints, not the connection.
- Open **Labels, comments, sections, and wire routing** to set a node label/comment. Identity and executable type remain stable. Text is rendered as text, not HTML, Markdown, or instructions to an agent.
- Select section members using the inspector checkboxes, enter a label, and group them. Frames fit their explicit members automatically. Drag the section header to move its nodes together in one undoable edit. Rename or ungroup from the inspector. Sections do not nest, execute, or infer membership merely because a node happens to overlap them. Pinned wire points remain in graph coordinates when a section moves or nodes are automatically arranged.

Node positions, annotations, groups, and routes share the designer’s undo/redo history with semantic edits. Saves are explicit. A completed drag creates one edit; transient browser pointer previews do not stream commands to the backend. Publish and restore preserve layout with its revision. Preview and run playback are read-only. The Studio fetches the recorded revision's layout for playback; template/ad-hoc runs and pre-layout revisions use default positions. Wire completion color still comes only from recorded transfer events, not geometry.

JSON definition export remains compatible. Separate layout export/import controls share the JSON editor and provide `workflow.layout.json`; importing validates IDs against the current definition and does not publish or execute anything. Consuming hosts implement `IWorkflowDesignCatalog` for persistence; hosts implementing only the older catalog retain semantic editing with presentation editing disabled.

## Verification and scope

`python3 scripts/graph_canvas_smoke.py --browser-executable /path/to/chromium` exercises real mouse/keyboard moves, socket alignment, shared undo/redo, labels/comments, grouped movement, reroute creation/dragging, pinned/right-angle geometry, zoom conversion, save/reload, stale-write denial, publication, immutable version preview/restore, and recorded-layout playback. `--screenshots DIRECTORY` captures the tested UI. CI runs it alongside the existing authoring and UI-skill checks.

Core/storage tests cover bounded imports, canonical layout round trips, immutable definition hashes, stale writes and rollback, schema-1 migration, revision capture, restore, and safe annotation rendering with no execution services. These do not establish complete accessibility or production readiness. The canvas can freely position the supported nodes, but arbitrary drag-to-connect rewiring, parallel branches, nested sections, collaborative live dragging, and graph virtualization remain future work.

Conditional insertion and predicate/arm editing use validated semantic operations. Port identities drive wire geometry, and topological display order is distinct from execution. See [conditional graphs](conditional-graphs.md).
