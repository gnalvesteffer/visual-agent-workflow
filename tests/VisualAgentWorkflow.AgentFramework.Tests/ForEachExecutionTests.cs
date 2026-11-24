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
}
