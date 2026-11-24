using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>Inserting a complete, valid For Each region on one execution edge of a unified schema 2.0 graph.</summary>
public sealed class GraphEditingForEachTests
{
    private static WorkflowDefinition Baseline() =>
        new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-insert", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("data-path", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("execution-path", new(new("input", "then"), new("output", "execute"), "execution"))
        };

    [Fact]
    public void The_inserted_region_validates_without_issues_and_wires_every_required_pin()
    {
        var inserted = GraphEditing.InsertForEachAtExecutionConnection(Baseline(), "execution-path");

        Assert.Empty(WorkflowValidator.Validate(inserted));
        var each = Assert.Single(inserted.Nodes, entry => entry.Value.Type == NodeTypes.ForEach);
        var body = Assert.Single(inserted.Nodes, entry => entry.Value.Type == NodeTypes.LoopBody);
        var array = Assert.Single(inserted.Nodes, entry => entry.Value.Type == NodeTypes.ArrayLiteral);
        Assert.Equal(WorkflowDataTypes.String, each.Value.ArrayElementType);
        Assert.Equal(WorkflowDataTypes.String, array.Value.ArrayLiteral!.ElementType);
        Assert.Equal(Baseline().Nodes.Count + 3, inserted.Nodes.Count);
        Assert.Contains(inserted.Connections.Values, edge => edge.Source == new PortReference(array.Key, "out") &&
            edge.Target == new PortReference(each.Key, "array"));
        var execution = inserted.ExecutionConnections!.Values.ToArray();
        Assert.DoesNotContain(execution, edge => edge.Source.NodeId == "input" && edge.Target.NodeId == "output");
        Assert.Contains(execution, edge => edge.Source == new PortReference("input", "then") && edge.Target == new PortReference(each.Key, "execute"));
        Assert.Contains(execution, edge => edge.Source == new PortReference(each.Key, "body") && edge.Target == new PortReference(body.Key, "execute"));
        Assert.Contains(execution, edge => edge.Source == new PortReference(body.Key, "then") && edge.Target == new PortReference(each.Key, "feedback"));
        Assert.Contains(execution, edge => edge.Source == new PortReference(each.Key, "completed") && edge.Target == new PortReference("output", "execute"));
    }

    [Fact]
    public void The_inserted_region_round_trips_through_json_with_a_stable_hash()
    {
        var inserted = GraphEditing.InsertForEachAtExecutionConnection(Baseline(), "execution-path");

        var json = WorkflowJson.Serialize(inserted);
        var parsed = WorkflowJson.Deserialize(json);

        Assert.Equal(json, WorkflowJson.Serialize(parsed));
        Assert.Equal(WorkflowJson.ComputeHash(inserted), WorkflowJson.ComputeHash(parsed));
    }

    [Fact]
    public void Insertion_is_pure_and_leaves_the_original_definition_unchanged()
    {
        var original = Baseline();
        var snapshot = WorkflowJson.Serialize(original);

        _ = GraphEditing.InsertForEachAtExecutionConnection(original, "execution-path");

        Assert.Equal(snapshot, WorkflowJson.Serialize(original));
        Assert.Equal(2, original.Nodes.Count);
    }

    [Fact]
    public void Insertion_is_rejected_when_the_node_limit_would_be_exceeded()
    {
        var full = Baseline();
        for (var index = 0; index < 254; index++)
            full = full with { Nodes = full.Nodes.Add($"pad-{index:000}", new(NodeTypes.StringConstant, "1.0.0")) };

        Assert.Equal(256, full.Nodes.Count);
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertForEachAtExecutionConnection(full, "execution-path"));
    }

    [Fact]
    public void Insertion_is_rejected_when_the_connection_limit_would_be_exceeded()
    {
        var crowded = Baseline();
        var data = crowded.Connections;
        for (var index = 0; index < 504; index++)
            data = data.Add($"pad-{index:000}", new(new("input", "out"), new("output", "in"), "data"));

        Assert.Throws<ArgumentException>(() =>
            GraphEditing.InsertForEachAtExecutionConnection(crowded with { Connections = data }, "execution-path"));
    }

    [Fact]
    public void Insertion_requires_an_execution_edge_into_an_execute_pin_of_a_unified_graph()
    {
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertForEachAtExecutionConnection(Baseline(), "missing"));
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertForEachAtExecutionConnection(
            Baseline() with { SchemaVersion = WorkflowSchemaVersions.SequenceGraph }, "execution-path"));
    }

    [Fact]
    public void Insertion_rejects_loop_body_anchor_and_feedback_edges()
    {
        var once = GraphEditing.InsertForEachAtExecutionConnection(Baseline(), "execution-path");

        var anchored = once.ExecutionConnections!.Where(edge => edge.Value.Target.PortId == "feedback" ||
            once.Nodes[edge.Value.Target.NodeId].Type == NodeTypes.LoopBody).ToArray();
        Assert.Equal(2, anchored.Length);
        foreach (var edge in anchored)
            Assert.Throws<ArgumentException>(() => GraphEditing.InsertForEachAtExecutionConnection(once, edge.Key));
    }

    [Fact]
    public void Insertion_is_rejected_where_the_loop_validator_would_not_accept_a_root_iterator()
    {
        var withStep = Baseline() with
        {
            Nodes = Baseline().Nodes.Add("step", new(NodeTypes.SequenceReverse, "1.0.0")),
            Connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("in-step", new(new("input", "out"), new("step", "in"), "data"))
                .Add("step-out", new(new("step", "out"), new("output", "in"), "data")),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("first", new(new("input", "then"), new("step", "execute"), "execution"))
                .Add("second", new(new("step", "then"), new("output", "execute"), "execution"))
        };
        Assert.Empty(WorkflowValidator.Validate(withStep));

        Assert.Throws<ArgumentException>(() => GraphEditing.InsertForEachAtExecutionConnection(withStep, "second"));
        Assert.Empty(WorkflowValidator.Validate(GraphEditing.InsertForEachAtExecutionConnection(withStep, "first")));
    }

    [Fact]
    public void A_second_root_region_is_rejected()
    {
        var once = GraphEditing.InsertForEachAtExecutionConnection(Baseline(), "execution-path");
        var completed = once.ExecutionConnections!.Single(edge => edge.Value.Source.PortId == "completed").Key;

        Assert.Throws<ArgumentException>(() => GraphEditing.InsertForEachAtExecutionConnection(once, completed));
    }
}
