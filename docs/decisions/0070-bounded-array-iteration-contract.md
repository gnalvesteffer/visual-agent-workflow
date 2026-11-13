# Decision 0070: Bounded array iteration needs an execution and evidence budget

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

This is still one workflow body activation. It does not provide array item/index
pins, repeated or nested activation frames, nearest-loop `Break`, activation
scoping for the production evaluator/variables/join, persistence-backed event
accounting, or shared atomic work/evidence budgets. The proof explicitly
rejects Sequence and collaboration graphs. It is not evidence that the full
parity-level scheduler has been selected or implemented.

`ExecuteStructuredActivationSequenceProofAsync` now adds a proof-only VAW-owned
sequential activation coordinator around the structured dispatcher. It runs the
same production body once for each string in a bounded JSON array, binds the
zero-based index through the existing typed workflow parameter, creates a
fresh evaluator/variable store/executor set per activation, and writes scoped
node evidence into one monotonically sequenced run event stream. Its focused
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
nested frame stack or authored `Break`, and does not compare failure,
cancellation, or join outcomes across candidates. Store reopening after process
loss, crash recovery, and shared atomic budgets also remain unproven. The
architecture decision and public iterator remain open.

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
maximum function-call arguments/results; it does not cover delegated failure,
recorder/store append failure, reservation exhaustion, or establish aggregate
budget limits.

`ArrayIterationVawBodyProofTests.ParallelDelegateFailure_PersistsFailureAndTerminalEvidence`
uses a synchronized two-profile team where one child fails and the sibling is
cancelled, then the parent model fails while trying to recover. Both stores
retain the team, delegated-agent, parent-agent, and run failure sequence with
no completed team aggregate: 26 events, 4,823–4,825 bytes of team/agent/
terminal failure evidence, and 23,185–23,191 bytes for the complete event
history. This covers a concurrent delegated failure and cancellation path; it
does not exercise a persistence append failure or guarantee terminal capacity.
The full Agent Framework suite passes (273/273) with both delegated cases
included.

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

This is a test-only accounting harness, not a production run budget. It proves
reservation mechanics for one bounded model call and one bounded MCP tool call,
using test-specific outcome byte allowances. It does not prove worst-case
bounds for all event shapes, MCP/delegate reservation races, unlimited turns,
timeout/cancellation settlement, or a production terminal reserve. No
aggregate cap is selected.

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
