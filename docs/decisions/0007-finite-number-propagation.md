# Finite number propagation into parameters and pure functions

**Status: Accepted contract gate; implementation in progress**  
**Scope: Blueprint-inspired authoring contracts**  
**Decision date: 2026-10-27**

## Context

Schema 1.40 established a finite `System.Double` value for Number constants
and top-level direct-MCP pins, but deliberately left workflow parameter getters
and pure graph-function ports on their existing string/Boolean/integer contract.
Blueprint-style typed composition is incomplete until the same scalar can cross
those two existing pure-data boundaries.

## Contract gate

Schema 1.41 adds only finite-number propagation:

- `number` workflow parameters may feed a typed Workflow value node.
- Pure graph functions may declare `number` inputs and outputs and may contain
  Number constants and number-to-number body edges.
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

The implementation is not considered shipped until tests prove legacy and 1.40
round trips/hashes, schema gating, parameter default/override parity, lexical
and finite boundaries, exact pin compatibility, graph-function hashes and
packages, lazy evaluation, numeric evidence, passive playback, and unchanged
direct-MCP JSON-number emission. Invalid drafts may be saved but cannot publish
or execute.
