# Development workflow API

These endpoints inherit the Studio's development-only loopback and browser-origin restrictions. They are not a production authorization API. Definitions and run payloads may contain sensitive data; use disposable local data until host access/redaction policies exist.

| Method and route | Behavior |
| --- | --- |
| `GET /api/workflows` | List saved drafts and their generation/head/active pointers |
| `GET /api/workflows/{id}` | Load a saved draft |
| `PUT /api/workflows/{id}/draft` | Save `{ "definition": { ... }, "expectedGeneration": N }`; initial creation requires 0 |
| `POST /api/workflows/{id}/publish` | Publish and activate using `{ "expectedGeneration": N }` |
| `GET /api/workflows/{id}/revisions` | List published versions newest first |
| `GET /api/revisions/{revisionId}` | Load an exact published version |
| `POST /api/workflows/{id}/restore` | Copy a revision to the draft using `{ "revisionId": "...", "expectedGeneration": N }` |
| `POST /api/workflows/{id}/activate` | Activate an existing published revision using the same request shape |
| `GET /api/workflows/{id}/audit` | Read catalog mutation audit entries |
| `POST /api/workflows/{id}/runs` | Run the active published revision using `{ "input": "text", "parameters": { "key": "value" } }` |
| `POST /api/revisions/{revisionId}/runs` | Run an exact published revision using `{ "input": "text", "parameters": { "key": "value" } }` |

Each successful catalog mutation increments the generation, including publishing and activation. Read the resulting draft after publishing to obtain the updated generation. A stale generation returns HTTP 409 with expected/actual generations. The caller must reload and reconcile; retrying without reconciliation can overwrite intent. Invalid definitions or cross-workflow revision references return 400. Missing workflow/revision references return 404. Running a workflow with no active publication returns 409.

Publishing activates the new revision by contract. Restoring does not publish or activate anything. Neither action changes existing run snapshots. Published versions and their IDs are never replaced by activation or restore.

The optional `parameters` map is available for schema-1.6 definitions. It contains string overrides only; omitted values use definition defaults. Unknown parameter IDs, oversized values and workflows without declared parameters are rejected before queueing. Resolved values are pinned in the run header for replay. Existing request bodies without `parameters` retain their behavior. See [workflow parameter contracts](workflow-parameters.md).

Existing `/api/runs`, `/api/runs/{id}`, `/api/runs/{id}/events?after=N`, and cancellation endpoints continue to provide local run execution and observation. `POST /api/runs` accepts an ad-hoc definition and creates an ephemeral revision identity, whereas the published routes pin a catalog revision.

`GET /api/tool-bindings` discovers descriptors for host-registered, approved read-only MCP bindings. It returns logical IDs, display names, server/tool identities, and contract fingerprints; it does not reveal connection factories or credentials. Selecting/importing a descriptor never grants access. The development host registers only `demo.uppercase` and `demo.reverse`. Schema 1.1 MCP nodes pin a `tool` object with `bindingId` and `contractHash` and keep string `in`/`out` ports. Runtime policy denial or contract drift fails the run and is recorded in its events.

`GET /api/agent-providers` lists host provider IDs, versions, display names, model identities, and whether each is deterministic. Schema 1.2 agent nodes use an `agent` object with `providerId`, `providerVersion`, `instructions`, and `tools` (an explicit array of binding/hash requirements). The development provider is `demo.scripted` version `1.0.0`. An empty tool array grants nothing and causes no MCP discovery by that agent. Agent runs use the same ad-hoc or published execution routes. Model request/response evidence and tool events are available through recorded run snapshots/cursors, not a separate execution API.

## Graph presentation

`GET /api/workflows/{workflowId}/design` returns `{ draft, layout }`. `PUT` to the same route accepts `{ definition, layout, expectedGeneration }` and saves both atomically. It shares the existing workflow generation and conflict response. `GET /api/revisions/{revisionId}/layout` returns the immutable published presentation (default positions for revisions predating layout capture). The existing definition-only endpoints remain supported. See [graph editor contracts and bounds](graph-editor.md).

The development HTTP host limits the entire design request (definition plus layout) to 1 MiB, independently of the per-artifact validation limits. Large integrations must choose host transport budgets explicitly; a layout cannot raise domain or capability limits.

## Portable package exports

`GET /api/workflows/{workflowId}/package` exports the saved executable definition, layout, derived requirements, and content checksums as package format 1.0. The design-capable catalog reads definition/layout consistently. `GET /api/revisions/{revisionId}/package` exports an exact immutable publication and its captured layout. Missing items return 404. These read-only routes do not execute anything or expose host credentials/policy grants. Package import in Studio is a local edit followed by normal explicit design save/publication; there is no HTTP package-install endpoint. See [reuse and sharing](reusable-pipelines.md).

## Optional pause/resume capability

The development host supports cooperative node-boundary control through `IWorkflowRunControl` / `IPublishedWorkflowRunControl`, paused starts, and HTTP pause/resume routes. Existing client interfaces remain supported. Controls preserve the same live run and pinned revision; browser disposal does not resume or cancel it. Host restart interrupts unfinished continuations. See [run-control semantics and requests](run-control.md).

## Consumer-owned observation API

The opt-in policy-protected observation API is a separate reusable GET-only route group, not an extension of the Studio development execution API. It exposes summary/snapshot/cursor reads and no mutation routes. See [consumer observation](observation.md).

## Conditional definitions

Schema 1.4 adds `core.condition` and `core.merge` version 1.0.0, true/false string output ports and a bounded condition configuration. Existing save/publish/run/package routes accept validated conditional graphs. Invalid wiring/predicates fail before execution. No new mutation route or catalog migration is needed. See [conditional contracts and compatibility](conditional-graphs.md).

## Approval controls

Schema 1.5 adds `core.approval` before an explicit MCP tool. `POST /api/runs/{id}/approvals/{requestId}/decision` accepts only `{ "actionHash": "…", "approve": true|false }`. The decision binds to recorded server-side action metadata; no actor can be supplied in JSON. See [approval routes, responses and development attribution](approvals.md). The consumer observation route group remains GET-only.

Schema 1.6 adds workflow string parameters and `core.workflow-value`; see [workflow parameter contracts](workflow-parameters.md). These values are ordinary audit data, not secrets.
