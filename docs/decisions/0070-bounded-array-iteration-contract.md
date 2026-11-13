# Decision 0070: Bounded array iteration needs an execution and evidence budget

## Current review outcome (2026-11-13)

The three-role architecture review returned **REVISE**: do not select either
existing candidate for production implementation yet. Extend the proof matrix
with a hybrid candidate in which VAW owns item sequencing, nested iterator
identity, nearest-loop `Break`, shared budgets, and run lifecycle, while Agent
Framework owns static routing and joins inside each body. This is a candidate
to test, not an approved production architecture. The review found that the
current subworkflow candidate has stronger production-body integration
evidence, while the structured candidate has stronger activation-state
evidence; neither demonstrates the full graph-capability and shared-budget
contract. The hybrid may still split lifecycle ownership and duplicate
semantics, so compare it directly rather than assuming its benefits.

The next architecture proof must run the same representative graph through the
structured, fresh-subworkflow, and hybrid candidates against pinned Agent
Framework 1.26.0. Compare typed item/index values, empty and multi-item input,
conditional routing, parallel joins, nested loops and nearest-loop `Break`,
Sequence and collaboration compatibility, cancellation races, failure
attribution, production adapters, file and SQLite stores, and atomic shared
work/evidence budgets. Preserve checkpoint recovery as out of scope until frame
capture and restoration are proven. No public iterator node or Studio authoring
should be implemented before this comparison resolves one coherent lifecycle,
cancellation, join, and budget owner.

A follow-up three-role readiness review after the production MCP concurrency and
timeout proofs returned **REVISE** again. The hybrid is the leading candidate,
but production selection remains gated on authored typed item/index and real
nearest-loop `Break` behavior, a production-owned shared budget and lifecycle,
the full persisted evidence matrix, and custom-broker failure/timeout behavior.
The proponent argued those could be closed as implementation gates; the
adjudicator rejected that sequencing because the remaining gaps define the
architecture being selected. Subsequent proof-only increments now cover
concurrent direct and delegated model reservations plus direct and delegated
production-MCP count/byte reservations across the three candidates and both
stores. They verify admission mechanics, not the missing production budget
owner/lifecycle or the complete evidence matrix. No body-allowlist is permitted.
Selection would begin integrated implementation, not establish parity;
checkpoint recovery remains excluded until frames are captured and restored.

The first hybrid increment extends `PerItemWorkflowExecutor` with immutable
parent/child activation frames while each item still runs the production VAW
workflow body, leaving static graph routing and joins inside Agent Framework.
`HybridFrameCoordinator_UsesFreshProductionWorkflowBodyAndOneNestedRunLedger`
verifies inherited outer parameters, nested scope identity, production Agent
and direct-MCP execution, fresh variable state, and one monotonic evidence
ledger persisted through both file and SQLite stores (2/2 focused cases). The
companion `HybridFrameCoordinator_ExecutesConfigurableParallelJoinUnderNestedActivation`
case also proves that the production Agent Framework body completes its
two-branch join beneath a nested frame, with both branch completions and join
arrivals attributed to the child scope in file and SQLite stores. Additional
hybrid cases verify explicit cancellation of a nested production Agent
activation, cancellation after both parallel Agent branches have entered, and
a synchronized parallel failure after the sibling Agent completes. They
preserve child-scope invocation identity, suppress the join, and prevent
admission of the following nested item in both stores. The proof-only
`HybridFrameCoordinator_BreakStopsNearestNestedLoopAndOuterLoopContinues` case
uses a private sentinel from the tool fixture as a stand-in for a future Break
node. It verifies the hybrid coordinator targets the current nearest frame,
stops the inner iteration, preserves the enclosing iteration, and persists
scoped production Agent/MCP evidence through both stores (2/2). This does not
prove an authored Break node or schema contract. At this point in the proof,
aggregate work limits, concurrency, delegation, broader join/cancellation
races, and full evidence bounds remain unproven. Later entries document the
subsequent serial work-counter and reservation comparisons; the hybrid
comparison still does not select the architecture.

`HybridFrameCoordinator_SharesEvidenceBudgetAcrossFreshNestedBodies` adds a
test-only shared evidence-reservation slice for two serial fresh production
bodies. The same recorder accounts for root and iteration lifecycle events,
scoped production Agent/MCP events, and terminal evidence. A baseline trace
calibrates event capacity so the first Agent request/response and tool path
complete, while the second model request is rejected before provider dispatch;
the reserved failure events still persist and close the root as failed. Both
file and SQLite variants pass (2/2), with persisted sequence/status and the
serialized event-byte count checked. This proves only serial, cross-activation
model evidence reservation in the hybrid test harness. It does not prove a
production-owned budget context, aggregate work/model-turn/tool limits,
delegated work, or all evidence shapes. The later candidate comparison covers
concurrent model and controlled-broker MCP reservations, but not production
budget ownership; architecture selection remains unresolved.

`IteratorCandidates_EnforceSharedEventAndByteBudgetsAcrossActivations` compares
event-count and byte-cap exhaustion across the structured,
fresh-subworkflow, and hybrid candidates. Each candidate is calibrated from
its own two-item baseline, then run through the same production Agent/MCP body
with file and SQLite stores. The first activation's model request/response and
MCP tool complete; the next activation starts, but its model request is
rejected before provider dispatch, the Agent fails, the second tool is not
called, and reserved failure evidence closes the run (2/2 per candidate). The
same case also calibrates a serialized-byte cap from each baseline's event
prefix and second model request, leaving a 1 KiB admission margin below the
bounded outcome and terminal reserves. All three candidates reject that
request before provider dispatch and preserve the same failed prefix in both
stores. These are test-only serial comparisons; they do not prove concurrent
production-broker reservations, aggregate work limits, or production budget
ownership and select no scheduler or public limit.

`IteratorCandidates_ReserveConcurrentModelOutcomesBeforeDispatch` compares the
three candidates on a synchronized parallel-Agent body. With two outcome
reservations, both model requests and responses persist and the join completes;
with one, the second request is rejected before provider dispatch, only one
provider call occurs, the join is suppressed, and failure evidence closes the
run. File and SQLite cases pass for each candidate (2/2 per store). This is a
test-only model-reservation comparison. The same candidate/store matrix now
calibrates a byte ceiling from the baseline prefix through the competing
request. It rejects the second request before provider dispatch, allows the
admitted response to settle, persists failure evidence, and suppresses the
join (6/6 candidate-store cases). Production MCP broker concurrency, aggregate
work limits, and production budget ownership remain open.

`IteratorCandidates_ReserveConcurrentMcpOutcomesBeforeDispatch` now compares
the same reservation boundary for a parallel direct-MCP body across structured,
fresh-subworkflow, and hybrid candidates. The deterministic broker synchronizes
both branches before authorization and holds successful calls until both
authorization events are recorded, making the reservation peak deterministic.
At capacity two, both calls dispatch, complete, and
reach the join; at capacity one, only one call passes authorization/dispatch,
the other is rejected before dispatch, its admitted sibling settles, the join is
suppressed, and the run fails with a contiguous persisted prefix. File and
SQLite cases pass for each candidate (2/2 per store). This is a candidate-harness
proof using a controlled `INodeToolBroker`; the production broker comparison
follows below. Aggregate work limits and production budget ownership remain
open. No scheduler or public cap is selected.

`IteratorCandidates_ReserveConcurrentProductionMcpOutcomesAndBytesBeforeDispatch`
repeats the three-candidate comparison with the production `McpToolBroker` and
two synchronized real stdio `demo.slow` nodes. With two outcome reservations,
both tool outcomes persist and the join completes. With one, one call is
authorized, the competing call fails before the broker's dispatch point, and
the admitted call records an uncertain outcome after sibling cancellation; no
join completes. File and SQLite cases pass (2/2), and the complete Agent
Framework suite passes (344/344). The broker connections and tool server are
real, but the evidence budget remains a test-only recorder; this selects no
production scheduler or budget owner. Concurrent model byte-cap exhaustion,
aggregate work bounds, and the full outcome-failure matrix remain open.

The same case calibrates a second byte ceiling from the persisted prefix through
the second authorization, then leaves 4,096 bytes of margin below the capacity
needed to reserve both bounded MCP outcomes. For all three candidates, one
production-broker authorization is persisted, the competing authorization is
rejected before dispatch, the admitted outcome settles as uncertain after
sibling cancellation, and the join is suppressed. Both stores pass for each
candidate (6/6 candidate-store cases). This proves concurrent production MCP
reservation under a measured byte cap, but selects no global cap or production
budget owner. The full Agent Framework suite passes 348/348 with both
concurrent count and byte reservation cases included.

`IteratorCandidates_ReserveConcurrentDelegatedModelOutcomesAndBytesBeforeDispatch`
uses the production `AgentNodeExecutor` team fan-out in each iterator candidate.
The baseline records the parent model request and both synchronized delegated
requests. A byte ceiling calibrated at the second delegated request rejects
that reservation before the second provider dispatch; the admitted delegate is
canceled when its sibling fails and its outstanding model outcome reservation
remains represented as unresolved evidence. The root fails with a contiguous
persisted prefix. Structured, fresh-subworkflow, and hybrid candidates pass
against both file and SQLite stores (6/6 candidate-store cases). The proof
closes only delegated-model concurrent byte admission in the test harness; it
does not establish direct sibling-model byte limits, aggregate work caps, or
production budget ownership.

`IteratorCandidates_ReserveDelegatedProductionMcpOutcomesAndBytesBeforeDispatch`
uses the production `AgentNodeExecutor` and `McpToolBroker` with two delegated
profiles calling synchronized real stdio `demo.slow` tools. Across structured,
fresh-subworkflow, and hybrid candidates, the two-reservation baseline completes
both calls and the team. A one-reservation limit and a prefix-calibrated byte
limit each reject the competing delegated authorization before dispatch; the
admitted tool completes, the rejected call records a host failure, and the
agent recovers so the team and workflow complete. The matrix covers all three
candidates and both file and SQLite stores (18 baseline/limited executions).
This closes concurrent delegated MCP admission for these success/rejection
paths only; it does not select production budget ownership or cover all
delegated failure, timeout, and cancellation combinations. The full Agent
Framework suite passes 352/352.

`IteratorCandidates_SettleProductionMcpTimeoutReservations` compares the
post-dispatch timeout path for the same parallel direct-MCP body through the
structured, fresh-subworkflow, and hybrid candidates. The production broker's
four-second deadline expires while the real demo tool is still running. Both
file and SQLite stores retain authorization and uncertain-outcome evidence,
the join does not complete, the root run fails, and both outcome reservations
settle (2/2 stores for each candidate). The test uses the real stdio fixture
and production `McpToolBroker`; it does not prove custom `INodeToolBroker`
implementations honor equivalent timeout or settlement behavior, nor select
production iterator scheduling or aggregate limits. The owning Agent Framework
suite passes 346/346 after adding this case and making the controlled concurrent
reservation comparison deterministic.

`IteratorCandidates_SettleCustomBrokerUnknownOutcomes` exercises the broker
extension boundary with a deterministic custom `INodeToolBroker`: two parallel
calls record authorization, hit a broker-owned deadline after dispatch, persist
`tool.outcome-unknown`, then fail. All three candidate harnesses settle both
reservations, suppress the join, and fail the root through file and SQLite
stores (6/6 candidate-store cases). This proves the runner/store path for a
custom broker that honors the event contract; it does not enforce that
arbitrary broker implementations emit truthful authorization and uncertainty
evidence, nor does it add a production budget owner. The Agent Framework suite
passes 348/348 with this case included.

`HybridFrameCoordinator_ExecutesUnifiedSequenceBodyPerActivation` exercises a
current unified-schema body containing a two-output Sequence whose paths each
invoke a direct MCP node. It executes the body for two nested activation
frames, verifies both MCP calls and Sequence output dispatch/completion evidence
per item, and persists the correctly scoped ledger through file and SQLite
stores (2/2). This closes one Sequence-composition case in the hybrid harness;
it does not compare all candidates or establish behavior in a selected
production iterator runtime.

`HybridFrameCoordinator_IsolatesCollaborationPerActivation` exercises two
sequential nested activations of unified-schema bodies containing either
single-phase or two-phase collaboration regions. Each activation publishes
and reads only its own branch messages; file and SQLite journals validate and
persist the scoped event history (4/4). Journal validation selects the evidence
contract from the region shape and validates repeated invocations independently
while preserving the root terminal outcome. This closes those compatibility
cases in the hybrid harness; it does not prove production iterator integration
or resolve the candidate comparison.

## Status

Architecture proof required; no iterator behavior is shipped.

## Context

The first-class collections workstream has shipped immutable typed arrays
through workflow inputs and outputs, parameters, and pure graph functions.
The roadmap identifies bounded sequential iteration as the next placement.
Decision 0066 requires ascending order, sequential activation, cancellation
between elements, per-element failure evidence, and a hard work limit.

The current string loop is not an array iterator. It has a predicate-driven
contract, a 16-iteration maximum, one body topology, and loop-specific
checkpoint state. Reusing it implicitly would conflate different serialized
and runtime semantics. A dedicated iterator with a typed array input and
activation-scoped item/index values is the leading direction.

## Review findings

Three independent review roles considered a dedicated iterator design. The
review supports the direction but recommends revising it before implementation.
The candidate's simple `64 items × 16 body nodes` bound limits graph-node
activations only. It does not, by itself, cap model turns, tool work, or the
resulting event volume. The current agent contract permits an unlimited-turn
setting, and run storage does not define a global event-count or aggregate
event-byte ceiling that an iterator can rely on.

The runtime also needs a precise per-activation context. `GraphMessage` does
not currently carry the iterator's array/index scope, and the explicit data
evaluator has run-scoped runtime outputs keyed by node and port. A valid
contract must ensure that item-derived values and cached results cannot leak
between activations. It must also define source evaluation/freeze timing,
initial versus body-return routing, what `done` forwards, event ordering,
failure attribution to the actual invocation and element index, cancellation
races, and checkpoint rejection.

The inspected node limits provide useful inputs but do not yet compose into an
iterator budget: the MCP broker caps each scope at 16 calls, 16,384 argument
characters, 16,384 output characters, and at most one minute per call; agent
invocations default to eight turns but can explicitly request unlimited turns.
Run journals and SQLite storage have no aggregate event-count or event-byte
ceiling. Therefore the iterator must meter nested agent/tool work and retained
evidence as a whole. Per-node limits alone do not prove an aggregate bound.

## Current runtime boundary

Production `WorkflowRunner.BuildWorkflow` creates one Agent Framework executor
per authored node and connects static edges and static parallel fan-in
barriers. `GraphMessage` carries scalar loop-iteration and parallel-activation
fields, but no nested activation-frame stack. The explicit data evaluator
stores runtime-produced values by `(nodeId, portId)` for the evaluator's whole
lifetime, so repeated node activations would currently collide. The variable
store is also created once per workflow run. These are concrete seams the
scheduler design must replace or scope; adding frame fields to `GraphMessage`
alone is insufficient.

`SequenceRuntimeCoordinator` and `SequenceActivationLedger` are helpers for a
fixed list of Sequence outputs. The production Sequence path builds a separate
bounded workflow for its admitted topology; neither is a general scheduler for
arbitrary nested graph activations. They may inform lifecycle evidence and
terminal accounting, but do not establish dynamic graph-body support.

A second three-role review rejected the proposed numeric aggregate values as
unproven. The 64 MiB byte cap could be consumed by valid model request/response
traces alone: each trace may be 262,144 UTF-16 characters, before JSON envelope,
UTF-8 expansion, MCP, and data-evaluation evidence. The MCP and agent event
paths also have different dispatch and outcome sequences, and delegated work
can dispatch concurrently. A shared ambient counter without atomic
reservations at the actual dispatch boundary would not establish the limit.

## Proposed iterator contract

The parity-level review agrees that static Agent Framework executor wiring
alone does not establish nested activation frames, per-iteration joins, or
nearest-loop break semantics. It did not establish that a general replacement
scheduler is the only solution. Two architectures remain under comparison:

1. A VAW structured activation runtime owns graph activations and control flow,
   and reuses node behavior through adapters; Agent Framework remains behind
   the agent adapter.
2. Each loop item runs in a fresh Agent Framework subworkflow instance. Agent
   Framework retains static routing and joins inside that body, while VAW owns
   item sequencing and loop control.

Official Agent Framework documentation describes workflows bound as nested
executors, isolated execution contexts, and arbitrary nesting
([sub-workflows](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/advanced/sub-workflows),
[Workflow API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.workflows.workflow?view=agent-framework-dotnet-latest)).
The repository pins package 1.26.0, and its installed package XML contains the
`Workflow.BindAsExecutor` API. Those facts establish the candidate API exists;
they do not prove fresh per-item instance isolation, dynamic nearest-loop
`Break`, VAW event attribution, or shared budget/evidence behavior.

An independent adjudicator therefore marked the architecture **REVISE** and
requires a non-public side-by-side executable proof against the pinned package
before choosing. The proof must compare both candidates with the same
representative graph and must not reduce the parity target to a linear body.
The test should include typed item/index access, empty and ordinary iteration,
conditional routing, a joined parallel body, nested iteration, nearest-loop
Break, cancellation, per-item failures, actual Agent and MCP adapter work,
runtime-value/cache and variable isolation, node/invocation event identity,
and the dispatch/evidence budget seam. If subworkflows cannot meet those
requirements without reconstructing a general scheduler around them, that is
evidence for candidate 1. If they can, compare lifecycle/state ownership and
failure behavior before deciding.

### First executable evidence

`ArrayIterationSubworkflowProofTests.FreshChildWorkflowPerItem_IsolatesConditionalAndParallelJoinState`
builds a new workflow body for each sequential array item, binds that body as an
Agent Framework subworkflow, and executes a body with an exclusive route and a
two-branch barrier join against package 1.26.0. It also creates nested child
workflows and checks that a nested break result stops the inner iteration while
the parent iteration proceeds. The test confirms distinct outer child Workflow
instances, sequential results, and iteration-correlated branch/join arrivals.
The focused test passes. The current full Agent Framework suite passes
247/247, including the second proof below.

This is positive evidence for candidate 2's dynamic construction and static
conditional/parallel body execution. The conditional routing, branches, join,
nested loop, and break controller are synthetic Agent Framework test
executors. The nested break is returned by a test executor and consumed by a
test loop controller; it does not prove an authored For Each/Break node
contract or arbitrary graph control-flow semantics. It does not compare
candidate 1 or prove VAW production-node integration.

`ArrayIterationVawBodyProofTests.FreshVawRunnerPerItem_ExecutesAgentAndMcpAndPersistsCorrelatedEvents`
adds a second candidate-2 proof: a test-only per-item coordinator invokes a
fresh production `WorkflowRunner` for a real unified graph containing Input,
Variable Set/Get, Uppercase, Agent, direct MCP, and Output nodes. Each item
writes and reads its own distinct value, evaluates the pure uppercase node,
then passes that result through the real Agent and tool adapters. Controlled
agent and tool fakes execute through those adapters. Their `ExecutionEvent`
callbacks are captured under one synthetic global sequence and per-item scope.
The proof also binds each zero-based array index as an integer workflow
parameter, reads it through a typed Workflow Value node, and verifies that the
item/index pair reaches the Agent and MCP tool. This demonstrates composition
using the existing parameter contract; it does not prove a dedicated iterator
index pin or frame-scoped parameter implementation.
The test verifies results, tool/model inputs, and that each Agent and MCP
start/completion pair retains one invocation identity inside its item's scope.
A failure variant
confirms that a failed Agent retains its matching invocation identity in that
item's scope and stops dispatching later array items. The event bridge now
persists one root run history through both `FileRunJournal` and
`SqliteWorkflowRunStore`, for success and failure, and checks the stored
sequence, terminal status, distinct pure-transform results, per-item variable
values and scopes, and invocation IDs. This checks for cross-item stale values
when each item uses a fresh VAW runner; it does not prove cache isolation for
multiple activations inside one retained runner. A synchronized cancellation
case stops an in-flight Agent, persists the matching `node.cancelled`
invocation and root cancellation, and proves that later items do not start in
either store. An empty-array case completes
without activating body nodes. A separate parallel-body case executes a real
two-branch configurable fork/join with production Agent nodes once per item;
the joined outputs and join evidence are persisted through both stores. Direct
MCP remains covered by the sequential body proof because the current graph
validator rejects MCP nodes inside configurable parallel branch regions. The
focused proofs and full Agent Framework suite pass 247/247. This does not
exercise reopening the store after process loss or crash-prefix reconciliation.

The test sequencer and its callback-to-store bridge are proof fixtures, not a
production iterator implementation. The outer item loop is a test executor,
and each iteration starts a fresh production VAW runner for the same body
graph; no authored iterator or item/index graph pins are exercised.
The candidate-2 proofs still do not cover cancellation racing with dispatch or
event publication, store reopening after process loss, cache isolation for
multiple activations within one retained runner, typed element/index pins, or
atomic shared budgets, and candidate 1 remains unimplemented. Architecture
remains undecided.

`ArrayIterationStructuredActivationProofTests` adds an initial candidate-1
contract prototype for immutable nested activation frames, item/index identity,
activation-scoped data outputs and variables, nearest-loop lookup, concurrent
fork arrivals keyed by activation, and cancellation before the next item. Its
two focused tests pass, and the full Agent Framework suite passes 254/254.
This fixture is deliberately not connected to
`WorkflowRunner`, `StringExecutor`, Agent/MCP adapters, the event recorder, or a
run store. It validates state-key and lifecycle mechanics only; it is not
evidence that the structured scheduler architecture can execute a representative
workflow or meet the shared budget contract. Candidate 1 and the full
side-by-side proof remain open.

The candidate-1 production seam is now more specific: `WorkflowRunner.BuildWorkflow`
creates one `StringExecutor` per authored node and wires static Agent Framework
edges. The executor returns the selected source node/port for ordinary routing;
its only direct `IWorkflowContext` dispatch is the current parallel-fork send.
`StringExecutor` now has an internal invocation path that accepts a
caller-owned fork-dispatch callback. The existing Agent Framework handler uses
that same path and forwards dispatch to `IWorkflowContext.SendMessageAsync`;
the Agent Framework suite passes 254/254 at that increment. The
proof-only `ExecuteStructuredSchedulerProofAsync` now calls the same production
node handlers directly and owns edge dispatch. Against the same Agent → direct
MCP body used by the subworkflow proof, it matches output, inputs, event kinds,
and node completion. It also executes production Agent nodes through a static
fork/join and demonstrates that a failed MCP branch cancels a blocked sibling
and suppresses join publication. The three focused scheduler tests pass, and
the full Agent Framework suite passes 257/257.

This scheduler proof initially exercises one workflow body activation. It does
not provide array item/index pins, nested activation dispatch, nearest-loop
`Break`, persistence-backed event accounting, or shared atomic work/evidence
budgets. The proof explicitly rejects Sequence and collaboration graphs. It is
not evidence that the full parity-level scheduler has been selected or
implemented.

The production-backed activation coordinator now accepts a parent activation
frame for a nested proof invocation. `StructuredActivationSequenceProof_NestedFramesInheritParametersAndScopeProductionNodes`
executes the same production Agent/MCP body beneath an outer frame, verifies
that the child inherits the outer index parameter, records distinct nested
scope paths, and resolves the child's frame as the nearest loop. Its focused
test passes, and the full Agent Framework suite passes 318/318. This proves
parameter inheritance and frame identity for one nested activation level only;
there is still no authored typed array item/index pin or `Break` dispatch, and
it does not select or ship an iterator architecture.

`ExecuteStructuredActivationSequenceProofAsync` now adds a proof-only VAW-owned
sequential activation coordinator around the structured dispatcher. It runs the
same production body once for each string in a bounded JSON array, binds the
zero-based index through the existing typed workflow parameter, and writes
scoped node evidence into one monotonically sequenced run event stream. The
proof now retains one production graph-data evaluator while indexing runtime
outputs, per-function array-value evidence budgets, item input, and parameters
by immutable activation frame. The graph-function node-visit ceiling remains
shared by the retained evaluator. It maps each frame to its own production
variable store. A proof session now retains one production `StringExecutor` per
node across sequential item activations. Its event callback and parameters
follow the current frame. Its focused
test verifies item/index isolation across three activations, production Agent
and direct-MCP calls, invocation-correlated events, and one root lifecycle.
Its failure variant preserves the failed Agent invocation under the active item
scope and proves a later item is not dispatched. A persistence variant sends
the same structured-candidate stream through the real file and SQLite run
stores; both snapshots retain the single monotonic sequence, completed status,
activation scopes, and correlated Agent/MCP node evidence.
The four focused activation-sequence cases pass, and the full Agent Framework
suite passes 261/261.
The matching fresh-subworkflow candidate already exercises the same body
definition and inputs with file and SQLite persistence. This is a useful
sequential repeat-activation comparison. A paired test runs both candidates
against that same three-item graph and parameter-index matrix, persists each
stream through both stores, and compares output, model/tool inputs, ordered
event shape, scopes, and invocation boundaries. It caught and fixed missing
connection IDs in the structured event bridge. This remains proof-only: the
outer coordinator accepts JSON strings rather than a typed array pin, has no
nested frame dispatch or authored `Break`, and does not select a production
architecture. Candidate comparisons now include failure, cancellation, and
joined outcomes. Store reopening after process loss, crash recovery, and shared
atomic budgets remain unproven. The architecture decision and public iterator
remain open.

`StructuredActivationSequenceProof_SharesEvaluatorButScopesRuntimeValuesAndVariables`
reuses the evaluator across three body invocations containing real Variable Set
and Get nodes plus Agent and MCP nodes. It verifies the correct per-item value
is evaluated after each activation, every activation observes the declared
variable default before its write, and the item/index still reaches the model
and tool. The first run exposed an unscoped fallback in evaluator input and
runtime-output access; the test failed because later activations reused the
first item's model/tool value. Explicit activation scope now flows through the
production handler and evaluator. This proves retained evaluator and variable
isolation in the sequential proof; it does not exercise nested dispatch or
typed item pins, or change production iteration scheduling.

`ArrayIterationVawBodyProofTests.StructuredAndFreshSubworkflowCandidates_MatchParallelJoinPerActivation`
now runs repeated activations through one retained structured-proof executor
set. Production parallel-join state is partitioned by activation-frame ID and
fork invocation ID, so each item gets an independent arrival set and stable
join invocation. Both stores preserve matching Agent outputs and join evidence
against the fresh-subworkflow comparison, and the test asserts only one
executor set was created. The file/SQLite regression cases pass (2/2), and the
full Agent Framework suite passes (317/317). This proves sequential repeated
join isolation, not concurrent nested frame dispatch or a production iterator.

`ArrayIterationVawBodyProofTests.StructuredAndFreshSubworkflowCandidates_StopAtSameFailedActivation`
compares both candidates against the same Agent/direct-MCP body and fails the
Agent at item index 1. For both file and SQLite stores, each candidate persists
the started activation prefix for indices 0 and 1, the same Agent start/failure
node-event shape scoped to index 1, no events for index 2, and a failed root
terminal. Neither candidate dispatches the tool for the failing item. Both
store variants pass. This narrows the comparison gap for per-item failure
attribution but does not compare cancellation races or joined-body outcomes.

`ArrayIterationVawBodyProofTests.StructuredAndFreshSubworkflowCandidates_CancelSameActiveActivation`
synchronizes cancellation while the Agent is blocked at item index 1. Across
both file and SQLite stores, both candidates persist the started activation
prefix for indices 0 and 1, the same Agent start/cancel node-event shape scoped
to index 1, no events for index 2, and a cancelled root terminal. Neither
candidate dispatches the MCP tool for the cancelled item. Both store variants
pass. This covers cooperative cancellation of an active body node; it does not
cover cancellation racing with external dispatch, event publication, or
persistence, and joined-body outcomes are still not compared side by side.

`ArrayIterationVawBodyProofTests.StructuredAndFreshSubworkflowCandidates_MatchParallelJoinPerActivation`
compares the same two-item, two-Agent fork/join body through both candidates.
For each item, the candidates produce identical joined output, activate both
Agent branches, complete the barrier, and persist equivalent node/connection
and join-progress event shapes under that item's scope. Both file and SQLite
variants pass. This covers successful joined bodies only; join cancellation
and shared dispatch/evidence exhaustion are not yet compared.

`ArrayIterationVawBodyProofTests.StructuredAndFreshSubworkflowCandidates_FailSameParallelActivationWithoutJoin`
compares a two-item fork/join where the left Agent fails. Both candidates fail
item 0, suppress the join, and do not start item 1; the failure branch's
node-event shape matches in both stores. Because this comparison does not
synchronize target delivery against the left failure, it makes no assertion
about the right branch's activation or cancellation prefix; those events vary
with the scheduler race. Both file and SQLite variants prove the shared final
failure invariants. Join-cancellation and budget-exhaustion comparisons remain
open.

`ArrayIterationVawBodyProofTests.StructuredAndFreshSubworkflowCandidates_CancelSiblingAfterHandlerEntry`
adds a gated race in which the right Agent handler signals entry and blocks;
the left Agent fails only after that signal. In both file and SQLite stores,
both candidates record the left Agent failure, cancellation of the entered
right Agent, no join completion, and no next item. Their branch lifecycle
events match. The proof narrows the mismatch to failure before the sibling
handler enters; it does not establish whether an admitted but not-yet-entered
branch should receive a start/cancel event. This is evidence about one
controlled lifecycle boundary, not a scheduler selection. The architecture
remains undecided.

`ArrayIterationVawBodyProofTests.StructuredAndFreshSubworkflowCandidates_CancelSiblingBeforeNodeAdmission`
uses the production execution gate on the right Agent. The left Agent fails
only after the right target reaches that gate; the gate contract runs before
node admission. Both candidates stop before the right Agent adapter,
`node.started`, or `node.cancelled`, suppress the join, and stop before the next
item in file and SQLite stores. This matches cancellation after target delivery
but before node admission. It does not erase the earlier mismatch when
structured dispatch fails before sending the sibling target, and it does not
define evidence for queued-but-not-admitted work. The architecture remains
undecided. Both controlled sibling-failure comparisons pass for file and
SQLite stores; the full Agent Framework suite passed 309/309 at that point.

The post-admission window is now controlled by a proof-only callback immediately
after the persisted `node.started` event. The right Agent blocks there; the left
Agent fails; both candidates persist the right `node.cancelled` event after its
start and before join completion or the next item. The right Agent adapter is
not entered. File and SQLite variants pass.

`ArrayIterationVawBodyProofTests.StructuredAndFreshSubworkflowCandidates_CompareJoinArrivalAfterSuccessfulBranch`
reverses the fork target connection ordering and synchronizes left failure
until the right Agent has completed. Both candidates record right completion
before left failure and suppress the join result and next item. The structured
candidate invokes the join for the first branch and records partial join
progress; the Agent Framework fan-in barrier retains that branch result and
does not invoke the join while the other source has not emitted. This is a
measured intermediate event-timing difference; final workflow outcomes agree.

`StructuredAndFreshSubworkflowCandidates_CancelAfterOneJoinBranchCompletes`
holds each candidate at the persisted right-Agent completion callback, then
cancels before that callback returns control to graph routing. The structured
candidate has neither delivered the left target nor entered its handler; the
Agent Framework candidate has delivered that target and entered its handler.
The test snapshots both the persisted event prefix and an explicit
post-`node.started` handler-entry signal while the completion callback is still
paused, then cancels. In both file and SQLite stores, the right branch
completion and root cancellation persist, the join does not complete, and the
next item does not start (2/2). This is a measured candidate scheduling
difference at a harness-controlled pause point. It does not establish a
general production cancellation contract, queued-work semantics, atomic
publication, or select the architecture. Broader dispatch, admission, and
cancellation races remain open.

A fresh three-role review adjudicated **REVISE**: keep the architecture and
lifecycle contract open. Queue-then-run is a reasonable proof candidate
because immediate recursive dispatch makes later delivery depend on an earlier
branch's failure, but neither all-target staging nor its failure semantics has
been proven. Before selecting a scheduler contract, prove post-admission
pre-handler cancellation (covered above), reversed target ordering (covered
above), and failure during staging.

The proof-only structured staged-fork candidate now proves both outcomes.
On success, both targets are staged before the first branch activation, and
both branch results reach the join in file and SQLite stores. On injected
failure after the first of two targets is staged, neither branch starts, no
join completes, and no later activation is dispatched; both stores retain the
evidence. In both cases, the fork's `node.completed` is recorded before its
dispatch callbacks; injected failure is followed by `node.failed` for the same
invocation. A separate three-role review rejected
changing production `node.completed` ordering: it is durable node-local
evidence, and buffering in the proof does not demonstrate atomic publication
through Agent Framework. Any future scheduler staging result needs a separately
defined event/contract and production-level proof of preparation, cancellation,
and partial publication. This test proves only the structured candidate's
withheld dispatch behavior; it does not establish Agent Framework staging
atomicity or select the iterator architecture.

The broader nested-frame, typed-pin, activation-scope, budget, and recovery
requirements remain open. Post-start, reversed-order, and staged-candidate
comparisons pass in file and SQLite variants; the full Agent Framework suite
passes 317/317.

### Initial persistence sizing evidence

`RunStoreTests.MaximumShapedModelMcpAndGraphEvents_MeasurePersistedUtf8Envelope`
serializes maximum-shaped model response, MCP completion, and graph data
evaluation events through the real file snapshot and SQLite event-row paths.
The fixture matches the 262,144-character model trace cap using escape-heavy
text, a 16,384-character MCP output, and a 16,384-character graph string value.
With a fixed timestamp, both stores persist the same event envelope sizes:

| Event | Inner `Data` UTF-8 bytes | Persisted event JSON bytes |
| --- | ---: | ---: |
| `model.responded` | 262,144 | 306,284 |
| `tool.completed` | 16,501 | 16,848 |
| `data.evaluated` | 16,456 | 16,830 |

The test checks each persisted raw event representation against
`JsonSerializer.SerializeToUtf8Bytes` with the store's web defaults. File
snapshot embedding and SQLite's `event_json` column produced identical sizes.
These are representative per-event measurements, not aggregate caps or a
complete worst-case proof. Concurrent delegated traces, function-call content,
the full set of failure/terminal evidence, event-count exhaustion, and atomic
reservations remain unmeasured; no runtime limits are selected from these
numbers.

`ArrayIterationVawBodyProofTests.ConcurrentMaximumMcpOutcomes_PersistBeforeAggregateJoinFailure`
adds a synchronized concurrent case through the production parallel graph
runner and a controlled MCP broker. Both branches return 16,384-character
outputs successfully; their combined join exceeds the existing 16,384-character
aggregate result limit, so the join and run fail after both external calls
complete. File and SQLite evidence retain both `tool.completed` records and the
terminal failure: 31 events, 33,742 bytes for the two tool outcome events, and
76,611–76,612 serialized event bytes total. The one-byte total difference is
timestamp precision in independently executed runs. Agent Framework tests
pass 265/265. This demonstrates concurrent outcome retention and a failed
aggregate path, but does not measure concurrent model/delegate traces,
reservation races, terminal-capacity exhaustion, or aggregate-limit behavior;
no cap is inferred from this scenario.

`ArrayIterationVawBodyProofTests.ConcurrentAgentRequests_PersistMaximumPromptEvidenceThroughBothStores`
drives two real `AgentNodeExecutor` invocations concurrently through a
deterministic `IChatClient`. Each prompt carries 8,192 instruction characters
and 16,384 user-input characters. The model calls synchronize before returning,
proving both requests were in flight together. File and SQLite histories each
retain 40 events and approximately 263.6 KB of serialized event JSON, including
approximately 50.5 KB for the two `model.requested` events. The one-to-two-byte
store/run variation comes from event timestamps. The focused cases and full
Agent Framework suite passes (269/269). This measures maximum prompt evidence
for two concurrent model calls, not live inference, delegated fan-out, function
call arguments, response-size maxima, failure paths, or a reservation limit.

`ArrayIterationVawBodyProofTests.ConcurrentAgentRequestFailures_PersistMaximumPromptAndFailureEvidence`
uses the same synchronized maximum-shaped prompts but fails both deterministic
model calls after request capture and before a response. Both stores retain the
two request events, Agent/run failure evidence, and no synthetic
`model.responded` event: 28 events and approximately 257.9 KB of serialized
event JSON. The complete Agent Framework suite passes (269/269) with both
concurrent success and failure cases included. This confirms the observed
failure path preserves the attempted request and terminal evidence. It does
not reserve terminal capacity or prove behavior when a recorder or store
rejects an append.

`ArrayIterationVawBodyProofTests.ParallelDelegateFanout_PersistsMaximumFunctionCallAndChildEvidence`
uses the production Agent adapter with one parent and two concurrent assigned
profiles. The parent prompt is 16,384 characters; its `team_0` function call
contains a 16,384-character task; each delegate has 8,192 instruction
characters and returns 16,384 characters. The parent and both child invocations
produce four model requests and four responses, with overlapping delegate
calls. Both stores retain 33 events: model-request envelopes total 154,529–
154,530 bytes, response envelopes total 52,717 bytes, and all event envelopes
total 404,517–404,518 bytes. This measures successful delegated fan-out and
maximum function-call arguments/results; it does not establish aggregate
budget limits.

`ArrayIterationVawBodyProofTests.ParallelDelegateFailure_PersistsFailureAndTerminalEvidence`
uses a synchronized two-profile team where one child fails and the sibling is
cancelled, then the parent model fails while trying to recover. Both stores
retain the team, delegated-agent, parent-agent, and run failure sequence with
no completed team aggregate: 26 events, 4,823–4,825 bytes of team/agent/
terminal failure evidence, and 23,185–23,191 bytes for the complete event
history. This covers a concurrent delegated failure and cancellation path; it
does not exercise a persistence append failure or guarantee terminal capacity.
The full Agent Framework suite passed (273/273) with the original cases.

Both delegated tests now also use `ReservedEvidenceProofRecorder`. On the
successful maximum-shaped fan-out, the two child model calls hold two outcome
reservations concurrently, all four parent/child model requests receive
accepted responses, and all reservations settle in both stores. On the
delegated failure path, one child fails, its sibling is cancelled, and the
parent follow-up fails; the remaining pending reservations equal the number of
model requests without a recorded response, so cancellation/failure does not
falsely settle an external outcome. The four base success/failure store cases
pass, and the full Agent Framework suite passed (289/289) before the later
delegated append-failure and admission-race cases. This remains a test-only
recorder and does not change production budget behavior.

The failure test also injects one `model.responded` append failure for a child
invocation at the actual file/SQLite store boundary. The rejected response is
absent from history, and its pending reservation remains counted with the
other requested calls that have no accepted response. This confirms an
unpersisted delegated response is not treated as settled. A one-slot variant
also races both children for the sole outcome reservation: one child is
admitted, the competing call is rejected before provider dispatch, and the
admitted call remains unsettled after cancellation. The complete delegated
reservation matrix has eight file/SQLite cases. A separate parent-side
`model.responded` append failure rejects the event containing the delegated
team function call; no child dispatch occurs, failure evidence persists, and
the parent outcome reservation remains unsettled (2/2 stores). These remain
test-only proofs. A synchronized root cancellation after both delegated model
calls enter the provider retains both child identities and team cancellation;
the two unanswered model requests remain reserved and the run is cancelled in
both stores (2/2 cases). A universal maximum failure envelope and timeout-
specific settlement remain uncovered. The maximum safe provider error shape
was then measured in the delegated failure path using `HTTP int.MaxValue`; its
`agent.failed` envelope is 926 UTF-8 bytes and the complete delegated failure
evidence is 4,789 bytes in both stores, within the 64,000-byte test reserve.
This does not bound MCP/host exception messages that are copied into failure
evidence, so those envelopes still need an explicit bound before a universal
maximum can be claimed. Timeout-specific settlement also remains uncovered.
These tests select no production aggregate caps. The full Agent Framework
suite passes (297/297).

`ArrayIterationVawBodyProofTests.ModelRequestEvidenceAppendFailure_PreventsProviderDispatch`
injects a run-event sink failure while persisting `model.requested`. Through
the production Agent adapter, the provider is never called, no request or
response event is persisted, and the failure terminal is retained in both file
and SQLite histories. This establishes the pre-dispatch side of the boundary
for a rejected model-request event. It simulates the recorder callback failing
before reaching the store; it does not simulate `IWorkflowRunStore.Append`
failure, a failure after an external tool completes, or reserved terminal
capacity. The Agent Framework suite passes (275/275) with this boundary case.

`StoreAppendFailureTests.ToolCompletionAppendFailure_RecordsUnknownOutcomeAndKeepsContiguousTerminalHistory`
rejects `tool.completed` at the actual file or SQLite store append boundary
after the local MCP tool has run. The broker records `tool.uncertain`, and the
sequence and root terminal failures remain persisted with contiguous event
numbers in both stores (2/2 cases pass). `WorkflowRunner.Emit` now commits its
sequence cursor and activation-ledger observation only after the event sink
accepts the event. This proves post-dispatch append recovery for one tool
completion event; it does not establish terminal capacity under a full event
budget, event-count exhaustion, or behavior when failure/terminal events
themselves cannot be appended.

### Reviewed failure-envelope direction

A three-role review recommended a typed, bounded failure envelope with
actionable category-specific safe messages and bounded structured metadata
where its meaning is stable. The MCP broker now emits stable failure codes and
fixed safe reasons for invocation failures, omits arbitrary host exception
messages and MCP server-declared error text from durable failure events, and
returns fixed corrective feedback for explicitly recoverable failures. This
avoids treating truncation as redaction: truncation limits size but does not
remove secrets or user data. Authorization, contract, recoverable, fatal,
cancelled, and outcome-unknown dispositions remain distinct; a dispatched
timeout or cancellation is recorded as uncertain and is never automatically
retried. Regression tests cover host-classified and unclassified post-dispatch
exceptions and inspect both persisted events and model feedback. The bounded
envelope is implemented at the MCP broker boundary; it does not bound arbitrary
custom broker implementations. The repository has no host diagnostic
lookup/retention contract, so a correlation ID does not imply retrieval of
richer details. Timeout-specific settlement and universal aggregate evidence
limits remain unproven; these tests select no production aggregate caps.

The adjudicated next proof distinguishes capacity from storage guarantees:
admission requires a request, one bounded outcome, and failure evidence to be
reserved before external dispatch; accounting charges the exact serialized
UTF-8 event envelope only after the store accepts it; a held terminal reserve
does not promise success when the store rejects the terminal append. The
proof-only `ReservedEvidenceProofRecorder` exercises these rules through the
production `WorkflowRunner` and `AgentNodeExecutor` with file and SQLite
stores. With an event ceiling derived from the baseline trace, both stores
reject `model.requested` before provider dispatch while retaining
`agent.failed`, `node.failed`, and `run.failed`: 10 events and 4,239–4,240
serialized bytes. A separate synchronized parallel-agent case admits one of
two competing calls, never more than one pending outcome reservation, and
persists 27 events in each store. Successful response append settles the
reservation and releases unused allowance (16 events, 7,256–7,257 bytes).
When the actual store wrapper rejects `model.responded` after provider dispatch,
the reservation remains held while failure evidence persists (11 events,
4,991–4,992 bytes). When the store rejects `run.failed`, the reserve cannot
make storage succeed; the persisted prefix remains nonterminal at 9 events and
4,011–4,012 bytes. The focused proof cases pass (10/10), and the complete Agent
Framework suite passed at 285/285 before this byte-only case; after adding it,
the complete suite passes (287/287).

`ArrayIterationVawBodyProofTests.ModelRequestByteCapacityExhaustion_RejectsDispatchAndPersistsReservedFailure`
sets the event ceiling well above the baseline trace and independently makes
the byte ceiling 256 bytes too small for the model request, bounded response,
and failure reserve. Both file and SQLite reject the request before provider
dispatch and preserve the three agent/node/run failure events (2/2 focused
cases pass). This is a standalone byte-admission proof for one measured model
response shape; it does not establish worst-case response/error envelope sizes.

`StoreAppendFailureTests.ToolAuthorizationEvidenceBudgetExhaustion_PreventsDispatchAndPersistsFailure`
extends the proof to the production `McpToolBroker` and its real stdio demo
server. It rejects recording `tool.authorized` when the combined event-count
and byte threshold cannot cover both a bounded tool outcome and failure
evidence. The broker records `tool.failed` without dispatching, and
both file and SQLite retain contiguous run-failure history (2/2 focused cases
pass).
The companion `ToolCompletionAppendFailure_RecordsUnknownOutcomeAndKeepsContiguousTerminalHistory`
now uses the same reservation recorder: when the store rejects `tool.completed`
after the tool ran, the reservation is still held when `tool.uncertain` is
appended, then settles only after that evidence is accepted. Both stores pass
(2/2 cases).

`StoreAppendFailureTests.ConcurrentToolAuthorizationReservationLimit_PreventsSecondDispatch`
exercises the production `McpToolBroker` with two parallel direct-MCP nodes
and synchronized real stdio `demo.slow` connections. Both reservations are
pending concurrently in the baseline and both tools complete before the join.
With a one-outcome limit, one `tool.authorized` is recorded, the competing
authorization recorder rejects before the broker's dispatch point, and the
admitted slow call is canceled by sibling failure and recorded as uncertain;
the join does not complete. File and SQLite tests pass (2/2), with the complete
MCP suite at 104 passed and 2 opt-in skips. The event recorder and reservation
limit remain test-only; this does not add a production run budget or compare the
three iterator candidates through the production broker.

This is a test-only accounting harness, not a production run budget. It proves
reservation mechanics for bounded model calls, delegated model fan-out, and MCP
tool calls, using test-specific outcome byte allowances. Together with the
concurrent broker cases above, it does not prove worst-case bounds for all event
shapes, all delegated MCP outcome paths, unlimited turns, timeout/cancellation
settlement for every outcome, or a production terminal reserve. No aggregate
cap is selected.

`IteratorCandidates_EnforceAggregateWorkLimitsAcrossActivations` adds a
test-only serial comparison of three shared work counters across the structured,
fresh-subworkflow, and hybrid candidates. With a limit of one, it rejects the
second `LoopIterationStarted` before body execution, the second `ModelRequested`
before provider dispatch, and the second `ToolAuthorized` before the fixture
broker dispatches; each candidate fails the root run and persists its accepted
prefix through both file and SQLite stores. The tool fixture records
authorization/completion around its deterministic dispatch; this is not another
production-broker proof. The matrix covers 18 limited candidate/store/counter
cases plus six baselines. Focused tests pass 2/2, and the full Agent Framework
suite passes 354/354. Every baseline and limited history is then read through a
new `FileRunJournal` or `SqliteWorkflowRunStore` instance; exact event sequence
and terminal status survive reopening in all 24 candidate/store executions.
This establishes persisted-prefix readability, not process-loss recovery or
execution resumption. It closes only serial work-counter exhaustion and store
reopen readability in the candidate recorder. It does not establish concurrent
atomic quotas, a production-owned budget context, numeric product limits, or
broader external outcome boundaries; no architecture or cap is selected.

`IteratorCandidates_ModelOutcomeAppendFailureStopsLaterActivations` injects a
store rejection for `model.responded` while running the same Agent/MCP body
through all three candidates and both stores. The provider is invoked once,
the request evidence remains but the response append fails, correlated
Agent/node/root failure evidence closes the run, the MCP node and following
item are not started, and the outcome reservation remains pending. Each
candidate/store history is reopened through a fresh store instance and retains
the same contiguous failed prefix (6/6 cases). The focused test passes 2/2, and
the full Agent Framework suite passes 356/356. This covers a production model
adapter with injected outcome-append rejection; tool-outcome and root-terminal
append failures remain to be compared across candidates. It does not authorize
retrying the provider request or establish recovery of the failed activation.

The public iterator contract must be Blueprint-style and composable with all
existing graph capabilities. The proposed pins are an execution input, typed
`array<T>` input, `Loop Body` execution output, typed `Array Element` data
output, integer `Array Index` data output, `Completed` execution output, and a
`Break` execution input. Iteration is ascending and zero-based, runs once per
item, and sends an empty array directly to `Completed`. `Break` targets the
nearest enclosing iterator, settles or cancels its active body descendants,
and then routes to that iterator's `Completed` output without dispatching the
next element. Nested iterators, conditional routes, parallel regions, agents,
MCP calls, workflow calls, and the other existing graph capabilities must be
valid body composition; a closed allowlist or straight-line-only first slice
would not meet the parity target.

The scheduler design must specify activation-frame identity and lifetime,
iteration-scoped data-cache keys, variable and evidence scope, body completion
and nested join behavior, cancellation and failure attribution, event ordering,
and atomic work/evidence reservations. A frame must carry the frozen input
array and current index/item, with derived data evaluations scoped to that
activation. Failure evidence identifies the exact invocation and element index
without copying raw array items into lifecycle events. Checkpoint recovery is
not admitted until frame capture and restoration are proven. The existing
predicate-based string Loop keeps its own contract.

For authored array literals, Studio will use one JSON text field (for example
`["a", "b"]`) rather than one control per element. The declared element type
drives validation and the typed output pin; the same typed pin can be connected
to an iterator or other compatible array input. JSON text is an authoring
representation, not a loss of graph type information.

## Compatibility constraints

- Keep iteration as a distinct node family; do not broaden or reinterpret the
  existing loop contract. The chosen runtime must preserve the agent adapter
  boundary and support all existing body capabilities.
- Retain ascending, zero-based, sequential iteration as the intended behavior.
- Keep the unified current schema. Additive node definitions do not require a
  feature-specific schema gate or graph-promotion step; historical graph
  interpretation remains unchanged.
- Keep `Array Get` as the supported way to read a known index while iteration
  is unresolved.
- Do not admit array iteration to checkpoint recovery until a frame proves
  capture and restoration of its frozen array, active index/context, native
  cursor, and evidence prefix.

## Work required before implementation

1. Extend the non-public architecture proof against Agent Framework 1.26.0:
   compare the structured VAW activation candidate and fresh-subworkflow
   candidate across the complete representative matrix described above. The
   first matched three-item Agent/direct-MCP slice now persists and compares
   equivalent evidence; static joins, nested frames, typed pins, cancellation,
   failures, and shared accounting remain incomplete. Do not add a public
   node, schema gate, or Studio authoring before the complete proof selects the
   architecture.
2. For the selected architecture, define the activation lifecycle, graph
   reachability, body completion, nested frames, dynamic branch/join
   activation, and adapter boundary. Do not constrain the body to an allowlist
   that excludes existing graph capabilities.
3. Define immutable source evaluation and activation identity, including how
   typed item/index values are resolved and how evaluator caches, variables,
   and evidence are scoped to one activation.
4. Define and implement an explicit run-level budget context that reaches
   nested agents, delegates/teams, MCP broker scopes, and the single run event
   recorder. The product direction remains full capability composition;
   excluding agents or MCP nodes merely to simplify the first implementation
   is not the target. Meter model calls, tool dispatches, body activations,
   event count, and serialized event bytes atomically. Compute byte usage with
   the exact event serialization and UTF-8 encoding used at persistence.
5. Reserve bounded outcome evidence and terminal failure capacity before
   dispatching each external operation. Specify every MCP and model success,
   denial, error, timeout, cancellation, serialization, and recorder-failure
   path. Fail before dispatch when reservations cannot be acquired. Process
   loss remains governed by existing reconciliation; do not promise that an
   in-memory reservation settles outcomes across crashes.
6. Measure maximum-shaped event payloads through the real serializers, include
   envelope/attribution bytes and concurrent delegated reservations, and only
   then set named hard count/byte caps. Verify the bounds and exhaustion paths
   against file and SQLite stores.
   - [x] Initial maximum-shaped `model.responded`, `tool.completed`, and
     `data.evaluated` event envelope measurements match between the actual
     file snapshot and SQLite event-row paths. The Hosting suite passes
     260/260. See the measured sizes above.
   - [ ] Complete the matrix for concurrent/delegated evidence, function calls,
     failures and terminal reserve, and event-count exhaustion; the initial
     per-event samples are not sufficient to select aggregate limits.
7. Specify lifecycle event order and bounded correlation metadata for starts,
   completions, cancellation, and failure. Preserve the failed node's
   invocation identity and active array index without duplicating raw items in
   iterator lifecycle events.
8. Prove checkpoint admission rejects iterator graphs and combinations with
   otherwise eligible loop or variable graphs.
9. Measure the chosen candidate's maximum-shaped model, MCP, and graph evidence
   through actual persistence serialization, including concurrent dispatch,
   before setting aggregate caps or admitting the graph to storage recovery.
10. Re-review the concrete contract, then implement Core validation, runtime,
   Studio authoring, passive playback, documentation, and regression tests as
   one integrated capability; do not add a schema gate or promotion step.

## Evidence

The review inspected `WorkflowRunner`'s static executor graph and `GraphMessage`, explicit data
evaluator, existing bounded-loop dispatch, checkpoint frame validation, the
typed-array decisions, the collection roadmap, MCP broker limits, agent turn
limits, model-trace limits, delegated execution, and run-store append paths.
The first review supports a dedicated typed iterator; the second rejected
speculative numeric budgets and requires a recorder-boundary accounting
contract. The parity-level review rejected a narrowed body allowlist. A later
architecture adjudication marked the scheduler choice **REVISE** pending the
side-by-side proof described above. AF subworkflow tests validate fresh per-item
child construction, conditional/static-parallel body execution, and a
production VAW Agent/MCP body with test-scoped event callbacks. Numeric budgets
remain unselected until measured. No public iterator behavior is claimed by
this decision.
