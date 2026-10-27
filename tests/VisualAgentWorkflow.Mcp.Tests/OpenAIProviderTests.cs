using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Providers.OpenAI;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

public sealed class OpenAIProviderTests
{
    private const string Key = "fixture-credential-never-record";
    private static OpenAIProviderOptions Options(TimeSpan? timeout = null) => new()
    {
        ProviderId = "openai.fixture",
        Model = "fixture-model",
        InvocationTimeout = timeout ?? TimeSpan.FromSeconds(30)
    };
    private static WorkflowRevision Revision(ToolBindingRequest? tool = null, int? maxOutputTokens = null)
    {
        var graph = WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.Agent, "1.0.0", Agent: new("openai.fixture", null, "Fixture instructions", tool is null ? [] : [tool])));
        if (maxOutputTokens is { } limit)
            graph = graph with
            {
                SchemaVersion = "1.10",
                Nodes = graph.Nodes.SetItem("step-1",
                graph.Nodes["step-1"] with { Agent = graph.Nodes["step-1"].Agent! with { MaxOutputTokens = limit } })
            };
        return WorkflowRevision.Create(graph, "fixture");
    }

    [Fact]
    public void ModelConnectionTimeoutConfigurationAcceptsAnyPositiveIntegerSeconds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["openai.fixture:Enabled"] = "true",
            ["openai.fixture:Model"] = "fixture-model",
            ["openai.fixture:TimeoutSeconds"] = "180"
        }).Build();

        var registration = Assert.Single(OpenAIProviders.FromConfiguration(configuration));

        Assert.Equal(TimeSpan.FromSeconds(180), registration.InvocationTimeout);
        Assert.Throws<InvalidOperationException>(() => OpenAIProviders.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["openai.fixture:Enabled"] = "true",
                ["openai.fixture:Model"] = "fixture-model",
                ["openai.fixture:TimeoutSeconds"] = "0"
            }).Build()));
    }

    [Fact]
    public async Task ConfiguredAgentOutputLimitIsSentToTheCompatibleEndpoint()
    {
        var handler = new Handler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var budget = body.RootElement.TryGetProperty("max_completion_tokens", out var modernBudget) ? modernBudget : body.RootElement.GetProperty("max_tokens");
            Assert.Equal(4096, budget.GetInt32());
            return Reply();
        });
        var registration = OpenAIProviders.Create(Options(), () => Key, () => handler);
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));
        var events = new List<(string Kind, string? Data)>();

        Assert.Equal("protocol response", await executor.ExecuteAsync(Context(Revision(maxOutputTokens: 4096), events), "hello"));
    }
    private static HttpResponseMessage Reply(string text = "protocol response", int? cachedTokens = null,
        int? reasoningTokens = null, int? cacheWriteTokens = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = "chatcmpl-fixture",
            @object = "chat.completion",
            created = 1_700_000_000,
            model = "fixture-resolved-model",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10,
                prompt_tokens_details = new { cached_tokens = cachedTokens ?? 2, cache_write_tokens = cacheWriteTokens },
                completion_tokens_details = new { reasoning_tokens = reasoningTokens } }
        }), Encoding.UTF8, "application/json")
    };
    private static NodeInvocationContext Context(WorkflowRevision revision, List<(string Kind, string? Data)> events) => new(Guid.NewGuid(), revision, "step-1", Guid.NewGuid(),
        (kind, data) => { events.Add((kind, data)); return ValueTask.CompletedTask; });

    [Fact]
    public async Task OfficialSdkPreservesInstructionsModelOutputAndUsageWithoutCapturingCredentials()
    {
        var requests = 0;
        var handler = new Handler(async (request, _) =>
        {
            requests++;
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Key, request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());
            var budget = body.RootElement.TryGetProperty("max_completion_tokens", out var modernBudget) ? modernBudget : body.RootElement.GetProperty("max_tokens");
            Assert.Equal(1024, budget.GetInt32());
            Assert.Contains("Fixture instructions", body.RootElement.GetProperty("messages").GetRawText());
            Assert.Contains("hello", body.RootElement.GetProperty("messages").GetRawText());
            return Reply();
        });
        var registration = OpenAIProviders.Create(Options(), () => Key, () => handler);
        var events = new List<(string Kind, string? Data)>();
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));
        Assert.Equal("protocol response", await executor.ExecuteAsync(Context(Revision(), events), "hello"));
        Assert.Equal(1, requests);
        Assert.True(handler.Disposed);
        var catalog = JsonSerializer.Serialize(await executor.ListProvidersAsync());
        Assert.DoesNotContain(Key, catalog);
        Assert.DoesNotContain("api.openai.com", catalog);
        Assert.All(events, e => Assert.DoesNotContain(Key, e.Data ?? ""));
        using var response = JsonDocument.Parse(Assert.Single(events, e => e.Kind == ExecutionEventKinds.ModelResponded).Data!);
        Assert.Equal("fixture-resolved-model", response.RootElement.GetProperty("ModelId").GetString());
        Assert.Equal("chatcmpl-fixture", response.RootElement.GetProperty("ResponseId").GetString());
        Assert.Equal(10, response.RootElement.GetProperty("usage").GetProperty("TotalTokenCount").GetInt64());
        Assert.Equal(2, response.RootElement.GetProperty("usage").GetProperty("CachedInputTokenCount").GetInt64());
    }

    [Fact]
    public async Task OfficialSdkUsageBridgePreservesCachedInputAndReasoningTokenCounts()
    {
        var handler = new Handler((_, _) => Task.FromResult(Reply(cachedTokens: 4, reasoningTokens: 2, cacheWriteTokens: 1)));
        var registration = OpenAIProviders.Create(Options(), () => Key, () => handler);
        var events = new List<(string Kind, string? Data)>();
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));

        await executor.ExecuteAsync(Context(Revision(), events), "hello");

        using var response = JsonDocument.Parse(Assert.Single(events, e => e.Kind == ExecutionEventKinds.ModelResponded).Data!);
        var usage = response.RootElement.GetProperty("usage");
        Assert.Equal(4, usage.GetProperty("CachedInputTokenCount").GetInt64());
        Assert.Equal(2, usage.GetProperty("ReasoningTokenCount").GetInt64());
        Assert.Equal(1, usage.GetProperty("cacheWriteInputTokenCount").GetInt64());
    }

    [Fact]
    public async Task OptionalApiKeyIsOmittedFromRequestsWhenEndpointDoesNotRequireAuthentication()
    {
        var handler = new Handler(async (request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            return Reply();
        });
        var registration = OpenAIProviders.Create(Options(), () => "", () => handler);
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));

        Assert.Equal("protocol response", await executor.ExecuteAsync(Context(Revision(), []), "no auth required"));
        Assert.True(handler.Disposed);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresAreNotRetriedOrCopiedIntoEvidence(int status)
    {
        var calls = 0;
        var handler = new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"message\":\"" + Key + "\",\"type\":\"fixture_error\"}}", Encoding.UTF8, "application/json") });
        });
        var registration = OpenAIProviders.Create(Options(), () => Key, () => handler);
        var events = new List<(string Kind, string? Data)>();
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));
        var failure = await Assert.ThrowsAsync<AgentProviderRequestException>(() => executor.ExecuteAsync(Context(Revision(), events), "failure"));
        Assert.Equal(status, failure.StatusCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(Key, failure.ToString());
        Assert.Equal(1, calls);
        Assert.True(handler.Disposed);
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.AgentFailed);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.AgentCompleted);
        Assert.All(events, e => Assert.DoesNotContain(Key, e.Data ?? ""));
    }

    [Fact]
    public async Task CancellationStopsAnActiveSdkRequestAndDisposesItsTransport()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Reply(); });
        var registration = OpenAIProviders.Create(Options(), () => Key, () => handler);
        var events = new List<(string Kind, string? Data)>();
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));
        using var cancellation = new CancellationTokenSource();
        var execution = executor.ExecuteAsync(Context(Revision(), events), "cancel", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(handler.Disposed);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.AgentCompleted);
    }

    [Fact]
    public async Task DeniedNodeToolRequirementsPreventCreatingAnyProviderTransport()
    {
        var created = 0;
        var registration = OpenAIProviders.Create(Options(), () => Key, () => { created++; return new Handler((_, _) => Task.FromResult(Reply())); });
        var events = new List<(string Kind, string? Data)>();
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));
        await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(Context(Revision(new("not.granted", new string('0', 64))), events), "deny"));
        Assert.Equal(0, created);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.ModelRequested);
    }

    [Fact]
    public void ConfigurationIsDisabledByDefaultAndAllowsEndpointsWithoutCredentials()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAI:disabled:Enabled"] = "false",
            ["OpenAI:disabled:Model"] = "ignored",
            ["OpenAI:openai.fixture:Enabled"] = "true",
            ["OpenAI:openai.fixture:Version"] = "1.0.0",
            ["OpenAI:openai.fixture:Model"] = "fixture-model"
        });
        var registration = Assert.Single(OpenAIProviders.FromConfiguration(configuration.GetSection("OpenAI")));
        Assert.False(registration.Descriptor.IsDeterministic);
        Assert.DoesNotContain("api.openai.com", registration.ToString());
        configuration["OpenAI:openai.fixture:Enabled"] = "false";
        Assert.Empty(OpenAIProviders.FromConfiguration(configuration.GetSection("OpenAI")));
    }

    [Theory]
    [InlineData("http://remote.invalid/v1/")]
    [InlineData("https://user:password@remote.invalid/v1/")]
    [InlineData("https://remote.invalid/v1/?key=unsafe")]
    public void UnsafeEndpointSettingsAreRejectedWithoutNetwork(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => OpenAIProviders.Create(new()
        { ProviderId = "openai.fixture", Model = "fixture-model", Endpoint = new(endpoint) }, () => Key));
    }

    [Fact]
    public void ExplicitHttpOptInAllowsAnOpenAiCompatibleEndpointOutsideLoopback()
    {
        var registration = OpenAIProviders.Create(new()
        {
            ProviderId = "unsloth.local",
            DisplayName = "Unsloth",
            Model = "served-model",
            Endpoint = new("http://192.168.12.217:8888/v1"),
            AllowInsecureHttp = true
        }, () => "");

        Assert.Equal("served-model", registration.Descriptor.ModelId);
    }

    [Fact]
    public async Task OfficialSdkFunctionCallsUseOnlyTheAssignedRealMcpBroker()
    {
        var broker = new McpToolBroker(BrokerTests.Registrations(), new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(b => b.BindingId == "demo.reverse");
        var calls = 0;
        var handler = new Handler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (++calls == 1)
            {
                Assert.Equal("tool_0", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        id = "chatcmpl-tool",
                        @object = "chat.completion",
                        created = 1_700_000_000,
                        model = "fixture-model",
                        choices = new[] { new { index = 0, message = new { role = "assistant", content = (string?)null,
                            tool_calls = new[] { new { id = "call-one", type = "function", function = new { name = "tool_0", arguments = "{\"text\":\"abc\"}" } } } }, finish_reason = "tool_calls" } }
                    }), Encoding.UTF8, "application/json")
                };
            }
            Assert.Contains("cba", body.RootElement.GetProperty("messages").GetRawText());
            return Reply("cba");
        });
        var registration = OpenAIProviders.Create(Options(), () => Key, () => handler);
        var events = new List<(string Kind, string? Data)>();
        var executor = new AgentNodeExecutor([registration], broker);
        Assert.Equal("cba", await executor.ExecuteAsync(Context(Revision(new(reverse.BindingId, reverse.ContractHash)), events), "abc"));
        Assert.Equal(2, calls);
        Assert.Single(events, e => e.Kind == ExecutionEventKinds.ToolCompleted);
        Assert.Equal(2, events.Count(e => e.Kind == ExecutionEventKinds.ModelResponded));
        Guid Correlation((string Kind, string? Data) evt, string property)
        {
            using var json = JsonDocument.Parse(evt.Data!);
            return json.RootElement.GetProperty(property).GetGuid();
        }
        var requested = events.Where(e => e.Kind == ExecutionEventKinds.ModelRequested).Select(e => Correlation(e, "modelCallId")).ToArray();
        Assert.Equal(2, requested.Distinct().Count());
        Assert.Equal(requested, events.Where(e => e.Kind == ExecutionEventKinds.ModelResponded).Select(e => Correlation(e, "modelCallId")));
        Assert.Single(events.Where(e => e.Kind is ExecutionEventKinds.ToolRequested or ExecutionEventKinds.ToolAuthorized or ExecutionEventKinds.ToolCompleted)
            .Select(e => Correlation(e, "toolCallId")).Distinct());
        Assert.All(events, e => Assert.DoesNotContain(Key, e.Data ?? ""));
    }

    [Fact]
    public async Task TransportBoundsErrorBodiesAndDoesNotFollowRedirects()
    {
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.Redirect })
        {
            var calls = 0;
            var handler = new Handler((_, _) =>
            {
                calls++;
                var response = new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                if (status == HttpStatusCode.OK) response.Content.Headers.ContentLength = 1_048_577;
                else response.Headers.Location = new Uri("https://unrelated.invalid/");
                return Task.FromResult(response);
            });
            var registration = OpenAIProviders.Create(Options(), () => Key, () => handler);
            var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));
            var events = new List<(string Kind, string? Data)>();
            await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(Context(Revision(), events), "bounded"));
            Assert.Equal(1, calls);
            Assert.True(handler.Disposed);
            Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.AgentCompleted);
        }
    }

    [Fact]
    public async Task ProviderDeadlineFailsWithoutCompletionOrRetry()
    {
        var created = 0;
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply(); });
        var registration = OpenAIProviders.Create(Options(TimeSpan.FromMilliseconds(200)), () => Key, () => { created++; return handler; });
        var events = new List<(string Kind, string? Data)>();
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(Context(Revision(), events), "timeout"));
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.AgentLimitExceeded && e.Data!.Contains("\"outcome\":\"timeout\"", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.AgentFailed && e.Data!.Contains("\"outcome\":\"timeout\"", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.AgentCompleted);
        Assert.True(created <= 1);
        if (created == 1) Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task UnknownLengthResponsesCannotBypassTheTransportBudget()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(new byte[1_048_577])) }));
        var registration = OpenAIProviders.Create(Options(), () => Key, () => handler);
        var executor = new AgentNodeExecutor([registration], new McpToolBroker([], new ExplicitToolAuthorizationPolicy([])));
        var events = new List<(string Kind, string? Data)>();
        await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(Context(Revision(), events), "oversized"));
        Assert.True(handler.Disposed);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.ModelResponded);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
