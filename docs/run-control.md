# Pause and resume

The development runtime supports cooperative, process-owned pause/resume for linear workflows. Execution stays on the backend when the editor closes. A paused run remains paused until explicitly resumed or cancelled, or its host stops. This is not a durable checkpoint across restarts.

## Boundary behavior

**Pause selected run** requests a pause. An admitted node finishes first, including its model rounds and tool calls. The runtime records `run.paused` before admitting the next node; it also checks a boundary before final run completion. A request does not cancel, undo, or interrupt an external operation. The graph highlights the next node when one is known. At the final completion boundary there is no next node to highlight.

```mermaid
stateDiagram-v2
    [*] --> Queued
    Queued --> Running: Node admitted
    Queued --> Paused: Start paused or pause before admission
    Running --> Pausing: Pause requested
    Pausing --> Paused: Current node finishes
    Pausing --> Running: Resume withdraws request
    Paused --> Queued: Resume awaits execution slot
    Queued --> Cancelled: Cancel
    Paused --> Cancelled: Cancel
    Running --> Completed: Completion boundary passed
    Running --> Failed: Execution fails
```

The diagram shows common paths; cancellation and failure can also terminate a pending pause. Restart marks any unfinished run interrupted. The snapshot status after resume is `queued` if execution has not started, otherwise `running`, including while awaiting the shared execution slot.

Resume continues the same live Agent Framework execution with the same run ID, immutable revision, and invocation history. Completed nodes are not rerun. It does not load a new definition or rebuild execution from recorded events. Use **Start paused** for quick runs or **Start published run paused** for published runs to avoid racing a fast deterministic workflow.

The runtime admits at most 32 owned contexts, including queued, running and paused contexts. One shared execution permit serializes active workflow work. Paused continuations release that permit, so another admitted run can execute; they still consume admission capacity. A full or stopping runtime records a failed run. Cancellation unblocks paused waits. Fair scheduling is not guaranteed.

## APIs and embedding

The additive Core interfaces `IWorkflowRunControl` and `IPublishedWorkflowRunControl` expose paused starts and pause/resume commands. Existing clients can keep implementing only `IWorkflowClient`; components show controls only when the corresponding capability is available. Component disposal stops observation, not execution or its pause state. Low-level hosts may supply `IWorkflowExecutionGate` to `WorkflowRunner.ExecuteControlledAsync`; they own synchronization, cancellation and persistence policy.

| Request | Behavior |
| --- | --- |
| `POST /api/runs` with `{ "definition": { ... }, "input": "text", "startPaused": true }` | Create an ad-hoc run paused before its first node |
| `POST /api/workflows/{id}/runs` with `{ "input": "text", "startPaused": true }` | Pin the active publication and start paused |
| `POST /api/revisions/{id}/runs` with the same body | Pin that exact publication and start paused |
| `POST /api/runs/{id}/pause` | Request a cooperative pause; return 202 and the current snapshot |
| `POST /api/runs/{id}/resume` | Resume or withdraw a pending pause; return 202 and the current snapshot |

Repeated commands are idempotent while execution is owned and nonterminal. An unknown run returns 404; a terminal or no-longer-owned run returns 409. Development origin restrictions still apply (403 for disallowed origins). HTTP 202 does not promise the current node has finished; observe the snapshot for `paused`.

## Breakpoints and node stepping

The live run inspector can set or clear a breakpoint on an executable node, pause at that node's admission boundary, continue, and step one node at a time. Pure data nodes are evaluated as dependencies of an admitted executor and do not have separate breakpoint controls. A breakpoint is process-owned control state and does not change a workflow definition or revision. `debug.breakpoints.changed` records the current node-ID set so the inspector can restore the controls from run history; `debug.breakpoint.hit` records the node where admission stopped. These events are evidence of debugger controls, not node execution. They are stored in the same sequenced run history and historical playback never executes the graph.

`POST /api/runs/{id}/breakpoints` accepts `{ "nodeIds": ["node-id"] }`; an empty array clears all breakpoints. IDs must identify nodes with a runtime execution-admission boundary. `POST /api/runs/{id}/step` admits one node and pauses before the next node boundary. Both controls require the current host to own the live continuation. The initial stepping contract is limited to sequential graphs without conditional routing, loops, parallel regions, nested workflow calls, or routed Agent outputs. The button is disabled for those unsupported topologies. Continue remains available and keeps its cooperative behavior. A step completes the current node in full, including its model/tool work; it cannot interrupt or roll back in-flight operations. Host restart loses live control state, while recorded breakpoint events remain inspectable.

## Audit, playback and failure

`run.pause-requested`, `run.paused`, and `run.resumed` share one sequenced journal writer with executor/model/tool events. Control data records a boundary and the development actor `local-developer`; this is not authenticated actor identity. Node/model evidence arriving after a pause request does not reset the status to running. Playback derives state from the selected recorded prefix and never invokes execution. The latest status remains visible separately.

Checkpoint `suspended` state belongs to the separate [explicit recovery API](checkpoint-recovery.md); it is not an in-process pause and cannot be resumed through these controls. The runtime and controller use the host-selected [run store](run-storage.md), with expected-sequence event commits and consistent read-only snapshots. Graceful shutdown cancels live waits. Restart marks remaining unfinished journal entries interrupted; their old continuation cannot be resumed. Provider errors may fail a run before its requested pause is acknowledged. Durable checkpoints, recovery of uncertain external effects, distributed ownership and authenticated control authorization remain planned. The adapter uses awaited executor boundaries supported by the pinned Microsoft Agent Framework 1.26.0; it does not claim checkpoint restore support from that integration.

## Verification

`PauseResumeTests` exercises in-flight pause acknowledgement, withdrawal, repeated commands, node boundaries, cancellation, admission limits, independent work while paused, pinned identity and owner loss. It also proves one-node stepping and breakpoint pause/continue behavior. `ApiParityTests.DebuggerEndpointsConfigureBreakpointsAndStepARecordedRun` exercises the API. Adapter tests cover the final completion boundary. `python3 scripts/pause_resume_smoke.py --browser-executable /usr/bin/chromium` exercises pause, breakpoint, step and continue controls through the Studio, browser reload, exact published versions and recorded playback using deterministic local data.

## Waiting for approval

`waiting-approval` is a nonterminal status. Resume does not approve, and approval does not clear a manual pause. Approval waits release execution ownership while still consuming an admission context. Cancellation/shutdown unblocks them; restart interrupts unfinished runs rather than reviving unresolved decisions. See [exact-action approvals](approvals.md).
