using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Typed For Each node contract (Decision 0070, 2026-11-24): a dedicated iterator over a bounded <c>array&lt;T&gt;</c>
/// with typed item and zero-based integer index outputs and explicit body/completed paths. It reuses the Loop's
/// pin roles and body anchor; validation here is exercised before any runtime exists.
/// </summary>
public sealed class ForEachNodeTests
{
    private static NodeDefinition ForEach(string? elementType = WorkflowDataTypes.String) =>
        new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = elementType };

    private static ConnectionDefinition Execution(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "execution");

    private static ConnectionDefinition Data(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "data");

    /// <summary>A valid workflow: a string array drives a body that reverses each item and returns to the iterator.</summary>
    private static WorkflowDefinition Workflow(Func<ImmutableSortedDictionary<string, NodeDefinition>.Builder, ImmutableSortedDictionary<string, NodeDefinition>.Builder>? nodes = null,
        Func<ImmutableSortedDictionary<string, ConnectionDefinition>, ImmutableSortedDictionary<string, ConnectionDefinition>>? data = null,
        Func<ImmutableSortedDictionary<string, ConnectionDefinition>, ImmutableSortedDictionary<string, ConnectionDefinition>>? execution = null)
    {
        var nodeBuilder = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodeBuilder["input"] = new(NodeTypes.Input, "1.0.0");
        nodeBuilder["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\"]") };
        nodeBuilder["each"] = ForEach();
        nodeBuilder["body"] = new(NodeTypes.LoopBody, "1.0.0");
        nodeBuilder["step"] = new(NodeTypes.SequenceReverse, "1.0.0");
        nodeBuilder["output"] = new(NodeTypes.Output, "1.0.0");
        nodes?.Invoke(nodeBuilder);
        var dataEdges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("items-each", Data("items", "out", "each", "array"))
            .Add("item-step", Data("each", "item", "step", "in"))
            .Add("result", Data("step", "out", "output", "in"));
        var executionEdges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("start", Execution("input", "then", "each", "execute"))
            .Add("body-start", Execution("each", "body", "body", "execute"))
            .Add("body-step", Execution("body", "then", "step", "execute"))
            .Add("return", Execution("step", "then", "each", "feedback"))
            .Add("done", Execution("each", "completed", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-workflow", "input", nodeBuilder.ToImmutable(),
            data?.Invoke(dataEdges) ?? dataEdges)
        {
            ExecutionConnections = execution?.Invoke(executionEdges) ?? executionEdges
        };
    }

    [Fact]
    public void The_contract_catalog_declares_a_dynamic_element_typed_flow_node()
    {
        Assert.True(WorkflowNodeContractCatalog.TryGet(NodeTypes.ForEach, out var contract));
        Assert.Equal("1.0.0", contract.TypeVersion);
        Assert.True(contract.HasDynamicPins);
        Assert.Equal(WorkflowDynamicPinIdentityKind.TypedArrayElementType, contract.DynamicPinIdentity);
    }

    [Theory]
    [InlineData(WorkflowDataTypes.String)]
    [InlineData(WorkflowDataTypes.Integer)]
    [InlineData(WorkflowDataTypes.Boolean)]
    [InlineData(WorkflowDataTypes.Json)]
    public void The_descriptor_types_array_item_and_index_pins_from_the_element_type(string elementType)
    {
        Assert.True(WorkflowNodeDescriptorResolver.TryResolve(ForEach(elementType), null, null, out var descriptor));

        PortOf(descriptor, "array", WorkflowPortDirection.Input, WorkflowPortKind.Data, out var array);
        PortOf(descriptor, "item", WorkflowPortDirection.Output, WorkflowPortKind.Data, out var item);
        PortOf(descriptor, "index", WorkflowPortDirection.Output, WorkflowPortKind.Data, out var index);
        Assert.Equal(WorkflowDataTypes.ArrayOf(elementType), array.DataType);
        Assert.Equal(elementType, item.DataType);
        Assert.Equal(WorkflowDataTypes.Integer, index.DataType);
        Assert.True(array.Required);
        Assert.Equal(WorkflowPortCardinality.ExactlyOne, array.Cardinality);
    }

    [Fact]
    public void The_descriptor_reuses_the_loop_execution_roles()
    {
        Assert.True(WorkflowNodeDescriptorResolver.TryResolve(ForEach(), null, null, out var descriptor));

        PortOf(descriptor, "execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution, out var execute);
        PortOf(descriptor, "feedback", WorkflowPortDirection.Input, WorkflowPortKind.Execution, out var feedback);
        PortOf(descriptor, "body", WorkflowPortDirection.Output, WorkflowPortKind.Execution, out var body);
        PortOf(descriptor, "completed", WorkflowPortDirection.Output, WorkflowPortKind.Execution, out var completed);
        Assert.Equal(WorkflowExecutionPinRole.LoopInitialInput, execute.ExecutionRole);
        Assert.Equal(WorkflowExecutionPinRole.LoopFeedbackInput, feedback.ExecutionRole);
        Assert.Equal(WorkflowExecutionPinRole.LoopBodyOutput, body.ExecutionRole);
        Assert.Equal(WorkflowExecutionPinRole.LoopDoneOutput, completed.ExecutionRole);
    }

    [Fact]
    public void An_unsupported_element_type_has_no_descriptor()
    {
        Assert.False(WorkflowNodeDescriptorResolver.TryResolve(ForEach("banana"), null, null, out _));
    }

    [Fact]
    public void A_valid_for_each_workflow_validates_without_issues() =>
        Assert.Empty(WorkflowValidator.Validate(Workflow()));

    [Fact]
    public void A_for_each_whose_body_does_not_return_to_its_feedback_pin_is_rejected()
    {
        var definition = Workflow(execution: edges => edges.Remove("return"));

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code.StartsWith("loop.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_for_each_array_input_must_be_connected_and_match_its_element_type()
    {
        var disconnected = Workflow(data: edges => edges.Remove("items-each"));
        Assert.Contains(WorkflowValidator.Validate(disconnected), issue => issue.Path.Contains("each", StringComparison.Ordinal));

        var wrongElement = Workflow(nodes: nodes =>
        {
            nodes["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.Integer, "[1,2]") };
            return nodes;
        });
        Assert.NotEmpty(WorkflowValidator.Validate(wrongElement));
    }

    [Fact]
    public void A_for_each_with_an_invalid_element_type_is_rejected()
    {
        var definition = Workflow(nodes: nodes =>
        {
            nodes["each"] = ForEach("banana");
            return nodes;
        });

        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void A_for_each_is_not_accepted_in_a_legacy_schema()
    {
        var definition = Workflow() with { SchemaVersion = WorkflowSchemaVersions.SequenceGraph };

        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void A_for_each_workflow_round_trips_through_json_with_a_stable_hash()
    {
        var definition = Workflow();

        var json = WorkflowJson.Serialize(definition);
        var parsed = WorkflowJson.Deserialize(json);

        Assert.Equal(json, WorkflowJson.Serialize(parsed));
        Assert.Equal(WorkflowRevision.Create(definition, "test").ContentHash, WorkflowRevision.Create(parsed, "test").ContentHash);
        Assert.Equal(WorkflowDataTypes.String, parsed.Nodes["each"].ArrayElementType);
    }

    [Fact]
    public void An_unknown_node_type_fails_closed_as_an_older_reader_would_see_a_for_each()
    {
        var definition = Workflow(nodes: nodes =>
        {
            nodes["each"] = new("core.for-each.from-the-future", "1.0.0") { ArrayElementType = WorkflowDataTypes.String };
            return nodes;
        });

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "node.unsupported");
    }

    [Fact]
    public void A_for_each_inside_a_sequence_path_is_rejected_in_the_first_slice()
    {
        var definition = Workflow(nodes: nodes =>
        {
            nodes["sequence"] = new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new([new("first", "First"), new("second", "Second")])
            };
            return nodes;
        }, execution: edges => edges.Remove("start")
            .Add("start", Execution("input", "then", "sequence", "execute"))
            .Add("sequence-each", Execution("sequence", "then.first", "each", "execute"))
            .Add("sequence-output", Execution("sequence", "then.second", "output", "execute")));

        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Node_stepping_is_not_offered_for_a_workflow_containing_a_for_each()
    {
        Assert.False(WorkflowDebugging.SupportsNodeStepping(Workflow()));
    }

    [Fact]
    public void Changing_the_element_type_of_a_for_each_is_an_editing_operation_that_revalidates()
    {
        var definition = Workflow();

        // The connected array<string> source no longer matches array<integer>, so the edit is rejected, not silently kept.
        Assert.ThrowsAny<Exception>(() => GraphEditing.ReplaceNode(definition, "each",
            definition.Nodes["each"] with { ArrayElementType = WorkflowDataTypes.Integer }));
        var same = GraphEditing.ReplaceNode(definition, "each", definition.Nodes["each"] with { ArrayElementType = WorkflowDataTypes.String });
        Assert.Equal(WorkflowDataTypes.String, same.Nodes["each"].ArrayElementType);
    }

    private static void PortOf(WorkflowNodeDescriptor descriptor, string id, WorkflowPortDirection direction,
        WorkflowPortKind kind, out WorkflowPortDescriptor port) =>
        port = Assert.Single(descriptor.Ports, candidate => candidate.Id == id && candidate.Direction == direction &&
            candidate.Kind == kind);
}
