# Decision 0076: Nested Fork/Join composition

**Status:** Accepted target; static schema-2.0 ownership validation, public success/failure/cancellation proofs, Studio insertion/removal and existing-region branch-count editing, versioned activation/arrival playback, File/SQLite reopen, and three-level workflow/project-package round trips are implemented. A string-result WorkflowCall is allowed in a static branch; public runner and File/SQLite proofs cover child success, failure, and host cancellation with no partial Join result. Proof-only admission/drain scheduler passes its duplicate-arrival cases, while production duplicate-arrival integration remains open.

**Date:** 2026-11-20

## Context

Unreal Blueprint graphs can compose control flow by placing a parallel region
inside another branch. VAW supports static Fork/Join regions. Core now validates
strictly owned nested regions and the public runner has a three-level success
proof. Studio can insert a two-branch execution skeleton on an eligible explicit
execution edge, remove the smallest containing region, and edit the branch count
of an existing region while preserving stable branch IDs. Earlier private
production-body proofs ran the same static inner Fork and Join concurrently
under separate outer branches, restored parent lineage, and covered success,
failure, cancellation, duplicate arrivals after join completion, unexpected
branch IDs, and an unrelated configured Fork identity. Their file/SQLite
reopen and playback checks bypassed public graph validation; the cross-layer
proofs and remaining gaps are tracked below.

Schema 2.0 is VAW's unified graph contract. Its purpose is to consolidate graph
capabilities without author-facing schema or capability promotion gates.
Nested Fork/Join must therefore become part of schema 2.0 rather than a new
versioned opt-in. Existing historical schemas retain their recorded behavior.

## Adjudicated direction

The independent proponent, opponent, and adjudicator review recommends
**arbitrary finite-depth nested Fork/Join as the final schema 2.0 contract**.
Do not add a fixed nesting-depth limit, schema promotion, or capability flag.
Enforce the existing general graph, activation, concurrency, event, and output
resource limits instead. An initial one-level implementation is acceptable as
a staged vertical slice, but it is not the final support boundary or a reason
to encode one-level assumptions in saved definitions.

The static validator must establish region ownership and matching joins before
publication or execution. Runtime correlation must use activation lineage, not
static Fork identity alone. Each nested region belongs to exactly one parent
branch; it rejoins there and continues within that owner. Do not share one
static nested region or continuation across sibling parent branches under this
contract. The private shared-Fork proof verifies isolated arrivals and parent
lineage restoration, but it cancels before proving that the continuation can
route to the correct outer branch-specific Join input. Supporting that shape
would require a separately reviewed parent-aware routing contract.

## Contract requirements

- Every declared Fork branch has one stable anchor and reaches exactly one
  matching Join input. Results from different Fork activations cannot mix.
- Every nested region has exactly one parent-branch owner; the child Join
  returns execution to that owner. A static child region cannot be shared
  across sibling owners.
- Nested regions cannot cross ownership boundaries, escape or bypass their
  matching Join, arrive through a wrong Join, or have ambiguous branch
  ownership. Malformed parent lineage fails closed.
- Concurrent activations of the same static Fork and Join remain independent.
  Duplicate, unknown, stale, and mismatched arrivals cannot complete a Join
  with partial or mixed values.
- Failure and cancellation settle active siblings deterministically and emit
  no joined result when any required branch does not succeed.
- Persisted events identify the activation and parent lineage sufficiently for
  file/SQLite reopening and passive playback without re-execution.
- Studio can create, edit, remove, diagnose, save, and reopen nested regions
  under the same schema 2.0 contract. Invalid drafts remain saveable but cannot
  publish or run.
- Limits remain global and explicit. Current general workflow bounds include
  256 graph nodes and bounded branch count and join-result size; implementation
  must verify that activation and recorded-event limits adequately bound
  nested concurrency and memory use.

## Implementation sequence

1. Specify and test the static ownership algorithm, including valid nested
   paths and rejection of crossing, escape, bypass, wrong-join, cycle, and
   ambiguous topologies.
2. Close runtime evidence gaps for parent-lineage tampering and duplicate
   arrivals before Join completion. Retain the existing success, failure,
   cancellation, duplicate-after-completion, unknown-branch, and mismatched
   Fork identity proofs.
3. Integrate validation with package/import handling, Studio editing, runtime,
   persistence, and passive playback without introducing a schema gate.
4. If useful, ship one nested level as an intermediate slice. Before claiming
   nested parallel parity, demonstrate deeper nesting and concurrent reuse of
   one static inner Fork under repeated activations of its same parent owner
   across the full cross-layer contract. Reject sibling-owner shared regions
   unless a separately reviewed parent-aware routing contract is introduced.

## Current status

Unified schema 2.0 now routes Join arrivals directly to VAW's
activation-correlated Join; historical schemas retain their existing fan-in
route. The focused Fork/Join runtime group passes 43/43. A malformed graph
whose Join names a different existing Fork fails in both file and SQLite
proofs, with the failure preserved by passive playback (2/2).

Core now validates nested unified-schema Fork/Join regions with arbitrary
finite depth, requires a unique parent-branch owner, and rejects shared
sibling ownership and joins paired with another Fork. Unified validation
also supports sequential root regions. Regressions cover two and three nested
levels, sequential roots, and malformed ownership/pairing cases. The
public runner also executes a normally validated graph with three nested levels
and a later sequential root region to a successful joined result. Public-runner
failure and cancellation regressions now verify that no nested Join or final
Output reports a completed result when a required branch does not succeed.
Studio resolves a selected node to the smallest containing nested Fork/Join
region. Its removal preview identifies the matching Join and all removed nodes;
removal deletes contained descendants, preserves the parent region, and
reconnects the owner branch to the removed region's continuation. Surviving
data consumers are listed and remain in a saveable invalid draft when their
inputs are disconnected. Studio insertion is one undoable edit and preserves
all data connections; authors wire branch data separately. A public runner
regression executes a nested region inserted inside an existing owned branch.
Core tests cover selecting/removing nested and deep regions, parent
preservation, continuation routing, and root removal without schema/output-type
changes. Schema 2.0 now emits versioned `parallel.join.arrival` events after
accepting a branch result. The payload identifies the Join, Fork activation,
branch, and complete root-to-leaf Fork lineage. Core validates configured
Fork/Join/branch identities and the static nested-owner chain, then passively
reconstructs accepted branch counts and nesting depth at any recorded prefix.
Schema 2.0 also records a versioned `parallel.join.activated` event immediately
before dispatching branch work. It means the Fork activation was admitted for
dispatch; it does not claim every branch started or reached the Join. The event
contains the paired Fork/Join IDs, activation ID, expected branch IDs, and
parent lineage. If recording fails, branch dispatch does not begin. Arrival
events remain the sole evidence that a branch result was accepted. Playback
validates activation and arrival identities against the saved graph and keeps
Join lifecycle state separate from the run outcome. A zero-arrival activation
left open at failure/cancellation is shown as incomplete with 0 of the expected
branches, rather than falsely assigning the run's cause to the Join. The run
inspector renders this projection without re-executing the workflow.

Histories without activation events retain the prior accepted-arrival
projection; histories with neither event retain their ordinary status and
progress, and the inspector explicitly reports missing lineage instead of
reconstructing it from progress text.
A three-level public-runner regression verifies eight arrivals, successful
reconstruction, rejection of tampered parent lineage, and rejection of duplicate
arrival evidence. Public failure/cancellation regressions reconstruct zero and
partial arrivals as incomplete while preserving the run terminal status. A
rendered run-inspector proof shows a zero-arrival activation and its accepted
count at a selected prefix. File and SQLite reopen tests cover zero and partial
arrivals for both failed and cancelled runs, without re-execution. A current
schema 2.0 graph with three nested Fork levels also round-trips through a
workflow package and a project release while preserving definition/layout
hashes and exact serialized bytes. Studio's existing-region editor can add and
reorder branches and remove empty branches by resolving the selected Fork's
uniquely matching Join. Core and browser regressions prove an inner branch-count
edit preserves its containing outer Fork/Join region, existing wires, stable
branch identities, and graph validity. Removal is available only when the
matching Join is unique. A per-activation failure latch now rejects arrivals queued after a duplicate or
other Join failure; the focused duplicate-then-final test covers this state
transition. File/SQLite runner proofs continue to cover late duplicates after
completion. A controlled File/SQLite proof also releases a duplicate branch-A
Agent and the final branch-B Agent together against one private nested Join
activation; both Agents settle, the Join fails, and reopened playback preserves
the same event history (2/2). This proof bypasses public validation and does not
establish that the duplicate was admitted before Join commit. A scheduler-level
test that races an already-admitted duplicate against final-branch commit
remains open. These tests
establish structural consistency and replay, not cryptographic tamper
resistance. See [the
Blueprint parity roadmap](../roadmap.md) and
[parallel graph behavior](../parallel-graphs.md).

The scheduler-level race has a sharper boundary than the initial checklist
stated. The per-activation semaphore serializes Join handlers, and the failure
latch rejects duplicates once they are processed. If the final unique arrival
acquires the semaphore first, however, the Join can emit `NodeCompleted` and
return its result before a queued duplicate is processed. A later failure
cannot retract downstream work. An atomic dispatch claim can ensure at-most-once
completion, but cannot identify a message that has not reached the Join.
Likewise, no finite Join can reject a duplicate delivered only after its result
has crossed the dispatch boundary.

The stronger guarantee to prove is: duplicate arrivals already admitted for an
activation before Join commit are accounted for before a joined result is
emitted or dispatched. This requires a scheduler admission/drain or branch
settlement boundary; a semaphore check, priority rule, yield, or quiet-period
delay does not establish one. A three-role review on 2026-11-20 recommends
**revise** and leaves this contract open because the current scheduler has no
proven settlement signal. The required deterministic test must hold the final
arrival at commit, admit a duplicate for an already accepted branch, and prove
that no Join completion or downstream continuation occurs; it must also prove
that ordinary unique arrivals complete and each activation dispatches at most
once. Duplicates delivered after commit remain a separate failure case and
cannot undo work already dispatched.

On 2026-11-21, the pinned Agent Framework 1.26.0 source was inspected at commit
[`9c8a3514a15a702f81bfda456d80c7ccaa4d2712`](https://github.com/microsoft/agent-framework/tree/9c8a3514a15a702f81bfda456d80c7ccaa4d2712).
`InProcessRunner.DeliverMessagesAsync` processes one receiver's queued messages
sequentially and calls that executor's `OnMessageDeliveryFinishedAsync` in a
`finally` block. `RunSuperstepAsync` runs receiver groups concurrently and
awaits all receiver tasks before advancing. Therefore the callback closes one
executor's current delivery batch; it does not prove that another executor has
no queued or in-flight work capable of reaching this Join in a later step.
Buffering a Join result until this callback alone is insufficient.

The next authorized investigation is a **proof-only scheduler prototype** that
owns every VAW message dispatch and its admission/settlement bookkeeping. Before
using a ledger in production, define one bounded work identity per admitted
delivery, prove successor reservation and current-work settlement are atomic,
and prove nested Fork fan-out and child-Join collapse preserve ancestor
accounting. Its deterministic cases must cover an admitted duplicate held while
the final unique arrival is ready, unique-arrival success, nested accounting,
an unrelated descendant that must not delay the Join, failure/cancellation,
dispatch or evidence-write failure, stale lineage, and bounded cleanup. The
prototype is not Agent Framework integration, checkpoint recovery, or public
runtime evidence. No production runtime change is selected until this
end-to-end proof passes and the lifecycle/boundary rules are specified.

The proof-only `StructuredProofDispatchLedger` now tracks unique delivery
identities, atomic successor reservation before settlement, nested parent
lineage, held Join arrivals, Join collapse, failure cleanup, and bounded
admission. It is wired into `ExecuteStructuredSchedulerProofCoreAsync` for
Fork/Join, Sequence output paths, ordinary successor dispatch, and one
collaboration region with one- or two-phase rendezvous; nested workflow-call
nodes remain unsupported in this private mode. File and SQLite proofs persist,
reopen, and render passive playback for unique-arrival success and
duplicate-arrival failure. A deterministic duplicate-injection hook exercises
the scheduler with a valid graph: both the duplicate and final unique Agent
finish, the Join fails, and neither Join completion nor Output
dispatch occurs. Other proofs verify unique-arrival output and two-level nested
Join parent-lineage restoration. Ledger-level tests cover stale/tampered
lineage, failure cleanup, repeat settlement, unrelated activation isolation,
and outstanding-delivery bounds. Structured-dispatch tests also cover branch
failure, cancellation with a held sibling, recorder failure before Join
commit, collaboration success across both rendezvous shapes, collaboration
failure suppression, and a controlled direct-MCP denial that respects node
grants and suppresses Join/output completion. The collaboration proof also
covers host cancellation at one- and two-phase release and timeout before
rendezvous completion. File/SQLite collaboration store and playback proofs
cover successful one- and two-phase runs; failed/cancelled collaboration
playback remains unqualified. This broker fixture does
not qualify a real MCP service or the full authority/evidence boundary. This is
still only a test-only scheduler prototype: it is not used by public Agent
Framework execution. The targeted store and playback proofs do not establish
scheduler crash recovery or checkpoint recovery. Sequence output failure with
sibling cancellation is covered, while host cancellation and broader Sequence
semantics remain open. MCP authority and evidence boundaries, failed/cancelled
collaboration persistence/playback, nested workflow-call integration, and
production scheduler persistence/playback integration remain open before any
production runtime decision.

A production-shaped dispatcher admission audit now guards this prototype:
every `DispatchNode` invocation must present an active ledger delivery, and a
delivery can be claimed for dispatch only once. A valid nested Fork/Join run
through the actual node executors finishes with no outstanding delivery
tickets; unit tests cover unadmitted and repeated claims. Sequence barrier
reservations may settle without a node dispatch, so admitted and started counts
are not required to match. This makes the proof scheduler's dispatch boundary
explicit; it does not prove interception of Agent Framework's automatic edges
or qualify the public runner.

A deterministic actual-dispatch test now holds one nested Agent after its
`NodeStarted` event and observes an unrelated sibling nested Join complete
before releasing it. The held path's own Join remains incomplete until release;
the run then succeeds and the shared ledger drains to zero. This complements
ledger-only activation isolation with execution through the proof scheduler's
existing node executors. It remains private proof-scheduler evidence and does
not establish the same scheduling behavior in public Agent Framework execution.

On 2026-11-21, a three-role review adjudicated the next proof boundary for
nested workflow calls. Separate child ledgers were rejected for this increment
because they do not establish a root-wide outstanding-work bound. The private
proof scheduler now admits one pinned `WorkflowCall` child root into the same
ledger while retaining the parent call delivery. Child Fork/Join work inherits
the parent delivery lineage, and child events pass through the root event
sequencer with one nested call scope. Focused regressions cover a passthrough
child, a child Fork/Join, rejection of a second call depth, and shared delivery
capacity (4/4). A deterministic File/SQLite test now reopens and passively
renders successful, failed, and cancelled call-bearing child Fork/Join histories
(6/6), including reconstruction of each call disposition and the completed
child Join event scope on success. Child failure suppresses the parent Output;
host cancellation settles the child call as cancelled. The earlier
duplicate-injection proof also admits the extra delivery before either branch
starts; the shared ledger holds Join release until that admitted delivery
settles, and the persisted duplicate outcome has no Join completion or Output
dispatch. These close only proof-scheduler success-history persistence, failure/
cancellation history persistence, and the bounded admitted-duplicate case.
This remains proof-only and does not change public execution or resolve the
production scheduler race.
At this stage, it did not yet qualify parent-fork call composition,
`WorkflowInterfaceCall`, recursive or arbitrary-depth calls, evidence-write
cleanup, approval or MCP interactions, or loops. Those boundaries must be
proven before treating nested calls as a general scheduler capability. The
following public-runner increment has since closed the bounded parent-fork
call-composition case.
Unified schema 2.0 now admits a pinned string-result WorkflowCall as a
static branch step, and the public runner waits for the child result before
completing the parent Join. Core regressions accept string results, reject JSON
results, and verify canonical serialization. Public File/SQLite regressions
reopen successful, failed, and host-cancelled call branches (6/6); neither
failure nor cancellation emits a joined result or starts Output. Architecture,
parallel-graph, and user-guide references describe this current boundary. The
production duplicate-arrival race remains open.

On 2026-11-21, the private scheduler proof was extended to execute a root
`WorkflowInterfaceCall` and its pinned implementation through the same root
ledger, event sequencer, and delivery bound. File and SQLite regressions cover
the implementation's nested Fork/Join unique-success path and a duplicate
admitted before Join commit; the duplicate fails the child Join and prevents
the caller's Output from starting (4/4). This proves only the bounded
proof-scheduler path. Interface calls inside static parallel branches,
recursive or arbitrary-depth calls, approval/MCP policy, and production
dispatch integration remain unqualified. The public scheduler and schema
admission were not changed.


## Public Join receiver-batch boundary (2026-11-21)

Unified schema 2.0 now stages a complete Join result instead of returning it
from the final arrival handler. `OnMessageDeliveryFinishedAsync` commits the
result and sends its continuation only after Agent Framework has delivered all
messages for the current superstep to that Join executor. A controlled
concurrent duplicate/final-arrival regression now asserts that the Join emits
no `NodeCompleted` and the workflow Output never starts when both arrivals are
in that receiver batch. Ordinary configurable Join execution and public
three-level nested success continue to pass their runner tests.

This is a receiver-batch boundary, not the activation-wide admission/drain
required by the accepted contract: it cannot account for active descendant
work that has not yet dispatched a Join message. The current race is narrowed
to that remaining boundary; it is not closed for every delivery admitted in a
branch activation. The production Join-specific experiment therefore does not
justify promoting the proof ledger or claiming full duplicate-arrival parity.
Next, establish whether every dispatch capable of reaching a Join can be
atomically accounted for under `InProcessRunner`; if not, evaluate the
root-owned dispatcher with the broader Agent Framework behavior-preservation
and failure/persistence proofs before production integration.

## Public nested progress boundary (2026-11-23)

A focused unit experiment used the ordinary `ExecuteCoreAsync` / Agent
Framework path via `ExecuteAfterNodeStartGateProofAsync`. It ran the valid
nested graph used by the private scheduler test, held `inner-agent-left-a`
after its start callback, and waited for `inner-join-right` to complete before
releasing the held Agent. The Join did not complete within the bounded wait;
after the Agent was released, the public run completed successfully. The
temporary investigation assertion was removed after recording this result, so
the checked-in suite remains green. The private scheduler test still proves
the desired independent progress only for its own dispatch path.

The pinned Agent Framework 1.26.0 implementation was checked at commit
[`9c8a3514a15a702f81bfda456d80c7ccaa4d2712`](https://github.com/microsoft/agent-framework/tree/9c8a3514a15a702f81bfda456d80c7ccaa4d2712):
`InProcessRunner.RunSuperstepAsync` starts all queued receiver groups and
awaits `Task.WhenAll` before advancing the step. Each receiver callback can
finish and queue a Join continuation, but the runner cannot advance that
continuation while another receiver in the current superstep remains held.
`InProcessRunnerContext.SendMessageAsync` maps deliveries into `_nextStep`;
there is no public runner hook for replacing the superstep scheduler or
intercepting its internal edge map. This explains the focused public-runner
failure and establishes that an executor/edge wrapper inside this runner cannot
provide the required independent nested progress.

The revised direction is a VAW-owned root dispatcher for public execution, but
only after a proof stage establishes parity with the production node handlers
and all supported graph constructs. Do not ship a schema/capability gate or
promote the current private proof entry point as production. The next proof
must exercise the public API route with deterministic unit tests and account
for every outgoing transition, including ordinary/conditional edges, Fork and
Join, Loop feedback, Sequence, workflow calls, collaboration, cancellation,
failure, evidence-write failure, and bounded cleanup. If any transition still
bypasses admission or the required behavior demands a hidden schema split, the
design is not ready for production integration.

## Proof scheduler nested WorkflowCall recursion (2026-11-23)

The proof-only dispatcher no longer imposes an extra one-child WorkflowCall
limit. A focused unit test runs three nested pinned child calls—the maximum
depth admitted by `WorkflowCallLimits`—through the same dispatch ledger and
event sequencer, and verifies successful output plus all three subworkflow
start/completion pairs. Recursive definitions beyond the existing validator
bound remain rejected by Core. This removes a prototype-only restriction; it
does not establish public root-dispatch integration or broader child-workflow
parity.

The same proof dispatcher also has a focused unit regression for both
conditional outcomes in a normalized current-schema graph. It verifies the
selected branch port, chosen execution connection, absence of the skipped
connection, and the resulting transformed output. The test exercises the
production `StringExecutor` handler through VAW-owned edge dispatch; the
ordinary public runner is unchanged.

The proof dispatcher also executes a normalized current-schema Loop in focused
unit tests. A matching predicate runs the body through all three configured
iterations and stops at the maximum; a non-matching predicate takes the done
route with zero body iterations. Both cases assert the loop lifecycle evidence
and that the dispatch ledger is fully settled. Nested loop/parallel interaction
and public integration remain unqualified.
