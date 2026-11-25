# Decision 0080: Events and custom entry points

**Status:** DEFERRED after three-role review (2026-11-24): Option 1 stays; Option 2 is not accepted as written; not implemented

**Date:** 2026-11-24

## Context

The [capability matrix](../blueprint-capability-matrix.md) row "Events and
custom entry points" is Partial: Input is the sole workflow entry point, and
approval, collaboration, and host observation are bounded interactions. The
roadmap asks for a re-audit of event entry points. Blueprint Custom Events are
named entry points in one object graph; Event Dispatchers are runtime
subscription lists on a live object. VAW has no live object, tick, or implicit
mutable state, so parity means the authoring concept (named, typed ways to
start a workflow) without those semantics.

## Confirmed current behavior

- A run has one entry: the Input node. Callers supply one bounded string
  (`WorkflowRunner.ExecuteAsync`; see
  [service integration](../service-integration.md)). Triggers (REST, queue,
  webhook, schedule) are host code outside the graph.
- Observation is read-only: no start, pause, resume, or cancel routes
  ([observation](../observation.md)). Approval is a bounded, host-resolved
  pause ([approvals](../approvals.md)).
- `WorkflowInterfaceDefinition` in
  `src/VisualAgentWorkflow.Core/WorkflowInterfaces.cs` already models a
  versioned, hashed set of named operations; schema 1.1 gives each operation
  named, typed required input and output fields, and an implementation revision
  binds each operation to an exact workflow revision
  ([0063](0063-workflow-interface-contract-foundation.md)). It carries no
  authority or subscription.

## Options

1. Do nothing: Input stays the only entry; hosts choose the workflow.
2. Named entry points: a workflow implements an interface operation, and the
   host invokes `(workflow revision, operation ID, typed fields)`.
3. Workflow-owned event subscriptions (dispatcher parity): graphs bind to
   sources and are woken by them.

## Proposed direction (Option 2, smallest contract)

**Entry points are interface operations, not a new node family.** Reuse the
interface asset and implementation revision. A custom entry point is an
operation the workflow's implementation revision binds. The host resolves the
operation with the existing static resolver, validates fields against the
declared types, and starts a run. Today Input stays one string; an operation
whose single field is the string payload is exactly today's behavior.

**Host-owned invocation.** Only the host starts runs. Graph authors may name
operations and types but never supply endpoints, URLs, queues, schedules, or
credentials. The host decides who may invoke which operation, so authority
stays where it is today. Authors do not grant invocation rights.

**Workflow ownership.** Entry-point names, field schemas, and any input
shaping live in workflow/interface assets and visible template nodes.
Platform code must not branch on workflow IDs or inject prompts (see
[workflow-ownership](../../.agents/skills/workflow-ownership/SKILL.md)); caller
fields are never silently replaced.

**Run-start contract (proposed).** A run records the interface ID and
revision, operation ID, and the pinned workflow revision. Fields are
validated before the first node runs; failure is a start rejection, not a
run. Each operation maps to one run; there is no multi-entry activation
within a run and no cross-operation shared state.

**Evidence.** Additive only: a start-context element on the existing run
start event, or at most one new event kind. Input values follow existing sensitivity and redaction
policy, and the evidence stores the operation ID and a content hash.

**Compatibility.** Older readers must keep playing runs: unknown fields on
existing events and unknown kinds are skipped as opaque and never block
playback. There are no schema-version or capability gates; a run without a
start context is a plain Input run. Playback of old histories is unchanged.

**Security.** Host authority only. Reject unknown operation IDs, stale
revision pins, and over-bound payloads. No author-supplied endpoint, header,
or callback ever exists in the contract. Observation and approval stay as
described by [0075](0075-approval-payload-privacy.md); entry-point input is
caller data like any run input.

## Why dispatcher subscriptions are out of scope

Subscriptions need durable delivery (at-least-once vs at-most-once, dedupe,
ordering), ownership (who owns the subscriber list and who may publish),
cancellation (unsubscribe, workflow cancel, run termination), and replay
(recorded deliveries must reproduce without re-calling the source). None
exists today. They would also imply live, addressable workflow instances,
which this design rejects. To add them, all of the following must hold:
a durable host-owned subscription store with restart recovery; delivery
recorded as evidence with idempotency keys; deterministic replay that never
re-contacts sources; defined cancellation and quota behavior; and a security
review of publisher authority. Until then, hosts compose subscriptions
outside the graph and invoke entry points.

## Staged proof plan

1. Core unit tests (no host, no UI): operation resolution from an
   implementation revision; field validation (missing, extra, wrong type,
   oversize); string-payload operation equals current Input behavior.
2. Runner tests: start-context recording; run start rejection leaves no
   run; hostile or stale operation IDs fail closed.
3. Evidence and playback: file and SQLite histories; older-reader fixtures
   that skip the new element; legacy histories unchanged.
4. Host sample invoking two operations of one workflow; then Studio.

## Evidence required before acceptance

The tests above, plus confirmation that no existing event, hash, or
canonical form changes.

## Open questions for the three-role review

1. Is Option 2 the smallest sufficient contract, or does Option 1 already
   satisfy the matrix row until a host needs typed multi-field starts?
2. Should entry points reuse interface operations unchanged, or need a
   distinct entry-point declaration on the workflow itself?
3. Is a start-context element on the existing start event additive enough,
   or must a new event kind be used for older-reader safety?
4. Who owns authorization for each operation, and must the contract record
   the invoking actor?
5. Are Input-node semantics preserved when several operations map to one
   workflow revision (one run, one operation)?
6. Are the listed conditions for dispatcher subscriptions complete, or is
   there a cheaper bounded form (host-side fan-out only)?

## Review outcome (2026-11-24)

Three roles reviewed this record independently: a proponent for Option 2, an
opponent, and an adjudicator who verified the code claims below.

**Ruling: DEFERRED. Option 1 satisfies the matrix row for now; Option 2 is
rejected as written.** The matrix row stays Partial and still says "proposal
only, not implemented", so it needs no change.

**Verified code claims.**

- `WorkflowInterfaceResolver.ResolveOperation`
  (`src/VisualAgentWorkflow.Core/WorkflowInterfaces.cs:163`) takes a whole
  `WorkflowInterfaceImplementationRevision` and throws `ArgumentException` for
  an unknown or unimplemented operation. It validates the revision and returns
  the signature and the bound workflow, so resolution already exists. It does
  not validate caller fields.
- `EntryNodeId` is a required positional field of the workflow definition
  (`WorkflowDefinition.cs:12`). It is read at 68 lines under `src`, well beyond
  the opponent's estimate of about 30.
- Checkpoint seeding builds one `GraphMessage` from the single Input node's
  output type (`AgentFramework/WorkflowCheckpoints.cs:194`).
- `WorkflowRunner.ExecuteAsync(Guid runId, WorkflowRevision revision, string input, ...)`
  (`WorkflowRunner.cs:45`) takes one string.

**Reasoning.** An operation with several typed fields has nowhere to land
except a flattened string Input, so Option 2 would be input validation, not a
new entry point. Changing the Started event shape risks older readers
replaying an operation run as a plain Input run. Run identity and idempotency,
authorization, and stale versus latest pin handling are undefined. No current
host needs this, so the cost is not justified.

**If a need appears, ship only a host-side shim.** It resolves the operation,
validates fields against the declared Inputs, serializes them into today's
bounded JSON entry input, and calls `ExecuteAsync` unchanged. Operation ID and
actor are kept in host-side metadata, not in the Started event.

**Exact first slice, when a concrete host asks.** A pure Core `StartRequest`
validator that resolves the operation with `ResolveOperation` and rejects
missing, extra, wrong-type and oversize fields and stale pins, creating no run
on rejection. Its tests are unit tests only, and a one-string-field operation
must equal today's `ExecuteAsync(input)`. No runner, event, checkpoint,
schema-gate, or Studio change is in that slice.

**Rulings on the open questions.**

1. Option 1 suffices; Option 2 is deferred.
2. Reuse interface operations unchanged; no workflow-level declaration.
3. Do not extend the Started event. If start evidence is later needed, use a
   new event kind with an explicit older-reader skip rule.
4. The host owns authorization and records the actor in host metadata.
5. One run, one operation, one Input node; multi-field mapping is the JSON
   Input serialization above.
6. Dispatcher subscriptions remain out of scope; host-side fan-out is the
   bounded form.

## Start operation shim first slice (2026-11-25)

**Shipped (the reviewed host-side shim only).** Core `WorkflowStartRequest` and
`WorkflowStartRequestValidator` (`WorkflowStartRequest.cs`) resolve the operation with
`WorkflowInterfaceResolver.ResolveOperation`, validate fields against the declared
`Inputs`, and return typed errors or a mapped entry input. Hosting adds
`LocalWorkflowRuntime.StartOperationAsync` (`WorkflowOperationStart.cs`), which
validates and then calls the existing `StartRevisionAsync` unchanged.

- Mapping: a compact JSON object of all declared fields in declared order, at most
  16,384 characters, valid for the JSON Input node (schema-1.1 operations always bind
  JSON workflows, so even a one-string-field operation maps to `{"field":"text"}`,
  byte-identical to what a caller passes to a start today; a raw text mapping would
  not be valid JSON Input).
- Rejections (no run started): invalid or unknown operation, unknown interface,
  stale interface or implementation pin, tampered revision, no field contract
  (schema 1.0), missing, unknown, duplicate or wrong-typed field, oversize or
  too-deep JSON, invalid actor label.
- The run store has no operation or actor metadata, so both are returned in the
  start result only.

**Still deferred.** Everything else in the ruling: Option 2 and any workflow-level
entry-point declaration, start-context element or new event kind, run identity and
idempotency, authorization, durable operation or actor evidence, checkpoint and
schema-gate changes, Studio, and dispatcher subscriptions. The matrix row stays
Partial.
