using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>
/// Runtime-facing, graph-derived ownership metadata for the private structured
/// activation implementation. Creating this plan does not admit a workflow or
/// dispatch any nodes.
/// </summary>
internal sealed record StructuredActivationGraphPlan(
    ImmutableArray<LoopFeedbackRegionProof> LoopRegions,
    ImmutableDictionary<string, LoopFeedbackRegionProof> RegionsByLoopNodeId,
    ImmutableDictionary<string, string> OwningLoopByBodyNodeId,
    ImmutableDictionary<string, string> TargetLoopByBreakNodeId)
{
    /// <summary>Body-local Sequence barriers by owning For Each (Decision 0070 slice (b) unblocker 4); empty otherwise.</summary>
    public ImmutableDictionary<string, LoopFeedbackSequenceBarrierProof> SequenceBarriersByLoopNodeId { get; init; } =
        ImmutableDictionary<string, LoopFeedbackSequenceBarrierProof>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// Body passes one iteration of <paramref name="loopNodeId"/> is charged. Interim conservative rule (Decision 0070
    /// slice (b) unblocker 4, needs review): a body-local Sequence barrier charges one pass plus one per output
    /// activation, so its iterations draw the run's pass pool down faster, never slower, than an ordinary pass.
    /// </summary>
    public int ChargedPassesPerIteration(string loopNodeId) =>
        RegionsByLoopNodeId.ContainsKey(loopNodeId)
            ? 1 + (SequenceBarriersByLoopNodeId.GetValueOrDefault(loopNodeId)?.SequenceOutputPinIds.Length ?? 0)
            : throw new ArgumentException($"'{loopNodeId}' is not a planned loop.", nameof(loopNodeId));

    /// <summary>The most iterations of <paramref name="loopNodeId"/> a pass budget admits under that charge.</summary>
    public int MaximumIterationsWithinPassBudget(string loopNodeId, int passBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(passBudget);
        return passBudget / ChargedPassesPerIteration(loopNodeId);
    }

    public static bool TryCreate(WorkflowDefinition definition,
        out StructuredActivationGraphPlan? plan,
        out ImmutableArray<LoopFeedbackTopologyIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(definition);
        plan = null;

        if (definition.SchemaVersion != WorkflowSchemaVersions.UnifiedWorkflowGraph)
        {
            issues = [new("activation.plan.schema", "schemaVersion",
                "Structured activation planning is available only for unified-schema proof candidates.")];
            return false;
        }

        var topology = LoopFeedbackTopologyProofAnalyzer.Analyze(definition,
            LoopFeedbackPinShape.ExplicitFeedbackInput);
        if (!topology.IsValid)
        {
            var inferredTopology = LoopFeedbackTopologyProofAnalyzer.Analyze(definition,
                LoopFeedbackPinShape.InferredFromBodyTopology);
            if (inferredTopology.IsValid)
                topology = inferredTopology;
            else
            {
                issues = topology.Issues.AddRange(inferredTopology.Issues);
                return false;
            }
        }
        issues = topology.Issues;

        var regionsByLoop = topology.Regions.ToImmutableDictionary(region => region.LoopNodeId,
            StringComparer.Ordinal);
        var owningLoopByBodyNode = topology.Regions
            .SelectMany(region => region.BodyNodeIds.Select(nodeId => (nodeId, region.LoopNodeId)))
            .ToImmutableDictionary(pair => pair.nodeId, pair => pair.LoopNodeId, StringComparer.Ordinal);
        var targetLoopByBreakNode = topology.Regions
            .SelectMany(region => region.BreakRoutes.Select(route => (route.BreakNodeId, route.LoopNodeId)))
            .ToImmutableDictionary(pair => pair.BreakNodeId, pair => pair.LoopNodeId, StringComparer.Ordinal);

        plan = new(topology.Regions, regionsByLoop, owningLoopByBodyNode, targetLoopByBreakNode);
        return true;
    }

    /// <summary>
    /// Decision 0070 slice (b) unblocker 4 (internal only): compiles the loop plan for a For Each whose body holds one
    /// body-local Sequence barrier. No validator or runner path calls this; the shape stays rejected in production.
    /// </summary>
    public static bool TryCreateForBodyLocalSequenceBarrier(WorkflowDefinition definition,
        out StructuredActivationGraphPlan? plan,
        out ImmutableArray<LoopFeedbackTopologyIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(definition);
        plan = null;
        if (definition.SchemaVersion != WorkflowSchemaVersions.UnifiedWorkflowGraph)
        {
            issues = [new("activation.plan.schema", "schemaVersion",
                "Structured activation planning is available only for unified-schema proof candidates.")];
            return false;
        }
        // Only the explicit feedback pin: the barrier rule refuses the inferred shape.
        var topology = LoopFeedbackTopologyProofAnalyzer.Analyze(definition,
            LoopFeedbackPinShape.ExplicitFeedbackInput, allowBodyLocalSequenceBarrier: true);
        issues = topology.Issues;
        if (!topology.IsValid) return false;
        var barriers = topology.Regions.Where(region => region.SequenceBarrier is not null)
            .ToImmutableDictionary(region => region.LoopNodeId, region => region.SequenceBarrier!, StringComparer.Ordinal);
        if (barriers.Count != 1)
        {
            issues = [new("activation.plan.sequence-barrier", "nodes",
                "The body-local Sequence barrier plan requires exactly one For Each with a Sequence barrier.")];
            return false;
        }
        plan = new(topology.Regions,
            topology.Regions.ToImmutableDictionary(region => region.LoopNodeId, StringComparer.Ordinal),
            topology.Regions.SelectMany(region => region.BodyNodeIds.Select(nodeId => (nodeId, region.LoopNodeId)))
                .ToImmutableDictionary(pair => pair.nodeId, pair => pair.LoopNodeId, StringComparer.Ordinal),
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal))
        {
            SequenceBarriersByLoopNodeId = barriers
        };
        return true;
    }
}
