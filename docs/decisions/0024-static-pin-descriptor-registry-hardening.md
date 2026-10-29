# Static pin descriptor registry hardening

Status: accepted bounded increment; non-serialized and non-authoritative

## Decision

Keep the existing `WorkflowNodeDescriptorRegistry` as an explicit, bounded
static-family slice rather than expanding it into a second node catalog or a
serialized compatibility identity. The registry currently covers pure value
nodes and the typed Boolean Branch. Its lookup remains exact by
`(node type, descriptor version)`; the node contract's implementation version
is a conformance fact, not a persisted descriptor reference.

Core now validates the registry as a whole. Every entry must have a unique
node-type/version key, map to a known non-dynamic node contract, use the current
descriptor version, and pass the existing port metadata and contract-shape
checks. Focused tests independently enumerate the bounded family and its
ordered pin facts, then prove parity with the existing port projection and
preservation of workflow JSON/hash behavior.

This is a drift detector and a stable in-memory authoring contract. It does not
make descriptors authoritative for schema admission, scheduling, grants,
runtime limits, cancellation, failure handling, persistence, or evidence.

## Compatibility boundary

The descriptor registry is not a serialized workflow identity. Changing a
descriptor version or pin shape requires a separate compatibility review; old
workflow definitions are not silently migrated or reinterpreted by this
increment. Dynamic/configuration-derived families continue through their
existing resolvers and schema-specific validation. The remaining switch-based
fallbacks in `WorkflowPorts` are intentionally not replaced here.

## Bounded follow-up: graph-function call data ports

The first convergence increment reconciles `FunctionCall` data-port projection
across the legacy `Inputs`/`Outputs` helpers, explicit data-port helpers, and
the descriptor resolver. A pinned graph-function's sorted input/output IDs and
declared data types are now the same through each Core port API. Missing or
unsupported function-call configuration receives no generic `in`/`out`
fallback pins, preserving fail-closed behavior for malformed definitions.

This remains schema-neutral: descriptor versions are not serialized, workflow
JSON and hashes are unchanged, execution pins and scheduling are untouched,
and historical node versions still receive no current pin semantics. Focused
tests cover multi-port typed functions, explicit/legacy projection parity, and
missing or unsupported function-call versions. Dynamic MCP, Agent, parallel,
Sequence, and graph-function migration behavior remain separate gates.

## Follow-up gates

Before claiming descriptor convergence, define historical descriptor
compatibility and schema ownership, independently reconcile all remaining port
fallbacks, and prove runtime/editor/validator use of one contract without
changing old definitions unexpectedly. Only then consider adding more node
families or serialized descriptor identities.
