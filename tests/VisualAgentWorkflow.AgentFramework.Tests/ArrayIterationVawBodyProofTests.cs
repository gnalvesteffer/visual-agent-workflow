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
    public async Task StructuredActivationSequenceProof_CancellationBeforeRootCompletionWins()
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-cancellation-before-root-completion-proof");
        using var cancellation = new CancellationTokenSource();
        var events = new ConcurrentQueue<ExecutionEvent>();

        ValueTask Record(ExecutionEvent evt)
        {
            events.Enqueue(evt);
            if (evt.Kind == ExecutionEventKinds.LoopIterationCompleted)
                cancellation.Cancel();
            return ValueTask.CompletedTask;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkflowRunner(new EchoToolBroker(), new EchoAgentExecutor())
                .ExecuteStructuredActivationSequenceProofAsync(Guid.NewGuid(), revision, "[\"only-item\"]",
                    "index", null, Record, cancellation.Token));

        var recorded = events.ToArray();
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.Started);
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.Cancelled && evt.ScopeId is null);
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task StructuredActivationCoordinator_ConsumesGraphDispatchedBreakAtNearestFrame()
    {
        const string input = "[\"continue\",\"stop\",\"must-not-run\"]";
        var revision = WorkflowRevision.CreateProofOnly(StructuredBreakProofDefinition(),
            "array-iteration-structured-graph-break-proof");
        var events = new ConcurrentQueue<ExecutionEvent>();

        ValueTask Record(ExecutionEvent evt)
        {
            events.Enqueue(evt);
            return ValueTask.CompletedTask;
        }

        var result = await new WorkflowRunner().ExecuteStructuredActivationSequenceProofAsync(Guid.NewGuid(),
            revision, input, "index", null, Record, itemParameterId: "item");

        var recorded = events.ToArray();
        Assert.Equal("continue:0", result.Output);
        Assert.False(result.Failed);
        Assert.Equal(2, recorded.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "proof-break");
        Assert.DoesNotContain(recorded, evt => evt.ScopeId == "array-loop/2");
        var breakFrameId = Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "array-loop/1").InvocationId;
        var breakEvent = Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "proof-break");
        using var breakData = JsonDocument.Parse(breakEvent.Data!);
        Assert.Equal("proof-break", breakData.RootElement.GetProperty("sourceNodeId").GetString());
        Assert.Equal(breakFrameId, breakData.RootElement.GetProperty("sourceFrameId").GetGuid());
        Assert.Equal(breakFrameId, breakData.RootElement.GetProperty("targetFrameId").GetGuid());
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    [Fact]
    public async Task StructuredActivationSequenceProof_BreakAbortsSequenceWithoutPublishingOutputCompletion()
    {
        var definition = SequenceBodyDefinition();
        var nodes = definition.Nodes.SetItem("inventory-tool",
            new NodeDefinition(WorkflowRunner.StructuredProofBreakNodeType, "proof-only"));
        var revision = WorkflowRevision.CreateProofOnly(definition with
        {
            Nodes = nodes,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        },
            "array-iteration-sequence-break-proof");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var tools = new EchoToolBroker();

        var result = await new WorkflowRunner(tools)
            .ExecuteStructuredActivationSequenceProofAsync(Guid.NewGuid(), revision, "[\"stop\",\"must-not-run\"]",
                "index", null, evt =>
                {
                    events.Enqueue(evt);
                    return ValueTask.CompletedTask;
                });

        var recorded = events.ToArray();
        Assert.False(result.Failed);
        Assert.Equal(string.Empty, result.Output);
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.Single(recorded, evt => evt.Kind == "sequence.aborted" && evt.NodeId == "sequence");
        Assert.DoesNotContain(recorded, evt => evt.Kind is ExecutionEventKinds.SequenceOutputCompleted or
            ExecutionEventKinds.SequenceCompleted);
        Assert.DoesNotContain(recorded, evt => evt.ScopeId == "array-loop/1");
    }

    [Fact]
    public async Task StructuredActivationSequenceProof_BreakAbortsForkAndSettlesAdmittedSibling()
    {
        var definition = ParallelBodyDefinition();
        var nodes = definition.Nodes.SetItem("agent-left",
            new NodeDefinition(WorkflowRunner.StructuredProofBreakNodeType, "proof-only"));
        var revision = WorkflowRevision.CreateProofOnly(definition with
        {
            Nodes = nodes,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        }, "array-iteration-fork-break-proof");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var agents = new BlockingAgentExecutor("north");

        var result = await new WorkflowRunner(agents: agents)
            .ExecuteStructuredActivationSequenceProofAsync(Guid.NewGuid(), revision, "[\"north\",\"later\"]",
                "index", null, evt =>
                {
                    events.Enqueue(evt);
                    return ValueTask.CompletedTask;
                }, afterNodeStarted: (nodeId, token) => nodeId == "agent-left"
                    ? new(agents.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token))
                    : ValueTask.CompletedTask);

        var recorded = events.ToArray();
        Assert.False(result.Failed);
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.True(recorded.Any(evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "agent-right"),
            string.Join(" | ", recorded.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(recorded, evt => evt.ScopeId == "array-loop/1");
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredActivationSequenceProof_PersistsGraphDispatchedBreakThroughStores(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition, "array-iteration-persisted-break-proof");
        using var runStore = new ProofRunStore(revision, "[\"north\",\"later\"]", sqlite);
        var recorder = new ProofRecorder(runStore, startRoot: false);
        var agents = new BlockingAgentExecutor("north");

        var result = await new WorkflowRunner(agents: agents)
            .ExecuteStructuredActivationSequenceProofAsync(runStore.RunId, revision, "[\"north\",\"later\"]",
                "index", null, evt =>
                {
                    recorder.Append(evt);
                    return ValueTask.CompletedTask;
                }, afterNodeStarted: (nodeId, token) => nodeId == "agent-left"
                    ? new(agents.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token))
                    : ValueTask.CompletedTask,
                proofBreakNodeIds: new HashSet<string>(StringComparer.Ordinal) { "agent-left" });

        var persistedEvents = recorder.Snapshot();
        Assert.False(result.Failed);
        Assert.Equal(string.Empty, result.Output);
        AssertPersistedSequence(runStore, persistedEvents, "completed");
        var reopened = runStore.ReopenStore().Get(runStore.RunId)!;
        Assert.Equal(persistedEvents, reopened.Events);
        Assert.Contains(reopened.Events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
            evt.NodeId == "agent-right");
        Assert.DoesNotContain(reopened.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "join");
        Assert.Single(reopened.Events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredActivationSequenceProof_PersistsAdmittedForkFailureWinningBreakRace(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition, "array-iteration-fork-break-failure-race-proof");
        using var runStore = new ProofRunStore(revision, "[\"north\"]", sqlite);
        var recorder = new ProofRecorder(runStore, startRoot: false);
        var agents = new FailingForkSiblingAgentExecutor();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(agents: agents).ExecuteStructuredActivationSequenceProofAsync(runStore.RunId,
                revision, "[\"north\"]", "index", null, evt =>
                {
                    recorder.Append(evt);
                    return ValueTask.CompletedTask;
                }, afterNodeStarted: (nodeId, token) => nodeId == "agent-left"
                    ? new(agents.FailureReached.Task.WaitAsync(TimeSpan.FromSeconds(5), token))
                    : ValueTask.CompletedTask,
                proofBreakNodeIds: new HashSet<string>(StringComparer.Ordinal) { "agent-left" }));

        var recorded = recorder.Snapshot();
        AssertPersistedSequence(runStore, recorded, "failed");
        var reopened = runStore.ReopenStore().Get(runStore.RunId)!;
        Assert.Equal(recorded, reopened.Events);
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "agent-right");
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredActivationSequenceProof_PersistsHostCancellationWinningBreakRace(bool sqlite)
    {
        var definition = StructuredBreakProofDefinition();
        var nodes = definition.Nodes.SetItem("proof-break", new NodeDefinition(NodeTypes.Terminate, "1.0.0")
        {
            Termination = new(WorkflowTerminationOutcomes.Success)
        });
        var revision = WorkflowRevision.Create(definition with { Nodes = nodes },
            "array-iteration-break-host-cancellation-race-proof");
        using var runStore = new ProofRunStore(revision, "[\"stop\"]", sqlite);
        var recorder = new ProofRecorder(runStore, startRoot: false);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkflowRunner().ExecuteStructuredActivationSequenceProofAsync(runStore.RunId, revision,
                "[\"stop\"]", "index", null, evt =>
                {
                    recorder.Append(evt);
                    return ValueTask.CompletedTask;
                }, cancellation.Token, afterNodeStarted: (nodeId, _) =>
                {
                    if (nodeId == "proof-break") cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }, itemParameterId: "item",
                proofBreakNodeIds: new HashSet<string>(StringComparer.Ordinal) { "proof-break" }));

        var recorded = recorder.Snapshot();
        AssertPersistedSequence(runStore, recorded, "cancelled");
        var reopened = runStore.ReopenStore().Get(runStore.RunId)!;
        Assert.Equal(recorded, reopened.Events);
        Assert.Single(recorded, evt => evt.Kind == ExecutionEventKinds.Cancelled && evt.ScopeId is null);
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    [Fact]
    public void StructuredActivationFrame_RejectsBreakSignalsFromForeignFrames()
    {
        var frame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null, "array-loop", 0, "item",
            ImmutableSortedDictionary<string, string>.Empty);
        var foreignTarget = new WorkflowRunner.StructuredActivationBreakSignal("proof-break", frame.Id,
            Guid.NewGuid(), frame.ScopeId);
        var staleSource = new WorkflowRunner.StructuredActivationBreakSignal("proof-break", Guid.NewGuid(),
            frame.Id, frame.ScopeId);

        Assert.Throws<InvalidOperationException>(() => frame.ValidateNearestBreak(foreignTarget));
        Assert.Throws<InvalidOperationException>(() => frame.ValidateNearestBreak(staleSource));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ResolveTypedItemAndIndexThroughGraphValues(bool sqlite)
    {
        const string input = "[-9223372036854775808,0,9223372036854775807]";
        var revision = WorkflowRevision.Create(TypedItemAndIndexBodyDefinition(),
            "array-iteration-typed-item-index-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredAgents = new EchoAgentExecutor();
        var subworkflowAgents = new EchoAgentExecutor();
        var hybridAgents = new EchoAgentExecutor();

        var structured = await new WorkflowRunner(agents: structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                }, itemParameterId: "item", arrayElementType: WorkflowDataTypes.Integer);

        string? subworkflowOutput = null;
        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, subworkflowAgents, new EchoToolBroker(),
            arrayElementType: WorkflowDataTypes.Integer);
        var subworkflowExecutor = new WorkflowBuilder(subworkflowIterator)
            .WithOutputFrom(subworkflowIterator).Build();
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflowExecutor, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            hybridAgents, new EchoToolBroker(), "array-loop",
            ParseProofArrayItems(input, WorkflowDataTypes.Integer), null, "index", [],
            async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var outcome = await new WorkflowRunner(agents: hybridAgents).ExecuteWithOutcomeAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (outcome.Failed || outcome.Output is null)
                    throw new InvalidOperationException("The hybrid typed-value body did not return a result.");
                return new ActivationLoopProofValue(outcome.Output);
            });
        hybridRecorder.CompleteRoot();

        Assert.Equal("{\"query\":\"-9223372036854775808:0\"};{\"query\":\"0:1\"};{\"query\":\"9223372036854775807:2\"}",
            structured.Output);
        Assert.Equal(structured.Output, subworkflowOutput);
        Assert.Equal(structured.Output, hybridOutput);
        Assert.Equal(["-9223372036854775808:0", "0:1", "9223372036854775807:2"], structuredAgents.Inputs);
        Assert.Equal(structuredAgents.Inputs, subworkflowAgents.Inputs);
        Assert.Equal(structuredAgents.Inputs, hybridAgents.Inputs);

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(EventShape(structuredEvents), EventShape(subworkflowEvents));
        Assert.Equal(EventShape(structuredEvents), EventShape(hybridEvents));
        Assert.Equal(DataEvaluationShape(structuredEvents), DataEvaluationShape(subworkflowEvents));
        Assert.Equal(DataEvaluationShape(structuredEvents), DataEvaluationShape(hybridEvents));

        var expectedItems = new long[] { long.MinValue, 0, long.MaxValue };
        foreach (var events in new[] { structuredEvents, subworkflowEvents, hybridEvents })
        {
            for (var index = 0; index < expectedItems.Length; index++)
            {
                var scopeId = $"array-loop/{index}";
                Assert.Equal(expectedItems[index], ReadIntegerDataEvaluation(events, scopeId, "data:item-value"));
                Assert.Equal(index, ReadIntegerDataEvaluation(events, scopeId, "data:index-value"));
            }
        }

        static (string Kind, string? NodeId, string? ScopeId)[] EventShape(IEnumerable<ExecutionEvent> events) =>
            events.Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
                ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.NodeStarted or
                ExecutionEventKinds.NodeCompleted)
                .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId)).ToArray();

        static (string? NodeId, string? ScopeId, string? ConnectionId, string? Data)[] DataEvaluationShape(
            IEnumerable<ExecutionEvent> events) => events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated)
            .Select(evt => (evt.NodeId, evt.ScopeId, evt.ConnectionId, evt.Data)).ToArray();

        static long ReadIntegerDataEvaluation(IEnumerable<ExecutionEvent> events, string scopeId, string connectionId)
        {
            var evt = Assert.Single(events, item => item.Kind == ExecutionEventKinds.DataEvaluated &&
                item.ScopeId == scopeId && item.ConnectionId == connectionId);
            using var payload = JsonDocument.Parse(evt.Data!);
            return payload.RootElement.GetProperty("value").GetInt64();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ScopeVariableStateToEachActivation(bool sqlite)
    {
        const string input = "[\"alpha\",\"beta\",\"gamma\"]";
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-variable-activation-scope-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredAgents = new EchoAgentExecutor();
        var subworkflowAgents = new EchoAgentExecutor();
        var hybridAgents = new EchoAgentExecutor();

        var structured = await new WorkflowRunner(new EchoToolBroker(), structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                });

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, subworkflowAgents, new EchoToolBroker());
        var subworkflow = new WorkflowBuilder(subworkflowIterator).WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflow, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            hybridAgents, new EchoToolBroker(), "array-loop",
            ParseProofArrayItems(input, WorkflowDataTypes.String), null, "index", [], async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var outcome = await new WorkflowRunner(new EchoToolBroker(), hybridAgents).ExecuteWithOutcomeAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (outcome.Failed || outcome.Output is null)
                    throw new InvalidOperationException("The hybrid variable-scope body failed.");
                return new ActivationLoopProofValue(outcome.Output);
            });
        hybridRecorder.CompleteRoot();

        Assert.Equal("echo:ALPHA:0;echo:BETA:1;echo:GAMMA:2", structured.Output);
        Assert.Equal(structured.Output, subworkflowOutput);
        Assert.Equal(structured.Output, hybridOutput);
        Assert.Equal(["ALPHA:0", "BETA:1", "GAMMA:2"], structuredAgents.Inputs);
        Assert.Equal(structuredAgents.Inputs, subworkflowAgents.Inputs);
        Assert.Equal(structuredAgents.Inputs, hybridAgents.Inputs);

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(VariableEvidence(structuredEvents), VariableEvidence(subworkflowEvents));
        Assert.Equal(VariableEvidence(structuredEvents), VariableEvidence(hybridEvents));
        Assert.Equal(RuntimeDataEvidence(structuredEvents), RuntimeDataEvidence(subworkflowEvents));
        Assert.Equal(RuntimeDataEvidence(structuredEvents), RuntimeDataEvidence(hybridEvents));

        var expectedItems = new[] { "alpha", "beta", "gamma" };
        foreach (var events in new[] { structuredEvents, subworkflowEvents, hybridEvents })
        {
            for (var index = 0; index < expectedItems.Length; index++)
            {
                var activation = events.Where(evt => evt.ScopeId == $"array-loop/{index}").ToArray();
                Assert.Equal(expectedItems[index], ReadVariableValue(activation,
                    ExecutionEventKinds.VariableWritten, "set"));
                Assert.Equal("default", ReadVariablePreviousValue(activation, "set"));
                Assert.Equal(expectedItems[index], ReadVariableValue(activation,
                    ExecutionEventKinds.VariableRead, "get"));
                Assert.Equal(expectedItems[index], ReadEvaluatedValue(activation, "get"));
                var agentResult = ReadRuntimeOutput(activation, "agent", "data:agent-tool");
                using (var agentPayload = JsonDocument.Parse(agentResult))
                    Assert.Equal(expectedItems[index].ToUpperInvariant() + ":" + index,
                        agentPayload.RootElement.GetProperty("query").GetString());
                Assert.Equal("echo:" + expectedItems[index].ToUpperInvariant() + ":" + index,
                    ReadRuntimeOutput(activation, "tool", "data:tool-output"));
            }
        }

        static (string Kind, string? NodeId, string? ScopeId, string? Data)[] VariableEvidence(
            IEnumerable<ExecutionEvent> events) => events.Where(evt => evt.Kind is
                ExecutionEventKinds.VariableWritten or ExecutionEventKinds.VariableRead)
            .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId, evt.Data)).ToArray();

        static (string? NodeId, string? ScopeId, string? ConnectionId, string? Data)[] RuntimeDataEvidence(
            IEnumerable<ExecutionEvent> events) => events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated)
            .Select(evt => (evt.NodeId, evt.ScopeId, evt.ConnectionId, evt.Data)).ToArray();

        static string ReadRuntimeOutput(IEnumerable<ExecutionEvent> events, string nodeId, string connectionId)
        {
            var evt = Assert.Single(events, item => item.Kind == ExecutionEventKinds.DataEvaluated &&
                item.NodeId == nodeId && item.ConnectionId == connectionId);
            using var payload = JsonDocument.Parse(evt.Data!);
            return payload.RootElement.GetProperty("value").GetString()!;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ScopeGraphFunctionResultsAndEvidenceToEachActivation(bool sqlite)
    {
        const string input = "[\"alpha\",\"beta\",\"gamma\"]";
        var revision = WorkflowRevision.Create(GraphFunctionValueBodyDefinition(),
            "array-iteration-function-activation-scope-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);

        var structured = await new WorkflowRunner().ExecuteStructuredActivationSequenceProofAsync(
            structuredStore.RunId, revision, input, "index", null, evt =>
            {
                structuredRecorder.Append(evt);
                return ValueTask.CompletedTask;
            });

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, new EchoAgentExecutor(), new EchoToolBroker());
        var subworkflow = new WorkflowBuilder(subworkflowIterator).WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflow, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            new EchoAgentExecutor(), new EchoToolBroker(), "array-loop",
            ParseProofArrayItems(input, WorkflowDataTypes.String), null, "index", [], async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The hybrid graph-function body failed.");
                return new ActivationLoopProofValue(result.Output);
            });
        hybridRecorder.CompleteRoot();

        const string expectedOutput = "alpha!;beta!;gamma!";
        Assert.Equal(expectedOutput, structured.Output);
        Assert.Equal(structured.Output, subworkflowOutput);
        Assert.Equal(structured.Output, hybridOutput);

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(FunctionEvidenceShape(structuredEvents), FunctionEvidenceShape(subworkflowEvents));
        Assert.Equal(FunctionEvidenceShape(structuredEvents), FunctionEvidenceShape(hybridEvents));

        var expectedItems = new[] { "alpha", "beta", "gamma" };
        foreach (var events in new[] { structuredEvents, subworkflowEvents, hybridEvents })
        {
            for (var index = 0; index < expectedItems.Length; index++)
            {
                var evt = Assert.Single(events, item => item.Kind == ExecutionEventKinds.DataEvaluated &&
                    item.NodeId == "call" && item.ScopeId == $"array-loop/{index}");
                Assert.Equal("data:call-output", evt.ConnectionId);
                using var payload = JsonDocument.Parse(evt.Data!);
                Assert.Equal(expectedItems[index] + "!", payload.RootElement.GetProperty("value").GetString());
                var functionEvidence = payload.RootElement.GetProperty("function");
                Assert.Equal(expectedItems[index], functionEvidence.GetProperty("inputs")
                    .GetProperty("text").GetString());
                Assert.Contains(functionEvidence.GetProperty("evaluatedValues").EnumerateArray(), value =>
                    value.GetProperty("nodeId").GetString() == "concat" &&
                    value.GetProperty("value").GetString() == expectedItems[index] + "!");
            }
        }

        static (string? NodeId, string? ScopeId, string? ConnectionId, string? Data)[] FunctionEvidenceShape(
            IEnumerable<ExecutionEvent> events) => events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
                evt.NodeId == "call").Select(evt => (evt.NodeId, evt.ScopeId, evt.ConnectionId, evt.Data)).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ExecuteParallelAgentGraphFunctionToolsPerActivation(bool sqlite)
    {
        const string input = "[\"north\",\"south\"]";
        var function = GraphFunctionValueBodyDefinition().Nodes["call"].GraphFunction!;
        var provider = new AgentProviderDescriptor("proof.parallel-graph-function", "Parallel graph function proof",
            "scripted-graph-function-v1", true);
        var agentConfiguration = new AgentConfiguration(provider.ProviderId, null,
            "Call the assigned deterministic graph function with the input text, then return a confirmation.", [])
        {
            GraphFunctionTools = [function]
        };
        var bodyDefinition = ParallelBodyDefinition();
        var definition = bodyDefinition with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer }),
            Nodes = bodyDefinition.Nodes
                .SetItem("agent-left", bodyDefinition.Nodes["agent-left"] with { Agent = agentConfiguration })
                .SetItem("agent-right", bodyDefinition.Nodes["agent-right"] with { Agent = agentConfiguration })
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-parallel-agent-graph-function-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredSession = new ParallelActivationBarrier();
        var subworkflowSession = new ParallelActivationBarrier();
        var hybridSession = new ParallelActivationBarrier();
        var structuredBroker = new EchoToolBroker();
        var subworkflowBroker = new EchoToolBroker();
        var hybridBroker = new EchoToolBroker();

        AgentNodeExecutor CreateAgents(INodeToolBroker broker, ParallelActivationBarrier session) =>
            new([new AgentProviderRegistration(provider, () => new ParallelGraphFunctionChatClient(session))], broker);

        var structured = await new WorkflowRunner(structuredBroker, CreateAgents(structuredBroker, structuredSession))
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                });

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, CreateAgents(subworkflowBroker, subworkflowSession), subworkflowBroker);
        var subworkflowWorkflow = new WorkflowBuilder(subworkflowIterator).WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflowWorkflow, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridAgents = CreateAgents(hybridBroker, hybridSession);
        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            hybridAgents, hybridBroker, "array-loop", ParseProofArrayItems(input, WorkflowDataTypes.String),
            null, "index", [], async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner(hybridBroker, hybridAgents).ExecuteWithOutcomeAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The hybrid parallel graph-function body failed.");
                return new ActivationLoopProofValue(result.Output);
            });
        hybridRecorder.CompleteRoot();

        Assert.False(structured.Failed);
        Assert.Equal(structured.Output, subworkflowOutput);
        Assert.Equal(structured.Output, hybridOutput);
        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(structuredEvents, structuredStore.ReopenStore().Get(structuredStore.RunId)!.Events);
        Assert.Equal(subworkflowEvents, subworkflowStore.ReopenStore().Get(subworkflowStore.RunId)!.Events);
        Assert.Equal(hybridEvents, hybridStore.ReopenStore().Get(hybridStore.RunId)!.Events);
        Assert.Equal(2, structuredSession.CompletedPairs);
        Assert.Equal(2, subworkflowSession.CompletedPairs);
        Assert.Equal(2, hybridSession.CompletedPairs);
        Assert.Equal(GraphFunctionToolEvidence(structuredEvents), GraphFunctionToolEvidence(subworkflowEvents));
        Assert.Equal(GraphFunctionToolEvidence(structuredEvents), GraphFunctionToolEvidence(hybridEvents));

        foreach (var events in new[] { structuredEvents, subworkflowEvents, hybridEvents })
        {
            foreach (var (scope, item) in new[] { ("array-loop/0", "north"), ("array-loop/1", "south") })
            foreach (var nodeId in new[] { "agent-left", "agent-right" })
            {
                var completed = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentGraphFunctionCompleted &&
                    evt.NodeId == nodeId && evt.ScopeId == scope);
                using var payload = JsonDocument.Parse(completed.Data!);
                Assert.Equal(function.Definition.FunctionId, payload.RootElement.GetProperty("functionId").GetString());
                Assert.Equal(item, payload.RootElement.GetProperty("inputs").GetProperty("text").GetString());
                Assert.Equal(item + "!", payload.RootElement.GetProperty("outputs").GetProperty("result").GetString());
                var functionInvocationId = payload.RootElement.GetProperty("functionInvocationId").GetGuid();
                var started = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentGraphFunctionStarted &&
                    evt.NodeId == nodeId && evt.ScopeId == scope &&
                    ReadFunctionInvocationId(evt.Data) == functionInvocationId);
                using var startPayload = JsonDocument.Parse(started.Data!);
                Assert.Equal(function.Definition.FunctionId,
                    startPayload.RootElement.GetProperty("functionId").GetString());
            }
        }

        static (string? NodeId, string? ScopeId, string? FunctionId, string? Input, string? Output)[]
            GraphFunctionToolEvidence(IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind == ExecutionEventKinds.AgentGraphFunctionCompleted)
            .Select(evt =>
            {
                using var payload = JsonDocument.Parse(evt.Data!);
                return (evt.NodeId, evt.ScopeId,
                    payload.RootElement.GetProperty("functionId").GetString(),
                    payload.RootElement.GetProperty("inputs").GetProperty("text").GetString(),
                    payload.RootElement.GetProperty("outputs").GetProperty("result").GetString());
            })
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.NodeId, StringComparer.Ordinal)
            .ToArray();

        static Guid ReadFunctionInvocationId(string? data)
        {
            using var payload = JsonDocument.Parse(data!);
            return payload.RootElement.GetProperty("functionInvocationId").GetGuid();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_EvaluateDirectGraphFunctionNodesInParallelPerActivation(bool sqlite)
    {
        const string input = "[\"north\",\"south\"]";
        var function = GraphFunctionValueBodyDefinition().Nodes["call"].GraphFunction!;
        var body = ParallelBodyDefinition();
        var nodes = body.Nodes
            .Add("function-left", new NodeDefinition(NodeTypes.FunctionCall, "1.0.0") { GraphFunction = function })
            .Add("function-right", new NodeDefinition(NodeTypes.FunctionCall, "1.0.0") { GraphFunction = function });
        var connections = body.Connections.Remove("input-left").Remove("input-right")
            .Add("input-function-left", new(new("input", "out"), new("function-left", "text"), "data"))
            .Add("input-function-right", new(new("input", "out"), new("function-right", "text"), "data"))
            .Add("function-left-agent", new(new("function-left", "result"), new("agent-left", "in"), "data"))
            .Add("function-right-agent", new(new("function-right", "result"), new("agent-right", "in"), "data"));
        var definition = body with
        {
            Nodes = nodes,
            Connections = connections,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-parallel-direct-function-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredAgents = new ParallelFunctionInputAgentExecutor(new ParallelActivationBarrier());
        var subworkflowAgents = new ParallelFunctionInputAgentExecutor(new ParallelActivationBarrier());
        var hybridAgents = new ParallelFunctionInputAgentExecutor(new ParallelActivationBarrier());

        var structured = await new WorkflowRunner(agents: structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                });

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, subworkflowAgents, new EchoToolBroker());
        var subworkflowWorkflow = new WorkflowBuilder(subworkflowIterator).WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflowWorkflow, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            hybridAgents, new EchoToolBroker(), "array-loop", ParseProofArrayItems(input, WorkflowDataTypes.String),
            null, "index", [], async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner(agents: hybridAgents).ExecuteWithOutcomeAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The hybrid direct graph-function body failed.");
                return new ActivationLoopProofValue(result.Output);
            });
        hybridRecorder.CompleteRoot();

        Assert.False(structured.Failed);
        Assert.Equal(structured.Output, subworkflowOutput);
        Assert.Equal(structured.Output, hybridOutput);
        Assert.Equal(2, structuredAgents.Barrier.CompletedPairs);
        Assert.Equal(2, subworkflowAgents.Barrier.CompletedPairs);
        Assert.Equal(2, hybridAgents.Barrier.CompletedPairs);
        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(structuredEvents, structuredStore.ReopenStore().Get(structuredStore.RunId)!.Events);
        Assert.Equal(subworkflowEvents, subworkflowStore.ReopenStore().Get(subworkflowStore.RunId)!.Events);
        Assert.Equal(hybridEvents, hybridStore.ReopenStore().Get(hybridStore.RunId)!.Events);
        Assert.Equal(DirectFunctionNodeEvidence(structuredEvents), DirectFunctionNodeEvidence(subworkflowEvents));
        Assert.Equal(DirectFunctionNodeEvidence(structuredEvents), DirectFunctionNodeEvidence(hybridEvents));

        foreach (var events in new[] { structuredEvents, subworkflowEvents, hybridEvents })
        foreach (var (scope, item) in new[] { ("array-loop/0", "north"), ("array-loop/1", "south") })
        foreach (var (nodeId, connectionId) in new[]
        {
            ("function-left", "data:function-left-agent"),
            ("function-right", "data:function-right-agent")
        })
        {
            var evaluated = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
                evt.NodeId == nodeId && evt.ScopeId == scope && evt.ConnectionId == connectionId);
            using var payload = JsonDocument.Parse(evaluated.Data!);
            Assert.Equal("string", payload.RootElement.GetProperty("dataType").GetString());
            Assert.Equal(item + "!", payload.RootElement.GetProperty("value").GetString());
            var functionEvidence = payload.RootElement.GetProperty("function");
            Assert.Equal(function.Definition.FunctionId, functionEvidence.GetProperty("functionId").GetString());
            Assert.Equal(item, functionEvidence.GetProperty("inputs").GetProperty("text").GetString());
            Assert.Contains(functionEvidence.GetProperty("evaluatedValues").EnumerateArray(), value =>
                value.GetProperty("nodeId").GetString() == "concat" &&
                value.GetProperty("value").GetString() == item + "!");
        }

        static (string? NodeId, string? ScopeId, string? ConnectionId, string? Data)[] DirectFunctionNodeEvidence(
            IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
                evt.NodeId is "function-left" or "function-right")
            .Select(evt => (evt.NodeId, evt.ScopeId, evt.ConnectionId, evt.Data))
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.NodeId, StringComparer.Ordinal)
            .ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ScopeRuntimeNodeOutputsToEachActivation(bool sqlite)
    {
        const string input = "[\"alpha\",\"beta\",\"gamma\"]";
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent-left", new(NodeTypes.Agent, "1.0.0", Agent: new("proof.echo-agent", null, "Left", [])))
            .Add("item", new(NodeTypes.WorkflowValue, "1.0.0")
                { ParameterId = "item", OutputDataType = WorkflowDataTypes.String })
            .Add("concat", new(NodeTypes.TextConcat, "1.0.0"))
            .Add("agent-right", new(NodeTypes.Agent, "1.0.0", Agent: new("proof.echo-agent", null, "Right", [])))
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String });
        var connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-left", new(new("input", "out"), new("agent-left", "in"), "data"))
            .Add("agent-left-concat", new(new("agent-left", "out"), new("concat", "left"), "data"))
            .Add("item-concat", new(new("item", "out"), new("concat", "right"), "data"))
            .Add("concat-right", new(new("concat", "out"), new("agent-right", "in"), "data"))
            .Add("agent-right-output", new(new("agent-right", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-agent-left", Edge("input", "then", "agent-left", "execute"))
            .Add("agent-left-right", Edge("agent-left", "out", "agent-right", "execute"))
            .Add("agent-right-output", Edge("agent-right", "out", "output", "execute"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-runtime-output-cache-proof", "input", nodes, connections)
        {
            ExecutionConnections = execution,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("item", new("Current item", "") { Type = WorkflowDataTypes.String })
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-runtime-output-cache-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredAgents = new EchoAgentExecutor();
        var subworkflowAgents = new EchoAgentExecutor();
        var hybridAgents = new EchoAgentExecutor();

        var structured = await new WorkflowRunner(agents: structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                }, itemParameterId: "item");

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, subworkflowAgents, new EchoToolBroker());
        var subworkflowWorkflow = new WorkflowBuilder(subworkflowIterator).WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflowWorkflow, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            hybridAgents, new EchoToolBroker(), "array-loop", ParseProofArrayItems(input, WorkflowDataTypes.String),
            null, "index", [], async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner(agents: hybridAgents).ExecuteWithOutcomeAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The hybrid runtime-output cache body failed.");
                return new ActivationLoopProofValue(result.Output);
            });
        hybridRecorder.CompleteRoot();

        Assert.False(structured.Failed);
        Assert.Equal(structured.Output, subworkflowOutput);
        Assert.Equal(structured.Output, hybridOutput);
        var expectedAgentInputs = new[]
        {
            "alpha", "{\"query\":\"alpha-agent-left\"}alpha",
            "beta", "{\"query\":\"beta-agent-left\"}beta",
            "gamma", "{\"query\":\"gamma-agent-left\"}gamma"
        };
        Assert.Equal(expectedAgentInputs, structuredAgents.Inputs);
        Assert.Equal(expectedAgentInputs, subworkflowAgents.Inputs);
        Assert.Equal(expectedAgentInputs, hybridAgents.Inputs);

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        foreach (var (store, events) in new[]
        {
            (structuredStore, structuredEvents), (subworkflowStore, subworkflowEvents), (hybridStore, hybridEvents)
        })
        {
            AssertPersistedSequence(store, events, "completed");
            Assert.Equal(events, store.ReopenStore().Get(store.RunId)!.Events);
        }
        Assert.Equal(RuntimeOutputEvidence(structuredEvents), RuntimeOutputEvidence(subworkflowEvents));
        Assert.Equal(RuntimeOutputEvidence(structuredEvents), RuntimeOutputEvidence(hybridEvents));

        foreach (var (events, item, index) in new[]
        {
            (structuredEvents, "alpha", 0), (structuredEvents, "beta", 1), (structuredEvents, "gamma", 2),
            (subworkflowEvents, "alpha", 0), (subworkflowEvents, "beta", 1), (subworkflowEvents, "gamma", 2),
            (hybridEvents, "alpha", 0), (hybridEvents, "beta", 1), (hybridEvents, "gamma", 2)
        })
        {
            var evaluated = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
                evt.NodeId == "agent-left" && evt.ScopeId == $"array-loop/{index}" &&
                evt.ConnectionId == "data:agent-left-concat");
            using var payload = JsonDocument.Parse(evaluated.Data!);
            Assert.Equal("agent-right", payload.RootElement.GetProperty("activatedByNodeId").GetString());
            Assert.Equal($"{{\"query\":\"{item}-agent-left\"}}",
                payload.RootElement.GetProperty("value").GetString());
        }

        static (string? NodeId, string? ScopeId, string? ConnectionId, string? Data)[] RuntimeOutputEvidence(
            IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "agent-left")
            .Select(evt => (evt.NodeId, evt.ScopeId, evt.ConnectionId, evt.Data))
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_EmptyTypedArrayCompletesWithoutBodyActivation(bool sqlite)
    {
        const string input = "[]";
        var revision = WorkflowRevision.Create(TypedItemAndIndexBodyDefinition(),
            "array-iteration-empty-typed-array-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);

        var structured = await new WorkflowRunner(agents: new EchoAgentExecutor())
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                }, itemParameterId: "item", arrayElementType: WorkflowDataTypes.Integer);

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, new EchoAgentExecutor(), new EchoToolBroker(),
            arrayElementType: WorkflowDataTypes.Integer);
        var subworkflowExecutor = new WorkflowBuilder(subworkflowIterator)
            .WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflowExecutor, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            new EchoAgentExecutor(), new EchoToolBroker(), "array-loop",
            ParseProofArrayItems(input, WorkflowDataTypes.Integer), null, "index", [],
            frame => Task.FromResult<LoopProofResult>(new ActivationLoopProofValue(frame.Item)));
        hybridRecorder.CompleteRoot();

        Assert.False(structured.Failed);
        Assert.Equal(string.Empty, structured.Output);
        Assert.Equal(string.Empty, subworkflowOutput);
        Assert.Equal(string.Empty, hybridOutput);

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(EventShape(structuredEvents), EventShape(subworkflowEvents));
        Assert.Equal(EventShape(structuredEvents), EventShape(hybridEvents));
        Assert.DoesNotContain(structuredEvents, evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
            ExecutionEventKinds.NodeStarted);
        Assert.DoesNotContain(subworkflowEvents, evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
            ExecutionEventKinds.NodeStarted);
        Assert.DoesNotContain(hybridEvents, evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
            ExecutionEventKinds.NodeStarted);

        static (string Kind, string? NodeId, string? ScopeId)[] EventShape(IEnumerable<ExecutionEvent> events) =>
            events.Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
                    ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.NodeStarted or
                    ExecutionEventKinds.NodeCompleted)
                .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId)).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_RouteEachItemThroughUnifiedBooleanBranch(bool sqlite)
    {
        const string input = "[\"yes\",\"no\",\"yes\"]";
        var revision = WorkflowRevision.Create(ConditionalItemBodyDefinition(),
            "array-iteration-conditional-route-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredAgents = new EchoAgentExecutor();
        var subworkflowAgents = new EchoAgentExecutor();
        var hybridAgents = new EchoAgentExecutor();

        var structured = await new WorkflowRunner(agents: structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                });

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, subworkflowAgents, new EchoToolBroker());
        var subworkflowExecutor = new WorkflowBuilder(subworkflowIterator)
            .WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflowExecutor, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            hybridAgents, new EchoToolBroker(), "array-loop", ["yes", "no", "yes"], null, "index", [],
            async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner(agents: hybridAgents).ExecuteWithOutcomeAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The conditional hybrid body did not return a result.");
                return new ActivationLoopProofValue(result.Output);
            });
        hybridRecorder.CompleteRoot();

        const string expected = "yes;no;yes";
        Assert.False(structured.Failed);
        Assert.Equal(expected, structured.Output);
        Assert.Equal(expected, subworkflowOutput);
        Assert.Equal(expected, hybridOutput);
        Assert.Equal(["yes", "no", "yes"], structuredAgents.Inputs);
        Assert.Equal(structuredAgents.Inputs, subworkflowAgents.Inputs);
        Assert.Equal(structuredAgents.Inputs, hybridAgents.Inputs);

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(EventShape(structuredEvents), EventShape(subworkflowEvents));
        Assert.Equal(EventShape(structuredEvents), EventShape(hybridEvents));
        Assert.Equal(["true", "false", "true"], BranchPorts(structuredEvents));
        Assert.Equal(BranchPorts(structuredEvents), BranchPorts(subworkflowEvents));
        Assert.Equal(BranchPorts(structuredEvents), BranchPorts(hybridEvents));
        Assert.Equal(["array-loop/0", "array-loop/1", "array-loop/2"], BranchScopes(structuredEvents));
        Assert.Equal(BranchScopes(structuredEvents), BranchScopes(subworkflowEvents));
        Assert.Equal(BranchScopes(structuredEvents), BranchScopes(hybridEvents));
        Assert.Equal(["agent-yes", "agent-no", "agent-yes"], StartedAgents(structuredEvents));
        Assert.Equal(StartedAgents(structuredEvents), StartedAgents(subworkflowEvents));
        Assert.Equal(StartedAgents(structuredEvents), StartedAgents(hybridEvents));

        static (string Kind, string? NodeId, string? ScopeId)[] EventShape(IEnumerable<ExecutionEvent> events) =>
            events.Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
                    ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.BranchSelected or
                    ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeCompleted)
                .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId)).ToArray();

        static string[] BranchPorts(IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "route")
            .Select(evt =>
            {
                using var payload = JsonDocument.Parse(evt.Data!);
                return payload.RootElement.GetProperty("port").GetString()!;
            })
            .ToArray();

        static string[] BranchScopes(IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "route")
            .Select(evt => evt.ScopeId ?? string.Empty).ToArray();

        static string[] StartedAgents(IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId?.StartsWith("agent-", StringComparison.Ordinal) == true)
            .Select(evt => evt.NodeId!).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_MatchUnifiedParallelJoinAcrossAllCandidates(bool sqlite)
    {
        const string input = "[\"north\",\"south\"]";
        var definition = ParallelBodyDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-three-candidate-parallel-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredAgents = new EchoAgentExecutor();
        var subworkflowAgents = new EchoAgentExecutor();
        var hybridAgents = new EchoAgentExecutor();

        var structured = await new WorkflowRunner(agents: structuredAgents)
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                });

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, subworkflowAgents, new EchoToolBroker());
        var subworkflowExecutor = new WorkflowBuilder(subworkflowIterator)
            .WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflowExecutor, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            hybridAgents, new EchoToolBroker(), "array-loop", ["north", "south"], null, "index", [],
            async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner(agents: hybridAgents).ExecuteWithOutcomeAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The parallel hybrid body did not return a result.");
                return new ActivationLoopProofValue(result.Output);
            });
        hybridRecorder.CompleteRoot();

        Assert.False(structured.Failed);
        Assert.Equal(structured.Output, subworkflowOutput);
        Assert.Equal(structured.Output, hybridOutput);
        Assert.Equal(4, structuredAgents.Inputs.Length);
        Assert.Equal(structuredAgents.Inputs.Order(StringComparer.Ordinal),
            subworkflowAgents.Inputs.Order(StringComparer.Ordinal));
        Assert.Equal(structuredAgents.Inputs.Order(StringComparer.Ordinal),
            hybridAgents.Inputs.Order(StringComparer.Ordinal));
        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(ParallelEventShape(structuredEvents), ParallelEventShape(subworkflowEvents));
        Assert.Equal(ParallelEventShape(structuredEvents), ParallelEventShape(hybridEvents));
        AssertPerItemJoin(structuredEvents);
        AssertPerItemJoin(subworkflowEvents);
        AssertPerItemJoin(hybridEvents);
        AssertParallelRuntimeOutputs(structuredEvents);
        AssertParallelRuntimeOutputs(subworkflowEvents);
        AssertParallelRuntimeOutputs(hybridEvents);

        static (string Kind, string? NodeId, string? ScopeId, string? ConnectionId)[] ParallelEventShape(
            IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.NodeId is "agent-left" or "agent-right" or "join")
            .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId))
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.Kind, StringComparer.Ordinal)
            .ThenBy(evt => evt.NodeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.ConnectionId, StringComparer.Ordinal)
            .ToArray();

        static void AssertParallelRuntimeOutputs(IReadOnlyList<ExecutionEvent> events)
        {
            var expectedItems = new[] { "north", "south" };
            for (var index = 0; index < expectedItems.Length; index++)
            {
                var scopeId = $"array-loop/{index}";
                var join = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                    evt.NodeId == "join" && evt.ScopeId == scopeId);
                using var joined = JsonDocument.Parse(join.Data!);
                foreach (var (branchId, agentId) in new[]
                {
                    ("left", "agent-left"),
                    ("right", "agent-right")
                })
                {
                    var output = joined.RootElement.GetProperty(branchId).GetString();
                    using var agentResult = JsonDocument.Parse(output!);
                    Assert.Equal(expectedItems[index] + "-" + agentId,
                        agentResult.RootElement.GetProperty("query").GetString());
                }
            }
        }

        static void AssertPerItemJoin(IReadOnlyList<ExecutionEvent> events)
        {
            Assert.Equal(["array-loop/0", "array-loop/1"], events
                .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
                .Select(evt => evt.ScopeId));
            foreach (var scopeId in new[] { "array-loop/0", "array-loop/1" })
            {
                var scoped = events.Where(evt => evt.ScopeId == scopeId).ToArray();
                foreach (var agentId in new[] { "agent-left", "agent-right" })
                {
                    var started = Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                        evt.NodeId == agentId);
                    var completed = Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                        evt.NodeId == agentId);
                    Assert.Equal(started.InvocationId, completed.InvocationId);
                }

                Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
                Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress &&
                    evt.NodeId == "join"));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_ScopeParallelMcpArgumentsAndResultsToEachActivation(bool sqlite)
    {
        const string input = "[\"north\",\"south\"]";
        var definition = ParallelMcpDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var connections = definition.Connections;
        var nodes = definition.Nodes;
        foreach (var toolId in new[] { "tool-research", "tool-review" })
        {
            var inputConnectionId = $"input-{toolId}";
            nodes = nodes.SetItem(toolId, nodes[toolId] with
            {
                McpArgumentsJson = null,
                McpInputPorts = [new("arg.query", "query", WorkflowDataTypes.String, true)]
            });
            connections = connections.SetItem(inputConnectionId,
                new(new("input", "out"), new(toolId, "arg.query"), "data"));
        }

        definition = definition with { Nodes = nodes, Connections = connections };
        WorkflowValidator.ThrowIfInvalid(definition);
        var revision = WorkflowRevision.Create(definition, "array-iteration-parallel-mcp-activation-scope-proof");
        using var structuredStore = new ProofRunStore(revision, input, sqlite);
        using var subworkflowStore = new ProofRunStore(revision, input, sqlite);
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);

        var structured = await new WorkflowRunner(new EchoToolBroker(recordToolDispatchEvents: true))
            .ExecuteStructuredActivationSequenceProofAsync(structuredStore.RunId, revision, input, "index", null,
                evt =>
                {
                    structuredRecorder.Append(evt);
                    return ValueTask.CompletedTask;
                });

        var subworkflowIterator = new PerItemWorkflowExecutor(subworkflowStore.RunId, revision,
            subworkflowRecorder, new EchoAgentExecutor(), new EchoToolBroker(recordToolDispatchEvents: true));
        var subworkflow = new WorkflowBuilder(subworkflowIterator).WithOutputFrom(subworkflowIterator).Build();
        string? subworkflowOutput = null;
        await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(subworkflow, input))
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowOutputEvent { Data: string output }) subworkflowOutput = output;
        subworkflowRecorder.CompleteRoot();

        var hybridOutput = await RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            new EchoAgentExecutor(), new EchoToolBroker(recordToolDispatchEvents: true), "array-loop",
            ["north", "south"], null, "index", [],
            async frame =>
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner(new EchoToolBroker(recordToolDispatchEvents: true))
                    .ExecuteWithOutcomeAsync(hybridStore.RunId, revision, frame.Item, frame.Parameters, Record);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The parallel MCP hybrid body did not return a result.");
                return new ActivationLoopProofValue(result.Output);
            });
        hybridRecorder.CompleteRoot();

        Assert.False(structured.Failed);
        Assert.Equal(structured.Output, subworkflowOutput);
        Assert.Equal(structured.Output, hybridOutput);
        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "completed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "completed");
        AssertPersistedSequence(hybridStore, hybridEvents, "completed");
        Assert.Equal(ArgumentEvaluationEvidence(structuredEvents), ArgumentEvaluationEvidence(subworkflowEvents));
        Assert.Equal(ArgumentEvaluationEvidence(structuredEvents), ArgumentEvaluationEvidence(hybridEvents));
        Assert.Equal(ToolOutcomeEvidence(structuredEvents), ToolOutcomeEvidence(subworkflowEvents));
        Assert.Equal(ToolOutcomeEvidence(structuredEvents), ToolOutcomeEvidence(hybridEvents));
        AssertActivationScopedArgumentsAndResults(structuredEvents);
        AssertActivationScopedArgumentsAndResults(subworkflowEvents);
        AssertActivationScopedArgumentsAndResults(hybridEvents);

        static (string? NodeId, string? ScopeId, string? ConnectionId, string? Data)[] ArgumentEvaluationEvidence(
            IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
                evt.NodeId == "input" &&
                evt.ConnectionId is "data:input-tool-research" or "data:input-tool-review")
            .Select(evt => (evt.NodeId, evt.ScopeId, evt.ConnectionId, evt.Data))
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.ConnectionId, StringComparer.Ordinal)
            .ToArray();

        static (string? NodeId, string? ScopeId, string? Output)[] ToolOutcomeEvidence(
            IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind == ExecutionEventKinds.ToolCompleted &&
                evt.NodeId is "tool-research" or "tool-review")
            .Select(evt =>
            {
                using var payload = JsonDocument.Parse(evt.Data!);
                return (evt.NodeId, evt.ScopeId, payload.RootElement.GetProperty("output").GetString());
            })
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.NodeId, StringComparer.Ordinal)
            .ToArray();

        static void AssertActivationScopedArgumentsAndResults(IReadOnlyList<ExecutionEvent> events)
        {
            var expectedItems = new[] { "north", "south" };
            for (var index = 0; index < expectedItems.Length; index++)
            {
                var scopeId = $"array-loop/{index}";
                foreach (var (toolId, connectionId) in new[]
                {
                    ("tool-research", "data:input-tool-research"),
                    ("tool-review", "data:input-tool-review")
                })
                {
                    var evaluated = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
                        evt.NodeId == "input" && evt.ScopeId == scopeId && evt.ConnectionId == connectionId);
                    using var argument = JsonDocument.Parse(evaluated.Data!);
                    Assert.Equal(toolId, argument.RootElement.GetProperty("activatedByNodeId").GetString());
                    Assert.Equal(expectedItems[index], argument.RootElement.GetProperty("value").GetString());

                    var completed = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted &&
                        evt.NodeId == toolId && evt.ScopeId == scopeId);
                    using var outcome = JsonDocument.Parse(completed.Data!);
                    Assert.Equal("echo:" + expectedItems[index],
                        outcome.RootElement.GetProperty("output").GetString());
                }

                var join = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                    evt.NodeId == "join" && evt.ScopeId == scopeId);
                using var joined = JsonDocument.Parse(join.Data!);
                Assert.Equal("echo:" + expectedItems[index], joined.RootElement.GetProperty("left").GetString());
                Assert.Equal("echo:" + expectedItems[index], joined.RootElement.GetProperty("right").GetString());
            }
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
    public async Task IteratorCandidates_CancelParallelSiblingAfterFailureAcrossAllCandidates(bool sqlite)
    {
        var definition = ParallelBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-three-candidate-parallel-failure-proof");
        const string input = "[\"north\",\"must-not-run\"]";

        var structured = await RunCandidateAsync("structured");
        var subworkflow = await RunCandidateAsync("fresh-subworkflow");
        var hybrid = await RunCandidateAsync("hybrid");

        foreach (var candidate in new[] { structured, subworkflow, hybrid })
        {
            var events = candidate.Events;
            Assert.Equal(["agent-left", "agent-right"], candidate.EnteredNodes.Order(StringComparer.Ordinal));
            AssertPersistedFailure(events);

            const string scopeId = "array-loop/0";
            var leftStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == "agent-left" && evt.ScopeId == scopeId);
            var leftFailed = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId == "agent-left" && evt.ScopeId == scopeId);
            var rightStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == "agent-right" && evt.ScopeId == scopeId);
            var rightCancelled = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
                evt.NodeId == "agent-right" && evt.ScopeId == scopeId);
            Assert.Equal(leftStarted.InvocationId, leftFailed.InvocationId);
            Assert.Equal(rightStarted.InvocationId, rightCancelled.InvocationId);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress && evt.NodeId == "join");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/1");
        }

        Assert.Equal(BranchFailureShape(structured.Events), BranchFailureShape(subworkflow.Events));
        Assert.Equal(BranchFailureShape(structured.Events), BranchFailureShape(hybrid.Events));

        async Task<(ExecutionEvent[] Events, string[] EnteredNodes)> RunCandidateAsync(string candidate)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ProofRecorder(store, startRoot: candidate != "structured");
            var agents = new CoordinatedBranchFailureAgentExecutor();
            var runner = new WorkflowRunner(agents: agents);

            if (candidate == "structured")
            {
                var failure = await Record.ExceptionAsync(() => runner
                    .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                        evt => { recorder.Append(evt); return ValueTask.CompletedTask; }));
                Assert.NotNull(failure);
            }
            else if (candidate == "fresh-subworkflow")
            {
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder, agents,
                    new EchoToolBroker());
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var workflowError = false;
                await using (var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input))
                    await foreach (var evt in run.WatchStreamAsync()) workflowError |= evt is WorkflowErrorEvent;
                Assert.True(workflowError);
                recorder.FailRoot();
            }
            else
            {
                var failure = await Record.ExceptionAsync(() => RunActivationLoopProofAsync(store.RunId,
                    revision, recorder, agents, new EchoToolBroker(), "array-loop", ["north", "must-not-run"],
                    null, "index", [], async frame =>
                    {
                        ValueTask Record(ExecutionEvent evt)
                        {
                            if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                                ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                                return ValueTask.CompletedTask;
                            recorder.Append(evt with { ScopeId = frame.ScopeId });
                            return ValueTask.CompletedTask;
                        }

                        var result = await new WorkflowRunner(agents: agents).ExecuteWithOutcomeAsync(store.RunId,
                            revision, frame.Item, frame.Parameters, Record);
                        if (result.Failed || result.Output is null)
                            throw new InvalidOperationException("The hybrid parallel body failed.");
                        return new ActivationLoopProofValue(result.Output);
                    }));
                Assert.NotNull(failure);
                recorder.FailRoot();
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(store, events, "failed");
            return (events, agents.EnteredNodes);
        }

        static void AssertPersistedFailure(IReadOnlyList<ExecutionEvent> events)
        {
            Assert.Equal(["array-loop/0"], events
                .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
                .Select(evt => evt.ScopeId));
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.ScopeId == "array-loop/0");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        }

        static (string Kind, string? NodeId, string? ScopeId, string? ConnectionId)[] BranchFailureShape(
            IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.NodeId is "agent-left" or "agent-right" or "join" &&
                evt.Kind is ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeFailed or
                    ExecutionEventKinds.NodeCancelled or ExecutionEventKinds.NodeCompleted or
                    ExecutionEventKinds.ParallelJoinProgress)
            .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId))
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.Kind, StringComparer.Ordinal)
            .ThenBy(evt => evt.NodeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.ConnectionId, StringComparer.Ordinal)
            .ToArray();
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
    public async Task IteratorCandidates_CancelAfterOneJoinBranchCompletes(bool sqlite)
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
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
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

        using var hybridCancellation = new CancellationTokenSource();
        var hybridAgents = new OneBranchCompletedThenBlockedAgentExecutor();
        IReadOnlyList<ExecutionEvent>? hybridEventsAtPause = null;
        var hybridTask = RunActivationLoopProofAsync(hybridStore.RunId, revision, hybridRecorder,
            hybridAgents, new EchoToolBroker(), "array-loop", ["north", "must-not-run"], null,
            "index", [], async frame =>
            {
                async ValueTask RecordHybrid(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent-right")
                    {
                        hybridEventsAtPause = hybridRecorder.Snapshot();
                        hybridAgents.RightCompleted.TrySetResult();
                        await hybridAgents.ReleaseRight.Task;
                    }
                }

                await new WorkflowRunner(agents: hybridAgents).ExecuteWithOutcomeAsync(hybridStore.RunId,
                    revision, frame.Item, frame.Parameters, RecordHybrid, hybridCancellation.Token);
                return new ActivationLoopProofValue("unexpected completion");
            });
        await hybridAgents.RightCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertCandidateAdmissionAtPause(hybridEventsAtPause!, hybridAgents.LeftEntered, "hybrid");
        hybridCancellation.Cancel();
        hybridAgents.ReleaseRight.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hybridTask);
        hybridRecorder.CancelRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "cancelled");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "cancelled");
        AssertPersistedSequence(hybridStore, hybridEvents, "cancelled");
        AssertCancelledJoinPrefix(structuredEvents, "structured");
        AssertCancelledJoinPrefix(subworkflowEvents, "fresh-subworkflow");
        AssertCancelledJoinPrefix(hybridEvents, "hybrid");
        Assert.Equal(BranchLifecycleShape(subworkflowEvents), BranchLifecycleShape(hybridEvents));

        static void AssertCandidateAdmissionAtPause(IReadOnlyList<ExecutionEvent> events,
            TaskCompletionSource leftHandlerEntered, string candidate)
        {
            const string scopeId = "array-loop/0";
            var trace = string.Join(" | ", events.Where(evt => evt.ScopeId == scopeId &&
                evt.NodeId is "anchor-left" or "agent-left" or "agent-right")
                .Select(evt => $"{evt.Sequence}:{evt.Kind}:{evt.NodeId}"));
            var expectedSiblingAdmission = candidate != "structured";
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
            if (candidate is "fresh-subworkflow" or "hybrid")
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

        static (string Kind, string? NodeId, string? ScopeId, string? ConnectionId)[] BranchLifecycleShape(
            IReadOnlyList<ExecutionEvent> events) => events
            .Where(evt => evt.NodeId is "agent-left" or "agent-right" or "join" &&
                evt.Kind is ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeCompleted or
                    ExecutionEventKinds.NodeCancelled or ExecutionEventKinds.ParallelJoinProgress)
            .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId, evt.ConnectionId))
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.Kind, StringComparer.Ordinal)
            .ThenBy(evt => evt.NodeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.ConnectionId, StringComparer.Ordinal)
            .ToArray();
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
    public async Task IteratorCandidates_CancelSiblingBeforeNodeAdmission(bool sqlite)
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
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredGate = new CoordinatedPreAdmissionGate();
        var subworkflowGate = new CoordinatedPreAdmissionGate();
        var hybridGate = new CoordinatedPreAdmissionGate();
        var structuredAgents = new CoordinatedGateFailureAgentExecutor(structuredGate);
        var subworkflowAgents = new CoordinatedGateFailureAgentExecutor(subworkflowGate);
        var hybridAgents = new CoordinatedGateFailureAgentExecutor(hybridGate);

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

        var hybridFailure = await Record.ExceptionAsync(() => RunActivationLoopProofAsync(hybridStore.RunId,
            revision, hybridRecorder, hybridAgents, new EchoToolBroker(), "array-loop",
            ["north", "south"], null, "index", [], async frame =>
            {
                ValueTask RecordHybrid(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner(agents: hybridAgents)
                    .ExecuteControlledWithParametersAndOutcomeAsync(hybridStore.RunId, revision,
                        frame.Item, frame.Parameters, RecordHybrid, hybridGate);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The hybrid parallel body failed.");
                return new ActivationLoopProofValue(result.Output);
            }));
        Assert.NotNull(hybridFailure);
        hybridRecorder.FailRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "failed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "failed");
        AssertPersistedSequence(hybridStore, hybridEvents, "failed");
        Assert.True(structuredGate.RightReached.Task.IsCompletedSuccessfully);
        Assert.True(subworkflowGate.RightReached.Task.IsCompletedSuccessfully);
        Assert.True(hybridGate.RightReached.Task.IsCompletedSuccessfully);
        Assert.Equal(["agent-left"], structuredAgents.EnteredNodes);
        Assert.Equal(structuredAgents.EnteredNodes, subworkflowAgents.EnteredNodes);
        Assert.Equal(structuredAgents.EnteredNodes, hybridAgents.EnteredNodes);
        AssertPreStartSiblingCancellation(structuredEvents);
        AssertPreStartSiblingCancellation(subworkflowEvents);
        AssertPreStartSiblingCancellation(hybridEvents);
        Assert.Equal(subworkflowEvents.Where(IsBranchLifecycleEvent).Select(EventShape),
            structuredEvents.Where(IsBranchLifecycleEvent).Select(EventShape));
        Assert.Equal(subworkflowEvents.Where(IsBranchLifecycleEvent).Select(EventShape),
            hybridEvents.Where(IsBranchLifecycleEvent).Select(EventShape));

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
    public async Task IteratorCandidates_CancelAdmittedSiblingBeforeAgentHandler(bool sqlite)
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
        using var hybridStore = new ProofRunStore(revision, input, sqlite);
        var structuredRecorder = new ProofRecorder(structuredStore, startRoot: false);
        var subworkflowRecorder = new ProofRecorder(subworkflowStore);
        var hybridRecorder = new ProofRecorder(hybridStore);
        var structuredGate = new CoordinatedPostStartGate();
        var subworkflowGate = new CoordinatedPostStartGate();
        var hybridGate = new CoordinatedPostStartGate();
        var structuredAgents = new CoordinatedPostStartFailureAgentExecutor(structuredGate);
        var subworkflowAgents = new CoordinatedPostStartFailureAgentExecutor(subworkflowGate);
        var hybridAgents = new CoordinatedPostStartFailureAgentExecutor(hybridGate);

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

        var hybridFailure = await Record.ExceptionAsync(() => RunActivationLoopProofAsync(hybridStore.RunId,
            revision, hybridRecorder, hybridAgents, new EchoToolBroker(), "array-loop",
            ["north", "south"], null, "index", [], async frame =>
            {
                ValueTask RecordHybrid(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    hybridRecorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var result = await new WorkflowRunner(agents: hybridAgents).ExecuteAfterNodeStartGateProofAsync(
                    hybridStore.RunId, revision, frame.Item, frame.Parameters, RecordHybrid, hybridGate.WaitAsync);
                if (result.Failed || result.Output is null)
                    throw new InvalidOperationException("The hybrid parallel body failed.");
                return new ActivationLoopProofValue(result.Output);
            }));
        Assert.NotNull(hybridFailure);
        hybridRecorder.FailRoot();

        var structuredEvents = structuredRecorder.Snapshot();
        var subworkflowEvents = subworkflowRecorder.Snapshot();
        var hybridEvents = hybridRecorder.Snapshot();
        AssertPersistedSequence(structuredStore, structuredEvents, "failed");
        AssertPersistedSequence(subworkflowStore, subworkflowEvents, "failed");
        AssertPersistedSequence(hybridStore, hybridEvents, "failed");
        Assert.True(structuredGate.RightNodeStarted.Task.IsCompletedSuccessfully);
        Assert.True(subworkflowGate.RightNodeStarted.Task.IsCompletedSuccessfully);
        Assert.True(hybridGate.RightNodeStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(["agent-left"], structuredAgents.EnteredNodes);
        Assert.Equal(structuredAgents.EnteredNodes, subworkflowAgents.EnteredNodes);
        Assert.Equal(structuredAgents.EnteredNodes, hybridAgents.EnteredNodes);
        AssertAdmittedSiblingCancellation(structuredEvents);
        AssertAdmittedSiblingCancellation(subworkflowEvents);
        AssertAdmittedSiblingCancellation(hybridEvents);
        Assert.Equal(subworkflowEvents.Where(IsBranchLifecycleEvent).Select(EventShape),
            structuredEvents.Where(IsBranchLifecycleEvent).Select(EventShape));
        Assert.Equal(subworkflowEvents.Where(IsBranchLifecycleEvent).Select(EventShape),
            hybridEvents.Where(IsBranchLifecycleEvent).Select(EventShape));

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
        var tools = new EchoToolBroker(query => $"echo:{query}");
        var breakDispatches = new ConcurrentQueue<LoopProofBreakDispatch>();
        var proofGraph = new LoopControlProofGraph();
        var proofGraphDispatches = new ConcurrentQueue<(string NodeId, string ScopeId)>();

        var output = await RunActivationLoopProofAsync(runStore.RunId, revision, recorder, agents, tools,
            "outer-loop", ["outer-a", "outer-b"], parentFrame: null, indexParameterId: "outerIndex",
            breakDispatches, async outerFrame =>
            {
                var innerOutput = await RunActivationLoopProofAsync(runStore.RunId, revision, recorder, agents,
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
                        var output = body.Output ?? throw new InvalidOperationException("The nested body had no output.");
                        return proofGraph.Execute(output, innerFrame, proofGraphDispatches);
                    });
                return new ActivationLoopProofValue($"outer-{outerFrame.Index}:{innerOutput}");
            });
        recorder.CompleteRoot();

        Assert.Equal("outer-0:echo:CONTINUE:0/0;outer-1:echo:CONTINUE:0/1", output);
        Assert.Equal(["CONTINUE:0/0", "BREAK:1/0", "CONTINUE:0/1", "BREAK:1/1"], agents.Inputs);
        Assert.Equal(agents.Inputs, tools.Queries);
        var dispatches = breakDispatches.ToArray();
        Assert.Equal(2, dispatches.Length);
        Assert.All(dispatches, dispatch => Assert.Equal("break", dispatch.SourceNodeId));
        Assert.All(dispatches, dispatch => Assert.Equal(dispatch.SourceFrameId, dispatch.TargetFrameId));
        Assert.Equal(["outer-loop/0/inner-loop/1", "outer-loop/1/inner-loop/1"],
            dispatches.Select(dispatch => dispatch.ScopeId));
        Assert.Equal(
            ["outer-loop/0/inner-loop/0", "outer-loop/0/inner-loop/1", "outer-loop/1/inner-loop/0", "outer-loop/1/inner-loop/1"],
            proofGraphDispatches.Where(dispatch => dispatch.NodeId == "break-condition")
                .Select(dispatch => dispatch.ScopeId));
        Assert.Equal(dispatches.Select(dispatch => dispatch.ScopeId),
            proofGraphDispatches.Where(dispatch => dispatch.NodeId == "break")
                .Select(dispatch => dispatch.ScopeId));

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
    public async Task StructuredNodeDispatcher_BreakStopsNearestNestedLoopAndOuterLoopContinues(bool sqlite)
    {
        var revision = WorkflowRevision.Create(NestedBodyDefinition(),
            "array-iteration-structured-nested-break-proof");
        const string input = "[\"outer-a\",\"outer-b\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker(query => $"echo:{query}");
        var breakDispatches = new ConcurrentQueue<LoopProofBreakDispatch>();
        var proofGraph = new LoopControlProofGraph();
        var proofGraphDispatches = new ConcurrentQueue<(string NodeId, string ScopeId)>();

        var output = await RunActivationLoopProofAsync(runStore.RunId, revision, recorder, agents, tools,
            "outer-loop", ["outer-a", "outer-b"], parentFrame: null, indexParameterId: "outerIndex",
            breakDispatches, async outerFrame =>
            {
                var innerOutput = await RunActivationLoopProofAsync(runStore.RunId, revision, recorder, agents,
                    tools, "inner-loop", ["continue", "break", "must-not-run"], outerFrame, "index",
                    breakDispatches, async innerFrame =>
                    {
                        ValueTask Record(ExecutionEvent evt)
                        {
                            if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                                ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or
                                ExecutionEventKinds.Terminated)
                                return ValueTask.CompletedTask;
                            recorder.Append(evt with { ScopeId = evt.ScopeId ?? innerFrame.ScopeId });
                            return ValueTask.CompletedTask;
                        }

                        var result = await new WorkflowRunner(tools, agents)
                            .ExecuteStructuredSchedulerProofAsync(runStore.RunId, revision, innerFrame.Item,
                                innerFrame.Parameters, Record, activationScopeId: innerFrame.ScopeId,
                                emitRunLifecycle: false);
                        if (result.Failed || result.Output is null)
                            throw new InvalidOperationException("The structured body did not return a result.");
                        return proofGraph.Execute(result.Output, innerFrame, proofGraphDispatches);
                    });
                return new ActivationLoopProofValue($"outer-{outerFrame.Index}:{innerOutput}");
            });
        recorder.CompleteRoot();

        Assert.Equal("outer-0:echo:CONTINUE:0/0;outer-1:echo:CONTINUE:0/1", output);
        Assert.Equal(["CONTINUE:0/0", "BREAK:1/0", "CONTINUE:0/1", "BREAK:1/1"], agents.Inputs);
        Assert.Equal(agents.Inputs, tools.Queries);
        Assert.Equal(2, breakDispatches.Count);
        Assert.All(breakDispatches, dispatch => Assert.Equal(dispatch.SourceFrameId, dispatch.TargetFrameId));
        Assert.Equal(["outer-loop/0/inner-loop/1", "outer-loop/1/inner-loop/1"],
            breakDispatches.Select(dispatch => dispatch.ScopeId));
        Assert.Equal(
            ["outer-loop/0/inner-loop/0", "outer-loop/0/inner-loop/1", "outer-loop/1/inner-loop/0", "outer-loop/1/inner-loop/1"],
            proofGraphDispatches.Where(dispatch => dispatch.NodeId == "break-condition")
                .Select(dispatch => dispatch.ScopeId));
        Assert.Equal(breakDispatches.Select(dispatch => dispatch.ScopeId),
            proofGraphDispatches.Where(dispatch => dispatch.NodeId == "break")
                .Select(dispatch => dispatch.ScopeId));

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
    public async Task FreshSubworkflowBody_BreakStopsNearestNestedLoopAndOuterLoopContinues(bool sqlite)
    {
        var revision = WorkflowRevision.Create(NestedBodyDefinition(),
            "array-iteration-subworkflow-nested-break-proof");
        const string input = "[\"outer-a\",\"outer-b\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker(query => $"echo:{query}");
        var breakDispatches = new ConcurrentQueue<LoopProofBreakDispatch>();
        var proofGraph = new LoopControlProofGraph();
        var proofGraphDispatches = new ConcurrentQueue<(string NodeId, string ScopeId)>();
        var childWorkflows = new ConcurrentBag<Workflow>();

        var output = await RunFreshSubworkflowLoopProofAsync(runStore.RunId, revision, recorder,
            "outer-loop", ["outer-a", "outer-b"], parentFrame: null, indexParameterId: "outerIndex",
            breakDispatches, async outerFrame =>
            {
                var innerOutput = await RunFreshSubworkflowLoopProofAsync(runStore.RunId, revision, recorder,
                    "inner-loop", ["continue", "break", "must-not-run"], outerFrame, "index",
                    breakDispatches, async innerFrame =>
                    {
                        var (child, workflow) = BuildFreshVawBodyWorkflow();
                        childWorkflows.Add(child);
                        var bodyInput = new FreshVawBodyInput(async cancellationToken =>
                        {
                            ValueTask Record(ExecutionEvent evt)
                            {
                                if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                                    ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or
                                    ExecutionEventKinds.Terminated)
                                    return ValueTask.CompletedTask;
                                recorder.Append(evt with { ScopeId = evt.ScopeId ?? innerFrame.ScopeId });
                                return ValueTask.CompletedTask;
                            }

                            var body = await new WorkflowRunner(tools, agents).ExecuteWithOutcomeAsync(
                                runStore.RunId, revision, innerFrame.Item, innerFrame.Parameters, Record,
                                cancellationToken);
                            if (body.Failed || body.Output is null)
                                throw new InvalidOperationException("The subworkflow body did not return a result.");
                            return proofGraph.Execute(body.Output, innerFrame, proofGraphDispatches);
                        });
                        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow,
                            bodyInput);
                        LoopProofResult? result = null;
                        await foreach (var evt in run.WatchStreamAsync())
                            if (evt is WorkflowOutputEvent { Data: LoopProofResult proofResult }) result = proofResult;
                        return result ?? throw new InvalidOperationException("The child subworkflow returned no result.");
                    });
                return new ActivationLoopProofValue($"outer-{outerFrame.Index}:{innerOutput}");
            });
        recorder.CompleteRoot();

        Assert.Equal("outer-0:echo:CONTINUE:0/0;outer-1:echo:CONTINUE:0/1", output);
        Assert.Equal(["CONTINUE:0/0", "BREAK:1/0", "CONTINUE:0/1", "BREAK:1/1"], agents.Inputs);
        Assert.Equal(agents.Inputs, tools.Queries);
        Assert.Equal(4, childWorkflows.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(2, breakDispatches.Count);
        Assert.All(breakDispatches, dispatch => Assert.Equal(dispatch.SourceFrameId, dispatch.TargetFrameId));
        Assert.Equal(["outer-loop/0/inner-loop/1", "outer-loop/1/inner-loop/1"],
            breakDispatches.Select(dispatch => dispatch.ScopeId));

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
    public async Task IteratorCandidates_MatchTypedNestedBreakContract(bool sqlite)
    {
        var revision = WorkflowRevision.Create(NestedBodyDefinition(),
            "array-iteration-matched-nested-break-proof");
        const string input = "[\"outer-a\",\"outer-b\"]";

        var structured = await RunCandidateAsync(useStructuredDispatcher: true,
            useFreshSubworkflow: false);
        var subworkflow = await RunCandidateAsync(useStructuredDispatcher: false,
            useFreshSubworkflow: true);
        var hybrid = await RunCandidateAsync(useStructuredDispatcher: false,
            useFreshSubworkflow: false);

        Assert.Equal("outer-0:echo:CONTINUE:0/0;outer-1:echo:CONTINUE:0/1", structured.Output);
        Assert.Equal(structured.Output, subworkflow.Output);
        Assert.Equal(structured.Output, hybrid.Output);
        Assert.Equal(["CONTINUE:0/0", "BREAK:1/0", "CONTINUE:0/1", "BREAK:1/1"], structured.AgentInputs);
        Assert.Equal(structured.AgentInputs, subworkflow.AgentInputs);
        Assert.Equal(structured.AgentInputs, hybrid.AgentInputs);
        Assert.Equal(structured.ToolQueries, subworkflow.ToolQueries);
        Assert.Equal(structured.ToolQueries, hybrid.ToolQueries);
        Assert.Equal(["outer-loop/0/inner-loop/1", "outer-loop/1/inner-loop/1"], structured.BreakScopes);
        Assert.Equal(structured.BreakScopes, subworkflow.BreakScopes);
        Assert.Equal(structured.BreakScopes, hybrid.BreakScopes);
        Assert.Equal(structured.IteratorPinDispatches, subworkflow.IteratorPinDispatches);
        Assert.Equal(structured.IteratorPinDispatches, hybrid.IteratorPinDispatches);
        Assert.Equal(
            [
                ("outer-loop/0/inner-loop/0", "element", "continue", "workflow-input", "value", "string"),
                ("outer-loop/0/inner-loop/0", "index", "0", "workflow-parameter", "index", "integer"),
                ("outer-loop/0/inner-loop/1", "element", "break", "workflow-input", "value", "string"),
                ("outer-loop/0/inner-loop/1", "index", "1", "workflow-parameter", "index", "integer"),
                ("outer-loop/1/inner-loop/0", "element", "continue", "workflow-input", "value", "string"),
                ("outer-loop/1/inner-loop/0", "index", "0", "workflow-parameter", "index", "integer"),
                ("outer-loop/1/inner-loop/1", "element", "break", "workflow-input", "value", "string"),
                ("outer-loop/1/inner-loop/1", "index", "1", "workflow-parameter", "index", "integer")
            ],
            structured.IteratorPinDispatches.Select(pin => (pin.ScopeId, pin.SourcePinId, pin.Value,
                pin.TargetNodeId, pin.TargetPinId, pin.DataType)).ToArray());
        Assert.All(structured.IteratorPinDispatches, pin =>
        {
            Assert.Equal("iterator", pin.SourceNodeId);
            Assert.Equal("array<string>", pin.InputArrayDataType);
        });
        Assert.Equal(structured.GraphDispatches, subworkflow.GraphDispatches);
        Assert.Equal(structured.GraphDispatches, hybrid.GraphDispatches);
        Assert.Equal(EventShape(structured.Events), EventShape(subworkflow.Events));
        Assert.Equal(EventShape(structured.Events), EventShape(hybrid.Events));
        Assert.Equal(4, subworkflow.ChildWorkflows.Length);
        Assert.Equal(4, subworkflow.ChildWorkflows.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Empty(structured.ChildWorkflows);
        Assert.Empty(hybrid.ChildWorkflows);

        static (string Kind, string? NodeId, string? ScopeId)[] EventShape(IEnumerable<ExecutionEvent> events) =>
            events.Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
                    ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.NodeStarted or
                    ExecutionEventKinds.NodeCompleted)
                .Select(evt => (evt.Kind, evt.NodeId, evt.ScopeId)).ToArray();

        async Task<MatchedBreakCandidateEvidence> RunCandidateAsync(bool useStructuredDispatcher,
            bool useFreshSubworkflow)
        {
            using var runStore = new ProofRunStore(revision, input, sqlite);
            var recorder = new ProofRecorder(runStore);
            var agents = new EchoAgentExecutor();
            var tools = new EchoToolBroker(query => $"echo:{query}");
            var proofGraph = new LoopControlProofGraph();
            var graphDispatches = new ConcurrentQueue<(string NodeId, string ScopeId)>();
            var iteratorPinDispatches = new ConcurrentQueue<LoopProofIteratorPinDispatch>();
            var breakDispatches = new ConcurrentQueue<LoopProofBreakDispatch>();
            var childWorkflows = new ConcurrentBag<Workflow>();

            async Task<LoopProofResult> ExecuteBodyAsync(WorkflowRunner.StructuredActivationFrame frame)
            {
                var pins = new LoopArrayIteratorProofNode().Bind(frame, WorkflowDataTypes.String);
                iteratorPinDispatches.Enqueue(pins.Element);
                iteratorPinDispatches.Enqueue(pins.Index);
                var bodyParameters = frame.Parameters.SetItem("index", pins.Index.Value);
                var bodyFrame = frame with { Item = pins.Element.Value, Parameters = bodyParameters };
                if (useFreshSubworkflow)
                {
                    var (child, workflow) = BuildFreshVawBodyWorkflow();
                    childWorkflows.Add(child);
                    var bodyInput = new FreshVawBodyInput(token => ExecuteVawBodyAsync(bodyFrame,
                        structuredDispatcher: false, cancellationToken: token));
                    await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, bodyInput);
                    LoopProofResult? output = null;
                    await foreach (var evt in run.WatchStreamAsync())
                        if (evt is WorkflowOutputEvent { Data: LoopProofResult result }) output = result;
                    return output ?? throw new InvalidOperationException("The fresh body workflow returned no control result.");
                }

                return await ExecuteVawBodyAsync(bodyFrame, useStructuredDispatcher, CancellationToken.None);
            }

            async Task<LoopProofResult> ExecuteVawBodyAsync(WorkflowRunner.StructuredActivationFrame frame,
                bool structuredDispatcher, CancellationToken cancellationToken)
            {
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    recorder.Append(evt with { ScopeId = evt.ScopeId ?? frame.ScopeId });
                    return ValueTask.CompletedTask;
                }

                var runner = new WorkflowRunner(tools, agents);
                string bodyOutput;
                if (structuredDispatcher)
                {
                    var result = await runner.ExecuteStructuredSchedulerProofAsync(runStore.RunId, revision,
                        frame.Item, frame.Parameters, Record, cancellationToken,
                        activationScopeId: frame.ScopeId, emitRunLifecycle: false);
                    if (result.Failed || result.Output is null)
                        throw new InvalidOperationException("The structured body did not return an output.");
                    bodyOutput = result.Output;
                }
                else
                {
                    var result = await runner.ExecuteWithOutcomeAsync(runStore.RunId, revision,
                        frame.Item, frame.Parameters, Record, cancellationToken);
                    if (result.Failed || result.Output is null)
                        throw new InvalidOperationException("The VAW body did not return an output.");
                    bodyOutput = result.Output;
                }

                return proofGraph.Execute(bodyOutput, frame, graphDispatches);
            }

            async Task<string> RunLoopAsync(string loopNodeId, IReadOnlyList<string> items,
                WorkflowRunner.StructuredActivationFrame? parentFrame, string indexParameterId)
            {
                async Task<LoopProofResult> ExecuteActivationAsync(WorkflowRunner.StructuredActivationFrame frame)
                {
                    if (loopNodeId != "outer-loop") return await ExecuteBodyAsync(frame);
                    var innerOutput = await RunLoopAsync("inner-loop",
                        ["continue", "break", "must-not-run"], frame, "index");
                    return new ActivationLoopProofValue($"outer-{frame.Index}:{innerOutput}");
                }

                Func<WorkflowRunner.StructuredActivationFrame, Task<LoopProofResult>> body = ExecuteActivationAsync;
                if (useFreshSubworkflow)
                    return await RunFreshSubworkflowLoopProofAsync(runStore.RunId, revision, recorder,
                        loopNodeId, items, parentFrame, indexParameterId, breakDispatches, body);
                return await RunActivationLoopProofAsync(runStore.RunId, revision, recorder, agents, tools,
                    loopNodeId, items, parentFrame, indexParameterId, breakDispatches, body);
            }

            var output = await RunLoopAsync("outer-loop", ["outer-a", "outer-b"], null, "outerIndex");
            recorder.CompleteRoot();
            var events = recorder.Snapshot();
            AssertPersistedSequence(runStore, events, "completed");
            Assert.DoesNotContain(events, evt => evt.ScopeId is
                "outer-loop/0/inner-loop/2" or "outer-loop/1/inner-loop/2");
            return new MatchedBreakCandidateEvidence(output, events, agents.Inputs, tools.Queries,
                breakDispatches.Select(item => item.ScopeId).ToArray(), graphDispatches.ToArray(),
                iteratorPinDispatches.ToArray(), childWorkflows.ToArray());
        }
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
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task StructuredActivationRunContext_ReservesSharedModelAndToolOutcomes(bool sqlite,
        bool exhaustModelLimit)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-structured-run-context-budget-proof");
        const string input = "[\"first\",\"second\"]";
        using var store = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(store, startRoot: false);
        var model = new SharedBudgetProofModel();
        var tools = new EchoToolBroker(recordToolDispatchEvents: true);
        var provider = new AgentProviderDescriptor("proof.echo-agent", "Proof model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new SharedBudgetProofChatClient(model))],
            new EchoToolBroker());

        await Assert.ThrowsAsync<WorkflowRunner.StructuredActivationBudgetExceededException>(async () =>
            await new WorkflowRunner(tools, agents).ExecuteStructuredActivationSequenceProofAsync(
                store.RunId, revision, input, "index", null, evt =>
                {
                    recorder.Append(evt);
                    return ValueTask.CompletedTask;
                }, runBudget: new WorkflowRunner.StructuredActivationRunBudget(
                    MaximumEvents: 512,
                    MaximumBytes: 4_000_000,
                    ReservedFailureEvents: 8,
                    ReservedFailureBytes: 64_000,
                    MaximumOutcomeBytes: 64_000,
                    MaximumConcurrentOutcomeReservations: 1,
                    MaximumModelDispatches: exhaustModelLimit ? 1 : 2,
                    MaximumToolDispatches: exhaustModelLimit ? 2 : 1)));

        var events = recorder.Snapshot();
        AssertPersistedSequence(store, events, "failed");
        Assert.Equal(exhaustModelLimit ? 1 : 2, model.Calls);
        Assert.Equal(["bounded"], tools.Queries);
        Assert.Equal(exhaustModelLimit ? 1 : 2, events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested));
        Assert.Equal(exhaustModelLimit ? 1 : 2, events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded));
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized));
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.ToolCompleted));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
            evt.ScopeId == "array-loop/1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredActivationRunContext_AtomicallyLimitsParallelModelReservations(bool sqlite)
    {
        const string providerId = "proof.structured-parallel-budget";
        var definition = ParallelBodyDefinition();
        var nodes = definition.Nodes;
        foreach (var nodeId in new[] { "agent-left", "agent-right" })
            nodes = nodes.SetItem(nodeId, nodes[nodeId] with
            {
                Agent = nodes[nodeId].Agent! with { ProviderId = providerId }
            });
        definition = definition with
        {
            Nodes = nodes,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition, "array-iteration-structured-parallel-budget-proof");
        const string input = "[\"one-item\"]";
        using var store = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(store, startRoot: false);
        var reservationRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ReservationRaceModel(reservationRejected.Task);
        var provider = new AgentProviderDescriptor(providerId, "Proof model", "proof-chat-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => new ReservationRaceChatClient(model))],
            new EchoToolBroker());

        await Assert.ThrowsAsync<WorkflowRunner.StructuredActivationBudgetExceededException>(async () =>
            await new WorkflowRunner(agents: agents).ExecuteStructuredActivationSequenceProofAsync(
                store.RunId, revision, input, "index", null, evt =>
                {
                    recorder.Append(evt);
                    if (evt.Kind == ExecutionEventKinds.AgentFailed) reservationRejected.TrySetResult();
                    return ValueTask.CompletedTask;
                }, runBudget: new WorkflowRunner.StructuredActivationRunBudget(
                    MaximumEvents: 512,
                    MaximumBytes: 4_000_000,
                    ReservedFailureEvents: 8,
                    ReservedFailureBytes: 64_000,
                    MaximumOutcomeBytes: 32_000,
                    MaximumConcurrentOutcomeReservations: 1)));

        var events = recorder.Snapshot();
        AssertPersistedSequence(store, events, "failed");
        Assert.Equal(1, model.Calls);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress &&
            evt.Data?.Contains("All 2 branch results", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredActivationRunContext_DeniedMcpCallsDoNotReserveDispatchOutcomes(bool sqlite)
    {
        var definition = ParallelMcpDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition, "array-iteration-structured-denied-budget-proof");
        const string input = "[\"one-item\"]";
        using var store = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(store, startRoot: false);
        await Assert.ThrowsAnyAsync<ToolAccessDeniedException>(() =>
            new WorkflowRunner(new ParallelMcpBroker(denyAll: true))
                .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                    evt =>
                    {
                        recorder.Append(evt);
                        return ValueTask.CompletedTask;
                    }, runBudget: StructuredRunBudget(maximumToolDispatches: 1) with
                    {
                        MaximumConcurrentOutcomeReservations = 1
                    }));

        var events = recorder.Snapshot();
        AssertPersistedSequence(store, events, "failed");
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ToolDenied));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredActivationRunContext_FailureReserveExhaustionDoesNotPersistPartialTerminal(bool sqlite)
    {
        var revision = WorkflowRevision.Create(BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-structured-failure-reserve-exhaustion-proof");
        const string input = "[\"one-item\"]";
        using var store = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(store, startRoot: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(agents: new EchoAgentExecutor(failNodeId: "agent"))
                .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                    evt =>
                    {
                        recorder.Append(evt);
                        return ValueTask.CompletedTask;
                    }, runBudget: StructuredRunBudget(maximumToolDispatches: 1) with
                    {
                        ReservedFailureBytes = 1
                    }));

        var events = recorder.Snapshot();
        AssertPersistedSequence(store, events, "running");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent");
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.AgentFailed or
            ExecutionEventKinds.NodeFailed or ExecutionEventKinds.Failed or ExecutionEventKinds.Completed);
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
            var structuredBudgetRejected = false;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync, runBudget: new WorkflowRunner.StructuredActivationRunBudget(
                                MaximumEvents: maximumEvents,
                                MaximumBytes: maximumBytes,
                                ReservedFailureEvents: 8,
                                ReservedFailureBytes: 64_000,
                                MaximumOutcomeBytes: 64_000));
                    output = result.Output;
                    structuredBudgetRejected = result.Failed;
                }
                catch (Exception error) when (error is EvidenceBudgetExhaustedException or
                    WorkflowRunner.StructuredActivationBudgetExceededException)
                {
                    structuredBudgetRejected = true;
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
                recorder.PersistedEventBytes, recorder.ReservationRejected.IsCompleted || structuredBudgetRejected);
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
            var structuredWorkLimitRejected = false;

            if (candidate == "structured")
            {
                try
                {
                    var result = await new WorkflowRunner(tools, agents)
                        .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                            recorder.RecordResequencedAsync, runBudget: new WorkflowRunner.StructuredActivationRunBudget(
                                MaximumEvents: 512,
                                MaximumBytes: 4_000_000,
                                ReservedFailureEvents: 8,
                                ReservedFailureBytes: 64_000,
                                MaximumOutcomeBytes: 64_000,
                                MaximumLoopActivations: workLimit == "activations" ? 1 : int.MaxValue,
                                MaximumModelDispatches: workLimit == "model-dispatches" ? 1 : int.MaxValue,
                                MaximumToolDispatches: workLimit == "tool-dispatches" ? 1 : int.MaxValue));
                    output = result.Output;
                    structuredWorkLimitRejected = result.Failed;
                }
                catch (Exception error) when (error is EvidenceBudgetExhaustedException or
                    WorkflowRunner.StructuredActivationBudgetExceededException)
                {
                    structuredWorkLimitRejected = true;
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
            return new(output, events, model.Calls, tools.Queries,
                recorder.WorkLimitRejected.IsCompleted || structuredWorkLimitRejected);
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
                            recorder.RecordResequencedAsync,
                            runBudget: StructuredRunBudget(maximumToolDispatches: 2));
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
    public async Task IteratorCandidates_DataEvaluationAppendFailureStopsBeforeAgentDispatch(bool sqlite)
    {
        var revision = WorkflowRevision.Create(
            BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-candidate-data-evaluation-append-failure-proof");
        const string input = "[\"first\",\"second\"]";

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            using var store = new ProofRunStore(revision, input, sqlite,
                rejectedAppendPredicate: evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
                    evt.NodeId == "uppercase");
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 64_000);
            var agents = new EchoAgentExecutor();
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
                    // The data evaluator ran, but the store rejected its evidence event.
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
            Assert.Empty(agents.Inputs);
            Assert.Empty(tools.Queries);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
                evt.NodeId == "uppercase");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
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
    public async Task IteratorCandidates_DelegatedFunctionCallAppendFailurePreventsChildDispatch(bool sqlite)
    {
        const string providerId = "proof.iterator-delegate-call-evidence";
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
        var body = BodyDefinition(includeVariableStore: true, includeIndexParameter: true);
        var definition = body with
        {
            Nodes = body.Nodes.SetItem("agent", body.Nodes["agent"] with
            {
                Agent = agent
            }),
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version,
                profile => profile, StringComparer.Ordinal)
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-delegated-call-append-failure-proof");
        const string input = "[\"first\",\"second\"]";
        (string Kind, string? NodeId, string? ScopeId)[]? expectedPrefix = null;

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            Guid? parentInvocationId = null;
            var rejectedAppendCount = 0;
            Func<ExecutionEvent, bool> rejectAppend = evt =>
            {
                if (parentInvocationId is null && evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent")
                    parentInvocationId = evt.InvocationId;
                return evt.Kind == ExecutionEventKinds.ModelResponded && evt.InvocationId == parentInvocationId &&
                    evt.Data?.Contains("team_0", StringComparison.Ordinal) == true &&
                    Interlocked.CompareExchange(ref rejectedAppendCount, 1, 0) == 0;
            };
            using var store = new ProofRunStore(revision, input, sqlite, rejectedAppendPredicate: rejectAppend);
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 64_000,
                maximumConcurrentOutcomeReservations: 8);
            var model = new ParallelDelegateEvidenceModel(parentInstructions, researcherInstructions,
                reviewerInstructions, task, delegateOutputCharacters: 64);
            var provider = new AgentProviderDescriptor(providerId, "Proof delegate model", "proof-chat-v1", true);
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
                catch (IOException)
                {
                    // The model returned a delegated call, but its response event was rejected.
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
            var prefix = events.Select(evt => (evt.Kind, evt.NodeId,
                ScopeId: evt.ScopeId?.Replace("outer-loop/0/", string.Empty, StringComparison.Ordinal))).ToArray();
            expectedPrefix ??= prefix;
            Assert.Equal(expectedPrefix, prefix);
            Assert.Null(output);
            Assert.Equal(1, rejectedAppendCount);
            Assert.Single(model.Prompts);
            Assert.Equal(0, model.MaximumConcurrentDelegateCalls);
            Assert.Empty(tools.Queries);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
            Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.AgentTeamStarted or
                ExecutionEventKinds.AgentTeamCompleted or ExecutionEventKinds.AgentTeamFailed);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed &&
                evt.InvocationId == parentInvocationId);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(1, recorder.PendingOutcomeReservations);
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
    public async Task IteratorCandidates_DelegatedChildOutcomeAppendFailureStopsLaterActivations(bool sqlite)
    {
        const string providerId = "proof.iterator-delegate-outcome";
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
        var body = BodyDefinition(includeVariableStore: true, includeIndexParameter: true);
        var definition = body with
        {
            Nodes = body.Nodes.SetItem("agent", body.Nodes["agent"] with { Agent = agent }),
            AgentProfiles = profiles.ToImmutableSortedDictionary(profile => profile.Id + "@" + profile.Version,
                profile => profile, StringComparer.Ordinal)
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-candidate-delegated-outcome-append-failure-proof");
        const string input = "[\"first\",\"second\"]";
        (string Kind, string? NodeId, string? ScopeId)[]? expectedPrefix = null;

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            Guid? parentInvocationId = null;
            var rejectedAppendCount = 0;
            var rejectedInvocationIds = new ConcurrentQueue<Guid>();
            Func<ExecutionEvent, bool> rejectAppend = evt =>
            {
                if (parentInvocationId is null && evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent")
                    parentInvocationId = evt.InvocationId;
                if (evt.Kind != ExecutionEventKinds.ModelResponded || parentInvocationId is not { } parentId ||
                    evt.InvocationId == parentId || evt.InvocationId is not { } invocationId ||
                    Interlocked.CompareExchange(ref rejectedAppendCount, 1, 0) != 0)
                    return false;
                rejectedInvocationIds.Enqueue(invocationId);
                return true;
            };
            using var store = new ProofRunStore(revision, input, sqlite, rejectedAppendPredicate: rejectAppend);
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 64_000,
                maximumConcurrentOutcomeReservations: 8);
            var model = new ParallelDelegateEvidenceModel(parentInstructions, researcherInstructions,
                reviewerInstructions, task, delegateOutputCharacters: 64, failParentFollowup: true,
                holdReviewerUntilOutcomeRejected: true);
            var provider = new AgentProviderDescriptor(providerId, "Proof delegate model", "proof-chat-v1", true);
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
                catch (Exception)
                {
                    // The rejected child outcome fails the team; the parent recovery request also fails.
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
            var prefix = events.Select(evt => (evt.Kind, evt.NodeId,
                ScopeId: evt.ScopeId?.Replace("outer-loop/0/", string.Empty, StringComparison.Ordinal))).ToArray();
            expectedPrefix ??= prefix;
            Assert.Equal(expectedPrefix, prefix);
            Assert.Null(output);
            Assert.Equal(1, rejectedAppendCount);
            Assert.Single(rejectedInvocationIds);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded &&
                evt.InvocationId == rejectedInvocationIds.Single());
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamStarted);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamFailed);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentTeamCompleted);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(events.Count(evt => evt.Kind == ExecutionEventKinds.ModelRequested) -
                events.Count(evt => evt.Kind == ExecutionEventKinds.ModelResponded),
                recorder.PendingOutcomeReservations);
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
                        store.RunId, revision, input, "index", null, recorder.RecordResequencedAsync,
                        runBudget: StructuredRunBudget(maximumToolDispatches: 2));
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
                        store.RunId, revision, input, "index", null, recorder.RecordResequencedAsync,
                        runBudget: StructuredRunBudget(maximumToolDispatches: 2));
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
    public async Task StructuredActivationLoopStartAppendFailureStopsBeforeBodyDispatch(bool sqlite)
    {
        const string input = "[\"first\"]";
        var revision = WorkflowRevision.Create(
            BodyDefinition(includeVariableStore: true, includeIndexParameter: true),
            "array-iteration-loop-start-append-failure-proof");
        using var store = new ProofRunStore(revision, input, sqlite,
            rejectedAppendEventKinds: [ExecutionEventKinds.LoopIterationStarted]);
        var recorder = new ReservedEvidenceProofRecorder(store,
            maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
            reservedFailureBytes: 64_000, maximumOutcomeBytes: 64_000);
        var agents = new EchoAgentExecutor();
        var tools = new EchoToolBroker();

        await Assert.ThrowsAsync<IOException>(() => new WorkflowRunner(tools, agents)
            .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                recorder.RecordResequencedAsync, runBudget: StructuredRunBudget(maximumToolDispatches: 2)));

        var events = recorder.Snapshot();
        Assert.Empty(agents.Inputs);
        Assert.Empty(tools.Queries);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
        AssertPersistedSequence(store, events, "failed");

        var reopened = store.ReopenStore().Get(store.RunId)!;
        Assert.Equal(events, reopened.Events);
        Assert.Equal("failed", reopened.Status);
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

            var secondAuthorization = baseline.Events.Where(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized)
                .OrderBy(evt => evt.Sequence).Last();
            const int reservedFailureEvents = 8;
            // Keep capacity for the first call and its outcome, but reject the
            // second authorization before the broker can dispatch it.
            var eventLimit = checked((int)secondAuthorization.Sequence + 1 + reservedFailureEvents);
            var eventLimited = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 2,
                holdDispatchUntilReservationRejected: true, maximumEvents: eventLimit);
            Assert.Null(eventLimited.Output);
            Assert.True(eventLimited.ReservationRejected);
            Assert.Equal(1, eventLimited.DispatchedCalls);
            Assert.Single(eventLimited.Events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            Assert.Single(eventLimited.Events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.DoesNotContain(eventLimited.Events,
                evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Contains(eventLimited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.True(eventLimited.Events.Length <= eventLimit);

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
            int maximumConcurrentReservations, bool holdDispatchUntilReservationRejected, int maximumEvents = 512)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents,
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
            const int reservedFailureEvents = 8;
            var eventLimit = checked((int)secondAuthorization.Sequence + 1 + reservedFailureEvents);
            var eventLimited = await ExecuteCandidateAsync(candidate, maximumConcurrentReservations: 2,
                maximumEvents: eventLimit, releaseConnectionOnReservationRejected: true);
            Assert.Null(eventLimited.Output);
            Assert.True(eventLimited.ReservationRejected);
            Assert.Single(eventLimited.Events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            Assert.DoesNotContain(eventLimited.Events,
                evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.Contains(eventLimited.Events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
            Assert.Equal(0, eventLimited.PendingReservations);
            Assert.True(eventLimited.Events.Length <= eventLimit);

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
            int maximumConcurrentReservations, long maximumBytes = 4_000_000, int maximumEvents = 512,
            bool releaseConnectionOnReservationRejected = false)
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store, maximumEvents,
                maximumBytes, reservedFailureEvents: 8, reservedFailureBytes: 64_000,
                maximumOutcomeBytes: 32_000,
                maximumConcurrentOutcomeReservations: maximumConcurrentReservations,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted,
                additionalReservationOutcomeKind: ExecutionEventKinds.ToolUncertain);
            var tools = CreateSynchronizedDemoMcpBroker(registrations, expectedConnections: 2,
                releaseConnectionBarrier: releaseConnectionOnReservationRejected ? recorder.ReservationRejected : null);
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
                            recorder.RecordResequencedAsync,
                            runBudget: StructuredRunBudget(maximumToolDispatches: 2));
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
                            recorder.RecordResequencedAsync,
                            runBudget: StructuredRunBudget(maximumToolDispatches: 2));
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
    public async Task IteratorCandidates_CancelAfterMcpDispatchRecordsUncertainOutcome(bool sqlite)
    {
        const string input = "[\"first\",\"second\"]";
        const string bindingId = "proof.cancel-after-dispatch";
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-cancel-after-mcp-dispatch", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("tool", new(NodeTypes.McpTool, "1.0.0",
                    new ToolBindingRequest(bindingId, new string('d', 64))) { McpArgumentsJson = "{}" })
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-tool", new(new("input", "out"), new("tool", "in"), "data"))
                .Add("tool-output", new(new("tool", "out"), new("output", "in"), "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-tool", Edge("input", "then", "tool", "execute"))
                .Add("tool-output", Edge("tool", "then", "output", "execute"))
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-cancel-after-mcp-dispatch-proof");

        foreach (var candidate in new[] { "structured", "fresh-subworkflow", "hybrid" })
        {
            using var store = new ProofRunStore(revision, input, sqlite);
            var recorder = new ReservedEvidenceProofRecorder(store,
                maximumEvents: 512, maximumBytes: 4_000_000, reservedFailureEvents: 8,
                reservedFailureBytes: 64_000, maximumOutcomeBytes: 32_000,
                reservationRequestKind: ExecutionEventKinds.ToolAuthorized,
                reservationOutcomeKind: ExecutionEventKinds.ToolCompleted,
                additionalReservationOutcomeKind: ExecutionEventKinds.ToolUncertain);
            var broker = new CancelAfterDispatchToolBroker(bindingId);

            if (candidate == "structured")
            {
                using var cancellation = new CancellationTokenSource();
                var execution = new WorkflowRunner(broker)
                    .ExecuteStructuredActivationSequenceProofAsync(store.RunId, revision, input, "index", null,
                        recorder.RecordResequencedAsync, cancellation.Token,
                        runBudget: StructuredRunBudget(maximumToolDispatches: 1));
                await broker.DispatchStarted.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution);
            }
            else if (candidate == "fresh-subworkflow")
            {
                await recorder.RecordResequencedAsync(new ExecutionEvent(store.RunId,
                    store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.Started));
                var iterator = new PerItemWorkflowExecutor(store.RunId, revision, recorder,
                    new EchoAgentExecutor(), broker);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                try
                {
                    var watch = ObserveAsync();
                    await broker.DispatchStarted.WaitAsync(TimeSpan.FromSeconds(5));
                    await run.CancelRunAsync();
                    await watch;
                }
                finally
                {
                    await run.DisposeAsync();
                }

                await recorder.RecordResequencedAsync(new ExecutionEvent(store.RunId,
                    store.Store.Get(store.RunId)!.Header.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.Cancelled));

                async Task ObserveAsync()
                {
                    await foreach (var _ in run.WatchStreamAsync()) { }
                }
            }
            else
            {
                using var cancellation = new CancellationTokenSource();
                var execution = ExecuteHybridBudgetLoopAsync(store, revision, recorder,
                    new EchoAgentExecutor(), broker, input, cancellationToken: cancellation.Token);
                await broker.DispatchStarted.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                var result = await execution;
                Assert.True(result.Cancelled);
            }

            var events = recorder.Snapshot();
            Assert.Equal(1, broker.DispatchAttempts);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
            var uncertain = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ToolUncertain);
            var nodeCancelled = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
                evt.NodeId == "tool");
            Assert.True(uncertain.Sequence < nodeCancelled.Sequence);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Cancelled && evt.ScopeId is null);
            Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.Failed or ExecutionEventKinds.Completed);
            Assert.Equal(0, recorder.PendingOutcomeReservations);
            AssertPersistedSequence(store, events, "cancelled");

            var reopened = store.ReopenStore().Get(store.RunId)!;
            Assert.Equal("cancelled", reopened.Status);
            Assert.Equal(events, reopened.Events);
            Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
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
    public async Task IteratorCandidates_ExecuteSequenceOutputsPerActivation(bool sqlite)
    {
        var definition = SequenceBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition, "array-iteration-structured-sequence-proof");
        const string input = "[\"hello\",\"world\"]";
        var candidates = new[]
        {
            await RunCandidateAsync("structured"),
            await RunCandidateAsync("fresh-subworkflow"),
            await RunCandidateAsync("hybrid")
        };

        Assert.All(candidates, candidate => Assert.Equal("echo:sequence;echo:sequence", candidate.Output));
        Assert.All(candidates, candidate => Assert.Equal(
            ["sequence", "sequence", "sequence", "sequence"], candidate.ToolQueries));
        Assert.Equal(SequenceShape(candidates[0].Events), SequenceShape(candidates[1].Events));
        Assert.Equal(SequenceShape(candidates[0].Events), SequenceShape(candidates[2].Events));

        foreach (var candidate in candidates)
            for (var index = 0; index < 2; index++)
            {
                var scopeId = $"array-loop/{index}";
                var scoped = candidate.Events.Where(evt => evt.ScopeId == scopeId).ToArray();
                Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.SequenceStarted);
                Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched));
                Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
                Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
                Assert.Equal(2, scoped.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                    evt.NodeId is "inventory-tool" or "architecture-tool"));
                Assert.Contains(scoped, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                    evt.NodeId == "array-loop");
            }

        async Task<(string Output, ExecutionEvent[] Events, string[] ToolQueries)> RunCandidateAsync(string candidate)
        {
            using var runStore = new ProofRunStore(revision, input, sqlite);
            var recorder = new ProofRecorder(runStore, startRoot: false);
            var tools = new EchoToolBroker();
            string output;

            if (candidate == "structured")
            {
                var result = await new WorkflowRunner(tools).ExecuteStructuredActivationSequenceProofAsync(
                    runStore.RunId, revision, input, "index", null, evt =>
                    {
                        recorder.Append(evt);
                        return ValueTask.CompletedTask;
                    });
                Assert.False(result.Failed);
                output = result.Output!;
            }
            else if (candidate == "fresh-subworkflow")
            {
                recorder.Append(new ExecutionEvent(runStore.RunId, revision.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.Started));
                var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder,
                    new EchoAgentExecutor(), tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                string? observedOutput = null;
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                await foreach (var evt in run.WatchStreamAsync())
                    if (evt is WorkflowOutputEvent { Data: string value }) observedOutput = value;
                output = observedOutput ?? throw new InvalidOperationException("The subworkflow candidate produced no output.");
                recorder.CompleteRoot();
            }
            else
            {
                recorder.Append(new ExecutionEvent(runStore.RunId, revision.RevisionId, 0, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.Started));
                var items = JsonSerializer.Deserialize<string[]>(input)!;
                var values = new List<string>(items.Length);
                for (var index = 0; index < items.Length; index++)
                {
                    var scopeId = $"array-loop/{index}";
                    var frame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
                        "array-loop", index, items[index], ImmutableSortedDictionary<string, string>.Empty
                            .Add("index", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    recorder.Append(ExecutionEventKinds.LoopIterationStarted, "array-loop", frame.Id, scopeId);
                    async ValueTask Record(ExecutionEvent evt)
                    {
                        if (evt.Kind is not (ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                            ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated))
                            recorder.Append(evt with { ScopeId = scopeId });
                    }
                    var result = await new WorkflowRunner(tools).ExecuteWithOutcomeAsync(runStore.RunId, revision,
                        items[index], frame.Parameters, Record);
                    Assert.False(result.Failed);
                    values.Add(result.Output!);
                    recorder.Append(ExecutionEventKinds.LoopIterationCompleted, "array-loop", frame.Id, scopeId);
                }
                output = string.Join(';', values);
                recorder.CompleteRoot();
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(runStore, events, "completed");
            return (output, events, tools.Queries);
        }

        static (string Kind, string? NodeId, string? ConnectionId, string? ScopeId)[] SequenceShape(
            IEnumerable<ExecutionEvent> events) => events
            .Where(evt => evt.Kind.StartsWith("sequence.", StringComparison.Ordinal))
            .Select(evt => (evt.Kind, evt.NodeId, evt.ConnectionId, evt.ScopeId))
            .OrderBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ThenBy(evt => evt.Kind, StringComparer.Ordinal)
            .ThenBy(evt => evt.ConnectionId, StringComparer.Ordinal)
            .ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredSequenceProof_FailureCancelsSiblingOutput(bool sqlite)
    {
        var definition = SequenceBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition,
            "array-iteration-structured-sequence-failure-proof");
        const string input = "[\"hello\"]";
        using var runStore = new ProofRunStore(revision, input, sqlite);
        var recorder = new ProofRecorder(runStore, startRoot: false);
        var tools = new FailSequenceOutputToolBroker("proof.inventory", "proof.architecture");

        var execution = new WorkflowRunner(tools).ExecuteStructuredActivationSequenceProofAsync(
            runStore.RunId, revision, input, "index", null, evt =>
            {
                recorder.Append(evt);
                return ValueTask.CompletedTask;
            });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("first Sequence output failed", failure.Message, StringComparison.Ordinal);
        Assert.True(tools.FailingOutputReachedSibling);
        Assert.True(tools.SiblingObservedCancellation);
        var events = recorder.Snapshot();
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            evt.Data?.Contains("inventory", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            evt.Data?.Contains("architecture", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IteratorCandidates_SequenceFailureCancelsSiblingOutput(bool sqlite)
    {
        var definition = SequenceBodyDefinition() with
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        var revision = WorkflowRevision.Create(definition, "array-iteration-sequence-failure-candidates-proof");
        const string input = "[\"hello\"]";
        var candidates = new[]
        {
            await RunCandidateAsync("structured"),
            await RunCandidateAsync("fresh-subworkflow"),
            await RunCandidateAsync("hybrid")
        };

        Assert.All(candidates, candidate => Assert.True(candidate.SiblingCancelled));
        Assert.All(candidates, candidate =>
        {
            Assert.Single(candidate.SequenceEvents, evt => evt.Kind == ExecutionEventKinds.SequenceStarted);
            Assert.Equal(2, candidate.SequenceEvents.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched));
            Assert.Single(candidate.SequenceEvents, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
            Assert.Single(candidate.SequenceEvents, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled);
            Assert.Single(candidate.SequenceEvents, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
            Assert.DoesNotContain(candidate.SequenceEvents, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
            Assert.Single(candidate.Events, evt => evt.Kind == ExecutionEventKinds.ToolUncertain);
        });
        Assert.Equal(SequenceShape(candidates[0].SequenceEvents), SequenceShape(candidates[1].SequenceEvents));
        Assert.Equal(SequenceShape(candidates[0].SequenceEvents), SequenceShape(candidates[2].SequenceEvents));

        async Task<(ExecutionEvent[] Events, ExecutionEvent[] SequenceEvents, bool SiblingCancelled)> RunCandidateAsync(
            string candidate)
        {
            using var runStore = new ProofRunStore(revision, input, sqlite);
            var recorder = new ProofRecorder(runStore);
            var tools = new FailSequenceOutputToolBroker("proof.inventory", "proof.architecture");
            var itemParameters = ImmutableSortedDictionary<string, string>.Empty.Add("index", "0");
            var scopeId = "array-loop/0";

            if (candidate == "structured")
            {
                var execution = new WorkflowRunner(tools).ExecuteStructuredActivationSequenceProofAsync(
                    runStore.RunId, revision, input, "index", null, evt =>
                    {
                        recorder.Append(evt);
                        return ValueTask.CompletedTask;
                    });
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await execution);
            }
            else if (candidate == "fresh-subworkflow")
            {
                var iterator = new PerItemWorkflowExecutor(runStore.RunId, revision, recorder,
                    new EchoAgentExecutor(), tools);
                var workflow = new WorkflowBuilder(iterator).WithOutputFrom(iterator).Build();
                await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, input);
                var workflowFailed = false;
                var watch = Task.Run(async () =>
                {
                    await foreach (var evt in run.WatchStreamAsync())
                        workflowFailed |= evt is WorkflowErrorEvent;
                });
                try
                {
                    await watch.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    if (!watch.IsCompleted) await run.CancelRunAsync();
                }
                Assert.True(workflowFailed);
                recorder.FailRoot();
            }
            else
            {
                var frame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null,
                    "array-loop", 0, "hello", itemParameters);
                recorder.Append(ExecutionEventKinds.LoopIterationStarted, "array-loop", frame.Id, scopeId);
                ValueTask Record(ExecutionEvent evt)
                {
                    if (evt.Kind is ExecutionEventKinds.Started or ExecutionEventKinds.Completed or
                        ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated)
                        return ValueTask.CompletedTask;
                    recorder.Append(evt with { ScopeId = scopeId });
                    return ValueTask.CompletedTask;
                }

                var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await new WorkflowRunner(tools).ExecuteWithOutcomeAsync(runStore.RunId,
                        revision, "hello", itemParameters, Record).WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Contains("inventory-tool", failure.Message, StringComparison.Ordinal);
                recorder.FailRoot();
            }

            var events = recorder.Snapshot();
            AssertPersistedSequence(runStore, events, "failed");
            var sequenceEvents = events.Where(evt => evt.Kind.StartsWith("sequence.", StringComparison.Ordinal))
                .ToArray();
            return (events, sequenceEvents, tools.SiblingObservedCancellation);
        }

        static (string Kind, string? NodeId, string? ConnectionId, string? ScopeId)[] SequenceShape(
            IEnumerable<ExecutionEvent> events) => events
            .Select(evt => (evt.Kind, evt.NodeId, evt.ConnectionId, evt.ScopeId))
            .OrderBy(evt => evt.Kind, StringComparer.Ordinal)
            .ThenBy(evt => evt.ConnectionId, StringComparer.Ordinal)
            .ThenBy(evt => evt.ScopeId, StringComparer.Ordinal)
            .ToArray();
    }

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StructuredFrameCoordinator_IsolatesCollaborationPerActivation(bool phased, bool sqlite)
    {
        var sourceDefinition = phased
            ? WorkflowRunnerCollaborationTests.CreatePhasedGraph(branchCount: 2, timeoutSeconds: 30)
            : WorkflowRunnerCollaborationTests.CreateGraph(branchCount: 2, timeoutSeconds: 30);
        var revision = WorkflowRevision.Create(sourceDefinition with
            {
                SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
                Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                    .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
            }, "array-iteration-structured-collaboration-proof");
        using var runStore = new ProofRunStore(revision, "[\"item-a\",\"item-b\"]", sqlite);
        var events = new List<ExecutionEvent>();
        var gate = new object();
        ValueTask Record(ExecutionEvent evt)
        {
            lock (gate) events.Add(evt);
            return ValueTask.CompletedTask;
        }

        var agent = new ActivationScopedCollaborationAgentExecutor(branchCount: 2, attemptUnauthorizedChannel: true);
        var result = await new WorkflowRunner(agents: agent)
            .ExecuteStructuredActivationSequenceProofAsync(runStore.RunId, revision,
                "[\"item-a\",\"item-b\"]", "index", null, Record)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal(phased ? 8 : 4, agent.UnauthorizedChannelRejections.Count);
        Assert.All(agent.UnauthorizedChannelRejections, Assert.True);
        var outputs = result.Output!.Split(';');
        Assert.Equal(2, outputs.Length);
        for (var index = 0; index < outputs.Length; index++)
        {
            using var joined = JsonDocument.Parse(outputs[index]);
            Assert.Equal(2, joined.RootElement.EnumerateObject().Count());
            Assert.All(joined.RootElement.EnumerateObject(), branch =>
                Assert.Equal(index == 0 ? "item-a" : "item-b", branch.Value.GetString()));
        }
        runStore.Store.Append(runStore.RunId, 0, events);
        AssertPersistedSequence(runStore, events, "completed");
        for (var index = 0; index < 2; index++)
        {
            var scopeId = $"array-loop/{index}";
            var scoped = events.Where(evt => evt.ScopeId == scopeId).ToArray();
            var started = Assert.Single(scoped, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationRegionStarted);
            using var startedData = JsonDocument.Parse(started.Data!);
            var regionInvocationId = startedData.RootElement.GetProperty("regionInvocationId").GetGuid();
            Assert.All(scoped.Where(evt => evt.Kind is ExecutionEventKinds.ParallelCollaborationArrival or
                ExecutionEventKinds.ParallelCollaborationMessageSent or ExecutionEventKinds.ParallelCollaborationMessagesRead or
                ExecutionEventKinds.ParallelCollaborationReleased), evt =>
            {
                using var data = JsonDocument.Parse(evt.Data!);
                Assert.Equal(regionInvocationId, data.RootElement.GetProperty("regionInvocationId").GetGuid());
            });
            Assert.All(scoped, evt => Assert.Equal(scopeId, evt.ScopeId));
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
        var invocationIds = events.Where(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationRegionStarted)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("regionInvocationId").GetGuid())
            .ToArray();
        Assert.Equal(2, invocationIds.Distinct().Count());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StructuredFrameCoordinator_FailedMemberAbortsOnlyItsActivation(bool phased, bool sqlite)
    {
        var sourceDefinition = phased
            ? WorkflowRunnerCollaborationTests.CreatePhasedGraph(branchCount: 2, timeoutSeconds: 30)
            : WorkflowRunnerCollaborationTests.CreateGraph(branchCount: 2, timeoutSeconds: 30);
        var revision = WorkflowRevision.Create(sourceDefinition with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
        }, "array-iteration-structured-collaboration-failure-proof");
        using var runStore = new ProofRunStore(revision, "[\"item-a\",\"item-b\"]", sqlite);
        var events = new List<ExecutionEvent>();
        var gate = new object();
        ValueTask Record(ExecutionEvent evt)
        {
            lock (gate) events.Add(evt);
            return ValueTask.CompletedTask;
        }
        var failedNodeId = phased ? "p2-branch_a" : "publish-branch_a";

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents:
                new ActivationScopedCollaborationAgentExecutor(branchCount: 2, failNodeId: failedNodeId))
            .ExecuteStructuredActivationSequenceProofAsync(runStore.RunId, revision,
                "[\"item-a\",\"item-b\"]", "index", null, Record)
            .WaitAsync(TimeSpan.FromSeconds(10)));

        runStore.Store.Append(runStore.RunId, 0, events);
        AssertPersistedSequence(runStore, events, "failed");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.Equal(phased ? 1 : 0, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.NodeId == "array-loop" &&
            evt.ScopeId == "array-loop/1");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StructuredFrameCoordinator_CancellationAbortsAndSettlesActivation(bool phased, bool sqlite)
    {
        var sourceDefinition = phased
            ? WorkflowRunnerCollaborationTests.CreatePhasedGraph(branchCount: 2, timeoutSeconds: 30)
            : WorkflowRunnerCollaborationTests.CreateGraph(branchCount: 2, timeoutSeconds: 30);
        var revision = WorkflowRevision.Create(sourceDefinition with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
        }, "array-iteration-structured-collaboration-cancellation-proof");
        using var runStore = new ProofRunStore(revision, "[\"item-a\",\"item-b\"]", sqlite);
        var events = new List<ExecutionEvent>();
        var gate = new object();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Record(ExecutionEvent evt)
        {
            lock (gate) events.Add(evt);
            return ValueTask.CompletedTask;
        }
        var blockedNodeId = phased ? "r1-branch_a" : "publish-branch_a";
        var execution = new WorkflowRunner(agents: new ActivationScopedCollaborationAgentExecutor(
                branchCount: 2, blockNodeId: blockedNodeId, blockedNodeEntered: entered))
            .ExecuteStructuredActivationSequenceProofAsync(runStore.RunId, revision,
                "[\"item-a\",\"item-b\"]", "index", null, Record, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));

        runStore.Store.Append(runStore.RunId, 0, events);
        AssertPersistedSequence(runStore, events, "cancelled");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.Equal(phased ? 1 : 0, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.NodeId == "array-loop" &&
            evt.ScopeId == "array-loop/1");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StructuredFrameCoordinator_TimeoutAbortsAndSettlesActivation(bool phased, bool sqlite)
    {
        var sourceDefinition = phased
            ? WorkflowRunnerCollaborationTests.CreatePhasedGraph(branchCount: 2, timeoutSeconds: 1)
            : WorkflowRunnerCollaborationTests.CreateGraph(branchCount: 2, timeoutSeconds: 1);
        var revision = WorkflowRevision.Create(sourceDefinition with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
        }, "array-iteration-structured-collaboration-timeout-proof");
        using var runStore = new ProofRunStore(revision, "[\"item-a\",\"item-b\"]", sqlite);
        var events = new List<ExecutionEvent>();
        var gate = new object();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Record(ExecutionEvent evt)
        {
            lock (gate) events.Add(evt);
            return ValueTask.CompletedTask;
        }
        var blockedNodeId = phased ? "r1-branch_a" : "publish-branch_a";

        await Assert.ThrowsAsync<TimeoutException>(() => new WorkflowRunner(agents:
                new ActivationScopedCollaborationAgentExecutor(branchCount: 2, blockNodeId: blockedNodeId,
                    blockedNodeEntered: entered))
            .ExecuteStructuredActivationSequenceProofAsync(runStore.RunId, revision,
                "[\"item-a\",\"item-b\"]", "index", null, Record)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runStore.Store.Append(runStore.RunId, 0, events);
        AssertPersistedSequence(runStore, events, "failed");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.Equal(phased ? 1 : 0, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.NodeId == "array-loop" &&
            evt.ScopeId == "array-loop/1");
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
        INodeToolBroker tools, string input, bool recordCompletionAppendFailureAsRunFailure = false,
        CancellationToken cancellationToken = default)
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
                cancellationToken.ThrowIfCancellationRequested();
                await recorder.RecordResequencedAsync(new ExecutionEvent(runId, revisionId, 0,
                    DateTimeOffset.UtcNow, ExecutionEventKinds.LoopIterationStarted, "array-loop", frame.Id)
                { ScopeId = frame.ScopeId });
                var result = await new WorkflowRunner(tools, agents).ExecuteWithOutcomeAsync(runId,
                    revision, items[index], parameters, Record, cancellationToken);
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
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    await RecordRootTerminalAsync(ExecutionEventKinds.Cancelled);
                    return new(null, exception.ToString(), Cancelled: true);
                }
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

    private sealed record HybridBudgetLoopResult(string? Output, string? Error, bool Cancelled = false);

    private sealed class ActivationScopedCollaborationAgentExecutor(int branchCount, string? failNodeId = null,
        string? blockNodeId = null, TaskCompletionSource? blockedNodeEntered = null,
        bool attemptUnauthorizedChannel = false) : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<bool> unauthorizedChannelRejections = new();
        public IReadOnlyCollection<bool> UnauthorizedChannelRejections => unauthorizedChannelRejections.ToArray();

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (invocation.NodeId == failNodeId)
                throw new InvalidOperationException($"Injected collaboration-member failure at {invocation.NodeId}.");
            if (invocation.NodeId == blockNodeId)
            {
                blockedNodeEntered?.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var branchId = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions;
            var phase = invocation.Revision.Definition.ParallelCollaborationRegions!.Values.Single()
                .SecondRendezvousNodeId is null ? null : invocation.CollaborationPhase;
            if (invocation.CollaborationRole == ParallelCollaborationRoles.Publish)
            {
                Assert.NotNull(invocation.CollaborationSend);
                var content = phase is null ? $"{input}|{branchId}" : $"{input}|{phase}|{branchId}";
                await invocation.CollaborationSend!("work", content, cancellationToken);
                if (attemptUnauthorizedChannel)
                {
                    try
                    {
                        await invocation.CollaborationSend!("unauthorized", "must not be appended", cancellationToken);
                        unauthorizedChannelRejections.Enqueue(false);
                    }
                    catch (InvalidOperationException)
                    {
                        unauthorizedChannelRejections.Enqueue(true);
                    }
                }
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

    private static async Task<string> RunActivationLoopProofAsync(Guid runId, WorkflowRevision revision,
        ProofRecorder recorder, IAgentNodeExecutor agents, INodeToolBroker tools, string loopNodeId,
        IReadOnlyList<string> items, WorkflowRunner.StructuredActivationFrame? parentFrame,
        string indexParameterId, ConcurrentQueue<LoopProofBreakDispatch> breakDispatches,
        Func<WorkflowRunner.StructuredActivationFrame, Task<LoopProofResult>> executeBody)
    {
        var results = new List<string>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var parameters = parentFrame?.Parameters.ToBuilder() ??
                ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            if (revision.Definition.Parameters?.ContainsKey("item") == true)
                parameters["item"] = items[index];
            parameters[indexParameterId] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var frameParameters = parameters.ToImmutable();
            var frame = parentFrame?.Enter(loopNodeId, index, items[index], frameParameters) ??
                new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null, loopNodeId, index,
                    items[index], frameParameters);
            recorder.Append(ExecutionEventKinds.LoopIterationStarted, loopNodeId, frame.Id, frame.ScopeId);

            var result = await executeBody(frame);
            if (result is LoopProofBreak signal)
            {
                breakDispatches.Enqueue(new(signal.SourceNodeId, signal.SourceFrameId, signal.TargetFrameId,
                    signal.ScopeId));
                if (signal.TargetFrameId != frame.Id)
                    throw new InvalidOperationException("The hybrid coordinator did not own the nearest loop target.");
                break;
            }

            recorder.Append(ExecutionEventKinds.LoopIterationCompleted, loopNodeId, frame.Id, frame.ScopeId);
            results.Add(((ActivationLoopProofValue)result).Value);
        }

        return string.Join(';', results);
    }

    private static async Task<string> RunFreshSubworkflowLoopProofAsync(Guid runId, WorkflowRevision revision,
        ProofRecorder recorder, string loopNodeId, IReadOnlyList<string> items,
        WorkflowRunner.StructuredActivationFrame? parentFrame, string indexParameterId,
        ConcurrentQueue<LoopProofBreakDispatch> breakDispatches,
        Func<WorkflowRunner.StructuredActivationFrame, Task<LoopProofResult>> executeBody)
    {
        var results = new List<string>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var parameters = parentFrame?.Parameters.ToBuilder() ??
                ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            if (revision.Definition.Parameters?.ContainsKey("item") == true)
                parameters["item"] = items[index];
            parameters[indexParameterId] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var frameParameters = parameters.ToImmutable();
            var frame = parentFrame?.Enter(loopNodeId, index, items[index], frameParameters) ??
                new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null, loopNodeId, index,
                    items[index], frameParameters);
            recorder.Append(ExecutionEventKinds.LoopIterationStarted, loopNodeId, frame.Id, frame.ScopeId);

            var result = await executeBody(frame);
            if (result is LoopProofBreak signal)
            {
                breakDispatches.Enqueue(new(signal.SourceNodeId, signal.SourceFrameId, signal.TargetFrameId,
                    signal.ScopeId));
                if (signal.TargetFrameId != frame.Id)
                    throw new InvalidOperationException("The subworkflow coordinator did not own the nearest loop target.");
                break;
            }

            recorder.Append(ExecutionEventKinds.LoopIterationCompleted, loopNodeId, frame.Id, frame.ScopeId);
            results.Add(((ActivationLoopProofValue)result).Value);
        }

        return string.Join(';', results);
    }

    private static (Workflow Child, Workflow Parent) BuildFreshVawBodyWorkflow()
    {
        var bodyExecutor = new FreshVawBodyExecutor();
        var child = new WorkflowBuilder(bodyExecutor).WithOutputFrom(bodyExecutor).Build();
        var binding = child.BindAsExecutor($"vaw-body-{Guid.NewGuid():N}");
        var start = new FreshVawBodyStartExecutor();
        var builder = new WorkflowBuilder(start);
        builder.AddEdge<FreshVawBodyInput>((ExecutorBinding)start, binding, _ => true);
        builder.WithOutputFrom(binding);
        return (child, builder.Build());
    }

    private abstract record LoopProofResult;
    private sealed record ActivationLoopProofValue(string Value) : LoopProofResult;
    private sealed record LoopProofBreak(string SourceNodeId, Guid SourceFrameId, Guid TargetFrameId,
        string ScopeId) : LoopProofResult;
    private sealed record LoopProofBreakDispatch(string SourceNodeId, Guid SourceFrameId,
        Guid TargetFrameId, string ScopeId);
    private sealed record LoopProofIteratorPinDispatch(string SourceNodeId, string InputArrayDataType,
        string SourcePinId, string DataType, string Value, string TargetNodeId, string TargetPinId,
        string ScopeId);
    private sealed record LoopProofIteratorPins(LoopProofIteratorPinDispatch Element,
        LoopProofIteratorPinDispatch Index);
    private sealed record MatchedBreakCandidateEvidence(string Output, ExecutionEvent[] Events,
        string[] AgentInputs, string[] ToolQueries, string[] BreakScopes,
        (string NodeId, string ScopeId)[] GraphDispatches,
        LoopProofIteratorPinDispatch[] IteratorPinDispatches, Workflow[] ChildWorkflows);

    private sealed record FreshVawBodyInput(Func<CancellationToken, Task<LoopProofResult>> Execute);

    private sealed class FreshVawBodyStartExecutor()
        : Executor<FreshVawBodyInput, FreshVawBodyInput>("vaw-body-start", declareCrossRunShareable: true)
    {
        public override ValueTask<FreshVawBodyInput> HandleAsync(FreshVawBodyInput input,
            IWorkflowContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(input);
    }

    private sealed class FreshVawBodyExecutor()
        : Executor<FreshVawBodyInput, LoopProofResult>("vaw-body", declareCrossRunShareable: true)
    {
        public override ValueTask<LoopProofResult> HandleAsync(FreshVawBodyInput input,
            IWorkflowContext context, CancellationToken cancellationToken = default) =>
            new(input.Execute(cancellationToken));
    }

    private sealed class LoopControlProofGraph
    {
        private readonly LoopBreakProofNode breakNode = new();

        public LoopProofResult Execute(string bodyResult,
            WorkflowRunner.StructuredActivationFrame activation,
            ConcurrentQueue<(string NodeId, string ScopeId)> dispatches)
        {
            dispatches.Enqueue(("break-condition", activation.ScopeId));
            if (!bodyResult.Contains("BREAK:", StringComparison.Ordinal))
                return new ActivationLoopProofValue(bodyResult);

            dispatches.Enqueue(("break", activation.ScopeId));
            return breakNode.Execute(activation);
        }
    }

    private sealed class LoopArrayIteratorProofNode
    {
        public LoopProofIteratorPins Bind(
            WorkflowRunner.StructuredActivationFrame activation, string elementDataType)
        {
            if (!WorkflowDataTypes.IsArrayElementType(elementDataType))
                throw new InvalidOperationException("The proof iterator requires a supported typed array element.");
            if (activation.Index < 0)
                throw new InvalidOperationException("The proof iterator index must be zero-based and non-negative.");

            var arrayDataType = WorkflowDataTypes.ArrayOf(elementDataType);
            var element = new LoopProofIteratorPinDispatch("iterator", arrayDataType, "element",
                elementDataType, activation.Item, "workflow-input", "value", activation.ScopeId);
            var index = new LoopProofIteratorPinDispatch("iterator", arrayDataType, "index",
                WorkflowDataTypes.Integer, activation.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "workflow-parameter", "index", activation.ScopeId);
            return new LoopProofIteratorPins(element, index);
        }
    }

    private sealed class LoopBreakProofNode
    {
        public LoopProofBreak Execute(WorkflowRunner.StructuredActivationFrame activation)
        {
            var nearestLoop = activation.NearestLoop();
            return new LoopProofBreak("break", activation.Id, nearestLoop.Id, activation.ScopeId);
        }
    }

    private static WorkflowDefinition ConditionalItemBodyDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("expected", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("yes") })
            .Add("equals", new(NodeTypes.StringEquals, "1.0.0"))
            .Add("route", new(NodeTypes.BooleanBranch, "1.0.0"))
            .Add("agent-yes", new(NodeTypes.Agent, "1.0.0",
                Agent: new("proof.echo-agent", null, "Handle the true route.", [])))
            .Add("agent-no", new(NodeTypes.Agent, "1.0.0",
                Agent: new("proof.echo-agent", null, "Handle the false route.", [])))
            .Add("merge", new(NodeTypes.Merge, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-condition", new(new("input", "out"), new("equals", "left"), "data"))
            .Add("expected-condition", new(new("expected", "out"), new("equals", "right"), "data"))
            .Add("condition-route", new(new("equals", "out"), new("route", "condition"), "data"))
            .Add("input-agent-yes", new(new("input", "out"), new("agent-yes", "in"), "data"))
            .Add("input-agent-no", new(new("input", "out"), new("agent-no", "in"), "data"))
            .Add("input-merge", new(new("input", "out"), new("merge", "in"), "data"))
            .Add("merge-output", new(new("merge", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-route", Edge("input", "then", "route", "execute"))
            .Add("route-yes", Edge("route", "true", "agent-yes", "execute"))
            .Add("route-no", Edge("route", "false", "agent-no", "execute"))
            .Add("yes-merge", Edge("agent-yes", "out", "merge", "execute"))
            .Add("no-merge", Edge("agent-no", "out", "merge", "execute"))
            .Add("merge-output", Edge("merge", "then", "output", "execute"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-conditional-route", "input", nodes, data)
        {
            ExecutionConnections = execution,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition;
    }

    private static WorkflowDefinition GraphFunctionValueBodyDefinition()
    {
        var function = new GraphFunctionDefinition("proof.append-suffix", 1, "Append suffix",
            "Append a fixed suffix to the supplied text.",
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("text", new("Text", WorkflowDataTypes.String)),
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("result", new("Result", WorkflowDataTypes.String)),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "text" })
                .Add("suffix", new(NodeTypes.StringConstant, "1.0.0")
                    { StringConstant = new("!") })
                .Add("concat", new(NodeTypes.TextConcat, "1.0.0"))
                .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(new("input", "out"), new("concat", "left"), "data"))
                .Add("suffix", new(new("suffix", "out"), new("concat", "right"), "data"))
                .Add("result", new(new("concat", "out"), new("output", "in"), "data")));
        var call = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0")
        {
            GraphFunction = new(function, GraphFunctionJson.ComputeHash(function))
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-function-values", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("call", call)
                .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input-call", new(new("input", "out"), new("call", "text"), "data"))
                .Add("call-output", new(new("call", "result"), new("output", "in"), "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input-output", Edge("input", "then", "output", "execute"))
        };
    }

    private static WorkflowDefinition TypedItemAndIndexBodyDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("item", new(NodeTypes.WorkflowValue, "1.0.0")
                { ParameterId = "item", OutputDataType = WorkflowDataTypes.Integer })
            .Add("index", new(NodeTypes.WorkflowValue, "1.0.0")
                { ParameterId = "index", OutputDataType = WorkflowDataTypes.Integer })
            .Add("item-text", new(NodeTypes.IntegerToString, "1.0.0"))
            .Add("index-text", new(NodeTypes.IntegerToString, "1.0.0"))
            .Add("separator", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new(":") })
            .Add("item-prefix", new(NodeTypes.TextConcat, "1.0.0"))
            .Add("item-index", new(NodeTypes.TextConcat, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0",
                Agent: new("proof.echo-agent", null, "Echo the input exactly.", [])))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("item-value", new(new("item", "out"), new("item-text", "in"), "data"))
            .Add("index-value", new(new("index", "out"), new("index-text", "in"), "data"))
            .Add("prefix-item", new(new("item-text", "out"), new("item-prefix", "left"), "data"))
            .Add("prefix-separator", new(new("separator", "out"), new("item-prefix", "right"), "data"))
            .Add("joined-prefix", new(new("item-prefix", "out"), new("item-index", "left"), "data"))
            .Add("joined-index", new(new("index-text", "out"), new("item-index", "right"), "data"))
            .Add("agent-input", new(new("item-index", "out"), new("agent", "in"), "data"))
            .Add("agent-output", new(new("agent", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", Edge("input", "then", "agent", "execute"))
            .Add("agent-output", Edge("agent", "out", "output", "execute"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-iteration-typed-values", "input", nodes, data)
        {
            ExecutionConnections = execution,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("item", new("Array item", "0") { Type = WorkflowDataTypes.Integer })
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
    }

    private static WorkflowDefinition StructuredBreakProofDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("item", new(NodeTypes.WorkflowValue, "1.0.0")
                { ParameterId = "item", OutputDataType = WorkflowDataTypes.String })
            .Add("index", new(NodeTypes.WorkflowValue, "1.0.0")
                { ParameterId = "index", OutputDataType = WorkflowDataTypes.Integer })
            .Add("index-text", new(NodeTypes.IntegerToString, "1.0.0"))
            .Add("separator", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new(":") })
            .Add("prefix", new(NodeTypes.TextConcat, "1.0.0"))
            .Add("render", new(NodeTypes.TextConcat, "1.0.0"))
            .Add("condition", new(NodeTypes.Condition, "1.0.0") { Condition = new("equals", "continue") })
            .Add("merge", new(NodeTypes.Merge, "1.0.0"))
            .Add("proof-break", new(WorkflowRunner.StructuredProofBreakNodeType, "proof-only"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("item-condition", new(new("item", "out"), new("condition", "in"), "data"))
            .Add("condition-prefix", new(new("condition", "true"), new("prefix", "left"), "data"))
            .Add("separator-prefix", new(new("separator", "out"), new("prefix", "right"), "data"))
            .Add("prefix-render", new(new("prefix", "out"), new("render", "left"), "data"))
            .Add("index-text", new(new("index", "out"), new("index-text", "in"), "data"))
            .Add("index-render", new(new("index-text", "out"), new("render", "right"), "data"))
            .Add("render-merge", new(new("render", "out"), new("merge", "in"), "data"))
            .Add("merge-output", new(new("merge", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-condition", Edge("input", "then", "condition", "execute"))
            .Add("condition-merge", Edge("condition", "true", "merge", "execute"))
            .Add("condition-break", Edge("condition", "false", "proof-break", "execute"))
            .Add("merge-output", Edge("merge", "then", "output", "execute"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "structured-array-break-proof", "input", nodes, data)
        {
            ExecutionConnections = execution,
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("item", new("Current item", "") { Type = WorkflowDataTypes.String })
                .Add("index", new("Array index", "0") { Type = WorkflowDataTypes.Integer })
        };
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

    private static string[] ParseProofArrayItems(string json, string elementType)
    {
        if (!WorkflowArrayLiteral.TryParseValue(elementType, json, out var values, out var error))
            throw new InvalidOperationException($"Proof input is not a valid {WorkflowDataTypes.ArrayOf(elementType)}: {error}");
        return values.EnumerateArray().Select(item => elementType switch
        {
            WorkflowDataTypes.String => item.GetString()!,
            WorkflowDataTypes.Boolean => item.GetBoolean() ? "true" : "false",
            WorkflowDataTypes.Integer => item.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            WorkflowDataTypes.Number => item.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Proof input requires a primitive array element type.")
        }).ToArray();
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
        private readonly string arrayElementType;

        public PerItemWorkflowExecutor(Guid runId, WorkflowRevision revision, ProofRecorder recorder,
            IAgentNodeExecutor agents, INodeToolBroker tools, IWorkflowExecutionGate? gate = null,
            Func<string, CancellationToken, ValueTask>? afterNodeStarted = null,
            Func<ExecutionEvent, ValueTask>? observeEvent = null,
            WorkflowRunner.StructuredActivationFrame? parentFrame = null, string loopNodeId = "array-loop",
            string arrayElementType = WorkflowDataTypes.String)
            : this(runId, revision, RecordTo(recorder), agents, tools, gate, afterNodeStarted, observeEvent,
                parentFrame, loopNodeId, arrayElementType) { }

        public PerItemWorkflowExecutor(Guid runId, WorkflowRevision revision,
            ReservedEvidenceProofRecorder recorder, IAgentNodeExecutor agents, INodeToolBroker tools)
            : this(runId, revision, recorder.RecordResequencedAsync, agents, tools, null, null, null, null,
                "array-loop", WorkflowDataTypes.String) { }

        private PerItemWorkflowExecutor(Guid runId, WorkflowRevision revision,
            Func<ExecutionEvent, ValueTask> recordEvent, IAgentNodeExecutor agents, INodeToolBroker tools,
            IWorkflowExecutionGate? gate, Func<string, CancellationToken, ValueTask>? afterNodeStarted,
            Func<ExecutionEvent, ValueTask>? observeEvent,
            WorkflowRunner.StructuredActivationFrame? parentFrame, string loopNodeId, string arrayElementType)
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
            this.arrayElementType = arrayElementType;
        }

        private static Func<ExecutionEvent, ValueTask> RecordTo(ProofRecorder recorder) => evt =>
        {
            recorder.Append(evt);
            return ValueTask.CompletedTask;
        };

        public override async ValueTask<string> HandleAsync(string input, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var items = ParseProofArrayItems(input, arrayElementType);
            var results = new List<string>(items.Length);
            for (var index = 0; index < items.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemParameters = parentFrame?.Parameters.ToBuilder() ??
                    ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                if (revision.Definition.Parameters?.ContainsKey("item") == true)
                    itemParameters["item"] = items[index];
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

    private static WorkflowRunner.StructuredActivationRunBudget StructuredRunBudget(int maximumToolDispatches) =>
        new(MaximumEvents: 512, MaximumBytes: 4_000_000, ReservedFailureEvents: 8,
            ReservedFailureBytes: 64_000, MaximumOutcomeBytes: 32_000,
            MaximumConcurrentOutcomeReservations: 2, MaximumModelDispatches: 2,
            MaximumToolDispatches: maximumToolDispatches);

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

    private sealed class ParallelActivationBarrier
    {
        private readonly object gate = new();
        private TaskCompletionSource pairReady = NewPair();
        private int arrivals;
        private int completedRequestPairs;

        public int CompletedPairs { get { lock (gate) return completedRequestPairs; } }

        public async Task WaitForConcurrentPairAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource current;
            var completePair = false;
            lock (gate)
            {
                current = pairReady;
                arrivals++;
                if (arrivals == 2)
                {
                    arrivals = 0;
                    completedRequestPairs++;
                    pairReady = NewPair();
                    completePair = true;
                }
            }

            if (completePair) current.TrySetResult();
            await current.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        private static TaskCompletionSource NewPair() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ParallelGraphFunctionChatClient(ParallelActivationBarrier session) : IChatClient
    {
        private int requestCount;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref requestCount) > 1)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Function completed."))
                { FinishReason = ChatFinishReason.Stop };

            var input = messages.Last(message => message.Role == ChatRole.User).Text!;
            await session.WaitForConcurrentPairAsync(cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(Guid.NewGuid().ToString("N"), "graph_function_0",
                    new Dictionary<string, object?> { ["text"] = input })]))
            { FinishReason = ChatFinishReason.ToolCalls };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ParallelFunctionInputAgentExecutor(ParallelActivationBarrier barrier) : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<(string NodeId, string Input)> invocations = new();

        public ParallelActivationBarrier Barrier => barrier;
        public (string NodeId, string Input)[] Invocations => [.. invocations];

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.parallel-function-input", "Parallel function input proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            invocations.Enqueue((invocation.NodeId, input));
            await barrier.WaitForConcurrentPairAsync(cancellationToken);
            return JsonSerializer.Serialize(new { query = $"{input}-{invocation.NodeId}" });
        }
    }

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

    private sealed class FailingForkSiblingAgentExecutor : IAgentNodeExecutor
    {
        public TaskCompletionSource FailureReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.break-race", "Break race proof", "proof", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (invocation.NodeId != "agent-right")
                throw new InvalidOperationException($"Unexpected proof Agent node '{invocation.NodeId}'.");
            FailureReached.TrySetResult();
            throw new InvalidOperationException("The admitted sibling failed while Break was settling.");
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
        public TaskCompletionSource LeftEntered { get; } =
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
            {
                LeftEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

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

    private sealed class FailSequenceOutputToolBroker : INodeToolBroker
    {
        private readonly string failingBindingId;
        private readonly string blockedBindingId;
        private readonly TaskCompletionSource blockedStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int failingOutputReachedSibling;
        private int siblingObservedCancellation;

        public FailSequenceOutputToolBroker(string failingBindingId, string blockedBindingId)
        {
            this.failingBindingId = failingBindingId;
            this.blockedBindingId = blockedBindingId;
        }

        public bool FailingOutputReachedSibling => Volatile.Read(ref failingOutputReachedSibling) != 0;
        public bool SiblingObservedCancellation => Volatile.Read(ref siblingObservedCancellation) != 0;
        public Task BlockedStarted => blockedStarted.Task;

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this, invocation);

        private sealed class Scope(FailSequenceOutputToolBroker owner, NodeInvocationContext invocation)
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
                var bindingId = requestedBindingId;
                if (bindingId != owner.failingBindingId && bindingId != owner.blockedBindingId)
                    throw new InvalidOperationException($"Unexpected Sequence proof binding '{requestedBindingId}'.");

                var toolCallId = Guid.NewGuid();
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolRequested,
                    JsonSerializer.Serialize(new { toolCallId, bindingId }));
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolAuthorized,
                    JsonSerializer.Serialize(new { toolCallId, bindingId, input = arguments }));

                if (bindingId == owner.blockedBindingId)
                {
                    owner.blockedStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        throw new InvalidOperationException("The sibling output unexpectedly completed.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Interlocked.Exchange(ref owner.siblingObservedCancellation, 1);
                        await invocation.RecordEventAsync(ExecutionEventKinds.ToolUncertain,
                            JsonSerializer.Serialize(new
                            {
                                toolCallId,
                                bindingId,
                                failureCode = "tool.outcome_unknown",
                                disposition = ToolInvocationFailureDisposition.OutcomeUnknown.ToString(),
                                reason = "The Sequence sibling failed after dispatch; its outcome is unknown."
                            }));
                        throw;
                    }
                }

                await owner.blockedStarted.Task.WaitAsync(cancellationToken);
                Interlocked.Exchange(ref owner.failingOutputReachedSibling, 1);
                throw new InvalidOperationException("The first Sequence output failed after its sibling dispatched.");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
        IReadOnlyList<McpToolRegistration> registrations, int expectedConnections, TimeSpan? timeout = null,
        Task? releaseConnectionBarrier = null)
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
                        var barrier = releaseConnectionBarrier is null
                            ? allConnectionsReady.Task
                            : Task.WhenAny(allConnectionsReady.Task, releaseConnectionBarrier);
                        await barrier.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
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

    private sealed class CancelAfterDispatchToolBroker(string bindingId) : INodeToolBroker
    {
        private readonly string expectedBindingId = bindingId;
        private readonly TaskCompletionSource dispatchStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int dispatchAttempts;

        public Task DispatchStarted => dispatchStarted.Task;
        public int DispatchAttempts => Volatile.Read(ref dispatchAttempts);

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this, invocation);

        private sealed class Scope(CancelAfterDispatchToolBroker owner, NodeInvocationContext invocation)
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
                if (requestedBindingId != owner.expectedBindingId)
                    throw new InvalidOperationException("The workflow requested an unexpected custom tool.");

                var toolCallId = Guid.NewGuid();
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolRequested,
                    JsonSerializer.Serialize(new { toolCallId, bindingId = requestedBindingId }));
                await invocation.RecordEventAsync(ExecutionEventKinds.ToolAuthorized,
                    JsonSerializer.Serialize(new { toolCallId, bindingId = requestedBindingId, input = arguments }));

                Interlocked.Increment(ref owner.dispatchAttempts);
                owner.dispatchStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The proof tool unexpectedly completed before cancellation.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    const string reason = "The tool was cancelled after dispatch; its outcome is unknown.";
                    await invocation.RecordEventAsync(ExecutionEventKinds.ToolUncertain,
                        JsonSerializer.Serialize(new
                        {
                            toolCallId,
                            bindingId = requestedBindingId,
                            failureCode = "tool.outcome_unknown",
                            disposition = ToolInvocationFailureDisposition.OutcomeUnknown.ToString(),
                            reason
                        }));
                    throw;
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
        Task? releaseAfterReservationRejected = null, bool waitForBothAuthorizations = false,
        bool denyAll = false) : INodeToolBroker
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
                waitForBothAuthorizations, denyAll);

        private sealed class Scope(ParallelMcpBroker owner, string nodeId, Guid invocationId,
            Func<string, string?, ValueTask> record, string? denyNode, string? failNode,
            bool holdUntilCancelled, string? resultPayload, Task? releaseAfterReservationRejected,
            bool waitForBothAuthorizations, bool denyAll) : INodeToolScope
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
                    if (denyAll || nodeId == denyNode)
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
        int? researcherFailureStatusCode = null, bool holdReviewerUntilOutcomeRejected = false)
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
                if (holdReviewerUntilOutcomeRejected && instructions == reviewerInstructions)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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
