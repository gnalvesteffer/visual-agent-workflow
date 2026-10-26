using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ConfigurableParallelGraphTests
{
    [Fact]
    public void ValidNWayParallelGraphUsesStableNamedPinsAndJsonJoin()
    {
        var definition = Definition(3);

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.True(WorkflowPorts.ExplicitExecutionOutputs(definition.Nodes["fork"]).SequenceEqual(new[] { "branch.research", "branch.review", "branch.write" }));
        Assert.True(WorkflowPorts.ExplicitExecutionInputs(definition.Nodes["join"]).SequenceEqual(new[] { "branch.research", "branch.review", "branch.write" }));
        Assert.Equal("json", Assert.Single(WorkflowPorts.ExplicitDataOutputs(definition.Nodes["join"])).DataType);
    }

    [Fact]
    public void RejectsCrossWiredBranchThatArrivesAtAnotherBranchesJoinPin()
    {
        var definition = Definition(3);
        var execution = definition.ExecutionConnections!;
        var wrongRoute = execution["agent-review-join"] with
        {
            Target = new("join", ParallelBranchLimits.PinId("research"))
        };

        var invalid = definition with
        {
            ExecutionConnections = execution.SetItem("agent-review-join", wrongRoute)
        };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "parallel.join.route");
    }

    [Fact]
    public void RejectsMissingBranchAnchorAndIncompleteJoinConfiguration()
    {
        var definition = Definition(3);
        var fork = definition.Nodes["fork"] with
        {
            ParallelFork = new([new("research", "Research", "research")])
        };
        var invalid = definition with
        {
            Nodes = definition.Nodes.SetItem("fork", fork)
        };

        var issues = WorkflowValidator.Validate(invalid);
        Assert.Contains(issues, issue => issue.Code == "parallel.fork.configuration");
        Assert.Contains(issues, issue => issue.Code == "parallel.join.configuration");
    }

    internal static WorkflowDefinition Definition(int branchCount)
    {
        var branches = new[]
        {
            new ParallelBranchDescriptor("research", "Research", "research"),
            new ParallelBranchDescriptor("review", "Review", "review"),
            new ParallelBranchDescriptor("write", "Write", "write")
        }.Take(branchCount).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0")
            {
                ParallelFork = new(branches)
            })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0")
            {
                ParallelJoin = new("fork", branches.Select(branch => branch.Id).ToImmutableArray())
            })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Execution("input", "then", "fork", "execute"));
        foreach (var branch in branches)
        {
            nodes = nodes.Add(branch.AnchorNodeId, new(NodeTypes.ParallelBranch, "1.0.0")
            {
                ParallelBranch = new("fork", branch.Id)
            }).Add($"agent-{branch.Id}", new(NodeTypes.Agent, "1.0.0",
                Agent: new("test.parallel", null, branch.Label, [])));
            execution = execution
                .Add($"fork-{branch.Id}", Execution("fork", ParallelBranchLimits.PinId(branch.Id), branch.AnchorNodeId, "execute"))
                .Add($"{branch.Id}-agent", Execution(branch.AnchorNodeId, "then", $"agent-{branch.Id}", "execute"))
                .Add($"agent-{branch.Id}-join", Execution($"agent-{branch.Id}", "out", "join", ParallelBranchLimits.PinId(branch.Id)));
        }
        execution = execution.Add("join-output", Execution("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.ConfigurableParallelGraph, "configurable-parallel", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static ConnectionDefinition Execution(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");
}
