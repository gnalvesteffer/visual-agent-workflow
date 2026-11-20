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
}
