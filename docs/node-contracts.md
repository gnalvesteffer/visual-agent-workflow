# Node contract reference

## Descriptor authority boundary

Resolved node descriptors provide a schema-neutral pin shape for authoring and
advisory conformance. They are not, by themselves, permission to admit a node
in a schema, validate family-specific topology, execute a capability, or replay
historical evidence. Those decisions remain owned by the schema validator,
runtime, host admission, and recorded revision boundaries. See [descriptor
authority boundary](decisions/0036-descriptor-authority-boundary.md).

For configuration-derived nodes, the descriptor resolver and compatibility
projections consume the same Core pin-shape catalog. This aligns pin identity,
type, and order without making the non-serialized descriptor a schema or
runtime authority.

`WorkflowDefinitionDescriptorAudit` provides a read-only audit for persisted
definitions. It resolves stored node configuration without live discovery,
checks descriptor pins against the Core projections, audits nested workflow
calls, and reports deterministic paths for unsupported versions, unresolved
descriptors, malformed metadata, and MCP mapping drift. It is advisory: it
does not admit, execute, grant, migrate, or replay a workflow.

`WorkflowNodeDescriptorResolver.ResolvePersisted` returns an explicit
`WorkflowNodeDescriptorResolutionKind` rather than collapsing unsupported nodes
into an empty pin set. Its resolved, unsupported-type, unsupported-version, and
malformed-configuration states are safe for future schema-qualified consumers;
live discovery remains outside persisted resolution.

This page documents the canonical metadata in
[`WorkflowNodeContractCatalog`](../src/VisualAgentWorkflow.Core/WorkflowNodeContracts.cs).
It is an authoring and review reference, not a replacement for schema-specific
validation or host capability policy. A node's portable definition never grants
the capability named by its contract.

## Metadata vocabulary

| Field | Meaning |
| --- | --- |
| Activation | How the runtime begins work for the node: `DataEvaluation`, `Execution`, `ExternalApproval`, `SchedulerFanOut`, `SchedulerBarrier`, or `ChildWorkflow`. |
| State | `Stateless`, `RunScoped`, or `HostOwned` state ownership. |
| Failure | The bounded failure disposition: propagation, route selection, termination, host wait, region abort, or child outcome propagation. |
| Evidence | The recorded evidence family used to explain the node: data evaluation, execution lifecycle, route decision, capability invocation, approval, parallel/Sequence progress, or child workflow. |
| Effects | Descriptive boundaries only: run-scoped state, host interaction, external capability, or child workflow. |
| Dynamic pins | Configuration-derived identity source. Dynamic pins are resolved from the persisted node configuration and optional transient discovery metadata; they are not inferred from display labels. |
| Authoring | Stable editor capabilities such as configurable, dynamic pins, context insertion, and inline editing. |

## Contract inventory

The following table is the current bounded contract inventory. “Data” and
“execution” describe the kinds of pins a family may expose; configuration may
change the exact typed pin set for dynamic families.

| Family | Category | Pins | Activation / state | Failure / evidence | Effects and authority | Limits or dynamic identity |
| --- | --- | --- | --- | --- | --- | --- |
| Input | Flow | Data + execution | Execution / run-scoped | Propagates / execution lifecycle | Caller value; no host authority | Configurable; context insertable |
| Output | Flow | Data + execution | Execution / run-scoped | Propagates / execution lifecycle | Workflow boundary; no host authority | Fixed terminal boundary |
| Uppercase, Reverse | Text | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure | Inline editable |
| String constant, Boolean constant, Integer constant, Number constant | Values | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure | Inline editable |
| Add numbers, Add integers | Values | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure | Bounded numeric inputs |
| String-to-integer | Values | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure | Strict conversion and checked integer bounds |
| Concatenate text, Compare strings, Compare integers | Text / Values | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure | Fixed typed inputs |
| Boolean NOT | Values | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure | Fixed Boolean input |
| Select JSON text, Project JSON fields, Text template | JSON / Text | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure | Bounded JSON/template configuration |
| Workflow parameter | Values | Data only | Data evaluation / stateless | Propagates / data evaluation | Reads immutable workflow-owned input | Configurable parameter reference |
| String condition | Flow | Data + execution | Execution / run-scoped | Routes / route decision | No host authority | Configurable predicate |
| Boolean branch | Flow | Data + execution | Execution / run-scoped | Routes / route decision | No host authority | Two fixed execution outcomes |
| Merge | Flow | Execution only | Execution / run-scoped | Propagates / execution lifecycle | No host authority | Bounded reconvergence |
| Terminate | Flow | Data + execution | Execution / run-scoped | Terminates / execution lifecycle | Explicit workflow outcome | Success/failure outcome |
| Approval | Interaction | Data + execution | External approval / host-owned | Waits for host / approval | Host interaction; requires host policy | Bounded approval action |
| Parallel fork, Parallel branch, Parallel join | Flow | Data + execution | Fan-out/barrier / run-scoped | Region abort or propagation / parallel progress | Run-scoped orchestration | Branch-count bound; `ParallelBranchId` dynamic pins |
| Collaboration rendezvous | Collaboration | Data + execution | Barrier / run-scoped | Region abort / parallel progress | Host-authorized collaboration boundary | Branch-count bound; `ParallelBranchId` dynamic pins |
| Sequence | Flow | Execution | Fan-out / run-scoped | Propagates / Sequence progress | Run-scoped ordered dispatch | Output-count bound; `SequenceOutputId` dynamic pins |
| Bounded loop and loop body | Flow | Data + execution | Execution / run-scoped | Routes / route decision | Run-scoped repetition | Maximum iteration bound |
| Agent | Capabilities | Data + execution | Execution / host-owned | Propagates / capability invocation | External model capability; host grant required | Route, output-token, total-token, and function-tool bounds; `AgentRouteId` dynamic pins |
| MCP tool | Capabilities | Data + execution | Execution / host-owned | Propagates / capability invocation | External tool capability; host grant required | Static argument bound; `McpArgumentId` dynamic pins |
| Workflow call | Composition | Data + execution | Child workflow / run-scoped | Child outcome propagation / child workflow | Calls a pinned child revision | Depth and expanded-node bounds |
| Function input, Function output | Functions | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure graph-function boundary | Interface-defined typed pins |
| Function call | Functions | Data only | Data evaluation / stateless | Propagates / data evaluation | Pure pinned computation | Body-node and node-visit bounds; `GraphFunctionPortId` dynamic pins; legacy and explicit Core port projections share the pinned function's ordered typed ports |

## Authority boundaries

The catalog is intentionally descriptive. Core validation remains the authority
for schema admission, graph reachability, edge degree, bounds, and dynamic
configuration. Runtime adapters remain the authority for host grants,
credentials, provider/tool availability, cancellation, and external effects.
Recorded evidence remains the authority for explaining what actually executed;
contract metadata must not be used to invent an event that was not recorded.

The static pin descriptor registry is a narrower, non-serialized subset of this
catalog. Its whole-registry drift checks are documented in
[decision 0024](decisions/0024-static-pin-descriptor-registry-hardening.md).
Dynamic/configured descriptor resolution is documented in [decision
0012](decisions/0012-dynamic-pin-descriptor-resolution.md).
