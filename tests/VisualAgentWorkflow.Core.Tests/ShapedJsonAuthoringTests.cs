using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>Pure Studio edit helpers for the shaped json Input and core.object.get (Decision 0082 authoring slice).</summary>
public sealed class ShapedJsonAuthoringTests
{
    private static WorkflowDefinition Graph(JsonObjectShape? shape = null, string inputType = "json", bool withReader = false, string readerField = "name")
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0") { InputDataType = inputType, InputShape = shape })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = withReader ? null : "json" });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty;
        if (!withReader) data = data.Add("c0", new(new("input", "out"), new("output", "in"), "data"));
        if (withReader)
        {
            nodes = nodes.Add("get", new(NodeTypes.ObjectGet, "1.0.0") { ObjectGet = new(readerField, "string") });
            data = data.Add("c1", new(new("input", "out"), new("get", "in"), "data"))
                .Add("c2", new(new("get", "out"), new("output", "in"), "data"));
        }
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("x", new(new("input", "then"), new("output", "execute"), "execution"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "shape", "input", nodes, data) { ExecutionConnections = execution };
    }

    private static JsonObjectShape TwoFields => new([new("name", "Name", "string", true), new("age", "Age", "integer", false)]);

    private static ImmutableArray<JsonObjectFieldShape> Fields(WorkflowDefinition definition) => definition.Nodes["input"].InputShape!.Fields;

    [Fact]
    public void Adding_the_first_field_creates_a_shape_with_a_generated_valid_id()
    {
        var edited = GraphEditing.AddInputShapeField(Graph(), "input");
        var field = Assert.Single(Fields(edited));
        Assert.True(JsonObjectShapes.IsValidFieldId(field.Id));
        Assert.Equal("string", field.Type);
        Assert.True(field.Required);
        Assert.False(string.IsNullOrWhiteSpace(field.Name));
        Assert.Null(JsonObjectShapes.Validate(edited.Nodes["input"].InputShape));
    }

    [Fact]
    public void Generated_ids_are_unique_and_stable_across_other_edits()
    {
        var edited = Graph();
        for (var i = 0; i < 5; i++) edited = GraphEditing.AddInputShapeField(edited, "input");
        Assert.Equal(5, Fields(edited).Select(field => field.Id).Distinct().Count());
        var ids = Fields(edited).Select(field => field.Id).ToArray();
        edited = GraphEditing.MoveInputShapeField(edited, "input", ids[0], 1);
        edited = GraphEditing.RenameInputShapeField(edited, "input", ids[1], "Renamed");
        Assert.Equal(ids.Order(), Fields(edited).Select(field => field.Id).Order());
    }

    [Fact]
    public void A_shape_is_limited_to_thirty_two_fields()
    {
        var edited = Graph();
        for (var i = 0; i < JsonObjectShapeLimits.MaximumFields; i++) edited = GraphEditing.AddInputShapeField(edited, "input");
        Assert.Equal(32, Fields(edited).Length);
        Assert.Throws<ArgumentException>(() => GraphEditing.AddInputShapeField(edited, "input"));
    }

    [Fact]
    public void Renaming_the_display_name_keeps_the_id_the_reader_wiring_and_validity()
    {
        var start = Graph(TwoFields, withReader: true);
        var edited = GraphEditing.RenameInputShapeField(start, "input", "name", "  Customer name  ");
        Assert.Equal("Customer name", Fields(edited)[0].Name);
        Assert.Equal("name", Fields(edited)[0].Id);
        Assert.Equal(start.Connections, edited.Connections);
        Assert.Equal("name", edited.Nodes["get"].ObjectGet!.FieldId);
        Assert.Empty(WorkflowValidator.Validate(edited).Where(issue => issue.Code.StartsWith("object.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_display_name_is_rejected(string name) =>
        Assert.ThrowsAny<Exception>(() => GraphEditing.RenameInputShapeField(Graph(TwoFields), "input", "name", name));

    [Fact]
    public void Moving_reorders_fields_without_changing_ids_and_rejects_the_ends()
    {
        var edited = GraphEditing.MoveInputShapeField(Graph(TwoFields), "input", "name", 1);
        Assert.Equal(["age", "name"], Fields(edited).Select(field => field.Id));
        Assert.Throws<ArgumentException>(() => GraphEditing.MoveInputShapeField(edited, "input", "name", 1));
        Assert.Throws<ArgumentException>(() => GraphEditing.MoveInputShapeField(edited, "input", "age", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphEditing.MoveInputShapeField(edited, "input", "age", 2));
    }

    [Fact]
    public void Type_and_required_edits_apply_to_one_field()
    {
        var edited = GraphEditing.SetInputShapeFieldType(Graph(TwoFields), "input", "age", "number");
        edited = GraphEditing.SetInputShapeFieldRequired(edited, "input", "age", true);
        Assert.Equal(new JsonObjectFieldShape("age", "Age", "number", true), Fields(edited)[1]);
        Assert.Equal(TwoFields.Fields[0], Fields(edited)[0]);
        Assert.Throws<ArgumentException>(() => GraphEditing.SetInputShapeFieldType(edited, "input", "age", "array<string>"));
        Assert.Throws<ArgumentException>(() => GraphEditing.SetInputShapeFieldType(edited, "input", "age", "json"));
    }

    [Fact]
    public void Removing_the_last_field_removes_the_shape_and_leaves_a_plain_json_input()
    {
        var edited = GraphEditing.RemoveInputShapeField(Graph(new([new("name", "Name", "string", true)])), "input", "name");
        Assert.Null(edited.Nodes["input"].InputShape);
        Assert.Equal("json", edited.Nodes["input"].InputDataType);
        Assert.Empty(WorkflowValidator.Validate(edited));
    }

    [Fact]
    public void A_field_read_by_an_object_get_cannot_be_removed_or_retyped_but_can_be_renamed_required_toggled_and_moved()
    {
        var start = Graph(TwoFields, withReader: true);
        Assert.Throws<ArgumentException>(() => GraphEditing.RemoveInputShapeField(start, "input", "name"));
        Assert.Throws<ArgumentException>(() => GraphEditing.SetInputShapeFieldType(start, "input", "name", "integer"));
        Assert.Same(start.Nodes["get"], GraphEditing.SetInputShapeFieldType(start, "input", "name", "string").Nodes["get"]);
        GraphEditing.SetInputShapeFieldRequired(start, "input", "name", false);
        GraphEditing.MoveInputShapeField(start, "input", "name", 1);
        GraphEditing.RemoveInputShapeField(start, "input", "age");
    }

    [Theory]
    [InlineData("string")]
    [InlineData("array<string>")]
    public void Shape_edits_require_a_json_input(string type) =>
        Assert.Throws<ArgumentException>(() => GraphEditing.AddInputShapeField(Graph(inputType: type), "input"));

    [Fact]
    public void Shape_edits_reject_unknown_nodes_non_inputs_and_unknown_fields()
    {
        var graph = Graph(TwoFields);
        Assert.Throws<ArgumentException>(() => GraphEditing.AddInputShapeField(graph, "missing"));
        Assert.Throws<ArgumentException>(() => GraphEditing.AddInputShapeField(graph, "output"));
        Assert.Throws<ArgumentException>(() => GraphEditing.RenameInputShapeField(graph, "input", "nope", "x"));
    }

    [Fact]
    public void Edited_shapes_survive_draft_serialization_round_trip()
    {
        var edited = GraphEditing.AddInputShapeField(Graph(TwoFields), "input");
        var round = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(edited));
        Assert.True(Fields(edited).SequenceEqual(round.Nodes["input"].InputShape!.Fields));
    }

    [Fact]
    public void Object_get_choices_come_from_the_directly_connected_shaped_input()
    {
        var connected = Graph(TwoFields, withReader: true);
        Assert.Equal(["name", "age"], GraphEditing.ObjectGetFieldChoices(connected, "get").Select(field => field.Id));
        var disconnected = connected with { Connections = connected.Connections.Remove("c1") };
        Assert.Empty(GraphEditing.ObjectGetFieldChoices(disconnected, "get"));
        Assert.Empty(GraphEditing.ObjectGetFieldChoices(Graph(null, withReader: true), "get"));
    }

    [Fact]
    public void Selecting_an_object_get_field_derives_the_pin_type_from_the_shape()
    {
        var edited = GraphEditing.SetObjectGetField(Graph(TwoFields, withReader: true), "get", "age");
        Assert.Equal(new ObjectGetConfiguration("age", "integer"), edited.Nodes["get"].ObjectGet);
        Assert.Equal("integer", WorkflowPorts.Explicit(edited.Nodes["get"]).Single(port => port.Id == "out").DataType);
    }

    [Fact]
    public void Selecting_an_object_get_field_rejects_unconnected_nodes_and_unknown_fields()
    {
        var connected = Graph(TwoFields, withReader: true);
        Assert.Throws<ArgumentException>(() => GraphEditing.SetObjectGetField(connected, "get", "nope"));
        Assert.Throws<ArgumentException>(() => GraphEditing.SetObjectGetField(connected, "input", "name"));
        var disconnected = connected with { Connections = connected.Connections.Remove("c1") };
        Assert.Throws<ArgumentException>(() => GraphEditing.SetObjectGetField(disconnected, "get", "name"));
    }

    [Fact]
    public void An_unwired_palette_object_get_node_can_be_added_and_a_field_edit_on_it_is_a_draft_edit()
    {
        var graph = Graph(TwoFields);
        var added = GraphEditing.AddUnconnectedNode(graph, new(NodeTypes.ObjectGet, "1.0.0") { ObjectGet = new("field", WorkflowDataTypes.String) });
        var id = added.Nodes.Keys.Except(graph.Nodes.Keys).Single();
        Assert.NotNull(WorkflowJson.SerializeDraft(added));
        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, added.SchemaVersion);
        var wired = GraphEditing.ConnectPins(added, "input", "out", id, "in");
        var picked = GraphEditing.SetObjectGetField(wired, id, "name");
        Assert.DoesNotContain(WorkflowValidator.Validate(picked), issue => issue.Code.StartsWith("object.get", StringComparison.Ordinal));
    }
}
