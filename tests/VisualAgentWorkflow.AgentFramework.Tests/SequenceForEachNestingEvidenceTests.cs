using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Evidence for the Sequence and For Each nesting prerequisites (Decision 0070, "Sequence nesting prerequisite
/// evidence (2026-11-25)"). Every test here runs against the unchanged guards: a For Each is still rejected inside a
/// Sequence route, so the only composition is a Sequence output that calls a pinned child workflow containing the
/// For Each. The tests characterize current behavior; they do not loosen or assume any guard change.
/// </summary>
public sealed class SequenceForEachNestingEvidenceTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private sealed class ItemAgent(Func<string, CancellationToken, Task<string>>? behavior = null) : IAgentNodeExecutor
    {
        public ConcurrentQueue<string> Inputs { get; } = new();

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            Inputs.Enqueue(input);
            return behavior is null ? Task.FromResult(input) : behavior(input, cancellationToken);
        }
    }

    private static string StringArray(IEnumerable<string> items) => JsonSerializer.Serialize(items);

    /// <summary>Input to For Each over a constant array; one Agent body node fed by the item; completed to Output.</summary>
    private static WorkflowDefinition ForEachChild(string workflowId, IReadOnlyList<string> items,
        IReadOnlyList<string>? innerItems = null)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, StringArray(items)) },
            ["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["items-each"] = new(new("items", "out"), new("each", "array"), "data"),
            ["result"] = new(new("agent", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = new(new("input", "then"), new("each", "execute"), "execution"),
            ["body-start"] = new(new("each", "body"), new("body", "execute"), "execution"),
            ["return"] = new(new("agent", "out"), new("each", "feedback"), "execution"),
            ["complete"] = new(new("each", "completed"), new("output", "execute"), "execution")
        };
        if (innerItems is null)
        {
            data["item-agent"] = new(new("each", "item"), new("agent", "in"), "data");
            execution["process"] = new(new("body", "then"), new("agent", "execute"), "execution");
        }
        else
        {
            nodes["inner-items"] = new(NodeTypes.ArrayLiteral, "1.0.0")
            {
                ArrayLiteral = new(WorkflowDataTypes.String, StringArray(innerItems))
            };
            nodes["inner"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String };
            nodes["inner-body"] = new(NodeTypes.LoopBody, "1.0.0");
            data["inner-array"] = new(new("inner-items", "out"), new("inner", "array"), "data");
            data["item-agent"] = new(new("inner", "item"), new("agent", "in"), "data");
            execution["enter-inner"] = new(new("body", "then"), new("inner", "execute"), "execution");
            execution["inner-start"] = new(new("inner", "body"), new("inner-body", "execute"), "execution");
            execution["process"] = new(new("inner-body", "then"), new("agent", "execute"), "execution");
            execution["return"] = new(new("inner", "completed"), new("each", "feedback"), "execution");
            execution["inner-return"] = new(new("agent", "out"), new("inner", "feedback"), "execution");
        }
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

    /// <summary>Control child with no loop: Input to one Agent to Output.</summary>
    private static WorkflowDefinition AgentChild(string workflowId) => new(WorkflowSchemaVersions.UnifiedWorkflowGraph,
        workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["in"] = new(new("input", "out"), new("agent", "in"), "data"),
            ["result"] = new(new("agent", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary())
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = new(new("input", "then"), new("agent", "execute"), "execution"),
            ["complete"] = new(new("agent", "out"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary()
    };

    private static WorkflowDefinition PlainChild(string workflowId) =>
        WorkflowEditing.InsertAfter(Baseline(workflowId), "input", NodeTypes.Uppercase);

    private static WorkflowDefinition Baseline(string workflowId) => new("1.0", workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["edge"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    /// <summary>Parent: Input to Sequence; each output (call, sibling) calls its pinned child and joins at Output.</summary>
    private static WorkflowDefinition Parent(string workflowId, WorkflowRevision first, WorkflowRevision second)
    {
        NodeDefinition Call(WorkflowRevision child) => new(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("call", "First child"), new("sibling", "Second child")])
                },
                ["call"] = Call(first),
                ["sibling"] = Call(second),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
                ["call-output"] = new(new("call", "out"), new("output", "execute"), "execution"),
                ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
                ["sibling-output"] = new(new("sibling", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
    }

    private sealed record Observed(WorkflowExecutionResult? Result, Exception? Failure, ExecutionEvent[] Events,
        WorkflowDispatchAdmissionOwner[] Owners, WorkflowRunner.StructuredActivationFrameOwner? RootFrameOwner,
        WorkflowRunner.StructuredActivationFrameOwner[] FrameOwners, ConcurrentQueue<ExecutionEvent> Live);

    private static async Task<Observed> RunAsync(WorkflowRunner runner, WorkflowDefinition parent,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "nesting-evidence");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var owners = new List<WorkflowDispatchAdmissionOwner>();
        WorkflowRunner.StructuredActivationFrameOwner? rootFrameOwner = null;
        var frameOwners = new List<WorkflowRunner.StructuredActivationFrameOwner>();
        WorkflowExecutionResult? result = null;
        Exception? failure = null;
        try
        {
            result = await runner.ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "start",
                _ => { }, evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellationToken,
                ownerProbe: owner => { lock (owners) owners.Add(owner); },
                frameOwnerProbe: owner => { rootFrameOwner ??= owner; lock (frameOwners) frameOwners.Add(owner); }).WaitAsync(timeout ?? Window);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        lock (owners) lock (frameOwners) return new(result, failure, events.ToArray(), [.. owners], rootFrameOwner, [.. frameOwners], events);
    }

    private static WorkflowRevision Revision(WorkflowDefinition definition) => WorkflowRevision.Create(definition, "test");

    private static string Chain(Exception? exception)
    {
        var parts = new List<string>();
        for (; exception is not null; exception = exception.InnerException)
            parts.Add($"{exception.GetType().Name}: {exception.Message}");
        return string.Join(" <- ", parts);
    }

    private static string Trace(Observed run) => string.Join(" | ",
        run.Events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ScopeId}:{evt.Data}"));

    private static bool InChildScope(ExecutionEvent evt, string callId) =>
        evt.ScopeId == callId || evt.ScopeId?.StartsWith(callId + "/", StringComparison.Ordinal) == true;

    private static void AssertQuiescent(Observed run)
    {
        Assert.NotEmpty(run.Owners);
        Assert.All(run.Owners, owner => Assert.Empty(owner.Outstanding()));
        Assert.Equal(0, run.RootFrameOwner!.RunBudget.Reservation.DispatchTicketsOutstanding);
    }

    // ---------------------------------------------------------------------------------------------------------
    // (1) The WorkflowCall workaround.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sequence_output_can_call_a_pinned_child_that_contains_a_for_each()
    {
        var agent = new ItemAgent();
        var child = Revision(ForEachChild("each-child", ["a", "b", "c"]));
        var other = Revision(PlainChild("plain-child"));
        var parent = Parent("sequence-calls-for-each", child, other);

        // Both graphs validate: the parent as a Sequence graph, the child as a For Each graph.
        Assert.Empty(WorkflowValidator.Validate(parent));
        Assert.Empty(WorkflowValidator.Validate(child.Definition));
        var run = await RunAsync(new WorkflowRunner(agents: agent), parent);

        Assert.True(run.Failure is null, run.Failure + Environment.NewLine + Trace(run));
        Assert.False(run.Result!.Failed, Trace(run));
        Assert.Equal(["a", "b", "c"], agent.Inputs);
        // The two Sequence outputs join at one Output; the arrival order is a race, so only the set is pinned.
        Assert.Contains(run.Result.Output, new[] { "c", "START" });
        Assert.Equal(2, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
        Assert.Single(run.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
        var completed = Assert.Single(run.Events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "call");
        Assert.Equal("c", completed.Data);

        // Iteration evidence lives in the child scope: the call id prefixes every iteration scope.
        var started = run.Events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        Assert.Equal(["call/each/1", "call/each/2", "call/each/3"], started.Select(evt => evt.ScopeId));
        Assert.Equal(3, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted && InChildScope(evt, "call")));
        var stopped = Assert.Single(run.Events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        Assert.Contains("array-exhausted", stopped.Data);
        Assert.DoesNotContain(run.Events, evt => evt.ScopeId?.Contains("/each/", StringComparison.Ordinal) == true &&
            !InChildScope(evt, "call"));

        // The child's three passes and its loop-scoped evidence are drawn from the root run's reservation.
        var reservation = run.RootFrameOwner!.RunBudget.Reservation;
        Assert.Equal(3, reservation.BodyPassesUsed);
        Assert.True(reservation.EvidenceEventsUsed > 0);
        Assert.Equal(3, run.Owners.Length);
        AssertQuiescent(run);

        // In-memory playback of the Sequence and of the call still reconstructs with the nested iteration scopes
        // present (File and SQLite playback of nested scope ids is not covered here).
        Assert.True(SequenceEvidence.TryReconstructPlayback(run.Events, out var sequencePlayback));
        Assert.Equal("completed", sequencePlayback!.Status);
        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(run.Events, out _));
    }

    private static async Task<(Exception? Failure, ExecutionEvent[] Events)> RunWithRootLimitsAsync(string id, int maximumPasses,
        int maximumEvidenceEvents)
    {
        var parent = Parent(id, Revision(ForEachChild(id + "-child", ["a", "b", "c"])), Revision(PlainChild(id + "-plain")));
        var events = new ConcurrentQueue<ExecutionEvent>();
        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(agents: new ItemAgent())
            .ExecuteWithLoopBudgetsProofAsync(Guid.NewGuid(),
                WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "pool"), "start",
                maximumPasses, maximumEvidenceEvents, evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
            .WaitAsync(Window));
        return (failure, events.ToArray());
    }

    [Fact]
    public async Task A_sequence_child_for_each_draws_its_passes_from_the_root_pass_pool()
    {
        // Two passes allowed for the whole run: the child's third item must be refused by the root's pool.
        var (failure, events) = await RunWithRootLimitsAsync("sequence-pool-pass", 2, 1000);

        Assert.Contains("aggregate Loop body-pass limit (1024)", Chain(failure), StringComparison.Ordinal);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
    }

    [Fact]
    public async Task A_sequence_child_for_each_draws_its_evidence_from_the_root_evidence_pool()
    {
        var (failure, events) = await RunWithRootLimitsAsync("sequence-pool-evidence", 1000, 4);

        Assert.Contains("Loop-scoped evidence event limit (4)", Chain(failure), StringComparison.Ordinal);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
    }

    [Fact]
    public async Task A_for_each_failing_in_the_child_fails_the_parent_sequence_deterministically()
    {
        ItemAgent NewAgent() => new((input, _) => input == "boom"
            ? throw new InvalidOperationException("item failed") : Task.FromResult(input));
        async Task<(Observed Run, ItemAgent Agent)> RunOnceAsync()
        {
            var agent = NewAgent();
            return (await RunAsync(new WorkflowRunner(agents: agent),
                Parent("sequence-failing-for-each", Revision(ForEachChild("each-child-failing", ["a", "boom", "c"])),
                    Revision(PlainChild("plain-child-failing")))), agent);
        }

        var first = await RunOnceAsync();
        var second = await RunOnceAsync();

        foreach (var (run, agent) in new[] { first, second })
        {
            // An agent exception inside the child surfaces as the run's exception, wrapped once per executor level.
            Assert.NotNull(run.Failure);
            Assert.Null(run.Result);
            Assert.Contains("Executor call failed.", Chain(run.Failure), StringComparison.Ordinal);
            Assert.Contains("item failed", Chain(run.Failure), StringComparison.Ordinal);
            // Item three never runs: the failing item ends the child before the next pass is admitted.
            Assert.Equal(["a", "boom"], agent.Inputs);
            Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "agent" &&
                evt.ScopeId == "call/each/2");
            Assert.DoesNotContain(run.Events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.ScopeId == "call/each/2");
            Assert.DoesNotContain(run.Events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
            Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.SubworkflowFailed && evt.ScopeId == "call");
            Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
            Assert.Single(run.Events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
            Assert.Single(run.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed);
            Assert.DoesNotContain(run.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
            Assert.Equal(2, run.RootFrameOwner!.RunBudget.Reservation.BodyPassesUsed);
            AssertQuiescent(run);
        }
        // The child's own ordered evidence is identical across runs (the sibling output races, so it is excluded).
        static string[] ChildKinds(Observed run) => run.Events.Where(evt => InChildScope(evt, "call"))
            .Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ScopeId}").ToArray();
        Assert.Equal(ChildKinds(first.Run), ChildKinds(second.Run));
    }

    // ---------------------------------------------------------------------------------------------------------
    // (2) Sequence-child budget sharing: the deliberate behavior change of the shared reservation commit.
    // ---------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> Items(int count) => Enumerable.Range(0, count).Select(index => $"i{index}").ToArray();

    private static WorkflowDefinition TwoLoopingChildren(string id, int outer, int inner) => Parent(id,
        Revision(ForEachChild(id + "-first", Items(outer), Items(inner))),
        Revision(ForEachChild(id + "-second", Items(outer), Items(inner))));

    [Fact]
    public async Task One_looping_child_that_fits_the_shared_pool_alone_succeeds()
    {
        // 20 outer passes + 20 x 40 inner passes = 820 body passes, under the 1,024 pool.
        var parent = Parent("sequence-pool-alone", Revision(ForEachChild("alone-child", Items(20), Items(40))),
            Revision(PlainChild("alone-plain")));

        var run = await RunAsync(new WorkflowRunner(agents: new ItemAgent()), parent);

        Assert.True(run.Failure is null, run.Failure?.ToString());
        Assert.False(run.Result!.Failed, Trace(run));
        Assert.Equal(820, run.RootFrameOwner!.RunBudget.Reservation.BodyPassesUsed);
        AssertQuiescent(run);
    }

    [Fact]
    public async Task Two_looping_sequence_children_that_together_exceed_the_shared_pool_fail_at_the_root()
    {
        // Each child alone needs 820 passes; together 1,640 against one 1,024 pool. Before the shared reservation
        // each child had fresh budgets and this run completed; now the children draw from the root's pool.
        var run = await RunAsync(new WorkflowRunner(agents: new ItemAgent()), TwoLoopingChildren("sequence-pool-over", 20, 40));

        Assert.NotNull(run.Failure);
        Assert.Null(run.Result);
        Assert.Contains("aggregate Loop body-pass limit (1024)", Chain(run.Failure), StringComparison.Ordinal);
        Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.Single(run.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed);
        Assert.DoesNotContain(run.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
        // A refused draw takes nothing: the pool ends exactly full and every ticket is back.
        Assert.Equal(1024, run.RootFrameOwner!.RunBudget.Reservation.BodyPassesUsed);
        AssertQuiescent(run);
    }

    [Fact]
    public async Task Two_looping_sequence_children_that_together_fit_the_shared_pool_succeed()
    {
        // 10 + 10 x 40 = 410 passes each, 820 together.
        var run = await RunAsync(new WorkflowRunner(agents: new ItemAgent()), TwoLoopingChildren("sequence-pool-fits", 10, 40));

        Assert.True(run.Failure is null, run.Failure?.ToString());
        Assert.False(run.Result!.Failed, Trace(run));
        Assert.Equal(820, run.RootFrameOwner!.RunBudget.Reservation.BodyPassesUsed);
        Assert.Equal(2, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
        AssertQuiescent(run);
    }

    /// <summary>
    /// Regression: a failing output left the sibling's admitted Output barrier ticket outstanding, because the failed
    /// superstep never delivered it and the failure path did not revoke unstarted tickets.
    /// </summary>
    [Fact]
    public async Task A_failed_sequence_run_revokes_the_admitted_output_barrier_ticket_and_is_quiescent()
    {
        // The failing child waits so the plain sibling has already arrived at the Output barrier.
        var agent = new ItemAgent(async (input, _) =>
        {
            if (input != "start") return input;
            await Task.Delay(300);
            throw new InvalidOperationException("boom");
        });

        var run = await RunAsync(new WorkflowRunner(agents: agent), Parent("sequence-failing-control",
            Revision(AgentChild("control-child")), Revision(PlainChild("control-plain"))));

        Assert.NotNull(run.Failure);
        AssertQuiescent(run);
    }

    // ---------------------------------------------------------------------------------------------------------
    // (3) Concurrent same-node activations.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_activations_of_the_same_for_each_revision_keep_their_items_and_scopes_apart()
    {
        // This is the strongest proof production can express: the private activation harness runs the SAME body
        // revision (a For Each) twice at once under different parent frames. Each activation builds its own
        // executor set, so it shows scope and evidence isolation per parent frame, not that one executor
        // instance's forEachItems key (parentFrameId, nodeId) separates two simultaneous activations.
        var revision = WorkflowRevision.Create(ForEachChild("concurrent-for-each", ["a", "b"]), "concurrent");
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var agent = new ItemAgent(async (input, token) =>
        {
            // The first item of each activation holds until the other activation's first item is also running.
            if (input == "a")
            {
                if (Interlocked.Increment(ref inFlight) == 2) bothStarted.TrySetResult();
                await bothStarted.Task.WaitAsync(token);
            }
            return input;
        });
        var runner = new WorkflowRunner(agents: agent);
        var parameters = ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        var firstFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null, "outer", 1, "first", parameters);
        var secondFrame = new WorkflowRunner.StructuredActivationFrame(Guid.NewGuid(), null, "outer", 2, "second", parameters);
        var firstEvents = new ConcurrentQueue<ExecutionEvent>();
        var secondEvents = new ConcurrentQueue<ExecutionEvent>();

        var first = runner.ExecuteBodyForActivationProofAsync(Guid.NewGuid(), revision, "start", null,
            evt => { firstEvents.Enqueue(evt); return ValueTask.CompletedTask; }, activationFrame: firstFrame);
        var second = runner.ExecuteBodyForActivationProofAsync(Guid.NewGuid(), revision, "start", null,
            evt => { secondEvents.Enqueue(evt); return ValueTask.CompletedTask; }, activationFrame: secondFrame);
        var outcomes = await Task.WhenAll(first, second).WaitAsync(Window);

        Assert.All(outcomes, outcome => Assert.Equal(WorkflowBodyProofDisposition.Succeeded, outcome.Disposition));
        Assert.All(outcomes, outcome => Assert.Equal("b", outcome.Output));
        // Four item activations in total, two per parent frame, each with its own array evaluation.
        Assert.Equal(4, agent.Inputs.Count);
        Assert.Equal(["a", "a", "b", "b"], agent.Inputs.Order());
        foreach (var (events, prefix) in new[] { (firstEvents, "outer/1/"), (secondEvents, "outer/2/") })
        {
            var scopes = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
                .Select(evt => evt.ScopeId!).ToArray();
            Assert.Equal([prefix + "each/1", prefix + "each/2"], scopes);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
            Assert.All(events.Where(evt => evt.ScopeId is not null && evt.ScopeId.Contains("each/", StringComparison.Ordinal)),
                evt => Assert.StartsWith(prefix, evt.ScopeId, StringComparison.Ordinal));
        }
    }

    [Fact(Skip = "UNTESTABLE on production today: the For Each executor's forEachItems key (parentFrameId, nodeId) cannot be " +
        "stressed with two simultaneous activations of one executor instance. A For Each is rejected inside a Sequence " +
        "route (sequence.branch.nested), and in the ordinary graph a For Each node is activated sequentially (loop passes, " +
        "Fork branches are distinct nodes). The needed test: one For Each node reached twice at once under a shared " +
        "Sequence ActivationFrame with two different array evaluations, asserting each activation iterates only its own " +
        "items and removes only its own forEachItems entry. It requires slice (b) or a private builder; do not unskip " +
        "without one.")]
    public void Concurrent_activations_of_one_for_each_instance_under_a_shared_sequence_frame_do_not_share_items()
    {
    }

    // ---------------------------------------------------------------------------------------------------------
    // (4) Cancel and abandon with a hung child item.
    // ---------------------------------------------------------------------------------------------------------

    private sealed class HungItem
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ItemAgent Agent { get; }

        /// <summary>Item "hang" blocks without observing its token; "wait" blocks but honors it; "boom" fails once "hang" runs.</summary>
        public HungItem() => Agent = new ItemAgent(async (input, token) =>
        {
            if (input == "hang")
            {
                Started.TrySetResult();
                await Release.Task;
            }
            else if (input == "wait")
            {
                WaitStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else if (input == "boom")
            {
                await Started.Task;
                throw new InvalidOperationException("sibling failed");
            }
            return input;
        });
    }

    private static SequenceLifecycleEvidence ReadEvidence(ExecutionEvent executionEvent)
    {
        Assert.True(SequenceEvidence.TryRead(executionEvent, out var evidence));
        return evidence!;
    }

    private static ExecutionEvent[] RootTerminals(IEnumerable<ExecutionEvent> events) => events.Where(evt =>
        evt.NodeId is null && evt.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
            ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated or ExecutionEventKinds.OutcomeUnknown).ToArray();

    [Fact]
    public async Task A_hung_for_each_item_in_a_child_is_abandoned_after_the_grace_and_no_later_item_starts()
    {
        var hung = new HungItem();
        var parent = Parent("sequence-hung-for-each", Revision(ForEachChild("hung-child", ["a", "hang", "c"])),
            Revision(ForEachChild("failing-child", ["boom"])));

        // The sibling output fails while item "hang" of the other child ignores its token; the grace then abandons it.
        var run = await RunAsync(new WorkflowRunner(agents: hung.Agent)
            { SequenceAbandonGrace = TimeSpan.FromMilliseconds(300) }, parent);
        var eventsAtReturn = run.Events;

        // The run still returns, as an unknown outcome rather than an exception.
        Assert.Null(run.Failure);
        Assert.True(run.Result!.Failed);
        Assert.True(run.Result.OutcomeUnknown);
        Assert.Equal(ExecutionEventKinds.OutcomeUnknown, Assert.Single(RootTerminals(eventsAtReturn)).Kind);
        Assert.Equal("call", ReadEvidence(Assert.Single(eventsAtReturn, evt => evt.Kind == ExecutionEventKinds.SequenceOutputAbandoned)).OutputId);
        Assert.Equal("sibling", ReadEvidence(Assert.Single(eventsAtReturn, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed)).OutputId);
        Assert.Single(eventsAtReturn, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        // The hung item was started and never reported; the second item pass was admitted but item "c" never was.
        Assert.Contains(eventsAtReturn, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent" &&
            evt.ScopeId == "call/each/2");
        Assert.DoesNotContain(eventsAtReturn, evt => evt.NodeId == "agent" && evt.ScopeId == "call/each/2" &&
            evt.Kind is ExecutionEventKinds.NodeCompleted or ExecutionEventKinds.NodeFailed or ExecutionEventKinds.NodeCancelled);
        Assert.DoesNotContain(eventsAtReturn, evt => evt.ScopeId == "call/each/3");
        Assert.Equal(["a", "boom", "hang"], hung.Agent.Inputs.Order());
        // Both executors that were mid-flight stay open in their owners: the child's agent and the parent's call slot.
        var outstanding = run.Owners.SelectMany(owner => owner.Outstanding()).ToArray();
        Assert.Contains("agent:Started", outstanding);
        Assert.Contains("call:Started", outstanding);
        Assert.True(run.RootFrameOwner!.AbandonRequested);
        // Only the root run's frame owner is observable here (the probe is not passed to child runs), so whether the
        // child's own owner was marked is not asserted: see the ADR table.

        // Releasing the orphan afterwards adds no events, starts no item "c" and records nothing on the finished run.
        hung.Release.TrySetResult();
        await Task.Delay(700);
        Assert.Equal(eventsAtReturn.Length, run.Live.Count);
        Assert.Equal(["a", "boom", "hang"], hung.Agent.Inputs.Order());
        Assert.All(run.Owners.SelectMany(owner => owner.Outstanding()), ticket => Assert.Equal("each:Admitted", ticket));
    }

    [Fact]
    public async Task Host_cancel_with_a_hung_item_and_a_token_honoring_sibling_ends_outcome_unknown_after_the_grace()
    {
        var hung = new HungItem();
        var parent = Parent("sequence-cancel-for-each", Revision(ForEachChild("cancel-child", ["a", "hang", "c"])),
            Revision(ForEachChild("cancel-honoring-child", ["wait"])));
        using var cancellation = new CancellationTokenSource();

        var execution = RunAsync(new WorkflowRunner(agents: hung.Agent)
            { SequenceAbandonGrace = TimeSpan.FromMilliseconds(300) }, parent, cancellation.Token);
        await Task.WhenAll(hung.Started.Task, hung.WaitStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var run = await execution;
        var eventsAtReturn = run.Events;

        Assert.Null(run.Failure);
        Assert.True(run.Result!.OutcomeUnknown);
        Assert.Equal(ExecutionEventKinds.OutcomeUnknown, Assert.Single(RootTerminals(eventsAtReturn)).Kind);
        Assert.Equal("call", ReadEvidence(Assert.Single(eventsAtReturn, evt => evt.Kind == ExecutionEventKinds.SequenceOutputAbandoned)).OutputId);
        Assert.DoesNotContain(eventsAtReturn, evt => evt.ScopeId == "call/each/3");
        Assert.Contains("agent:Started", run.Owners.SelectMany(owner => owner.Outstanding()));

        hung.Release.TrySetResult();
        await Task.Delay(700);
        Assert.Equal(eventsAtReturn.Length, run.Live.Count);
        Assert.Equal(["a", "hang", "wait"], hung.Agent.Inputs.Order());
    }

    [Fact]
    public async Task Host_cancel_with_only_a_hung_item_keeps_the_run_open_until_the_item_returns_even_with_a_grace()
    {
        // Characterization of a limit that predates For Each: the grace starts only once the Sequence coordinator sees
        // an output settle after the stop. With a sole token-ignoring item nothing settles, so no abandon happens.
        // The same is true of a hung agent placed directly in a Sequence output (see the ADR table).
        var hung = new HungItem();
        var parent = Parent("sequence-cancel-sole-hung", Revision(ForEachChild("cancel-sole-child", ["a", "hang", "c"])),
            Revision(PlainChild("cancel-sole-plain")));
        using var cancellation = new CancellationTokenSource();

        var execution = RunAsync(new WorkflowRunner(agents: hung.Agent)
            { SequenceAbandonGrace = TimeSpan.FromMilliseconds(300) }, parent, cancellation.Token, TimeSpan.FromSeconds(60));
        await hung.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Task.Delay(2000);

        Assert.False(execution.IsCompleted);
        hung.Release.TrySetResult();
        var run = await execution.WaitAsync(TimeSpan.FromSeconds(30));
        // Once released the run ends as an ordinary cancellation (an OperationCanceledException), not outcome-unknown.
        Assert.IsAssignableFrom<OperationCanceledException>(run.Failure);
        Assert.DoesNotContain(run.Events, evt => evt.Kind == ExecutionEventKinds.OutcomeUnknown);
        Assert.DoesNotContain(run.Events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputAbandoned);
        Assert.Equal(["a", "hang"], hung.Agent.Inputs.Order());
        Assert.DoesNotContain(run.Events, evt => evt.ScopeId == "call/each/3");
    }
}
