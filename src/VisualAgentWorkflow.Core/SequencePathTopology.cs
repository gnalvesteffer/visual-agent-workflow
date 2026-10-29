using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Describes the executable nodes owned by one ordered Sequence output.</summary>
public sealed record SequencePathTopology(
    string OutputId,
    string TerminalNodeId,
    ImmutableArray<string> NodeIds,
    bool ContainsPlainTextAgent,
    bool ContainsDeterministicTransform)
{
    public bool IsDirectMcpPath => !ContainsPlainTextAgent && !ContainsDeterministicTransform;
}

/// <summary>A stable, actionable diagnostic produced by the non-authoritative Sequence path analyzer.</summary>
public sealed record SequencePathTopologyIssue(string Code, string Path, string Message);

/// <summary>
/// Result of analyzing a persisted Sequence graph against the proposed bounded Agent-path shape.
/// </summary>
/// <remarks>
/// This analyzer intentionally does not admit a schema, promote a definition, or create a runtime
/// plan. It is a proof seam for persisted-graph admission work; callers must still use the
/// schema-specific validator and runner until the contract is promoted.
/// </remarks>
public sealed record SequencePathTopologyAnalysis(
    ImmutableArray<SequencePathTopology> Paths,
    ImmutableArray<SequencePathTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Analyzes disjoint, bounded Sequence output paths using the same node-family contract as the public schema catalog.
/// Schema 1.46 paths may contain zero or more direct MCP nodes followed by one terminal plain-text Agent;
/// schemas 1.47 and 1.48 additionally admit one terminal deterministic text step;
/// schema 1.49 admits at most one uppercase and one reverse step in sequence.
/// </summary>
public static class SequencePathTopologyAnalyzer
{
    public const int MaximumPathNodes = 16;

    public static SequencePathTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var paths = ImmutableArray.CreateBuilder<SequencePathTopology>();
        var issues = ImmutableArray.CreateBuilder<SequencePathTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        var sequenceNodes = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Sequence).ToArray();
        var sequence = sequenceNodes.Length == 1 ? sequenceNodes[0] : default;
        var input = definition.Nodes.SingleOrDefault(pair => pair.Value?.Type == NodeTypes.Input);
        var output = definition.Nodes.SingleOrDefault(pair => pair.Value?.Type == NodeTypes.Output);
        if (sequenceNodes.Length != 1)
            Issue("sequence.count", "nodes", "A Sequence path topology requires exactly one Sequence node.");
        if (input.Value is null || output.Value is null || definition.EntryNodeId != input.Key)
            Issue("workflow.terminals", "nodes", "A Sequence path topology requires one input entry and one output node.");
        if (definition.ExecutionConnections is null)
        {
            Issue("execution.missing", "executionConnections", "A Sequence path topology requires explicit execution connections.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }
        if (sequence.Value is null || output.Value is null)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var edges = definition.ExecutionConnections
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .Where(edge => edge is not null)
            .ToArray();
        foreach (var edge in edges)
        {
            if (edge.Kind != "execution" ||
                !definition.Nodes.TryGetValue(edge.Source.NodeId, out var sourceNode) || sourceNode is null ||
                !definition.Nodes.TryGetValue(edge.Target.NodeId, out var targetNode) || targetNode is null ||
                !WorkflowPorts.ExplicitExecutionOutputs(sourceNode).Contains(edge.Source.PortId) ||
                !WorkflowPorts.ExplicitExecutionInputs(targetNode).Contains(edge.Target.PortId))
                Issue("sequence.path.port", "executionConnections", "Every Sequence topology edge must connect declared execution ports.");
        }
        var outgoing = edges.GroupBy(edge => edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var incoming = edges.GroupBy(edge => edge.Target.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var owned = new HashSet<string>(StringComparer.Ordinal) { input.Key, sequence.Key, output.Key };
        var pathOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var sequenceOutputs = sequence.Value.Sequence?.Outputs ?? [];
        if (sequence.Value.Sequence is null)
            Issue("sequence.configuration", $"nodes.{sequence.Key}.sequence", "The Sequence node must declare ordered outputs.");

        foreach (var sequenceOutput in sequenceOutputs)
        {
            var outputId = sequenceOutput.Id;
            var outputPin = SequenceOutputLimits.PinId(outputId);
            var rootEdges = edges.Where(edge => edge.Source.NodeId == sequence.Key && edge.Source.PortId == outputPin).ToArray();
            var name = $"'{sequenceOutput.Label}' ({outputPin})";
            if (rootEdges.Length != 1)
            {
                Issue(rootEdges.Length == 0 ? "sequence.path.root" : "sequence.path.duplicate",
                    $"nodes.{sequence.Key}.sequence.outputs.{outputId}",
                    rootEdges.Length == 0
                        ? $"Sequence output {name} has no downstream path. Connect it to a direct MCP path or a single plain-text Agent path ending at Output."
                        : $"Sequence output {name} starts {rootEdges.Length} downstream paths; connect exactly one path.");
                continue;
            }

            var current = rootEdges[0].Target.NodeId;
            if (rootEdges[0].Kind != "execution" || rootEdges[0].Target.PortId != "execute")
                Issue("sequence.path.port", $"nodes.{sequence.Key}.sequence.outputs.{outputId}",
                    $"Sequence output {name} must enter the target execution pin.");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var nodeIds = ImmutableArray.CreateBuilder<string>();
            var containsAgent = false;
            var containsTransform = false;
            var agentCount = 0;
            var transformCount = 0;
            var sawUppercase = false;
            var sawReverse = false;
            var transformStarted = false;
            var isTransformChainGraph = definition.SchemaVersion == WorkflowSchemaVersions.SequenceTransformChainGraph;
            while (current != output.Key && visited.Add(current))
            {
                if (nodeIds.Count >= MaximumPathNodes)
                {
                    Issue("sequence.path.limit", $"nodes.{sequence.Key}.sequence.outputs.{outputId}",
                        $"Sequence output {name} exceeds the {MaximumPathNodes}-node downstream path limit.");
                    break;
                }
                if (!definition.Nodes.TryGetValue(current, out var node) || node is null)
                {
                    Issue("sequence.path.reference", $"nodes.{current}",
                        $"Sequence output {name} reaches missing node '{current}'.");
                    break;
                }
                if (node.Type is not (NodeTypes.McpTool or NodeTypes.Agent or NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse))
                {
                    Issue("sequence.path.node", $"nodes.{current}",
                        $"Sequence output {name} reaches '{current}', which is neither a direct MCP tool, an approved plain-text Agent, nor a deterministic uppercase step.");
                }
                if (!owned.Add(current))
                {
                    var owner = pathOwners.GetValueOrDefault(current);
                    var ownerText = owner is null ? "another Sequence path" : $"Sequence output '{owner}'";
                    Issue("sequence.path.shared", $"nodes.{current}",
                        $"Sequence output {name} shares '{current}' with {ownerText}; each path must remain disjoint.");
                }
                else
                {
                    pathOwners[current] = outputId;
                }

                nodeIds.Add(current);
                if (node.Type == NodeTypes.Agent)
                {
                    containsAgent = true;
                    agentCount++;
                    ValidateAgent(node, current, outputId, Issue);
                }
                var isTransform = node.Type is (NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse);
                if (isTransform)
                {
                    containsTransform = true;
                    var hadTransformBefore = transformStarted;
                    transformCount++;
                    if (!isTransformChainGraph && hadTransformBefore)
                        Issue("sequence.transform.position", $"nodes.{current}", "The deterministic text step must be terminal after any direct MCP tools.");
                    transformStarted = true;
                    if (transformCount > 2)
                        Issue("sequence.transform.limit", $"nodes.{current}", "Schema 1.49 permits at most one uppercase and one reverse step per Sequence path.");
                    if (node.Type == NodeTypes.SequenceUppercase && sawUppercase)
                        Issue("sequence.transform.duplicate", $"nodes.{current}", "Schema 1.49 permits at most one uppercase step per Sequence path.");
                    if (node.Type == NodeTypes.SequenceReverse && sawReverse)
                        Issue("sequence.transform.duplicate", $"nodes.{current}", "Schema 1.49 permits at most one reverse step per Sequence path.");
                    sawUppercase |= node.Type == NodeTypes.SequenceUppercase;
                    sawReverse |= node.Type == NodeTypes.SequenceReverse;
                }
                if (isTransformChainGraph && node.Type == NodeTypes.McpTool && transformStarted)
                    Issue("sequence.transform.order", $"nodes.{current}", "Schema 1.49 requires all direct MCP nodes to precede deterministic text steps.");
                var next = outgoing.GetValueOrDefault(current) ?? [];
                if (next.Length != 1)
                {
                    Issue("sequence.path.linear", $"nodes.{current}",
                        $"Sequence output {name} reaches '{current}' with {next.Length} execution successors; each path node must have exactly one successor.");
                    break;
                }
                var nextEdge = next[0];
                if (nextEdge.Target.PortId != "execute")
                    Issue("sequence.path.port", $"nodes.{current}", "Each Sequence path successor must enter the target execution pin.");
                if (node.Type == NodeTypes.McpTool && nextEdge.Source.PortId != "then" ||
                    node.Type == NodeTypes.Agent && nextEdge.Source.PortId != "out" ||
                    node.Type is (NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse) && nextEdge.Source.PortId != "then")
                    Issue("sequence.path.port", $"nodes.{current}", "MCP tool paths must leave through 'then', Agent paths through 'out', and uppercase paths through 'then'.");
                if (isTransform)
                {
                    var nextIsTransform = definition.Nodes.TryGetValue(nextEdge.Target.NodeId, out var nextNode) &&
                        nextNode?.Type is (NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse);
                    if ((!isTransformChainGraph && nextEdge.Target.NodeId != output.Key) ||
                        (isTransformChainGraph && nextEdge.Target.NodeId != output.Key && !nextIsTransform))
                        Issue("sequence.transform.position", $"nodes.{current}", isTransformChainGraph
                            ? "Schema 1.49 transform steps may only be followed by the other deterministic text step or Output."
                            : "The deterministic text step must connect directly to Output.");
                }
                current = nextEdge.Target.NodeId;
            }

            if (current != output.Key)
            {
                Issue("sequence.path.terminal", $"nodes.{sequence.Key}.sequence.outputs.{outputId}",
                    $"Sequence output {name} does not reach Output without a cycle or incomplete path.");
            }
            else
            {
                var finalEdge = nodeIds.Count == 0
                    ? rootEdges[0]
                    : (outgoing.GetValueOrDefault(nodeIds[nodeIds.Count - 1]) ?? [])
                        .SingleOrDefault(edge => edge.Target.NodeId == output.Key);
                if (finalEdge is null)
                    Issue("sequence.path.terminal", $"nodes.{sequence.Key}.sequence.outputs.{outputId}",
                        $"Sequence output {name} must end at the workflow Output.");
                if (containsAgent && (agentCount != 1 || definition.Nodes[nodeIds[nodeIds.Count - 1]].Type != NodeTypes.Agent))
                    Issue("sequence.path.mixed", $"nodes.{sequence.Key}.sequence.outputs.{outputId}",
                        $"Sequence output {name} mixes MCP tools with an Agent; the bounded Agent path must contain exactly one terminal plain-text Agent after its direct MCP tools.");
                paths.Add(new(outputId, current, nodeIds.ToImmutable(), containsAgent, containsTransform));
            }
        }

        if (input.Value is not null && (outgoing.GetValueOrDefault(input.Key) ?? []).Count(edge => edge.Source.PortId == "then") != 1)
            Issue("graph.degree", $"nodes.{input.Key}", "Input must activate Sequence exactly once.");
        if ((incoming.GetValueOrDefault(sequence.Key) ?? []).Count(edge => edge.Target.PortId == "execute") != 1)
            Issue("graph.degree", $"nodes.{sequence.Key}", "Sequence requires exactly one execution input.");
        if ((outgoing.GetValueOrDefault(sequence.Key) ?? []).Count(edge => edge.Source.PortId.StartsWith("then.", StringComparison.Ordinal)) != sequenceOutputs.Length)
            Issue("graph.degree", $"nodes.{sequence.Key}", "Every Sequence output must connect exactly once.");
        if ((incoming.GetValueOrDefault(output.Key) ?? []).Count(edge => edge.Target.PortId == "execute") != sequenceOutputs.Length)
            Issue("graph.degree", $"nodes.{output.Key}", "Output must receive one activation from every Sequence output.");

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([input.Key]);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current)) continue;
            foreach (var edge in outgoing.GetValueOrDefault(current) ?? []) pending.Push(edge.Target.NodeId);
        }
        foreach (var nodeId in definition.Nodes.Keys.Where(nodeId => !reachable.Contains(nodeId)))
            Issue("graph.unreachable", $"nodes.{nodeId}", "Every node must be reachable from the input entry.");

        return new(paths.ToImmutable(), issues.ToImmutable());
    }

    private static void ValidateAgent(NodeDefinition node, string nodeId, string outputId,
        Action<string, string, string> issue)
    {
        var agent = node.Agent;
        if (agent is null)
        {
            issue("sequence.agent.configuration", $"nodes.{nodeId}.agent", $"Sequence output '{outputId}' requires an Agent configuration.");
            return;
        }
        if (string.IsNullOrWhiteSpace(agent.ProviderId) || string.IsNullOrWhiteSpace(agent.Instructions))
            issue("sequence.agent.configuration", $"nodes.{nodeId}.agent", "The bounded Agent path requires a provider and instructions.");
        if (!agent.Tools.IsDefaultOrEmpty || !agent.Skills.GetValueOrDefault().IsDefaultOrEmpty ||
            !agent.Delegates.GetValueOrDefault().IsDefaultOrEmpty || !agent.GraphFunctionTools.GetValueOrDefault().IsDefaultOrEmpty ||
            !agent.Routes.GetValueOrDefault().IsDefaultOrEmpty || agent.RequireStructuredResult == true)
            issue("sequence.agent.restriction", $"nodes.{nodeId}.agent", "The bounded Agent path allows one plain-text Agent with no tools, skills, delegates, routes, graph-function tools, or structured result.");
        if (node.Approval is not null || node.Condition is not null || node.WorkflowCall is not null || node.Loop is not null ||
            node.ParallelFork is not null || node.ParallelBranch is not null || node.ParallelJoin is not null || node.ParallelRendezvous is not null)
            issue("sequence.agent.restriction", $"nodes.{nodeId}", "The bounded Agent path cannot carry routing, approval, loop, parallel, or workflow-call configuration.");
    }
}
