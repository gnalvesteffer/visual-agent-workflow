# Decision 0081: Bounded Wait/Delay (latent) node

**Status:** PROPOSED, pending three-role architecture review; not implemented

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

**Evidence and compatibility.** Additive event kinds only. Older readers must
fail closed on unknown kinds (an `unknown event kind` playback error), never
silently skip them. Playback shows a pending wait and its deadline as recorded
data and never recomputes elapsed time from the current clock. No schema
version or capability gate is added, per repository policy.

**Test determinism.** An injected `TimeProvider` (fake clock) controls
advance. There are no sleeping tests. Required cases: elapse, cancellation
before and during, shutdown, deadline already past, bound exceeded, restart
with an unsettled wait, parallel waits, Sequence abandon, a Loop of waits,
File and SQLite playback, and older-reader rejection.

**Security.** The maximum, the time source, and any total-run wait budget are
host-owned. Workflows, MCP outputs, and run input cannot raise them.

## Evidence required

- A runner proof that the permit is released during a wait and reacquired
  after it, with the admission count unchanged.
- Journal tests for the event kinds, with an old-reader fixture failing closed.
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
