using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>One bounded Sequence path with a pinned child call and optional terminal transform.</summary>
public sealed record SequenceWorkflowCallTransformPath(
    string OutputId,
    ImmutableArray<string> NodeIds,
    string CallNodeId,
    string? TransformNodeId,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash);

public sealed record SequenceWorkflowCallTransformTopologyAnalysis(
    ImmutableArray<SequenceWorkflowCallTransformPath> Paths,
    ImmutableArray<SequenceWorkflowCallTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// An additive schema-1.51 analyzer. It deliberately does not broaden the schema-1.50
/// terminal-call contract: only one deterministic transform may follow a WorkflowCall.
/// </summary>
public static class SequenceWorkflowCallTransformTopology
{
    public const int MaximumPathNodes = 16;

    public static SequenceWorkflowCallTransformTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var paths = ImmutableArray.CreateBuilder<SequenceWorkflowCallTransformPath>();
        var issues = ImmutableArray.CreateBuilder<SequenceWorkflowCallTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        var sequences = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Sequence).ToArray();
        var outputs = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Output).ToArray();
        var callNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.WorkflowCall).ToArray();
        if (sequences.Length != 1) Issue("sequence.call.transform.graph", "nodes", "A schema 1.51 graph requires exactly one Sequence node.");
        if (outputs.Length != 1) Issue("sequence.call.transform.graph", "nodes", "A schema 1.51 graph requires exactly one Output node.");
        if (sequences.Length != 1 || outputs.Length != 1 || definition.ExecutionConnections is not { } execution)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var sequence = sequences[0];
        var output = outputs[0];
        var edges = execution.Values.Where(edge => edge is not null).ToArray();
        var outgoing = edges.GroupBy(edge => edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var owned = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var descriptor in sequence.Value.Sequence?.Outputs ?? [])
        {
            var roots = edges.Where(edge => edge.Source.NodeId == sequence.Key &&
                edge.Source.PortId == SequenceOutputLimits.PinId(descriptor.Id)).ToArray();
            if (roots.Length != 1) continue;
            var current = roots[0].Target.NodeId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var nodes = ImmutableArray.CreateBuilder<string>();
            string? callId = null;
            string? transformId = null;
            while (current != output.Key && visited.Add(current))
            {
                if (nodes.Count >= MaximumPathNodes)
                {
                    Issue("sequence.call.transform.limit", $"nodes.{current}", "A WorkflowCall transform path exceeds its bounded node limit.");
                    break;
                }
                if (!definition.Nodes.TryGetValue(current, out var node) || node is null)
                {
                    Issue("sequence.call.transform.reference", $"nodes.{current}", "A WorkflowCall transform path reaches a missing node.");
                    break;
                }
                var isTransform = node.Type is NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse;
                if (node.Type is not (NodeTypes.McpTool or NodeTypes.WorkflowCall or NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse))
                    Issue("sequence.call.transform.node", $"nodes.{current}", "A schema 1.51 path may contain only direct MCP nodes, one WorkflowCall, and one deterministic transform.");
                if (node.Type == NodeTypes.WorkflowCall)
                {
                    if (callId is not null) Issue("sequence.call.transform.multiple", $"nodes.{current}", "A path may contain exactly one WorkflowCall.");
                    callId = current;
                    ValidateCall(node, current, Issue);
                }
                else if (isTransform)
                {
                    if (callId is null) Issue("sequence.call.transform.order", $"nodes.{current}", "The deterministic transform must follow the WorkflowCall.");
                    if (transformId is not null) Issue("sequence.call.transform.limit", $"nodes.{current}", "A path may contain exactly one deterministic transform after the WorkflowCall.");
                    transformId = current;
                }
                else if (node.Type == NodeTypes.McpTool && callId is not null)
                    Issue("sequence.call.transform.order", $"nodes.{current}", "Direct MCP nodes must precede the WorkflowCall and transform.");

                if (!owned.TryAdd(current, descriptor.Id))
                    Issue("sequence.call.transform.shared", $"nodes.{current}", "WorkflowCall transform paths may not share downstream nodes.");
                nodes.Add(current);
                var successors = outgoing.GetValueOrDefault(current) ?? [];
                if (successors.Length != 1)
                {
                    Issue("sequence.call.transform.linear", $"nodes.{current}", "Every WorkflowCall transform path node must have exactly one execution successor.");
                    break;
                }
                var next = successors[0];
                if (next.Target.PortId != "execute") Issue("sequence.call.transform.port", $"nodes.{current}", "Every path successor must enter execute.");
                if (node.Type == NodeTypes.McpTool && next.Source.PortId != "then" ||
                    node.Type == NodeTypes.WorkflowCall && next.Source.PortId != "out" ||
                    isTransform && next.Source.PortId != "then")
                    Issue("sequence.call.transform.port", $"nodes.{current}", "MCP and deterministic transforms leave through then; WorkflowCall leaves through out.");
                if (node.Type == NodeTypes.WorkflowCall && next.Target.NodeId != output.Key &&
                    definition.Nodes.GetValueOrDefault(next.Target.NodeId)?.Type is not (NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse))
                    Issue("sequence.call.transform.position", $"nodes.{current}", "WorkflowCall may be followed only by one deterministic transform or Output.");
                if (isTransform && next.Target.NodeId != output.Key)
                    Issue("sequence.call.transform.position", $"nodes.{current}", "The deterministic transform must be terminal before Output.");
                current = next.Target.NodeId;
            }
            if (callId is null) continue;
            if (current != output.Key)
                Issue("sequence.call.transform.terminal", $"nodes.{sequence.Key}.sequence.outputs.{descriptor.Id}", "The path must reach Output without a cycle.");
            else
            {
                var call = definition.Nodes[callId].WorkflowCall!;
                paths.Add(new(descriptor.Id, nodes.ToImmutable(), callId, transformId, call.WorkflowId,
                    call.RevisionId, call.ContentHash));
            }
        }
        foreach (var call in callNodes)
            if (!paths.Any(path => path.CallNodeId == call.Key))
                Issue("sequence.call.transform.unreachable", $"nodes.{call.Key}", "Every WorkflowCall must belong to one valid path.");
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
        if (call.Definition.Nodes.TryGetValue(call.Definition.EntryNodeId, out var input) &&
            call.Definition.Nodes.Values.FirstOrDefault(candidate => candidate?.Type == NodeTypes.Output) is { } output &&
            WorkflowPorts.Outputs(input!).SingleOrDefault()?.DataType == WorkflowDataTypes.String &&
            WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType == WorkflowDataTypes.String) return;
        issue("sequence.call.contract", $"nodes.{nodeId}.workflowCall", "The WorkflowCall requires string input and output boundaries.");
    }
}
