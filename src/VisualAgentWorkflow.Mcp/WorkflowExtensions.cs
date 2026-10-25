using System.Text.Json;
using ModelContextProtocol.Server;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Stable identity declared by one host-approved extension assembly.</summary>
public sealed record WorkflowExtensionDescriptor(string Id, string Version);

/// <summary>Extension entry point loaded by the isolated worker, never by the workflow engine.</summary>
public interface IWorkflowExtension
{
    WorkflowExtensionDescriptor Descriptor { get; }
    IReadOnlyList<McpServerTool> CreateTools(IWorkflowExtensionContext context);
}

/// <summary>The narrow MCP capability surface available to an isolated extension.</summary>
public interface IWorkflowExtensionContext
{
    Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<string> InvokeMcpToolAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default);
}
