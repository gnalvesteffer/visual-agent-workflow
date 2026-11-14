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

A further proponent/opponent/adjudicator review after the matched parallel
Agent-result and typed direct-MCP argument/result proofs also returned
**REVISE**. The added comparisons strengthen activation-scoped value and join
evidence across the three candidates and both stores, but remain harness-owned.
The adjudicator supports a focused, non-public production-seam proof and
rejects selecting the hybrid or publishing a typed iterator contract yet. The
next proof must exercise typed item/index dispatch through real Core value
contracts and nearest-frame `Break` consumption alongside one VAW-owned frame,
lifecycle, and shared-budget owner. It must then cover the remaining persisted
outcome and custom-broker failure/timeout cases through both stores. Re-review
that concrete seam before adding a public node or Studio authoring. No
schema-specific gate or body allowlist is justified; checkpoint recovery stays
excluded until frame capture and restoration are demonstrated.

## Readiness re-review (2026-11-14)

A new proponent/opponent/adjudicator review returned **REVISE**. The hybrid
remains the leading proof candidate, but the integrated seam is still absent:
the matched candidate comparisons use harness-owned Break coordination, and
the private structured dispatcher had selected the nearest target rather than
leaving target derivation to its activation coordinator. The private proof does
graph-dispatch a synthetic `vaw.proof.array-break` and the coordinator consumes
its typed result, but this does not establish a public Core node contract or
one production VAW owner for frames, root lifecycle, and shared atomic budgets.
The reviewers confirmed the existing typed-binding, body-dispatch, Agent/MCP,
join, and file/SQLite results as useful candidate evidence while distinguishing
them from shipped iterator behavior.

The roadmap gate remains unchanged. Continue with a non-public integrated seam
proof: a graph-dispatched typed Break carrying source and activation identity;
source-only dispatcher requests with target derivation/nearest-frame
consumption in the coordinator; one VAW-owned run context whose dispatcher
returns typed outcomes without finalizing the root; and the persisted
lifecycle, budget, adapter, and playback matrix through both stores. Preserve
`core.terminate` as workflow-level successful exit. The review found no
demonstrated compatibility defect, but compatibility and playback evidence
remain prerequisites. Do not add public iterator/Break schema or Studio
authoring until the integrated proof passes and the design is reviewed again.
Checkpoint recovery remains excluded until activation frames are captured and
restored.

On 2026-11-13, the structured proof candidate moved item/index binding into its
VAW-owned activation frame. The frame carries the declared primitive element
type, requires an item parameter of that same type when one is requested,
requires the configured index parameter to be an integer, and resolves both
through Core's `WorkflowParameters` contract before body dispatch. The existing
integer minimum/zero/maximum candidate comparison now exercises this frame
binding path. This establishes a typed parameter-binding seam only: it does not
add graph output pins, a production iterator node, real graph-dispatched
nearest-loop `Break`, or production-owned shared lifecycle/budget semantics.
The architecture remains **REVISE** and the review gate remains open.

The following proof-only increment extracted the structured candidate's root
event ledger into a private run context. That context serializes writes, allows
one root start and one root terminal event, and advances its sequence only
after the recorder accepts an event. It clarifies lifecycle ownership but does
not own atomic work/evidence reservations: the current proof recorder still
does. Nearest-frame `Break` remains produced and consumed by the test harness.
The adjudicated next boundary therefore remains incomplete; do not infer a
production budget owner or select the structured candidate from this
refactoring.

A subsequent three-role review of that boundary returned **REVISE**. Keep the
proof private and separate three owners: the VAW activation coordinator owns
frames and consumes only a typed Break addressed to its nearest active frame;
one run context owns root/activation lifecycle and atomic work/evidence
reservations; the structured dispatcher routes a body and returns typed
outcomes without finalizing the run. The current dispatcher has no Break
outcome. A subsequent proof increment adds a private run-context ledger for
atomic event/outcome-byte reservations and shared iteration/model/tool caps;
its six file/SQLite cases prove serial Agent/MCP settlement, pre-dispatch model
and tool rejection, and parallel model reservation contention. Six more
file/SQLite cases run the structured candidate with production MCP timeout,
controlled custom-broker unknown outcomes, and host cancellation after dispatch;
each records the correlated uncertain outcome and terminal run state. Two
more cases verify parallel pre-dispatch denial persists without consuming the
one-call dispatch cap or publishing a join. Event/byte exhaustion and shared
activation/model/tool caps are also exercised through the run-context ledger;
the persisted prefix stays under its configured bounds and ends in failure.
Two file/SQLite cases confirm an intentionally undersized failure-byte reserve
rejects failure evidence before persistence and leaves no partial terminal.
Additional file/SQLite fault injection rejects a model outcome, root completion,
or root failure append. A rejected outcome or completion preserves the
recorded prefix; it writes `run.failed` when the store accepts that event, while
a rejected `run.failed` leaves the journal running. These cases do not establish
recovery after storage failure, so the run-context owner claim is still partial.
The structured coordinator now checks cancellation again after the final
iteration-completed event and before emitting the root terminal outcome. A
deterministic regression cancels from the accepted final iteration event and
confirms the run records cancellation without also completing (focused test:
1/1). This closes one lifecycle race in the proof candidate only; it does not
resolve the missing typed Break dispatch or select the architecture.
Acceptance must exercise the same coordinator with real Agent/MCP adapters and
file/SQLite stores, including foreign/stale Break targets, lifecycle races,
pre-dispatch budget rejection, and reservation settlement. Stop if the
dispatcher and coordinator both finalize lifecycle, Break is still created or
consumed by the test loop after body return, external calls bypass the budget,
or reservations leak/double-settle. This review does not select an architecture
or authorize public schema/Studio changes.

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
scoped production Agent/MCP evidence through both stores (2/2). The hybrid
fixture no longer uses a magic tool-result string: a test-only graph condition
routes the body result to a Break node, which emits a typed result carrying its
node ID and nearest target activation ID for the owning loop to consume. The
graph and trigger remain synthetic harness behavior. This does not prove an
authored workflow Break node or schema contract, and it is not yet the matched
graph-level contract across candidates. At this point in the proof,
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

`IteratorCandidates_ReserveConcurrentMcpOutcomesBeforeDispatch` now also
calibrates a shared event-count ceiling from the successful two-branch trace.
Across the structured, fresh-subworkflow, and hybrid candidates, with both
file and SQLite stores, the ceiling admits one authorization/outcome and
failure reserve, then rejects the competing `tool.authorized` before broker
dispatch. The admitted call settles, the join is suppressed, and the failed
history stays within the event limit (6 candidate/store executions; focused
theory passes 2/2; the full Agent Framework suite passes 380/380). This is
controlled-broker event-count evidence. Production
MCP count/byte reservation, bounded model/delegate outcomes, terminal reserve,
and event-count exhaustion now have candidate comparisons, but these remain
test-only accounting proofs. They do not bound every payload shape, choose
aggregate product caps, or establish production-owned budget/lifecycle.

The production-broker candidate comparison also now calibrates event-count
exhaustion from its successful parallel trace. With the real stdio `demo.slow`
MCP registration, the file and SQLite stores, and each of the three candidates,
the budget rejects the second `tool.authorized` event before that call can
reach MCP dispatch. The admitted sibling settles, the join is suppressed, and
the root fails with reserved terminal evidence (6 candidate/store executions;
focused theory passes 2/2; the full Agent Framework suite passes 380/380). The
test releases the first connection barrier when the reservation is rejected,
so this boundary is synchronized instead of depending on a timeout. This is
production-broker integration evidence, while the recorder and budget remain
test-owned; it selects no scheduler or product limit.

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

`IteratorCandidates_CustomBrokerExceptionKeepsOutcomeUnknownAndFailureSafe`
adds the complementary unclassified-exception path. A custom broker records a
correlated `tool.requested`/`tool.authorized` pair, attempts its dispatch, then
throws an exception containing a secret canary without emitting an outcome.
Across all three candidates and both stores, the run stops before the next
item, persisted node/root failure messages omit the raw exception, and the
unmatched authorization remains visible with its outcome reservation held.
Reopening preserves the same contiguous failed prefix (6/6 executions; focused
tests pass 2/2; the full Agent Framework suite passes 364/364). Together with
the timeout proof, this covers both a contract-conforming uncertainty event and
a broker that omits outcome evidence. It does not make arbitrary broker
outcome claims enforceable or establish production budget ownership.

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
workflows. The nested decision executor returns a typed Break signal carrying
its node ID and the nested activation as both source and nearest target; the
outer executor verifies and consumes that target, stopping the inner sequence
while the parent iteration proceeds. The test confirms distinct outer child Workflow
instances, sequential results, and iteration-correlated branch/join arrivals.
The focused test passes. The current full Agent Framework suite passes
247/247, including the second proof below.

This is positive evidence for candidate 2's dynamic construction and static
conditional/parallel body execution. The conditional routing, branches, join,
nested loop, and typed break controller are synthetic Agent Framework test
executors. This does not prove an authored For Each/Break node contract or
arbitrary graph control-flow semantics. It does not compare candidate 1 or
prove VAW production-node integration.

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
budgets. It is not evidence that the full parity-level scheduler has been
selected or implemented.

The proof-only structured activation coordinator now composes the production
collaboration state with VAW-owned rendezvous dispatch. The activation frame
passes an isolated collaboration runtime through retained production node
handlers; the structured dispatcher validates branch/fork/activation identity,
collects arrivals, and delivers each released branch without using the
Agent Framework rendezvous executor. The production runtime continues to own
grant checks, bounded exchanges and snapshots, timeout, abort, and collaboration
evidence. Two sequential activations of single-phase and two-phase unified
graphs pass through file and SQLite histories (4 successful store/shape cases).
Focused failure, cancellation, and timeout cases also pass through both stores
(12 cases total); they verify one abort, no join, no following item, and
preservation of a first-phase release when the second phase fails, is cancelled,
or times out. The success proof attempts an ungranted channel and confirms the
runtime denies it without recording a send. Event scopes and region invocation
IDs remain activation-specific. The focused matrix passes 16/16.

This is proof-only composition, not production iterator support: typed array
pins and actual nearest-loop `Break` dispatch remain unproven, and broader
post-release races, store append failures, shared budget ownership, recovery,
and production architecture selection remain open.

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

`IteratorCandidates_CancelSiblingBeforeNodeAdmission` uses the production
execution gate on the right Agent across structured, fresh-subworkflow, and
hybrid candidates. The left Agent fails only after the right target reaches
that gate; the gate runs before node admission. All candidates stop before the
right Agent adapter, `node.started`, or `node.cancelled`, suppress the join,
and stop before the next item. Their branch event shapes match in file and
SQLite stores (6 candidate/store executions). This matches cancellation after
target delivery but before node admission. It does not erase the earlier
mismatch when structured dispatch fails before sending the sibling target, and
it does not define evidence for queued-but-not-admitted work. The architecture
remains undecided.

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

`IteratorCandidates_CancelAfterOneJoinBranchCompletes` holds each candidate at
the persisted right-Agent completion callback, then cancels before that
callback returns control to graph routing. The structured candidate has
neither delivered the left target nor entered its handler; both Agent
Framework candidates have delivered that target and entered its handler, and
their normalized branch lifecycle shapes match. The test snapshots the
persisted event prefix and handler-entry signal while the callback is paused,
then cancels. In file and SQLite stores, all three candidates persist the
right-branch completion and root cancellation, suppress join completion, and
stop before the next item (6 candidate/store executions). This is a measured
scheduling difference at a harness-controlled pause point. It does not
establish a general production cancellation contract, queued-work semantics,
atomic publication, or select the architecture. Broader dispatch, admission,
and cancellation races remain open.

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
adapter with injected outcome-append rejection. It does not authorize retrying
the provider request or establish recovery of the failed activation.

`IteratorCandidates_McpOutcomeAppendFailureStopsLaterActivations` injects a
store rejection for `tool.completed` while each candidate invokes the real
local `demo.slow` MCP registration through the production broker. The broker
records `tool.uncertain`, does not claim completion, and fails the activation;
no later array item or parallel join runs. All three candidates are exercised
against both file and SQLite stores. The authorization and uncertainty event
counts match, all outcome reservations settle, and every failed event prefix
is contiguous and identical after reopening a new store instance (6/6 cases).
The focused test passes 2/2 and the full Agent Framework suite passes
362/362. This proves uncertain-outcome recording for this append failure, not
tool replay safety or process-loss execution recovery.

`IteratorCandidates_RootCompletionAppendFailureRecordsFailedFallback` and
`IteratorCandidates_RootFailureAppendFailureLeavesUnterminalHistory` compare
root terminal append failures for all three test-only candidates and both
stores. When `run.completed` is rejected after two successful body
activations, the structured runner and fresh/hybrid candidate fixtures persist
`run.failed` as fallback and preserve the original storage exception. When the
root `run.failed` append is rejected after a body failure, no terminal event
is durable and the run remains `running`. The
accepted prefixes remain contiguous and identical after store reopening in
all twelve candidate/store/outcome traces (focused tests pass 4/4; the full
Agent Framework suite passes 362/362). This demonstrates the storage boundary;
a `running` prefix is not resumable
execution, and the fallback failure does not recover the lost completion
record.

`IteratorCandidates_DataEvaluationAppendFailureStopsBeforeAgentDispatch`
rejects persistence of the `uppercase` node's `data.evaluated` event while the
same variable/index/Agent/MCP body runs through the structured, fresh
subworkflow, and hybrid candidates. Across file and SQLite stores (6/6
candidate/store cases), each candidate stops before Agent or MCP dispatch,
does not complete the active iteration or start the next item, and persists a
contiguous failed prefix that matches the reopened store. The rejected data
event is absent. This compares one derived-data evidence append failure; it
does not close the full data/function-call failure matrix or establish
process-loss recovery or production budget ownership. The focused tests pass
2/2 and the full Agent Framework suite passes 366/366.

`IteratorCandidates_DelegatedFunctionCallAppendFailurePreventsChildDispatch`
rejects persistence of the parent Agent's `model.responded` event containing
the delegated team function call. Across structured, fresh-subworkflow, and
hybrid candidates with file and SQLite stores (6/6 cases), the children never
start, the parent outcome reservation remains unsettled, the active iteration
and run fail, and each reopened store preserves its contiguous prefix. The
candidate event-kind/node/scope shapes also match after normalizing the
hybrid's outer activation prefix. The focused test passes 2/2. This compares
the pre-child-dispatch boundary; it does not cover later function-call
evidence failures or crash recovery. The full Agent Framework suite passes
368/368.

`IteratorCandidates_DelegatedChildOutcomeAppendFailureStopsLaterActivations`
rejects a delegated child's `model.responded` append after the child provider
returns. The reviewer is held until the researcher outcome boundary, making
the sibling cancellation deterministic. Across all three candidates and both
stores (6/6 cases), the team and root fail, no later item starts, the rejected
response remains absent and its reservation stays unsettled. Normalized
event-kind/node/scope sequences match across candidates, and each reopened
store retains its exact event prefix. This covers one post-dispatch delegated
outcome failure, not process-loss recovery or production budget ownership. The
focused tests pass 2/2 and the full Agent Framework suite passes 370/370.

`IteratorCandidates_CancelAfterMcpDispatchRecordsUncertainOutcome` cancels the
caller only after a proof broker confirms MCP dispatch. Across the three
candidates and both stores (6/6 cases), the tool outcome is recorded as
uncertain before the node and run are cancelled, no iteration completes or
later item starts, and the reservation settles only after the uncertain event
is accepted. Reopened stores retain the exact event prefix. This covers
post-dispatch caller cancellation in the proof broker; production broker
publication races and process-loss recovery remain open. The focused tests
pass 2/2 and the full Agent Framework suite passes 372/372.

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

## 2026-11-13 adjudication and matched graph-level proof

A three-role review considered whether to prove typed item/index and nearest
`Break` behavior first in the structured candidate or as one matched graph
contract across all candidates. The adjudicator returned **REVISE**: the
structured-only proof could anchor the architecture before comparison. The
resulting matched proof runs the same private, production-backed graph-level
contract through the structured, fresh-subworkflow, and hybrid candidates.
It resolves typed item and zero-based index through graph data paths; routes a
typed `Break` signal to the nearest nested iterator; skips the remaining inner
items while allowing the outer iteration to continue; and compares scoped
event ordering and persisted prefixes. A coordinator-only sentinel is not
sufficient evidence of graph dispatch. This proof remains private and selects
no runtime architecture; production-owned budget/lifecycle and the rest of the
matrix remain open. No public node, schema contract, or Studio authoring is
admitted by this adjudication.

The first matched sub-proof now passes: the same current-schema integer
item/index data graph uses typed `WorkflowValue` nodes, converts both values
through explicit graph pins, and sends the combined value to the Agent in the
structured, fresh-subworkflow, and hybrid candidates. Outputs, activation
scopes, and node lifecycle ordering match; persisted prefixes pass for file
and SQLite stores (6 candidate/store executions). This demonstrates typed
parameter dataflow only. The proof also compares each `data.evaluated` record's
scope, connection identity, and serialized value, confirming that changing
item/index results are attributed to the matching activation in each candidate.
Dedicated iterator element/index pins and production nearest-loop `Break`
dispatch remain unproven, so the architecture gate stays open.

The structured node-dispatch proof now runs the same production
`NestedBodyDefinition` used by the hybrid nested-break case through
`ExecuteStructuredSchedulerProofAsync`, with a test-owned activation
coordinator and the private typed control graph. For each outer item it
resolves the nested item/index through graph data pins, dispatches the real
Agent/MCP-backed body, consumes a nearest-inner-frame Break result, and
continues the outer item. File and SQLite cases pass (2/2) with scoped
invocation evidence and no body dispatch for the item after Break. Iteration
sequencing remains in a test coordinator.

The fresh-subworkflow body proof now also uses `NestedBodyDefinition` for the
nested Break case. Each inner item binds a fresh Agent Framework child
workflow whose executor runs the production VAW body, then routes its result
through the same private typed control graph. The `WorkflowOutputEvent`
transports the typed Break result to the owning loop, which checks the target
activation and stops before the next inner item. Both file and SQLite cases
pass (2/2), and four distinct child workflow instances are asserted across the
two outer activations. This is transport and body-composition evidence for
candidate 2; it does not prove production loop scheduling.

`IteratorCandidates_MatchTypedNestedBreakContract` now composes those paths in
one comparison against the same current-schema `NestedBodyDefinition` and
private typed control graph. Structured dispatch, the fresh child workflow per
body activation, and the hybrid runner produce the same result, Agent inputs,
tool queries, control-graph node/scope sequence, loop/node lifecycle shape, and
persisted event prefix through file and SQLite stores (6 candidate/store
executions). The inner Break targets its own activation, item index 2 never
starts, and outer index 1 still runs. Structured and hybrid use test-owned
activation-loop coordinators; this establishes matched proof-harness behavior,
not a production iterator architecture, authored Break node, or dedicated
iterator pins. The broader body-capability and shared-budget matrix remains
the architecture gate.

The matched candidate matrix also now includes the same current-schema
two-output Sequence body. The structured production-node dispatcher, fresh
Agent Framework child workflow, and hybrid runner each execute both direct
MCP paths for two item activations. The file and SQLite variants match output,
tool queries, normalized Sequence event shape, and activation-scoped completion
evidence (2/2 focused theory cases). This establishes Sequence composition
within the proof harness only; it does not select the production iterator
architecture or claim authored iteration/Break support. The full Agent
Framework suite passes 382/382 for this increment.

`StructuredSequenceProof_FailureCancelsSiblingOutput` verifies the structured
candidate's failure boundary: one direct-MCP path fails only after its sibling
has dispatched, and the sibling's in-flight tool observes cancellation. The
Sequence evidence records the failed path, cancelled path, and failed Sequence
terminal state through file and SQLite stores (2/2 focused cases). The
proof-only dispatcher now uses a linked cancellation token for output paths;
the test would otherwise leave the sibling call running. This does not yet
compare Sequence failure settlement across candidates, and it is not production
iterator behavior. The Agent Framework suite passes 384/384 after this change.

`IteratorCandidates_SequenceFailureCancelsSiblingOutput` extends the same
synchronized failure through all three candidates. Across file and SQLite
stores (six candidate/store executions), each records the same failed output,
cancelled sibling output, failed Sequence terminal, uncertain in-flight MCP
outcome, and failed root status. The focused theory passes 2/2. This closes
this Sequence failure case in the comparison matrix only; broader Sequence
failure shapes, cancellation races, custom-broker conformance, and production
budget/lifecycle ownership remain open. The full Agent Framework suite passes
386/386.

## Typed primitive payload comparison

`IteratorCandidates_ResolveTypedItemAndIndexThroughGraphValues` now compares
the structured, fresh-subworkflow, and hybrid proof candidates using the
validated JSON integer array `[-9223372036854775808,0,9223372036854775807]`.
The proof parses it against the existing `array<integer>` contract, including
both signed 64-bit limits, and carries each canonical integer value into the
integer-typed workflow parameter, and reads both item and zero-based index
through production Workflow Value and integer-to-string graph nodes before the
Agent node. Candidate output, Agent input, and lifecycle event shapes match;
persisted histories pass through file and SQLite stores (2/2 focused cases).

This strengthens scalar-value evidence only. Item and index still enter the
body through the existing parameter contract rather than authored iterator
pins, and nearest-loop `Break` remains a test-only graph harness. The result
does not select an architecture or establish a public iterator contract.
`IteratorCandidates_EmptyTypedArrayCompletesWithoutBodyActivation` also
compares `[]` as `array<integer>` across all candidates: each returns an empty
result without iteration/body-node starts and persists the same lifecycle
shape in file and SQLite stores (2/2 focused cases). This closes only the
empty typed-input comparison; it does not resolve production lifecycle,
budget, or authoring contracts.

`IteratorCandidates_RouteEachItemThroughUnifiedBooleanBranch` compares a
current-schema production body containing `String Equals`, `Boolean Branch`,
two Agent arms, and an exclusive Merge. For `yes/no/yes`, the three candidates
produce the same output, branch decisions, selected Agent invocations, event
shape, and activation scopes; file and SQLite histories pass (2/2 focused
cases). This closes one real conditional-routing slice in the candidate
matrix. The loop coordinator is still test-owned, and the proof does not
establish iterator pins, authored `Break`, or recovery.

`IteratorCandidates_MatchUnifiedParallelJoinAcrossAllCandidates` compares a
current-schema production fork with two Agent branches and a barrier join for
two items. The structured dispatcher, fresh per-item Agent Framework
subworkflow, and hybrid runner produce the same joined outputs and normalized
branch/join event shape, including arrival connection IDs and matching Agent
invocation start/completion identities. Both item scopes complete their joins;
the event histories persist through file and SQLite stores (2/2 focused
cases). This closes successful parallel composition in the candidate matrix
only. Parallel failure/cancellation races, production lifecycle/budget
ownership, authored iterator pins, `Break`, and process-loss recovery remain
open.

`IteratorCandidates_MatchTypedNestedBreakContract` now models explicit
`Array Element` (`array<string>` → `string`) and `Array Index` (`integer`)
output pins in the private matched proof. The element pin supplies each body
run's input and the index pin supplies its typed workflow parameter. A
separate proof `Break` node emits a typed signal to the nearest activation;
the three candidates match pin values, Agent/MCP inputs, scoped lifecycle
shape, and persisted prefixes through file and SQLite stores (2/2 focused
theory cases). This removes the prior parameter-only binding from this proof
path, but the pin and Break nodes are test harness constructs. Production graph
descriptors, runtime dispatch, the architecture decision, and the remaining
failure/cancellation, budget, evidence-bound, and recovery gates remain open.

`IteratorCandidates_CancelParallelSiblingAfterFailureAcrossAllCandidates`
compares a synchronized parallel-Agent failure in all three candidates. Both
branches enter; the left invocation fails; the right invocation is cancelled;
the join and following item do not run. Normalized branch lifecycle evidence
matches across the structured dispatcher, fresh Agent Framework subworkflow,
and hybrid runner, and each failed history persists through both file and
SQLite stores (2/2 focused theory cases). This closes this controlled sibling
failure shape only. Other join/cancellation/publication races, production
lifecycle and budget ownership, and interrupted-process recovery remain open.

`IteratorCandidates_CancelAdmittedSiblingBeforeAgentHandler` adds the same
post-start cancellation boundary to all three candidates. The right branch is
held after its persisted `node.started` event and before Agent adapter entry;
left-branch failure then cancels it. Structured dispatch, fresh subworkflow,
and hybrid runner histories match across file and SQLite stores, including
persisted cancellation evidence, no right adapter entry, no join, and no next
iteration (2/2 focused theory cases). This remains proof-harness evidence; it
does not select production scheduling or close recovery and lifecycle gates.

`IteratorCandidates_ScopeVariableStateToEachActivation` compares the existing
mutable-variable path across all three candidates. Each of three string items
starts the variable at its declared default, writes the current item, and reads
that value within the same activation. The structured, fresh-subworkflow, and
hybrid candidates produce matching variable events, Agent inputs, direct MCP
outputs, and persisted `data.evaluated` evidence, and each history persists
through file and SQLite stores (2/2 focused theory cases). The test reuses the
structured candidate's graph-data evaluator across sequential activations,
then checks each runtime output and evaluation record against the current
item. This verifies these linear Agent/MCP and data-evaluation boundaries only;
parallel runtime-output cache identity beyond the tested join result, broader
cache identity, and production iterator frames remain unresolved.

`IteratorCandidates_MatchUnifiedParallelJoinAcrossAllCandidates` also checks
the persisted result of two parallel Agent branches for each of two changing
items. The structured, fresh-subworkflow, and hybrid candidates preserve the
matching left/right Agent payloads under the correct item scope, and the
normalized branch/join events and result values match through file and SQLite
stores (2/2 focused theory cases). This verifies parallel Agent result
association at the join boundary; it does not establish parallel runtime
cache behavior for other node families or select a production scheduler.

`IteratorCandidates_ScopeParallelMcpArgumentsAndResultsToEachActivation`
extends the same repeated-item comparison to two concurrent direct MCP nodes
with typed argument pins sourced from the current item. Evaluated argument
values, tool outcomes, join payloads, and activation scopes match across the
structured, fresh-subworkflow, and hybrid candidates and persist through both
stores (2/2 focused theory cases). This proves parallel direct-MCP argument
and result association for this body; it does not establish every runtime
cache lifetime or select a production scheduler.

`IteratorCandidates_ScopeGraphFunctionResultsAndEvidenceToEachActivation`
reuses one pinned pure graph function across three changing string items. The
structured, fresh-subworkflow, and hybrid candidates produce the same outputs
and persisted function input/evaluated-node evidence, scoped to the matching
item in file and SQLite stores (2/2 focused theory cases). This proves the
linear pure-data function-call boundary only; parallel function-call
activations remain open.

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
   - [x] Compare mutable variable-store isolation and linear Agent/MCP runtime
     outputs across structured, fresh-subworkflow, and hybrid candidates. Each
     activation starts from the declared default, writes and reads only its own
     item, and produces matching Agent inputs, direct MCP outputs, and scoped
     persisted `data.evaluated` evidence through file and SQLite stores (2/2
     focused theory cases). The structured candidate reuses its evaluator
     across activations. Parallel runtime calls, broader cache identity, and
     production frame semantics remain open.
   - [x] Compare persisted `data.evaluated` evidence for changing typed item
     and index parameters across all three candidates. Per-activation values,
     connection IDs, scopes, and serialized payloads match in file and SQLite
     (2/2 focused theory cases). This proves the tested parameter-evaluation
     boundary; broader runtime-output cache identity remains open.
   - [x] Compare a pinned pure graph-function call across three item
     activations. Outputs and function input/evaluated-node evidence match the
     fresh-subworkflow and hybrid candidates, with each event scoped to its
     own item in both stores (2/2 focused theory cases). Parallel Agent graph-
     function tool invocations and direct pure function nodes consumed from
     concurrent Agent branches are proven in the follow-ups below. Agent output
     reuse across sequential activations is also tested below for Agent,
     direct MCP, and WorkflowCall output paths; other runtime output families
     and broader cache identity remain open.
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
   - [x] Exercise concurrent direct/delegated model and MCP evidence,
     maximum-shaped delegate function-call evidence, failures and terminal
     reserve, and event-count exhaustion against file and SQLite stores. The
     three iterator candidates now compare shared event-count admission at
     model request and controlled MCP authorization boundaries. This closes
     matrix coverage for the tested shapes only; it does not establish a
     universal maximum payload or select aggregate limits.
7. Specify lifecycle event order and bounded correlation metadata for starts,
   completions, cancellation, and failure. Preserve the failed node's
   invocation identity and active array index without duplicating raw items in
   iterator lifecycle events.
8. [x] Prove current array graph shapes remain outside checkpoint admission.
   A schema-2.0 typed-array input/output graph is rejected by the deterministic
   checkpoint shape gate; otherwise eligible loop and variable graphs are
   admitted under their existing policy, while adding a typed array to either
   is rejected by Core's `array.placement` rule, including with explicit loop
   recovery opt-in. This proves current placement and admission boundaries,
   not future iterator-frame capture/restoration.
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

## Private structured Break dispatch increment (2026-11-14)

The proof-only structured dispatcher now reaches a private Break node through
ordinary graph routing and returns its typed source node, source frame, target
frame, and scope identity to the activation coordinator. Sequence output paths
and static fork branches use local cancellation scopes: Break stops new work,
settles admitted siblings, suppresses normal Sequence/join completion, and
propagates to the nearest frame. Host cancellation still wins, while a settled
non-cancellation descendant failure is selected before Break. The in-memory
proof callback emits `sequence.aborted` when Break exits a Sequence, without
claiming an ordinary Sequence terminal outcome. This event is not a recognized
`SequenceEvidence` lifecycle kind and is not supported passive playback
evidence. The Sequence-break proof remains in-memory; it does not persist or
reopen that history through a run store. `core.terminate` retains its separate
workflow-exit behavior.

Focused structured tests pass (10 cases), including Break through Sequence and
a static fork with a cooperatively cancelled Agent sibling, nearest-frame
consumption, foreign/stale target rejection, an admitted sibling failure
racing with Break, host cancellation racing with Break, and the existing
failed-fork cancellation regression. A test-only node-dispatch override runs
the fork proof against an otherwise validated revision; its root/branch
event prefix persists and reopens through both file and SQLite stores (2/2).
The Break behavior itself is not serialized in that revision, and the
proof-only Sequence graph remains in-memory. A three-role adjudication keeps
Break-driven Sequence abort out of persisted Sequence lifecycle evidence until
a contract identifies the exact Sequence invocation and Break frame, settles
every dispatched output and join, preserves uncertain external outcomes, and
specifies older-reader behavior. Continue proving graph-dispatched Break in
memory and persist only histories the current reader reconstructs consistently;
do not encode Break as cancellation.

The static-fork failure race also runs against both supported stores. When an
admitted sibling fails after Break dispatch, the file and SQLite histories
reopen with the sibling failure and failed root outcome, with no completed join
or successful root outcome (2/2 focused store cases). This closes persisted
failure-race coverage for this static-fork proof only. The host-cancellation
race is also persisted and reopened through both stores (2/2 focused store
cases); the root remains cancelled and has no successful terminal event. These
store cases cover the supported root/branch histories, not passive playback
support for Sequence abort. A rejected `loop.iteration.started` append is also
tested through both stores (2/2 focused cases): the runtime records root
failure and dispatches no body Agent or MCP work. Production scheduler
selection, authored iterator pins, and public Break/array contracts remain
open. This increments the proof only and does not change the **REVISE**
architecture outcome or authorize public schema/Studio work.

## Break target ownership in the structured proof (2026-11-14)

The private structured dispatcher now returns a typed Break request containing
only its source node, source activation frame, and scope. It no longer selects
the nearest target. `ExecuteStructuredActivationSequenceProofAsync` passes the
request to the active VAW frame, which validates source identity/scope, derives
the nearest loop target, consumes the request, and records the resolved target
in the activation completion evidence. Focused tests cover nested nearest-frame
selection, stale/foreign request rejection, graph-dispatched Break behavior,
and file/SQLite persistence (8 focused cases pass). The dispatcher/coordinator
split is clearer in this proof candidate, but the implementation remains
private and the three-candidate harness still owns its own loop coordinators.
This does not establish a production-owned shared lifecycle/budget context,
public node contract, or selected scheduler; the architecture remains
**REVISE**.

## Parallel Agent graph-function calls across array activations (2026-11-14)

The structured, fresh-subworkflow, and hybrid candidates now execute a
two-Agent fork/join per item, with both Agents invoking the same pinned pure
graph function through the production `AgentNodeExecutor`. A synchronized test
chat client holds each pair of initial model requests until both branches have
arrived, so the test verifies overlapping branch work instead of assuming it
from the graph shape. For two changing items, all three candidates produce
matching function input/output evidence scoped to the correct Agent and item;
each candidate history persists and reopens through file and SQLite stores
(2/2 focused store cases). This closes parallel Agent graph-function tool
invocation for this candidate matrix only. Parallel direct `FunctionCall` nodes
are exercised separately below. Neither test proves production-provider
concurrency, a selected iterator scheduler, or public typed iterator authoring.

## Parallel direct graph-function nodes across array activations (2026-11-14)

The same three candidates now run two direct pure `FunctionCall` nodes as the
data inputs of a parallel Agent fork for each item. Each Agent waits at an
explicit pair barrier after its data input has been evaluated, proving both
branch computations reach the concurrent boundary in each activation. For two
changing items, function results and their pinned function/input/evaluated-node
evidence match by branch and scope across candidates; every event stream
persists and reopens through file and SQLite stores (2/2 focused store cases).
This covers direct function nodes used by concurrent fork branches in this
matrix. It does not establish parallel data-node evaluation under arbitrary
schedulers, broader cache identity, production-provider concurrency, or a
selected iterator architecture.

## Runtime Agent output cache identity across array activations (2026-11-14)

The three candidates now run an Agent whose runtime output feeds a downstream
`TextConcat`, which combines that output with the current typed item and passes
it to a second Agent. The structured candidate retains one graph data
evaluator across all items, so the test specifically detects a stale runtime
output reused under the next activation. Across three changing items, the
structured, fresh-subworkflow, and hybrid candidates produce the same second
Agent inputs and `data.evaluated` values under the correct activation scope;
each history persists and reopens through file and SQLite stores (2/2 focused
store cases). This proves activation-keyed reuse for the tested Agent output
path. Other runtime-producing node types, arbitrary cache graphs, and
production iterator integration remain open.

## Runtime MCP output cache identity across array activations (2026-11-14)

The three candidates now run a direct MCP tool node between two Agents, with
the MCP output also serving as the second Agent's data input. The structured
candidate retains one graph data evaluator across all items, so this test
detects a prior activation's tool result being reused. Across three changing
items, the structured, fresh-subworkflow, and hybrid candidates produce the
same per-item MCP outputs and `data.evaluated` values under the correct scope;
all event streams persist and reopen through file and SQLite stores (2/2
focused store cases). The production node handler is exercised with a fake
tool broker; this proves the direct MCP output path, not provider behavior,
other runtime-producing node types, arbitrary cache graphs, or production
iterator integration.

## Runtime WorkflowCall output cache identity across array activations (2026-11-14)

The three candidates now execute a pinned child workflow between two Agents.
The child passes through each changing Agent result, and the second Agent
consumes the WorkflowCall output. The structured candidate reuses its parent
data evaluator across activations; the fresh-subworkflow and hybrid candidates
provide comparisons. All three produce matching per-item child results and
activation-scoped `data.evaluated` payloads; their event histories persist and
reopen through file and SQLite stores (2/2 focused store cases). This exercises
the production WorkflowCall runner with a real inline child definition, not a
test broker. It proves the tested WorkflowCall output path only; other runtime
output families, arbitrary cache graphs, and production iterator integration
remain open.

## Delegated outcome reservation identity in the structured run context (2026-11-14)

The structured run-context ledger initially recognized `modelCallId` and
`toolCallId` only at the top level of event data. Agent Framework delegation
records those same model/MCP events inside the delegated event's JSON
`eventData` envelope, so the ledger could reject valid delegated outcomes as
unreserved even though direct calls settled correctly. Reservation identity
extraction now descends through at most four JSON `eventData` envelopes while
the exact outer event remains what is persisted and byte-accounted. New
file/SQLite regressions execute two delegated model calls and two delegated
production-MCP calls through the structured coordinator and verify successful
request/outcome settlement and reopened terminal histories (4 focused cases).
This closes delegated reservation-envelope coverage for this private proof;
it does not select an iterator architecture or establish a public runtime
budget contract.

## Private production body-dispatch outcome boundary (2026-11-14)

`WorkflowRunner` now has an internal proof-only body-dispatch entry point that
uses the normal Agent Framework workflow translation and node adapters, but
does not publish root lifecycle events to its caller. It returns a typed
disposition for success, workflow termination, failure, host cancellation,
timeout, unresolved external outcome, and evidence-write failure. This gives
an enclosing VAW coordinator an explicit result channel while preserving
node-scoped body evidence and keeping `core.terminate` distinct from failure.
Four focused regressions pass (success, termination, failure/cancellation/
timeout/unknown outcome, and evidence-write failure); body success and
termination produce no root terminal event at this boundary.

This closes the dispatcher result/lifecycle suppression contract only; the
coordinator integration below adds a first production-backed sequential proof.
No public schema, iterator, Break node, Studio authoring, or checkpoint
recovery is admitted by this increment.

## Sequential coordinator over production body dispatch (2026-11-14)

A private VAW activation coordinator now invokes the production Agent
Framework body dispatcher for each typed primitive array item. A single
`StructuredActivationRunContext` owns the root lifecycle, event sequence,
activation frames, and shared model/MCP dispatch and evidence reservations.
The dispatcher returns typed outcomes, including budget rejection, without
publishing root lifecycle events. Scoped Agent and direct-MCP evidence is
recorded under each frame and the resulting run history persists and reopens
through file and SQLite stores. Focused store tests cover successful execution,
pre-dispatch tool-budget rejection, and a body `Terminate` outcome (6 cases).
The terminal event carries the terminating node in its payload while remaining
a root-scoped event, so the run context enforces exactly one terminal outcome.

This is a sequential proof path, not the selected production iterator
architecture. It does not yet consume graph-dispatched typed `Break`, complete
the persisted failure/race/custom-broker/store-fault matrix, or prove all
reservation-settlement cases. The main gate remains **REVISE**; public typed
iteration, Studio authoring, and checkpoint recovery remain out of scope.
