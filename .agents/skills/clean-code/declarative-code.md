# Write code that reads declaratively

This is repository-owned guidance shared by the C# and clean-code skills. Apply it when writing or reviewing implementation and tests. Code should communicate the intended result and domain rules directly, so readers can follow the behavior without reconstructing incidental bookkeeping.

## Express intent

- Name operations and predicates for their domain meaning. A caller should read the workflow of the task at a consistent level of detail. Extract a helper when its name explains a meaningful rule or responsibility; keep trivial operations inline when a wrapper adds no clarity.
- Prefer pure transformations, immutable records and `with` expressions, named predicates, pattern matching, and switch expressions when they make the result clearer. Use guard clauses to keep the main path visible.
- Use LINQ for straightforward filtering, projection, grouping, and ordering. Give intermediate results meaningful names when a chain becomes difficult to scan. Preserve meaningful ordering and make materialization deliberate rather than repeatedly enumerating deferred queries.
- Keep side effects explicit: readers must see authorization checks, persistence, transaction boundaries, awaited I/O, cancellation, and execution order. Do not hide them in predicates, properties, projections, or deferred queries. Prefer a clear loop for ordered effects; do not introduce concurrency as a stylistic refactor.
- Choose straightforward loops for stateful traversal, mutation, resource management, or performance-sensitive paths when they explain the algorithm better. Declarative readability is the goal; using LINQ or a one-line expression is not a requirement.
- Avoid nested ternaries, compressed one-liners, giant Boolean expressions, and chains that mix querying with mutations. Split distinct decisions into named rules when needed.
- Do not add a DSL, fluent framework, generic pipeline abstraction, or extra interface merely to make syntax look declarative. Preserve existing public contracts, validation, capability enforcement, and runtime semantics.

## C# example

For already validated definitions, a pure query can describe which tool bindings are requested. Host authorization remains a separate operation; this query grants nothing.

```csharp
static IEnumerable<ToolBindingRequest> GetToolRequirements(NodeDefinition node) =>
    node switch
    {
        { Tool: { } tool } => [tool],
        { Agent: { } agent } => agent.Tools,
        _ => []
    };

var requestedBindings = definition.Nodes.Values
    .SelectMany(GetToolRequirements)
    .Distinct()
    .OrderBy(request => request.BindingId, StringComparer.Ordinal)
    .ToArray();
```

This is an illustrative composition of existing Core types, not a replacement for broker policy checks or a request to refactor unrelated working code.

## Review questions

Can a reader identify the result, domain decisions, and ordering by reading the method? Are effects visible and separate from pure computation? Would a named intermediate result or a simple loop make the behavior easier to understand? Does the change preserve complexity, enumeration behavior, cancellation, authorization, and failure semantics? Keep the clearest implementation rather than maximizing functional syntax.
