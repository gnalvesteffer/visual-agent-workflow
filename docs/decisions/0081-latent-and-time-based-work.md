# Decision 0081: Bounded Wait/Delay (latent) node

**Status:** PROPOSED, reviewed 2026-11-24. The review's prerequisites are met and its narrowed first slice (duration-only, bounded, non-resumable Wait) is implemented; see [Wait first slice implementation](#wait-first-slice-implementation-2026-11-24). Absolute deadlines, durable timers, and resumable waits remain unimplemented.

**Date:** 2026-11-24

## Context

The [capability matrix](../blueprint-capability-matrix.md) marks latent and
time-based work "Partial, intentionally bounded" and asks for durable wait
identity, timer authority, restart behavior, and evidence before any
timeline-like node. The [Unreal reference](../unreal-blueprint-reference.md)
says a Delay needs a durable wake condition, deadline, owner, persisted cursor
and post-restart behavior, and that a client timer is not a latent node. This
decision covers only a Delay-style node. Timelines, repeating timers, and
event-driven waits are out of scope.

## Confirmed current behavior

- **Approval waits** (`ControlledRun.WaitForApprovalAsync`, `ReleaseExecutionSlot`
  in `src/VisualAgentWorkflow.Hosting/ControlledRun.cs`): the run releases the
  shared execution permit, keeps its admission context, and ends on
  cancellation or shutdown. Restart marks unfinished runs interrupted and does
  not revive the hold ([approvals](../approvals.md), [run control](../run-control.md)).
  The durable identity is the `approval.requested` event: request ID, reserved
  invocation, and action fingerprint.
- **Pause**: paused continuations release the permit, keep admission capacity
  (at most 32 owned contexts), and are unblocked by cancellation. They are not
  durable across restart.
- **Bounded provider/tool waits**: `McpToolBroker` and the Studio provider
  stores use `CancelAfter` deadlines. The deadline is a host setting. A timeout
  after authorization is recorded as `tool.outcome-unknown`
  ([reconciliation](../run-reconciliation.md)).
- **Host-owned bounded waits**: `SequenceDrainOptions.AbandonGrace` is a single
  host-wide, positive, finite bound with a 24-hour maximum. Workflow authors
  and run input cannot set it. `WorkflowRunner` uses `Task.Delay` for the
  grace and `MonitorTimeoutAsync` chunks waits into spans of at most one day.
  Abandonment records `abandoned` outputs and a `run.outcome-unknown` root.
  See the 2026-11-24 sections of [0070](0070-bounded-array-iteration-contract.md).
- **Checkpoint recovery** ([checkpoint recovery](../checkpoint-recovery.md))
  admits only pure graphs. It rejects agents, MCP tools, and approvals before
  admission. No Delay node exists, and no durable timer source exists (see "TimeProvider seam" for the in-process clock seam).

## Options

1. **No Delay node.** Authors use approvals or host scheduling. No new risk,
   but Blueprint parity stays partial and there is no way to express a
   deliberate pause between side effects.
2. **In-process bounded Delay (recommended for a first slice).** A node holds
   the run in a durable-looking wait without durable resumption. It releases
   the execution permit, keeps admission capacity, and ends on deadline,
   cancellation, or shutdown. A restart interrupts the run, as approvals do.
3. **Durable timer resumed after restart.** Requires checkpoint recovery for
   graphs that contain agents and tools, which it does not support, plus an
   owner and wake store. Defer until that recovery policy exists.
4. **Polling or tick loop.** Rejected: unbounded evidence growth and no
   deterministic replay.

## Direction (proposed)

**Contract.** A Wait node takes an explicit finite duration or an absolute
UTC deadline from the graph, plus a node-local cancellation path. The
host-owned maximum (default and ceiling, configured like `AbandonGrace`)
rejects larger values at validation or admission, never by silent clamping.
The deadline is computed once, from a host-authorized `TimeProvider`, and
recorded. Run input is caller data and cannot extend the bound. There are no
ticks, polling, repeats, or timers outside the run's owned context.

**Wait identity.** `wait.requested` records the wait ID, node invocation, kind
(duration or deadline), requested value, resolved UTC deadline, and the host
bound applied. The wait ID is a stable host-generated reference. Settlement is
exactly one of `wait.elapsed`, `wait.cancelled`, or `wait.interrupted`.

**Restart.** The first slice does not require checkpoint recovery. Process loss
leaves `wait.requested` with no settlement. Reconciliation shows it as
unresolved, non-resumable, and not an external effect. The run is interrupted,
consistent with approvals. A resumable timer would need the checkpoint host to
accept this node, which stays gated.

**Concurrency.** A wait releases the execution slot like approvals and consumes
one admission context for its run. Parallel Fork/Join branches each hold one
wait inside the same context. The run-level maximum must bound the sum of
waits in series and the longest in parallel. Loop budgets count a wait body as
one iteration. A loop of waits needs a total wall-clock bound, not only an
iteration bound. In Sequence drain, a wait that does not settle before
`AbandonGrace` is `abandoned`, and its cancellation must be prompt because it
holds no external effect. It never produces `outcome-unknown` on its own, but
a sibling's unknown outcome still does.

**Evidence and compatibility.** Additive event kinds only. **Corrected
2026-11-24:** older readers do not fail closed. Every reader ignores an unknown
kind (see "Unknown event kind behavior"); fail-closed behavior would be new
work, not an existing property. Playback shows a pending wait and its deadline as recorded
data and never recomputes elapsed time from the current clock. No schema
version or capability gate is added, per repository policy.

**Test determinism.** An injected `TimeProvider` (fake clock) controls
advance. There are no sleeping tests. Required cases: elapse, cancellation
before and during, shutdown, deadline already past, bound exceeded, restart
with an unsettled wait, parallel waits, Sequence abandon, a Loop of waits,
File and SQLite playback, and older-reader behavior (currently: ignored, not rejected).

**Security.** The maximum, the time source, and any total-run wait budget are
host-owned. Workflows, MCP outputs, and run input cannot raise them.

## Evidence required

- A runner proof that the permit is released during a wait and reacquired
  after it, with the admission count unchanged.
- Journal tests for the event kinds, with an old-reader fixture pinning ignore-or-reject behavior (today: ignore).
- Reconciliation tests classifying a dangling wait as unresolved and
  non-resumable.
- Studio and playback tests showing the pending, elapsed, cancelled, and
  interrupted states.
- Authoring guidance and a user-guide update, kept distinct from future design.

## Open questions for the three-role review

1. Is an in-process, non-resumable first slice acceptable, or must Wait wait
   for checkpoint recovery of effectful graphs?
2. Should a wait consume admission capacity for up to the host maximum, or is a
   separate cap on concurrently waiting runs needed to prevent starvation of
   the 32 contexts?
3. Duration only, absolute deadline only, or both? How are past or
   clock-skewed deadlines handled?
4. What is the default and the ceiling of the host maximum, and must a
   run-level cumulative wait budget exist for Loops and Fork branches?
5. Should unsettled waits become `wait.interrupted` at restart, or only be
   inferred by reconciliation from the missing settlement?
6. Is an abandoned wait's claim of no external effect safe if the node is
   placed between a tool authorization and its outcome?
7. Is fail-closed old-reader behavior required to name the event kind, and does
   it need a documented minimum reader version?

## Review outcome (2026-11-24)

Three-role review (proponent, opponent, adjudicator). **Ruling: defer.** Option
2 is the right shape, but the proposal as written is not accepted. Ship
prerequisites first, then a narrowed duration-only slice.

**Claims verified (read-only):**

- True: the "unknown event kind" playback error does not exist. The only
  related code is `ParallelCollaborationJournalValidation.cs:87,303`, which
  rejects unknown kinds inside the collaboration region only. (Refined: those
  defaults are unreachable for any kind outside its fixed list, because events
  are pre-filtered by `IsCollaborationEvent`.) Fail-closed
  old-reader behavior for new kinds is unproven, so the "Evidence and
  compatibility" paragraph above is unverified and must not be relied on.
- True at review time (since addressed for the run path, see "TimeProvider seam"): no `TimeProvider` exists in `src` or `tests`. `DateTimeOffset.UtcNow`
  is called directly in 22 places (for example `ControlledRun.cs:47,90,146,192`).
  An injected clock is a cross-cutting refactor, not a node-local detail.
- True: `WaitForApprovalAsync` (`ControlledRun.cs:182-225`) allows exactly one
  pending approval (`:191`). Per-Fork-branch waiting is not supported by that
  path, so the Concurrency paragraph overreaches.
- True: runs are capped at 32 (`LocalWorkflowRuntime.cs:35,142`). A waiting run
  keeps its slot, so 32 waits saturate the host. A waiting-run cap and a
  cumulative budget are requirements.
- Resolved in the proponent's favor: checkpoint exclusion is an allowlist of
  deterministic node types (`WorkflowCheckpoints.cs:238-246`), so a new Wait
  type is rejected by default. A regression test is still required, and Wait
  inside Loop/Sequence frames stays out of scope.
- Not verified as a defect: the tool-authorization placement concern
  (open question 6) follows from the existing `tool.outcome-unknown` model and
  is handled by a placement restriction below.

**Prerequisites, in order:**

1. A test proving how readers (File, SQLite, playback, reconciliation) treat an
   unknown event kind, plus any code change needed to make it fail closed.
   Document the minimum reader version.
2. A `TimeProvider` seam for run timing, with fake-clock tests. Waits measure
   duration on the monotonic clock (`TimeProvider.GetTimestamp`); the recorded
   wall-clock deadline is informational only.
3. A checkpoint-rejection test for a Wait node.

**Exact first slice (after the prerequisites):**

- Duration-only Wait; absolute deadlines are cut. Duration is graph-authored
  and validated at admission against a host per-wait cap (<= 24h, configured
  like `AbandonGrace`). No silent clamping; run input cannot alter it.
- Allowed only on top-level paths: no Sequence, Loop, Fork/Join, subworkflow,
  and not between a tool authorization and its outcome.
- Host-owned cumulative per-run wait budget, and a global waiting-run cap well
  below 32 (the default must leave admission headroom for non-waiting runs).
- Reuse the approval path: `wait.requested` (wait ID, invocation, duration,
  monotonic start, host bound) recorded under the gate lock, release the
  execution slot, await with cancellation, then settle with exactly one of
  `wait.elapsed`, `wait.cancelled`, `wait.interrupted`; reacquire the slot
  before the node completes. Pause, stop, cancel, and elapse races are decided
  by the gate lock: the first recorded settlement wins, and tests cover each.
- Restart leaves `wait.requested` unsettled; reconciliation reports it as
  unresolved and non-resumable.

**Open questions resolved:** 1 yes (in-process, non-resumable); 2 yes, a
separate waiting-run cap; 3 duration only; 4 24h per-wait ceiling plus a
cumulative per-run budget, both host-owned; 5 inferred by reconciliation, with
`wait.interrupted` recorded only on a clean shutdown; 6 forbidden placement;
7 yes, the reader must name the kind and the minimum version is documented.

No change to the capability matrix or roadmap is needed: both still call the
work proposal-only.

## Unknown event kind behavior (2026-11-24)

Characterization tests feed `future.unknown-kind` and a realistic additive
"requested" kind to each reader. **Update 2026-11-24:** `wait.*` is now a recognized kind (see the implementation section),
so those tests use `future.requested` for the still-unrecognized case; the table describes behavior for any kind a
reader does not name. **No reader rejects or crashes on an
unknown kind; all ignore it.** The earlier claim that older readers "fail
closed on unknown kinds" is disproved. No code was changed: nothing crashes
or misclassifies run status or terminal state.

| Reader | Behavior | Tests |
| --- | --- | --- |
| `RunSnapshot.Status` / `IsTerminal` | Ignore. An unknown kind alone gives `running`; it never changes completed, failed, paused, suspended or waiting-approval. | Core `UnknownEventKindTests`: `Unknown_event_kind_leaves_run_status_running_and_non_terminal`, `..._does_not_change_terminal_pause_suspension_or_approval_status`, `..._as_the_only_event_is_running_not_queued` |
| `WorkflowWorkEvidence` (reconciliation work) | Ignore. An unsettled `wait.requested` is never reported as unresolved work. | `Unknown_event_kind_is_ignored_by_work_evidence`, `Unsettled_unrecognized_requested_kind_is_not_reported_as_unresolved_work_until_a_reader_names_it` |
| `WorkflowApprovalEvidence` | Ignore. | `Unknown_event_kind_is_ignored_by_pending_approval_projection` |
| `SequenceEvidence`, `VariableEvidence`, `WorkflowCallEvidence` playback | Ignore (kind filters or fall-through); an unknown-only history yields no playback. | `..._ignored_by_sequence_playback`, `..._ignored_by_variable_playback`, `..._ignored_by_workflow_call_playback` |
| Loop checkpoint frame (`WorkflowCheckpointFrame.Validate`) | Not interpreted, but hashed into the evidence prefix, so a frame cannot validate against a prefix lacking it. | `Unknown_event_kind_is_covered_by_loop_checkpoint_evidence_hash_but_not_interpreted` |
| `FileRunJournal` and `SqliteWorkflowRunStore` write/read | Accept and round-trip. Only blank kinds, bad sequence, or events after a terminal are rejected, for every kind. | Hosting `UnknownEventKindTests`: `..._accepted_persisted_and_read_back_by_run_stores`, `..._after_a_terminal_outcome_is_rejected_like_any_other_kind`, `Blank_event_kind_is_rejected_by_run_stores_but_unknown_nonblank_kind_is_not` |
| `ParallelCollaborationJournalValidation` | Ignore outside its fixed kind list, including `parallel.collaboration.*` kinds it does not list. | covered by the blank-kind test above |
| `WorkflowRunReconciliation.Assess` | Ignore. A run ending `run.interrupted` with an unsettled `wait.requested` is `Terminal` with zero unresolved work. After a saved suspension, an unknown kind yields `EvidenceAdvanced` and is not resumable. | `Unknown_event_kind_is_ignored_by_reconciliation_and_terminal_status_still_wins`, `..._after_a_saved_suspension_makes_evidence_advanced_and_not_resumable` |
| `WorkflowRunViewer` (Blazor) | Render with the raw, HTML-escaped kind in the recorded-events table; node state ignores it. | `Unknown_event_kind_is_rendered_with_its_raw_kind_by_the_recorded_viewer` |
| `ParallelJoinEvidence` playback | Ignore by source reading (falls through the kind checks); after a root terminal any late event is rejected, whatever its kind. Not separately tested. | none |

**Consequences for Wait.** Ignore-by-default is a compatibility hazard: an old
reader sees an unsettled `wait.requested` as no work at all, so reconciliation
from an older build would not flag it as unresolved. Wait therefore needs
either a reader that names the kind in `WorkflowWorkEvidence` (as the proposal
already requires) or an explicit fail-closed reader check added in the same
change, together with a documented minimum reader version. Adding a new
`UnresolvedWorkKind` also needs care: `HttpWorkflowRunObserver` treats an
undefined enum value as an invalid report. Prerequisite 1 is now met for
characterization only; fail-closed behavior remains unbuilt.

## TimeProvider seam (2026-11-24)

Prerequisite 2 is met only for the sites below. There is no behavior or
serialized-value change: every owner defaults to `TimeProvider.System`,
resolved once. Public surface is additive only: `LocalWorkflowRuntime` and
`CheckpointWorkflowHost` gain overloads with a required `TimeProvider`
parameter (existing constructors are unchanged, and the pinned-signature tests
still pass), `MarkUnfinishedAsInterrupted(store, clock)` is a new overload, and
`WorkflowRunner` takes an internal `Clock` init property. Internal owners take
optional parameters. The clock overload of `LocalWorkflowRuntime` does not
combine with `SequenceDrainOptions`.

**Reads from the provider (event or evidence timestamps, deadlines):**

- `ControlledRun`: control events, breakpoint events, approval request time
  and approval event timestamps.
- `WorkflowRunner`: root event timestamps (`SequencedExecutionEventWriter`,
  structured-proof and checkpointed emitters), the collaboration deadline and
  its timeout monitor (`Task.Delay(delay, clock, ...)`), the Sequence
  `AbandonGrace` delay, and child workflow runners.
- `LocalWorkflowRuntime`: the queue-full failure event, restart interruption
  (`MarkUnfinishedAsInterrupted(store, timeProvider)`), and the provider handed
  to each `ControlledRun` and `WorkflowRunner`.
- `CheckpointWorkflowHost`: checkpoint-captured and suspended events.

**Intentionally not on the provider:**

- Diagnostics: `Stopwatch` in `AgentNodeExecutor`.
- Metadata, not run timing: revision `CreatedAt`, Studio project and catalog
  timestamps, and the reconciliation-decision timestamp.
- `FileRunJournal.MarkUnfinishedAsInterrupted()` (the store's own method) and
  `SequenceRunnerProof`, a test proof.
- `DemoMcpServer` `Task.Delay`, a demo fixture.
- Provider and tool `CancelAfter` deadlines and collaboration channel waits.

Tests use a deterministic manual `TimeProvider` (no new package): timestamps
equal the fake clock and move only when it is advanced, and the collaboration
timeout fires only on `Advance` with no sleeping. The Wait node must still use
`GetTimestamp` for monotonic duration; the seam does not add that yet.

## Wait first slice implementation (2026-11-24)

The slice the review ruled for is implemented as `core.wait`, schema 2.0 only. This section lists every sub-decision
made while implementing it, why, and what was deferred. It does not change the review ruling. Items marked
**limit** are deliberate restrictions, not bugs.

### What shipped

- **Node.** `core.wait` (`NodeTypes.Wait`, type version `1.0.0`): one execution input `execute`, one execution output
  `then`, no data pins. Configuration is `wait.durationMilliseconds`, an integer from 1 to 86,400,000 (24 hours). The
  activation passes through: the node's result is the text it received, and downstream data comes from data edges as
  for any execution node.
- **Evidence.** `wait.requested` then exactly one of `wait.elapsed`, `wait.cancelled`, `wait.interrupted`, all on the
  Wait node's invocation. `ExecutionEventKinds` names them.
- **Host gate.** `IWorkflowWaitGate` (Core) is implemented by the Hosting `ControlledRun`. `LocalWorkflowRuntime` takes
  a new `WorkflowWaitOptions` overload.
- **Authoring.** Studio palette entry "Wait" and an inspector duration field. No other Studio authoring.

### Sub-decisions

| # | Decision | Why |
| --- | --- | --- |
| 1 | **Schema 2.0 only**; `wait.schema` rejects it elsewhere. No `WorkflowSchemaVersions` constant. | 2.0 derives its node families from the contract catalog. Historical schemas are frozen. The descriptor's `MinimumSchemaVersion` is 2.0 as well. |
| 2 | **Duration in milliseconds, an `integer`, not a data pin.** It is node configuration, not a value from run input. | Run input is caller data and must never extend a bound. A graph-authored constant is reviewable and hashed with the revision. |
| 3 | **Fixed ceiling 24 h in Core; the host may only tighten it.** `wait.duration` rejects 0, negatives, and values over 24 h in `WorkflowValidator`. | Validation is host-independent. The ceiling is a constant, not a setting. |
| 4 | **Host options `WorkflowWaitOptions`:** per-wait maximum default 1 h (ceiling 24 h), cumulative per-run budget default 2 h (at least the per-wait maximum, ceiling 24 h), waiting-run cap default 4 (1 to 16). Invalid options throw at construction. | Safe small defaults, fail-closed construction, headroom below the 32-run admission limit. |
| 5 | **Over-bound is rejected at admission, never clamped.** `LocalWorkflowRuntime` checks `WorkflowWaitOptions.Check` before it creates a run and throws `WorkflowWaitAdmissionException` (an `ArgumentException`, so Studio's existing 400 mappings apply and no run record exists). `ControlledRun` repeats the checks as defense in depth. | Review ruling. Defense in depth covers a gate used outside the runtime. |
| 6 | **The cumulative budget is a static sum of the definition's Wait durations.** | A Wait cannot sit in a loop, branch, or Sequence output, so each runs at most once. The sum is a sound upper bound, so admission can decide it. `ControlledRun` still tracks the running total. |
| 7 | **The waiting-run cap is applied at admission, not at the wait.** A run whose definition contains any Wait counts against the cap from admission until its context is released. An over-cap start records a `run.failed` ("at most N concurrent runs that contain a Wait node") and queues nothing. Runs without a Wait are unaffected. | Failing mid-run after side effects, or blocking while holding a context, would be worse. A static count is deterministic. **Limit:** a Wait-capable run counts even before it reaches its Wait or on a branch that never waits. |
| 8 | **Placement rule: top-level execution paths only.** The validator rejects a Wait that is execution-reachable from a Sequence output, a Loop or For Each `body` pin, or a Fork branch pin. Reaching a Loop or For Each through `feedback` ends the walk. A Join continues the walk only when its Fork is itself inside a region, so a Wait after a top-level Fork/Join is legal and a Wait after a Join nested in a loop body is not. A Wait in a child workflow definition is rejected by the calling workflow (`wait.placement` at the child path). | Implements the review's placement ruling by reachability, not by node-type allowlist. Fork and loop bodies hold concurrent or repeated work, so a single wait budget and one-at-a-time slot release do not hold there. |
| 9 | **Tool authorization window: satisfied by construction, no extra rule.** The window between `tool.authorized` and its outcome is inside one MCP tool node's execution. A top-level path runs one node at a time, and everything concurrent (Fork branches, Sequence outputs, loop bodies) is excluded by the placement rule. An Approval must immediately precede its tool (`approval.target`), so a Wait cannot sit between them. Test: `A_wait_cannot_sit_between_an_approval_and_its_tool_so_it_never_overlaps_an_authorization_window`. | Review open question 6, answered without a new adjacency check. **Limit:** the argument rests on the single-token top level. If concurrent top-level activation is ever added, this must be re-reviewed. |
| 10 | **Topology profiles may reject other combinations.** Core adds Wait to the unified plain-graph profile only through the contract catalog. The Sequence, Loop, and Fork profiles validate with their own node allowlists, so a top-level Wait in a workflow that also contains those regions can still be rejected by that profile. | Not widened, to keep this slice from reopening those proofs. **Verified (validation only):** a Wait after a For Each validates; a Wait before one is rejected by the loop profile (`loop.feedback.root`), not by a `wait.*` rule. **Not verified:** the Sequence and Fork profiles with a Wait, and any mixed graph at runtime; only the plain top-level graph is executed in tests. |
| 11 | **Clock.** The duration is measured by `Task.Delay(duration, TimeProvider, token)`, a `TimeProvider` timer, and the settlement records `elapsedMilliseconds` from `GetTimestamp`/`GetElapsedTime` (monotonic). `deadlineUtc` is `GetUtcNow() + duration` at request time, for display only, and nothing reads it back. Playback shows the recorded deadline and never a current-clock countdown. | Review ruling. A wall-clock jump cannot lengthen or shorten a wait. |
| 12 | **The wait capability is an optional interface, `IWorkflowWaitGate`,** next to the execution gate, not a member of `IWorkflowExecutionGate`. | Existing gate implementers stay source and binary compatible. `IWorkflowApprovalGate` already sets the pattern. |
| 13 | **Fail closed in the runner, before any event.** `WorkflowRunner` throws `InvalidOperationException` ("...did not provide a wait coordinator (IWorkflowWaitGate)...") from its preflight, after validation and before the run starts, when the definition contains a Wait and the gate is missing or not a wait gate. The node-level check repeats it. | A host that cannot wait must refuse before side effects, not fail halfway through. |
| 14 | **The host re-derives the duration.** `ControlledRun.WaitAsync` reads the node's configuration from the captured header definition and rejects a request that names another node or duration. | The runner is not trusted to extend a wait. |
| 15 | **Request recording order.** Under the gate lock: create the timer, record `wait.requested`, register the wait, release the execution slot, then acknowledge any pending pause. Data: `waitId`, `nodeId`, `durationMilliseconds`, `deadlineUtc`, `bound` (`perWaitMaximumMilliseconds`, `cumulativeBudgetMilliseconds`, `cumulativeRequestedMilliseconds`). | The timer exists before the request is visible, so a recorded request always has a live timer. Tests need no real delay. If recording fails, the timer is cancelled. |
| 16 | **Only one wait at a time per run** (`A wait is already pending`). | Top-level execution is serial, so a second concurrent wait is a bug. |
| 17 | **Settlement winner.** One waiter records the settlement under the gate lock, guarded so only one is ever written. The timer task decides which: if it completed successfully before cancellation was observed, `wait.elapsed` wins (and a later cancel ends the run at the next boundary); otherwise stop, cancel, or `Cancel()` gives `wait.cancelled`, and a token cancelled with no cancel request (host shutdown) gives `wait.interrupted`. | Deterministic with a fake clock: `Advance` then `Cancel` is elapsed, `Cancel` then `Advance` is cancelled. The task state decides, never thread timing. A cancel arriving after elapse can complete the node normally or throw at the reacquire boundary, depending on scheduling. Both leave `wait.elapsed` as the only settlement. |
| 18 | **Reacquire before settle completes the node.** After settlement, the node reacquires the execution slot through the existing boundary path with breakpoint evaluation off. | Review ruling. Without the flag, a breakpoint on the Wait would hit again after the wait. |
| 19 | **Pause during a wait.** The pause is acknowledged at once (`run.pause-requested`, `run.paused` with the Wait node as boundary), exactly as for an approval wait. **The clock does not pause.** When the timer elapses, `wait.elapsed` is recorded, and the node then holds at the reacquire boundary until `Resume`. Cancel while paused settles `wait.cancelled`. | The wait is host time, not the run's. A paused run must not shorten or stretch recorded wait semantics. The held-after-elapse state is shown by the paused run status. |
| 20 | **Stepping.** `WorkflowDebugging.SupportsNodeStepping` is false for any workflow containing a Wait. Breakpoints before the Wait stay supported. | A step admits one node and pauses at the next boundary; a node that releases and reacquires its slot has no single such boundary. |
| 21 | **Restart.** Process loss leaves `wait.requested` with no settlement. On restart `MarkUnfinishedAsInterrupted` ends the run, and reconciliation reports unresolved work. `wait.interrupted` is recorded only on a clean shutdown (the host cancelled the run token). | Review resolution of question 5. |
| 22 | **Reconciliation: no new enum value.** `WorkflowWorkEvidence` reports an unsettled `wait.requested` as `UnresolvedWorkKind.Node` correlated by `waitId` (`CallId`), state `AwaitingOutcome`, in addition to the node's own `node.started` entry, as an approval does. A request or settlement without a `waitId` marks correlation incomplete, and a settlement without an ID never closes the node's own entry. The run is `UnresolvedWork` and `CanResume` is false. | `HttpWorkflowRunObserver` rejects an undefined `UnresolvedWorkKind`, so a new value would make a newer server unreadable by an older client. A test pins the enum names. |
| 23 | **Older readers.** A reader without this change still ignores `wait.*` (see "Unknown event kind behavior"). Its reconciliation still reports the node's own `node.started` as unresolved Node work, so an old reader does see a restarted wait as unresolved, but not the separate wait entry. No fail-closed reader check and no minimum reader version were added; both remain a deferred decision for the next additive kind. | A fail-closed reader would reject whole histories. The unknown-kind characterization tests now use `future.requested`, so the still-unnamed-kind hazard stays pinned. |
| 24 | **Checkpoint.** `ValidateCheckpointGraph` rejects a Wait explicitly, before the shape checks. The allowlist already rejected it by default. | Review prerequisite 3, and a clearer message than a shape failure. |
| 25 | **Playback.** `WorkflowRunViewer` shows "Waiting", the recorded duration, and the recorded deadline (`display only`) for a pending wait, and "Wait elapsed", "Wait cancelled", or "Wait interrupted by host shutdown" afterwards. The recorded-events table keeps the raw kind. | Review: a readable label, no clock-derived value. |
| 26 | **Studio.** Palette entry "Wait" (flow category) creates a 1,000 ms Wait. The inspector's duration field passes the typed integer through unclamped. An out-of-range value is refused by the existing draft validation and the old value stays. | "Rejected, never clamped" applies in the editor as well. |
| 27 | **Placement fails closed without an edge table.** If `ExecutionConnections` is null and the definition holds a Wait together with any Sequence, Loop, For Each, Fork, Branch, Join, Loop body or Break node, every Wait is reported as `wait.placement`. | The walk cannot prove a Wait is top-level without edges. A genuine schema 2.0 graph keeps its edges there (tested). |
| 28 | **Settlement is attempted whenever the journal is writable.** The waiter's settlement no longer skips a disposed run, and `Dispose` itself settles a pending wait as `wait.interrupted`, so a live journal does not keep an unsettled request. Settlement errors are swallowed and never mask the cancellation. | Review finding. **Remains unsettled, by design:** a request whose journal append fails or whose journal already holds a terminal event; the request then reconciles as unresolved Node work. Process loss is the same case. |

### Deferred (not built)

- Absolute deadlines and any wall-clock wake condition (the review cut them).
- Durable timers and resumption after restart, and any checkpoint admission for Wait.
- Waits in loops, For Each, Fork/Join branches, Sequence outputs, and sub-workflows.
- A Studio setting for the host bounds. They are constructor options on `LocalWorkflowRuntime`; `AddLocalWorkflowRuntime`
  uses the defaults.
- Fail-closed reader behavior for unrecognized kinds and a documented minimum reader version.
- A Wait that carries data (a pass-through data pin). The activation text passes through; no data pin exists.
- Exposing the host bounds or the waiting-run count to clients or operators.
- Retry or "extend wait" controls, and any user-visible countdown.

### Verification

Core `WaitNodeTests`, AgentFramework `WaitExecutionTests`, and Hosting `WaitViewerTests` plus
`UnifiedEditorRenderingTests`. None of them sleep for a wait: `ManualTimeProvider` controls the clock. They poll the
journal in real time only to observe that a background run reached a recorded event. Not verified: Studio in a
real browser, keyboard and screen-reader behavior of the new inspector field, restart of a real process, and the
mixed-region graphs in sub-decision 10.

## Wait placement review outcome (2026-11-25)

Adjudication of a proponent/opponent review on extending `core.wait` beyond top-level paths. Claims were checked against
the code at commit 6c0bfeec.

### Claims verified

| Claim | Verdict | Evidence |
| --- | --- | --- |
| The cumulative budget check is racy across the unlocked tail of `WaitAsync`. | **False.** | `ControlledRun.WaitAsync` checks `activeWaitId`, the per-wait maximum, and `cumulative + ms > budget`, then increments `cumulativeWaitMilliseconds` and sets `activeWaitId`, all inside one `lock (gate)` (about lines 271-302). The budget is reserved before the timer is awaited and is never released on cancel. A second wait throws instead of racing. Atomicity only becomes a question once a second concurrent wait is allowed. |
| The runtime budget check is already dynamic. | **True.** | It compares the running total, not the static sum. |
| The static `Check` is a sum over Wait nodes. | **True.** | `WorkflowWaitOptions.Check` sums node durations once. Its doc comment relies on "each Wait runs at most once". Changing that meaning is a real edit, not a relaxation. |
| A loop body is strictly sequential. | **True for a body without a Fork.** | The runner has no `Task.WhenAll` or parallel dispatch. A Fork inside the body stays a rejected region. The loop bound is `WorkflowLoopLimits.MaximumIterations` = 16, with 1,024 total body passes per run. For Each is capped at `WorkflowJsonArrayLimits.MaximumItems` = 64. |
| Holding a wait slot for up to 24 hours is a new risk. | **Overstated.** | Run waiting is already capped by the host cumulative budget (default 2 hours, ceiling 24 hours), enforced dynamically. Multiplying by iterations does not raise that cap. It only changes where rejection happens. |
| The wait event carries scope or iteration. | **False.** | `RecordWait` passes only `NodeId` and `InvocationId`. The payload has `waitId`, `nodeId`, and durations. The runner has `received.LoopIteration` at the call site (WorkflowRunner.cs ~3123) but `WorkflowWaitRequest` does not carry it. |
| The sub-workflow waiting-run cap bypass is real. | **Not today, real if relaxed.** | `ContainsWait` scans only the top-level `Nodes`, but `WorkflowValidator` (line ~70) rejects any child Wait with `wait.placement`. The bypass appears only if that rejection is lifted without making `ContainsWait` recurse. |
| Sequence outputs run concurrently. | **Not established.** | No parallel dispatch was found. Sequence output order is not documented as strictly serial, so a pure relaxation is unproven. `RecordWait` also loses scope (no scope parameter, unlike approval evidence). |
| Fork is blocked by the single slot. | **True.** | `activeWaitId is not null` throws. `RequestPause` acknowledgement while sibling branches run is untested. |

### Ruling

| Placement | Decision |
| --- | --- |
| (2) Loop / For Each body | **Implement now, sequential only.** |
| (4) Sub-workflow | **Defer.** |
| (3) Sequence output | **Defer.** |
| (1) Fork/Join branch | **Defer.** |

### First slice: Wait in a Loop or For Each body

1. `WorkflowWaitPlacement.NodesInsideRegions`: Loop and For Each `body` no longer opens a region. Sequence, Fork, Branch, and Join still do. A Wait reachable through a Sequence or Fork that sits inside a loop body stays rejected. The fail-closed no-edge-table branch no longer counts Loop, For Each, Loop body, or Break as region nodes.
2. `WorkflowWaitOptions.Check`: for each Wait, compute a provable execution bound. The bound is the product of the enclosing Loop `MaximumIterations` values (at most 16 each), or 64 for an enclosing For Each, capped by the 1,024 body-pass ceiling. Add `duration x bound` to the sum. A Wait whose bound cannot be proven is rejected with `wait.budget`. `wait.bound` (per-wait maximum) is unchanged. Update the doc comment.
3. Wait events gain `loopIteration` (from the runner, passed on `WorkflowWaitRequest`), without changing existing fields. Distinct `waitId` per iteration already holds.
4. Tests: accept a Wait in a Loop and in a For Each body; reject a Wait in a Loop inside a Fork or Sequence; reject at admission when `duration x bound` exceeds the budget; budget exhaustion at runtime still throws at iteration N+1 if the static bound is bypassed with a smaller host budget; stop, cancel, and break mid-loop settle the pending wait exactly once; repeated waits get distinct `waitId` and the `loopIteration` value; the waiting-run cap and `HoldsWaitCapacity` are unchanged.
5. Update decision 0081 sub-decision 10, the Deferred list, the roadmap, and the user guide in the same change.

### Prerequisites for the rest, in order

1. Carry a scope id (and `loopIteration`) on `WorkflowWaitRequest` and in `RecordWait`; add scope-evidence tests. Needed by (3) and (1).
2. Make `WorkflowWaitOptions.ContainsWait` recurse into expanded child definitions. Needed by (4) before the validator rejection is lifted.
3. Replace the single `activeWaitId` slot with per-wait state: a keyed dictionary of pending waits, each with its own settle action, and an atomic reserve-then-release budget (release on cancel, or document that cancel consumes it). Needed by (1).
4. Prove or document Sequence output ordering, then relax placement for (3) with scope-id evidence and an authorization-to-outcome test for tool nodes.
5. For Fork: define pause acknowledgement while sibling branches run, settlement of all pending waits on stop, and the per-run budget under concurrency.
6. For (4): lift the child-Wait rejection only after step 2, validate `request.NodeId` against the child definition, and test the 32-run cap with a child Wait.

Not verified here: no dotnet was run; findings come from reading code only.

## Wait in loop bodies implementation (2026-11-25)

Implements the first slice ruled in "Wait placement review outcome (2026-11-25)": `core.wait` may sit in a Loop body or a
For Each body, sequential case only. Sequence output, Fork/Join branch, and sub-workflow placement stay rejected. This section
lists the sub-decisions made while implementing it. It supersedes sub-decisions 6, 8, 10 and the "waits in loops" deferral
above only where stated.

| # | Decision | Why |
| --- | --- | --- |
| 1 | **Placement.** A Loop or For Each `body` pin no longer opens a rejected region in `WorkflowWaitPlacement.NodesInsideRegions`. Sequence outputs and Fork pins still do. A Wait reachable from a Fork branch or Sequence output stays rejected even when that Fork or Sequence sits inside a loop body. | Ruling item 1. |
| 2 | **A Join of a Fork nested in a loop body still continues the rejected Fork region**, so a Wait after such a Join stays rejected (the existing test is unchanged). A Join of a top-level Fork still does not. | Keeps the previous rule that concurrency in a body is not made legal by this slice. |
| 3 | **"Feedback path" reading.** Reaching a Loop or For Each through `feedback` ends the walk, as before. A Wait whose `then` feeds `feedback` directly is an ordinary body node and is accepted. Nothing past the feedback pin is considered part of the body, so a Wait after the loop's `done`/`completed` pin is top-level (bound 1). | The body must end at `feedback`; rejecting that case would make a Wait unable to be the last body node. |
| 4 | **Fail-closed no-edge-table branch.** Loop, For Each, Loop body and Break are removed from the region-node set (ruling item 1). That is safe because the static budget now fails closed for them: without edges `ExecutionBounds` returns "unprovable" for every Wait when any Loop or For Each exists, so `Check` rejects with `wait.budget`. The branch is still fail-closed for Sequence, Fork, Branch and Join. | A genuine schema 2.0 graph always has edges; this only matters for malformed input. |
| 5 | **Loop profile.** The unified loop profile's body-node allowlist (`loop.body.node`) now includes `core.wait`. Without it the validator rejected every Wait in a body regardless of placement. Nothing else in that profile changed. | The profile is a separate gate from placement (sub-decision 10 characterized this). |
| 6 | **Static budget.** `WorkflowWaitOptions.Check` charges each Wait `duration x bound`. `WorkflowWaitPlacement.ExecutionBounds` computes the bound as the product, over every Loop or For Each whose body reaches the Wait, of `Loop.MaximumIterations` (1 to 16) or `WorkflowJsonArrayLimits.MaximumItems` (64), capped by `WorkflowLoopLimits.MaximumTotalBodyPassesPerRun` (1,024). A Wait after an inner loop is charged by the outer loop only; nested loops multiply. This replaces sub-decision 6's "each Wait runs at most once". | Ruling item 2. |
| 7 | **Unprovable bound is rejected, not clamped.** A Loop with a missing or out-of-range `MaximumIterations`, or a definition with loops but no edge table, gives `wait.budget` at `nodes.<waitId>`. The per-wait `wait.bound` check is unchanged. | Fail closed. |
| 8 | **For Each is charged at the 64-item ceiling**, not the actual array length. The array can come from run input, so the actual length is not provable at admission. A For Each Wait of `d` ms therefore needs a budget of at least `64 x d`. | The only sound static bound. A host that wants longer per-item waits must raise its budget (at most 24 h). |
| 9 | **Runtime backstop unchanged.** `ControlledRun.WaitAsync` still checks the running cumulative total inside `lock (gate)` before reserving. A test bypasses admission and shows the over-budget wait fails with the budget error without a `wait.requested` event. The waiting-run cap and `HoldsWaitCapacity` are unchanged. | Ruling; defense in depth. |
| 10 | **Evidence.** `WorkflowWaitRequest` gains optional `LoopIteration` and `ScopeId`. Inside a loop body the runner sets them from the activation frame: `LoopIteration` is the same 1-based `received.LoopIteration` used by variable evidence, and `ScopeId` is the same scope the node's own events use (for example `loop/2`, or `each/3`). The `wait.requested` and settlement payloads gain a trailing `loopIteration` property and the event carries `ScopeId`. A top-level wait omits both: its payload is byte-identical (property order tested). | Ruling item 3; `RecordWait` now takes the scope from the request. |
| 11 | **Distinct `waitId` per iteration** (a new GUID each request). Reconciliation correlates by `(invocation, waitId)` and needed no change: a test shows two iterations where only the unsettled one is unresolved. | Existing behavior, now pinned. |
| 12 | **Viewer.** Because in-loop wait events are scoped, `WorkflowRunViewer` no longer limits wait playback to root-scoped events. The latest wait event of the node decides its state, and a pending wait shows `· iteration N` after its duration. A settled earlier iteration never hides a pending later one. | Otherwise the loop-body wait would show no state at all. |
| 13 | **Still rejected, now with a loop containing a Wait:** node stepping (`SupportsNodeStepping` is false) and checkpoint recovery (`ValidateCheckpointGraph` rejects Wait nodes). Asserted for a Loop and a For Each. | Review prerequisite 3 and sub-decisions 20 and 24 still hold. |
| 14 | **Pause mid-loop.** Pause during an iteration's wait behaves as before: acknowledged at once, the clock keeps running, the node holds at the reacquire boundary after the elapse, and Resume continues with the next iteration. Cancel settles that iteration's wait as `wait.cancelled` exactly once and starts no further wait. A clean shutdown settles it as `wait.interrupted` once. A Break after a wait (For Each) ends the loop with every wait settled once. | Tested with `ManualTimeProvider`, no sleeps. |

### Still deferred

Waits in Sequence outputs, Fork/Join branches, and sub-workflows (prerequisites 1 to 6 of the review outcome, except that the
scope-carrying request of prerequisite 1 now exists for the loop case); a Wait behind a Fork or Join inside a loop body;
actual-array-length budgeting for For Each; durable timers.

### Verification

Core `WaitNodeTests`, AgentFramework `WaitInLoopExecutionTests`, and Hosting `WaitViewerTests`. Not verified: Studio authoring of a
Wait inside a loop body in a real browser, and mixed graphs (Sequence or Fork profiles) with a loop-body Wait at runtime.

## Wait per-wait state (2026-11-25)

Implements prerequisites 1 to 3 of "Wait placement review outcome (2026-11-25)" as groundwork only. No placement rule changed:
`WorkflowWaitPlacement.NodesInsideRegions` and the validator still reject a Wait in a Sequence output, a Fork/Join branch, and a
sub-workflow (`wait.placement`), and every single-slot behavior reachable today is unchanged.

| # | Decision | Why |
| --- | --- | --- |
| 1 | **Per-wait state in `ControlledRun`.** The single `activeWaitId` and `settleActiveWait` are replaced by a dictionary keyed by `waitId`, each entry holding its own settle action, all under the existing `lock (gate)`. A settle action removes its own entry and records one settlement, so settlement is exactly-once per wait. The timer task of a wait settles only its own `waitId`. `Dispose` settles every pending wait as `wait.interrupted`. | Prerequisite 3. |
| 2 | **Internal concurrency limit, default 1.** `ControlledRun.ConcurrentWaitLimit` (internal, init-only) is the number of waits that may be pending at once. At the default the second wait still throws `A wait is already pending.` The only places that can set it are tests. A future slice raises it. | Keeps behavior byte-identical; no host option or public API. |
| 3 | **Budget reservation is atomic with registration.** The pending-count check, per-wait maximum, cumulative check, journaling of `wait.requested`, the cumulative increment and the dictionary insert happen in one critical section. A request that cannot be journaled reserves nothing. A wait that elapses, is cancelled or is interrupted keeps its reservation (consumed): the run is stopping or the time was spent. Releasing on cancel was considered and rejected because nothing can reuse the budget after a stop and releasing would make the total depend on settlement order. | Review prerequisite 3 allowed either; this is the simpler invariant. |
| 4 | **In-flight is a count, not a bool.** `inFlightNodes` replaces `inFlight`. An ordinary boundary ends the caller's previous node and starts its next, so the count is unchanged for a sequential run. A wait or approval request leaves the count. Its reacquire boundary does not leave it again (`releasesInFlight: false`), so it never consumes a sibling's count. A sibling branch start is a different event: the internal `WaitForBranchStartAsync` adds one. Nothing calls it outside tests today. | Pause acknowledgement needs to know whether any sibling is executing. |
| 5 | **Pause acknowledgement.** `RequestPause`, a wait or approval request, and a boundary acknowledge the pause (record `run.paused`, release the shared execution slot) only when `inFlightNodes` is zero. The last node to reach a boundary or enter a wait acknowledges. Earlier nodes at a boundary hold until Resume without acknowledging. Pending waits do not block the acknowledgement (the clock keeps running while paused, as in sub-decision 14 of the loop slice). The execution slot is also not released at a wait while a sibling still runs. | Prevents "paused" being reported while a sibling executes. |
| 6 | **Scope and iteration evidence.** `WorkflowWaitRequest` already carries `ScopeId` and `LoopIteration`, and `RecordWait` already writes `ScopeId` from the request for the request and the settlement. A test drives two concurrent requests with `fork/...` and `sequence/...` scopes and shows each event keeps its own scope and that only the loop-iteration request carries `loopIteration`. The scope string conventions for Sequence and branch scopes remain owned by the runner when those placements are enabled. | Prerequisite 1 is satisfied for the carrier. |
| 7 | **The reacquire boundary scope is captured per wait.** It was read from the shared `waitingForScope` field after the timer; it is now read under the lock at registration. | A shared field is wrong once two waits are pending. |
| 8 | **Child waits are visible.** `WorkflowWaitClosure.Enumerate` walks a definition and its pinned child definitions (`core.workflow-call` and resolvable `core.workflow-interface-call`, bounded by `WorkflowCallLimits.MaximumDepth`, cycles skipped) and returns each Wait with its path, duration and execution bound (call-node bound times the child's own bound). `WorkflowWaitOptions.ContainsWait`, which feeds the waiting-run cap (`LocalWorkflowRuntime` and `ControlledRun.HoldsWaitCapacity`) and the runner's fail-closed capability check, uses it, and `WorkflowWaitOptions.Check` charges and bound-checks child waits through it. The validator's `wait.placement` rejection of a child Wait is unchanged. | Prerequisite 2: the cap and the budget must not be bypassable once the rejection is lifted. |
| 8a | **The closure fails closed, defensively.** A child that cannot be inspected (a cycle by workflow id, an unresolvable interface call, a missing pinned id or definition, nesting past `WorkflowCallLimits.MaximumDepth`) is returned in `WorkflowWaitClosure.Analyze(...).Unverified` and counts as possibly containing a Wait: `ContainsWait` is true, which only affects waiting-run cap accounting. `Check` reports `wait.unverifiable` at the call path only when the definition itself contains a Wait node, so a workflow with no top-level Wait is admitted exactly as before even when a child is uninspectable (the validator already rejects child Waits). A definition with no call nodes skips the child walk (tested through an internal walk counter), so its result is identical to the earlier top-level scan. | A skipped child must never mean "no wait", and the walk must never reject a previously admitted workflow. |
| 9 | **Reconciliation is unchanged and now pinned.** Tests show several unsettled `wait.requested` events of one node and invocation stay separate by `waitId`, and a settlement resolves only its own wait in a settled/unsettled mix. | Prerequisite for concurrent waits. |

Known limits left for the slice that raises the limit: `waitingForNode` and `waitingForScope` are still single shared fields (they describe the last boundary, not each branch), and the boundary admission window (`executionSlot.WaitAsync` after the in-flight count dropped) can stall a sibling while another waits for the shared slot. Neither is reachable at limit 1.

### What remains before each placement can be enabled

Each placement needs its own review and the listed changes. Nothing below is done.

**Sequence output.** (a) Prove or document that Sequence outputs run strictly serially, or else treat them as concurrent and raise `ConcurrentWaitLimit` for them. (b) Relax the Sequence entry in `NodesInsideRegions` and add `Sequence` handling to `WorkflowWaitPlacement.ExecutionBounds` (an output reachable from a Sequence executes once per activation). (c) Pass the Sequence output scope id into `WorkflowWaitRequest` from the runner's Sequence path. (d) Add the authorization-to-outcome test for a tool node that follows a Wait. (e) Decide the viewer state for a Wait in a non-root scope (already scope-agnostic). (f) Keep the Sequence profile allowlist in step with the placement change.

**Fork/Join branch.** (a) Raise `ConcurrentWaitLimit` to the branch count (at most the Fork width) from the runner or host, and call `WaitForBranchStartAsync` for each branch start so the in-flight count is right. (b) Replace the anonymous shared execution slot with per-branch accounting, or define that all branches share one slot. (c) Define `run.paused` acknowledgement semantics for the UI while branches wait. (d) Decide whether static `Check` charges concurrent waits as a sum (safe, current meaning) or as the maximum. (e) Prove stop settles every pending wait at once. (f) Relax the Fork, Branch and Join entries in `NodesInsideRegions` and the fail-closed no-edge-table branch. (g) Add runner scope ids for branches. Step stepping and checkpoint recovery stay rejected.

**Sub-workflow.** (a) Remove the child `wait.placement` rejection in `WorkflowValidator.ValidateNested` only after confirming `WorkflowWaitClosure` covers every way a child is pinned. (b) Validate `request.NodeId` in `ControlledRun.WaitAsync` against the child definition: it currently looks the node up in `header.Definition` only, so a child Wait id would fail the cross-check. (c) Pass the child scope id and invocation through the call. (d) Test the 32-run cap and `HoldsWaitCapacity` with a child Wait. (e) Extend the viewer and reconciliation node lookup (`Definition.Nodes.ContainsKey`) to child node ids. (f) Decide how the child's loop bounds multiply into the parent's budget (the closure already multiplies them).

### Verification

Core `WaitNodeTests` and `ReconciliationEvidenceTests`; AgentFramework `WaitPerWaitStateTests` (limit 1 behavior, limit 2 per-wait state and scope evidence, atomic budget, cancel, dispose, and both pause-acknowledgement cases with `ManualTimeProvider` and no sleeps). The limit-2 cases drive the gate directly because no graph can produce concurrent waits yet; no real concurrent graph run is verified.

## Sequence output Wait facts (2026-11-25)

Fact-finding for prerequisite 4 of "Wait placement review outcome (2026-11-25)" (Wait in a Sequence output). **No placement rule
changed and no behavior was relaxed:** the validator still rejects a Wait in a Sequence output (`wait.placement`), and the unified
Sequence path profile independently rejects it (`sequence.path.node`). Every fact below is pinned by a test in AgentFramework
`SequenceOutputWaitFactsTests`, which drives the production runner, dispatcher, admission owner, Sequence coordinator, and the host
`ControlledRun` gate with `ManualTimeProvider`. The only test-only piece is the internal runner entry
`ExecuteIgnoringWaitPlacementProofAsync`, which ignores exactly the `wait.placement` and `sequence.path.*` validation issues (every
other issue still throws) and is not reachable from persisted content or any host.

### Decision table

| # | Question | Observed behavior (test) | Verdict for relaxing |
| --- | --- | --- | --- |
| 1 | Do outputs run concurrently or one at a time? | **Concurrently.** The production root path (no dispatch session) calls `DispatchNext` for every output in one loop, then `Task.WhenAll`s the sends; no `maximumConcurrentOutputs` applies there (that cap, 1 to 4, exists only for sessions nested in a Loop or For Each pass). `sequenceEventGate` only serializes lifecycle-event delivery, not execution. With the first output's tool held, the second output's tool started and completed (`Production_dispatches_every_output_at_once_...`). Execution advances in lockstep Agent Framework supersteps: the second output's later nodes (Output, collector) and its `sequence.output.completed` come only after the held node of the first output ends (`A_wait_stalls_the_sibling_output_...`). Drain mode (`DrainInFlightOutputs = true` in production) is therefore real concurrency, not an ordering. | **Unfavorable.** Prerequisite 4(a) resolves to "treat as concurrent": two waits in two outputs fail the run today (`A wait is already pending.`, first wait settled `wait.interrupted`, `Two_waits_in_two_outputs_collide_...`). `ConcurrentWaitLimit` must be raised for this placement and the static budget charged as a sum (already the case). With limit 2 both waits run, each with its own `waitId` (`With_a_limit_of_two_...`). |
| 2 | Scope and lineage on a Wait in an output | `RecordWait` carries `ScopeId` and `LoopIteration` from the request, but the runner sets them only when `received.ActivationFrame` is non-null. A production root Sequence output has no frame, so `wait.requested` and its settlement have **no `ScopeId`, no `loopIteration`, and no `sequenceLineage`** in the payload. The wait's `execution.activated` event does carry `sequenceLineage` (`OutputId`, `Ordinal`) and shares the wait's `InvocationId`, so attribution works only by joining on invocation id (`Wait_events_in_a_sequence_output_carry_no_scope_...`). `SequenceEvidence.TryReconstructPlayback` filters to lifecycle kinds, so `wait.*` events between `sequence.output.dispatched` and the terminal do **not** break the state machine; it returns running while pending and completed afterwards. Reconciliation shows no unresolved work after settlement. | **Needs change, not blocking.** Add Sequence output scope (`sequence/<outputId>`, the same string the collector uses) and lineage to `WorkflowWaitRequest` and the wait payloads, plus a test; the viewer already keys on the latest wait event of a node. |
| 3 | Stop, pause, cancel, drain, abandon with a pending Wait | **Stop** settles the wait as `wait.cancelled` once, each output as `sequence.output.cancelled` once, `sequence.cancelled` once, run `cancelled`, no unresolved work (`Stop_during_an_output_wait_...`). Note the sibling whose tool already completed is also recorded cancelled, because its collector step never ran (superstep stall, row 1). **Pause** is acknowledged at the wait, the clock keeps running, the node holds after elapse, and Resume completes (`Pause_is_acknowledged_at_the_wait_...`). **Defect:** with a sibling output's tool still executing, `run.paused` is recorded anyway (`Known_gap_pause_is_acknowledged_...`): runner boundaries keep the in-flight count at one for the whole Sequence, so the wait's request drops it to zero, and `WaitForBranchStartAsync` is not called for outputs. Without a Wait the same scenario does **not** acknowledge the pause (`Pause_without_any_wait_is_not_acknowledged_while_a_sibling_output_tool_is_executing`): Agent Framework supersteps run in lockstep, so no boundary is reached until the held tool returns. The gap exists only because of the Wait. **Sibling failure** (a tool throws) settles the pending wait as `wait.interrupted` with the reason "The host stopped while the wait was pending", not `wait.cancelled`, because the host never requested a stop (`A_failing_sibling_output_settles_...`). **Failure termination in a sibling** is invisible while the wait runs: the failed output is settled by the collector after the superstep, so drain does not act and the abandon grace never starts even when shorter than the wait; the waiting output then runs to completion and the Sequence ends failed (`A_failure_termination_in_a_sibling_output_is_invisible_...`). A wait can delay failure visibility by up to the per-wait maximum; Stop is prompt. | **Unfavorable until fixed.** (i) Per-output in-flight accounting so pause is not acknowledged while a sibling executes. (ii) Decide the settlement kind and reason for a failure-driven cancellation of a pending wait (cancelled with an accurate reason, or keep interrupted and fix the text). (iii) Decide whether a pending wait should be preempted when a sibling terminates the run. |
| 4 | Wait between tool authorization and outcome | **Unreachable.** `approval.target` ("an approval must immediately precede exactly one MCP tool node") rejects a Wait between an Approval and its tool, independent of placement, and the test seam does not ignore it (`A_wait_between_an_approval_...`). A Wait **before** an Approval runs and completes before `approval.requested`; no `wait.*` event falls between `approval.approved` and the tool's completion (`A_wait_before_an_approval_...`). An Approval in a Sequence output also needs the approval-to-tool data edge, or `ControlledRun` rejects the action as not matching the captured workflow (also true without a Wait). | **Favorable.** No change needed. |

### Recommendation: defer

Do not relax the Sequence output placement yet. Blockers, in order:

1. Raise and test `ConcurrentWaitLimit` for outputs (rows 1 and 3), and keep the static budget as a sum.
2. Carry the Sequence output scope and lineage on `WorkflowWaitRequest` and the wait events, with production tests (row 2).
3. Fix per-output in-flight accounting for pause acknowledgement (row 3, defect).
4. Decide the settlement kind and reason when a sibling failure cancels a pending wait, and whether a wait should be preempted by a terminating sibling (row 3).
5. When all of those land: relax the Sequence entry in `NodesInsideRegions`, add `core.wait` to the unified Sequence path allowlists (`supportedPathNodes` and the linear-path check in `ValidateUnifiedSequenceGraph`), and move the characterization tests above onto the validated production entry point.

Row 4 and the playback state machine need no change. Not verified: Studio authoring, a Wait inside a Sequence that is itself nested in a Loop or For Each (still rejected), Waits in non-unified Sequence schemas, and a real browser.

### Sequence output pause acknowledgement design note (2026-11-25)

**Finding.** The defect needs a Wait. With two concurrent outputs, one tool held and no Wait, `RequestPause` stays unacknowledged until the held tool returns, because the next runner boundary is not reached before the superstep ends. A Wait in one output calls the register path in `ControlledRun`, which decrements the single in-flight count (held at one for the whole Sequence by the Sequence's own boundary) to zero and acknowledges the pause while the sibling's tool still runs. The Wait placement remains rejected by the validator, so no shipped workflow can hit this.

**Intended semantics.** A pause is acknowledged only when no node is executing; nodes already running may finish; a pending Wait or approval does not count as executing.

**Why no production change.** Satisfying that needs the host to know how many nodes the Sequence's concurrent outputs are executing, i.e. per-node (or per-output) in-flight tracking with the runner reporting each node start and end. Today the count is one number driven by serial boundaries, and Wait and approval registration assume they are the only running node. That is a redesign of the gate, so production is unchanged and the behavior stays pinned: `Known_gap_pause_is_acknowledged_at_the_wait_...` characterizes today, and the skipped `A_pause_is_not_acknowledged_at_a_wait_while_a_sibling_output_tool_is_executing` states the desired result. This remains blocker 3 above.

## Per-activation in-flight tracking: attempted and held back (2026-11-25)

A hybrid design for `ControlledRun` was implemented in an isolated copy and reviewed adversarially; it is **not merged**. It kept the baton count, added a set of executing (node id, invocation id) activations fed from node lifecycle events, and switched a run permanently to set-based quiescence when a Wait or approval registered while a sibling was executing. The skipped test `A_pause_is_not_acknowledged_at_a_wait_while_a_sibling_output_tool_is_executing` stays skipped.

The review found two blocking problems:

1. **The switch is order-dependent.** It happens only if the sibling has already emitted `node.started` when the Wait or approval registers. Sequence outputs run concurrently, so if the waiting lane registers first the run falls back to the old baton logic and the early pause acknowledgement returns. The new tests did not control which lane registered first.
2. **The shared execution slot stays held across an unbounded approval** in tracked mode when a sibling settles and no pause is pending, because nothing re-checks quiescence on removal. That starves other runs for as long as a human takes to decide.

Non-blocking findings: the changed `Step` handling applied to untracked sequential runs too, so a leaked set entry for the stepped node id would stop `pauseAfterSteppedNode` from ever firing; the approval reacquire path re-adds no key; `TracksActivation` tracked unknown node ids against its own description; a 4096-entry cap silently stops tracking; and a leaked entry in the permanent tracked mode would hang pause forever, which the baton design could not do.

**Direction chosen.** Do not ship a sticky mode switch. The only reachable trigger today is an Approval in a Sequence output, because a Wait is rejected by validation there. The simpler design to evaluate first is to reject an approval or Wait beside concurrently running Sequence outputs at validation, which removes the set and its leak surface. If concurrent waits are later required, a replacement must enter tracked mode from the boundary path rather than at registration, release the slot when the set empties while a suspension is pending, and prove every started node ends with a terminal event.

## Approval beside concurrent Sequence outputs: attempted and rejected (2026-11-25)

An approval inside a Sequence output can acknowledge a pause while a sibling output is still executing. A fix was tried in an isolated copy: the approval skipped the in-flight decrement and the slot release, marked by a non-journaled flag set from the Sequence lineage. It was **not merged**. Adversarial review found two blocking problems:

1. **Host-wide, unbounded starvation.** The execution slot is one semaphore shared by every owned run, and a pending Sequence-output approval holds it until a human decides or the run is stopped. Waits are capped so they cannot starve other work, and the same cap logic would be needed here.
2. **The invariant still leaks.** The in-flight count is pooled across outputs, so the reacquire on a decision made while the sibling still runs drops the count to zero, and a multi-node sibling's next gated node acknowledges the pause. The invariant the fix claimed does not hold.

The simpler design the review recommends is to reject an approval inside a Sequence output at validation, mirroring `wait.placement`. That is a compatibility-breaking change for saved workflows of that shape and needs an explicit decision and a migration path, so it is not made here. The real fix is per-output in-flight accounting, which remains deferred. The early `run.paused` defect stays open and documented, with its skipped desired-behaviour test.

## Approval placement rule (2026-11-25)

**Rule.** `WorkflowValidator` rejects an Approval whose execution path lies inside a Sequence output of a Sequence with more than one output. The issue is `approval.placement` at `nodes.<approval>`. The walk is the region walk behind `wait.placement`: `WorkflowWaitPlacement.NodesInsideRegions` and the new `NodesInsideConcurrentSequenceOutputs` share `NodesReachableFromOpeners`, and only the opening Sequence outputs and the reported node type differ. Approvals on the root path, after a Fork/Join region, or in a Loop or For Each body are not reported by this rule.

**Why.** Outputs of one Sequence run concurrently (row 1 of "Sequence output Wait facts"). An approval suspends its own output while the siblings keep executing, and the in-flight count is pooled across outputs. The "attempted and rejected" section names rejection at validation as the simpler design. Waits keep `wait.placement`; their rule is unchanged.

**What still works.** Approvals on the root path and after a Fork/Join region validate. The approval-to-MCP-tool target rule (`approval.target`) is unchanged.

**Facts found while implementing.**
- The unified Sequence path profile already rejected an approval in a Sequence output as `sequence.path.node` and `sequence.path.terminal`, so no validated graph contained one. The new diagnostic is specific and independent of the path profile, as `wait.placement` is. It adds no new rejection of a validated saved workflow.
- Sequence outputs need 2 to 4 entries (`SequenceOutputLimits.MinimumOutputs`), so a one-output Sequence never validates (`sequence.configuration`). The "single-output Sequence keeps working" clause has no validated case. The test pins only that the placement rule does not fire for a one-output Sequence.
- A Sequence graph has exactly one Output barrier (`sequence.count`), so no root-path step can follow a Sequence in the same graph. "After a join" is therefore tested as an approval after a Fork/Join region.

**Migration for saved workflows.** Nothing validated changes, because the path profile already rejected this shape. An author whose workflow fails with `approval.placement` or `sequence.path.node` has two options. Split the Sequence so the gated tool runs on a single path, with the approval and tool on a root path before the Sequence, when the tool input is available there. Or move the approval onto a path that is not a Sequence output.

**Test seam.** `WorkflowRunner.ExecuteIgnoringWaitPlacementProofAsync` also ignores `approval.placement`, as it already ignores `wait.placement` and `sequence.path.*`. It stays internal and test-only. The two approval-driven characterization tests in AgentFramework `SequenceOutputWaitFactsTests` are bypass-only: they assert the production rejection and then run through the seam. The `approval.target` hazard still throws through the seam.

**Not verified.** Studio authoring feedback for this diagnostic, and browser behavior. Core unit tests cover the validator and walk; no runtime change was made.
