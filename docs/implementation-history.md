# Implementation history

Delivered milestones are listed in reverse chronological order. This page records completed work and its scope; for the current product boundary and priorities, see the [roadmap](roadmap.md). Detailed behavior and limits remain in the linked feature documentation.

## Sequence path promotion diagnostics

The schema 1.38 to 1.43 promotion is now paired with actionable validation
evidence for downstream topology errors. Problems identify the affected
ordered output and stable execution pin, unsupported path nodes, competing
path ownership, missing or duplicate roots, and linear-successor or path
length violations. Invalid drafts remain saveable while publish and run
admission remain strict. Core regression tests cover unconnected outputs,
unsupported path nodes, and shared downstream nodes. See [ordered Sequence
nodes](user-guide.md#ordered-sequence-nodes) and the [Blueprint parity
roadmap](roadmap.md).

## Blueprint parity vertical proof slice

The bounded Blueprint-inspired proof now crosses authored editing, definition
round-trip, backend execution, and recorded evidence for pure workflow values,
direct MCP tools, conditional routing, workflow parameters, and pinned child
workflow revisions. A separate passive playback proof renders a recorded
conditional run in the run viewer with no runtime or client services registered.
The proof is deliberately representative: per-node playback qualification,
broader debugger behavior, and unrestricted Blueprint compatibility remain
roadmap work. See the [Blueprint parity roadmap](roadmap.md).

## Public Sequence contract slice

Schema 1.38 now persists a bounded Blueprint-style `core.sequence` definition with two to four ordered output descriptors and stable `then.<outputId>` execution pins. Core validates the initial Input → Sequence → Output topology, preserves descriptor order through JSON round trips, and the Studio exposes label, reorder, add, and remove editing for schema 1.38. The normal production runner consumes those persisted descriptors and records their authored node/connection identity; schema promotion and richer downstream topology remain deferred. See [bounded Sequence dispatch boundary](decisions/0003-bounded-sequence-dispatch-boundary.md) and the [Blueprint parity roadmap](roadmap.md).

## Non-public bounded Sequence activation ledger

The Blueprint parity audit established that Agent Framework dispatch order does not guarantee downstream start or completion order. A three-role design review therefore kept public Sequence execution out of the contract until its runtime boundary was proven. The first follow-up slice adds an internal, non-serialized activation ledger with stable output order, one-active-output admission, explicit terminal states, and skipped-output accounting after failure or cancellation. Focused tests cover the two-to-four output bound, ordered continuation, terminal completion, and failure short-circuiting. This proof infrastructure remains the basis for the unfinished runner integration.

The runner now also owns an internal `ExecutionActivationLedger` at the recorded event boundary. It tracks started node invocations across workflow-call scopes and requires each invocation to reach a completed, failed, or cancelled event before a normal run completion or termination is emitted. Focused tests cover scoped identity and partial active-work detection. Sequence-specific lineage, continuation admission, and pause/host-stop semantics remain deferred.

The private `SequenceRuntimeCoordinator` proof now assigns stable sequence invocation, output ID, ordinal, and connection identity to each lifecycle fact. It distinguishes successful completion from failed or cancelled termination and records later outputs as skipped after a short-circuit. These facts are not yet serialized into passive playback or connected to a public node.

Core now defines the versioned `SequenceLifecycleEvidence` payload and a fail-closed passive reader for the reserved `sequence.*` event family. It accepts only version 1 payloads with stable sequence identity and ignores malformed, wrong-version, and non-Sequence records. The coordinator also proves conversion to passive `ExecutionEvent` records with monotonic sequence numbers. The real runner does not emit these events yet, so public Sequence playback remains deferred.

## Branch-local Boolean routing in schema 1.34

Schema 1.34 allows at most one Boolean Branch in each static schema 1.32 fork branch. Core validates both possible outcomes, rejects branch escapes, cross-branch routes, cycles, and incomplete paths, and preserves invalid but structurally safe topology as a saveable draft while publication and run admission stay strict. The runner records the selected port and actual execution connection with fork activation and stable branch IDs; deterministic tests cover true/false selection, skipped Agent suppression, exclusive local reconvergence, failure/cancellation without a partial join, and a final join result. Schema 1.34 definitions round-trip through packages without changing definition hashes; fixed package/hash fixtures protect schema 1.32 and 1.33 compatibility. Studio upgrades a branch-adding edit to schema 1.34 and preserves the incomplete draft across save and reload. The Playwright smoke verifies those draft and publish/run gates, then publishes a valid exclusive-reconvergence graph. Schema 1.33 collaborative branches remain linear because phase-aware routing and grant invariants have not been validated together. See [branch-local routing](parallel-branch-routing.md), [parallel graphs](parallel-graphs.md), and the [user guide](user-guide.md#route-within-a-parallel-branch).

## Configurable branch management in Studio

The schema 1.32 fork inspector can append branches up to the eight-branch bound, reorder their pin presentation, and remove an empty branch while at least two remain. Each operation updates fork descriptors, anchor nodes, and the join's branch IDs and execution pins atomically; undo/redo uses the existing workflow edit history. Surviving branch IDs, labels, and wires are retained. A branch with Agent work cannot be removed through this control, so authors must move or delete that work first. Core regression tests cover maximum/minimum counts, stable IDs and pins through reorder, empty-branch removal, and refusal to discard populated paths. A Chromium smoke selects, saves, reorders, removes, publishes, runs, and removes/undoes the region. See [parallel graphs](parallel-graphs.md), the [user guide](user-guide.md#run-configurable-parallel-branches), and [roadmap](roadmap.md).

## Initial configurable parallel authoring in Studio

The Studio can insert a schema 1.32 fork/anchor/join into a valid schema 1.31 graph, letting the author choose 2–8 branches in one undoable edit. Each branch gets a stable ID and editable display label; changing a label preserves its socket and connections. The join JSON feeds the workflow Output. Authors can place Agent nodes in the linear branch paths and connect each result to the matching join pin. Removing the block restores the previous input-to-output data path and execution route, and undo restores the block. Removing and recreating is the current way to change branch count. Studio tests exercise authoring, save, stable labels/pins, publish, run, removal, and undo in Chromium. Core tests cover builder limits, typed parameter preservation, labels, and restoration. Branch resizing/reordering in place, branch-local control flow, nested forks, and playback customization remain future work. See [parallel graphs](parallel-graphs.md), [user guide](user-guide.md#run-configurable-parallel-branches), and [roadmap](roadmap.md).

## Configurable N-way parallel execution core

Schema 1.32 adds one bounded static fork-to-join region with 2–8 branches. Stable branch IDs define execution pin names and the JSON object keys emitted by the join; labels remain presentation-only. Fork activation IDs and branch IDs travel with runtime messages, so the join rejects duplicate, unexpected, or cross-activation arrivals and exposes no partial result. A completed join publishes its JSON value to downstream data evaluation. Existing schemas retain their two-branch ports and scheduling behavior. Core validation tests cover named pins and crossed/missing branches; Agent Framework tests prove concurrent three- and eight-way execution, named results, and failure without a partial output. The complete Release suite passed (458 passed, 2 optional tests skipped), and the existing Studio two-branch authoring/execution/playback/removal browser smoke passed. Studio cannot author schema 1.32 yet; its bounded branch-count editor, stable-ID editing, diagnostics, and playback remain planned. See [parallel graphs](parallel-graphs.md), [architecture](architecture.md), and [roadmap](roadmap.md).

## Folder-backed execution and restore-point testing

The folder editor can run a saved executable workflow after a fresh bounded manifest/source validation and exact expected manifest/asset hash check. It also tests the selected workflow from a validated `.vaw/snapshots/` archive without restoring or changing the current project manifest. Both paths capture definition and layout in memory before admission and use the existing `LocalWorkflowRuntime` and configured host run store. The run header persists the normalized definition hash plus captured layout and source provenance (project/workflow IDs, manifest, source asset and layout hashes, and restore-point archive content hash). Provider connections and MCP grants remain host-owned, while project source and `.vaw/` remain free of run records. The UI blocks saved-source execution when the buffer is dirty or a folder change has been detected, renders typed parameter overrides, and shows the run viewer. Studio tests verify saved execution, stale manifest rejection before run admission, distinct layout provenance for the same definition, restore-point execution after the active source changes, and evidence persistence through file-store reopen. The Studio test project and Release build passed. Browser-level end-to-end testing remains to be completed for the added controls. Git/editor writes are not locked; each admitted run uses the validated in-memory capture, while standard runtime restart behavior can still mark unfinished runs interrupted. See [project workspaces](project-workspaces.md), [architecture](architecture.md), and [run storage](run-storage.md).

## Typed signed-integer graph values

Schema 1.31 adds `Int64` constants, checked addition, equality, and string/Boolean/integer workflow parameter getters with explicit typed pins. Output nodes and pure graph functions preserve those types; graph functions use the same integer subset as workflows. The Studio exposes integer nodes in the workflow library, lets authors set signed constants and parameter types, and extends the reusable function editor's interface and node library. Older schemas remain string-compatible and parameter edits no longer silently upgrade their schema. Overflow emits failure evidence attributed to the arithmetic node and its requesting execution node. Core tests cover type contracts and old-schema compatibility; Agent Framework runtime tests verify arithmetic, branch selection, typed audit values, and overflow; `scripts/integer_graph_smoke.py` exercises editor rendering, authoring/save, execution evidence, and run-inspector navigation. The Release build and complete test suite passed on Linux; live model inference and container integration tests remain opt-in/skipped. See [graph values](graph-values.md), [architecture](architecture.md), and [roadmap](roadmap.md).

Schema 1.39 adds the first explicit conversion node: `core.convert.string-to-integer`. The Studio upgrade path and Values library expose its typed string input and integer output, and an incompatible string-to-integer pin drop can offer the same conversion as one explicit atomic edit. The shared Core parser enforces the portable ASCII grammar and bounds, while the Agent Framework records stable failure evidence against the conversion node before propagating failure to the requesting node. Its pins now also use the canonical static descriptor registry. Existing schemas remain unchanged; graph functions and checkpoint recovery deliberately reject the node. Core regression tests cover parser boundaries, schema compatibility, typed ports, graph-function rejection, legacy hash stability, and descriptor conformance. Agent Framework tests cover successful typed output and deterministic failure evidence. Broader conversion families remain follow-up work.

## Finite-number direct MCP pins

Schema 1.40 adds the bounded finite-number slice: a Number constant and
top-level numeric input pins for direct MCP tools. Core validation rejects
non-finite values and preserves the existing schema 1.37 contract; runtime
serialization emits JSON numbers. Core and Agent Framework tests cover schema
upgrade, pin compatibility, rejection, round-trip behavior, and broker payload
kind. Numeric operators, parameter/function number ports, and nested numeric
pins remain deferred. See [finite number primitive](decisions/0006-finite-number-primitive.md).

## Finite-number parameter propagation

Schema 1.41 extends the finite-number contract across typed Workflow value
nodes and pure graph-function ports. It also provides an explicit promotion
from schema 1.40 so a finite number parameter can feed an existing persisted
direct-MCP number pin. Runtime emits a JSON number and records typed evidence;
legacy definitions and 1.40 hashes remain unchanged. Numeric operators,
conversions, containers, nested numeric expansion, and generic numeric outputs
remain deferred. See [finite number propagation](decisions/0007-finite-number-propagation.md).

## Finite-number addition

Schema 1.42 adds the pure `core.number.add` node with exact finite-double `left`, `right`, and `out` pins. Inserting it into an editable schema-1.41 direct-MCP graph promotes the draft to 1.42; older definitions remain unchanged and reject the node. The Agent Framework evaluates the node lazily, records typed data evidence, and reports non-finite results with a stable diagnostic. Pure graph functions and the Studio function editors expose the same number contract. Core and Agent Framework regression suites cover schema gating, typed MCP payloads, function evaluation, evidence, and overflow. See [finite number addition](decisions/0010-finite-number-addition.md).

## Actionable typed-wire diagnostics

Core validation and editor connection edits now identify the source and target
pin IDs and declared types when a data wire is invalid. The diagnostic keeps
the exact-identity compatibility rule explicit and points authors to an
explicit conversion node rather than suggesting implicit coercion. Existing
wire semantics and serialized definitions are unchanged.

## Agent-selected execution routes

Schema 1.30 lets an Agent node declare up to 16 stable route IDs, labels, and descriptions. Studio renders labeled execution pins and preserves stable pin IDs when labels change. The Agent Framework adapter exposes an invocation-scoped `select_route` function constrained to those authored IDs; it is an internal function and grants no MCP or graph access. The runner requires exactly one valid selection, latches it, and dispatches only after successful agent completion. Missing, duplicate, unknown, or later-discarded proposals fail without activating a route. Proposed, rejected, committed, and discarded selections are recorded for replay. Removing a route preserves its old edge as an invalid draft issue; save remains available while publish and execution validation stay strict. Runtime tests cover selected dispatch, missing selection, and provider failure after proposal; a validator regression covers removed-route edges. The Playwright smoke exercises route editing, pin labels, save, and incomplete-draft validation. Providers must support function calling. See [agent-selected routes](graph-values.md#agent-selected-routes).

## Portable selected-workflow releases

Folder projects can export an explicit selection of saved workflows as deterministic `.vawpkg` archives. Each selected definition/layout is validated as a normal executable `WorkflowPackageJson`; the top manifest pins inner payload hashes and a derived sorted requirement union. The bounded Core reader rejects unsafe/colliding paths, unknown formats, missing/extra files, hash drift, duplicate properties, and requirement mismatch without extracting content. The folder editor avoids copying reusable snapshots already embedded in workflow definitions. A standalone `WorkflowService` `pack`/`bundle` path builds releases and runs a selected workflow without Studio or a project catalog. The end-to-end browser smoke exports two selected workflows and runs them from the resulting archive; MCP execution is denied without a consumer grant and succeeds when the host explicitly grants it. Package format tests cover deterministic byte output, selection, requirement drift, tampering, missing/extra files, unsafe paths and duplicate JSON properties. The archive contains no host credentials, endpoints, grants, Git data, `.vaw/`, or run history; authored prompt text remains included. It is not signed, installed, or activated, and publisher verification and immutable deployment staging remain future work. See [project releases](project-workspaces.md#consumer-artifact), [service integration](service-integration.md), and the sample [commands](../samples/WorkflowService/README.md).

## Offline catalog-to-folder cutover

Catalog-backed projects can now be converted in place with `--migrate-project-to-folder <project-id> <new-folder>`. The command exports the current editable workflow drafts/layouts and skill, profile, and graph-function assets, writes them to a fresh sibling staging directory, validates the resulting manifest and hashes, and updates the local registry to preserve the same project ID. A durable journal lets Studio finish a published-but-unmapped folder before opening writable workspace services. Studio and the maintenance command use one exclusive lock file in the host data directory; this prevents live Blazor circuits and a second Studio process from writing during migration. The old catalog remains local recovery data, while publication/revision history and run evidence remain in host storage. Regression tests cover identity/source preservation, restart recovery from validated staging, and exclusive lock behavior. This remains an offline, single-host operation; multi-process external Git/editor writes are not covered. See [project folders and workspaces](project-workspaces.md) and the [user guide](user-guide.md#organize-work-with-projects).

## Folder-backed graph-function editing

The Open folder project editor now opens pure graph-function assets from the project tree, edits their typed string/Boolean interface and visual dataflow body, and saves a new immutable content-addressed revision through the project manifest. A versioned Studio authoring envelope stores the function catalog entry with its validated graph layout; the same format is used by the local function library and project archive import/export. Layout remains outside the Core runtime definition and semantic content hash. Legacy raw catalog entries remain readable with a generated layout because their former placement cannot be recovered. Saves validate the graph contract and manifest as a whole, reject stale manifest hashes and duplicate versions, preserve earlier revisions, and refresh the project tree. Automated source tests verify content hashes, layout roundtrips, legacy fallback, immutable revision retention, duplicate rejection, and stale-write conflicts. The editor supports only the existing pure node allowlist. Multiple roots, external file watching, and catalog migration remain open. See [project folders and workspaces](project-workspaces.md), the [user guide](user-guide.md#organize-work-with-projects), and [architecture](architecture.md).

## Consistent Studio navigation and responsive page layouts

Every Studio route now uses one shared navigation component with the same project selector, theme selector, links, and active-page indication. Common content surfaces remain aligned across authoring, project, MCP, model, skill, agent, function, and observation pages. The function workbench responds to its available container width, and workflow-name/project controls reflow without page-level horizontal scroll. The browser audit checks all eight routes on desktop and mobile and verifies no viewport overflow at normal and 200% text sizes. The graph stays independently scrollable inside the workbench. No page-specific capability or route changed.

## Single-root folder workflow editing

The Studio can open one explicitly entered local project path, validate its versioned manifest and bounded asset set, show manifest-indexed files, and edit workflow definition/layout source directly. `StudioFolderWorkflowSource` is distinct from the catalog API: it does not create revisions, publish/activate workflows, or store runs in the project. A save writes a content-addressed workflow JSON file and atomically replaces the manifest; stale hashes are rejected, and prior immutable files are retained for readers that loaded the prior manifest. Aggregate directory reads and saves are bounded to the project source size limits. Regression coverage verifies create/update, layout persistence, stable IDs, and stale-edit rejection. The current editor supports one root and workflow files only; it does not provide a hierarchical tree, external change watching, reusable-asset editing, catalog migration, or cross-process compare-and-swap guarantees. Execution evidence stays in the host run store. See [project folders and workspaces](project-workspaces.md) and the [user guide](user-guide.md#organize-work-with-projects).

## Portable source directory adapter foundation

The source manifest format now has a directory reader/writer beside the ZIP adapter. Both share the same schema, hash, path, and size validation; directory reads reject symbolic links/reparse points, and writes require a fresh destination and add a `.gitignore` entry for `.vaw/`. A round-trip regression test verifies ordinary manifest-listed source content and confirms local metadata is not created in the project tree. This helper is not yet connected to the Projects UI or active catalogs: folder opening, file watching, and folder-backed authoring remain roadmap work. See [project folders and workspaces](project-workspaces.md).

## Studio pure graph-function library and editor

Studio now provides a local Function library with folders and immutable content-hash-pinned revisions, plus a visual editor for named `string`/`boolean` interfaces and bounded deterministic graph bodies. Authors can create a function, add/remove/rename/type its boundary pins, add constants and pure operators, wire typed pins, inspect validation issues, save a revision, copy a revision into a new version, move it between folders, and remove its library entry. A Studio asset envelope preserves canvas layout in the local library and portable project source/archive assets; the executable function snapshot hash remains based only on its definition. Legacy raw entries load with a generated layout. Valid workflow authors can upgrade to schema 1.27 and insert an exact saved revision as a function-call data node. The workflow embeds the immutable snapshot, so execution does not depend on the local library. Library persistence/reopen, layout roundtrip, legacy fallback, immutable version behavior, invalid-body rejection, typed boundary mismatch, and snapshot independence have automated tests. Current gaps are standalone function-asset package import/export, collaborative drafts, and agent-tool exposure; the pure runtime constraints described below still apply. See [the user guide](user-guide.md#create-and-reuse-pure-graph-functions), [architecture](architecture.md), [graph values](graph-values.md), and [roadmap](roadmap.md).

## Pure graph-function runtime contract

Schema 1.27 introduces reusable pure function definitions embedded as immutable, content-hash-pinned snapshots on typed `core.function.call` data nodes. Function bodies are data-only acyclic graphs with named string/Boolean inputs and outputs, bounded to 64 nodes, 128 connections, 16 interface pins, 16,384 characters per string, and 1,024 evaluated function-node visits per workflow run. Supported operations are string and Boolean constants, text concatenation, string equality, and Boolean NOT. Calls evaluate lazily when a consumer requests an output; their exact function revision, hash, arguments, output, and nested evaluated values are included in run evidence for replay without live calls. Workflow packages declare the graph-function dependency. Regression tests cover body/schema validation, hash and serialization stability, effect-node and cycle rejection, package metadata, lazy execution, and nested audit evidence. The Studio library/editor is described in the stage above; agent tool invocation and standalone function-asset packages are still not implemented. See [visual programming and function design](graph-values.md), [architecture](architecture.md), and the [roadmap](roadmap.md).

## Bounded Boolean branch topologies

Schema 1.26 expands the explicit typed Boolean graph from one fixed Branch to an acyclic decision graph with 1–8 Branch nodes and up to 16 success/failure Terminate nodes. Each Branch chooses one route to another Branch, Output, or Terminate. Shared targets accept alternative incoming execution edges and execute once when reached; merges are not required. Validation requires all executable nodes to be reachable and a structural Input-to-Output route, while documenting that predicate feasibility cannot be proven statically. Studio upgrades valid schema 1.25 drafts and allows bounded node additions without automatic wiring. Core tests cover bounds, unreachable Output, and schema upgrade; Agent Framework tests exercise true/false nested routes, audit events, termination outcomes, data-evaluation boundaries, and reconvergent one-time execution. Prior graph schemas retain their validation and edge semantics. Adversarial review rejected expanding this milestone to generic transforms, loops, parallel fan-out, or compatibility checkpoints. See [graph values](graph-values.md), [graph editor](graph-editor.md), [user guide](user-guide.md#try-the-typed-graph-preview), and [roadmap](roadmap.md).

## Typed Boolean primitives

Schema 1.25 adds an explicitly configured Boolean constant and pure Boolean NOT to the schema 1.24 typed branch proof. The Studio offers a guarded upgrade from a valid 1.24 draft and exposes both nodes as unconnected library items. Boolean data pins are type-checked, constants preserve explicit false values through JSON round trips, and both operators evaluate only when reached through a data dependency. Framework tests cover true/false constants, NOT inversion, selected branch routes, terminal outcomes, and typed audit evidence. The browser smoke verifies the upgrade, socket types, constant editing, and persistence. This leaves the proof topology unchanged; broader branch and terminal arrangements remain roadmap work.

## Typed Boolean branch proof

Schema 1.24 extends the explicit execution/data graph with ordinal string equality and a Boolean Branch that chooses exactly one route. Its bounded proof graph routes true to Output and false to a failure Terminate; the failure handoff does not activate Output or evaluate Output data. Run evidence records typed data values with data-connection IDs, the selected branch output, and the delivered execution route. Studio can upgrade a valid schema 1.23 draft and exposes the new nodes in the library without auto-wiring them; incomplete topology remains saveable as a draft while publish/run validation stays strict. Regression tests cover both outcomes, typed-port rejection, branch audit evidence, output evaluation boundaries, and draft preservation; the full suite passed 359 tests with two optional tests skipped. The Studio browser smoke and all 38 local Mermaid diagrams also passed. This is a small mixed-graph proof, not general branching or a complete Blueprint toolbox.

## Explicit execution and data graph authoring preview

Schema 1.23 introduced a deliberately opt-in Studio preview for blank Input-to-Output graphs with separate execution and data pins, editable string constants, and pure text concatenation. See [typed Boolean branch proof](#typed-boolean-branch-proof) for the schema 1.24 extension and [typed Boolean primitives](#typed-boolean-primitives) for schema 1.25. This is not the complete visual-programming milestone: general branch topologies, reconvergence, migration of legacy node families, and the wider toolbox remain open. See [graph values](graph-values.md), [graph editor](graph-editor.md), and [roadmap](roadmap.md).

## Bounded agent outcomes and resource budgets

Schema 1.21 adds optional `MaxTurns` (positive model-response count, default eight; `-1` means unlimited), `TimeoutSeconds` (positive whole number; when configured it overrides the model connection default), and structured JSON results to agent nodes. Schema 1.22 adds `MaxTotalTokens` (1–100,000) shared across the parent and delegated profiles. The adapter limits output allowance from remaining usage and rejects a response before executing its requested tools when reported use exceeds the budget. Missing provider usage fails closed. Oversized prompts and parallel requests already in flight may exceed the threshold, so this is not a strict billing cap. Core contract tests and deterministic adapter tests cover schema bounds, submitted results, timeout, turn exhaustion, usage-based allowance reduction, and tool suppression after an over-budget response. Cost limits remain planned. The opt-in Linux Qwen3 1.7B inference test also submits `1+1` through its assigned `submit_answer` MCP binding and verifies provider-reported aggregate usage stays within the configured budget. The test uses the generated model-facing function name (`tool_0`) while asserting the stable MCP binding identity in audit evidence. Structured-result real-model compliance is still unqualified. See [agent result and budget design](agent-results-and-budgets.md) and [roadmap](roadmap.md).

## Flexible graph authoring and explicit termination outcomes

The canvas starts centered at 100%, with fit-to-graph available as a separate action. Library additions create unconnected nodes; condition and merge nodes are independent, while wire-context insertion remains explicit. Draft serialization accepts disconnected/missing-pin topology so authors can save incomplete work; strict publish/run validation still reports and blocks unresolved issues. Termination nodes support separate success/failure outcomes, multiple exits, and audited run statuses. Regression coverage verifies adding a single condition without a merge, saving/disconnecting/reconnecting an exit, and executing two outcome paths. The roadmap now defines a Blueprint-inspired deterministic visual language and bounded reusable graph functions; the agent result/budget milestone is specified separately. See [conditional graphs](conditional-graphs.md), [workflow termination](workflow-termination.md), [visual programming and function design](graph-values.md), and [roadmap](roadmap.md).

## Named workflows in Studio

Workflow definitions can carry an optional, bounded display name without changing the stable `WorkflowId` used by storage, calls, packages, and host integrations. New drafts start as **Untitled workflow**; the designer edits the name and searches/selects workflows by either name or ID. Run history and observation surfaces use the name captured in that run's revision. Legacy definitions without names remain valid and continue to display their ID. Rename operations are versioned with the definition and therefore appear in revision comparisons and hashes.

## Sequential bounded parallel blocks

Schema 1.20 allows up to four sequential fork/barrier blocks, each with two branches. Studio inserts later blocks on string paths after earlier selectors and removes a selected fork-to-join region independently. Older schemas remain limited to one. Core validation rejects nested and independently concurrent blocks, and adapter execution keeps separate join state and audit events for every block. Core regression tests cover sequential insertion/removal, schema compatibility, the four-block bound, and condition arms that would execute blocks independently; the Agent Framework test executes two joins and checks their recorded results. See [parallel graphs](parallel-graphs.md), [architecture](architecture.md), and [the user guide](user-guide.md#run-two-paths-in-parallel).

## Nested versioned workflow calls

Schema 1.19 adds `core.workflow-call`, which embeds the exact published child definition, revision ID/version, and content hash. The Studio adds a call node from its reusable-workflow panel; copy-on-insert remains available when editable expansion is preferred. Runtime child events use the parent run ID/revision and sequence with slash-separated scope paths, preserving one journal and allowing read-only child evidence playback. Child success returns its declared string or JSON result; child no-result termination propagates as successful parent termination. Parent parameters map into compatible declared child parameters; unmapped values retain child defaults. Mappings are part of the pinned parent definition and resolved per run. Call pins inherit the child's string or JSON Input and Output types, with the normal graph validator enforcing compatible connections. The Studio lists pinned dependencies and reviews child definition, requirements, and mapping changes before an explicit revision upgrade. Accepted upgrades change only the parent draft and can be undone; the old child snapshot remains until publication. Authors can inspect the pinned or candidate child definition in a read-only canvas without leaving the parent. Validation bounds calls to three nested levels and 1,024 expanded nodes and rejects re-entry cycles, invalid hashes, unsupported child contracts, and incompatible mappings. A follow-up scoped-approval milestone binds nested approval requests to the root run plus exact pinned child path/revision and action; dispatch still requires independent host authorization. Core contract/editing and Agent Framework execution tests cover the feature. See [nested workflow calls](nested-workflows.md), [approvals](approvals.md), and the [roadmap](roadmap.md).

## Scoped approvals in nested workflows

Pinned child definitions may include an approval immediately before an MCP action. Each request identifies the root run/revision/hash, all exact call-node and child-revision/hash edges, the executing child definition, the action input and tool contract, and the reserved invocation. Its event `ScopeId` must match the captured call path, and the approval controller revalidates the embedded definition chain and immediate approval-to-tool edge before accepting a decision. Playback identifies the parent call that is waiting and renders the child path/revision. Two parallel branches that can both suspend for approval are rejected because one run has one pending approval slot. Root-run approval tests remain compatible; runtime tests cover scoped approval, exact action, decision evidence, and no dispatch before approval. Restart still interrupts the process-owned continuation. See [approvals](approvals.md) and [nested workflow calls](nested-workflows.md).

## Recursive JSON object and collection mapping

Schema 1.16 adds recursive declarative mapping contracts to JSON projection fields. Object contracts select named source properties through relative JSON Pointers, with required/optional handling; array contracts map each item through its own value schema. Unlisted object properties are omitted, array size is capped at 64, and schema depth/node/configuration limits bound work. Runtime checks kinds and required paths before emitting output; node failure evidence explains the path/type issue without copying the source value. The Studio uses a recursive property editor, and saved definitions preserve earlier schemas and hashes when no new contract is used. Core serialization/validation tests and Agent Framework mapping, playback, missing-property, and size-limit tests cover the behavior. See [architecture](architecture.md), [roadmap](roadmap.md), and the [user guide](user-guide.md#select-and-project-json-input).

## Typed JSON projection fields

Schema 1.15 lets each field in `core.json.project` declare the expected top-level JSON value kind. The runner checks the selected value before writing it and fails with a node event that identifies the field and expected/actual kind without echoing the value. Studio exposes the type selector and displays the projection contract on the graph card. Missing type metadata preserves schema 1.13/1.14 pass-through semantics and canonical serialization. This is a shallow contract; nested object properties and array items remain unvalidated. Core round-trip/schema-upgrade tests and an Agent Framework success/failure execution test cover the behavior. See [architecture](architecture.md), [roadmap](roadmap.md), and [the user guide](user-guide.md#select-and-project-json-input).

## Bounded parallel graph block

Schema 1.14 adds a visible fork, two named branch anchors, and a barrier join that returns the branch values as bounded JSON. Studio inserts the block into a connected string path, preserves a downstream string route through a JSON selector, gates invalid branches from execution/publication, and supports whole-block preview/removal with reconnect and undo. The Agent Framework 1.26.0 adapter uses concurrent in-process execution and `AddFanInBarrierEdge`; synchronized fake agents prove overlapping calls, deterministic named results, join transfer/progress evidence, output limits, and failure without partial output. The browser smoke covers authoring, branch insertion, publication/run, progress playback, removal, reconnect, and undo. Run playback remains event-only. External side effects in a successful branch cannot be rolled back when another branch fails. The block is limited to two simple paths, one block per workflow, no nested conditions/joins/loops, and 16,384 serialized result characters. The full .NET suite passed 283 tests with one opt-in live-model test skipped. See [parallel graphs](parallel-graphs.md), [architecture](architecture.md), [roadmap](roadmap.md), and [user guide](user-guide.md#run-two-paths-in-parallel).

## Bounded JSON projection and typed workflow output

Schema 1.13 adds a declarative `core.json.project` node for mapping 1–16 named RFC 6901 pointers into a new JSON object, plus a JSON-typed Output pin. The Studio exposes editable field rows and makes projection nodes available only on JSON paths. Core validates unique names, pointers, field/configuration limits, and typed connections; the runner bounds JSON depth and input/output payload size and records the exact projected object in normal node/transfer evidence for playback. A selected value can be converted back to text before an existing agent or MCP node. At that milestone, general expressions, dynamic mappings, arbitrary object ports, loops, and graph-level joins were still planned; schema 1.14 subsequently shipped the bounded parallel join described in [parallel graphs](parallel-graphs.md). See [architecture](architecture.md), [roadmap](roadmap.md), and [the user guide](user-guide.md#select-and-project-json-input).

## Parent-coordinated sequential subagent turns

The parent agent can select assigned delegate tools in sequence, and those child invocations share bounded coordination channels scoped to that parent invocation. Parallel team fan-out uses a distinct per-team board. Every exchange remains attached to the child invocation for audit and replay; tool isolation, one-level delegation and the shared four-child limit remain in force. A deterministic end-to-end regression test verifies manager-selected order and a later delegate reading the earlier delegate's message. Release build passed with zero warnings; the full suite passed (271 tests, one opt-in live-model test skipped), and all 24 Mermaid diagrams rendered. See [agent profiles](agent-profiles.md), [architecture](architecture.md), and the [user guide](user-guide.md#design-reusable-subagents).

## Run-scoped agent team channels

Schema 1.12 adds channel IDs to immutable agent profiles. Parallel team members receive send/read functions only for channels pinned to their exact profile revision. Each team invocation gets a fresh append-only board with bounds of 4,000 characters per message, 32 messages per channel and 64 total; every write and read is captured under the member's child invocation, including the exact messages returned to the model. Playback uses those records and makes no calls. Core validation, package round-trip, library validation, cross-run isolation, and denied-membership tests cover the new contract. Release build and full test suite pass (270 passed; one opt-in live-model test skipped); all 24 Mermaid diagrams rendered locally. Manager-directed turns and shared durable memory remain out of scope. See [agent profiles](agent-profiles.md), [architecture](architecture.md), and [the user guide](user-guide.md#design-reusable-subagents).

## Rewire compatible pins and preserve incomplete drafts

The canvas connects matching typed pins by dragging an output to a highlighted input, and disconnects a selected wire from its properties or context menu. Incomplete graph topology can be saved and reloaded as a draft, with duplicate-property, size, and non-topology validation still enforced. A visible issue list identifies each failure; publish and activate are disabled, export is gated, and runtime validation remains strict. Canvas display order is deterministic for invalid/cyclic drafts and is never used to schedule execution. Core and hosting tests cover draft round-trip, strict revision rejection, publish rejection, and successful reconnection. The graph browser smoke covers saving while invalid, gating publication/activation, pin rewire, and agent settings. See [graph editor](graph-editor.md), [conditional graphs](conditional-graphs.md), and the [user guide](user-guide.md).

## Keep historical run evidence readable across validation changes

Run snapshot reads verify their stored content hash and event sequence without reapplying current workflow validation to the archived definition. New revisions and execution admission remain strictly validated; resuming a saved checkpoint also revalidates before execution. This prevents an older recorded agent setting from aborting Studio startup after schema rules evolve. A Windows regression test starts the local runtime with such archived evidence and verifies it is cleanly marked interrupted. See [run storage](run-storage.md).

## Per-agent inference limits and reasoning off

Schema 1.10 adds an optional bounded `maxOutputTokens` setting (1–16,384) to agent configuration. Absent values retain the legacy 1,024-token cap, while configured values are passed to Microsoft.Extensions.AI and captured in model request evidence. Providers that advertise reasoning controls now expose explicit **Off**, mapped to `ReasoningEffort.None`, distinct from provider default. Numeric reasoning enum assignments remain stable (`0=Low`, `1=Medium`, `2=High`) so saved drafts, published revisions, and run history retain their original meaning; explicit Off uses a new value and requires schema 1.10. Core validation, Agent Framework evidence, and OpenAI-compatible request tests cover bounds and propagation. See [architecture](architecture.md) and the [user guide](user-guide.md).

## Confirmed conditional block removal

Core can preview and remove an isolated condition region, including both arms, nested decisions and the matching merge, then reconnect the surrounding path. The editor lists affected nodes and requires confirmation; cancellation leaves the draft unchanged. Context-menu and Delete-key entry points use the same flow, and shared undo/redo restores definition and layout. Outside incoming paths cause rejection, preserving unrelated branches. Published revisions and historical runs stay unchanged. Eight focused Core cases cover empty/nested/sequential regions, immutable source definitions and shared-boundary/non-condition rejection. The conditional browser smoke verifies cancellation, confirmation, definition/layout undo/redo, saved removal, unchanged published v1, and framework execution of the reconnected v2. Release build, full .NET suite (246 passed; one opt-in inference test skipped), and documentation link/Mermaid checks passed locally on Linux. See [conditional graphs](conditional-graphs.md).

## Workflow parameter descriptions

Workflow parameter definitions can include bounded optional descriptions. The Studio edits these descriptions and displays them beside published-run inputs; reusable insertion carries them with copied parameter definitions. Null descriptions remain omitted from JSON, preserving old canonical workflow hashes. Core tests cover round trips, bounds, edits and reuse. See [workflow parameters](workflow-parameters.md) and the [user guide](user-guide.md).

## Deterministic text-template node

Schema 1.7 adds a `core.text.template` graph node that composes bounded literal text, its connected input, and declared workflow parameters using the structured prompt-part format already used by agents. The Studio reuses the token editor and preview; runtime output appears in normal node and transfer evidence. Reusable insertion copies and remaps referenced parameters, and referenced values cannot be deleted from the workflow. The node is deterministic and has no expression evaluation or external side effects. Core validation/round-trip/package tests, Agent Framework execution tests, the workbench browser smoke, and docs checks cover the feature. See [workflow parameters](workflow-parameters.md), [user guide](user-guide.md), and [architecture](architecture.md).

## Bounded JSON input and pointer selection

Schema 1.9 adds a JSON-typed entry output and a bounded RFC 6901 selector that emits text, allowing existing string nodes to continue unchanged. Core validates typed edges and inserts/removes the entry-selector boundary as one edit; the Agent Framework runner normalizes JSON, selects object keys or array indexes, and preserves the value through audit and playback. The Studio exposes **Use JSON input and add a value selector** and a JSON Pointer field. Tests cover pointer escaping/limits, incompatible connections, atomic editing, string/array/root selection, and recorded output. General object ports, mapping expressions and typed joins remain planned. See [user guide](user-guide.md), [architecture](architecture.md), and [roadmap](roadmap.md).

## Persistent Studio theme selector

The Studio navigation now offers Dark Modern, Light Modern, VS Code Dark+, Solarized Dark, Nord, Monokai, GitHub Dark, GitHub Light, Dracula, Neon Glow, and High Contrast themes. A small synchronous bootstrap applies the browser's saved choice before the stylesheet paints; the interactive selector updates and persists it. A Blazor enhanced-navigation callback restores the root theme after route transitions, and each selector reads the saved preference. Theme tokens adapt the standalone shell and graph together, while embedded components continue to inherit their host's own theme.

## Retro Drive Studio theme

The Studio theme selector now includes Retro Drive, a dashboard-inspired palette based on smoked instrument panels, amber indicators, warm ivory labels, and cyan/green display traces. It remains a standalone Studio theme; embeddable components continue to inherit host-owned tokens.

## Schema-driven direct MCP arguments

Direct MCP nodes now expose the selected tool's bounded input schema in the properties pane. Authors can configure portable static JSON arguments without hand-writing the object; clearing the configuration restores the existing connected-input behavior. Configured arguments make the direct node's data input optional, while broker authorization, contract pinning, live schema validation, and audit evidence remain unchanged.

Schema 1.37 adds Blueprint-style top-level argument pins for direct MCP nodes. Each discovered property is persisted as a stable typed `arg.<property>` pin; connected values override matching static values, and the runtime reconstructs the complete JSON object before the normal broker validation. Schema 1.36 remains unchanged for compatibility. Nested values remain JSON pins and automatic schema migration is intentionally deferred.

## Flat Studio theme and fitted graph viewport

The standalone Studio now uses a flat, light neutral shell with a single dark graph canvas. Its subdued grid fills the whole viewport and tracks camera pan and zoom rather than ending at the logical world's edge. Initial attachment and **Fit graph** center visible nodes using transient camera offsets; workflow layout coordinates remain unchanged. Browser review covers full-width grid, initial centering, fit, graph gestures, theme contrast, forced colors, reduced motion, and narrow embeds.

## Windows history compatibility and read-only browser smoke

Portable LF hashes now verify historical Windows CRLF hashes for run records, published revisions and shared packages without rewriting their evidence. New definitions and layouts use portable line endings. A Linux browser smoke assertion now checks that insertion controls are absent in read-only revision view without opening an authoring-only disclosure. Tests cover legacy hash rehydration, file-journal listing and package import; the real Chromium Studio smoke passes locally.

## Cross-platform definition hashes and file-journal replacement

Windows CI exposed portability defects: indented JSON used CRLF when hashing workflow definitions, and file replacement could fail while another in-process store instance read the snapshot. Serialization now normalizes line endings to LF. File journal instances for one path now share an in-process access gate, with bounded retries for transient OS file-access denials. The local stdio MCP fixture pins an initialize-capable protocol version so cold startup cannot cause a false fallback from the newer discover probe. The .NET matrix, including Windows, passes.

## schema-1.6 workflow parameters

Workflow definitions can carry up to 64 non-secret string parameters with stable IDs, display names and defaults. The Studio can manage them, add explicit workflow-value getter nodes, and supply per-run overrides for a published revision. Existing run client/store contracts remain supported through optional parameter capabilities; built-in file/SQLite headers capture the fully resolved map for playback. Reusable workflow expansion copies used parameter definitions and remaps conflicts. Core, Agent Framework and Hosting tests cover validation, legacy JSON omission, defaults, overrides, execution, persistence and replay; a headless browser smoke test covered author, save, publish, run and playback. Types, required parameters, exposed agent configuration pins and mutable variables remain future work. Current controls and boundaries are in [workflow parameters](workflow-parameters.md).

## Provider management and Studio theme refinement

The Settings page now lists, creates, edits in place, and deletes locally saved OpenAI-compatible model connections by stable ID. Credentials remain protected in local Studio storage, descriptors remain secret-free, and executor registrations update immediately. The settings and standalone Studio shell use a more restrained graphite and steel-blue theme with improved hierarchy and responsive forms. Host configuration remains available for consumer services. The Studio remains a loopback-only unauthenticated prototype; multi-user credential management and workflow-reference checks before deletion are not implemented. See [provider configuration](model-providers.md) and [design system](design-system.md).

## canvas-first creation workbench

Blank connected drafts, searchable node library, contextual search/actions, drag placement and output-pin creation use validated insertion paths. Compact cards and a selected properties panel prioritize the canvas. Designer/run views preserve state; native fullscreen includes editing/playback controls, with fit, keyboard shortcuts, filtering and clearer recorded paths. Browser validation covers these gestures and existing authoring/version/run flows. Arbitrary existing-node rewiring, explicit branch-block removal and broader graph semantics remain planned. See [graph editor](graph-editor.md) and [user guide](user-guide.md).

## read-only interrupted-run reconciliation

Cursor-bound assessments explain unsettled node/model/tool/approval facts, terminal status and saved/missing/invalid/advanced checkpoint state. New per-call model/tool IDs distinguish repeated calls, with conservative legacy matching. Built-in observers, the protected GET API, bounded HTTP client and embeddable observation panel expose metadata without native state, payloads or mutation controls. Eligibility is suppressed on cursor races and source failures and does not grant ownership. See [run reconciliation](run-reconciliation.md).

## deterministic saved-suspension recovery

An opt-in consumer host saves native Agent Framework JSON checkpoints atomically with audit/state and resumes an exact deterministic suspension after host replacement. Tests and a forced-process-loss proof cover stable revision/identity, both branch paths, no duplicate nodes, ownership exclusion, stale evidence and rollback. Built-in ordinary runtime hosts also enforce exclusive local ownership. SQLite run schema 2 migrates existing evidence. Studio's queue retains interruption semantics; external-node/approval and arbitrary-crash recovery remain excluded. See [checkpoint recovery](checkpoint-recovery.md).

## run storage boundary

The local runtime/controller now consume a host-selected `IWorkflowRunStore`. Atomic bounded batches and expected-sequence checks keep evidence and derived status consistent. File snapshots preserve legacy reads; an optional dedicated SQLite store supplies transactions and independent-connection concurrency checks. Studio/service configuration, observer consistency and rollback/conformance checks are implemented. Runtime ownership remains exclusive; restart still interrupts unfinished work. See [run storage](run-storage.md).

## opt-in host-configured model adapter

Studio and the standalone service can explicitly enable stable-ID OpenAI Chat Completions connection factories. The optional library owns fresh bounded SDK clients, server-side secrets, cancellation/deadlines, safe categorized failures and no automatic retries. Agent evidence adds response IDs and token usage. Unit and local protocol/browser/service fixtures verify the SDK path without live model calls. Credentialed deployment qualification remains planned; see [model providers](model-providers.md).

## exact-action approvals and deterministic vertical slice

Schema 1.5 adds a backend-owned approval immediately before an explicit MCP tool. Requests bind the pinned revision, exact input/binding/contract and reserved invocation identity; decisions are sequenced and host-attributed. Waiting releases execution ownership, while pause and approval remain independent. Designer/review/playback controls and the scripted Agent → Condition → Approval → MCP → Output browser proof are implemented. Remote observation remains read-only. Restart interrupts unfinished waits; durable approval recovery and live-model validation remain planned. See [approvals](approvals.md).

## Latest completed milestone: bounded workflow loop

Schema 1.17 adds one explicit bounded loop over a linear string body. Agent Framework feedback-edge execution is verified end to end: predicate changes exit normally, a continuing predicate stops at the configured 1–16 cap, and loop audit records expose iteration start/completion and stop reason for replay. The Studio inserts and configures the loop, edits its body with ordinary processing/agent/MCP nodes, and removes the structure while retaining the body as a one-pass route. Validation rejects extra loops, other control nodes, unsupported body nodes, invalid predicates, wrong return edges, and cycles outside the validated loop. Core and runtime tests cover these contracts. See [bounded loop graphs](loop-graphs.md).

## conditional string graphs

Schema 1.4 adds approved ordinal predicates, true/false string ports, exclusive merges and bounded acyclic validation. The adapter uses real Agent Framework conditional edges and records decisions separately from actual deliveries. Designer controls insert conditional blocks, edit predicates/arms and preserve undo/version/package/playback behavior. Existing linear schemas/hashes remain supported; linear-only reuse rejects routing graphs. See [conditional graphs](conditional-graphs.md). Richer port types and nested or variable control-flow structures remain planned. Bounded parallel joins shipped in schema 1.14; see [parallel graphs](parallel-graphs.md).

## opt-in consumer observation

Consumers can explicitly map policy-protected read-only summary/snapshot/cursor endpoints. Studio attaches to operator-configured remote sources through a separate HTTP observer; the same `WorkflowObservationPanel` embeds in consumer Blazor apps without execution services. API, lifecycle and two-process browser checks cover read-only access, playback, source replacement and reconnect. The endpoint is disabled by default in the sample. See [consumer observation](observation.md) for authentication, current default-layout/polling limits and host-owned audit requirements.

## standalone service integration and local hosting API

The [standalone consumer](../samples/WorkflowService/README.md) runs reviewed pinned packages directly or through a backend-owned queue without Studio/Blazor or an authoring catalog. It verifies real MCP, independent node grants, missing/drifted dependency rejection and host-stop cancellation. The sample and Studio share the additive [local hosting DI helper](hosting-sdk.md). Project-reference consumption is documented; stable NuGet distribution and minimal worker dependency packaging remain planned. Conditional string graph authoring is now implemented; the deterministic approval/MCP slice is implemented; the opt-in host-owned SDK adapter is protocol-verified; credentialed live qualification remains planned.

## cooperative pause/resume

Process-owned runs can start paused, pause between nodes, resume without reexecuting completed nodes, and cancel while paused. API and optional embedded controls share sequenced audit events; recorded playback includes pause state. Paused contexts release the execution permit so other admitted work can run. The 32-context budget includes paused runs. The ordinary queue interrupts unfinished continuations; the separate deterministic checkpoint host now supports saved-suspension recovery. See [run control](run-control.md).

## reusable workflows and sharing

Core contracts, Studio controls, and end-to-end validation are complete. The headless service-integration proof and local registration API are complete. Conditional string graph authoring is now implemented; the opt-in host-owned SDK adapter is protocol-verified; credentialed live qualification remains planned.

| Deliverable | Status | Completion evidence |
| --- | --- | --- |
| Extract consecutive processing steps into a separate workflow | Complete | Core extraction preserves annotations and rejects invalid selections |
| Insert an exact published source version with fresh identities | Complete | Copied requirements/source identity, bounded expansion, real framework execution without source lookup |
| Portable definition/layout packages with hashes and requirement manifests | Complete | Canonical round trips, bounds, mismatch/unknown/duplicate-property rejection |
| Save a labeled section as a reusable draft and choose published versions | Complete | Real controls preserve the consumer draft and source history |
| Insert a selected version with shared undo/redo and review requirements | Complete | Browser checks, saved consumer output, reload and immutable source-version behavior |
| Export/import the full package through Studio | Complete | Layout/annotation preservation, local-only import, explicit save/publication, no permission transfer |

This increment uses copy-on-insert template expansion, not recursive runtime prefab calls or automatic instance upgrades. See [reusable workflow contracts](reusable-workflows.md).

## agent tools from pinned pure graph functions

Schema 1.28 lets authors assign up to eight exact immutable function snapshots to an Agent node. The model sees only those selected tools. Calls validate the named contract, enforce per-agent call, evaluated-node, and payload bounds, return declared outputs, and record correlated inputs/outputs/evaluated values/failures with the content hash for replay. MCP grants are not inherited by a graph function, which remains a pure deterministic graph. Workflow packages include exact function snapshots and `graph-function-tool` requirements. Runtime regression coverage verifies invocation, audit correlation, package requirements, and that unassigned library functions are not discovered.

## Local project-scoped authoring catalogs

Studio supports creating, selecting, archiving, and restoring local projects. Workflow catalogs, skills, agent profiles, and graph functions are scoped to the selected project, and project-aware API routes use that catalog. First access to Default Project migrates legacy workflow data with a SQLite backup and copies legacy JSON assets without deleting their sources. Regression tests preserve workflow identity, immutable revisions and hashes, layouts, generations, active/head pointers, and audit entries. This is a local SQLite/JSON transition format; portable folder-backed Git projects, a multi-project file tree, and consumer bundles remain active roadmap work. See [project folders and workspaces](project-workspaces.md).

The Projects page downloads a deterministic ZIP source snapshot containing a versioned project manifest and hashed workflow/layout, skill, agent-profile, and graph-function JSON files. Workflow source keeps incomplete disconnected drafts so source capture does not imply publishability. The bounded reader verifies safe paths, index/content agreement, and hashes without extracting files. Studio can import a validated archive as a new isolated project, restoring workflows/layouts and reusable assets without copying execution evidence or host settings. The import does not merge into an existing project, and the SQLite/JSON catalogs remain authoritative; folder-backed Git editing is not implemented. See [project folders and workspaces](project-workspaces.md).

## Folder-backed agent profile authoring

The single-root source editor now creates immutable, content-addressed agent profile revisions in the project manifest. Workflow editing in folder mode receives the project's profile, skill, and graph-function libraries, while model connections, MCP contracts, credentials, and grants remain host-owned. Stale manifest writes and duplicate profile revisions are rejected; catalog-backed projects are not mirrored or rewritten. Graph-function source editing, multiple roots, external file watching, and explicit catalog migration remain unfinished. Focused archive/source tests verify profile content hashes, retained revisions, folder-only visibility, duplicate rejection, and stale-write conflicts. See [project folders and workspaces](project-workspaces.md).

## Agent node execution telemetry and timeout precedence

The run graph shows model token totals, the last completed prompt's context-window usage while a later request remains in flight, cost when provider usage and prices are available, and recorded host context-compaction counts. Provider-internal compaction remains unobservable, and the adapter does not currently enable a host compaction strategy. An agent node timeout now overrides the model connection's default timeout; blank node settings use the connection timeout. Regression tests cover the 300-second node override over a 120-second connection default, in-flight context labeling, observed compaction counting, and unconfigured context metadata.

## Registered folder projects in Studio navigation

Opening a validated Git folder now records its canonical path in the local Studio project registry. The project selector and Projects list reopen registered folder roots in the source editor, and the folder's project-specific source snapshot can be downloaded. Project-scoped catalog APIs return HTTP 409 for folder-backed project IDs rather than silently selecting catalog storage. Folder-backed publishing and workflow execution are still unavailable; catalog migration and multi-root workspace support remain roadmap items. Regression tests cover idempotent folder registration, registry persistence, and isolation from catalog storage.

## External project-folder change reconciliation

The open folder editor now coalesces filesystem notifications and also performs a full source validation every 20 seconds, covering missed notifications and indexed-file edits that leave the manifest unchanged. It preserves dirty workflow, skill, profile, and graph-function buffers, offers a reload only for a clean editor, and requires explicit discard-and-reload for dirty buffers. Reload validates the current folder again at click time. A Playwright regression covers atomic manifest replacement, clean reload, and dirty-buffer protection. The save-time optimistic manifest comparison remains susceptible to an external process replacing the manifest between comparison and replacement; notifications and polling improve conflict visibility but do not provide a filesystem lock.


## Multi-folder project workspace

The folder editor now supports eight simultaneously open project roots in separate tabs. Each root keeps a keyed editor component mounted in the current browser circuit, preserving its workflow/asset form state while inactive; only the active root owns a filesystem watcher, and reactivation validates the latest manifest snapshot. Open-tab order and selected project ID are stored in Studio-local `open-workspace.json`, not in project source. Closing a dirty tab requires explicit confirmation. A Studio restart or browser refresh restores tabs but cannot restore unsaved in-memory buffers. Store tests cover ordering, selection, close behavior, folder-only roots, the tab cap, and stale/archived project pruning; a Playwright smoke test exercises multiple roots and the dirty-close guard. This remains a single-operator authoring workspace, not collaboration or a portable workspace file. See [project folders and workspaces](project-workspaces.md).

## Parameterized agent token controls

Schema 1.29 allows an Agent node's per-response output-token cap and parent-plus-delegate total-token budget to select an integer workflow parameter instead of a static node value. Defaults must fall within each setting's finite range; run overrides are range-checked before agent execution and rejected rather than clamped. Agent start/model request evidence records the resolved cap, and the existing run header preserves the effective parameter map for playback. The settings editor displays the selected parameter and its default, and offers only compatible integer parameters. Provider IDs, endpoints, credentials, tools, skills, and host grants remain outside parameter bindings. Core validation/serialization and Agent Framework dispatch regression tests pass; timeout and model-turn bindings remain deferred pending host ceilings. See [workflow parameters](workflow-parameters.md) and [agent results and budgets](agent-results-and-budgets.md).

## Local project restore points

Folder projects can save manual content-addressed restore points under ignored `.vaw/snapshots/`. A point contains exactly one validated manifest and its indexed assets; preview compares manifest paths and hashes, and restore materializes missing immutable assets before atomically replacing the manifest pointer. Newer and unindexed files are preserved. Sidecar deletion or corrupt snapshots do not invalidate project source, and snapshot count/bytes are bounded without automatic pruning. Studio adds the root `.vaw/` ignore rule while preserving existing `.gitignore` bytes. Store regression tests cover restoration, stale previews, corrupt/deleted sidecars, unindexed files, and ignore-rule preservation; the Playwright smoke exercises capture, external-change reload, preview, and restore. Folder-backed workflow testing now runs against the selected validated restore point without restoring it; see [folder-backed execution](#folder-backed-project-execution-and-snapshot-testing). See [project folders and workspaces](project-workspaces.md).

## Folder-backed project execution and snapshot testing

The Studio can execute a saved workflow from a freshly revalidated project manifest or execute a workflow from a content-addressed restore point without restoring it. Both paths capture the definition and layout in memory, enqueue through the regular host runtime, and write evidence only to the host-selected run store. Run headers persist source kind, project/workflow IDs, manifest and asset hashes, captured layout plus hash, and restore archive hash when applicable; playback therefore remains tied to the exact source even after the folder changes. Stale source, dirty editor state, missing workflows, and invalid workflows block the run. Regression tests cover saved-source conflicts, archived source isolation, persisted provenance, and layout differences. `scripts/folder_execution_smoke.py` exercises both run paths in Chromium and checks that snapshot testing leaves the active manifest unchanged and no `.vaw/runs` data is created. See [project folders and workspaces](project-workspaces.md) and [run storage](run-storage.md).

## Workflow IDE document layout (2026-10-27)

Applied the approved document-oriented layout to both catalog-backed and folder-backed workflows: document tabs/actions, full-height graph, Configure/Inputs/Outputs inspector, selection/pan modes, graph overview navigation, Runs/Output/Problems panels, and compact status feedback. Settings holds workflow metadata and less frequent configuration. Folder project utilities moved behind the Explorer's Project tools action. Fixed project-ID string bindings, selection of newly discovered workflows, hidden-canvas centering, folder titlebar selection, and duplicate workflow tabs after saving to a new indexed path.

Validation: Studio Release build (zero warnings/errors), 17 focused rendering/lifecycle/architecture tests, both workflow IDE browser smoke suites including real deterministic execution and mobile layout, documentation links, and a live Impeccable CDP scan (zero primary findings; one advisory for the intentional graph grid). The full legacy publication-oriented browser suite was not part of this document-layout validation.

## Captured direct-run layouts and cross-platform diagnostics (2026-10-27)

Direct catalog starts now capture a matching saved layout without publishing a revision, and playback prefers the immutable run-header layout. Presentation-only snapshots are validated structurally and against the 1 MiB bound before persistence and on read; folder/restore-point provenance checks remain intact. The independent proponent/opponent/adjudicator review recommended this existing-field approach with pre-write validation and no synthetic provenance. Old readers that require provenance for every captured layout cannot read these new records.

The graph browser suite now follows the IDE document controls and direct-source execution, while retaining gesture, persistence, CAS, invalid-draft, and read-only playback coverage. New store regressions cover all direct-start variants, both store implementations, reopening, mismatched definitions, and malformed/oversized layouts. Windows CI also exposed a pooled SQLite connection in a tampering test and a platform-dependent stdio launch failure diagnostic. The test now closes its connection without pooling; Studio reports a stdio-specific executable/process-exit message without exposing configured environment values.

## Provider UI checks and isolated MCP demo configuration (2026-10-27)

The configured-provider browser proof now follows workflow settings, direct source runs, and Runs → Inspect. It retains tool-loop/evidence, rate-limit redaction, parallel user-message template, and independent direct/queued consumer execution checks. It additionally tests model-connection form testing, save, saved-entry testing, model editing under a stable ID, and deletion against a loopback protocol fixture; saves/edits must not issue model requests and connectivity probes request at most one output token.

The built-in pure MCP demo no longer loads or watches appsettings from its caller's working directory. A regression proves discovery succeeds beside invalid application JSON. Configured user MCP server processes keep their existing launch/configuration behavior. These checks use deterministic local responses, not a live model service.
