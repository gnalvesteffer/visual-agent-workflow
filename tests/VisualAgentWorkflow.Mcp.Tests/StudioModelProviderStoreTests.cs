using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

public sealed class StudioModelProviderStoreTests
{
    [Fact]
    public async Task TestingUnsavedAndSavedConnectionsUsesOneBoundedRequestWithoutSavingSecrets()
    {
        const string key = "test-key-never-persisted-by-test";
        var directory = Path.Combine(Path.GetTempPath(), "vaw-model-test-" + Guid.NewGuid().ToString("N"));
        var requests = 0;
        string? authorization = null;
        string? requestBody = null;
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var serviceProvider = services.BuildServiceProvider();
        var broker = new McpToolBroker([], new ExplicitToolAuthorizationPolicy([]));
        var executor = new AgentNodeExecutor([], broker);
        try
        {
            var store = new StudioModelProviderStore(directory, serviceProvider.GetRequiredService<IDataProtectionProvider>(),
                executor, broker, [], () => new TestHandler(async (request, _) =>
                {
                    requests++;
                    authorization = request.Headers.Authorization?.ToString();
                    requestBody = await request.Content!.ReadAsStringAsync();
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"id":"test","object":"chat.completion","created":1,"model":"served","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}""", Encoding.UTF8, "application/json")
                    };
                }));

            var test = await store.TestConnectionAsync("local.test", "Local test", "served-model",
                "https://models.example/v1/", 10, key, false);
            Assert.True(test.Succeeded, test.Message);
            Assert.Empty(store.List());
            Assert.Equal(1, requests);
            Assert.Equal("Bearer " + key, authorization);
            using (var body = JsonDocument.Parse(requestBody!))
            {
                Assert.Equal("served-model", body.RootElement.GetProperty("model").GetString());
                var maxTokens = body.RootElement.TryGetProperty("max_completion_tokens", out var modern)
                    ? modern.GetInt32() : body.RootElement.GetProperty("max_tokens").GetInt32();
                Assert.Equal(1, maxTokens);
            }

            store.Save("local.test", "Local test", "served-model", "https://models.example/v1/", 10, key, false);
            var savedTest = await store.TestSavedConnectionAsync("local.test");
            Assert.True(savedTest.Succeeded, savedTest.Message);
            Assert.Equal(2, requests);
            Assert.True(Assert.Single(store.List()).HasCredential);
            Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(directory, "model-providers.json")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderConnectionTestReturnsSanitizedAuthenticationFailure()
    {
        const string secret = "fixture-secret-do-not-return";
        var directory = Path.Combine(Path.GetTempPath(), "vaw-model-test-error-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var serviceProvider = services.BuildServiceProvider();
        var broker = new McpToolBroker([], new ExplicitToolAuthorizationPolicy([]));
        try
        {
            var store = new StudioModelProviderStore(directory, serviceProvider.GetRequiredService<IDataProtectionProvider>(),
                new AgentNodeExecutor([], broker), broker, [], () => new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"" + secret + "\"}")
                })));

            var result = await store.TestConnectionAsync("local.test", "Local", "served-model", "https://models.example/v1/", 10, secret, false);

            Assert.False(result.Succeeded);
            Assert.Contains("Authentication failed", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
            Assert.Empty(store.List());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnsavedConnectionNeverReusesSavedCredentialUnlessEditingExplicitlyRequestsIt()
    {
        const string savedKey = "saved-key-must-not-leak";
        var directory = Path.Combine(Path.GetTempPath(), "vaw-model-test-key-scope-" + Guid.NewGuid().ToString("N"));
        var authorizationHeaders = new List<string?>();
        var requestUris = new List<string?>();
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var serviceProvider = services.BuildServiceProvider();
        var broker = new McpToolBroker([], new ExplicitToolAuthorizationPolicy([]));
        try
        {
            var store = new StudioModelProviderStore(directory,
                serviceProvider.GetRequiredService<IDataProtectionProvider>(), new AgentNodeExecutor([], broker), broker, [],
                () => new TestHandler((request, _) =>
                {
                    authorizationHeaders.Add(request.Headers.Authorization?.ToString());
                    requestUris.Add(request.RequestUri?.ToString());
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"id":"test","choices":[{"message":{"role":"assistant","content":"OK"}}]}""")
                    });
                }));
            store.Save("openai.primary", "Existing connection", "old-model", "https://old.example/v1", 10, savedKey, false);

            var unsaved = await store.TestConnectionAsync("openai.primary", "New endpoint", "new-model",
                "https://new.example/v1", 10, "", false);
            var editing = await store.TestConnectionAsync("openai.primary", "Existing connection", "new-model",
                "https://old.example/v1", 10, "", false, keepSavedCredentialWhenBlank: true);

            Assert.True(unsaved.Succeeded, unsaved.Message);
            Assert.True(editing.Succeeded, editing.Message);
            Assert.Equal(2, authorizationHeaders.Count);
            Assert.Null(authorizationHeaders[0]);
            Assert.Equal("Bearer " + savedKey, authorizationHeaders[1]);
            Assert.Contains("new.example", requestUris[0], StringComparison.Ordinal);
            Assert.Contains("old.example", requestUris[1], StringComparison.Ordinal);
            Assert.DoesNotContain(savedKey, unsaved.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingAnExistingConnectionIdEditsTheCurrentModelEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-model-connections-" + Guid.NewGuid().ToString("N"));
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDataProtection();
        using var services = serviceCollection.BuildServiceProvider();
        var broker = new McpToolBroker([], new ExplicitToolAuthorizationPolicy([]));
        var executor = new AgentNodeExecutor([], broker);
        try
        {
            var store = new StudioModelProviderStore(directory,
                services.GetRequiredService<IDataProtectionProvider>(), executor, broker, []);

            store.Save("unsloth.local", "Unsloth", "model-a", "https://models.example/v1", 45, "first-key", false,
                contextWindowTokens: 32_768, inputCostPerMillionTokens: 0.2m, outputCostPerMillionTokens: 0.8m,
                cacheReadCostPerMillionTokens: 0.02m, cacheWriteCostPerMillionTokens: 0.1m);
            store.Save("unsloth.local", "Unsloth local", "model-b", "https://models.example/v1", 60, "", false,
                contextWindowTokens: 65_536, inputCostPerMillionTokens: 0.3m, outputCostPerMillionTokens: 1.2m,
                cacheReadCostPerMillionTokens: 0.03m, cacheWriteCostPerMillionTokens: 0.12m);

            var connection = Assert.Single(store.List());
            var registration = Assert.Single(await executor.ListProvidersAsync());
            Assert.Equal("unsloth.local", connection.ProviderId);
            Assert.Equal("model-b", connection.Model);
            Assert.Equal(60, connection.TimeoutSeconds);
            Assert.True(connection.HasCredential);
            Assert.Equal(65_536, connection.ContextWindowTokens);
            Assert.Equal(0.3m, connection.InputCostPerMillionTokens);
            Assert.Equal(1.2m, connection.OutputCostPerMillionTokens);
            Assert.Equal(0.03m, connection.CacheReadCostPerMillionTokens);
            Assert.Equal(0.12m, connection.CacheWriteCostPerMillionTokens);
            Assert.Equal("model-b", registration.ModelId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
