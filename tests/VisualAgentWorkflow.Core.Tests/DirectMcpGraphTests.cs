using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class DirectMcpGraphTests
{
    [Fact]
    public void Direct_mcp_graph_declares_typed_data_and_execution_ports()
    {
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.echo", new string('a', 64)));

        Assert.Equal(new WorkflowPort("in", "string"), Assert.Single(WorkflowPorts.ExplicitDataInputs(tool)));
        Assert.Equal(new WorkflowPort("out", "string"), Assert.Single(WorkflowPorts.ExplicitDataOutputs(tool)));
        Assert.Equal("execute", Assert.Single(WorkflowPorts.ExplicitExecutionInputs(tool)));
        Assert.Equal("then", Assert.Single(WorkflowPorts.ExplicitExecutionOutputs(tool)));
    }

    [Fact]
    public void Direct_mcp_graph_accepts_a_linear_tool_route()
    {
        var definition = Definition();

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Direct_mcp_graph_rejects_agent_routes()
    {
        var definition = Definition() with
        {
            Nodes = Definition().Nodes.SetItem("tool", new NodeDefinition(NodeTypes.Agent, "1.0.0"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "execution.node.unsupported");
    }

    [Fact]
    public void Configured_direct_mcp_arguments_make_the_data_input_optional()
    {
        var baseline = Definition();
        var tool = baseline.Nodes["tool"] with { McpArgumentsJson = "{}" };
        var definition = baseline with
        {
            Nodes = baseline.Nodes.SetItem("tool", tool),
            Connections = baseline.Connections.Remove("tool-input")
        };

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Direct_mcp_arguments_must_be_a_bounded_json_object()
    {
        var definition = Definition() with
        {
            Nodes = Definition().Nodes.SetItem("tool", Definition().Nodes["tool"] with { McpArgumentsJson = "[]" })
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "mcp.arguments");
    }

    [Fact]
    public void Schema_1_37_exposes_stable_typed_mcp_argument_pins()
    {
        var definition = PinDefinition();

        var ports = WorkflowPorts.ExplicitDataInputs(definition.Nodes["tool"]);
        Assert.Equal(2, ports.Length);
        Assert.Equal("arg.path", ports[0].Id);
        Assert.Equal("string", ports[0].DataType);
        Assert.Equal("arg.maxBytes", ports[1].Id);
        Assert.Equal("integer", ports[1].DataType);
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Schema_1_37_requires_each_required_argument_to_be_connected_or_static()
    {
        var definition = PinDefinition() with { Connections = PinDefinition().Connections.Remove("path") };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "data.degree" && issue.Path.Contains("arg.path"));
    }

    [Fact]
    public void Schema_1_37_pin_contract_round_trips_without_reordering()
    {
        var definition = PinDefinition();
        var restored = JsonSerializer.Deserialize<WorkflowDefinition>(JsonSerializer.Serialize(definition));

        Assert.NotNull(restored);
        Assert.Equal(definition.Nodes["tool"].McpInputPorts, restored!.Nodes["tool"].McpInputPorts);
    }

    [Fact]
    public void Schema_1_40_accepts_a_finite_number_mcp_pin_and_constant()
    {
        var definition = NumberPinDefinition();

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.Equal(WorkflowDataTypes.Number, Assert.Single(WorkflowPorts.ExplicitDataOutputs(definition.Nodes["amount"])).DataType);
        Assert.Equal(WorkflowDataTypes.Number, definition.Nodes["tool"].McpInputPorts!.Value[0].DataType);
    }

    [Fact]
    public void Schema_1_41_accepts_a_finite_number_parameter_on_a_persisted_mcp_pin()
    {
        var baseline = NumberPinDefinition();
        var definition = baseline with
        {
            SchemaVersion = WorkflowSchemaVersions.NumberPropagationGraph,
            Nodes = baseline.Nodes.SetItem("amount", new(NodeTypes.WorkflowValue, "1.0.0")
            {
                ParameterId = "amount", OutputDataType = WorkflowDataTypes.Number
            }),
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("amount", new WorkflowParameterDefinition("Amount", "12.5") { Type = "number" })
        };

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Number_parameter_graph_can_upgrade_without_rewriting_mcp_pin_contract()
    {
        var upgraded = GraphEditing.EnableNumberPropagationGraph(NumberPinDefinition());

        Assert.Equal(WorkflowSchemaVersions.NumberPropagationGraph, upgraded.SchemaVersion);
        Assert.Equal(NumberPinDefinition().Nodes["tool"].McpInputPorts, upgraded.Nodes["tool"].McpInputPorts);
        Assert.Empty(WorkflowValidator.Validate(upgraded));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Schema_1_40_rejects_non_finite_number_constants(double value)
    {
        var definition = NumberPinDefinition() with
        {
            Nodes = NumberPinDefinition().Nodes.SetItem("amount", new(NodeTypes.NumberConstant, "1.0.0") { NumberConstant = new(value) })
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "constant.configuration");
    }

    [Fact]
    public void Number_constants_are_not_accepted_by_schema_1_37()
    {
        var definition = NumberPinDefinition() with { SchemaVersion = WorkflowSchemaVersions.DirectMcpPinsGraph };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code is "constant.configuration" or "execution.node.unsupported");
    }

    [Fact]
    public void Direct_mcp_pin_graph_can_upgrade_to_number_schema_without_changing_pins()
    {
        var definition = PinDefinition();

        var upgraded = GraphEditing.EnableNumberGraph(definition);

        Assert.Equal(WorkflowSchemaVersions.NumberGraph, upgraded.SchemaVersion);
        Assert.Equal(definition.Nodes["tool"].McpInputPorts, upgraded.Nodes["tool"].McpInputPorts);
        Assert.Empty(WorkflowValidator.Validate(upgraded));
    }

    [Fact]
    public void Explicit_port_descriptors_expose_mcp_cardinality_and_static_fallback()
    {
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.search", new string('a', 64)))
        {
            McpInputPorts =
            [
                new("arg.query", "query", "string", true),
                new("arg.limit", "limit", "integer", false)
            ]
        };

        var ports = WorkflowPorts.Explicit(tool);

        Assert.Collection(ports,
            port =>
            {
                Assert.Equal("arg.query", port.Id);
                Assert.Equal(WorkflowPortDirection.Input, port.Direction);
                Assert.True(port.Required);
                Assert.Equal(WorkflowPortCardinality.AtMostOne, port.Cardinality);
                Assert.True(port.SupportsStaticValue);
            },
            port =>
            {
                Assert.Equal("arg.limit", port.Id);
                Assert.False(port.Required);
                Assert.Equal(WorkflowPortCardinality.AtMostOne, port.Cardinality);
                Assert.True(port.SupportsStaticValue);
            },
            port =>
            {
                Assert.Equal("out", port.Id);
                Assert.Equal(WorkflowPortDirection.Output, port.Direction);
                Assert.Equal(WorkflowPortCardinality.Many, port.Cardinality);
            },
            port =>
            {
                Assert.Equal("execute", port.Id);
                Assert.Equal(WorkflowPortDirection.Input, port.Direction);
                Assert.Equal(WorkflowPortCardinality.ExactlyOne, port.Cardinality);
            },
            port =>
            {
                Assert.Equal("then", port.Id);
                Assert.Equal(WorkflowPortDirection.Output, port.Direction);
                Assert.Equal(WorkflowPortCardinality.ExactlyOne, port.Cardinality);
                Assert.True(port.Required);
            });
    }

    [Fact]
    public void Explicit_port_resolution_uses_discovered_mcp_metadata_without_overriding_persisted_metadata()
    {
        var discovered = new[]
        {
            new McpInputPortDefinition("arg.query", "query", "string", true),
            new McpInputPortDefinition("arg.limit", "limit", "integer", false)
        }.ToImmutableArray();
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.search", new string('a', 64)));

        var discoveredPorts = WorkflowPorts.Explicit(tool, discovered);
        Assert.Equal(["arg.query", "arg.limit"], discoveredPorts
            .Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data)
            .Select(port => port.Id));
        Assert.Contains(discoveredPorts, port => port.Id == "arg.query" && port.Required && port.SupportsStaticValue);
        Assert.Contains(discoveredPorts, port => port.Id == "arg.limit" && !port.Required && port.SupportsStaticValue);

        var persisted = tool with
        {
            McpInputPorts = [new("arg.path", "path", "string", true)]
        };
        var persistedPorts = WorkflowPorts.Explicit(persisted, discovered);
        Assert.DoesNotContain(persistedPorts, port => port.Id == "arg.query");
        Assert.Contains(persistedPorts, port => port.Id == "arg.path" && port.Required && port.SupportsStaticValue);
    }

    [Fact]
    public void Mcp_pin_descriptions_are_transient_display_metadata_and_do_not_change_persisted_pin_shape()
    {
        var discovered = new[]
        {
            new McpInputPortDefinition("arg.query", "query", "string", true),
            new McpInputPortDefinition("arg.limit", "limit", "integer", false)
        }.ToImmutableArray();
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["arg.query"] = "The search text to send to the tool."
        };
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.search", new string('a', 64)));

        var ports = WorkflowPorts.Explicit(tool, discovered, descriptions);

        Assert.Equal("The search text to send to the tool.", ports.Single(port => port.Id == "arg.query").Description);
        Assert.Null(ports.Single(port => port.Id == "arg.limit").Description);
        Assert.Null(tool.McpInputPorts);
        Assert.Equal(5, ports.Count(port => port.Direction == WorkflowPortDirection.Input || port.Direction == WorkflowPortDirection.Output));
    }

    private static WorkflowDefinition Definition() => new(
        WorkflowSchemaVersions.DirectMcpGraph, "direct-mcp", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.echo", new string('a', 64))),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["tool-input"] = new(new("input", "out"), new("tool", "in"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition PinDefinition() => new(
        WorkflowSchemaVersions.DirectMcpPinsGraph, "direct-mcp-pins", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["path"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("README.md") },
            ["max"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(50000) },
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.read", new string('a', 64)))
            {
                McpInputPorts = [new("arg.path", "path", "string", true), new("arg.maxBytes", "maxBytes", "integer", false)]
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["path"] = new(new("path", "out"), new("tool", "arg.path"), "data"),
            ["max"] = new(new("max", "out"), new("tool", "arg.maxBytes"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition NumberPinDefinition() => new(
        WorkflowSchemaVersions.NumberGraph, "direct-mcp-number", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["amount"] = new(NodeTypes.NumberConstant, "1.0.0") { NumberConstant = new(12.5d) },
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.number", new string('a', 64)))
            {
                McpInputPorts = [new("arg.amount", "amount", WorkflowDataTypes.Number, true)]
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["amount"] = new(new("amount", "out"), new("tool", "arg.amount"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };
}
