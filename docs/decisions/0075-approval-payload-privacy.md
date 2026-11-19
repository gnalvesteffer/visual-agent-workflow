# Decision 0075: Approval payload privacy and exact-action review

**Status:** REVISE; specify a protected approval-payload lifecycle before changing persisted evidence

**Date:** 2026-11-19

## Context

An Approval node captures the exact string input for the immediately following
MCP node. `WorkflowApprovalAction` stores that input and a SHA-256 input hash;
`ControlledRun` records the complete request in `approval.requested`, and
`WorkflowApprovalEvidence.Pending` reconstructs and validates the actionable
request from the event. The Studio approval panel displays the exact input so
the reviewer can inspect what they are authorizing. See [the approval
contract](../approvals.md).

This creates a direct MCP argument copy in ordinary run evidence, even if
`data.evaluated`, `tool.authorized`, or `tool.completed` later redact their
payloads. Removing the input from `approval.requested` without changing the
reader would break the approval flow; asking a reviewer to approve only a hash
would weaken exact-action review. Existing histories are append-only and may
already contain plaintext approval inputs.

## Adjudicated direction

The three-role review returned **REVISE**. Treat approval payload privacy as a
separate design gate. Preserve exact-action review while specifying who can
retrieve the action, how access is authorized, how the payload is bound to the
recorded request and eventual decision, what happens on process restart or
payload loss, and how legacy events remain readable. Do not change the current
approval evidence shape until that lifecycle has its own reviewed contract.

The review's suggestion to keep approval details in a live-only protected path
is a candidate, not an approved architecture. The development Studio's
placeholder actor and loopback restrictions are not a production
authentication boundary. A hash or fingerprint alone is not a substitute for
showing the exact action to the reviewer.

## Required contract before implementation

- The reviewer sees the exact bytes that will be dispatched, including the
  resolved MCP arguments, before approving.
- Approval binds the displayed payload to the root run/revision, nested call
  path, approval request, target node, reserved invocation, tool binding, and
  pinned contract. Missing, stale, or mismatched payloads fail closed.
- Ordinary run evidence distinguishes protected payload, absent payload, and
  legacy plaintext payload. New records do not silently look like empty input.
- The lifecycle specifies access authorization, storage ownership, retention,
  cancellation, terminal cleanup, process loss/restart, and store failure.
- Existing readers continue to interpret legacy events; no historical event
  is rewritten or presented as redacted when its contents are still plaintext.
- Tests cover file and SQLite histories, the live reviewer path, denied and
  stale reads, exact dispatched bytes, payload loss, and legacy playback.

## Parity dependency

The [nested MCP argument pin proof](0074-nested-mcp-argument-pins.md) may
continue on non-approval direct MCP routes after its own redaction and
compatibility gates pass. Nested pin authoring for Approval-wrapped MCP calls
remains gated until this decision's exact-action path is resolved. This
boundary does not claim that current run evidence, legacy history, or values
downstream from an MCP node are protected.
