using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Mcp;
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
    public async Task StructuredActivationSequenceProof_SharesEvaluatorButScopesRuntimeValuesAndVariables()
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
            Assert.Equal("default", ReadVariablePreviousValue(activationEvents, "set"));
            Assert.Equal($"alpha;beta;gamma".Split(';')[index], ReadVariableValue(activationEvents,
                ExecutionEventKinds.VariableRead, "get"));
            Assert.Equal($"alpha;beta;gamma".Split(';')[index],
                ReadEvaluatedValue(activationEvents, "get"));
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
    public async Task StructuredActivationSequenceProof_NestedFramesInheritParametersAndScopeProductionNodes()
    {
        var revision = WorkflowRevision.Create(NestedBodyDefinition(),
            "array-iteration-nested-activation-proof");
        var parentParameters = ImmutableSortedDictionary<string, string>.Empty
            .Add("outerIndex", "7");
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 7, "outer", parentParameters);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executorSetCreations = 0;

        var result = await new WorkflowRunner(tools, agents).ExecuteStructuredActivationSequenceProofAsync(
            Guid.NewGuid(), revision, "[\"alpha\",\"beta\"]", "index", null, evt =>
            {
                events.Enqueue(evt);
                return ValueTask.CompletedTask;
            }, executorSetCreated: () => executorSetCreations++, parentFrame: parentFrame,
            activationLoopNodeId: "inner-loop");

        Assert.Equal("echo:ALPHA:0/7;echo:BETA:1/7", result.Output);
        Assert.Equal(["ALPHA:0/7", "BETA:1/7"], agents.Inputs);
        Assert.Equal(agents.Inputs, tools.Queries);
        Assert.Equal(1, executorSetCreations);
        var recorded = events.ToArray();
        Assert.Equal(Enumerable.Range(1, recorded.Length).Select(value => (long)value),
            recorded.Select(evt => evt.Sequence));
        for (var index = 0; index < 2; index++)
        {
            var scopeId = $"outer-loop/7/inner-loop/{index}";
            var scoped = recorded.Where(evt => evt.ScopeId == scopeId).ToArray();
            Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
                evt.NodeId == "inner-loop");
            Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.NodeId == "inner-loop");
            Assert.Equal("default", ReadVariablePreviousValue(scoped, "set"));
            Assert.Equal(index == 0 ? "alpha" : "beta",
                ReadVariableValue(scoped, ExecutionEventKinds.VariableRead, "get"));
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
        var structuredExecutorSetCreations = 0;

        var structuredResult = await new WorkflowRunner(structuredTools, structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt => { structuredRecorder.Append(evt); return ValueTask.CompletedTask; },
                executorSetCreated: () => structuredExecutorSetCreations++);
        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, subworkflowTools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        string? subworkflowOutput = null;
        await foreach (var evt in run.WatchStreamAsync())
            if (evt is WorkflowOutputEvent { Data: string value }) subworkflowOutput = value;
        subworkflowRecorder.CompleteRoot();

        Assert.Equal(subworkflowOutput, structuredResult.Output);
        Assert.Equal(1, structuredExecutorSetCreations);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_StopAtSameFailedActivation(bool sqlite)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-failure-comparison-proof");
        const string input = "[\"alpha\",\"beta\",\"gamma\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var structuredAgents = new EchoAgentExecutor(failInput: "BETA:1");
        var subworkflowAgents = new EchoAgentExecutor(failInput: "BETA:1");
        var structuredTools = new EchoToolBroker();
        var subworkflowTools = new EchoToolBroker();

        var structuredFailure = await Record.ExceptionAsync(() =>
            new WorkflowRunner(structuredTools, structuredAgents)
                .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                    evt => { structuredRecorder.Append(evt); return ValueTask.CompletedTask; }));
        Assert.NotNull(structuredFailure);

        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, subworkflowTools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string value }) subworkflowOutput = value;
        Assert.Null(subworkflowOutput);
        subworkflowRecorder.FailRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "failed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "failed");
        Assert.Equal(["ALPHA:0", "BETA:1"], structuredAgents.Inputs);
        Assert.Equal(structuredAgents.Inputs, subworkflowAgents.Inputs);
        Assert.Equal(["ALPHA:0"], structuredTools.Queries);
        Assert.Equal(structuredTools.Queries, subworkflowTools.Queries);
        AssertActivationFailurePrefix(structuredEvents);
        AssertActivationFailurePrefix(subworkflowEvents);
        Assert.Equal(subworkflowEvents.Where(IsNodeFailureBoundary).Select(EventShape),
            structuredEvents.Where(IsNodeFailureBoundary).Select(EventShape));

        static (string Kind, string? NodeId, string? ScopeId) EventShape(ExecutionEvent evt) =>
            (evt.Kind, evt.NodeId, evt.ScopeId);
        static bool IsNodeFailureBoundary(ExecutionEvent evt) =>
            evt.Kind is ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeFailed;
        static void AssertActivationFailurePrefix(IReadOnlyList<ExecutionEvent> events)
        {
            Assert.Equal(["array-loop/0", "array-loop/1"], events
                .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
                .Select(evt => evt.ScopeId));
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId == "agent" && evt.ScopeId == "array-loop/1");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == "tool" && evt.ScopeId == "array-loop/1");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/2");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_CancelSameActiveActivation(bool sqlite)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-cancellation-comparison-proof");
        const string input = "[\"alpha\",\"beta\",\"gamma\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var structuredAgents = new BlockingAgentExecutor("BETA:1");
        var subworkflowAgents = new BlockingAgentExecutor("BETA:1");
        var structuredTools = new EchoToolBroker();
        var subworkflowTools = new EchoToolBroker();
        using var structuredCancellation = new CancellationTokenSource();

        var structuredTask = new WorkflowRunner(structuredTools, structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt => { structuredRecorder.Append(evt); return ValueTask.CompletedTask; },
                structuredCancellation.Token);
        await structuredAgents.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        structuredCancellation.Cancel();
        var structuredFailure = await Record.ExceptionAsync(async () => await structuredTask);
        Assert.IsAssignableFrom<OperationCanceledException>(structuredFailure);

        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, subworkflowTools);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        var watch = Task.Run(async () =>
        {
            await foreach (var _ in run.WatchStreamAsync()) { }
        });
        await subworkflowAgents.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.CancelRunAsync();
        await watch.WaitAsync(TimeSpan.FromSeconds(5));
        subworkflowRecorder.CancelRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "cancelled");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "cancelled");
        Assert.Equal(["ALPHA:0", "BETA:1"], structuredAgents.Inputs);
        Assert.Equal(structuredAgents.Inputs, subworkflowAgents.Inputs);
        Assert.Equal(["ALPHA:0"], structuredTools.Queries);
        Assert.Equal(structuredTools.Queries, subworkflowTools.Queries);
        AssertActiveCancellationPrefix(structuredEvents);
        AssertActiveCancellationPrefix(subworkflowEvents);
        Assert.Equal(subworkflowEvents.Where(IsNodeCancellationBoundary).Select(EventShape),
            structuredEvents.Where(IsNodeCancellationBoundary).Select(EventShape));

        static (string Kind, string? NodeId, string? ScopeId) EventShape(ExecutionEvent evt) =>
            (evt.Kind, evt.NodeId, evt.ScopeId);
        static bool IsNodeCancellationBoundary(ExecutionEvent evt) =>
            evt.Kind is ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeCancelled;
        static void AssertActiveCancellationPrefix(IReadOnlyList<ExecutionEvent> events)
        {
            Assert.Equal(["array-loop/0", "array-loop/1"], events
                .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
                .Select(evt => evt.ScopeId));
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
                evt.NodeId == "agent" && evt.ScopeId == "array-loop/1");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/2");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_MatchParallelJoinPerActivation(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-parallel-comparison-proof");
        const string input = "[\"north\",\"south\"]";
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
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string value }) subworkflowOutput = value;
        subworkflowRecorder.CompleteRoot();

        Assert.Equal(subworkflowOutput, structuredResult.Output);
        Assert.Equal(4, structuredAgents.Inputs.Length);
        Assert.Equal(structuredAgents.Inputs.Order(StringComparer.Ordinal),
            subworkflowAgents.Inputs.Order(StringComparer.Ordinal));
        Assert.Empty(structuredTools.Queries);
        Assert.Empty(subworkflowTools.Queries);
        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        foreach (var (events, candidateOutput) in new[]
                 { (structuredEvents, structuredResult.Output), (subworkflowEvents, subworkflowOutput) })
        {
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
            for (var index = 0; index < 2; index++)
            {
                var scopeId = $"array-loop/{index}";
                var scoped = events.Where(evt => evt.ScopeId == scopeId).ToArray();
                Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                    evt.NodeId is "agent-left" or "agent-right"));
                Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
                Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress && evt.NodeId == "join"));
            }
            Assert.NotNull(candidateOutput);
        }

        static IEnumerable<(string Kind, string? NodeId, string? ScopeId, string? ConnectionId)> EventShape(
            IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.NodeId is "agent-left" or "agent-right" or "join")
            .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId))
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.Kind, StringComparer.Ordinal)
            .ThenBy(evt => evt.NodeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.ConnectionId, StringComparer.Ordinal);

        Assert.Equal(EventShape(subworkflowEvents), EventShape(structuredEvents));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_FailSameParallelActivationWithoutJoin(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-parallel-failure-comparison-proof");
        const string input = "[\"north\",\"south\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var structuredAgents = new EchoAgentExecutor(failNodeId: "agent-left");
        var subworkflowAgents = new EchoAgentExecutor(failNodeId: "agent-left");

        var structuredFailure = await Record.ExceptionAsync(() =>
            new WorkflowRunner(agents: structuredAgents)
                .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                    evt => { structuredRecorder.Append(evt); return ValueTask.CompletedTask; }));
        Assert.NotNull(structuredFailure);

        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, new EchoToolBroker());
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        var workflowError = false;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
            await foreach (var evt in run.WatchStreamAsync()) workflowError |= evt is WorkflowErrorEvent;
        Assert.True(workflowError);
        subworkflowRecorder.FailRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "failed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "failed");
        AssertParallelFailurePrefix(structuredEvents);
        AssertParallelFailurePrefix(subworkflowEvents);
        Assert.Equal(subworkflowEvents.Where(IsFailingBranchBoundary).Select(EventShape),
            structuredEvents.Where(IsFailingBranchBoundary).Select(EventShape));
        Assert.DoesNotContain(structuredEvents, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent-right");

        static (string Kind, string? NodeId, string? ScopeId, string? ConnectionId) EventShape(ExecutionEvent evt) =>
            (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId);
        static bool IsFailingBranchBoundary(ExecutionEvent evt) => evt.NodeId == "agent-left" ||
            evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join";
        static void AssertParallelFailurePrefix(IReadOnlyList<ExecutionEvent> events)
        {
            Assert.Equal(["array-loop/0"], events
                .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
                .Select(evt => evt.ScopeId));
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId == "agent-left" && evt.ScopeId == "array-loop/0");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/1");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_CompareJoinArrivalAfterSuccessfulBranch(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var execution = definition.ExecutionConnections!;
        definition = definition with
        {
            ExecutionConnections = execution.Remove("fork-left").Remove("fork-right")
                .Add("fork-z-left", Edge("fork", ParallelBranchLimits.PinId("left"), "anchor-left", "execute"))
                .Add("fork-a-right", Edge("fork", ParallelBranchLimits.PinId("right"), "anchor-right", "execute"))
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-reversed-parallel-order-proof");
        const string input = "[\"north\",\"south\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var structuredAdvance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subworkflowAdvance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var structuredAgents = new FirstSuccessThenFailAgentExecutor(structuredAdvance);
        var subworkflowAgents = new FirstSuccessThenFailAgentExecutor(subworkflowAdvance);

        ValueTask RecordStructured(ExecutionEvent evt)
        {
            structuredRecorder.Append(evt);
            if (evt.Kind == ExecutionEventKinds.ParallelJoinProgress && evt.NodeId == "join")
                structuredAdvance.TrySetResult();
            return ValueTask.CompletedTask;
        }

        var structuredFailure = await Record.ExceptionAsync(() =>
            new WorkflowRunner(agents: structuredAgents)
                .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                    RecordStructured));
        Assert.NotNull(structuredFailure);

        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, new EchoToolBroker(), observeEvent: evt =>
            {
                if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent-right")
                    subworkflowAdvance.TrySetResult();
                return ValueTask.CompletedTask;
            });
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        var workflowError = false;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
            await foreach (var evt in run.WatchStreamAsync()) workflowError |= evt is WorkflowErrorEvent;
        Assert.True(workflowError);
        subworkflowRecorder.FailRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "failed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "failed");
        AssertSuccessfulBranchBeforeFailure(structuredEvents, joinStarts: true);
        AssertSuccessfulBranchBeforeFailure(subworkflowEvents, joinStarts: false);

        static void AssertSuccessfulBranchBeforeFailure(IReadOnlyList<ExecutionEvent> events, bool joinStarts)
        {
            var rightTargetActivated = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated &&
                evt.NodeId == "anchor-right" && evt.ConnectionId == "execution:fork-a-right" &&
                evt.ScopeId == "array-loop/0");
            var leftTargetActivated = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated &&
                evt.NodeId == "anchor-left" && evt.ConnectionId == "execution:fork-z-left" &&
                evt.ScopeId == "array-loop/0");
            Assert.True(rightTargetActivated.Sequence < leftTargetActivated.Sequence);
            var rightCompleted = events.SingleOrDefault(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "agent-right" && evt.ScopeId == "array-loop/0");
            var leftFailed = events.SingleOrDefault(evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId == "agent-left" && evt.ScopeId == "array-loop/0");
            var trace = string.Join(" | ", events.Where(evt => evt.NodeId is "agent-left" or "agent-right" or "join")
                .Select(evt => $"{evt.Sequence}:{evt.Kind}:{evt.NodeId}"));
            Assert.True(rightCompleted is not null, trace);
            Assert.True(leftFailed is not null, trace);
            Assert.True(rightCompleted.Sequence < leftFailed.Sequence,
                trace);
            Assert.Equal(joinStarts, events.Any(evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == "join" && evt.ScopeId == "array-loop/0"));
            Assert.Equal(joinStarts, events.Any(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress &&
                evt.NodeId == "join" && evt.ScopeId == "array-loop/0"));
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/1");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_CancelAfterOneJoinBranchCompletes(bool sqlite)
    {
        var definition = ParallelBodyDefinition();
        var execution = definition.ExecutionConnections!;
        definition = definition with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer }),
            ExecutionConnections = execution.Remove("fork-left").Remove("fork-right")
                .Add("fork-z-left", Edge("fork", ParallelBranchLimits.PinId("left"), "anchor-left", "execute"))
                .Add("fork-a-right", Edge("fork", ParallelBranchLimits.PinId("right"), "anchor-right", "execute"))
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-join-cancellation-proof");
        const string input = "[\"north\",\"must-not-run\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        using var structuredCancellation = new CancellationTokenSource();
        var structuredAgents = new OneBranchCompletedThenBlockedAgentExecutor();
        IReadOnlyList<ExecutionEvent>? structuredEventsAtPause = null;
        var structuredLeftHandlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var structuredTask = new WorkflowRunner(agents: structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                async evt =>
                {
                    structuredRecorder.Append(evt);
                    if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent-right")
                    {
                        structuredEventsAtPause = structuredRecorder.Snapshot();
                        structuredAgents.RightCompleted.TrySetResult();
                        await structuredAgents.ReleaseRight.Task;
                    }
                }, structuredCancellation.Token,
                afterNodeStarted: (nodeId, _) =>
                {
                    if (nodeId == "agent-left") structuredLeftHandlerEntered.TrySetResult();
                    return ValueTask.CompletedTask;
                });
        var structuredFirstSignal = await Task.WhenAny(structuredAgents.RightCompleted.Task, structuredTask)
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (structuredFirstSignal == structuredTask) await structuredTask;
        await structuredAgents.RightCompleted.Task;
        structuredCancellation.Cancel();
        structuredAgents.ReleaseRight.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => structuredTask);

        var subworkflowAgents = new OneBranchCompletedThenBlockedAgentExecutor();
        IReadOnlyList<ExecutionEvent>? subworkflowEventsAtPause = null;
        var subworkflowLeftHandlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, new EchoToolBroker(), observeEvent: async evt =>
            {
                if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent-right")
                {
                    subworkflowEventsAtPause = subworkflowRecorder.Snapshot();
                    subworkflowAgents.RightCompleted.TrySetResult();
                    await subworkflowAgents.ReleaseRight.Task;
                }
            }, afterNodeStarted: (nodeId, _) =>
            {
                if (nodeId == "agent-left") subworkflowLeftHandlerEntered.TrySetResult();
                return ValueTask.CompletedTask;
            });
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
        {
            var watch = Task.Run(async () =>
            {
                await foreach (var _ in run.WatchStreamAsync()) { }
            });
            await subworkflowAgents.RightCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertCandidateAdmissionAtPause(structuredEventsAtPause!, structuredLeftHandlerEntered, "structured");
            AssertCandidateAdmissionAtPause(subworkflowEventsAtPause!, subworkflowLeftHandlerEntered, "fresh-subworkflow");
            var cancellation = run.CancelRunAsync();
            subworkflowAgents.ReleaseRight.TrySetResult();
            await cancellation;
            await watch.WaitAsync(TimeSpan.FromSeconds(5));
        }
        subworkflowRecorder.CancelRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "cancelled");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "cancelled");
        AssertCancelledJoinPrefix(structuredEvents, "structured");
        AssertCancelledJoinPrefix(subworkflowEvents, "fresh-subworkflow");

        static void AssertCandidateAdmissionAtPause(IReadOnlyList<ExecutionEvent> events,
            TaskCompletionSource leftHandlerEntered, string candidate)
        {
            const string scopeId = "array-loop/0";
            var trace = string.Join(" | ", events.Where(evt => evt.ScopeId == scopeId &&
                evt.NodeId is "anchor-left" or "agent-left" or "agent-right")
                .Select(evt => $"{evt.Sequence}:{evt.Kind}:{evt.NodeId}"));
            var expectedSiblingAdmission = candidate == "fresh-subworkflow";
            var leftTargetDeliveredAtPause = events.Any(evt =>
                evt.Kind == ExecutionEventKinds.ExecutionActivated && evt.NodeId == "anchor-left" &&
                evt.ScopeId == scopeId);
            Assert.True(expectedSiblingAdmission == leftTargetDeliveredAtPause, trace);
            var leftStartedAtPause = events.Any(evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == "agent-left" && evt.ScopeId == scopeId);
            Assert.True(expectedSiblingAdmission == leftStartedAtPause, trace);
            Assert.True(expectedSiblingAdmission == leftHandlerEntered.Task.IsCompleted, trace);
        }

        static void AssertCancelledJoinPrefix(IReadOnlyList<ExecutionEvent> events, string candidate)
        {
            const string scopeId = "array-loop/0";
            var rightStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == "agent-right" && evt.ScopeId == scopeId);
            var rightCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "agent-right" && evt.ScopeId == scopeId);
            Assert.Equal(rightStarted.InvocationId, rightCompleted.InvocationId);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "join" && evt.ScopeId == scopeId);
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/1");
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Cancelled);
            if (candidate == "fresh-subworkflow")
            {
                var leftStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                    evt.NodeId == "agent-left" && evt.ScopeId == scopeId);
                var leftCancelled = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
                    evt.NodeId == "agent-left" && evt.ScopeId == scopeId);
                Assert.Equal(leftStarted.InvocationId, leftCancelled.InvocationId);
                Assert.True(rightCompleted.Sequence < leftCancelled.Sequence);
            }
            else
            {
                Assert.DoesNotContain(events, evt =>
                    (evt.Kind is ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeCancelled) &&
                    evt.NodeId == "agent-left" && evt.ScopeId == scopeId);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredStagedForkCandidate_StagingFailureDoesNotStartAnyBranch(bool sqlite)
    {
        var revision = WorkflowRevision.Create(ParallelBodyDefinition(),
            "array-iteration-candidate-fork-staging-failure-proof");
        using var runStore = new ProofRunStore(revision, "north", sqlite);
        var recorder = new ProofRecorder(runStore);
        var stagedTargets = new List<(int Ordinal, string NodeId)>();

        var failure = await Record.ExceptionAsync(() =>
            new WorkflowRunner(agents: new EchoAgentExecutor())
                .ExecuteStructuredSchedulerProofAsync(runStore.RunId, revision, "north", null,
                    evt => { recorder.Append(evt); return ValueTask.CompletedTask; },
                    activationScopeId: "array-loop/0", emitRunLifecycle: false,
                    stageForkTargetsBeforeDispatch: true,
                    afterForkTargetStaged: (ordinal, target) =>
                    {
                        stagedTargets.Add((ordinal, target));
                        return ordinal == 1
                            ? ValueTask.FromException(new IOException("Injected fork staging failure."))
                            : ValueTask.CompletedTask;
                    }));
        Assert.IsType<IOException>(failure);
        recorder.FailRoot();

        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "failed");
        Assert.Equal([(1, "anchor-left")], stagedTargets);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated &&
            evt.NodeId is "anchor-left" or "anchor-right");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId is "anchor-left" or "anchor-right" or "agent-left" or "agent-right" or "join");
        var forkCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "fork" && evt.ScopeId == "array-loop/0");
        var forkFailed = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
            evt.NodeId == "fork" && evt.ScopeId == "array-loop/0");
        Assert.Equal(forkCompleted.InvocationId, forkFailed.InvocationId);
        Assert.True(forkCompleted.Sequence < forkFailed.Sequence);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredStagedForkCandidate_DispatchesAllTargetsAfterStaging(bool sqlite)
    {
        var revision = WorkflowRevision.Create(ParallelBodyDefinition(),
            "array-iteration-candidate-fork-staging-success-proof");
        using var runStore = new ProofRunStore(revision, "north", sqlite);
        var recorder = new ProofRecorder(runStore);
        var stagedTargets = new List<(int Ordinal, string NodeId)>();
        var stagedCountAtFirstBranchActivation = 0;
        var branchActivationAfterForkCompletion = true;
        var forkCompletionObserved = false;

        var result = await new WorkflowRunner(agents: new EchoAgentExecutor())
            .ExecuteStructuredSchedulerProofAsync(runStore.RunId, revision, "north", null,
                evt =>
                {
                    recorder.Append(evt);
                    if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "fork")
                        forkCompletionObserved = true;
                    if (evt.Kind == ExecutionEventKinds.ExecutionActivated &&
                        evt.NodeId is "anchor-left" or "anchor-right")
                    {
                        if (stagedCountAtFirstBranchActivation == 0)
                            stagedCountAtFirstBranchActivation = stagedTargets.Count;
                        branchActivationAfterForkCompletion &= forkCompletionObserved;
                    }
                    return ValueTask.CompletedTask;
                }, stageForkTargetsBeforeDispatch: true,
                afterForkTargetStaged: (ordinal, target) =>
                {
                    stagedTargets.Add((ordinal, target));
                    return ValueTask.CompletedTask;
                });

        Assert.False(result.Failed);
        Assert.NotNull(result.Output);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        Assert.Equal([(1, "anchor-left"), (2, "anchor-right")], stagedTargets);
        Assert.Equal(2, stagedCountAtFirstBranchActivation);
        Assert.True(branchActivationAfterForkCompletion);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent-left");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent-right");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_CancelSiblingAfterHandlerEntry(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-parallel-sibling-entry-proof");
        const string input = "[\"north\",\"south\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var structuredAgents = new CoordinatedBranchFailureAgentExecutor();
        var subworkflowAgents = new CoordinatedBranchFailureAgentExecutor();

        var structuredFailure = await Record.ExceptionAsync(() =>
            new WorkflowRunner(agents: structuredAgents)
                .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                    evt => { structuredRecorder.Append(evt); return ValueTask.CompletedTask; }));
        Assert.NotNull(structuredFailure);

        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, new EchoToolBroker());
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        var workflowError = false;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
            await foreach (var evt in run.WatchStreamAsync()) workflowError |= evt is WorkflowErrorEvent;
        Assert.True(workflowError);
        subworkflowRecorder.FailRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "failed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "failed");
        Assert.Equal(["agent-left", "agent-right"], structuredAgents.EnteredNodes.Order(StringComparer.Ordinal));
        Assert.Equal(structuredAgents.EnteredNodes.Order(StringComparer.Ordinal),
            subworkflowAgents.EnteredNodes.Order(StringComparer.Ordinal));
        AssertParallelSiblingCancellation(structuredEvents);
        AssertParallelSiblingCancellation(subworkflowEvents);
        Assert.Equal(subworkflowEvents.Where(IsBranchLifecycleEvent).Select(EventShape),
            structuredEvents.Where(IsBranchLifecycleEvent).Select(EventShape));

        static (string Kind, string? NodeId, string? ScopeId, string? ConnectionId) EventShape(ExecutionEvent evt) =>
            (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId);
        static bool IsBranchLifecycleEvent(ExecutionEvent evt) =>
            evt.NodeId is "agent-left" or "agent-right" or "join";
        static void AssertParallelSiblingCancellation(IReadOnlyList<ExecutionEvent> events)
        {
            Assert.Equal(["array-loop/0"], events
                .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
                .Select(evt => evt.ScopeId));
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId == "agent-left" && evt.ScopeId == "array-loop/0");
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
                evt.NodeId == "agent-right" && evt.ScopeId == "array-loop/0");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/1");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_CancelSiblingBeforeNodeAdmission(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-parallel-prestart-gate-proof");
        const string input = "[\"north\",\"south\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var structuredGate = new CoordinatedPreAdmissionGate();
        var subworkflowGate = new CoordinatedPreAdmissionGate();
        var structuredAgents = new CoordinatedGateFailureAgentExecutor(structuredGate);
        var subworkflowAgents = new CoordinatedGateFailureAgentExecutor(subworkflowGate);

        var structuredFailure = await Record.ExceptionAsync(() =>
            new WorkflowRunner(agents: structuredAgents)
                .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                    evt => { structuredRecorder.Append(evt); return ValueTask.CompletedTask; }, gate: structuredGate));
        Assert.NotNull(structuredFailure);

        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, new EchoToolBroker(), subworkflowGate);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        var workflowError = false;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
            await foreach (var evt in run.WatchStreamAsync()) workflowError |= evt is WorkflowErrorEvent;
        Assert.True(workflowError);
        subworkflowRecorder.FailRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "failed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "failed");
        Assert.True(structuredGate.RightReached.Task.IsCompletedSuccessfully);
        Assert.True(subworkflowGate.RightReached.Task.IsCompletedSuccessfully);
        Assert.Equal(["agent-left"], structuredAgents.EnteredNodes);
        Assert.Equal(structuredAgents.EnteredNodes, subworkflowAgents.EnteredNodes);
        AssertPreStartSiblingCancellation(structuredEvents);
        AssertPreStartSiblingCancellation(subworkflowEvents);
        Assert.Equal(subworkflowEvents.Where(IsBranchLifecycleEvent).Select(EventShape),
            structuredEvents.Where(IsBranchLifecycleEvent).Select(EventShape));

        static (string Kind, string? NodeId, string? ScopeId, string? ConnectionId) EventShape(ExecutionEvent evt) =>
            (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId);
        static bool IsBranchLifecycleEvent(ExecutionEvent evt) =>
            evt.NodeId is "agent-left" or "agent-right" or "join";
        static void AssertPreStartSiblingCancellation(IReadOnlyList<ExecutionEvent> events)
        {
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId == "agent-left" && evt.ScopeId == "array-loop/0");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
                evt.NodeId == "agent-right");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent-right");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/1");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredAndFreshSubworkflowCandidates_CancelAdmittedSiblingBeforeAgentHandler(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-parallel-admitted-sibling-proof");
        const string input = "[\"north\",\"south\"]";
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var structuredGate = new CoordinatedPostStartGate();
        var subworkflowGate = new CoordinatedPostStartGate();
        var structuredAgents = new CoordinatedPostStartFailureAgentExecutor(structuredGate);
        var subworkflowAgents = new CoordinatedPostStartFailureAgentExecutor(subworkflowGate);

        var structuredFailure = await Record.ExceptionAsync(() =>
            new WorkflowRunner(agents: structuredAgents)
                .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                    evt => { structuredRecorder.Append(evt); return ValueTask.CompletedTask; },
                    afterNodeStarted: structuredGate.WaitAsync));
        Assert.NotNull(structuredFailure);

        var iterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision, subworkflowRecorder,
            subworkflowAgents, new EchoToolBroker(), afterNodeStarted: subworkflowGate.WaitAsync);
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
        var workflowError = false;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
            await foreach (var evt in run.WatchStreamAsync()) workflowError |= evt is WorkflowErrorEvent;
        Assert.True(workflowError);
        subworkflowRecorder.FailRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "failed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "failed");
        Assert.True(structuredGate.RightNodeStarted.Task.IsCompletedSuccessfully);
        Assert.True(subworkflowGate.RightNodeStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(["agent-left"], structuredAgents.EnteredNodes);
        Assert.Equal(structuredAgents.EnteredNodes, subworkflowAgents.EnteredNodes);
        AssertAdmittedSiblingCancellation(structuredEvents);
        AssertAdmittedSiblingCancellation(subworkflowEvents);
        Assert.Equal(subworkflowEvents.Where(IsBranchLifecycleEvent).Select(EventShape),
            structuredEvents.Where(IsBranchLifecycleEvent).Select(EventShape));

        static (string Kind, string? NodeId, string? ScopeId, string? ConnectionId) EventShape(ExecutionEvent evt) =>
            (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId);
        static bool IsBranchLifecycleEvent(ExecutionEvent evt) =>
            evt.NodeId is "agent-left" or "agent-right" or "join";
        static void AssertAdmittedSiblingCancellation(IReadOnlyList<ExecutionEvent> events)
        {
            var rightStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == "agent-right" && evt.ScopeId == "array-loop/0");
            var leftFailed = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId == "agent-left" && evt.ScopeId == "array-loop/0");
            var rightCancelled = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
                evt.NodeId == "agent-right" && evt.ScopeId == "array-loop/0");
            Assert.Equal(rightStarted.InvocationId, rightCancelled.InvocationId);
            Assert.True(rightStarted.Sequence < leftFailed.Sequence);
            Assert.True(leftFailed.Sequence < rightCancelled.Sequence);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/1");
        }
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
    public async Task HybridFrameCoordinator_UsesFreshProductionWorkflowBodyAndOneNestedRunLedger(bool sqlite)
    {
        var definition = NestedBodyDefinition();
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-hybrid-nested-frame-proof");
        const string input = "[\"alpha\",\"beta\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var outerParameters = ImmutableSortedDictionary<string, string>.Empty.Add("outerIndex", "7");
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 7, "outer", outerParameters);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents, tools,
            parentFrame: parentFrame, loopNodeId: "inner-loop");
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        string? output = null;
        await foreach (var evt in run.WatchStreamAsync())
            if (evt is WorkflowOutputEvent { Data: string value }) output = value;
        recorder.CompleteRoot();

        Assert.Equal("echo:ALPHA:0/7;echo:BETA:1/7", output);
        Assert.Equal(["ALPHA:0/7", "BETA:1/7"], agents.Inputs);
        Assert.Equal(agents.Inputs, tools.Queries);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        for (var index = 0; index < 2; index++)
        {
            var scopeId = $"outer-loop/7/inner-loop/{index}";
            var scoped = events.Where(evt => evt.ScopeId == scopeId).ToArray();
            Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
                evt.NodeId == "inner-loop");
            Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.NodeId == "inner-loop");
            Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent");
            Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool");
            Assert.Equal("default", ReadVariablePreviousValue(scoped, "set"));
            Assert.Equal(index == 0 ? "alpha" : "beta",
                ReadVariableValue(scoped, ExecutionEventKinds.VariableRead, "get"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridFrameCoordinator_ExecutesConfigurableParallelJoinUnderNestedActivation(bool sqlite)
    {
        var definition = ParallelBodyDefinition();
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-parallel-proof");
        const string input = "[\"north\",\"south\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 4, "outer", ImmutableSortedDictionary<string, string>.Empty);
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents, tools,
            parentFrame: parentFrame, loopNodeId: "inner-loop");
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
        for (var index = 0; index < 2; index++)
        {
            var scopeId = $"outer-loop/4/inner-loop/{index}";
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
                evt.ScopeId == scopeId && evt.NodeId == "inner-loop");
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress &&
                evt.NodeId == "join" && evt.ScopeId == scopeId));
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId is "agent-left" or "agent-right" && evt.ScopeId == scopeId));
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.ScopeId == scopeId && evt.NodeId == "inner-loop");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridFrameCoordinator_FailedNestedParallelBranchSuppressesJoin(bool sqlite)
    {
        var definition = ParallelBodyDefinition();
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-hybrid-nested-join-failure-proof");
        const string input = "[\"north\",\"must-not-run\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 4, "outer", ImmutableSortedDictionary<string, string>.Empty);
        var rightBranchArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agents = new FirstSuccessThenFailAgentExecutor(rightBranchArrived);
        ValueTask Observe(ExecutionEvent evt)
        {
            if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent-right")
                rightBranchArrived.TrySetResult();
            return ValueTask.CompletedTask;
        }
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents,
            new EchoToolBroker(), observeEvent: Observe, parentFrame: parentFrame, loopNodeId: "inner-loop");
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        var workflowFailed = false;
        await foreach (var evt in run.WatchStreamAsync())
            workflowFailed |= evt is WorkflowErrorEvent;
        recorder.FailRoot();

        Assert.True(workflowFailed);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "failed");
        const string failedScopeId = "outer-loop/4/inner-loop/0";
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "agent-right" && evt.ScopeId == failedScopeId);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
            evt.NodeId == "agent-left" && evt.ScopeId == failedScopeId);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "join" && evt.ScopeId == failedScopeId);
        Assert.DoesNotContain(events, evt => evt.ScopeId == "outer-loop/4/inner-loop/1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridFrameCoordinator_CancelsNestedParallelBranchesBeforeNextItem(bool sqlite)
    {
        var definition = ParallelBodyDefinition();
        var revision = WorkflowRevision.Create(definition, "array-iteration-hybrid-nested-join-cancel-proof");
        const string input = "[\"north\",\"must-not-run\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 4, "outer", ImmutableSortedDictionary<string, string>.Empty);
        var agents = new TwoBranchBlockingAgentExecutor();
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents,
            new EchoToolBroker(), parentFrame: parentFrame, loopNodeId: "inner-loop");
        var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
        var watch = Task.Run(async () =>
        {
            await foreach (var _ in run.WatchStreamAsync()) { }
        });

        try
        {
            await agents.BothBranchesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await run.CancelRunAsync();
            await watch.WaitAsync(TimeSpan.FromSeconds(5));
            recorder.CancelRoot();
        }
        finally
        {
            if (!watch.IsCompleted) await run.CancelRunAsync();
        }

        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "cancelled");
        const string scopeId = "outer-loop/4/inner-loop/0";
        foreach (var nodeId in new[] { "agent-left", "agent-right" })
        {
            var started = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == nodeId && evt.ScopeId == scopeId);
            var cancelled = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
                evt.NodeId == nodeId && evt.ScopeId == scopeId);
            Assert.Equal(started.InvocationId, cancelled.InvocationId);
        }
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "join" && evt.ScopeId == scopeId);
        Assert.DoesNotContain(events, evt => evt.ScopeId == "outer-loop/4/inner-loop/1");
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
            maximumOutcomeBytes: 64_000,
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
    [InlineData(false, false, 8, 0)]
    [InlineData(true, false, 8, 0)]
    [InlineData(false, true, 8, 0)]
    [InlineData(true, true, 8, 0)]
    [InlineData(false, false, 1, 0)]
    [InlineData(true, false, 1, 0)]
    [InlineData(false, false, 8, int.MaxValue)]
    [InlineData(true, false, 8, int.MaxValue)]
    public async Task ParallelDelegateFailure_PreservesUnsettledReservations(bool sqlite,
        bool rejectChildOutcomeAppend, int maximumConcurrentReservations, int researcherFailureStatusCode)
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
            maximumOutcomeBytes: 64_000,
            maximumConcurrentOutcomeReservations: maximumConcurrentReservations);
        var model = new ParallelDelegateEvidenceModel(parentInstructions, researcherInstructions,
            reviewerInstructions, "Review this proposal.", 64, failResearcher: !rejectChildOutcomeAppend,
            failParentFollowup: true, researcherFailureStatusCode: researcherFailureStatusCode is > 0
                ? researcherFailureStatusCode
                : null);
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
        if (researcherFailureStatusCode > 0)
        {
            var failureEvents = events.Where(evt =>
            {
                if (evt.Kind != ExecutionEventKinds.AgentFailed) return false;
                using var envelope = JsonDocument.Parse(evt.Data!);
                if (!envelope.RootElement.TryGetProperty("eventData", out var eventData) ||
                    eventData.ValueKind != JsonValueKind.String) return false;
                using var failurePayload = JsonDocument.Parse(eventData.GetString()!);
                return failurePayload.RootElement.TryGetProperty("statusCode", out var statusCode) &&
                    statusCode.GetInt32() == researcherFailureStatusCode;
            }).ToArray();
            Assert.True(failureEvents.Length == 1,
                $"Expected one provider status failure envelope, observed: {string.Join(" | ", events.Where(evt => evt.Kind == ExecutionEventKinds.AgentFailed).Select(evt => evt.Data))}");
            var maximumShapeFailure = Assert.Single(failureEvents);
            var failureEnvelopeBytes = events.Where(evt => evt.Kind is ExecutionEventKinds.AgentFailed or
                    ExecutionEventKinds.NodeFailed or ExecutionEventKinds.Failed)
                .Sum(EventBytes);
            Assert.True(EventBytes(maximumShapeFailure) < 1_000);
            Assert.True(failureEnvelopeBytes < 64_000);
            Console.WriteLine($"Maximum provider status failure event: {EventBytes(maximumShapeFailure)} UTF-8 bytes; " +
                $"delegated failure envelope: {failureEnvelopeBytes} bytes.");
        }

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
            maximumOutcomeBytes: 64_000,
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
            maximumOutcomeBytes: 64_000,
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
            maximumOutcomeBytes: responseBytes + 512);

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
            maximumOutcomeBytes: responseBytes + 512);

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
            maximumOutcomeBytes: 128_000);

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
            maximumOutcomeBytes: responseBytes + 512);

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
            maximumOutcomeBytes: 128_000);

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
            maximumOutcomeBytes: 32_000,
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
    public async Task HybridFrameCoordinator_CancelsNestedActiveItemAndPersistsCancellation(bool sqlite)
    {
        var definition = BodyDefinition();
        var revision = WorkflowRevision.Create(definition, "array-iteration-cancellation-proof");
        const string input = "[\"hold\",\"must-not-run\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new BlockingAgentExecutor("hold");
        var tools = new EchoToolBroker();
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 4, "outer", ImmutableSortedDictionary<string, string>.Empty);
        var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder, agents, tools,
            parentFrame: parentFrame, loopNodeId: "inner-loop");
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
        const string cancelledScopeId = "outer-loop/4/inner-loop/0";
        var cancelledScope = events.Where(evt => evt.ScopeId == cancelledScopeId).ToArray();
        var agentStarted = Assert.Single(cancelledScope,
            evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent");
        var agentCancelled = Assert.Single(cancelledScope,
            evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "agent");
        Assert.Equal(agentStarted.InvocationId, agentCancelled.InvocationId);
        Assert.DoesNotContain(events, evt => evt.ScopeId == "outer-loop/4/inner-loop/1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridFrameCoordinator_BreakStopsNearestNestedLoopAndOuterLoopContinues(bool sqlite)
    {
        var definition = NestedBodyDefinition();
        var revision = WorkflowRevision.Create(definition, "array-iteration-hybrid-nested-break-proof");
        const string input = "[\"outer-a\",\"outer-b\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker(query => query.StartsWith("BREAK:", StringComparison.Ordinal)
            ? HybridLoopProofBreak.Signal
            : $"echo:{query}");
        var breakDispatches = new ConcurrentQueue<HybridLoopProofBreakDispatch>();

        var output = await RunHybridLoopProofAsync(runStore.RunId, revision, recorder, agents, tools,
            "outer-loop", ["outer-a", "outer-b"], parentFrame: null, indexParameterId: "outerIndex",
            breakDispatches, async outerFrame =>
            {
                var innerOutput = await RunHybridLoopProofAsync(runStore.RunId, revision, recorder, agents,
                    tools, "inner-loop", ["continue", "break", "must-not-run"], outerFrame, "index",
                    breakDispatches, async innerFrame =>
                    {
                        ValueTask Record(ExecutionEvent evt)
                        {
                            if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                                ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or
                                ExecutionEventKinds.Terminated)
                                return ValueTask.CompletedTask;
                            recorder.Append(evt with { ScopeId = innerFrame.ScopeId });
                            return ValueTask.CompletedTask;
                        }

                        var body = await new WorkflowRunner(tools, agents).ExecuteWithOutcomeAsync(
                            runStore.RunId, revision, innerFrame.Item, innerFrame.Parameters, Record);
                        return body.Output ?? throw new InvalidOperationException("The nested body had no output.");
                    });
                return $"outer-{outerFrame.Index}:{innerOutput}";
            });
        recorder.CompleteRoot();

        Assert.Equal("outer-0:echo:CONTINUE:0/0;outer-1:echo:CONTINUE:0/1", output);
        Assert.Equal(["CONTINUE:0/0", "BREAK:1/0", "CONTINUE:0/1", "BREAK:1/1"], agents.Inputs);
        Assert.Equal(agents.Inputs, tools.Queries);
        var dispatches = breakDispatches.ToArray();
        Assert.Equal(2, dispatches.Length);
        Assert.All(dispatches, dispatch => Assert.Equal(dispatch.SourceFrameId, dispatch.TargetFrameId));
        Assert.Equal(["outer-loop/0/inner-loop/1", "outer-loop/1/inner-loop/1"],
            dispatches.Select(dispatch => dispatch.ScopeId));

        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        Assert.DoesNotContain(events, evt => evt.ScopeId is
            "outer-loop/0/inner-loop/2" or "outer-loop/1/inner-loop/2");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.NodeId == "outer-loop" && evt.ScopeId == "outer-loop/1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridFrameCoordinator_SharesEvidenceBudgetAcrossFreshNestedBodies(bool sqlite)
    {
        var definition = BodyDefinition(includeVariableStore: true, includeIndexParameter: true);
        var revision = WorkflowRevision.Create(definition, "array-iteration-hybrid-shared-evidence-budget-proof");
        const string input = "[\"first\",\"second\"]";
        var provider = new AgentProviderDescriptor("proof.echo-agent", "Proof model", "proof-chat-v1", true);

        using var baselineStore = new ProofRunStore(revision, input, sqlite);
        var baselineRecorder = new ReservedEvidenceProofRecorder(baselineStore, maximumEvents: 512,
            maximumBytes: 4_000_000, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
            maximumOutcomeBytes: 64_000);
        var baselineModel = new SharedBudgetProofModel();
        var baselineAgents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SharedBudgetProofChatClient(baselineModel))],
            new EchoToolBroker());
        var baselineTools = new EchoToolBroker();
        var baseline = await ExecuteHybridBudgetLoopAsync(baselineStore, revision, baselineRecorder,
            baselineAgents, baselineTools, input);

        Assert.Null(baseline.Error);
        Assert.Equal("echo:bounded;echo:bounded", baseline.Output);
        Assert.Equal(2, baselineModel.Calls);
        Assert.Equal(["bounded", "bounded"], baselineTools.Queries);
        var baselineEvents = baselineRecorder.Snapshot();
        AssertPersistedSequence(baselineStore, baselineEvents, "completed");
        var secondRequest = Assert.Single(baselineEvents, evt => evt.Kind == ExecutionEventKinds.ModelRequested &&
            evt.ScopeId == "outer-loop/0/array-loop/1");

        const int reservedFailureEvents = 8;
        var maximumEvents = checked((int)secondRequest.Sequence - 1 + reservedFailureEvents);
        using var limitedStore = new ProofRunStore(revision, input, sqlite);
        var limitedRecorder = new ReservedEvidenceProofRecorder(limitedStore, maximumEvents,
            maximumBytes: 4_000_000, reservedFailureEvents: reservedFailureEvents,
            reservedFailureBytes: 64_000, maximumOutcomeBytes: 64_000);
        var limitedModel = new SharedBudgetProofModel();
        var limitedAgents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SharedBudgetProofChatClient(limitedModel))],
            new EchoToolBroker());
        var limitedTools = new EchoToolBroker();
        var limited = await ExecuteHybridBudgetLoopAsync(limitedStore, revision, limitedRecorder,
            limitedAgents, limitedTools, input);

        Assert.Contains("run evidence budget", limited.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, limitedModel.Calls);
        Assert.Equal(["bounded"], limitedTools.Queries);
        Assert.Equal(0, limitedRecorder.PendingOutcomeReservations);
        var limitedEvents = limitedRecorder.Snapshot();
        AssertPersistedSequence(limitedStore, limitedEvents, "failed");
        Assert.Single(limitedEvents, evt => evt.Kind == ExecutionEventKinds.ModelRequested &&
            evt.ScopeId == "outer-loop/0/array-loop/0");
        Assert.Contains(limitedEvents, evt => evt.Kind == ExecutionEventKinds.ModelResponded &&
            evt.ScopeId == "outer-loop/0/array-loop/0");
        Assert.Contains(limitedEvents, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "tool" && evt.ScopeId == "outer-loop/0/array-loop/0");
        Assert.Contains(limitedEvents, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.NodeId == "array-loop" && evt.ScopeId == "outer-loop/0/array-loop/1");
        Assert.Contains(limitedEvents, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "agent" && evt.ScopeId == "outer-loop/0/array-loop/1");
        Assert.DoesNotContain(limitedEvents, evt =>
            (evt.Kind is ExecutionEventKinds.ModelRequested or ExecutionEventKinds.ModelResponded) &&
            evt.ScopeId == "outer-loop/0/array-loop/1");
        var failedAgent = Assert.Single(limitedEvents, evt => evt.Kind == ExecutionEventKinds.AgentFailed &&
            evt.ScopeId == "outer-loop/0/array-loop/1");
        Assert.Equal(failedAgent.InvocationId, Assert.Single(limitedEvents, evt =>
            evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent" &&
            evt.ScopeId == "outer-loop/0/array-loop/1").InvocationId);
        Assert.Contains(limitedEvents, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
        Assert.DoesNotContain(limitedEvents, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
            evt.ScopeId == "outer-loop/0/array-loop/1");
        Assert.True(limitedEvents.Length <= maximumEvents);
        Assert.True(limitedRecorder.PersistedEventBytes <= 4_000_000);
        Assert.Equal(limitedEvents.Sum(EventBytes), limitedRecorder.PersistedEventBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_EnforceSharedEventAndByteBudgetsAcrossActivations(bool sqlite)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-shared-evidence-budget-proof");
        const string input = "[\"first\",\"second\"]";

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var baseline = await ExecuteCandidateAsync(candidate, maximumEvents: 512);
            Assert.Equal("echo:bounded;echo:bounded", baseline.Output);
            Assert.Equal(2, baseline.ModelCalls);
            Assert.Equal(["bounded", "bounded"], baseline.ToolQueries);

            var secondIterationScope = candidate == "hybrid"
                ? "outer-loop/0/array-loop/1"
                : "array-loop/1";
            var secondRequest = Assert.Single(baseline.Events, evt =>
                evt.Kind == ExecutionEventKinds.ModelRequested && evt.ScopeId == secondIterationScope);
            const int reservedFailureEvents = 8;
            var maximumEvents = checked((int)secondRequest.Sequence - 1 + reservedFailureEvents);
            var eventLimited = await ExecuteCandidateAsync(candidate, maximumEvents);
            AssertBudgetRejected(eventLimited, candidate, maximumEvents, maximumBytes: 4_000_000);

            const long maximumOutcomeBytes = 64_000;
            const long reservedFailureBytes = 64_000;
            const long byteAdmissionMargin = 1_024;
            var prefixBytes = baseline.Events.Where(evt => evt.Sequence < secondRequest.Sequence).Sum(EventBytes);
            var maximumBytes = checked(prefixBytes + EventBytes(secondRequest) + maximumOutcomeBytes +
                reservedFailureBytes - byteAdmissionMargin);
            var byteLimited = await ExecuteCandidateAsync(candidate, maximumEvents: 512, maximumBytes);
            Assert.True(byteLimited.ReservationRejected,
                $"{candidate} did not reject the second request at {maximumBytes} bytes; " +
                $"prefix {prefixBytes}, request {EventBytes(secondRequest)}, outcome reserve " +
                $"{maximumOutcomeBytes}, failure reserve {reservedFailureBytes}; persisted " +
                $"{byteLimited.PersistedEventBytes} bytes across {byteLimited.Events.Length} events.");
            AssertBudgetRejected(byteLimited, candidate, maximumEvents: 512, maximumBytes);
        }

        void AssertBudgetRejected(CandidateBudgetResult limited, string candidate, int maximumEvents,
            long maximumBytes)
        {
            var iterationZeroScope = candidate == "hybrid"
                ? "outer-loop/0/array-loop/0"
                : "array-loop/0";
            var iterationOneScope = candidate == "hybrid"
                ? "outer-loop/0/array-loop/1"
                : "array-loop/1";
            Assert.Null(limited.Output);
            Assert.True(limited.ReservationRejected);
            Assert.Equal(1, limited.ModelCalls);
            Assert.Equal(["bounded"], limited.ToolQueries);
            Assert.Equal(0, limited.PendingOutcomeReservations);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ModelRequested &&
                evt.ScopeId == iterationZeroScope);
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.ModelResponded &&
                evt.ScopeId == iterationZeroScope);
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "tool" && evt.ScopeId == iterationZeroScope);
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
                evt.NodeId == "array-loop" && evt.ScopeId == iterationOneScope);
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == "agent" && evt.ScopeId == iterationOneScope);
            Assert.DoesNotContain(limited.Events, evt =>
                (evt.Kind is ExecutionEventKinds.ModelRequested or ExecutionEventKinds.ModelResponded) &&
                evt.ScopeId == iterationOneScope);
            var failedAgent = Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.AgentFailed &&
                evt.ScopeId == iterationOneScope);
            Assert.Equal(failedAgent.InvocationId, Assert.Single(limited.Events, evt =>
                evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent" &&
                evt.ScopeId == iterationOneScope).InvocationId);
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.ScopeId == iterationOneScope);
            Assert.True(limited.Events.Length <= maximumEvents);
            Assert.True(limited.PersistedEventBytes <= maximumBytes);
            Assert.Equal(limited.Events.Sum(EventBytes), limited.PersistedEventBytes);
        }

        async Task<CandidateBudgetResult> ExecuteCandidateAsync(string candidate, int maximumEvents,
            long maximumBytes = 4_000_000)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents,
                maximumBytes, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 64_000);
            var model = new SharedBudgetProofModel();
            var provider = new AgentProviderDescriptor("proof.echo-agent", "Proof model", "proof-chat-v1", true);
            var agents = new AgentNodeExecutor(
                [new AgentProviderRegistration(provider, () => new SharedBudgetProofChatClient(model))],
                new EchoToolBroker());
            var tools = new EchoToolBroker();
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (EvidenceBudgetExhaustedException)
                {
                }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await recorder.RecordResequencedAsync(new ExecutionEvent(store.RunId,
                    store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.Started));
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }

                await recorder.RecordResequencedAsync(new ExecutionEvent(store.RunId,
                    store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow,
                    failed ? ExecutionEventKinds.Failed : ExecutionEventKinds.Completed));
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, output is null ? "failed" : "completed");
            return new(output, events, model.Calls, tools.Queries, recorder.PendingOutcomeReservations,
                recorder.PersistedEventBytes, recorder.ReservationRejected.IsCompleted);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_EnforceAggregateWorkLimitsAcrossActivations(bool sqlite)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-aggregate-work-limit-proof");
        const string input = "[\"first\",\"second\"]";

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var baseline = await ExecuteCandidateAsync(candidate);
            Assert.Equal("echo:bounded;echo:bounded", baseline.Output);
            Assert.Equal(2, baseline.ModelCalls);
            Assert.Equal(["bounded", "bounded"], baseline.ToolQueries);
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized));
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolCompleted));

            foreach (var workLimit in new[] { "activations", "model-dispatches", "tool-dispatches" })
            {
                var limited = await ExecuteCandidateAsync(candidate, workLimit);
                Assert.Null(limited.Output);
                Assert.True(limited.WorkLimitRejected, $"{candidate} did not enforce {workLimit}.");
                Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);

                var expectedModelCalls = workLimit == "tool-dispatches" ? 2 : 1;
                Assert.Equal(expectedModelCalls, limited.ModelCalls);
                Assert.Single(limited.ToolQueries);
                Assert.Equal(workLimit == "activations" ? 1 : 2,
                    limited.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
                Assert.Equal(expectedModelCalls,
                    limited.Events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested));
                Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
                Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            }
        }

        async Task<CandidateWorkLimitResult> ExecuteCandidateAsync(string candidate, string? workLimit = null)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents: 512,
                maximumBytes: 4_000_000, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 64_000,
                maximumLoopActivations: workLimit == "activations" ? 1 : int.MaxValue,
                maximumModelDispatches: workLimit == "model-dispatches" ? 1 : int.MaxValue,
                maximumToolDispatches: workLimit == "tool-dispatches" ? 1 : int.MaxValue);
            var model = new SharedBudgetProofModel();
            var provider = new AgentProviderDescriptor("proof.echo-agent", "Proof model", "proof-chat-v1", true);
            var tools = new EchoToolBroker(recordToolDispatchEvents: true);
            var agents = new AgentNodeExecutor(
                [new AgentProviderRegistration(provider, () => new SharedBudgetProofChatClient(model))],
                new EchoToolBroker());
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (EvidenceBudgetExhaustedException)
                {
                }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await recorder.RecordResequencedAsync(new ExecutionEvent(store.RunId,
                    store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.Started));
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }

                await recorder.RecordResequencedAsync(new ExecutionEvent(store.RunId,
                    store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow,
                    failed ? ExecutionEventKinds.Failed : ExecutionEventKinds.Completed));
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            var expectedStatus = output is null ? "failed" : "completed";
            AssertPersistedSequence(store, events, expectedStatus);
            var reopened = store.ReopenStore().Get(store.RunId)!;
            Assert.Equal(expectedStatus, reopened.Status);
            Assert.Equal(events, reopened.Events);
            Assert.Equal(Enumerable.Range(1, events.Length).Select(value => (long)value),
                reopened.Events.Select(evt => evt.Sequence));
            return new(output, events, model.Calls, tools.Queries, recorder.WorkLimitRejected.IsCompleted);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ModelOutcomeAppendFailureStopsLaterActivations(bool sqlite)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-model-outcome-append-failure-proof");
        const string input = "[\"first\",\"second\"]";
        const string providerId = "proof.echo-agent";

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            using var store = new ProofRunStore(revision, input, sqlite,
                rejectedAppendEventKinds: [ExecutionEventKinds.ModelResponded]);
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 64_000);
            var model = new SharedBudgetProofModel();
            var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
            var agents = new AgentNodeExecutor(
                [new AgentProviderRegistration(provider, () => new SharedBudgetProofChatClient(model))],
                new EchoToolBroker());
            var tools = new EchoToolBroker();
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (IOException)
                {
                    // The provider returned, but the store rejected its outcome event.
                }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await recorder.RecordResequencedAsync(new ExecutionEvent(store.RunId,
                    store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.Started));
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }

                await recorder.RecordResequencedAsync(new ExecutionEvent(store.RunId,
                    store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow,
                    failed ? ExecutionEventKinds.Failed : ExecutionEventKinds.Completed));
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            Assert.Null(output);
            Assert.Equal(1, model.Calls);
            Assert.Empty(tools.Queries);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            var activationScope = candidate == "hybrid"
                ? "outer-loop/0/array-loop/0"
                : "array-loop/0";
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed &&
                evt.ScopeId == activationScope);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId == "agent" && evt.ScopeId == activationScope);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(1, recorder.PendingOutcomeReservations);
            Assert.True(recorder.PendingOutcomeBytes > 0);
            AssertPersistedSequence(store, events, "failed");

            var reopened = store.ReopenStore().Get(store.RunId)!;
            Assert.Equal("failed", reopened.Status);
            Assert.Equal(events, reopened.Events);
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_McpOutcomeAppendFailureStopsLaterActivations(bool sqlite)
    {
        const string input = "[\"first\",\"second\"]";
        var registrations = CreateDemoMcpRegistrations();
        var discoveryBroker = new McpToolBroker(registrations, new DemoToolPolicy());
        var slowBinding = Assert.Single(await discoveryBroker.ListBindingsAsync(),
            binding => binding.BindingId == "demo.slow");
        var definition = ParallelMcpDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var configuredTool = new ToolBindingRequest(slowBinding.BindingId, slowBinding.ContractHash)
        {
            DescriptionHash = slowBinding.DescriptionHash
        };
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "tool-research", "tool-review" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Tool = configuredTool,
                McpArgumentsJson = "{}"
            });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes },
            "array-iteration-candidate-mcp-outcome-append-failure-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            using var store = new ProofRunStore(revision, input, sqlite,
                rejectedAppendEventKinds: [ExecutionEventKinds.ToolCompleted]);
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 32_000,
                maximumConcurrentOutcomeReservations: 2,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted,
                additionalReservationOutcomeKind: ExecutionEventKinds.ToolUncertain);
            var tools = new McpToolBroker(registrations, new DemoToolPolicy(), TimeSpan.FromSeconds(20));
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (Exception)
                {
                    // The external tool ran, but the store rejected its completion evidence.
                }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder,
                    new EchoAgentExecutor(), tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null
                    ? ExecutionEventKinds.Failed
                    : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder,
                    new EchoAgentExecutor(), tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            var authorizations = events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            Assert.Null(output);
            Assert.InRange(authorizations, 1, 2);
            Assert.Equal(authorizations, events.Count(evt => evt.Kind == ExecutionEventKinds.ToolUncertain));
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(0, recorder.PendingOutcomeReservations);
            Assert.InRange(recorder.MaximumPendingOutcomeReservationsObserved, 1, 2);
            AssertPersistedSequence(store, events, "failed");

            var reopened = store.ReopenStore().Get(store.RunId)!;
            Assert.Equal("failed", reopened.Status);
            Assert.Equal(events, reopened.Events);
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_RootCompletionAppendFailureRecordsFailedFallback(bool sqlite)
    {
        const string input = "[\"first\",\"second\"]";
        var revision = WorkflowRevision.Create(
            BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-root-completion-append-failure-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            using var store = new ProofRunStore(revision, input, sqlite,
                rejectedAppendEventKinds: [ExecutionEventKinds.Completed]);
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 64_000);
            var agents = new EchoAgentExecutor();
            var tools = new EchoToolBroker();
            Exception? failure = null;

            try
            {
                if (candidate == "structured")
                {
                    await new WorkflowRunner(tools, agents).ExecuteStructuredActivationSequenceProofAsync(
                        store.RunId, revision, input, "index", null, recorder.RecordResequencedAsync);
                }
                else if (candidate == "fresh-subworkflow")
                {
                    await RecordRootAsync(ExecutionEventKinds.Started);
                    var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, tools);
                    var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                    await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                    await foreach (var evt in run.WatchStreamAsync())
                        Assert.IsNotType<WorkflowErrorEvent>(evt);

                    try
                    {
                        await RecordRootAsync(ExecutionEventKinds.Completed);
                    }
                    catch (IOException)
                    {
                        await RecordRootAsync(ExecutionEventKinds.Failed);
                        throw;
                    }
                }
                else
                {
                    await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, tools, input,
                        recordCompletionAppendFailureAsRunFailure: true);
                }
            }
            catch (IOException exception)
            {
                failure = exception;
            }

            Assert.NotNull(failure);
            Assert.Contains("Injected store append failure", failure.ToString(), StringComparison.Ordinal);
            var events = recorder.Snapshot();
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent"));
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool"));
            Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.Completed or
                ExecutionEventKinds.Cancelled);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal("failed", store.Store.Get(store.RunId)!.Status);
            AssertPersistedSequence(store, events, "failed");

            var reopened = store.ReopenStore().Get(store.RunId)!;
            Assert.Equal("failed", reopened.Status);
            Assert.Equal(events, reopened.Events);
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_RootFailureAppendFailureLeavesUnterminalHistory(bool sqlite)
    {
        const string input = "[\"first\",\"second\"]";
        var revision = WorkflowRevision.Create(
            BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-root-failure-append-failure-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            using var store = new ProofRunStore(revision, input, sqlite,
                rejectedAppendEventKinds: [ExecutionEventKinds.Failed]);
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 64_000);
            var agents = new EchoAgentExecutor(failNodeId: "agent");
            var tools = new EchoToolBroker();
            Exception? failure = null;

            try
            {
                if (candidate == "structured")
                {
                    await new WorkflowRunner(tools, agents).ExecuteStructuredActivationSequenceProofAsync(
                        store.RunId, revision, input, "index", null, recorder.RecordResequencedAsync);
                }
                else if (candidate == "fresh-subworkflow")
                {
                    await RecordRootAsync(ExecutionEventKinds.Started);
                    var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, tools);
                    var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                    var workflowFailed = false;
                    await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                    await foreach (var evt in run.WatchStreamAsync())
                        workflowFailed |= evt is WorkflowErrorEvent;

                    Assert.True(workflowFailed);
                    await RecordRootAsync(ExecutionEventKinds.Failed);
                }
                else
                {
                    await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, tools, input);
                }
            }
            catch (IOException exception)
            {
                failure = exception;
            }

            Assert.NotNull(failure);
            Assert.Contains("Injected store append failure", failure.ToString(), StringComparison.Ordinal);
            var events = recorder.Snapshot();
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "agent");
            Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.Completed or
                ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled);
            Assert.Equal("running", store.Store.Get(store.RunId)!.Status);
            AssertPersistedSequence(store, events, "running");

            var reopened = store.ReopenStore().Get(store.RunId)!;
            Assert.Equal("running", reopened.Status);
            Assert.Equal(events, reopened.Events);
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ReserveConcurrentModelOutcomesBeforeDispatch(bool sqlite)
    {
        const string providerId = "proof.iterator-parallel-reservation";
        const string input = "[\"one\"]";
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "agent-left", "agent-right" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Agent = nodes[nodeId].Agent! with
                {
                    ProviderId = providerId,
                    Instructions = $"Concurrent reservation proof for {nodeId}."
                }
            });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes },
            "array-iteration-parallel-reservation-candidate-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var baseline = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 2);
            Assert.NotNull(baseline.Output);
            Assert.Equal(2, baseline.ProviderCalls);
            Assert.Equal(2, baseline.MaximumConcurrentModelCalls);
            Assert.Equal(2, baseline.MaximumPendingReservations);
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested));
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded));
            Assert.Contains(baseline.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");

            var limited = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 1);
            Assert.Null(limited.Output);
            Assert.True(limited.ReservationRejected);
            Assert.Equal(1, limited.ProviderCalls);
            Assert.Equal(1, limited.MaximumConcurrentModelCalls);
            Assert.Equal(1, limited.MaximumPendingReservations);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "join");
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);

            var requests = baseline.Events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested)
                .OrderBy(evt => evt.Sequence).ToArray();
            var secondRequest = requests[1];
            var prefixBytes = baseline.Events.Where(evt => evt.Sequence < secondRequest.Sequence).Sum(EventBytes);
            const long maximumOutcomeBytes = 32_000;
            const long reservedFailureBytes = 64_000;
            const long byteAdmissionMargin = 4_096;
            var maximumBytes = checked(prefixBytes + EventBytes(secondRequest) + (2 * maximumOutcomeBytes) +
                reservedFailureBytes - byteAdmissionMargin);
            var byteLimited = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 2,
                maximumBytes);
            Assert.Null(byteLimited.Output);
            Assert.True(byteLimited.ReservationRejected);
            Assert.Equal(1, byteLimited.ProviderCalls);
            Assert.Equal(1, byteLimited.MaximumConcurrentModelCalls);
            Assert.Equal(1, byteLimited.MaximumPendingReservations);
            Assert.Single(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
            Assert.Single(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
            Assert.DoesNotContain(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "join");
            Assert.Contains(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(0, byteLimited.PendingReservations);
            Assert.True(byteLimited.PersistedEventBytes <= maximumBytes);
            Assert.Equal(byteLimited.Events.Sum(EventBytes), byteLimited.PersistedEventBytes);
        }

        async Task<CandidateReservationResult> ExecuteCandidateAsync(string candidate,
            int maximumConcurrentReservations, long maximumBytes = 4_000_000)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents: 512,
                maximumBytes, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 32_000,
                maximumConcurrentOutcomeReservations: maximumConcurrentReservations);
            var model = new ConcurrentPromptModel(expectedConcurrentCalls: 2,
                releaseOnReservationRejected: recorder.ReservationRejected);
            var provider = new AgentProviderDescriptor(providerId, "Concurrent reservation proof", "proof-chat-v1", true);
            var agents = new AgentNodeExecutor(
                [new AgentProviderRegistration(provider, () => new SynchronizedResponseChatClient(model))],
                new EchoToolBroker());
            var tools = new EchoToolBroker();
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (EvidenceBudgetExhaustedException) when (recorder.ReservationRejected.IsCompleted) { }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null ? ExecutionEventKinds.Failed : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, output is null ? "failed" : "completed");
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
            return new(output, events, model.Calls.Length, model.MaximumConcurrentCalls,
                recorder.MaximumPendingOutcomeReservationsObserved, recorder.PendingOutcomeReservations,
                recorder.PersistedEventBytes, recorder.ReservationRejected.IsCompleted);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ReserveConcurrentMcpOutcomesBeforeDispatch(bool sqlite)
    {
        const string input = "[\"one\"]";
        var definition = ParallelMcpDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-parallel-mcp-reservation-candidate-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var baseline = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 2,
                holdDispatchUntilReservationRejected: false);
            Assert.NotNull(baseline.Output);
            Assert.Equal(2, baseline.DispatchedCalls);
            Assert.Equal(2, baseline.MaximumConcurrentCalls);
            Assert.Equal(2, baseline.MaximumPendingReservations);
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized));
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolCompleted));
            Assert.Contains(baseline.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");

            var limited = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 1,
                holdDispatchUntilReservationRejected: true);
            Assert.Null(limited.Output);
            Assert.True(limited.ReservationRejected);
            Assert.Equal(1, limited.DispatchedCalls);
            Assert.Equal(2, limited.MaximumConcurrentCalls);
            Assert.Equal(1, limited.MaximumPendingReservations);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
        }

        async Task<CandidateMcpReservationResult> ExecuteCandidateAsync(string candidate,
            int maximumConcurrentReservations, bool holdDispatchUntilReservationRejected)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents: 512,
                maximumBytes: 4_000_000, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 32_000,
                maximumConcurrentOutcomeReservations: maximumConcurrentReservations,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted);
            var tools = new ParallelMcpBroker(
                releaseAfterReservationRejected: holdDispatchUntilReservationRejected ? recorder.ReservationRejected : null,
                waitForBothAuthorizations: !holdDispatchUntilReservationRejected);
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (EvidenceBudgetExhaustedException) when (recorder.ReservationRejected.IsCompleted) { }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder,
                    new EchoAgentExecutor(), tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null ? ExecutionEventKinds.Failed : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder,
                    new EchoAgentExecutor(), tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, output is null ? "failed" : "completed");
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
            return new(output, events, tools.DispatchedCalls, tools.MaximumConcurrentCalls,
                recorder.MaximumPendingOutcomeReservationsObserved, recorder.ReservationRejected.IsCompleted);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ReserveConcurrentProductionMcpOutcomesAndBytesBeforeDispatch(bool sqlite)
    {
        const string input = "[\"one\"]";
        var registrations = CreateDemoMcpRegistrations();
        var discoveryBroker = new McpToolBroker(registrations, new DemoToolPolicy());
        var slowBinding = Assert.Single(await discoveryBroker.ListBindingsAsync(), binding => binding.BindingId == "demo.slow");
        var definition = ParallelMcpDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var configuredTool = new ToolBindingRequest(slowBinding.BindingId, slowBinding.ContractHash)
        {
            DescriptionHash = slowBinding.DescriptionHash
        };
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "tool-research", "tool-review" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Tool = configuredTool,
                McpArgumentsJson = "{}"
            });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes },
            "array-iteration-production-mcp-reservation-candidate-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var baseline = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 2);
            Assert.NotNull(baseline.Output);
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized));
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolCompleted));
            Assert.Contains(baseline.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Equal(2, baseline.MaximumPendingReservations);
            Assert.Equal(0, baseline.PendingReservations);

            var authorizations = baseline.Events.Where(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized)
                .OrderBy(evt => evt.Sequence).ToArray();
            var secondAuthorization = authorizations[1];
            var bytesBeforeSecondAuthorization = baseline.Events
                .Where(evt => evt.Sequence < secondAuthorization.Sequence).Sum(EventBytes);
            var concurrentOutcomeByteLimit = bytesBeforeSecondAuthorization + EventBytes(secondAuthorization) +
                (2 * 32_000L) + 64_000 - 4_096;
            var byteLimited = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 2,
                maximumBytes: concurrentOutcomeByteLimit);
            Assert.Null(byteLimited.Output);
            Assert.True(byteLimited.ReservationRejected);
            Assert.Single(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            Assert.Single(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.ToolFailed);
            Assert.Single(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.ToolUncertain);
            Assert.DoesNotContain(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.DoesNotContain(byteLimited.Events,
                evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Contains(byteLimited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(1, byteLimited.MaximumPendingReservations);
            Assert.Equal(0, byteLimited.PendingReservations);

            var limited = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 1);
            Assert.Null(limited.Output);
            Assert.True(limited.ReservationRejected);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolFailed);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolUncertain);
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(1, limited.MaximumPendingReservations);
            Assert.Equal(0, limited.PendingReservations);
        }

        async Task<CandidateProductionMcpReservationResult> ExecuteCandidateAsync(string candidate,
            int maximumConcurrentReservations, long maximumBytes = 4_000_000)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents: 512,
                maximumBytes, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 32_000,
                maximumConcurrentOutcomeReservations: maximumConcurrentReservations,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted,
                additionalReservationOutcomeKind: ExecutionEventKinds.ToolUncertain);
            var tools = CreateSynchronizedDemoMcpBroker(registrations, expectedConnections: 2);
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (Exception) when (recorder.ReservationRejected.IsCompleted) { }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder,
                    new EchoAgentExecutor(), tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null ? ExecutionEventKinds.Failed : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder,
                    new EchoAgentExecutor(), tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, output is null ? "failed" : "completed");
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
            return new(output, events, recorder.MaximumPendingOutcomeReservationsObserved,
                recorder.PendingOutcomeReservations, recorder.ReservationRejected.IsCompleted);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ReserveConcurrentDelegatedModelOutcomesAndBytesBeforeDispatch(bool sqlite)
    {
        const string input = "[\"proposal\"]";
        const string providerId = "proof.iterator-delegated-model";
        const string parentInstructions = "Review the proposal with the team.";
        const string researcherInstructions = "Find supporting evidence.";
        const string reviewerInstructions = "Review the evidence.";
        const string task = "Review the proposal.";
        var profiles = new[]
        {
            AgentProfile.Create("researcher", "1", "Researcher", "Find relevant facts.", providerId,
                researcherInstructions, []),
            AgentProfile.Create("reviewer", "1", "Reviewer", "Check the answer.", providerId,
                reviewerInstructions, [])
        };
        var configuredAgent = new AgentConfiguration(providerId, null, parentInstructions, [])
        {
            Delegates = [.. profiles.Select(profile => new AgentProfileReference(profile.Id, profile.Version,
                profile.ContentHash!))]
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: configuredAgent))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "iterator-delegated-model-byte-reservation-proof", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
                .Add("agent-output", new(new("agent", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", Edge("input", "then", "agent", "execute"))
                .Add("agent-output", Edge("agent", "out", "output", "execute")),
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer }),
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version,
                profile => profile, StringComparer.Ordinal)
        };
        var revision = WorkflowRevision.Create(definition, "iterator-delegated-model-byte-reservation-proof");
        var provider = new AgentProviderDescriptor(providerId, "Proof delegated model", "proof-chat-v1", true);

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var baseline = await ExecuteCandidateAsync(candidate, maximumBytes: 4_000_000);
            Assert.NotNull(baseline.Output);
            Assert.Equal(4, baseline.Model.Prompts.Length);
            Assert.Equal(2, baseline.Model.MaximumConcurrentDelegateCalls);
            Assert.Single(baseline.Events, evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted);
            Assert.Single(baseline.Events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted);
            Assert.Equal(4, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested));
            Assert.Equal(4, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded));

            var parentInvocation = Assert.Single(baseline.Events, evt =>
                evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent").InvocationId;
            var delegatedRequests = baseline.Events.Where(evt =>
                    evt.Kind == ExecutionEventKinds.ModelRequested && evt.InvocationId != parentInvocation)
                .OrderBy(evt => evt.Sequence).ToArray();
            Assert.Equal(2, delegatedRequests.Length);
            var secondRequest = delegatedRequests[1];
            var prefixBytes = baseline.Events.Where(evt => evt.Sequence < secondRequest.Sequence).Sum(EventBytes);
            const long maximumOutcomeBytes = 64_000;
            const long reservedFailureBytes = 64_000;
            const long byteAdmissionMargin = 4_096;
            var maximumBytes = checked(prefixBytes + EventBytes(secondRequest) + (2 * maximumOutcomeBytes) +
                reservedFailureBytes - byteAdmissionMargin);

            var limited = await ExecuteCandidateAsync(candidate, maximumBytes);
            Assert.Null(limited.Output);
            Assert.True(limited.ReservationRejected,
                $"{candidate} dispatched a delegated model call after its second concurrent outcome reservation " +
                $"was rejected at {maximumBytes} bytes.");
            Assert.Equal(1, limited.Model.MaximumConcurrentDelegateCalls);
            Assert.Equal(2, limited.Model.Prompts.Length); // parent plus only the admitted delegate
            Assert.Equal(2, limited.Events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested));
            var limitedParentInvocation = Assert.Single(limited.Events, evt =>
                evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent").InvocationId;
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.ModelResponded &&
                evt.InvocationId != limitedParentInvocation);
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.AgentFailed);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.AgentTeamFailed);
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "agent");
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(1, limited.PendingOutcomeReservations);
            Assert.Equal(limited.Events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested) -
                limited.Events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded),
                limited.PendingOutcomeReservations);
            Assert.True(limited.PersistedEventBytes <= maximumBytes);
            Assert.Equal(limited.Events.Sum(EventBytes), limited.PersistedEventBytes);
        }

        async Task<DelegatedCandidateResult> ExecuteCandidateAsync(string candidate, long maximumBytes)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents: 512,
                maximumBytes, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 64_000, maximumConcurrentOutcomeReservations: 2,
                reservationRequestKind: ExecutionEventKinds.ModelRequested,
                reservationOutcomeKind: ExecutionEventKinds.ModelResponded);
            var model = new ParallelDelegateEvidenceModel(parentInstructions, researcherInstructions,
                reviewerInstructions, task, delegateOutputCharacters: 128);
            var agents = new AgentNodeExecutor(
                [new AgentProviderRegistration(provider, () => new ParallelDelegateEvidenceChatClient(model))],
                new EchoToolBroker());
            var tools = new EchoToolBroker();
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (EvidenceBudgetExhaustedException) when (recorder.ReservationRejected.IsCompleted) { }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null
                    ? ExecutionEventKinds.Failed
                    : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, output is null ? "failed" : "completed");
            return new(output, events, model, recorder.PendingOutcomeReservations,
                recorder.PersistedEventBytes, recorder.ReservationRejected.IsCompleted);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ReserveDelegatedProductionMcpOutcomesAndBytesBeforeDispatch(bool sqlite)
    {
        const string input = "[\"proposal\"]";
        const string providerId = "proof.iterator-delegated-mcp";
        const string parentInstructions = "Ask the team to complete the proposal.";
        const string researcherInstructions = "Research the proposal.";
        const string reviewerInstructions = "Review the proposal.";
        var registrations = CreateDemoMcpRegistrations();
        var discoveryBroker = new McpToolBroker(registrations, new DemoToolPolicy());
        var slowBinding = Assert.Single(await discoveryBroker.ListBindingsAsync(),
            binding => binding.BindingId == "demo.slow");
        var tool = new ToolBindingRequest(slowBinding.BindingId, slowBinding.ContractHash)
        {
            DescriptionHash = slowBinding.DescriptionHash
        };
        var profiles = new[]
        {
            AgentProfile.Create("researcher", "1", "Researcher", "Researches the proposal.", providerId,
                researcherInstructions, [tool]),
            AgentProfile.Create("reviewer", "1", "Reviewer", "Reviews the proposal.", providerId,
                reviewerInstructions, [tool])
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
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "iterator-delegated-production-mcp-reservation-proof", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
                .Add("agent-output", new(new("agent", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", Edge("input", "then", "agent", "execute"))
                .Add("agent-output", Edge("agent", "out", "output", "execute")),
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer }),
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version,
                profile => profile, StringComparer.Ordinal)
        };
        var revision = WorkflowRevision.Create(definition, "iterator-delegated-production-mcp-reservation-proof");
        var provider = new AgentProviderDescriptor(providerId, "Proof delegated MCP", "proof-chat-v1", true);

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var baseline = await ExecuteCandidateAsync(candidate, maximumBytes: 4_000_000,
                maximumConcurrentReservations: 2);
            Assert.NotNull(baseline.Output);
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized));
            Assert.Equal(2, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolCompleted));
            Assert.Single(baseline.Events, evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted);
            Assert.Single(baseline.Events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted);
            Assert.Equal(2, baseline.MaximumPendingOutcomeReservations);
            Assert.Equal(0, baseline.PendingOutcomeReservations);

            var countLimited = await ExecuteCandidateAsync(candidate, maximumBytes: 4_000_000,
                maximumConcurrentReservations: 1);
            AssertDelegatedToolRecovery(countLimited, maximumBytes: 4_000_000);

            var authorizations = baseline.Events.Where(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized)
                .OrderBy(evt => evt.Sequence).ToArray();
            var secondAuthorization = authorizations[1];
            var prefixBytes = baseline.Events.Where(evt => evt.Sequence < secondAuthorization.Sequence)
                .Sum(EventBytes);
            const long maximumOutcomeBytes = 32_000;
            const long reservedFailureBytes = 64_000;
            const long byteAdmissionMargin = 4_096;
            var maximumBytes = checked(prefixBytes + EventBytes(secondAuthorization) +
                (2 * maximumOutcomeBytes) + reservedFailureBytes - byteAdmissionMargin);

            var byteLimited = await ExecuteCandidateAsync(candidate, maximumBytes,
                maximumConcurrentReservations: 2);
            AssertDelegatedToolRecovery(byteLimited, maximumBytes);
        }

        void AssertDelegatedToolRecovery(DelegatedMcpCandidateResult limited, long maximumBytes)
        {
            Assert.True(limited.ReservationRejected,
                $"Reservation was not rejected at {limited.PersistedEventBytes}/{maximumBytes} bytes; " +
                $"maximum concurrent reservations: {limited.MaximumPendingOutcomeReservations}.");
            Assert.Equal("Work completed.", limited.Output);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            var failedTool = Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolFailed);
            using var failureEnvelope = JsonDocument.Parse(failedTool.Data!);
            using var failureData = JsonDocument.Parse(failureEnvelope.RootElement.GetProperty("eventData").GetString()!);
            Assert.Equal("tool.host_failure", failureData.RootElement.GetProperty("failureCode").GetString());
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.ToolUncertain);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted);
            Assert.DoesNotContain(limited.Events, evt => evt.Kind == ExecutionEventKinds.AgentTeamFailed);
            Assert.Single(limited.Events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted);
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted);
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent");
            Assert.Contains(limited.Events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
            Assert.Equal(0, limited.PendingOutcomeReservations);
            Assert.True(limited.PersistedEventBytes <= maximumBytes);
            Assert.Equal(limited.Events.Sum(EventBytes), limited.PersistedEventBytes);
        }

        async Task<DelegatedMcpCandidateResult> ExecuteCandidateAsync(string candidate, long maximumBytes,
            int maximumConcurrentReservations)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents: 512,
                maximumBytes, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 32_000, maximumConcurrentOutcomeReservations: maximumConcurrentReservations,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted,
                additionalReservationOutcomeKind: ExecutionEventKinds.ToolUncertain);
            var model = new DelegatedMcpProofModel(parentInstructions, researcherInstructions, reviewerInstructions);
            var tools = CreateSynchronizedDemoMcpBroker(registrations, expectedConnections: 2);
            var agents = new AgentNodeExecutor(
                [new AgentProviderRegistration(provider, () => new DelegatedMcpProofChatClient(model))],
                tools);
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (EvidenceBudgetExhaustedException) when (recorder.ReservationRejected.IsCompleted) { }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null
                    ? ExecutionEventKinds.Failed
                    : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, output is null ? "failed" : "completed");
            return new(output, events, recorder.MaximumPendingOutcomeReservationsObserved,
                recorder.PendingOutcomeReservations, recorder.PersistedEventBytes,
                recorder.ReservationRejected.IsCompleted);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_SettleProductionMcpTimeoutReservations(bool sqlite)
    {
        const string input = "[\"one\"]";
        var registrations = CreateDemoMcpRegistrations();
        var discoveryBroker = new McpToolBroker(registrations, new DemoToolPolicy());
        var slowBinding = Assert.Single(await discoveryBroker.ListBindingsAsync(), binding => binding.BindingId == "demo.slow");
        var definition = ParallelMcpDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var configuredTool = new ToolBindingRequest(slowBinding.BindingId, slowBinding.ContractHash)
        {
            DescriptionHash = slowBinding.DescriptionHash
        };
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "tool-research", "tool-review" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Tool = configuredTool,
                McpArgumentsJson = "{}"
            });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes },
            "array-iteration-production-mcp-timeout-candidate-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var timedOut = await ExecuteCandidateAsync(candidate);
            Assert.Null(timedOut.Output);
            Assert.Equal(2, timedOut.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized));
            Assert.InRange(timedOut.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolUncertain), 1, 2);
            Assert.DoesNotContain(timedOut.Events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.DoesNotContain(timedOut.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Contains(timedOut.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(0, timedOut.PendingReservations);
            Assert.Equal(2, timedOut.MaximumPendingReservations);
        }

        async Task<CandidateProductionMcpReservationResult> ExecuteCandidateAsync(string candidate)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents: 512,
                maximumBytes: 4_000_000, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 32_000, maximumConcurrentOutcomeReservations: 2,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted,
                additionalReservationOutcomeKind: ExecutionEventKinds.ToolUncertain);
            var tools = CreateSynchronizedDemoMcpBroker(registrations, expectedConnections: 2,
                timeout: TimeSpan.FromSeconds(4));
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (Exception) { }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder,
                    new EchoAgentExecutor(), tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null ? ExecutionEventKinds.Failed : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder,
                    new EchoAgentExecutor(), tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, output is null ? "failed" : "completed");
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
            return new(output, events, recorder.MaximumPendingOutcomeReservationsObserved,
                recorder.PendingOutcomeReservations, recorder.ReservationRejected.IsCompleted);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_SettleCustomBrokerUnknownOutcomes(bool sqlite)
    {
        const string input = "[\"one\"]";
        const string bindingId = "proof.custom-timeout";
        var definition = ParallelMcpDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var configuredTool = new ToolBindingRequest(bindingId, new string('a', 64))
        {
            DescriptionHash = new string('b', 64)
        };
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "tool-research", "tool-review" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Tool = configuredTool,
                McpArgumentsJson = "{}"
            });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes },
            "array-iteration-custom-broker-outcome-candidate-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            var outcome = await ExecuteCandidateAsync(candidate);
            Assert.Null(outcome.Output);
            Assert.Equal(2, outcome.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized));
            Assert.InRange(outcome.Events.Count(evt => evt.Kind == ExecutionEventKinds.ToolUncertain), 1, 2);
            Assert.DoesNotContain(outcome.Events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.DoesNotContain(outcome.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Contains(outcome.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(0, outcome.PendingReservations);
            Assert.Equal(2, outcome.MaximumPendingReservations);
        }

        async Task<CandidateProductionMcpReservationResult> ExecuteCandidateAsync(string candidate)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents: 512,
                maximumBytes: 4_000_000, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 32_000, maximumConcurrentOutcomeReservations: 2,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted,
                additionalReservationOutcomeKind: ExecutionEventKinds.ToolUncertain);
            var tools = new ConcurrentUnknownOutcomeToolBroker(bindingId, expectedAuthorizations: 2);
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (ToolInvocationException) { }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder,
                    new EchoAgentExecutor(), tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null ? ExecutionEventKinds.Failed : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder,
                    new EchoAgentExecutor(), tools, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, output is null ? "failed" : "completed");
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
            return new(output, events, recorder.MaximumPendingOutcomeReservationsObserved,
                recorder.PendingOutcomeReservations, recorder.ReservationRejected.IsCompleted);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_CustomBrokerExceptionKeepsOutcomeUnknownAndFailureSafe(bool sqlite)
    {
        const string input = "[\"first\",\"second\"]";
        const string bindingId = "proof.custom-throwing-tool";
        const string secretCanary = "custom-broker-secret-canary-should-not-persist";
        var definition = BodyDefinition(includeVariableStore: true, includeIndexParameter: true);
        var nodes = definition.Nodes.SetItem("tool", definition.Nodes["tool"] with
        {
            Tool = new ToolBindingRequest(bindingId, new string('c', 64)),
            McpArgumentsJson = "{}"
        });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes },
            "array-iteration-custom-broker-exception-candidate-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 32_000,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted,
                additionalReservationOutcomeKind: ExecutionEventKinds.ToolUncertain);
            var broker = new ThrowingCustomOutcomeToolBroker(bindingId, secretCanary);
            var agents = new EchoAgentExecutor();
            string? output = null;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(broker, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync);
                    output = result.Output;
                }
                catch (Exception)
                {
                    // A custom broker failed after authorization without an outcome record.
                }
            }
            else if (candidate == "fresh-subworkflow")
            {
                await RecordRootAsync(ExecutionEventKinds.Started);
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents, broker);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var failed = false;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                {
                    if (evt is WorkflowOutputEvent { Data: string value }) output = value;
                    if (evt is WorkflowErrorEvent) failed = true;
                }
                await RecordRootAsync(failed || output is null
                    ? ExecutionEventKinds.Failed
                    : ExecutionEventKinds.Completed);
            }
            else
            {
                var result = await ExecuteHybridBudgetLoopAsync(store, revision, recorder, agents, broker, input);
                output = result.Output;
            }

            var events = recorder.Snapshot();
            var requested = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolRequested);
            var authorized = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            Assert.Equal(1, broker.DispatchAttempts);
            Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.ToolCompleted or
                ExecutionEventKinds.ToolFailed or ExecutionEventKinds.ToolUncertain);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "tool" &&
                evt.Data == "Node execution failed.");
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Null(output);
            Assert.DoesNotContain(events, evt => evt.Data?.Contains(secretCanary, StringComparison.Ordinal) == true);
            Assert.Equal(1, recorder.PendingOutcomeReservations);
            Assert.True(recorder.PendingOutcomeBytes > 0);
            AssertPersistedSequence(store, events, "failed");

            using var requestedPayload = JsonDocument.Parse(requested.Data!);
            using var authorizedPayload = JsonDocument.Parse(authorized.Data!);
            Assert.Equal(requestedPayload.RootElement.GetProperty("toolCallId").GetString(),
                authorizedPayload.RootElement.GetProperty("toolCallId").GetString());

            var reopened = store.ReopenStore().Get(store.RunId)!;
            Assert.Equal("failed", reopened.Status);
            Assert.Equal(events, reopened.Events);

            ValueTask RecordRootAsync(string kind) => recorder.RecordResequencedAsync(new ExecutionEvent(
                store.RunId, store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow, kind));
        }
    }

    private sealed record CandidateReservationResult(string? Output, ExecutionEvent[] Events, int ProviderCalls,
        int MaximumConcurrentModelCalls, int MaximumPendingReservations, int PendingReservations,
        long PersistedEventBytes, bool ReservationRejected);

    private sealed record CandidateMcpReservationResult(string? Output, ExecutionEvent[] Events, int DispatchedCalls,
        int MaximumConcurrentCalls, int MaximumPendingReservations, bool ReservationRejected);

    private sealed record CandidateProductionMcpReservationResult(string? Output, ExecutionEvent[] Events,
        int MaximumPendingReservations, int PendingReservations, bool ReservationRejected);

    private sealed record DelegatedCandidateResult(string? Output, ExecutionEvent[] Events,
        ParallelDelegateEvidenceModel Model, int PendingOutcomeReservations, long PersistedEventBytes,
        bool ReservationRejected);

    private sealed record DelegatedMcpCandidateResult(string? Output, ExecutionEvent[] Events,
        int MaximumPendingOutcomeReservations, int PendingOutcomeReservations, long PersistedEventBytes,
        bool ReservationRejected);

    private sealed record CandidateBudgetResult(string? Output, ExecutionEvent[] Events, int ModelCalls,
        string[] ToolQueries, int PendingOutcomeReservations, long PersistedEventBytes,
        bool ReservationRejected);

    private sealed record CandidateWorkLimitResult(string? Output, ExecutionEvent[] Events, int ModelCalls,
        string[] ToolQueries, bool WorkLimitRejected);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridFrameCoordinator_ExecutesUnifiedSequenceBodyPerActivation(bool sqlite)
    {
        var revision = WorkflowRevision.Create(SequenceBodyDefinition(),
            "array-iteration-hybrid-sequence-body-proof");
        const string input = "[\"hello\",\"world\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var tools = new EchoToolBroker();
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 3, "outer", ImmutableSortedDictionary<string, string>.Empty);
        var items = JsonSerializer.Deserialize<string[]>(input)!;
        var outputs = new List<string>(items.Length);

        for (var index = 0; index < items.Length; index++)
        {
            var frame = parentFrame.Enter("array-loop", index, items[index],
                ImmutableSortedDictionary<string, string>.Empty);
            recorder.Append(ExecutionEventKinds.LoopIterationStarted, "array-loop", frame.Id, frame.ScopeId);
            ValueTask Record(ExecutionEvent evt)
            {
                if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                    ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                    return ValueTask.CompletedTask;
                recorder.Append(evt with { ScopeId = frame.ScopeId });
                return ValueTask.CompletedTask;
            }

            var result = await new WorkflowRunner(tools).ExecuteWithOutcomeAsync(runStore.RunId,
                revision, items[index], null, Record);
            Assert.False(result.Failed);
            Assert.NotNull(result.Output);
            outputs.Add(result.Output!);
            recorder.Append(ExecutionEventKinds.LoopIterationCompleted, "array-loop", frame.Id, frame.ScopeId);
        }
        recorder.CompleteRoot();

        Assert.Equal(["echo:sequence", "echo:sequence"], outputs);
        Assert.Equal(["sequence", "sequence", "sequence", "sequence"], tools.Queries);
        var events = recorder.Snapshot();
        AssertPersistedSequence(runStore, events, "completed");
        for (var index = 0; index < items.Length; index++)
        {
            var scopeId = $"outer-loop/3/array-loop/{index}";
            var scoped = events.Where(evt => evt.ScopeId == scopeId).ToArray();
            Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched));
            Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
            Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId is "inventory-tool" or "architecture-tool"));
            Assert.Contains(scoped, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.NodeId == "array-loop");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HybridFrameCoordinator_IsolatesCollaborationPerActivation(bool phased, bool sqlite)
    {
        var sourceDefinition = phased
            ? WorkflowRunnerCollaborationTests.CreatePhasedGraph(branchCount: 2, timeoutSeconds: 30)
            : WorkflowRunnerCollaborationTests.CreateGraph(branchCount: 2, timeoutSeconds: 30);
        var definition = sourceDefinition
            with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var revision = WorkflowRevision.Create(definition, "array-iteration-hybrid-collaboration-proof");
        const string input = "[\"item-a\",\"item-b\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var eventGate = new object();
        var events = new List<ExecutionEvent>();
        long sequence = 0;
        var runId = runStore.RunId;
        var revisionId = runStore.Store.Get(runId)!.Header.RevisionId;
        void RecordPersistable(ExecutionEvent evt)
        {
            lock (eventGate) events.Add(evt with { Sequence = ++sequence });
        }
        RecordPersistable(new ExecutionEvent(runId, revisionId, 0, DateTimeOffset.UtcNow,
            ExecutionEventKinds.Started));
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 4, "outer", ImmutableSortedDictionary<string, string>.Empty);
        var agent = new ActivationScopedCollaborationAgentExecutor(branchCount: 2);
        var items = JsonSerializer.Deserialize<string[]>(input)!;

        for (var index = 0; index < items.Length; index++)
        {
            var frame = parentFrame.Enter("array-loop", index, items[index],
                ImmutableSortedDictionary<string, string>.Empty);
            RecordPersistable(new ExecutionEvent(runId, revisionId, 0, DateTimeOffset.UtcNow,
                ExecutionEventKinds.LoopIterationStarted, "array-loop", frame.Id) { ScopeId = frame.ScopeId });
            ValueTask Record(ExecutionEvent evt)
            {
                if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                    ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                    return ValueTask.CompletedTask;
                RecordPersistable(evt with { ScopeId = frame.ScopeId });
                return ValueTask.CompletedTask;
            }

            var result = await new WorkflowRunner(agents: agent).ExecuteWithOutcomeAsync(runStore.RunId,
                revision, items[index], null, Record);
            Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
            Assert.NotNull(result.Output);
            using var joined = JsonDocument.Parse(result.Output!);
            Assert.Equal(2, joined.RootElement.EnumerateObject().Count());
            Assert.All(joined.RootElement.EnumerateObject(), branch => Assert.Equal(items[index], branch.Value.GetString()));
            RecordPersistable(new ExecutionEvent(runId, revisionId, 0, DateTimeOffset.UtcNow,
                ExecutionEventKinds.LoopIterationCompleted, "array-loop", frame.Id) { ScopeId = frame.ScopeId });
        }
        RecordPersistable(new ExecutionEvent(runId, revisionId, 0, DateTimeOffset.UtcNow,
            ExecutionEventKinds.Completed));
        runStore.Store.Append(runId, 0, events);

        AssertPersistedSequence(runStore, events, "completed");
        for (var index = 0; index < items.Length; index++)
        {
            var scopeId = $"outer-loop/4/array-loop/{index}";
            var scoped = events.Where(evt => evt.ScopeId == scopeId).ToArray();
            Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationRegionStarted);
            Assert.Equal(phased ? 2 : 1,
                scoped.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased));
            Assert.Equal(phased ? 4 : 2,
                scoped.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationArrival));
            Assert.Equal(phased ? 4 : 2,
                scoped.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessageSent));
            Assert.Equal(phased ? 4 : 2,
                scoped.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessagesRead));
            Assert.DoesNotContain(scoped, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        }
    }

    private static WorkflowDefinition SequenceBodyDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new([new("inventory", "Inventory"), new("architecture", "Architecture")])
            })
            .Add("inventory-tool", new(NodeTypes.McpTool, "1.0.0",
                new ToolBindingRequest("proof.inventory", new string('a', 64)))
            { McpArgumentsJson = "{\"query\":\"sequence\"}" })
            .Add("architecture-tool", new(NodeTypes.McpTool, "1.0.0",
                new ToolBindingRequest("proof.architecture", new string('b', 64)))
            { McpArgumentsJson = "{\"query\":\"sequence\"}" })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-sequence", Edge("input", "then", "sequence", "execute"))
            .Add("sequence-inventory", Edge("sequence", "then.inventory", "inventory-tool", "execute"))
            .Add("inventory-output", Edge("inventory-tool", "then", "output", "execute"))
            .Add("sequence-architecture", Edge("sequence", "then.architecture", "architecture-tool", "execute"))
            .Add("architecture-output", Edge("architecture-tool", "then", "output", "execute"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-sequence-body", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = execution
        };
    }

    private static async Task<HybridBudgetLoopResult> ExecuteHybridBudgetLoopAsync(ProofRunStore runStore,
        WorkflowRevision revision, ReservedEvidenceProofRecorder recorder, IAgentNodeExecutor agents,
        INodeToolBroker tools, string input, bool recordCompletionAppendFailureAsRunFailure = false)
    {
        var runId = runStore.RunId;
        var revisionId = runStore.Store.Get(runId)!.Header.RevisionId;
        var parentFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
            "outer-loop", 0, "outer", ImmutableSortedDictionary<string, string>.Empty);
        await recorder.RecordResequencedAsync(new ExecutionEvent(runId, revisionId, 0,
            DateTimeOffset.UtcNow, ExecutionEventKinds.Started));
        var items = JsonSerializer.Deserialize<string[]>(input)
            ?? throw new InvalidOperationException("Array input was not a string array.");
        var results = new List<string>(items.Length);

        for (var index = 0; index < items.Length; index++)
        {
            var parameters = ImmutableSortedDictionary<string, string>.Empty
                .Add("index", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var frame = parentFrame.Enter("array-loop", index, items[index], parameters);
            ValueTask Record(ExecutionEvent evt)
            {
                if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                    ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                    return ValueTask.CompletedTask;
                return recorder.RecordResequencedAsync(evt with { ScopeId = frame.ScopeId });
            }

            try
            {
                await recorder.RecordResequencedAsync(new ExecutionEvent(runId, revisionId, 0,
                    DateTimeOffset.UtcNow, ExecutionEventKinds.LoopIterationStarted, "array-loop", frame.Id)
                { ScopeId = frame.ScopeId });
                var result = await new WorkflowRunner(tools, agents).ExecuteWithOutcomeAsync(runId,
                    revision, items[index], parameters, Record);
                if (result.Failed || result.Output is null)
                {
                    var error = recorder.Snapshot().LastOrDefault(evt => evt.Kind == ExecutionEventKinds.AgentFailed)?.Data
                        ?? "Nested workflow body failed.";
                    await RecordRootTerminalAsync(ExecutionEventKinds.Failed);
                    return new(null, error);
                }

                results.Add(result.Output);
                await recorder.RecordResequencedAsync(new ExecutionEvent(runId, revisionId, 0,
                    DateTimeOffset.UtcNow, ExecutionEventKinds.LoopIterationCompleted, "array-loop", frame.Id)
                { ScopeId = frame.ScopeId });
            }
            catch (Exception exception)
            {
                await RecordRootTerminalAsync(ExecutionEventKinds.Failed);
                return new(null, exception.ToString());
            }
        }

        try
        {
            await RecordRootTerminalAsync(ExecutionEventKinds.Completed);
        }
        catch (IOException) when (recordCompletionAppendFailureAsRunFailure)
        {
            await RecordRootTerminalAsync(ExecutionEventKinds.Failed);
            throw;
        }
        return new(string.Join(';', results), null);

        ValueTask RecordRootTerminalAsync(string kind) => recorder.RecordResequencedAsync(
            new ExecutionEvent(runId, revisionId, 0, DateTimeOffset.UtcNow, kind));
    }

    private sealed record HybridBudgetLoopResult(string? Output, string? Error);

    private sealed class ActivationScopedCollaborationAgentExecutor(int branchCount) : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            var branchId = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions;
            var phase = invocation.Revision.Definition.ParallelCollaborationRegions!.Values.Single()
                .SecondRendezvousNodeId is null ? null : invocation.CollaborationPhase;
            if (invocation.CollaborationRole == ParallelCollaborationRoles.Publish)
            {
                Assert.NotNull(invocation.CollaborationSend);
                var content = phase is null ? $"{input}|{branchId}" : $"{input}|{phase}|{branchId}";
                await invocation.CollaborationSend!("work", content, cancellationToken);
                return input;
            }

            Assert.Equal(ParallelCollaborationRoles.Read, invocation.CollaborationRole);
            Assert.NotNull(invocation.CollaborationRead);
            var snapshot = await invocation.CollaborationRead!("work", cancellationToken);
            using var document = JsonDocument.Parse(snapshot);
            var messages = document.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("content").GetString()!).ToArray();
            Assert.Equal(branchCount, messages.Length);
            var prefix = phase is null ? $"{input}|" : $"{input}|{phase}|";
            Assert.All(messages, message => Assert.StartsWith(prefix, message, StringComparison.Ordinal));
            return input;
        }
    }

    private static async Task<string> RunHybridLoopProofAsync(Guid runId, WorkflowRevision revision,
        ProofRecorder recorder, IAgentNodeExecutor agents, INodeToolBroker tools, string loopNodeId,
        IReadOnlyList<string> items, WorkflowRunner.StructuredActivationFrame? parentFrame,
        string indexParameterId, ConcurrentQueue<HybridLoopProofBreakDispatch> breakDispatches,
        Func<WorkflowRunner.StructuredActivationFrame, Task<string>> executeBody)
    {
        var results = new List<string>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var parameters = parentFrame?.Parameters.ToBuilder() ??
                ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            parameters[indexParameterId] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var frameParameters = parameters.ToImmutable();
            var frame = parentFrame?.Enter(loopNodeId, index, items[index], frameParameters) ??
                new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null, loopNodeId, index,
                    items[index], frameParameters);
            recorder.Append(ExecutionEventKinds.LoopIterationStarted, loopNodeId, frame.Id, frame.ScopeId);

            var result = await executeBody(frame);
            if (result == HybridLoopProofBreak.Signal)
            {
                var target = frame.NearestLoop();
                breakDispatches.Enqueue(new(frame.Id, target.Id, frame.ScopeId));
                if (target.Id != frame.Id)
                    throw new InvalidOperationException("The hybrid coordinator did not own the nearest loop target.");
                break;
            }

            recorder.Append(ExecutionEventKinds.LoopIterationCompleted, loopNodeId, frame.Id, frame.ScopeId);
            results.Add(result);
        }

        return string.Join(';', results);
    }

    private static class HybridLoopProofBreak
    {
        public const string Signal = "__hybrid_loop_break_proof__";
    }

    private sealed record HybridLoopProofBreakDispatch(Guid SourceFrameId, Guid TargetFrameId, string ScopeId);

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

    private static WorkflowDefinition NestedBodyDefinition()
    {
        var definition = BodyDefinition(includeVariableStore: true, includeIndexParameter: true);
        var nodes = definition.Nodes
            .Add("outer-index", new(NodeTypes.WorkflowValue, "1.0.0")
                { ParameterId = "outerIndex", OutputDataType = WorkflowDataTypes.Integer })
            .Add("outer-index-text", new(NodeTypes.IntegerToString, "1.0.0"))
            .Add("outer-separator", new(NodeTypes.StringConstant, "1.0.0")
                { StringConstant = new("/") })
            .Add("outer-suffix", new(NodeTypes.TextConcat, "1.0.0"))
            .Add("nested-coordinate", new(NodeTypes.TextConcat, "1.0.0"));
        var connections = definition.Connections.Remove("item-index-uppercase")
            .Add("outer-index-text", new(new("outer-index", "out"), new("outer-index-text", "in"), "data"))
            .Add("outer-separator", new(new("outer-separator", "out"), new("outer-suffix", "left"), "data"))
            .Add("outer-suffix-index", new(new("outer-index-text", "out"), new("outer-suffix", "right"), "data"))
            .Add("coordinate-item", new(new("item-index", "out"), new("nested-coordinate", "left"), "data"))
            .Add("coordinate-outer", new(new("outer-suffix", "out"), new("nested-coordinate", "right"), "data"))
            .Add("coordinate-uppercase", new(new("nested-coordinate", "out"), new("uppercase", "in"), "data"));
        return definition with
        {
            Nodes = nodes,
            Connections = connections,
            Parameters = definition.Parameters!.Add("outerIndex",
                new("Outer array index", "0") { Type = WorkflowDataTypes.Integer })
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

    private static string? ReadVariablePreviousValue(IEnumerable<ExecutionEvent> events, string nodeId)
    {
        var evt = Assert.Single(events, item => item.Kind == ExecutionEventKinds.VariableWritten && item.NodeId == nodeId);
        using var payload = JsonDocument.Parse(evt.Data!);
        return payload.RootElement.GetProperty("previousValue").GetString();
    }

    private static string? ReadEvaluatedValue(IEnumerable<ExecutionEvent> events, string nodeId)
    {
        var evt = Assert.Single(events, item => item.Kind == ExecutionEventKinds.DataEvaluated && item.NodeId == nodeId);
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
        private readonly bool sqlite;

        public ProofRunStore(WorkflowRevision revision, string input, bool sqlite,
            string[]? rejectedAppendEventKinds = null,
            Func<ExecutionEvent, bool>? rejectedAppendPredicate = null)
        {
            this.sqlite = sqlite;
            Directory.CreateDirectory(directory);
            IWorkflowRunStore store = OpenStore();
            Store = rejectedAppendEventKinds is { Length: > 0 } || rejectedAppendPredicate is not null
                ? new RejectingAppendRunStore(store,
                    rejectedAppendEventKinds?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal),
                    rejectedAppendPredicate)
                : store;
            RunId = Store.Create(revision, input).Header.RunId;
        }

        public IWorkflowRunStore Store { get; }
        public Guid RunId { get; }

        public IWorkflowRunStore ReopenStore() => OpenStore();

        private IWorkflowRunStore OpenStore() => sqlite
            ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
            : new FileRunJournal(directory);

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

    private sealed class PerItemWorkflowExecutor : Executor<string, string>
    {
        private readonly Guid runId;
        private readonly WorkflowRevision revision;
        private readonly Func<ExecutionEvent, ValueTask> recordEvent;
        private readonly IAgentNodeExecutor agents;
        private readonly INodeToolBroker tools;
        private readonly IWorkflowExecutionGate? gate;
        private readonly Func<string, CancellationToken, ValueTask>? afterNodeStarted;
        private readonly Func<ExecutionEvent, ValueTask>? observeEvent;
        private readonly WorkflowRunner.StructuredActivationFrame? parentFrame;
        private readonly string loopNodeId;

        public PerItemWorkflowExecutor(Guid runId, WorkflowRevision revision, ProofRecorder recorder,
            IAgentNodeExecutor agents, INodeToolBroker tools, IWorkflowExecutionGate? gate = null,
            Func<string, CancellationToken, ValueTask>? afterNodeStarted = null,
            Func<ExecutionEvent, ValueTask>? observeEvent = null,
            WorkflowRunner.StructuredActivationFrame? parentFrame = null, string loopNodeId = "array-loop")
            : this(runId, revision, RecordTo(recorder), agents, tools, gate, afterNodeStarted, observeEvent,
                parentFrame, loopNodeId) { }

        public PerItemWorkflowExecutor(Guid runId, WorkflowRevision revision,
            ReservedEvidenceProofRecorder recorder, IAgentNodeExecutor agents, INodeToolBroker tools)
            : this(runId, revision, recorder.RecordResequencedAsync, agents, tools, null, null, null, null,
                "array-loop") { }

        private PerItemWorkflowExecutor(Guid runId, WorkflowRevision revision,
            Func<ExecutionEvent, ValueTask> recordEvent, IAgentNodeExecutor agents, INodeToolBroker tools,
            IWorkflowExecutionGate? gate, Func<string, CancellationToken, ValueTask>? afterNodeStarted,
            Func<ExecutionEvent, ValueTask>? observeEvent,
            WorkflowRunner.StructuredActivationFrame? parentFrame, string loopNodeId)
            : base("array-iterator-proof", declareCrossRunShareable: true)
        {
            this.runId = runId;
            this.revision = revision;
            this.recordEvent = recordEvent;
            this.agents = agents;
            this.tools = tools;
            this.gate = gate;
            this.afterNodeStarted = afterNodeStarted;
            this.observeEvent = observeEvent;
            this.parentFrame = parentFrame;
            this.loopNodeId = loopNodeId;
        }

        private static Func<ExecutionEvent, ValueTask> RecordTo(ProofRecorder recorder) => evt =>
        {
            recorder.Append(evt);
            return ValueTask.CompletedTask;
        };

        public override async ValueTask<string> HandleAsync(string input, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var items = JsonSerializer.Deserialize<string[]>(input)
                ?? throw new InvalidOperationException("Array input was not a string array.");
            var results = new List<string>(items.Length);
            for (var index = 0; index < items.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemParameters = parentFrame?.Parameters.ToBuilder() ??
                    ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                if (revision.Definition.Parameters?.ContainsKey("index") == true)
                    itemParameters["index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var frameParameters = itemParameters.ToImmutable();
                var frame = parentFrame?.Enter(loopNodeId, index, items[index], frameParameters) ??
                    new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null, loopNodeId, index,
                        items[index], frameParameters);
                if (parentFrame is not null && frame.NearestLoop().Id != frame.Id)
                    throw new InvalidOperationException("The child activation did not resolve its nearest loop frame.");
                var invocationId = frame.Id;
                var scopeId = frame.ScopeId;
                await recordEvent(new ExecutionEvent(runId, revision.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.LoopIterationStarted, loopNodeId, invocationId) { ScopeId = scopeId });
                async ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is not (ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated))
                    {
                        var scopedEvent = evt with { ScopeId = scopeId };
                        await recordEvent(scopedEvent);
                        if (observeEvent is not null) await observeEvent(scopedEvent);
                    }
                }
                var runner = new WorkflowRunner(tools, agents);
                var result = afterNodeStarted is not null
                    ? await runner.ExecuteAfterNodeStartGateProofAsync(runId, revision, items[index], frameParameters,
                        Record, afterNodeStarted, cancellationToken)
                    : gate is null
                        ? await runner.ExecuteWithOutcomeAsync(runId, revision, items[index], frameParameters, Record, cancellationToken)
                        : await runner.ExecuteControlledWithParametersAndOutcomeAsync(runId, revision,
                            items[index], frameParameters, Record, gate, cancellationToken);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException($"Body workflow failed at array index {index}.");
                await recordEvent(new ExecutionEvent(runId, revision.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.LoopIterationCompleted, loopNodeId, invocationId) { ScopeId = scopeId });
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
        private readonly long maximumOutcomeBytes;
        private readonly int maximumConcurrentOutcomeReservations;
        private readonly int maximumLoopActivations;
        private readonly int maximumModelDispatches;
        private readonly int maximumToolDispatches;
        private readonly string reservationRequestKind;
        private readonly string reservationOutcomeKind;
        private readonly string? additionalReservationOutcomeKind;
        private readonly object gate = new();
        private readonly List<ExecutionEvent> events = [];
        private readonly TaskCompletionSource reservationRejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource workLimitRejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int ordinaryEvents;
        private int failureEvents;
        private int pendingOutcomeReservations;
        private int maximumPendingOutcomeReservationsObserved;
        private int loopActivations;
        private int modelDispatches;
        private int toolDispatches;
        private long ordinaryBytes;
        private long pendingOutcomeBytes;
        private long failureBytes;
        private long persistedEventBytes;

        public ReservedEvidenceProofRecorder(ProofRunStore runStore, int maximumEvents, long maximumBytes,
            int reservedFailureEvents, long reservedFailureBytes, long maximumOutcomeBytes,
            int maximumConcurrentOutcomeReservations = int.MaxValue,
            string reservationRequestKind = ExecutionEventKinds.ModelRequested,
            string reservationOutcomeKind = ExecutionEventKinds.ModelResponded,
            string? additionalReservationOutcomeKind = null,
            int maximumLoopActivations = int.MaxValue,
            int maximumModelDispatches = int.MaxValue,
            int maximumToolDispatches = int.MaxValue)
        {
            this.runStore = runStore;
            ordinaryEventLimit = maximumEvents - reservedFailureEvents;
            this.maximumBytes = maximumBytes;
            this.reservedFailureEvents = reservedFailureEvents;
            this.reservedFailureBytes = reservedFailureBytes;
            this.maximumOutcomeBytes = maximumOutcomeBytes;
            this.maximumConcurrentOutcomeReservations = maximumConcurrentOutcomeReservations;
            this.maximumLoopActivations = maximumLoopActivations;
            this.maximumModelDispatches = maximumModelDispatches;
            this.maximumToolDispatches = maximumToolDispatches;
            this.reservationRequestKind = reservationRequestKind;
            this.reservationOutcomeKind = reservationOutcomeKind;
            this.additionalReservationOutcomeKind = additionalReservationOutcomeKind;
            if (ordinaryEventLimit < 0 || reservedFailureEvents < FailureEvidenceKinds.Count || maximumBytes < reservedFailureBytes ||
                reservedFailureBytes < 0 || maximumOutcomeBytes < 0 || maximumConcurrentOutcomeReservations < 1 ||
                maximumLoopActivations < 1 || maximumModelDispatches < 1 || maximumToolDispatches < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        public int PendingOutcomeReservations { get { lock (gate) return pendingOutcomeReservations; } }
        public long PendingOutcomeBytes { get { lock (gate) return pendingOutcomeBytes; } }
        public int MaximumPendingOutcomeReservationsObserved { get { lock (gate) return maximumPendingOutcomeReservationsObserved; } }
        public long PersistedEventBytes { get { lock (gate) return persistedEventBytes; } }
        public Task ReservationRejected => reservationRejected.Task;
        public Task WorkLimitRejected => workLimitRejected.Task;

        public ValueTask RecordAsync(ExecutionEvent evt)
        {
            lock (gate)
            {
                return RecordLocked(evt);
            }
        }

        public ValueTask RecordResequencedAsync(ExecutionEvent evt)
        {
            lock (gate)
            {
                return RecordLocked(evt with { Sequence = events.Count + 1L });
            }
        }

        private ValueTask RecordLocked(ExecutionEvent evt)
        {
            var bytes = EventBytes(evt);
            var isRequest = evt.Kind == reservationRequestKind;
            var isOutcome = evt.Kind == reservationOutcomeKind || evt.Kind == additionalReservationOutcomeKind;
            var isFailureEvidence = FailureEvidenceKinds.Contains(evt.Kind);

            if ((evt.Kind == ExecutionEventKinds.LoopIterationStarted && loopActivations >= maximumLoopActivations) ||
                (evt.Kind == ExecutionEventKinds.ModelRequested && modelDispatches >= maximumModelDispatches) ||
                (evt.Kind == ExecutionEventKinds.ToolAuthorized && toolDispatches >= maximumToolDispatches))
            {
                workLimitRejected.TrySetResult();
                throw new EvidenceBudgetExhaustedException();
            }

            if (isRequest && pendingOutcomeReservations >= maximumConcurrentOutcomeReservations)
            {
                reservationRejected.TrySetResult();
                throw new EvidenceBudgetExhaustedException();
            }
            if (isRequest && (ordinaryEvents + pendingOutcomeReservations + 2 > ordinaryEventLimit ||
                ordinaryBytes + pendingOutcomeBytes + bytes + maximumOutcomeBytes > maximumBytes - reservedFailureBytes))
            {
                reservationRejected.TrySetResult();
                throw new EvidenceBudgetExhaustedException();
            }
            if (isOutcome && (pendingOutcomeReservations == 0 || bytes > maximumOutcomeBytes ||
                ordinaryBytes + pendingOutcomeBytes - maximumOutcomeBytes + bytes > maximumBytes - reservedFailureBytes))
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

            if (evt.Kind == ExecutionEventKinds.LoopIterationStarted) loopActivations++;
            if (evt.Kind == ExecutionEventKinds.ModelRequested) modelDispatches++;
            if (evt.Kind == ExecutionEventKinds.ToolAuthorized) toolDispatches++;

            if (isRequest)
            {
                ordinaryEvents++;
                ordinaryBytes += bytes;
                pendingOutcomeReservations++;
                maximumPendingOutcomeReservationsObserved = Math.Max(maximumPendingOutcomeReservationsObserved,
                    pendingOutcomeReservations);
                pendingOutcomeBytes += maximumOutcomeBytes;
            }
            else if (isOutcome)
            {
                ordinaryEvents++;
                ordinaryBytes += bytes;
                pendingOutcomeReservations--;
                pendingOutcomeBytes -= maximumOutcomeBytes;
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

        public ExecutionEvent[] Snapshot() { lock (gate) return [.. events]; }
    }

    private sealed class EvidenceBudgetExhaustedException()
        : InvalidOperationException("The run evidence budget or aggregate work limit was exhausted.");

    private sealed class EchoAgentExecutor(string? failInput = null, string? failNodeId = null) : IAgentNodeExecutor
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
            if (input == failInput || invocation.NodeId == failNodeId)
                throw new InvalidOperationException("Deterministic proof failure.");
            var query = invocation.NodeId == "agent" ? input : $"{input}-{invocation.NodeId}";
            return Task.FromResult(JsonSerializer.Serialize(new { query }));
        }
    }

    private sealed class FirstSuccessThenFailAgentExecutor(TaskCompletionSource successfulBranchAdvanced)
        : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.branch-order", "Branch order proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (invocation.NodeId == "agent-right")
                return JsonSerializer.Serialize(new { query = $"{input}-agent-right" });
            if (invocation.NodeId == "agent-left")
            {
                await successfulBranchAdvanced.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Deterministic second-branch failure.");
            }

            throw new InvalidOperationException($"Unexpected proof Agent node '{invocation.NodeId}'.");
        }
    }

    private sealed class OneBranchCompletedThenBlockedAgentExecutor : IAgentNodeExecutor
    {
        public TaskCompletionSource RightCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRight { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.join-cancellation", "Join cancellation proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (invocation.NodeId == "agent-right")
                return JsonSerializer.Serialize(new { query = $"{input}-agent-right" });
            if (invocation.NodeId == "agent-left")
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            throw new InvalidOperationException($"Unexpected proof Agent node '{invocation.NodeId}'.");
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

    private sealed class TwoBranchBlockingAgentExecutor : IAgentNodeExecutor
    {
        private readonly ConcurrentDictionary<string, byte> started = new(StringComparer.Ordinal);

        public TaskCompletionSource BothBranchesStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.parallel-cancellation", "Parallel cancellation proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (invocation.NodeId is not ("agent-left" or "agent-right"))
                throw new InvalidOperationException($"Unexpected proof Agent node '{invocation.NodeId}'.");

            started.TryAdd(invocation.NodeId, 0);
            if (started.Count == 2) BothBranchesStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled branch unexpectedly resumed.");
        }
    }

    private sealed class CoordinatedBranchFailureAgentExecutor : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<string> enteredNodes = new();
        private readonly TaskCompletionSource rightEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string[] EnteredNodes => [.. enteredNodes];

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.coordinated", "Coordinated proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            enteredNodes.Enqueue(invocation.NodeId);
            if (invocation.NodeId == "agent-right")
            {
                rightEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (invocation.NodeId == "agent-left")
            {
                await rightEntered.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Deterministic coordinated branch failure.");
            }

            throw new InvalidOperationException($"Unexpected proof Agent node '{invocation.NodeId}'.");
        }
    }

    private sealed class CoordinatedPreAdmissionGate : IWorkflowExecutionGate
    {
        public TaskCompletionSource RightReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId != "agent-right") return;
            RightReached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CoordinatedGateFailureAgentExecutor(CoordinatedPreAdmissionGate gate) : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<string> enteredNodes = new();

        public string[] EnteredNodes => [.. enteredNodes];

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.prestart-gate", "Pre-start gate proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            enteredNodes.Enqueue(invocation.NodeId);
            if (invocation.NodeId == "agent-left")
            {
                await gate.RightReached.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Deterministic failure after sibling target delivery.");
            }

            throw new InvalidOperationException($"Unexpected proof Agent node '{invocation.NodeId}'.");
        }
    }

    private sealed class CoordinatedPostStartGate
    {
        public TaskCompletionSource RightNodeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WaitAsync(string nodeId, CancellationToken cancellationToken)
        {
            if (nodeId != "agent-right") return;
            RightNodeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CoordinatedPostStartFailureAgentExecutor(CoordinatedPostStartGate gate) : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<string> enteredNodes = new();

        public string[] EnteredNodes => [.. enteredNodes];

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.post-start", "Post-start gate proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            enteredNodes.Enqueue(invocation.NodeId);
            if (invocation.NodeId == "agent-left")
            {
                await gate.RightNodeStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Deterministic failure after sibling node start.");
            }

            throw new InvalidOperationException($"Unexpected proof Agent node '{invocation.NodeId}'.");
        }
    }

    private sealed class EchoToolBroker(Func<string, string>? respond = null,
        bool recordToolDispatchEvents = false) : INodeToolBroker
    {
        private readonly ConcurrentQueue<string> queries = new();
        public string[] Queries => [.. queries];
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public INodeToolScope OpenScope(NodeInvocationContext invocation) =>
            new EchoToolScope(queries, respond, invocation, recordToolDispatchEvents);
    }

    private static IReadOnlyList<McpToolRegistration> CreateDemoMcpRegistrations()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VisualAgentWorkflow.slnx")))
            root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate the local MCP fixture host.");

        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var studio = Path.Combine(root.FullName, "src", "VisualAgentWorkflow.Studio", "bin", configuration,
            "net10.0", "VisualAgentWorkflow.Studio.dll");
        var runtimeRoot = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory()
            .TrimEnd(Path.DirectorySeparatorChar))!.Parent!.Parent!.FullName;
        var dotnet = Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return DemoMcpServer.Registrations(dotnet, [studio, "--mcp-demo-server"])
            .Where(registration => registration.BindingId == "demo.slow").ToArray();
    }

    private static McpToolBroker CreateSynchronizedDemoMcpBroker(
        IReadOnlyList<McpToolRegistration> registrations, int expectedConnections, TimeSpan? timeout = null)
    {
        var allConnectionsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionCount = 0;
        var synchronized = registrations.Select(registration =>
        {
            var connect = registration.Connect;
            return registration with
            {
                Connect = async cancellationToken =>
                {
                    var client = await connect(cancellationToken);
                    if (Interlocked.Increment(ref connectionCount) == expectedConnections)
                        allConnectionsReady.TrySetResult();
                    try
                    {
                        await allConnectionsReady.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                        return client;
                    }
                    catch
                    {
                        await client.DisposeAsync();
                        throw;
                    }
                }
            };
        }).ToArray();
        return new McpToolBroker(synchronized, new DemoToolPolicy(), timeout ?? TimeSpan.FromSeconds(20));
    }

    private sealed class EchoToolScope(ConcurrentQueue<string> queries, Func<string, string>? respond,
        NodeInvocationContext invocation, bool recordToolDispatchEvents) : INodeToolScope
    {
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new NotSupportedException("The proof expects structured MCP arguments."));
        public async Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = arguments.GetProperty("query").GetString()!;
            var toolCallId = Guid.NewGuid();
            if (recordToolDispatchEvents)
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolAuthorized,
                    JsonSerializer.Serialize(new { toolCallId, bindingId, input = arguments }));
            queries.Enqueue(query);
            var output = respond?.Invoke(query) ?? $"echo:{query}";
            if (recordToolDispatchEvents)
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolCompleted,
                    JsonSerializer.Serialize(new { toolCallId, bindingId, output }));
            return output;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConcurrentUnknownOutcomeToolBroker(string bindingId, int expectedAuthorizations) : INodeToolBroker
    {
        private readonly TaskCompletionSource allAuthorized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string registeredBindingId = bindingId;
        private readonly int expectedAuthorizationCount = expectedAuthorizations;
        private int authorizationCount;

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this, invocation);

        private sealed class Scope(ConcurrentUnknownOutcomeToolBroker owner, NodeInvocationContext invocation) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string requestedBindingId, string input, CancellationToken cancellationToken = default) =>
                Task.FromException<string>(new NotSupportedException("The proof expects structured MCP arguments."));

            public async Task<string> InvokeJsonAsync(string requestedBindingId, JsonElement arguments,
                CancellationToken cancellationToken = default)
            {
                if (requestedBindingId != owner.registeredBindingId)
                    throw new InvalidOperationException("The workflow requested an unexpected custom tool.");
                var toolCallId = Guid.NewGuid();
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolRequested,
                    JsonSerializer.Serialize(new { toolCallId, bindingId = requestedBindingId }));
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolAuthorized,
                    JsonSerializer.Serialize(new { toolCallId, bindingId = requestedBindingId, input = arguments }));
                if (Interlocked.Increment(ref owner.authorizationCount) == owner.expectedAuthorizationCount)
                    owner.allAuthorized.TrySetResult();
                await owner.allAuthorized.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(10));
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
                    throw new InvalidOperationException("The custom tool unexpectedly completed before its timeout.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                const string reason = "The custom tool timed out after dispatch; its outcome may be unknown.";
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolUncertain, JsonSerializer.Serialize(new
                {
                    toolCallId,
                    bindingId = requestedBindingId,
                    failureCode = "tool.outcome_unknown",
                    disposition = ToolInvocationFailureDisposition.OutcomeUnknown.ToString(),
                    reason
                }));
                throw new ToolInvocationException(reason, ToolInvocationFailureDisposition.OutcomeUnknown);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingCustomOutcomeToolBroker(string bindingId, string failureMessage) : INodeToolBroker
    {
        private readonly string registeredBindingId = bindingId;
        private readonly string rawFailureMessage = failureMessage;
        private int dispatchAttempts;

        public int DispatchAttempts => Volatile.Read(ref dispatchAttempts);

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this, invocation);

        private sealed class Scope(ThrowingCustomOutcomeToolBroker owner, NodeInvocationContext invocation)
            : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string requestedBindingId, string input,
                CancellationToken cancellationToken = default) =>
                Task.FromException<string>(new NotSupportedException("The proof expects structured tool arguments."));

            public async Task<string> InvokeJsonAsync(string requestedBindingId, JsonElement arguments,
                CancellationToken cancellationToken = default)
            {
                if (requestedBindingId != owner.registeredBindingId)
                    throw new InvalidOperationException("The workflow requested an unexpected custom tool.");

                var toolCallId = Guid.NewGuid();
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolRequested,
                    JsonSerializer.Serialize(new { toolCallId, bindingId = requestedBindingId }));
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolAuthorized,
                    JsonSerializer.Serialize(new { toolCallId, bindingId = requestedBindingId, input = arguments }));

                Interlocked.Increment(ref owner.dispatchAttempts);
                throw new InvalidOperationException(owner.rawFailureMessage);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ParallelMcpBroker(string? denyNode = null, string? failNode = null,
        bool holdUntilCancelled = false, string? resultPayload = null,
        Task? releaseAfterReservationRejected = null, bool waitForBothAuthorizations = false) : INodeToolBroker
    {
        private readonly ConcurrentQueue<ParallelToolCall> calls = new();
        private readonly TaskCompletionSource branchesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource bothAuthorizationsRecorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int authorizationCount;
        private int activeCalls;
        private int maximumConcurrentCalls;
        private int dispatchedCalls;

        public int MaximumConcurrentCalls => maximumConcurrentCalls;
        public int DispatchedCalls => dispatchedCalls;
        public ParallelToolCall[] Calls => [.. calls];
        public Task BothBranchesStarted => branchesStarted.Task;
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public INodeToolScope OpenScope(NodeInvocationContext invocation) =>
            new Scope(this, invocation.NodeId, invocation.InvocationId, invocation.Record,
                denyNode, failNode, holdUntilCancelled, resultPayload, releaseAfterReservationRejected,
                waitForBothAuthorizations);

        private sealed class Scope(ParallelMcpBroker owner, string nodeId, Guid invocationId,
            Func<string, string?, ValueTask> record, string? denyNode, string? failNode,
            bool holdUntilCancelled, string? resultPayload, Task? releaseAfterReservationRejected,
            bool waitForBothAuthorizations) : INodeToolScope
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
                    if (waitForBothAuthorizations && Interlocked.Increment(ref owner.authorizationCount) == 2)
                        owner.bothAuthorizationsRecorded.TrySetResult();
                    if (waitForBothAuthorizations)
                        await owner.bothAuthorizationsRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    Interlocked.Increment(ref owner.dispatchedCalls);
                    if (releaseAfterReservationRejected is not null)
                        await releaseAfterReservationRejected;
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
        bool failParentFollowup = false, bool blockDelegatesUntilCancelled = false,
        int? researcherFailureStatusCode = null)
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
                {
                    if (researcherFailureStatusCode is { } statusCode)
                        throw new AgentProviderRequestException(AgentProviderFailureKind.Unavailable, statusCode);
                    throw new InvalidOperationException("Deterministic researcher failure.");
                }
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

    private sealed class DelegatedMcpProofModel(string parentInstructions, string researcherInstructions,
        string reviewerInstructions)
    {
        private readonly ConcurrentDictionary<string, int> turnsByProfile = new(StringComparer.Ordinal);
        public Task<ChatResponse> RespondAsync(ChatOptions? options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instructions = options?.Instructions ?? string.Empty;
            var turn = turnsByProfile.AddOrUpdate(instructions, 1, static (_, previous) => previous + 1);
            if (instructions == parentInstructions && turn == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("team-call", "team_0",
                        new Dictionary<string, object?> { ["task"] = "Review the proposal." })]))
                { ModelId = "proof-chat-v1", FinishReason = ChatFinishReason.ToolCalls });
            if ((instructions == researcherInstructions || instructions == reviewerInstructions) && turn == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"slow-call-{instructions[0]}", "tool_0",
                        new Dictionary<string, object?>())]))
                { ModelId = "proof-chat-v1", FinishReason = ChatFinishReason.ToolCalls });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Work completed."))
            { ModelId = "proof-chat-v1", FinishReason = ChatFinishReason.Stop });
        }
    }

    private sealed class DelegatedMcpProofChatClient(DelegatedMcpProofModel model) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => model.RespondAsync(options, cancellationToken);
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

    private sealed class ConcurrentPromptModel(int expectedConcurrentCalls, bool failAfterStart = false,
        Task? releaseOnReservationRejected = null)
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
                Task release = releaseOnReservationRejected is null
                    ? bothStarted.Task
                    : Task.WhenAny(bothStarted.Task, releaseOnReservationRejected);
                await release.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
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

    private sealed class SharedBudgetProofModel
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public Task<ChatResponse> RespondAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"query\":\"bounded\"}"))
            {
                ModelId = "proof-chat-v1",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 2, OutputTokenCount = 1, TotalTokenCount = 3 }
            });
        }
    }

    private sealed class SharedBudgetProofChatClient(SharedBudgetProofModel model) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => model.RespondAsync(cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed record PromptCall(int InstructionCharacters, int UserMessageCharacters);
    private sealed record ParallelToolCall(string NodeId, Guid InvocationId, string Query);
}
