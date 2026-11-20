using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public class WorkflowRunnerTests
{
    private static WorkflowRevision Revision() => WorkflowRevision.Create(new("1.0", "demo", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["uppercase"] = new(NodeTypes.Uppercase, "1.0.0"),
            ["reverse"] = new(NodeTypes.Reverse, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["a"] = new(new("input", "out"), new("uppercase", "in"), "data"),
            ["b"] = new(new("uppercase", "out"), new("reverse", "in"), "data"),
            ["c"] = new(new("reverse", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary()), "test");

    [Fact]
    public async Task ExecutesUsingFrameworkAndRecordsActualTransfers()
    {
        var revision = Revision();
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();
        var output = await new WorkflowRunner().ExecuteAsync(runId, revision, "Hello 🌍", e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.Equal("🌍 OLLEH", output);
        Assert.Equal(ExecutionEventKinds.Started, events[0].Kind);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.All(events, e => { Assert.Equal(runId, e.RunId); Assert.Equal(revision.RevisionId, e.RevisionId); });
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(4, events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).Select(e => e.InvocationId).Distinct().Count());
        var transfers = events.Where(e => e.Kind == ExecutionEventKinds.Transfer).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, transfers.Select(e => e.ConnectionId));
        Assert.Equal(new[] { "Hello 🌍", "HELLO 🌍", "🌍 OLLEH" }, transfers.Select(e => e.Data));
    }

    [Fact]
    public async Task ExecutesStrictlyOwnedNestedForkJoinThroughPublicRunnerPath()
    {
        var revision = WorkflowRevision.Create(NestedForkDefinition(), "nested-public-runner");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: new EchoNestedAgentExecutor())
            .ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "research input", null,
                evt => { events.Add(evt); return ValueTask.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Failed);
        Assert.True(result.HasOutput);
        Assert.Contains("research input", result.Output, StringComparison.Ordinal);
        Assert.Equal(4, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId is "deep-join" or "inner-join" or "outer-join" or "post-join"));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "deep-join");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "inner-join");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "outer-join");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "post-join");
    }

    [Fact]
    public async Task ExecutesNestedForkJoinAuthoredOnAnExplicitExecutionEdge()
    {
        var original = NestedForkDefinition();
        var definition = GraphEditing.InsertParallelRegionAtExecutionConnection(original, "inner-b-agent");
        var insertedForkId = definition.Nodes.Keys.Except(original.Nodes.Keys, StringComparer.Ordinal)
            .Single(id => definition.Nodes[id].Type == NodeTypes.ParallelFork);
        var insertedJoinId = definition.Nodes.Keys.Except(original.Nodes.Keys, StringComparer.Ordinal)
            .Single(id => definition.Nodes[id].Type == NodeTypes.ParallelJoin);
        var revision = WorkflowRevision.Create(definition, "nested-inserted-parallel-runner");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: new EchoNestedAgentExecutor())
            .ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "research input", null,
                evt => { events.Add(evt); return ValueTask.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Failed);
        Assert.True(result.HasOutput);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == insertedForkId);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == insertedJoinId);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "inner-join");
    }

    [Fact]
    public async Task NestedForkFailureDoesNotProduceAnyJoinedResult()
    {
        var revision = WorkflowRevision.Create(NestedForkDefinition(), "nested-public-runner-failure");
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new WorkflowRunner(agents: new NestedForkFailureAgentExecutor())
                .ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "research input", null,
                    evt => { events.Add(evt); return ValueTask.CompletedTask; })
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "deep-agent-x");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId is "deep-join" or "inner-join" or "outer-join" or "post-join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "output");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);
    }

    [Fact]
    public async Task NestedForkCancellationDoesNotProduceAnyJoinedResult()
    {
        var revision = WorkflowRevision.Create(NestedForkDefinition(), "nested-public-runner-cancellation");
        var events = new List<ExecutionEvent>();
        var agents = new NestedForkCancellationAgentExecutor();
        using var cancellation = new CancellationTokenSource();

        var execution = new WorkflowRunner(agents: agents)
            .ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "research input", null,
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        await agents.DeepBranchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId is "deep-join" or "inner-join" or "outer-join" or "post-join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "output");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Cancelled);
    }

    private static WorkflowDefinition NestedForkDefinition()
    {
        var outerBranches = ImmutableArray.Create(
            new ParallelBranchDescriptor("research", "Research", "research-anchor"),
            new ParallelBranchDescriptor("review", "Review", "review-anchor"));
        var innerBranches = ImmutableArray.Create(
            new ParallelBranchDescriptor("a", "Inner A", "inner-anchor-a"),
            new ParallelBranchDescriptor("b", "Inner B", "inner-anchor-b"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("outer-fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(outerBranches) })
            .Add("research-anchor", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("outer-fork", "research") })
            .Add("review-anchor", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("outer-fork", "review") })
            .Add("inner-fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(innerBranches) })
            .Add("inner-anchor-a", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("inner-fork", "a") })
            .Add("inner-anchor-b", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("inner-fork", "b") })
            .Add("deep-fork", new(NodeTypes.ParallelFork, "1.0.0")
            {
                ParallelFork = new([
                    new("x", "Deep X", "deep-anchor-x"),
                    new("y", "Deep Y", "deep-anchor-y")])
            })
            .Add("deep-anchor-x", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("deep-fork", "x") })
            .Add("deep-anchor-y", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("deep-fork", "y") })
            .Add("inner-agent-a", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-echo", null, "Inner A", [])))
            .Add("inner-agent-b", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-echo", null, "Inner B", [])))
            .Add("deep-agent-x", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-echo", null, "Deep X", [])))
            .Add("deep-agent-y", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-echo", null, "Deep Y", [])))
            .Add("review-agent", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-echo", null, "Review", [])))
            .Add("research-agent", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-echo", null, "Research", [])))
            .Add("deep-join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("deep-fork", ["x", "y"]) })
            .Add("inner-join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("inner-fork", ["a", "b"]) })
            .Add("outer-join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("outer-fork", ["research", "review"]) })
            .Add("post-fork", new(NodeTypes.ParallelFork, "1.0.0")
            {
                ParallelFork = new([
                    new("post_a", "Post A", "post-anchor-a"),
                    new("post_b", "Post B", "post-anchor-b")])
            })
            .Add("post-anchor-a", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("post-fork", "post_a") })
            .Add("post-anchor-b", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("post-fork", "post_b") })
            .Add("post-agent-a", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-echo", null, "Post A", [])))
            .Add("post-agent-b", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-echo", null, "Post B", [])))
            .Add("post-join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("post-fork", ["post_a", "post_b"]) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-inner-a", new(new("input", "out"), new("inner-agent-a", "in"), "data"))
            .Add("input-inner-b", new(new("input", "out"), new("inner-agent-b", "in"), "data"))
            .Add("input-deep-x", new(new("input", "out"), new("deep-agent-x", "in"), "data"))
            .Add("input-deep-y", new(new("input", "out"), new("deep-agent-y", "in"), "data"))
            .Add("input-research", new(new("input", "out"), new("research-agent", "in"), "data"))
            .Add("input-review", new(new("input", "out"), new("review-agent", "in"), "data"))
            .Add("input-post-a", new(new("input", "out"), new("post-agent-a", "in"), "data"))
            .Add("input-post-b", new(new("input", "out"), new("post-agent-b", "in"), "data"))
            .Add("post-result", new(new("post-join", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-outer-fork", Execution("input", "then", "outer-fork", "execute"))
            .Add("outer-research", Execution("outer-fork", "branch.research", "research-anchor", "execute"))
            .Add("outer-review", Execution("outer-fork", "branch.review", "review-anchor", "execute"))
            .Add("research-inner-fork", Execution("research-anchor", "then", "inner-fork", "execute"))
            .Add("inner-a", Execution("inner-fork", "branch.a", "inner-anchor-a", "execute"))
            .Add("inner-b", Execution("inner-fork", "branch.b", "inner-anchor-b", "execute"))
            .Add("inner-a-deep-fork", Execution("inner-anchor-a", "then", "deep-fork", "execute"))
            .Add("deep-x", Execution("deep-fork", "branch.x", "deep-anchor-x", "execute"))
            .Add("deep-y", Execution("deep-fork", "branch.y", "deep-anchor-y", "execute"))
            .Add("deep-x-agent", Execution("deep-anchor-x", "then", "deep-agent-x", "execute"))
            .Add("deep-y-agent", Execution("deep-anchor-y", "then", "deep-agent-y", "execute"))
            .Add("deep-x-join", Execution("deep-agent-x", "out", "deep-join", "branch.x"))
            .Add("deep-y-join", Execution("deep-agent-y", "out", "deep-join", "branch.y"))
            .Add("deep-join-inner-a", Execution("deep-join", "then", "inner-agent-a", "execute"))
            .Add("inner-b-agent", Execution("inner-anchor-b", "then", "inner-agent-b", "execute"))
            .Add("inner-a-join", Execution("inner-agent-a", "out", "inner-join", "branch.a"))
            .Add("inner-b-join", Execution("inner-agent-b", "out", "inner-join", "branch.b"))
            .Add("inner-join-research", Execution("inner-join", "then", "research-agent", "execute"))
            .Add("research-outer-join", Execution("research-agent", "out", "outer-join", "branch.research"))
            .Add("review-agent-outer-join", Execution("review-anchor", "then", "review-agent", "execute"))
            .Add("review-outer-join", Execution("review-agent", "out", "outer-join", "branch.review"))
            .Add("outer-post-fork", Execution("outer-join", "then", "post-fork", "execute"))
            .Add("post-a", Execution("post-fork", "branch.post_a", "post-anchor-a", "execute"))
            .Add("post-b", Execution("post-fork", "branch.post_b", "post-anchor-b", "execute"))
            .Add("post-anchor-a-agent", Execution("post-anchor-a", "then", "post-agent-a", "execute"))
            .Add("post-anchor-b-agent", Execution("post-anchor-b", "then", "post-agent-b", "execute"))
            .Add("post-agent-a-join", Execution("post-agent-a", "out", "post-join", "branch.post_a"))
            .Add("post-agent-b-join", Execution("post-agent-b", "out", "post-join", "branch.post_b"))
            .Add("post-output", Execution("post-join", "then", "output", "execute"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "nested-public-runner", "input", nodes, connections)
        {
            ExecutionConnections = execution
        };
    }

    private static ConnectionDefinition Execution(string sourceNode, string sourcePort, string targetNode, string targetPort) =>
        new(new(sourceNode, sourcePort), new(targetNode, targetPort), "execution");

    private sealed class EchoNestedAgentExecutor : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("test.nested-echo", "Nested echo", "test", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(input);
        }
    }

    private sealed class NestedForkFailureAgentExecutor : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("test.nested-echo", "Nested echo", "test", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return invocation.NodeId == "deep-agent-x"
                ? Task.FromException<string>(new InvalidOperationException("Nested branch failed."))
                : Task.FromResult(input);
        }
    }

    private sealed class NestedForkCancellationAgentExecutor : IAgentNodeExecutor
    {
        public TaskCompletionSource DeepBranchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("test.nested-echo", "Nested echo", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (invocation.NodeId == "deep-agent-x")
            {
                DeepBranchStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return input;
        }
    }

    [Fact]
    public async Task WorkflowValueNodePassesDefaultOrRunOverrideAsRecordedGraphData()
    {
        var definition = new WorkflowDefinition("1.6", "parameter-runtime", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["parameter"] = new(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "message" },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["a"] = new(new("input", "out"), new("parameter", "in"), "data"),
                ["b"] = new(new("parameter", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.Add("message", new("Message", "default"))
        };
        var revision = WorkflowRevision.Create(definition, "test");
        var events = new List<ExecutionEvent>();

        var defaultOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "ignored",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        Assert.Equal("default", defaultOutput);
        events.Clear();

        var output = await new WorkflowRunner().ExecuteWithParametersAsync(Guid.NewGuid(), revision, "ignored",
            new Dictionary<string, string> { ["message"] = "override" }, evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("override", output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "parameter" && evt.Data == "override");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "b" && evt.Data == "override");
    }

    [Fact]
    public async Task TextTemplateNodeRendersInputAndResolvedParametersAsRecordedData()
    {
        var definition = new WorkflowDefinition("1.7", "text-template-runtime", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["format"] = new(NodeTypes.TextTemplate, "1.0.0")
                {
                    TextTemplate = new([new("literal", "Tell "), new("input", ""), new("literal", " to greet "), new("parameter", "name")])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["a"] = new(new("input", "out"), new("format", "in"), "data"),
                ["b"] = new(new("format", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.Add("name", new("Name", "Ada"))
        };
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithParametersAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "answer the question", new Dictionary<string, string> { ["name"] = "Grace" },
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Tell answer the question to greet Grace", result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "format" && evt.Data == result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "b" && evt.Data == result);
    }

    [Theory]
    [InlineData("{\"user\":{\"name\":\"Ada\"},\"items\":[{\"id\":7}]}", "/user/name", "Ada")]
    [InlineData("{\"items\":[{\"id\":7}]}", "/items/0/id", "7")]
    [InlineData("{\"name\":\"Ada\",\"age\":3}", "", "{\"name\":\"Ada\",\"age\":3}")]
    public async Task JsonInputAndPointerSelectorExecuteAndRecordSelectedString(string input, string pointer, string expected)
    {
        var definition = new WorkflowDefinition("1.9", "json-run", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["select"] = new(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new(pointer) },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("select", "in"), "data"),
                ["text"] = new(new("select", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var revision = WorkflowRevision.Create(definition, "test");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, input,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(expected, result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "select" && evt.Data == expected);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "text" && evt.Data == expected);
    }

    [Fact]
    public async Task JsonProjectionBuildsBoundedTypedObjectAndRecordsProjectedValue()
    {
        const string expected = "{\"person\":\"Ada\",\"amount\":42}";
        var definition = new WorkflowDefinition("1.13", "json-project-run", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new("person", "/customer/name"), new("amount", "/orders/0/amount")])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("project", "in"), "data"),
                ["result"] = new(new("project", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "{\"customer\":{\"name\":\"Ada\"},\"orders\":[{\"amount\":42,\"secret\":\"omit\"}]}",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(expected, result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "project" && evt.Data == expected);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "result" && evt.Data == expected);
        Assert.DoesNotContain("secret", events.Single(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "project").Data);
    }

    [Fact]
    public async Task JsonProjectionRejectsOutputBeyondPayloadLimitAndRecordsFailure()
    {
        var definition = new WorkflowDefinition("1.13", "json-project-limit", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new(new string('x', JsonProjectionLimits.MaximumNameCharacters), "/value")])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("project", "in"), "data"),
                ["result"] = new(new("project", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var events = new List<ExecutionEvent>();
        var input = "{\"value\":\"" + new string('x', 16_260) + "\"}";

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), input,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "project");
        Assert.Equal(ExecutionEventKinds.Failed, events[^1].Kind);
    }

    [Fact]
    public async Task TypedJsonProjectionChecksMappedValueKindsBeforeEmittingOutput()
    {
        var definition = new WorkflowDefinition("1.15", "json-projection-types", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new JsonProjectionField("name", "/name") { ValueType = "string" }, new JsonProjectionField("items", "/items") { ValueType = "array" }])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("project", "in"), "data"),
                ["result"] = new(new("project", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "{\"name\":\"Ada\",\"items\":[1,2]}", evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("{\"name\":\"Ada\",\"items\":[1,2]}", output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "project" && evt.Data == output);

        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "{\"name\":42,\"items\":[]}",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "project" && evt.Data!.Contains("expected string but received number", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecursiveJsonProjectionMapsNestedObjectsAndArrayItemsWithPlaybackEvidence()
    {
        var schema = new JsonValueSchema("object")
        {
            Properties = System.Collections.Immutable.ImmutableArray.Create(
                new JsonValueProperty("displayName", "/fullName", new("string")),
                new JsonValueProperty("active", "/metadata/active", new("boolean")) { Optional = true },
                new JsonValueProperty("orders", "/orders", new("array")
                {
                    Items = new("object")
                    {
                        Properties = System.Collections.Immutable.ImmutableArray.Create(
                            new JsonValueProperty("orderId", "/id", new("integer")),
                            new JsonValueProperty("total", "/amount", new("number")))
                    }
                }))
        };
        var definition = new WorkflowDefinition("1.16", "recursive-json-project", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new JsonProjectionField("customer", "/customer") { ValueSchema = schema }])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("project", "in"), "data"),
                ["result"] = new(new("project", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "{\"customer\":{\"fullName\":\"Ada\",\"metadata\":{\"active\":true},\"secret\":\"drop\",\"orders\":[{\"id\":7,\"amount\":12.5}]}}",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("{\"customer\":{\"displayName\":\"Ada\",\"active\":true,\"orders\":[{\"orderId\":7,\"total\":12.5}]}}", output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "project" && evt.Data == output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "result" && evt.Data == output);
        Assert.DoesNotContain("secret", output);

        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "{\"customer\":{\"metadata\":{},\"orders\":[]}}",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.Data!.Contains("Required mapped property 'customer.displayName' was not found.", StringComparison.Ordinal));

        var tooManyItems = "{\"customer\":{\"fullName\":\"Ada\",\"orders\":[" + string.Join(',', Enumerable.Repeat("{}", JsonProjectionLimits.MaximumArrayItems + 1)) + "]}}";
        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), tooManyItems,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.Data!.Contains("exceeds 64 items", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpandedPublishedWorkflowExecutesWithoutSourceCatalogOrRuntimePrefabLookup()
    {
        var source = Revision();
        var composition = WorkflowReuse.Insert(source.Definition, WorkflowLayout.Create(source.Definition), "input",
            new(1, source), WorkflowLayout.Create(source.Definition));
        var revision = WorkflowRevision.Create(composition.Definition, "consumer");
        var events = new List<ExecutionEvent>();
        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "abc", e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.Equal("ABC", output); // uppercase/reverse from the copy, followed by uppercase/reverse from the target.
        Assert.Equal(6, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(5, events.Count(e => e.Kind == ExecutionEventKinds.Transfer));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task CancellationRecordsTerminalOutcomeWithoutNodeExecution()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowRunner().ExecuteAsync(
            Guid.NewGuid(), Revision(), "input", e => { events.Add(e); return ValueTask.CompletedTask; }, cancellation.Token));
        Assert.Equal(new[] { ExecutionEventKinds.Started, ExecutionEventKinds.Cancelled }, events.Select(e => e.Kind));
    }

    [Fact]
    public async Task InvalidDefinitionNeverStartsAFrameworkRun()
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<ArgumentException>(() => new WorkflowRunner().ExecuteAsync(
            Guid.NewGuid(), Revision(), new string('x', 16_385), e => { events.Add(e); return ValueTask.CompletedTask; }));
        Assert.Empty(events);
    }
    [Fact]
    public async Task CompletionBoundaryWaitsWithoutRepeatingFinalNode()
    {
        var gate = new CompletionGate();
        var events = new List<ExecutionEvent>();
        var execution = new WorkflowRunner().ExecuteControlledAsync(Guid.NewGuid(), Revision(), "abc",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, gate);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeCompleted));
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.Completed);
        gate.Release.TrySetResult();
        Assert.Equal("CBA", await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    private sealed class CompletionGate : IWorkflowExecutionGate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId is not null) return;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

}
