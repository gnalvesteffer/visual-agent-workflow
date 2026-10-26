using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public class WorkflowEditingTests
{
    private static WorkflowDefinition Empty() => new("1.0", "editing", "input",
        new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0"), ["output"] = new(NodeTypes.Output, "1.0.0") }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition> { ["direct"] = new(new("input", "out"), new("output", "in"), "data") }.ToImmutableSortedDictionary());

    [Fact]
    public void InsertMoveRemovePreserveStableNodeAndUnaffectedConnectionIds()
    {
        var first = WorkflowEditing.InsertAfter(Empty(), "input", NodeTypes.Uppercase);
        var originalJson = WorkflowJson.Serialize(first);
        var firstId = WorkflowEditing.OrderedNodes(first)[1];
        var firstLink = first.Connections.Single(e => e.Value.Source.NodeId == "input");
        var second = WorkflowEditing.InsertAfter(first, firstId, NodeTypes.Reverse);
        var secondId = WorkflowEditing.OrderedNodes(second)[2];
        Assert.Equal(firstLink.Value, second.Connections[firstLink.Key]);
        var moved = WorkflowEditing.Move(second, secondId, -1);
        Assert.Equal(new[] { "input", secondId, firstId, "output" }, WorkflowEditing.OrderedNodes(moved));
        Assert.Equal(NodeTypes.Uppercase, moved.Nodes[firstId].Type);
        var removed = WorkflowEditing.Remove(moved, secondId);
        Assert.Equal(new[] { "input", firstId, "output" }, WorkflowEditing.OrderedNodes(removed));
        Assert.Equal(originalJson, WorkflowJson.Serialize(first));
        Assert.Equal(3, first.Nodes.Count);
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void OperationChangeKeepsNodeAndConnectionIdentityAndAppearsInDiff()
    {
        var initial = WorkflowEditing.InsertAfter(Empty(), "input", NodeTypes.Uppercase);
        var id = WorkflowEditing.OrderedNodes(initial)[1];
        var changed = WorkflowEditing.ChangeOperation(initial, id, NodeTypes.Reverse);
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
            "test-provider", "Research the question.", []);
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
        Assert.Throws<ArgumentException>(() => WorkflowEditing.Remove(definition, "input"));
        Assert.Throws<ArgumentException>(() => WorkflowEditing.InsertAfter(definition, "output", NodeTypes.Uppercase));
        Assert.Throws<ArgumentException>(() => WorkflowEditing.InsertAfter(definition, "input", "arbitrary.code"));
        var withNode = WorkflowEditing.InsertAfter(definition, "input", NodeTypes.Uppercase);
        var id = WorkflowEditing.OrderedNodes(withNode)[1];
        Assert.Throws<ArgumentException>(() => WorkflowEditing.Move(withNode, id, -1));
        Assert.Throws<ArgumentException>(() => WorkflowEditing.Move(withNode, id, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkflowEditing.Move(withNode, id, 2));
        Assert.Throws<ArgumentException>(() => WorkflowEditing.ChangeOperation(withNode, "output", NodeTypes.Reverse));
    }

    [Fact]
    public void NodeLimitIsEnforcedDuringEditing()
    {
        var definition = Empty();
        for (var i = 0; i < 254; i++) definition = WorkflowEditing.InsertAfter(definition, "input", NodeTypes.Uppercase);
        Assert.Throws<ArgumentException>(() => WorkflowEditing.InsertAfter(definition, "input", NodeTypes.Uppercase));
        Assert.Equal(256, definition.Nodes.Count);
    }
}
