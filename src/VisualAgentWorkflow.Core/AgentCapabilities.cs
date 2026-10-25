using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

public sealed record AgentConfiguration(string ProviderId, string ProviderVersion, string Instructions, ImmutableArray<ToolBindingRequest> Tools)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PromptTemplate? UserMessage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<SkillReference>? Skills { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AgentReasoningEffort? ReasoningEffort { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ReasoningEffortParameterId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxOutputTokens { get; init; }
}

// Preserve the persisted numeric values from the original schema: 0=Low, 1=Medium, 2=High.
public enum AgentReasoningEffort { Low = 0, Medium = 1, High = 2, Off = 3 }

public static class AgentOutputTokenLimits
{
    public const int LegacyDefault = 1024;
    public const int Minimum = 1;
    public const int Maximum = 16_384;
}

/// <summary>Only literal text and explicit stable references are rendered. No expression evaluation occurs.</summary>
public sealed record PromptPart(string Kind, string Value);
public sealed record PromptTemplate(ImmutableArray<PromptPart> Parts);
public sealed record SkillReference(string Id, string Version, string ContentHash);
public sealed record WorkflowSkill(string Id, string Version, string Name, string Instructions, string ContentHash)
{
    public static WorkflowSkill Create(string id, string version, string name, string instructions) =>
        new(id, version, name, instructions, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions))));
}

public sealed record SkillLibraryItem(WorkflowSkill Skill, string Folder);
public interface ISkillLibrary
{
    IReadOnlyList<SkillLibraryItem> List();
}

public static class PromptRendering
{
    public static string Render(PromptTemplate template, string input, IReadOnlyDictionary<string, string>? parameters)
    {
        var text = new StringBuilder();
        foreach (var part in template.Parts)
        {
            var value = part.Kind switch
            {
                "literal" => part.Value,
                "input" => input,
                "parameter" when parameters is not null && parameters.TryGetValue(part.Value, out var parameter) => parameter,
                _ => throw new InvalidOperationException("A prompt reference is unavailable for this run.")
            };
            if (text.Length + value.Length > 16_384) throw new InvalidOperationException("Rendered user message exceeds 16,384 characters.");
            text.Append(value);
        }
        return text.ToString();
    }
}
public sealed record AgentProviderDescriptor(string ProviderId, string Version, string DisplayName, string ModelId, bool IsDeterministic)
{
    public bool SupportsReasoningEffort { get; init; }
}

public interface IAgentProviderCatalog
{
    Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync();
}

public interface IAgentNodeExecutor : IAgentProviderCatalog
{
    Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default);
}

public static class NodeToolRequirements
{
    public static IReadOnlyList<ToolBindingRequest> For(NodeDefinition node) => node.Agent is { } agent
        ? agent.Tools : node.Tool is { } tool ? [tool] : [];
}
