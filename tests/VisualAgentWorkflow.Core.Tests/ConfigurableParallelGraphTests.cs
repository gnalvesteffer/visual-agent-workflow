using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ConfigurableParallelGraphTests
{
    [Fact]
    public void TypedBooleanWorkflowCanReceiveNamedParallelFanOutWithChosenBranchCount()
    {
        var definition = GraphEditing.CreateConfigurableParallelGraph(IntegerBranchGraph(), 4);

        Assert.Equal(WorkflowSchemaVersions.ConfigurableParallelGraph, definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(definition));
        var fork = Assert.Single(definition.Nodes.Values, node => node.Type == NodeTypes.ParallelFork);
        Assert.Equal(["branch_1", "branch_2", "branch_3", "branch_4"],
            fork.ParallelFork!.Branches.Select(branch => branch.Id));
        Assert.Equal("json", definition.Nodes["output"].OutputDataType);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void ConfigurableParallelBuilderRejectsBranchCountsOutsideTheSupportedRange(int branchCount) =>
        Assert.Throws<ArgumentException>(() => GraphEditing.CreateConfigurableParallelGraph(IntegerBranchGraph(), branchCount));

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
    public void RenamingABranchChangesItsCaptionWithoutChangingItsStablePin()
    {
        var definition = Definition(3);
        var renamed = GraphEditing.RenameConfigurableParallelBranch(definition, "fork", "review", "Independent review");
        var fork = renamed.Nodes["fork"];

        Assert.Equal("review", fork.ParallelFork!.Branches[1].Id);
        Assert.Equal("Independent review", fork.ParallelFork.Branches[1].Label);
        Assert.Contains("branch.review", WorkflowPorts.ExplicitExecutionOutputs(fork));
        Assert.Empty(WorkflowValidator.Validate(renamed));
    }

    [Fact]
    public void AddingABranchAtomicallyAddsItsAnchorAndMatchingForkAndJoinPins()
    {
        var definition = Definition(3);
        var added = GraphEditing.AddConfigurableParallelBranch(definition, "fork");
        var fork = added.Nodes["fork"].ParallelFork!;
        var join = added.Nodes["join"].ParallelJoin!;
        var branch = Assert.Single(fork.Branches, item => item.Id == "branch_1");

        Assert.True(fork.Branches.Select(item => item.Id).SequenceEqual(new[] { "research", "review", "write", "branch_1" }));
        Assert.True(join.BranchIds.SequenceEqual(new[] { "research", "review", "write", "branch_1" }));
        Assert.Equal(new ParallelBranchConfiguration("fork", "branch_1"), added.Nodes[branch.AnchorNodeId].ParallelBranch);
        Assert.Contains(added.ExecutionConnections!.Values, edge => edge.Source == new PortReference("fork", "branch.branch_1") && edge.Target == new PortReference(branch.AnchorNodeId, "execute"));
        Assert.Contains(added.ExecutionConnections.Values, edge => edge.Source == new PortReference(branch.AnchorNodeId, "then") && edge.Target == new PortReference("join", "branch.branch_1"));
        Assert.Empty(WorkflowValidator.Validate(added));
        Assert.Equal(3, definition.Nodes["fork"].ParallelFork!.Branches.Length);
    }

    [Fact]
    public void AddingBranchesStopsAtTheEightBranchLimit()
    {
        var definition = Definition(3);
        for (var index = 0; index < 5; index++) definition = GraphEditing.AddConfigurableParallelBranch(definition, "fork");

        Assert.Equal(ParallelGraphLimits.MaximumExplicitBranches, definition.Nodes["fork"].ParallelFork!.Branches.Length);
        Assert.Throws<ArgumentException>(() => GraphEditing.AddConfigurableParallelBranch(definition, "fork"));
    }

    [Fact]
    public void ReorderingABranchPreservesItsIdentityPinsAndConnections()
    {
        var definition = Definition(3);
        var execution = definition.ExecutionConnections;
        var reordered = GraphEditing.MoveConfigurableParallelBranch(definition, "fork", "write", -1);

        Assert.True(reordered.Nodes["fork"].ParallelFork!.Branches.Select(branch => branch.Id)
            .SequenceEqual(new[] { "research", "write", "review" }));
        Assert.True(reordered.Nodes["join"].ParallelJoin!.BranchIds.SequenceEqual(new[] { "research", "write", "review" }));
        Assert.Equal(execution, reordered.ExecutionConnections);
        Assert.Contains("branch.write", WorkflowPorts.ExplicitExecutionOutputs(reordered.Nodes["fork"]));
        Assert.Empty(WorkflowValidator.Validate(reordered));
    }

    [Fact]
    public void EmptyBranchCanBeRemovedWithoutChangingSurvivingBranchPins()
    {
        var definition = Definition(3);
        var execution = definition.ExecutionConnections!
            .Remove("research-agent")
            .Remove("agent-research-join")
            .Add("research-join", Execution("research", "then", "join", "branch.research"));
        var emptyPath = definition with
        {
            Nodes = definition.Nodes.Remove("agent-research"),
            ExecutionConnections = execution
        };
        Assert.Empty(WorkflowValidator.Validate(emptyPath));

        var removed = GraphEditing.RemoveEmptyConfigurableParallelBranch(emptyPath, "fork", "research");

        Assert.True(removed.Nodes["fork"].ParallelFork!.Branches.Select(branch => branch.Id)
            .SequenceEqual(new[] { "review", "write" }));
        Assert.True(removed.Nodes["join"].ParallelJoin!.BranchIds.SequenceEqual(new[] { "review", "write" }));
        Assert.DoesNotContain("research", removed.Nodes.Keys);
        Assert.Contains("branch.review", WorkflowPorts.ExplicitExecutionOutputs(removed.Nodes["fork"]));
        Assert.Contains("branch.write", WorkflowPorts.ExplicitExecutionInputs(removed.Nodes["join"]));
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void PopulatedBranchAndMinimumBranchCountCannotBeRemoved()
    {
        var populated = Definition(3);
        Assert.Throws<ArgumentException>(() => GraphEditing.RemoveEmptyConfigurableParallelBranch(populated, "fork", "research"));
        Assert.Equal(3, populated.Nodes["fork"].ParallelFork!.Branches.Length);

        var execution = populated.ExecutionConnections!
            .Remove("research-agent")
            .Remove("agent-research-join")
            .Add("research-join", Execution("research", "then", "join", "branch.research"));
        var emptyPath = populated with { Nodes = populated.Nodes.Remove("agent-research"), ExecutionConnections = execution };
        var minimumRegion = GraphEditing.RemoveEmptyConfigurableParallelBranch(emptyPath, "fork", "research");
        Assert.Equal(2, minimumRegion.Nodes["fork"].ParallelFork!.Branches.Length);
        Assert.Throws<ArgumentException>(() => GraphEditing.RemoveEmptyConfigurableParallelBranch(minimumRegion, "fork", "review"));
    }

    [Fact]
    public void TypedParameterEditsPreserveTheConfigurableParallelSchema()
    {
        var definition = GraphEditing.CreateConfigurableParallelGraph(IntegerBranchGraph(), 3);
        var withParameter = WorkflowParameterEditing.Add(definition, "limit", "Limit", "3");
        var typedParameter = WorkflowParameterEditing.SetType(withParameter, "limit", "integer", "3");

        Assert.Equal(WorkflowSchemaVersions.ConfigurableParallelGraph, typedParameter.SchemaVersion);
        Assert.Equal("integer", typedParameter.Parameters!["limit"].Type);
        Assert.Empty(WorkflowValidator.Validate(typedParameter));
    }

    [Fact]
    public void RemovingTheConfigurableRegionRevertsToThePriorIntegerGraphShape()
    {
        var definition = GraphEditing.CreateConfigurableParallelGraph(IntegerBranchGraph(), 4);
        var forkId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork).Key;
        var members = ParallelBlockEditing.Nodes(definition, forkId);

        Assert.Equal(6, members.Length);
        var restored = ParallelBlockEditing.Remove(definition, forkId);

        Assert.Equal(WorkflowSchemaVersions.IntegerGraph, restored.SchemaVersion);
        Assert.DoesNotContain(restored.Nodes.Values, node => node.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin);
        Assert.Equal("string", WorkflowPorts.ExplicitDataInputs(restored.Nodes["output"]).Single().DataType);
        Assert.Contains(restored.ExecutionConnections!.Values, edge => edge.Source == new PortReference("input", "then") && edge.Target == new PortReference("branch", "execute"));
        Assert.Contains(restored.Connections.Values, edge => edge.Source == new PortReference("input", "out") && edge.Target == new PortReference("output", "in"));
        Assert.Empty(WorkflowValidator.Validate(restored));
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

    private static WorkflowDefinition IntegerBranchGraph()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.IntegerGraph, "blank-integer-graph", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("condition", new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(true) })
                .Add("branch", new(NodeTypes.BooleanBranch, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0"))
                .Add("failure", new(NodeTypes.Terminate, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("predicate", new(new("condition", "out"), new("branch", "condition"), "data"))
                .Add("result", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("activate", Execution("input", "then", "branch", "execute"))
                .Add("success", Execution("branch", "true", "output", "execute"))
                .Add("failure", Execution("branch", "false", "failure", "execute"))
        };
        return definition;
    }

    private static ConnectionDefinition Execution(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");
}
