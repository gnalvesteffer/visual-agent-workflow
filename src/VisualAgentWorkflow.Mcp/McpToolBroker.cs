using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Trusted host registration, never deserialized from a workflow.</summary>
public sealed record McpToolRegistration(string BindingId, string DisplayName, string ServerId, string ToolName,
    Func<CancellationToken, Task<McpClient>> Connect, bool ReadOnly = false)
{
    public bool AllowInvocation { get; init; }
    /// <summary>Allows a known server-declared failure to be fed back to the model once for correction.</summary>
    public bool AllowModelRecoveryAfterFailure { get; init; }
    /// <summary>Optional invocation-bound connector for registrations that need a scoped host capability channel.</summary>
    public Func<NodeInvocationContext, CancellationToken, Task<McpToolConnection>>? ConnectInScope { get; init; }
}

/// <summary>A scoped MCP connection and any invocation-specific resources it owns.</summary>
public sealed class McpToolConnection(McpClient client, IAsyncDisposable? lifetime = null) : IAsyncDisposable
{
    public McpClient Client { get; } = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisposeAsync(); }
        finally
        {
            if (lifetime is not null) await lifetime.DisposeAsync();
        }
    }
}

public sealed class McpToolBroker : INodeToolBroker
{
    private IReadOnlyDictionary<string, McpToolRegistration> registrations;
    private readonly IToolAuthorizationPolicy policy;
    private readonly TimeSpan timeout;

    public McpToolBroker(IEnumerable<McpToolRegistration> registrations, IToolAuthorizationPolicy policy, TimeSpan? timeout = null)
    {
        this.registrations = registrations.ToDictionary(r => r.BindingId, StringComparer.Ordinal);
        this.policy = policy;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>Atomically replaces host-approved registrations. Active scopes recheck each dispatch.</summary>
    public void ReplaceRegistrations(IEnumerable<McpToolRegistration> updated) =>
        Volatile.Write(ref registrations, updated.ToDictionary(registration => registration.BindingId, StringComparer.Ordinal));

    public async Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default)
    {
        var serverGroups = Volatile.Read(ref registrations).Values
            .Where(registration => registration.ReadOnly || registration.AllowInvocation)
            .GroupBy(registration => (registration.ServerId, registration.Connect))
            .ToArray();

        var serverResults = await Task.WhenAll(serverGroups.Select(group => ListServerBindingsAsync(
            group.ToArray(), cancellationToken)));
        return serverResults.SelectMany(bindings => bindings).ToArray();
    }

    private async Task<IReadOnlyList<ToolBindingDescriptor>> ListServerBindingsAsync(
        IReadOnlyList<McpToolRegistration> registrationsForServer, CancellationToken cancellationToken)
    {
        var representative = registrationsForServer[0];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var client = await representative.Connect(deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);

        return registrationsForServer.Select(registration =>
        {
            var tool = FindTool(tools, registration);
            return Describe(registration, tool);
        }).ToArray();
    }

    public INodeToolScope OpenScope(NodeInvocationContext invocation)
    {
        WorkflowValidator.ThrowIfInvalid(invocation.Revision.Definition);
        if (invocation.RunId == Guid.Empty || invocation.InvocationId == Guid.Empty ||
            !invocation.Revision.Definition.Nodes.TryGetValue(invocation.NodeId, out var node))
            throw new ArgumentException("Invalid runtime invocation context.", nameof(invocation));
        var scopedInvocation = invocation with { OpenToolScope = OpenScope };
        return new Scope(this, scopedInvocation, scopedInvocation.ToolRequirements ?? NodeToolRequirements.For(node));
    }

    private static async Task<McpClientTool> FindTool(McpClient client, McpToolRegistration registration, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return FindTool(tools, registration);
    }

    private static McpClientTool FindTool(IList<McpClientTool> tools, McpToolRegistration registration)
    {
        var tool = tools.SingleOrDefault(t => t.Name == registration.ToolName)
            ?? throw new ToolInvocationException("The registered tool is unavailable.");
        McpInputSchema.Validate(tool.JsonSchema);
        McpToolDescription.Normalize(tool.Description);
        return tool;
    }

    private static ToolBindingDescriptor Describe(McpToolRegistration registration, McpClientTool tool)
    {
        var description = McpToolDescription.Normalize(tool.Description);
        return new(registration.BindingId, registration.DisplayName, registration.ServerId, registration.ToolName, ToolContractHash.Compute(tool))
        {
            InputSchemaJson = McpInputSchema.Normalize(tool.JsonSchema).GetRawText(),
            InputSummary = McpInputSchema.Summarize(tool.JsonSchema),
            ToolDescription = description,
            DescriptionHash = McpToolDescription.ComputeHash(description)
        };
    }

    private sealed class Scope(McpToolBroker owner, NodeInvocationContext invocation, IReadOnlyList<ToolBindingRequest> requested) : INodeToolScope
    {
        private int disposed;
        private int calls;

        private bool Allowed(string bindingId) => requested.Any(r => r.BindingId == bindingId) &&
            Volatile.Read(ref owner.registrations).TryGetValue(bindingId, out var registration) && (registration.ReadOnly || registration.AllowInvocation) &&
            owner.policy.IsAllowed(invocation.Revision, invocation.NodeId, bindingId);

        public async Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var results = new List<ToolBindingDescriptor>();
            foreach (var requirement in requested.Where(r => Allowed(r.BindingId)))
            {
                if (!Volatile.Read(ref owner.registrations).TryGetValue(requirement.BindingId, out var registration))
                    throw new ToolAccessDeniedException();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(owner.timeout);
                await using var connection = await Connect(registration, invocation, deadline.Token);
                var client = connection.Client;
                var tool = await FindTool(client, registration, deadline.Token);
                var descriptor = Describe(registration, tool);
                ValidatePinnedMetadata(requirement, descriptor);
                if (Allowed(requirement.BindingId)) results.Add(descriptor);
            }
            return results;
        }

        public async Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(input);
            JsonElement? typedArguments = null;
            try
            {
                using var parsed = JsonDocument.Parse(input);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                    typedArguments = parsed.RootElement.Clone();
            }
            catch (JsonException) { }
            if (typedArguments is { } objectArguments)
                return await InvokeJsonAsync(bindingId, objectArguments, cancellationToken);
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = input }));
            return await InvokeJsonAsync(bindingId, arguments.RootElement, cancellationToken);
        }

        public async Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var toolCallId = Guid.NewGuid();
            await Record(ExecutionEventKinds.ToolRequested, new { toolCallId, parentToolCallId = invocation.ParentToolCallId, bindingId });
            if (!Allowed(bindingId))
            {
                await Record(ExecutionEventKinds.ToolDenied, new
                { toolCallId, parentToolCallId = invocation.ParentToolCallId, bindingId, reason = "No effective node grant." });
                throw new ToolAccessDeniedException();
            }
            if (Interlocked.Increment(ref calls) > 8 || arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 16_384)
            {
                await Record(ExecutionEventKinds.ToolDenied, new
                { toolCallId, parentToolCallId = invocation.ParentToolCallId, bindingId, reason = "Invocation budget exceeded." });
                throw new ToolInvocationException("Tool invocation budget exceeded.");
            }
            if (!Volatile.Read(ref owner.registrations).TryGetValue(bindingId, out var registration))
                throw new ToolAccessDeniedException();
            var requirement = requested.Single(r => r.BindingId == bindingId);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(owner.timeout);
            var dispatched = false;
            try
            {
                await using var connection = await Connect(registration,
                    invocation with { ParentToolCallId = toolCallId }, deadline.Token);
                var client = connection.Client;
                var tool = await FindTool(client, registration, deadline.Token);
                var descriptor = Describe(registration, tool);
                ValidatePinnedMetadata(requirement, descriptor);
                var schema = McpInputSchema.Validate(tool.JsonSchema);
                var callArguments = schema.ToCallArguments(arguments);
                if (!Allowed(bindingId)) throw new ToolAccessDeniedException();
                await Record(ExecutionEventKinds.ToolAuthorized, new
                {
                    toolCallId,
                    parentToolCallId = invocation.ParentToolCallId,
                    bindingId,
                    registration.ServerId,
                    registration.ToolName,
                    requirement.ContractHash,
                    requirement.DescriptionHash,
                    input = arguments
                });
                dispatched = true;
                var result = await client.CallToolAsync(registration.ToolName, callArguments, cancellationToken: deadline.Token);
                if (result.IsError == true)
                    throw new ToolInvocationException("The MCP server reported a tool failure.",
                        registration.AllowModelRecoveryAfterFailure
                            ? ToolInvocationFailureDisposition.RecoverableFeedback
                            : ToolInvocationFailureDisposition.Fatal);
                if (result.Content.Any(c => c is not TextContentBlock)) throw new ToolInvocationException("The tool returned unsupported non-text content.");
                var output = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                if (output.Length > 16_384) throw new ToolInvocationException("Tool output exceeds the workflow limit.");
                await Record(ExecutionEventKinds.ToolCompleted, new { toolCallId, parentToolCallId = invocation.ParentToolCallId, bindingId, output });
                return output;
            }
            catch (OperationCanceledException)
            {
                await Record(dispatched ? ExecutionEventKinds.ToolUncertain : ExecutionEventKinds.ToolFailed,
                    new
                    {
                        toolCallId,
                        parentToolCallId = invocation.ParentToolCallId,
                        bindingId,
                        reason = "Tool call cancelled or timed out; no automatic retry."
                    });
                if (cancellationToken.IsCancellationRequested) throw;
                throw new ToolInvocationException("The tool operation timed out.");
            }
            catch (Exception error)
            {
                await Record(error is ToolAccessDeniedException ? ExecutionEventKinds.ToolDenied :
                    dispatched && error is not ToolInvocationException ? ExecutionEventKinds.ToolUncertain : ExecutionEventKinds.ToolFailed,
                    new
                    {
                        toolCallId,
                        parentToolCallId = invocation.ParentToolCallId,
                        bindingId,
                        disposition = error is ToolInvocationException invocationFailure
                            ? invocationFailure.Disposition.ToString()
                            : dispatched ? ToolInvocationFailureDisposition.OutcomeUnknown.ToString() : ToolInvocationFailureDisposition.Fatal.ToString(),
                        reason = error switch
                        {
                            ToolContractMismatchException => "Input/output schema mismatch.",
                            ToolDescriptionMismatchException => "Description mismatch.",
                            _ => "Tool operation failed."
                        }
                    });
                throw;
            }
        }

        private static async Task<McpToolConnection> Connect(McpToolRegistration registration,
            NodeInvocationContext invocation, CancellationToken cancellationToken) =>
            registration.ConnectInScope is { } scoped
                ? await scoped(invocation, cancellationToken)
                : new(await registration.Connect(cancellationToken));

        private static void ValidatePinnedMetadata(ToolBindingRequest requirement, ToolBindingDescriptor actual)
        {
            if (actual.ContractHash != requirement.ContractHash) throw new ToolContractMismatchException();
            if (requirement.DescriptionHash is { } descriptionHash && descriptionHash != actual.DescriptionHash)
                throw new ToolDescriptionMismatchException();
        }

        private ValueTask Record(string kind, object payload) => invocation.RecordEventAsync(kind, JsonSerializer.Serialize(payload));
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref disposed, 1); return ValueTask.CompletedTask; }
    }
}
