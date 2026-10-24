namespace VisualAgentWorkflow.Providers.OpenAI;

/// <summary>Trusted host settings, never workflow configuration. Credentials are supplied separately.</summary>
public sealed class OpenAIProviderOptions
{
    public required string ProviderId { get; init; }
    public required string Version { get; init; }
    public required string Model { get; init; }
    public string DisplayName { get; init; } = "OpenAI chat model";
    public Uri Endpoint { get; init; } = new("https://api.openai.com/v1/");
    public TimeSpan InvocationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool AllowInsecureLoopback { get; init; }
}
