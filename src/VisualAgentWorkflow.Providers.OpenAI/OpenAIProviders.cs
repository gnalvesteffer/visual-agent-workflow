using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Providers.OpenAI;

/// <summary>Opt-in host factories using the official OpenAI SDK and Microsoft.Extensions.AI bridge.</summary>
public static class OpenAIProviders
{
    public static AgentProviderRegistration Create(OpenAIProviderOptions options, Func<string> getApiKey) =>
        Create(options, getApiKey, () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    /// <summary>The host owns the transport factory; return a fresh handler per invocation.
    /// Custom transports must reject redirects and preserve TLS validation.</summary>
    public static AgentProviderRegistration Create(OpenAIProviderOptions options, Func<string> getApiKey, Func<HttpMessageHandler> createTransport)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getApiKey);
        ArgumentNullException.ThrowIfNull(createTransport);
        Validate(options);
        var descriptor = new AgentProviderDescriptor(options.ProviderId, options.Version, options.DisplayName, options.Model, false);
        return new(descriptor, () =>
        {
            var key = getApiKey();
            ValidateKey(key);
            var http = new HttpClient(new BoundedResponseHandler(createTransport())) { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                var sdk = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions
                {
                    Endpoint = options.Endpoint,
                    RetryPolicy = new ClientRetryPolicy(0),
                    NetworkTimeout = options.InvocationTimeout,
                    Transport = new HttpClientPipelineTransport(http)
                });
                return new OwnedChatClient(sdk.GetChatClient(options.Model).AsIChatClient(), http);
            }
            catch
            {
                http.Dispose();
                throw;
            }
        }) { InvocationTimeout = options.InvocationTimeout };
    }

    /// <summary>Reads named entries under a trusted host configuration section. Disabled entries create no clients.</summary>
    public static IReadOnlyList<AgentProviderRegistration> FromConfiguration(IConfiguration section)
    {
        var registrations = new List<AgentProviderRegistration>();
        foreach (var entry in section.GetChildren())
        {
            if (entry["Enabled"] is not { } enabledText) continue;
            if (!bool.TryParse(enabledText, out var enabled)) throw new InvalidOperationException("Invalid model provider enablement setting.");
            if (!enabled) continue;
            if (!int.TryParse(entry["TimeoutSeconds"] ?? "30", out var seconds) || seconds is < 1 or > 120)
                throw new InvalidOperationException("Model provider timeout must be 1–120 seconds.");
            if (!bool.TryParse(entry["AllowInsecureLoopback"] ?? "false", out var loopback))
                throw new InvalidOperationException("Invalid model provider loopback setting.");
            if (!Uri.TryCreate(entry["Endpoint"] ?? "https://api.openai.com/v1/", UriKind.Absolute, out var endpoint))
                throw new InvalidOperationException("Invalid model provider endpoint configuration.");
            ValidateKey(entry["ApiKey"]);
            var options = new OpenAIProviderOptions
            {
                ProviderId = entry.Key,
                Version = entry["Version"] ?? "",
                Model = entry["Model"] ?? "",
                DisplayName = entry["DisplayName"] ?? "OpenAI · " + entry.Key,
                Endpoint = endpoint,
                InvocationTimeout = TimeSpan.FromSeconds(seconds),
                AllowInsecureLoopback = loopback
            };
            registrations.Add(Create(options, () => entry["ApiKey"] ?? ""));
        }
        return registrations;
    }

    public static void ValidateOptions(OpenAIProviderOptions options, string apiKey)
    {
        Validate(options);
        ValidateKey(apiKey);
    }

    private static void Validate(OpenAIProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderId) || options.ProviderId.Length > 128 || options.ProviderId.StartsWith("demo.", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(options.Version) || options.Version.Length > 64 ||
            string.IsNullOrWhiteSpace(options.Model) || options.Model.Length > 128 || options.Model.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(options.DisplayName) || options.DisplayName.Length > 160)
            throw new ArgumentException("A bounded non-demo provider ID, version, model and display name are required.");
        var endpoint = options.Endpoint;
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.Scheme != "https" && !(options.AllowInsecureLoopback && endpoint.Scheme == "http" && endpoint.IsLoopback))
            throw new ArgumentException("Provider endpoints require HTTPS without user information, query or fragment; loopback HTTP needs explicit opt-in.");
        if (options.InvocationTimeout <= TimeSpan.Zero || options.InvocationTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options), "Model timeout must be positive and at most two minutes.");
    }

    private static void ValidateKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 4096 || key.Any(c => c <= ' ' || c > '~'))
            throw new InvalidOperationException("An enabled model provider requires a valid host credential.");
    }

    private sealed class OwnedChatClient(IChatClient inner, HttpClient http) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            try { return await base.GetResponseAsync(messages, options, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (ClientResultException error)
            {
                var kind = error.Status switch
                {
                    401 or 403 => AgentProviderFailureKind.Authentication,
                    429 => AgentProviderFailureKind.RateLimited,
                    >= 500 and <= 599 => AgentProviderFailureKind.Unavailable,
                    >= 400 and <= 499 => AgentProviderFailureKind.InvalidRequest,
                    >= 300 and <= 399 => AgentProviderFailureKind.InvalidResponse,
                    _ => AgentProviderFailureKind.Transport
                };
                throw new AgentProviderRequestException(kind, error.Status is >= 100 and <= 599 ? error.Status : null);
            }
            catch (System.Text.Json.JsonException) { throw new AgentProviderRequestException(AgentProviderFailureKind.InvalidResponse); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new AgentProviderRequestException(AgentProviderFailureKind.Transport);
            }
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The bounded provider adapter supports complete responses only.");

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) http.Dispose(); }
        }
    }
}
