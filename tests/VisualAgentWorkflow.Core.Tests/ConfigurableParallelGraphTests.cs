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
    public void Schema134AllowsOneBranchLocalBooleanRouteWithExclusiveReconvergence()
    {
        var definition = BranchLocalDefinitionFromIntegerGraph();

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.Equal(WorkflowSchemaVersions.BranchLocalParallelGraph, WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)).SchemaVersion);
    }

    [Fact]
    public void UnifiedGraphAllowsDirectMcpCallsInsideStaticParallelBranches()
    {
        var definition = UnifiedMcpBranchDefinition();

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void UnifiedGraphAllowsNestedForkJoinOwnedByOneOuterBranch()
    {
        var definition = NestedUnifiedParallelDefinition();

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void UnifiedGraphAllowsArbitrarilyDeeperForkJoinOwnership()
    {
        var definition = DeeperNestedUnifiedParallelDefinition();

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void NestedConfigurableBranchEditsUseTheSelectedForkAndItsMatchingJoin()
    {
        var definition = NestedUnifiedParallelDefinition();
        var originalOuterFork = definition.Nodes["fork"].ParallelFork;
        var originalOuterJoin = definition.Nodes["join"].ParallelJoin;

        var added = GraphEditing.AddConfigurableParallelBranch(definition, "inner-fork");
        var addedBranch = Assert.Single(added.Nodes["inner-fork"].ParallelFork!.Branches,
            branch => branch.Id == "branch_1");
        Assert.Equal(3, added.Nodes["inner-join"].ParallelJoin!.BranchIds.Length);
        Assert.Equal(originalOuterFork, added.Nodes["fork"].ParallelFork);
        Assert.Equal(originalOuterJoin, added.Nodes["join"].ParallelJoin);
        Assert.Empty(WorkflowValidator.Validate(added));

        var reordered = GraphEditing.MoveConfigurableParallelBranch(added, "inner-fork", "inner_b", -1);
        Assert.Equal(["inner_b", "inner_a", "branch_1"],
            reordered.Nodes["inner-fork"].ParallelFork!.Branches.Select(branch => branch.Id));
        Assert.Equal(["inner_b", "inner_a", "branch_1"],
            reordered.Nodes["inner-join"].ParallelJoin!.BranchIds.ToArray());
        Assert.Equal(added.ExecutionConnections, reordered.ExecutionConnections);
        Assert.Empty(WorkflowValidator.Validate(reordered));

        var removed = GraphEditing.RemoveEmptyConfigurableParallelBranch(reordered, "inner-fork", "branch_1");
        Assert.DoesNotContain(addedBranch.AnchorNodeId, removed.Nodes.Keys);
        Assert.Equal(["inner_b", "inner_a"],
            removed.Nodes["inner-fork"].ParallelFork!.Branches.Select(branch => branch.Id));
        Assert.Equal(["inner_b", "inner_a"],
            removed.Nodes["inner-join"].ParallelJoin!.BranchIds.ToArray());
        Assert.Equal(originalOuterFork, removed.Nodes["fork"].ParallelFork);
        Assert.Equal(originalOuterJoin, removed.Nodes["join"].ParallelJoin);
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void UnifiedGraphParallelRegionInsertionCreatesExecutionTopologyAndLeavesDataWiresUntouched()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "parallel-insert", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("data-path", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("execution-path", Execution("input", "then", "output", "execute"))
        };

        var inserted = GraphEditing.InsertParallelRegionAtExecutionConnection(definition, "execution-path");

        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, inserted.SchemaVersion);
        Assert.Equal(definition.Connections, inserted.Connections);
        Assert.Equal(6, inserted.Nodes.Count);
        Assert.Empty(WorkflowValidator.Validate(inserted));
        var fork = Assert.Single(inserted.Nodes, entry => entry.Value.Type == NodeTypes.ParallelFork);
        var join = Assert.Single(inserted.Nodes, entry => entry.Value.Type == NodeTypes.ParallelJoin);
        Assert.Contains(inserted.ExecutionConnections!.Values, edge => edge.Source == new PortReference("input", "then") &&
            edge.Target == new PortReference(fork.Key, "execute"));
        Assert.Contains(inserted.ExecutionConnections.Values, edge => edge.Source == new PortReference(join.Key, "then") &&
            edge.Target == new PortReference("output", "execute"));
    }

    [Fact]
    public void UnifiedGraphCanInsertParallelRegionInsideAnOwnedNestedBranch()
    {
        var definition = NestedUnifiedParallelDefinition();
        var originalData = definition.Connections;

        var inserted = GraphEditing.InsertParallelRegionAtExecutionConnection(definition, "inner-anchor-b-agent");

        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, inserted.SchemaVersion);
        Assert.Equal(originalData, inserted.Connections);
        Assert.Equal(definition.Nodes.Count + 4, inserted.Nodes.Count);
        Assert.Equal(3, inserted.Nodes.Count(entry => entry.Value.Type == NodeTypes.ParallelFork));
        Assert.Equal(3, inserted.Nodes.Count(entry => entry.Value.Type == NodeTypes.ParallelJoin));
        Assert.Empty(WorkflowValidator.Validate(inserted));
    }

    [Theory]
    [InlineData("outer-review")]
    [InlineData("inner-agent-a-join")]
    public void UnifiedGraphParallelInsertionRejectsBranchAnchorAndBarrierInputs(string executionConnectionId)
    {
        var definition = NestedUnifiedParallelDefinition();

        Assert.Throws<ArgumentException>(() =>
            GraphEditing.InsertParallelRegionAtExecutionConnection(definition, executionConnectionId));
    }

    [Fact]
    public void NestedRegionSelectionResolvesTheInnermostOwnedForkJoinBlock()
    {
        var definition = NestedUnifiedParallelDefinition();

        var expected = new[] { "inner-fork", "inner-anchor-a", "inner-anchor-b", "inner-agent-a", "inner-agent-b", "inner-join" }
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, ParallelBlockEditing.Nodes(definition, "inner-fork"));
        Assert.Equal(expected, ParallelBlockEditing.Nodes(definition, "inner-anchor-a"));
        Assert.Equal(expected, ParallelBlockEditing.Nodes(definition, "inner-join"));
        Assert.Equal(expected, ParallelBlockEditing.Nodes(definition, "inner-agent-a"));
    }

    [Fact]
    public void RemovingNestedRegionPreservesItsParentAndReconnectsWithinTheOwnerBranch()
    {
        var definition = NestedUnifiedParallelDefinition();

        var removed = ParallelBlockEditing.Remove(definition, "inner-anchor-a");

        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, removed.SchemaVersion);
        Assert.All(new[] { "inner-fork", "inner-anchor-a", "inner-anchor-b", "inner-agent-a", "inner-agent-b", "inner-join" },
            id => Assert.DoesNotContain(id, removed.Nodes.Keys));
        Assert.Contains("fork", removed.Nodes.Keys);
        Assert.Contains("review", removed.Nodes.Keys);
        Assert.Contains("join", removed.Nodes.Keys);
        Assert.Contains(removed.ExecutionConnections!.Values, edge =>
            edge.Source == new PortReference("research", "then") && edge.Target == new PortReference("agent-research", "execute"));
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void RemovingMiddleNestedRegionAlsoRemovesItsDescendantsAndPreservesTheOuterRegion()
    {
        var definition = DeeperNestedUnifiedParallelDefinition();

        var removed = ParallelBlockEditing.Remove(definition, "inner-fork");

        Assert.DoesNotContain(removed.Nodes.Keys, id => id.StartsWith("inner-", StringComparison.Ordinal) ||
            id.StartsWith("deep-", StringComparison.Ordinal));
        Assert.Contains("fork", removed.Nodes.Keys);
        Assert.Contains("review", removed.Nodes.Keys);
        Assert.Contains("join", removed.Nodes.Keys);
        Assert.Contains(removed.ExecutionConnections!.Values, edge =>
            edge.Source == new PortReference("research", "then") && edge.Target == new PortReference("agent-research", "execute"));
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void RemovingInnermostRegionPreservesItsParentContinuation()
    {
        var definition = DeeperNestedUnifiedParallelDefinition();

        var removed = ParallelBlockEditing.Remove(definition, "deep-agent-x");

        Assert.DoesNotContain(removed.Nodes.Keys, id => id.StartsWith("deep-", StringComparison.Ordinal));
        Assert.Contains("inner-fork", removed.Nodes.Keys);
        Assert.Contains("inner-agent-a", removed.Nodes.Keys);
        Assert.Contains("inner-join", removed.Nodes.Keys);
        Assert.Contains(removed.ExecutionConnections!.Values, edge =>
            edge.Source == new PortReference("inner-anchor-a", "then") && edge.Target == new PortReference("inner-agent-a", "execute"));
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void RemovingRootRegionPreservesSchemaAndOutputTypeWhileReportingDisconnectedDataResult()
    {
        var definition = Definition(2) with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };

        var removed = ParallelBlockEditing.Remove(definition, "fork");

        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, removed.SchemaVersion);
        Assert.Equal("json", removed.Nodes["output"].OutputDataType);
        Assert.DoesNotContain(removed.Nodes.Values, node => node.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin);
        Assert.Contains(removed.ExecutionConnections!.Values, edge =>
            edge.Source == new PortReference("input", "then") && edge.Target == new PortReference("output", "execute"));
        Assert.Contains(WorkflowValidator.Validate(removed), issue => issue.Code == "data.degree");
    }

    [Fact]
    public void UnifiedGraphAllowsMultipleSequentialRootForkJoinRegions()
    {
        var definition = SequentialUnifiedParallelDefinition();

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void UnifiedGraphRejectsNestedForkSharedBySiblingOwners()
    {
        var definition = NestedUnifiedParallelDefinition();
        var execution = definition.ExecutionConnections!
            .Remove("research-inner-fork")
            .Add("review-inner-fork", Execution("review", "then", "inner-fork", "execute"));
        var shared = definition with { ExecutionConnections = execution };

        Assert.Contains(WorkflowValidator.Validate(shared), issue => issue.Code == "parallel.nested.owner");
    }

    [Fact]
    public void UnifiedGraphRejectsNestedJoinPairedWithAnotherFork()
    {
        var definition = NestedUnifiedParallelDefinition();
        var invalid = definition with
        {
            Nodes = definition.Nodes.SetItem("inner-join", definition.Nodes["inner-join"] with
            {
                ParallelJoin = new("fork", ["inner_a", "inner_b"])
            })
        };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "parallel.join.match");
    }

    [Fact]
    public void UnifiedGraphDiagnosesMultipleNestedJoinsWithoutThrowing()
    {
        var definition = NestedUnifiedParallelDefinition();
        var invalid = definition with
        {
            Nodes = definition.Nodes.Add("duplicate-inner-join", new(NodeTypes.ParallelJoin, "1.0.0")
            {
                ParallelJoin = new("inner-fork", ["inner_a", "inner_b"])
            })
        };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "parallel.join.match");
    }

    [Fact]
    public void UnifiedParallelGraphKeepsApprovalNodesOutsideParallelBranches()
    {
        var definition = UnifiedMcpBranchDefinition();
        var nodes = definition.Nodes;
        var execution = definition.ExecutionConnections!;
        var data = definition.Connections;
        foreach (var branch in new[] { "research", "review" })
        {
            var toolId = $"tool-{branch}";
            var approvalId = $"approval-{branch}";
            nodes = nodes.Add(approvalId, new(NodeTypes.Approval, "1.0.0")
                { Approval = new($"Approve {branch}") });
            data = data.Add($"input-{approvalId}", new(new("input", "out"), new(approvalId, "in"), "data"));
            execution = execution
                .SetItem($"{branch}-tool", Execution($"{branch}", "then", approvalId, "execute"))
                .Add($"{approvalId}-tool", Execution(approvalId, "then", toolId, "execute"));
        }
        var withApprovals = definition with { Nodes = nodes, Connections = data, ExecutionConnections = execution };

        Assert.Contains(WorkflowValidator.Validate(withApprovals), issue => issue.Code == "parallel.branch.node");
    }

    [Fact]
    public void HistoricalParallelSchemaDoesNotAcquireUnifiedMcpBranchBehavior()
    {
        var definition = UnifiedMcpBranchDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.ConfigurableParallelGraph
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "parallel.branch.node");
    }

    [Fact]
    public void Schema134WorkflowPackageRoundTripsWithoutChangingDefinitionHashOrLayout()
    {
        var definition = BranchLocalDefinitionFromIntegerGraph();
        var layout = WorkflowLayout.Create(definition);
        var json = WorkflowPackageJson.Serialize(definition, layout);

        var imported = WorkflowPackageJson.Deserialize(json);

        Assert.Equal(WorkflowSchemaVersions.BranchLocalParallelGraph, imported.Definition.SchemaVersion);
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(imported.Definition));
        Assert.Equal(WorkflowJson.Serialize(definition), WorkflowJson.Serialize(imported.Definition));
        Assert.Equal(json, WorkflowPackageJson.Serialize(imported.Definition, imported.Layout));
    }

    [Fact]
    public void UnifiedThreeLevelNestedParallelPackageAndProjectReleaseRoundTripWithoutSchemaGate()
    {
        var definition = DeeperNestedUnifiedParallelDefinition();
        var layout = WorkflowLayout.Create(definition);
        var packageJson = WorkflowPackageJson.Serialize(definition, layout);

        var imported = WorkflowPackageJson.Deserialize(packageJson);

        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, imported.Definition.SchemaVersion);
        Assert.Equal(WorkflowJson.ComputeHash(definition), imported.DefinitionHash);
        Assert.Equal(WorkflowJson.Serialize(definition), WorkflowJson.Serialize(imported.Definition));
        Assert.Equal(WorkflowLayoutJson.ComputeHash(layout, definition), imported.LayoutHash);
        Assert.Equal(packageJson, WorkflowPackageJson.Serialize(imported.Definition, imported.Layout));
        Assert.All(new[] { "fork", "inner-fork", "deep-fork" }, forkId =>
            Assert.Contains(imported.Definition.Nodes, node => node.Key == forkId && node.Value.Type == NodeTypes.ParallelFork));

        var archive = WorkflowProjectPackageJson.CreateArchive("nested-parallel-project", "Nested parallel",
            [imported]);
        var release = WorkflowProjectPackageJson.Read(archive);
        var released = release.Workflows[definition.WorkflowId];

        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, released.Definition.SchemaVersion);
        Assert.Equal(imported.DefinitionHash, released.DefinitionHash);
        Assert.Equal(imported.LayoutHash, released.LayoutHash);
        Assert.Equal(packageJson, WorkflowPackageJson.Serialize(released.Definition, released.Layout));
    }

    [Fact]
    public void Schema132StillRejectsBranchLocalBooleanRouting()
    {
        var definition = BranchLocalDefinition() with { SchemaVersion = WorkflowSchemaVersions.ConfigurableParallelGraph };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code.StartsWith("parallel.branch.", StringComparison.Ordinal));
    }

    [Fact]
    public void BranchLocalBooleanRouteRejectsCrossBranchEscapeAndCycles()
    {
        var definition = BranchLocalDefinition();
        var escaped = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!.SetItem("research-false", Execution("branch-research", "false", "agent-review", "execute"))
        };
        var cyclic = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!.SetItem("research-false", Execution("branch-research", "false", "branch-research", "execute"))
        };

        Assert.Contains(WorkflowValidator.Validate(escaped), issue => issue.Code is "parallel.branch.entry" or "parallel.branch.overlap");
        Assert.Contains(WorkflowValidator.Validate(cyclic), issue => issue.Code == "parallel.branch.cycle");
    }

    [Fact]
    public void BranchLocalBooleanRouteRequiresBothOutcomesAndStableMatchingJoin()
    {
        var definition = BranchLocalDefinition();
        var missingOutcome = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!.Remove("research-false")
        };
        var wrongJoin = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!.SetItem("agent-research-join",
                Execution("agent-research", "out", "join", ParallelBranchLimits.PinId("review")))
        };

        Assert.Contains(WorkflowValidator.Validate(missingOutcome), issue => issue.Code is "graph.degree" or "parallel.branch.path");
        Assert.Contains(WorkflowValidator.Validate(wrongJoin), issue => issue.Code is "parallel.join.route" or "parallel.branch.entry");
    }

    [Fact]
    public void IncompleteSchema134RouteRemainsSaveableAsADraftButCannotBePublished()
    {
        var incomplete = BranchLocalDefinition() with
        {
            ExecutionConnections = BranchLocalDefinition().ExecutionConnections!.Remove("research-false")
        };

        var saved = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(incomplete));

        Assert.Equal(WorkflowJson.ComputeDraftHash(incomplete), WorkflowJson.ComputeDraftHash(saved));
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(incomplete));
        Assert.Contains(WorkflowValidator.Validate(saved), issue => issue.Code is "parallel.branch.path" or "graph.degree");
    }

    [Fact]
    public void RemovingTheSchema134RegionRemovesItsLocalRoutesAndReturnsToIntegerGraph()
    {
        var definition = BranchLocalDefinitionFromIntegerGraph();
        var memberIds = ParallelBlockEditing.Nodes(definition, "branch-research");

        Assert.Contains("branch-research", memberIds);
        Assert.Contains("research-condition", memberIds);
        var restored = ParallelBlockEditing.Remove(definition, "branch-research");

        Assert.Equal(WorkflowSchemaVersions.IntegerGraph, restored.SchemaVersion);
        Assert.DoesNotContain(restored.Nodes.Values, node => node.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin);
        Assert.DoesNotContain("branch-research", restored.Nodes.Keys);
        Assert.DoesNotContain("research-condition", restored.Nodes.Keys);
        Assert.Empty(WorkflowValidator.Validate(restored));
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

    private static WorkflowDefinition UnifiedMcpBranchDefinition()
    {
        var definition = Definition(2) with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var nodes = definition.Nodes;
        var connections = definition.Connections;
        var execution = definition.ExecutionConnections!;
        foreach (var branch in new[] { "research", "review" })
        {
            var agentId = $"agent-{branch}";
            var toolId = $"tool-{branch}";
            nodes = nodes.Remove(agentId).Add(toolId, new(NodeTypes.McpTool, "1.0.0",
                new ToolBindingRequest("test.tool", new string('a', 64)))
            {
                McpArgumentsJson = $"{{\"query\":\"{branch}\"}}"
            });
            execution = execution.Remove($"{branch}-agent").Remove($"agent-{branch}-join")
                .Add($"{branch}-tool", Execution(branch, "then", toolId, "execute"))
                .Add($"{toolId}-join", Execution(toolId, "then", "join", ParallelBranchLimits.PinId(branch)));
            connections = connections.Add($"input-{toolId}",
                new(new("input", "out"), new(toolId, "in"), "data"));
        }
        return definition with { Nodes = nodes, Connections = connections, ExecutionConnections = execution };
    }

    private static WorkflowDefinition NestedUnifiedParallelDefinition()
    {
        var definition = Definition(2) with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var nodes = definition.Nodes
            .Add("inner-fork", new(NodeTypes.ParallelFork, "1.0.0")
            {
                ParallelFork = new([
                    new("inner_a", "Inner A", "inner-anchor-a"),
                    new("inner_b", "Inner B", "inner-anchor-b")])
            })
            .Add("inner-anchor-a", new(NodeTypes.ParallelBranch, "1.0.0")
            {
                ParallelBranch = new("inner-fork", "inner_a")
            })
            .Add("inner-anchor-b", new(NodeTypes.ParallelBranch, "1.0.0")
            {
                ParallelBranch = new("inner-fork", "inner_b")
            })
            .Add("inner-agent-a", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "Inner A", [])))
            .Add("inner-agent-b", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "Inner B", [])))
            .Add("inner-join", new(NodeTypes.ParallelJoin, "1.0.0")
            {
                ParallelJoin = new("inner-fork", ["inner_a", "inner_b"])
            });
        var execution = definition.ExecutionConnections!
            .Remove("research-agent")
            .Remove("agent-research-join")
            .Add("research-inner-fork", Execution("research", "then", "inner-fork", "execute"))
            .Add("inner-fork-a", Execution("inner-fork", "branch.inner_a", "inner-anchor-a", "execute"))
            .Add("inner-fork-b", Execution("inner-fork", "branch.inner_b", "inner-anchor-b", "execute"))
            .Add("inner-anchor-a-agent", Execution("inner-anchor-a", "then", "inner-agent-a", "execute"))
            .Add("inner-anchor-b-agent", Execution("inner-anchor-b", "then", "inner-agent-b", "execute"))
            .Add("inner-agent-a-join", Execution("inner-agent-a", "out", "inner-join", "branch.inner_a"))
            .Add("inner-agent-b-join", Execution("inner-agent-b", "out", "inner-join", "branch.inner_b"))
            .Add("inner-join-research-agent", Execution("inner-join", "then", "agent-research", "execute"))
            .Add("agent-research-join", Execution("agent-research", "out", "join", "branch.research"));
        var connections = definition.Connections
            .Add("input-inner-a", new(new("input", "out"), new("inner-agent-a", "in"), "data"))
            .Add("input-inner-b", new(new("input", "out"), new("inner-agent-b", "in"), "data"));
        return definition with { Nodes = nodes, Connections = connections, ExecutionConnections = execution };
    }

    private static WorkflowDefinition DeeperNestedUnifiedParallelDefinition()
    {
        var definition = NestedUnifiedParallelDefinition();
        var nodes = definition.Nodes
            .Add("deep-fork", new(NodeTypes.ParallelFork, "1.0.0")
            {
                ParallelFork = new([
                    new("deep_x", "Deep X", "deep-anchor-x"),
                    new("deep_y", "Deep Y", "deep-anchor-y")])
            })
            .Add("deep-anchor-x", new(NodeTypes.ParallelBranch, "1.0.0")
            {
                ParallelBranch = new("deep-fork", "deep_x")
            })
            .Add("deep-anchor-y", new(NodeTypes.ParallelBranch, "1.0.0")
            {
                ParallelBranch = new("deep-fork", "deep_y")
            })
            .Add("deep-agent-x", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "Deep X", [])))
            .Add("deep-agent-y", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "Deep Y", [])))
            .Add("deep-join", new(NodeTypes.ParallelJoin, "1.0.0")
            {
                ParallelJoin = new("deep-fork", ["deep_x", "deep_y"])
            });
        var execution = definition.ExecutionConnections!
            .Remove("inner-anchor-a-agent")
            .Add("inner-a-deep-fork", Execution("inner-anchor-a", "then", "deep-fork", "execute"))
            .Add("deep-fork-x", Execution("deep-fork", "branch.deep_x", "deep-anchor-x", "execute"))
            .Add("deep-fork-y", Execution("deep-fork", "branch.deep_y", "deep-anchor-y", "execute"))
            .Add("deep-anchor-x-agent", Execution("deep-anchor-x", "then", "deep-agent-x", "execute"))
            .Add("deep-anchor-y-agent", Execution("deep-anchor-y", "then", "deep-agent-y", "execute"))
            .Add("deep-agent-x-join", Execution("deep-agent-x", "out", "deep-join", "branch.deep_x"))
            .Add("deep-agent-y-join", Execution("deep-agent-y", "out", "deep-join", "branch.deep_y"))
            .Add("deep-join-inner-a", Execution("deep-join", "then", "inner-agent-a", "execute"));
        var connections = definition.Connections
            .Add("input-deep-x", new(new("input", "out"), new("deep-agent-x", "in"), "data"))
            .Add("input-deep-y", new(new("input", "out"), new("deep-agent-y", "in"), "data"));
        return definition with { Nodes = nodes, Connections = connections, ExecutionConnections = execution };
    }

    private static WorkflowDefinition SequentialUnifiedParallelDefinition()
    {
        var definition = Definition(2) with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("next_a", "Next A", "next-anchor-a"),
            new ParallelBranchDescriptor("next_b", "Next B", "next-anchor-b"));
        var nodes = definition.Nodes
            .Add("next-fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("next-anchor-a", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("next-fork", "next_a") })
            .Add("next-anchor-b", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("next-fork", "next_b") })
            .Add("next-agent-a", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "Next A", [])))
            .Add("next-agent-b", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "Next B", [])))
            .Add("next-join", new(NodeTypes.ParallelJoin, "1.0.0")
            {
                ParallelJoin = new("next-fork", ["next_a", "next_b"])
            });
        var execution = definition.ExecutionConnections!
            .Remove("join-output")
            .Add("join-next-fork", Execution("join", "then", "next-fork", "execute"))
            .Add("next-fork-a", Execution("next-fork", "branch.next_a", "next-anchor-a", "execute"))
            .Add("next-fork-b", Execution("next-fork", "branch.next_b", "next-anchor-b", "execute"))
            .Add("next-anchor-a-agent", Execution("next-anchor-a", "then", "next-agent-a", "execute"))
            .Add("next-anchor-b-agent", Execution("next-anchor-b", "then", "next-agent-b", "execute"))
            .Add("next-agent-a-join", Execution("next-agent-a", "out", "next-join", "branch.next_a"))
            .Add("next-agent-b-join", Execution("next-agent-b", "out", "next-join", "branch.next_b"))
            .Add("next-join-output", Execution("next-join", "then", "output", "execute"));
        var connections = definition.Connections
            .Remove("join-json")
            .Add("input-next-a", new(new("input", "out"), new("next-agent-a", "in"), "data"))
            .Add("input-next-b", new(new("input", "out"), new("next-agent-b", "in"), "data"))
            .Add("next-join-json", new(new("next-join", "out"), new("output", "in"), "data"));
        return definition with { Nodes = nodes, Connections = connections, ExecutionConnections = execution };
    }

    private static WorkflowDefinition BranchLocalDefinition()
    {
        var definition = Definition(2);
        var nodes = definition.Nodes
            .Add("research-condition", new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(true) })
            .Add("branch-research", new(NodeTypes.BooleanBranch, "1.0.0"));
        var execution = definition.ExecutionConnections!
            .Remove("research-agent")
            .Add("research-anchor-branch", Execution("research", "then", "branch-research", "execute"))
            .Add("research-true", Execution("branch-research", "true", "agent-research", "execute"))
            .Add("research-false", Execution("branch-research", "false", "agent-research", "execute"));
        var data = definition.Connections.Add("research-condition-value",
            new(new("research-condition", "out"), new("branch-research", "condition"), "data"));
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.BranchLocalParallelGraph,
            Nodes = nodes,
            Connections = data,
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition BranchLocalDefinitionFromIntegerGraph()
    {
        var definition = GraphEditing.CreateConfigurableParallelGraph(IntegerBranchGraph(), 2);
        var forkId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork).Key;
        var joinId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelJoin).Key;
        var branch = definition.Nodes[forkId].ParallelFork!.Branches[0];
        var anchorEdge = definition.ExecutionConnections!.Single(entry => entry.Value.Source.NodeId == branch.AnchorNodeId);
        var nodes = definition.Nodes
            .Add("research-condition", new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(true) })
            .Add("branch-research", new(NodeTypes.BooleanBranch, "1.0.0"))
            .Add("research-after", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "after", [])));
        var execution = definition.ExecutionConnections!
            .Remove(anchorEdge.Key)
            .Add("research-anchor-branch", Execution(branch.AnchorNodeId, "then", "branch-research", "execute"))
            .Add("research-true", Execution("branch-research", "true", "research-after", "execute"))
            .Add("research-false", Execution("branch-research", "false", "research-after", "execute"))
            .Add("research-after-join", Execution("research-after", "out", joinId, ParallelBranchLimits.PinId(branch.Id)));
        var data = definition.Connections.Add("research-condition-value",
            new(new("research-condition", "out"), new("branch-research", "condition"), "data"));
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.BranchLocalParallelGraph,
            Nodes = nodes,
            Connections = data,
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
