# Blueprint-style graph editing

## Reference research

Epic's current documentation pages (Connecting Nodes, Nodes, and the Blueprint Editor Cheat Sheet) were blocked by this environment's proxy during research. The following accessible references were inspected instead:

- [Archived Epic Wiki: Blueprint Fundamentals](https://github.com/MichaelJCole/wiki.unrealengine.com/blob/5f5d1dfc7542ba3e826f959986cf540d288091ed/docs/Blueprint_Fundamentals/README.md): direction and type of pins, left-to-right connected flow, and named comment blocks around selected nodes. Linked screenshot hosts were also blocked; this is a historical UE4 reference, not a visual verification of current UE5.
- [Unreal SGraphPin reference](https://github.com/folgerwang/UnrealEngine/blob/release/Engine/Source/Editor/GraphEditor/Private/SGraphPin.cpp): input pin before its label on the left; output pin after its label on the right; connected/unconnected and type-specific pin styling.
- [Unreal reroute-knot contract](https://github.com/folgerwang/UnrealEngine/blob/release/Engine/Source/Editor/BlueprintGraph/Classes/K2Node_Knot.h): pass-through control points used to organize wires, not compiler-relevant executable work.

These inform interaction conventions. No Unreal source code or artwork is incorporated into this project. Our current string pipeline has data links only; the UI must not invent separate execution pins or imply support for arbitrary branching.

## Portable presentation model

`WorkflowLayout` schema `1.0` is a separate presentation artifact. It stores node coordinates, user labels and comments, wire style and ordered waypoints with optional pinned tangent angles, and named sections with explicit node membership. Moving nodes, adding comments, and routing wires do not change executable definitions, capability grants, event evidence, or `WorkflowJson` content hashes.

Positions are finite and bounded to 0–32768 logical canvas units. Limits are 160 characters per label, 4000 per comment, 32 waypoints per wire, and 64 sections. A node belongs to at most one section. Sections are organizational only, not runnable subgraphs or prefabs. Curved-wire tangent angles are clockwise screen degrees [0,360); orthogonal wires accept multiples of 90 only. Duplicate/unknown JSON properties and invalid references are rejected. Layout edits never become executable nodes.

`IWorkflowDesignCatalog` extends the optional host contract without changing existing `IWorkflowCatalog` methods. Its design save updates definition and layout under one expected generation and SQLite transaction. An explicit save records `draft.saved` and `layout.saved`; the latter identifies the presentation hash. Publishing snapshots layout with the immutable revision; restoring restores both. Older catalogs/revisions without layout use deterministic default positions. Definition-only API saves preserve annotations for surviving node IDs and prune routes/sections whose references disappear.

SQLite schema 2 adds presentation tables to schema 1 without changing existing definitions, generations, revisions, or hashes. The development database remains single-host and unauthenticated, as documented in [implementation status](implementation.md).

## Delivery status

Presentation contracts, transactional storage, migration, revision snapshots, and design API endpoints are implemented. The interactive canvas is the next milestone; do not infer drag-and-drop or annotation UI support solely from these storage APIs.
