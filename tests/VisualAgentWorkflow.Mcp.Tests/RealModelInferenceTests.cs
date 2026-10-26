using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;
using VisualAgentWorkflow.Providers.OpenAI;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

public sealed class RealModelInferenceTests
{
    private const string ExpectedResponse = "2";

    [RealModelFact]
    [Trait("Category", "RealModel")]
    public async Task LocalOpenAiCompatibleModelExecutesThroughTheWorkflow()
    {
        var endpoint = Environment.GetEnvironmentVariable("VAW_REAL_MODEL_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("VAW_REAL_MODEL_NAME");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttp || !endpointUri.IsLoopback)
            throw new InvalidOperationException("Real-model tests require an HTTP loopback endpoint.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Real-model tests require VAW_REAL_MODEL_NAME.");

        var options = new OpenAIProviderOptions
        {
            ProviderId = "openai.local-ci",
            Model = model,
            DisplayName = "Local CI model",
            Endpoint = endpointUri,
            AllowInsecureLoopback = true,
            InvocationTimeout = TimeSpan.FromMinutes(2)
        };
        var provider = OpenAIProviders.Create(options, () => "local-inference-test-key");
        var broker = new McpToolBroker(BrokerTests.Registrations(), new DemoToolPolicy());
        var submitAnswer = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.submit-answer");
        var agents = new AgentNodeExecutor([provider], broker);
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.22" };
        var definition = baseline with
        {
            Nodes = baseline.Nodes
                .SetItem("step-1", new(NodeTypes.Agent, "1.0.0", Agent: new(provider.Descriptor.ProviderId, null,
                    "Solve the arithmetic question. You must call the tool named tool_0 exactly once, " +
                    "with text set to only the final base-10 integer. Do not answer in prose.",
                    [new ToolBindingRequest(submitAnswer.BindingId, submitAnswer.ContractHash)])
                {
                    MaxOutputTokens = 64,
                    MaxTurns = 4,
                    MaxTotalTokens = 4096
                }))
        };
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner(broker, agents).ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "local-model-arithmetic-workflow"),
            "What is 1+1? /no_think",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(ExpectedResponse, output);
        Assert.InRange(events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested), 2, 4);
        Assert.InRange(events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded), 2, 4);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized &&
            HasStringProperty(evt.Data, "bindingId", submitAnswer.BindingId) &&
            HasStringProperty(evt.Data, "input", ExpectedResponse));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted &&
            HasStringProperty(evt.Data, "output", ExpectedResponse));
        using var completed = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted).Data!);
        Assert.Equal(ExpectedResponse, completed.RootElement.GetProperty("output").GetString());
        using var started = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted).Data!);
        Assert.Equal(4096, started.RootElement.GetProperty("maxTotalTokens").GetInt32());
        var observedTokenCount = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelResponded).Sum(evt =>
        {
            using var response = JsonDocument.Parse(evt.Data!);
            var count = response.RootElement.GetProperty("usage").GetProperty("TotalTokenCount").GetInt32();
            Assert.True(count > 0);
            return count;
        });
        Assert.InRange(observedTokenCount, 1, 4096);
    }

    private static bool HasStringProperty(string? data, string property, string expected)
    {
        if (data is null) return false;
        using var json = JsonDocument.Parse(data);
        return json.RootElement.TryGetProperty(property, out var value) && ContainsString(value, expected);
    }

    private static bool ContainsString(JsonElement value, string expected) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == expected,
        JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsString(property.Value, expected)),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsString(item, expected)),
        _ => false
    };
}

public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VAW_RUN_REAL_MODEL_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set VAW_RUN_REAL_MODEL_TESTS=1 and provide a local OpenAI-compatible model endpoint to run inference tests.";
    }
}
