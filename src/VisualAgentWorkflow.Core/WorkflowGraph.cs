using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowPort(string Id, string DataType);
public enum WorkflowPortKind { Execution, Data }
public enum WorkflowPortDirection { Input, Output }
public enum WorkflowPortCardinality { AtMostOne, ExactlyOne, Many }
public sealed record WorkflowPortDescriptor(string Id, WorkflowPortDirection Direction, WorkflowPortKind Kind, string? DataType = null, string? DisplayName = null)
{
    /// <summary>Transient, untrusted authoring guidance from a live capability schema; never persisted or executed.</summary>
    public string? Description { get; init; }

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
    /// <summary>
    /// Resolves the complete explicit pin contract. The optional MCP metadata is an
    /// authoring-time view used when a live binding has been discovered but has not
    /// yet been persisted on the node; it is never written by this method.
    /// </summary>
    public static ImmutableArray<WorkflowPortDescriptor> Explicit(
        NodeDefinition node,
        ImmutableArray<McpInputPortDefinition>? discoveredMcpInputPorts = null,
        IReadOnlyDictionary<string, string>? discoveredMcpInputDescriptions = null) =>
        WorkflowNodeDescriptorResolver.TryResolve(node, discoveredMcpInputPorts, discoveredMcpInputDescriptions, out var descriptor)
            ? descriptor.Ports
            : [];

    public static ImmutableArray<WorkflowPort> ExplicitDataInputs(NodeDefinition node) =>
        WorkflowNodeDescriptorRegistry.TryGet(node.Type, WorkflowNodeDescriptorRegistry.CurrentVersion, out var staticDescriptor)
            ? [.. staticDescriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input).Select(port => new WorkflowPort(port.Id, port.DataType!))]
            : node.Type switch
    {
        NodeTypes.FunctionOutput => [new("in", node.InputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.Output => [new("in", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.TextConcat => [new("left", WorkflowDataTypes.String), new("right", WorkflowDataTypes.String)],
        NodeTypes.StringEquals => [new("left", WorkflowDataTypes.String), new("right", WorkflowDataTypes.String)],
        NodeTypes.IntegerAdd or NodeTypes.IntegerEquals => [new("left", WorkflowDataTypes.Integer), new("right", WorkflowDataTypes.Integer)],
        NodeTypes.NumberAdd => [new("left", WorkflowDataTypes.Number), new("right", WorkflowDataTypes.Number)],
        NodeTypes.BooleanBranch => [new("condition", WorkflowDataTypes.Boolean)],
        NodeTypes.BooleanNot => [new("value", WorkflowDataTypes.Boolean)],
        NodeTypes.StringToInteger => [new("in", WorkflowDataTypes.String)],
        NodeTypes.McpTool when node.McpInputPorts is { } ports => [.. ports.Where(port => port is not null).Select(port => new WorkflowPort(port.Id, port.DataType))],
        NodeTypes.McpTool => [new("in", WorkflowDataTypes.String)],
        NodeTypes.FunctionCall when node.GraphFunction?.Definition?.Inputs is { } inputs => [.. inputs.Where(pair => pair.Value is not null).Select(pair => new WorkflowPort(pair.Key, pair.Value.DataType))],
        _ => []
    };

    public static ImmutableArray<WorkflowPort> ExplicitDataOutputs(NodeDefinition node) =>
        WorkflowNodeDescriptorRegistry.TryGet(node.Type, WorkflowNodeDescriptorRegistry.CurrentVersion, out var staticDescriptor)
            ? [.. staticDescriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output).Select(port => new WorkflowPort(port.Id, port.DataType!))]
            : node.Type switch
    {
        NodeTypes.FunctionInput => [new("out", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.Input or NodeTypes.StringConstant or NodeTypes.TextConcat => [new("out", WorkflowDataTypes.String)],
        NodeTypes.IntegerConstant or NodeTypes.IntegerAdd => [new("out", WorkflowDataTypes.Integer)],
        NodeTypes.NumberConstant => [new("out", WorkflowDataTypes.Number)],
        NodeTypes.NumberAdd => [new("out", WorkflowDataTypes.Number)],
        NodeTypes.StringToInteger => [new("out", WorkflowDataTypes.Integer)],
        NodeTypes.WorkflowValue => [new("out", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.Agent => [new("out", node.Agent?.RequireStructuredResult == true ? WorkflowDataTypes.Json : WorkflowDataTypes.String)],
        NodeTypes.McpTool => [new("out", WorkflowDataTypes.String)],
        NodeTypes.ParallelJoin => [new("out", WorkflowDataTypes.Json)],
        NodeTypes.StringEquals or NodeTypes.IntegerEquals or NodeTypes.BooleanConstant or NodeTypes.BooleanNot => [new("out", WorkflowDataTypes.Boolean)],
        NodeTypes.FunctionCall when node.GraphFunction?.Definition?.Outputs is { } outputs => [.. outputs.Where(pair => pair.Value is not null).Select(pair => new WorkflowPort(pair.Key, pair.Value.DataType))],
        _ => []
    };

    public static ImmutableArray<string> ExplicitExecutionInputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Agent or NodeTypes.McpTool or NodeTypes.Output or NodeTypes.BooleanBranch or NodeTypes.Terminate or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.Sequence => ["execute"],
        NodeTypes.ParallelJoin when node.ParallelJoin is { } join => [.. join.BranchIds.Where(branchId => branchId is not null).Select(ParallelBranchLimits.PinId)],
        NodeTypes.ParallelRendezvous when node.ParallelRendezvous is { BranchIds.IsDefaultOrEmpty: false } rendezvous => [.. rendezvous.BranchIds.Where(branchId => branchId is not null).Select(ParallelBranchLimits.PinId)],
        _ => []
    };

    public static ImmutableArray<string> ExplicitExecutionOutputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Input => ["then"],
        NodeTypes.Agent when node.Agent?.Routes is { IsDefaultOrEmpty: false } routes => [.. routes.Where(route => route is not null).Select(route => AgentExecutionRouteLimits.PinId(route.Id))],
        NodeTypes.Agent => ["out"],
        NodeTypes.McpTool => ["then"],
        NodeTypes.BooleanBranch => ["true", "false"],
        NodeTypes.ParallelFork when node.ParallelFork is { } fork => [.. fork.Branches.Where(branch => branch is not null).Select(branch => ParallelBranchLimits.PinId(branch.Id))],
        NodeTypes.ParallelRendezvous when node.ParallelRendezvous is { BranchIds.IsDefaultOrEmpty: false } rendezvous => [.. rendezvous.BranchIds.Where(branchId => branchId is not null).Select(ParallelBranchLimits.PinId)],
        NodeTypes.ParallelBranch => ["then"],
        NodeTypes.ParallelJoin => ["then"],
        NodeTypes.Sequence when node.Sequence is { } sequence => [.. sequence.Outputs.Where(output => output is not null).Select(output => SequenceOutputLimits.PinId(output.Id))],
        _ => []
    };

    public static ImmutableArray<WorkflowPort> Inputs(NodeDefinition node) =>
        WorkflowNodeDescriptorRegistry.TryGet(node.Type, WorkflowNodeDescriptorRegistry.CurrentVersion, out var staticDescriptor)
            ? [.. staticDescriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input).Select(port => new WorkflowPort(port.Id, port.DataType!))]
            : node.Type switch
    {
        NodeTypes.Input => [],
        NodeTypes.JsonSelectString or NodeTypes.JsonProject => [new("in", WorkflowDataTypes.Json)],
        NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.Loop or NodeTypes.LoopBody => [new("in", WorkflowDataTypes.String)],
        NodeTypes.ParallelJoin when node.ParallelJoin is not null => [],
        NodeTypes.ParallelJoin => [new("branch_1", WorkflowDataTypes.String), new("branch_2", WorkflowDataTypes.String)],
        NodeTypes.Output => [new("in", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.WorkflowCall => [new("in", WorkflowCallInputType(node))],
        NodeTypes.Terminate => [new("in", WorkflowDataTypes.String)],
        _ => [new("in", WorkflowDataTypes.String)]
    };
    public static ImmutableArray<WorkflowPort> Outputs(NodeDefinition node) =>
        WorkflowNodeDescriptorRegistry.TryGet(node.Type, WorkflowNodeDescriptorRegistry.CurrentVersion, out var staticDescriptor)
            ? [.. staticDescriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output).Select(port => new WorkflowPort(port.Id, port.DataType!))]
            : node.Type switch
    {
        NodeTypes.Output or NodeTypes.Terminate => [],
        NodeTypes.WorkflowCall => [new("out", WorkflowCallOutputType(node))],
        NodeTypes.Agent when node.Agent?.RequireStructuredResult == true => [new("out", WorkflowDataTypes.Json)],
        NodeTypes.Condition => [new("true", WorkflowDataTypes.String), new("false", WorkflowDataTypes.String)],
        NodeTypes.Input when node.InputDataType == WorkflowDataTypes.Json => [new("out", WorkflowDataTypes.Json)],
        NodeTypes.JsonProject => [new("out", WorkflowDataTypes.Json)],
        NodeTypes.ParallelFork when node.ParallelFork is not null => [],
        NodeTypes.ParallelFork => [new("branch_1", WorkflowDataTypes.String), new("branch_2", WorkflowDataTypes.String)],
        NodeTypes.ParallelJoin => [new("out", WorkflowDataTypes.Json)],
        NodeTypes.Loop => [new("body", WorkflowDataTypes.String), new("done", WorkflowDataTypes.String)],
        _ => [new("out", WorkflowDataTypes.String)]
    };

    private static string WorkflowCallInputType(NodeDefinition node) =>
        node.WorkflowCall?.Definition is { Nodes: { } nodes } definition && nodes.TryGetValue(definition.EntryNodeId, out var entry) && entry is not null
            ? Outputs(entry).SingleOrDefault()?.DataType ?? WorkflowDataTypes.String
            : WorkflowDataTypes.String;

    private static string WorkflowCallOutputType(NodeDefinition node) =>
        node.WorkflowCall?.Definition?.Nodes is { } nodes && nodes.Values.FirstOrDefault(candidate => candidate?.Type == NodeTypes.Output) is { } output
            ? Inputs(output).SingleOrDefault()?.DataType ?? WorkflowDataTypes.String
            : WorkflowDataTypes.String;
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

public static class SequenceOutputLimits
{
    public const int MinimumOutputs = 2;
    public const int MaximumOutputs = 4;
    public const int MaximumIdLength = 32;
    public const int MaximumLabelLength = 64;

    public static string PinId(string outputId) => $"then.{outputId}";

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
        n?.Type is NodeTypes.Condition or NodeTypes.BooleanBranch or NodeTypes.Merge or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous or NodeTypes.Sequence or NodeTypes.Loop or NodeTypes.Terminate);

    public static bool HasControlNodes(WorkflowDefinition definition) => HasRouting(definition) || definition.Nodes.Values.Any(n => n?.Type == NodeTypes.Approval);

    public static bool HasTypedJson(WorkflowDefinition definition) => definition.Nodes.Values.Any(node =>
        node?.Type is NodeTypes.JsonSelectString or NodeTypes.JsonProject || node?.InputDataType == WorkflowDataTypes.Json || node?.OutputDataType == WorkflowDataTypes.Json);

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
