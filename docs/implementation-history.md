# Implementation history

Delivered milestones are listed in reverse chronological order. This page records completed work and its scope; for the current product boundary and priorities, see the [roadmap](roadmap.md). Detailed behavior and limits remain in the linked feature documentation.

## Nested versioned workflow calls

Schema 1.19 adds `core.workflow-call`, which embeds the exact published child definition, revision ID/version, and content hash. The Studio adds a call node from its reusable-workflow panel; copy-on-insert remains available when editable expansion is preferred. Runtime child events use the parent run ID/revision and sequence with slash-separated scope paths, preserving one journal and allowing read-only child evidence playback. Child success returns text; child no-result termination propagates as successful parent termination. Recursive package requirements list child revisions and derived dependencies. Validation bounds calls to three nested levels and 1,024 expanded nodes and rejects re-entry cycles, invalid hashes, non-string child contracts, and nested approvals pending a scoped approval contract. Core contract/package and Agent Framework child execution/termination tests passed; local link and Mermaid validation passed. An end-to-end browser smoke could not be run because this environment already has Studio processes owning the configured development ports/run store. See [nested workflow calls](nested-workflows.md) and the [roadmap](roadmap.md).

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

The Studio navigation now offers Dark Modern, Light Modern, VS Code Dark+, Solarized Dark, Nord, Monokai, and High Contrast themes. A small synchronous bootstrap applies the browser's saved choice before the stylesheet paints; the interactive selector updates and persists it. A Blazor enhanced-navigation callback restores the root theme after route transitions, and each selector reads the saved preference. Theme tokens adapt the standalone shell and graph together, while embedded components continue to inherit their host's own theme.

## Flat Studio theme and fitted graph viewport

The standalone Studio now uses a flat, light neutral shell with a single dark graph canvas. Its subdued grid fills the whole viewport and tracks camera pan and zoom rather than ending at the logical world's edge. Initial attachment and **Fit graph** center visible nodes using transient camera offsets; workflow layout coordinates remain unchanged. Browser review covers full-width grid, initial centering, fit, graph gestures, theme contrast, forced colors, reduced motion, and narrow embeds.

## Windows history compatibility and read-only browser smoke

Portable LF hashes now verify historical Windows CRLF hashes for run records, published revisions and shared packages without rewriting their evidence. New definitions and layouts use portable line endings. A Linux browser smoke assertion now checks that insertion controls are absent in read-only revision view without opening an authoring-only disclosure. Tests cover legacy hash rehydration, file-journal listing and package import; the real Chromium Studio smoke passes locally.

## Cross-platform definition hashes and file-journal replacement

Windows CI exposed portability defects: indented JSON used CRLF when hashing workflow definitions, and file replacement could fail while another in-process store instance read the snapshot. Serialization now normalizes line endings to LF. File journal instances for one path now share an in-process access gate, with bounded retries for transient OS file-access denials. The local stdio MCP fixture pins an initialize-capable protocol version so cold startup cannot cause a false fallback from the newer discover probe. The .NET matrix, including Windows, passes.

## schema-1.6 workflow parameters

Workflow definitions can carry up to 64 non-secret string parameters with stable IDs, display names and defaults. The Studio can manage them, add explicit workflow-value getter nodes, and supply per-run overrides for a published revision. Existing run client/store contracts remain supported through optional parameter capabilities; built-in file/SQLite headers capture the fully resolved map for playback. Reusable pipeline expansion copies used parameter definitions and remaps conflicts. Core, Agent Framework and Hosting tests cover validation, legacy JSON omission, defaults, overrides, execution, persistence and replay; a headless browser smoke test covered author, save, publish, run and playback. Types, required parameters, exposed agent configuration pins and mutable variables remain future work. Current controls and boundaries are in [workflow parameters](workflow-parameters.md).

## Provider management and Studio theme refinement

The Settings page now lists, creates, edits as a new immutable version, and deletes locally saved OpenAI-compatible provider configurations. Credentials remain protected in local Studio storage, descriptors remain secret-free, and executor registrations update immediately. The settings and standalone Studio shell use a more restrained graphite and steel-blue theme with improved hierarchy and responsive forms. Host configuration remains available for consumer services. The Studio remains a loopback-only unauthenticated prototype; multi-user credential management and workflow-reference checks before deletion are not implemented. See [provider configuration](model-providers.md) and [design system](design-system.md).

## canvas-first creation workbench

Blank connected drafts, searchable node library, contextual search/actions, drag placement and output-pin creation use validated insertion paths. Compact cards and a selected properties panel prioritize the canvas. Designer/run views preserve state; native fullscreen includes editing/playback controls, with fit, keyboard shortcuts, filtering and clearer recorded paths. Browser validation covers these gestures and existing authoring/version/run flows. Arbitrary existing-node rewiring, explicit branch-block removal and broader graph semantics remain planned. See [graph editor](graph-editor.md) and [user guide](user-guide.md).

## read-only interrupted-run reconciliation

Cursor-bound assessments explain unsettled node/model/tool/approval facts, terminal status and saved/missing/invalid/advanced checkpoint state. New per-call model/tool IDs distinguish repeated calls, with conservative legacy matching. Built-in observers, the protected GET API, bounded HTTP client and embeddable observation panel expose metadata without native state, payloads or mutation controls. Eligibility is suppressed on cursor races and source failures and does not grant ownership. See [run reconciliation](run-reconciliation.md).

## deterministic saved-suspension recovery

An opt-in consumer host saves native Agent Framework JSON checkpoints atomically with audit/state and resumes an exact deterministic suspension after host replacement. Tests and a forced-process-loss proof cover stable revision/identity, both branch paths, no duplicate nodes, ownership exclusion, stale evidence and rollback. Built-in ordinary runtime hosts also enforce exclusive local ownership. SQLite run schema 2 migrates existing evidence. Studio's queue retains interruption semantics; external-node/approval and arbitrary-crash recovery remain excluded. See [checkpoint recovery](checkpoint-recovery.md).

## run storage boundary

The local runtime/controller now consume a host-selected `IWorkflowRunStore`. Atomic bounded batches and expected-sequence checks keep evidence and derived status consistent. File snapshots preserve legacy reads; an optional dedicated SQLite store supplies transactions and independent-connection concurrency checks. Studio/service configuration, observer consistency and rollback/conformance checks are implemented. Runtime ownership remains exclusive; restart still interrupts unfinished work. See [run storage](run-storage.md).

## opt-in host-configured model adapter

Studio and the standalone service can explicitly enable version-pinned OpenAI Chat Completions factories. The optional library owns fresh bounded SDK clients, server-side secrets, cancellation/deadlines, safe categorized failures and no automatic retries. Agent evidence adds response IDs and token usage. Unit and local protocol/browser/service fixtures verify the SDK path without live model calls. Credentialed deployment qualification remains planned; see [model providers](model-providers.md).

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

## reusable pipelines and sharing

Core contracts, Studio controls, and end-to-end validation are complete. The headless service-integration proof and local registration API are complete. Conditional string graph authoring is now implemented; the opt-in host-owned SDK adapter is protocol-verified; credentialed live qualification remains planned.

| Deliverable | Status | Completion evidence |
| --- | --- | --- |
| Extract consecutive processing steps into a separate workflow | Complete | Core extraction preserves annotations and rejects invalid selections |
| Insert an exact published source version with fresh identities | Complete | Copied requirements/source identity, bounded expansion, real framework execution without source lookup |
| Portable definition/layout packages with hashes and requirement manifests | Complete | Canonical round trips, bounds, mismatch/unknown/duplicate-property rejection |
| Save a labeled section as a reusable draft and choose published versions | Complete | Real controls preserve the consumer draft and source history |
| Insert a selected version with shared undo/redo and review requirements | Complete | Browser checks, saved consumer output, reload and immutable source-version behavior |
| Export/import the full package through Studio | Complete | Layout/annotation preservation, local-only import, explicit save/publication, no permission transfer |

This increment uses copy-on-insert template expansion, not recursive runtime prefab calls or automatic instance upgrades. See [reusable pipeline contracts](reusable-pipelines.md).
