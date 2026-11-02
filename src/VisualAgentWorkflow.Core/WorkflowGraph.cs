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

/// <summary>
/// Shape fallback for configuration-derived pins that do not have a registered
/// fixed descriptor. The resolver consumes these projections to produce the
/// effective descriptor without making authoring metadata authoritative for schema
/// admission or runtime behavior.
/// </summary>
internal static class WorkflowPortShapeCatalog
{
    public static ImmutableArray<WorkflowPort> ExplicitDataInputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.FunctionOutput => [new("in", node.InputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.Output => [new("in", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.VariableSet => [new("value", node.InputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.McpTool when node.McpInputPorts is { IsDefault: false } ports => [.. ports.Where(port => port is not null).Select(port => new WorkflowPort(port.Id, port.DataType))],
        NodeTypes.McpTool => [new("in", WorkflowDataTypes.String)],
        NodeTypes.Agent => [new("in", WorkflowDataTypes.String)],
        NodeTypes.WorkflowCall => InputsForWorkflowCall(node),
        NodeTypes.WorkflowInterfaceCall => InterfaceCallInputs(node),
        NodeTypes.FunctionCall => FunctionCallDataInputs(node),
        NodeTypes.ArrayLength => [new("in", WorkflowDataTypes.ArrayOf(node.ArrayElementType ?? WorkflowDataTypes.Json))],
        NodeTypes.ArrayGet => [new("array", WorkflowDataTypes.ArrayOf(node.ArrayElementType ?? WorkflowDataTypes.Json)), new("index", WorkflowDataTypes.Integer)],
        _ => []
    };

    public static ImmutableArray<WorkflowPort> ExplicitDataOutputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.FunctionInput => [new("out", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.Input => [new("out", node.InputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.WorkflowCall => OutputsForWorkflowCall(node),
        NodeTypes.WorkflowInterfaceCall => InterfaceCallOutputs(node),
        NodeTypes.WorkflowValue => [new("out", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.VariableGet => [new("out", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.Agent => [new("out", node.Agent?.RequireStructuredResult == true ? WorkflowDataTypes.Json : WorkflowDataTypes.String)],
        NodeTypes.McpTool => [new("out", WorkflowDataTypes.String)],
        NodeTypes.ParallelJoin => [new("out", WorkflowDataTypes.Json)],
        NodeTypes.FunctionCall => FunctionCallDataOutputs(node),
        NodeTypes.ArrayLiteral => [new("out", WorkflowDataTypes.ArrayOf(node.ArrayLiteral?.ElementType ?? WorkflowDataTypes.String))],
        NodeTypes.ArrayLength => [new("out", WorkflowDataTypes.Integer)],
        NodeTypes.ArrayGet => [new("item", node.ArrayElementType ?? WorkflowDataTypes.Json)],
        _ => []
    };

    public static ImmutableArray<string> ExplicitExecutionInputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Agent or NodeTypes.McpTool or NodeTypes.WorkflowCall or NodeTypes.WorkflowInterfaceCall or NodeTypes.Output or NodeTypes.Terminate or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.Sequence or NodeTypes.VariableGet or NodeTypes.VariableSet => ["execute"],
        NodeTypes.ParallelJoin when node.ParallelJoin is { BranchIds.IsDefault: false } join => [.. join.BranchIds.Where(branchId => branchId is not null).Select(ParallelBranchLimits.PinId)],
        NodeTypes.ParallelRendezvous when node.ParallelRendezvous is { BranchIds.IsDefaultOrEmpty: false } rendezvous => [.. rendezvous.BranchIds.Where(branchId => branchId is not null).Select(ParallelBranchLimits.PinId)],
        _ => []
    };

    public static ImmutableArray<string> ExplicitExecutionOutputs(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Input => ["then"],
        NodeTypes.Agent when node.Agent?.Routes is { IsDefaultOrEmpty: false } routes => [.. routes.Where(route => route is not null).Select(route => AgentExecutionRouteLimits.PinId(route.Id))],
        NodeTypes.Agent => ["out"],
        NodeTypes.McpTool => ["then"],
        NodeTypes.WorkflowCall => ["out"],
        NodeTypes.WorkflowInterfaceCall => ["then"],
        NodeTypes.ParallelBranch => ["then"],
        NodeTypes.ParallelFork when node.ParallelFork is { Branches.IsDefault: false } fork => [.. fork.Branches.Where(branch => branch is not null).Select(branch => ParallelBranchLimits.PinId(branch.Id))],
        NodeTypes.ParallelRendezvous when node.ParallelRendezvous is { BranchIds.IsDefaultOrEmpty: false } rendezvous => [.. rendezvous.BranchIds.Where(branchId => branchId is not null).Select(ParallelBranchLimits.PinId)],
        NodeTypes.ParallelJoin => ["then"],
        NodeTypes.Sequence when node.Sequence is { Outputs.IsDefault: false } sequence => [.. sequence.Outputs.Where(output => output is not null).Select(output => SequenceOutputLimits.PinId(output.Id))],
        NodeTypes.VariableGet or NodeTypes.VariableSet => ["then"],
        _ => []
    };

    private static ImmutableArray<WorkflowPort> FunctionCallDataInputs(NodeDefinition node) =>
        node.GraphFunction?.Definition?.Inputs is { } inputs
            ? [.. inputs.Where(pair => pair.Value is not null).Select(pair => new WorkflowPort(pair.Key, pair.Value.DataType))]
            : [];

    private static ImmutableArray<WorkflowPort> InputsForWorkflowCall(NodeDefinition node) =>
        [new("in", node.WorkflowCall?.Definition is { Nodes: { } nodes } child &&
            nodes.TryGetValue(child.EntryNodeId, out var input) && input is not null
            ? input.InputDataType ?? WorkflowDataTypes.String : WorkflowDataTypes.String)];

    private static ImmutableArray<WorkflowPort> OutputsForWorkflowCall(NodeDefinition node) =>
        [new("out", node.WorkflowCall?.Definition?.Nodes?.Values.FirstOrDefault(candidate => candidate?.Type == NodeTypes.Output)?.OutputDataType ?? WorkflowDataTypes.String)];

    private static ImmutableArray<WorkflowPort> InterfaceCallInputs(NodeDefinition node) =>
        TryGetInterfaceSignature(node, out var signature)
            ? signature.Inputs is { IsDefault: false } inputs
                ? [.. inputs.Where(field => field is not null).Select(field => new WorkflowPort(field.FieldId, field.DataType))]
                : [new("in", signature.InputDataType)]
            : [];

    private static ImmutableArray<WorkflowPort> InterfaceCallOutputs(NodeDefinition node) =>
        TryGetInterfaceSignature(node, out var signature)
            ? signature.Outputs is { IsDefault: false } outputs
                ? [.. outputs.Where(field => field is not null).Select(field => new WorkflowPort(field.FieldId, field.DataType))]
                : [new("out", signature.OutputDataType)]
            : [];

    private static bool TryGetInterfaceSignature(NodeDefinition node, out WorkflowInterfaceOperation signature)
    {
        signature = null!;
        return node.WorkflowInterfaceCall is { } configuration &&
            WorkflowInterfaceResolver.TryGetOperationSignature(configuration, out signature);
    }

    private static ImmutableArray<WorkflowPort> FunctionCallDataOutputs(NodeDefinition node) =>
        node.GraphFunction?.Definition?.Outputs is { } outputs
            ? [.. outputs.Where(pair => pair.Value is not null).Select(pair => new WorkflowPort(pair.Key, pair.Value.DataType))]
            : [];
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
        !IsSupportedNodeVersion(node) ? [] : WorkflowNodeDescriptorRegistry.TryGetForNode(node, out var staticDescriptor)
            ? [.. staticDescriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data).Select(port => new WorkflowPort(port.Id, port.DataType!))]
            : WorkflowPortShapeCatalog.ExplicitDataInputs(node);

    public static ImmutableArray<WorkflowPort> ExplicitDataOutputs(NodeDefinition node) =>
        !IsSupportedNodeVersion(node) ? [] : WorkflowNodeDescriptorRegistry.TryGetForNode(node, out var staticDescriptor)
            ? [.. staticDescriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data).Select(port => new WorkflowPort(port.Id, port.DataType!))]
            : WorkflowPortShapeCatalog.ExplicitDataOutputs(node);

    public static ImmutableArray<string> ExplicitExecutionInputs(NodeDefinition node) =>
        !IsSupportedNodeVersion(node) ? [] : WorkflowNodeDescriptorRegistry.TryGetForNode(node, out var descriptor)
            ? [.. descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Execution).Select(port => port.Id)]
            : WorkflowPortShapeCatalog.ExplicitExecutionInputs(node);

    public static ImmutableArray<string> ExplicitExecutionOutputs(NodeDefinition node) =>
        !IsSupportedNodeVersion(node) ? [] : WorkflowNodeDescriptorRegistry.TryGetForNode(node, out var descriptor)
            ? [.. descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Execution).Select(port => port.Id)]
            : WorkflowPortShapeCatalog.ExplicitExecutionOutputs(node);

    public static ImmutableArray<WorkflowPort> Inputs(NodeDefinition node) =>
        !IsSupportedNodeVersion(node) ? [] : WorkflowNodeDescriptorRegistry.TryGetForNode(node, out var staticDescriptor)
            ? [.. staticDescriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data).Select(port => new WorkflowPort(port.Id, port.DataType!))]
            : node.Type switch
    {
        NodeTypes.Input => [],
        NodeTypes.ParallelFork or NodeTypes.ParallelBranch => [new("in", WorkflowDataTypes.String)],
        NodeTypes.VariableSet => [new("value", node.InputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.ParallelJoin when node.ParallelJoin is not null => [],
        NodeTypes.ParallelJoin => [new("branch_1", WorkflowDataTypes.String), new("branch_2", WorkflowDataTypes.String)],
        NodeTypes.Output => [new("in", node.OutputDataType ?? WorkflowDataTypes.String)],
        NodeTypes.WorkflowCall => [new("in", WorkflowCallInputType(node))],
        NodeTypes.FunctionCall => FunctionCallDataInputs(node),
        NodeTypes.Terminate => [new("in", WorkflowDataTypes.String)],
        _ => [new("in", WorkflowDataTypes.String)]
    };
    public static ImmutableArray<WorkflowPort> Outputs(NodeDefinition node) =>
        !IsSupportedNodeVersion(node) ? [] : WorkflowNodeDescriptorRegistry.TryGetForNode(node, out var staticDescriptor)
            ? [.. staticDescriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data).Select(port => new WorkflowPort(port.Id, port.DataType!))]
            : node.Type switch
    {
        NodeTypes.Output or NodeTypes.Terminate => [],
        NodeTypes.WorkflowCall => [new("out", WorkflowCallOutputType(node))],
        NodeTypes.FunctionCall => FunctionCallDataOutputs(node),
        NodeTypes.Agent when node.Agent?.RequireStructuredResult == true => [new("out", WorkflowDataTypes.Json)],
        NodeTypes.ParallelFork when node.ParallelFork is not null => [],
        NodeTypes.Input when node.InputDataType == WorkflowDataTypes.Json => [new("out", WorkflowDataTypes.Json)],
        NodeTypes.Input when WorkflowDataTypes.IsPrimitiveArrayType(node.InputDataType) => [new("out", node.InputDataType!)],
        NodeTypes.ParallelFork => [new("branch_1", WorkflowDataTypes.String), new("branch_2", WorkflowDataTypes.String)],
        NodeTypes.ParallelJoin => [new("out", WorkflowDataTypes.Json)],
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

    private static ImmutableArray<WorkflowPort> FunctionCallDataInputs(NodeDefinition node) =>
        node.GraphFunction?.Definition?.Inputs is { } inputs
            ? [.. inputs.Where(pair => pair.Value is not null).Select(pair => new WorkflowPort(pair.Key, pair.Value.DataType))]
            : [];

    private static ImmutableArray<WorkflowPort> FunctionCallDataOutputs(NodeDefinition node) =>
        node.GraphFunction?.Definition?.Outputs is { } outputs
            ? [.. outputs.Where(pair => pair.Value is not null).Select(pair => new WorkflowPort(pair.Key, pair.Value.DataType))]
            : [];

    private static bool IsSupportedNodeVersion(NodeDefinition node) =>
        WorkflowNodeContractCatalog.TryGet(node.Type, out var contract) &&
        string.Equals(node.TypeVersion, contract.TypeVersion, StringComparison.Ordinal);
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
    public const int MaximumMergeProperties = 32;
    public static readonly ImmutableArray<string> ValueTypes = ["any", "string", "number", "integer", "boolean", "object", "array", "null"];
}

public static class WorkflowGraph
{
    public static bool HasRouting(WorkflowDefinition definition) => definition.Nodes.Values.Any(n =>
        n?.Type is NodeTypes.Condition or NodeTypes.BooleanBranch or NodeTypes.Merge or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous or NodeTypes.Sequence or NodeTypes.Loop or NodeTypes.Terminate);

    public static bool HasControlNodes(WorkflowDefinition definition) => HasRouting(definition) || definition.Nodes.Values.Any(n => n?.Type == NodeTypes.Approval);

    public static bool HasTypedJson(WorkflowDefinition definition) => definition.Nodes.Values.Any(node =>
        node?.Type is NodeTypes.JsonSelectString or NodeTypes.JsonProject or NodeTypes.JsonMerge or NodeTypes.JsonToArray or NodeTypes.ArrayLength or NodeTypes.ArrayGet || node?.InputDataType == WorkflowDataTypes.Json || node?.OutputDataType == WorkflowDataTypes.Json || node?.InputDataType == WorkflowDataTypes.JsonArray || node?.OutputDataType == WorkflowDataTypes.JsonArray);

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
