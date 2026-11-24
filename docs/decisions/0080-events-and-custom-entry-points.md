# Decision 0080: Events and custom entry points

**Status:** PROPOSED, pending three-role architecture review; not implemented

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
