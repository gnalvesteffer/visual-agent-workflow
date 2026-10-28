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
}
