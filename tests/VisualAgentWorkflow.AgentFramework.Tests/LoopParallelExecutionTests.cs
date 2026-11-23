using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class LoopParallelExecutionTests
{
    [Fact]
    public async Task Feedback_scheduler_binds_each_graph_owned_array_item_and_drains_join_before_next_item()
    {
        var definition = LoopWithParallelBodyAndArrayParameters(includeAgentPrefix: true);
        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "loop.feedback.root");
        var revision = WorkflowRevision.CreateProofOnly(definition, "feedback-array-iteration-proof");
        var executor = new HeldArrayFeedbackAgentExecutor();
        var events = new ConcurrentQueue<ExecutionEvent>();

        var execution = new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "loop", "items", "index", WorkflowDataTypes.String,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, prefixAgentId: "prefix-agent");

        await Task.WhenAll(executor.FirstLeftBranchStarted.Task, executor.FirstRightBranchStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(executor.Invocations, call => call.NodeId == "agent-a");
        Assert.Single(executor.Invocations, call => call.NodeId == "agent-b");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "loop/2");

        executor.ReleaseFirstLeftBranch.TrySetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Failed);
        Assert.Equal(ExecutionEventKinds.Completed, events.Last().Kind);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, executor.Invocations
            .Where(call => call.NodeId == "agent-a").Select(call => call.Input));
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, executor.Invocations
            .Where(call => call.NodeId == "agent-b").Select(call => call.Input));
        var bodyAgentInputs = executor.Invocations.Where(call => call.NodeId == "body-agent")
            .Select(call => call.Input).ToArray();
        Assert.Equal(3, bodyAgentInputs.Length);
        Assert.Equal("continue", bodyAgentInputs[0]);
        Assert.Single(executor.Invocations, call => call.NodeId == "prefix-agent");
        var recordedEvents = events.ToArray();
        var prefixCompleted = Array.FindIndex(recordedEvents, evt =>
            evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "prefix-agent");
        var loopStarted = Array.FindIndex(recordedEvents, evt =>
            evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "loop");
        Assert.True(prefixCompleted >= 0);
        Assert.True(loopStarted > prefixCompleted);
        foreach (var call in executor.Invocations.Where(call => call.NodeId is "agent-a" or "agent-b"))
        {
            var expectedIndex = Array.IndexOf(new[] { "alpha", "beta", "gamma" }, call.Input);
            Assert.Equal(expectedIndex.ToString(), call.Parameters!["index"]);
        }
        Assert.Equal(6, events.Count(evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
            evt.NodeId == "array-get"));

        var starts = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        var completions = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted).ToArray();
        Assert.Equal(new[] { "loop/1", "loop/2", "loop/3" }, starts.Select(evt => evt.ScopeId));
        Assert.Equal(starts.Select(evt => evt.ScopeId), completions.Select(evt => evt.ScopeId));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinActivated));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(evt => evt.Sequence));
        Assert.Single(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Feedback_scheduler_failure_does_not_admit_later_array_items()
    {
        var revision = WorkflowRevision.Create(LoopWithParallelBodyAndArrayParameters(),
            "feedback-array-iteration-failure-proof");
        var executor = new HeldArrayFeedbackAgentExecutor(failOnInput: "beta");
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "loop", "items", "index", WorkflowDataTypes.String,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Equal(new[] { "alpha", "beta" }, executor.Invocations
            .Where(call => call.NodeId == "agent-a").Select(call => call.Input));
        Assert.DoesNotContain(executor.Invocations, call => call.Input == "gamma");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "loop/3");
        Assert.Equal(ExecutionEventKinds.Failed, events.Last().Kind);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(evt => evt.Sequence));
    }

    [Fact]
    public async Task Feedback_scheduler_graph_data_empty_array_skips_body_nodes()
    {
        var revision = WorkflowRevision.Create(LoopWithParallelBodyAndArrayParameters(),
            "feedback-array-iteration-empty-proof");
        var executor = new HeldArrayFeedbackAgentExecutor();
        var events = new ConcurrentQueue<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[]", "loop", "items", "index", WorkflowDataTypes.String,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("[]", result.Output);
        Assert.Empty(executor.Invocations);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId is "body-agent" or "agent-a" or "agent-b");
        Assert.Equal(ExecutionEventKinds.Completed, events.Last().Kind);
    }

    [Fact]
    public async Task Feedback_scheduler_once_only_agent_prefix_runs_for_empty_array()
    {
        var definition = LoopWithParallelBodyAndArrayParameters(includeAgentPrefix: true);
        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "loop.feedback.root");
        var revision = WorkflowRevision.CreateProofOnly(definition, "feedback-array-agent-prefix-empty-proof");
        var executor = new HeldArrayFeedbackAgentExecutor();
        var events = new ConcurrentQueue<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[]", "loop", "items", "index", WorkflowDataTypes.String,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, prefixAgentId: "prefix-agent");

        Assert.False(result.Failed);
        Assert.Equal("[]", result.Output);
        Assert.Single(executor.Invocations, call => call.NodeId == "prefix-agent");
        Assert.DoesNotContain(executor.Invocations, call => call.NodeId is "body-agent" or "agent-a" or "agent-b");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.Equal(ExecutionEventKinds.Completed, events.Last().Kind);
    }

    [Fact]
    public async Task Feedback_scheduler_prefix_failure_prevents_loop_admission()
    {
        var definition = LoopWithParallelBodyAndArrayParameters(includeAgentPrefix: true);
        var revision = WorkflowRevision.CreateProofOnly(definition, "feedback-array-agent-prefix-failure-proof");
        var executor = new HeldArrayFeedbackAgentExecutor(failOnNodeId: "prefix-agent");
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\"]", "loop", "items", "index", WorkflowDataTypes.String,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, prefixAgentId: "prefix-agent"));

        Assert.Single(executor.Invocations, call => call.NodeId == "prefix-agent");
        Assert.DoesNotContain(executor.Invocations, call => call.NodeId is "body-agent" or "agent-a" or "agent-b");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "loop");
        Assert.Equal(ExecutionEventKinds.Failed, events.Last().Kind);
    }

    [Fact]
    public async Task Feedback_scheduler_prefix_cancellation_prevents_loop_admission()
    {
        using var cancellation = new CancellationTokenSource();
        var definition = LoopWithParallelBodyAndArrayParameters(includeAgentPrefix: true);
        var revision = WorkflowRevision.CreateProofOnly(definition, "feedback-array-agent-prefix-cancellation-proof");
        var executor = new HeldArrayFeedbackAgentExecutor(blockPrefix: true);
        var events = new ConcurrentQueue<ExecutionEvent>();

        var execution = new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\"]", "loop", "items", "index", WorkflowDataTypes.String,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellation.Token,
                prefixAgentId: "prefix-agent");
        await executor.PrefixStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Single(executor.Invocations, call => call.NodeId == "prefix-agent");
        Assert.DoesNotContain(executor.Invocations, call => call.NodeId is "body-agent" or "agent-a" or "agent-b");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "loop");
        Assert.Equal(ExecutionEventKinds.Cancelled, events.Last().Kind);
    }

    [Fact]
    public async Task Feedback_scheduler_cancellation_does_not_admit_later_array_items()
    {
        using var cancellation = new CancellationTokenSource();
        var revision = WorkflowRevision.Create(LoopWithParallelBodyAndArrayParameters(),
            "feedback-array-iteration-cancellation-proof");
        var executor = new HeldArrayFeedbackAgentExecutor();
        var events = new ConcurrentQueue<ExecutionEvent>();

        var execution = new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "loop", "items", "index", WorkflowDataTypes.String,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        await Task.WhenAll(executor.FirstLeftBranchStarted.Task, executor.FirstRightBranchStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Equal(new[] { "alpha" }, executor.Invocations
            .Where(call => call.NodeId == "agent-a").Select(call => call.Input));
        Assert.DoesNotContain(executor.Invocations, call => call.Input is "beta" or "gamma");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "loop/2");
        Assert.Equal(ExecutionEventKinds.Cancelled, events.Last().Kind);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(evt => evt.Sequence));
    }

    [Fact]
    public async Task Feedback_scheduler_stops_at_the_shared_root_loop_pass_budget()
    {
        var revision = WorkflowRevision.Create(LoopWithParallelBodyAndArrayParameters(),
            "feedback-array-iteration-budget-proof");
        var executor = new HeldArrayFeedbackAgentExecutor();
        executor.ReleaseFirstLeftBranch.TrySetResult();
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "loop", "items", "index", WorkflowDataTypes.String,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, maximumLoopPasses: 2));

        Assert.Equal(new[] { "alpha", "beta" }, executor.Invocations
            .Where(call => call.NodeId == "agent-a").Select(call => call.Input));
        Assert.DoesNotContain(executor.Invocations, call => call.Input == "gamma");
        Assert.Equal(new[] { "loop/1", "loop/2" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).Select(evt => evt.ScopeId));
        Assert.Equal(ExecutionEventKinds.Failed, events.Last().Kind);
        Assert.Contains("Loop body-pass limit", events.Last().Data, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(evt => evt.Sequence));
    }

    [Fact]
    public async Task Feedback_scheduler_recorder_rejection_does_not_admit_later_items_or_fabricate_a_terminal()
    {
        var revision = WorkflowRevision.Create(LoopWithParallelBodyAndArrayParameters(),
            "feedback-array-iteration-recorder-proof");
        var executor = new HeldArrayFeedbackAgentExecutor();
        executor.ReleaseFirstLeftBranch.TrySetResult();
        var acceptedEvents = new List<ExecutionEvent>();
        var rejected = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: executor)
            .ExecuteAgentFrameworkArrayIterationFeedbackProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "loop", "items", "index", WorkflowDataTypes.String,
                evt =>
                {
                    if (!rejected && evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.ScopeId == "loop/2")
                    {
                        rejected = true;
                        return ValueTask.FromException(new IOException("Controlled feedback evidence rejection."));
                    }
                    acceptedEvents.Add(evt);
                    return ValueTask.CompletedTask;
                }));

        Assert.True(rejected);
        Assert.DoesNotContain(executor.Invocations, call => call.Input is "beta" or "gamma");
        Assert.DoesNotContain(acceptedEvents, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "loop/3");
        Assert.DoesNotContain(acceptedEvents, evt => evt.NodeId is null &&
            evt.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
                ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated);
        Assert.Equal(Enumerable.Range(1, acceptedEvents.Count).Select(value => (long)value),
            acceptedEvents.Select(evt => evt.Sequence));
    }

    [Fact]
    public async Task Public_runner_repeats_a_joined_loop_body_to_its_iteration_cap()
    {
        var definition = LoopWithParallelBody();
        var executor = new ParallelAgentExecutor();
        var events = new ConcurrentQueue<ExecutionEvent>();

        string result;
        try
        {
            result = await new WorkflowRunner(agents: executor).ExecuteAsync(Guid.NewGuid(),
                WorkflowRevision.Create(definition, "loop-parallel"), "continue",
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(string.Join("\n", events.Select(evt =>
                $"{evt.Sequence} {evt.Kind} {evt.NodeId} {evt.ScopeId} {evt.Data}")), exception);
        }

        using var output = JsonDocument.Parse(result);
        Assert.Contains("continue:A", output.RootElement.GetProperty("a").GetString());
        Assert.EndsWith(":A", output.RootElement.GetProperty("a").GetString());
        Assert.Contains("continue:B", output.RootElement.GetProperty("b").GetString());
        Assert.EndsWith(":B", output.RootElement.GetProperty("b").GetString());
        Assert.Equal(4, executor.CallsByNode.Values.Sum());
        Assert.Equal(2, executor.CallsByNode["agent-a"]);
        Assert.Equal(2, executor.CallsByNode["agent-b"]);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var iterationStarts = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        Assert.Equal(["loop/1", "loop/2"], iterationStarts.Select(evt => evt.ScopeId).Order(StringComparer.Ordinal));
        var forkActivations = events.Where(evt => evt.Kind == ExecutionEventKinds.ParallelJoinActivated).ToArray();
        Assert.Equal(2, forkActivations.Length);
        var activationIds = forkActivations.Select(evt =>
        {
            using var evidence = JsonDocument.Parse(evt.Data!);
            return evidence.RootElement.GetProperty("ForkActivationId").GetGuid();
        });
        Assert.Equal(2, activationIds.Distinct().Count());
        Assert.Equal(2, events.Where(evt => evt.Kind == ExecutionEventKinds.ParallelJoinArrival)
            .Select(evt => evt.ScopeId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress));
        Assert.Equal(ExecutionEventKinds.Completed, events.Last().Kind);
    }

    [Fact]
    public async Task Public_runner_does_not_complete_loop_iteration_when_a_parallel_branch_fails()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(
            agents: new ParallelAgentExecutor(failedNodeId: "agent-b"))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(LoopWithParallelBody(), "loop-parallel-failure"),
                "continue", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.Equal(ExecutionEventKinds.Failed, events.Last().Kind);
    }

    [Fact]
    public async Task Public_runner_does_not_complete_loop_iteration_when_a_parallel_branch_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var runner = new WorkflowRunner(agents: new ParallelAgentExecutor(
            cancelledNodeId: "agent-b", cancellationSource: cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(LoopWithParallelBody(), "loop-parallel-cancellation"), "continue",
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellation.Token));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.Equal(ExecutionEventKinds.Cancelled, events.Last().Kind);
    }

    private static WorkflowDefinition LoopWithParallelBody()
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("a", "A", "anchor-a"),
            new ParallelBranchDescriptor("b", "B", "anchor-b"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("loop", new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 2) })
            .Add("body", new(NodeTypes.LoopBody, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) })
            .Add("anchor-a", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "a") })
            .Add("anchor-b", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "b") })
            .Add("agent-a", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "A", [])))
            .Add("agent-b", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "B", [])))
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-loop", Edge("input", "then", "loop", "execute"))
            .Add("loop-body", Edge("loop", "body", "body", "execute"))
            .Add("body-fork", Edge("body", "then", "fork", "execute"))
            .Add("fork-a", Edge("fork", ParallelBranchLimits.PinId("a"), "anchor-a", "execute"))
            .Add("fork-b", Edge("fork", ParallelBranchLimits.PinId("b"), "anchor-b", "execute"))
            .Add("anchor-a-agent", Edge("anchor-a", "then", "agent-a", "execute"))
            .Add("anchor-b-agent", Edge("anchor-b", "then", "agent-b", "execute"))
            .Add("agent-a-join", Edge("agent-a", "out", "join", ParallelBranchLimits.PinId("a")))
            .Add("agent-b-join", Edge("agent-b", "out", "join", ParallelBranchLimits.PinId("b")))
            .Add("join-loop", Edge("join", "then", "loop", "feedback"))
            .Add("loop-output", Edge("loop", "done", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output", new(new("join", "out"), new("output", "in"), "data"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "loop-parallel", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition LoopWithParallelBodyAndArrayParameters(bool includeAgentPrefix = false)
    {
        var definition = LoopWithParallelBody();
        var arrayType = WorkflowDataTypes.ArrayOf(WorkflowDataTypes.String);
        var nodes = definition.Nodes
            .SetItem("body-agent", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "body side effect", [])))
            .SetItem("array-value", new(NodeTypes.WorkflowValue, "1.0.0")
            {
                ParameterId = "items",
                OutputDataType = arrayType
            })
            .SetItem("index-value", new(NodeTypes.WorkflowValue, "1.0.0")
            {
                ParameterId = "index",
                OutputDataType = WorkflowDataTypes.Integer
            })
            .SetItem("array-get", new(NodeTypes.ArrayGet, "1.0.0")
            {
                ArrayElementType = WorkflowDataTypes.String
            })
            .SetItem("output", definition.Nodes["output"] with { OutputDataType = arrayType });
        var execution = definition.ExecutionConnections!
            .Remove("body-fork")
            .Add("body-agent", Edge("body", "then", "body-agent", "execute"))
            .Add("body-fork", Edge("body-agent", "out", "fork", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("array-get-array", new(new("array-value", "out"), new("array-get", "array"), "data"))
            .Add("array-get-index", new(new("index-value", "out"), new("array-get", "index"), "data"))
            .Add("agent-a-item", new(new("array-get", "item"), new("agent-a", "in"), "data"))
            .Add("agent-b-item", new(new("array-get", "item"), new("agent-b", "in"), "data"))
            .Add("array-output", new(new("array-value", "out"), new("output", "in"), "data"));
        var prefixNodes = nodes;
        if (includeAgentPrefix)
        {
            prefixNodes = prefixNodes.Add("prefix-agent",
                new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "run once before iteration", [])));
            execution = execution.Remove("input-loop")
                .Add("input-prefix-agent", Edge("input", "then", "prefix-agent", "execute"))
                .Add("prefix-agent-loop", Edge("prefix-agent", "out", "loop", "execute"));
            data = data.Add("input-prefix-agent",
                new(new("input", "out"), new("prefix-agent", "in"), "data"));
        }
        return definition with
        {
            WorkflowId = "feedback-array-iteration-proof",
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("items", new("Items", "[]") { Type = arrayType })
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer }),
            Nodes = prefixNodes.SetItem("loop", definition.Nodes["loop"] with
            {
                Loop = new(new("contains", "continue"), 3)
            }),
            Connections = data,
            ExecutionConnections = execution
        };
    }

    private static ConnectionDefinition Edge(string source, string sourcePin, string target, string targetPin) =>
        new(new(source, sourcePin), new(target, targetPin), "execution");

    private sealed class ParallelAgentExecutor(string? failedNodeId = null, string? cancelledNodeId = null,
        CancellationTokenSource? cancellationSource = null) : IAgentNodeExecutor
    {
        private readonly ConcurrentDictionary<string, int> callsByNode = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, int> CallsByNode => callsByNode;

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("test.parallel", "Test", "test", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            callsByNode.AddOrUpdate(invocation.NodeId, 1, (_, count) => count + 1);
            if (invocation.NodeId == failedNodeId)
                throw new InvalidOperationException("Branch fixture failure.");
            if (invocation.NodeId == cancelledNodeId)
            {
                cancellationSource!.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }
            return Task.FromResult($"{input}:{invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions}");
        }
    }

    private sealed class HeldArrayFeedbackAgentExecutor(string? failOnInput = null,
        string? failOnNodeId = null, bool blockPrefix = false) : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<AgentInvocation> invocations = new();
        private readonly TaskCompletionSource firstLeftBranchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource firstRightBranchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstLeftBranch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource prefixStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releasePrefix = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentInvocation[] Invocations => [.. invocations];
        public TaskCompletionSource FirstLeftBranchStarted => firstLeftBranchStarted;
        public TaskCompletionSource FirstRightBranchStarted => firstRightBranchStarted;
        public TaskCompletionSource ReleaseFirstLeftBranch => releaseFirstLeftBranch;
        public TaskCompletionSource PrefixStarted => prefixStarted;

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("test.feedback-array", "Feedback Array", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            invocations.Enqueue(new(invocation.NodeId, input, invocation.Parameters));
            if (invocation.NodeId == "body-agent") return "continue";
            if (invocation.NodeId == "prefix-agent")
            {
                prefixStarted.TrySetResult();
                if (blockPrefix) await releasePrefix.Task.WaitAsync(cancellationToken);
                if (failOnNodeId == invocation.NodeId)
                    throw new InvalidOperationException("Controlled prefix Agent failure.");
                return "continue";
            }
            if (failOnNodeId == invocation.NodeId)
                throw new InvalidOperationException("Controlled Agent failure.");
            if (input == failOnInput) throw new InvalidOperationException("Controlled item failure.");
            if (failOnInput is null && invocation.NodeId == "agent-a" && input == "alpha")
            {
                firstLeftBranchStarted.TrySetResult();
                await releaseFirstLeftBranch.Task.WaitAsync(cancellationToken);
            }
            if (invocation.NodeId == "agent-b" && input == "alpha")
                firstRightBranchStarted.TrySetResult();
            return input;
        }

        public sealed record AgentInvocation(string NodeId, string Input,
            IReadOnlyDictionary<string, string>? Parameters);
    }
}
