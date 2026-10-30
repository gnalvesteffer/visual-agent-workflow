# Decision 0059: explicit integer-to-string conversion

## Status

Accepted and implemented in schema 1.58.

## Decision

Add `core.convert.integer-to-string` as a pure, visible data node with one required signed-64-bit integer input and one string output. It has no execution pins, configuration, host capability, or implicit coercion. Formatting uses invariant decimal notation, including the exact Int64 minimum and maximum values, and is bounded to 20 UTF-16 code units.

Schema 1.58 is an explicit compatibility boundary. Studio exposes guarded promotion and typed-wire insertion. Runtime evaluation is lazy, reuses the existing data-evaluator cache, and records typed data evidence for playback. The node is not admitted to graph-function bodies or checkpoint recovery.

## Rationale and limits

Blueprint-style typed graphs need visible conversions, but a general coercion system would make compatibility, failure, and replay ambiguous. This slice proves one deterministic conversion; additional numeric and container conversions require separate contracts.

## Verification

Core covers invariant formatting, Int64 boundaries, descriptor/schema admission, and atomic insertion. Agent Framework covers lazy execution, typed output, and recorded evidence. Studio exposes the schema-gated library and context insertion actions.
