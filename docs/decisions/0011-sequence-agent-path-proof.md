# Sequence-to-Agent path proof gate

**Status: Non-public proof only**  
**Scope: Blueprint-inspired Sequence descendants and evidence**  
**Decision date: 2026-10-28**

## Decision

Do not expose Agent nodes in persisted Sequence paths or add a public Agent-path
schema yet. Schemas 1.44 and 1.45 are already assigned to mutable-variable
slices; any future public Agent-path schema must be a newly reviewed additive
version, currently expected to be 1.46.
The next verified increment is an internal production proof only:

```text
Sequence output → one Agent invocation → Output
```

The proof uses the normal `WorkflowRunner` recorder, `StringExecutor`, injected
host Agent executor, runner-owned activation accounting, and the existing
private Sequence lineage envelope. It is not reachable from persisted workflow
definitions, schema discovery, Studio authoring, package compatibility, or
checkpoint recovery.

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

The eventual additive schema may allow one Agent directly before the Output
barrier, alongside the existing direct-MCP path shape, but only after tests
prove nested event lineage, uncertain outcomes, host-grant denial, cancellation
races, no premature run completion, and playback without live calls. This
document records the gate; it does not advertise that future schema.

## Adjudication addendum (2026-10-28)

The required proponent/opponent/adjudicator review revisited promotion after
the initial proof landed. The adjudicator kept the status **Non-public proof
only**. The current proof is feasibility evidence, not public-contract
readiness: it now exercises the real Agent executor for a one-output path and
for two distinct out-of-order completions, plus missing-provider and timeout
failure admission. The private failure/cancellation fan-out still uses a
deterministic executor, and public host-grant admission, persisted nested
playback, and public schema compatibility remain unproven.

The candidate future schema is additive `1.46`, preserving schemas 1.38, 1.43,
1.44, and 1.45. It may eventually allow each of the existing two-to-four Sequence
outputs to use either its current direct-MCP path or exactly one terminal
plain-text Agent immediately before Output. The candidate excludes Agent
routes, structured results, MCP or graph-function tools, skills, delegation,
teams, collaboration, approvals, workflow calls, loops, branches, joins,
retries, nested Sequences, and durable resume. It is not implemented or
advertised.

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
- [ ] Add Core/package/Studio authoring and invalid-topology coverage only after
  the runtime and evidence gates above pass; then consider the new additive
  schema 1.46.
