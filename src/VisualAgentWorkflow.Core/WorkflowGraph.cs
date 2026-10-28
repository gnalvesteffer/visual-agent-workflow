using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowPort(string Id, string DataType);
public enum WorkflowPortKind { Execution, Data }
public enum WorkflowPortDirection { Input, Output }
public enum WorkflowPortCardinality { AtMostOne, ExactlyOne, Many }
public sealed record WorkflowPortDescriptor(string Id, WorkflowPortDirection Direction, WorkflowPortKind Kind, string? DataType = null, string? DisplayName = null)
{
    /// <summary>Whether the pin must receive or emit a connection in an executable graph.</summary>
    public bool Required { get; init; }

    /// <summary>How many edges may be attached to this pin.</summary>
    public WorkflowPortCardinality Cardinality { get; init; } = WorkflowPortCardinality.Many;

    /// <summary>Whether a data input can be satisfied by node-local authored configuration.</summary>
    public bool SupportsStaticValue { get; init; }
}
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
    public static ImmutableArray<WorkflowPortDescriptor> Explicit(NodeDefinition node) =>
        [.. ExplicitDataInputs(node).Select(port => new WorkflowPortDescriptor(port.Id, WorkflowPortDirection.Input, WorkflowPortKind.Data, port.DataType)
         {
             Required = node.Type != NodeTypes.McpTool || node.McpInputPorts is not { } inputPorts ||
                 inputPorts.FirstOrDefault(candidate => candidate.Id == port.Id)?.Required == true,
             Cardinality = node.Type == NodeTypes.McpTool ? WorkflowPortCardinality.AtMostOne : WorkflowPortCardinality.ExactlyOne,
             SupportsStaticValue = node.Type == NodeTypes.McpTool
         }),
         .. ExplicitDataOutputs(node).Select(port => new WorkflowPortDescriptor(port.Id, WorkflowPortDirection.Output, WorkflowPortKind.Data, port.DataType)
         {
             Cardinality = WorkflowPortCardinality.Many
         }),
         .. ExplicitExecutionInputs(node).Select(port => new WorkflowPortDescriptor(port, WorkflowPortDirection.Input, WorkflowPortKind.Execution)
         {
             Required = true,
             Cardinality = WorkflowPortCardinality.ExactlyOne
         }),
         .. ExplicitExecutionOutputs(node).Select(port => new WorkflowPortDescriptor(port, WorkflowPortDirection.Output, WorkflowPortKind.Execution,
             DisplayName: node.Type == NodeTypes.Agent
                 ? node.Agent?.Routes?.FirstOrDefault(route => route is not null && AgentExecutionRouteLimits.PinId(route.Id) == port)?.Label
                 : node.Type == NodeTypes.ParallelFork
                     ? node.ParallelFork?.Branches.FirstOrDefault(branch => branch is not null && ParallelBranchLimits.PinId(branch.Id) == port)?.Label
                     : null)
         {
             Required = true,
             Cardinality = WorkflowPortCardinality.ExactlyOne
         })];

    public static ImmutableArray<WorkflowPort> ExplicitDataInputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.FunctionOutput => [new("in", node.InputDataType ?? "string")],
        NodeTypes.Output => [new("in", node.OutputDataType ?? "string")],
        NodeTypes.TextConcat => [new("left", "string"), new("right", "string")],
        NodeTypes.StringEquals => [new("left", "string"), new("right", "string")],
        NodeTypes.IntegerAdd or NodeTypes.IntegerEquals => [new("left", "integer"), new("right", "integer")],
        NodeTypes.BooleanBranch => [new("condition", "boolean")],
        NodeTypes.BooleanNot => [new("value", "boolean")],
        NodeTypes.McpTool when node.McpInputPorts is { } ports => [.. ports.Select(port => new WorkflowPort(port.Id, port.DataType))],
        NodeTypes.McpTool => [new("in", "string")],
        NodeTypes.FunctionCall when node.GraphFunction?.Definition?.Inputs is { } inputs => [.. inputs.Select(pair => new WorkflowPort(pair.Key, pair.Value.DataType))],
        _ => []
    };

    public static ImmutableArray<WorkflowPort> ExplicitDataOutputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.FunctionInput => [new("out", node.OutputDataType ?? "string")],
        NodeTypes.Input or NodeTypes.StringConstant or NodeTypes.TextConcat => [new("out", "string")],
        NodeTypes.IntegerConstant or NodeTypes.IntegerAdd => [new("out", "integer")],
        NodeTypes.WorkflowValue => [new("out", node.OutputDataType ?? "string")],
        NodeTypes.Agent => [new("out", node.Agent?.RequireStructuredResult == true ? "json" : "string")],
        NodeTypes.McpTool => [new("out", "string")],
        NodeTypes.ParallelJoin => [new("out", "json")],
        NodeTypes.StringEquals or NodeTypes.IntegerEquals or NodeTypes.BooleanConstant or NodeTypes.BooleanNot => [new("out", "boolean")],
        NodeTypes.FunctionCall when node.GraphFunction?.Definition?.Outputs is { } outputs => [.. outputs.Select(pair => new WorkflowPort(pair.Key, pair.Value.DataType))],
        _ => []
    };

    public static ImmutableArray<string> ExplicitExecutionInputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Agent or NodeTypes.McpTool or NodeTypes.Output or NodeTypes.BooleanBranch or NodeTypes.Terminate or NodeTypes.ParallelFork or NodeTypes.ParallelBranch => ["execute"],
        NodeTypes.ParallelJoin when node.ParallelJoin is { } join => [.. join.BranchIds.Select(ParallelBranchLimits.PinId)],
        NodeTypes.ParallelRendezvous when node.ParallelRendezvous is { BranchIds.IsDefaultOrEmpty: false } rendezvous => [.. rendezvous.BranchIds.Select(ParallelBranchLimits.PinId)],
        _ => []
    };

    public static ImmutableArray<string> ExplicitExecutionOutputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Input => ["then"],
        NodeTypes.Agent when node.Agent?.Routes is { IsDefaultOrEmpty: false } routes => [.. routes.Select(route => AgentExecutionRouteLimits.PinId(route.Id))],
        NodeTypes.Agent => ["out"],
        NodeTypes.McpTool => ["then"],
        NodeTypes.BooleanBranch => ["true", "false"],
        NodeTypes.ParallelFork when node.ParallelFork is { } fork => [.. fork.Branches.Select(branch => ParallelBranchLimits.PinId(branch.Id))],
        NodeTypes.ParallelRendezvous when node.ParallelRendezvous is { BranchIds.IsDefaultOrEmpty: false } rendezvous => [.. rendezvous.BranchIds.Select(ParallelBranchLimits.PinId)],
        NodeTypes.ParallelBranch => ["then"],
        NodeTypes.ParallelJoin => ["then"],
        _ => []
    };

    public static ImmutableArray<WorkflowPort> Inputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Input => [],
        NodeTypes.JsonSelectString or NodeTypes.JsonProject => [new("in", "json")],
        NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.Loop or NodeTypes.LoopBody => [new("in", "string")],
        NodeTypes.ParallelJoin when node.ParallelJoin is not null => [],
        NodeTypes.ParallelJoin => [new("branch_1", "string"), new("branch_2", "string")],
        NodeTypes.Output => [new("in", node.OutputDataType ?? "string")],
        NodeTypes.WorkflowCall => [new("in", WorkflowCallInputType(node))],
        NodeTypes.Terminate => [new("in", "string")],
        _ => [new("in", "string")]
    };
    public static ImmutableArray<WorkflowPort> Outputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Output or NodeTypes.Terminate => [],
        NodeTypes.WorkflowCall => [new("out", WorkflowCallOutputType(node))],
        NodeTypes.Agent when node.Agent?.RequireStructuredResult == true => [new("out", "json")],
        NodeTypes.Condition => [new("true", "string"), new("false", "string")],
        NodeTypes.Input when node.InputDataType == "json" => [new("out", "json")],
        NodeTypes.JsonProject => [new("out", "json")],
        NodeTypes.ParallelFork when node.ParallelFork is not null => [],
        NodeTypes.ParallelFork => [new("branch_1", "string"), new("branch_2", "string")],
        NodeTypes.ParallelJoin => [new("out", "json")],
        NodeTypes.Loop => [new("body", "string"), new("done", "string")],
        _ => [new("out", "string")]
    };

    private static string WorkflowCallInputType(NodeDefinition node) =>
        node.WorkflowCall?.Definition is { Nodes: { } nodes } definition && nodes.TryGetValue(definition.EntryNodeId, out var entry) && entry is not null
            ? Outputs(entry).SingleOrDefault()?.DataType ?? "string"
            : "string";

    private static string WorkflowCallOutputType(NodeDefinition node) =>
        node.WorkflowCall?.Definition?.Nodes is { } nodes && nodes.Values.FirstOrDefault(candidate => candidate?.Type == NodeTypes.Output) is { } output
            ? Inputs(output).SingleOrDefault()?.DataType ?? "string"
            : "string";
}

public static class ParallelGraphLimits
{
    public const int MaximumBranches = 2;
    public const int MaximumExplicitBranches = 8;
    public const int MaximumBlocks = 4;
    public const int MaximumAggregateCharacters = 16_384;
}

public static class ParallelBranchLimits
{
    public const int MinimumBranches = 2;
    public const int MaximumIdLength = 32;
    public const int MaximumLabelLength = 64;

    public static string PinId(string branchId) => $"branch.{branchId}";

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= MaximumIdLength } &&
        id[0] is >= 'a' and <= 'z' && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

public static class WorkflowLoopLimits
{
    public const int MaximumIterations = 16;
    public static readonly ImmutableArray<string> Operators = ["equals", "contains", "starts-with", "is-empty"];
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
        n?.Type is NodeTypes.Condition or NodeTypes.BooleanBranch or NodeTypes.Merge or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous or NodeTypes.Loop or NodeTypes.Terminate);

    public static bool HasControlNodes(WorkflowDefinition definition) => HasRouting(definition) || definition.Nodes.Values.Any(n => n?.Type == NodeTypes.Approval);

    public static bool HasTypedJson(WorkflowDefinition definition) => definition.Nodes.Values.Any(node =>
        node?.Type is NodeTypes.JsonSelectString or NodeTypes.JsonProject || node?.InputDataType == "json" || node?.OutputDataType == "json");

    /// <summary>Deterministic presentation/analysis order, never a runtime scheduler.</summary>
    public static ImmutableArray<string> OrderedNodes(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        var order = TopologicalOrder(definition, LoopBackConnectionId(definition));
        return order.AddRange(definition.Nodes.Keys.Except(order, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>Safe deterministic canvas order for incomplete drafts. Never use for runtime execution.</summary>
    public static ImmutableArray<string> DisplayOrder(WorkflowDefinition definition)
    {
        var connectedOrder = TopologicalOrder(definition, LoopBackConnectionId(definition));
        var connected = connectedOrder.AddRange(definition.Nodes.Keys.Except(connectedOrder, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return connected;
    }

    internal static ImmutableArray<string> TopologicalOrder(WorkflowDefinition definition, string? ignoredConnectionId = null)
    {
        var incoming = definition.Nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var outgoing = definition.Nodes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (connectionId, edge) in definition.Connections)
        {
            if (connectionId == ignoredConnectionId) continue;
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

    internal static string? LoopBackConnectionId(WorkflowDefinition definition)
    {
        var loops = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Loop).Select(entry => entry.Key).ToArray();
        if (loops.Length != 1) return null;
        var loopId = loops[0];
        var bodyEdge = definition.Connections.FirstOrDefault(entry => entry.Value?.Source?.NodeId == loopId && entry.Value?.Source?.PortId == "body");
        if (bodyEdge.Value?.Target?.NodeId is not { } bodyStart) return null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = bodyStart;
        while (visited.Add(current) && visited.Count <= definition.Nodes.Count)
        {
            if (current == loopId) return null;
            var outgoing = definition.Connections.Where(entry => entry.Value?.Source?.NodeId == current).ToArray();
            if (outgoing.Length != 1 || outgoing[0].Value?.Target?.NodeId is not { } target) return null;
            if (target == loopId) return outgoing[0].Key;
            current = target;
        }
        return null;
    }
}
