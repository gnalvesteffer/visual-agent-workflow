using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Trusted host registration, never deserialized from a workflow.</summary>
public sealed record McpToolRegistration(string BindingId, string DisplayName, string ServerId, string ToolName,
    Func<CancellationToken, Task<McpClient>> Connect, bool ReadOnly = false);

public sealed class McpToolBroker : INodeToolBroker
{
    private readonly IReadOnlyDictionary<string, McpToolRegistration> registrations;
    private readonly IToolAuthorizationPolicy policy;
    private readonly TimeSpan timeout;

    public McpToolBroker(IEnumerable<McpToolRegistration> registrations, IToolAuthorizationPolicy policy, TimeSpan? timeout = null)
    {
        this.registrations = registrations.ToDictionary(r => r.BindingId, StringComparer.Ordinal);
        this.policy = policy;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ToolBindingDescriptor>();
        foreach (var registration in registrations.Values.Where(r => r.ReadOnly))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await using var client = await registration.Connect(deadline.Token);
            var tool = await FindTool(client, registration, deadline.Token);
            results.Add(Describe(registration, tool));
        }
        return results;
    }

    public INodeToolScope OpenScope(NodeInvocationContext invocation)
    {
        WorkflowValidator.ThrowIfInvalid(invocation.Revision.Definition);
        if (invocation.RunId == Guid.Empty || invocation.InvocationId == Guid.Empty ||
            !invocation.Revision.Definition.Nodes.TryGetValue(invocation.NodeId, out var node))
            throw new ArgumentException("Invalid runtime invocation context.", nameof(invocation));
        return new Scope(this, invocation, NodeToolRequirements.For(node));
    }

    private static async Task<McpClientTool> FindTool(McpClient client, McpToolRegistration registration, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var tool = tools.SingleOrDefault(t => t.Name == registration.ToolName)
            ?? throw new ToolInvocationException("The registered tool is unavailable.");
        // Initial adapter supports one required string argument and text-only results.
        var schema = tool.JsonSchema;
        if (!schema.TryGetProperty("properties", out var properties) || properties.EnumerateObject().Count() != 1 ||
            !properties.TryGetProperty("text", out var text) || !text.TryGetProperty("type", out var type) || type.GetString() != "string")
            throw new ToolInvocationException("Only tools with one string 'text' input are supported.");
        return tool;
    }

    private static ToolBindingDescriptor Describe(McpToolRegistration registration, McpClientTool tool) =>
        new(registration.BindingId, registration.DisplayName, registration.ServerId, registration.ToolName, ToolContractHash.Compute(tool));

    private sealed class Scope(McpToolBroker owner, NodeInvocationContext invocation, IReadOnlyList<ToolBindingRequest> requested) : INodeToolScope
    {
        private int disposed;
        private int calls;

        private bool Allowed(string bindingId) => requested.Any(r => r.BindingId == bindingId) &&
            owner.registrations.TryGetValue(bindingId, out var registration) && registration.ReadOnly &&
            owner.policy.IsAllowed(invocation.Revision, invocation.NodeId, bindingId);

        public async Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var results = new List<ToolBindingDescriptor>();
            foreach (var requirement in requested.Where(r => Allowed(r.BindingId)))
            {
                var registration = owner.registrations[requirement.BindingId];
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(owner.timeout);
                await using var client = await registration.Connect(deadline.Token);
                var tool = await FindTool(client, registration, deadline.Token);
                var descriptor = Describe(registration, tool);
                if (descriptor.ContractHash != requirement.ContractHash) throw new ToolContractMismatchException();
                if (Allowed(requirement.BindingId)) results.Add(descriptor);
            }
            return results;
        }

        public async Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(input);
            await Record(ExecutionEventKinds.ToolRequested, new { bindingId });
            if (!Allowed(bindingId))
            {
                await Record(ExecutionEventKinds.ToolDenied, new { bindingId, reason = "No effective node grant." });
                throw new ToolAccessDeniedException();
            }
            if (Interlocked.Increment(ref calls) > 8 || input.Length > 16_384)
            {
                await Record(ExecutionEventKinds.ToolDenied, new { bindingId, reason = "Invocation budget exceeded." });
                throw new ToolInvocationException("Tool invocation budget exceeded.");
            }
            var registration = owner.registrations[bindingId];
            var requirement = requested.Single(r => r.BindingId == bindingId);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(owner.timeout);
            var dispatched = false;
            try
            {
                await using var client = await registration.Connect(deadline.Token);
                var tool = await FindTool(client, registration, deadline.Token);
                if (ToolContractHash.Compute(tool) != requirement.ContractHash) throw new ToolContractMismatchException();
                if (!Allowed(bindingId)) throw new ToolAccessDeniedException();
                await Record(ExecutionEventKinds.ToolAuthorized, new
                { bindingId, registration.ServerId, registration.ToolName, requirement.ContractHash, input });
                dispatched = true;
                var result = await client.CallToolAsync(registration.ToolName,
                    new Dictionary<string, object?> { ["text"] = input }, cancellationToken: deadline.Token);
                if (result.IsError == true) throw new ToolInvocationException("The MCP server reported a tool failure.");
                if (result.Content.Any(c => c is not TextContentBlock)) throw new ToolInvocationException("The tool returned unsupported non-text content.");
                var output = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                if (output.Length > 16_384) throw new ToolInvocationException("Tool output exceeds the pipeline limit.");
                await Record(ExecutionEventKinds.ToolCompleted, new { bindingId, output });
                return output;
            }
            catch (OperationCanceledException)
            {
                await Record(dispatched ? ExecutionEventKinds.ToolUncertain : ExecutionEventKinds.ToolFailed,
                    new { bindingId, reason = "Tool call cancelled or timed out; no automatic retry." });
                if (cancellationToken.IsCancellationRequested) throw;
                throw new ToolInvocationException("The tool operation timed out.");
            }
            catch (Exception error)
            {
                await Record(error is ToolAccessDeniedException ? ExecutionEventKinds.ToolDenied :
                    dispatched && error is not ToolInvocationException ? ExecutionEventKinds.ToolUncertain : ExecutionEventKinds.ToolFailed,
                    new { bindingId, reason = error is ToolContractMismatchException ? "Contract mismatch." : "Tool operation failed." });
                throw;
            }
        }

        private ValueTask Record(string kind, object payload) => invocation.Record(kind, JsonSerializer.Serialize(payload));
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref disposed, 1); return ValueTask.CompletedTask; }
    }
}
