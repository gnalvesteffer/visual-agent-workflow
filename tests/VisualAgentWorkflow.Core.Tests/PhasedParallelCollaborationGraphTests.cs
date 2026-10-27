using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class PhasedParallelCollaborationGraphTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void ValidPhasedRegionBindsTwoOrderedBarriersAndStableBranchPins(int branchCount)
    {
        var definition = Definition(branchCount);

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.Equal(WorkflowSchemaVersions.PhasedCollaborativeParallelGraph, definition.SchemaVersion);
        Assert.Equal(Enumerable.Range(0, branchCount).Select(index => ParallelBranchLimits.PinId(BranchId(index))),
            WorkflowPorts.ExplicitExecutionInputs(definition.Nodes["rendezvous-1"]));
        Assert.Equal(Enumerable.Range(0, branchCount).Select(index => ParallelBranchLimits.PinId(BranchId(index))),
            WorkflowPorts.ExplicitExecutionInputs(definition.Nodes["rendezvous-2"]));
    }

    [Fact]
    public void RequiresBothDistinctRendezvousNodesAndPhaseOrder()
    {
        var definition = Definition(2);
        var region = definition.ParallelCollaborationRegions!["rendezvous-1"];

        Assert.Contains(WorkflowValidator.Validate(definition with
        {
            ParallelCollaborationRegions = definition.ParallelCollaborationRegions.SetItem("rendezvous-1", region with { SecondRendezvousNodeId = null })
        }), issue => issue.Code == "parallel.collaboration.barriers");
        Assert.Contains(WorkflowValidator.Validate(definition with
        {
            ParallelCollaborationRegions = definition.ParallelCollaborationRegions.SetItem("rendezvous-1", region with { SecondRendezvousNodeId = "rendezvous-1" })
        }), issue => issue.Code == "parallel.collaboration.topology.reference");

        var reversed = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("a-p1-to-r1").Add("a-p1-to-r2", Execution("agent-a-p1-publish", "out", "rendezvous-2", Pin("a")))
        };
        Assert.Contains(WorkflowValidator.Validate(reversed), issue => issue.Code is
            "execution.target" or "parallel.collaboration.topology.bypass" or "parallel.collaboration.topology.incomplete");
    }

    [Fact]
    public void RejectsMissingOrDuplicateBarrierNodesAndMismatchedStablePins()
    {
        var definition = Definition(2);
        var missing = definition with { Nodes = definition.Nodes.Remove("rendezvous-2") };
        Assert.Contains(WorkflowValidator.Validate(missing), issue => issue.Code is "parallel.collaboration.topology.count" or "parallel.collaboration.topology.reference");

        var duplicate = definition with
        {
            Nodes = definition.Nodes.Add("rendezvous-extra", definition.Nodes["rendezvous-1"])
        };
        Assert.Contains(WorkflowValidator.Validate(duplicate), issue => issue.Code == "parallel.collaboration.topology.count");

        var mismatched = definition with
        {
            Nodes = definition.Nodes.SetItem("rendezvous-2", definition.Nodes["rendezvous-2"] with
            {
                ParallelRendezvous = new(["a"])
            })
        };
        Assert.Contains(WorkflowValidator.Validate(mismatched), issue => issue.Code == "parallel.collaboration.topology.branches");
    }

    [Theory]
    [InlineData(WorkflowSchemaVersions.CollaborativeParallelGraph)]
    [InlineData(WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)]
    public void AddingAnUnconfiguredRendezvousToAnIncompleteCollaborationDraftRemainsSaveable(string schemaVersion)
    {
        var source = UnconfiguredRendezvousDraft(schemaVersion, "rendezvous-1");
        var edited = source with
        {
            Nodes = source.Nodes.Add("rendezvous-2", new(NodeTypes.ParallelRendezvous, "1.0.0"))
        };

        Assert.Contains(WorkflowValidator.Validate(edited), issue => issue.Code == "parallel.collaboration.missing");
        var savedDraft = WorkflowDraftEditing.ValidateEdit(edited);
        var roundTripped = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(savedDraft));

        Assert.Contains("rendezvous-2", roundTripped.Nodes.Keys);
        Assert.Contains(WorkflowValidator.Validate(roundTripped), issue => issue.Code == "parallel.collaboration.missing");
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(roundTripped));
        Assert.Throws<ArgumentException>(() => WorkflowRevision.Create(roundTripped, "test"));
    }

    [Fact]
    public void RejectsBranchEscapeAndCrossBranchRendezvousPins()
    {
        var definition = Definition(2);
        var escaped = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("a-p1-read-to-p2-publish")
                .Add("a-escape", Execution("agent-a-p1-read", "out", "output", "execute"))
        };
        Assert.Contains(WorkflowValidator.Validate(escaped), issue => issue.Code is
            "execution.target" or "parallel.collaboration.topology.bypass" or "parallel.collaboration.topology.incomplete");

        var crossBranch = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("a-p1-to-r1")
                .Add("a-cross-r1", Execution("agent-a-p1-publish", "out", "rendezvous-1", Pin("b")))
        };
        Assert.Contains(WorkflowValidator.Validate(crossBranch), issue => issue.Code == "parallel.collaboration.topology.branch-pin");
    }

    [Fact]
    public void GrantsMustMatchExactAgentPhaseRoleAndConfiguredChannels()
    {
        var definition = Definition(2);
        var region = definition.ParallelCollaborationRegions!["rendezvous-1"];
        var branchA = region.Branches[0];
        var invalidCases = new[]
        {
            branchA with { Agents = [new("agent-a-p1-publish", ParallelCollaborationPhases.Phase1, ["work"]) ] },
            branchA with { Agents = [new("agent-a-p1-publish", ParallelCollaborationPhases.Phase2, ["work"]) { Role = ParallelCollaborationRoles.Publish }] },
            branchA with { Agents = [new("input", ParallelCollaborationPhases.Phase1, ["work"]) { Role = ParallelCollaborationRoles.Publish }] },
            branchA with { Agents = [new("agent-a-p1-publish", ParallelCollaborationPhases.Phase1, ["missing"]) { Role = ParallelCollaborationRoles.Publish }] },
            branchA with { Agents = [new("agent-a-p1-publish", "other", ["work"]) { Role = "write" }] },
            branchA with { Agents = [new("agent-a-p1-publish", ParallelCollaborationPhases.Phase1, ["work"]) { Role = ParallelCollaborationRoles.Read }] }
        };
        foreach (var invalid in invalidCases)
        {
            var candidate = definition with
            {
                ParallelCollaborationRegions = definition.ParallelCollaborationRegions.SetItem("rendezvous-1", region with
                {
                    Branches = [invalid, region.Branches[1]]
                })
            };
            Assert.Contains(WorkflowValidator.Validate(candidate), issue =>
                issue.Code.StartsWith("parallel.collaboration.grant", StringComparison.Ordinal));
        }

        var duplicateAgent = branchA with
        {
            Agents = [branchA.Agents[0], branchA.Agents[0] with { Phase = ParallelCollaborationPhases.Phase2 }]
        };
        var duplicateCandidate = definition with
        {
            ParallelCollaborationRegions = definition.ParallelCollaborationRegions.SetItem("rendezvous-1", region with
            {
                Branches = [duplicateAgent, region.Branches[1]]
            })
        };
        Assert.Contains(WorkflowValidator.Validate(duplicateCandidate), issue => issue.Code == "parallel.collaboration.grant");
    }

    [Fact]
    public void PackageRoundTripsSchema135AndSchema133KeepsItsCanonicalHashAndBytes()
    {
        var phased = Definition(2);
        var imported = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(phased, WorkflowLayout.Create(phased)));
        Assert.Equal(WorkflowSchemaVersions.PhasedCollaborativeParallelGraph, imported.Definition.SchemaVersion);
        Assert.Equal(WorkflowJson.ComputeHash(phased), WorkflowJson.ComputeHash(imported.Definition));
        Assert.Equal(WorkflowPackageJson.Serialize(phased, WorkflowLayout.Create(phased)),
            WorkflowPackageJson.Serialize(imported.Definition, imported.Layout));

        var legacy = LegacyDefinition();
        var legacyJson = WorkflowJson.Serialize(legacy);
        Assert.Equal("71c99a48b8a26120a561be17c702fdc93116d67cc0ddb9de080a88763c3eddac", WorkflowJson.ComputeHash(legacy));
        Assert.DoesNotContain("secondRendezvousNodeId", legacyJson);
        Assert.DoesNotContain("role", legacyJson);
        Assert.Equal(legacyJson, WorkflowJson.Serialize(WorkflowJson.Deserialize(legacyJson)));
    }


    private static WorkflowDefinition LegacyDefinition()
    {
        var ids = new[] { "a", "b" };
        var branches = ids.Select((id, index) => new ParallelBranchDescriptor(id, $"Branch {index + 1}", $"anchor-{id}")).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("rendezvous", new(NodeTypes.ParallelRendezvous, "1.0.0") { ParallelRendezvous = new(ids.ToImmutableArray()) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ids.ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Execution("input", "then", "fork", "execute"));
        var grants = ImmutableArray.CreateBuilder<ParallelCollaborationBranch>();
        foreach (var id in ids)
        {
            var anchor = $"anchor-{id}";
            var publish = $"agent-{id}-publish";
            var read = $"agent-{id}-read";
            nodes = nodes.Add(anchor, new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", id) })
                .Add(publish, AgentNode()).Add(read, AgentNode());
            execution = execution.Add($"fork-{id}", Execution("fork", Pin(id), anchor, "execute"))
                .Add($"{id}-publish", Execution(anchor, "then", publish, "execute"))
                .Add($"{id}-rendezvous", Execution(publish, "out", "rendezvous", Pin(id)))
                .Add($"rendezvous-{id}", Execution("rendezvous", Pin(id), read, "execute"))
                .Add($"{id}-join", Execution(read, "out", "join", Pin(id)));
            grants.Add(new(id,
            [
                new(publish, ParallelCollaborationPhases.Publish, ["work"]),
                new(read, ParallelCollaborationPhases.Read, ["work"])
            ]));
        }
        execution = execution.Add("join-output", Execution("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.CollaborativeParallelGraph, "collaboration", "input", nodes, data)
        {
            ExecutionConnections = execution,
            ParallelCollaborationRegions = ImmutableSortedDictionary<string, ParallelCollaborationRegion>.Empty.Add("rendezvous",
                new("fork", "rendezvous", "join", ["work"], 60, grants.ToImmutable()))
        };
    }

    private static WorkflowDefinition Definition(int branchCount)
    {
        var ids = Enumerable.Range(0, branchCount).Select(BranchId).ToArray();
        var branches = ids.Select((id, index) => new ParallelBranchDescriptor(id, $"Branch {index + 1}", $"anchor-{id}")).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("rendezvous-1", Rendezvous(ids))
            .Add("rendezvous-2", Rendezvous(ids))
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ids.ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Execution("input", "then", "fork", "execute"));
        var regionBranches = ImmutableArray.CreateBuilder<ParallelCollaborationBranch>();
        foreach (var id in ids)
        {
            var anchor = $"anchor-{id}";
            var publish1 = $"agent-{id}-p1-publish";
            var read1 = $"agent-{id}-p1-read";
            var publish2 = $"agent-{id}-p2-publish";
            var read2 = $"agent-{id}-p2-read";
            nodes = nodes
                .Add(anchor, new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", id) })
                .Add(publish1, AgentNode())
                .Add(read1, AgentNode())
                .Add(publish2, AgentNode())
                .Add(read2, AgentNode());
            execution = execution
                .Add($"fork-{id}", Execution("fork", Pin(id), anchor, "execute"))
                .Add($"{id}-p1", Execution(anchor, "then", publish1, "execute"))
                .Add($"{id}-p1-to-r1", Execution(publish1, "out", "rendezvous-1", Pin(id)))
                .Add($"r1-{id}-read", Execution("rendezvous-1", Pin(id), read1, "execute"))
                .Add($"{id}-p1-read-to-p2-publish", Execution(read1, "out", publish2, "execute"))
                .Add($"{id}-p2-to-r2", Execution(publish2, "out", "rendezvous-2", Pin(id)))
                .Add($"r2-{id}-read", Execution("rendezvous-2", Pin(id), read2, "execute"))
                .Add($"{id}-join", Execution(read2, "out", "join", Pin(id)));
            regionBranches.Add(new(id,
            [
                new(publish1, ParallelCollaborationPhases.Phase1, ["work"]) { Role = ParallelCollaborationRoles.Publish },
                new(read1, ParallelCollaborationPhases.Phase1, ["work"]) { Role = ParallelCollaborationRoles.Read },
                new(publish2, ParallelCollaborationPhases.Phase2, ["work"]) { Role = ParallelCollaborationRoles.Publish },
                new(read2, ParallelCollaborationPhases.Phase2, ["work"]) { Role = ParallelCollaborationRoles.Read }
            ]));
        }
        execution = execution.Add("join-output", Execution("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.PhasedCollaborativeParallelGraph, "phased-collaboration", "input", nodes, data)
        {
            ExecutionConnections = execution,
            ParallelCollaborationRegions = ImmutableSortedDictionary<string, ParallelCollaborationRegion>.Empty.Add("rendezvous-1",
                new("fork", "rendezvous-1", "join", ["work"], 60, regionBranches.ToImmutable())
                {
                    SecondRendezvousNodeId = "rendezvous-2"
                })
        };
    }

    private static WorkflowDefinition UnconfiguredRendezvousDraft(string schemaVersion, string rendezvousId) => new(
        schemaVersion,
        "incomplete-collaboration",
        "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add(rendezvousId, new(NodeTypes.ParallelRendezvous, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-output", new(new("input", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
    };

    private static NodeDefinition Rendezvous(string[] ids) => new(NodeTypes.ParallelRendezvous, "1.0.0")
    {
        ParallelRendezvous = new(ids.ToImmutableArray())
    };

    private static string BranchId(int index) => ((char)('a' + index)).ToString();
    private static string Pin(string id) => ParallelBranchLimits.PinId(id);
    private static NodeDefinition AgentNode() => new(NodeTypes.Agent, "1.0.0", Agent: new("test.provider", null, "test agent", []));
    private static ConnectionDefinition Execution(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");
}
