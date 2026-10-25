# Workflow parameters

Schema 1.6 adds portable string parameters for workflow-specific configuration. Schema 1.8 adds scalar types (`string`, `integer`, `number`, `boolean`, and bounded `choice`) with validated defaults and overrides. Parameters remain immutable definition data with stable IDs and display names. A `core.workflow-value` node reads one resolved parameter and replaces the incoming string with its canonical text value, making the data path explicit in the graph.

Typed parameters do not add mutable variables or arbitrary object ports. Every value must be treated as non-secret because local run histories record it in plaintext. Schema 1.8 can expose a choice parameter as an agent's reasoning-effort setting, limited to `default`, `low`, `medium`, and `high`; provider support is checked at runtime. Schema 1.7 also supports deterministic `core.text.template` nodes: ordered literal, incoming-text and declared-parameter parts build one bounded string without expression evaluation. Referenced parameters are copied and remapped when a reusable pipeline containing a template node is inserted.

## Definition contract

The optional `parameters` property is an object keyed by parameter ID. IDs contain 1–64 ASCII letters, digits, dashes, or underscores. A definition may contain at most 64 parameters; display names are 1–128 characters, defaults are bounded at 16,384 characters, and combined IDs/names/defaults are bounded at 128,000 characters to remain inside the workflow JSON size limit. The optional `type` field defaults to `string`. Choice parameters require 1–32 unique values, each at most 128 characters.

```json
{
  "schemaVersion": "1.6",
  "workflowId": "greeting-flow",
  "entryNodeId": "input",
  "nodes": {
    "input": { "type": "core.input", "typeVersion": "1.0.0" },
    "greeting": {
      "type": "core.workflow-value",
      "typeVersion": "1.0.0",
      "parameterId": "greeting-text"
    },
    "output": { "type": "core.output", "typeVersion": "1.0.0" }
  },
  "connections": {
    "input-to-greeting": {
      "source": { "nodeId": "input", "portId": "out" },
      "target": { "nodeId": "greeting", "portId": "in" },
      "kind": "data"
    },
    "greeting-to-output": {
      "source": { "nodeId": "greeting", "portId": "out" },
      "target": { "nodeId": "output", "portId": "in" },
      "kind": "data"
    }
  },
  "parameters": {
    "greeting-text": {
      "displayName": "Greeting text",
      "defaultValue": "Hello",
      "type": "string"
    }
  }
}
```

Legacy workflow schemas continue to omit `parameters` and `parameterId` when unused; optional parameter fields remain omitted for the old string form, preserving canonical hashes. Editing helpers upgrade definitions to the minimum schema when adding new features and preserve newer schema versions through graph edits. Reusable workflow insertion copies parameters referenced by inserted value nodes, text-template nodes, agent prompts, and agent settings, then remaps conflicting IDs.

## Resolve, execute, and replay

At run admission, the host validates caller-supplied override IDs and typed values against the pinned definition, fills every missing value from its default, and enforces type, choice, and length constraints. Unknown IDs and out-of-range values are rejected. The fully resolved map is separate from the workflow definition and is written into `RunHeader.Parameters` before execution. The worker passes that same map to the Agent Framework adapter. Each value node records its actual output and downstream transfer like any other node. Playback reads stored evidence only; it does not resolve current defaults or make live calls.

```mermaid
sequenceDiagram
    actor Caller
    participant Host as Run host
    participant Store as Run store
    participant Worker as Workflow executor
    participant Viewer as Playback
    Caller->>Host: Definition revision, input, optional typed overrides
    Host->>Host: Validate IDs and types, fill defaults, enforce allowed values
    Host->>Store: Pin definition and effective parameter map
    Host->>Worker: Execute exact revision with resolved values
    Worker->>Store: Record node output and data transfer evidence
    Viewer->>Store: Read recorded snapshot and events
```

`LocalWorkflowRuntime` implements the additive `IParameterizedWorkflowClient` capability. Existing `IWorkflowClient` method signatures remain unchanged and still resolve definition defaults. Parameter-bearing runs require an `IParameterizedWorkflowRunStore`; built-in file and SQLite stores implement it. Older/custom run stores that do not implement the optional capability reject parameterized definitions rather than dropping audit values.

The Studio accepts overrides for selected published revisions and displays the effective values in the run inspector. The development HTTP endpoint accepts a `parameters` string map alongside `input`; consumer hosts should call the parameterized client directly or implement equivalent authenticated invocation policy. Text-template nodes resolve the same effective map used by agent prompts; their rendered result is captured in node and connection evidence for playback. Do not treat the unauthenticated local Studio endpoint as a production service boundary.

## Security and limits

Parameters cannot select an endpoint, create tools, or grant host capabilities. Workflow packages include parameter defaults and schemas but no run overrides or credentials. Current values are recorded verbatim in run headers and node/transfer events; there is no secret classification or redaction. Consumers must keep secrets in host-owned secret storage and should not pass them as workflow parameters. Graphs containing workflow-value nodes are not eligible for the deterministic checkpoint host unless parameter state is added to that recovery contract.
