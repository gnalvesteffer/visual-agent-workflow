# Decision 0038: bounded authoring-only graph clipboard

## Status

Accepted as a bounded authoring slice. Core owns the strict contract, and Studio exposes
selection-aware copy/paste through the graph context menu for eligible pure data nodes.

## Decision

Introduce `GraphClipboardFragment` as a separate transient authoring contract. It is not a graph template, graph function, workflow package, published asset, runtime node, or capability grant.

The first slice accepts only current, stateless, pure data nodes with no execution pins, dynamic pins, effects, capabilities, mutable state, control-flow region, or origin provenance. Incoming and outgoing data edges must be represented by explicit typed boundaries. The fragment preserves only per-node positions; comments, sections, reroutes, camera state, and project metadata are intentionally excluded.

Serialization uses a strict versioned envelope, canonical content hash, bounded JSON size, duplicate/unknown-property rejection, and fail-closed version handling. Paste generates fresh node and connection IDs, validates every target binding and the complete resulting workflow, replaces only explicitly bound incoming data edges, and returns definition/layout together as one atomic result. Same-workflow source/target IDs may be carried as non-authoritative boundary hints so the Studio context-menu action can reconnect a copied node; paste still validates target ports and never treats hints as permissions. Invalid or unsupported fragments do not mutate the target.

Host capability references, credentials, grants, approvals, endpoints, subscriptions, and live schemas are rejected rather than copied. Clipboard data is never accepted by runtime/package admission and its hash is integrity evidence only, not trust or provenance.

## Rationale and follow-up

This deliberately does not broaden the pure-template envelope from [decision 0023](0023-pure-graph-template-proof.md). The Studio UI uses the canvas's existing box/Ctrl/Shift selection and context-menu commands; it does not copy execution wires, annotations, capabilities, stateful nodes, or cross-workflow dependency metadata. Keyboard shortcuts, explicit binding prompts, and project-aware transport require separate boundary, security, compatibility, and evidence proofs.

