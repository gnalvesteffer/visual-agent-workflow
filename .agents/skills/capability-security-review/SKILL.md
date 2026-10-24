---
name: capability-security-review
description: Use when changing MCP access, node permissions, credentials, approvals, tenant authorization, imported content, or custom assembly isolation.
---

# Capability security review

Read [project-guide](../project-guide/SKILL.md) and the capability/extension sections of [the design](../../../docs/design.md).

## Trace authority

Trace an invocation from authenticated actor and workspace through published workflow revision, node/prefab scope, worker identity, tool broker, and MCP server. Connected servers are not universal grants. Enforce selected tools and resource/argument constraints on the backend for every invocation.

- Compute effective access from explicit host grants and applicable parent/node restrictions. Nested calls must not amplify authority.
- Do not trust client-supplied tenant, node, invocation, or approval identities. Bind approval to the exact server-recorded action and authorized actor.
- Skills, shared workflows, imports, and prefabs declare requirements; they cannot grant permissions or install executable assemblies automatically.
- Logical bindings resolve to host-owned credentials. Keep secrets out of definitions, browser data, logs, payload previews, exports, and examples.
- Treat prompts, MCP metadata/results, imported JSON, and extension output as untrusted. Validate schemas, limits, safe rendering, paths, and resource references.

## Verify the isolation boundary

A .NET interface, restricted context, Roslyn, or AssemblyLoadContext is not a sandbox. User assemblies must not enter the UI/orchestrator process. Review worker identity, broker authentication, direct network denial, filesystem restrictions, resource limits, and absence of host secrets. State residual risk and trust assumptions.

Use controlled fixtures to exercise cross-node and cross-tenant access denial, guessed tool names, revoked grants, approval reuse, and nested permission escalation when affected. For worker changes, test attempted direct I/O under actual isolation. A mocked denial does not establish containment. Never probe unrelated public systems.

Report concrete threats, evidence, mitigation, and remaining limitations. Do not claim production security from a code-only review.

The implemented MCP boundary is `VisualAgentWorkflow.Mcp/McpToolBroker`: node requirements intersect host policy on each call. Registrations default to non-read-only and are denied; the development host explicitly enables only two pure fixture tools. Tool connections are host code, never imported definition fields. Run `VisualAgentWorkflow.Mcp.Tests` for real stdio, denial, drift, revocation, bounds, and timeout checks. A trusted stdio child with a restricted environment is not a sandbox. Preserve schema 1.0 hashes when extending optional node configuration.

Explicit MCP approval is a workflow gate, never a broker grant. Match the request ID and exact recorded action fingerprint on the backend, derive actors from trusted server context, deny stale/cross-run/opposite decisions and keep observation endpoints read-only. Broker authorization/contract checks still run after approval. Approval nodes do not guard tools selected inside agent loops or mandate approval on workflows that omit the node.

Provider credentials belong in host secret accessors, never options records, workflow artifacts, catalogs or browser fields. Provider SDK exceptions can contain response bodies and credentials: retain only safe categories/status, without raw inner exceptions. Preserve bounded response reads, disabled redirects/retries, and node-tool preflight before client creation.
