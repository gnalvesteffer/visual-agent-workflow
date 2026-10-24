---
name: microsoft-agent-framework
description: Use when selecting or upgrading Microsoft Agent Framework .NET packages, implementing its adapter, integrating agents and MCP tools, or verifying workflow APIs and supported runtime behavior.
---

# Microsoft Agent Framework integration

Read [project-guide](../project-guide/SKILL.md). The adapter targets Agent Framework 1.26.0 on .NET 10. Tests cover linear/conditional string workflows, real stdio MCP tools, and `ChatClientAgent` function loops with a deterministic `IChatClient`. See [roadmap and current scope](../../../docs/roadmap.md) for supported scope. These checks do not validate live LLM providers, parallel joins, loops or durability.

## Establish version-specific evidence

1. Inspect actual SDK pins, project targets, package references, central package versions, lockfiles, and existing adapter code. If absent, make the version selection explicit before implementation.
2. Consult [official documentation](https://learn.microsoft.com/en-us/agent-framework/) and [official .NET source and samples](https://github.com/microsoft/agent-framework/tree/main/dotnet). Prefer the tag/commit corresponding to the selected package. Main-branch samples may differ from released packages.
3. Verify package identity, release status, target-framework compatibility, required hosting services, and provider dependencies. Do not infer .NET support from a Python example or copy Semantic Kernel/AutoGen APIs as Agent Framework APIs.
4. Compile a minimal representative integration against the actual package set before making a public API or persistence contract depend on it. Record version, source reference, verified behavior, and limitations in the relevant design decision or implementation documentation. If source access is blocked, state that limitation rather than claiming verification.

## Keep the adapter boundary

Translate platform-owned definitions into framework agents, executors, connections, and supported execution mechanisms. Keep framework runtime instances and provider-specific types out of serialized documents and public UI contracts.

For each visual primitive, document its mapping, input/output types, routing, completion, error propagation, and output behavior. Check current samples for agents in workflows, mixed agents/executors, conditional routing, parallel fan-out/fan-in, loops, and subworkflows. Reject unsupported mappings explicitly; do not silently substitute semantics.

Select provider clients through host configuration/DI. Keep model credentials server-side. The same node-scoped capability broker must mediate agent tool calls and extension calls. Verify how the chosen framework/MCP integration exposes tools without giving agents an unrestricted server client. MCP tool metadata and model output cannot authorize operations.

## Verify execution and observation

- Use the selected version's streaming/event APIs and preserve run, executor, invocation, attempt, and message correlation through translation. Check actual event payloads; completion events alone do not prove edge traversal.
- Keep browser/circuit cancellation separate from explicit run cancellation. Check ownership and disposal of agents, clients, streams, and long-lived runs.
- Verify human requests/tool approvals and continuation with persisted identities and state. Do not assume a default in-memory session store survives restart.
- Distinguish in-process execution, checkpointing, and durable hosting. Confirm which integration and storage support the required recovery behavior; framework checkpoint support does not establish safe external retries.
- Treat observability traces as diagnostic evidence, not an automatic substitute for the persisted audit/event contract. Check sensitive-data capture settings before enabling telemetry.
- Do not use DevUI as the production editor or authorization boundary. Its development/debugging role is distinct from our host-facing APIs and embeddable components.

Use [workflow-runtime-review](../workflow-runtime-review/SKILL.md) for failure/recovery validation, [capability-security-review](../capability-security-review/SKILL.md) for permission enforcement, and [audit-playback-review](../audit-playback-review/SKILL.md) for recording and reconstruction. These skills own their detailed checks.

## Package upgrades

Review release notes and affected source before upgrading. Compile and exercise representative mappings, streaming instrumentation, tool approvals, and recovery paths affected by the change. Inspect public API and persisted-checkpoint compatibility; do not assume checkpoints can resume across runtime versions. Apply [version-compatibility-review](../version-compatibility-review/SKILL.md) and document any required migration or continued support for older runtime artifacts.

Historical playback must remain independent of framework execution and available provider packages. Report builds, deterministic integration checks, real-provider checks, and unrun cases separately.

In the tested version, a workflow stream can finish normally when its execution token is cancelled. Check the token inside/after enumeration before interpreting no output as a failure. Preserve the active-provider cancellation regression in `VisualAgentWorkflow.Mcp.Tests`. Agent functions must delegate to the scoped platform broker rather than exposing raw SDK MCP tools. The deterministic fixture deliberately ignores instructions; never describe it as live model behavior.

Agent Framework 1.26.0 conditional edges were checked against the peeled `dotnet-1.26.0` tag `9c8a3514a15a702f81bfda456d80c7ccaa4d2712` and installed XML, then executed in adapter tests. Use a private source-node/port envelope to identify actual received merge connections; never infer delivery from predicate/branch completion. No extra scheduler or barrier is needed for the single-path exclusive-merge model. See [mapping and evidence](../../../docs/conditional-graphs.md).

Explicit-MCP approval uses the existing awaited executor plus the optional platform approval gate; it does not claim native durable request/checkpoint restoration. A reserved target invocation ID passes through the trusted private message envelope so tool evidence matches the exact reviewed action. Preserve the fail-closed direct-runner test and real scripted-agent/approval/MCP proof; provider-selected tool calls inside agent loops remain outside this approval feature.

The optional OpenAI adapter is pinned to OpenAI 2.14.0 and Microsoft.Extensions.AI.OpenAI 10.10.1, matching the checked Agent Framework tag. Its complete-response SDK path is protocol-verified, not credentialed live-service qualified. The bridge JSON-encodes string function results; protocol fixtures must decode that representation rather than changing production MCP results. Preserve host-owned fresh clients, bounded bodies and disabled SDK retries. See [provider contracts](../../../docs/model-providers.md).
