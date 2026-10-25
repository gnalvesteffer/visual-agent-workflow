# Local runtime hosting API

The repository exposes an additive DI helper for .NET 10 Generic Hosts and ASP.NET Core hosts. It registers the existing single-process development runtime. It does not add production durability, authentication or a published NuGet SDK.

```csharp
using Microsoft.Extensions.Hosting;
using VisualAgentWorkflow.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLocalWorkflowRuntime(".local/my-service-runs");
using var host = builder.Build();
await host.StartAsync();

// Resolve LocalWorkflowRuntime for exact-revision starts, or IWorkflowClient
// and IWorkflowRunControl for ordinary invocation/observation/control.
// The host owns triggers, artifact approval, tool/provider configuration,
// run authorization, lifetime, and explicit shutdown.
```

For a complete executable invocation and stop sequence, use the [service sample](../samples/WorkflowService/README.md). The sample validates a deployment pin and dependencies before admitting a run. Low-level direct execution through `WorkflowRunner.ExecuteAsync` remains available without the helper or a hosted queue.

## Registration behavior

`AddLocalWorkflowRuntime(string journalDirectory)` registers a lazy fallback `FileRunJournal` and `IWorkflowRunStore` alias, one `LocalWorkflowRuntime`, its `IWorkflowClient` and `IWorkflowRunControl` aliases, and the same runtime as an `IHostedService`. The directory is normalized at registration and created lazily when the journal is resolved. Empty paths fail immediately. Repeated calls with the same normalized path do not add another worker; a conflicting path fails instead of silently changing storage. Directory-path equality follows platform case conventions; it does not resolve symlinks or provide cross-process locking.

Existing journal/store/runtime/client/control registrations are preserved. An explicit `IWorkflowRunStore` determines runtime storage; otherwise the existing `FileRunJournal` registration or lazy file fallback is used. See [run storage](run-storage.md) for SQLite registration and the matching read-only observer. A host that supplies alternate clients must ensure their controls and ownership match. Register host-owned `INodeToolBroker` / `IAgentNodeExecutor` services to enable those node types; there is no permissive default provider or tool grant. DI registration alone is not dependency preflight or authorization.

The optional [OpenAI provider library](model-providers.md) can supply host-owned factories; it depends on Core/AgentFramework, not Blazor/Studio, and is not automatically registered by this helper.

There is no automatic authoring catalog, SQLite database, published client, UI component, endpoint, credential lookup or live model registration. Studio explicitly adds its catalog and replaces client aliases with its publication-aware client; the service sample uses the runtime directly. Hosts must await start before accepting work, explicitly stop on shutdown, and give one runtime exclusive ownership of its run store. Pause/cancel semantics and admission bounds remain those in [run control](run-control.md).

## Consumption and distribution contract

Today, consume reviewed source using project references at a pinned repository commit and the committed SDK/package lockfiles. The sample is included in solution restore/build and CI verification. No Visual Agent Workflow package is published or declared stable by this milestone; do not assume the project assembly names identify available NuGet releases.

| Project | Role and dependency boundary |
| --- | --- |
| Core | Portable definitions, revisions, package/layout contracts and client interfaces; no infrastructure dependencies |
| AgentFramework | Real framework execution adapter; depends inward on Core |
| Providers.OpenAI | Optional host-configured Chat Completions factories; depends inward on Core and AgentFramework |
| Mcp | Host-registered MCP broker and development fixtures; depends inward on Core |
| Observation.Http | Optional bounded read-only HTTP observer; depends only on Core |
| Hosting | Optional local runtime/journal and separate SQLite catalog; currently carries ASP.NET Core and SQLite transitively |
| Blazor | Optional reusable editor/viewer components; depends only on Core |
| Studio | Development composition app; unnecessary for service execution |

The hosting helper is additive: existing constructors and direct execution signatures continue to work. Workflow definition, layout, package and catalog schemas remain unchanged. The optional run database has its own schema version; file snapshots have an explicit legacy read path. See [storage compatibility](run-storage.md). Deployments retain exact revision/content identities; DI registration does not create publication lineage or silently upgrade artifacts. Published-library versioning, package metadata/licensing decisions, compatibility baselines, minimal worker dependency separation, local package-consumer verification and release automation remain prerequisites for a supported NuGet distribution. External package publication is a separate release action.

## Verified scope

Registration tests cover lazy creation, client/worker identity, duplicate registration, conflicting/empty paths, host overrides and Generic Host shutdown of a paused continuation. The standalone smoke check runs direct/queued pure and MCP artifacts without Studio; Studio's API/browser/control checks verify the same registration path in the IDE. These tests do not establish distributed coordination, crash recovery, request authentication, live-provider reliability or multi-tenant isolation.

## Explicit deterministic checkpoint host

`CheckpointWorkflowHost` is independent of this DI helper and queue. It requires SQLite and admits deterministic graphs only, saving native framework state and explicitly resuming an exact saved suspension in a new host. Built-in ordinary/checkpoint hosts honor the same local exclusive owner handle. See [checkpoint recovery](checkpoint-recovery.md); the helper does not enable it automatically.

## Optional observation endpoint

Runtime registration alone still maps no endpoints. Hosts can explicitly register an `IWorkflowRunObserver` and call `MapWorkflowObservation` with a named authorization policy. The API is read-only and separate from workflow control. See [consumer observation](observation.md).

## Optional approval capability

Local-runtime registration now also supplies `IWorkflowApprovalClient` as an alias of the same worker, preserving existing host registrations. `LocalWorkflowRuntime` has a trusted decision overload accepting a server-derived actor. Direct adapter execution without a coordinator cannot auto-approve a workflow. The existing sample direct mode has no approval coordinator; approval-enabled consumers must use the controlled runtime or implement the trusted gate. See [approvals](approvals.md).
