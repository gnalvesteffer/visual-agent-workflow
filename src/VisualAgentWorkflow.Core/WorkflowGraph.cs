using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowPort(string Id, string DataType);
public sealed record StringCondition(string Operator, string Operand)
{
    public bool Evaluate(string input) => Operator switch
    {
        "equals" => string.Equals(input, Operand, StringComparison.Ordinal),
        "contains" => input.Contains(Operand, StringComparison.Ordinal),
        "starts-with" => input.StartsWith(Operand, StringComparison.Ordinal),
        "is-empty" => input.Length == 0,
        _ => throw new InvalidOperationException("Unsupported condition operator.")
    };
}

/// <summary>Explicit typed ports for the bounded string graph; no CLR types or executable expressions.</summary>
public static class WorkflowPorts
{
    public static ImmutableArray<WorkflowPort> Inputs(NodeDefinition node) => node.Type == NodeTypes.Input ? [] : [new("in", "string")];
    public static ImmutableArray<WorkflowPort> Outputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Output => [],
        NodeTypes.Condition => [new("true", "string"), new("false", "string")],
        _ => [new("out", "string")]
    };
}

public static class WorkflowGraph
{
    public static bool HasRouting(WorkflowDefinition definition) => definition.Nodes.Values.Any(n => n?.Type is NodeTypes.Condition or NodeTypes.Merge);

    /// <summary>Deterministic presentation/analysis order, never a runtime scheduler.</summary>
    public static ImmutableArray<string> OrderedNodes(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        return TopologicalOrder(definition);
    }

    internal static ImmutableArray<string> TopologicalOrder(WorkflowDefinition definition)
    {
        var incoming = definition.Nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var outgoing = definition.Nodes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in definition.Connections.Values)
        {
            if (edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target || !incoming.ContainsKey(source) || !incoming.ContainsKey(target)) continue;
            incoming[target]++;
            outgoing[source].Add(target);
        }
        var ready = new SortedSet<string>(incoming.Where(p => p.Value == 0).Select(p => p.Key), StringComparer.Ordinal);
        var order = ImmutableArray.CreateBuilder<string>();
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(id);
            foreach (var next in outgoing[id])
                if (--incoming[next] == 0) ready.Add(next);
        }
        return order.ToImmutable();
    }
}
