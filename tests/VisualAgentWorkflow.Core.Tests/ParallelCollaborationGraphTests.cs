using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ParallelCollaborationGraphTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void ValidCollaborativeRegionBindsBranchPathsAndPhaseGrants(int branchCount)
    {
        var definition = Definition(branchCount);

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.Equal(WorkflowSchemaVersions.CollaborativeParallelGraph, definition.SchemaVersion);
        Assert.Equal(Enumerable.Range(0, branchCount).Select(index => ParallelBranchLimits.PinId(BranchId(index))),
            WorkflowPorts.ExplicitExecutionInputs(definition.Nodes["rendezvous"]));
        Assert.Equal(Enumerable.Range(0, branchCount).Select(index => ParallelBranchLimits.PinId(BranchId(index))),
            WorkflowPorts.ExplicitExecutionOutputs(definition.Nodes["rendezvous"]));
    }

    [Fact]
    public void GrantsMustNameAnAgentOnTheSameBranchAndCorrectSideOfRendezvous()
    {
        var definition = Definition(2);
        var region = definition.ParallelCollaborationRegions!["rendezvous"];
        var branchA = region.Branches[0];
        var wrongBranch = branchA with
        {
            Agents = [new("agent-b-publish", ParallelCollaborationPhases.Publish, ["work"])]
        };
        var wrongPhase = branchA with
        {
            Agents = [new("agent-a-publish", ParallelCollaborationPhases.Read, ["work"])]
        };

        Assert.Contains(WorkflowValidator.Validate(definition with
        {
            ParallelCollaborationRegions = definition.ParallelCollaborationRegions.SetItem("rendezvous", region with
            {
                Branches = [wrongBranch, region.Branches[1]]
            })
        }), issue => issue.Code == "parallel.collaboration.grant.phase");
        Assert.Contains(WorkflowValidator.Validate(definition with
        {
            ParallelCollaborationRegions = definition.ParallelCollaborationRegions.SetItem("rendezvous", region with
            {
                Branches = [wrongPhase, region.Branches[1]]
            })
        }), issue => issue.Code == "parallel.collaboration.grant.phase");
    }

    [Fact]
    public void RejectsMissingRegionAndMismatchedRendezvousBranchPins()
    {
        var definition = Definition(2);
        Assert.Contains(WorkflowValidator.Validate(definition with { ParallelCollaborationRegions = null }),
            issue => issue.Code == "parallel.collaboration.missing");

        var mismatched = definition with
        {
            Nodes = definition.Nodes.SetItem("rendezvous", definition.Nodes["rendezvous"] with
            {
                ParallelRendezvous = new(["a"])
            })
        };
        Assert.Contains(WorkflowValidator.Validate(mismatched), issue => issue.Code == "parallel.collaboration.topology.branches");
    }

    [Fact]
    public void IncompleteDraftWithUnconfiguredRendezvousReturnsDiagnostics()
    {
        var definition = Definition(2) with
        {
            Nodes = Definition(2).Nodes.SetItem("rendezvous", new(NodeTypes.ParallelRendezvous, "1.0.0"))
        };

        var issues = WorkflowValidator.Validate(definition);
        Assert.Contains(issues, issue => issue.Code == "parallel.rendezvous.configuration");
        Assert.Contains(issues, issue => issue.Code == "parallel.collaboration.topology.branches");

        var savedDraft = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(definition));
        Assert.Equal(WorkflowJson.ComputeDraftHash(definition), WorkflowJson.ComputeDraftHash(savedDraft));
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(definition));
    }

    [Fact]
    public void CollaborativeDraftCanPreserveExistingRoutesWhileRendezvousIsBeingAuthored()
    {
        var configured = Definition(2);
        var staged = configured with
        {
            Nodes = configured.Nodes.SetItem("rendezvous", new(NodeTypes.ParallelRendezvous, "1.0.0")),
            ParallelCollaborationRegions = null,
            ExecutionConnections = configured.ExecutionConnections!
                .Remove("a-rendezvous")
                .Add("a-direct-to-join", Execution("anchor-a", "then", "join", "branch.a"))
        };

        Assert.Contains(WorkflowValidator.Validate(staged), issue => issue.Code == "execution.target");
        var roundTripped = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(staged));

        Assert.Equal(WorkflowJson.ComputeDraftHash(staged), WorkflowJson.ComputeDraftHash(roundTripped));
        Assert.Contains(WorkflowValidator.Validate(roundTripped), issue => issue.Code == "execution.target");
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(staged));
    }

    [Fact]
    public void WorkflowPackageRoundTripsCollaborationContractWithoutExportingHostConfiguration()
    {
        var definition = Definition(2);
        var layout = WorkflowLayout.Create(definition);

        var json = WorkflowPackageJson.Serialize(definition, layout);
        var imported = WorkflowPackageJson.Deserialize(json);

        Assert.Equal(WorkflowSchemaVersions.CollaborativeParallelGraph, imported.Definition.SchemaVersion);
        Assert.True(WorkflowJson.ComputeHash(imported.Definition) ==
            "71c99a48b8a26120a561be17c702fdc93116d67cc0ddb9de080a88763c3eddac",
            WorkflowJson.ComputeHash(imported.Definition));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(imported.Definition));
        Assert.Equal(json, WorkflowPackageJson.Serialize(imported.Definition, imported.Layout));
        Assert.DoesNotContain("endpoint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowPackageRoundTripPreservesSchema132ParallelGraph()
    {
        var definition = ConfigurableParallelGraphTests.Definition(3);
        var layout = WorkflowLayout.Create(definition);

        var imported = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, layout));

        Assert.Equal(WorkflowSchemaVersions.ConfigurableParallelGraph, imported.Definition.SchemaVersion);
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(imported.Definition));
        Assert.Null(imported.Definition.ParallelCollaborationRegions);
        Assert.True(WorkflowJson.ComputeHash(imported.Definition) ==
            "67f82470a960cdea119e7e1458843bdfac59b2dc38ef75523b147df2fb8dc60a",
            WorkflowJson.ComputeHash(imported.Definition));
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("join")]
    [InlineData("rendezvous")]
    public void RejectsMissingRegionNodeReferences(string nodeId)
    {
        var definition = Definition(2) with
        {
            Nodes = Definition(2).Nodes.Remove(nodeId)
        };
        Assert.Contains(WorkflowValidator.Validate(definition), issue =>
            issue.Code is "parallel.collaboration.topology.count" or "parallel.collaboration.topology.reference");
    }

    [Fact]
    public void RejectsCrossBranchArrivalAndJoinBypass()
    {
        var definition = Definition(2);
        var crossBranch = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!.Remove("a-rendezvous").Add("a-publish-rendezvous",
                Execution("agent-a-publish", "out", "rendezvous", ParallelBranchLimits.PinId("b")))
        };
        Assert.Contains(WorkflowValidator.Validate(crossBranch), issue => issue.Code == "parallel.collaboration.topology.branch-pin");

        var bypass = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("a-publish-rendezvous")
                .Add("a-publish-join", Execution("agent-a-publish", "out", "join", ParallelBranchLimits.PinId("a")))
        };
        Assert.Contains(WorkflowValidator.Validate(bypass), issue => issue.Code is "execution.target" or "parallel.collaboration.topology.bypass" or "parallel.collaboration.topology.incomplete");
    }

    [Fact]
    public void RejectsOrphanRendezvousAndAgentGrantOutsideRegion()
    {
        var definition = Definition(2);
        var withOrphan = definition with
        {
            Nodes = definition.Nodes.Add("orphan-rendezvous", new(NodeTypes.ParallelRendezvous, "1.0.0")
            {
                ParallelRendezvous = new(["a", "b"])
            })
        };
        Assert.Contains(WorkflowValidator.Validate(withOrphan), issue => issue.Code == "parallel.collaboration.topology.count");

        var region = definition.ParallelCollaborationRegions!["rendezvous"];
        var invalidGrant = region.Branches[0] with
        {
            Agents = [new("input", ParallelCollaborationPhases.Publish, ["work"])]
        };
        var invalid = definition with
        {
            ParallelCollaborationRegions = definition.ParallelCollaborationRegions.SetItem("rendezvous", region with
            {
                Branches = [invalidGrant, region.Branches[1]]
            })
        };
        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code is "parallel.collaboration.grant.node" or "parallel.collaboration.grant.phase");
    }

    [Fact]
    public void CollaborationRegionsCannotBeReenteredByWorkflowLoops()
    {
        var original = Definition(2);
        var definition = original with
        {
            Nodes = original.Nodes
                .Add("loop", new(NodeTypes.Loop, "1.0.0")
                {
                    Loop = new(new StringCondition("equals", "done"), 2)
                })
                .Add("loop-body", new(NodeTypes.LoopBody, "1.0.0"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "parallel.collaboration.loop");
    }

    [Fact]
    public void Schema132SerializationAndHashesRemainUnchangedAndRejectNewNodeConfiguration()
    {
        var definition = ConfigurableParallelGraphTests.Definition(3);
        var json = WorkflowJson.Serialize(definition);
        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.Equal(json, WorkflowJson.Serialize(definition with { ParallelCollaborationRegions = null }));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(
            WorkflowJson.Deserialize(json)));
        Assert.DoesNotContain("parallelCollaborationRegions", json);
        Assert.DoesNotContain("parallelRendezvous", json);

        var wrongSchemaNode = definition with
        {
            Nodes = definition.Nodes.SetItem("agent-research", definition.Nodes["agent-research"] with
            {
                ParallelRendezvous = new(["research", "review", "write"])
            })
        };
        Assert.Contains(WorkflowValidator.Validate(wrongSchemaNode), issue => issue.Code == "parallel.rendezvous.unexpected");
        Assert.Contains(WorkflowValidator.Validate(definition with
        {
            ParallelCollaborationRegions = ImmutableSortedDictionary<string, ParallelCollaborationRegion>.Empty.Add("rendezvous",
                new("fork", "rendezvous", "join", ["work"], null, []))
        }), issue => issue.Code == "parallel.collaboration.unexpected");
    }

    [Fact]
    public void RendezvousPinIdsRemainStableWhenBranchDisplayOrderChanges()
    {
        var definition = Definition(3);
        var originalIds = WorkflowPorts.ExplicitExecutionInputs(definition.Nodes["rendezvous"])
            .Order(StringComparer.Ordinal).ToArray();
        var region = definition.ParallelCollaborationRegions!["rendezvous"];
        var reordered = definition with
        {
            Nodes = definition.Nodes
                .SetItem("fork", definition.Nodes["fork"] with
                {
                    ParallelFork = definition.Nodes["fork"].ParallelFork! with
                    {
                        Branches = definition.Nodes["fork"].ParallelFork!.Branches.Reverse().ToImmutableArray()
                    }
                })
                .SetItem("join", definition.Nodes["join"] with
                {
                    ParallelJoin = definition.Nodes["join"].ParallelJoin! with
                    {
                        BranchIds = definition.Nodes["join"].ParallelJoin!.BranchIds.Reverse().ToImmutableArray()
                    }
                })
                .SetItem("rendezvous", definition.Nodes["rendezvous"] with
                {
                    ParallelRendezvous = definition.Nodes["rendezvous"].ParallelRendezvous! with
                    {
                        BranchIds = definition.Nodes["rendezvous"].ParallelRendezvous!.BranchIds.Reverse().ToImmutableArray()
                    }
                }),
            ParallelCollaborationRegions = definition.ParallelCollaborationRegions.SetItem("rendezvous", region with
            {
                Branches = region.Branches.Reverse().ToImmutableArray()
            })
        };

        Assert.Equal(originalIds, WorkflowPorts.ExplicitExecutionInputs(reordered.Nodes["rendezvous"]).Order(StringComparer.Ordinal));
        Assert.Empty(WorkflowValidator.Validate(reordered));
    }

    private static WorkflowDefinition Definition(int branchCount)
    {
        var ids = Enumerable.Range(0, branchCount).Select(BranchId).ToArray();
        var branches = ids.Select((id, index) => new ParallelBranchDescriptor(id, $"Branch {index + 1}", $"anchor-{id}")).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("rendezvous", new(NodeTypes.ParallelRendezvous, "1.0.0") { ParallelRendezvous = new(ids.ToImmutableArray()) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ids.ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Execution("input", "then", "fork", "execute"));
        var regionBranches = ImmutableArray.CreateBuilder<ParallelCollaborationBranch>();
        foreach (var id in ids)
        {
            var anchor = $"anchor-{id}";
            var publishAgent = $"agent-{id}-publish";
            var readAgent = $"agent-{id}-read";
            nodes = nodes
                .Add(anchor, new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", id) })
                .Add(publishAgent, AgentNode())
                .Add(readAgent, AgentNode());
            execution = execution
                .Add($"fork-{id}", Execution("fork", ParallelBranchLimits.PinId(id), anchor, "execute"))
                .Add($"{id}-publish", Execution(anchor, "then", publishAgent, "execute"))
                .Add($"{id}-rendezvous", Execution(publishAgent, "out", "rendezvous", ParallelBranchLimits.PinId(id)))
                .Add($"rendezvous-{id}", Execution("rendezvous", ParallelBranchLimits.PinId(id), readAgent, "execute"))
                .Add($"{id}-join", Execution(readAgent, "out", "join", ParallelBranchLimits.PinId(id)));
            regionBranches.Add(new(id,
            [
                new(publishAgent, ParallelCollaborationPhases.Publish, ["work"]),
                new(readAgent, ParallelCollaborationPhases.Read, ["work"])
            ]));
        }
        execution = execution.Add("join-output", Execution("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.CollaborativeParallelGraph, "collaboration", "input", nodes, data)
        {
            ExecutionConnections = execution,
            ParallelCollaborationRegions = ImmutableSortedDictionary<string, ParallelCollaborationRegion>.Empty.Add("rendezvous",
                new("fork", "rendezvous", "join", ["work"], 60, regionBranches.ToImmutable()))
        };
    }

    private static string BranchId(int index) => ((char)('a' + index)).ToString();
    private static NodeDefinition AgentNode() => new(NodeTypes.Agent, "1.0.0", Agent: new("test.provider", null, "test agent", []));
    private static ConnectionDefinition Execution(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");
}
