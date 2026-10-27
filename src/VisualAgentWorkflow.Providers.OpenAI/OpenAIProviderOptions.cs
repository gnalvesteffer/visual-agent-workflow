namespace VisualAgentWorkflow.Providers.OpenAI;

/// <summary>Trusted host settings, never workflow configuration. Credentials are supplied separately.</summary>
public sealed class OpenAIProviderOptions
{
    public required string ProviderId { get; init; }
    public required string Model { get; init; }
    public string DisplayName { get; init; } = "OpenAI chat model";
    public Uri Endpoint { get; init; } = new("https://api.openai.com/v1/");
    public TimeSpan InvocationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional context window metadata for observability; the API does not enforce this value.</summary>
    public int? ContextWindowTokens { get; init; }
    /// <summary>Optional USD per million input tokens for cost estimates shown in run observations.</summary>
    public decimal? InputCostPerMillionTokens { get; init; }
    /// <summary>Optional USD per million output tokens for cost estimates shown in run observations.</summary>
    public decimal? OutputCostPerMillionTokens { get; init; }
    /// <summary>Optional USD per million provider-reported cached input tokens.</summary>
    public decimal? CacheReadCostPerMillionTokens { get; init; }
    /// <summary>Optional USD per million provider-reported cache-write tokens.</summary>
    public decimal? CacheWriteCostPerMillionTokens { get; init; }
    /// <summary>Explicit opt-in for any plain-HTTP endpoint. Credentials are sent without transport encryption.</summary>
    public bool AllowInsecureHttp { get; init; }
    /// <summary>Legacy loopback-only option retained for source and configuration compatibility.</summary>
    public bool AllowInsecureLoopback { get; init; }
}
