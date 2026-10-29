using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>One bounded JSON WorkflowCall path with zero, one, or two pure data-only composition nodes.</summary>
public sealed record SequenceWorkflowCallJsonChainPath(
    string OutputId,
    ImmutableArray<string> NodeIds,
    string CallNodeId,
    ImmutableArray<string> JsonNodeIds,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash);

public sealed record SequenceWorkflowCallJsonChainTopologyAnalysis(
    ImmutableArray<SequenceWorkflowCallJsonChainPath> Paths,
    ImmutableArray<SequenceWorkflowCallTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Schema 1.54 keeps post-call composition deliberately linear and pure. A Project may feed
/// another Project or a terminal SelectString; a SelectString cannot feed another node.
/// </summary>
public static class SequenceWorkflowCallJsonChainTopology
{
    public const int MaximumCompositionNodes = 2;
    public const int MaximumPathNodes = 16;

    public static bool IsJsonCompositionGraph(WorkflowDefinition definition) =>
        definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph &&
        definition.Nodes.Values.Any(node => node?.Type == NodeTypes.WorkflowCall && IsJsonCall(node));

    public static SequenceWorkflowCallJsonChainTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var paths = ImmutableArray.CreateBuilder<SequenceWorkflowCallJsonChainPath>();
        var issues = ImmutableArray.CreateBuilder<SequenceWorkflowCallTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        var sequences = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Sequence).ToArray();
        var outputs = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Output).ToArray();
        var calls = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.WorkflowCall).ToArray();
        if (sequences.Length != 1) Issue("sequence.call.json.chain.graph", "nodes", "A schema 1.54 graph requires exactly one Sequence node.");
        if (outputs.Length != 1) Issue("sequence.call.json.chain.graph", "nodes", "A schema 1.54 graph requires exactly one Output node.");
        if (sequences.Length != 1 || outputs.Length != 1 || definition.ExecutionConnections is not { } execution)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var sequence = sequences[0];
        var output = outputs[0];
        var executionEdges = execution.Values.Where(edge => edge is not null).ToArray();
        var outgoing = executionEdges.GroupBy(edge => edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var owned = new Dictionary<string, string>(StringComparer.Ordinal);
        var dataEdges = definition.Connections.Values.Where(edge => edge is not null).ToArray();
        var chainNodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in sequence.Value.Sequence?.Outputs ?? [])
        {
            var roots = executionEdges.Where(edge => edge.Source.NodeId == sequence.Key &&
                edge.Source.PortId == SequenceOutputLimits.PinId(descriptor.Id)).ToArray();
            if (roots.Length != 1) continue;

            var current = roots[0].Target.NodeId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var nodes = ImmutableArray.CreateBuilder<string>();
            string? callId = null;
            while (current != output.Key && visited.Add(current))
            {
                if (nodes.Count >= MaximumPathNodes)
                {
                    Issue("sequence.call.json.chain.limit", $"nodes.{current}", "A WorkflowCall JSON chain exceeds its bounded node limit.");
                    break;
                }
                if (!definition.Nodes.TryGetValue(current, out var node) || node is null)
                {
                    Issue("sequence.call.json.chain.reference", $"nodes.{current}", "A WorkflowCall JSON chain reaches a missing node.");
                    break;
                }
                if (node.Type is not (NodeTypes.McpTool or NodeTypes.WorkflowCall))
                    Issue("sequence.call.json.chain.node", $"nodes.{current}", "A schema 1.54 execution path may contain only direct MCP nodes and one pinned JSON WorkflowCall; JSON composition nodes are data-only.");
                if (node.Type == NodeTypes.WorkflowCall)
                {
                    if (callId is not null) Issue("sequence.call.json.chain.multiple", $"nodes.{current}", "A path may contain exactly one WorkflowCall.");
                    callId = current;
                    ValidateCall(node, current, Issue);
                }
                else if (node.Type == NodeTypes.McpTool && callId is not null)
                    Issue("sequence.call.json.chain.order", $"nodes.{current}", "Direct MCP nodes must precede the WorkflowCall.");

                if (!owned.TryAdd(current, descriptor.Id))
                    Issue("sequence.call.json.chain.shared", $"nodes.{current}", "WorkflowCall JSON chains may not share downstream nodes.");
                nodes.Add(current);
                var successors = outgoing.GetValueOrDefault(current) ?? [];
                if (successors.Length != 1)
                {
                    Issue("sequence.call.json.chain.linear", $"nodes.{current}", "Every WorkflowCall JSON chain node must have exactly one execution successor.");
                    break;
                }
                var next = successors[0];
                if (next.Target.PortId != "execute") Issue("sequence.call.json.chain.port", $"nodes.{current}", "Every path successor must enter execute.");
                if (node.Type is NodeTypes.McpTool && next.Source.PortId != "then" ||
                    node.Type is NodeTypes.WorkflowCall && next.Source.PortId != "out")
                    Issue("sequence.call.json.chain.port", $"nodes.{current}", "MCP nodes leave through then and WorkflowCall nodes leave through out.");
                current = next.Target.NodeId;
            }

            if (callId is null) continue;
            if (current != output.Key)
                Issue("sequence.call.json.chain.terminal", $"nodes.{sequence.Key}.sequence.outputs.{descriptor.Id}", "The path must reach Output without a cycle.");
            else
            {
                var call = definition.Nodes[callId].WorkflowCall!;
                var jsonNodes = ValidateDataComposition(callId, output.Key, dataEdges, definition, Issue);
                foreach (var jsonNode in jsonNodes) chainNodeIds.Add(jsonNode);
                paths.Add(new(descriptor.Id, nodes.ToImmutable(), callId, jsonNodes.ToImmutableArray(),
                    call.WorkflowId, call.RevisionId, call.ContentHash));
            }
        }

        foreach (var call in calls)
            if (!paths.Any(path => path.CallNodeId == call.Key))
                Issue("sequence.call.json.chain.unreachable", $"nodes.{call.Key}", "Every WorkflowCall must belong to one valid JSON composition path.");
        foreach (var json in definition.Nodes.Where(entry => entry.Value?.Type is NodeTypes.JsonSelectString or NodeTypes.JsonProject))
            if (!chainNodeIds.Contains(json.Key))
                Issue("sequence.call.json.chain.orphan", $"nodes.{json.Key}", "Every JSON composition node must belong to the call-to-Output data chain.");
        return new(paths.ToImmutable(), issues.ToImmutable());
    }

    private static ImmutableArray<string> ValidateDataComposition(string callId, string outputId,
        IReadOnlyCollection<ConnectionDefinition> edges, WorkflowDefinition definition,
        Action<string, string, string> issue)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        var current = callId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(current))
            {
                issue("sequence.call.json.chain.cycle", $"nodes.{current}", "JSON composition data edges may not form a cycle.");
                break;
            }
            var outgoing = edges.Where(edge => edge.Source.NodeId == current && edge.Source.PortId == "out").ToArray();
            if (outgoing.Length != 1)
            {
                if (current == callId && outgoing.Length == 0) return result.ToImmutable();
                issue("sequence.call.json.chain.data", $"nodes.{current}", "A JSON composition chain requires exactly one outgoing out data edge.");
                break;
            }
            var edge = outgoing[0];
            if (edge.Target.NodeId == outputId)
            {
                if (edge.Target.PortId != "in") issue("sequence.call.json.chain.data", $"nodes.{current}", "Output data must enter its in pin.");
                ValidateOutputType(current, outputId, definition, issue);
                break;
            }
            if (!definition.Nodes.TryGetValue(edge.Target.NodeId, out var node) || node is null ||
                node.Type is not (NodeTypes.JsonProject or NodeTypes.JsonSelectString) || edge.Target.PortId != "in")
            {
                issue("sequence.call.json.chain.data", $"nodes.{edge.Target.NodeId}", "JSON composition data edges may target only a JSON node's in pin or Output.in.");
                break;
            }
            if (result.Count == MaximumCompositionNodes)
            {
                issue("sequence.call.json.chain.limit", $"nodes.{edge.Target.NodeId}", "A JSON WorkflowCall may have at most two composition nodes.");
                break;
            }
            if (result.Count > 0 && definition.Nodes[result[^1]].Type == NodeTypes.JsonSelectString)
                issue("sequence.call.json.chain.type", $"nodes.{edge.Target.NodeId}", "JsonSelectString is terminal and cannot feed another composition node.");
            if (edges.Count(other => other.Target.NodeId == edge.Target.NodeId && other.Target.PortId == "in") != 1 ||
                edges.Count(other => other.Source.NodeId == edge.Target.NodeId && other.Source.PortId == "out") != 1)
                issue("sequence.call.json.chain.cardinality", $"nodes.{edge.Target.NodeId}", "Each JSON composition node must have exactly one in and one out data edge.");
            result.Add(edge.Target.NodeId);
            current = edge.Target.NodeId;
        }
        return result.ToImmutable();
    }

    private static void ValidateOutputType(string current, string outputId, WorkflowDefinition definition,
        Action<string, string, string> issue)
    {
        var outputType = definition.Nodes[outputId].OutputDataType;
        if (current == outputId) return;
        var final = definition.Nodes[current].Type;
        if (final == NodeTypes.JsonSelectString && outputType != WorkflowDataTypes.String)
            issue("sequence.call.json.chain.output", $"nodes.{outputId}", "JsonSelectString requires a string workflow Output.");
        if (final == NodeTypes.JsonProject && outputType != WorkflowDataTypes.Json)
            issue("sequence.call.json.chain.output", $"nodes.{outputId}", "JsonProject requires a JSON workflow Output.");
    }

    private static bool IsJsonCall(NodeDefinition node) => node.WorkflowCall?.Definition is { } child &&
        child.Nodes.TryGetValue(child.EntryNodeId, out var input) &&
        child.Nodes.Values.FirstOrDefault(candidate => candidate?.Type == NodeTypes.Output) is { } output &&
        WorkflowPorts.Outputs(input!).SingleOrDefault()?.DataType == WorkflowDataTypes.Json &&
        WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType == WorkflowDataTypes.Json;

    private static void ValidateCall(NodeDefinition node, string nodeId, Action<string, string, string> issue)
    {
        var call = node.WorkflowCall;
        if (call is null || call.Definition is null || call.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(call.WorkflowId) || !WorkflowJson.MatchesHash(call.Definition, call.ContentHash) || !IsJsonCall(node))
            issue("sequence.call.json.chain.binding", $"nodes.{nodeId}.workflowCall", "WorkflowCall must pin a JSON-input/JSON-output child with a matching identity and content hash.");
    }
}
