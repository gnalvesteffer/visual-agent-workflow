# Decision 0074: Bounded proof for nested MCP argument pins

**Status:** REVISE; bounded non-approval evidence redaction is implemented, nested pin proof and authoring remain gated

**Date:** 2026-11-19

## Context

Unified workflow graphs expose each supported top-level MCP argument as a
typed, connectable input pin. Nested objects and arrays can be passed through a
`json` pin, and the normalized MCP schema descriptor retains their bounded
structure, but nested fields are not separate graph pins. The persisted
top-level mapping and connected-over-static behavior are covered by
[Decision 0034](0034-mcp-argument-descriptor-convergence.md); the descriptor
boundary is documented in [Decision 0020](0020-mcp-schema-descriptor.md).

Separate pins for tool inputs match the Blueprint-inspired exposed-input goal
and the existing authoring direction. Flattening every nested field directly
into the current pin model, however, would create unresolved questions about
stable path identity, parent/child overlap, optional omission, arrays, schema
drift, migration, and whether values leak through data-evaluation evidence.
The current broker still validates the complete invocation against its pinned
contract, live schema, and host grant; nested pins must not replace that
authority.

## Adjudicated direction

The three-role review returned **REVISE**. Proceed with a bounded, non-public
proof for explicit nested object field pins. Do not add Studio authoring,
public/package admission, or new persisted invocation-value evidence until the
proof establishes an argument-redaction contract and compatibility behavior.
Existing top-level pins and JSON-valued object/array inputs keep their current
meaning and remain available throughout the proof.

The eventual representation should identify schema fields by stable property
paths rather than display labels, property ordering, or discovery-time numeric
positions. The proof must use the existing shared descriptor-to-pin mapping
seam; editor, validator, and runner must not grow independent path mappings.
Pins for overlapping parent and descendant paths must be rejected rather than
resolved by incidental ordering. Defaults remain annotations and are not
silently inserted. Broker schema and grant checks remain mandatory at dispatch.

## Required proof gates

Before enabling nested pins in authoring or runtime admission:

1. Prove one bounded closed-object shape with deterministic property-path and
   pin identity, one required scalar child, one optional child, and a static
   leaf combined with a connected leaf.
2. Reject parent/descendant overlap, duplicate paths, missing required nested
   values, invalid types, and values outside schema bounds. Do not infer array
   length from connected item pins; arrays remain on the existing JSON pin
   path until a separate explicit item-shape contract exists.
3. Reconstruct the exact nested MCP argument object from connected and static
   values, then verify the real broker independently rejects changed pinned
   contracts, invalid live schemas, and missing node grants.
4. Define and test redaction before nested values can be persisted in
   `data.evaluated` or exposed in run details, playback, diagnostics, or error
   messages. The bounded direct-MCP slice now redacts values in direct-node
   `data.evaluated`, `tool.authorized`, `tool.completed`, direct MCP
   `node.started`/`node.completed`, and directly sourced Output lifecycle
   events. It retains event/invocation identity, sequence lineage when
   present, type, and character-count metadata without plain hashes. Explicit
   graph schemas separate control transfers from data dependencies and do not
   emit `connection.received` events for direct-MCP data wires. Redaction
   happens before observation/persistence and does not change runtime values.
   Focused runner and broker regressions verify the actual argument/result
   flow. File/SQLite reopen tests preserve redacted broker outcomes and also
   execute the configured nested direct-MCP path through each store, reopen
   the run, and verify that node lifecycle and data-evaluation events contain
   neither nested secret value and retain their redaction markers. The
   reopened snapshots are also rendered through `WorkflowRunViewer`; the event
   evidence table contains redaction metadata and neither secret value. This
   does not invoke an executor or MCP service during rendering. This is
   not an end-to-end confidentiality guarantee: arbitrary
   downstream nodes and Sequence aggregation events can still record derived
   values; static literals remain in workflow definitions;
   approval events retain exact plaintext input under Decision 0075; agent-loop
   tool evidence and old histories keep their existing shape. Nested pin proof
   must still cover nested path metadata and these residual boundaries.
5. Define explicit migration and schema-drift behavior. Existing top-level
   pin IDs, definitions, hashes, package behavior, and old-reader interpretation
   must remain stable; no discovery refresh may silently rewrite authored pins
   or connections.

Approval-wrapped direct MCP calls have a separate exact-action evidence path:
`approval.requested` currently stores the resolved input so a reviewer can
inspect what they are approving. Nested-pin authoring for those calls remains
gated until the protected payload lifecycle and exact-action review path are
resolved in [Decision 0075](0075-approval-payload-privacy.md).

If these gates require a different value/evidence contract, return for another
architecture review before broadening the proof. A successful proof does not
by itself establish general nested pins, array-item pins, unrestricted JSON
Schema support, or Blueprint parity.

## Evidence and follow-up

The first proof-only implementation adds an internal candidate mapper for one
required, closed object containing scalar leaves, reachable through at most two
required, closed object segments. Other static fields, including bounded arrays,
remain intact. Property segments use RFC 6901 escaping and nested pin IDs derive
from the exact case-sensitive pointer using SHA-256. Focused MCP tests cover
delimiter escaping, literal-dot distinction, rename/case identity changes,
property reordering, requiredness, bounds, and rejection of open or multiple
nested objects. An internal reconstruction proof combines connected scalar
leaves with static JSON, gives connected leaves precedence while preserving
sibling values, and validates the complete result against the MCP input schema.
Invalid types, bounds, missing required values, and unknown pin IDs fail with a
generic diagnostic that does not echo values. A real stdio broker test dispatches
the reconstructed nested object, rejects invalid argument values and changed
pinned contracts, and enforces a missing node grant. The mapper is internal and
is not used by descriptor projection, workflow persistence, runtime admission,
or Studio. Broker evidence for the nested request is checked for redaction, and
runner regressions confirm configured nested direct-MCP values do not appear
in node lifecycle or data-evaluation events, both at the observer callback and
after File/SQLite persistence and reopen. The regression renders each reopened
snapshot through `WorkflowRunViewer` and verifies that the visible event table
shows redaction metadata without either secret value. Rendering uses recorded
evidence only; it does not invoke an executor or MCP service. These runner
tests use the existing static JSON argument path; nested field pins themselves
remain unadmitted. A
separate real stdio fixture advertises a schema with unsupported `format`
metadata, and the broker rejects it during discovery. This closes the bounded
broker/schema/grant proof for the tested shape. Gate 4 is now proven for this
bounded direct-MCP evidence path through File/SQLite reopen and rendered
inspector playback. Old-definition/package compatibility remains unproven, so
gate 5 and the overall proof and authoring stay gated.

The current implementation already supports nested MCP values on JSON pins and
retains normalized nested schema metadata. It does not yet provide the
path-level pin contract, end-to-end evidence privacy, or migration guarantees
above. Track the implementation and verification gates in the [Blueprint parity matrix](../blueprint-capability-matrix.md)
and [roadmap](../roadmap.md#planned-phase-blueprint-inspired-authoring-contracts-and-ergonomics).
