using System.Collections.Immutable;
using System.Reflection;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioNestedMcpPinProjectionTests
{
    [Fact]
    public void Schema_two_editor_pins_resolve_against_the_core_mcp_descriptor()
    {
        var binding = new ToolBindingDescriptor("demo.inspect-payload", "Inspect payload", "demo-text",
            "inspect_payload", new string('a', 64))
        {
            InputSchemaJson = """{"type":"object","properties":{"payload":{"type":"object","properties":{"request":{"type":"object","properties":{"name":{"type":"string"},"count":{"type":"integer"}},"required":["name","count"],"additionalProperties":false},"labels":{"type":"array","items":{"type":"string"}}},"required":["request","labels"],"additionalProperties":false}},"required":["payload"],"additionalProperties":false}"""
        };
        var method = typeof(WorkflowEditor).GetMethod("McpInputPorts", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var pins = Assert.IsType<ImmutableArray<McpInputPortDefinition>>(
            method.Invoke(null, [binding, true]));
        var node = new NodeDefinition(NodeTypes.McpTool, "1.0.0",
            new ToolBindingRequest(binding.BindingId, binding.ContractHash)) { McpInputPorts = pins };

        Assert.True(WorkflowNodeDescriptorResolver.TryResolveMcpArguments(node, out var resolved),
            string.Join(", ", pins.Select(pin => $"{pin.Id}:{pin.DataType}:{pin.JsonPointer}")));
        Assert.Equal(["/payload/request/count", "/payload/request/name"],
            resolved.Select(argument => argument.Argument.JsonPointer).Order(StringComparer.Ordinal));
    }
}
