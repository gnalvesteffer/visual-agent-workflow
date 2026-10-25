namespace VisualAgentWorkflow.Providers.OpenAI;

/// <summary>Removes the SDK's placeholder bearer credential when the endpoint was configured without a key.</summary>
internal sealed class OptionalApiKeyHandler(bool hasApiKey, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!hasApiKey) request.Headers.Authorization = null;
        return base.SendAsync(request, cancellationToken);
    }
}
