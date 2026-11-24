using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// For Each Break with in-flight Fork siblings (Decision 0070, waiting frame drain slice 3): a Break on a Boolean Branch
/// outcome inside a fork branch cancels its pass frame's other work, waits for it to settle, then continues on completed.
/// </summary>
public sealed class ForEachBreakDrainTests
{
    private enum SiblingMode { ObserveToken, IgnoreToken, Return }

    /// <summary>Agent "agent-a" blocks for item "b"; "after" counts its calls; every other call returns the input.</summary>
    private sealed class SiblingAgents(SiblingMode mode) : IAgentNodeExecutor
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int afterCalls;
        public int AfterCalls => Volatile.Read(ref afterCalls);
        public TaskCompletionSource BlockedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }
        public ConcurrentQueue<string> Inputs { get; } = new();
        public void ReleaseIgnoringSibling() => release.TrySetResult();

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            Inputs.Enqueue($"{invocation.NodeId}:{input}");
            if (invocation.NodeId == "after") Interlocked.Increment(ref afterCalls);
            if (invocation.NodeId != "agent-a" || input != "b" || mode == SiblingMode.Return) return input;
            BlockedStarted.TrySetResult();
            try
            {
                if (mode == SiblingMode.ObserveToken) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                else await release.Task;
            }
            catch (OperationCanceledException) { ObservedCancellation = true; throw; }
            return input;
        }
    }

    /// <summary>
    /// For Each over [a,b,c]; body → Fork. Branch a runs agent-a (blocks for "b"); branch b breaks on "b" through a
    /// Boolean Branch and otherwise runs agent-b. The Join returns to the iterator; completed → after → output.
    /// </summary>
    private static WorkflowDefinition Workflow(bool slowBranchB = false, bool mcpSibling = false)
    {
        var branches = ImmutableArray.Create(new ParallelBranchDescriptor("a", "A", "anchor-a"),
            new ParallelBranchDescriptor("b", "B", "anchor-b"));
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\",\"c\"]") },
            ["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["fork"] = new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) },
            ["join"] = new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) },
            ["anchor-a"] = new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "a") },
            ["anchor-b"] = new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "b") },
            ["agent-a"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "A", [])),
            ["agent-b"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "B", [])),
            ["route"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["is-b"] = new(NodeTypes.StringEquals, "1.0.0"),
            ["b-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("b") },
            ["break"] = new(NodeTypes.Break, "1.0.0"),
            ["after"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "After", [])),
            ["done-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("done") },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["items-each"] = new(new("items", "out"), new("each", "array"), "data"),
            ["item-eq"] = new(new("each", "item"), new("is-b", "left"), "data"),
            ["text-eq"] = new(new("b-text", "out"), new("is-b", "right"), "data"),
            ["eq-route"] = new(new("is-b", "out"), new("route", "condition"), "data"),
            ["item-a"] = new(new("each", "item"), new("agent-a", "in"), "data"),
            ["item-b"] = new(new("each", "item"), new("agent-b", "in"), "data"),
            ["done-after"] = new(new("done-text", "out"), new("after", "in"), "data"),
            ["result"] = new(new("after", "out"), new("output", "in"), "data")
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-break-drain", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("each", "execute"), "execution"),
                ["body-start"] = new(new("each", "body"), new("body", "execute"), "execution"),
                ["body-fork"] = new(new("body", "then"), new("fork", "execute"), "execution"),
                ["fork-a"] = new(new("fork", ParallelBranchLimits.PinId("a")), new("anchor-a", "execute"), "execution"),
                ["fork-b"] = new(new("fork", ParallelBranchLimits.PinId("b")), new("anchor-b", "execute"), "execution"),
                ["anchor-a-agent"] = new(new("anchor-a", "then"), new("agent-a", "execute"), "execution"),
                ["agent-a-join"] = new(new("agent-a", "out"), new("join", ParallelBranchLimits.PinId("a")), "execution"),
                ["anchor-b-route"] = new(new("anchor-b", "then"), new("route", "execute"), "execution"),
                ["route-break"] = new(new("route", "true"), new("break", "execute"), "execution"),
                ["route-agent-b"] = new(new("route", "false"), new("agent-b", "execute"), "execution"),
                ["agent-b-join"] = new(new("agent-b", "out"), new("join", ParallelBranchLimits.PinId("b")), "execution"),
                ["join-return"] = new(new("join", "then"), new("each", "feedback"), "execution"),
                ["complete"] = new(new("each", "completed"), new("after", "execute"), "execution"),
                ["after-output"] = new(new("after", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        if (mcpSibling)
            definition = definition with
            {
                Nodes = definition.Nodes.SetItem("agent-a", new(NodeTypes.McpTool, "1.0.0",
                    new ToolBindingRequest("test.block", new string('a', 64)))
                {
                    McpArgumentsJson = "{\"value\":\"\"}",
                    McpInputPorts = [new McpInputPortDefinition("arg.value", "value", WorkflowDataTypes.String, true)]
                }),
                Connections = definition.Connections.SetItem("item-a", new(new("each", "item"), new("agent-a", "arg.value"), "data")),
                ExecutionConnections = definition.ExecutionConnections!
                    .SetItem("agent-a-join", new(new("agent-a", "then"), new("join", ParallelBranchLimits.PinId("a")), "execution"))
            };
        // One extra step before the decision lets branch a reach the Join first, so the Break finds it pending.
        return !slowBranchB ? definition : definition with
        {
            Nodes = definition.Nodes.Add("pre-b", new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "PreB", []))),
            Connections = definition.Connections.Add("item-pre", new(new("each", "item"), new("pre-b", "in"), "data")),
            ExecutionConnections = definition.ExecutionConnections!.Remove("anchor-b-route")
                .Add("anchor-b-pre", new(new("anchor-b", "then"), new("pre-b", "execute"), "execution"))
                .Add("pre-route", new(new("pre-b", "out"), new("route", "execute"), "execution"))
        };
    }

    /// <summary>An MCP tool that blocks on its token for argument "b" and otherwise returns at once.</summary>
    private sealed class BlockingToolBroker : INodeToolBroker
    {
        public bool ObservedCancellation { get; private set; }

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([
                new("test.block", "Blocking test tool", "test", "block", new string('a', 64))]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this);

        private sealed class Scope(BlockingToolBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("The fixture uses typed JSON MCP arguments.");

            public async Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments,
                CancellationToken cancellationToken = default)
            {
                var value = arguments.GetProperty("value").GetString()!;
                if (value != "b") return value;
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { owner.ObservedCancellation = true; throw; }
                return value;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed record Run(WorkflowExecutionResult? Result, Exception? Failure, List<ExecutionEvent> Events,
        WorkflowDispatchAdmissionOwner Owner, WorkflowRunner.StructuredActivationFrameOwner Frames);

    private static async Task<Run> RunAsync(WorkflowDefinition definition, SiblingAgents agents,
        TimeSpan? grace = null, Func<Task>? whileRunning = null, INodeToolBroker? tools = null)
    {
        var revision = WorkflowRevision.Create(definition, "for-each-break-drain");
        var events = new List<ExecutionEvent>();
        WorkflowDispatchAdmissionOwner? owner = null;
        WorkflowRunner.StructuredActivationFrameOwner? frames = null;
        var run = new WorkflowRunner(tools, agents).ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(),
            revision, "go", _ => { }, evt =>
            {
                lock (events) events.Add(evt);
                return ValueTask.CompletedTask;
            }, ownerProbe: created => owner = created, frameOwnerProbe: created =>
            {
                frames = created;
                if (grace is { } value) created.BreakDrainGrace = value;
            });
        try
        {
            if (whileRunning is not null) await whileRunning();
            return new(await run.WaitAsync(TimeSpan.FromSeconds(20)), null, events, owner!, frames!);
        }
        catch (TimeoutException)
        {
            lock (events)
                throw new TimeoutException("The run did not finish. Events: " + string.Join(" | ",
                    events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ScopeId}")));
        }
        catch (Exception exception) { return new(null, exception, events, owner!, frames!); }
    }

    private static string Describe(Run run)
    {
        lock (run.Events)
            return run.Failure + " Events: " + string.Join(" | ",
                run.Events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ScopeId}"));
    }

    [Fact]
    public void The_fixture_workflow_validates()
    {
        Assert.Empty(WorkflowValidator.Validate(Workflow()));
    }

    [Fact]
    public async Task A_fork_branch_break_cancels_its_blocked_sibling_and_continues_on_completed_once()
    {
        var agents = new SiblingAgents(SiblingMode.ObserveToken);

        var run = await RunAsync(Workflow(), agents);

        Assert.True(run.Failure is null, Describe(run));
        Assert.False(run.Result!.Failed);
        Assert.True(agents.ObservedCancellation);
        Assert.Equal(1, agents.AfterCalls);
        // Items a and b ran; c never started.
        Assert.DoesNotContain(agents.Inputs, input => input.EndsWith(":c", StringComparison.Ordinal));
        lock (run.Events)
        {
            Assert.Equal(2, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
            var stopped = Assert.Single(run.Events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
            Assert.Equal("{\"reason\":\"break\",\"iterations\":2,\"drained\":true}", stopped.Data);
            var cancelled = Assert.Single(run.Events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "agent-a");
            Assert.Equal("each/2", cancelled.ScopeId);
            Assert.DoesNotContain(run.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent-a" && evt.ScopeId == "each/2");
            // The sibling's cancellation is recorded before the pass frame's iteration completes.
            var cancelledAt = run.Events.IndexOf(cancelled);
            var completedAt = run.Events.FindIndex(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted && evt.ScopeId == "each/2");
            Assert.InRange(cancelledAt, 0, completedAt - 1);
            Assert.Equal(1, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "output"));
            Assert.Single(run.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
        }
        run.Owner.EnsureQuiescent();
    }

    [Fact]
    public async Task A_join_that_already_received_the_sibling_is_cancelled_when_the_other_branch_breaks()
    {
        var agents = new SiblingAgents(SiblingMode.Return);

        var run = await RunAsync(Workflow(slowBranchB: true), agents);

        Assert.True(run.Failure is null, Describe(run));
        Assert.False(run.Result!.Failed);
        Assert.Equal(1, agents.AfterCalls);
        lock (run.Events)
        {
            Assert.True(run.Events.Any(evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "join" && evt.ScopeId == "each/2"), Describe(run));
            Assert.Contains("\"reason\":\"break\"", Assert.Single(run.Events, evt => evt.Kind == ExecutionEventKinds.LoopStopped).Data);
            Assert.Equal(1, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "output"));
        }
        run.Owner.EnsureQuiescent();
    }

    [Fact]
    public async Task A_fork_branch_break_cancels_a_blocked_mcp_tool_sibling()
    {
        var agents = new SiblingAgents(SiblingMode.ObserveToken);
        var broker = new BlockingToolBroker();
        var definition = Workflow(mcpSibling: true);
        Assert.Empty(WorkflowValidator.Validate(definition));

        var run = await RunAsync(definition, agents, tools: broker);

        Assert.True(run.Failure is null, Describe(run));
        Assert.False(run.Result!.Failed);
        Assert.True(broker.ObservedCancellation);
        Assert.Equal(1, agents.AfterCalls);
        lock (run.Events)
        {
            Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "agent-a" && evt.ScopeId == "each/2");
            Assert.Contains("\"reason\":\"break\"", Assert.Single(run.Events, evt => evt.Kind == ExecutionEventKinds.LoopStopped).Data);
        }
        run.Owner.EnsureQuiescent();
    }

    [Fact]
    public async Task A_sibling_that_ignores_cancellation_fails_the_run_with_a_clear_message_instead_of_hanging()
    {
        var agents = new SiblingAgents(SiblingMode.IgnoreToken);
        try
        {
            var run = await RunAsync(Workflow(), agents, grace: TimeSpan.FromMilliseconds(300));

            var text = run.Failure?.ToString() ?? string.Join('|', run.Events.Select(evt => evt.Data));
            Assert.True(run.Failure is not null || run.Result is { Failed: true });
            Assert.Contains("did not stop within", text, StringComparison.Ordinal);
            lock (run.Events)
            {
                Assert.Equal(0, agents.AfterCalls);
                Assert.DoesNotContain(run.Events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
                Assert.Single(run.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed);
            }
            // The late sibling finally returns: its events in the closed pass scope are dropped, its ticket still settles.
            int eventsAtFailure;
            lock (run.Events) eventsAtFailure = run.Events.Count;
            agents.ReleaseIgnoringSibling();
            // (Its follow-on message to the Join was admitted into a dead run and is never delivered.)
            for (var attempt = 0; attempt < 100 && run.Owner.Outstanding().Any(slot => slot.StartsWith("agent-a:", StringComparison.Ordinal)); attempt++)
                await Task.Delay(50);
            Assert.DoesNotContain(run.Owner.Outstanding(), slot => slot.StartsWith("agent-a:", StringComparison.Ordinal));
            lock (run.Events) Assert.Equal(eventsAtFailure, run.Events.Count);
        }
        finally { agents.ReleaseIgnoringSibling(); }
    }

    [Fact]
    public async Task A_for_each_with_a_fork_and_no_break_emits_no_cancellation_or_break_events()
    {
        var definition = Workflow();
        definition = definition with
        {
            Nodes = definition.Nodes
                .SetItem("b-text", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("never") })
                .SetItem("items", new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"c\"]") })
        };
        var agents = new SiblingAgents(SiblingMode.ObserveToken);

        var run = await RunAsync(definition, agents);

        Assert.Null(run.Failure);
        Assert.False(run.Result!.Failed);
        lock (run.Events)
        {
            Assert.DoesNotContain(run.Events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled);
            Assert.Contains("array-exhausted", Assert.Single(run.Events, evt => evt.Kind == ExecutionEventKinds.LoopStopped).Data);
        }
        run.Owner.EnsureQuiescent();
    }

    /// <summary>Outer For Each over [x,y]; each pass runs an inner For Each over [1,2] that breaks at "2".</summary>
    private static WorkflowDefinition NestedBreakWorkflow()
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
            ["cond"] = new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "2") },
            ["break"] = new(NodeTypes.Break, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["done-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("done") },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["outer-array"] = new(new("outer-items", "out"), new("outer", "array"), "data"),
            ["inner-array"] = new(new("inner-items", "out"), new("inner", "array"), "data"),
            ["inner-cond"] = new(new("inner", "item"), new("cond", "in"), "data"),
            ["inner-agent"] = new(new("inner", "item"), new("agent", "in"), "data"),
            ["result"] = new(new("done-text", "out"), new("output", "in"), "data")
        };
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-nested-break", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("outer", "execute"), "execution"),
                ["outer-start"] = new(new("outer", "body"), new("outer-body", "execute"), "execution"),
                ["enter-inner"] = new(new("outer-body", "then"), new("inner", "execute"), "execution"),
                ["inner-start"] = new(new("inner", "body"), new("inner-body", "execute"), "execution"),
                ["body-cond"] = new(new("inner-body", "then"), new("cond", "execute"), "execution"),
                ["cond-break"] = new(new("cond", "true"), new("break", "execute"), "execution"),
                ["cond-agent"] = new(new("cond", "false"), new("agent", "execute"), "execution"),
                ["inner-return"] = new(new("agent", "out"), new("inner", "feedback"), "execution"),
                ["outer-return"] = new(new("inner", "completed"), new("outer", "feedback"), "execution"),
                ["complete"] = new(new("outer", "completed"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
    }

    [Fact]
    public async Task A_break_in_an_inner_for_each_cancels_only_its_own_pass_frame_and_leaves_outer_work_running()
    {
        // The validator rejects a Fork in a workflow with two loops, so the outer sibling is simulated: work admitted and
        // started in the outer pass scope (a "ghost" slot) and one in the inner pass scope, settled by the harness.
        var definition = NestedBreakWorkflow();
        Assert.Empty(WorkflowValidator.Validate(definition));
        var revision = WorkflowRevision.Create(definition, "for-each-nested-break");
        var events = new List<ExecutionEvent>();
        WorkflowDispatchAdmissionOwner? owner = null;
        WorkflowRunner.StructuredActivationFrameOwner? frames = null;
        Guid? outerGhost = null, innerGhost = null;
        CancellationToken outerToken = default, innerToken = default;
        string? outerScope = null, innerScope = null;
        var outerCancelledAtBreak = true;
        var innerWasCancelled = false;
        var settleInnerGhost = new Action(() => { });

        var run = new WorkflowRunner(agents: new SiblingAgents(SiblingMode.ObserveToken)).ExecuteWithProductionDispatchAdmissionProofAsync(
            Guid.NewGuid(), revision, "go", _ => { }, evt =>
            {
                lock (events) events.Add(evt);
                if (evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.ScopeId is { } scope &&
                    scope.EndsWith("inner/2", StringComparison.Ordinal) && innerGhost is null && outerGhost is null)
                {
                    innerScope = scope;
                    outerScope = scope[..scope.LastIndexOf("/inner/", StringComparison.Ordinal)];
                    outerToken = frames!.GetFrameToken(outerScope);
                    innerToken = frames.GetFrameToken(innerScope);
                    outerGhost = owner!.Admit(["outer-ghost"], outerScope);
                    owner.Begin(outerGhost, "outer-ghost");
                    innerGhost = owner.Admit(["inner-ghost"], innerScope);
                    owner.Begin(innerGhost, "inner-ghost");
                    innerToken.Register(() => { innerWasCancelled = true; owner.Settle(innerGhost, "inner-ghost"); });
                }
                if (evt.Kind == ExecutionEventKinds.LoopStopped && evt.NodeId == "inner" &&
                    evt.Data!.Contains("\"reason\":\"break\"", StringComparison.Ordinal) && outerGhost is { } ghost)
                {
                    outerCancelledAtBreak = outerToken.IsCancellationRequested;
                    owner!.Settle(ghost, "outer-ghost");
                    outerGhost = null;
                }
                return ValueTask.CompletedTask;
            }, ownerProbe: created => owner = created, frameOwnerProbe: created => frames = created);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.Failed);
        Assert.True(innerWasCancelled);
        Assert.False(outerCancelledAtBreak);
        Assert.False(outerToken.IsCancellationRequested);
        owner!.EnsureQuiescent();
        lock (events)
            Assert.Contains("array-exhausted", Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped && evt.NodeId == "outer").Data);
    }
}
