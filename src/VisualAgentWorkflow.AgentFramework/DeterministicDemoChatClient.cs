using Microsoft.Extensions.AI;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Scripted fixture: calls the first assigned function once, or echoes. It does not interpret instructions.</summary>
public sealed class DeterministicDemoChatClient : IChatClient
{
    public static AgentProviderRegistration Registration() => new(
        new("demo.scripted", "1.0.0", "Deterministic demo agent", "scripted-text-v1", true), () => new DeterministicDemoChatClient());

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = messages.ToArray();
        var result = snapshot.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        if (result is not null) return Task.FromResult(Response(new(ChatRole.Assistant, result.Result?.ToString() ?? ""), ChatFinishReason.Stop));
        var input = snapshot.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var tool = options?.Tools?.FirstOrDefault();
        if (tool is null) return Task.FromResult(Response(new(ChatRole.Assistant, $"Demo agent: {input}"), ChatFinishReason.Stop));
        var arguments = tool is AIFunctionDeclaration function
            ? BuildArguments(function.JsonSchema, input)
            : new Dictionary<string, object?> { ["text"] = input };
        return Task.FromResult(Response(new(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool.Name, arguments)]), ChatFinishReason.ToolCalls));
    }

    private static Dictionary<string, object?> BuildArguments(JsonElement schema, string input)
    {
        var properties = schema.GetProperty("properties");
        var required = schema.TryGetProperty("required", out var requiredElements)
            ? requiredElements.EnumerateArray().Select(element => element.GetString()!).ToHashSet(StringComparer.Ordinal)
            : properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        return properties.EnumerateObject().Where(property => required.Contains(property.Name)).ToDictionary(
            property => property.Name,
            property => property.Value.GetProperty("type").GetString() switch
            {
                "string" => (object?)input,
                "integer" => 1L,
                "number" => 1d,
                "boolean" => true,
                _ => throw new InvalidOperationException("The scripted agent fixture encountered an unsupported schema.")
            }, StringComparer.Ordinal);
    }

    private static ChatResponse Response(ChatMessage message, ChatFinishReason reason) => new(message)
    { ModelId = "scripted-text-v1", FinishReason = reason };

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The deterministic fixture supports complete responses only.");
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
