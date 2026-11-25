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
