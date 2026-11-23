using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class BoundedLoopExecutionTests
{
    [Fact]
    public async Task SequencedEventWriterCommitsAcceptedSequenceAndLedgerOnlyAfterRecorderReturns()
    {
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var ledger = new ExecutionActivationLedger();
        var accepted = new List<ExecutionEvent>();
        var failFirst = true;
        using var writer = new SequencedExecutionEventWriter(runId, revisionId, executionEvent =>
        {
            if (failFirst)
            {
                failFirst = false;
                return ValueTask.FromException(new IOException("injected recorder failure"));
            }

            accepted.Add(executionEvent);
            return ValueTask.CompletedTask;
        }, ledger);

        await Assert.ThrowsAsync<IOException>(() => writer.RecordAsync(
            ExecutionEventKinds.NodeStarted, "agent", invocationId).AsTask());
        Assert.Empty(ledger.Snapshot());

        await writer.RecordAsync(ExecutionEventKinds.NodeStarted, "agent", invocationId);
        await writer.RecordAsync(ExecutionEventKinds.NodeCompleted, "agent", invocationId);

        Assert.Equal(new long[] { 1, 2 }, accepted.Select(evt => evt.Sequence));
        Assert.Equal(new[] { ExecutionEventKinds.NodeStarted, ExecutionEventKinds.NodeCompleted },
            accepted.Select(evt => evt.Kind));
        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public async Task SequencedEventWriterSerializesConcurrentAcceptedEvents()
    {
        var accepted = new List<ExecutionEvent>();
        var acceptedGate = new object();
        var writer = new SequencedExecutionEventWriter(Guid.NewGuid(), Guid.NewGuid(), executionEvent =>
        {
            lock (acceptedGate) accepted.Add(executionEvent);
            return ValueTask.CompletedTask;
        }, new ExecutionActivationLedger());
        var tasks = Enumerable.Range(0, 32).Select(index => writer.RecordAsync(
            ExecutionEventKinds.NodeStarted, $"node-{index}", Guid.NewGuid()).AsTask()).ToArray();

        await Task.WhenAll(tasks);

        ExecutionEvent[] snapshot;
        lock (acceptedGate) snapshot = [.. accepted];
        Assert.Equal(Enumerable.Range(1, snapshot.Length).Select(value => (long)value),
            snapshot.Select(evt => evt.Sequence));
    }

    [Fact]
    public async Task StructuredRunContextRetainsRetryAfterRecorderFailure()
    {
        var accepted = new List<ExecutionEvent>();
        var recorderCalls = 0;
        using var context = new WorkflowRunner.StructuredActivationRunContext(Guid.NewGuid(), Guid.NewGuid(),
            executionEvent =>
            {
                if (recorderCalls++ == 0)
                    return ValueTask.FromException(new IOException("injected recorder failure"));
                accepted.Add(executionEvent);
                return ValueTask.CompletedTask;
            }, budget: null);

        await Assert.ThrowsAsync<IOException>(() => context.EmitAsync(ExecutionEventKinds.Started).AsTask());
        await context.EmitAsync(ExecutionEventKinds.Started);
        await context.EmitAsync(ExecutionEventKinds.Completed);

        Assert.Equal(new long[] { 1, 2 }, accepted.Select(evt => evt.Sequence));
        Assert.Equal(new[] { ExecutionEventKinds.Started, ExecutionEventKinds.Completed },
            accepted.Select(evt => evt.Kind));
    }

    [Fact]
    public async Task ActivationOwnerBlocksWritesAfterRecorderIsMarkedUncertain()
    {
        var definition = WorkflowSchemaNormalization.NormalizeForEditing(ExplicitLoopGraph());
        Assert.True(StructuredActivationGraphPlan.TryCreate(definition, out var plan, out var issues),
            string.Join(Environment.NewLine, issues));
        using var owner = new WorkflowRunner.StructuredActivationFrameOwner(plan, null, definition);
        var recorderCalls = 0;
        owner.BindRootEventRecorder(Guid.NewGuid(), Guid.NewGuid(), _ =>
        {
            recorderCalls++;
            return ValueTask.FromException(new IOException("injected recorder failure"));
        }, new ExecutionActivationLedger());

        await Assert.ThrowsAsync<IOException>(() => owner.RecordEventAsync(ExecutionEventKinds.NodeStarted,
            "agent", Guid.NewGuid()).AsTask());
        owner.MarkRootEventRecorderUncertain();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RecordEventAsync(
            ExecutionEventKinds.NodeStarted, "agent", Guid.NewGuid()).AsTask());

        Assert.Contains("uncertain", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, recorderCalls);
    }

    private static WorkflowDefinition ExplicitLoopGraph()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["set-value"] = new(new("input", "out"), new("set", "value"), "data"),
            ["output-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-loop"] = new(new("input", "then"), new("loop", "execute"), "execution"),
            ["loop-body"] = new(new("loop", "body"), new("body", "execute"), "execution"),
            ["body-set"] = new(new("body", "then"), new("set", "execute"), "execution"),
            ["set-get"] = new(new("set", "then"), new("get", "execute"), "execution"),
            ["get-loop"] = new(new("get", "then"), new("loop", "feedback"), "execution"),
            ["loop-output"] = new(new("loop", "done"), new("output", "execute"), "execution")
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.CurrentAuthoringSchema, "loop-variable-candidate", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("message", new("Message", "default")),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

    private static WorkflowDefinition Graph()
    {
        var baseline = new WorkflowDefinition("1.0", "bounded-loop-runtime", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["edge"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        return GraphEditing.InsertBoundedLoopAfter(baseline, "input", "out");
    }

    [Fact]
    public async Task FrameworkRepeatsBodyAndStopsWhenPredicateBecomesFalse()
    {
        var graph = Graph();
        var anchorId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        graph = GraphEditing.InsertNodeAfter(graph, anchorId, "out", new(NodeTypes.Uppercase, "1.0.0"));
        var bodyId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Uppercase).Key;
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(graph);
        var events = new List<ExecutionEvent>();
        var normalizedEvents = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        var normalizedOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(normalized, "normalized-predicate-loop"), "continue",
            evt => { normalizedEvents.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("CONTINUE", output);
        Assert.Equal(output, normalizedOutput);
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        Assert.Equal("predicate-false", JsonDocument.Parse(stopped.Data!).RootElement.GetProperty("reason").GetString());
        Assert.Equal(1, events.Count(evt => evt.NodeId == bodyId && evt.Kind == ExecutionEventKinds.NodeStarted));
        Assert.All(events.Where(evt => evt.NodeId == bodyId), evt => Assert.Null(evt.ScopeId));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.NodeId == graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
        Assert.Equal(events.Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
            .Select(evt => (evt.Kind, evt.Data)), normalizedEvents
            .Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
            .Select(evt => (evt.Kind, evt.Data)));
    }

    [Fact]
    public async Task PlaybackPairsOrdinaryLoopIterationAcrossLoopNodeInvocations()
    {
        var events = new List<ExecutionEvent>();
        var graph = Graph();
        await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "loop-playback"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        var started = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        var completed = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted).ToArray();
        Assert.Equal(3, started.Length);
        Assert.Equal(3, completed.Length);
        Assert.NotEqual(started[0].InvocationId, completed[0].InvocationId);

        var html = await RenderLoopPlayback(events);

        Assert.Contains("Iteration 1 · Completed", html, StringComparison.Ordinal);
        Assert.Contains("Iteration 2 · Completed", html, StringComparison.Ordinal);
        Assert.Contains("Iteration 3 · Completed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaybackPairsLegacyLoopEventsByInvocationWhenIterationMetadataIsMissing()
    {
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            new ExecutionEvent(runId, revisionId, 1, now, ExecutionEventKinds.LoopIterationStarted,
                "loop", invocationId, null, null),
            new ExecutionEvent(runId, revisionId, 2, now, ExecutionEventKinds.LoopIterationCompleted,
                "loop", invocationId, null, null)
        };

        var html = await RenderLoopPlayback(events);

        Assert.Contains("Iteration 1 · Completed", html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderLoopPlayback(IEnumerable<ExecutionEvent> events, string runStatus = "completed")
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<LoopActivationPlayback>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(LoopActivationPlayback.Events)] = events,
                    [nameof(LoopActivationPlayback.RunStatus)] = runStatus
                }));
            return component.ToHtmlString();
        });
    }

    [Fact]
    public async Task FrameworkEnforcesMaximumIterationsAndRecordsEveryPass()
    {
        var graph = Graph();
        var events = new List<ExecutionEvent>();
        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("continue", output);
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        var stopData = JsonDocument.Parse(stopped.Data!).RootElement;
        Assert.Equal("maximum-iterations", stopData.GetProperty("reason").GetString());
        Assert.Equal(3, stopData.GetProperty("iterations").GetInt32());
        Assert.Equal(3, events.Count(evt => evt.NodeId == graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key && evt.Kind == ExecutionEventKinds.NodeCompleted));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Theory]
    [InlineData("continue", 3, "maximum-iterations")]
    [InlineData("stop", 0, "predicate-false")]
    public async Task StructuredSchedulerProofRoutesLoopFeedbackUntilItsStopCondition(string input,
        int expectedIterations, string expectedReason)
    {
        var graph = WorkflowSchemaNormalization.NormalizeForEditing(Graph());
        var revision = WorkflowRevision.Create(graph, "structured-scheduler-loop-proof");
        var loopNode = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
        var events = new List<ExecutionEvent>();
        var ledger = new StructuredProofDispatchLedger();

        var result = await new WorkflowRunner().ExecuteStructuredSchedulerProofAsync(Guid.NewGuid(), revision,
            input, null, evt =>
            {
                events.Add(evt);
                return ValueTask.CompletedTask;
            }, proofDispatchLedger: ledger);

        Assert.Equal(input, result.Output);
        Assert.Equal(expectedIterations, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(expectedIterations, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        Assert.Equal(expectedReason, JsonDocument.Parse(stopped.Data!).RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, ledger.OutstandingDeliveryCount);
        Assert.Contains(events, evt => evt.NodeId == loopNode &&
            evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task Defensive_activation_guard_rejection_preserves_prior_loop_pass_after_store_reopen(string storeKind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-activation-guard-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var storePath = storeKind == "sqlite" ? Path.Combine(directory, "runs.sqlite3") : Path.Combine(directory, "runs");
            IWorkflowRunStore store = CreateStore(storeKind, storePath);
            var revision = WorkflowRevision.Create(WorkflowSchemaNormalization.NormalizeForEditing(Graph()),
                "loop-activation-guard-proof");
            var initial = store.Create(revision, "continue");
            var loopId = revision.Definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Loop).Key;
            var bodyId = revision.Definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.LoopBody).Key;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new WorkflowRunner().ExecuteWithActivationLimitProofAsync(initial.Header.RunId, revision, "continue",
                    maximumLoopActivations: 1, executionEvent =>
                    {
                        store.Append(executionEvent);
                        return ValueTask.CompletedTask;
                    }));

            var reopened = Assert.IsType<RunSnapshot>(CreateStore(storeKind, storePath).Get(initial.Header.RunId));
            Assert.Equal("failed", reopened.Status);
            Assert.Equal(Enumerable.Range(1, reopened.Events.Length).Select(value => (long)value),
                reopened.Events.Select(evt => evt.Sequence));
            Assert.Equal(ExecutionEventKinds.Failed, reopened.Events[^1].Kind);
            var iterationStarted = Assert.Single(reopened.Events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.NodeId == loopId);
            var iterationCompleted = Assert.Single(reopened.Events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted && evt.NodeId == loopId);
            Assert.Equal(iterationStarted.ScopeId, iterationCompleted.ScopeId);
            Assert.Single(reopened.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == bodyId);
            Assert.DoesNotContain(reopened.Events, evt => evt.Kind == ExecutionEventKinds.LoopStopped && evt.NodeId == loopId);
            Assert.DoesNotContain(reopened.Events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == bodyId &&
                evt.ScopeId == $"{loopId}/2");

            var playback = await RenderLoopPlayback(reopened.Events, reopened.Status);
            Assert.Contains("Iteration 1 · Completed", playback, StringComparison.Ordinal);
            Assert.DoesNotContain("Iteration 2", playback, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static IWorkflowRunStore CreateStore(string storeKind, string storePath) => storeKind == "sqlite"
        ? new SqliteWorkflowRunStore(storePath)
        : new FileRunJournal(storePath);

    [Fact]
    public async Task UnifiedPredicateLoopGivesEachBodyPassAnIndependentActivationScope()
    {
        var graph = Graph();
        graph = WorkflowSchemaNormalization.NormalizeForEditing(graph);
        var bodyId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        var loopId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(graph, "unified-loop-activation-scope"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("continue", output);
        var bodyActivations = events.Where(evt => evt.NodeId == bodyId && evt.Kind == ExecutionEventKinds.NodeStarted)
            .Select(evt => evt.ScopeId).ToArray();
        Assert.Equal(new string?[] { $"{loopId}/1", $"{loopId}/2", $"{loopId}/3" }, bodyActivations);
        Assert.Equal(3, bodyActivations.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(bodyActivations, events.Where(evt => evt.NodeId == bodyId && evt.Kind == ExecutionEventKinds.NodeCompleted)
            .Select(evt => evt.ScopeId));
    }

    [Fact]
    public async Task UnifiedPredicateLoopPropagatesActivationScopeThroughEveryBodyNode()
    {
        var graph = ExplicitLoopGraph();
        var loopBodyId = "body";
        var bodyNodeIds = new[] { loopBodyId, "set", "get" };
        var loopId = "loop";
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(graph, "unified-loop-body-scope-propagation"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("continue", output);
        var expectedScopes = new[] { $"{loopId}/1", $"{loopId}/2", $"{loopId}/3" };
        foreach (var nodeId in bodyNodeIds)
        {
            var startedScopes = events.Where(evt => evt.NodeId == nodeId && evt.Kind == ExecutionEventKinds.NodeStarted)
                .Select(evt => evt.ScopeId).ToArray();
            Assert.Equal(expectedScopes, startedScopes);
            Assert.Equal(startedScopes, events.Where(evt => evt.NodeId == nodeId && evt.Kind == ExecutionEventKinds.NodeCompleted)
                .Select(evt => evt.ScopeId));
        }
    }

    [Fact]
    public void ActivationOwnerSharesEvidenceBudgetAndRejectsWorkPastDerivedLoopBound()
    {
        var definition = WorkflowSchemaNormalization.NormalizeForEditing(Graph());
        Assert.True(StructuredActivationGraphPlan.TryCreate(definition, out var plan, out var issues),
            string.Join(Environment.NewLine, issues));
        var owner = new WorkflowRunner.StructuredActivationFrameOwner(plan, null, definition);
        var loopId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Loop).Key;
        var parameters = ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

        Assert.Equal(3, owner.MaximumLoopActivations);
        Assert.Same(owner.EvidenceBudget, owner.CreateVariableStore(definition).ArrayEvidenceBudget);
        for (var index = 0; index < 3; index++)
            owner.CreateBodyPass(loopId, 1, 3, "continue", parameters, null);
        Assert.Equal(3, owner.LoopActivations);

        Assert.Throws<WorkflowRunner.StructuredActivationBudgetExceededException>(() =>
            owner.CreateBodyPass(loopId, 1, 3, "duplicate activation", parameters, null));
        Assert.Equal(3, owner.LoopActivations);

        var repeatedGraphOwner = new WorkflowRunner.StructuredActivationFrameOwner(plan, null, definition,
            graphActivationMultiplicity: 2);
        Assert.Equal(6, repeatedGraphOwner.MaximumLoopActivations);
        for (var index = 0; index < 6; index++)
            repeatedGraphOwner.CreateBodyPass(loopId, 1, 3, "repeated graph activation", parameters, null);
        Assert.Throws<WorkflowRunner.StructuredActivationBudgetExceededException>(() =>
            repeatedGraphOwner.CreateBodyPass(loopId, 1, 3, "past repeated graph bound", parameters, null));
    }

    [Fact]
    public async Task ActivationOwnerAdvancesEventSequenceOnlyAfterRecorderAcceptsEvent()
    {
        var definition = WorkflowSchemaNormalization.NormalizeForEditing(Graph());
        Assert.True(StructuredActivationGraphPlan.TryCreate(definition, out var plan, out var issues),
            string.Join(Environment.NewLine, issues));
        using var owner = new WorkflowRunner.StructuredActivationFrameOwner(plan, null, definition);
        var ledger = new ExecutionActivationLedger();
        var acceptedEvents = new List<ExecutionEvent>();
        var recorderCalls = 0;
        var invocationId = Guid.NewGuid();
        owner.BindRootEventRecorder(Guid.NewGuid(), Guid.NewGuid(), executionEvent =>
        {
            if (recorderCalls++ == 0)
                return ValueTask.FromException(new IOException("injected recorder failure"));
            acceptedEvents.Add(executionEvent);
            return ValueTask.CompletedTask;
        }, ledger);

        await Assert.ThrowsAsync<IOException>(() => owner.RecordEventAsync(
            ExecutionEventKinds.NodeStarted, "node", invocationId).AsTask());
        Assert.Empty(ledger.Snapshot());

        await owner.RecordEventAsync(ExecutionEventKinds.NodeStarted, "node", invocationId);
        Assert.Single(ledger.Snapshot());
        await owner.RecordEventAsync(ExecutionEventKinds.NodeCompleted, "node", invocationId);

        Assert.Empty(ledger.Snapshot());
        Assert.Equal(new long[] { 1, 2 }, acceptedEvents.Select(executionEvent => executionEvent.Sequence));
        Assert.Equal(new[] { ExecutionEventKinds.NodeStarted, ExecutionEventKinds.NodeCompleted },
            acceptedEvents.Select(executionEvent => executionEvent.Kind));
    }

    [Fact]
    public async Task ActivationOwnerEnforcesAcceptedRootLifecycleExactlyOnce()
    {
        var definition = WorkflowSchemaNormalization.NormalizeForEditing(Graph());
        Assert.True(StructuredActivationGraphPlan.TryCreate(definition, out var plan, out var issues),
            string.Join(Environment.NewLine, issues));
        using var owner = new WorkflowRunner.StructuredActivationFrameOwner(plan, null, definition);
        var acceptedEvents = new List<ExecutionEvent>();
        var recorderCalls = 0;
        owner.BindRootEventRecorder(Guid.NewGuid(), Guid.NewGuid(), executionEvent =>
        {
            if (recorderCalls++ == 0)
                return ValueTask.FromException(new IOException("injected start append failure"));
            acceptedEvents.Add(executionEvent);
            return ValueTask.CompletedTask;
        }, new ExecutionActivationLedger());

        await Assert.ThrowsAsync<IOException>(() => owner.RecordEventAsync(ExecutionEventKinds.Started).AsTask());
        await owner.RecordEventAsync(ExecutionEventKinds.Started);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.RecordEventAsync(ExecutionEventKinds.Started).AsTask());
        await owner.RecordEventAsync(ExecutionEventKinds.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.RecordEventAsync(ExecutionEventKinds.Failed).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.RecordEventAsync(ExecutionEventKinds.NodeStarted, "late-node").AsTask());

        Assert.Equal(new long[] { 1, 2 }, acceptedEvents.Select(executionEvent => executionEvent.Sequence));
        Assert.Equal(new[] { ExecutionEventKinds.Started, ExecutionEventKinds.Completed },
            acceptedEvents.Select(executionEvent => executionEvent.Kind));
    }

    [Fact]
    public async Task ActivationOwnerRejectsRootTerminalUntilSequenceOutputEvidenceSettles()
    {
        var definition = WorkflowSchemaNormalization.NormalizeForEditing(Graph());
        Assert.True(StructuredActivationGraphPlan.TryCreate(definition, out var plan, out var issues),
            string.Join(Environment.NewLine, issues));
        using var owner = new WorkflowRunner.StructuredActivationFrameOwner(plan, null, definition);
        var acceptedEvents = new List<ExecutionEvent>();
        var ledger = new ExecutionActivationLedger();
        owner.BindRootEventRecorder(Guid.NewGuid(), Guid.NewGuid(), executionEvent =>
        {
            acceptedEvents.Add(executionEvent);
            return ValueTask.CompletedTask;
        }, ledger);

        await owner.RecordEventAsync(ExecutionEventKinds.Started);
        var sequenceInvocation = Guid.NewGuid();
        var activationId = Guid.NewGuid();
        var evidence = new SequenceLifecycleEvidence(sequenceInvocation, "sequence", "body", 0,
            activationId, "sequence-body");
        await owner.RecordEventAsync(ExecutionEventKinds.SequenceOutputDispatched, "sequence",
            sequenceInvocation, evidence.ConnectionId, SequenceEvidence.Serialize(evidence));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.RecordEventAsync(ExecutionEventKinds.Completed).AsTask());
        Assert.DoesNotContain(acceptedEvents, evt => evt.Kind == ExecutionEventKinds.Completed);

        await owner.RecordEventAsync(ExecutionEventKinds.SequenceOutputCompleted, "sequence",
            sequenceInvocation, evidence.ConnectionId, SequenceEvidence.Serialize(evidence));
        await owner.RecordEventAsync(ExecutionEventKinds.Completed);
        Assert.Equal(new[]
        {
            ExecutionEventKinds.Started,
            ExecutionEventKinds.SequenceOutputDispatched,
            ExecutionEventKinds.SequenceOutputCompleted,
            ExecutionEventKinds.Completed
        }, acceptedEvents.Select(evt => evt.Kind));
    }

    [Fact]
    public async Task LegacyDataLoopReevaluatesBodyTransformsOnEveryIteration()
    {
        var graph = Graph();
        var anchorId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        graph = GraphEditing.InsertNodeAfter(graph, anchorId, "out", new(NodeTypes.Reverse, "1.0.0"));
        var firstReverseId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Reverse).Key;
        graph = GraphEditing.InsertNodeAfter(graph, firstReverseId, "out", new(NodeTypes.Reverse, "1.0.0"));
        var secondReverseId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Reverse && pair.Key != firstReverseId).Key;
        var reverseIds = new[] { firstReverseId, secondReverseId };
        var loopId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(graph);
        var events = new List<ExecutionEvent>();
        var normalizedEvents = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(graph, "legacy-loop-transform"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        var normalizedOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(normalized, "normalized-loop-transform"), "continue",
            evt => { normalizedEvents.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("continue", output);
        Assert.Equal(output, normalizedOutput);
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        Assert.Equal(3, events.Count(evt => evt.NodeId == reverseIds[0] && evt.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(3, events.Count(evt => evt.NodeId == reverseIds[1] && evt.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(new[] { "eunitnoc", "eunitnoc", "eunitnoc" }, events
            .Where(evt => evt.NodeId == reverseIds[0] && evt.Kind == ExecutionEventKinds.NodeCompleted)
            .Select(evt => evt.Data));
        Assert.Equal(new[] { "continue", "continue", "continue" }, events
            .Where(evt => evt.NodeId == reverseIds[1] && evt.Kind == ExecutionEventKinds.NodeCompleted)
            .Select(evt => evt.Data));
        var stop = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        Assert.Equal("maximum-iterations", JsonDocument.Parse(stop.Data!).RootElement.GetProperty("reason").GetString());
        Assert.Equal("continue", events.Last(evt => evt.NodeId == loopId && evt.Kind == ExecutionEventKinds.NodeStarted).Data);
        Assert.Equal(events.Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
            .Select(evt => (evt.Kind, evt.Data)), normalizedEvents
            .Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
            .Select(evt => (evt.Kind, evt.Data)));
        foreach (var (nodeId, expectedValues) in new[]
        {
            (reverseIds[0], new[] { "eunitnoc", "eunitnoc", "eunitnoc" }),
            (reverseIds[1], new[] { "continue", "continue", "continue" })
        })
        {
            var recordedValues = normalizedEvents.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == nodeId)
                .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("value").GetString());
            Assert.Equal(expectedValues, recordedValues);
        }
    }

    [Fact]
    public async Task UnifiedSchemaNormalizationPreservesExplicitLegacyLoopExecutionEvidence()
    {
        var legacy = ExplicitLoopGraph();
        var sourceJson = WorkflowJson.Serialize(legacy);
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(legacy);
        var legacyEvents = new List<ExecutionEvent>();
        var normalizedEvents = new List<ExecutionEvent>();

        var legacyOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(legacy, "legacy-loop"), "continue",
            evt => { legacyEvents.Add(evt); return ValueTask.CompletedTask; });
        var normalizedOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(normalized, "normalized-loop"), "continue",
            evt => { normalizedEvents.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("2.0", normalized.SchemaVersion);
        Assert.Equal(legacy.Nodes.Keys, normalized.Nodes.Keys);
        Assert.Equal(legacy.Connections.Keys, normalized.Connections.Keys);
        Assert.Equal(legacy.Connections, normalized.Connections);
        Assert.Equal(legacy.ExecutionConnections!.Keys, normalized.ExecutionConnections!.Keys);
        Assert.Equal(legacy.ExecutionConnections, normalized.ExecutionConnections);
        Assert.Equal(sourceJson, WorkflowJson.Serialize(legacy));
        Assert.Equal(legacyOutput, normalizedOutput);
        Assert.Equal(legacyEvents.Select(evt => (evt.Kind, evt.NodeId, evt.Data)),
            normalizedEvents.Select(evt => (evt.Kind, evt.NodeId, evt.Data)));
    }
}
