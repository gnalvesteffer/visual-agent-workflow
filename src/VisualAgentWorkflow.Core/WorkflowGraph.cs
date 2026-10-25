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

/// <summary>Explicit typed ports for the bounded workflow graph; no CLR types or executable expressions.</summary>
public static class WorkflowPorts
{
    public static ImmutableArray<WorkflowPort> Inputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Input => [],
        NodeTypes.JsonSelectString or NodeTypes.JsonProject => [new("in", "json")],
        NodeTypes.ParallelFork or NodeTypes.ParallelBranch => [new("in", "string")],
        NodeTypes.ParallelJoin => [new("branch_1", "string"), new("branch_2", "string")],
        NodeTypes.Output => [new("in", node.OutputDataType ?? "string")],
        _ => [new("in", "string")]
    };
    public static ImmutableArray<WorkflowPort> Outputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Output => [],
        NodeTypes.Condition => [new("true", "string"), new("false", "string")],
        NodeTypes.Input when node.InputDataType == "json" => [new("out", "json")],
        NodeTypes.JsonProject => [new("out", "json")],
        NodeTypes.ParallelFork => [new("branch_1", "string"), new("branch_2", "string")],
        NodeTypes.ParallelJoin => [new("out", "json")],
        _ => [new("out", "string")]
    };
}

public static class ParallelGraphLimits
{
    public const int MaximumBranches = 2;
    public const int MaximumAggregateCharacters = 16_384;
}

/// <summary>Parses bounded RFC 6901 JSON Pointers used by declarative JSON selector nodes.</summary>
public static class WorkflowJsonPointer
{
    public const int MaximumLength = 1_024;
    public const int MaximumSegments = 16;

    public static ImmutableArray<string> Parse(string pointer)
    {
        if (pointer is null || pointer.Length > MaximumLength)
            throw new ArgumentException("A JSON pointer must be at most 1,024 characters.", nameof(pointer));
        if (pointer.Length == 0) return [];
        if (pointer[0] != '/') throw new ArgumentException("A JSON pointer must be empty or begin with '/'.", nameof(pointer));

        var segments = pointer[1..].Split('/');
        if (segments.Length > MaximumSegments)
            throw new ArgumentException("A JSON pointer can contain at most 16 segments.", nameof(pointer));

        return segments.Select(DecodeSegment).ToImmutableArray();
    }

    private static string DecodeSegment(string segment)
    {
        var decoded = new System.Text.StringBuilder(segment.Length);
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] != '~') { decoded.Append(segment[index]); continue; }
            if (++index >= segment.Length || segment[index] is not ('0' or '1'))
                throw new ArgumentException("Use '~0' for '~' and '~1' for '/' in a JSON pointer.", "pointer");
            decoded.Append(segment[index] == '0' ? '~' : '/');
        }
        return decoded.ToString();
    }
}

public static class JsonProjectionLimits
{
    public const int MaximumFields = 16;
    public const int MaximumNameCharacters = 128;
    public const int MaximumConfigurationCharacters = 16_384;
    public const int MaximumPayloadCharacters = 16_384;
    public const int MaximumSchemaDepth = 8;
    public const int MaximumSchemaNodes = 128;
    public const int MaximumArrayItems = 64;
    public static readonly ImmutableArray<string> ValueTypes = ["any", "string", "number", "integer", "boolean", "object", "array", "null"];
}

public static class WorkflowGraph
{
    public static bool HasRouting(WorkflowDefinition definition) => definition.Nodes.Values.Any(n =>
        n?.Type is NodeTypes.Condition or NodeTypes.Merge or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin);

    public static bool HasControlNodes(WorkflowDefinition definition) => HasRouting(definition) || definition.Nodes.Values.Any(n => n?.Type == NodeTypes.Approval);

    public static bool HasTypedJson(WorkflowDefinition definition) => definition.Nodes.Values.Any(node =>
        node?.Type is NodeTypes.JsonSelectString or NodeTypes.JsonProject || node?.InputDataType == "json" || node?.OutputDataType == "json");

    /// <summary>Deterministic presentation/analysis order, never a runtime scheduler.</summary>
    public static ImmutableArray<string> OrderedNodes(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        return TopologicalOrder(definition);
    }

    /// <summary>Safe deterministic canvas order for incomplete drafts. Never use for runtime execution.</summary>
    public static ImmutableArray<string> DisplayOrder(WorkflowDefinition definition)
    {
        var connected = TopologicalOrder(definition);
        return connected.AddRange(definition.Nodes.Keys.Except(connected, StringComparer.Ordinal).Order(StringComparer.Ordinal));
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
