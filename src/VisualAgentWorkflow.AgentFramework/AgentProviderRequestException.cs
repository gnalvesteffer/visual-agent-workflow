namespace VisualAgentWorkflow.AgentFramework;

public enum AgentProviderFailureKind { Transport, Authentication, RateLimited, Unavailable, InvalidRequest, InvalidResponse }

/// <summary>Safe provider failure metadata. Deliberately excludes vendor bodies, credentials and inner exceptions.</summary>
public sealed class AgentProviderRequestException(AgentProviderFailureKind failureKind, int? statusCode = null)
    : InvalidOperationException("The configured model provider request failed.")
{
    public AgentProviderFailureKind FailureKind { get; } = failureKind;
    public int? StatusCode { get; } = statusCode;
}
