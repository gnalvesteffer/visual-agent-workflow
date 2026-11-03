# Decision 0068: Typed array workflow parameters

**Status:** Implemented in schema 2.0
**Date:** 2026-11-02
**Scope:** Bounded primitive-array parameters, overrides, getter values, and text rendering

## Context

[Decision 0066](0066-unified-typed-array-values.md) establishes immutable,
homogeneous primitive arrays, and [Decision 0067](0067-typed-array-workflow-boundaries.md)
uses JSON text at workflow input/output boundaries. Parameters are also carried
through existing string maps, so array parameters can preserve those public APIs
while adding exact typed getter pins. Pure graph-function ports remain a separate
proof because they have independent snapshot and evaluation contracts.

## Decision

Current schema 2.0 supports only `array<string>`, `array<boolean>`,
`array<integer>`, and `array<number>` parameters. `array<json>` is not a
primitive array parameter. Historical definitions and hashes are unchanged;
there is no parameter-specific schema gate or promotion step.

Defaults are preserved exactly as authored in the workflow definition. Defaults
and run overrides are JSON text and are validated by the shared typed-array
parser. The UTF-8 encoding of the supplied text is limited to 16,384 bytes; the
array is limited to 64 items. Surrounding JSON whitespace is accepted and
counts toward the byte limit. Nulls, nested values, malformed JSON, non-array
roots, mismatched primitives, out-of-range integers, and non-finite numbers are
rejected. Empty arrays are valid. A caller override that is invalid is rejected
before run admission.

Before admission, resolved array values are rewritten to compact canonical JSON
and stored in the existing string-valued parameter map and `RunHeader`. Strings
and Booleans use the platform JSON serializer; integers use invariant signed
64-bit decimal; finite numbers use round-trip invariant double formatting.
Insignificant whitespace is removed and negative zero is normalized to `0`.
The workflow definition keeps the original default text, so equivalent authored
defaults remain distinct definition content and retain their package/hash
identity. Prompt and text-template interpolation use the same compact
canonical representation as the resolved run map.

The `core.workflow-value` node exposes exactly the parameter's `array<T>` type
in schema 2.0. Exact matching typed graph edges may carry it into already
supported consumers, including the typed workflow Output boundary. Child
workflow parameter mappings require exact type compatibility and reuse the
resolved canonical JSON text. `RunHeader` and existing client APIs remain
string-valued. Runtime values and data-evaluation evidence use bounded typed
JSON arrays; playback reads the recorded evidence and does not resolve current
defaults or re-evaluate the graph.

This decision does not extend typed arrays to function ports, workflow
interfaces, direct MCP arguments, variables, execution-region state, or
checkpoint state. Those placements require their own validation and runtime
proofs. Arrays remain non-secret: authored defaults are packaged and effective
values are plaintext run evidence under the existing parameter policy.

## Verification

- Core validates current-schema primitive arrays and rejects `array<json>` and
  historical-schema placements.
- Resolution tests cover the four primitive types, canonical defaults and
  overrides, invalid JSON and item types, integer and finite-number bounds,
  negative zero, 64/65 items, and UTF-8 payload bounds.
- Getter pins preserve exact array type; package serialization preserves
  authored defaults and definition identity.
- Runtime tests verify typed getter evaluation, canonical Output text, and
  recorded typed data evidence. Parameter maps remain compatible with calls,
  prompts, templates, packages, and passive playback.
