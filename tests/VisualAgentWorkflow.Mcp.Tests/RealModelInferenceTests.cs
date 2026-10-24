using System.Text.Json;
using System.Text.RegularExpressions;
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
        var submitAnswer = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.submit-answer");
        var agents = new AgentNodeExecutor([provider], broker);
        var definition = PipelineEditing.ReplaceNode(PipelineTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.Agent, "1.0.0", Agent: new(provider.Descriptor.ProviderId, provider.Descriptor.Version,
                "Solve the arithmetic question. You must submit the final numeric answer by calling " +
                "the submit_answer tool with only a base-10 integer. After the tool accepts it, answer " +
                "with only that number.", [new ToolBindingRequest(submitAnswer.BindingId, submitAnswer.ContractHash)])));
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner(broker, agents).ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "local-model-arithmetic-workflow"),
            "What is 1+1? /no_think",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        var answers = Regex.Matches(output, @"(?<!\d)-?\d+(?!\d)");
        Assert.NotEmpty(answers);
        Assert.Equal(ExpectedResponse, answers[answers.Count - 1].Value);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested));
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized &&
            HasStringProperty(evt.Data, "bindingId", submitAnswer.BindingId) &&
            HasStringProperty(evt.Data, "input", ExpectedResponse));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted &&
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
