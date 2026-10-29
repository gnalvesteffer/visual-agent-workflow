using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class PureNodeInsertionTests
{
    [Fact]
    public void InsertPureNodeBetween_replaces_only_the_selected_data_edge()
    {
        var definition = ConversionWorkflow();
        var execution = definition.ExecutionConnections;

        var inserted = GraphEditing.InsertPureNodeBetween(definition, "equals-invert", new(NodeTypes.BooleanNot, "1.0.0"));
        var pure = Assert.Single(inserted.Nodes, pair => pair.Key.StartsWith("pure-", StringComparison.Ordinal)).Key;

        Assert.Equal(definition.SchemaVersion, inserted.SchemaVersion);
        Assert.Equal(execution, inserted.ExecutionConnections);
        Assert.Equal(new PortReference("equals", "out"), inserted.Connections["equals-invert"].Source);
        Assert.Equal(new PortReference(pure, "value"), inserted.Connections["equals-invert"].Target);
        var bridge = Assert.Single(inserted.Connections.Values, edge => edge.Source == new PortReference(pure, "out"));
        Assert.Equal(new PortReference("invert", "value"), bridge.Target);
        Assert.Empty(WorkflowValidator.Validate(inserted));
    }

    [Fact]
    public void InsertPureNodeBetween_rejects_execution_and_impure_or_non_unary_nodes_without_mutation()
    {
        var definition = ConversionWorkflow();
        var snapshot = WorkflowJson.Serialize(definition);

        Assert.Throws<ArgumentException>(() => GraphEditing.InsertPureNodeBetween(definition, "activate", new(NodeTypes.BooleanNot, "1.0.0")));
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertPureNodeBetween(definition, "equals-invert", new(NodeTypes.McpTool, "1.0.0")));
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertPureNodeBetween(definition, "equals-invert", new(NodeTypes.TextConcat, "1.0.0")));
        Assert.Equal(snapshot, WorkflowJson.Serialize(definition));
    }

    private static WorkflowDefinition ConversionWorkflow() => new(WorkflowSchemaVersions.TypedBooleanGraph, "typed-boolean", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("expected", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("yes") })
            .Add("equals", new(NodeTypes.StringEquals, "1.0.0"))
            .Add("invert", new(NodeTypes.BooleanNot, "1.0.0"))
            .Add("branch", new(NodeTypes.BooleanBranch, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"))
            .Add("terminate", new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-left", new(new("input", "out"), new("equals", "left"), "data"))
            .Add("expected-right", new(new("expected", "out"), new("equals", "right"), "data"))
            .Add("equals-invert", new(new("equals", "out"), new("invert", "value"), "data"))
            .Add("invert-branch", new(new("invert", "out"), new("branch", "condition"), "data"))
            .Add("output-value", new(new("input", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("activate-branch", new(new("input", "then"), new("branch", "execute"), "execution") )
            .Add("true-route", new(new("branch", "true"), new("output", "execute"), "execution"))
            .Add("false-route", new(new("branch", "false"), new("terminate", "execute"), "execution"))
    };
}
