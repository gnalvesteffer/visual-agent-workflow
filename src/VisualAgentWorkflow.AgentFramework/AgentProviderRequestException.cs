namespace VisualAgentWorkflow.AgentFramework;

public enum AgentProviderFailureKind { Transport, Authentication, RateLimited, Unavailable, InvalidRequest, InvalidResponse }

/// <summary>Safe provider failure metadata. Deliberately excludes vendor bodies, credentials and inner exceptions.</summary>
public sealed class AgentProviderRequestException(AgentProviderFailureKind failureKind, int? statusCode = null)
    : InvalidOperationException("The configured model provider request failed.")
{
    public AgentProviderFailureKind FailureKind { get; } = failureKind;
    public int? StatusCode { get; } = statusCode;

    /// <summary>Safe, provider-independent guidance for editor and run evidence surfaces.</summary>
    public string UserMessage => FailureKind switch
    {
        AgentProviderFailureKind.Authentication => "The model provider rejected authentication. Check the API key and endpoint permissions.",
        AgentProviderFailureKind.RateLimited => "The model provider rate-limited the request. Wait and retry when the endpoint permits it.",
        AgentProviderFailureKind.Unavailable => "The model provider is unavailable. Check that the endpoint is running and reachable.",
        AgentProviderFailureKind.InvalidRequest => "The model provider rejected the model or request settings. Check the model ID and chat-completions compatibility.",
        AgentProviderFailureKind.InvalidResponse => "The model provider returned an unsupported response. Check chat-completions compatibility.",
        _ => "The model provider could not be reached. Check the endpoint and local network configuration."
    } + (StatusCode is { } status ? $" (HTTP {status})." : string.Empty);
}
