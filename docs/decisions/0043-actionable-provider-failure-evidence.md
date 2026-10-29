# Decision 0043: preserve actionable provider failure guidance

## Status

Accepted and implemented as a bounded debugging/evidence increment.

## Decision

Agent provider failures expose a safe, provider-independent user message derived
only from the failure category and optional HTTP status. Agent failure evidence
and structured provider-error outcomes use that message; Studio connection tests
use the same mapping. The message distinguishes authentication, rate limiting,
service unavailability, invalid request/model settings, invalid responses, and
transport failures.

The implementation deliberately excludes provider response bodies, URLs,
credentials, headers, and raw exception details. No automatic retry behavior is
changed, and the failure kind/status remain separately recorded for diagnostics.

## Consequences

When a local model server cannot load a configured model, users now get guidance
to check the model ID and compatibility instead of only seeing “provider request
failed.” The bounded mapping is portable across OpenAI-compatible endpoints and
cannot disclose vendor payloads through recorded evidence. More detailed
provider-specific diagnostics would require a separate redaction and retention
contract.
