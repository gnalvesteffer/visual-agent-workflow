using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceGraphTests
{
    [Fact]
    public void Sequence_exposes_ordered_execution_pins_and_round_trips()
    {
        var definition = Definition();
        var sequence = definition.Nodes["sequence"];

        Assert.Equal(["then.inventory", "then.architecture", "then.quality"], WorkflowPorts.ExplicitExecutionOutputs(sequence).ToArray());
        Assert.Equal("Architecture research", WorkflowPorts.Explicit(sequence).Single(port => port.Id == "then.architecture").DisplayName);

        var restored = JsonSerializer.Deserialize<WorkflowDefinition>(JsonSerializer.Serialize(definition));
        Assert.NotNull(restored);
        Assert.Equal(sequence.Sequence!.Outputs.ToArray(), restored!.Nodes["sequence"].Sequence!.Outputs.ToArray());
        Assert.Empty(WorkflowValidator.Validate(restored));
    }

    [Fact]
    public void Sequence_requires_two_to_four_unique_bounded_outputs()
    {
        var invalid = Definition() with
        {
            Nodes = Definition().Nodes.SetItem("sequence", Definition().Nodes["sequence"] with
            {
                Sequence = new SequenceConfiguration([
                    new("first", "First"),
                    new("first", "Duplicate"),
                    new("third", "Third"),
                    new("fourth", "Fourth"),
                    new("fifth", "Fifth")])
            })
        };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "sequence.configuration");
    }

    [Fact]
    public void Sequence_is_rejected_by_legacy_schema_versions()
    {
        var invalid = Definition() with { SchemaVersion = WorkflowSchemaVersions.DirectMcpPinsGraph };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code is "execution.node.unsupported" or "node.schema");
    }

    [Fact]
    public void Sequence_requires_each_ordered_output_to_be_connected_once()
    {
        var missing = Definition() with { ExecutionConnections = Definition().ExecutionConnections!.Remove("sequence-quality") };

        Assert.Contains(WorkflowValidator.Validate(missing), issue => issue.Code == "graph.degree" && issue.Path.Contains("then.quality", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_editing_preserves_stable_ids_and_wires()
    {
        var renamed = GraphEditing.RenameSequenceOutput(Definition(), "sequence", "architecture", "System architecture");
        var moved = GraphEditing.MoveSequenceOutput(renamed, "sequence", "quality", -1);
        var added = GraphEditing.AddSequenceOutput(moved, "sequence");

        Assert.Equal(["inventory", "quality", "architecture", "output_1"], added.Nodes["sequence"].Sequence!.Outputs.Select(output => output.Id));
        Assert.Contains(added.ExecutionConnections!.Values, edge => edge.Source == new PortReference("sequence", "then.output_1"));
        Assert.Equal("System architecture", added.Nodes["sequence"].Sequence!.Outputs.Single(output => output.Id == "architecture").Label);
        Assert.Empty(WorkflowValidator.Validate(added));

        var removed = GraphEditing.RemoveSequenceOutput(added, "sequence", "output_1");
        Assert.DoesNotContain(removed.ExecutionConnections!.Values, edge => edge.Source == new PortReference("sequence", "then.output_1"));
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void Sequence_path_schema_accepts_disjoint_linear_tool_paths()
    {
        var definition = PathDefinition();

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Sequence_path_promotion_preserves_the_existing_graph_contract()
    {
        var promoted = GraphEditing.EnableSequencePathGraph(Definition());

        Assert.Equal(WorkflowSchemaVersions.SequencePathGraph, promoted.SchemaVersion);
        Assert.Equal(Definition().Nodes["sequence"].Sequence!.Outputs.ToArray(), promoted.Nodes["sequence"].Sequence!.Outputs.ToArray());
        Assert.Equal(Definition().ExecutionConnections!.Select(edge => edge.Value).ToArray(), promoted.ExecutionConnections!.Select(edge => edge.Value).ToArray());
        Assert.Empty(WorkflowValidator.Validate(promoted));
    }

    [Fact]
    public void Sequence_path_schema_rejects_shared_downstream_nodes()
    {
        var definition = PathDefinition() with
        {
            ExecutionConnections = PathDefinition().ExecutionConnections!
                .Remove("sequence-architecture")
                .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("inventory-tool", "execute"), "execution"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "sequence.path.shared");
    }

    private static WorkflowDefinition Definition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([
                    new("inventory", "Inventory research"),
                    new("architecture", "Architecture research"),
                    new("quality", "Quality research")])
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("output", "execute"), "execution"))
            .Add("sequence-quality", new(new("sequence", "then.quality"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceGraph, "sequence", "input", nodes, ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition PathDefinition()
    {
        var definition = Definition();
        var nodes = definition.Nodes
            .Add("inventory-tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.inventory", new string('a', 64)))
            {
                McpArgumentsJson = "{}"
            })
            .Add("architecture-tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.architecture", new string('b', 64)))
            {
                McpArgumentsJson = "{}"
            });
        var execution = definition.ExecutionConnections!
            .Remove("sequence-inventory")
            .Remove("sequence-architecture")
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-tool", "execute"), "execution"))
            .Add("inventory-output", new(new("inventory-tool", "then"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("architecture-tool", "execute"), "execution"))
            .Add("architecture-output", new(new("architecture-tool", "then"), new("output", "execute"), "execution"));
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.SequencePathGraph,
            Nodes = nodes,
            ExecutionConnections = execution
        };
    }
}
