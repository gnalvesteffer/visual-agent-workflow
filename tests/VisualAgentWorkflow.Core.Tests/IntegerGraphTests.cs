using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class IntegerGraphTests
{
    [Fact]
    public void Integer_nodes_expose_only_matching_signed_integer_and_boolean_ports()
    {
        var constant = new NodeDefinition(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(long.MinValue) };
        var add = new NodeDefinition(NodeTypes.IntegerAdd, "1.0.0");
        var equals = new NodeDefinition(NodeTypes.IntegerEquals, "1.0.0");

        Assert.Equal("integer", Assert.Single(WorkflowPorts.ExplicitDataOutputs(constant)).DataType);
        Assert.Equal(["integer", "integer"], WorkflowPorts.ExplicitDataInputs(add).Select(port => port.DataType));
        Assert.Equal("integer", Assert.Single(WorkflowPorts.ExplicitDataOutputs(add)).DataType);
        Assert.Equal(["integer", "integer"], WorkflowPorts.ExplicitDataInputs(equals).Select(port => port.DataType));
        Assert.Equal("boolean", Assert.Single(WorkflowPorts.ExplicitDataOutputs(equals)).DataType);
    }

    [Fact]
    public void Integer_values_require_schema_131_and_reject_mismatched_workflow_parameter_types()
    {
        var definition = IntegerWorkflow() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("amount", new WorkflowParameterDefinition("Amount", "42") { Type = "integer" })
        };
        var value = GraphEditing.AddUnconnectedNode(definition,
            new(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "amount", OutputDataType = "integer" });

        var valueNode = value.Nodes.Values.Single(node => node.Type == NodeTypes.WorkflowValue);
        Assert.Equal("integer", valueNode.OutputDataType);
        var connected = GraphEditing.ConnectPins(value, value.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowValue).Key,
            "out", "output", "in");
        Assert.Equal("integer", connected.Nodes["output"].OutputDataType);
        Assert.Empty(WorkflowValidator.Validate(connected));

        var withSecondParameter = WorkflowParameterEditing.Add(connected, "second", "Second", "7");
        withSecondParameter = WorkflowParameterEditing.SetType(withSecondParameter, "second", "integer", "7");
        var valueId = withSecondParameter.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowValue).Key;
        var rebound = WorkflowParameterEditing.BindNode(withSecondParameter, valueId, "second");
        Assert.Empty(WorkflowValidator.Validate(rebound));

        var mismatched = connected with
        {
            Parameters = connected.Parameters!.SetItem("amount", connected.Parameters["amount"] with { Type = "boolean", DefaultValue = "true" })
        };
        Assert.Contains(WorkflowValidator.Validate(mismatched), issue => issue.Code == "parameter.type");
    }

    [Fact]
    public void Older_workflow_parameter_edits_preserve_their_schema()
    {
        var definition = IntegerWorkflow() with { SchemaVersion = WorkflowSchemaVersions.AgentExecutionRoutes, WorkflowId = "schema-preservation" };

        var edited = WorkflowParameterEditing.Add(definition, "count", "Count", "3");

        Assert.Equal(WorkflowSchemaVersions.AgentExecutionRoutes, edited.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(edited));
    }

    [Fact]
    public void Integer_constant_configuration_can_be_edited_without_changing_node_type()
    {
        var initial = IntegerWorkflow();
        var definition = initial with
        {
            Nodes = initial.Nodes.Add("literal", new(NodeTypes.IntegerConstant, "1.0.0")
            {
                IntegerConstant = new(1)
            })
        };

        var edited = GraphEditing.ReplaceNode(definition, "literal",
            definition.Nodes["literal"] with { IntegerConstant = new(2) });

        Assert.Equal(2, edited.Nodes["literal"].IntegerConstant!.Value);
        Assert.Equal(WorkflowSchemaVersions.IntegerGraph, edited.SchemaVersion);
    }

    private static WorkflowDefinition IntegerWorkflow() => new(WorkflowSchemaVersions.IntegerGraph, "integer-values", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("constant", new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(true) })
            .Add("branch", new(NodeTypes.BooleanBranch, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"))
            .Add("failure", new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("condition", new(new("constant", "out"), new("branch", "condition"), "data"))
            .Add("data", new(new("input", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("activate", new(new("input", "then"), new("branch", "execute"), "execution"))
            .Add("success", new(new("branch", "true"), new("output", "execute"), "execution"))
            .Add("failure", new(new("branch", "false"), new("failure", "execute"), "execution"))
    };
}
