using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>A requirement, never a permission grant or a server connection specification.</summary>
public sealed record ToolBindingRequest(string BindingId, string ContractHash)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DescriptionHash { get; init; }
}
public sealed record ToolBindingDescriptor(string BindingId, string DisplayName, string ServerId, string ToolName, string ContractHash)
{
    /// <summary>Discovered input schema for authoring and agent function declaration; never an authority grant.</summary>
    public string InputSchemaJson { get; init; } = "{}";
    /// <summary>Untrusted, bounded MCP metadata shown to authors and included in agent function declarations.</summary>
    public string ToolDescription { get; init; } = "";
    /// <summary>Concise, bounded argument summary for authoring surfaces.</summary>
    public string InputSummary { get; init; } = "";
    /// <summary>Optional compatibility pin for the MCP tool description; absent on legacy workflow bindings.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DescriptionHash { get; init; }
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
    /// <summary>Trusted runtime restriction for delegated calls; it can only narrow host policy access.</summary>
    public IReadOnlyList<ToolBindingRequest>? ToolRequirements { get; init; }
    /// <summary>Runtime event writer that preserves distinct child invocation identities.</summary>
    public Func<Guid, string, string?, ValueTask>? RecordWithInvocation { get; init; }

    public ValueTask RecordEventAsync(string kind, string? data) => RecordEventForAsync(InvocationId, kind, data);

    public ValueTask RecordEventForAsync(Guid invocationId, string kind, string? data) =>
        RecordWithInvocation is { } writer ? writer(invocationId, kind, data) : Record(kind, data);
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
public sealed class ToolContractMismatchException() : InvalidOperationException("The pinned tool schema no longer matches the server.");
public sealed class ToolDescriptionMismatchException() : InvalidOperationException("The pinned tool description no longer matches the server.");
public sealed class ToolInvocationException(string message) : InvalidOperationException(message);
