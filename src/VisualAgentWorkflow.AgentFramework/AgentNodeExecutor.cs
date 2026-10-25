using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Host-registered provider factory; workflow documents cannot construct clients or supply endpoints/secrets.</summary>
public sealed record AgentProviderRegistration(AgentProviderDescriptor Descriptor, Func<IChatClient> Create)
{
    public TimeSpan InvocationTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class AgentNodeExecutor(IEnumerable<AgentProviderRegistration> providers, INodeToolBroker tools) : IAgentNodeExecutor
{
    private IReadOnlyDictionary<string, AgentProviderRegistration> registrations = Index(providers);

    public void ReplaceProviders(IEnumerable<AgentProviderRegistration> providers) =>
        Volatile.Write(ref registrations, Index(providers));

    private static IReadOnlyDictionary<string, AgentProviderRegistration> Index(IEnumerable<AgentProviderRegistration> providers) =>
        providers.ToDictionary(provider => Key(provider.Descriptor.ProviderId, provider.Descriptor.Version), StringComparer.Ordinal);

    private static string Key(string providerId, string version) => $"{providerId}\0{version}";

    public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
        Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(Volatile.Read(ref registrations).Values.Select(r => r.Descriptor).ToArray());

    public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default)
    {
        WorkflowValidator.ThrowIfInvalid(invocation.Revision.Definition);
        var configuration = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent
            ?? throw new ArgumentException("The invocation is not an agent node.");
        if (!Volatile.Read(ref registrations).TryGetValue(Key(configuration.ProviderId, configuration.ProviderVersion), out var provider))
            throw new InvalidOperationException("The pinned agent provider is unavailable.");
        if (configuration.ReasoningEffort is not null && !provider.Descriptor.SupportsReasoningEffort)
            throw new InvalidOperationException("The selected provider does not support reasoning effort.");
        var assignedSkills = (configuration.Skills ?? []).Select(reference =>
            invocation.Revision.Definition.Skills![reference.Id + "@" + reference.Version]).ToArray();
        var instructions = string.Join("\n\n", new[] { configuration.Instructions }
            .Concat(assignedSkills.Select(skill => $"Skill: {skill.Name}\n{skill.Instructions}")));
        if (instructions.Length > 65_536) throw new InvalidOperationException("Combined agent instructions exceed the prompt budget.");
        var userMessage = configuration.UserMessage is null ? input :
            PromptRendering.Render(configuration.UserMessage, input, invocation.Parameters);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (provider.InvocationTimeout <= TimeSpan.Zero || provider.InvocationTimeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("The registered agent timeout is invalid.");
        deadline.CancelAfter(provider.InvocationTimeout);
        await Record(ExecutionEventKinds.AgentStarted, new
        {
            provider = provider.Descriptor,
            timeoutSeconds = provider.InvocationTimeout.TotalSeconds,
            requestedTools = configuration.Tools.Select((binding, index) => new { functionName = $"tool_{index}", binding.BindingId, binding.ContractHash })
            , skills = assignedSkills.Select(skill => new { skill.Id, skill.Version, skill.ContentHash })
        });
        try
        {
            await using var scope = tools.OpenScope(invocation);
            var allowed = await scope.ListToolsAsync(deadline.Token);
            foreach (var requirement in configuration.Tools)
            {
                if (allowed.Any(t => t.BindingId == requirement.BindingId)) continue;
                await Record(ExecutionEventKinds.ToolDenied, new { requirement.BindingId, reason = "Agent tool requirement has no effective host grant." });
                throw new ToolAccessDeniedException();
            }
            var functions = new List<AITool>();
            foreach (var binding in allowed)
            {
                var name = $"tool_{functions.Count}";
                functions.Add(AIFunctionFactory.Create(async (string text, CancellationToken token) =>
                    await scope.InvokeAsync(binding.BindingId, text, token), name,
                    description: $"Approved text operation: {binding.DisplayName}. Binding: {binding.BindingId}."));
            }
            using var client = new FunctionInvokingChatClient(new RecordingClient(provider.Create(), invocation))
            {
                MaximumIterationsPerRequest = 8,
                MaximumConsecutiveErrorsPerRequest = 1,
                AllowConcurrentInvocation = false,
                IncludeDetailedErrors = false
            };
            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Name = invocation.NodeId,
                UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = functions,
                MaxOutputTokens = 1024,
                Reasoning = configuration.ReasoningEffort is { } effort
                    ? new ReasoningOptions { Effort = effort switch
                    {
                        AgentReasoningEffort.Low => Microsoft.Extensions.AI.ReasoningEffort.Low,
                        AgentReasoningEffort.Medium => Microsoft.Extensions.AI.ReasoningEffort.Medium,
                        AgentReasoningEffort.High => Microsoft.Extensions.AI.ReasoningEffort.High,
                        _ => throw new InvalidOperationException("Unsupported reasoning effort.")
                    } }
                    : null
            }
            });
            var response = await agent.RunAsync(userMessage, cancellationToken: deadline.Token);
            if (string.IsNullOrWhiteSpace(response.Text) || response.Text.Length > 16_384)
                throw new InvalidOperationException("The agent did not produce bounded final text.");
            await Record(ExecutionEventKinds.AgentCompleted, new { output = response.Text });
            return response.Text;
        }
        catch (OperationCanceledException)
        {
            await Record(ExecutionEventKinds.AgentFailed, new { reason = "Agent cancelled or timed out. Provider outcome may be unknown; no automatic retry." });
            if (cancellationToken.IsCancellationRequested) throw;
            throw new InvalidOperationException("The agent operation timed out.");
        }
        catch (AgentProviderRequestException failure)
        {
            await Record(ExecutionEventKinds.AgentFailed, new { reason = "Configured provider request failed; no automatic retry.",
                failureKind = failure.FailureKind.ToString(), statusCode = failure.StatusCode });
            throw;
        }
        catch
        {
            await Record(ExecutionEventKinds.AgentFailed, new { reason = "Agent execution failed." });
            throw;
        }

        ValueTask Record(string kind, object payload) => invocation.Record(kind, JsonSerializer.Serialize(payload));
    }

    private sealed class RecordingClient(IChatClient inner, NodeInvocationContext invocation) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            var modelCallId = Guid.NewGuid();
            await Record(ExecutionEventKinds.ModelRequested, new
            {
                modelCallId,
                instructions = options?.Instructions,
                reasoningEffort = options?.Reasoning?.Effort?.ToString(),
                messages = Project(snapshot),
                tools = options?.Tools?.Select(t => t.Name).ToArray()
            });
            var response = await InnerClient.GetResponseAsync(snapshot, options, cancellationToken).WaitAsync(cancellationToken);
            await Record(ExecutionEventKinds.ModelResponded, new
            {
                modelCallId,
                response.ModelId,
                response.ResponseId,
                finishReason = response.FinishReason?.ToString(),
                usage = response.Usage is { } usage ? new { usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount } : null,
                messages = Project(response.Messages)
            });
            return response;
        }

        private ValueTask Record(string kind, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            if (json.Length > 262_144) throw new InvalidOperationException("Model trace exceeds the local capture budget.");
            return invocation.Record(kind, json);
        }

        private static object[] Project(IEnumerable<ChatMessage> messages) => messages.Select(message => (object)new
        {
            role = message.Role.ToString(),
            content = message.Contents.Select(content => content switch
            {
                TextContent text => (object)new { kind = "text", text = text.Text },
                FunctionCallContent call => new { kind = "function-call", call.CallId, call.Name, call.Arguments },
                FunctionResultContent result => new { kind = "function-result", result.CallId, result = result.Result?.ToString() },
                _ => new { kind = "unsupported-content" }
            }).ToArray()
        }).ToArray();
    }
}
