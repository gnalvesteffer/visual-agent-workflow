using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ConditionalGraphTests
{
    private static WorkflowDefinition Baseline() => new("1.0", "conditional-test", "input",
        new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0"), ["output"] = new(NodeTypes.Output, "1.0.0") }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition> { ["edge"] = new(new("input", "out"), new("output", "in"), "data") }.ToImmutableSortedDictionary());
    private static WorkflowDefinition Graph() => GraphEditing.InsertConditionalAfter(Baseline(), "input", "out");

    [Fact]
    public void ConditionalGraphRoundTripsAndPackagesWithoutChangingLegacyHashes()
    {
        var baseline = Baseline();
        var json = WorkflowJson.Serialize(baseline);
        Assert.DoesNotContain("\"condition\":", json);
        var graph = Graph();
        Assert.Equal("1.4", graph.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(graph));
        Assert.Equal(4, WorkflowGraph.OrderedNodes(graph).Length);
        Assert.Equal(2, graph.Nodes.Values.Count(node => node.Type is NodeTypes.Condition or NodeTypes.Merge));
        Assert.DoesNotContain(graph.Nodes.Values, node => node.Type is NodeTypes.Uppercase or NodeTypes.Reverse);
        var layout = WorkflowLayout.Create(graph);
        var package = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(graph, layout));
        Assert.Equal(WorkflowJson.ComputeHash(graph), package.DefinitionHash);
        Assert.Equal(json, WorkflowJson.Serialize(WorkflowJson.Deserialize(json)));
        Assert.Throws<ArgumentException>(() => WorkflowEditing.OrderedNodes(graph));
        Assert.Throws<ArgumentException>(() => WorkflowEditing.InsertAfter(graph, "input", NodeTypes.Uppercase));
    }

    [Theory]
    [InlineData("equals", "yes", "yes", true)]
    [InlineData("equals", "yes", "YES", false)]
    [InlineData("contains", "yes", "say yes now", true)]
    [InlineData("starts-with", "yes", "say yes", false)]
    [InlineData("is-empty", "", "", true)]
    [InlineData("is-empty", "", " ", false)]
    public void ConditionsUseExplicitOrdinalSemantics(string operation, string operand, string text, bool expected) =>
        Assert.Equal(expected, new StringCondition(operation, operand).Evaluate(text));

    [Fact]
    public void OldSchemasAndExecutableExpressionsCannotEnableRouting()
    {
        var graph = Graph();
        Assert.Contains(WorkflowValidator.Validate(graph with { SchemaVersion = "1.3" }), i => i.Code == "node.schema");
        var id = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        foreach (var condition in new[] { new StringCondition("csharp", "File.Delete(path)"), new("contains", new string('a', 1025)), new("is-empty", "unexpected") })
            Assert.Contains(WorkflowValidator.Validate(graph with { Nodes = graph.Nodes.SetItem(id, graph.Nodes[id] with { Condition = condition }) }), i => i.Code == "condition.configuration");
        Assert.Contains(WorkflowValidator.Validate(Baseline() with { Nodes = Baseline().Nodes.SetItem("input", new(NodeTypes.Input, "1.0.0") { Condition = new("equals", "x") }) }), i => i.Code == "condition.unexpected");
    }

    [Fact]
    public void MissingArmsDuplicatePortsCyclesAndImplicitJoinsAreRejected()
    {
        var graph = Graph();
        var branch = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var merge = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Merge).Key;
        var edge = graph.Connections.Single(e => e.Value.Source.NodeId == branch && e.Value.Source.PortId == "false");
        Assert.Contains(WorkflowValidator.Validate(graph with { Connections = graph.Connections.Remove(edge.Key) }), i => i.Code == "graph.degree");
        var duplicatePort = edge.Value with { Source = new(branch, "true") };
        Assert.Contains(WorkflowValidator.Validate(graph with { Connections = graph.Connections.SetItem(edge.Key, duplicatePort) }), i => i.Code == "graph.degree");
        Assert.Contains(WorkflowValidator.Validate(graph with { Nodes = graph.Nodes.SetItem(merge, new(NodeTypes.Reverse, "1.0.0")) }), i => i.Code == "graph.degree");
        var back = graph.Connections.Single(e => e.Value.Source.NodeId == merge);
        Assert.Contains(WorkflowValidator.Validate(graph with { Connections = graph.Connections.SetItem(back.Key, back.Value with { Target = new(branch, "in") }) }), i => i.Code == "graph.cycle");
    }

    [Fact]
    public void ConditionArmsMayTerminateAtOutputAndMergeCanBeRemovedToSplitPaths()
    {
        var graph = Graph();
        var branch = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var merge = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Merge).Key;

        var split = GraphEditing.RemoveNode(graph, merge);
        split = GraphEditing.ConnectPins(split, branch, "true", "output", "in");
        split = GraphEditing.ConnectPins(split, branch, "false", "output", "in");

        Assert.Empty(WorkflowValidator.Validate(split));
        Assert.Equal(2, split.Connections.Values.Count(edge => edge.Target.NodeId == "output"));
        Assert.DoesNotContain(split.Nodes, node => node.Value.Type == NodeTypes.Merge);
    }

    [Fact]
    public void LocalEditsPreserveOtherArmIdentitiesAndNeverFlattenRouting()
    {
        var graph = Graph();
        var branch = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var originalFalse = graph.Connections.Single(e => e.Value.Source.NodeId == branch && e.Value.Source.PortId == "false");
        var inserted = GraphEditing.InsertNodeAfter(graph, branch, "true", new(NodeTypes.Reverse, "1.0.0"));
        Assert.Equal(originalFalse.Value, inserted.Connections[originalFalse.Key]);
        var added = inserted.Nodes.Keys.Except(graph.Nodes.Keys).Single();
        var removed = GraphEditing.RemoveNode(inserted, added);
        Assert.Equal(WorkflowJson.Serialize(graph), WorkflowJson.Serialize(removed));
        var nested = GraphEditing.InsertConditionalAfter(graph, branch, "true");
        Assert.Empty(WorkflowValidator.Validate(nested));
        Assert.Equal(2, nested.Nodes.Values.Count(n => n.Type == NodeTypes.Condition));
        Assert.Throws<ArgumentException>(() => GraphEditing.RemoveNode(graph, branch));
    }

    [Fact]
    public void DisconnectedDraftCanBeRewiredAndValidationClearsOnlyWhenGraphIsWhole()
    {
        var graph = Baseline();
        var disconnected = GraphEditing.Disconnect(graph, "edge");
        Assert.NotEmpty(WorkflowValidator.Validate(disconnected));
        Assert.NotEqual(WorkflowJson.ComputeDraftHash(graph), WorkflowJson.ComputeDraftHash(disconnected));
        Assert.Throws<ArgumentException>(() => WorkflowJson.ComputeHash(disconnected));
        Assert.Throws<ArgumentException>(() => WorkflowRevision.Create(disconnected, "test"));

        Assert.Equal(WorkflowJson.ComputeHash(graph), WorkflowJson.ComputeDraftHash(graph));

        var reconnected = GraphEditing.ConnectPins(disconnected, "input", "out", "output", "in");

        Assert.Empty(WorkflowValidator.Validate(reconnected));
        Assert.Equal("output", reconnected.Connections.Values.Single().Target.NodeId);
    }

    [Fact]
    public void DraftJsonRoundTripsIncompleteTopologyButStrictWorkflowCodecRejectsIt()
    {
        var disconnected = GraphEditing.Disconnect(Baseline(), "edge");
        var json = WorkflowJson.SerializeDraft(disconnected);

        Assert.Equal(WorkflowJson.ComputeDraftHash(disconnected), WorkflowJson.ComputeDraftHash(WorkflowJson.DeserializeDraft(json)));
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(disconnected));
        Assert.Throws<ArgumentException>(() => WorkflowJson.Deserialize(json));
    }

    [Fact]
    public void UnconnectedNodeCanBeRemovedWhileDraftTopologyIsInvalid()
    {
        var baseline = Baseline();
        var draft = GraphEditing.AddUnconnectedNode(baseline, new(NodeTypes.Reverse, "1.0.0"));
        var addedId = draft.Nodes.Keys.Single(id => !baseline.Nodes.ContainsKey(id));

        Assert.NotEmpty(WorkflowValidator.Validate(draft));
        var cleaned = GraphEditing.RemoveNode(draft, addedId);

        Assert.Equal(WorkflowJson.ComputeDraftHash(baseline), WorkflowJson.ComputeDraftHash(cleaned));
        Assert.Empty(WorkflowValidator.Validate(cleaned));
    }

    [Fact]
    public void UnconnectedProcessingNodeCanBeConfiguredWhileDraftTopologyIsInvalid()
    {
        var draft = GraphEditing.AddUnconnectedNode(Baseline(), new(NodeTypes.Uppercase, "1.0.0"));
        var addedId = draft.Nodes.Keys.Single(id => id != "input" && id != "step" && id != "output");

        var configured = WorkflowEditing.ChangeOperation(draft, addedId, NodeTypes.Reverse);

        Assert.Equal(NodeTypes.Reverse, configured.Nodes[addedId].Type);
        Assert.Contains(WorkflowValidator.Validate(configured), issue => issue.Code is "workflow.degree" or "graph.degree");
        var withParameter = WorkflowParameterEditing.Add(configured, "customer", "Customer", "Ada");
        Assert.Equal("Ada", withParameter.Parameters!["customer"].DefaultValue);
        Assert.Equal(WorkflowJson.ComputeDraftHash(withParameter),
            WorkflowJson.ComputeDraftHash(WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(withParameter))));
    }

    [Fact]
    public void TerminationNodeCanBeDisconnectedConfiguredAndReconnectedAsAnInvalidDraft()
    {
        var graph = Graph();
        var branch = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Condition).Key;
        var merge = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Merge).Key;
        var split = GraphEditing.RemoveNode(graph, merge);
        var withTermination = GraphEditing.AddUnconnectedNode(split, new(NodeTypes.Terminate, "1.0.0"));
        var termination = withTermination.Nodes.Single(node => node.Value.Type == NodeTypes.Terminate).Key;
        var connected = GraphEditing.ConnectPins(withTermination, branch, "true", termination, "in");
        var edge = connected.Connections.Single(connection => connection.Value.Target.NodeId == termination);

        var disconnected = GraphEditing.Disconnect(connected, edge.Key);
        var configuredWhileDisconnected = WorkflowTerminationEditing.SetOutcome(disconnected, termination, WorkflowTerminationOutcomes.Failure);
        Assert.Contains(WorkflowValidator.Validate(configuredWhileDisconnected), issue => issue.Code == "graph.degree");
        Assert.Equal(WorkflowJson.ComputeDraftHash(configuredWhileDisconnected),
            WorkflowJson.ComputeDraftHash(WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(configuredWhileDisconnected))));

        var reconnected = GraphEditing.ConnectPins(configuredWhileDisconnected, edge.Value.Source.NodeId, edge.Value.Source.PortId,
            edge.Value.Target.NodeId, edge.Value.Target.PortId);
        reconnected = GraphEditing.ConnectPins(reconnected, branch, "false", "output", "in");
        Assert.Empty(WorkflowValidator.Validate(reconnected));
        Assert.Equal(WorkflowTerminationOutcomes.Failure, reconnected.Nodes[termination].Termination?.Outcome);
    }

    [Fact]
    public void AddingConditionCreatesOneUnconnectedNodeAndLeavesAnEditableInvalidDraft()
    {
        var before = Baseline();
        var after = GraphEditing.AddUnconnectedNode(before,
            new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "yes") });

        Assert.Equal(before.Connections, after.Connections);
        var condition = Assert.Single(after.Nodes, entry => entry.Value.Type == NodeTypes.Condition);
        Assert.Equal(WorkflowJson.ComputeDraftHash(after),
            WorkflowJson.ComputeDraftHash(WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(after))));
        Assert.Contains(WorkflowValidator.Validate(after), issue => issue.Code == "graph.degree");
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(after));

        var connected = GraphEditing.ConnectPins(after, "input", "out", condition.Key, "in");
        connected = GraphEditing.ConnectPins(connected, condition.Key, "true", "output", "in");
        connected = GraphEditing.ConnectPins(connected, condition.Key, "false", "output", "in");
        WorkflowValidator.ThrowIfInvalid(connected);
        Assert.Single(connected.Nodes, entry => entry.Value.Type == NodeTypes.Condition);
        Assert.DoesNotContain(connected.Nodes, entry => entry.Value.Type == NodeTypes.Merge);
    }

    [Fact]
    public void MultipleTerminationNodesCanRepresentSeparateWorkflowPaths()
    {
        var graph = Graph();
        var branch = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Condition).Key;
        var merge = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Merge).Key;
        var split = GraphEditing.RemoveNode(graph, merge);
        var withTrueExit = GraphEditing.AddUnconnectedNode(split, new(NodeTypes.Terminate, "1.0.0"));
        var withFalseExit = GraphEditing.AddUnconnectedNode(withTrueExit, new(NodeTypes.Terminate, "1.0.0"));
        var trueExitId = withTrueExit.Nodes.Single(node => node.Value.Type == NodeTypes.Terminate).Key;
        var falseExitId = withFalseExit.Nodes.Single(node => node.Value.Type == NodeTypes.Terminate && node.Key != trueExitId).Key;
        var withTwoExits = GraphEditing.ConnectPins(withFalseExit, branch, "true", trueExitId, "in");
        withTwoExits = GraphEditing.ConnectPins(withTwoExits, branch, "false", falseExitId, "in");
        var falseExit = withTwoExits.Nodes.Single(node => node.Value.Type == NodeTypes.Terminate &&
            withTwoExits.Connections.Values.Any(edge => edge.Target.NodeId == node.Key && edge.Source.PortId == "false")).Key;
        var configured = WorkflowTerminationEditing.SetOutcome(withTwoExits, falseExit, WorkflowTerminationOutcomes.Failure);

        Assert.Empty(WorkflowValidator.Validate(configured));
        Assert.Equal(2, configured.Nodes.Values.Count(node => node.Type == NodeTypes.Terminate));
    }
}
