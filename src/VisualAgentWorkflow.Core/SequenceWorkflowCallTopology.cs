using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Describes one bounded Sequence output path that reaches a child workflow call.</summary>
public sealed record SequenceWorkflowCallPath(
    string OutputId,
    ImmutableArray<string> NodeIds,
    string CallNodeId,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash);

/// <summary>A stable diagnostic for a bounded Sequence-to-WorkflowCall path.</summary>
public sealed record SequenceWorkflowCallTopologyIssue(string Code, string Path, string Message);

/// <summary>Framework-independent analysis of the deferred Sequence-to-WorkflowCall contract.</summary>
public sealed record SequenceWorkflowCallTopologyAnalysis(
    ImmutableArray<SequenceWorkflowCallPath> Paths,
    ImmutableArray<SequenceWorkflowCallTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Defines the proof-only call-bearing Sequence topology. It is intentionally independent of
/// schema admission: callers may use it to diagnose a saveable draft, but it does not promote a
/// schema or make a persisted graph executable.
/// </summary>
public static class SequenceWorkflowCallTopology
{
    public const int MaximumPathNodes = 16;

    public static SequenceWorkflowCallTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var paths = ImmutableArray.CreateBuilder<SequenceWorkflowCallPath>();
        var issues = ImmutableArray.CreateBuilder<SequenceWorkflowCallTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        var callNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.WorkflowCall).ToArray();
        if (callNodes.Length == 0)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var sequences = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Sequence).ToArray();
        var outputs = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Output).ToArray();
        if (sequences.Length != 1)
            Issue("sequence.call.graph", "nodes", "A Sequence workflow-call graph requires exactly one Sequence node.");
        if (outputs.Length != 1)
            Issue("sequence.call.graph", "nodes", "A Sequence workflow-call graph requires exactly one Output node.");
        if (sequences.Length != 1 || outputs.Length != 1 || definition.ExecutionConnections is not { } execution)
        {
            return new(paths.ToImmutable(), issues.ToImmutable());
        }
        var sequence = sequences[0];
        var output = outputs[0];

        var edges = execution.Values.Where(edge => edge is not null).ToArray();
        foreach (var edge in edges)
            if (edge.Kind != "execution" || edge.Target.PortId != "execute")
                Issue("sequence.call.path.port", "executionConnections", "Every call-bearing Sequence edge must be an execution edge entering the target execute pin.");
        var outgoing = edges.GroupBy(edge => edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var owned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var outputDescriptor in sequence.Value.Sequence?.Outputs ?? [])
        {
            var rootPort = SequenceOutputLimits.PinId(outputDescriptor.Id);
            var rootEdges = edges.Where(edge => edge.Source.NodeId == sequence.Key && edge.Source.PortId == rootPort).ToArray();
            if (rootEdges.Length != 1) continue;
            if (rootEdges[0].Target.PortId != "execute")
                Issue("sequence.call.path.port", $"nodes.{sequence.Key}.sequence.outputs.{outputDescriptor.Id}", "A Sequence output must enter the target execute pin.");

            var current = rootEdges[0].Target.NodeId;
            var nodeIds = ImmutableArray.CreateBuilder<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? callNodeId = null;
            var unsupportedNodeBeforeCall = false;
            while (current != output.Key && visited.Add(current))
            {
                if (nodeIds.Count >= MaximumPathNodes)
                {
                    Issue("sequence.call.path.limit", $"nodes.{current}", "A call-bearing Sequence path exceeds its bounded node limit.");
                    break;
                }
                if (!definition.Nodes.TryGetValue(current, out var node) || node is null)
                {
                    Issue("sequence.call.path.reference", $"nodes.{current}", "A call-bearing Sequence path reaches a missing node.");
                    break;
                }
                if (node.Type == NodeTypes.WorkflowCall)
                {
                    if (callNodeId is not null)
                        Issue("sequence.call.multiple", $"nodes.{current}", "A Sequence output may contain exactly one WorkflowCall.");
                    callNodeId = current;
                    ValidateCall(node, current, Issue);
                }
                else if (node.Type != NodeTypes.McpTool)
                    unsupportedNodeBeforeCall = true;
                if (callNodeId is not null && unsupportedNodeBeforeCall)
                    Issue("sequence.call.path.node", $"nodes.{current}", "A call-bearing Sequence path may contain only direct MCP nodes before one terminal WorkflowCall.");

                if (!owned.TryAdd(current, outputDescriptor.Id))
                    Issue("sequence.call.path.shared", $"nodes.{current}", "Call-bearing Sequence paths may not share downstream nodes.");
                nodeIds.Add(current);
                var successors = outgoing.GetValueOrDefault(current) ?? [];
                if (successors.Length != 1)
                {
                    Issue("sequence.call.path.linear", $"nodes.{current}", "Every call-bearing Sequence path node must have exactly one execution successor.");
                    break;
                }
                var next = successors[0];
                if (next.Target.PortId != "execute")
                    Issue("sequence.call.path.port", $"nodes.{current}", "Every call-bearing Sequence successor must enter its execute pin.");
                if (node.Type == NodeTypes.McpTool && next.Source.PortId != "then" ||
                    node.Type == NodeTypes.WorkflowCall && next.Source.PortId != "out")
                    Issue("sequence.call.path.port", $"nodes.{current}", "MCP paths leave through then and workflow calls leave through out.");
                if (node.Type == NodeTypes.WorkflowCall && next.Target.NodeId != output.Key)
                    Issue("sequence.call.path.terminal", $"nodes.{current}", "WorkflowCall must be the terminal node before the Output barrier.");
                current = next.Target.NodeId;
            }

            if (callNodeId is null) continue;
            if (current != output.Key)
                Issue("sequence.call.path.terminal", $"nodes.{sequence.Key}.sequence.outputs.{outputDescriptor.Id}", "A call-bearing Sequence path must reach the Output barrier without a cycle.");
            else
            {
                var call = definition.Nodes[callNodeId].WorkflowCall!;
                paths.Add(new(outputDescriptor.Id, nodeIds.ToImmutable(), callNodeId, call.WorkflowId,
                    call.RevisionId, call.ContentHash));
            }
        }

        foreach (var callNode in callNodes)
            if (!paths.Any(path => path.CallNodeId == callNode.Key))
                Issue("sequence.call.unreachable", $"nodes.{callNode.Key}", "Every WorkflowCall in a Sequence graph must belong to one valid call-bearing output path.");

        return new(paths.ToImmutable(), issues.ToImmutable());
    }

    private static void ValidateCall(NodeDefinition node, string nodeId,
        Action<string, string, string> issue)
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
            WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType == WorkflowDataTypes.String)
            return;

        issue("sequence.call.contract", $"nodes.{nodeId}.workflowCall", "The bounded Sequence WorkflowCall contract requires string input and string output boundaries.");
    }
}
