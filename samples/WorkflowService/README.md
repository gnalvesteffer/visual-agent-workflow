# Standalone workflow service sample

This .NET console/Generic Host consumer uses `AddLocalWorkflowRuntime` to register its backend worker and client/control interfaces. It has no Studio or Blazor dependency, opens no HTTP listener in its direct/queued/paused modes, and does not instantiate an authoring catalog. It demonstrates service-owned invocation with real Agent Framework execution and a trusted local MCP fixture. It is not a production worker SDK or durable scheduler.

From the repository root after the normal Release build:

```sh
dotnet run --project samples/WorkflowService --configuration Release --no-build -- direct samples/WorkflowService/artifacts/pure.deployment.json .local/service-direct "abc"
dotnet run --project samples/WorkflowService --configuration Release --no-build -- queued samples/WorkflowService/artifacts/pure.deployment.json .local/service-queued "abc"
dotnet run --project samples/WorkflowService --configuration Release --no-build -- queued samples/WorkflowService/artifacts/mcp.deployment.json .local/service-mcp "abc" --grant-demo-tool
```

The pure pipeline returns `CBA`; the MCP pipeline returns `cba`. Output is JSON lines: queued mode first reports an accepted, paused run identity, then resumes and reports the result. Its initial admission method returns before any node executes, where an integrating API/message handler could return control. Subsequent execution and observation belong to the host. There is no network API in this sample.

Use `paused` instead of `queued` to leave execution parked; Ctrl+C or SIGTERM stops the host and cancels the continuation. Exit codes: 0 completed, 1 configuration/dependency/execution error, 2 non-completed recorded outcome. Direct provider errors may return 1 after recording failure. Input and recorded payloads are plaintext; use disposable text. Give one runtime exclusive ownership of its run store. Set `WorkflowService__RunStore=sqlite` to opt into `runs.sqlite3` in the journal directory; the default remains file snapshots. Store selection does not migrate histories. See [run storage and upgrade notes](../../docs/run-storage.md). Restart interrupts unfinished work rather than restoring it. The journal/Hosting dependency still brings ASP.NET Core support and SQLite transitively; the sample creates no authoring database and requires no catalog. SQLite run persistence is opt-in.

`artifacts/*.deployment.json` is operator-owned configuration mapping an exported package to a reviewed definition hash and stable deployment revision ID. The sample reconstructs that exact identity with `WorkflowRevision.Rehydrate`; package hashes are not signatures or authenticated publication lineage. Deploy the manifest through your own trusted release process. Package paths resolve relative to the manifest, not the current directory. Replacing content requires a new reviewed hash and revision identity. Layout is validated by the package loader but does not drive execution.

The sample registers only the known read-only `demo.reverse` MCP fixture and no agent providers by default. It can opt into host-configured SDK providers through `WorkflowAgents:OpenAI`; see [model providers](../../docs/model-providers.md). `--grant-demo-tool` is host configuration and grants only workflow `service-mcp`, node `transform`, binding `demo.reverse`. A requirement alone grants nothing. Provider absence, missing bindings, denied node grants, contract drift, invalid artifacts and incorrect content pins are rejected before a run is created. Runtime MCP calls independently recheck policy/contracts. Pure workflows do not connect to or discover MCP servers.

To integrate your own reviewed artifact, export its package from Studio, create an operator-owned deployment manifest with its definition hash, and wire host-owned providers/bindings and policy in `DeploymentDependencies`. Do not let workflow content choose executable paths, supply secrets or grant capabilities. External models require explicit host configuration; no credentials or default live model are shipped. The local protocol smoke verifies both direct and queued SDK execution; credentialed live-service qualification remains outstanding.

Run `python3 scripts/service_smoke.py` after building. It starts child consumers directly, checks persisted events and negative dependency cases, and sends SIGTERM to its own parked child to verify host lifetime. Studio and a browser are unnecessary. See [local hosting registration](../../docs/hosting-sdk.md), [service integration](../../docs/service-integration.md) and [run control](../../docs/run-control.md).

## Opt-in observation host

The additional `serve` mode runs the consumer on loopback port 5100. Observation is disabled by default. Set `WorkflowObservation__Enabled=true` and supply a strong random `WorkflowObservation__Token` via environment/secret configuration to expose the authenticated read-only API. Studio can attach to it without hosting execution. See [consumer observation](../../docs/observation.md) for commands, named Studio sources, embedding and policy limits. `WorkflowService__Port` selects a separate loopback port for isolated samples.


## Deterministic checkpoint commands

`checkpoint DEPLOYMENT.json DATABASE_PATH INPUT COMPLETED_NODE_COUNT [--hold]` saves a deterministic native framework boundary. `recover DATABASE_PATH RUN_ID CHECKPOINT_ID` explicitly resumes that exact saved suspension in the same run/history, including from another process after owner loss. Both modes use the dedicated SQLite checkpoint host, not the normal local queue or provider configuration. Agent/MCP/approval nodes are rejected before admission. See [commands, ownership, migration and recovery limits](../../docs/checkpoint-recovery.md). The existing direct/queued/paused/serve modes retain restart interruption semantics.


Checkpoint/recover output includes a metadata-only reconciliation assessment. Optional observation mode also exposes the protected read-only assessment endpoint through the same store/observer. The assessment does not acquire ownership or execute recovery; it can report unresolved external work even for a terminal run. See [run reconciliation](../../docs/run-reconciliation.md).
