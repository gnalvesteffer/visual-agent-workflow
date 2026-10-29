# Decision 0038: bounded authoring-only graph clipboard

## Status

Accepted as a Core proof slice. The contract is not yet exposed as Studio copy/paste UI.

## Decision

Introduce `GraphClipboardFragment` as a separate transient authoring contract. It is not a graph template, graph function, workflow package, published asset, runtime node, or capability grant.

The first slice accepts only current, stateless, pure data nodes with no execution pins, dynamic pins, effects, capabilities, mutable state, control-flow region, or origin provenance. Incoming and outgoing data edges must be represented by explicit typed boundaries. The fragment preserves only per-node positions; comments, sections, reroutes, camera state, and project metadata are intentionally excluded.

Serialization uses a strict versioned envelope, canonical content hash, bounded JSON size, duplicate/unknown-property rejection, and fail-closed version handling. Paste generates fresh node and connection IDs, validates every target binding and the complete resulting workflow, replaces only explicitly bound incoming data edges, and returns definition/layout together as one atomic result. Invalid or unsupported fragments do not mutate the target.

Host capability references, credentials, grants, approvals, endpoints, subscriptions, and live schemas are rejected rather than copied. Clipboard data is never accepted by runtime/package admission and its hash is integrity evidence only, not trust or provenance.

## Rationale and follow-up

This deliberately does not broaden the pure-template envelope from [decision 0023](0023-pure-graph-template-proof.md). Broader copy/paste for execution graphs, stateful nodes, capability nodes, partial control-flow regions, annotations, and browser clipboard UI requires separate boundary, security, compatibility, and evidence proofs.

