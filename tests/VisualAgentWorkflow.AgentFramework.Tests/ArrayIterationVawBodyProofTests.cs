using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Verifies the per-item subworkflow candidate against VAW's real node runner,
/// agent adapter, direct-MCP broker, and event contract.
/// </summary>
public sealed class ArrayIterationVawBodyProofTests
{
    [Fact]
    public async Task StructuredSchedulerProof_ReusesProductionAgentAndMcpNodeAdapters()
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-structured-adapter-proof");
        var standardAgent = new EchoAgentExecutor();
        var standardTools = new EchoToolBroker();
        var structuredAgent = new EchoAgentExecutor();
        var structuredTools = new EchoToolBroker();
        var standardEvents = new ConcurrentQueue<ExecutionEvent>();
        var structuredEvents = new ConcurrentQueue<ExecutionEvent>();
        var parameters = ImmutableSortedDictionary<string, string>.Empty.Add("index", "2");

        var standard = await new WorkflowRunner(standardTools, standardAgent).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), revision, "alpha", parameters, evt =>
            {
                standardEvents.Enqueue(evt);
                return ValueTask.CompletedTask;
            });
        var structured = await new WorkflowRunner(structuredTools, structuredAgent).ExecuteStructuredSchedulerProofAsync(
            Guid.NewGuid(), revision, "alpha", parameters, evt =>
            {
                structuredEvents.Enqueue(evt);
                return ValueTask.CompletedTask;
            });

        Assert.Equal("echo:ALPHA:2", standard.Output);
        Assert.Equal(standard.Output, structured.Output);
        Assert.Equal(["ALPHA:2"], standardAgent.Inputs);
        Assert.Equal(standardAgent.Inputs, structuredAgent.Inputs);
        Assert.Equal(["ALPHA:2"], standardTools.Queries);
        Assert.Equal(standardTools.Queries, structuredTools.Queries);
        Assert.Equal(standardEvents.Select(evt => evt.Kind).Order(StringComparer.Ordinal),
            structuredEvents.Select(evt => evt.Kind).Order(StringComparer.Ordinal));
        Assert.Equal(1, structuredEvents.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent"));
        Assert.Equal(1, structuredEvents.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool"));
    }

    [Fact]
    public async Task StructuredActivationSequenceProof_IsolatesProductionNodeStateAcrossItemsAndCorrelatesEvents()
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-structured-activation-proof");
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();
        var events = new ConcurrentQueue<ExecutionEvent>();

        var result = await new WorkflowRunner(tools, agents).ExecuteStructuredActivationSequenceProofAsync(
            Guid.NewGuid(), revision, "[\"alpha\",\"beta\",\"gamma\"]", "index", null, evt =>
            {
                events.Enqueue(evt);
                return ValueTask.CompletedTask;
            });

        Assert.Equal("echo:ALPHA:0;echo:BETA:1;echo:GAMMA:2", result.Output);
        Assert.Equal(["ALPHA:0", "BETA:1", "GAMMA:2"], agents.Inputs);
        Assert.Equal(["ALPHA:0", "BETA:1", "GAMMA:2"], tools.Queries);
        var recorded = events.ToArray();
        Assert.Equal(Enumerable.Range(1, recorded.Length).Select(value => (long)value),
            recorded.Select(evt => evt.Sequence));
        Assert.Equal(1, recorded.Count(evt => evt.Kind == ExecutionEventKinds.Started));
        Assert.Equal(1, recorded.Count(evt => evt.Kind == ExecutionEventKinds.Completed));
        Assert.Equal(3, recorded.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(3, recorded.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        for (var index = 0; index < 3; index++)
        {
            var scopeId = $"array-loop/{index}";
            var activationEvents = recorded.Where(evt => evt.ScopeId == scopeId).ToArray();
            Assert.Equal($"alpha;beta;gamma".Split(';')[index], ReadVariableValue(activationEvents,
                ExecutionEventKinds.VariableWritten, "set"));
            Assert.Equal($"alpha;beta;gamma".Split(';')[index], ReadVariableValue(activationEvents,
                ExecutionEventKinds.VariableRead, "get"));
            var agentStart = Assert.Single(activationEvents,
                evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent");
            var agentDone = Assert.Single(activationEvents,
                evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent");
            var toolStart = Assert.Single(activationEvents,
                evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "tool");
            var toolDone = Assert.Single(activationEvents,
                evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool");
            Assert.Equal(agentStart.InvocationId, agentDone.InvocationId);
            Assert.Equal(toolStart.InvocationId, toolDone.InvocationId);
        }
    }

    [Fact]
    public async Task StructuredActivationSequenceProof_FailureIsAttributedToItemAndStopsLaterDispatch()
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-structured-activation-failure-proof");
        var agents = new EchoAgentExecutor(failInput: "BETA:1");
        var events = new ConcurrentQueue<ExecutionEvent>();

        var failure = await Record.ExceptionAsync(async () =>
            await new WorkflowRunner(new EchoToolBroker(), agents).ExecuteStructuredActivationSequenceProofAsync(
                Guid.NewGuid(), revision, "[\"alpha\",\"beta\",\"gamma\"]", "index", null, evt =>
                {
                    events.Enqueue(evt);
                    return ValueTask.CompletedTask;
                }));

        Assert.NotNull(failure);
        Assert.Equal(["ALPHA:0", "BETA:1"], agents.Inputs);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "agent" &&
            evt.ScopeId == "array-loop/1");
        Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/2");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredActivationSequenceProof_PersistsOneCorrelatedRunThroughBothStores(bool sqlite)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-structured-persistence-proof");
        const string input = "[\"alpha\",\"beta\",\"gamma\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore, startRoot: false);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();

        var result = await new WorkflowRunner(tools, agents).ExecuteStructuredActivationSequenceProofAsync(
            runStore.RunId, revision, input, "index", null, evt =>
            {
                recorder.Append(evt);
                return ValueTask.CompletedTask;
            });

        Assert.Equal("echo:ALPHA:0;echo:BETA:1;echo:GAMMA:2", result.Output);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        for (var index = 0; index < 3; index++)
        {
            var scopedEvents = events.Where(evt => evt.ScopeId == $"array-loop/{index}").ToArray();
            Assert.Equal($"alpha;beta;gamma".Split(';')[index], ReadVariableValue(scopedEvents,
                ExecutionEventKinds.VariableWritten, "set"));
            Assert.Single(scopedEvents, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent");
            Assert.Single(scopedEvents, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_MatchOnSamePersistedActivationMatrix(bool sqlite)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-comparison-proof");
        const string input = "[\"alpha\",\"beta\",\"gamma\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var structuredAgents = new EchoAgentExecutor();
        var subworkflowAgents = new EchoAgentExecutor();
        var structuredTools = new EchoToolBroker();
        var subworkflowTools = new EchoToolBroker();

        var structuredResult = await new WorkflowRunner(structuredTools, structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt => { structuredRecorder.Append(evt); return ValueTask.CompletedTask; });
        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, subworkflowTools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        string? subworkflowOutput = null;
        await foreach (var evt in run.WatchStreamAsync())
            if (evt is WorkflowOutputEvent { Data: string value }) subworkflowOutput = value;
        subworkflowRecorder.CompleteRoot();

        Assert.Equal(subworkflowOutput, structuredResult.Output);
        Assert.Equal(subworkflowAgents.Inputs, structuredAgents.Inputs);
        Assert.Equal(subworkflowTools.Queries, structuredTools.Queries);
        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        Assert.Equal(subworkflowEvents.Select(EventShape), structuredEvents.Select(EventShape));
        Assert.Equal(subworkflowEvents.Where(IsInvocationBoundary).Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId)),
            structuredEvents.Where(IsInvocationBoundary).Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId)));

        static (string Kind, string? NodeId, string? ScopeId, string? ConnectionId) EventShape(ExecutionEvent evt) =>
            (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId);
        static bool IsInvocationBoundary(ExecutionEvent evt) =>
            evt.Kind is ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeCompleted;
    }

    [Fact]
    public async Task StructuredSchedulerProof_ExecutesProductionAgentNodesAcrossParallelJoin()
    {
        var revision = WorkflowRevision.Create(ParallelBodyDefinition(), "array-iteration-structured-parallel-proof");
        var agents = new EchoAgentExecutor();
        var events = new ConcurrentQueue<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agents).ExecuteStructuredSchedulerProofAsync(
            Guid.NewGuid(), revision, "north", null, evt =>
            {
                events.Enqueue(evt);
                return ValueTask.CompletedTask;
            });

        using var output = JsonDocument.Parse(Assert.IsType<string>(result.Output));
        Assert.Equal("{\"query\":\"north-agent-left\"}", output.RootElement.GetProperty("left").GetString());
        Assert.Equal("{\"query\":\"north-agent-right\"}", output.RootElement.GetProperty("right").GetString());
        Assert.Equal(2, agents.Inputs.Length);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId is "agent-left" or "agent-right"));
    }

    [Fact]
    public async Task StructuredSchedulerProof_FailedMcpBranchCancelsBlockedSiblingAndSuppressesJoin()
    {
        var revision = WorkflowRevision.Create(ParallelMcpDefinition(), "array-iteration-structured-failure-proof");
        var tools = new ParallelMcpBroker(failNode: "tool-research", holdUntilCancelled: true);
        var events = new ConcurrentQueue<ExecutionEvent>();

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(tools: tools)
            .ExecuteStructuredSchedulerProofAsync(Guid.NewGuid(), revision, "north", null, evt =>
            {
                events.Enqueue(evt);
                return ValueTask.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.NotNull(failure);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "tool-research");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "tool-review");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshVawRunnerPerItem_ExecutesAgentAndMcpAndPersistsCorrelatedEvents(bool sqlite)
    {
        var definition = BodyDefinition(includeVariableStore: true, includeIndexParameter: true);
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-proof");
        const string input = "[\"alpha\",\"beta\",\"gamma\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents, tools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        string? output = null;
        await foreach (var evt in run.WatchStreamAsync())
            if (evt is WorkflowOutputEvent { Data: string value }) output = value;
        recorder.CompleteRoot();

        Assert.Equal("echo:ALPHA:0;echo:BETA:1;echo:GAMMA:2", output);
        Assert.Equal(["ALPHA:0", "BETA:1", "GAMMA:2"], agents.Inputs);
        Assert.Equal(["ALPHA:0", "BETA:1", "GAMMA:2"], tools.Queries);

        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        Assert.Equal(Enumerable.Range(1, events.Length).Select(value => (long)value), events.Select(evt => evt.Sequence));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent"));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool"));

        for (var index = 0; index < 3; index++)
        {
            var scopeId = $"array-loop/{index}";
            var scopedEvents = events.Where(evt => evt.ScopeId == scopeId).ToArray();
            Assert.Equal($"alpha;beta;gamma".Split(';')[index], ReadVariableValue(scopedEvents,
                ExecutionEventKinds.VariableWritten, "set"));
            Assert.Equal($"alpha;beta;gamma".Split(';')[index], ReadVariableValue(scopedEvents,
                ExecutionEventKinds.VariableRead, "get"));
            var agentStarted = Assert.Single(scopedEvents, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent");
            var agentCompleted = Assert.Single(scopedEvents, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent");
            var toolStarted = Assert.Single(scopedEvents, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "tool");
            var toolCompleted = Assert.Single(scopedEvents, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool");
            Assert.Equal(agentStarted.InvocationId, agentCompleted.InvocationId);
            Assert.Equal(toolStarted.InvocationId, toolCompleted.InvocationId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshVawRunnerPerItem_ExecutesConfigurableParallelJoin(bool sqlite)
    {
        var definition = ParallelBodyDefinition();
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-parallel-proof");
        const string input = "[\"north\",\"south\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents, tools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        string? output = null;
        await foreach (var evt in run.WatchStreamAsync())
            if (evt is WorkflowOutputEvent { Data: string value }) output = value;
        recorder.CompleteRoot();

        var joinedResults = Assert.IsType<string>(output).Split(';');
        Assert.Equal(2, joinedResults.Length);
        for (var index = 0; index < joinedResults.Length; index++)
        {
            using var joined = JsonDocument.Parse(joinedResults[index]);
            var item = index == 0 ? "north" : "south";
            Assert.Equal($"{{\"query\":\"{item}-agent-left\"}}", joined.RootElement.GetProperty("left").GetString());
            Assert.Equal($"{{\"query\":\"{item}-agent-right\"}}", joined.RootElement.GetProperty("right").GetString());
        }
        Assert.Equal(4, agents.Inputs.Length);
        Assert.Empty(tools.Queries);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        Assert.Equal(4, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress && evt.NodeId == "join"));
        Assert.Equal(4, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId is "agent-left" or "agent-right"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshVawRunnerPerItem_ExecutesConcurrentMcpBranchesAndPersistsResults(bool sqlite)
    {
        var definition = ParallelMcpDefinition();
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-parallel-mcp-proof");
        const string input = "[\"one\",\"two\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var tools = new ParallelMcpBroker();
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, new EchoAgentExecutor(), tools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        string? output = null;
        await foreach (var evt in run.WatchStreamAsync())
            if (evt is WorkflowOutputEvent { Data: string value }) output = value;
        recorder.CompleteRoot();

        var results = Assert.IsType<string>(output).Split(';');
        Assert.Equal(2, results.Length);
        foreach (var result in results)
        {
            using var joined = JsonDocument.Parse(result);
            Assert.Equal("result:research", joined.RootElement.GetProperty("left").GetString());
            Assert.Equal("result:review", joined.RootElement.GetProperty("right").GetString());
        }
        Assert.Equal(2, tools.MaximumConcurrentCalls);
        Assert.Equal(4, tools.Calls.Length);
        Assert.Equal(4, tools.Calls.Select(call => call.InvocationId).Distinct().Count());
        Assert.Equal(2, tools.Calls.Count(call => call.NodeId == "tool-research"));
        Assert.Equal(2, tools.Calls.Count(call => call.NodeId == "tool-review"));

        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        foreach (var nodeId in new[] { "tool-research", "tool-review" })
        {
            var starts = events.Where(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == nodeId).ToArray();
            var completes = events.Where(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == nodeId).ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Equal(starts.Select(evt => evt.InvocationId).Order(), completes.Select(evt => evt.InvocationId).Order());
        }
        Assert.Equal(4, events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized));
        Assert.Equal(4, events.Count(evt => evt.Kind == ExecutionEventKinds.ToolCompleted));
    }

    [Fact]
    public async Task ParallelMcpBranchDenialFailsRunWithoutPublishingJoinResult()
    {
        var revision = WorkflowRevision.Create(ParallelMcpDefinition(), "parallel-mcp-denial-proof");
        var tools = new ParallelMcpBroker(denyNode: "tool-research");
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(tools).ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "ignored", null,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ToolDenied && evt.NodeId == "tool-research");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task ParallelMcpBranchFailureDoesNotPublishPartialJoinResult()
    {
        var revision = WorkflowRevision.Create(ParallelMcpDefinition(), "parallel-mcp-failure-proof");
        var tools = new ParallelMcpBroker(failNode: "tool-research");
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(tools).ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "ignored", null,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "tool-research");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task ParallelMcpBranchCancellationCancelsCallsWithoutPublishingJoinResult()
    {
        var revision = WorkflowRevision.Create(ParallelMcpDefinition(), "parallel-mcp-cancel-proof");
        var tools = new ParallelMcpBroker(holdUntilCancelled: true);
        var events = new ConcurrentQueue<ExecutionEvent>();
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner(tools).ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "ignored", null,
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        await tools.BothBranchesStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
            evt.NodeId is "tool-research" or "tool-review"));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentMaximumMcpOutcomes_PersistBeforeAggregateJoinFailure(bool sqlite)
    {
        var revision = WorkflowRevision.Create(ParallelMcpDefinition(), "parallel-mcp-maximum-evidence-proof");
        const string input = "maximum-output";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var broker = new ParallelMcpBroker(resultPayload: new string('x', 16_384));

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(broker)
            .ExecuteWithOutcomeAsync(runStore.RunId, revision, input, null, evt =>
            {
                recorder.Append(evt);
                return ValueTask.CompletedTask;
            }));

        Assert.NotNull(failure);
        Assert.Equal(2, broker.MaximumConcurrentCalls);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "failed");
        var completedTools = events.Where(evt => evt.Kind == ExecutionEventKinds.ToolCompleted).ToArray();
        Assert.Equal(2, completedTools.Length);
        foreach (var evt in completedTools)
        {
            using var payload = JsonDocument.Parse(evt.Data!);
            Assert.Equal(16_384, payload.RootElement.GetProperty("output").GetString()!.Length);
        }
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "join");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);

        var totalPersistedEventBytes = events.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        var toolEvidenceBytes = completedTools.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} parallel max-output run: {events.Length} events, " +
            $"{toolEvidenceBytes} UTF-8 bytes of successful tool evidence, {totalPersistedEventBytes} UTF-8 event bytes total, failed join retained.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAgentRequests_PersistMaximumPromptEvidenceThroughBothStores(bool sqlite)
    {
        const int maximumInputCharacters = 16_384;
        const int maximumInstructionCharacters = 8_192;
        var definition = ParallelBodyDefinition();
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "agent-left", "agent-right" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Agent = new AgentConfiguration("proof.concurrent-model", null,
                    new string('i', maximumInstructionCharacters), [])
            });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes }, "parallel-model-evidence-proof");
        var input = new string('u', maximumInputCharacters);
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var model = new ConcurrentPromptModel(expectedConcurrentCalls: 2);
        var provider = new AgentProviderDescriptor("proof.concurrent-model", "Proof model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
            new EchoToolBroker());

        var result = await new WorkflowRunner(agents: agents).ExecuteWithOutcomeAsync(runStore.RunId,
            revision, input, null, evt =>
            {
                recorder.Append(evt);
                return ValueTask.CompletedTask;
            });

        Assert.False(result.Failed);
        Assert.Equal(2, model.MaximumConcurrentCalls);
        Assert.Equal(2, model.Calls.Length);
        Assert.All(model.Calls, call =>
        {
            Assert.Equal(maximumInstructionCharacters, call.InstructionCharacters);
            Assert.Equal(maximumInputCharacters, call.UserMessageCharacters);
        });
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        foreach (var nodeId in new[] { "agent-left", "agent-right" })
        {
            var requested = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested && evt.NodeId == nodeId);
            var responded = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded && evt.NodeId == nodeId);
            using var requestPayload = JsonDocument.Parse(requested.Data!);
            Assert.Equal(maximumInstructionCharacters, requestPayload.RootElement.GetProperty("instructions").GetString()!.Length);
            var userMessage = requestPayload.RootElement.GetProperty("messages").EnumerateArray()
                .SelectMany(message => message.GetProperty("content").EnumerateArray())
                .Single(content => content.GetProperty("kind").GetString() == "text");
            Assert.Equal(maximumInputCharacters, userMessage.GetProperty("text").GetString()!.Length);
            Assert.True(requested.Sequence < responded.Sequence);
        }

        var requestedEvents = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested).ToArray();
        var requestEvidenceBytes = requestedEvents.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        var totalPersistedEventBytes = events.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} parallel max-prompt agent run: {events.Length} events, " +
            $"{requestEvidenceBytes} UTF-8 bytes of concurrent model-request evidence, " +
            $"{totalPersistedEventBytes} UTF-8 event bytes total, successful join.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAgentRequestFailures_PersistMaximumPromptAndFailureEvidence(bool sqlite)
    {
        const int maximumInputCharacters = 16_384;
        const int maximumInstructionCharacters = 8_192;
        var definition = ParallelBodyDefinition();
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "agent-left", "agent-right" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Agent = new AgentConfiguration("proof.concurrent-model", null,
                    new string('i', maximumInstructionCharacters), [])
            });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes }, "parallel-model-failure-evidence-proof");
        var input = new string('u', maximumInputCharacters);
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var model = new ConcurrentPromptModel(expectedConcurrentCalls: 2, failAfterStart: true);
        var provider = new AgentProviderDescriptor("proof.concurrent-model", "Proof model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
            new EchoToolBroker());

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(agents: agents).ExecuteWithOutcomeAsync(
            runStore.RunId, revision, input, null, evt =>
            {
                recorder.Append(evt);
                return ValueTask.CompletedTask;
            }));

        Assert.NotNull(failure);
        Assert.Equal(2, model.MaximumConcurrentCalls);
        Assert.Equal(2, model.Calls.Length);
        Assert.All(model.Calls, call =>
        {
            Assert.Equal(maximumInstructionCharacters, call.InstructionCharacters);
            Assert.Equal(maximumInputCharacters, call.UserMessageCharacters);
        });
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "failed");
        var requestedEvents = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested).ToArray();
        Assert.Equal(2, requestedEvents.Length);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);

        var requestEvidenceBytes = requestedEvents.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        var totalPersistedEventBytes = events.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} parallel max-prompt failure run: {events.Length} events, " +
            $"{requestEvidenceBytes} UTF-8 bytes of concurrent model-request evidence, " +
            $"{totalPersistedEventBytes} UTF-8 event bytes total, failed before response.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelDelegateFanout_PersistsMaximumFunctionCallAndChildEvidence(bool sqlite)
    {
        const int maximumInputCharacters = 16_384;
        const int maximumInstructionsCharacters = 8_192;
        const int maximumTaskCharacters = 16_384;
        const int maximumDelegateOutputCharacters = 16_384;
        const string providerId = "proof.delegate-model";
        var parentInstructions = "P" + new string('p', maximumInstructionsCharacters - 1);
        var researcherInstructions = "R" + new string('r', maximumInstructionsCharacters - 1);
        var reviewerInstructions = "V" + new string('v', maximumInstructionsCharacters - 1);
        var profiles = new[]
        {
            AgentProfile.Create("researcher", "1", "Researcher", "Find relevant facts.", providerId,
                researcherInstructions, []),
            AgentProfile.Create("reviewer", "1", "Reviewer", "Check the answer.", providerId,
                reviewerInstructions, [])
        };
        var agent = new AgentConfiguration(providerId, null, parentInstructions, [])
        {
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version,
                profile.ContentHash!))]
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: agent))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
            .Add("agent-output", new(new("agent", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", Edge("input", "then", "agent", "execute"))
            .Add("agent-output", Edge("agent", "out", "output", "execute"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-delegated-evidence-proof", "input", nodes, data)
        {
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version,
                profile => profile, StringComparer.Ordinal),
            ExecutionConnections = execution
        };
        var revision = WorkflowRevision.Create(definition, "parallel-delegate-evidence-proof");
        var input = new string('u', maximumInputCharacters);
        var task = new string('t', maximumTaskCharacters);
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ReservedEvidenceProofRecorder(runStore,
            maximumEvents: 512,
            maximumBytes: 4_000_000,
            reservedFailureEvents: 8,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: 64_000,
            maximumConcurrentOutcomeReservations: 2);
        var model = new ParallelDelegateEvidenceModel(parentInstructions, researcherInstructions,
            reviewerInstructions, task, maximumDelegateOutputCharacters);
        var provider = new AgentProviderDescriptor(providerId, "Proof delegate model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new ParallelDelegateEvidenceChatClient(model))],
            new EchoToolBroker());

        var result = await new WorkflowRunner(agents: agents).ExecuteWithOutcomeAsync(
            runStore.RunId, revision, input, null, recorder.RecordAsync);

        Assert.False(result.Failed);
        Assert.Equal("Team review complete.", result.Output);
        Assert.Equal(2, model.MaximumConcurrentDelegateCalls);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        var parentInvocation = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "agent").InvocationId;
        var modelRequests = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested).ToArray();
        var modelResponses = events.Where(evt => evt.Kind == ExecutionEventKinds.ModelResponded).ToArray();
        Assert.Equal(4, modelRequests.Length);
        Assert.Equal(4, modelResponses.Length);
        Assert.Equal(2, recorder.MaximumPendingOutcomeReservationsObserved);
        Assert.Equal(0, recorder.PendingOutcomeReservations);
        Assert.Equal(0, recorder.PendingOutcomeBytes);
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        Assert.Equal(2, modelRequests.Count(evt => evt.InvocationId != parentInvocation));
        Assert.Equal(4, model.Prompts.Length);
        Assert.All(model.Prompts, prompt => Assert.Equal(maximumInstructionsCharacters,
            prompt.InstructionCharacters));
        Assert.All(model.Prompts, prompt => Assert.Equal(maximumInputCharacters,
            prompt.UserMessageCharacters));
        Assert.Equal(4, model.Prompts.Count(prompt => prompt.UserMessageCharacters == maximumTaskCharacters));
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted));
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted));
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.AgentTeamFailed or ExecutionEventKinds.Failed);

        var parentCallResponse = Assert.Single(modelResponses, evt => evt.InvocationId == parentInvocation &&
            evt.Data!.Contains("function-call", StringComparison.Ordinal));
        using (var responsePayload = JsonDocument.Parse(parentCallResponse.Data!))
        {
            var functionCall = responsePayload.RootElement.GetProperty("messages").EnumerateArray()
                .SelectMany(message => message.GetProperty("content").EnumerateArray())
                .Single(content => content.GetProperty("kind").GetString() == "function-call");
            Assert.Equal(maximumTaskCharacters,
                functionCall.GetProperty("Arguments").GetProperty("task").GetString()!.Length);
        }

        var parentRequests = modelRequests.Where(evt => evt.InvocationId == parentInvocation).ToArray();
        Assert.Equal(2, parentRequests.Length);
        using var initialRequestPayload = JsonDocument.Parse(parentRequests[0].Data!);
        var initialUserMessage = initialRequestPayload.RootElement.GetProperty("messages").EnumerateArray()
            .SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Single(content => content.GetProperty("kind").GetString() == "text");
        Assert.Equal(maximumInputCharacters, initialUserMessage.GetProperty("text").GetString()!.Length);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted &&
            evt.Data!.Length > maximumDelegateOutputCharacters);

        var requestEvidenceBytes = modelRequests.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        var responseEvidenceBytes = modelResponses.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        var totalPersistedEventBytes = events.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} parallel delegated evidence run: {events.Length} events, " +
            $"{requestEvidenceBytes} UTF-8 bytes of model requests, {responseEvidenceBytes} model responses, " +
            $"{totalPersistedEventBytes} total event bytes, including max-size function-call and delegated results.");
    }

    [Theory]
    [InlineData(false, false, 8)]
    [InlineData(true, false, 8)]
    [InlineData(false, true, 8)]
    [InlineData(true, true, 8)]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1)]
    public async Task ParallelDelegateFailure_PreservesUnsettledReservations(bool sqlite,
        bool rejectChildOutcomeAppend, int maximumConcurrentReservations)
    {
        const string providerId = "proof.delegate-model";
        var parentInstructions = "Parent instructions.";
        var researcherInstructions = "Research instructions.";
        var reviewerInstructions = "Review instructions.";
        var profiles = new[]
        {
            AgentProfile.Create("researcher", "1", "Researcher", "Find relevant facts.", providerId,
                researcherInstructions, []),
            AgentProfile.Create("reviewer", "1", "Reviewer", "Check the answer.", providerId,
                reviewerInstructions, [])
        };
        var agent = new AgentConfiguration(providerId, null, parentInstructions, [])
        {
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version,
                profile.ContentHash!))]
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: agent))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
            .Add("agent-output", new(new("agent", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", Edge("input", "then", "agent", "execute"))
            .Add("agent-output", Edge("agent", "out", "output", "execute"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-delegated-failure-proof", "input", nodes, data)
        {
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version,
                profile => profile, StringComparer.Ordinal),
            ExecutionConnections = execution
        };
        var revision = WorkflowRevision.Create(definition, "parallel-delegate-failure-proof");
        const string input = "Review this proposal.";
        Guid? parentInvocationId = null;
        var rejectedAppendCount = 0;
        var rejectedInvocationIds = new ConcurrentQueue<Guid>();
        Func<ExecutionEvent, bool>? rejectAppend = rejectChildOutcomeAppend ? evt =>
        {
            if (parentInvocationId is null && evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent")
                parentInvocationId = evt.InvocationId;
            if (evt.Kind != ExecutionEventKinds.ModelResponded || parentInvocationId is not { } parentId ||
                evt.InvocationId == parentId || evt.InvocationId is not { } invocationId ||
                Interlocked.CompareExchange(ref rejectedAppendCount, 1, 0) != 0) return false;
            rejectedInvocationIds.Enqueue(invocationId);
            return true;
        } : null;
        using var runStore = new ProofRunStore(revision, input, sqlite,
            rejectedAppendPredicate: rejectAppend);
        var recorder = new ReservedEvidenceProofRecorder(runStore,
            maximumEvents: 512,
            maximumBytes: 4_000_000,
            reservedFailureEvents: 8,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: 64_000,
            maximumConcurrentOutcomeReservations: maximumConcurrentReservations);
        var model = new ParallelDelegateEvidenceModel(parentInstructions, researcherInstructions,
            reviewerInstructions, "Review this proposal.", 64, failResearcher: !rejectChildOutcomeAppend,
            failParentFollowup: true);
        var provider = new AgentProviderDescriptor(providerId, "Proof delegate model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new ParallelDelegateEvidenceChatClient(model))],
            new EchoToolBroker());

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(agents: agents).ExecuteWithOutcomeAsync(
            runStore.RunId, revision, input, null, recorder.RecordAsync));

        Assert.NotNull(failure);
        Assert.Equal(maximumConcurrentReservations == 1 ? 1 : 2, model.MaximumConcurrentDelegateCalls);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "failed");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed &&
            evt.Data!.Contains("Delegated agent failed.", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);
        if (rejectChildOutcomeAppend)
        {
            var rejectedInvocationId = Assert.Single(rejectedInvocationIds);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded &&
                evt.InvocationId == rejectedInvocationId);
        }
        var unsettledModelCalls = events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested) -
            events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.True(unsettledModelCalls >= (maximumConcurrentReservations == 1 ? 1 : 2));
        Assert.Equal(unsettledModelCalls, recorder.PendingOutcomeReservations);
        if (maximumConcurrentReservations == 1)
        {
            Assert.Contains("run evidence budget", failure.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, recorder.MaximumPendingOutcomeReservationsObserved);
            Assert.Equal(1, recorder.PendingOutcomeReservations);
            Assert.True(model.MaximumConcurrentDelegateCalls <= 1);
        }
        else
        {
            Assert.True(recorder.MaximumPendingOutcomeReservationsObserved >= 2);
        }
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);

        var teamFailureBytes = events.Where(evt => evt.Kind is ExecutionEventKinds.AgentTeamFailed or
                ExecutionEventKinds.AgentFailed or ExecutionEventKinds.Failed)
            .Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt, JsonSerializerOptions.Web).Length);
        var totalPersistedEventBytes = events.Sum(evt => JsonSerializer.SerializeToUtf8Bytes(evt,
            JsonSerializerOptions.Web).Length);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} parallel delegated failure run: {events.Length} events, " +
            $"{teamFailureBytes} UTF-8 bytes of team/agent/terminal failure evidence, " +
            $"{totalPersistedEventBytes} UTF-8 event bytes total.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedFunctionCallEvidenceAppendFailure_PreventsChildDispatch(bool sqlite)
    {
        const string providerId = "proof.delegate-call-evidence";
        const string parentInstructions = "Parent instructions.";
        const string researcherInstructions = "Research instructions.";
        const string reviewerInstructions = "Review instructions.";
        const string task = "Review this proposal.";
        var profiles = new[]
        {
            AgentProfile.Create("researcher", "1", "Researcher", "Find relevant facts.", providerId,
                researcherInstructions, []),
            AgentProfile.Create("reviewer", "1", "Reviewer", "Check the answer.", providerId,
                reviewerInstructions, [])
        };
        var agent = new AgentConfiguration(providerId, null, parentInstructions, [])
        {
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version,
                profile.ContentHash!))]
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: agent))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
            .Add("agent-output", new(new("agent", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", Edge("input", "then", "agent", "execute"))
            .Add("agent-output", Edge("agent", "out", "output", "execute"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-delegated-call-append-proof", "input", nodes, data)
        {
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version,
                profile => profile, StringComparer.Ordinal),
            ExecutionConnections = execution
        };
        var revision = WorkflowRevision.Create(definition, "delegate-call-append-proof");
        Guid? parentInvocationId = null;
        var rejectedAppendCount = 0;
        Func<ExecutionEvent, bool> rejectAppend = evt =>
        {
            if (parentInvocationId is null && evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent")
                parentInvocationId = evt.InvocationId;
            if (evt.Kind != ExecutionEventKinds.ModelResponded || evt.InvocationId != parentInvocationId ||
                evt.Data?.Contains("team_0", StringComparison.Ordinal) != true ||
                Interlocked.CompareExchange(ref rejectedAppendCount, 1, 0) != 0)
                return false;
            return true;
        };
        using var runStore = new ProofRunStore(revision, task, sqlite, rejectedAppendPredicate: rejectAppend);
        var recorder = new ReservedEvidenceProofRecorder(runStore,
            maximumEvents: 512,
            maximumBytes: 4_000_000,
            reservedFailureEvents: 8,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: 64_000,
            maximumConcurrentOutcomeReservations: 8);
        var model = new ParallelDelegateEvidenceModel(parentInstructions, researcherInstructions,
            reviewerInstructions, task, delegateOutputCharacters: 64);
        var provider = new AgentProviderDescriptor(providerId, "Proof delegate model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new ParallelDelegateEvidenceChatClient(model))],
            new EchoToolBroker());

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(agents: agents).ExecuteWithOutcomeAsync(
            runStore.RunId, revision, task, null, recorder.RecordAsync));

        Assert.NotNull(failure);
        Assert.Equal(1, rejectedAppendCount);
        Assert.Equal(0, model.MaximumConcurrentDelegateCalls);
        Assert.DoesNotContain(model.Prompts, prompt => prompt.InstructionCharacters != parentInstructions.Length);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "failed");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.AgentTeamStarted or
            ExecutionEventKinds.AgentTeamCompleted or ExecutionEventKinds.AgentTeamFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted && evt.InvocationId != parentInvocationId);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);
        Assert.Equal(1, recorder.PendingOutcomeReservations);
        Assert.Equal(1, recorder.MaximumPendingOutcomeReservationsObserved);
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine((sqlite ? "sqlite" : "file") + " rejected delegated function-call evidence: " +
            $"{events.Length} events, no child dispatch, {recorder.PendingOutcomeReservations} unsettled model reservation.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelDelegateCancellation_PreservesUnsettledChildReservations(bool sqlite)
    {
        const string providerId = "proof.delegate-cancellation";
        const string parentInstructions = "Parent instructions.";
        const string researcherInstructions = "Research instructions.";
        const string reviewerInstructions = "Review instructions.";
        const string task = "Review this proposal.";
        var profiles = new[]
        {
            AgentProfile.Create("researcher", "1", "Researcher", "Find relevant facts.", providerId,
                researcherInstructions, []),
            AgentProfile.Create("reviewer", "1", "Reviewer", "Check the answer.", providerId,
                reviewerInstructions, [])
        };
        var agent = new AgentConfiguration(providerId, null, parentInstructions, [])
        {
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version,
                profile.ContentHash!))]
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: agent))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
            .Add("agent-output", new(new("agent", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", Edge("input", "then", "agent", "execute"))
            .Add("agent-output", Edge("agent", "out", "output", "execute"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-delegated-cancellation-proof", "input", nodes, data)
        {
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version,
                profile => profile, StringComparer.Ordinal),
            ExecutionConnections = execution
        };
        var revision = WorkflowRevision.Create(definition, "delegate-cancellation-proof");
        using var runStore = new ProofRunStore(revision, task, sqlite);
        var recorder = new ReservedEvidenceProofRecorder(runStore,
            maximumEvents: 512,
            maximumBytes: 4_000_000,
            reservedFailureEvents: 8,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: 64_000,
            maximumConcurrentOutcomeReservations: 8);
        var model = new ParallelDelegateEvidenceModel(parentInstructions, researcherInstructions,
            reviewerInstructions, task, delegateOutputCharacters: 64, blockDelegatesUntilCancelled: true);
        var provider = new AgentProviderDescriptor(providerId, "Proof delegate model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new ParallelDelegateEvidenceChatClient(model))],
            new EchoToolBroker());
        using var cancellation = new CancellationTokenSource();
        var executionTask = new WorkflowRunner(agents: agents).ExecuteWithOutcomeAsync(
            runStore.RunId, revision, task, null, recorder.RecordAsync, cancellation.Token);

        await model.BothDelegateCallsStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var failure = await Record.ExceptionAsync(() => executionTask);

        Assert.NotNull(failure);
        Assert.Equal(2, model.MaximumConcurrentDelegateCalls);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "cancelled");
        var parentInvocationId = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "agent").InvocationId;
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded &&
            evt.InvocationId == parentInvocationId);
        Assert.Equal(2, events.Where(evt => evt.Kind == ExecutionEventKinds.AgentStarted &&
                evt.InvocationId != parentInvocationId)
            .Select(evt => evt.InvocationId).Distinct().Count());
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Cancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted);
        var unsettledModelCalls = events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested) -
            events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.Equal(2, unsettledModelCalls);
        Assert.Equal(unsettledModelCalls, recorder.PendingOutcomeReservations);
        Assert.Equal(2, recorder.MaximumPendingOutcomeReservationsObserved);
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine((sqlite ? "sqlite" : "file") + " cancelled parallel delegates: " +
            $"{events.Length} events, {recorder.PendingOutcomeReservations} unsettled child model reservations.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelRequestEvidenceAppendFailure_PreventsProviderDispatch(bool sqlite)
    {
        const string providerId = "proof.concurrent-model";
        var definition = BodyDefinition();
        var agent = definition.Nodes["agent"].Agent! with
        {
            ProviderId = providerId,
            Instructions = "Request persistence proof."
        };
        var revision = WorkflowRevision.Create(definition with
        {
            Nodes = definition.Nodes.SetItem("agent", definition.Nodes["agent"] with { Agent = agent })
        }, "model-request-persistence-failure-proof");
        const string input = "Do not invoke the model if its request cannot be recorded.";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var model = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
            new EchoToolBroker());

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(new EchoToolBroker(), agents)
            .ExecuteWithOutcomeAsync(runStore.RunId, revision, input, null, evt =>
            {
                if (evt.Kind == ExecutionEventKinds.ModelRequested)
                    throw new IOException("Deterministic run-store append failure.");
                recorder.Append(evt);
                return ValueTask.CompletedTask;
            }));

        Assert.NotNull(failure);
        Assert.Empty(model.Calls);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "failed");
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.ModelRequested or
            ExecutionEventKinds.ModelResponded);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);
        Assert.DoesNotContain(events, evt => evt.NodeId == "tool" &&
            evt.Kind is ExecutionEventKinds.ToolAuthorized or ExecutionEventKinds.ToolCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelRequestEventCapacityExhaustion_RejectsDispatchAndPersistsReservedFailure(bool sqlite)
    {
        const string providerId = "proof.concurrent-model";
        const string input = "Evidence capacity proof.";
        var revision = ModelEvidenceRevision(providerId);
        using var baselineStore = new ProofRunStore(revision, input, sqlite);
        var baselineRecorder = new ProofRecorder(baselineStore);
        var baselineModel = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
        var baselineAgents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(baselineModel))],
            new EchoToolBroker());
        await new WorkflowRunner(new EchoToolBroker(), baselineAgents).ExecuteWithOutcomeAsync(baselineStore.RunId, revision,
            input, null, evt => { baselineRecorder.Append(evt); return ValueTask.CompletedTask; });
        var baselineEvents = baselineRecorder.Snapshot();
        var requestIndex = Array.FindIndex(baselineEvents, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
        Assert.True(requestIndex > 0);
        Assert.Equal(2, baselineEvents.Count(evt => evt.Kind == ExecutionEventKinds.Started));
        var eventsBeforeRequest = requestIndex - 1; // ProofRecorder writes an extra root-start event.
        var responseBytes = EventBytes(Assert.Single(baselineEvents, evt => evt.Kind == ExecutionEventKinds.ModelResponded));

        using var limitedStore = new ProofRunStore(revision, input, sqlite);
        var model = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
            new EchoToolBroker());
        const int reservedFailureEvents = 3;
        var recorder = new ReservedEvidenceProofRecorder(limitedStore,
            maximumEvents: eventsBeforeRequest + 1 + reservedFailureEvents,
            maximumBytes: 4_000_000,
            reservedFailureEvents,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: responseBytes + 512);

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(new EchoToolBroker(), agents).ExecuteWithOutcomeAsync(
            limitedStore.RunId, revision, input, null, recorder.RecordAsync));

        Assert.NotNull(failure);
        Assert.Contains("run evidence budget", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(model.Calls);
        var events = recorder.Snapshot();
        AssertPersistedSequence(limitedStore, events, "failed");
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.ModelRequested or
            ExecutionEventKinds.ModelResponded);
        Assert.Equal([ExecutionEventKinds.AgentFailed, ExecutionEventKinds.NodeFailed, ExecutionEventKinds.Failed],
            events.TakeLast(reservedFailureEvents).Select(evt => evt.Kind));
        Assert.Equal(0, recorder.PendingOutcomeReservations);
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} event-count exhaustion: {events.Length} persisted events, " +
            $"{recorder.PersistedEventBytes} UTF-8 bytes, no model dispatch, all three reserved failure records persisted.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelRequestByteCapacityExhaustion_RejectsDispatchAndPersistsReservedFailure(bool sqlite)
    {
        const string providerId = "proof.concurrent-model";
        const string input = "Evidence byte-capacity proof.";
        var revision = ModelEvidenceRevision(providerId);
        using var baselineStore = new ProofRunStore(revision, input, sqlite);
        var baselineRecorder = new ProofRecorder(baselineStore);
        var baselineModel = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
        var baselineAgents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(baselineModel))],
            new EchoToolBroker());
        await new WorkflowRunner(new EchoToolBroker(), baselineAgents).ExecuteWithOutcomeAsync(baselineStore.RunId, revision,
            input, null, evt => { baselineRecorder.Append(evt); return ValueTask.CompletedTask; });
        var baselineEvents = baselineRecorder.Snapshot();
        var requestIndex = Array.FindIndex(baselineEvents, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
        Assert.True(requestIndex > 0);
        Assert.Equal(2, baselineEvents.Count(evt => evt.Kind == ExecutionEventKinds.Started));
        var request = baselineEvents[requestIndex];
        var eventsBeforeRequestBytes = baselineEvents.Take(requestIndex).Skip(1).Sum(EventBytes);
        var responseBytes = EventBytes(Assert.Single(baselineEvents, evt => evt.Kind == ExecutionEventKinds.ModelResponded));

        using var limitedStore = new ProofRunStore(revision, input, sqlite);
        var model = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
            new EchoToolBroker());
        const int reservedFailureEvents = 3;
        const long reservedFailureBytes = 64_000;
        var recorder = new ReservedEvidenceProofRecorder(limitedStore,
            maximumEvents: 1_000,
            maximumBytes: eventsBeforeRequestBytes + EventBytes(request) + responseBytes + 512 +
                reservedFailureBytes - 256,
            reservedFailureEvents,
            reservedFailureBytes,
            maximumModelOutcomeBytes: responseBytes + 512);

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(new EchoToolBroker(), agents).ExecuteWithOutcomeAsync(
            limitedStore.RunId, revision, input, null, recorder.RecordAsync));

        Assert.NotNull(failure);
        Assert.Contains("run evidence budget", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(model.Calls);
        var events = recorder.Snapshot();
        AssertPersistedSequence(limitedStore, events, "failed");
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.ModelRequested or
            ExecutionEventKinds.ModelResponded);
        Assert.Equal([ExecutionEventKinds.AgentFailed, ExecutionEventKinds.NodeFailed, ExecutionEventKinds.Failed],
            events.TakeLast(reservedFailureEvents).Select(evt => evt.Kind));
        Assert.Equal(0, recorder.PendingOutcomeReservations);
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} byte-only exhaustion: {events.Length} persisted events, " +
            $"{recorder.PersistedEventBytes} UTF-8 bytes, 256-byte shortfall with event capacity available, no model dispatch.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelOutcomeAccepted_SettlesReservationAndReleasesUnusedBytes(bool sqlite)
    {
        const string providerId = "proof.concurrent-model";
        const string input = "Outcome reservation settlement proof.";
        var revision = ModelEvidenceRevision(providerId);
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var model = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
            new EchoToolBroker());
        var recorder = new ReservedEvidenceProofRecorder(runStore,
            maximumEvents: 64,
            maximumBytes: 4_000_000,
            reservedFailureEvents: 3,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: 128_000);

        var result = await new WorkflowRunner(new EchoToolBroker(), agents).ExecuteWithOutcomeAsync(
            runStore.RunId, revision, input, null, recorder.RecordAsync);

        Assert.Equal("done", result.Output);
        AssertPersistedSequence(runStore, recorder.Snapshot(), "completed");
        Assert.Single(recorder.Snapshot(), evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.Equal(0, recorder.PendingOutcomeReservations);
        Assert.Equal(0, recorder.PendingOutcomeBytes);
        Assert.Equal(recorder.Snapshot().Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} outcome reservation settlement: {recorder.Snapshot().Length} events, " +
            $"{recorder.PersistedEventBytes} UTF-8 bytes; unused outcome allowance released after append acceptance.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelOutcomeAppendFailure_KeepsReservationUntilTerminalEvidence(bool sqlite)
    {
        const string providerId = "proof.concurrent-model";
        const string input = "Outcome append failure proof.";
        var revision = ModelEvidenceRevision(providerId);
        using var baselineStore = new ProofRunStore(revision, input, sqlite);
        var baselineRecorder = new ProofRecorder(baselineStore);
        var baselineModel = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
        var baselineAgents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(baselineModel))],
            new EchoToolBroker());
        await new WorkflowRunner(new EchoToolBroker(), baselineAgents).ExecuteWithOutcomeAsync(baselineStore.RunId, revision,
            input, null, evt => { baselineRecorder.Append(evt); return ValueTask.CompletedTask; });
        var responseBytes = EventBytes(Assert.Single(baselineRecorder.Snapshot(),
            evt => evt.Kind == ExecutionEventKinds.ModelResponded));

        using var limitedStore = new ProofRunStore(revision, input, sqlite,
            rejectedAppendEventKinds: [ExecutionEventKinds.ModelResponded]);
        var model = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
            new EchoToolBroker());
        var recorder = new ReservedEvidenceProofRecorder(limitedStore,
            maximumEvents: 64,
            maximumBytes: 4_000_000,
            reservedFailureEvents: 3,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: responseBytes + 512);

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(new EchoToolBroker(), agents).ExecuteWithOutcomeAsync(
            limitedStore.RunId, revision, input, null, recorder.RecordAsync));

        Assert.NotNull(failure);
        Assert.Contains("Injected store append failure", failure.ToString(), StringComparison.Ordinal);
        Assert.Single(model.Calls);
        var events = recorder.Snapshot();
        AssertPersistedSequence(limitedStore, events, "failed");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);
        Assert.Equal(1, recorder.PendingOutcomeReservations);
        Assert.True(recorder.PendingOutcomeBytes > 0);
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} rejected model outcome append: {events.Length} events, " +
            $"{recorder.PersistedEventBytes} UTF-8 bytes; one outcome reservation retained through run failure.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalAppendFailure_RemainsAStorageFailureDespiteReservedCapacity(bool sqlite)
    {
        const string providerId = "proof.concurrent-model";
        const string input = "Terminal persistence failure proof.";
        var revision = ModelEvidenceRevision(providerId);
        using var baselineStore = new ProofRunStore(revision, input, sqlite);
        var baselineRecorder = new ProofRecorder(baselineStore);
        var baselineModel = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
        var baselineAgents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(baselineModel))],
            new EchoToolBroker());
        await new WorkflowRunner(new EchoToolBroker(), baselineAgents).ExecuteWithOutcomeAsync(baselineStore.RunId,
            revision, input, null, evt => { baselineRecorder.Append(evt); return ValueTask.CompletedTask; });
        var baselineEvents = baselineRecorder.Snapshot();
        var requestIndex = Array.FindIndex(baselineEvents, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
        Assert.True(requestIndex > 0);
        var eventsBeforeRequest = requestIndex - 1;

        using var runStore = new ProofRunStore(revision, input, sqlite,
            rejectedAppendEventKinds: [ExecutionEventKinds.Failed]);
        var model = new ConcurrentPromptModel(expectedConcurrentCalls: 1);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
            new EchoToolBroker());
        var recorder = new ReservedEvidenceProofRecorder(runStore,
            maximumEvents: eventsBeforeRequest + 1 + 3,
            maximumBytes: 1_000_000,
            reservedFailureEvents: 3,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: 128_000);

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(new EchoToolBroker(), agents)
            .ExecuteWithOutcomeAsync(runStore.RunId, revision, input, null, recorder.RecordAsync));

        Assert.NotNull(failure);
        Assert.Contains("Injected store append failure", failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(model.Calls);
        var events = recorder.Snapshot();
        Assert.Equal("running", runStore.Store.Get(runStore.RunId)!.Status);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Failed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed);
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} rejected terminal append: {events.Length} events, " +
            $"{recorder.PersistedEventBytes} UTF-8 bytes; failure reserve available but store remains running.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelModelRequests_CannotOvercommitOutcomeReservations(bool sqlite)
    {
        const string providerId = "proof.concurrent-model";
        const string input = "Two branches compete for one in-flight model reservation.";
        var definition = ParallelBodyDefinition();
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "agent-left", "agent-right" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Agent = nodes[nodeId].Agent! with
                {
                    ProviderId = providerId,
                    Instructions = $"Reservation race for {nodeId}."
                }
            });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes }, "parallel-evidence-reservation-proof");
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ReservedEvidenceProofRecorder(runStore,
            maximumEvents: 512,
            maximumBytes: 4_000_000,
            reservedFailureEvents: 8,
            reservedFailureBytes: 64_000,
            maximumModelOutcomeBytes: 32_000,
            maximumConcurrentOutcomeReservations: 1);
        var model = new ReservationRaceModel(recorder.ReservationRejected);
        var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new ReservationRaceChatClient(model))],
            new EchoToolBroker());

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(agents: agents).ExecuteWithOutcomeAsync(
            runStore.RunId, revision, input, null, recorder.RecordAsync));

        Assert.NotNull(failure);
        Assert.Contains("run evidence budget", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, recorder.MaximumPendingOutcomeReservationsObserved);
        AssertPersistedSequence(runStore, recorder.Snapshot(), "failed");
        Assert.Single(recorder.Snapshot(), evt => evt.Kind == ExecutionEventKinds.ModelRequested);
        Assert.Contains(recorder.Snapshot(), evt => evt.Kind == ExecutionEventKinds.Failed);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} parallel reservation race: {recorder.Snapshot().Length} events, " +
            $"one provider dispatch, maximum {recorder.MaximumPendingOutcomeReservationsObserved} pending outcome reservations.");
    }

    private static WorkflowRevision ModelEvidenceRevision(string providerId)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new(providerId, null,
                "Run the bounded event evidence proof.", [])))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
            .Add("agent-output", new(new("agent", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", Edge("input", "then", "agent", "execute"))
            .Add("agent-output", Edge("agent", "out", "output", "execute"));
        return WorkflowRevision.Create(new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "bounded-event-evidence-proof", "input", nodes, data)
        {
            ExecutionConnections = execution
        }, "bounded-event-evidence-proof");
    }

    private static long EventBytes(ExecutionEvent evt) =>
        JsonSerializer.SerializeToUtf8Bytes(evt, JsonSerializerOptions.Web).Length;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshVawRunnerPerItem_EmptyArrayCompletesWithoutActivatingBody(bool sqlite)
    {
        var definition = BodyDefinition(includeVariableStore: true);
        var revision = WorkflowRevision.Create(definition, "array-iteration-empty-proof");
        const string input = "[]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents, tools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        string? output = null;
        await foreach (var evt in run.WatchStreamAsync())
            if (evt is WorkflowOutputEvent { Data: string value }) output = value;
        recorder.CompleteRoot();

        Assert.Equal(string.Empty, output);
        Assert.Empty(agents.Inputs);
        Assert.Empty(tools.Queries);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
            ExecutionEventKinds.VariableWritten or ExecutionEventKinds.VariableRead or ExecutionEventKinds.NodeStarted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshVawRunnerPerItem_PersistsFailedNodeEvidenceAndStopsLaterItems(bool sqlite)
    {
        var definition = BodyDefinition();
        var revision = WorkflowRevision.Create(definition, "array-iteration-failure-proof");
        const string input = "[\"first\",\"bad\",\"must-not-run\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new EchoAgentExecutor(failInput: "bad");
        var tools = new EchoToolBroker();
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents, tools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        var workflowFailed = false;
        await foreach (var evt in run.WatchStreamAsync())
            workflowFailed |= evt is WorkflowErrorEvent;
        recorder.FailRoot();

        Assert.True(workflowFailed);
        Assert.Equal(["first", "bad"], agents.Inputs);
        Assert.Equal(["first"], tools.Queries);

        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "failed");
        var failedScope = events.Where(evt => evt.ScopeId == "array-loop/1").ToArray();
        var agentStarted = Assert.Single(failedScope,
            evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent");
        var agentFailed = Assert.Single(failedScope,
            evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "agent");
        Assert.Equal(agentStarted.InvocationId, agentFailed.InvocationId);
        Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshVawRunnerPerItem_CancelsActiveItemAndPersistsCancellation(bool sqlite)
    {
        var definition = BodyDefinition();
        var revision = WorkflowRevision.Create(definition, "array-iteration-cancellation-proof");
        const string input = "[\"hold\",\"must-not-run\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new BlockingAgentExecutor("hold");
        var tools = new EchoToolBroker();
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents, tools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        var watch = Task.Run(async () =>
        {
            await foreach (var _ in run.WatchStreamAsync()) { }
        });

        try
        {
            await agents.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await run.CancelRunAsync();
            await watch.WaitAsync(TimeSpan.FromSeconds(5));
            recorder.CancelRoot();
        }
        finally
        {
            if (!watch.IsCompleted) await run.CancelRunAsync();
        }

        Assert.Equal(["hold"], agents.Inputs);
        Assert.Empty(tools.Queries);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "cancelled");
        var cancelledScope = events.Where(evt => evt.ScopeId == "array-loop/0").ToArray();
        var agentStarted = Assert.Single(cancelledScope,
            evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent");
        var agentCancelled = Assert.Single(cancelledScope,
            evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "agent");
        Assert.Equal(agentStarted.InvocationId, agentCancelled.InvocationId);
        Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/1");
    }

    private static WorkflowDefinition BodyDefinition(bool includeVariableStore = false, bool includeIndexParameter = false)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0",
                Agent: new("proof.echo-agent", null, "Convert the input to the query field.", [])))
            .Add("tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("proof.echo-tool", new string('a', 64))))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
            .Add("agent-tool", new(new("agent", "out"), new("tool", "in"), "data"))
            .Add("tool-output", new(new("tool", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", Edge("input", "then", "agent", "execute"))
            .Add("agent-tool", Edge("agent", "out", "tool", "execute"))
            .Add("tool-output", Edge("tool", "then", "output", "execute"));

        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-agent-mcp-proof", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
        if (!includeVariableStore) return definition;

        nodes = nodes
            .Add("set", new(NodeTypes.VariableSet, "1.0.0")
            { VariableId = "item", InputDataType = WorkflowDataTypes.String })
            .Add("get", new(NodeTypes.VariableGet, "1.0.0")
            { VariableId = "item", OutputDataType = WorkflowDataTypes.String })
            .Add("uppercase", new(NodeTypes.Uppercase, "1.0.0"));
        data = data.Remove("input-agent")
            .Add("input-set", new(new("input", "out"), new("set", "value"), "data"))
            .Remove("get-agent")
            .Add("get-uppercase", new(new("get", "out"), new("uppercase", "in"), "data"))
            .Add("uppercase-agent", new(new("uppercase", "out"), new("agent", "in"), "data"));
        execution = execution.Remove("input-agent")
            .Add("input-set", Edge("input", "then", "set", "execute"))
            .Add("set-get", Edge("set", "then", "get", "execute"))
            .Add("get-agent", Edge("get", "then", "agent", "execute"));
        if (includeIndexParameter)
        {
            nodes = nodes
                .Add("index", new(NodeTypes.WorkflowValue, "1.0.0")
                    { ParameterId = "index", OutputDataType = WorkflowDataTypes.Integer })
                .Add("index-text", new(NodeTypes.IntegerToString, "1.0.0"))
                .Add("index-separator", new(NodeTypes.StringConstant, "1.0.0")
                    { StringConstant = new(":") })
                .Add("index-suffix", new(NodeTypes.TextConcat, "1.0.0"))
                .Add("item-index", new(NodeTypes.TextConcat, "1.0.0"));
            data = data.Remove("get-uppercase")
                .Add("index-text-value", new(new("index", "out"), new("index-text", "in"), "data"))
                .Add("separator-left", new(new("index-separator", "out"), new("index-suffix", "left"), "data"))
                .Add("index-suffix-right", new(new("index-text", "out"), new("index-suffix", "right"), "data"))
                .Add("item-index-left", new(new("get", "out"), new("item-index", "left"), "data"))
                .Add("item-index-right", new(new("index-suffix", "out"), new("item-index", "right"), "data"))
                .Add("item-index-uppercase", new(new("item-index", "out"), new("uppercase", "in"), "data"));
        }
        return definition with
        {
            Nodes = nodes,
            Connections = data,
            ExecutionConnections = execution,
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("item", new("Current item", "default")),
            Parameters = includeIndexParameter
                ? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                    .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
                : null
        };
    }

    private static WorkflowDefinition ParallelBodyDefinition()
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("left", "Left", "anchor-left"),
            new ParallelBranchDescriptor("right", "Right", "anchor-right"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("anchor-left", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "left") })
            .Add("anchor-right", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "right") })
            .Add("agent-left", new(NodeTypes.Agent, "1.0.0", Agent: new("proof.echo-agent", null, "Left", [])))
            .Add("agent-right", new(NodeTypes.Agent, "1.0.0", Agent: new("proof.echo-agent", null, "Right", [])))
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0")
            {
                ParallelJoin = new("fork", ["left", "right"])
            })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-left", new(new("input", "out"), new("agent-left", "in"), "data"))
            .Add("input-right", new(new("input", "out"), new("agent-right", "in"), "data"))
            .Add("join-output", new(new("join", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"))
            .Add("fork-left", Edge("fork", ParallelBranchLimits.PinId("left"), "anchor-left", "execute"))
            .Add("fork-right", Edge("fork", ParallelBranchLimits.PinId("right"), "anchor-right", "execute"))
            .Add("left-agent", Edge("anchor-left", "then", "agent-left", "execute"))
            .Add("right-agent", Edge("anchor-right", "then", "agent-right", "execute"))
            .Add("left-join", Edge("agent-left", "out", "join", "branch.left"))
            .Add("right-join", Edge("agent-right", "out", "join", "branch.right"))
            .Add("join-output", Edge("join", "then", "output", "execute"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-parallel-body-proof", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition ParallelMcpDefinition()
    {
        var definition = ParallelBodyDefinition();
        var nodes = definition.Nodes;
        var data = definition.Connections;
        var execution = definition.ExecutionConnections!;
        foreach (var branch in new[] { "left", "right" })
        {
            var agentId = $"agent-{branch}";
            var toolId = $"tool-{(branch == "left" ? "research" : "review")}";
            nodes = nodes.Remove(agentId).Add(toolId, new(NodeTypes.McpTool, "1.0.0",
                new ToolBindingRequest("proof.parallel-tool", new string('a', 64)))
            {
                McpArgumentsJson = $"{{\"query\":\"{(branch == "left" ? "research" : "review")}\"}}"
            });
            data = data.Remove($"input-{branch}")
                .Add($"input-{toolId}", new(new("input", "out"), new(toolId, "in"), "data"));
            execution = execution.Remove($"{branch}-agent")
                .Remove($"{branch}-join")
                .Add($"{branch}-tool", Edge($"anchor-{branch}", "then", toolId, "execute"))
                .Add($"{toolId}-join", Edge(toolId, "then", "join", $"branch.{branch}"));
        }
        return definition with { Nodes = nodes, Connections = data, ExecutionConnections = execution };
    }

    private static string? ReadVariableValue(IEnumerable<ExecutionEvent> events, string eventKind, string nodeId)
    {
        var evt = Assert.Single(events, item => item.Kind == eventKind && item.NodeId == nodeId);
        using var payload = JsonDocument.Parse(evt.Data!);
        return payload.RootElement.GetProperty("value").GetString();
    }

    private static ConnectionDefinition Edge(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");

    private static void AssertPersistedSequence(ProofRunStore store, IReadOnlyList<ExecutionEvent> expected,
        string expectedStatus)
    {
        var persisted = store.Store.Get(store.RunId)!;
        Assert.Equal(expectedStatus, persisted.Status);
        Assert.Equal(expected, persisted.Events);
        Assert.Equal(Enumerable.Range(1, expected.Count).Select(value => (long)value),
            persisted.Events.Select(evt => evt.Sequence));
    }

    private sealed class ProofRunStore : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"vaw-array-proof-{Guid.NewGuid():N}");

        public ProofRunStore(WorkflowRevision revision, string input, bool sqlite,
            string[]? rejectedAppendEventKinds = null,
            Func<ExecutionEvent, bool>? rejectedAppendPredicate = null)
        {
            Directory.CreateDirectory(directory);
            IWorkflowRunStore store = sqlite
                ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
                : new FileRunJournal(directory);
            Store = rejectedAppendEventKinds is { Length: > 0 } || rejectedAppendPredicate is not null
                ? new RejectingAppendRunStore(store,
                    rejectedAppendEventKinds?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal),
                    rejectedAppendPredicate)
                : store;
            RunId = Store.Create(revision, input).Header.RunId;
        }

        public IWorkflowRunStore Store { get; }
        public Guid RunId { get; }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class RejectingAppendRunStore(IWorkflowRunStore inner, IReadOnlySet<string> rejectedKinds,
        Func<ExecutionEvent, bool>? rejectedAppendPredicate)
        : IWorkflowRunStore
    {
        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();
        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
        {
            var rejected = events.FirstOrDefault(evt => rejectedKinds.Contains(evt.Kind) ||
                rejectedAppendPredicate?.Invoke(evt) == true);
            if (rejected is not null)
                throw new IOException($"Injected store append failure for {rejected.Kind}.");
            return inner.Append(runId, expectedSequence, events);
        }
    }

    private sealed class PerItemWorkflowExecutor(Guid runId, WorkflowRevision revision, ProofRecorder recorder,
        IAgentNodeExecutor agents, INodeToolBroker tools)
        : Executor<string, string>("array-iterator-proof", declareCrossRunShareable: true)
    {
        public override async ValueTask<string> HandleAsync(string input, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var items = JsonSerializer.Deserialize<string[]>(input)
                ?? throw new InvalidOperationException("Array input was not a string array.");
            var results = new List<string>(items.Length);
            for (var index = 0; index < items.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var invocationId = Guid.NewGuid();
                var scopeId = $"array-loop/{index}";
                recorder.Append(ExecutionEventKinds.LoopIterationStarted, "array-loop", invocationId, scopeId);
                var result = await new WorkflowRunner(tools, agents).ExecuteWithOutcomeAsync(runId, revision,
                    items[index], revision.Definition.Parameters is null ? null :
                        ImmutableSortedDictionary<string, string>.Empty.Add("index", index.ToString()), evt =>
                    {
                        if (evt.Kind is not (ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                            ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated))
                            recorder.Append(evt with { ScopeId = scopeId });
                        return ValueTask.CompletedTask;
                    }, cancellationToken);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException($"Body workflow failed at array index {index}.");
                recorder.Append(ExecutionEventKinds.LoopIterationCompleted, "array-loop", invocationId, scopeId);
                results.Add(result.Output);
            }
            return string.Join(';', results);
        }
    }

    private sealed class ProofRecorder
    {
        private readonly ProofRunStore store;
        private readonly object gate = new();
        private readonly List<ExecutionEvent> events = [];
        private long sequence;

        public ProofRecorder(ProofRunStore store, bool startRoot = true)
        {
            this.store = store;
            if (startRoot) Append(CreateEvent(ExecutionEventKinds.Started));
        }

        public void Append(ExecutionEvent evt)
        {
            lock (gate)
            {
                var persisted = evt with { Sequence = ++sequence };
                store.Store.Append(store.RunId, sequence - 1, [persisted]);
                events.Add(persisted);
            }
        }

        public void Append(string kind, string nodeId, Guid invocationId, string scopeId)
        {
            Append(new ExecutionEvent(store.RunId, RevisionId, 0, DateTimeOffset.UtcNow, kind, nodeId, invocationId)
            { ScopeId = scopeId });
        }

        public void CompleteRoot() => Append(CreateEvent(ExecutionEventKinds.Completed));

        public void FailRoot() => Append(CreateEvent(ExecutionEventKinds.Failed));

        public void CancelRoot() => Append(CreateEvent(ExecutionEventKinds.Cancelled));

        private ExecutionEvent CreateEvent(string kind) =>
            new(store.RunId, RevisionId, 0, DateTimeOffset.UtcNow, kind);

        private Guid RevisionId => store.Store.Get(store.RunId)!.Header.RevisionId;

        public ExecutionEvent[] Snapshot()
        {
            lock (gate) return [.. events];
        }
    }

    private sealed class ReservedEvidenceProofRecorder
    {
        private static readonly HashSet<string> FailureEvidenceKinds =
        [ExecutionEventKinds.AgentFailed, ExecutionEventKinds.NodeFailed, ExecutionEventKinds.Failed];

        private readonly ProofRunStore runStore;
        private readonly int ordinaryEventLimit;
        private readonly int reservedFailureEvents;
        private readonly long maximumBytes;
        private readonly long reservedFailureBytes;
        private readonly long maximumModelOutcomeBytes;
        private readonly int maximumConcurrentOutcomeReservations;
        private readonly object gate = new();
        private readonly List<ExecutionEvent> events = [];
        private readonly TaskCompletionSource reservationRejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int ordinaryEvents;
        private int failureEvents;
        private int pendingOutcomeReservations;
        private int maximumPendingOutcomeReservationsObserved;
        private long ordinaryBytes;
        private long pendingOutcomeBytes;
        private long failureBytes;
        private long persistedEventBytes;

        public ReservedEvidenceProofRecorder(ProofRunStore runStore, int maximumEvents, long maximumBytes,
            int reservedFailureEvents, long reservedFailureBytes, long maximumModelOutcomeBytes,
            int maximumConcurrentOutcomeReservations = int.MaxValue)
        {
            this.runStore = runStore;
            ordinaryEventLimit = maximumEvents - reservedFailureEvents;
            this.maximumBytes = maximumBytes;
            this.reservedFailureEvents = reservedFailureEvents;
            this.reservedFailureBytes = reservedFailureBytes;
            this.maximumModelOutcomeBytes = maximumModelOutcomeBytes;
            this.maximumConcurrentOutcomeReservations = maximumConcurrentOutcomeReservations;
            if (ordinaryEventLimit < 0 || reservedFailureEvents < FailureEvidenceKinds.Count || maximumBytes < reservedFailureBytes ||
                reservedFailureBytes < 0 || maximumModelOutcomeBytes < 0 || maximumConcurrentOutcomeReservations < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        public int PendingOutcomeReservations { get { lock (gate) return pendingOutcomeReservations; } }
        public long PendingOutcomeBytes { get { lock (gate) return pendingOutcomeBytes; } }
        public int MaximumPendingOutcomeReservationsObserved { get { lock (gate) return maximumPendingOutcomeReservationsObserved; } }
        public long PersistedEventBytes { get { lock (gate) return persistedEventBytes; } }
        public Task ReservationRejected => reservationRejected.Task;

        public ValueTask RecordAsync(ExecutionEvent evt)
        {
            lock (gate)
            {
                var bytes = EventBytes(evt);
                var isRequest = evt.Kind == ExecutionEventKinds.ModelRequested;
                var isOutcome = evt.Kind == ExecutionEventKinds.ModelResponded;
                var isFailureEvidence = FailureEvidenceKinds.Contains(evt.Kind);

                if (isRequest && pendingOutcomeReservations >= maximumConcurrentOutcomeReservations)
                {
                    reservationRejected.TrySetResult();
                    throw new EvidenceBudgetExhaustedException();
                }
                if (isRequest && (ordinaryEvents + pendingOutcomeReservations + 2 > ordinaryEventLimit ||
                    ordinaryBytes + pendingOutcomeBytes + bytes + maximumModelOutcomeBytes > maximumBytes - reservedFailureBytes))
                {
                    reservationRejected.TrySetResult();
                    throw new EvidenceBudgetExhaustedException();
                }
                if (isOutcome && (pendingOutcomeReservations == 0 || bytes > maximumModelOutcomeBytes ||
                    ordinaryBytes + pendingOutcomeBytes - maximumModelOutcomeBytes + bytes > maximumBytes - reservedFailureBytes))
                    throw new InvalidOperationException("The model outcome exceeded its proof reservation.");
                if (isFailureEvidence && (failureEvents >= reservedFailureEvents || failureBytes + bytes > reservedFailureBytes))
                    throw new InvalidOperationException("The terminal failure evidence exceeded its proof reserve.");
                if (!isRequest && !isOutcome && !isFailureEvidence &&
                    (ordinaryEvents + pendingOutcomeReservations + 1 > ordinaryEventLimit ||
                     ordinaryBytes + pendingOutcomeBytes + bytes > maximumBytes - reservedFailureBytes))
                    throw new EvidenceBudgetExhaustedException();

                runStore.Store.Append(runStore.RunId, evt.Sequence - 1, [evt]);
                events.Add(evt);
                persistedEventBytes += bytes;

                if (isRequest)
                {
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    pendingOutcomeReservations++;
                    maximumPendingOutcomeReservationsObserved = Math.Max(maximumPendingOutcomeReservationsObserved,
                        pendingOutcomeReservations);
                    pendingOutcomeBytes += maximumModelOutcomeBytes;
                }
                else if (isOutcome)
                {
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    pendingOutcomeReservations--;
                    pendingOutcomeBytes -= maximumModelOutcomeBytes;
                }
                else if (isFailureEvidence)
                {
                    failureEvents++;
                    failureBytes += bytes;
                }
                else
                {
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                }
                return ValueTask.CompletedTask;
            }
        }

        public ExecutionEvent[] Snapshot() { lock (gate) return [.. events]; }
    }

    private sealed class EvidenceBudgetExhaustedException()
        : InvalidOperationException("The run evidence budget cannot reserve the model request and outcome.");

    private sealed class EchoAgentExecutor(string? failInput = null) : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<string> inputs = new();
        public string[] Inputs => [.. inputs];

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("proof.echo-agent", "Proof echo", "proof", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inputs.Enqueue(input);
            if (input == failInput) throw new InvalidOperationException("Deterministic proof failure.");
            var query = invocation.NodeId == "agent" ? input : $"{input}-{invocation.NodeId}";
            return Task.FromResult(JsonSerializer.Serialize(new { query }));
        }
    }

    private sealed class BlockingAgentExecutor(string blockedInput) : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<string> inputs = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string[] Inputs => [.. inputs];

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("proof.blocking-agent", "Blocking proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            inputs.Enqueue(input);
            if (input == blockedInput)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return JsonSerializer.Serialize(new { query = input });
        }
    }

    private sealed class EchoToolBroker : INodeToolBroker
    {
        private readonly ConcurrentQueue<string> queries = new();
        public string[] Queries => [.. queries];
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new EchoToolScope(queries);
    }

    private sealed class EchoToolScope(ConcurrentQueue<string> queries) : INodeToolScope
    {
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new NotSupportedException("The proof expects structured MCP arguments."));
        public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = arguments.GetProperty("query").GetString()!;
            queries.Enqueue(query);
            return Task.FromResult($"echo:{query}");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ParallelMcpBroker(string? denyNode = null, string? failNode = null,
        bool holdUntilCancelled = false, string? resultPayload = null) : INodeToolBroker
    {
        private readonly ConcurrentQueue<ParallelToolCall> calls = new();
        private readonly TaskCompletionSource branchesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;
        private int maximumConcurrentCalls;

        public int MaximumConcurrentCalls => maximumConcurrentCalls;
        public ParallelToolCall[] Calls => [.. calls];
        public Task BothBranchesStarted => branchesStarted.Task;
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public INodeToolScope OpenScope(NodeInvocationContext invocation) =>
            new Scope(this, invocation.NodeId, invocation.InvocationId, invocation.Record,
                denyNode, failNode, holdUntilCancelled, resultPayload);

        private sealed class Scope(ParallelMcpBroker owner, string nodeId, Guid invocationId,
            Func<string, string?, ValueTask> record, string? denyNode, string? failNode,
            bool holdUntilCancelled, string? resultPayload) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
                Task.FromException<string>(new NotSupportedException());
            public async Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
            {
                var active = Interlocked.Increment(ref owner.activeCalls);
                UpdateMaximum(owner, active);
                var query = arguments.GetProperty("query").GetString()!;
                owner.calls.Enqueue(new(nodeId, invocationId, query));
                if (owner.calls.Count >= 2) owner.branchesStarted.TrySetResult();
                try
                {
                    if (owner.calls.Count >= 2) owner.branchesStarted.TrySetResult();
                    await owner.branchesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    if (nodeId == denyNode)
                    {
                        await record(ExecutionEventKinds.ToolDenied, "No effective node grant.");
                        throw new ToolAccessDeniedException();
                    }
                    await record(ExecutionEventKinds.ToolAuthorized, query);
                    if (nodeId == failNode)
                    {
                        await record(ExecutionEventKinds.ToolFailed, "Deterministic branch tool failure.");
                        throw new ToolInvocationException("Deterministic branch tool failure.");
                    }
                    if (holdUntilCancelled)
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    var result = resultPayload ?? $"result:{query}";
                    await record(ExecutionEventKinds.ToolCompleted, JsonSerializer.Serialize(new
                    {
                        toolCallId = Guid.NewGuid(), parentToolCallId = (Guid?)null, bindingId, output = result
                    }));
                    return result;
                }
                finally { Interlocked.Decrement(ref owner.activeCalls); }
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private static void UpdateMaximum(ParallelMcpBroker owner, int candidate)
        {
            var current = Volatile.Read(ref owner.maximumConcurrentCalls);
            while (candidate > current)
            {
                var previous = Interlocked.CompareExchange(ref owner.maximumConcurrentCalls, candidate, current);
                if (previous == current) return;
                current = previous;
            }
        }
    }

    private sealed class ParallelDelegateEvidenceModel(string parentInstructions, string researcherInstructions,
        string reviewerInstructions, string task, int delegateOutputCharacters, bool failResearcher = false,
        bool failParentFollowup = false, bool blockDelegatesUntilCancelled = false)
    {
        private readonly ConcurrentQueue<DelegatedPrompt> prompts = new();
        private readonly TaskCompletionSource bothDelegatesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int parentTurns;
        private int activeDelegates;
        private int maximumConcurrentDelegateCalls;

        public DelegatedPrompt[] Prompts => [.. prompts];
        public int MaximumConcurrentDelegateCalls => maximumConcurrentDelegateCalls;
        public Task BothDelegateCallsStarted => bothDelegatesStarted.Task;

        public async Task<ChatResponse> RespondAsync(IEnumerable<ChatMessage> messages, ChatOptions? options,
            CancellationToken cancellationToken)
        {
            var instructions = options?.Instructions ?? string.Empty;
            var inputCharacters = messages.Where(message => message.Role == ChatRole.User)
                .SelectMany(message => message.Contents.OfType<TextContent>())
                .Sum(content => content.Text.Length);
            prompts.Enqueue(new(instructions.Length, inputCharacters));

            if (instructions == parentInstructions)
            {
                if (Interlocked.Increment(ref parentTurns) == 1)
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("team-call", "team_0",
                            new Dictionary<string, object?> { ["task"] = task })]))
                    { ModelId = "proof-chat-v1", FinishReason = ChatFinishReason.ToolCalls };

                if (failParentFollowup)
                    throw new InvalidOperationException("Deterministic parent model failure after team failure.");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Team review complete."))
                { ModelId = "proof-chat-v1", FinishReason = ChatFinishReason.Stop };
            }

            var output = instructions == researcherInstructions
                ? new string('r', delegateOutputCharacters)
                : instructions == reviewerInstructions
                    ? new string('v', delegateOutputCharacters)
                    : throw new InvalidOperationException("Unexpected delegated model instructions.");
            var active = Interlocked.Increment(ref activeDelegates);
            UpdateMaximum(active);
            if (active >= 2) bothDelegatesStarted.TrySetResult();
            try
            {
                await bothDelegatesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                if (blockDelegatesUntilCancelled)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (failResearcher && instructions == researcherInstructions)
                    throw new InvalidOperationException("Deterministic researcher failure.");
                if (failResearcher && instructions == reviewerInstructions)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, output))
                { ModelId = "proof-chat-v1", FinishReason = ChatFinishReason.Stop };
            }
            finally
            {
                Interlocked.Decrement(ref activeDelegates);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            var current = Volatile.Read(ref maximumConcurrentDelegateCalls);
            while (candidate > current)
            {
                var previous = Interlocked.CompareExchange(ref maximumConcurrentDelegateCalls, candidate, current);
                if (previous == current) return;
                current = previous;
            }
        }
    }

    private sealed class ParallelDelegateEvidenceChatClient(ParallelDelegateEvidenceModel model) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => model.RespondAsync(messages, options, cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed record DelegatedPrompt(int InstructionCharacters, int UserMessageCharacters);

    private sealed class ReservationRaceModel(Task reservationRejected)
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public async Task<ChatResponse> RespondAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await reservationRejected.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                ModelId = "proof-chat-v1",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 2, OutputTokenCount = 1, TotalTokenCount = 3 }
            };
        }
    }

    private sealed class ReservationRaceChatClient(ReservationRaceModel model) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => model.RespondAsync(cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ConcurrentPromptModel(int expectedConcurrentCalls, bool failAfterStart = false)
    {
        private readonly ConcurrentQueue<PromptCall> calls = new();
        private readonly TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;
        private int maximumConcurrentCalls;

        public PromptCall[] Calls => [.. calls];
        public int MaximumConcurrentCalls => maximumConcurrentCalls;

        public async Task<ChatResponse> RespondAsync(IEnumerable<ChatMessage> messages, ChatOptions? options,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref activeCalls);
            UpdateMaximum(active);
            var userCharacters = messages.Where(message => message.Role == ChatRole.User)
                .SelectMany(message => message.Contents.OfType<TextContent>())
                .Sum(content => content.Text.Length);
            calls.Enqueue(new(options?.Instructions?.Length ?? 0, userCharacters));
            if (calls.Count >= expectedConcurrentCalls) bothStarted.TrySetResult();
            try
            {
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                if (failAfterStart) throw new InvalidOperationException("Deterministic model failure after request capture.");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
                {
                    ModelId = "proof-chat-v1",
                    FinishReason = ChatFinishReason.Stop,
                    Usage = new UsageDetails { InputTokenCount = 2, OutputTokenCount = 1, TotalTokenCount = 3 }
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            var current = Volatile.Read(ref maximumConcurrentCalls);
            while (candidate > current)
            {
                var previous = Interlocked.CompareExchange(ref maximumConcurrentCalls, candidate, current);
                if (previous == current) return;
                current = previous;
            }
        }
    }

    private sealed class SynchronizedResponseChatClient(ConcurrentPromptModel model) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => model.RespondAsync(messages, options, cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed record PromptCall(int InstructionCharacters, int UserMessageCharacters);
    private sealed record ParallelToolCall(string NodeId, Guid InvocationId, string Query);
}
