using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

internal enum LoopFeedbackPinShape
{
    InferredFromBodyTopology,
    ExplicitFeedbackInput
}

internal sealed record LoopFeedbackSequenceOutputRouteProof(
    string SequenceNodeId,
    string SequenceOutputPinId);

internal sealed record LoopFeedbackBreakRouteProof(
    string LoopNodeId,
    string BreakNodeId,
    string SequenceNodeId,
    string SequenceOutputPinId);

internal sealed record LoopFeedbackRegionProof(
    string LoopNodeId,
    string BodyAnchorNodeId,
    string? ParentLoopNodeId,
    string InitialConnectionId,
    string FeedbackConnectionId,
    ImmutableArray<string> BodyNodeIds,
    ImmutableArray<string> BreakNodeIds,
    ImmutableArray<LoopFeedbackBreakRouteProof> BreakRoutes);

internal sealed record LoopFeedbackTopologyIssue(string Code, string Path, string Message);

internal sealed record LoopFeedbackTopologyProof(
    ImmutableArray<LoopFeedbackRegionProof> Regions,
    ImmutableArray<LoopFeedbackTopologyIssue> Issues)
{
    public bool IsValid => !Regions.IsDefaultOrEmpty && Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Graph-derived ownership analysis for bounded Loop feedback topologies.
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

        var pinDescriptors = new Dictionary<string, WorkflowNodeDescriptor>(StringComparer.Ordinal);
        foreach (var (nodeId, node) in definition.Nodes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (node is null || !WorkflowTopologyProofDescriptors.TryResolve(node, out var descriptor))
            {
                Issue("loop.feedback.descriptor", $"nodes.{nodeId}", "Every node in a bounded loop proof requires a resolved pin descriptor.");
                continue;
            }
            pinDescriptors.Add(nodeId, descriptor);
        }
        WorkflowPortDescriptor[] Pins(string nodeId, WorkflowPortDirection direction, WorkflowExecutionPinRole role) =>
            pinDescriptors.TryGetValue(nodeId, out var descriptor)
                ? descriptor.Ports.Where(port => port.Kind == WorkflowPortKind.Execution && port.Direction == direction && port.ExecutionRole == role).ToArray()
                : [];
        bool HasRole(string nodeId, WorkflowPortDirection direction, WorkflowExecutionPinRole role) => Pins(nodeId, direction, role).Length > 0;
        var loops = pinDescriptors.Where(pair => HasRole(pair.Key, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopInitialInput) &&
                HasRole(pair.Key, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopFeedbackInput))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        var anchors = pinDescriptors.Where(pair => HasRole(pair.Key, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopBodyEntryInput))
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
            var bodyPorts = Pins(loopId, WorkflowPortDirection.Output, WorkflowExecutionPinRole.LoopBodyOutput);
            var bodyEdges = bodyPorts.Length == 1
                ? edges.Where(item => item.Edge.Source == new PortReference(loopId, bodyPorts[0].Id)).ToArray()
                : [];
            var anchorId = bodyEdges.Length == 1 ? bodyEdges[0].Edge.Target.NodeId : null;
            if (bodyEdges.Length != 1 || anchorId is null ||
                !Pins(anchorId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopBodyEntryInput).Any(pin => pin.Id == bodyEdges[0].Edge.Target.PortId))
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
        var visitedBreakNodes = new HashSet<string>(StringComparer.Ordinal);
        var visitedForEachBreaks = new HashSet<string>(StringComparer.Ordinal);

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
            var initialPins = Pins(loopId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopInitialInput);
            var feedbackPins = Pins(loopId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopFeedbackInput);
            var initialPort = initialPins.SingleOrDefault()?.Id ?? "";
            var feedbackPort = shape == LoopFeedbackPinShape.InferredFromBodyTopology ? initialPort : feedbackPins.SingleOrDefault()?.Id ?? "";
            var loopIncoming = incoming.GetValueOrDefault(loopId) ?? [];
            var initialEdges = loopIncoming.Where(item => item.Edge.Target.PortId == initialPort).ToArray();
            var feedbackEdges = loopIncoming.Where(item => item.Edge.Target.PortId == feedbackPort).ToArray();

            var donePins = Pins(loopId, WorkflowPortDirection.Output, WorkflowExecutionPinRole.LoopDoneOutput);
            var doneEdges = donePins.Length == 1
                ? (outgoing.GetValueOrDefault(loopId) ?? []).Where(item => item.Edge.Source.PortId == donePins[0].Id).ToArray()
                : [];
            var doneTargetNodeId = doneEdges.Length == 1 ? doneEdges[0].Edge.Target.NodeId : null;
            var doneTargetPort = doneTargetNodeId is null
                ? null
                : loops.Contains(doneTargetNodeId, StringComparer.Ordinal)
                    ? shape == LoopFeedbackPinShape.InferredFromBodyTopology
                        ? Pins(doneTargetNodeId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopInitialInput).SingleOrDefault()?.Id
                        : Pins(doneTargetNodeId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopFeedbackInput).SingleOrDefault()?.Id
                    : Pins(doneTargetNodeId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.TerminalInput).SingleOrDefault()?.Id ??
                      Pins(doneTargetNodeId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.ActivationInput).SingleOrDefault()?.Id;
            if (doneEdges.Length != 1 || doneEdges[0].Edge.Target.PortId != doneTargetPort)
                Issue("loop.feedback.done", $"nodes.{loopId}.done", "Each loop must have one continuation from its done output.");

            var body = ImmutableArray.CreateBuilder<string>();
            var breakNodes = ImmutableArray.CreateBuilder<string>();
            var breakRoutes = ImmutableArray.CreateBuilder<LoopFeedbackBreakRouteProof>();
            var bodyNodeIds = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<(string NodeId, HashSet<string> Path, LoopFeedbackSequenceOutputRouteProof? SequenceRoute)>();
            pending.Enqueue((loopAnchors[loopId], new(StringComparer.Ordinal), null));
            string? returnConnectionId = null;

            void Follow(string sourceNodeId, string sourcePortId, HashSet<string> path,
                LoopFeedbackSequenceOutputRouteProof? sequenceRoute)
            {
                var successors = (outgoing.GetValueOrDefault(sourceNodeId) ?? [])
                    .Where(item => item.Edge.Source.PortId == sourcePortId).ToArray();
                if (successors.Length != 1)
                {
                    Issue("loop.feedback.body-path", $"nodes.{sourceNodeId}",
                        "Each body route must have exactly one successor in this topology comparison.");
                    return;
                }

                var next = successors[0];
                if (next.Edge.Target.NodeId == loopId)
                {
                    if (returnConnectionId is not null)
                        Issue("loop.feedback.cardinality", $"executionConnections.{next.Id}",
                            "A loop body must have exactly one normal return route; other Sequence outputs may end in Break.");
                    returnConnectionId ??= next.Id;
                    if (next.Edge.Target.PortId != feedbackPort)
                        Issue("loop.feedback.pin", $"executionConnections.{next.Id}",
                            "The body return must target the candidate feedback input.");
                    return;
                }
                if (loops.Contains(next.Edge.Target.NodeId, StringComparer.Ordinal) &&
                    (activeLoops.Contains(next.Edge.Target.NodeId) || next.Edge.Target.PortId != initialPort))
                    Issue("loop.feedback.cross-owner", $"executionConnections.{next.Id}",
                        "A loop body may return only to its owning loop; nested loops are entered through their initial input.");
                pending.Enqueue((next.Edge.Target.NodeId, path, sequenceRoute));
            }

            while (pending.TryDequeue(out var route))
            {
                var current = route.NodeId;
                var path = route.Path;
                var sequenceRoute = route.SequenceRoute;
                if (!path.Add(current))
                {
                    Issue("loop.feedback.cycle", $"nodes.{current}", "An unowned cycle occurs inside a bounded loop body.");
                    continue;
                }
                if (bodyNodeIds.Count >= MaximumBodyNodes)
                {
                    Issue("loop.feedback.limit", $"nodes.{loopId}", "The candidate body exceeds the topology proof bound.");
                    continue;
                }
                if (!bodyNodeIds.Add(current))
                {
                    Issue("loop.feedback.multiple-owner", $"nodes.{current}",
                        "Sequence output routes in the candidate must remain disjoint.");
                    continue;
                }
                body.Add(current);
                if (current == loopId)
                {
                    Issue("loop.feedback.cycle", $"nodes.{loopId}", "A loop body cannot re-enter its owner except through its declared feedback input.");
                    continue;
                }

                if (!definition.Nodes.TryGetValue(current, out var node) || node is null)
                {
                    Issue("loop.feedback.node", $"nodes.{current}", "The loop body references a missing node.");
                    continue;
                }

                if (HasRole(current, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopBodyEntryInput) && current != loopAnchors[loopId])
                    Issue("loop.feedback.anchor", $"nodes.{current}", "A body anchor must have one owning loop.");

                if (node.Type == NodeTypes.Break)
                {
                    visitedForEachBreaks.Add(current);
                    if (definition.Nodes[loopId].Type != NodeTypes.ForEach)
                        Issue("foreach.break.outside", $"nodes.{current}", "A Break is legal only inside a For each body.");
                    else if (path.Any(id => definition.Nodes.TryGetValue(id, out var onPath) &&
                                 onPath?.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or
                                     NodeTypes.ParallelRendezvous or NodeTypes.Sequence or NodeTypes.Loop or NodeTypes.ForEach))
                        Issue("foreach.break.nonlinear", $"nodes.{current}",
                            "A Break requires a strictly linear For each body path with no fork, join, sequence or nested loop before it.");
                    if ((outgoing.GetValueOrDefault(current) ?? []).Length > 0)
                        Issue("foreach.break.terminal", $"nodes.{current}", "A Break has no outputs.");
                    continue;
                }

                if (HasRole(current, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopBreakInput))
                {
                    visitedBreakNodes.Add(current);
                    if ((outgoing.GetValueOrDefault(current) ?? []).Length > 0)
                        Issue("loop.feedback.break-terminal", $"nodes.{current}", "A proof Break must be a terminal route.");
                    breakNodes.Add(current);
                    if (sequenceRoute is null)
                        Issue("loop.feedback.break-owner", $"nodes.{current}", "A proof Break must be reached from one declared Sequence output route.");
                    else
                        breakRoutes.Add(new(loopId, current, sequenceRoute.SequenceNodeId, sequenceRoute.SequenceOutputPinId));
                    continue;
                }

                if (HasRole(current, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopInitialInput) &&
                    HasRole(current, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopFeedbackInput))
                {
                    if (!loopAnchors.ContainsKey(current))
                    {
                        Issue("loop.feedback.nested", $"nodes.{current}", "The nested loop has no uniquely identified body anchor.");
                        continue;
                    }

                    if (!AnalyzeRegion(current, loopId)) continue;
                    Follow(current, donePins.Single().Id, path, sequenceRoute);
                    continue;
                }

                if (node.Type == NodeTypes.ParallelFork)
                {
                    var matchingJoins = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.ParallelJoin &&
                        pair.Value.ParallelJoin?.ForkNodeId == current).Select(pair => pair.Key).ToArray();
                    if (matchingJoins.Length != 1)
                    {
                        Issue("loop.feedback.parallel-join", $"nodes.{current}",
                            "A loop-body fork requires exactly one matching Join.");
                        continue;
                    }

                    var joinId = matchingJoins[0];
                    var parallelNodes = CollectParallelNodes(current, joinId, edges);
                    if (bodyNodeIds.Count + parallelNodes.Count > MaximumBodyNodes)
                    {
                        Issue("loop.feedback.limit", $"nodes.{loopId}",
                            "The candidate body exceeds the topology proof bound.");
                        continue;
                    }
                    foreach (var parallelNodeId in parallelNodes)
                    {
                        if (!bodyNodeIds.Add(parallelNodeId) && parallelNodeId != current)
                            Issue("loop.feedback.multiple-owner", $"nodes.{parallelNodeId}",
                                "Parallel region nodes must remain inside their unique loop owner.");
                        body.Add(parallelNodeId);
                    }

                    Follow(joinId, "then", path, sequenceRoute);
                    continue;
                }

                var fanOutPorts = Pins(current, WorkflowPortDirection.Output, WorkflowExecutionPinRole.FanOutOutput)
                    .Concat(Pins(current, WorkflowPortDirection.Output, WorkflowExecutionPinRole.AlternativeOutput))
                    .OrderBy(port => port.Id, StringComparer.Ordinal).ToArray();
                if (fanOutPorts.Length > 0)
                {
                    foreach (var output in fanOutPorts)
                    {
                        var outputRoute = pinDescriptors[current].ConfigurationKind == "sequence"
                            ? new LoopFeedbackSequenceOutputRouteProof(current, output.Id)
                            : sequenceRoute;
                        Follow(current, output.Id, new(path, StringComparer.Ordinal), outputRoute);
                    }
                    continue;
                }

                var continuationPorts = Pins(current, WorkflowPortDirection.Output, WorkflowExecutionPinRole.ContinuationOutput);
                if (continuationPorts.Length != 1)
                {
                    Issue("loop.feedback.continuation", $"nodes.{current}",
                        "Each ordinary body node must declare exactly one continuation output.");
                    continue;
                }
                Follow(current, continuationPorts[0].Id, path, sequenceRoute);
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
                    initialConnectionId, returnConnectionId, body.ToImmutable(), breakNodes.ToImmutable(),
                    breakRoutes.OrderBy(route => route.BreakNodeId, StringComparer.Ordinal).ToImmutableArray()));

            activeLoops.Remove(loopId);
            return returnConnectionId is not null;
        }

        var rootOutputs = pinDescriptors.Where(pair => !pair.Value.Ports.Any(port => port.Kind == WorkflowPortKind.Execution && port.Direction == WorkflowPortDirection.Input))
            .SelectMany(pair => pair.Value.Ports.Where(port => port.Kind == WorkflowPortKind.Execution && port.Direction == WorkflowPortDirection.Output &&
                    port.ExecutionRole == WorkflowExecutionPinRole.ContinuationOutput)
                .Select(port => (NodeId: pair.Key, PortId: port.Id))).ToArray();
        var initialLoopEdges = edges.Where(item =>
                Pins(item.Edge.Target.NodeId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopInitialInput)
                    .Any(pin => pin.Id == item.Edge.Target.PortId) && loops.Contains(item.Edge.Target.NodeId, StringComparer.Ordinal))
            .ToArray();
        var rootEdges = initialLoopEdges.Where(item => rootOutputs.Contains((item.Edge.Source.NodeId, item.Edge.Source.PortId)) ||
            IsOnceOnlyAgentPrefixEdge(item)).ToArray();
        foreach (var edge in initialLoopEdges.Where(item => pinDescriptors.GetValueOrDefault(item.Edge.Source.NodeId)?.NodeType == NodeTypes.Agent &&
                     !rootEdges.Contains(item) && !IsBodyReturnEdge(item)))
            Issue("loop.feedback.root.prefix", $"executionConnections.{edge.Id}",
                "A root Loop may follow an Agent only through the exact Input → Agent → Loop prefix, with caller input connected and no Agent data output consumers.");

        var rootLoops = rootEdges.Select(item => item.Edge.Target.NodeId).Distinct(StringComparer.Ordinal).ToArray();
        if (rootLoops.Length != 1)
            Issue("loop.feedback.root", "executionConnections", "The graph must enter exactly one root loop for this comparison.");
        else
            AnalyzeRegion(rootLoops[0], null);

        // In the inferred pin shape one `execute` pin is both the initial entry and the body return, so an Agent
        // inside the loop's own body that returns to it is a body return, not a root entry after an Agent.
        bool IsBodyReturnEdge((string Id, ConnectionDefinition Edge) candidate)
        {
            if (!loopAnchors.TryGetValue(candidate.Edge.Target.NodeId, out var anchorId)) return false;
            var loopId = candidate.Edge.Target.NodeId;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>([anchorId]);
            while (queue.TryDequeue(out var nodeId))
            {
                if (nodeId == loopId || !seen.Add(nodeId)) continue;
                if (nodeId == candidate.Edge.Source.NodeId) return true;
                foreach (var next in outgoing.GetValueOrDefault(nodeId) ?? [])
                    queue.Enqueue(next.Edge.Target.NodeId);
            }
            return false;
        }

        bool IsOnceOnlyAgentPrefixEdge((string Id, ConnectionDefinition Edge) candidate)
        {
            var agentId = candidate.Edge.Source.NodeId;
            if (candidate.Edge.Source.PortId != "out" || candidate.Edge.Target.PortId != "execute" ||
                definition.EntryNodeId is not { } entryId ||
                !definition.Nodes.TryGetValue(entryId, out var entryNode) || entryNode?.Type != NodeTypes.Input ||
                !definition.Nodes.TryGetValue(agentId, out var agentNode) || agentNode?.Type != NodeTypes.Agent ||
                definition.Nodes.Values.Count(node => node?.Type == NodeTypes.Input) != 1)
                return false;

            var agentExecution = edges.Where(item => item.Edge.Source.NodeId == agentId || item.Edge.Target.NodeId == agentId).ToArray();
            var entryExecution = edges.Where(item => item.Edge.Source.NodeId == entryId).ToArray();
            var agentDataInputs = definition.Connections.Values.Where(edge => edge.Target.NodeId == agentId).ToArray();
            return agentExecution.Length == 2 &&
                agentExecution.Any(item => item.Edge.Source == new PortReference(entryId, "then") &&
                    item.Edge.Target == new PortReference(agentId, "execute")) &&
                agentExecution.Any(item => item.Id == candidate.Id) &&
                entryExecution.Length == 1 && entryExecution[0].Edge.Source == new PortReference(entryId, "then") &&
                agentDataInputs.Length == 1 &&
                agentDataInputs[0].Source == new PortReference(entryId, "out") &&
                agentDataInputs[0].Target == new PortReference(agentId, "in") &&
                !definition.Connections.Values.Any(edge => edge.Source.NodeId == agentId);
        }

        foreach (var loopId in loops.Where(id => !analyzedLoops.Contains(id)))
            Issue("loop.feedback.unreachable", $"nodes.{loopId}", "Every loop must be reachable from the root loop through one owning body.");

        foreach (var breakNodeId in definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Break)
                     .Select(pair => pair.Key).Where(nodeId => !visitedForEachBreaks.Contains(nodeId))
                     .Order(StringComparer.Ordinal))
            Issue("foreach.break.outside", $"nodes.{breakNodeId}", "A Break is legal only inside a For each body.");

        foreach (var breakNodeId in pinDescriptors.Keys
                     .Where(nodeId => HasRole(nodeId, WorkflowPortDirection.Input, WorkflowExecutionPinRole.LoopBreakInput))
                     .Where(nodeId => !visitedBreakNodes.Contains(nodeId))
                     .Order(StringComparer.Ordinal))
            Issue("loop.feedback.unreachable", $"nodes.{breakNodeId}",
                "Every proof Break must be reachable from a root loop body through a Sequence output route.");

        return new(regions.OrderBy(region => region.LoopNodeId, StringComparer.Ordinal).ToImmutableArray(),
            issues.ToImmutable());
    }

    private static HashSet<string> CollectParallelNodes(string forkId, string joinId,
        (string Id, ConnectionDefinition Edge)[] edges)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(forkId);
        while (pending.TryPop(out var current))
        {
            if (!nodes.Add(current) || current == joinId)
                continue;
            foreach (var edge in edges.Where(item => item.Edge.Source.NodeId == current))
                pending.Push(edge.Edge.Target.NodeId);
        }
        return nodes;
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
