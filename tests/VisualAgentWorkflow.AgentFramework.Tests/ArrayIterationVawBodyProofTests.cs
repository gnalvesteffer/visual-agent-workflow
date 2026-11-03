using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
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

        public ProofRunStore(WorkflowRevision revision, string input, bool sqlite)
        {
            Directory.CreateDirectory(directory);
            Store = sqlite
                ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
                : new FileRunJournal(directory);
            RunId = Store.Create(revision, input).Header.RunId;
        }

        public IWorkflowRunStore Store { get; }
        public Guid RunId { get; }

        public void Dispose() => Directory.Delete(directory, recursive: true);
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

        public ProofRecorder(ProofRunStore store)
        {
            this.store = store;
            Append(CreateEvent(ExecutionEventKinds.Started));
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
        bool holdUntilCancelled = false) : INodeToolBroker
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
                denyNode, failNode, holdUntilCancelled);

        private sealed class Scope(ParallelMcpBroker owner, string nodeId, Guid invocationId,
            Func<string, string?, ValueTask> record, string? denyNode, string? failNode,
            bool holdUntilCancelled) : INodeToolScope
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
                    var result = $"result:{query}";
                    await record(ExecutionEventKinds.ToolCompleted, result);
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

    private sealed record ParallelToolCall(string NodeId, Guid InvocationId, string Query);
}
