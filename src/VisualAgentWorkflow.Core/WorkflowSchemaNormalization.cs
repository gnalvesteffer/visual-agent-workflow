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
            if (definition.Nodes.Values.Any(node => node.Type == NodeTypes.Loop))
            {
                candidate = NormalizeLegacyDataLoop(definition);
            }
            else if (definition.Nodes.Values.Any(node => node.Type is not
                    (NodeTypes.Input or NodeTypes.Output or NodeTypes.Uppercase or NodeTypes.Reverse or NodeTypes.Agent) ||
                node.Type == NodeTypes.Agent && node.Agent?.Routes is not null))
                throw new ArgumentException("normalization.implicit-unsafe: Only implicit linear Input, Output, Uppercase, Reverse and unrouted Agent graphs have a verified translation; legacy control-flow graphs require a separately verified conversion.", nameof(definition));
            else
            {
                var order = WorkflowEditing.OrderedNodes(definition);
                var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
                var executableNodes = order.Where(id => definition.Nodes[id].Type is NodeTypes.Input or NodeTypes.Agent or NodeTypes.Output).ToArray();
                for (var index = 0; index < executableNodes.Length - 1; index++)
                {
                    var sourceId = executableNodes[index];
                    var targetId = executableNodes[index + 1];
                    var sourcePort = definition.Nodes[sourceId].Type == NodeTypes.Input ? "then" : "out";
                    execution.Add($"normalize-execution-{index:D3}", new(
                        new(sourceId, sourcePort), new(targetId, "execute"), "execution"));
                }
                candidate = definition with
                {
                    SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
                    ExecutionConnections = execution.ToImmutable()
                };
            }
        }
        var issues = WorkflowValidator.Validate(candidate);
        if (!issues.IsEmpty)
            throw new ArgumentException("normalization.invalid: " + string.Join("; ", issues.Select(issue => $"{issue.Code} ({issue.Path}): {issue.Message}")), nameof(definition));
        return candidate;
    }

    private static WorkflowDefinition NormalizeLegacyDataLoop(WorkflowDefinition definition)
    {
        if (definition.SchemaVersion is not ("1.17" or "1.18" or "1.19" or "1.20" or "1.21" or "1.22") ||
            definition.ExecutionConnections is { Count: > 0 } ||
            definition.Nodes.Values.Any(node => node.Type is not
                (NodeTypes.Input or NodeTypes.Output or NodeTypes.Loop or NodeTypes.LoopBody or NodeTypes.Uppercase or NodeTypes.Reverse)))
            throw new ArgumentException("normalization.loop-unsafe: Only a validated bounded loop with pure string transforms has a verified legacy translation.", nameof(definition));

        var topology = LoopDataTopologyAnalyzer.Analyze(definition);
        if (topology is null)
            throw new ArgumentException("normalization.loop-topology: The bounded loop data feedback path is ambiguous or unsupported.", nameof(definition));

        var inputId = definition.EntryNodeId;
        var outputId = definition.Nodes.Single(node => node.Value.Type == NodeTypes.Output).Key;
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("normalize-loop-initial", new(new(inputId, "then"), new(topology.LoopNodeId, "execute"), "execution"))
            .Add("normalize-loop-body", new(new(topology.LoopNodeId, "body"), new(topology.BodyAnchorNodeId, "execute"), "execution"))
            .Add("normalize-loop-return", new(new(topology.BodyAnchorNodeId, "then"), new(topology.LoopNodeId, "execute"), "execution"))
            .Add("normalize-loop-done", new(new(topology.LoopNodeId, "done"), new(outputId, "execute"), "execution"));

        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            ExecutionConnections = execution
        };
    }
}
