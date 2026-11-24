using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Describes one bounded decision embedded in a Sequence output path.</summary>
public sealed record SequenceBranchPathTopology(
    string OutputId,
    string DecisionNodeId,
    string MergeNodeId,
    ImmutableArray<string> PrefixNodeIds,
    ImmutableArray<string> TrueNodeIds,
    ImmutableArray<string> FalseNodeIds,
    ImmutableArray<string> SuffixNodeIds);

/// <summary>A stable diagnostic from the proof-only branchable Sequence topology analyzer.</summary>
public sealed record SequenceBranchTopologyIssue(string Code, string Path, string Message);

/// <summary>Result of analyzing bounded, single-decision Sequence output paths.</summary>
public sealed record SequenceBranchTopologyAnalysis(
    ImmutableArray<SequenceBranchPathTopology> Paths,
    ImmutableArray<SequenceBranchTopologyIssue> Issues)
{
    public bool IsValid => !Paths.IsDefaultOrEmpty && Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Proves a bounded topology shape for Sequence outputs containing at most one Condition or
/// Boolean Branch and one corresponding exclusive Merge. This analyzer does not admit definitions to runtime.
/// </summary>
public static class SequenceBranchTopologyAnalyzer
{
    public const int MaximumPathNodes = 16;

    public static SequenceBranchTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var paths = ImmutableArray.CreateBuilder<SequenceBranchPathTopology>();
        var issues = ImmutableArray.CreateBuilder<SequenceBranchTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        var sequences = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Sequence).ToArray();
        var outputs = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Output).ToArray();
        if (sequences.Length != 1 || outputs.Length != 1 || definition.ExecutionConnections is not { } execution)
        {
            Issue("sequence.branch.graph", "nodes", "A branchable Sequence graph requires one Sequence, one Output, and explicit execution connections.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var sequence = sequences[0];
        var output = outputs[0];
        var descriptors = sequence.Value.Sequence?.Outputs ?? [];
        if (descriptors.IsDefaultOrEmpty ||
            descriptors.Any(descriptor => descriptor is null || !SequenceOutputLimits.IsValidId(descriptor.Id) ||
                string.IsNullOrWhiteSpace(descriptor.Label) || descriptor.Label.Length > SequenceOutputLimits.MaximumLabelLength) ||
            descriptors.Select(descriptor => descriptor.Id).Distinct(StringComparer.Ordinal).Count() != descriptors.Length)
        {
            Issue("sequence.branch.configuration", $"nodes.{sequence.Key}.sequence", "The Sequence must declare bounded, uniquely identified ordered outputs with labels.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var edges = execution.Values.Where(edge => edge is not null).ToArray();
        var outgoing = edges.GroupBy(edge => edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var incoming = edges.GroupBy(edge => edge.Target.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var reachedDecisions = new HashSet<string>(StringComparer.Ordinal);
        var entries = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Input).ToArray();
        if (entries.Length != 1 || definition.EntryNodeId != entries[0].Key ||
            incoming.GetValueOrDefault(sequence.Key)?.Length != 1 ||
            incoming[sequence.Key][0].Target.PortId != "execute" ||
            outgoing.GetValueOrDefault(entries[0].Key)?.Length != 1 ||
            outgoing[entries[0].Key][0].Target.NodeId != sequence.Key)
            Issue("sequence.branch.entry", "executionConnections", "Input must activate the Sequence exactly once.");
        if ((outgoing.GetValueOrDefault(sequence.Key) ?? []).Length != descriptors.Length)
            Issue("sequence.branch.roots", $"nodes.{sequence.Key}", "Every Sequence output must start exactly one execution path.");

        foreach (var descriptor in descriptors)
        {
            var roots = edges.Where(edge => edge.Source == new PortReference(sequence.Key, SequenceOutputLimits.PinId(descriptor.Id))).ToArray();
            if (roots.Length != 1 || roots[0].Target.PortId != "execute")
            {
                Issue("sequence.branch.root", $"nodes.{sequence.Key}.sequence.outputs.{descriptor.Id}", "Each Sequence output must connect exactly once to an execution input.");
                continue;
            }

            var pathNodes = new HashSet<string>(StringComparer.Ordinal);
            var prefix = WalkLinear(roots[0].Target.NodeId, output.Key, "sequence.branch.prefix", pathNodes, owners,
                descriptor.Id, definition.Nodes, outgoing, Issue, stopAtBranch: true);
            if (prefix is null) continue;
            if (prefix.Value.TerminalNodeId == output.Key)
                continue;

            var branchId = prefix.Value.TerminalNodeId;
            if (!TryOwn(branchId, descriptor.Id, owners, pathNodes, Issue)) continue;
            reachedDecisions.Add(branchId);
            if (!definition.Nodes.TryGetValue(branchId, out var branchNode) || branchNode is null || !IsDecision(branchNode.Type))
            {
                Issue("sequence.branch.node", $"nodes.{branchId}", "A branchable Sequence decision must use a Condition or Boolean Branch node.");
                continue;
            }
            var branchEdges = outgoing.GetValueOrDefault(branchId) ?? [];
            var trueEdges = branchEdges.Where(edge => edge.Source.PortId == "true").ToArray();
            var falseEdges = branchEdges.Where(edge => edge.Source.PortId == "false").ToArray();
            var decisionPorts = WorkflowPorts.ExplicitExecutionOutputs(branchNode);
            if (branchEdges.Length != 2 || trueEdges.Length != 1 || falseEdges.Length != 1 ||
                !decisionPorts.Contains("true") || !decisionPorts.Contains("false") ||
                trueEdges[0].Target.PortId != "execute" || falseEdges[0].Target.PortId != "execute")
            {
                Issue("sequence.branch.outputs", $"nodes.{branchId}", "A Sequence decision must have exactly one true and one false execution successor.");
                continue;
            }

            var trueArm = WalkArm(trueEdges[0], descriptor.Id, "true", outgoing, owners, pathNodes, definition.Nodes, Issue);
            var falseArm = WalkArm(falseEdges[0], descriptor.Id, "false", outgoing, owners, pathNodes, definition.Nodes, Issue);
            if (trueArm is null || falseArm is null) continue;
            if (branchNode.Type == NodeTypes.BooleanBranch &&
                (trueArm.Value.Nodes.IsDefaultOrEmpty || falseArm.Value.Nodes.IsDefaultOrEmpty))
                Issue("sequence.branch.merge-data", $"nodes.{branchId}", "Each Boolean Branch arm must pass through a data-producing execution node before the exclusive Merge; use Condition for direct string pass-through.");
            if (!StringComparer.Ordinal.Equals(trueArm.Value.MergeNodeId, falseArm.Value.MergeNodeId))
            {
                Issue("sequence.branch.ambiguous-terminal", $"nodes.{branchId}", "The true and false routes must converge at the same exclusive Merge.");
                continue;
            }

            var mergeId = trueArm.Value.MergeNodeId;
            if (!TryOwn(mergeId, descriptor.Id, owners, pathNodes, Issue)) continue;
            if (!definition.Nodes.TryGetValue(mergeId, out var mergeNode) || mergeNode?.Type != NodeTypes.Merge)
            {
                Issue("sequence.branch.merge", $"nodes.{mergeId}", "Both decision routes must converge at an exclusive Merge node.");
                continue;
            }
            var mergeInputs = incoming.GetValueOrDefault(mergeId) ?? [];
            if (mergeInputs.Length != 2 || mergeInputs.Any(edge => edge.Target.PortId != "execute") ||
                !mergeInputs.Contains(trueArm.Value.Arrival) || !mergeInputs.Contains(falseArm.Value.Arrival))
                Issue("sequence.branch.merge-cardinality", $"nodes.{mergeId}", "The exclusive Merge must receive exactly one execution arrival from each selected alternative.");

            var mergeOutputs = outgoing.GetValueOrDefault(mergeId) ?? [];
            if (mergeOutputs.Length != 1 || mergeOutputs[0].Source.PortId != "then" || mergeOutputs[0].Target.PortId != "execute")
            {
                Issue("sequence.branch.merge-successor", $"nodes.{mergeId}", "The exclusive Merge must continue through one then execution connection.");
                continue;
            }

            var suffix = WalkLinear(mergeOutputs[0].Target.NodeId, output.Key, "sequence.branch.suffix", pathNodes, owners,
                descriptor.Id, definition.Nodes, outgoing, Issue, stopAtBranch: false);
            if (suffix is null) continue;
            if (suffix.Value.TerminalNodeId != output.Key)
                Issue("sequence.branch.ambiguous-terminal", $"nodes.{mergeId}", "The merged route must reach the workflow Output exactly once.");
            if (prefix.Value.Nodes.Length + trueArm.Value.Nodes.Length + falseArm.Value.Nodes.Length +
                suffix.Value.Nodes.Length + 2 > MaximumPathNodes)
                Issue("sequence.branch.limit", $"nodes.{branchId}", "The complete Sequence decision region exceeds the bounded node limit.");

            paths.Add(new(descriptor.Id, branchId, mergeId, prefix.Value.Nodes,
                trueArm.Value.Nodes, falseArm.Value.Nodes, suffix.Value.Nodes));
        }

        foreach (var decision in definition.Nodes.Where(entry => IsDecision(entry.Value?.Type)))
            if (!reachedDecisions.Contains(decision.Key))
                Issue("sequence.branch.unowned", $"nodes.{decision.Key}", "Every Condition or Boolean Branch must belong to exactly one Sequence output decision region.");

        if (edges.Count(edge => edge.Target.NodeId == output.Key) != descriptors.Length)
            Issue("sequence.branch.output-settlement", $"nodes.{output.Key}", "Output must receive exactly one terminal execution arrival per Sequence output.");

        return new(paths.ToImmutable(), issues.ToImmutable());
    }

    private static (string TerminalNodeId, ImmutableArray<string> Nodes)? WalkLinear(
        string startNodeId,
        string outputNodeId,
        string diagnosticPrefix,
        HashSet<string> pathNodes,
        Dictionary<string, string> owners,
        string outputId,
        ImmutableSortedDictionary<string, NodeDefinition> definitionNodes,
        Dictionary<string, ConnectionDefinition[]> outgoing,
        Action<string, string, string> issue,
        bool stopAtBranch)
    {
        var nodes = ImmutableArray.CreateBuilder<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = startNodeId;
        while (current != outputNodeId && visited.Add(current))
        {
            if (!definitionNodes.TryGetValue(current, out var currentNode) || currentNode is null)
            {
                issue($"{diagnosticPrefix}.reference", $"nodes.{current}", "The Sequence path reaches a missing node.");
                return null;
            }
            if (IsDecision(currentNode.Type))
            {
                if (!stopAtBranch)
                    issue("sequence.branch.nested", $"nodes.{current}", "A Sequence decision region cannot contain a nested Condition or Boolean Branch.");
                return stopAtBranch ? (current, nodes.ToImmutable()) : null;
            }
            if (currentNode.Type is NodeTypes.Merge or NodeTypes.ParallelJoin or NodeTypes.ParallelFork or
                NodeTypes.ParallelBranch or NodeTypes.ParallelRendezvous or NodeTypes.Loop or NodeTypes.ForEach or NodeTypes.Terminate)
            {
                issue($"{diagnosticPrefix}.control", $"nodes.{current}", "Only the decision's exclusive Merge may join Sequence routes.");
                return null;
            }
            if (nodes.Count >= MaximumPathNodes)
            {
                issue("sequence.branch.limit", $"nodes.{current}", "A Sequence branch path exceeds the bounded node limit.");
                return null;
            }
            if (!TryOwn(current, outputId, owners, pathNodes, issue)) return null;
            if (!outgoing.TryGetValue(current, out var next))
            {
                issue($"{diagnosticPrefix}.terminal", $"nodes.{current}", "The Sequence path ends before reaching its required boundary.");
                return null;
            }
            if (next.Length != 1)
            {
                issue($"{diagnosticPrefix}.linear", $"nodes.{current}", "A Sequence prefix or suffix must have exactly one execution successor.");
                return null;
            }
            if (next[0].Target.PortId != "execute" ||
                !WorkflowPorts.ExplicitExecutionOutputs(currentNode).Contains(next[0].Source.PortId))
            {
                issue($"{diagnosticPrefix}.port", $"nodes.{current}", "A linear Sequence step must continue through its declared execution output.");
                return null;
            }
            nodes.Add(current);
            current = next[0].Target.NodeId;
        }
        if (current != outputNodeId && visited.Contains(current))
            issue("sequence.branch.cycle", $"nodes.{current}", "Sequence branch paths cannot contain cycles.");
        return (current, nodes.ToImmutable());
    }

    private static (string MergeNodeId, ImmutableArray<string> Nodes, ConnectionDefinition Arrival)? WalkArm(
        ConnectionDefinition firstEdge,
        string outputId,
        string armName,
        Dictionary<string, ConnectionDefinition[]> outgoing,
        Dictionary<string, string> owners,
        HashSet<string> pathNodes,
        ImmutableSortedDictionary<string, NodeDefinition> definitionNodes,
        Action<string, string, string> issue)
    {
        var nodes = ImmutableArray.CreateBuilder<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var edge = firstEdge;
        while (visited.Add(edge.Target.NodeId))
        {
            var current = edge.Target.NodeId;
            if (!definitionNodes.TryGetValue(current, out var currentNode) || currentNode is null)
            {
                issue("sequence.branch.reference", $"nodes.{current}", "A decision arm reaches a missing node.");
                return null;
            }
            if (currentNode.Type == NodeTypes.Merge)
                return (current, nodes.ToImmutable(), edge);
            if (!TryOwn(current, outputId, owners, pathNodes, issue))
            {
                issue("sequence.branch.shared", $"nodes.{current}", $"The {armName} route shares a node with another Sequence route or decision arm.");
                return null;
            }
            if (IsDecision(currentNode.Type) || currentNode.Type is NodeTypes.ParallelJoin or NodeTypes.ParallelFork or
                NodeTypes.ParallelBranch or NodeTypes.ParallelRendezvous or NodeTypes.Loop or NodeTypes.ForEach or NodeTypes.Terminate)
            {
                issue("sequence.branch.nested", $"nodes.{current}", $"The {armName} route contains unsupported nested control flow.");
                return null;
            }
            if (!outgoing.TryGetValue(current, out var next))
            {
                issue("sequence.branch.ambiguous-terminal", $"nodes.{current}", $"The {armName} route must reach the decision's exclusive Merge.");
                return null;
            }
            if (next.Length != 1)
            {
                issue("sequence.branch.ambiguous-terminal", $"nodes.{current}", $"The {armName} route has an ambiguous terminal or fan-out before its Merge.");
                return null;
            }
            if (next[0].Target.PortId != "execute" ||
                !WorkflowPorts.ExplicitExecutionOutputs(currentNode).Contains(next[0].Source.PortId))
            {
                issue("sequence.branch.port", $"nodes.{current}", $"The {armName} route uses an undeclared execution connection.");
                return null;
            }
            nodes.Add(current);
            edge = next[0];
        }
        issue("sequence.branch.cycle", $"nodes.{edge.Target.NodeId}", $"The {armName} route contains a cycle.");
        return null;
    }

    private static bool TryOwn(string nodeId, string outputId, Dictionary<string, string> owners,
        HashSet<string> pathNodes, Action<string, string, string> issue)
    {
        if (!pathNodes.Add(nodeId))
        {
            issue("sequence.branch.shared", $"nodes.{nodeId}", "A decision region cannot share nodes between its prefix, arms, merge, or suffix.");
            return false;
        }
        if (owners.TryGetValue(nodeId, out var owner) && owner != outputId)
        {
            issue("sequence.branch.shared", $"nodes.{nodeId}", $"Sequence output '{outputId}' shares a node with output '{owner}'.");
            return false;
        }
        owners[nodeId] = outputId;
        return true;
    }

    private static bool IsDecision(string? nodeType) => nodeType is NodeTypes.Condition or NodeTypes.BooleanBranch;
}
