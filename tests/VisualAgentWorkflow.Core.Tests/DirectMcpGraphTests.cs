using System.Collections.Immutable;
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
}
