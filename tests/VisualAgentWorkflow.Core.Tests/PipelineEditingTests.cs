using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public class PipelineEditingTests
{
    private static WorkflowDefinition Empty() => new("1.0", "editing", "input",
        new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0"), ["output"] = new(NodeTypes.Output, "1.0.0") }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition> { ["direct"] = new(new("input", "out"), new("output", "in"), "data") }.ToImmutableSortedDictionary());

    [Fact]
    public void InsertMoveRemovePreserveStableNodeAndUnaffectedConnectionIds()
    {
        var first = PipelineEditing.InsertAfter(Empty(), "input", NodeTypes.Uppercase);
        var originalJson = WorkflowJson.Serialize(first);
        var firstId = PipelineEditing.OrderedNodes(first)[1];
        var firstLink = first.Connections.Single(e => e.Value.Source.NodeId == "input");
        var second = PipelineEditing.InsertAfter(first, firstId, NodeTypes.Reverse);
        var secondId = PipelineEditing.OrderedNodes(second)[2];
        Assert.Equal(firstLink.Value, second.Connections[firstLink.Key]);
        var moved = PipelineEditing.Move(second, secondId, -1);
        Assert.Equal(new[] { "input", secondId, firstId, "output" }, PipelineEditing.OrderedNodes(moved));
        Assert.Equal(NodeTypes.Uppercase, moved.Nodes[firstId].Type);
        var removed = PipelineEditing.Remove(moved, secondId);
        Assert.Equal(new[] { "input", firstId, "output" }, PipelineEditing.OrderedNodes(removed));
        Assert.Equal(originalJson, WorkflowJson.Serialize(first));
        Assert.Equal(3, first.Nodes.Count);
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void OperationChangeKeepsNodeAndConnectionIdentityAndAppearsInDiff()
    {
        var initial = PipelineEditing.InsertAfter(Empty(), "input", NodeTypes.Uppercase);
        var id = PipelineEditing.OrderedNodes(initial)[1];
        var changed = PipelineEditing.ChangeOperation(initial, id, NodeTypes.Reverse);
        Assert.Equal(initial.Connections, changed.Connections);
        var change = Assert.Single(WorkflowDifference.Compare(initial, changed));
        Assert.Equal(id, change.Id);
        Assert.Equal("node", change.Kind);
        Assert.Contains(NodeTypes.Uppercase, change.Before);
        Assert.Contains(NodeTypes.Reverse, change.After);
        Assert.Empty(WorkflowDifference.Compare(initial, WorkflowJson.Deserialize(WorkflowJson.Serialize(initial))));
    }

    [Fact]
    public void DifferenceIncludesWorkflowParametersSkillsAndAgentProfiles()
    {
        var initial = WorkflowParameterEditing.Add(Empty() with { SchemaVersion = "1.19" }, "model", "Model", "small");
        var skill = WorkflowSkill.Create("research", "1.0.0", "Research", "Use concise, cited answers.");
        var profile = AgentProfile.Create("researcher", "1.0.0", "Researcher", "Research specialist.",
            "test-provider", "1.0.0", "Research the question.", []);
        var updated = WorkflowParameterEditing.Add(initial, "locale", "Locale", "en") with
        {
            Skills = ImmutableSortedDictionary<string, WorkflowSkill>.Empty.Add(skill.Id + "@" + skill.Version, skill),
            AgentProfiles = ImmutableSortedDictionary<string, AgentProfile>.Empty.Add(profile.Id + "@" + profile.Version, profile)
        };

        var changes = WorkflowDifference.Compare(initial, updated);

        Assert.Equal(["agentProfile", "parameter", "skill"], changes.Select(change => change.Kind).Order(StringComparer.Ordinal));
        Assert.Contains(changes, change => change.Kind == "parameter" && change.Id == "locale" && change.Before is null && change.After!.Contains("Locale", StringComparison.Ordinal));
        Assert.Contains(changes, change => change.Kind == "skill" && change.Id == "research@1.0.0");
        Assert.Contains(changes, change => change.Kind == "agentProfile" && change.Id == "researcher@1.0.0");
    }

    [Fact]
    public void TerminalEditsUnknownOperationsAndIllegalMovesAreRejected()
    {
        var definition = Empty();
        Assert.Throws<ArgumentException>(() => PipelineEditing.Remove(definition, "input"));
        Assert.Throws<ArgumentException>(() => PipelineEditing.InsertAfter(definition, "output", NodeTypes.Uppercase));
        Assert.Throws<ArgumentException>(() => PipelineEditing.InsertAfter(definition, "input", "arbitrary.code"));
        var withNode = PipelineEditing.InsertAfter(definition, "input", NodeTypes.Uppercase);
        var id = PipelineEditing.OrderedNodes(withNode)[1];
        Assert.Throws<ArgumentException>(() => PipelineEditing.Move(withNode, id, -1));
        Assert.Throws<ArgumentException>(() => PipelineEditing.Move(withNode, id, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PipelineEditing.Move(withNode, id, 2));
        Assert.Throws<ArgumentException>(() => PipelineEditing.ChangeOperation(withNode, "output", NodeTypes.Reverse));
    }

    [Fact]
    public void NodeLimitIsEnforcedDuringEditing()
    {
        var definition = Empty();
        for (var i = 0; i < 254; i++) definition = PipelineEditing.InsertAfter(definition, "input", NodeTypes.Uppercase);
        Assert.Throws<ArgumentException>(() => PipelineEditing.InsertAfter(definition, "input", NodeTypes.Uppercase));
        Assert.Equal(256, definition.Nodes.Count);
    }
}
