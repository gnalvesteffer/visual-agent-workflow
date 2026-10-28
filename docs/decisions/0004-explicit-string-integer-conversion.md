# Explicit string-to-integer conversion boundary

**Status: Accepted with bounded scope**  
**Scope: Blueprint-inspired typed data flow**  
**Decision date: 2026-10-27**

## Context

Blueprint-style graphs need visible conversion nodes rather than implicit coercion. The workflow engine already has typed string, Boolean, integer, and JSON values, but no portable conversion from caller text to the signed 64-bit integer graph.

## Decision

Add `core.convert.string-to-integer` only in schema **1.39**, a new typed explicit-graph schema. Schema 1.38 remains reserved for the deferred Sequence decision. Existing schemas 1.0–1.37 remain byte-compatible and reject this node.

The node has exactly one required `in: string` data input and one `out: integer` data output. It has no execution pins, implicit conversions, defaults, capability grants, or error route. It is evaluated lazily when a downstream value requests it. A failed conversion produces no value and fails the requesting run; it does not activate any downstream execution route.

The portable grammar is strict: maximum 20 UTF-16 code units; an optional one-character ASCII `+` or `-`, followed by one or more ASCII decimal digits; leading zeros and signed zero are accepted; whitespace, separators, decimal points, exponents, non-ASCII digits, empty input, sign-only input, overlength input, and signed 64-bit overflow are rejected; parsing is invariant and performed only after lexical validation.

Failures use stable bounded evidence (`code`, parser version, reason, and input length), not exception text. The node is intentionally unavailable inside immutable graph-function bodies and checkpoint recovery until those separate contracts define their own schema/version and evidence requirements.

## Required proof

The implementation must cover parser boundaries, schema rejection, typed port compatibility, canonical round trips, lazy unused/repeated consumers, failure attribution, package/reuse preservation, and passive playback without reevaluation. Studio must expose the node only for schema 1.39 and preserve the explicit conversion in save/reload/inspection flows.

## Consequences

This is an explicit conversion, not a general expression system. Culture-sensitive parsing, trimming/coercion, decimal or arbitrary-precision results, fallback values, typed error outputs, and graph-function support are deferred to separately reviewed contracts.
