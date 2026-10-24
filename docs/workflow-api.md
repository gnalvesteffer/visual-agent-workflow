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
| `POST /api/workflows/{id}/runs` | Run the active published revision using `{ "input": "text" }` |
| `POST /api/revisions/{revisionId}/runs` | Run an exact published revision using `{ "input": "text" }` |

Each successful catalog mutation increments the generation, including publishing and activation. Read the resulting draft after publishing to obtain the updated generation. A stale generation returns HTTP 409 with expected/actual generations. The caller must reload and reconcile; retrying without reconciliation can overwrite intent. Invalid definitions or cross-workflow revision references return 400. Missing workflow/revision references return 404. Running a workflow with no active publication returns 409.

Publishing activates the new revision by contract. Restoring does not publish or activate anything. Neither action changes existing run snapshots. Published versions and their IDs are never replaced by activation or restore.

Existing `/api/runs`, `/api/runs/{id}`, `/api/runs/{id}/events?after=N`, and cancellation endpoints continue to provide local run execution and observation. `POST /api/runs` accepts an ad-hoc definition and creates an ephemeral revision identity, whereas the published routes pin a catalog revision.

`GET /api/tool-bindings` discovers descriptors for host-registered, approved read-only MCP bindings. It returns logical IDs, display names, server/tool identities, and contract fingerprints; it does not reveal connection factories or credentials. Selecting/importing a descriptor never grants access. The development host registers only `demo.uppercase` and `demo.reverse`. Schema 1.1 MCP nodes pin a `tool` object with `bindingId` and `contractHash` and keep string `in`/`out` ports. Runtime policy denial or contract drift fails the run and is recorded in its events.

`GET /api/agent-providers` lists host provider IDs, versions, display names, model identities, and whether each is deterministic. Schema 1.2 agent nodes use an `agent` object with `providerId`, `providerVersion`, `instructions`, and `tools` (an explicit array of binding/hash requirements). The development provider is `demo.scripted` version `1.0.0`. An empty tool array grants nothing and causes no MCP discovery by that agent. Agent runs use the same ad-hoc or published execution routes. Model request/response evidence and tool events are available through recorded run snapshots/cursors, not a separate execution API.
