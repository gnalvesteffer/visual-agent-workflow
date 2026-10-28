# Sequence-to-Agent path proof gate

**Status: Non-public proof only**  
**Scope: Blueprint-inspired Sequence descendants and evidence**  
**Decision date: 2026-10-28**

## Decision

Do not expose Agent nodes in persisted Sequence paths or add schema 1.44 yet.
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
