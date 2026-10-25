using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowLayoutTests
{
    [Fact]
    public void LayoutRoundTripsWithoutChangingExecutableSerialization()
    {
        var definition = Definition(true);
        var original = WorkflowJson.Serialize(definition);
        var layout = WorkflowLayout.Create(definition);
        layout = layout with
        {
            Nodes = layout.Nodes.SetItem("step-1", new(new(512, 192), "Normalize", "Keep the original input in audit history.")),
            Wires = layout.Wires.Add(definition.Connections.Keys.First(), new("curve", [new(new(420, 64), 45)])),
            Sections = layout.Sections.Add("preparation", new("Prepare text", ["step-2", "step-1"]))
        };
        var json = WorkflowLayoutJson.Serialize(layout, definition);
        var loaded = WorkflowLayoutJson.Deserialize(json, definition);
        Assert.Equal(json, WorkflowLayoutJson.Serialize(loaded, definition));
        Assert.Equal(WorkflowLayoutJson.ComputeHash(layout, definition), WorkflowLayoutJson.ComputeHash(loaded, definition));
        Assert.Equal("Normalize", loaded.Nodes["step-1"].Label);
        Assert.Equal(45, loaded.Wires.Values.First().Points[0].Angle);
        Assert.Equal(original, WorkflowJson.Serialize(definition));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(32769)]
    public void RejectsInvalidCoordinates(double x)
    {
        var definition = Definition();
        var layout = WorkflowLayout.Create(definition);
        layout = layout with { Nodes = layout.Nodes.SetItem("step-1", new(new(x, 100))) };
        Assert.Throws<ArgumentException>(() => WorkflowLayoutJson.Serialize(layout, definition));
    }

    [Fact]
    public void RejectsUnknownReferencesDuplicateMembershipAndInvalidPinnedAngles()
    {
        var definition = Definition();
        var layout = WorkflowLayout.Create(definition);
        Assert.Throws<ArgumentException>(() => WorkflowLayoutJson.Serialize(layout with
            { Nodes = layout.Nodes.Add("missing", new(new(1, 1))) }, definition));
        Assert.Throws<ArgumentException>(() => WorkflowLayoutJson.Serialize(layout with
            { Sections = layout.Sections.Add("a", new("A", ["step-1"])).Add("b", new("B", ["step-1"])) }, definition));
        Assert.Throws<ArgumentException>(() => WorkflowLayoutJson.Serialize(layout with
            { Wires = layout.Wires.Add(definition.Connections.Keys.First(), new("orthogonal", [new(new(100, 100), 45)])) }, definition));
        Assert.Throws<ArgumentException>(() => WorkflowLayoutJson.Serialize(layout with
            { Wires = layout.Wires.Add(definition.Connections.Keys.First(), new("curve", [new(new(100, 100), 360)])) }, definition));
        var json = WorkflowLayoutJson.Serialize(layout, definition).Replace("\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"1.0\", \"schemaVersion\": \"1.0\"");
        Assert.Throws<System.Text.Json.JsonException>(() => WorkflowLayoutJson.Deserialize(json, definition));
    }

    [Fact]
    public void SemanticEditsPreserveSurvivingAnnotationsAndPruneRemovedIdentities()
    {
        var definition = Definition(true);
        var layout = WorkflowLayout.Create(definition);
        layout = layout with
        {
            Nodes = layout.Nodes.SetItem("step-1", new(new(400, 200), "Keep me", "Keep this note")),
            Wires = layout.Wires.Add(definition.Connections.Keys.First(), new("curve", [new(new(100, 100))])),
            Sections = layout.Sections.Add("section", new("Steps", ["step-1", "step-2"]))
        };
        var removed = WorkflowEditing.Remove(definition, "step-2");
        var reconciled = layout.Reconcile(removed);
        WorkflowLayoutJson.Validate(reconciled, removed);
        Assert.Equal(layout.Nodes["step-1"], reconciled.Nodes["step-1"]);
        Assert.DoesNotContain("step-2", reconciled.Nodes.Keys);
        Assert.Equal(new[] { "step-1" }, reconciled.Sections["section"].NodeIds);
        Assert.All(reconciled.Wires.Keys, id => Assert.Contains(id, removed.Connections.Keys));
    }

    [Fact]
    public void ReconcilePositionsNewUnconnectedNodesAwayFromLinearGraphNodes()
    {
        var definition = Definition();
        var layout = WorkflowLayout.Create(definition);
        var expanded = GraphEditing.AddUnconnectedNode(definition, new(NodeTypes.Condition, "1.0.0")
            { Condition = new("contains", "yes") });

        var reconciled = layout.Reconcile(expanded);
        var addedId = expanded.Nodes.Keys.Single(id => !definition.Nodes.ContainsKey(id));
        Assert.All(definition.Nodes.Keys, id =>
        {
            var old = reconciled.Nodes[id].Position;
            var added = reconciled.Nodes[addedId].Position;
            Assert.True(Math.Abs(old.X - added.X) >= 300 || Math.Abs(old.Y - added.Y) >= 650);
        });
        WorkflowLayoutJson.Validate(reconciled, expanded);
    }
    private static WorkflowDefinition Definition(bool twoSteps = false)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["step-1"] = new(NodeTypes.Uppercase, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        if (twoSteps) nodes["step-2"] = new(NodeTypes.Reverse, "1.0.0");
        var links = new Dictionary<string, ConnectionDefinition>
        {
            ["first"] = new(new("input", "out"), new("step-1", "in"), "data"),
            ["last"] = new(new(twoSteps ? "step-2" : "step-1", "out"), new("output", "in"), "data")
        };
        if (twoSteps) links["middle"] = new(new("step-1", "out"), new("step-2", "in"), "data");
        return new("1.0", "layout-test", "input", nodes.ToImmutableSortedDictionary(), links.ToImmutableSortedDictionary());
    }

}
