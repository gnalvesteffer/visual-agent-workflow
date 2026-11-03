# Decision 0071: Permit direct MCP steps in unified-schema parallel branches

## Status

Accepted for unified schema 2.0; historical schemas retain their prior
validation and execution behavior.

## Context

The current explicit graph runtime already dispatches direct MCP nodes through
the node-scoped broker and executes static parallel regions concurrently. The
schema-2.0 validator nevertheless rejected MCP nodes inside those regions,
blocking deterministic tool calls that do not need an Agent. The Blueprint
parity objective favors composing existing node capabilities in the current
graph format rather than introducing another promotion step.

## Reviewed contract

Three independent review roles considered the change. The adjudicator returned
**REVISE**, requiring evidence and explicit bounds for concurrent calls,
per-invocation host grants and audit identity, denial/failure/cancellation,
join suppression, and historical-schema compatibility. The implementation is
limited to direct MCP nodes in unified schema 2.0 static branch paths. Each
branch still follows its matching named join input exactly once. The runtime
keeps its existing fan-out, barrier, bounded result, cancellation, and
failure behavior. Every tool invocation passes through the ordinary
node-scoped broker; branch membership or metadata grants no permission.

Denial or failure aborts the run without a partial join value. Cancellation
propagates to active calls and produces no join result. An already completed
external side effect in another branch cannot be rolled back. Approval nodes,
loops, nested forks, and other unsupported control-flow nodes remain invalid
inside these static branches. Branch-local Approval routing is specifically
excluded: the guarded-tool route has not been proven end-to-end.

## Compatibility and evidence

Historical schema definitions keep their existing admission rules, hashes,
and runtime behavior. The new capability is only accepted by schema 2.0; no
schema bump or promotion action is introduced.

Core regressions cover unified-schema acceptance, approval exclusion, and
historical-schema rejection. Agent Framework regressions execute two concurrent
direct MCP calls and verify invocation/node attribution and persistence through
both file and SQLite stores. Separate tests cover denial, failure, and
cancellation, asserting that no join result is published. These tests use
controlled brokers; host grant enforcement remains the responsibility of the
production broker and is not replaced by branch validation.
