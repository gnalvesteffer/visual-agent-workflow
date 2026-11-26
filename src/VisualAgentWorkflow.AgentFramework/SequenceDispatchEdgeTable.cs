using System.Collections.Immutable;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>
/// The branch-sourced edges of one production Sequence workflow. The workflow builder registers exactly
/// these edges, and the admission resolver predicts a returned message's receivers from the same list, so
/// the two cannot drift. Edges between the same pair of executors are merged into one edge whose predicate
/// is the union of the originals, so a message is never wired to a receiver twice.
/// </summary>
internal sealed class SequenceDispatchEdgeTable
{
    public const string CollectorId = SequenceRuntimeExecutors.CollectorNodeId;

    internal sealed record Edge(string SourceId, string TargetId, Func<WorkflowRunner.GraphMessage?, bool> Matches);

    private readonly ImmutableArray<Edge> edges;

    private SequenceDispatchEdgeTable(ImmutableArray<Edge> edges) => this.edges = edges;

    public IReadOnlyList<Edge> Edges => edges;

    /// <summary>Every distinct receiver the table delivers a message returned by <paramref name="executorId"/> to.</summary>
    public IReadOnlyCollection<string> TargetsFor(string executorId, WorkflowRunner.GraphMessage message) =>
        edges.Where(edge => edge.SourceId == executorId && edge.Matches(message))
            .Select(edge => edge.TargetId).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Decision 0070 slice (b) unblocker 4 (internal only): the body-local barrier mode. Each Sequence output route's
    /// return to the For Each feedback pin is delivered to the Sequence collector instead, and only the collector
    /// continues to the feedback pin, so the outputs join before the pass returns. No runner path builds this table.
    /// </summary>
    public static SequenceDispatchEdgeTable CreateBodyLocal(WorkflowDefinition definition,
        LoopFeedbackSequenceBarrierProof barrier)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(barrier);
        var execution = definition.ExecutionConnections ??
            throw new ArgumentException("The body-local barrier requires explicit execution connections.", nameof(definition));
        var routeNodes = barrier.RouteNodeIds.ToHashSet(StringComparer.Ordinal);
        var returns = barrier.ReturnConnectionIds.Select(id => execution.TryGetValue(id, out var edge) &&
                edge.Target.NodeId == barrier.LoopNodeId && routeNodes.Contains(edge.Source.NodeId)
                ? edge
                : throw new ArgumentException($"Return connection '{id}' is not a barrier route return to '{barrier.LoopNodeId}'.",
                    nameof(barrier)))
            .ToArray();
        if (returns.Length == 0 || returns.Select(edge => edge.Target.PortId).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new ArgumentException("The barrier returns must target one feedback pin.", nameof(barrier));

        var raw = new List<Edge>();
        // Edges inside the output routes are delivered as authored.
        foreach (var edge in execution.Values.Where(edge =>
                     routeNodes.Contains(edge.Source.NodeId) && routeNodes.Contains(edge.Target.NodeId)))
            raw.Add(new(edge.Source.NodeId, edge.Target.NodeId,
                message => message?.SourceNode == edge.Source.NodeId && message.SourcePort == edge.Source.PortId));
        // Each route's return joins at the Sequence collector instead of reaching the feedback pin.
        foreach (var edge in returns)
            raw.Add(new(edge.Source.NodeId, CollectorId,
                message => message?.SourceNode == edge.Source.NodeId && message.SourcePort == edge.Source.PortId));
        // Only the collector continues to the For Each feedback pin: one return per pass.
        raw.Add(new(CollectorId, barrier.LoopNodeId,
            message => message?.SourceNode == CollectorId && message.SourcePort == "completed"));
        return new(raw.GroupBy(edge => (edge.SourceId, edge.TargetId))
            .Select(group => group.Count() == 1
                ? group.Single()
                : new Edge(group.Key.SourceId, group.Key.TargetId, message => group.Any(edge => edge.Matches(message))))
            .ToImmutableArray());
    }

    public static SequenceDispatchEdgeTable Create(WorkflowDefinition definition,
        WorkflowRunner.SequenceProductionPlan plan, IReadOnlySet<string>? breakNodeIds)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(plan);
        var raw = new List<Edge>();
        void Add(string source, string target, Func<WorkflowRunner.GraphMessage?, bool> matches) =>
            raw.Add(new(source, target, matches));

        foreach (var output in plan.Outputs)
            if (definition.ExecutionConnections is null)
                Add(output.OutputNodeId, CollectorId, message => message?.SequenceLineage?.OutputId == output.OutputId);

        var authoredEdges = plan.BypassRevisionExecutionEdges
            ? Enumerable.Empty<ConnectionDefinition>()
            : (definition.ExecutionConnections ?? ImmutableSortedDictionary<string, ConnectionDefinition>.Empty).Values;
        foreach (var edge in authoredEdges.Where(edge =>
                     edge.Source.NodeId != plan.SequenceNodeId && edge.Target.NodeId != plan.SequenceNodeId &&
                     edge.Source.NodeId != definition.EntryNodeId))
            Add(edge.Source.NodeId, edge.Target.NodeId,
                message => message?.SourceNode == edge.Source.NodeId && message.SourcePort == edge.Source.PortId);

        if (!plan.BypassRevisionExecutionEdges && definition.ExecutionConnections is not null)
            foreach (var outputNodeId in definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.Output)
                         .Select(entry => entry.Key))
                Add(outputNodeId, CollectorId, message => message?.SourceNode == outputNodeId &&
                    message.SourcePort == "out" || breakNodeIds is not null &&
                    message?.ActivationBreakRequest is { } breakRequest &&
                    message.SourceNode == breakRequest.SourceNodeId && message.SourcePort == "break" &&
                    breakNodeIds.Contains(breakRequest.SourceNodeId));

        if (!plan.BypassRevisionExecutionEdges)
            foreach (var terminateNodeId in definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.Terminate)
                         .Select(entry => entry.Key))
                Add(terminateNodeId, CollectorId, message => message?.SourceNode == terminateNodeId &&
                    message.SourcePort == "terminated" && message.TerminationSignal);

        foreach (var callNodeId in definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.WorkflowCall)
                     .Select(entry => entry.Key))
        {
            bool CallTerminated(WorkflowRunner.GraphMessage? message) => message?.SourceNode == callNodeId &&
                message.SourcePort == "terminated" && message.TerminationSignal;
            foreach (var terminalOutputNodeId in definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.Output)
                         .Select(entry => entry.Key))
                Add(callNodeId, terminalOutputNodeId, CallTerminated);
            Add(callNodeId, CollectorId, CallTerminated);
        }

        if (plan.DirectOutputCollection)
            foreach (var output in plan.Outputs)
                Add(output.OutputNodeId, CollectorId, message => message?.SequenceLineage?.OutputId == output.OutputId);

        // One edge per executor pair: a message matching two original edges is still delivered once.
        var merged = raw.GroupBy(edge => (edge.SourceId, edge.TargetId))
            .Select(group => group.Count() == 1
                ? group.Single()
                : new Edge(group.Key.SourceId, group.Key.TargetId,
                    message => group.Any(edge => edge.Matches(message))))
            .ToImmutableArray();
        return new(merged);
    }
}
