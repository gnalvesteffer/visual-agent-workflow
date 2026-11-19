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

## Checkpoint-recovery sequencing review (2026-11-16)

A new proponent/opponent/adjudicator review returned **REVISE**: keep
typed-array iteration excluded from `CheckpointWorkflowHost` while completing
the iterator candidate and persisted-evidence gates. The existing checkpoint
frame's exact identity, native payload, evidence-prefix, atomic-publication,
and local-ownership mechanisms are useful foundations, but they do not bind
the frozen typed input, nested activation stack, per-item state, or
coordinator-owned control state required by array iteration. The production
architecture, lifecycle owner, and shared-budget owner are also not selected;
designing a durable frame against a candidate now could preserve the wrong
state model.

This review changes recovery sequencing only. It does not reduce the Blueprint
parity target, add a schema/capability gate, or justify a loop-body allowlist.
Continue the non-public integrated runtime and evidence matrix through file and
SQLite stores, then resolve the lifecycle, cancellation, join, and budget
owners and repeat the architecture review. After selecting that lifecycle,
specify an immutable frame that binds the frozen array and nested activation
state to the exact native cursor and evidence prefix. Prove capture and
restoration, including publication faults, stale or tampered frames,
cancellation, exclusive ownership, and uncertain external effects, before
changing checkpoint admission. Read-only feasibility analysis and documenting
these future requirements remain in scope meanwhile.

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
cases). Each Agent result is connected by its own data edge to the Merge; the
fixture does not substitute a shared Input-to-Merge edge for the selected
branch's value. The output comparison includes the deterministic Agent result
payloads. This closes one real conditional-routing slice in the candidate
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

A further controlled-broker proof covers Break cancelling a static-fork sibling
after MCP authorization. The broker reports an unknown post-dispatch outcome;
the `tool.uncertain` event retains the authorized call ID, and file/SQLite
reopen preserve it with a failed root, no join completion, and no next-item
admission (2/2 focused cases). This is proof of the coordinator's handling of
that broker contract, not a guarantee for arbitrary custom brokers or the
production Agent Framework scheduler. Sequence abort remains in-memory and
outside supported passive playback.

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

## Parallel direct-MCP join in the integrated coordinator (2026-11-15)

`ProductionBodyActivationCoordinator_JoinsParallelMcpPerItem` extends the
private integrated coordinator proof from parallel Agents to two direct-MCP
branches. Each branch receives its own typed query pin sourced from the
current array item; the test broker records reservation-correlated
authorization/completion evidence, and the static join retains both item
specific results. File and SQLite histories reopen with matching invocation,
scope, join-arrival, and terminal evidence, and passive playback renders both
completed joins (2/2 focused cases).

This closes only successful parallel direct-MCP argument/result composition
through the private VAW coordinator and production body dispatcher. The test
uses a controlled broker; the following production stdio broker proof extends
the evidence to the real MCP boundary. At this stage, failure-triggered
sibling-cancellation races, process recovery, and architecture selection were
still open; a later section records the production broker cancellation proofs.
This is not public iterator authoring or a shipped workflow capability.

`ProductionBodyActivationCoordinator_ExecutesParallelProductionMcpPerItem`
runs the same two-item fork/join using the real stdio `McpToolBroker`, exact
pinned `demo.uppercase` contract, and separate typed `text` input pins sourced
from the current item. Four actual calls authorize and complete; each item's
uppercase values reach its own join, and reopened file/SQLite histories render
both joins (2/2 focused cases). This proves successful production-broker
dispatch and evidence through the private integrated coordinator. At this
point it did not prove production-broker failure-triggered sibling
cancellation; the later cancellation sections add that evidence. No iterator
architecture is selected or published.

`ProductionBodyActivationCoordinator_HostCancellationUnsettlesParallelProductionMcpCalls`
cancels the root only after both real stdio MCP branches persist
`tool.authorized`. The production broker records a matching `tool.uncertain`
for each dispatched call; the root remains `cancelled`, no join completes,
and no later item starts. File/SQLite histories reopen with the same
correlated evidence and passive playback retains the uncertainty (2/2 focused
cases). This qualifies host cancellation of parallel production-broker calls,
not cancellation caused by a sibling's failure.

`ProductionBodyActivationCoordinator_ParallelMcpFailureSuppressesJoinAndLaterItems`
adds the corresponding synchronized failure boundary for two parallel direct
MCP branches. Both branches authorize before deterministic failure; the test
proves authorization/failure reservation IDs match, both reservations settle,
the join and iteration completion are suppressed, and the following item is
not admitted. The failed scoped history reopens and passive playback shows the
incomplete iteration through both stores (2/2 focused cases). This is a
controlled-broker failure proof, not a sibling-cancellation race or
production-broker qualification.

`ProductionBodyActivationCoordinator_ParallelMcpFailurePreservesCancelledSiblingUncertainty`
holds the review branch after authorization and dispatch, then makes the
research branch fail. The canceled review call records `tool.uncertain` with
the same `toolCallId` as its authorization; both branches settle, the join and
next item stay suppressed, and file/SQLite playback preserves the incomplete
failed iteration (2/2 focused cases). The root status remains `failed` because
the graph has a definitive branch failure; the sibling's unresolved external
outcome remains explicit in its tool evidence and is not cleared by terminal
status. This validates the controlled coordinator path, not cancellation
behavior of the real MCP broker in a parallel body.

## Production body Break transport proof (2026-11-14)

The private proof now carries a graph-dispatched Break through the production
Agent Framework `WorkflowBuilder` path using a typed field on `GraphMessage`.
The configured Output executor serves as the framework output handoff, but the
private Break handoff bypasses ordinary Output-node events and visible
execution-transfer evidence. The body dispatcher returns a source-only request
(node, activation frame, and scope); the VAW coordinator validates it with the
active frame, derives the target, records that target in iteration evidence,
and stops admitting later items. The bridge does not set or reinterpret the
workflow termination signal. A production-backed condition route proves the
bridge; existing tests separately preserve ordinary `core.terminate` behavior.
The focused dispatcher/coordinator and frame-rejection tests pass.

This remains an in-memory proof over a private, non-publishable node type. The
current transport rejects Sequence and parallel-fork bodies because sequence
abort and concurrent sibling settlement are not yet implemented here. It also
does not prove nested outer-loop continuation or stale/foreign identity
rejection through the production body boundary, and the Break-specific event
history has not been persisted through file/SQLite playback. The structured
scheduler remains behavioral evidence for those cases, not proof of this
Agent-Framework path. Keep the parent gate open and do not introduce public
schema or Studio authoring.

## Production static-fork Break settlement proof (2026-11-14)

The private production-body proof now supports one narrowly shaped static
fork: exactly one private Break endpoint, each fork branch contains only its
branch anchor followed directly by an Agent, a direct MCP tool, or that Break,
and every endpoint feeds the matching join input. Sequence, multiple Break
nodes, and longer or conditional branch paths remain rejected. This is a
proof-topology constraint, not a proposed public workflow allowlist.

The iterator frame carries a private atomic abort scope. When Break is
requested, the dispatcher marks the scope and emits the source-only typed
request. Branch work already dispatched by the fork is allowed to start and
settle; the join barrier drains its arrivals, while the join executor and
ordinary Output handoffs are suppressed. The private Break handoff is given
priority over any ordinary output event, and the coordinator stops before
admitting the next array item. The implementation does not cancel the overall
Agent Framework run to express Break. A synchronized production Agent test
proves the sibling completes before the body returns, another proves a
post-Break Agent failure wins, a controlled direct-MCP call that reports an
unknown outcome after Break fails the iteration, and a cancellation triggered
at the Break completion event remains a host cancellation. Focused tests pass.

The production Break event trace now persists and reopens through both the
file and SQLite run stores. Proof-only revisions carry a SHA-256 hash of the
normalized serialized graph for historical evidence integrity; this hash does
not make the private node publishable, and ordinary `WorkflowRevision.Create`
continues to reject it. Regression tests verify the complete contiguous event
sequence and terminal run status after reopening each store.

This remains a private, non-publishable proof. It does not establish delegated
work or calls through the production MCP broker on this production path, nested
outer-loop continuation, or multiple/long fork paths. The direct-MCP case uses
the production node executor with a controlled broker; it does not exercise a
real MCP server. Earlier structured-scheduler and candidate tests for unknown
external outcomes are not evidence for this production adapter path. The
roadmap parent remains open. Since the increment changes no public runtime
contract or user-visible workflow behavior, the architecture overview and
user guide remain unchanged.

## Multi-node static-fork dispatch follow-up (2026-11-14)

The initial longer-path tests timed out because they waited for a downstream
branch node while deliberately holding a sibling Agent that was active in the
same Agent Framework superstep. That timeout did not show a missing edge.
`ProductionBodyBreakAfterAgentChainDispatchesSuccessorsAndSettlesSibling`
corrects the gate: the first branch Agent and its sibling start together, the
test confirms the next branch Agent has not advanced yet, releases the sibling,
then observes the second Agent and private Break dispatch. Both file- and
SQLite-backed event histories persist and reopen exactly. The static-fork
proof topology now walks disjoint linear Agent/MCP paths and checks each
private Break's branch-local data input and matching join endpoint.

The focused production-body Break and configurable-parallel tests pass (9/9).
This proves the two-Agent branch path and the framework's superstep boundary;
it does not yet prove multi-node MCP paths, mixed Agent/MCP chains, deterministic
transforms, conditional paths, or the full failure/cancellation/playback matrix
for longer paths. Continue those cases through the real runner before widening
the proof or selecting a public iterator contract. No public iterator schema
or Studio authoring is introduced by this proof.

## Nested production-body Break proof boundary (reviewed 2026-11-14)

The three-role review recommends that the VAW coordinator own both the nested
activation-frame tree and the single root lifecycle. The Agent Framework body
remains a single-activation dispatcher that returns a typed Break request with
source node, source frame, and scope only. At the coordinator boundary, validate
the source against the active graph and frame, derive the nearest loop target,
and unwind only that loop. An inner Break must return control to its caller so
the next outer item can be admitted. Sharing an event context alone is
insufficient: nested calls must not emit root terminal events or consume a
Break for the wrong frame. This remains proof-only and does not select a public
array/Break contract.

The minimum proof is a production-dispatched inner Break followed by an outer
item, rejection of stale-frame, stale-scope, and unadmitted-source requests at
the same coordinator validator, one root start and terminal with contiguous
scoped history, unchanged `core.terminate`, and file/SQLite reopen checks.

## Nested production-body Break proof (2026-11-14)

The proof-only VAW coordinator now owns a two-level outer/inner activation
sequence and the single root `StructuredActivationRunContext`. Each inner item
is dispatched through the production Agent Framework body path. A graph-routed
Break returns only its source identity; the coordinator validates and consumes
it against the active inner frame, ends that inner sequence, then continues
with the next outer item. The same coordinator validator rejects a mismatched
source frame, stale scope, and a source node outside the admitted Break set.
The test uses the actual dispatched request as the basis for those tampering
checks.

File and SQLite tests verify one root start and terminal event, contiguous
sequences, correct nested scopes, no later inner item after Break, the following
outer item, and identical reopened history. Existing production `core.terminate`
tests still pass for both stores. This closes the private production-body Break
proof gate. It does not add public nested iterator/Break support or close the
separate VAW lifecycle/reservation, broader body-capability, or checkpoint
recovery gates in the roadmap.

## Production-body shared reservation settlement (2026-11-14)

The production-body coordinator now has file and SQLite proof coverage for
pre-dispatch model-budget rejection in addition to the existing MCP-budget
rejection. The provider is called only for the admitted model request; its
response and the following MCP result settle their correlated evidence
reservations. The rejected second activation does not emit a model request or
complete its loop iteration, and the root records exactly one failed terminal
event.

Delegated model and delegated production-MCP reservation settlement tests now
run through the same production-body coordinator. Both persist and reopen the
scoped run history through file and SQLite stores and verify one root terminal
event with all admitted model/tool request and outcome events. The focused
production-body test filter passed 18/18 cases. This closes the private
root/activation lifecycle and shared-reservation proof item in the roadmap.
It does not close the broader persisted playback, joins, cancellation/failure
race, custom-broker uncertainty, or store-append-failure matrix; nor does it
admit a public iterator/Break schema or Studio authoring. Keep the main
architecture gate open pending that matrix and readiness review.

## Production-body custom broker uncertainty and terminal projection (2026-11-14)

A file/SQLite production-body test now sends a custom broker's explicit
`tool.outcome-unknown` through the shared run context. The matching request,
authorization, uncertainty, and root `run.outcome-unknown` events persist and
reopen; no later loop item or success event is recorded. This exposed that
`RunSnapshot` omitted the already-defined root uncertainty event from its
terminal projection, leaving both stores reporting `running`. Core now reports
the distinct `outcome-unknown` status and treats it as terminal, so a later
event append is rejected. Focused Core and production-body tests pass for both
stores. This covers an explicit uncertainty report only; raw custom-broker
exceptions, timeout behavior, full passive playback, and append-failure races
remain in the broader matrix.

The production-body matrix now also includes a raw custom-broker exception and
a rejected `tool.completed` store append. In both file and SQLite histories,
the root fails after the authorized dispatch, the next item is not admitted,
and no tool outcome is inferred. The custom exception text is not persisted.
The append-failure case retains the accepted authorization prefix while
leaving the external effect unresolved. The production-body timeout path is
also qualified against the real stdio `demo.slow` MCP server: two parallel
calls time out after authorization, correlated uncertainty settles their
reservations, the join is suppressed, and file/SQLite histories reopen with
terminal `outcome-unknown` status. Other append-failure points,
cancellation/failure playback, and the full passive run playback matrix remain
open.

The run inspector now gives terminal `outcome-unknown` a distinct warning:
review the recorded events before starting another run because the external
operation may already have taken effect. An HtmlRenderer regression verifies
that this status does not show the generic retry message. This improves
operator guidance but does not close the passive playback matrix.

A production-body file/SQLite regression now rejects the `model.responded`
append after the model request is accepted. The provider is called once, but
the missing response event fails the run; no MCP call or later activation is
recorded, and reopening either store yields the same failed event prefix. This
extends the production append-failure proof beyond direct MCP outcome writes;
other failure, race, and append boundaries remain open. This test-only proof
does not change the architecture overview or user-facing guide.

## Production-body append-failure terminalization review (2026-11-14)

The three-role review recommends a narrow best-effort terminal fallback for
the private production-body coordinator. Once root `run.started` has been
accepted, an exception before any root terminal append attempt triggers at
most one root `run.failed` append using the run context's last accepted
sequence. The original exception is rethrown even if the fallback append
fails. The coordinator does not retry the event whose append failed, dispatch
more work, or settle any outstanding external outcome from that failed append.
Root-start append failure does not trigger a terminal write; a root terminal
append failure is not retried. This makes the production-body failure prefix
match the structured proof's behavior where the store accepts the fallback.

File/SQLite tests cover a rejected `loop.iteration.started` with accepted
fallback, rejection of both that event and `run.failed` while preserving the
original error and running prefix, failed `run.started` without a terminal
event, and failed root `run.completed` without retry. Existing tests also cover
model-response and tool-completion append failures after dispatch. Built-in
store atomic batches and expected-sequence checks prevent the fallback from
extending a prefix whose prior event already committed; arbitrary custom
callbacks may still throw after committing, so no universal unchanged-prefix
claim is made. An additional callback test commits `loop.iteration.started`
before throwing to simulate a lost acknowledgment: the failure fallback uses
the stale accepted sequence, the store rejects it, the original callback
exception is rethrown, and reopening both stores retains the committed prefix
without a fabricated root outcome. Custom stores that violate the documented
atomic append and expected-sequence contract remain outside this guarantee.
A further file/SQLite case rejects `loop.iteration.completed` after the
Agent/MCP body completes. Its accepted `tool.completed` evidence remains in
the failed history, no later item is admitted, and the root failure is
reconstructed after reopen. The private proof change selects no iterator
architecture and adds no public schema or Studio authoring. Architecture
overview and user guide remain unaffected.

## Passive activation playback increment (2026-11-14)

The Run inspector now projects loop activation records from the event prefix at
the selected playback cursor. It pairs iteration start and completion by node,
scope, and invocation, reports Break only after the coordinator records the
accepted iteration outcome, and exposes exact-scope body events and whether a
declared parallel join has an event at that cursor. Missing or ambiguous
completion evidence stays incomplete. This view consumes the reopened snapshot
only and performs no node, model, or MCP execution.

HtmlRenderer integration tests run the production nested Break and successful
joined-body Break proofs, persist and reopen both file and SQLite histories,
and render those snapshots. They verify outer-loop continuation, the consumed
inner Break, the admitted sibling's completion, the absence of a join event,
and an event prefix that ends after Break dispatch but before coordinator
acceptance. This closes only that successful passive playback slice. Failure
and cancellation playback, uncertain outcomes, append-failure prefixes, and
public iterator/Break schema and Studio authoring remain open; the readiness
gate stays **REVISE**.

## Scoped failure and cancellation evidence in playback (2026-11-14)

Three-role review rejected inferring an iteration's Failed or Cancelled status
from node terminal events, even when they share the iteration scope. A parallel
branch can fail while a sibling is cancelled, recoverable work may emit a node
failure, and an event prefix can end before the coordinator records the whole
iteration outcome. Root and nested-scope events also cannot establish a
different activation's result.

For an activation without exactly one matching `loop.iteration.completed`, the
Run inspector therefore keeps the activation In progress or Incomplete. If
`node.failed` or `node.cancelled` exists at that exact activation scope, it
labels those events as recorded node evidence and explicitly says the
iteration outcome is not established. The event list remains the source of
node identity and event kind. This adds no runtime event or serialized
contract. Definitive failure, cancellation, and uncertain iteration outcomes,
append-failure prefixes, and public iterator/Break schema and Studio authoring
remain open; the architecture readiness gate stays **REVISE**.

## Multi-node direct MCP and mixed-path follow-up (2026-11-14)

The private production-body proof now exercises a two-node direct-MCP branch
and an Agent-to-MCP branch ending at the private Break while a sibling static
branch settles. The success cases prove the next superstep dispatches after
the sibling settles, the Break is consumed by the VAW coordinator, the join
does not execute, and no later array item is admitted. Tool failure, unknown
external outcome, and host cancellation each stop the MCP path before Break,
suppress the join, persist their terminal evidence, and reopen identically
from file and SQLite stores. HtmlRenderer playback verifies the persisted
success, failure, uncertain-tool, and cancelled-run projections. The focused
matrix passes 16/16 cases.

These tests use the production Agent Framework graph translation and MCP node
adapter with controlled executors/brokers. They do not invoke a real MCP server,
prove real external-service behavior, add a public iterator/Break contract, or
select the production iterator architecture. Static branch paths containing
other control flow, deterministic transforms, and the remaining shared frame,
lifecycle, and budget readiness criteria remain open. The architecture
overview and user guide are unchanged because this is private proof evidence,
not shipped workflow behavior.

## Passive playback for terminal and append-failure prefixes (2026-11-14)

The integrated production-body MCP failure, unknown-outcome, and cancellation
matrix now renders reopened file and SQLite snapshots in the Run inspector.
Failure is shown as an incomplete iteration with exact-scope node-failure
evidence and no inferred join; explicit coordinator outcomes retain their
`Outcome unknown` and `Cancelled` statuses, including correlated uncertainty.
The Run inspector now passes its selected terminal status to the loop playback
projection, verified by a regression that previously rendered a failed run's
iteration as in progress.

Store append-failure playback now covers rejected model and tool outcomes,
rejected iteration completion after successful body work, a committed
iteration-start event whose acknowledgment is lost, and rejected root
completion after an accepted iteration completion. Playback preserves the
recorded prefix: terminal run failure leaves the unmatched iteration
incomplete, an unterminal running prefix remains in progress, and an accepted
iteration completion remains completed even when the run terminal append is
rejected. These focused tests run against file and SQLite stores. Other append
failure positions, process-loss recovery, public iterator/Break authoring, and
the architecture readiness review remain open; this evidence does not select
an iterator candidate or change the **REVISE** decision.

The real stdio `demo.slow` timeout path is also rendered for the production-body
candidate after reopening both stores. The inspector retains the terminal run
status and correlated `tool.uncertain` evidence, reports the iteration outcome
from its recorded completion (or leaves it incomplete when absent), and shows
that the parallel join has no event. The focused timeout theory passes for
both file and SQLite. This adds playback evidence for the real adapter path;
it does not close other append/failure cases or architecture selection.

## Three-role readiness re-review (2026-11-15)

An independent proponent, opponent, and adjudicator review returned
**REVISE**. The VAW-owned coordinator remains a viable candidate, but the
review did not select it or authorize public typed array pins/Break authoring.
Confirmed evidence includes private frame ownership, root lifecycle and
budget-ledger proofs, source-only nearest-frame Break consumption, production
Agent Framework dispatch, selected Agent/MCP fork shapes, file/SQLite
reopening, and the newly qualified terminal, uncertainty, cancellation, and
append-prefix playback cases.

The reviewers found the proof matrix still narrower than the supported body
contract: candidate comparisons and private coordinator proofs do not yet
cover the same breadth of control flow, the full deterministic-node family,
failure and append boundaries, or every shared-budget settlement path. A
post-review source audit confirmed existing coordinator coverage for
Variable Set/Get, TextConcat, Uppercase, Agent, and MCP across two activations
in both stores (`ProductionBodyActivationCoordinator_PersistsOneScopedRunLedger`,
2/2 focused cases); the gap is the remaining deterministic node families and
their compositions, not all transforms. WorkflowValue parameter dataflow also
does not establish authored iterator-pin serialization or Studio
compatibility. Checkpoint recovery remains excluded until activation frames
can be captured and restored. Continue proof-only integration, preserve the
existing predicate-based string Loop, and run another three-role review only
after the remaining matrix is complete.

## Conditional-body composition on the private coordinator (2026-11-15)

`ProductionBodyActivationCoordinator_RoutesConditionalItemsThroughExclusiveMerge`
now sends three values through a current-schema Boolean Branch, mutually
exclusive Agent paths, and a Merge using the private VAW coordinator and
production body dispatcher. It verifies selected ports, exact iteration
scopes, merged outputs, one root lifecycle, and file/SQLite reopen (2/2 focused
cases). This closes conditional body composition for this path only. It does
not select an architecture, establish all supported-body parity, or authorize
public iterator/Break schema and Studio authoring.

`ProductionBodyActivationCoordinator_ExecutesSequencePerItem` adds one
current-schema Sequence body to the integrated private path. Two MCP outputs
dispatch and complete in each of two activations; scoped Sequence/tool/loop
evidence and the root lifecycle reopen through file and SQLite (2/2 focused
cases). `ProductionBodyActivationCoordinator_SequenceFailureCancelsSiblingAndStopsLaterItems`
then fails one output after its sibling has dispatched. Both stores retain the
failed output, cancelled sibling, uncertain in-flight MCP outcome, failed
iteration attribution, failed root status, and no later item admission (2/2
focused cases). The reopened snapshots also render through the Run inspector as
an incomplete iteration rather than implying completion. These tests verify
bounded Sequence composition and one failure shape only; the full supported-body
matrix, architecture selection, and public iterator authoring remain open.

`ProductionBodyActivationCoordinator_EvaluatesGraphFunctionPerItem` adds a
current-schema pure graph function to the integrated body path. The function
receives each item's value, returns the expected transformed output, and
records its evaluated input/output evidence in the matching activation scope;
the one-root history reopens from file and SQLite (2/2 focused cases). This
qualifies successful graph-function composition only; function failures,
shared-budget interaction, the broader body matrix, architecture selection,
and public iterator authoring remain open.

`ProductionBodyActivationCoordinator_AttributesGraphFunctionFailureToCall`
fails a pinned pure function's `ArrayGet` on the second typed item. The event
uses the visible outer FunctionCall node ID and includes the function display
name, internal failing node ID, and deterministic failure reason. The Output
consumer also records its failure; the run stops before a third item and
file/SQLite histories reopen identically (2/2 focused cases). This qualifies
one function failure shape only; broader function failure and budget coverage
remain open.

`ProductionBodyActivationCoordinator_ScopesGraphFunctionArrayBudgetPerActivation`
evaluates four outputs backed by a roughly 5 KB typed array in each of two
production-body activations. The retained evidence stays below the 64 KiB
aggregate limit within each activation, while the combined history exceeds that
limit; both file and SQLite runs complete, persist four call-output evaluations
per scope, and reopen identically (2/2 focused cases). This verifies that the
graph-function evidence budget is keyed to the activation frame rather than
accumulated across the root run. It does not cover other budget settlement
paths, the broader supported-body matrix, architecture selection, or public
iterator authoring.

`ProductionBodyActivationCoordinator_RejectsGraphFunctionArrayBudgetOverflow`
returns five outputs backed by the same roughly 5 KB typed array in one
activation. The 65,536-byte aggregate evidence budget rejects the fifth result;
the internal `FunctionInput` boundary and safe limit reason are recorded on the
visible FunctionCall failure, its Output consumer fails, and the second array
item is not admitted. The failed event prefix persists and reopens identically
from file and SQLite (2/2 focused cases). This verifies one budget-exhaustion
failure shape; it does not close other budget settlement paths or the broader
body matrix.

`ProductionBodyActivationCoordinator_EvaluatesGraphFunctionsInParallelPerItem`
composes two pinned pure FunctionCall nodes with the separate Agent branches
of a current-schema static fork. Across two typed string items, each function
receives the correct item, records its evaluated output in the matching scope,
and feeds the expected Agent result to the completed join. File and SQLite
histories reopen identically (2/2 focused cases). This proves one parallel
function-to-Agent composition; function failure races and broader fork/body
coverage remain open.

`ProductionBodyActivationCoordinator_BindsTypedIntegerItemAndIndex` exercises
the Core parameter-binding seam through the same coordinator and production
Agent dispatcher. It uses `long.MinValue`, zero, and `long.MaxValue`, verifies
each typed item is paired with its zero-based integer index at the Agent, and
checks the scoped `data.evaluated` records and completed root after reopening
both stores (2/2 focused cases). This does not add serialized iterator pins or
Studio authoring; those remain behind the architecture review.

`ProductionBodyActivationCoordinator_RoutesTypedBooleanItemsThroughBranch`
proves a second typed item shape at the integrated boundary. Boolean items
flow directly from the coordinator-bound typed workflow parameter into
Boolean Branch, select only the matching Agent path, pass through the Merge,
and produce activation-scoped branch evidence in file/SQLite histories (2/2
focused cases). This extends the prior string-valued condition and integer
item/index proofs; it does not establish every element type, branch-failure
shape, or the remaining body matrix.

`ProductionBodyActivationCoordinator_EvaluatesTypedNumberItems` adds finite
double items to the integrated typed binding matrix. Each item flows through a
`number` WorkflowValue and `NumberAdd` before Output; its exact expected sum
and scoped input/output evaluations persist through file and SQLite (2/2
focused cases). `ProductionBodyActivationCoordinator_AttributesTypedNumberOverflowAndStopsLaterItems`
adds finite `double.MaxValue` inputs whose sum overflows to a non-finite result.
The failure is attributed to NumberAdd and its Output consumer, stops later
items, and reopens as the same failed file/SQLite history (2/2 focused cases).
Other numeric compositions and failure cases remain open.

`ProductionBodyActivationCoordinator_ComposesTypedIntegerArithmeticAndConditionalRouting`
adds typed signed-integer items composed through `IntegerAdd`, `IntegerEquals`,
Boolean Branch, mutually exclusive Agent paths, and Merge. It verifies input
and arithmetic evaluation values, branch choices, merged output, and reopened
iteration histories in file and SQLite stores (2/2 focused cases). This covers
one deterministic integer composition. The separately recorded overflow case
below covers one checked-add failure boundary; other deterministic failures,
the remaining body matrix, architecture selection, and public iterator
authoring remain open.

`ProductionBodyActivationCoordinator_AttributesTypedIntegerOverflowAndStopsLaterItems`
exercises checked `IntegerAdd` overflow with `long.MaxValue + 1`. The runtime
attributes the failure to the arithmetic node and its consuming Branch, closes
the root as failed, admits no later item, and preserves the same contiguous
event history after reopening file and SQLite stores (2/2 focused cases). This
does not cover other deterministic-node failures or select the iterator
architecture.

`ProductionBodyActivationCoordinator_ComposesStringIntegerConversionPerItem`
passes typed string items through strict `StringToInteger`, checked
`IntegerAdd`, invariant `IntegerToString`, and Output. It verifies the parsed,
arithmetic, and formatted values in each activation scope and reopens the
completed history from both stores (2/2 focused cases).
`ProductionBodyActivationCoordinator_AttributesInvalidIntegerTextAndStopsLaterItems`
uses malformed text to verify stable parser code/reason/length evidence at the
conversion node, root failure, no iteration completion, and suppression of the
next item, again through file and SQLite (2/2). This covers one conversion
composition and malformed-text boundary; the broader supported-body and
architecture gates remain.

`ProductionBodyActivationCoordinator_ComposesBooleanNotWithTypedItemsAndBranch`
composes coordinator-bound Boolean items through `BooleanNot`, Boolean Branch,
exclusive Agent paths, and Merge. The test verifies each negated scoped value,
selected route, merged output, and reopened file/SQLite history (2/2 focused
cases), while retaining the separate direct typed-item-to-Branch proof. Other
deterministic body compositions and the architecture review remain open.

`ProductionBodyActivationCoordinator_SelectsJsonFieldsFromTypedStringItems`
uses typed string array items containing JSON objects. A JSON-typed body Input
parses each value, `JsonSelectString` selects `/name`, and Output returns the
selected strings. The proof verifies the original JSON and selected string in
each activation scope plus file/SQLite reopen (2/2 focused cases). It does not
cover JSON projection, merge, malformed input, or the broader supported-body
matrix.

`ProductionBodyActivationCoordinator_ProjectsTypedStringItemJsonBeforeSelection`
now composes `JsonProject` with `JsonSelectString` for each typed string item.
The proof checks that declared field types are enforced, unselected fields are
dropped, and projected values and final output survive file/SQLite reopen
(2/2). `ProductionBodyActivationCoordinator_AttributesJsonProjectionFailureAndStopsLaterItems`
also proves a projection type mismatch is attributed to `project` and its
consumer, fails the root, and prevents admission of the next item (2/2).
Pure data evaluation now wraps projection contract errors with the originating
node ID, matching the explicit-node failure evidence. JSON merge, malformed
JSON, and the broader body matrix were still unproven at this increment; later
proofs record JSON merge and input/resource failure coverage. This is still
proof-only and does not settle the iterator architecture or public authoring
contract.

`ProductionBodyActivationCoordinator_MergesProjectedJsonPerItem` extends that
composition through two disjoint `JsonProject` nodes, `JsonMerge`, and
`JsonSelectString`. Merged fields and final outputs persist through file and
SQLite (2/2). `ProductionBodyActivationCoordinator_AttributesJsonMergeConflictAndStopsLaterItems`
proves duplicate-key failure is attributed to the merge and its consumer,
closes the root as failed, and admits no later item (2/2). The pure evaluator
now wraps JSON merge contract errors with the originating node ID, matching
projection errors. Malformed JSON and other bounds/failure shapes remained
open at this stage and are documented in the later JSON boundary proof.

`ProductionBodyActivationCoordinator_ConvertsNestedJsonArraysAndCountsItems`
converts each string item containing JSON into bounded `array<json>`, counts
its elements with `ArrayLength`, and formats the count. Non-empty and empty
arrays produce scoped evidence and output through both stores (2/2).
`ProductionBodyActivationCoordinator_AttributesNestedJsonArrayRootFailure`
rejects a valid non-array JSON root at `JsonToArray`, attributes the consumer
failure to Output, fails the root, and admits no later item (2/2). Malformed
JSON and configured depth/item-limit boundaries remained unproven at this
stage; see the later boundary proof.

`ProductionBodyActivationCoordinator_SelectsNestedJsonArrayItemPerActivation`
composes `JsonToArray`, a fixed zero-based `ArrayGet`, and `JsonSelectString`.
Changing arrays return their second item's name with scoped data evidence and
file/SQLite reopen (2/2). `ProductionBodyActivationCoordinator_AttributesNestedJsonArrayIndexFailure`
uses an empty first array to prove the out-of-range error is attributed to
ArrayGet and its consumer, fails the root, and stops before the next item (2/2).
Negative indices and other element kinds remain unproven in this seam.

`ProductionBodyActivationCoordinator_RendersTextTemplateFromItemAndIndex`
renders one explicit template from the current string item and the typed index
parameter over three activations. Each output and scoped evaluation matches,
and file/SQLite histories reopen identically (2/2). This verifies this template
part combination only; the coordinator architecture, broader body matrix, and
public iterator authoring remain under review.

`ProductionBodyActivationCoordinator_IndexesTypedArrayLiteralWithEachIntegerItem`
creates an immutable `array<string>` with `ArrayLiteral` and uses each typed
integer item as the index for `ArrayGet`. Per-item output and data evidence
reopen through file and SQLite stores (2/2). An out-of-range item fails at
`ArrayGet`, records the Output consumer failure, and blocks later activation
(2/2). Other literal element kinds and mutable-array behavior remain outside
this proof.

`ProductionBodyActivationCoordinator_IndexesEveryPrimitiveArrayLiteralType`
extends the `ArrayLiteral`/`ArrayGet` proof to Boolean, integer, and finite
number arrays. The changing integer items select typed values, and outputs plus
scoped data evidence reopen from file and SQLite (6 theory cases). Together
with the string literal proof above, this covers all four currently supported
primitive array literal element types in this composition. It does not establish
mutable collections or the full body/architecture contract.

`ProductionBodyActivationCoordinator_AttributesMalformedNestedJsonInput`
records a specific JSON parse failure on the JSON Input node for malformed
nested JSON content. The runtime suppresses a duplicate generic consumer error
when source and consumer are the same node. The item-count-limit case attributes
overflow to `JsonToArray`; excessive depth is rejected earlier by the JSON
Input boundary, which enforces the maximum-depth guard. All three failures stop
before a later activation and persist/reopen identically in file and SQLite
stores (6 focused cases). These boundary proofs do not imply that the complete
body matrix or public iterator contract is closed.

`ProductionBodyActivationCoordinator_ReversesUnicodeStringItems` exercises
the pure `Reverse` node over three changing inputs, including an accented
character, a supplementary Unicode rune, and empty text. Reversed values and
scoped evaluation evidence persist through both stores (2/2). This adds one
deterministic transform family to the body matrix; it does not close the full
matrix or alter the architecture review gate.

`ProductionBodyActivationCoordinator_EnforcesSharedActivationLimit` now
exercises the run-context activation cap through the integrated path. After the
first item's Agent and MCP work completes, a one-activation budget rejects
admission of the second item before it emits scope events or dispatches work.
The failed prefix reopens through both stores, and passive playback preserves
the first iteration as completed without inventing a second (2/2 focused
cases). Model/tool limits are covered by adjacent coordinator tests; aggregate
event/byte and descendant-work limits remain separate proof obligations.

`ProductionBodyActivationCoordinator_JoinsParallelAgentsPerItem` covers the
normal static-fork path through the integrated coordinator. Two Agent branches
receive each item's input, the join event preserves the correct left/right
results under that iteration scope, and reopened file/SQLite snapshots render
both joins as completed (2/2 focused cases). This closes only successful
two-Agent fork/join composition; fork failure, cancellation, other branch body
types, and the full supported-body matrix remain open.

`ProductionBodyActivationCoordinator_ParallelAgentFailureSuppressesJoinAndLaterItems`
adds the ordinary failure counterpart to the integrated parallel-Agent
cancellation case. One branch fails after the sibling has entered; the
sibling's matching invocation is cancelled, no join result or next activation
is admitted, and file/SQLite reopen preserves the failed root event prefix
(2 focused store cases). Passive playback shows the failed run without a
completed join. This covers one synchronized Agent failure boundary only;
failure races and other branch body combinations remain open.

`ProductionBodyActivationCoordinator_ComposesAgentAndProductionMcpPerItem`
composes a deterministic Agent branch with a real local stdio MCP branch in
the production-body fork for two array items. Each item produces the expected
Agent result and uppercase MCP value, correlated authorization/completion
evidence, and a completed join. File and SQLite reopen preserve both joins and
passive playback renders them (2 focused store cases). This proves one mixed
success path; mixed failure/cancellation, other branch combinations, and the
supported-body matrix remain open.

`ProductionBodyActivationCoordinator_MixedAgentMcpFailureCancelsSiblingAndStopsLaterItems`
adds the controlled failure counterpart. The MCP branch emits correlated
authorization/failure evidence only after the Agent sibling has entered and
blocked. The coordinator cancels that matching Agent invocation, suppresses
the join and next item, and file/SQLite reopen preserves the contiguous failed
prefix and passive playback result (2 focused store cases). This composes the
production body dispatcher with mixed branch lifecycle settlement; the failing
MCP behavior is supplied by a deterministic test broker, so real-server mixed
failure and the broader cancellation/race matrix remain unproven.

`ProductionBodyActivationCoordinator_MixedAgentMcpHostCancellationSettlesBranches`
adds the synchronized host-cancellation case. After both branches enter and
the MCP call is dispatched, cancellation settles both matching node
invocations, records the MCP's correlated unknown outcome, records a cancelled
iteration, and suppresses the join and next item. File and SQLite reopen
preserve the same cancelled prefix (2 focused store cases). The broker is
deterministic and test-only; this does not qualify remote server cancellation
or resolve the broader cancellation/publication race matrix.

`ProductionBodyActivationCoordinator_MixedAgentFailurePreservesMcpUnknownOutcome`
fails the Agent after its sibling MCP call is dispatched. The Agent failure
settles the root as `failed`; the sibling retains correlated
`tool.outcome-unknown` plus node-failure evidence. Playback keeps the run
failure distinct from the uncertain external side effect, marks the iteration
incomplete, and shows no completed join or later item. File and SQLite reopen
preserve the same prefix (2 focused store cases). The expanded matrix also
rejects the `tool.uncertain` append in both stores: playback reports that an
authorized call has no recorded outcome and may have an external effect, rather
than presenting the rejected event as saved. The accepted/rejected matrix
passes 4/4 cases. This controlled broker case does not qualify real-server
failure races.

## Production-broker sibling cancellation after MCP protocol failure (2026-11-15)

`ProductionBodyActivationCoordinator_ProductionMcpFailureCancelsSiblingAndPreservesEvidence`
runs `demo.protocol-failure` and `demo.slow` as parallel branches through the
real stdio `McpToolBroker`, with both broker connections synchronized before
dispatch. It verifies both default post-dispatch uncertainty and host
classification of the protocol exception as a definitive tool failure. Both
calls persist authorization; the canceled slow sibling retains its correlated
`tool.uncertain` outcome in either case. The failed call's `tool.uncertain` or
`tool.failed` event precedes sibling uncertainty. The join is suppressed and
the next item is not admitted. The root settles as `outcome-unknown` under the
default policy and `failed` under the host classifier. Only the
unknown-outcome case emits an iteration terminal event; the definitive failure
remains an incomplete iteration. File and SQLite reopen with identical event
sequences and passive playback retains the evidence (4/4 focused cases).

This qualifies one production-broker protocol-failure cancellation path and
one host classification policy. It does not prove that the remote fixture
processed the canceled request, select the iterator architecture, or add
public iterator schema/Studio authoring. The broader append-failure, race,
recovery, and supported-body matrices remain open.

## Mixed Agent fork with production MCP protocol failure (2026-11-16)

`ProductionBodyActivationCoordinator_ProductionMcpFailureSettlesMixedAgentFork`
puts the pinned local stdio `demo.protocol-failure` MCP tool in one fork branch
and a blocking Agent in the other. The MCP connection waits until the Agent
has entered, making sibling cancellation deterministic. Across file and
SQLite stores, the default policy records correlated authorization and
`tool.uncertain`, then settles the root as `outcome-unknown`; when the host
classifies the protocol exception as definitive, the same path records
`tool.failed` and settles the root as `failed`. In both policies, the matching
Agent invocation is cancelled, the join and second item are suppressed, and
reopened playback retains the evidence (4/4 focused cases).

This proves one local production-broker protocol-failure path and two host
classification outcomes. It does not qualify arbitrary MCP servers, prove
whether a remote server applied a request before reporting a protocol error,
select the iterator architecture, or close the general race, recovery, and
append-failure matrices.

`ProductionBodyActivationCoordinator_ProductionMcpHostCancellationSettlesMixedAgentFork`
adds the adjacent host-cancellation case. Its `demo.slow` stdio connection is
held until the Agent sibling has entered; cancellation is then requested after
the MCP authorization event is accepted. Both the real broker invocation and
the Agent settle as cancelled, the dispatched MCP retains call-correlated
`tool.uncertain` evidence, and the join and following item are suppressed.
File and SQLite histories reopen with the same cancelled status and playback
projection (2/2 focused cases). This proves one deterministic local fixture
boundary; it does not prove whether arbitrary remote tools applied side
effects before cancellation or resolve the broader cancellation/race matrix.

`ProductionBodyActivationCoordinator_AgentFailureSettlesProductionMcpUnknownOutcome`
tests the reverse failure direction with the production stdio broker. The
blocked Agent fails after the `demo.slow` tool's `tool.authorized` event is
accepted. The root and iteration remain failed/incomplete, while the MCP call
retains matching authorization and `tool.uncertain` evidence; the join and
second item are absent after file/SQLite reopen and playback (2/2 focused
cases). This relies on the broker's explicit post-authorization uncertainty
contract and does not claim the fixture completed the external operation or
qualify arbitrary-server side effects.

The same production-broker test now rejects the sibling's `tool.uncertain`
append in both stores under both root-outcome policies (8/8 focused matrix
cases). The append attempt is correlated to the accepted authorization, but
the rejected uncertainty and subsequent `node.cancelled` are absent from the
persisted event prefix. Reopened playback retains the authorization and does
not invent a tool terminal event. This records a narrow failed-evidence-write
prefix; it does not establish recovery or a general append-failure policy.

## Actionable scoped MCP outcomes in loop playback (2026-11-15)

Passive loop playback now correlates authorized MCP call IDs with completion,
failure, denial, or uncertainty events in the exact iteration scope. An
unmatched authorization is described as an unrecorded outcome whose external
effect may be unknown. Persisted `tool.failed` and `tool.outcome-unknown`
records surface their safe `reason` payload. The production-broker sibling
cancellation matrix renders the explanation from reopened file and SQLite
histories, including the rejected uncertainty append (8/8 cases). The view
does not infer completion, retry the call, or establish process recovery.

## Authorization evidence append failure in the integrated body proof (2026-11-15)

`ProductionBodyActivationCoordinator_AuthorizationAppendFailurePreventsToolDispatch`
rejects the `tool.authorized` append at the file and SQLite store boundaries
(2 focused cases). The already-running body Agent may complete, but the direct
MCP broker is not invoked. The tool node and root fail; no authorization,
completion, failure, or uncertain tool outcome is persisted or inferred. The
reopened event prefix retains the iteration as incomplete and does not claim a
join or iteration completion.

This qualifies one pre-dispatch evidence-write failure only. It does not
establish a general append-failure policy, crash recovery, or select the
iterator architecture; the Decision 0070 readiness gate remains **REVISE**.

`ProductionBodyActivationCoordinator_ModelRequestAppendFailurePreventsProviderDispatch`
rejects `model.requested` before the configured provider receives a request.
The Agent node and root fail, no later tool call or iteration completion is
recorded, and file/SQLite playback reopens with an incomplete iteration (2
focused cases). This covers the pre-provider boundary only; model response,
MCP authorization, and terminal append failures remain distinct boundaries.
The architecture decision remains **REVISE**.

`ProductionBodyActivationCoordinator_ToolRequestAppendFailurePreventsMcpConnection`
uses the production stdio `McpToolBroker` and rejects `tool.requested` at the
run-store append boundary. In both file and SQLite cases no MCP connection is
opened; the tool and root fail and the reopened iteration remains incomplete
(2 focused cases). This establishes the earliest MCP dispatch-evidence
boundary for the integrated proof, but does not change the architecture
decision or qualify recovery after a persisted request.

## Boolean constant body composition (2026-11-15)

`ProductionBodyActivationCoordinator_EvaluatesBooleanConstantForEachItem`
composes a literal Boolean constant with NOT, Boolean Branch, mutually
exclusive Agent paths, and Merge over three activations. Each selected path
and evaluated value is scoped to its activation, and file/SQLite histories
reopen identically (2 focused cases). This closes one deterministic-node
composition in the private body matrix only; the supported-body matrix remains
incomplete and the architecture decision remains **REVISE**.

## Production WorkflowCall body composition (2026-11-15)

`ProductionBodyActivationCoordinator_CallsPinnedWorkflowPerItem` runs an
immutable pinned child workflow once for each of three array items through the
production body coordinator. Each child receives and returns its item's value;
start/completion and child lifecycle evidence are scoped beneath the matching
activation. File and SQLite run histories reopen identically (2 focused
cases). This proves only the successful WorkflowCall body path. Child failure,
cancellation, the remaining supported-body matrix, and the architecture
decision remain open; readiness remains **REVISE**.

`ProductionBodyActivationCoordinator_StopsAtFailedPinnedWorkflow` forces a
child's typed string-to-integer conversion to fail during the first item. The
child's exact-scope failure is recorded, the root fails, the next item is not
admitted, and both file and SQLite histories reopen identically (2 focused
cases). The broader body/architecture gates remain open; readiness remains
**REVISE**.

The same production pinned-child proof now cancels the host immediately after
the child starts. The child and root each record cancellation, the activation
is marked cancelled, no later item starts, and file/SQLite playback reopens
with the same prefix (2 focused cases). This surfaced a playback validation
defect: legitimate activation-prefixed call scopes such as
`array-loop/0/call` were rejected because the call node ID was only accepted at
the start of the scope. `WorkflowCallLifecycleEvidence.TryRead` now also
accepts the call node ID as the final scope segment, while retaining exact
event/payload identity checks and rejecting unrelated final segments. The
broader body matrix and architecture decision remain open; readiness remains
**REVISE**.

## Production WorkflowInterfaceCall body composition (2026-11-15)

`ProductionBodyActivationCoordinator_CallsPinnedWorkflowInterfacePerItem`
invokes a pinned interface implementation for three array items. Each item is
packed through a typed interface field into the child's JSON input, projected
through the typed output field, and returned in activation order. Child
lifecycle evidence uses the activation-prefixed scope and reconstructs as three
completed calls after both file and SQLite histories reopen (2 focused cases).
This proves only successful interface calls in the production body; interface
failure/cancellation and the broader body/architecture gates remain open.
Readiness remains **REVISE**.

The production interface-call proof now also covers two terminal paths. A
child JSON projection failure is recorded as a failed call, stops later item
admission, and reopens from both stores. Host cancellation after child start
records a cancelled call and root, stops later admission, and reopens from both
stores (4 focused cases total). These tests cover ordinary child failure and
host cancellation only; external uncertainty, append races, and the broader
body/architecture gates remain open. Readiness remains **REVISE**.

## Definitive tool-failure evidence append rejection (2026-11-16)

`ProductionBodyActivationCoordinator_ToolFailureAppendFailurePreservesAuthorizedPrefix`
uses a deterministic custom broker that records `tool.requested` and
`tool.authorized`, then attempts to record a definitive `tool.failed` event.
The file and SQLite stores reject that outcome append. The coordinator does
not retry the dispatched call or admit the next array item; the accepted
prefix ends with root failure and retains the authorization without claiming
the tool outcome. Reopened playback reports that the external effect may be
unknown and keeps the activation incomplete (2/2 focused cases).
The full Agent Framework test project passes 651/651 with this proof included.

This qualifies one post-dispatch failure-evidence persistence boundary. It
does not provide recovery or establish the iterator architecture; readiness
remains **REVISE**. The existing TextTemplate case also exercises every
supported part kind (`literal`, connected `input`, and typed `parameter`);
only broader template/body composition remains open.

## Predicate-based string Loop inside typed-array activations (2026-11-16)

`ProductionBodyActivationCoordinator_ComposesPredicateLoopInsideEachArrayActivation`
places the existing schema-2.0 predicate `Loop` and its `LoopBody` inside the
private typed-array body. Each of two string items runs its bounded
two-iteration Agent path independently. The test checks the item-specific
inputs, outer and inner iteration lifecycle counts, inner stop reason and
activation scopes, and rendered playback after reopening both file and SQLite
stores (2/2 focused cases).

This demonstrates composition with the existing predicate loop while keeping
its contract separate from array iteration. It does not add nested-loop
authoring or alter the architecture review outcome; readiness remains
**REVISE**.

## Approval-gated MCP dispatch inside typed-array activations (2026-11-16)

`ProductionBodyActivationCoordinator_ApprovesExactMcpActionPerArrayItem`
places an explicit `Approval` node before a direct MCP node in the private
typed-array body. A deterministic test gate captures each exact action and
matching permit. For two array items, the proof checks distinct approval
request IDs, action hashes, target invocation IDs, MCP inputs, and matching
`tool.authorized` evidence under the corresponding item scope. Both file and
SQLite histories reopen and passive playback retains the expected result
(2/2 focused cases).

This proves approval-node dispatch composition and scoped persisted evidence
through the production coordinator. The test gate and broker do not qualify
host approval-request persistence, real user decisions, or permission-policy
behavior. It does not alter the iterator architecture review; readiness
remains **REVISE**.

## Bounded predicate Loop composition across iterator candidates (2026-11-16)

`IteratorCandidates_ExecutePredicateLoopBodyPerActivation` runs the existing
bounded predicate Loop body for two changing string items using the structured
dispatcher, a fresh Agent Framework subworkflow per item, and the hybrid
candidate. Every candidate performs the same two Agent passes per item, stops
at the configured maximum, and produces matching output, Agent inputs, and
scoped Loop/Agent lifecycle shapes. Each of the six candidate/store histories
passes persisted-prefix validation and reopens identically through file and
SQLite (2 focused theory cases). This closes the tested Loop-composition
comparison only; it neither authors a typed iterator nor selects a production
scheduler. Architecture readiness remains **REVISE**.

`IteratorCandidates_StopPredicateLoopAfterAgentFailureAcrossCandidates` adds
the second-pass failure case for the same three candidates. Every candidate
records one completed Loop pass, the same second-pass Agent input and scoped
`node.failed` event, fails the root without starting the next array item or
recording `LoopStopped`, and reopens identically for file and SQLite stores (6
candidate/store runs; 2 focused theory cases). This closes only this tested
failure boundary; it does not establish general failure/cancellation
equivalence or alter the architecture decision.

`IteratorCandidates_CancelPredicateLoopDuringSecondAgentPassAcrossCandidates`
then cancels while the second body Agent call is blocked. All candidates record
two scoped Loop starts, only the first completed pass, the matching Agent
invocation's `node.cancelled` event, one cancelled root, and no later array
activation. The six file/SQLite histories reopen identically (2 focused theory
cases). This covers this deterministic cancellation point only; it does not
establish cancellation-race parity elsewhere or change the architecture
decision.

`IteratorCandidates_AttributeJsonArrayOperationFailureAcrossCandidates`
injects an invalid index into Array Get, Insert, Replace, and Remove in the
existing typed JSON-array transform body. Structured dispatch, fresh
subworkflow, and hybrid record the same `node.failed` event under the first
item scope for each operation, suppress the Variable Set/write, iteration
completion, and next item, and reopen identical failed histories through both
stores (24 candidate/store executions; 8 focused theory cases). This qualifies
these four deterministic data-operation failures only; it does not establish
the full deterministic-node failure matrix or alter the architecture
decision.

`IteratorCandidates_AttributeTypedArrayOperationFailureAcrossCandidates`
injects invalid indices into Array Get, Insert, Replace, and Remove in the
typed string-array variable body. All candidates record the same scoped
failure, suppress the write and item completion, stop before the next item,
and reopen identical file/SQLite histories (24 candidate/store executions; 8
focused theory cases). This extends the four JSON-array operation failures to
a non-JSON type; other element types and failure modes remain open.

`IteratorCandidates_AttributeJsonArrayAppendOverflowAcrossCandidates`
initializes the activation variable just below the typed-array payload limit
and appends the current JSON item so the result exceeds 16 KiB. All three
candidates attribute `node.failed` to Append, suppress the variable write,
iteration completion, and next item, and reopen identical file/SQLite
histories (6 candidate/store runs; 2 focused theory cases). This covers one
payload-bound failure only; shared-budget exhaustion and recorder/store failure
remain distinct.

The `append-overflow` variant of
`IteratorCandidates_AttributeTypedArrayOperationFailureAcrossCandidates`
repeats this bound with an `array<string>` variable and a typed string item.
Structured dispatch, fresh subworkflow, and hybrid match the Append failure,
stop before writing or activating the next item, and reopen identical
file/SQLite histories (6 candidate/store runs; 2 focused theory cases). This
extends the payload-bound result beyond JSON while leaving other typed element
types and shared-budget/store-failure behavior open.

After the graph-function failure, expanded typed-operation, JSON and typed
string Append-overflow, WorkflowInterfaceCall child-failure/cancellation, and
WorkflowCall child-failure comparisons were added, the complete Agent Framework
suite passed 765/765 (0 failed or skipped; 6m17s). This is broad regression
evidence for that suite snapshot, not completion of the supported-body,
lifecycle/budget, playback, or architecture-review gates.

`IteratorCandidates_AttributeGraphFunctionFailureAcrossCandidates` runs the
same pinned `Lookup` function that fails on the second integer item because its
internal Array Get index is out of range. Structured dispatch, fresh
subworkflow, and hybrid all attribute failure to the outer FunctionCall while
retaining the internal node and bounded reason, keep the first item completed,
and suppress the third item. Their file/SQLite histories reopen identically
(6 candidate/store runs; 2 focused theory cases). This covers one pure
graph-function failure only; other function failures and budget interactions
remain open.

## Three-candidate WorkflowInterfaceCall output comparison (2026-11-16)

`IteratorCandidates_ScopeWorkflowInterfaceCallOutputsAcrossAllCandidates`
compares a pinned interface call across three changing string items in the
structured dispatcher, fresh Agent Framework subworkflow, and hybrid
candidates. Each returns the same output and `data.evaluated` values, and
file/SQLite histories reopen identically. Passive playback reconstructs all
three completed interface calls for each candidate/store run (6 candidate/
store executions). This comparison exposed that the fresh-subworkflow and
hybrid proof adapters flattened child scopes while leaving embedded lifecycle
evidence at the child-local scope. Both adapters now prefix nested event scopes
and update the corresponding lifecycle evidence identity before persistence.
The fix is confined to proof harnesses; it does not change production
WorkflowCall behavior or select an iterator architecture. Broader output-cache
families, failure/race comparisons, and shared-budget parity remain open.
Readiness remains **REVISE**.

`IteratorCandidates_AttributeWorkflowInterfaceChildFailureAcrossCandidates`
extends that comparison to a child projection failure. Structured dispatch,
fresh subworkflow, and hybrid all retain the missing-field `node.failed` under
the first item scope, record the failed interface call, and suppress the next
activation. Each candidate's file and SQLite history reopens identically and
reconstructs one failed call through passive playback (6 candidate/store runs;
2 focused theory cases). This adds one persisted child-failure boundary; other
interface-call race shapes and the architecture gate remain open.

`IteratorCandidates_AttributeWorkflowCallChildFailureAcrossCandidates`
compares a pinned child whose string-to-integer conversion fails on the first
item. All three candidates retain the child `parse` failure under
`array-loop/0/call`, suppress iteration completion and the next item, and
reopen identical file/SQLite histories. Passive playback reconstructs one
failed child call in each candidate/store run (6 candidate/store runs; 2
focused theory cases). This adds one regular WorkflowCall failure boundary;
other child-call races, shared-budget parity, and the architecture gate remain
open.

`IteratorCandidates_CancelWorkflowInterfaceChildAcrossCandidates` blocks an
Agent inside the pinned child and cancels the host while that child is active.
Structured dispatch, fresh subworkflow, and hybrid record the matching
cancelled child lifecycle, omit item completion and later item admission, and
reopen identical file/SQLite histories. Passive playback reconstructs one
cancelled interface call in every candidate/store run (6 candidate/store
runs; 2 focused theory cases). This closes only this child-cancellation point;
other cancellation races, shared-budget parity, and the architecture gate
remain open.

`ProductionBodyActivationCoordinator_CancellationWhileAwaitingItemApprovalStopsRun`
blocks on the explicit approval gate for the first array item, then cancels
the host token. The production body coordinator records the node and run
cancellation, does not dispatch the MCP target or admit the next item, and
reopens through file and SQLite with a rendered `Cancelled` iteration (2/2
focused cases). The gate is deterministic and does not exercise
`ControlledRun`, persisted approval requests/decisions, or real user approval.
The iterator architecture status remains **REVISE**.

`ProductionBodyActivationCoordinator_RejectedItemApprovalStopsBeforeMcpDispatch`
uses a deterministic gate that rejects the exact first-item request with
`WorkflowApprovalRejectedException`. The approval node and root fail; neither
the MCP target nor the next item is dispatched, and the failed prefix reopens
from file and SQLite (2/2 focused cases). The fake gate does not persist the
host's `ApprovalRejected` decision event; this qualifies body failure handling
only. Readiness remains **REVISE**.

## Typed mutable-array state inside activations (2026-11-16)

`ProductionBodyActivationCoordinator_IsolatesTypedArrayVariableStatePerItem`
uses the existing typed `VariableGet`, `ArrayAppend`, and `VariableSet` nodes
inside two production body activations. Both begin with the declared
`["seed"]` default, append only their own current item, return the expected
array, and persist a distinct scoped variable history. Reopened file and
SQLite events reconstruct each activation independently (2/2 focused cases).

This verifies typed mutable-array state composition and playback inside the
private coordinator. It does not add public iterator authoring, shared state
between iterations, or checkpoint recovery; the architecture readiness remains
**REVISE**. Architecture and user-guide descriptions are unchanged because
this proof introduces no user-visible runtime behavior.

`ProductionBodyActivationCoordinator_ArrayVariableOverflowStopsLaterItems`
starts with the maximum-sized valid array, then appends an item during the
first activation. `ArrayAppend` failure is attributed at the data evaluator;
the variable write and second activation are absent, and the file/SQLite
failure prefixes reopen identically (2/2 focused cases). This adds one bounded
mutation-failure case without changing the proof's architecture status.

## Matched typed Boolean item binding (2026-11-16)

`IteratorCandidates_BindTypedBooleanItemsAcrossExclusiveRoutes` sends
`[true,false,true]` through the structured, fresh-subworkflow, and hybrid
candidates. Each binds the current Boolean as the declared `item` parameter,
selects the same exclusive branch, and returns the same branch-specific value.
The test compares lifecycle/branch event shapes and Boolean data-evaluation
evidence, then reopens both file and SQLite histories (2/2 focused cases).
This adds matched typed-item evidence beyond the existing integer comparison;
it remains a private candidate proof and selects no architecture or public
iterator contract.

`IteratorCandidates_BindTypedNumberItemsThroughArithmetic` sends
`[-1.25,0,9.5]` through the same candidates with a typed numeric item parameter
and `NumberAdd`. All candidates produce `-0.5;0.75;10.25`; the proof compares
each activation's numeric operand/result evidence and reopens both file and
SQLite histories (2/2 focused cases). This establishes finite numeric item
binding and arithmetic for this candidate body only; it does not cover numeric
overflow or select a production architecture.

## JSON-valued items in the integrated proof (2026-11-16)

The production-body proof coordinator now preserves each `array<json>` item as
its raw JSON representation while building per-item activation inputs. A
file/SQLite regression passes object items with nested nulls, arrays, objects,
and a `1.00` number token through the existing JSON-typed Input pin. The same
body reads the zero-based index through the existing integer Workflow Value
contract, combines both values for a production Agent invocation, and checks
scoped `data.evaluated` evidence after reopening the journal (2/2 cases).

This is private proof support for JSON item transport only. It does not add
JSON-typed workflow parameters, authored iterator item/index pins, public Break
behavior, or a selected production iterator architecture. Readiness remains
**REVISE**.

## JSON item parity across private candidates (2026-11-16)

The structured dispatcher, fresh Agent Framework subworkflow, and hybrid
candidate now all preserve JSON array items as raw JSON text when binding the
per-item run input. A matching file/SQLite test sends nested JSON records
through the existing JSON-typed Input pin, selects a string field, combines it
with the typed integer index, and invokes the same Agent body. The candidates
match output, Agent inputs, event shape, and scoped evaluated JSON values,
including nested null/array/object values and the `1.00` number token (2/2
focused store cases).

This closes a candidate-comparison gap for current JSON item transport only.
It does not define JSON workflow parameters, authored iterator pins or Break,
production scheduler ownership, or architecture readiness. Readiness remains
**REVISE**.

The same candidates also pass every valid JSON root kind through a direct
JSON-typed Input-to-Output body: null, both Boolean values, signed 64-bit
minimum, a `1.00` number token, a string, an array, and an object. The output
and scoped evaluation preserve the original JSON token text, and file/SQLite
histories reopen identically (2/2 focused cases). This adds primitive and
compound JSON value transport evidence; it does not add JSON parameters or
close the production body/lifecycle/budget matrix.

## Typed array transformations inside item activations (2026-11-16)

`ProductionBodyActivationCoordinator_ComposesEveryTypedArrayOperationPerItem`
composes Array Append, Length, Get, Insert, Replace, Remove, and Concatenate
inside each production body activation. The current typed item and a fresh
per-activation mutable array feed the operations; the resulting array is
committed through Variable Set and returned through Output. Scoped evaluation
events and writes retain the correct item values, and file/SQLite playback
reopens with the same completed history (2/2 focused cases).

This qualifies one successful deterministic collection composition only. It
does not qualify operation failures, cross-activation state, checkpoint
recovery, authored iterator semantics, or the runtime architecture. Readiness
remains **REVISE**.

## Array-operation failure attribution inside item activations (2026-11-16)

`ProductionBodyActivationCoordinator_AttributesTypedArrayReplaceFailureAndStopsLaterItems`
supplies an out-of-range index to Array Replace during the first item. The
production body records the operation's bounded safe reason at the Replace
node and propagates failure to the consuming Variable Set. No variable write,
iteration completion, or later item is admitted. Both file and SQLite prefixes
reopen with the same failed status and evidence (2/2 focused cases).

This proves one deterministic data failure and stop-admission boundary. It
does not cover cancellation races, sibling settlement, or select the iterator
architecture; readiness remains **REVISE**.

## JSON array transformation inside item activations (2026-11-16)

`ProductionBodyActivationCoordinator_AppendsJsonItemsAndReopensTypedArrayEvidence`
uses an `array<json>` as the outer iteration input. Each item enters the
production body's JSON Input, is appended to a typed JSON-array literal, and
reaches an `array<json>` Output. The two items include an object and a nested
array containing null and Boolean values. Scoped evaluation and output evidence
preserve object order and `1.00` number tokens, and file/SQLite playback
reopens with the same completed history (2/2 focused cases).

`ProductionBodyActivationCoordinator_IsolatesJsonArrayVariableStatePerItem`
extends this body to mutable JSON-array state. Each activation begins from the
same declared `[null,{"seed":1.00}]` value, appends its heterogeneous input
item, commits through Variable Set, and returns the state through Variable Get.
Variable-write evidence and playback reconstruction remain activation-scoped;
the object and nested-array values preserve their JSON representation after
file/SQLite reopen (2/2 focused cases).

`ProductionBodyActivationCoordinator_ComposesJsonArrayOperationsPerItem`
composes Array Literal, Length, Get, Append, Insert, Replace, Remove, and
Concatenate over each JSON item inside the same activation-scoped variable
flow. An object with a `1.00` token and a nested array pass through the typed
dataflow, while each activation starts at the declared default. Scoped data
and write evidence preserve the resulting order and exact JSON representation
after file/SQLite reopen (2/2 focused cases).

`ProductionBodyActivationCoordinator_AttributesJsonArrayOperationFailureAndStopsLaterItems`
injects invalid indices at Get, Insert, Replace, and Remove in that same body.
Each failure is attributed to its originating node, prevents Variable Set and
iteration completion, suppresses the later item, and reopens with the same
failed file/SQLite playback history (8/8 focused cases).

`ProductionBodyActivationCoordinator_RejectsJsonArrayVariableOverflowPerItem`
uses a valid declared default near the 16 KiB typed-array bound. Appending the
first heterogeneous item fails at Array Append, records no Variable Set, does
not complete the activation or admit the second item, and reopens as the same
failed file/SQLite prefix (2/2 focused cases).

`ProductionBodyActivationCoordinator_CancellationAfterJsonArrayVariableWritePreservesState`
cancels from the recorder immediately after the first activation's durable
Variable Written event. The committed JSON value remains reconstructable from
that activation scope after file/SQLite reopen, the run reopens as cancelled,
and the second item is never admitted (2/2 focused cases).

`ProductionBodyActivationCoordinator_CancellationBeforeJsonArrayVariableWriteLeavesNoCommit`
cancels at the first activation's Variable Set start event. No write or Set
completion is recorded, the second item is not admitted, and both stores reopen
with the same cancelled prefix (2/2 focused cases).

`ProductionBodyActivationCoordinator_RecorderFailureAfterJsonArrayVariableWritePreservesPrefix`
injects a recorder exception after the `Variable Written` event has been
accepted by the store. The coordinator's subsequent failure append encounters
the expected-sequence conflict; the surfaced conflict does not retry work or
admit another item. Both stores reopen with the committed write as the final
event, an incomplete `running` status, and reconstructable scoped variable state
(2/2 focused cases). This demonstrates preservation of an accepted prefix, not
automatic recovery or terminal settlement after ambiguous append outcomes.

These prove successful JSON-array transformation and mutable-variable state,
plus bounded failure, cancellation on both sides of Variable Set, and one
post-commit recorder-failure boundary across item activations. They do not
establish a public iterator contract, parallel cancellation races for this type,
automatic recovery of ambiguous persistence outcomes, or the architecture
choice. Readiness remains **REVISE**.

`ProductionBodyActivationCoordinator_StopsAfterJsonArrayAppendExceedsPayloadLimit`
complements the successful path with an oversized Append result in the same
production-body activation flow. The first item fails at the Append node with
the typed-array 16 KiB bound error; the second item is never admitted and the
first item's Output does not complete. File and SQLite runs both reopen with
the identical failed event sequence (2/2 focused cases). This establishes
bounded JSON-transform failure attribution and persistence for this proof
shape; it does not establish cancellation behavior or select a public iterator
architecture. Readiness remains **REVISE**.

`ProductionBodyActivationCoordinator_EmptyJsonArrayCompletesWithoutActivation`
closes the corresponding zero-item path in the integrated production-body
coordinator. For both file and SQLite, an empty `array<json>` records root
start and completion with no iteration or body-node events, returns an empty
result, and reopens with the same completed history (2/2 focused cases).
Candidate-level empty typed arrays were already covered; this adds only the
integrated lifecycle/playback boundary. It does not change the public iterator
contract or architecture readiness, which remains **REVISE**.

`IteratorCandidates_ApplyJsonArrayOperationsWithMatchingScopedEvidence`
compares Array Literal, Length, Get, Append, Insert, Replace, Remove, and
Concatenate inside two item activations. The structured dispatcher, fresh
subworkflow, and hybrid candidate return the same transformed JSON and record
the same 19 data-evaluation edges plus one isolated variable write per item.
File and SQLite histories reopen with matching lifecycle and evaluation
evidence (6 candidate/store runs). This proves successful JSON-array operation
parity across the three private candidates; it does not qualify operation
failures across candidates, authored iterator pins, Break authoring, or select
the runtime architecture. Readiness remains **REVISE**.

## Integrated typed-pin, Agent/MCP, and nested-Break proof (2026-11-16)

`ProductionBodyCoordinator_ComposesTypedPinsAgentMcpAndNestedBreak` exercises
one integrated private proof through the production-body coordinator. The
body's typed item and index WorkflowValue pins feed a real Agent dispatch and
MCP tool dispatch; a private proof Break node is graph-routed on the selected
item and requests nearest-frame resolution from the coordinator. The inner
loop stops before the later item while the outer loop continues. The proof
checks typed evaluation values, Agent/tool inputs, Break request/consumption
evidence, lifecycle boundaries, and identical persisted histories after
reopening both file and SQLite stores (2 focused cases).

This closes one integrated proof seam; it does not establish public authored
iterator pins, a serialized Break contract, scheduler ownership, broader
failure/cancellation/budget coverage, or checkpoint recovery. It introduces no
user-visible behavior, so architecture and user-guide descriptions remain
unchanged. Readiness remains **REVISE**.

## Nested Break and unresolved MCP settlement review (2026-11-16)

A new independent proponent/opponent/adjudicator review returned **REVISE**.
Keep the hybrid as the leading proof target, but do not select it for
production or expose public iterator/Break authoring yet. The next gate is one
coherent VAW owner for activation frames, root lifecycle, and atomic shared
budgets, with the production body dispatcher returning typed outcomes without
root finalization. Prove admission-stop/drain behavior and failure or unknown
external outcomes overriding Break, along with budget settlement, static-body
compatibility, and file/SQLite playback. Do not broaden into a single-scheduler
rewrite unless the hybrid cannot establish a reliable body-settlement boundary.

### Follow-up adversarial review (2026-11-17)

A clean-context proponent, opponent, and adjudicator review returned
**REVISE**. Keep the hybrid as the leading proof candidate, but do not select
production ownership or expose public For Each/Break authoring. The reviewers
agreed the key unknown is one production run context owning frames, root
lifecycle, and shared reservations while the Agent Framework dispatcher
settles admitted work without finalizing the root. The adjudicated first gate
was success, definitive failure, and unknown MCP settlement after a graph
Break; the six controlled-broker file/SQLite cases below now cover that gate.
Before architecture selection, still combine all Agent/MCP reservations with
activation/lifecycle ownership and qualify supported body shapes, including
Sequence and cancellation interactions, through file/SQLite playback. Repeat
the three-role review before selecting the architecture or publishing node
contracts.

`ProductionBodyNestedBreak_SettlesAdmittedMcpOutcomeBeforeRootTerminal` now
covers an admitted parallel MCP call settling as success, definitive failure,
or unknown while the sibling requests nested Break. Across both file and
SQLite stores (6 focused cases), the Break event precedes MCP settlement, no
join or later inner item is emitted, and exactly one root terminal matches the
sibling outcome: success completes the run, while failure and unknown outcome
override Break. Reopened playback preserves the corresponding terminal state.
This directly proves the controlled-broker settlement boundary for these
three outcomes; it remains proof-only and does not establish production
ownership, broader cancellation/budget races, the public node contract, or
recovery.

`ProductionBodyNestedBreak_ComposesRealMcpCompletionWithBreak` adds a direct
MCP node backed by the local stdio fixture to the nested Break graph. The real
broker authorizes and completes the slow MCP operation, then the Agent
Framework fork advances to the Break node; file and SQLite histories reopen
with identical completed evidence (2 focused cases). The proof harness uses a
valid broker-only revision projection because the private Break node is not a
publishable workflow node. This composition does **not** prove a real MCP call
settles after Break is requested: Agent Framework settles that direct MCP node
before scheduling the downstream Break in this topology. The controlled-broker
unknown-outcome case above remains the drain-order evidence; public iterator
behavior remains unproven.

`ProductionBodyNestedCoordinator_SharesModelBudgetAcrossOuterActivations`
adds a separate nested budget case using the real Agent Framework model adapter.
A single run-wide model-dispatch allowance lets the first outer activation's
Agent/MCP body complete, then rejects the second model request before provider
dispatch. File and SQLite stores reopen with the same failed history (2
focused cases). The existing private run context already accounts for both
frames; this test verifies cross-activation cap behavior, not a production
scheduler entry point or all reservation settlement paths.

`ProductionBodyNestedCoordinator_HostCancellationAfterBreakStopsOuterActivation`
cancels the host from the recorded completion of the inner graph-dispatched
Break. The coordinator records a cancelled root rather than treating local
Break as successful root completion, and does not admit the later inner item
or next outer activation. Both file and SQLite histories reopen identically
(2 focused cases). This checks cancellation after Break at one deterministic
boundary; it does not cover cancellation racing already-admitted parallel
descendants.

`ProductionBodyNestedCoordinator_CancellationAfterBreakConsumptionStopsOuterActivation`
adds the adjacent coordinator boundary: cancellation is requested after the
consumed Break completion has been appended. The persisted Break remains in
the event prefix, the next inner item and outer activation are not admitted,
and the single root terminal outcome is cancelled. File and SQLite histories
reopen identically (2 focused cases). This is boundary evidence for the
proof-only coordinator; it does not add public iterator behavior or broaden
the cancellation races for already-admitted descendants.

`ProductionBodyNestedCoordinator_CallsPinnedWorkflowInEachNestedActivation`
executes the exact pinned child revision once for each of four nested items.
Each child receives and returns the matching item, and lifecycle/evidence
scopes include both the outer and inner activation IDs. File and SQLite
histories reopen identically (2 focused cases). This extends successful
WorkflowCall body composition to nested frames. The paired cancellation case
cancels at the first nested child start, records exact-scope child lifecycle
evidence and root cancellation, admits no later inner or outer activation, and
reopens both file and SQLite histories (2 more focused cases). The typed
conversion failure case records child node failure and failed-call lifecycle
evidence at the nested call scope, fails the root, admits no later activation,
and reopens both stores (2 more focused cases). This closes nested
WorkflowCall success, failure, and cancellation for the tested linear body
shape; parallel child settlement and the wider body matrix remain open. The
proof is private and does not change architecture or user-guide behavior.

`ProductionBodyNestedCoordinator_CallsPinnedWorkflowInterfacePerNestedActivation`
extends that static composition check to a pinned interface implementation.
Four nested calls pack each inner string through the declared interface field
and return the corresponding value; file and SQLite playback reconstructs the
completed calls with their nested scopes. Host cancellation at the first child
and a child JSON-projection failure both stop later inner/outer activations,
preserve child lifecycle evidence, and reopen as cancelled/failed interface
calls (6 focused store cases total). This closes nested interface-call
success/failure/cancellation for the tested linear body; parallel child
settlement and other supported-body compositions remain open. The proof is
private and leaves architecture readiness at **REVISE**.

`IteratorCandidates_CancelPinnedWorkflowCallChildAcrossCandidates` compares
host cancellation while an Agent inside an exact pinned `WorkflowCall` child
is blocked. The structured, fresh-subworkflow, and hybrid candidates each
record one cancelled child call at `array-loop/0/call`, suppress item
completion and the next activation, and preserve the same scoped lifecycle
shape after reopening the file and SQLite stores (2 focused theory cases; six
candidate/store executions). Passive playback reconstructs the child as
cancelled in every case. This fills the exact-pinned-child cancellation point
beside the existing interface-child comparison; it does not cover parallel
child settlement or select a production iterator architecture. Readiness
remains **REVISE**.

`IteratorCandidates_RouteWorkflowInterfaceCallOutputToAgentAcrossAllCandidates`
routes the pinned interface result into a downstream Agent for three changing
items. The structured, fresh-subworkflow, and hybrid candidates pass the
matching per-item values to that Agent and preserve identical interface
evaluation/call evidence after file and SQLite reopen (2 focused theory cases;
six candidate/store executions). Passive playback reconstructs all three
completed child calls. This proves one downstream runtime-data consumer path;
it does not establish every runtime-output cache family or change the
production iterator decision. Readiness remains **REVISE**.

`ProductionBodyActivationCoordinator_HostCancellationSettlesParallelAgents`
adds synchronized host cancellation after both production Agent Framework
fork branches have entered. Both matching Agent invocations settle as
cancelled, no join result or later item is admitted, and the current iteration
is recorded with outcome `cancelled`. Reopened file and SQLite histories
preserve that terminal event sequence (2 focused store cases). This closes one
parallel-Agent cancellation point in the private production-body coordinator;
it does not qualify cancellation races with already-dispatched MCP side
effects, nested child settlement, or production iterator authoring. Readiness
remains **REVISE**.

The review explicitly keeps the end state unchanged: typed array iteration
and nearest-loop Break must be authored in the current unified schema without
feature-specific schema gates. Checkpoint recovery stays excluded until the
complete frame and coordinator state can be captured and restored. Readiness
remains **REVISE**.

## Sequence/Break integration review (2026-11-17)

A new independent proponent/opponent/adjudicator review returned **REVISE**.
The adjudicator permits a private, bounded experiment to test Sequence inside
the hybrid production-body activation, but explicitly does not select that
architecture, enable Sequence in the Break path, or close the iterator gate.
Public iterator and Break contracts remain withheld until the integrated
supported-body, budget, lifecycle, race, persistence, and playback matrix is
complete and receives another three-role review.

The current production Break proof rejects every body containing
`NodeTypes.Sequence` (`WorkflowRunner.cs`). This is not safely removed as a
standalone guard change: unified-schema Sequence execution is diverted through
`BuildSequenceProductionWorkflow`, whose `SequenceProductionSource` queues all
configured outputs before their descendants settle and whose collector owns
per-output Sequence settlement (`WorkflowRunner.Sequence.cs`). The structured
proof's `DispatchSequence` cancels and drains admitted output tasks, but it is
a separate proof scheduler and does not establish that the hybrid body path
shares the VAW activation frame, lifecycle, or budgets.

The next experiment must keep frame/root lifecycle/shared budget ownership with
the VAW activation coordinator, stop later Sequence output and item admission
after Break, settle already admitted descendants, and prove explicit precedence
for failure, unknown MCP outcome, and host cancellation. It must preserve
nearest-frame Break identity and coherent event attribution, then reopen and
reconstruct the same outcome through both file and SQLite stores. If satisfying
those requirements needs a second independent settlement/lifecycle/budget
protocol, record the hybrid experiment as inconclusive and return to scheduler
selection. No runtime behavior changed as part of this review.

### Ordered-admission clarification (2026-11-17)

A follow-up three-role review returned **REVISE** and tightened the experiment
contract. The Blueprint reference promises ordered Sequence output dispatch,
but no barrier waiting for latent descendants. Therefore the proof must not
wait for output A's complete descendant scope before admitting output B. It
must record an atomic admission boundary, allow already admitted descendants
to overlap, and prevent only the outputs not yet admitted when Break wins that
boundary. Output admission order is observable; descendant start, side-effect,
and completion order are not guaranteed.

The one VAW-owned coordinator for admission, frames, shared budgets, and root
lifecycle remains a proof obligation rather than a selected architecture.
Before changing the existing Sequence guard, the private production-body proof
must cover: an active A while B is admitted; Break before, between, and racing
with admission; skipped undispatched suffix evidence; draining the complete
admitted scopes; nearest-frame targeting; failure, unknown MCP outcome,
cancellation, pause/stop, and budget exhaustion; and file/SQLite reopen with
passive playback. Existing termination, linear, and bounded-loop behavior must
remain intact. If the seam needs a second owner or waits for descendant
completion before admitting the next output, return to architecture review.
This remains an integration experiment, not shipped Sequence/Break parity.

The first admission-state increment adds `StopAdmission` to the private
Sequence activation ledger/coordinator. `DispatchNext` and `StopAdmission` are
serialized on the same lock: if admission wins, that output remains dispatched
and must settle; if Break wins, all undeclared outputs become skipped and later
dispatch is rejected. The coordinator emits skip evidence immediately and
waits for dispatched work to settle before emitting successful Sequence
completion. Focused ledger/coordinator coverage passes 11/11, including a
synchronized admission-versus-close race; the full Agent Framework suite passes
798/798. This is a tested primitive only: the production Sequence source still
queues all outputs, the Break-enabled body guard remains, and no integrated
Sequence/Break runtime behavior is established.

An attempted production integration on 2026-11-17 changed the source to admit
one output per source invocation and recursively schedule the next invocation.
The final focused run failed 40 of 55 existing Sequence production tests,
including missing workflow outputs, child-boundary timeouts, and incorrect
sibling settlement. That implementation was reverted. This is evidence that a
naive reentrant source dispatcher does not preserve the existing scheduler and
settlement contract; it is not evidence against the ordered-admission
requirement, nor a verified diagnosis of the framework's internal scheduling.
Keep the production Break guard. The next design must preserve concurrent
descendant progress while making admission and Break closure atomic, and must
pass the full existing Sequence production suite before adding Break behavior.

### Self-continuation scheduling proof (2026-11-17)

`SequenceDispatchProofTests.Superstep_continuation_runs_but_queues_next_body_until_active_superstep_drains`
uses the pinned Agent Framework 1.26.0 concurrent runner. The dispatcher emits
body A and a continuation message to its own connected executor edge. While A
is blocked, the continuation handler runs and records that B is next, but the
message for B does not start until A is released and its active superstep
settles. The focused test passes and distinguishes continuation execution
from downstream admission; it is not an integrated Sequence/Break proof.

This rules out a self-continuation token as the mechanism for the required
ordered admission with overlapping latent descendants. The previous production
integration and this isolated framework proof both expose a superstep boundary
that cannot be used to admit B while A remains active. Keep the existing
production Break guard and return to scheduler selection. Any candidate must
be tested against the same synchronized A-active/B-admitted condition before
production Sequence changes resume; architecture remains unselected.

A fresh proponent/opponent/adjudicator review returned **REVISE**. The
adjudicator requires the next proof to exercise production dispatch, allowing
a private scheduler only as part of that experiment. A standalone scheduler
fixture could pass without establishing compatibility with the production
body or lifecycle seams. The proof must directly show B admitted in declared
order while A's latent descendant remains blocked, then cover the atomic Break
race, draining, failure/unknown outcome precedence, cancellation, shared
ownership, persistence/playback, and existing runtime regressions. Passing
would justify a new three-role architecture comparison, not select production
architecture or authorize public iterator/Break contracts.

`SequenceProductionRunnerTests.Production_sequence_source_holds_first_descendant_until_later_dispatch_boundary_releases`
characterizes the current production path. At the synchronized `sequence:second`
boundary, output `first` is recorded as dispatched but its first descendant has
not started. Releasing the boundary lets the source finish admission and the
first descendant then starts. The test passes; it demonstrates why the current
source cannot receive a descendant Break between its output admissions. This
is a failing result for the required A-active/B-admitted gate, not evidence that
the desired contract should change. Keep Break withheld and continue scheduler
mechanism work; no architecture is selected.

### Private integrated Sequence/Break evidence proof (2026-11-17)

The private production-body proof now uses the structured scheduler to admit
output B while output A's real Agent executor remains active, atomically reject
the undispatched C suffix after Break, and wait for A to settle. The focused
proof records and reopens identical file and SQLite histories. It remains a
test-only proof path and does not alter the production Sequence guard, public
schema, or Studio authoring.

A three-role adjudication selected a distinct successful-Break lifecycle
terminal, `sequence.break_completed`, with version-1 Sequence evidence carrying
reason `break`. It is emitted only after admissions close, all admitted outputs
settle, and skipped suffix evidence is recorded. Passive playback reports
`break-completed`, requires a contiguous completed prefix followed by at least
one skipped suffix output, and rejects missing/incorrect reason, active work,
or events after the terminal. Ordinary `sequence.completed` continues to mean
all outputs completed; failed and cancelled terminals retain their meanings.
Older readers that do not recognize the new event may display only a running
prefix; they must not reinterpret it as ordinary completion. This narrows the
private evidence gap only. Public iterator/Break readiness remains **REVISE**
until the supported-body, race, failure/unknown, shared-budget, persistence,
and playback matrix passes and receives a new architecture review. The
user-facing guide is unchanged because this behavior is not shipped or
authorable.

The next failure-precedence proof runs a blocked real Agent as output A, a
graph-dispatched Break as output B, and an unadmitted output C through both run
stores. After B requests Break, A is released to fail. The run remains open
until A settles, then records A failed, C skipped, no `sequence.break_completed`,
and a failed root. Reopened passive playback reports `failed` (2/2 stores).
This closes the admitted-Agent-failure-over-Break case for this private
structured proof; the focused file/SQLite regression passes 2/2.

A companion regression admits an MCP tool call before Break. The controlled
broker records request and authorization, then reports an unresolved outcome
after Break; the admitted call is drained, the unadmitted suffix is skipped,
and the root remains `outcome-unknown` after both store reopens and passive
playback (2/2). This qualifies direct-MCP uncertainty in the private
Sequence/Break path only. A host cancellation injected after Break is
requested while an admitted Agent remains active is also persisted as a
distinct cancelled root and Sequence, with the remaining suffix skipped and
passive playback reconstructing `cancelled` through both stores (2/2).
Pause/stop, concurrent/racing admission, and the broader production scheduler
remain open.

### Mixed failure and unresolved external outcome review (2026-11-17)

A three-role review returned **REVISE** on root precedence. The proponent's
proposal to make correlated `outcome-unknown` dominate a known failure would
remove order dependence, but it assumes one root status can represent both
workflow execution disposition and external-effect certainty. The opponent
identified that these are separate facts: a run may certainly fail while an
admitted MCP side effect remains unresolved. The adjudicator requires both
facts to remain observable before choosing a single root policy.

Confirmed code behavior: `RunSnapshot.Status` reflects the last root terminal
event; `WorkflowRunner.StructuredProof.cs` records distinct root `failed` and
`outcome-unknown` outcomes. The existing private proof
`ProductionBodyActivationCoordinator_ParallelMcpFailurePreservesCancelledSiblingUncertainty`
records a known MCP failure plus correlated sibling `tool.outcome-unknown`,
then reopens and passively renders a `failed` root. Its complementary proof,
`ProductionBodyActivationCoordinator_UnknownOutcomeBeforeParallelMcpFailurePreservesBoth`,
records the unresolved sibling outcome before the definitive failure. Together,
the two settlement orders preserve both per-call authorization/outcome
correlations, suppress the join and next iteration, and reopen with identical
event histories in file and SQLite stores; both pass passive playback (4/4
focused cases). The root projection is captured and checked for reopen
stability, but the tests intentionally allow the current `failed` or
`outcome-unknown` projection rather than selecting precedence.

`ProductionBodyActivationCoordinator_MixedMcpOutcomesSurviveRejectedRootTerminal`
rejects both possible root terminal appends after the per-call outcomes have
been recorded. Across both settlement orders and both stores (4/4 focused
cases), execution surfaces the storage failure, the durable prefix remains
`running`, and reopen plus passive playback retain both correlated MCP
outcomes without fabricating a root terminal.
The complete Agent Framework suite passed after these proofs (814/814).

`ProductionBodyActivationCoordinator_HostCancellationDrainsMixedMcpOutcomes`
injects host cancellation as soon as the first definitive or unresolved MCP
outcome is recorded. Both already-authorized calls still settle and retain
their correlated outcomes in either order; file and SQLite reopen plus passive
playback preserve the evidence (4/4 focused cases). The existing root projection
is recorded without making a precedence assertion.
The complete Agent Framework suite passed after this increment (818/818).

Remaining proof control: combine both settlement orders with Sequence Break.
`StructuredSequenceBreakDrainsMixedMcpOutcomes` now exercises the integrated
Sequence path with four bounded linear outputs: a known MCP failure, an
unresolved MCP outcome, a Break output, and a suffix output. The first two
outcomes are held until both calls are authorized and the Break has caused the
suffix to be skipped. Both settlement orders drain the admitted calls, suppress
successful Sequence completion, and preserve correlated tool evidence through
file/SQLite reopen and passive playback (4/4 focused cases). This closes the
enumerated mixed-outcome evidence controls, but does not select root precedence
or public Sequence/Break support. A fresh three-role review must now decide
whether root status remains a single execution disposition or needs an
additional aggregate projection. The review returned REVISE: preserve root
status as execution disposition and investigate a separate read-only external-
effect certainty projection derived from the committed event prefix. It must
represent incomplete evidence as unknown, and must not infer no effects from
missing historical records. Current proof does not establish that projection;
add coverage for no effects, resolved effects, success, cancellation, and
incomplete correlation before selecting or shipping that contract.
The complete Agent Framework suite passes after this increment (822/822).

### Run evidence projection follow-up audit (2026-11-17)

The recommended separation already exists at the Core/Hosting boundary:
`RunSnapshot.Status` remains the root execution disposition, while
`WorkflowWorkEvidence.Read` derives outstanding work from correlated events
and `WorkflowRunReconciliation.Assess` returns that work beside the unchanged
status. `UnresolvedWork`, `TotalUnresolvedWork`, and
`HasIncompleteCorrelation` preserve unsettled tool calls and ambiguous or
malformed evidence independently of terminal status. Existing Core tests cover
resolved and unresolved calls, no outstanding requests, terminal work,
pre/post-authorization failure, and incomplete legacy correlation. The
identified cross-layer gap is now covered: three mixed-outcome matrices assert
that reopened file/SQLite evidence retains the unchanged root status and two
separately correlated unresolved tool calls with the `UnresolvedWork`
disposition (12 focused cases pass). Both settlement orders remain visible,
including after terminal-append rejection and host cancellation, and after
Sequence Break. This projection describes recorded calls only; it cannot detect
external effects for which no event was persisted.

The first cross-layer run exposed an invalid private-harness event: on
failure-first terminal-append rejection, it emitted `node.failed` for the
virtual array-loop frame even though that frame is not a node in the captured
workflow definition. Reconciliation correctly flagged that event as
incompletely correlated. The proof now records the safe activation failure
reason on the root failure event instead of attributing it to a nonexistent
node. All four rejected-terminal combinations then report two unresolved,
correlated tool calls with no incomplete correlation, and the complete focused
matrix passes 12/12. The full Agent Framework suite passes 822/822 after the
correction. This repairs only the private proof evidence; it does not close an
iterator architecture gate or select a public API. Keep the architecture and
public authoring gate **REVISE**.
Until that proof and a subsequent review are complete, the integrated
Sequence/Break proof remains private; the production guard, public schema, and
Studio authoring do not change.

### Production-dispatch coordinator re-review (2026-11-17)

A fresh proponent/opponent/adjudicator review returned **REVISE**. It supports
one bounded private experiment, but selects neither the current Sequence
production source nor a production iterator architecture. The current
`SequenceRuntimeCoordinator` proves atomic admission/closure only for a fixed
set of outputs; `SequenceProductionSource` still queues all configured
outputs through the framework message path. The prior one-output-per-source
reintegration failed 40/55 Sequence production cases. The structured proof
consumes Break after a body dispatcher returns and does not establish active
sibling settlement. These are confirmed limits, not proof that the requested
Blueprint behavior should be weakened.

The next experiment is a VAW-owned item coordinator that binds typed
item/index values and nearest-frame Break, owns the one root lifecycle and
shared budgets, and invokes each admitted body through the existing production
Agent Framework dispatcher. It must retain admitted body scopes through
settlement, atomically stop only future admission on Break, and skip the
undispatched suffix. It must not depend on `SequenceProductionSource`
recursive continuation. First prove a held A body while a later admitted B
requests Break and C is skipped, with root completion only after A drains;
then extend through the atomic admission/Break race, nested frames, admitted
failure, unresolved MCP outcome, host cancellation, budget contention, and
file/SQLite reopen plus passive playback. Reconcile invocation identity,
evidence correlation, and body scope settlement before considering any
architecture selection. If production dispatch cannot preserve those, reopen
the alternatives comparison.

No public iterator/Break contract, schema change, production guard change, or
Studio authoring follows from this review. Passing the proof only earns another
three-role architecture review; it does not establish feature parity. Typed
array iteration remains gated in the roadmap, and checkpoint recovery remains
excluded until activation frames and coordinator state can be captured and
restored.

### Single-stream bounded Sequence/Break experiment (2026-11-17)

The next private experiment reuses one Agent Framework production stream with
a proof-scoped `SequenceProductionDispatchSession`. The VAW coordinator admits
at most the configured number of outputs, closes admission when an admitted
Break node requests it, records the skipped suffix, and keeps the stream alive
until previously admitted work settles. The file and SQLite regression holds
the first Agent active while the second admitted output requests Break; the
third output never starts, both admitted outputs settle, the root completes
once, and reopened passive playback reconstructs `break-completed` (2/2).
The matching admitted-Agent failure case also passes through this exact
single-stream session: after Break closes admission, the first Agent is
released to fail, the suffix remains skipped, no successful Break terminal is
recorded, and file/SQLite playback reconstructs `failed` (2/2). Existing
structured activation Break/failure/unknown/cancellation regressions pass
(28/28), and the Sequence production runner group passes (58/58).

The exact same-stream session now also covers unresolved MCP outcome and host
cancellation. An authorized call held across Break is released as
`outcome-unknown`; the root preserves that disposition while Sequence playback
records the failed output and skipped suffix. Cancelling the host after Break
while the admitted Agent is held records a cancelled root and Sequence, one
cancelled admitted output, and the skipped suffix. Both cases reopen and
reconstruct through file and SQLite stores (2/2 each).

A coordinator contention regression races `TryDispatchNext` against
`StopAdmission(successfulBreak: true)` for 128 synchronized iterations. It
accepts either legal winner: the racing output is dispatched before Break and
must settle, or Break wins and that output is skipped. In both cases the suffix
is skipped, no dispatch event occurs after the first skip, subsequent admission
is rejected, and the Sequence reaches its Break terminal only after admitted
outputs settle. This verifies the coordinator lock invariant; a race driven
through the production session and graph-dispatched Break remains open.

This is evidence for the same-stream A-active/B-Break/C-skipped/drain gate, not
an architecture selection or a public contract. The new session is reachable
only through the internal proof method. Admission races, nested frames,
shared budgets, and the complete production matrix still need proof and a fresh
three-role review. During this experiment the lifecycle serializer
was found to omit the coordinator's Break reason; it now persists that reason,
which is required for passive playback to validate the terminal. No public
schema, production admission policy, or Studio authoring changes.

### Production body overlap proof (2026-11-17)

The first bounded experiment now passes through independent invocations of
`ExecuteBodyForActivationProofAsync`, which uses the production Agent Framework
workflow dispatcher. The private VAW coordinator admits at most two item
activations concurrently and serializes Break notification against later item
admission. In the deterministic case, item A's Agent remains held while item B
reaches a graph-dispatched Break; item C is not admitted. The coordinator drains
A, emits one root completion, and file and SQLite stores reopen with identical
events and passive playback (2/2 focused cases). The test is
`ConcurrentProductionBodyCoordinator_AdmitsBreakWhileEarlierAgentIsActive`.

This is stronger than the structured-scheduler proof for the A-active/B-Break
gate, but it remains a proof harness with a fixed concurrency of two. It does
not establish policy for general admission races or prove the complete body
capability matrix. A second deterministic case holds A through B's Break, then
fails A. The coordinator drains A, fails the root instead of completing it,
suppresses C, and reopens identical file/SQLite histories with passive failure
playback (2/2). This confirms admitted-Agent failure precedence in this proof
seam. A third deterministic case drains A's authorized MCP call after B's
Break; the correlated unknown result settles the root `outcome-unknown`,
suppresses C, and reopens identically with passive playback through both
stores (2/2). A fourth case cancels the host after B's Break while A's Agent
is held. A records `node.cancelled`, the root settles cancelled, C remains
suppressed, and passive playback matches after either store reopens (2/2).
This proves host cancellation through one admitted Agent drain, not pause/stop
or cancellation races during MCP dispatch. A fifth deterministic test pauses
after item C has atomically reserved an admission slot, then lets B request
Break. C is still dispatched and included in the ordered result; D is
suppressed, and both stores reopen the same completed playback (2/2). Together
with the Break-first test, this proves both outcomes at the serialized
admission boundary. It does not exercise uncontrolled scheduler races. The
remaining gates are enclosing concurrent production coordination after nested
Break, host-control pause-event integration, cancellation while paused,
remaining shared-budget contention, uncontrolled production races, and
compatibility with the full existing Sequence/loop/collaboration suite. Keep
production Sequence/Break guarded and public iterator/Break authoring withheld
until that matrix passes and a new three-role review resolves the architecture.

The concurrent production-body proof now also checks the shared model
dispatch ceiling. Two body activations are admitted, but a run-level limit of
one permits exactly one `model.requested` and one provider call; the other
activation fails before dispatch. Both file and SQLite stores reopen the same
failed event history and passive playback (2/2). This extends shared-budget
evidence to concurrent body dispatch only; byte/event contention, tool and
outcome reservation races, and a production iterator budget owner remain
unproven.

A nested-frame case now exercises the same concurrent production-body
coordinator with an explicit outer frame. The graph-dispatched Break records
the inner activation frame as its `targetFrameId`, not the outer frame; the
remaining inner suffix is not admitted, and file/SQLite reopen with matching
Break playback (2/2). This proves nearest-frame identity and local suffix
suppression in the concurrent body seam. It does not prove an enclosing
production coordinator resuming after a nested concurrent iterator settles.

The admission/Break boundary now also has a 48-schedule stress test. Seeded
task yields vary the order in which admitted body events and Break completion
resume; an internal proof callback records the exact `nextIndex` while the
admission lock closes. Across every schedule, the started iterations equal
the reserved prefix, each admitted iteration settles, output matches the
successful prefix, no run-level failure occurs, and the root completes once.
The separate deterministic tests force both a Break-first boundary and a
pre-reserved later-item boundary; the stress schedules themselves are not
required to realize both outcomes under varying suite load. This is
scheduler-yield stress evidence, not an uncontrolled production workload
race test or production iterator integration.

Pause/resume is now exercised through `IWorkflowExecutionGate` on the
concurrent production-body coordinator. The test requests pause after the
first admitted iteration settles, confirms the third item is not admitted and
the root remains nonterminal, then resumes and verifies all three items finish
exactly once. Both file and SQLite histories reopen with the same completed
result (2/2). This proves cooperative gate behavior at iterator admission and
root completion in the private seam. It does not integrate host-owned
`PauseRequested`/`Paused`/`Resumed` control events with this coordinator or
cover cancellation arriving while the gate is paused.

This proof-only test change does not alter shipped workflow behavior, so the
architecture and user-guide descriptions remain accurate; the roadmap and this
decision record the new evidence and remaining limits.

### Nested concurrent coordinator composition (2026-11-17)

A fresh three-role review returned **REVISE**: nested resumption could only be
proved after specifying that a child coordinator settles as a scoped,
nonterminal outcome, while the root owner alone owns shared budgets and emits
the root terminal. The proof now passes one shared
`StructuredActivationRunContext` through nested coordinator calls. A child
closes only its own frame on Break, drains already admitted work, and returns
its output/disposition; its parent resumes only after settlement. Child calls
do not emit root lifecycle events or create independent budget ledgers.

Five controlled cases pass through file and SQLite stores (10/10 focused
cases): (1) inner Break waits for a held Agent sibling, suppresses the inner
suffix, then allows the parent to complete and admit its next item; (2) an
admitted sibling failure after Break fails the root after drain and suppresses
the next outer item; (3) an admitted MCP call's correlated unknown outcome
survives inner Break and prevents parent continuation; (4) host cancellation
during inner drain records node/root cancellation and prevents later
admission; and (5) a root loop-activation budget is shared across outer and
inner coordinators. Each case checks a single root lifecycle and reopened
event history; applicable success/failure/unknown/cancellation cases also
check passive playback or correlated tool evidence.

This closes nested item-coordinator settlement for these controlled cases. It
does not prove arbitrary nesting depth, nested graph fork regions, uncontrolled
production scheduling, production host pause-event integration, cancellation
while paused, or checkpoint recovery. The proof uses configurable concurrency
bounded to 1–8 (the nested case uses outer concurrency 1 and inner concurrency
2); it does not select a production scheduling policy. Public iterator/Break
authoring remains gated, and the production architecture remains unselected
pending the remaining compatibility/race matrix and a fresh architecture
review.

### Cancellation at a paused admission boundary (2026-11-17)

The concurrent production-body proof now cancels the host while a later item is
waiting at a paused admission boundary and an already admitted Agent remains
active. Cancellation releases the boundary wait, cancels and drains that Agent,
records the admitted iteration's `cancelled` settlement before the single root
cancellation, and suppresses the undispatched suffix. The recorded playback
marks the first item completed and the held second item cancelled rather than
leaving an unmatched iteration-start event. Both file and SQLite reopen paths
preserve the same event sequence and playback (2/2 focused cases).

This qualifies cancellation at this paused coordinator boundary only. It does
not integrate the host-owned `PauseRequested`/`Paused`/`Resumed` event writer,
cover cancellation racing with MCP dispatch, exercise uncontrolled production
scheduling, or select the public iterator architecture.

### Cancellation after direct-MCP dispatch (2026-11-17)

The concurrent production-body coordinator is also tested with one admitted
item and concurrency set to one. Host cancellation occurs only after the
controlled broker confirms MCP dispatch. The broker records a correlated
`tool.uncertain`, the body records node cancellation, and the coordinator
records the activation as cancelled before the single root cancellation. It
does not admit the next item. File and SQLite reopen preserve the same event
sequence and passive loop playback; reconciliation retains the uncertain tool
as unresolved work while run status remains `cancelled` (2/2 focused cases).

This closes only cancellation of a single admitted MCP call in the private
coordinator. It does not qualify concurrent MCP cancellation/reservation
races, host pause-control event integration, uncontrolled scheduler races, or
public iterator behavior.

### Host pause-control integration with the private iterator proof (2026-11-17)

A fresh proponent/opponent/adjudicator review returned **REVISE**. It permits a
test-only integration proof, but requires the actual host-owned `ControlledRun`
and its `IWorkflowExecutionGate`; a simulated event adapter would repeat the
existing coordinator-level pause tests without proving composition. Public
schema, Studio authoring, production dispatch, and scheduler selection remain
unchanged.

The proof now sends iterator events through `ControlledRun` for both file and
SQLite journals. It confirms one ordered `PauseRequested` → `Paused` →
`Resumed` lifecycle, no later item admission while paused, shared execution-slot
release during pause and reacquisition before the next admission, and exactly
once continuation. A second case cancels while paused with a sibling Agent
still active; that Agent settles cancelled, the suffix is suppressed, and the
root ends cancelled. Reopened event histories preserve the same evidence and
passive loop playback (4/4 focused store cases).

This proves integration only for these controlled cases. It does not qualify
uncontrolled production races, concurrent MCP cancellation, arbitrary nested
scheduling, the supported-body matrix, checkpoint recovery, or public
iterator/Break semantics. No architecture or user-guide behavior changes
because the proof remains private and does not alter a shipped execution path.

### Shared tool admission budgets under concurrent body admission (2026-11-17)

Two concurrent body activations now contend against separate root limits: a
tool-dispatch count of one, and one outstanding tool-outcome reservation. In
both cases the private coordinator admits both bodies, allows exactly one
correlated authorization to reach the controlled MCP broker, and rejects the
competing activation before dispatch. The admitted tool settles, the failed
run drains without admitting a third item, and file/SQLite reopen plus passive
playback agree (4/4 focused cases).

This proves these two MCP admission limits across concurrent body activations.
It does not qualify event/byte budget contention or establish a
production-owned budget contract for public iterator execution.

### Settlement-capacity reservation under event and byte budgets (2026-11-17)

A clean-context three-role review returned **REVISE** on reserving capacity
only for the successful path: a started activation must have a bounded
settlement slot before body dispatch, and every definitive body outcome must
attempt exactly one matching completion. The private concurrent coordinator
now applies that contract through its shared root budget ledger. Each accepted
`loop.iteration.started` reserves one ordinary event and enough serialized
bytes for a completion carrying up to 1,024 characters of outcome data. The
reservation is keyed by loop node, activation invocation, and scope; ordinary
events and model/tool outcome reservations account for held completion slots
and bytes. Completion commits the actual event and releases unused bytes only
after the evidence callback accepts the append. The separate failure reserve
remains available for root failure evidence.

If start plus settlement cannot fit, the coordinator rejects admission before
recording the iteration start or dispatching its body. Event-count and byte-cap
tests exercise exhaustion after an accepted start: a later Agent/model event
is rejected before provider dispatch, the node failure is retained, and the
iteration still records one matching `outcome: failed` completion. The
Run-inspector activation projection renders that recorded outcome as **Failed**;
missing completion evidence remains incomplete. Three cases across file and
SQLite stores pass (6/6), including refusal to start when settlement capacity
cannot be reserved. The concurrent coordinator suite passes (31/31).

This reservation guarantees budget capacity to attempt the settlement append,
not store acceptance. A rejected or ambiguously acknowledged completion append
can still leave a started prefix incomplete. The result is private proof
evidence only: it does not establish a production-owned iterator budget,
arbitrary nested contention, general scheduler races, or public iterator
support. Architecture and user-guide behavior remain unchanged because no
shipped workflow runtime path uses this proof coordinator.

`ConcurrentProductionBodyCoordinator_IterationSettlementAppendFailureDrainsSibling`
rejects every `loop.iteration.completed` append in both stores. The second
parallel Agent completes before the first is cancelled during failure drain;
the coordinator stops admission, records root failure, and does not fabricate
either settlement. Reopened file and SQLite histories equal the accepted
prefix, and passive playback marks both started iterations incomplete after
run end (2/2). This characterizes store rejection after successful reservation;
it does not establish recovery from process loss or a production scheduler
contract.

`ConcurrentProductionBodyCoordinator_ReservesToolOutcomeBytesBeforeDispatch`
calibrates an integrated coordinator byte ceiling from a synchronized
two-item baseline. It includes the serialized prefix through the competing
MCP authorization, outstanding iteration settlement slots, two bounded tool
outcomes, and failure reserve. With that cap, only one authorization reaches
the broker; its sibling settles, the run fails without a joined result, and
both file and SQLite stores reopen the same failed history (2/2). The
concurrent production-body coordinator group passes 39/39. This covers one
direct-MCP event shape and bounded two-item race, not every payload envelope,
production budget ownership, or general scheduler races.

### Concurrent model outcome byte reservations (2026-11-17)

`ConcurrentProductionBodyCoordinator_EnforcesModelOutcomeReservationsBeforeDispatch`
uses a synchronized two-activation production Agent body to test both
outstanding model-reservation count and byte limits. With one reservation, the
competing `model.requested` event is rejected before provider dispatch. The
byte-limited case calibrates the serialized prefix through the second request
and includes both pending iteration settlement slots, two bounded model
outcomes, and failure reserve; the resulting prefix remains under the
configured ceiling. The accepted provider is released after its sibling
records failure. Both file and SQLite histories reopen with matching events
and playback (2/2); the concurrent coordinator group passes 41/41. This
verifies one model event shape under a bounded concurrent case, not all
prompt/response sizes, delegated-call envelopes, production budget ownership,
or uncontrolled scheduler races.

### Integrated failure-reserve exhaustion (2026-11-17)

`ConcurrentProductionBodyCoordinator_FailureReserveExhaustionLeavesRunNonterminal`
sets the failure-byte reserve below the serialized Agent failure event size.
One Agent fails while its admitted sibling succeeds. The failed iteration and
successful iteration each persist a settlement, but the rejected body failure
and root failure events do not appear; both file and SQLite stores reopen to
the exact accepted prefix with status still `running` (2/2). This confirms
failure-reserve exhaustion does not fabricate a terminal run outcome. It does
not recover, retry, or repair the incomplete run; production ownership and
operator recovery semantics remain outside this proof.

### Sequence composition across concurrent body activations (2026-11-17)

`ConcurrentProductionBodyCoordinator_ComposesSequenceAcrossActiveItems` runs
two typed array items concurrently through a production-body Sequence with two
direct MCP outputs per item. The broker holds all four outcomes until all four
authorizations have arrived, forcing overlapping Sequence activations. Each
item records one ordered Sequence lifecycle with both outputs dispatched and
completed; the coordinator retains one root lifecycle, and both file and
SQLite reopen/render the same two completed iterations (2/2). This closes one
supported-body composition case only. It does not qualify Sequence failure,
Break/admission races, arbitrary concurrent branches, production budget or
lifecycle ownership, or the remaining integrated matrix.

### Concurrent Sequence failure with a completed sibling activation (2026-11-17)

`ConcurrentProductionBodyCoordinator_SequenceFailurePreservesSiblingItemCompletion`
holds four direct-MCP invocations until all are authorized. It lets the second
item's two-output Sequence complete, and the first item's architecture tool
record a successful result, before failing the first item's inventory tool.
The persisted prefix retains the second item's completed Sequence and
iteration, plus the first item's failed Sequence output, cancelled sibling
output, and failed iteration. The sibling tool's completed side effect remains
separately visible even though its Sequence output path is cancelled. Both
file and SQLite stores reopen and render that exact mixed prefix (2/2).

This establishes one synchronized ordering only; it does not qualify alternate
failure positions, cancellation or Break races, arbitrary body compositions,
or production iterator ownership. Keep external tool completion distinct from
whether the enclosing Sequence output path completed.

### Per-output production-dispatch experiment review (2026-11-17)

A fresh proponent, opponent, and adjudicator reviewed a candidate to dispatch
each admitted Sequence output through a separate invocation of the existing
`ExecuteCoreAsync`/production body path, under one VAW-owned coordinator. The
adjudication is **REVISE**: this is a candidate experiment only, not a selected
runtime architecture or permission to remove the production Break guard.

The candidate is plausible because independent production body invocations
could let output B's Agent Framework descendant start while output A remains
active. However, `ExecuteCoreAsync` creates per-call activation, variable,
event-sequencing, and workflow state, while the normal Sequence source/collector
already owns output settlement. Reusing the root recorder alone does not prove
one event sequence, shared budgets, original revision attribution, preserved
mutable-variable semantics, or exactly-once output settlement. A proof must
make the VAW coordinator the sole owner of output admission/closure, frame and
root lifecycle, shared budgets, and final output settlement; production body
invocations may only return scoped outcomes. Preserve the original revision
identity and node IDs, assign one monotonic run sequence, and correlate each
event with its output, connection, invocation, and activation scope. Do not
leave settlement of the same output to both the coordinator and the production
Sequence collector.

The first falsifiable case remains: hold output A in a real production Agent,
admit output B and reach its graph Break before releasing A, record C skipped,
then drain A. Assert one gap-free run event sequence, exactly-once output
settlement, original revision identity, one root terminal after all admitted
work settles, and identical file/SQLite reopen plus passive playback. Failure,
unknown MCP outcome, and cancellation must retain their established precedence
over successful Break. Any duplicate/missing settlement, identity mismatch,
early root terminal, or failure to start B before A releases rejects this
candidate. No code or shipped behavior changed for this review.

On 2026-11-17, the focused current-checkout characterization and Sequence
composition/failure cases passed 5/5, including both file/SQLite theory cases
for each proof. This confirms the existing boundary and prior proofs still hold;
it does not exercise the per-output dispatch candidate or close its first gate.

### Separate streaming-run dispatch attempt and single-run follow-up (2026-11-17)

A proof-only implementation attempted to run each admitted Sequence output by
building a one-output production Sequence workflow and starting another
`ExecuteCoreAsync` streaming run, while sharing the outer VAW Sequence
coordinator and run recorder. The focused regression
`ProductionSequenceOutputCoordinator_AdmitsBreakWhileEarlierAgentIsActive`
failed for both file and SQLite stores (0/2): output A's Agent started and
remained held, output B was recorded as dispatched, but B's production
Sequence source emitted no start event and its graph-dispatched Break never
completed. The same code change left the existing Sequence production filter
passing 56/56. This falsifies the separate concurrent streaming-run route at
its first A-active/B-start gate; it is not evidence that the VAW coordinator or
the underlying body proof is invalid.

A fresh proponent/opponent/adjudicator review of the alternative—one
Agent Framework streaming run with bounded, lazy Sequence output admission—
returned **REVISE**. The proponent found it best aligned with the production
topology. The opponent highlighted unresolved source/collector reentrancy,
completion protocol, drain, and admission-bound risks. The adjudicator requires
a same-stream test that holds A, starts B, requests Break, proves C is never
admitted, drains A, and validates store reopen/playback; it also requires
controlled failure, unknown-tool-outcome, and cancellation cases. Keep VAW as
the single owner of Sequence admission, closure, output settlement, root
lifecycle, and persisted evidence. The production graph may report descendant
outcomes but must not independently settle the same output. This follow-up is
only an experiment direction: no shipped behavior, public schema, or runtime
architecture is selected.

### Shared Break/admission linearization in the production dispatch session (2026-11-17)

Following the three-role review's **REVISE** adjudication, the private
`SequenceProductionDispatchSession.TryAdmit` now performs coordinator admission
inside `ActivationAbortScope.TryAdmit`. The same scope lock guards
`ActivationAbortScope.TryRequest`, so an admission cannot pass a stale
`Request == null` check and dispatch after Break has been accepted. The
session-local lock still serializes Sequence session state; it is not treated
as the Break linearization point. No await occurs under either lock.

The focused proof
`SingleStreamSequenceProof_AdmissionWinsAgainstGraphBreakAndDrains` holds a
real graph-dispatched Break before acceptance while already admitted Agent
outputs are active, then accepts Break and verifies those outputs drain while
the suffix stays undispatched. It reopens both file and SQLite histories and
reconstructs `break-completed` playback (2/2). Together with
`SingleStreamSequenceProof_AdmitsBreakWhileEarlierAgentIsActive`, this covers
both observed orderings at the integrated graph boundary: an admitted prefix
that drains after Break, and Break accepted before the next output so that the
suffix is skipped. This is not a stress proof that races the exact lock
instruction under arbitrary scheduling; the 128-schedule coordinator test
remains the contention check.

The wider iterator gate remains open. This change is proof-only and does not
select production Sequence/Break support or public authoring. Nested frames,
shared budgets, and the complete supported-body/runtime matrix remain pending.

### Concurrent Break isolation meets the linear Sequence path contract (2026-11-17)

A fresh proponent/opponent/adjudicator review recommended a private proof of
activation-local Break while a sibling production-body activation is active,
with a bounded Sequence continuation in that sibling. The adjudication was
**REVISE**: retain the concurrent sibling boundary, but stop if the current
graph contract cannot express the topology; a passing private proof would not
select runtime architecture or public authoring.

The candidate graph was rejected by unified-schema validation before runtime
dispatch. The current Sequence path validator requires every output to follow
a bounded, disjoint linear path ending at Output (`sequence.path.node`,
`sequence.path.terminal`, `sequence.path.unreachable`, and
`sequence.path.barrier`). It cannot conditionally choose Break for one
activation while the sibling continues through a Sequence path. No runtime or
schema change was retained from this experiment. This is evidence of a separate
Blueprint composition gap, not a failing runtime proof.

The narrower existing concurrent proof remains verified:
`ConcurrentProductionBodyCoordinator_AdmitsBreakWhileEarlierAgentIsActive`
shows a graph-dispatched Break in one activation while an Agent in another
activation is held, with the admitted Agent drained before root completion and
file/SQLite playback reopened. The isolated nearest-frame test also passed both
store variants on this checkout. These facts do not establish conditional
Sequence-path support or close the full iterator matrix. Revisit the combined
scenario only after a separately reviewed Sequence-path contract can represent
it without bypassing validation.

## Fresh architecture review and next composition proof (2026-11-18)

A new independent proponent/opponent/adjudicator review compared the structured
scheduler, fresh-subworkflow, and hybrid candidates against current proof
evidence. The proponent recommends the hybrid as the strongest target for the
next proof: VAW would own frames, admission, Break targeting, root lifecycle,
and shared reservations while the production Agent Framework dispatcher runs
the admitted body and static joins. The opponent and adjudicator agree that it
is not yet selected. The dispatcher/coordinator ownership split, complete
shared-budget boundary, and production lifecycle remain proof-only and
incomplete. This keeps the full body-composition target; it does not authorize
a linear-only body or public iterator behavior.

The adjudicator checked the new unified schema 2.0 Sequence branch admission
from commit `955151a`. It removes the former rejection for a bounded
Condition/BooleanBranch → exclusive Merge → linear suffix. It does not resolve
the reported composition: one activation conditionally selects Break while a
concurrent sibling continues through Sequence. The admitted region requires
both arms to converge at Merge, and its topology analyzer rejects nested
control nodes. Thus the Break terminal and sibling Sequence outcome are still
not represented together by the public graph contract.

The next falsifiable proof is validator-to-playback composition for that exact
case. Construct one unified schema 2.0 graph with two concurrent activation
frames: one takes a graph-dispatched nearest-frame Break and the other settles
a bounded branchable Sequence. Require public validation and production-body
dispatch, atomic Break/admission ordering, exactly-once sibling Sequence
settlement, admitted-work drain, one root lifecycle and reservation owner, and
matching file/SQLite reopen plus passive playback. If validation rejects the
required terminal shape, record the precise rejection and review the smallest
topology-contract change before implementing it. A passing case would justify
another architecture readiness review but would not alone close the broader
supported-body, race, shared-budget, failure, and playback matrix.

The first public-topology probe confirms that an early route ending at the
existing successful `Terminate` node is rejected as
`sequence.branch.nested` at that node; a route that skips the required Merge is
reported as `sequence.branch.ambiguous-terminal`. Neither node is a substitute
for iterator Break: Terminate exits the workflow, while Break must settle only
the nearest activation and resume its parent. Core SequenceGraphTests pass
41/41. This records the present contract boundary; the validator-to-playback
composition proof still requires a reviewed Break-terminal ownership rule.

### Break-terminal topology review (2026-11-18)

A separate proponent/opponent/adjudicator review of the proposed typed Break
terminal returned **REVISE**. Keep Break as a candidate in unified schema 2.0;
do not add public node authoring or select a production runtime architecture.
The proposal correctly distinguishes nearest-iteration Break from workflow
`core.terminate`, and the no-schema-gate/no-body-allowlist direction remains
unchanged. The unresolved contract is how one Break request settles its owning
Sequence output while other outputs are queued or active.

Existing proof evidence does not close this gate: the private
`vaw.proof.array-break` path projects Break to a validator-known node and uses
a synthetic Output handoff, while the nested production-body proof is invoked
through a proof-only coordinator. Those tests are useful runtime evidence but
do not establish public unified-schema validation or a no-result Break route.

Before another architecture review, the composition proof must specify and
exercise: (1) which Sequence output owns the selected Break route and how that
output settles exactly once; (2) whether queued and already admitted sibling
outputs continue, drain, or skip; (3) source-only Break requests whose nearest
frame is resolved by the single VAW owner, with atomic closure of future item
admission; (4) precedence of Break against failure, unknown external outcomes,
host cancellation, and rejected persistence; and (5) one owner for root and
activation lifecycle plus shared reservations. Break must never synthesize a
workflow Output value or a Merge arrival.

The falsifiable case remains a unified-schema graph with concurrent iteration
activations: one chooses nearest-frame Break while another follows a bounded
branchable Sequence path. It must pass public validation, production-body
dispatch, and file/SQLite persistence and reopen with identical passive
playback, including exact-once Sequence settlements, admission ordering,
draining, parent resumption, malformed topology, and failure/cancellation
races. Passing would justify another architecture review, not automatic public
admission or architecture selection. Do not weaken graph validation or hide
unsupported body nodes to make this proof pass.

### Proof-only settlement contract (adjudicated 2026-11-18)

The follow-up three-role review returned **PROCEED** for a narrow prototype
with the following contract. This is a test oracle, not a selected public
runtime architecture, shipped Break node, or general authorization to compose
every node family.

- A Break request contains its source node and active execution scope, but no
  caller-selected target. The VAW activation owner resolves the nearest active
  frame. Correlate the request with the owning Sequence invocation, output ID,
  ordinal, and output activation; record the resolved target when the frame
  owner consumes it.
- The frame owner serializes Break against iteration admission and the owning
  Sequence invocation serializes it against Sequence-output admission. A
  racing output is either admitted before Break and drained, or skipped after
  Break; no output can be dispatched after its skip is recorded. The gate
  closes fail-closed while the correlated Break-request event is appended.
  Only an accepted append consumes Break. If the append is rejected, keep the
  gate closed, stop new effects/admissions, do not claim Break, and preserve
  the accepted prefix as incomplete.
- The Break-containing output uses the existing `sequence.output.completed`
  terminal only after its admitted descendants complete successfully. If
  descendants fail or are cancelled, use the corresponding existing output
  terminal. The correlated Break request plus the Sequence-level
  `sequence.break_completed` terminal distinguish this control outcome; no
  new per-output Break state is required. Break emits no workflow Output value
  and no Merge arrival. Already-admitted outputs in that Sequence drain;
  undispatched outputs in that same invocation are skipped. Already-admitted
  sibling iterator frames remain independent and drain their own routes.
- Resolve the root disposition globally after all admitted frames settle:
  `outcome-unknown` > known `failed` > accepted host `cancelled` > consumed
  Break > normal completion. Unknown external outcomes remain correlated in
  tool and root evidence even if a Sequence output settles as failed. Host
  cancellation counts when observed before root outcome finalization. Persisted
  event order records accepted facts; it does not change this outcome priority.
- Persistence rejection is not a workflow outcome in that ordering. Stop new
  admissions and effects; drain only already-dispatched work as needed to
  resolve and record it, without retrying external effects. If a required
  request, settlement, or root-terminal append is rejected, emit no synthetic
  settlement and leave the durable run at its accepted nonterminal prefix.
  Playback must show that incomplete prefix, never a successful Break.

The prototype must force rejection both before the Break-request append and
during final settlement. It must also inject a known failure, an unresolved
external outcome, and host cancellation during drain, including a failure in
an already-admitted sibling frame. Test the exact two-activation graph through
unified-schema validation and production body dispatch, reject malformed or
multiply-owned topology, and reopen file and SQLite evidence into identical
passive playback. Passing permits another architecture review only.

## Flat graph and bounded activation-region proof review (adjudicated 2026-11-18)

A fresh three-role review compared a flat serialized graph with derived
activation regions, serialized nested regions, and root-level/special-case
composition. The adjudicator returned **REVISE**. Flat serialization is the
strongest proof hypothesis because it preserves the existing node, pin, and
connection identities and the unified editing model. This is not an
implementation selection: the topology analyzer and runtime ownership model
have not passed production evidence, and nested regions have not been shown to
be inferior through an equivalent proof. Root-level or WorkflowCall-only
composition does not meet the full composability/parity objective.

The next proof must keep authored identities flat and derive a bounded
activation-region plan from explicit node/pin contracts and graph topology,
without node-family allowlists. Reject arbitrary cycles; accept only feedback
edges declared and bounded by a loop owner. Do not serialize trusted derived
ownership. Public validation and package validation must independently
recompute the plan and reject ambiguous, multiply-owned, unreachable, or
unbounded structures. Keep the proof-only Break settlement contract above
unchanged.

Exercise the smallest unified-schema composition containing nested bounded
activation regions, a Sequence, and nearest-frame Break through public
validation, package validation, production coordinator and adapters, file and
SQLite persistence/reopen, and passive playback. Prove one activation owner,
exactly-once settlement, and deterministic failure, cancellation, and
persistence-rejection behavior. Include migration from supported existing
flat definitions, preserving authored identities and accepting the migrated
result without an author-facing schema or capability gate. Synthetic
proof-only dispatch does not satisfy this gate.

Pass only when every layer accepts the same valid composition and rejects
malformed ownership/cycles, production dispatch settles each activation once
across required races, both stores reproduce the same accepted evidence and
playback, and migration/package validation preserve identity without a
capability gate. On failure, identify whether the topology contract or flat
representation caused it; reconsider nested serialized regions only with that
evidence. A pass permits another architecture review, not public Break
admission or general runtime selection.

## Evidence-write interruption proof (2026-11-18)

The private structured dispatcher and concurrent production-body coordinator
now distinguish a rejected Break event append from an accepted Break outcome.
If the Break request append fails, the coordinator stops admission, drains
already admitted bodies, and emits no synthetic iteration settlement. If the
Break request is accepted but the final `sequence.break_completed` append is
rejected, the already accepted Break node completion remains in the prefix but
no Sequence skip or root terminal event is synthesized. File and SQLite
reopen to the exact accepted prefix, with the run still nonterminal. Coverage
is 4/4 structured request/final-settlement cases and 2/2 concurrent request
cases; adjacent structured Sequence and concurrent coordinator regressions
pass 87/87.

This proof also classifies failure-evidence reserve exhaustion as a run-budget
signal, preserving the existing bounded-settlement behavior instead of
misclassifying it as a persistence failure. These are proof-only interruption
semantics. They do not establish process-loss recovery, production ownership
of atomic budgets, or the full candidate evidence matrix, and do not change
the current **REVISE** architecture outcome.

The Agent Framework append-failure matrix now applies that prefix contract
across the structured, fresh-subworkflow, and hybrid candidates. Rejected body
evidence writes propagate through the executor wrapper, stop later admissions,
and leave file/SQLite playback at the accepted nonterminal prefix; tests no
longer expect a synthetic root failure after an ambiguous write rejection.
Budget exhaustion uses the runtime's dedicated budget signal and retains its
reserved failure settlement. The complete Agent Framework suite passes 933/933.
This requalifies the proof harness only; it does not select production iterator
ownership or close the architecture gate.

## Loop feedback pin contract re-review (2026-11-18)

A separate proponent/opponent/adjudicator review compared two flat graph
contracts: one `execute` input whose bounded body return is inferred from
topology, and distinct initial-entry and bounded-feedback inputs. The review
returned **REVISE**: preserve both as proof candidates until a representative
nested composition distinguishes them. The existing unified graph uses
`execute` for both roles and recognizes the return through a linear body walk;
this single-loop implementation is not evidence that topology inference will
remain unambiguous for nested loops, Sequence, and nearest-frame Break.

The next topology proof should run both pin shapes against the same cases:
nested bounded loops with Sequence and nearest-frame Break; cross-owner,
duplicate, unreachable, and arbitrary-cycle feedback; and incomplete drafts
during edge editing. Public and package validation must agree, and both file
and SQLite reopen plus passive playback must preserve the accepted evidence.
For existing definitions, automatic migration must preserve node, pin, and
connection identities wherever feedback can be mapped unambiguously; an
ambiguous mapping must produce a diagnostic rather than guess. This review
does not add a schema/capability gate, select production runtime ownership, or
authorize public Break.

### Topology discriminator scope adjudication (2026-11-18)

A fresh three-role review recommends proceeding with a same-graph comparison of
the inferred-feedback and explicit-feedback pin candidates, with the next
increment limited to topology and migration evidence. Run the same nested-loop,
malformed-feedback, incomplete-draft, and legacy-migration fixtures through
both candidates so differences are attributable to pin shape. Public/package
validation should agree on each candidate's topology result, and migration
must preserve existing node, pin, and connection identities whenever the
mapping is unambiguous.

Keep Sequence/Break production execution, persistence, and playback outside
this discriminator's acceptance criteria wherever the Sequence output
settlement contract in [Decision 0073](0073-branchable-sequence-output-contract.md)
remains unresolved. A failure at that boundary cannot distinguish the
feedback-pin candidates. This narrower proof does not replace Decision 0070's
broader integrated runtime gate, select production ownership, authorize public
Break, or reduce the full Blueprint-parity objective.

The first discriminator increment is implemented as an internal, non-authoritative
Core analyzer. Its shared flat nested-loop fixture produces the same outer/inner
ownership under both pin shapes (14/14 focused cases). It rejects a cross-owner
return, an unowned cycle, an unreachable loop, an incomplete return, and an
ambiguous migration. The candidate migration preserves node and connection
IDs while changing only the target pin on the two established feedback edges.
Current public and package validation still reject nested loops. The topology
fixture now includes a two-output Sequence whose distinct routes return to the
inner loop or end at a proof-only nearest-frame Break. Both feedback-pin
candidates derive the same nested loop ownership and Break owner. Shared output
routes, Break fallthrough, cross-owner return, unowned cycle, and incomplete
feedback are rejected for both shapes. The candidate migration preserves node,
connection, and data-edge identities and changes only the loop feedback targets.
Public/package validation continues to reject the candidate as unsupported.
The focused topology suite passes 29/29 and the complete Core suite passes
678/678. This remains a non-authoritative topology comparison; it does not
prove package round-trip of an admitted shape, production execution, or runtime
migration. The feedback-pin choice remains unresolved.

### Concurrent parent-frame Break isolation proof (2026-11-18)

The nested production-body proof now overlaps two parent activation frames.
The first frame reaches an inner Break while the second parent frame is already
admitted but held at its body boundary. Releasing that boundary after the first
Break confirms the second frame still dispatches its own inner body, reaches
and consumes its own nearest-frame Break, and settles independently. Both inner
suffixes remain unadmitted; each Break evidence payload targets the matching
inner-frame invocation; the root completes once. File and SQLite reopen retain
the same event stream and passive playback identifies both nested Break scopes
(2/2 store cases).

This closes the controlled concurrent-parent-frame isolation case for the
private proof coordinator. It does not establish an uncontrolled scheduler
race, public Sequence/Break topology, production-owned scheduling or budgets,
unknown-outcome or cancellation precedence across sibling frames, or general
Blueprint parity. A known sibling-failure ordering is covered in the following
proof.

### Sibling-frame failure precedence proof (2026-11-18)

The concurrent nested production-body proof now holds two parent frames open,
lets the first frame consume its inner Break, then releases the second frame's
Agent to fail. The Break does not settle the root early; the independent
sibling failure wins the root disposition. File and SQLite reopen preserve the
same ordered Break/failure prefix, with one failed root terminal and passive
playback showing both the inner Break and Agent failure (2/2 focused cases).
This adds one controlled known-failure ordering only. Unknown outcome and host
cancellation precedence across sibling parent frames, persistence interruption
at this exact boundary, public graph validation, and production ownership
remain open; the architecture gate remains **REVISE**.

The same ordering now rejects the root `run.failed` append after the first
parent's Break and the sibling Agent failure have both been accepted. File and
SQLite reopen to the identical nonterminal prefix, with both facts preserved,
no synthetic root terminal, and passive playback reporting the run as still
running (2/2 focused cases). This proves root failure-terminal append
interruption for this one cross-frame ordering; it does not cover process loss,
other settlement append boundaries, public topology, or production ownership.

### Sibling unknown-outcome terminal-append interruption proof (2026-11-18)

The cross-frame unknown-outcome case now rejects the root
`run.outcome-unknown` append after the sibling MCP call has persisted correlated
`tool.authorized` and `tool.uncertain` evidence. File and SQLite reopen to the
same accepted nonterminal prefix, retaining the first frame's Break and the
uncertain external call; passive playback reports the run as still running
(2/2 focused cases). The rejected terminal is not reconstructed or retried.
This covers one root-terminal interruption after an uncertain sibling call;
arbitrary process loss, other settlement boundaries, public topology, and
production ownership remain open.

### Sibling-frame unknown-tool precedence proof (2026-11-18)

The same two-parent production-body setup now completes the first frame's MCP
call and consumes its inner Break, then admits the second frame and holds its
MCP call until the broker reports an unknown external outcome. The run remains
active until that outcome settles, then ends `outcome-unknown`; correlated
authorization/uncertainty evidence belongs to the second frame. File and SQLite
reopen preserve the Break and uncertain tool call, and passive playback shows
both (2/2 focused cases). This is controlled proof-only evidence; sibling
cancellation precedence, persistence interruption at this boundary, public
topology, and production ownership remain open.

### Sibling-frame host cancellation after Break proof (2026-11-18)

The proof now holds Agent calls active in two concurrent parent frames, waits
for the first frame to consume its inner Break, then cancels the host. Both
admitted Agents record cancellation, neither frame fabricates successful
completion, and the single root terminal is `run.cancelled`. File and SQLite
reopen preserve the same event sequence and passive playback retains the Break
and cancelled root (2/2 focused cases). This closes only the controlled
cross-frame host-cancellation ordering. Persistence interruption at this
boundary, public topology, uncontrolled scheduler races, and production
ownership remain open; architecture readiness remains **REVISE**.

### Sibling-frame cancellation terminal-append interruption proof (2026-11-18)

The same synchronized two-frame cancellation now rejects the root
`run.cancelled` append after both Agent nodes have recorded cancellation. The
coordinator propagates the storage failure without synthesizing another
terminal outcome. Each store reopens to its accepted nonterminal prefix: the
first frame's Break and both cancelled Agent events are present, while the run
remains `running` (2/2 focused cases). This covers root terminal-append
rejection only; interruption during node or loop settlement, process loss,
public topology, and production ownership remain open.

### Nested Break settlement-append failure proof (2026-11-18)

The private nested coordinator now rejects the inner `loop.iteration.completed`
append after the Break node's `node.completed` event has been accepted. The
inner iteration is not recorded as settled; the coordinator preserves the
accepted prefix, records the enclosing outer iteration as failed, and records
one failed root outcome. The remaining inner suffix is not dispatched. File
and SQLite reopen with the same contiguous failed history, and passive playback
renders the loop activation (2/2 focused cases). This covers an injected
settlement-append rejection at one nested Break boundary, not process loss,
arbitrary interruptions, sibling-frame settlement races, public topology, or
production loop ownership; the architecture gate remains **REVISE**.

### Sibling drain after Break-node completion append rejection (2026-11-18)

The proof rejects `node.completed` for the first frame's Break node while a
sibling parent frame remains admitted. The coordinator waits for that sibling
to finish and records its known Agent failure, but does not record the rejected
Break completion, inner iteration settlement, or a synthetic root terminal.
Both stores reopen to the same `running` prefix, and passive playback shows the
started-but-incomplete Break node alongside the sibling failure (2/2 focused
cases). This is one node-evidence rejection during cross-frame drain; arbitrary
process loss, other graph topologies, public admission, and production runtime
ownership remain open.

### Explicit feedback target adjudication (2026-11-18)

A new three-role review selected a distinct bounded-feedback input as the
target graph contract. Initial entry and body return have separate roles; a
valid existing loop whose two roles share `execute` is migrated deterministically
by retargeting only its proven body-return edge. The user's parity objective
governs this choice: preserving an inferred serialized shape solely to avoid
schema migration would prioritize compatibility over the requested authoring
semantics. This selects the target contract, but does not admit it to public
validation or production execution yet.

The adjudicator's exact next step was to broaden the non-public Core proof across
representative graph shapes while leaving the public contract unchanged. The
proof now covers three nested loops and a multi-exit Sequence with one normal
return and two terminal Break routes. Both pin candidates derive identical loop
ownership and feedback edges. Migration preserves node, data-edge, and
execution-edge identities while retargeting only feedback edges. Focused tests
pass 32/32. Serialized legacy single-loop definitions from schemas 1.17–1.22
also round-trip, normalize to 2.0, and migrate without source mutation or loss
of authored IDs (6/6 cases). Malformed duplicate-return imports from each
legacy schema fail with no target/provenance and preserve every validator code,
path, and full message, without changing source bytes (6/6 cases). The full Core
suite passes 693/693. Existing malformed-route, disconnected-loop,
incomplete-draft, and ambiguous-migration cases remain rejected.

The following remain required before integration: broader malformed and
ambiguous legacy-migration diagnostics without partial source mutation; public
and package validation; editor authoring and editing behavior; a single
production runtime owner for loop entry, feedback, Break, and lifecycle;
file/SQLite persistence and passive playback; and
failure/cancellation/persistence-race coverage.
Sequence/Break settlement remains independently gated and cannot be used as
evidence for this pin choice. No schema capability gate should be introduced
when the selected contract is integrated; supported old definitions must be
normalized automatically or fail with actionable diagnostics.

### Public contract adjudication (2026-11-18)

A subsequent clean-context proponent/opponent/adjudicator review returned
**REVISE** and selected a staged integration: expose the explicit feedback
input in unified schema 2.0 and integrate it through Core validation,
serialization/package, editing, and existing single-loop runtime behavior;
keep nested loops, public Break, and unresolved Sequence/Break settlement
outside runtime admission until their production evidence is complete. Keep
published revisions and recorded runs immutable, and migrate only editable
copies when the existing return mapping is unambiguous.

The public single-loop slice is implemented. Loop descriptors declare
`execute` and `feedback`; Core validates one activation at each, packages retain
the feedback target, and the Agent Framework executes a schema-2.0 variable
Loop through the explicit feedback wire. Studio normalizes an old unified draft
copy and retains the stable connection ID. The previous unified shape is still
accepted for historical compatibility. Core tests pass 695/695 and focused
bounded-loop runtime tests pass 6/6. This does not admit nested Loops or
Sequence/Break composition: nested production settlement, failure/cancellation
races, store reopen, and passive-playback proof remain open under the larger
Blueprint parity objective.

### Contract-derived pin-role topology experiment (2026-11-19)

The non-public topology experiment now resolves execution-routing roles from
the canonical static and configuration-derived pin descriptors. Its walk uses
those declarations to identify initial and feedback inputs, loop body entries,
done continuations, ordinary continuations, fan-out/alternative outputs, and
the synthetic proof Break exit. The region walk no longer recognizes Loop,
LoopBody, Sequence, or the proof Break through node-family branches, and no
longer assumes that an ordinary continuation is named `then`.

This is still proof scaffolding only. The routing roles are not serialized and
do not authorize schema validation, package admission, production dispatch,
host access, or playback. Descriptor role direction, dynamic resolution, and
non-serialization are covered. Focused topology/descriptor tests pass 82/82,
the Core suite passes 697/697, and the Core project builds without warnings.
Production proof derivation, public/package validation, persistence, and
playback remain open gates.

### Conditional Sequence Break topology review (adjudicated 2026-11-19)

A clean-context proponent/opponent/adjudicator review returned **REVISE**.
Proceed only with a proof-only topology extension; do not change public
validation, package admission, runtime selection, or Studio authoring yet. Keep
the candidate graph flat in unified schema 2.0, represent Break as a terminal
route owned by one Sequence output activation, and derive nearest-frame and
output ownership from declared pins and topology. Continue to forbid hidden
ownership, body allowlists, and feature-specific schema/capability gates.

The proof must reject ambiguous, shared, cyclic, unreachable, and falling-
through routes. Its eventual composition gate remains the adjudicated
source/scope-only Break request, atomic frame/Sequence admission closure,
draining admitted work, skipping only undispatched work, exactly-once output
settlement, existing outcome precedence, accepted-prefix behavior on write
rejection, and identical file/SQLite passive playback. A passing topology
proof authorizes another architecture review only; it does not authorize public
Break support or establish Blueprint parity.

The proof fixture now places a Condition inside one nested Sequence output:
one route ends at the proof-only Break input and the other returns through an
ordinary body node. The descriptor-derived topology analyzer assigns the Break
only to the nearest inner loop under both feedback-pin projections (2/2). The
public validator still rejects the proof-only Break node. This verifies one
topology shape only; it does not prove concurrent sibling drain, public/package
admission, production dispatch, persistence, or playback.

### Sequence-output provenance proof (2026-11-19)

The private topology result now retains the Sequence node and exact execution
output pin that led to each proof Break. Provenance follows descriptor-derived
fan-out and alternative routes and survives ordinary continuations and nested
loop completion. The conditional fixture proves `inner-break` belongs to
`inner-loop` through `inner-sequence` pin `then.route` under both feedback-pin
projections, and a Break without Sequence-output provenance is rejected.
Two distinct Sequence outputs now retain their respective Break-node ownership
under both feedback-pin projections. Focused topology tests pass 38/38 and the
Core suite passes 703/703. This remains non-authoritative proof metadata; it
does not change public validation, package admission, runtime dispatch, or
playback.

### Sequence/Break topology rejection matrix (2026-11-19)

A fail-first regression demonstrated that a disconnected Sequence output
ending at a proof Break was ignored by the region walk. The analyzer now emits
`loop.feedback.unreachable` for proof Break nodes that are not reachable from a
root-loop body through a Sequence output route. The named topology cases cover
ambiguous/shared ownership (`Shared_sequence_output_routes_are_rejected`),
cycles (`Nested_sequence_break_unowned_cycle_is_rejected`), unreachable loop
regions and Sequence/Break routes (`Disconnected_loop_region_is_rejected_for_both_pin_shapes`,
`Disconnected_sequence_break_route_is_rejected`), and Break fallthrough
(`Sequence_break_terminal_cannot_have_a_successor`). Incomplete return routes
are covered by `Nested_sequence_break_incomplete_return_is_rejected`. Each
route case runs under inferred and explicit feedback-pin projections. The
focused topology suite passes 40/40 and Core passes 705/705. This completes the
enumerated proof-only topology rejection matrix, not the distinct runtime /
settlement gate or public feature admission.

The production-body proof now composes the branchable Sequence output with a
nearest-frame Break in a nested iterator. It verifies the Break target is the
inner activation, the Sequence suffix is skipped, the later inner item is not
admitted, the enclosing outer iteration proceeds, and file/SQLite reopen plus
passive playback preserve the same event prefix (2/2). This is one integrated
success-path proof; failure, uncertainty, cancellation, pause/stop races,
shared-budget stress, append rejection, and public admission remain unproven.

### Nested Sequence selected-branch failure proof (2026-11-19)

A deterministic MCP failure placed after the selected branch's Merge and
before its conditional Break now exercises the nested production-body Sequence
path through both file and SQLite stores. The selected output settles failed,
its undispatched Sequence suffix is skipped, no Break terminal is fabricated,
and neither the next inner item nor the next outer item starts. Reopening each
store preserves the event prefix and passive playback reports failed/skipped
Sequence outputs (2/2).

This proves failure before Break on the composed nested route; it does not prove
that an already-admitted sibling's late failure overrides a concurrently
reached Break. A held-Agent overlap fixture isolates the scheduling boundary:
the conditional Branch completes while the sibling Agent is held, but the
downstream Break does not start until that Agent is released and completes. The
same fixture now runs through both the current production-body candidate and a
fresh Agent Framework subworkflow candidate. Both preserve the observed event
shape and reconstruct the same `break-completed` state after file and SQLite
reopen (2/2 per store/candidate). This is evidence for this exact composition,
not a general Agent Framework scheduling guarantee or an architecture selection.

Zero-skipped Break playback previously had no evidence that distinguished an
accepted Break from an otherwise completed Sequence. The runtime now emits a
separate version-1 `sequence.break_accepted` record immediately when the Break
route requests it, before sibling drain, skipped-output settlement, and the
terminal. It binds Sequence invocation/node, Break source
node/frame, nearest target frame, and enclosing activation scope. Runtime
acceptance also checks that the request belongs to a currently dispatched
Sequence output and the active frame. Playback requires exactly one valid
acceptance for a zero-skip `break-completed`; legacy v1 histories remain valid
when they contain the prior completed-prefix/skipped-suffix evidence. An
accepted prefix without a terminal remains running, and malformed, mismatched,
duplicate, late, or normally completed-after-Break histories fail closed.
Rejecting the `sequence.break_accepted` append in either file or SQLite leaves
the Break node completion as the final accepted prefix; no suffix skip or
Sequence terminal is recorded (2/2). A second file/SQLite proof commits the
acceptance event and loses the writer acknowledgment; reopen reconstructs an
attributed `running` prefix with no skipped output or terminal (2/2). These
tests cover rejection and ambiguous acknowledgment at this event, not process
loss or other append boundaries.
Focused Core evidence tests pass 16/16, coordinator tests pass 8/8, and the
paired file/SQLite candidate test passes 2/2. Unknown external outcome,
cancellation, pause/stop races, broader append-rejection cases, the complete
body matrix, and public admission remain open. This proof does not establish
Blueprint parity or authorize public Break support.

The nested production-body conditional Break fixture now also holds its
admitted Agent sibling after the Branch selects the Break route, then releases
the Agent with a deterministic failure. In both file and SQLite stores, the
failure remains the root outcome; the queued Break never starts or records
acceptance, playback reconstructs the Sequence outputs as cancelled/failed,
and reopen preserves the same event prefix (2/2). This distinguishes a queued
Break route from an accepted Break and proves that an admitted sibling failure
is not masked in this scheduling shape. It does not prove a late failure
after Break acceptance or concurrent branch draining. No public iterator
contract or runtime architecture is selected.

The same nested production-body fixture now rejects the persisted
`sequence.break_accepted` append in both file and SQLite stores. The Break node
completion remains in the accepted event prefix, and the production-body
coordinator propagates the evidence-write failure without emitting a root
terminal. On reopen, root and Sequence playback remain running; no Break
acceptance, skipped suffix, or Sequence terminal is fabricated (2/2). This
qualifies the append-rejection boundary for this candidate; it does not prove
process loss or other append sites in this composition.

The nested production-body proof also commits `sequence.break_accepted` and
then loses the writer acknowledgment. Across file and SQLite reopen, the
acceptance appears exactly once, and root and Sequence playback remain
running with the acceptance and dispatched/completed output prefix;
no skipped suffix, Sequence terminal, or root terminal is added (2/2). This
qualifies ambiguous acknowledgment at Break acceptance for this candidate,
not process loss or other append boundaries.

Rejecting `sequence.break_completed` after Break acceptance now has matching
nested production-body coverage in both stores. The root remains running, and
the accepted Break prefix reopens with root and Sequence playback still
running. No Break, normal Sequence, or root terminal is fabricated (2/2).
This qualifies rejection of the Break terminal append only; root-terminal
append failure is covered separately below.

When a known MCP failure is recorded in the nested body but root `run.failed`
is rejected, the nested proof propagates the root-terminal `IOException`
without retrying. File and SQLite reopen retain the running root prefix and
the failed/completed Sequence output states (2/2). Process loss remains open.

`ProductionBodyNestedCoordinator_SettlesParallelPinnedChildPerActivation`
places a real Agent Framework parallel fork inside the exact pinned child
workflow invoked from a nested activation. One child Agent completes while its
parallel sibling is held; the child call and enclosing iteration remain
unsettled until that sibling is released. Both Agents and the child call then
complete, and file/SQLite reopen plus passive WorkflowCall playback preserve
the same nested scope and outcome. Matching file/SQLite cases inject failure
from one child Agent or cancel the host while the sibling is held; the child
call and root settle failed/cancelled, later inner and outer activations do
not start, and passive playback reconstructs the call outcome (6 focused cases
total). This closes parallel-child success/failure/cancellation for this
specific nested pinned-call shape. Other parallel descendants and the
remaining supported-body matrix are still open. Public iterator/Break
admission and architecture selection remain gated.

The same nested parallel settlement matrix now covers a pinned
`WorkflowInterfaceCall` implementation. The child interface contract packs a
typed string input into JSON, dispatches both parallel Agents, and projects the
joined result back through its declared output field. A held sibling keeps the
interface call and loop activation unsettled; success, child-Agent failure,
and host cancellation each reopen through file and SQLite with matching
WorkflowCall playback and no later activation after failure/cancellation (6
focused cases). This closes those three outcomes for this interface graph
shape; other child topologies, crash recovery, and the full iterator gate
remain open.

Subprocess proofs now exercise actual writer process loss immediately after
`sequence.break_accepted`, `sequence.break_completed`, and root `run.completed`
are committed. The parent terminates the child at each boundary and reopens
both file and SQLite stores. Playback preserves the running accepted prefix or
`break-completed` Sequence state; after root completion, the run remains
completed while Sequence playback remains `break-completed` (6 cases). This
proves durable reconstruction at these three boundaries; it does not recover
or resume an interrupted workflow, and other process-loss cut points remain
open.
