using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>One bounded Sequence path with a pinned JSON child call and optional pure JSON composition.</summary>
public sealed record SequenceWorkflowCallJsonPath(
    string OutputId,
    ImmutableArray<string> NodeIds,
    string CallNodeId,
    string? JsonNodeId,
    string? JsonNodeType,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash);

public sealed record SequenceWorkflowCallJsonTopologyAnalysis(
    ImmutableArray<SequenceWorkflowCallJsonPath> Paths,
    ImmutableArray<SequenceWorkflowCallTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// The schema-1.53 composition boundary is intentionally narrow: each ordered path contains
/// direct MCP nodes, one pinned JSON WorkflowCall, and at most one pure JSON selector/project
/// before the shared Output. It does not admit arbitrary JSON chains or post-call Agents.
/// </summary>
public static class SequenceWorkflowCallJsonTopology
{
    public const int MaximumPathNodes = 16;

    public static bool IsJsonCompositionGraph(WorkflowDefinition definition) =>
        definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph &&
        definition.Nodes.Values.Any(node => node?.Type == NodeTypes.WorkflowCall &&
            node.WorkflowCall?.Definition is { } child &&
            child.Nodes.TryGetValue(child.EntryNodeId, out var input) &&
            child.Nodes.Values.FirstOrDefault(candidate => candidate?.Type == NodeTypes.Output) is { } output &&
            WorkflowPorts.Outputs(input!).SingleOrDefault()?.DataType == WorkflowDataTypes.Json &&
            WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType == WorkflowDataTypes.Json);

    public static SequenceWorkflowCallJsonTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var paths = ImmutableArray.CreateBuilder<SequenceWorkflowCallJsonPath>();
        var issues = ImmutableArray.CreateBuilder<SequenceWorkflowCallTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        var sequences = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Sequence).ToArray();
        var outputs = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Output).ToArray();
        var calls = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.WorkflowCall).ToArray();
        if (sequences.Length != 1) Issue("sequence.call.json.graph", "nodes", "A schema 1.53 graph requires exactly one Sequence node.");
        if (outputs.Length != 1) Issue("sequence.call.json.graph", "nodes", "A schema 1.53 graph requires exactly one Output node.");
        if (sequences.Length != 1 || outputs.Length != 1 || definition.ExecutionConnections is not { } execution)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var sequence = sequences[0];
        var output = outputs[0];
        var executionEdges = execution.Values.Where(edge => edge is not null).ToArray();
        var outgoing = executionEdges.GroupBy(edge => edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var owned = new Dictionary<string, string>(StringComparer.Ordinal);
        var dataEdges = definition.Connections.Values.Where(edge => edge is not null).ToArray();

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
                    Issue("sequence.call.json.limit", $"nodes.{current}", "A WorkflowCall JSON path exceeds its bounded node limit.");
                    break;
                }
                if (!definition.Nodes.TryGetValue(current, out var node) || node is null)
                {
                    Issue("sequence.call.json.reference", $"nodes.{current}", "A WorkflowCall JSON path reaches a missing node.");
                    break;
                }

                if (node.Type is not (NodeTypes.McpTool or NodeTypes.WorkflowCall))
                    Issue("sequence.call.json.node", $"nodes.{current}", "A schema 1.53 execution path may contain only direct MCP nodes and one pinned JSON WorkflowCall; JSON composition nodes are data-only.");
                if (node.Type == NodeTypes.WorkflowCall)
                {
                    if (callId is not null) Issue("sequence.call.json.multiple", $"nodes.{current}", "A path may contain exactly one WorkflowCall.");
                    callId = current;
                    ValidateCall(node, current, Issue);
                }
                else if (node.Type == NodeTypes.McpTool && callId is not null)
                    Issue("sequence.call.json.order", $"nodes.{current}", "Direct MCP nodes must precede the WorkflowCall.");

                if (!owned.TryAdd(current, descriptor.Id))
                    Issue("sequence.call.json.shared", $"nodes.{current}", "WorkflowCall JSON paths may not share downstream nodes.");
                nodes.Add(current);
                var successors = outgoing.GetValueOrDefault(current) ?? [];
                if (successors.Length != 1)
                {
                    Issue("sequence.call.json.linear", $"nodes.{current}", "Every WorkflowCall JSON path node must have exactly one execution successor.");
                    break;
                }
                var next = successors[0];
                if (next.Target.PortId != "execute") Issue("sequence.call.json.port", $"nodes.{current}", "Every path successor must enter execute.");
                if (node.Type is NodeTypes.McpTool && next.Source.PortId != "then" ||
                    node.Type is NodeTypes.WorkflowCall && next.Source.PortId != "out")
                    Issue("sequence.call.json.port", $"nodes.{current}", "MCP nodes leave through then and WorkflowCall nodes leave through out.");
                current = next.Target.NodeId;
            }

            if (callId is null) continue;
            if (current != output.Key)
                Issue("sequence.call.json.terminal", $"nodes.{sequence.Key}.sequence.outputs.{descriptor.Id}", "The path must reach Output without a cycle.");
            else
            {
                var call = definition.Nodes[callId].WorkflowCall!;
                var composition = ValidateDataComposition(callId, output.Key, dataEdges, definition, Issue);
                paths.Add(new(descriptor.Id, nodes.ToImmutable(), callId, composition.NodeId, composition.NodeType,
                    call.WorkflowId, call.RevisionId, call.ContentHash));
            }
        }

        foreach (var call in calls)
            if (!paths.Any(path => path.CallNodeId == call.Key))
                Issue("sequence.call.json.unreachable", $"nodes.{call.Key}", "Every WorkflowCall must belong to one valid JSON composition path.");
        return new(paths.ToImmutable(), issues.ToImmutable());
    }

    private static void ValidateCall(NodeDefinition node, string nodeId, Action<string, string, string> issue)
    {
        var call = node.WorkflowCall;
        if (call is null || call.Definition is null || call.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(call.WorkflowId) || !WorkflowJson.MatchesHash(call.Definition, call.ContentHash))
        {
            issue("sequence.call.binding", $"nodes.{nodeId}.workflowCall", "WorkflowCall must pin an embedded child workflow, revision, workflow ID, and matching content hash.");
            return;
        }
        var input = call.Definition.Nodes.GetValueOrDefault(call.Definition.EntryNodeId);
        var output = call.Definition.Nodes.Values.FirstOrDefault(candidate => candidate?.Type == NodeTypes.Output);
        if (input is not null && output is not null &&
            WorkflowPorts.Outputs(input).SingleOrDefault()?.DataType == WorkflowDataTypes.Json &&
            WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType == WorkflowDataTypes.Json) return;
        issue("sequence.call.json.contract", $"nodes.{nodeId}.workflowCall", "The WorkflowCall requires JSON input and output boundaries for schema 1.53 composition.");
    }

    private static (string? NodeId, string? NodeType) ValidateDataComposition(string callId, string outputId,
        IReadOnlyCollection<ConnectionDefinition> edges, WorkflowDefinition definition, Action<string, string, string> issue)
    {
        var candidates = definition.Nodes.Where(entry => entry.Value?.Type is NodeTypes.JsonSelectString or NodeTypes.JsonProject &&
            edges.Any(edge => edge.Source.NodeId == callId && edge.Target.NodeId == entry.Key) &&
            edges.Any(edge => edge.Source.NodeId == entry.Key && edge.Target.NodeId == outputId)).ToArray();
        if (candidates.Length > 1)
            issue("sequence.call.json.limit", $"nodes.{callId}", "A path may contain at most one JSON composition node.");
        var composition = candidates.FirstOrDefault();
        var jsonId = composition.Value is null ? null : composition.Key;
        var jsonType = composition.Value?.Type;
        var callToJson = edges.Where(edge => edge.Source.NodeId == callId && edge.Target.NodeId == jsonId).ToArray();
        var callToOutput = edges.Where(edge => edge.Source.NodeId == callId && edge.Target.NodeId == outputId).ToArray();
        var jsonToOutput = jsonId is null ? [] : edges.Where(edge => edge.Source.NodeId == jsonId && edge.Target.NodeId == outputId).ToArray();
        if (jsonId is null)
        {
            if (callToOutput.Length == 0)
                return (null, null); // A sibling call may be executed for its side effects only.
            if (callToOutput.Length != 1 || callToOutput[0].Source.PortId != "out" || callToOutput[0].Target.PortId != "in")
                issue("sequence.call.json.data", $"nodes.{callId}", "A direct JSON WorkflowCall path must connect its out data pin to Output.in.");
            return (null, null);
        }
        if (callToJson.Length != 1 || callToJson[0].Source.PortId != "out" || callToJson[0].Target.PortId != "in" ||
            jsonToOutput.Length != 1 || jsonToOutput[0].Source.PortId != "out" || jsonToOutput[0].Target.PortId != "in")
            issue("sequence.call.json.data", $"nodes.{jsonId}", "A JSON composition path requires call.out → JSON node.in → Output.in data edges.");
        if (jsonType == NodeTypes.JsonSelectString && definition.Nodes[outputId].OutputDataType != WorkflowDataTypes.String)
            issue("sequence.call.json.output", $"nodes.{outputId}", "JsonSelectString composition requires a string workflow Output.");
        if (jsonType == NodeTypes.JsonProject && definition.Nodes[outputId].OutputDataType != WorkflowDataTypes.Json)
            issue("sequence.call.json.output", $"nodes.{outputId}", "JsonProject composition requires a JSON workflow Output.");
        return (jsonId, jsonType);
    }
}
