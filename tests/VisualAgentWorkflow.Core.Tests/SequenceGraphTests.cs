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
}
