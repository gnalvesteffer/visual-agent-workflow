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
    private const string ModelResponse = "ORANGE";
    private const string ExpectedResponse = "EGNARO";

    [RealModelFact]
    [Trait("Category", "RealModel")]
    public async Task LocalOpenAiCompatibleModelExecutesThroughTheWorkflowPipeline()
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
            Version = "1.0.0",
            Model = model,
            DisplayName = "Local CI model",
            Endpoint = endpointUri,
            AllowInsecureLoopback = true,
            InvocationTimeout = TimeSpan.FromMinutes(2)
        };
        var provider = OpenAIProviders.Create(options, () => "local-inference-test-key");
        var broker = new McpToolBroker(BrokerTests.Registrations(), new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.reverse");
        var agents = new AgentNodeExecutor([provider], broker);
        var definition = PipelineEditing.ReplaceNode(PipelineTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.Agent, "1.0.0", Agent: new(provider.Descriptor.ProviderId, provider.Descriptor.Version,
                $"Output exactly the word {ModelResponse}, with no punctuation.", [])));
        definition = GraphEditing.InsertNodeAfter(definition, "step-1", "out",
            new(NodeTypes.McpTool, "1.0.0", new(reverse.BindingId, reverse.ContractHash)));
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner(broker, agents).ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "local-model-mcp-workflow"),
            "What word should the workflow pass to its next node? /no_think",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(ExpectedResponse, output.Trim());
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized &&
            HasStringProperty(evt.Data, "bindingId", reverse.BindingId) &&
            HasStringProperty(evt.Data, "input", ModelResponse));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted &&
            HasStringProperty(evt.Data, "output", ExpectedResponse));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted);
    }

    private static bool HasStringProperty(string? data, string property, string expected)
    {
        if (data is null) return false;
        using var json = JsonDocument.Parse(data);
        return json.RootElement.TryGetProperty(property, out var value) && value.GetString() == expected;
    }
}

public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VAW_RUN_REAL_MODEL_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set VAW_RUN_REAL_MODEL_TESTS=1 and provide a local OpenAI-compatible model endpoint to run inference tests.";
    }
}
