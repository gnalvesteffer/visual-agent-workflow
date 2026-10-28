# Finite number addition

**Status: Accepted and shipped as a bounded contract**  
**Scope: Blueprint-inspired authoring contracts**  
**Decision date: 2026-10-28**

## Context

Schema 1.41 established finite `number` propagation through workflow
parameters, typed MCP pins, and pure graph-function boundaries. A small
deterministic operator is the next useful Blueprint-style composition proof,
but a general numeric language would expand the compatibility and failure
surface substantially.

## Contract

Schema 1.42 adds `core.number.add`:

- It is a pure data node with required `left` and `right` `number` inputs and
  one `out` `number` output.
- It accepts only finite IEEE-754 `System.Double` values. There is no implicit
  integer/number conversion, string parsing, or Boolean coercion.
- IEEE-754 addition is used for signed zero and underflow behavior. If the
  result is non-finite, evaluation fails with the stable diagnostic
  `number.add.nonfinite-result`.
- It has no execution pins and does not perform external effects.
- It is available in schema 1.42 direct-MCP data graphs and pure graph
  functions when the caller explicitly opts into the number-add contract.
- Existing schemas retain their behavior and reject the node; adding it to an
  editable schema-1.41 graph promotes the draft to schema 1.42.

The runtime records the ordinary typed `data.evaluated` evidence for a
successful result and attributes a non-finite-result failure to the arithmetic
node and the requesting active node. The MCP broker remains responsible for
the final tool contract and host authorization.

## Non-goals

This slice does not add comparisons, subtraction/multiplication/division,
implicit conversions, generic numeric workflow outputs, nested numeric MCP
objects/arrays, mutable variables, Sequence, or unbounded arithmetic graphs.

## Verification

Core tests cover schema gating, typed ports, promotion through node insertion,
function evaluation, overflow failure, and compatibility. Agent Framework
tests cover number-add evaluation into a typed direct-MCP argument and its
recorded data evidence. Studio builds verify number ports, constants, and the
operator are authorable in the function editor and schema-1.42 node picker.
