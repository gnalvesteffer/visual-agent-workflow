# Decision 0062: Selected data subgraph function extraction

**Status:** Accepted; Core plan and Studio extraction flow implemented and browser-verified (2026-11-02)

## Decision

Extract a selected, stateless pure data region into a typed immutable
`GraphFunctionDefinition`, then replace that region with a `FunctionCall` that
embeds the exact hashed function configuration. The Core operation prepares
the function, caller definition, and both layouts without mutating its inputs.
It validates the function body and the complete resulting caller before
returning a plan.

Boundary pins come directly from crossing data connections. Inputs are grouped
by their external source pin; one function input can therefore feed multiple
selected pins. Outputs are grouped by selected source pin; one function output
can preserve fan-out to multiple caller inputs. Each generated mapping is
explicit in the function's typed interface and connections. A region must have
at least one input and one output, matching the current graph-function
contract. Missing boundaries are rejected rather than filled with fabricated
values.

Selected nodes must be stateless, pure data nodes with static pins. Execution
connections, effects, dynamic pins, unsupported function-body nodes, invalid
or disconnected bodies, and invalid caller replacements are rejected. The
function layout carries selected node positions, labels, comments, expanded
comment state, sections, and internal wire routes. The caller layout replaces
the selected region with a call at its center and preserves a containing
section when the whole selection belongs to it.

The Studio exposes this through a selected-node context action. It prepares and
validates before mutation, saves one immutable catalog revision, then applies
the prepared definition/layout as one undoable edit. Opening a selected node's
context menu preserves the canvas multi-selection. The call snapshot remains
authoritative. If caller application fails after the function revision is
saved, the dialog reports that partial outcome and leaves the valid revision in
the catalog. Browser verification covers valid and invalid selections,
undo/redo, and save/reload.

## Rationale

Graph functions already provide immutable typed data-only snapshots, and
`FunctionCall` embeds the exact definition and content hash. Existing
`GraphTemplateEditing` expands a template into a caller; it does not remove and
replace a selected graph region, so extraction has its own Core contract.
Catalog storage is separate from workflow draft storage, but an embedded call
does not depend on the catalog at runtime. Keeping the function revision after
a later caller-save failure avoids a cross-catalog transaction while retaining
a truthful and useful asset.

## Verification contract

- Core tests prove deterministic typed boundaries, grouped external input
  sources, output fan-out, exact snapshot/hash embedding, removal of only
  selected nodes and touching data wires, and preservation of execution wires.
- Rejection tests cover zero-boundary regions, effectful selections, invalid
  generated identities, and invalid resulting graphs.
- Layout tests cover selected-node presentation and section preservation in
  both the function and caller layouts.
- Studio tests cover immutable catalog save/conflict behavior. The editor
  checks definition and layout hashes before saving, reports a catalog revision
  that survives a later caller-application failure, and applies one graph edit.
- Browser verification covers selected-node extraction, invalid selection
  rejection, one-step definition-and-layout undo/redo, and save/reload with the
  embedded snapshot. The same browser flow checks that right-clicking an
  already-selected node does not collapse the selection.
