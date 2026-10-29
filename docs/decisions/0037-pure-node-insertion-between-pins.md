# Pure node insertion between pins

**Status: Bounded implementation**
**Scope: Blueprint-style typed data authoring**
**Decision date: 2026-10-28**

The graph editor may offer a Blueprint-style “insert between” action when one
existing data connection is selected and a compatible unary pure node can bridge
its endpoints. This is an authoring transformation, not a new runtime or
conversion system.

## Contract

- The operation is keyed by the exact existing data connection ID. Stale,
  missing, execution, malformed, or ambiguous connections are rejected.
- The candidate must be a current, catalogued pure data-evaluation node with no
  effects or execution pins, exactly one data input, and exactly one data output.
- The source output type must exactly match the candidate input type, and the
  candidate output type must exactly match the original target input type.
- The operation is limited to explicit typed graphs and does not implicitly
  promote schemas, add capabilities, or alter execution connections.
- The original edge is replaced atomically by source → candidate → target;
  unrelated edges and the selected source/target semantics remain unchanged.
- The final definition must pass the existing schema-specific validator. A
  failure leaves the input definition unchanged.

Multi-input/output nodes, constants, capability nodes, dynamic contracts,
execution wires, implicit coercion, and occupied/stale endpoint repair are
explicitly outside this slice.

The Studio picker intersects schema eligibility with both endpoint types and
only offers candidates accepted by the Core operation. Definition and layout
changes are emitted as one editor history entry.
