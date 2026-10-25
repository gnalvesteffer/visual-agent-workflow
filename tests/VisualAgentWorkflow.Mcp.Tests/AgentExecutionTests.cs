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
            SchemaVersion = "1.10",
            Skills = ImmutableSortedDictionary<string, WorkflowSkill>.Empty.Add("arithmetic@1", skill),
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("context", new("Context", "default"))
                .Add("effort", new("Reasoning effort", "default") { Type = "choice", Choices = ["default", "off", "low", "medium", "high"] }),
            Nodes = baseline.Nodes.SetItem("step-1", node with { Agent = node.Agent! with
            {
                Skills = [new("arithmetic", "1", skill.ContentHash)],
                UserMessage = new([new("literal", "Question: "), new("input", ""), new("literal", " / "), new("parameter", "context")]),
                ReasoningEffortParameterId = "effort",
                MaxOutputTokens = 4096
            } })
        };
        var events = new List<ExecutionEvent>();
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var fixture = DeterministicDemoChatClient.Registration();
        var executor = new AgentNodeExecutor([fixture with { Descriptor = fixture.Descriptor with { SupportsReasoningEffort = true } }], broker);
        await new PipelineRunner(broker, executor).ExecuteWithParametersAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "1+1?", new Dictionary<string, string> { ["context"] = "integer arithmetic", ["effort"] = "off" },
            e => { events.Add(e); return ValueTask.CompletedTask; });
        var request = Assert.Single(events, e => e.Kind == ExecutionEventKinds.ModelRequested);
        Assert.Contains("Give one number.", request.Data);
        Assert.Contains("\"reasoningEffort\":\"None\"", request.Data);
        Assert.Contains("\"maxOutputTokens\":4096", request.Data);
        using var recorded = JsonDocument.Parse(request.Data!);
        var messageText = recorded.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("Question: 1+1? / integer arithmetic", messageText);
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.AgentStarted && e.Data!.Contains(skill.ContentHash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParentCanInvokeOnlyItsPinnedProfileAndChildToolsUseDistinctAuditedInvocation()
    {
        var broker = BrokerTests.CreateBroker(policy: new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.reverse");
        var baseDefinition = Revision().Definition;
        var profile = AgentProfile.Create("specialist", "1.0.0", "Text specialist", "Reverses text exactly.",
            "demo.scripted", "1.0.0", "Specialist instructions.",
            [new ToolBindingRequest(reverse.BindingId, reverse.ContractHash) { DescriptionHash = reverse.DescriptionHash }]);
        var agent = baseDefinition.Nodes["step-1"].Agent! with
        {
            Instructions = "Parent instructions.",
            Delegates = [new(profile.Id, profile.Version, profile.ContentHash!)]
        };
        var definition = baseDefinition with
        {
            SchemaVersion = "1.11",
            AgentProfiles = ImmutableSortedDictionary<string, AgentProfile>.Empty.Add("specialist@1.0.0", profile),
            Nodes = baseDefinition.Nodes.SetItem("step-1", baseDefinition.Nodes["step-1"] with { Agent = agent })
        };
        var responses = new Dictionary<string, int>(StringComparer.Ordinal);
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((_, options, _) =>
            {
                var role = options?.Instructions?.Contains("Specialist instructions.", StringComparison.Ordinal) == true ? "child" : "parent";
                responses[role] = responses.GetValueOrDefault(role) + 1;
                if (role == "parent" && responses[role] == 1)
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("delegate-call", "delegate_0", new Dictionary<string, object?> { ["task"] = "reverse abc" })]))
                    { FinishReason = ChatFinishReason.ToolCalls });
                if (role == "child" && responses[role] == 1)
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("reverse-call", "tool_0", new Dictionary<string, object?> { ["text"] = "abc" })]))
                    { FinishReason = ChatFinishReason.ToolCalls });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, role == "child" ? "" : "Final: cba"))
                { FinishReason = ChatFinishReason.Stop });
            })
        };
        var events = new List<ExecutionEvent>();
        var output = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "abc",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Final: cba", output);
        Assert.Equal(2, responses["parent"]);
        Assert.Equal(2, responses["child"]);
        var parentInvocation = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "step-1").InvocationId;
        var childModelCalls = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.InvocationId != parentInvocation).ToArray();
        Assert.Equal(2, childModelCalls.Length);
        Assert.All(childModelCalls, childModelCall => Assert.Contains("Specialist instructions.", childModelCall.Data));
        var parentModelCalls = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.InvocationId == parentInvocation).ToArray();
        Assert.Equal(2, parentModelCalls.Length);
        Assert.All(parentModelCalls, parentModelCall => Assert.DoesNotContain("tool_0", parentModelCall.Data));
        Assert.Contains(parentModelCalls, parentModelCall => parentModelCall.Data!.Contains("delegate_0", StringComparison.Ordinal));
        var toolCall = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
        Assert.NotEqual(parentInvocation, toolCall.InvocationId);
        var delegatedStart = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted && evt.Data!.Contains(profile.ContentHash!, StringComparison.Ordinal));
        var delegatedCompletions = events.Where(evt => evt.Kind == ExecutionEventKinds.AgentCompleted && evt.Data!.Contains("delegatedProfileId", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(delegatedCompletions);
        Assert.NotEqual(parentInvocation, delegatedStart.InvocationId);
        Assert.All(delegatedCompletions, delegatedCompletion => Assert.Equal(delegatedStart.InvocationId, delegatedCompletion.InvocationId));
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
    public async Task SuccessfulToolCallIsWorkflowOutputWhenModelEndsWithoutText()
    {
        var broker = BrokerTests.CreateBroker(policy: new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.reverse");
        var responses = 0;
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((_, _, _) => Task.FromResult(++responses == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("reverse-call", "tool_0", new Dictionary<string, object?> { ["text"] = "abc" })]))
                  { FinishReason = ChatFinishReason.ToolCalls }
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.Stop }))
        };
        var executor = new AgentNodeExecutor([registration], broker);
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner(broker, executor).ExecuteAsync(Guid.NewGuid(),
            Revision(new ToolBindingRequest(reverse.BindingId, reverse.ContractHash)), "abc",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("cba", output);
        var completed = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted);
        Assert.Contains("successful-tool-result-fallback", completed.Data);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed);
    }

    [Fact]
    public async Task AgentFunctionDeclarationUsesPinnedTypedMcpSchema()
    {
        var broker = BrokerTests.CreateBroker(policy: new DemoToolPolicy());
        var addition = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.add-integers");
        var events = new List<ExecutionEvent>();
        var executor = new AgentNodeExecutor([DeterministicDemoChatClient.Registration()], broker);
        var output = await new PipelineRunner(broker, executor).ExecuteAsync(Guid.NewGuid(),
            Revision(new ToolBindingRequest(addition.BindingId, addition.ContractHash) { DescriptionHash = addition.DescriptionHash }), "2+2",
            e => { events.Add(e); return ValueTask.CompletedTask; });

        Assert.Equal("2", output);
        var authorized = Assert.Single(events, e => e.Kind == ExecutionEventKinds.ToolAuthorized);
        Assert.Contains("\"left\":1", authorized.Data);
        Assert.Contains("\"right\":1", authorized.Data);
        Assert.Contains(addition.DescriptionHash!, authorized.Data);
        var modelRequest = events.First(e => e.Kind == ExecutionEventKinds.ModelRequested);
        Assert.Contains("tool_0", modelRequest.Data);
        Assert.Contains("Adds two base-10 integers", modelRequest.Data);
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
