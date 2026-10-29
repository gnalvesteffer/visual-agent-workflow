using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

public sealed record AgentConfiguration(string ProviderId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderVersion,
    string Instructions, ImmutableArray<ToolBindingRequest> Tools)
{
    /// <summary>Schema 1.46 immutable provider contract identity used for run admission.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProviderContractHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PromptTemplate? UserMessage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<SkillReference>? Skills { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AgentReasoningEffort? ReasoningEffort { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ReasoningEffortParameterId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxOutputTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MaxOutputTokensParameterId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxTotalTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MaxTotalTokensParameterId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxTurns { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TimeoutSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RequireStructuredResult { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<AgentProfileReference>? Delegates { get; init; }
    /// <summary>Exact immutable pure graph-function revisions this agent may invoke as tools.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<GraphFunctionConfiguration>? GraphFunctionTools { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<AgentExecutionRoute>? Routes { get; init; }
}

/// <summary>A portable, immutable specialist-agent preset. Host registrations still resolve providers and authorize tools.</summary>
public sealed record AgentProfile(string Id, string Version, string Name, string Description, string ProviderId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderVersion,
    string Instructions, ImmutableArray<ToolBindingRequest> Tools)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<WorkflowSkill>? Skills { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AgentReasoningEffort? ReasoningEffort { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxOutputTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<string>? Channels { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContentHash { get; init; }

    public static AgentProfile Create(string id, string version, string name, string description, string providerId,
        string instructions, ImmutableArray<ToolBindingRequest> tools,
        ImmutableArray<WorkflowSkill>? skills = null, AgentReasoningEffort? reasoningEffort = null, int? maxOutputTokens = null,
        ImmutableArray<string>? channels = null) =>
        Create(id, version, name, description, providerId, null, instructions, tools, skills, reasoningEffort, maxOutputTokens, channels);

    /// <summary>Preserves a legacy provider version only when reconstructing historical serialized profiles.</summary>
    public static AgentProfile Create(string id, string version, string name, string description, string providerId,
        string? providerVersion, string instructions, ImmutableArray<ToolBindingRequest> tools,
        ImmutableArray<WorkflowSkill>? skills = null, AgentReasoningEffort? reasoningEffort = null, int? maxOutputTokens = null,
        ImmutableArray<string>? channels = null)
    {
        var profile = new AgentProfile(id, version, name, description, providerId, providerVersion, instructions, tools)
        { Skills = skills, ReasoningEffort = reasoningEffort, MaxOutputTokens = maxOutputTokens, Channels = channels };
        var content = System.Text.Json.JsonSerializer.Serialize(profile with { ContentHash = null });
        return profile with { ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))) };
    }
}

/// <summary>A stable model-selectable execution route authored on an Agent node.</summary>
public sealed record AgentExecutionRoute(string Id, string Label, string Description);

public static class AgentExecutionRouteLimits
{
    public const int MaximumRoutes = 16;
    public const int MaximumIdLength = 64;
    public const int MaximumLabelLength = 128;
    public const int MaximumDescriptionLength = 512;
    public static string PinId(string routeId) => $"route.{routeId}";
}

public sealed record AgentProfileReference(string Id, string Version, string ContentHash);
public sealed record AgentProfileLibraryItem(AgentProfile Profile, string Folder);

public interface IAgentProfileLibrary
{
    IReadOnlyList<AgentProfileLibraryItem> List();
}

// Preserve the persisted numeric values from the original schema: 0=Low, 1=Medium, 2=High.
public enum AgentReasoningEffort { Low = 0, Medium = 1, High = 2, Off = 3 }

public static class AgentOutputTokenLimits
{
    public const int LegacyDefault = 1024;
    public const int Minimum = 1;
    public const int Maximum = 16_384;
}

public static class AgentTotalTokenLimits
{
    public const int AuthoringDefault = 4_000;
    public const int Minimum = 1;
    public const int Maximum = 100_000;
}

public static class AgentExecutionLimits
{
    public const int DefaultMaximumTurns = 8;
    /// <summary>Bounds model-driven correction feedback without automatically retrying a tool call.</summary>
    public const int MaximumRecoverableToolFeedbacks = 3;
    public const int UnlimitedTurns = -1;

    public static bool IsValidMaximumTurns(int value) => value == UnlimitedTurns || value >= 1;
    public static bool IsValidTimeoutSeconds(int value) => value > 0;
}

public static class AgentResultLimits
{
    public const int MaximumPayloadCharacters = 16_384;
}

public static class AgentGraphFunctionToolLimits
{
    public const int MaximumAssignedFunctions = 8;
    public const int MaximumCallsPerInvocation = 16;
    public const int MaximumEvaluatedNodesPerInvocation = 1024;
    public const int MaximumInputCharacters = 65_536;
    public const int MaximumOutputCharacters = 65_536;
}

public static class AgentResultStatuses
{
    public const string Success = "success";
    public const string ReportedFailure = "reported_failure";
    public const string TurnsExhausted = "turns_exhausted";
    public const string TokensExhausted = "tokens_exhausted";
    public const string TokenUsageUnavailable = "token_usage_unavailable";
    public const string Timeout = "timeout";
    public const string InvalidResult = "invalid_result";
    public const string ProviderError = "provider_error";
    public const string ToolError = "tool_error";
    public const string ExecutionError = "execution_error";
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
public sealed record AgentProviderDescriptor(string ProviderId, string DisplayName, string ModelId, bool IsDeterministic)
{
    /// <summary>Stable execution identity. Presentation, pricing, and telemetry fields are deliberately excluded.</summary>
    [JsonIgnore]
    public string ContractHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"agent-provider-contract/v1\u001f{ProviderId}\u001f{ModelId}\u001f{IsDeterministic}\u001f{SupportsReasoningEffort}\u001f{ContextWindowTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}")));
    public bool SupportsReasoningEffort { get; init; }
    /// <summary>Optional model context size supplied by the trusted provider host.</summary>
    public int? ContextWindowTokens { get; init; }
    /// <summary>Optional USD per million input tokens, for a labeled cost estimate.</summary>
    public decimal? InputCostPerMillionTokens { get; init; }
    /// <summary>Optional USD per million output tokens, for a labeled cost estimate.</summary>
    public decimal? OutputCostPerMillionTokens { get; init; }
    /// <summary>Optional USD per million provider-reported cached input tokens.</summary>
    public decimal? CacheReadCostPerMillionTokens { get; init; }
    /// <summary>Optional USD per million provider-reported cache-write tokens.</summary>
    public decimal? CacheWriteCostPerMillionTokens { get; init; }
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
