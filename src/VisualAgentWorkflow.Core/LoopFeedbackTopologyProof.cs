using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

internal enum LoopFeedbackPinShape
{
    InferredFromBodyTopology,
    ExplicitFeedbackInput
}

internal sealed record LoopFeedbackRegionProof(
    string LoopNodeId,
    string BodyAnchorNodeId,
    string? ParentLoopNodeId,
    string InitialConnectionId,
    string FeedbackConnectionId,
    ImmutableArray<string> BodyNodeIds);

internal sealed record LoopFeedbackTopologyIssue(string Code, string Path, string Message);

internal sealed record LoopFeedbackTopologyProof(
    ImmutableArray<LoopFeedbackRegionProof> Regions,
    ImmutableArray<LoopFeedbackTopologyIssue> Issues)
{
    public bool IsValid => !Regions.IsDefaultOrEmpty && Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Non-authoritative topology experiment for comparing nested loop feedback pin contracts.
/// It does not participate in graph validation, serialization admission, or execution.
/// </summary>
internal static class LoopFeedbackTopologyProofAnalyzer
{
    private const int MaximumBodyNodes = 256;

    public static LoopFeedbackTopologyProof Analyze(WorkflowDefinition definition, LoopFeedbackPinShape shape)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var issues = ImmutableArray.CreateBuilder<LoopFeedbackTopologyIssue>();
        var regions = ImmutableArray.CreateBuilder<LoopFeedbackRegionProof>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        if (definition.ExecutionConnections is not { } execution)
        {
            Issue("loop.feedback.execution", "executionConnections", "The candidate requires explicit execution connections.");
            return new([], issues.ToImmutable());
        }

        var loops = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Loop)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        var anchors = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.LoopBody)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        if (loops.Length == 0 || anchors.Length != loops.Length)
        {
            Issue("loop.feedback.count", "nodes", "Each bounded loop requires exactly one body anchor.");
            return new([], issues.ToImmutable());
        }

        var edges = execution.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(pair => pair.Value is not null)
            .Select(pair => (Id: pair.Key, Edge: pair.Value!)).ToArray();
        var outgoing = edges.GroupBy(item => item.Edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var incoming = edges.GroupBy(item => item.Edge.Target.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var loopAnchors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var loopId in loops)
        {
            var bodyEdges = edges.Where(item => item.Edge.Source == new PortReference(loopId, "body")).ToArray();
            var anchorId = bodyEdges.Length == 1 ? bodyEdges[0].Edge.Target.NodeId : null;
            if (bodyEdges.Length != 1 || bodyEdges[0].Edge.Target.PortId != "execute" ||
                anchorId is null || definition.Nodes.GetValueOrDefault(anchorId)?.Type != NodeTypes.LoopBody)
            {
                Issue("loop.feedback.body", $"nodes.{loopId}", "Each loop body output must activate exactly one loop-body anchor.");
                continue;
            }
            loopAnchors[loopId] = bodyEdges[0].Edge.Target.NodeId;
        }

        if (loopAnchors.Count != loops.Length || loopAnchors.Values.Distinct(StringComparer.Ordinal).Count() != anchors.Length)
        {
            Issue("loop.feedback.anchor-owner", "nodes", "Every loop body anchor must belong to exactly one loop.");
            return new([], issues.ToImmutable());
        }

        var activeLoops = new HashSet<string>(StringComparer.Ordinal);
        var analyzedLoops = new HashSet<string>(StringComparer.Ordinal);

        bool AnalyzeRegion(string loopId, string? parentLoopId)
        {
            if (!activeLoops.Add(loopId))
            {
                Issue("loop.feedback.nested-cycle", $"nodes.{loopId}", "Nested loop ownership cannot contain a cycle.");
                return false;
            }
            if (!analyzedLoops.Add(loopId))
            {
                Issue("loop.feedback.multiple-owner", $"nodes.{loopId}", "A loop can belong to only one enclosing body.");
                activeLoops.Remove(loopId);
                return false;
            }
            var initialPort = "execute";
            var feedbackPort = shape == LoopFeedbackPinShape.InferredFromBodyTopology ? "execute" : "feedback";
            var loopIncoming = incoming.GetValueOrDefault(loopId) ?? [];
            var initialEdges = loopIncoming.Where(item => item.Edge.Target.PortId == initialPort).ToArray();
            var feedbackEdges = loopIncoming.Where(item => item.Edge.Target.PortId == feedbackPort).ToArray();

            var doneEdges = (outgoing.GetValueOrDefault(loopId) ?? [])
                .Where(item => item.Edge.Source.PortId == "done").ToArray();
            var doneTargetPort = doneEdges.Length == 1 && loops.Contains(doneEdges[0].Edge.Target.NodeId, StringComparer.Ordinal)
                ? feedbackPort
                : "execute";
            if (doneEdges.Length != 1 || doneEdges[0].Edge.Target.PortId != doneTargetPort)
                Issue("loop.feedback.done", $"nodes.{loopId}.done", "Each loop must have one continuation from its done output.");

            var body = ImmutableArray.CreateBuilder<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = loopAnchors[loopId];
            string? returnConnectionId = null;
            while (true)
            {
                if (!visited.Add(current))
                {
                    Issue("loop.feedback.cycle", $"nodes.{current}", "An unowned cycle occurs inside a bounded loop body.");
                    break;
                }
                if (visited.Count > MaximumBodyNodes)
                {
                    Issue("loop.feedback.limit", $"nodes.{loopId}", "The candidate body exceeds the topology proof bound.");
                    break;
                }
                body.Add(current);
                if (current == loopId)
                {
                    Issue("loop.feedback.cycle", $"nodes.{loopId}", "A loop body cannot re-enter its owner except through its declared feedback input.");
                    break;
                }

                if (!definition.Nodes.TryGetValue(current, out var node) || node is null)
                {
                    Issue("loop.feedback.node", $"nodes.{current}", "The loop body references a missing node.");
                    break;
                }

                if (node.Type == NodeTypes.LoopBody && current != loopAnchors[loopId])
                    Issue("loop.feedback.anchor", $"nodes.{current}", "A body anchor must have one owning loop.");

                if (node.Type == NodeTypes.Loop)
                {
                    if (!loopAnchors.ContainsKey(current))
                    {
                        Issue("loop.feedback.nested", $"nodes.{current}", "The nested loop has no uniquely identified body anchor.");
                        break;
                    }

                    if (!AnalyzeRegion(current, loopId)) break;
                    var continuation = (outgoing.GetValueOrDefault(current) ?? [])
                        .Where(item => item.Edge.Source.PortId == "done").ToArray();
                    if (continuation.Length != 1) break;
                    if (continuation[0].Edge.Target.NodeId == loopId)
                    {
                        returnConnectionId = continuation[0].Id;
                        if (continuation[0].Edge.Target.PortId != feedbackPort)
                            Issue("loop.feedback.pin", $"executionConnections.{continuation[0].Id}",
                                "The nested loop continuation must target the owning loop's candidate feedback input.");
                        break;
                    }
                    if (loops.Contains(continuation[0].Edge.Target.NodeId, StringComparer.Ordinal))
                    {
                        Issue("loop.feedback.cross-owner", $"executionConnections.{continuation[0].Id}",
                            "A nested loop continuation may return only to its owning loop.");
                        break;
                    }
                    current = continuation[0].Edge.Target.NodeId;
                    continue;
                }

                var successors = (outgoing.GetValueOrDefault(current) ?? [])
                    .Where(item => item.Edge.Source.PortId == "then").ToArray();
                if (successors.Length != 1)
                {
                    Issue("loop.feedback.body-path", $"nodes.{current}", "Each body step must have exactly one successor in this topology comparison.");
                    break;
                }

                var next = successors[0];
                if (next.Edge.Target.NodeId == loopId)
                {
                    returnConnectionId = next.Id;
                    if (next.Edge.Target.PortId != feedbackPort)
                        Issue("loop.feedback.pin", $"executionConnections.{next.Id}", "The body return must target the candidate feedback input.");
                    break;
                }
                if (loops.Contains(next.Edge.Target.NodeId, StringComparer.Ordinal) &&
                    (activeLoops.Contains(next.Edge.Target.NodeId) || next.Edge.Target.PortId != initialPort))
                    Issue("loop.feedback.cross-owner", $"executionConnections.{next.Id}",
                        "A loop body may return only to its owning loop; nested loops are entered through their initial input.");

                current = next.Edge.Target.NodeId;
            }

            if (returnConnectionId is null && !issues.Any(issue => issue.Path.StartsWith($"nodes.{loopId}", StringComparison.Ordinal)))
                Issue("loop.feedback.missing", $"nodes.{loopId}", "The loop body has no return to its owning loop.");

            if (shape == LoopFeedbackPinShape.InferredFromBodyTopology)
            {
                if (initialEdges.Length != 2 || feedbackEdges.Length != 2 || returnConnectionId is null ||
                    !feedbackEdges.Any(item => item.Id == returnConnectionId))
                    Issue("loop.feedback.cardinality", $"nodes.{loopId}.execute", "The inferred shape requires one initial edge and one body return on execute.");
            }
            else if (initialEdges.Length != 1 || feedbackEdges.Length != 1 ||
                     feedbackEdges[0].Id != returnConnectionId)
                Issue("loop.feedback.cardinality", $"nodes.{loopId}", "The explicit shape requires one initial edge and one separately identified body return.");

            var initialCandidates = shape == LoopFeedbackPinShape.InferredFromBodyTopology
                ? loopIncoming.Where(item => item.Id != returnConnectionId).ToArray()
                : initialEdges;
            var initialConnectionId = initialCandidates.Length == 1 ? initialCandidates[0].Id : "";
            if (returnConnectionId is not null)
                regions.Add(new(loopId, loopAnchors[loopId], parentLoopId,
                    initialConnectionId, returnConnectionId, body.ToImmutable()));

            activeLoops.Remove(loopId);
            return returnConnectionId is not null;
        }

        var inputs = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Input).ToArray();
        var input = inputs.Length == 1 ? inputs[0].Key : "";
        var rootLoops = edges.Where(item => item.Edge.Source.NodeId == input &&
                item.Edge.Target.PortId == "execute" && loops.Contains(item.Edge.Target.NodeId, StringComparer.Ordinal))
            .Select(item => item.Edge.Target.NodeId).Distinct(StringComparer.Ordinal).ToArray();
        if (rootLoops.Length != 1)
            Issue("loop.feedback.root", "executionConnections", "The graph must enter exactly one root loop for this comparison.");
        else
            AnalyzeRegion(rootLoops[0], null);

        foreach (var loopId in loops.Where(id => !analyzedLoops.Contains(id)))
            Issue("loop.feedback.unreachable", $"nodes.{loopId}", "Every loop must be reachable from the root loop through one owning body.");

        return new(regions.OrderBy(region => region.LoopNodeId, StringComparer.Ordinal).ToImmutableArray(),
            issues.ToImmutable());
    }

    public static WorkflowDefinition ToExplicitFeedbackCandidate(WorkflowDefinition definition)
    {
        var proof = Analyze(definition, LoopFeedbackPinShape.InferredFromBodyTopology);
        if (!proof.IsValid)
            throw new ArgumentException("Only an unambiguous inferred-feedback candidate can be migrated.", nameof(definition));

        var connections = definition.ExecutionConnections!;
        foreach (var region in proof.Regions)
        {
            var edge = connections[region.FeedbackConnectionId];
            connections = connections.SetItem(region.FeedbackConnectionId,
                edge with { Target = edge.Target with { PortId = "feedback" } });
        }

        return definition with { ExecutionConnections = connections };
    }
}
