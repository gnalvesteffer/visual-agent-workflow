using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>A requirement, never a permission grant or a server connection specification.</summary>
public sealed record ToolBindingRequest(string BindingId, string ContractHash);
public sealed record ToolBindingDescriptor(string BindingId, string DisplayName, string ServerId, string ToolName, string ContractHash)
{
    /// <summary>Discovered input schema for authoring and agent function declaration; never an authority grant.</summary>
    public string InputSchemaJson { get; init; } = "{}";
}

public interface IToolBindingCatalog
{
    Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Constructed only by the trusted runtime, independent of browser request identity.</summary>
public sealed record NodeInvocationContext(Guid RunId, WorkflowRevision Revision, string NodeId, Guid InvocationId,
    Func<string, string?, ValueTask> Record)
{
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

public interface INodeToolScope : IAsyncDisposable
{
    Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default);
    Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.EnumerateObject().Count() == 1 &&
            arguments.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return InvokeAsync(bindingId, text.GetString()!, cancellationToken);
        return Task.FromException<string>(new NotSupportedException("This tool scope does not support typed MCP arguments."));
    }
}

public interface INodeToolBroker : IToolBindingCatalog
{
    INodeToolScope OpenScope(NodeInvocationContext invocation);
}

/// <summary>Host policy is independent of workflow requirements. Imports cannot grant authority.</summary>
public interface IToolAuthorizationPolicy
{
    bool IsAllowed(WorkflowRevision revision, string nodeId, string bindingId);
}

public sealed record NodeToolGrant(string WorkflowId, string NodeId, string BindingId);

public sealed class ExplicitToolAuthorizationPolicy(IEnumerable<NodeToolGrant> grants) : IToolAuthorizationPolicy
{
    private readonly HashSet<NodeToolGrant> allowed = [.. grants];
    public bool IsAllowed(WorkflowRevision revision, string nodeId, string bindingId) =>
        allowed.Contains(new(revision.Definition.WorkflowId, nodeId, bindingId));
}

public sealed class ToolAccessDeniedException() : InvalidOperationException("The node has no effective grant for this tool.");
public sealed class ToolContractMismatchException() : InvalidOperationException("The pinned tool contract no longer matches the server.");
public sealed class ToolInvocationException(string message) : InvalidOperationException(message);
