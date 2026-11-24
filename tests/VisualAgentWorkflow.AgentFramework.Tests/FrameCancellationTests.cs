using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Per-frame cooperative cancellation (Decision 0070, waiting frame drain slice 2): cancelling a frame scope cancels
/// that scope and its descendants only, and a node running in the frame observes it as a cancelled token.
/// </summary>
public sealed class FrameCancellationTests
{
    private static WorkflowRunner.StructuredActivationFrameOwner NewOwner()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "frame-cancel", "input",
            new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0") }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty);
        return new WorkflowRunner.StructuredActivationFrameOwner(null, null, definition);
    }

    [Fact]
    public void Cancelling_a_frame_cancels_it_and_every_descendant_but_not_its_parent_or_siblings()
    {
        using var owner = NewOwner();
        var parent = owner.GetFrameToken("outer/2");
        var child = owner.GetFrameToken("outer/2/inner/1");
        var grandchild = owner.GetFrameToken("outer/2/inner/1/deep/3");
        var sibling = owner.GetFrameToken("outer/3");
        var grandparent = owner.GetFrameToken("outer/2/inner/1".Split('/')[0]);

        owner.CancelFrame("outer/2/inner/1");

        Assert.True(child.IsCancellationRequested);
        Assert.True(grandchild.IsCancellationRequested);
        Assert.False(parent.IsCancellationRequested);
        Assert.False(sibling.IsCancellationRequested);
        Assert.False(grandparent.IsCancellationRequested);

        owner.CancelFrame("outer/2");
        Assert.True(parent.IsCancellationRequested);
        Assert.False(sibling.IsCancellationRequested);
    }

    [Fact]
    public void A_scope_id_that_is_only_a_string_prefix_is_not_a_descendant()
    {
        using var owner = NewOwner();
        var twenty = owner.GetFrameToken("outer/20");
        var twentyChild = owner.GetFrameToken("outer/20/inner/1");
        var two = owner.GetFrameToken("outer/2");

        owner.CancelFrame("outer/2");

        Assert.True(two.IsCancellationRequested);
        Assert.False(twenty.IsCancellationRequested);
        Assert.False(twentyChild.IsCancellationRequested);
    }

    [Fact]
    public void A_frame_first_seen_after_its_ancestor_was_cancelled_starts_cancelled()
    {
        using var owner = NewOwner();
        owner.CancelFrame("outer/2");

        Assert.True(owner.GetFrameToken("outer/2/inner/1").IsCancellationRequested);
        Assert.True(owner.GetFrameToken("outer/2").IsCancellationRequested);
        Assert.False(owner.GetFrameToken("outer/20").IsCancellationRequested);
    }

    [Fact]
    public void Cancelling_an_unknown_scope_or_a_disposed_owner_is_a_no_op()
    {
        var owner = NewOwner();
        var token = owner.GetFrameToken("outer/1");

        owner.CancelFrame("never/seen");
        Assert.False(token.IsCancellationRequested);

        owner.Dispose();
        owner.CancelFrame("outer/1");
        owner.CancelFrame("anything");
        Assert.Equal(CancellationToken.None, owner.GetFrameToken("outer/1"));
        Assert.Throws<ArgumentException>(() => owner.CancelFrame(" "));
    }

    private static WorkflowDefinition AgentLoop() => new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "frame-cancel-loop",
        "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["latest-result"] = new(new("agent", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary())
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = new(new("input", "then"), new("loop", "execute"), "execution"),
            ["body"] = new(new("loop", "body"), new("body", "execute"), "execution"),
            ["process"] = new(new("body", "then"), new("agent", "execute"), "execution"),
            ["return"] = new(new("agent", "out"), new("loop", "execute"), "execution"),
            ["complete"] = new(new("loop", "done"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary()
    };

    /// <summary>Blocks on its token in the configured pass (1-based), otherwise appends and returns.</summary>
    private sealed class BlockingAgent(int blockOnCall) : IAgentNodeExecutor
    {
        private int calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) != blockOnCall) return input + "!";
            Started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { ObservedCancellation = true; throw; }
            return input;
        }
    }

    private static async Task<(WorkflowExecutionResult? Result, Exception? Failure, List<ExecutionEvent> Events,
        WorkflowDispatchAdmissionOwner Owner)> RunAsync(IAgentNodeExecutor agent,
        Func<WorkflowRunner.StructuredActivationFrameOwner, string, Task>? onPassStarted, CancellationToken runToken = default)
    {
        var revision = WorkflowRevision.Create(AgentLoop(), "frame-cancel");
        var events = new List<ExecutionEvent>();
        WorkflowDispatchAdmissionOwner? owner = null;
        WorkflowRunner.StructuredActivationFrameOwner? frames = null;
        var scopes = new List<string>();
        var run = new WorkflowRunner(agents: agent).ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(),
            revision, "continue", _ => { }, evt =>
            {
                lock (events) events.Add(evt);
                if (evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.ScopeId is { } scope)
                    lock (scopes) scopes.Add(scope);
                return ValueTask.CompletedTask;
            }, runToken, ownerProbe: created => owner = created, frameOwnerProbe: created => frames = created);
        if (onPassStarted is not null && agent is BlockingAgent blocking)
        {
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string scope;
            lock (scopes) scope = scopes[0];
            await onPassStarted(frames!, scope);
        }
        try
        {
            return (await run.WaitAsync(TimeSpan.FromSeconds(10)), null, events, owner!);
        }
        catch (TimeoutException) { throw; }
        catch (Exception exception) { return (null, exception, events, owner!); }
    }

    [Fact]
    public async Task Cancelling_a_pass_frame_cancels_its_blocked_agent_and_records_it_without_cancelling_the_run()
    {
        var agent = new BlockingAgent(blockOnCall: 1);
        using var runCancellation = new CancellationTokenSource();

        var (result, failure, events, owner) = await RunAsync(agent,
            (frames, scope) => { frames.CancelFrame(scope); return Task.CompletedTask; }, runCancellation.Token);

        Assert.True(agent.ObservedCancellation);
        Assert.False(runCancellation.IsCancellationRequested);
        Assert.True(failure is not null || result is { Failed: true } || result is { Cancelled: true });
        lock (events)
        {
            var cancelled = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "agent");
            Assert.NotNull(cancelled.ScopeId);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent");
        }
        // Every admitted ticket was settled or revoked: no leak after the run is torn down.
        owner.EnsureQuiescent();
    }

    [Fact]
    public async Task A_run_without_frame_cancellation_emits_the_same_events_as_a_run_that_cancels_an_unrelated_scope()
    {
        var baseline = await RunAsync(new BlockingAgent(blockOnCall: 99), onPassStarted: null);
        var unrelated = await RunWithUnrelatedCancelAsync();

        Assert.NotNull(baseline.Result);
        Assert.False(baseline.Result!.Failed);
        Assert.Equal(3, baseline.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        Assert.DoesNotContain(baseline.Events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled);
        Assert.Equal(Shape(baseline.Events), Shape(unrelated.Events));
        baseline.Owner.EnsureQuiescent();

        static async Task<(WorkflowExecutionResult? Result, Exception? Failure, List<ExecutionEvent> Events,
            WorkflowDispatchAdmissionOwner Owner)> RunWithUnrelatedCancelAsync()
        {
            var agent = new BlockingAgent(blockOnCall: 99);
            var revision = WorkflowRevision.Create(AgentLoop(), "frame-cancel-unrelated");
            var events = new List<ExecutionEvent>();
            WorkflowDispatchAdmissionOwner? owner = null;
            var result = await new WorkflowRunner(agents: agent).ExecuteWithProductionDispatchAdmissionProofAsync(
                Guid.NewGuid(), revision, "continue", _ => { }, evt =>
                {
                    lock (events) events.Add(evt);
                    return ValueTask.CompletedTask;
                }, ownerProbe: created => owner = created,
                frameOwnerProbe: frames => frames.CancelFrame("not/a/real/scope/9")).WaitAsync(TimeSpan.FromSeconds(10));
            return (result, null, events, owner!);
        }
    }

    private sealed class SelfCancellingAgent : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        // A simulated internal timeout: cancelled by its own token, not by the frame or the run.
        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default) => throw new OperationCanceledException(new CancellationToken(true));
    }

    [Fact]
    public async Task A_node_that_cancels_itself_while_its_frame_is_not_cancelled_still_fails_the_run()
    {
        var (result, failure, events, owner) = await RunAsync(new SelfCancellingAgent(), onPassStarted: null);

        Assert.True(failure is not null || result is { Failed: true }, "The run must not swallow an unrelated cancellation.");
        // The executor's failure surfaces as such, not as a swallowed cancellation that leaves "no output".
        Assert.DoesNotContain("no output", failure?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("failed", failure?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Shape(List<ExecutionEvent> events)
    {
        lock (events)
            return events.Select(evt => $"{evt.Kind}|{evt.NodeId}|{evt.ScopeId}|{evt.Data}").ToArray();
    }
}
