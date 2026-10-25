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
    public async Task AgentTurnLimitIsRecordedAndPassedToTheFrameworkLoop()
    {
        var baseline = Revision().Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.21",
            Nodes = baseline.Nodes.SetItem("step-1", node with { Agent = node.Agent! with { MaxTurns = 1 } })
        };
        var events = new List<ExecutionEvent>();
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "bounded"))
            { FinishReason = ChatFinishReason.Stop }))
        };

        var result = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("bounded", result);
        Assert.NotNull(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested));
        using var started = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted).Data!);
        Assert.Equal(1, started.RootElement.GetProperty("maximumTurns").GetInt32());
    }

    [Fact]
    public async Task AgentAggregateTokenBudgetShrinksRemainingOutputAllowanceAcrossTurns()
    {
        var broker = BrokerTests.CreateBroker(policy: new DemoToolPolicy());
        var binding = (await broker.ListBindingsAsync()).Single(tool => tool.BindingId == "demo.reverse");
        var baseline = Revision(new ToolBindingRequest(binding.BindingId, binding.ContractHash)
        { DescriptionHash = binding.DescriptionHash }).Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.22",
            Nodes = baseline.Nodes.SetItem("step-1", node with { Agent = node.Agent! with { MaxOutputTokens = 6, MaxTotalTokens = 8 } })
        };
        var modelCall = 0;
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((_, _, _) =>
            {
                modelCall++;
                return Task.FromResult(modelCall == 1
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("reverse-call", "tool_0", new Dictionary<string, object?> { ["text"] = "abc" })]))
                    { FinishReason = ChatFinishReason.ToolCalls, Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1, TotalTokenCount = 4 } }
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
                    { FinishReason = ChatFinishReason.Stop, Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1, TotalTokenCount = 4 } });
            })
        };
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "reverse this",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("done", result);
        Assert.Equal(2, modelCall);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.Data!.Contains("\"maxOutputTokens\":6", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.Data!.Contains("\"maxOutputTokens\":4", StringComparison.Ordinal));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
    }

    [Fact]
    public async Task AgentAggregateTokenBudgetStopsBeforeExecutingToolsFromAnOverBudgetResponse()
    {
        var broker = BrokerTests.CreateBroker(policy: new DemoToolPolicy());
        var binding = (await broker.ListBindingsAsync()).Single(tool => tool.BindingId == "demo.reverse");
        var baseline = Revision(new ToolBindingRequest(binding.BindingId, binding.ContractHash)
        { DescriptionHash = binding.DescriptionHash }).Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.22",
            Nodes = baseline.Nodes.SetItem("step-1", node with { Agent = node.Agent! with { MaxOutputTokens = 16, MaxTotalTokens = 5 } })
        };
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("reverse-call", "tool_0", new Dictionary<string, object?> { ["text"] = "abc" })]))
            { FinishReason = ChatFinishReason.ToolCalls, Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 3, TotalTokenCount = 7 } }))
        };
        var events = new List<ExecutionEvent>();

        var failure = await Record.ExceptionAsync(() => new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "reverse this",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.NotNull(failure);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentLimitExceeded &&
            evt.Data!.Contains("tokens_exhausted", StringComparison.Ordinal) && evt.Data.Contains("7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentAggregateTokenBudgetFailsClosedWhenProviderOmitsUsage()
    {
        var broker = BrokerTests.CreateBroker(policy: new DemoToolPolicy());
        var binding = (await broker.ListBindingsAsync()).Single(tool => tool.BindingId == "demo.reverse");
        var baseline = Revision(new ToolBindingRequest(binding.BindingId, binding.ContractHash)
        { DescriptionHash = binding.DescriptionHash }).Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.22",
            Nodes = baseline.Nodes.SetItem("step-1", node with { Agent = node.Agent! with { MaxTotalTokens = 20 } })
        };
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("reverse-call", "tool_0", new Dictionary<string, object?> { ["text"] = "abc" })]))
            { FinishReason = ChatFinishReason.ToolCalls }))
        };
        var events = new List<ExecutionEvent>();

        var failure = await Record.ExceptionAsync(() => new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "reverse this",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.NotNull(failure);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed &&
            evt.Data!.Contains("token_usage_unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentTurnLimitBoundsRepeatedToolCallRounds()
    {
        var broker = BrokerTests.CreateBroker(policy: new DemoToolPolicy());
        var binding = (await broker.ListBindingsAsync()).Single(tool => tool.BindingId == "demo.reverse");
        var baseline = Revision(new ToolBindingRequest(binding.BindingId, binding.ContractHash)
        { DescriptionHash = binding.DescriptionHash }).Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.21",
            Nodes = baseline.Nodes.SetItem("step-1", node with { Agent = node.Agent! with { MaxTurns = 1 } })
        };
        var modelResponses = 0;
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((_, _, _) =>
            {
                modelResponses++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("reverse-call", "tool_0", new Dictionary<string, object?> { ["text"] = "abc" })]))
                { FinishReason = ChatFinishReason.ToolCalls });
            })
        };
        var events = new List<ExecutionEvent>();

        var failure = await Record.ExceptionAsync(() => new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "reverse this",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Equal(1, modelResponses);
        Assert.NotNull(failure);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentLimitExceeded && evt.Data!.Contains("turns_exhausted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentTimeoutRecordsLimitOutcomeAndDoesNotReportCompletion()
    {
        var baseline = Revision().Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.21",
            Nodes = baseline.Nodes.SetItem("step-1", node with { Agent = node.Agent! with { TimeoutSeconds = 1 } })
        };
        var events = new List<ExecutionEvent>();
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient(async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
            })
        };

        var error = await Record.ExceptionAsync(() => new PipelineRunner(broker,
            new AgentNodeExecutor([registration], broker)).ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.NotNull(error);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentLimitExceeded && evt.Data!.Contains("timeout", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed && evt.Data!.Contains("\"outcome\":\"timeout\"", StringComparison.Ordinal));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted);
    }

    [Fact]
    public async Task StructuredAgentResultReturnsReportedFailureAndTypedJsonOutput()
    {
        var baseline = Revision().Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.21",
            Nodes = baseline.Nodes
                .SetItem("step-1", node with { Agent = node.Agent! with { RequireStructuredResult = true } })
                .SetItem("output", baseline.Nodes["output"] with { OutputDataType = "json" })
        };
        var calls = 0;
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((_, options, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    Assert.Contains(options!.Tools!, tool => tool.Name == "submit_result");
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("result-call", "submit_result", new Dictionary<string, object?>
                        {
                            ["status"] = "failure",
                            ["payload"] = "{\"reason\":\"not found\",\"attempts\":2}"
                        })])) { FinishReason = ChatFinishReason.ToolCalls });
                }
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Submitted."))
                { FinishReason = ChatFinishReason.Stop });
            })
        };
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "find it",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        using var result = JsonDocument.Parse(output);
        Assert.Equal(AgentResultStatuses.ReportedFailure, result.RootElement.GetProperty("status").GetString());
        Assert.Equal("not found", result.RootElement.GetProperty("payload").GetProperty("reason").GetString());
        Assert.Equal("json", WorkflowPorts.Outputs(definition.Nodes["step-1"]).Single().DataType);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted && evt.Data!.Contains("reported_failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StructuredAgentTimeoutReturnsRoutableOutcomeInsteadOfFailingRun()
    {
        var baseline = Revision().Definition;
        var node = baseline.Nodes["step-1"];
        var definition = baseline with
        {
            SchemaVersion = "1.21",
            Nodes = baseline.Nodes
                .SetItem("step-1", node with { Agent = node.Agent! with { RequireStructuredResult = true, TimeoutSeconds = 1 } })
                .SetItem("output", baseline.Nodes["output"] with { OutputDataType = "json" })
        };
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient(async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
            })
        };
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        using var result = JsonDocument.Parse(output);
        Assert.Equal(AgentResultStatuses.Timeout, result.RootElement.GetProperty("status").GetString());
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentOutcome && evt.Data!.Contains("timeout", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed);
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
    public async Task ParentCanRunAssignedProfilesInParallelAndReceiveNamedTeamResults()
    {
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var baseDefinition = Revision().Definition;
        var profiles = new[]
        {
            AgentProfile.Create("researcher", "1", "Researcher", "Find relevant facts.", "demo.scripted", "1.0.0", "Research instructions.", []),
            AgentProfile.Create("reviewer", "1", "Reviewer", "Check the answer.", "demo.scripted", "1.0.0", "Review instructions.", [])
        };
        var agent = baseDefinition.Nodes["step-1"].Agent! with
        {
            Instructions = "Parent instructions.",
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version, profile.ContentHash!))]
        };
        var definition = baseDefinition with
        {
            SchemaVersion = "1.11",
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version, profile => profile, StringComparer.Ordinal),
            Nodes = baseDefinition.Nodes.SetItem("step-1", baseDefinition.Nodes["step-1"] with { Agent = agent })
        };
        var parentCalls = 0;
        var activeMembers = 0;
        var maximumConcurrentMembers = 0;
        var bothMembersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient(async (_, options, cancellationToken) =>
            {
                var instructions = options?.Instructions ?? "";
                if (instructions == "Parent instructions.")
                {
                    if (Interlocked.Increment(ref parentCalls) == 1)
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            [new FunctionCallContent("team-call", "team_0", new Dictionary<string, object?> { ["task"] = "Review the proposal." })]))
                        { FinishReason = ChatFinishReason.ToolCalls };
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Team review complete."))
                    { FinishReason = ChatFinishReason.Stop };
                }

                var member = instructions.StartsWith("Research", StringComparison.Ordinal) ? "Researcher" : "Reviewer";
                var active = Interlocked.Increment(ref activeMembers);
                UpdateMaximum(ref maximumConcurrentMembers, active);
                if (active == 2) bothMembersStarted.TrySetResult();
                try
                {
                    await bothMembersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, member + " result."))
                    { FinishReason = ChatFinishReason.Stop };
                }
                finally { Interlocked.Decrement(ref activeMembers); }
            })
        };
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "Analyze this.",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Team review complete.", result);
        Assert.Equal(2, maximumConcurrentMembers);
        Assert.Equal(events.OrderBy(evt => evt.Sequence).ToArray(), events.ToArray());
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamFailed);
        var completedTeam = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted);
        using var completedTeamData = JsonDocument.Parse(completedTeam.Data!);
        var teamInvocation = completedTeamData.RootElement.GetProperty("teamInvocationId").GetGuid();
        using var teamResult = JsonDocument.Parse(completedTeamData.RootElement.GetProperty("output").GetString()!);
        Assert.Equal("Researcher result.", teamResult.RootElement[0].GetProperty("output").GetString());
        Assert.Equal("Reviewer result.", teamResult.RootElement[1].GetProperty("output").GetString());
        var parentInvocation = Assert.Single(events, item => item.Kind == ExecutionEventKinds.NodeStarted && item.NodeId == "step-1").InvocationId;
        var childRequests = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.InvocationId != parentInvocation).ToArray();
        Assert.Equal(2, childRequests.Length);
        Assert.Equal(2, childRequests.Select(evt => evt.InvocationId).Distinct().Count());
        Assert.All(childRequests, request => Assert.Contains(teamInvocation.ToString(), request.Data));
        var secondParentRequest = events.Last(evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.InvocationId == parentInvocation);
        Assert.Contains("Researcher result.", secondParentRequest.Data);
        Assert.Contains("Reviewer result.", secondParentRequest.Data);

        static void UpdateMaximum(ref int maximum, int candidate)
        {
            var observed = Volatile.Read(ref maximum);
            while (candidate > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }
    }

    [Fact]
    public async Task FailedTeamCancelsRemainingMembersAndDoesNotPublishPartialAggregate()
    {
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var baseDefinition = Revision().Definition;
        var profiles = new[]
        {
            AgentProfile.Create("failing", "1", "Failing", "Fails during review.", "demo.scripted", "1.0.0", "Failing instructions.", []),
            AgentProfile.Create("waiting", "1", "Waiting", "Waits for cancellation.", "demo.scripted", "1.0.0", "Waiting instructions.", [])
        };
        var agent = baseDefinition.Nodes["step-1"].Agent! with
        {
            Instructions = "Parent instructions.",
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version, profile.ContentHash!))]
        };
        var definition = baseDefinition with
        {
            SchemaVersion = "1.11",
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version, profile => profile, StringComparer.Ordinal),
            Nodes = baseDefinition.Nodes.SetItem("step-1", baseDefinition.Nodes["step-1"] with { Agent = agent })
        };
        var parentCalls = 0;
        var activeMembers = 0;
        var bothMembersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient(async (_, options, cancellationToken) =>
            {
                var instructions = options?.Instructions ?? "";
                if (instructions == "Parent instructions.")
                {
                    if (Interlocked.Increment(ref parentCalls) == 1)
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            [new FunctionCallContent("team-call", "team_0", new Dictionary<string, object?> { ["task"] = "Review this." })]))
                        { FinishReason = ChatFinishReason.ToolCalls };
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will review this manually."))
                    { FinishReason = ChatFinishReason.Stop };
                }

                if (Interlocked.Increment(ref activeMembers) == 2) bothMembersStarted.TrySetResult();
                try
                {
                    await bothMembersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    if (instructions == "Failing instructions.") throw new InvalidOperationException("sensitive provider detail");
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Should be cancelled."));
                }
                finally { Interlocked.Decrement(ref activeMembers); }
            })
        };
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "Review this proposal.",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("I will review this manually.", result);
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.AgentTeamFailed));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted);
        Assert.DoesNotContain(events, evt => evt.Data?.Contains("sensitive provider detail", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TeamChannelsShareMessagesBetweenMembersAndAreIsolatedPerRun()
    {
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var baseDefinition = Revision().Definition;
        var profiles = new[]
        {
            AgentProfile.Create("analyst", "1", "Writer", "Posts a finding.", "demo.scripted", "1.0.0", "Writer instructions.", [], channels: ["research"]),
            AgentProfile.Create("analyst", "2", "Reader", "Reads a finding.", "demo.scripted", "1.0.0", "Reader instructions.", [], channels: ["research"])
        };
        var agent = baseDefinition.Nodes["step-1"].Agent! with
        {
            Instructions = "Parent instructions.",
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version, profile.ContentHash!))]
        };
        var definition = baseDefinition with
        {
            SchemaVersion = "1.12",
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version, profile => profile, StringComparer.Ordinal),
            Nodes = baseDefinition.Nodes.SetItem("step-1", baseDefinition.Nodes["step-1"] with { Agent = agent })
        };
        var rounds = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var writerPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient(async (messages, options, cancellationToken) =>
            {
                var role = options?.Instructions switch
                {
                    "Parent instructions." => "parent",
                    "Writer instructions." => "writer",
                    "Reader instructions." => "reader",
                    _ => throw new InvalidOperationException("Unexpected team member.")
                };
                var task = messages.First(message => message.Role == ChatRole.User).Text;
                var round = rounds.AddOrUpdate(role + "|" + task, 1, (_, current) => current + 1);
                if (role == "parent")
                    return round == 1
                        ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            [new FunctionCallContent("team-call", "team_0", new Dictionary<string, object?> { ["task"] = task })]))
                          { FinishReason = ChatFinishReason.ToolCalls }
                        : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Team completed.")) { FinishReason = ChatFinishReason.Stop };
                if (role == "writer")
                {
                    if (round == 1)
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            [new FunctionCallContent("send-call", "channel_send", new Dictionary<string, object?>
                            { ["channel"] = "research", ["message"] = "finding-" + task })]))
                        { FinishReason = ChatFinishReason.ToolCalls };
                    writerPosted.TrySetResult();
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finding posted.")) { FinishReason = ChatFinishReason.Stop };
                }
                if (round == 1)
                {
                    await writerPosted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("read-call", "channel_read", new Dictionary<string, object?> { ["channel"] = "research" })]))
                    { FinishReason = ChatFinishReason.ToolCalls };
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finding reviewed.")) { FinishReason = ChatFinishReason.Stop };
            })
        };
        var executor = new PipelineRunner(broker, new AgentNodeExecutor([registration], broker));
        var definitionRevision = WorkflowRevision.Create(definition, "test");

        async Task<List<ExecutionEvent>> ExecuteRun(string task)
        {
            writerPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new List<ExecutionEvent>();
            var output = await executor.ExecuteAsync(Guid.NewGuid(), definitionRevision, task,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });
            Assert.Equal("Team completed.", output);
            return events;
        }

        var firstRun = await ExecuteRun("first");
        var secondRun = await ExecuteRun("second");

        foreach (var (events, expectedMessage) in new[] { (firstRun, "finding-first"), (secondRun, "finding-second") })
        {
            var written = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentChannelMessageSent);
            var read = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentChannelMessagesRead);
            using var sentData = JsonDocument.Parse(written.Data!);
            using var sentEventData = JsonDocument.Parse(sentData.RootElement.GetProperty("eventData").GetString()!);
            Assert.Equal(expectedMessage, sentEventData.RootElement.GetProperty("message").GetProperty("Content").GetString());
            using var readData = JsonDocument.Parse(read.Data!);
            using var readEventData = JsonDocument.Parse(readData.RootElement.GetProperty("eventData").GetString()!);
            var messages = readEventData.RootElement.GetProperty("messages");
            Assert.Equal(1, messages.GetArrayLength());
            Assert.Equal(expectedMessage, messages[0].GetProperty("Content").GetString());
            Assert.NotEqual(written.InvocationId, read.InvocationId);
            Assert.Equal(written.RunId, read.RunId);
            Assert.Equal(written.RevisionId, read.RevisionId);
            Assert.Equal(events.OrderBy(evt => evt.Sequence).ToArray(), events.ToArray());
            Assert.Equal(sentEventData.RootElement.GetProperty("coordinationId").GetGuid(),
                readEventData.RootElement.GetProperty("coordinationId").GetGuid());
            Assert.Equal("team", sentEventData.RootElement.GetProperty("coordinationScope").GetString());
        }

        Assert.NotEqual(firstRun[0].RunId, secondRun[0].RunId);
        Assert.Contains(firstRun, evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.Data!.Contains("channel_send", StringComparison.Ordinal));
        Assert.Contains(firstRun, evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.Data!.Contains("channel_read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TeamMemberCannotReadChannelOutsideItsPinnedMembership()
    {
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var baseDefinition = Revision().Definition;
        var profiles = new[]
        {
            AgentProfile.Create("sender", "1", "Sender", "Uses research.", "demo.scripted", "1.0.0", "Sender instructions.", [], channels: ["research"]),
            AgentProfile.Create("reviewer", "1", "Reviewer", "Uses review.", "demo.scripted", "1.0.0", "Reviewer instructions.", [], channels: ["review"])
        };
        var agent = baseDefinition.Nodes["step-1"].Agent! with
        {
            Instructions = "Parent instructions.",
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version, profile.ContentHash!))]
        };
        var definition = baseDefinition with
        {
            SchemaVersion = "1.12",
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version, profile => profile, StringComparer.Ordinal),
            Nodes = baseDefinition.Nodes.SetItem("step-1", baseDefinition.Nodes["step-1"] with { Agent = agent })
        };
        var parentCalls = 0;
        string? readerChannelSchema = null;
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((messages, options, _) =>
            {
                var instructions = options?.Instructions;
                if (instructions == "Parent instructions.")
                    return Task.FromResult(Interlocked.Increment(ref parentCalls) == 1
                        ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            [new FunctionCallContent("team-call", "team_0", new Dictionary<string, object?> { ["task"] = "Review." })]))
                          { FinishReason = ChatFinishReason.ToolCalls }
                        : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Team access check complete.")) { FinishReason = ChatFinishReason.Stop });
                if (instructions == "Reviewer instructions.")
                {
                    readerChannelSchema = options!.Tools!.OfType<AIFunctionDeclaration>().Single(tool => tool.Name == "channel_read").JsonSchema.GetRawText();
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("unauthorized-read", "channel_read", new Dictionary<string, object?> { ["channel"] = "research" })]))
                    { FinishReason = ChatFinishReason.ToolCalls });
                }
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "No channel action.")) { FinishReason = ChatFinishReason.Stop });
            })
        };
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "Review this.",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Team access check complete.", result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentChannelMessagesRead);
        Assert.Contains("review", readerChannelSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("research", readerChannelSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParentCoordinatesSequentialDelegateTurnsOverTheirSharedChannel()
    {
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var baseDefinition = Revision().Definition;
        var profiles = new[]
        {
            AgentProfile.Create("analyst", "1", "Researcher", "Posts evidence.", "demo.scripted", "1.0.0", "Writer instructions.", [], channels: ["research"]),
            AgentProfile.Create("analyst", "2", "Reviewer", "Reviews evidence.", "demo.scripted", "1.0.0", "Reader instructions.", [], channels: ["research"])
        };
        var agent = baseDefinition.Nodes["step-1"].Agent! with
        {
            Instructions = "Coordinator instructions.",
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version, profile.ContentHash!))]
        };
        var definition = baseDefinition with
        {
            SchemaVersion = "1.12",
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version, profile => profile, StringComparer.Ordinal),
            Nodes = baseDefinition.Nodes.SetItem("step-1", baseDefinition.Nodes["step-1"] with { Agent = agent })
        };
        var parentRounds = 0;
        var childRounds = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var registration = DeterministicDemoChatClient.Registration() with
        {
            Create = () => new CallbackClient((messages, options, _) =>
            {
                var role = options?.Instructions switch
                {
                    "Coordinator instructions." => "parent",
                    "Writer instructions." => "writer",
                    "Reader instructions." => "reader",
                    _ => throw new InvalidOperationException("Unexpected coordinator participant.")
                };
                if (role == "parent")
                {
                    var round = Interlocked.Increment(ref parentRounds);
                    return Task.FromResult(round switch
                    {
                        1 => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            [new FunctionCallContent("research-call", "delegate_0", new Dictionary<string, object?> { ["task"] = "Research the claim." })]))
                            { FinishReason = ChatFinishReason.ToolCalls },
                        2 => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            [new FunctionCallContent("review-call", "delegate_1", new Dictionary<string, object?> { ["task"] = "Review the research finding." })]))
                            { FinishReason = ChatFinishReason.ToolCalls },
                        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Research reviewed.")) { FinishReason = ChatFinishReason.Stop }
                    });
                }

                var roundNumber = childRounds.AddOrUpdate(role, 1, (_, current) => current + 1);
                if (role == "writer" && roundNumber == 1)
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("post-finding", "channel_send", new Dictionary<string, object?>
                        { ["channel"] = "research", ["message"] = "The claim matches the recorded evidence." })]))
                    { FinishReason = ChatFinishReason.ToolCalls });
                if (role == "reader" && roundNumber == 1)
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("read-finding", "channel_read", new Dictionary<string, object?> { ["channel"] = "research" })]))
                    { FinishReason = ChatFinishReason.ToolCalls });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, role == "writer" ? "Evidence posted." : "Evidence confirmed."))
                { FinishReason = ChatFinishReason.Stop });
            })
        };
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "Check the claim.",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Research reviewed.", output);
        Assert.Equal(3, parentRounds);
        var parentInvocation = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "step-1").InvocationId;
        var sent = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentChannelMessageSent);
        var read = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentChannelMessagesRead);
        Assert.NotEqual(parentInvocation, sent.InvocationId);
        Assert.NotEqual(parentInvocation, read.InvocationId);
        Assert.NotEqual(sent.InvocationId, read.InvocationId);
        using var readEnvelope = JsonDocument.Parse(read.Data!);
        using var readEventData = JsonDocument.Parse(readEnvelope.RootElement.GetProperty("eventData").GetString()!);
        Assert.Equal("agent-invocation", readEventData.RootElement.GetProperty("coordinationScope").GetString());
        Assert.Equal(parentInvocation, readEventData.RootElement.GetProperty("coordinationId").GetGuid());
        Assert.Equal(parentInvocation, readEventData.RootElement.GetProperty("parentInvocationId").GetGuid());
        Assert.Equal("The claim matches the recorded evidence.",
            readEventData.RootElement.GetProperty("messages")[0].GetProperty("Content").GetString());
        var parentRequests = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.InvocationId == parentInvocation).ToArray();
        Assert.Equal(3, parentRequests.Length);
        Assert.Contains("delegate_0", parentRequests[0].Data);
        Assert.Contains("delegate_1", parentRequests[1].Data);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted);
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
    public async Task AgentCanInvokeOnlyItsPinnedPureGraphFunctionAndRecordsReplayEvidence()
    {
        var registration = DeterministicDemoChatClient.Registration();
        var function = AppendSuffixFunction();
        var functionSnapshot = new GraphFunctionConfiguration(function, GraphFunctionJson.ComputeHash(function));
        var definition = AgentGraphFunctionWorkflow(registration.Descriptor.ProviderId, registration.Descriptor.Version, functionSnapshot);
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var calls = 0;
        var modelRequests = new List<string>();
        var registrationWithFunctionCall = registration with
        {
            Create = () => new CallbackClient((messages, options, _) =>
            {
                modelRequests.Add(JsonSerializer.Serialize(new
                {
                    declaredTools = options?.Tools?.Select(tool => tool.Name),
                    messages
                }));
                calls++;
                return Task.FromResult(calls == 1
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("function-call", "graph_function_0", new Dictionary<string, object?> { ["text"] = "Hello" })]))
                    { FinishReason = ChatFinishReason.ToolCalls }
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")) { FinishReason = ChatFinishReason.Stop });
            })
        };
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner(broker, new AgentNodeExecutor([registrationWithFunctionCall], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "Hello",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Done", output);
        Assert.Contains(WorkflowPackageJson.Requirements(definition), requirement =>
            requirement.Kind == "graph-function-tool" && requirement.Id == function.FunctionId &&
            requirement.Version == function.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            requirement.ContractHash == functionSnapshot.ContentHash);
        Assert.Contains("graph_function_0", modelRequests[0]);
        Assert.Contains("Hello!", modelRequests[1]);
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.ToolAuthorized or ExecutionEventKinds.ToolCompleted);
        using var started = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted).Data!);
        Assert.Equal(function.FunctionId, started.RootElement.GetProperty("requestedGraphFunctionTools")[0].GetProperty("functionId").GetString());
        var functionStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentGraphFunctionStarted);
        var functionCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentGraphFunctionCompleted);
        using var completion = JsonDocument.Parse(functionCompleted.Data!);
        Assert.Equal(functionSnapshot.ContentHash, completion.RootElement.GetProperty("contentHash").GetString());
        Assert.Equal("Hello!", completion.RootElement.GetProperty("outputs").GetProperty("result").GetString());
        using var functionStart = JsonDocument.Parse(functionStarted.Data!);
        Assert.Equal(functionStart.RootElement.GetProperty("functionInvocationId").GetGuid(),
            completion.RootElement.GetProperty("functionInvocationId").GetGuid());
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted && evt.Data!.Contains("Done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentDoesNotDiscoverFunctionsThatAreNotPinnedToItsConfiguration()
    {
        var registration = DeterministicDemoChatClient.Registration();
        var definition = AgentGraphFunctionWorkflow(registration.Descriptor.ProviderId, registration.Descriptor.Version, null);
        var broker = BrokerTests.CreateBroker([], new ExplicitToolAuthorizationPolicy([]));
        var events = new List<ExecutionEvent>();

        await new PipelineRunner(broker, new AgentNodeExecutor([registration], broker))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "Hello",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        using var request = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested).Data!);
        Assert.Empty(request.RootElement.GetProperty("tools").EnumerateArray());
        using var started = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted).Data!);
        Assert.Empty(started.RootElement.GetProperty("requestedGraphFunctionTools").EnumerateArray());
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

    private static WorkflowDefinition AgentGraphFunctionWorkflow(string providerId, string providerVersion,
        GraphFunctionConfiguration? functionTool)
    {
        var agent = new AgentConfiguration(providerId, providerVersion, "Use the assigned graph function, then return a short confirmation.", [])
        {
            GraphFunctionTools = functionTool is null ? null : [functionTool]
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.AgentGraphFunctionTools, "agent-graph-function", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: agent),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["agent-result"] = new(new("agent", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["activate-agent"] = new(new("input", "then"), new("agent", "execute"), "execution"),
                ["agent-completed"] = new(new("agent", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
    }

    private static GraphFunctionDefinition AppendSuffixFunction() => new(
        "text.append-suffix", 1, "Append suffix", "Appends a fixed suffix to text.",
        new Dictionary<string, GraphFunctionPort>
        {
            ["text"] = new("Text", "string")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, GraphFunctionPort>
        {
            ["result"] = new("Result", "string")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "text" },
            ["suffix"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") },
            ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["output"] = new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["argument"] = new(new("input", "out"), new("concat", "left"), "data"),
            ["suffix"] = new(new("suffix", "out"), new("concat", "right"), "data"),
            ["result"] = new(new("concat", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    private sealed class CallbackClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> callback) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => callback(messages, options, cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
