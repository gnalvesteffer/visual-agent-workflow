using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

[Collection(McpStdioCollection.Name)]
public class AgentExecutionTests
{
    private static WorkflowRevision Revision(params ToolBindingRequest[] tools)
    {
        var definition = PipelineEditing.ReplaceNode(PipelineTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.Agent, "1.0.0", Agent: new("demo.scripted", "1.0.0", "Use only assigned tools.", [.. tools])));
        return WorkflowRevision.Create(definition, "test");
    }

    [Fact]
    public async Task AgentReceivesAssignedSkillAndResolvedUserMessageInRecordedModelRequest()
    {
        var skill = WorkflowSkill.Create("arithmetic", "1", "Arithmetic", "Give one number.");
        var baseline = Revision().Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.8",
            Skills = ImmutableSortedDictionary<string, WorkflowSkill>.Empty.Add("arithmetic@1", skill),
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("context", new("Context", "default"))
                .Add("effort", new("Reasoning effort", "default") { Type = "choice", Choices = ["default", "low", "medium", "high"] }),
            Nodes = baseline.Nodes.SetItem("step-1", node with { Agent = node.Agent! with
            {
                Skills = [new("arithmetic", "1", skill.ContentHash)],
                UserMessage = new([new("literal", "Question: "), new("input", ""), new("literal", " / "), new("parameter", "context")]),
                ReasoningEffortParameterId = "effort"
            } })
        };
        var events = new List<ExecutionEvent>();
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var fixture = DeterministicDemoChatClient.Registration();
        var executor = new AgentNodeExecutor([fixture with { Descriptor = fixture.Descriptor with { SupportsReasoningEffort = true } }], broker);
        await new PipelineRunner(broker, executor).ExecuteWithParametersAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "1+1?", new Dictionary<string, string> { ["context"] = "integer arithmetic", ["effort"] = "high" },
            e => { events.Add(e); return ValueTask.CompletedTask; });
        var request = Assert.Single(events, e => e.Kind == ExecutionEventKinds.ModelRequested);
        Assert.Contains("Give one number.", request.Data);
        Assert.Contains("\"reasoningEffort\":\"High\"", request.Data);
        using var recorded = JsonDocument.Parse(request.Data!);
        var messageText = recorded.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("Question: 1+1? / integer arithmetic", messageText);
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.AgentStarted && e.Data!.Contains(skill.ContentHash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FrameworkAgentCallsOnlyItsAssignedMcpToolAndRecordsModelRounds()
    {
        var broker = BrokerTests.CreateBroker(policy: new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(b => b.BindingId == "demo.reverse");
        var agent = new AgentNodeExecutor([DeterministicDemoChatClient.Registration()], broker);
        var events = new List<ExecutionEvent>();
        var output = await new PipelineRunner(broker, agent).ExecuteAsync(Guid.NewGuid(), Revision(new ToolBindingRequest(reverse.BindingId, reverse.ContractHash)),
            "abc", e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.Equal("cba", output);
        Assert.Equal(2, events.Count(e => e.Kind == ExecutionEventKinds.ModelRequested));
        Assert.Equal(2, events.Count(e => e.Kind == ExecutionEventKinds.ModelResponded));
        var authorized = Assert.Single(events, e => e.Kind == ExecutionEventKinds.ToolAuthorized);
        Assert.Contains("demo.reverse", authorized.Data);
        Assert.DoesNotContain("demo.uppercase", authorized.Data);
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.AgentCompleted);
        Assert.All(events.Where(e => e.Kind.StartsWith("model.", StringComparison.Ordinal)), e => Assert.Equal("step-1", e.NodeId));
        Assert.Contains(events.Where(e => e.Kind == ExecutionEventKinds.ModelRequested), e => e.Data!.Contains("Use only assigned tools.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentWithNoToolsDoesNotDiscoverOrExposeConnectedServers()
    {
        var connections = 0;
        var registrations = BrokerTests.Registrations().Select(r => r with
        { Connect = _ => { connections++; throw new InvalidOperationException("Must not connect"); } });
        var broker = BrokerTests.CreateBroker(registrations, new DemoToolPolicy());
        var executor = new AgentNodeExecutor([DeterministicDemoChatClient.Registration()], broker);
        var events = new List<(string Kind, string? Data)>();
        var context = new NodeInvocationContext(Guid.NewGuid(), Revision(), "step-1", Guid.NewGuid(),
            (kind, data) => { events.Add((kind, data)); return ValueTask.CompletedTask; });
        Assert.Equal("Demo agent: abc", await executor.ExecuteAsync(context, "abc"));
        Assert.Equal(0, connections);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.ToolRequested);
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.ModelRequested && e.Data!.Contains("\"tools\":[]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeniedAgentRequirementFailsBeforeProviderCreation()
    {
        var descriptor = new ToolBindingRequest("demo.reverse", new string('0', 64));
        var broker = BrokerTests.CreateBroker(BrokerTests.Registrations(), new ExplicitToolAuthorizationPolicy([]));
        var created = 0;
        var registration = DeterministicDemoChatClient.Registration() with { Create = () => { created++; return new DeterministicDemoChatClient(); } };
        var executor = new AgentNodeExecutor([registration], broker);
        var events = new List<string>();
        var context = new NodeInvocationContext(Guid.NewGuid(), Revision(descriptor), "step-1", Guid.NewGuid(),
            (kind, _) => { events.Add(kind); return ValueTask.CompletedTask; });
        await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(context, "abc"));
        Assert.Equal(0, created);
        Assert.Contains(ExecutionEventKinds.ToolDenied, events);
        Assert.Contains(ExecutionEventKinds.AgentFailed, events);
        Assert.DoesNotContain(ExecutionEventKinds.ModelRequested, events);
    }

    [Fact]
    public async Task UnregisteredProviderVersionFailsBeforeAnyModelCall()
    {
        var definition = Revision().Definition;
        var node = definition.Nodes["step-1"];
        definition = definition with { Nodes = definition.Nodes.SetItem("step-1", node with { Agent = node.Agent! with { ProviderVersion = "2.0.0" } }) };
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var executor = new AgentNodeExecutor([DeterministicDemoChatClient.Registration()], broker);
        var events = new List<string>();
        var context = new NodeInvocationContext(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "step-1", Guid.NewGuid(),
            (kind, _) => { events.Add(kind); return ValueTask.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(context, "abc"));
        Assert.DoesNotContain(ExecutionEventKinds.ModelRequested, events);
    }

    [Fact]
    public async Task ProviderFailureIsRecordedWithoutExceptionDetails()
    {
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var registration = DeterministicDemoChatClient.Registration() with
        { Create = () => new CallbackClient((_, _, _) => throw new InvalidOperationException("sensitive-provider-detail")) };
        var executor = new AgentNodeExecutor([registration], broker);
        var events = new List<(string Kind, string? Data)>();
        var context = new NodeInvocationContext(Guid.NewGuid(), Revision(), "step-1", Guid.NewGuid(),
            (kind, data) => { events.Add((kind, data)); return ValueTask.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(context, "abc"));
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.AgentFailed);
        Assert.DoesNotContain(events, e => e.Data?.Contains("sensitive-provider-detail", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ActiveProviderCallCanBeCancelledWithoutACompletedEvent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = DeterministicDemoChatClient.Registration() with { Create = () => new CallbackClient(async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        }) };
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var executor = new AgentNodeExecutor([registration], broker);
        using var cancellation = new CancellationTokenSource();
        var events = new List<ExecutionEvent>();
        var execution = new PipelineRunner(broker, executor).ExecuteAsync(Guid.NewGuid(), Revision(), "abc",
            e => { events.Add(e); return ValueTask.CompletedTask; }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.AgentCompleted);
        Assert.Equal(ExecutionEventKinds.Cancelled, events[^1].Kind);
    }

    [Fact]
    public async Task ModelCannotExecuteAnUndeclaredFunctionByGuessingItsName()
    {
        var connections = 0;
        var broker = BrokerTests.CreateBroker(BrokerTests.Registrations().Select(r => r with
        { Connect = _ => { connections++; throw new InvalidOperationException("Must not connect"); } }), new DemoToolPolicy());
        var registration = DeterministicDemoChatClient.Registration() with { Create = () => new CallbackClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("guessed-call", "demo.reverse", new Dictionary<string, object?> { ["text"] = "abc" })]))
            { FinishReason = ChatFinishReason.ToolCalls })) };
        var executor = new AgentNodeExecutor([registration], broker);
        var events = new List<string>();
        var context = new NodeInvocationContext(Guid.NewGuid(), Revision(), "step-1", Guid.NewGuid(),
            (kind, _) => { events.Add(kind); return ValueTask.CompletedTask; });
        var error = await Record.ExceptionAsync(() => executor.ExecuteAsync(context, "abc"));
        Assert.NotNull(error);
        Assert.Equal(0, connections);
        Assert.DoesNotContain(ExecutionEventKinds.ToolAuthorized, events);
        Assert.DoesNotContain(ExecutionEventKinds.AgentCompleted, events);
    }

    private sealed class CallbackClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> callback) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => callback(messages, options, cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
