using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

// These integration tests launch a separate .NET process for each registered MCP tool.
// Keep that process startup isolated from other MCP tests on slower Windows runners.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class McpStdioCollection
{
    public const string Name = "MCP stdio process integration";
}
