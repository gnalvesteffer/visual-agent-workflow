# Sequence-to-Agent path proof gate

**Status: Bounded backend slice; full promotion gates remain open**
**Scope: Blueprint-inspired Sequence descendants and evidence**  
**Decision date: 2026-10-28**

## Decision

Do not expose Agent paths through Studio authoring or call the capability fully
qualified yet. The reviewed additive schema 1.46 now exists as a bounded Core
and normal-runner backend slice. Schemas 1.44 and 1.45 remain unchanged.
The next verified increment is the backend contract:

```text
Sequence output → one Agent invocation → Output
```

The backend slice uses the normal `WorkflowRunner` recorder, `StringExecutor`,
host Agent executor, runner-owned activation accounting, and the existing
Sequence lineage envelope. It is reachable from a validated persisted 1.46
definition, but is not yet exposed by Studio promotion controls or package
authoring. Checkpoint recovery remains excluded.

The proof intentionally excludes Agent routes, delegation, teams, collaboration
channels, graph-function tools, MCP tools, approvals, nested workflows,
nested Sequences, retries, and durable resume. A single output is permitted
only inside this proof seam; public Sequence definitions remain bounded at two
to-four outputs.

## Evidence contract

When an Agent receives Sequence lineage, model and Agent lifecycle events are
augmented with the same private lineage identity: Sequence invocation, output
ID, ordinal, activation ID, connection ID, and descendant scope. Existing event
fields remain intact for ordinary runs. The lineage is correlation evidence,
not a capability grant and not a durable cursor.

The proof records the Agent's normal terminal outcome and the Sequence output
terminal outcome through the same append-only event recorder. It does not infer
success from a request, provider error, cancellation, or an uncertain external
effect. Playback remains read-only and makes no model, MCP, approval, extension,
or executor calls.

## Why public promotion is deferred

An Agent is not one simple node operation: it may contain model turns, tools,
approvals, delegated profiles, graph functions, routes, collaboration, and
provider uncertainty. Those capabilities need an explicit compositional
contract for failure, cancellation, authority closure, sibling settlement,
and replay before they can be admitted to a public Sequence path.

The additive schema allows one Agent directly before the Output
barrier, alongside the existing direct-MCP path shape, but only after tests
prove nested event lineage, uncertain outcomes, host-grant denial, cancellation
races, no premature run completion, and playback without live calls. This
document records the gate; it does not advertise that future schema.

## Adjudication addendum (2026-10-28)

The required proponent/opponent/adjudicator review revisited promotion after
the initial proof landed. The adjudicator kept full promotion gated. The current
proof is feasibility evidence plus a bounded backend slice, not full public-contract
readiness: it now exercises the real Agent executor for a one-output path and
for two distinct out-of-order completions, plus missing-provider and timeout
failure admission. The private failure/cancellation fan-out still uses a
deterministic executor, and public host-grant admission, persisted nested
playback, and public schema compatibility remain unproven.

The schema is additive `1.46`, preserving schemas 1.38, 1.43,
1.44, and 1.45. It may eventually allow each of the existing two-to-four Sequence
outputs to use either its current direct-MCP path or exactly one terminal
plain-text Agent immediately before Output. The candidate excludes Agent
routes, structured results, MCP or graph-function tools, skills, delegation,
teams, collaboration, approvals, workflow calls, loops, branches, joins,
retries, nested Sequences, and durable resume. It is implemented in Core and
the normal runner, but not advertised as complete parity.

## Backend slice update (2026-10-29)

Schema 1.46 now authoritatively validates the bounded topology and runs it
through the normal persisted-definition runner. Each ordered Sequence output
must choose one disjoint linear path containing either direct MCP nodes or
exactly one terminal plain-text Agent immediately before Output. Agent paths
reject tools, skills, delegation, graph-function tools, routes, structured
results, parameter-bound limits, and unbounded model turns. Focused Core and
Agent Framework regressions cover mixed-path success plus Agent failure and
cancellation ordering.

The bounded provider-ID admission seam is now covered: package requirements
include the terminal Agent provider, an exact host snapshot admits it, and a
missing provider is rejected before a run record is created. Still required
before marking this capability fully shipped: provider contract/grant-denial
coverage for the public definition, tamper-resistant run-store handling,
broader passive nested failure/cancellation playback, package/1.43-to-1.46
compatibility proofs, and Studio authoring controls. Both file and SQLite
stores now have a success-path nested Sequence/Agent/model playback proof
with no live executor calls during reopen. Schema 1.46 package JSON now also
round-trips the Agent configuration, execution topology, provider requirement,
and definition hash; this does not establish migration or older-runtime
compatibility.

Promotion gates, in order:

- [x] Record Sequence output failure/cancellation in the private one-output
  proof before the runner propagates the outcome. Multi-output sibling
  settlement is still unproven and remains a public-promotion gate.
- [x] Prove in a private two-invocation fan-out that a failed descendant
  cancels an active sibling and skips undeclared outputs before Sequence
  failure. Public Agent-path admission and real executor cancellation remain
  gated.
- [x] Exercise the real Agent executor in the private proof, including missing
  provider fail-closed admission, provider timeout uncertainty, and two
  distinct Agent paths completing out of order; passive Sequence playback
  remains read-only.
- [ ] Prove the same settlement contract for the public candidate with the
  real Agent executor and host capability admission; never complete the
  workflow while descendant work remains active.
- [ ] Add public-subset finite resource limits and explicit grant-denial tests
  once the schema-1.44 candidate is admitted for implementation.
- [x] Define deterministic private final-result semantics: the lowest authored
  output ordinal wins even when a later Agent path completes first.
- [ ] Prove passive playback of nested Sequence/Agent/model identity with zero
  provider, tool, or executor calls, plus schema 1.38/1.43 compatibility.
- [ ] Add package/Studio authoring and invalid-topology coverage only after the
  remaining runtime/evidence gates above pass; then advertise schema 1.46 as a
  complete public capability.
