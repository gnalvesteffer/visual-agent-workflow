# Decision 0081: Bounded Wait/Delay (latent) node

**Status:** PROPOSED, reviewed 2026-11-24: option 2 deferred behind prerequisites (see Review outcome); not implemented

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
  admission. No production time source or Delay node exists.

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
- True: no `TimeProvider` exists in `src` or `tests`. `DateTimeOffset.UtcNow`
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

Characterization tests feed `future.unknown-kind` and the realistic additive
kind `wait.requested` to each reader. **No reader rejects or crashes on an
unknown kind; all ignore it.** The earlier claim that older readers "fail
closed on unknown kinds" is disproved. No code was changed: nothing crashes
or misclassifies run status or terminal state.

| Reader | Behavior | Tests |
| --- | --- | --- |
| `RunSnapshot.Status` / `IsTerminal` | Ignore. An unknown kind alone gives `running`; it never changes completed, failed, paused, suspended or waiting-approval. | Core `UnknownEventKindTests`: `Unknown_event_kind_leaves_run_status_running_and_non_terminal`, `..._does_not_change_terminal_pause_suspension_or_approval_status`, `..._as_the_only_event_is_running_not_queued` |
| `WorkflowWorkEvidence` (reconciliation work) | Ignore. An unsettled `wait.requested` is never reported as unresolved work. | `Unknown_event_kind_is_ignored_by_work_evidence`, `Unsettled_wait_requested_is_not_reported_as_unresolved_work_by_older_readers` |
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
