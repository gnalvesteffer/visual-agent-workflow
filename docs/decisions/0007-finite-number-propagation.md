# Finite number propagation into parameters and pure functions

**Status: Accepted and shipped as a bounded contract**
**Scope: Blueprint-inspired authoring contracts**  
**Decision date: 2026-10-27**

## Context

Schema 1.40 established a finite `System.Double` value for Number constants
and top-level direct-MCP pins, but deliberately left workflow parameter getters
and pure graph-function ports on their existing string/Boolean/integer contract.
Blueprint-style typed composition is incomplete until the same scalar can cross
those two existing pure-data boundaries.

## Contract gate

Schema 1.41 adds only finite-number propagation across the existing typed-MCP pin boundary:

- `number` workflow parameters may feed a typed Workflow value node.
- Pure graph functions may declare `number` inputs and outputs and may contain
  Number constants and number-to-number body edges.
- Existing schema 1.40 direct-MCP pin graphs can be explicitly promoted so a
  finite `number` workflow parameter can feed a persisted `number` MCP pin.
- Compatibility remains exact identity: only `number` connects to `number`.
- Values are finite IEEE-754 `System.Double`; NaN and both infinities are
  rejected at declaration, override, evaluation, serialization, and runtime
  boundaries.
- Parameter text is parsed invariantly without trimming. The accepted grammar
  is a bounded, nonempty decimal floating-point form with an optional ASCII
  sign, optional decimal point, and optional exponent (`e`/`E` with an optional
  ASCII sign and at least one ASCII digit). Whitespace, `NaN`, `Infinity`,
  culture-specific separators, and non-ASCII digits are rejected. Overflow and
  underflow that produce a non-finite result are rejected.
- Negative zero is accepted and its sign is preserved through typed evaluation
  and JSON round trips. Canonical numeric evidence uses round-trip invariant
  formatting; parameter source text remains unchanged for compatibility.

## Explicit non-goals

Schema 1.41 does not add numeric operators, implicit conversions, nested MCP
numeric pin expansion, generic workflow-output number support, mutable
variables, containers, Sequence, or effectful graph functions. Existing
schemas and their canonical JSON/hashes retain their behavior and do not
reinterpret number values.

## Required proof

The shipped slice is covered by legacy/1.40 compatibility tests, schema gating,
parameter default/override parsing, lexical and finite boundaries, exact pin
compatibility, typed numeric evidence, negative-zero preservation, explicit
1.40-to-1.41 promotion, and unchanged 1.40 JSON-number emission. Invalid
drafts may be saved but cannot publish or execute. Numeric operators,
conversions, containers, and generic numeric workflow outputs remain future
parity work.
