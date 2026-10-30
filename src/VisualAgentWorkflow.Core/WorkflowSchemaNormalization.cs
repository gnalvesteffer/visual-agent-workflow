using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Explicit, source-preserving conversion to the current editor contract.</summary>
public static class WorkflowSchemaNormalization
{
    /// <summary>
    /// Preserves existing graph identities and embedded revision snapshots. Implicit linear
    /// string graphs receive deterministic execution wires; ambiguous control flows are rejected.
    /// No publication, dependency upgrade, or persistence is performed.
    /// </summary>
    public static WorkflowDefinition NormalizeForEditing(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph) return definition;
        WorkflowDefinition candidate;
        if (WorkflowSchemaVersions.UsesExplicitGraph(definition))
        {
            candidate = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        }
        else
        {
            WorkflowValidator.ThrowIfInvalid(definition);
            if (definition.Nodes.Values.Any(node => node.Type is not
                    (NodeTypes.Input or NodeTypes.Output or NodeTypes.Uppercase or NodeTypes.Reverse)))
                throw new ArgumentException("normalization.implicit-unsafe: Only implicit linear Input, Output, Uppercase and Reverse graphs have a verified translation; other legacy graphs require an explicit conversion.", nameof(definition));
            var order = WorkflowEditing.OrderedNodes(definition);
            var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
            execution.Add("normalize-execution-000", new(new(order[0], "then"), new(order[^1], "execute"), "execution"));
            candidate = definition with
            {
                SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
                ExecutionConnections = execution.ToImmutable()
            };
        }
        var issues = WorkflowValidator.Validate(candidate);
        if (!issues.IsEmpty)
            throw new ArgumentException("normalization.invalid: " + string.Join("; ", issues.Select(issue => $"{issue.Code} ({issue.Path}): {issue.Message}")), nameof(definition));
        return candidate;
    }
}
