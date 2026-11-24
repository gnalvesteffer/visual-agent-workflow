using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Supplies nested MCP argument pins to authoring surfaces through the Core capability contract.</summary>
public sealed class McpInputPortFactory : IMcpInputPortFactory
{
    public ImmutableArray<McpInputPortDefinition> CreateNestedInputPorts(JsonElement schema) =>
        McpToolInputSchemas.CreateNestedInputPorts(schema);
}
