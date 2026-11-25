using System.Collections.Concurrent;
using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Typed For Each runtime (Decision 0070, 2026-11-24): one evaluation of the array per activation, one body pass
/// per item in ascending order, typed item and zero-based index outputs, then the completed path.
/// </summary>
public sealed class ForEachExecutionTests
{
    private sealed class RecordingAgent(Func<string, string?>? failOn = null) : IAgentNodeExecutor
    {
        public ConcurrentQueue<string> Inputs { get; } = new();

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            Inputs.Enqueue(input);
            if (failOn?.Invoke(input) is { } message) throw new InvalidOperationException(message);
            return Task.FromResult(input);
        }
    }

    /// <summary>Input → array literal → For Each → body agent fed by item+index → feedback; completed → Output.</summary>
    private static WorkflowDefinition Workflow(string elementType, string json, bool outputFromAgent = true)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(elementType, json) },
            ["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = elementType },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["label"] = new(NodeTypes.IntegerToString, "1.0.0"),
            ["text"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["items-each"] = new(new("items", "out"), new("each", "array"), "data"),
            ["index-label"] = new(new("each", "index"), new("label", "in"), "data"),
            ["label-text"] = new(new("label", "out"), new("text", "right"), "data"),
            ["text-agent"] = new(new("text", "out"), new("agent", "in"), "data"),
            ["result"] = outputFromAgent
                ? new(new("agent", "out"), new("output", "in"), "data")
                : new(new("done-text", "out"), new("output", "in"), "data")
        };
        if (!outputFromAgent) nodes["done-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("done") };
        data["item-text"] = elementType == WorkflowDataTypes.String
            ? new(new("each", "item"), new("text", "left"), "data")
            : throw new NotSupportedException();
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-runtime", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("each", "execute"), "execution"),
                ["body-start"] = new(new("each", "body"), new("body", "execute"), "execution"),
                ["process"] = new(new("body", "then"), new("agent", "execute"), "execution"),
                ["return"] = new(new("agent", "out"), new("each", "feedback"), "execution"),
                ["complete"] = new(new("each", "completed"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
    }

    private static async Task<(WorkflowExecutionResult Result, List<ExecutionEvent> Events)> RunAsync(
        WorkflowDefinition definition, RecordingAgent agent)
    {
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(agents: agent).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "for-each"), "start", null, evt =>
            {
                lock (events) events.Add(evt);
                return ValueTask.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(20));
        return (result, events);
    }

    [Fact]
    public async Task Each_item_runs_one_body_pass_in_ascending_order_with_its_zero_based_index()
    {
        var agent = new RecordingAgent();

        var (result, events) = await RunAsync(Workflow(WorkflowDataTypes.String, "[\"a\",\"b\",\"c\"]"), agent);

        Assert.False(result.Failed);
        Assert.Equal(["a0", "b1", "c2"], agent.Inputs);
        lock (events)
        {
            Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
            var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
            Assert.Contains("array-exhausted", stopped.Data);
            Assert.Equal(3, events.Select(evt => evt.ScopeId).Where(scope => scope is not null && scope.StartsWith("each/", StringComparison.Ordinal)).Distinct().Count());
        }
    }

    private sealed class PlainGate : IWorkflowExecutionGate
    {
        public List<string?> Nodes { get; } = [];
        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            lock (Nodes) Nodes.Add(nextNodeId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FrameGate : IWorkflowFrameExecutionGate
    {
        public List<(string? Node, string? Scope)> Boundaries { get; } = [];
        public ValueTask WaitForBoundaryAsync(WorkflowBoundary boundary, CancellationToken cancellationToken)
        {
            lock (Boundaries) Boundaries.Add((boundary.NextNodeId, boundary.ScopeId));
            return ValueTask.CompletedTask;
        }
        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The frame-aware overload must be preferred.");
    }

    private static Task<string> RunGatedAsync(WorkflowDefinition definition, IWorkflowExecutionGate gate) =>
        new WorkflowRunner(agents: new RecordingAgent()).ExecuteControlledAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "for-each"), "start", _ => ValueTask.CompletedTask, gate)
            .WaitAsync(TimeSpan.FromSeconds(20));

    [Fact]
    public async Task A_frame_aware_gate_receives_each_boundary_with_its_iteration_scope()
    {
        var gate = new FrameGate();

        await RunGatedAsync(Workflow(WorkflowDataTypes.String, "[\"a\",\"b\",\"c\"]"), gate);

        Assert.Equal([("input", null), ("each", null), ("body", "each/1"), ("agent", "each/1"), ("each", "each/1"),
            ("body", "each/2"), ("agent", "each/2"), ("each", "each/2"), ("body", "each/3"), ("agent", "each/3"),
            ("each", "each/3"), ("output", "each/3"), (null, null)], gate.Boundaries);
    }

    [Fact]
    public async Task A_gate_that_only_implements_the_original_boundary_method_still_works()
    {
        var gate = new PlainGate();

        await RunGatedAsync(Workflow(WorkflowDataTypes.String, "[\"a\",\"b\"]"), gate);

        Assert.Equal(["input", "each", "body", "agent", "each", "body", "agent", "each", "output", null], gate.Nodes);
    }

    [Fact]
    public async Task An_empty_array_skips_the_body_and_continues_on_completed()
    {
        var agent = new RecordingAgent();

        var (result, events) = await RunAsync(Workflow(WorkflowDataTypes.String, "[]", outputFromAgent: false), agent);

        Assert.False(result.Failed);
        Assert.Empty(agent.Inputs);
        lock (events)
        {
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.Contains("array-exhausted", Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped).Data);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "output");
        }
    }

    [Fact]
    public async Task A_failing_item_stops_later_items_and_fails_the_run()
    {
        var agent = new RecordingAgent(input => input == "b1" ? "boom" : null);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(Workflow(WorkflowDataTypes.String, "[\"a\",\"b\",\"c\"]"), agent));

        Assert.Contains("agent", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["a0", "b1"], agent.Inputs);
    }

    [Fact]
    public void Checkpoint_recovery_rejects_a_workflow_containing_a_for_each()
    {
        var failure = Assert.ThrowsAny<InvalidOperationException>(() =>
            WorkflowRunner.ValidateCheckpointGraph(Workflow(WorkflowDataTypes.String, "[\"a\"]"), allowLoopCheckpointRecovery: true));

        Assert.NotNull(failure);
    }

    [Fact]
    public void Checkpoint_recovery_rejects_a_workflow_containing_a_for_each_break()
    {
        Assert.ThrowsAny<InvalidOperationException>(() =>
            WorkflowRunner.ValidateCheckpointGraph(BreakWorkflow(), allowLoopCheckpointRecovery: true));
    }

    /// <summary>Outer For Each over ["x","y"]; each pass runs an inner For Each over ["1","2"] whose body records outer+inner item.</summary>
    private static WorkflowDefinition NestedWorkflow()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["outer-items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"x\",\"y\"]") },
            ["inner-items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"1\",\"2\"]") },
            ["outer"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["inner"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["outer-body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["inner-body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["text"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["done-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("done") },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["outer-array"] = new(new("outer-items", "out"), new("outer", "array"), "data"),
            ["inner-array"] = new(new("inner-items", "out"), new("inner", "array"), "data"),
            ["outer-item"] = new(new("outer", "item"), new("text", "left"), "data"),
            ["inner-item"] = new(new("inner", "item"), new("text", "right"), "data"),
            ["text-agent"] = new(new("text", "out"), new("agent", "in"), "data"),
            ["result"] = new(new("done-text", "out"), new("output", "in"), "data")
        };
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-nested", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("outer", "execute"), "execution"),
                ["outer-start"] = new(new("outer", "body"), new("outer-body", "execute"), "execution"),
                ["enter-inner"] = new(new("outer-body", "then"), new("inner", "execute"), "execution"),
                ["inner-start"] = new(new("inner", "body"), new("inner-body", "execute"), "execution"),
                ["process"] = new(new("inner-body", "then"), new("agent", "execute"), "execution"),
                ["inner-return"] = new(new("agent", "out"), new("inner", "feedback"), "execution"),
                ["outer-return"] = new(new("inner", "completed"), new("outer", "feedback"), "execution"),
                ["complete"] = new(new("outer", "completed"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
    }

    [Fact]
    public async Task A_for_each_nested_in_a_for_each_runs_its_full_inner_array_for_every_outer_item()
    {
        var agent = new RecordingAgent();

        var (result, events) = await RunAsync(NestedWorkflow(), agent);

        Assert.False(result.Failed);
        Assert.Equal(["x1", "x2", "y1", "y2"], agent.Inputs);
        lock (events)
        {
            Assert.Equal(6, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopStopped));
        }
    }

    /// <summary>For Each over [a,b,c]; the body breaks when the item contains "b", otherwise runs the agent.</summary>
    private static WorkflowDefinition BreakWorkflow()
    {
        var definition = Workflow(WorkflowDataTypes.String, "[\"a\",\"b\",\"c\"]", outputFromAgent: false);
        return definition with
        {
            Nodes = definition.Nodes
                .SetItem("cond", new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "b") })
                .SetItem("break", new(NodeTypes.Break, "1.0.0")),
            Connections = definition.Connections.Add("item-cond", new(new("each", "item"), new("cond", "in"), "data")),
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("process")
                .Add("body-cond", new(new("body", "then"), new("cond", "execute"), "execution"))
                .Add("cond-break", new(new("cond", "true"), new("break", "execute"), "execution"))
                .Add("cond-agent", new(new("cond", "false"), new("agent", "execute"), "execution"))
        };
    }

    [Fact]
    public async Task A_break_ends_the_for_each_early_and_continues_on_completed()
    {
        var agent = new RecordingAgent();

        var (result, events) = await RunAsync(BreakWorkflow(), agent);

        Assert.False(result.Failed);
        Assert.Equal(["a0"], agent.Inputs);
        lock (events)
        {
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
            var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
            Assert.Equal("{\"reason\":\"break\",\"iterations\":2}", stopped.Data);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "output");
        }
    }

    [Fact]
    public async Task A_break_in_an_inner_for_each_exits_only_the_inner_loop()
    {
        var nested = NestedWorkflow();
        var definition = nested with
        {
            Nodes = nested.Nodes
                .SetItem("cond", new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "2") })
                .SetItem("break", new(NodeTypes.Break, "1.0.0")),
            Connections = nested.Connections.Add("inner-cond", new(new("inner", "item"), new("cond", "in"), "data")),
            ExecutionConnections = nested.ExecutionConnections!
                .Remove("process")
                .Add("body-cond", new(new("inner-body", "then"), new("cond", "execute"), "execution"))
                .Add("cond-break", new(new("cond", "true"), new("break", "execute"), "execution"))
                .Add("cond-agent", new(new("cond", "false"), new("agent", "execute"), "execution"))
        };
        var agent = new RecordingAgent();

        var (result, events) = await RunAsync(definition, agent);

        Assert.False(result.Failed);
        Assert.Equal(["x1", "y1"], agent.Inputs);
        lock (events)
        {
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopStopped && evt.Data!.Contains("\"reason\":\"break\"")));
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped && evt.NodeId == "outer" && evt.Data!.Contains("array-exhausted"));
        }
    }

    [Fact]
    public async Task A_region_inserted_by_the_graph_editor_runs_two_passes_and_returns_the_input()
    {
        var baseline = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "inserted-for-each", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("data-path", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("execution-path", new(new("input", "then"), new("output", "execute"), "execution"))
        };
        var inserted = GraphEditing.InsertForEachAtExecutionConnection(baseline, "execution-path");

        var (result, events) = await RunAsync(inserted, new RecordingAgent());

        Assert.False(result.Failed);
        lock (events)
        {
            Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            Assert.Contains("array-exhausted", Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped).Data);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "output");
        }
    }
}
