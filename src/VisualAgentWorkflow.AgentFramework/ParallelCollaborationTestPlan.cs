using System.Collections.Immutable;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Test-only topology overlay. It is deliberately absent from workflow definitions and public APIs.</summary>
internal sealed record ParallelCollaborationTestPlan(
    string ForkNodeId,
    string JoinNodeId,
    ImmutableArray<string> Channels,
    ImmutableArray<ParallelCollaborationTestBranch> Branches,
    TimeSpan? Timeout = null,
    Func<CancellationToken, ValueTask>? BeforeRelease = null)
{
    public void Validate(VisualAgentWorkflow.Core.WorkflowDefinition definition)
    {
        if (definition.SchemaVersion != VisualAgentWorkflow.Core.WorkflowSchemaVersions.ConfigurableParallelGraph)
            throw new InvalidOperationException("The collaboration test plan only overlays schema 1.32 graphs.");
        if (!definition.Nodes.TryGetValue(ForkNodeId, out var fork) || fork.Type != VisualAgentWorkflow.Core.NodeTypes.ParallelFork ||
            !definition.Nodes.TryGetValue(JoinNodeId, out var join) || join.Type != VisualAgentWorkflow.Core.NodeTypes.ParallelJoin)
            throw new InvalidOperationException("The collaboration test plan must identify an existing fork and join.");
        if (Branches.Length is < 2 or > 8 || Branches.Select(branch => branch.BranchId).Distinct(StringComparer.Ordinal).Count() != Branches.Length ||
            Channels.IsDefaultOrEmpty || Channels.Distinct(StringComparer.Ordinal).Count() != Channels.Length)
            throw new InvalidOperationException("The collaboration test plan has invalid branch or channel membership.");

        var configuredBranches = fork.ParallelFork!.Branches.Select(branch => branch.Id).ToHashSet(StringComparer.Ordinal);
        if (!configuredBranches.SetEquals(Branches.Select(branch => branch.BranchId)) ||
            !join.ParallelJoin!.BranchIds.ToHashSet(StringComparer.Ordinal).SetEquals(configuredBranches) ||
            join.ParallelJoin.ForkNodeId != ForkNodeId)
            throw new InvalidOperationException("The collaboration test plan must match every configured fork and join branch.");

        var edges = definition.ExecutionConnections!.Values;
        foreach (var branch in Branches)
        {
            var branchNodeId = definition.Nodes.SingleOrDefault(entry => entry.Value.Type == VisualAgentWorkflow.Core.NodeTypes.ParallelBranch &&
                entry.Value.ParallelBranch?.ForkNodeId == ForkNodeId && entry.Value.ParallelBranch.BranchId == branch.BranchId).Key;
            if (branchNodeId is null ||
                !edges.Any(edge => edge.Source.NodeId == ForkNodeId && edge.Source.PortId == $"branch.{branch.BranchId}" && edge.Target.NodeId == branchNodeId) ||
                !HasEdge(edges, branchNodeId, branch.PublishAgentNodeId) || !HasEdge(edges, branch.PublishAgentNodeId, branch.ReadAgentNodeId) ||
                !HasEdge(edges, branch.ReadAgentNodeId, JoinNodeId) ||
                definition.Nodes[branch.PublishAgentNodeId].Type != VisualAgentWorkflow.Core.NodeTypes.Agent ||
                definition.Nodes[branch.ReadAgentNodeId].Type != VisualAgentWorkflow.Core.NodeTypes.Agent)
                throw new InvalidOperationException($"The collaboration test plan does not match the authored path for branch '{branch.BranchId}'.");
        }
        if (Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("A collaboration test-plan timeout must be positive.");
    }

    private static bool HasEdge(IEnumerable<VisualAgentWorkflow.Core.ConnectionDefinition> edges, string source, string target) =>
        edges.Any(edge => edge.Source.NodeId == source && edge.Target.NodeId == target);
}

internal sealed record ParallelCollaborationTestBranch(string BranchId, string PublishAgentNodeId, string ReadAgentNodeId);
