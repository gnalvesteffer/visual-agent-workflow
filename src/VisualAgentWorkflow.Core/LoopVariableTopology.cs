using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Describes a candidate bounded loop body that contains run-scoped scalar state.</summary>
public sealed record LoopVariableTopology(
    string LoopId,
    string BodyAnchorId,
    ImmutableArray<string> BodyNodeIds,
    ImmutableArray<string> VariableNodeIds);

/// <summary>A stable diagnostic from the non-authoritative loop-variable proof analyzer.</summary>
public sealed record LoopVariableTopologyIssue(string Code, string Path, string Message);

/// <summary>
/// Proof result for the future explicit loop-variable contract. This analyzer does not admit a
/// schema, alter validation, or create a runtime plan.
/// </summary>
public sealed record LoopVariableTopologyAnalysis(
    LoopVariableTopology? Topology,
    ImmutableArray<LoopVariableTopologyIssue> Issues)
{
    public bool IsValid => Topology is not null && Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Checks the smallest explicit loop-variable shape: one bounded loop, one linear body, scalar
/// Get/Set nodes, and no other control or authority-bearing node. It is intentionally advisory.
/// </summary>
public static class LoopVariableTopologyAnalyzer
{
    private static readonly ImmutableHashSet<string> ScalarConstants =
        [NodeTypes.StringConstant, NodeTypes.BooleanConstant, NodeTypes.IntegerConstant, NodeTypes.NumberConstant];

    private static readonly ImmutableHashSet<string> AllowedNodes =
        [NodeTypes.Input, NodeTypes.Loop, NodeTypes.LoopBody, NodeTypes.VariableGet,
            NodeTypes.VariableSet, NodeTypes.Output, NodeTypes.StringConstant,
            NodeTypes.BooleanConstant, NodeTypes.IntegerConstant, NodeTypes.NumberConstant];

    public const int MaximumBodyNodes = 16;

    public static LoopVariableTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var issues = ImmutableArray.CreateBuilder<LoopVariableTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        var loops = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Loop).Select(pair => pair.Key).ToArray();
        var anchors = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.LoopBody).Select(pair => pair.Key).ToArray();
        if (loops.Length != 1) Issue("loop.count", "nodes", "A loop-variable topology requires exactly one bounded loop.");
        if (anchors.Length != 1) Issue("loop.body.count", "nodes", "A loop-variable topology requires exactly one loop-body anchor.");

        var input = definition.Nodes.SingleOrDefault(pair => pair.Value?.Type == NodeTypes.Input);
        var output = definition.Nodes.SingleOrDefault(pair => pair.Value?.Type == NodeTypes.Output);
        if (input.Value is null || output.Value is null || definition.EntryNodeId != input.Key)
            Issue("workflow.terminals", "nodes", "A loop-variable topology requires one input entry and one output node.");
        if (definition.Variables is null || definition.Variables.Count == 0)
            Issue("variables.missing", "variables", "A loop-variable topology requires at least one declared scalar variable.");

        foreach (var (id, node) in definition.Nodes)
        {
            if (node is null || !AllowedNodes.Contains(node.Type))
                Issue("node.unsupported", $"nodes.{id}", "Only Input, one bounded loop, one body anchor, scalar Get/Set nodes, scalar constants, and Output are allowed.");
            if (node?.Type is NodeTypes.VariableGet or NodeTypes.VariableSet)
                ValidateVariableNode(id, node, definition.Variables, Issue);
        }

        if (definition.ExecutionConnections is null)
        {
            Issue("execution.missing", "executionConnections", "Loop-variable topology requires explicit execution connections.");
            return new(null, issues.ToImmutable());
        }
        if (loops.Length != 1 || anchors.Length != 1 || input.Value is null || output.Value is null)
            return new(null, issues.ToImmutable());

        var loopId = loops[0];
        var anchorId = anchors[0];
        var edges = definition.ExecutionConnections
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(pair => pair.Value is not null)
            .Select(pair => (Id: pair.Key, Edge: pair.Value!))
            .ToArray();
        var outgoing = edges.GroupBy(item => item.Edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var incoming = edges.GroupBy(item => item.Edge.Target.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var (id, edge) in edges)
        {
            if (!definition.Nodes.TryGetValue(edge.Source.NodeId, out var source) || source is null ||
                !definition.Nodes.TryGetValue(edge.Target.NodeId, out var target) || target is null)
            {
                Issue("execution.reference", $"executionConnections.{id}", "Both execution endpoints must reference existing nodes.");
                continue;
            }
            if (!IsExecutionPort(source.Type, edge.Source.PortId, output: true) ||
                !IsExecutionPort(target.Type, edge.Target.PortId, output: false) || edge.Kind != "execution")
                Issue("execution.port", $"executionConnections.{id}", "Every proof edge must connect the declared future loop-variable execution pins.");
        }

        var initial = EdgesFrom(outgoing, input.Key, "then").Where(edge => edge.Edge.Target.NodeId == loopId).ToArray();
        var bodyStart = EdgesFrom(outgoing, loopId, "body").Where(edge => edge.Edge.Target.NodeId == anchorId).ToArray();
        var done = EdgesFrom(outgoing, loopId, "done").Where(edge => edge.Edge.Target.NodeId == output.Key).ToArray();
        if (initial.Length != 1) Issue("loop.initial", $"nodes.{loopId}", "Input must activate the loop exactly once.");
        if (bodyStart.Length != 1) Issue("loop.body.start", $"nodes.{loopId}.body", "Loop body must activate the body anchor exactly once.");
        if (done.Length != 1) Issue("loop.done", $"nodes.{loopId}.done", "Loop done must activate Output exactly once.");
        if (incoming.GetValueOrDefault(loopId)?.Count(item => item.Edge.Target.PortId == "execute") != 2)
            Issue("loop.input", $"nodes.{loopId}.execute", "Loop requires exactly one initial and one body-return activation.");

        var body = ImmutableArray.CreateBuilder<string>();
        var variables = ImmutableArray.CreateBuilder<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = anchorId;
        while (visited.Add(current))
        {
            if (body.Count >= MaximumBodyNodes)
            {
                Issue("loop.body.limit", $"nodes.{current}", $"The proof body may contain at most {MaximumBodyNodes} nodes.");
                break;
            }
            if (!definition.Nodes.TryGetValue(current, out var node) || node is null ||
                (!AllowedNodes.Contains(node.Type) && node.Type != NodeTypes.LoopBody))
            {
                Issue("loop.body.node", $"nodes.{current}", "The loop body contains an unsupported node.");
                break;
            }
            body.Add(current);
            if (node.Type is NodeTypes.VariableGet or NodeTypes.VariableSet) variables.Add(current);

            var next = EdgesFrom(outgoing, current, current == anchorId ? "then" : "then");
            if (next.Length != 1)
            {
                Issue("loop.body.path", $"nodes.{current}", "Each body node must have exactly one successor.");
                break;
            }
            if (next[0].Edge.Target.NodeId == loopId)
            {
                if (next[0].Edge.Target.PortId != "execute")
                    Issue("loop.body.return", $"executionConnections.{next[0].Id}", "The body must return to the loop execute pin.");
                break;
            }
            current = next[0].Edge.Target.NodeId;
        }
        if (!visited.Contains(loopId) && !EdgesFrom(outgoing, current, "then").Any(edge => edge.Edge.Target.NodeId == loopId))
            Issue("loop.body.return", $"nodes.{loopId}", "The linear body must return to the loop execute pin.");
        if (body.Count == 0) Issue("loop.body.empty", $"nodes.{anchorId}", "The loop body may not be empty.");

        var reachable = Reachable(input.Key, outgoing);
        foreach (var id in definition.Nodes.Keys.Where(id => !ScalarConstants.Contains(definition.Nodes[id]?.Type ?? "") && !reachable.Contains(id)))
            Issue("graph.unreachable", $"nodes.{id}", "Every executable loop-variable node must be reachable from Input.");

        var topology = issues.Count == 0
            ? new LoopVariableTopology(loopId, anchorId, body.ToImmutable(), variables.ToImmutable())
            : null;
        return new(topology, issues.ToImmutable());
    }

    private static void ValidateVariableNode(string id, NodeDefinition node,
        ImmutableSortedDictionary<string, WorkflowVariableDefinition>? variables,
        Action<string, string, string> issue)
    {
        if (node.VariableId is not { Length: > 0 } variableId || variables?.TryGetValue(variableId, out var variable) != true || variable is null)
        {
            issue("variable.reference", $"nodes.{id}", "Every variable node must reference a declared scalar variable.");
            return;
        }
        var nodeType = node.Type == NodeTypes.VariableGet ? node.OutputDataType : node.InputDataType;
        if (nodeType != (variable.Type ?? WorkflowDataTypes.String))
            issue("variable.type", $"nodes.{id}", "A variable node type must match its declaration.");
    }

    private static (string Id, ConnectionDefinition Edge)[] EdgesFrom(
        IReadOnlyDictionary<string, (string Id, ConnectionDefinition Edge)[]> outgoing, string nodeId, string port) =>
        outgoing.GetValueOrDefault(nodeId)?.Where(item => item.Edge.Source.PortId == port).ToArray() ?? [];

    private static HashSet<string> Reachable(string inputId,
        IReadOnlyDictionary<string, (string Id, ConnectionDefinition Edge)[]> outgoing)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([inputId]);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current)) continue;
            foreach (var edge in outgoing.GetValueOrDefault(current) ?? []) pending.Push(edge.Edge.Target.NodeId);
        }
        return reachable;
    }

    private static bool IsExecutionPort(string nodeType, string port, bool output) => output
        ? nodeType switch
        {
            NodeTypes.Input or NodeTypes.LoopBody or NodeTypes.VariableGet or NodeTypes.VariableSet => port == "then",
            NodeTypes.Loop => port is "body" or "done",
            _ => false
        }
        : nodeType switch
        {
            NodeTypes.Loop or NodeTypes.LoopBody or NodeTypes.VariableGet or NodeTypes.VariableSet or NodeTypes.Output => port == "execute",
            _ => false
        };
}
