# Decision 0032: bounded agent tool-failure feedback

Status: implemented

## Context

An MCP server can reject a call after receiving it and return an explicit
protocol result marked as an error. Treating that known failure as an immediate
agent failure prevents a model from correcting invalid arguments or choosing a
different assigned tool. Treating every exception as recoverable would be
unsafe: a timeout, transport failure, cancellation, authorization failure, or
lost response can leave an external side effect uncertain.

## Decision

Keep the existing three-way disposition contract:

- `RecoverableFeedback` is limited to an opted-in server-declared MCP failure or
  a trusted host classifier that identifies a definitive server failure.
- `OutcomeUnknown` remains the disposition for post-dispatch uncertainty,
  including timeout, cancellation, transport, and connection failures.
- `Fatal` remains the disposition for authorization, contract, schema, budget,
  unsupported-result, and other non-recoverable failures.

For `RecoverableFeedback`, the broker does not retry the tool. It records the
failed tool-call identity and returns bounded, sanitized feedback to the model.
The agent may receive at most three such feedback messages in one invocation;
the existing model-turn, token, timeout, and MCP invocation limits still apply.
After the bound is reached, execution fails deterministically. The feedback
includes the broker's already-bounded safe failure detail so the model can
correct arguments without exposing unbounded provider or server output.

Read-only reconciliation treats a `tool.failed` event with the explicit
`RecoverableFeedback` disposition as settling that failed attempt. It continues
to report authorized failures without that disposition, and all
`tool.outcome-unknown` events, as unresolved external work. This distinction is
evidence classification only; it grants no permission and authorizes no retry.

## Compatibility and security boundary

The exception enum, workflow JSON, tool grants, pinned contracts, and event
kinds remain unchanged. Recovery remains host-owned and opt-in; workflow data
cannot enable it. A host classifier must reject cancellation, timeout,
transport, authorization, and contract failures. The model chooses any next
call, so a failed write-like tool is never retried automatically by the host.

## Verification

Focused MCP tests cover one corrected call, multiple bounded failures, and
correlated recoverable evidence. Core reconciliation tests verify that only an
explicit recoverable disposition settles a failed attempt; authorized unknown
failures remain unresolved. Existing broker tests retain the fatal and
outcome-unknown boundaries.
