using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Decision 0070, "Slice (b) unblocker 7": characterizes abandon and host cancel while a Sequence admitted in a pass
/// scope (the internal <c>sequencePassScopeId</c> seam from unblocker 3) holds concurrent outputs, and pausing a
/// validated, controlled Sequence run whose two outputs execute concurrently. Slice (b) stays closed; no For Each runs a
/// Sequence here.
/// </summary>
public sealed class SequencePassAbandonAndPauseTests : IDisposable
{
    private const string PassScope = "each/1";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-seqpass-" + Guid.NewGuid());
    private readonly SemaphoreSlim slot = new(1, 1);

    public void Dispose()
    {
        slot.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static ConnectionDefinition Exec(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "execution");

    private static NodeDefinition Tool(string binding) =>
        new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest(binding, new string('a', 64))) { McpArgumentsJson = "{}" };

    /// <summary>input -> Sequence[first, second]; first: tool demo.first -> output; second: tool demo.second -> output.</summary>
    private static WorkflowDefinition TwoOutputGraph() =>
        new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "sequence-pass-abandon-pause", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First"), new("second", "Second")]) },
                ["first-tool"] = Tool("demo.first"),
                ["second-tool"] = Tool("demo.second"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(), ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = Exec("input", "then", "sequence", "execute"),
                ["sequence-first"] = Exec("sequence", "then.first", "first-tool", "execute"),
                ["sequence-second"] = Exec("sequence", "then.second", "second-tool", "execute"),
                ["first-output"] = Exec("first-tool", "then", "output", "execute"),
                ["second-output"] = Exec("second-tool", "then", "output", "execute")
            }.ToImmutableSortedDictionary()
        };

    /// <summary>Holds each tool until released; optionally ignores cancellation like a token-ignoring executor.</summary>
    private sealed class HeldBroker(bool honorCancellation) : INodeToolBroker
    {
        public bool HonorCancellation { get; } = honorCancellation;
        public ConcurrentDictionary<string, TaskCompletionSource<string>> Release { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource> Started { get; } = new();

        public TaskCompletionSource<string> ReleaseFor(string id) =>
            Release.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public TaskCompletionSource StartedFor(string id) =>
            Started.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this);

        private sealed class Scope(HeldBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
                Invoke(bindingId, cancellationToken);
            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default) =>
                Invoke(bindingId, cancellationToken);
            private Task<string> Invoke(string bindingId, CancellationToken cancellationToken)
            {
                owner.StartedFor(bindingId).TrySetResult();
                var release = owner.ReleaseFor(bindingId).Task;
                return owner.HonorCancellation ? release.WaitAsync(cancellationToken) : release;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static async Task BothStarted(HeldBroker broker)
    {
        await broker.StartedFor("demo.first").Task.WaitAsync(TimeSpan.FromSeconds(10));
        await broker.StartedFor("demo.second").Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string Kinds(IEnumerable<ExecutionEvent> events) => string.Join(",", events.Select(evt => evt.Kind));

    [Fact]
    public async Task Abandon_after_grace_in_a_pass_scope_leaves_the_running_outputs_tickets_outstanding_so_the_frame_stays_unsettled()
    {
        Assert.Empty(WorkflowValidator.Validate(TwoOutputGraph()));
        var revision = WorkflowRevision.Create(TwoOutputGraph(), "sequence-pass-abandon");
        var broker = new HeldBroker(honorCancellation: false);
        var events = new ConcurrentQueue<ExecutionEvent>();
        WorkflowRunner.StructuredActivationFrameOwner? frameOwner = null;
        WorkflowDispatchAdmissionOwner? admissionOwner = null;
        using var cancel = new CancellationTokenSource();

        var execution = new WorkflowRunner(broker) { SequenceAbandonGrace = TimeSpan.FromMilliseconds(200) }
            .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "hello", _ => { },
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancel.Token,
                ownerProbe: owner => admissionOwner = owner, frameOwnerProbe: owner => frameOwner = owner,
                sequencePassScopeId: PassScope);
        await BothStarted(broker);
        Assert.Equal(2, admissionOwner!.OutstandingIn(PassScope).Count);

        cancel.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        // The run ends with an unknown outcome and both outputs are recorded as abandoned.
        Assert.True(result.OutcomeUnknown, Kinds(events));
        Assert.True(result.Failed);
        Assert.Single(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.OutcomeUnknown);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputAbandoned));
        // Pinned current behavior (abandon limits rule): the abandon path does not revoke tickets, so the two started
        // output tickets stay outstanding in the pass scope and the pass frame refuses to settle. A For Each owning this
        // pass would therefore not see the pass as settled; nothing was silently released.
        var outstanding = admissionOwner.OutstandingIn(PassScope);
        Assert.Equal(2, outstanding.Count);
        Assert.Empty(admissionOwner.Outstanding().Except(outstanding));
        var error = Assert.Throws<InvalidOperationException>(() => frameOwner!.EnsureFrameSettled(PassScope, null));
        Assert.Contains(PassScope, error.Message);

        // The detached executors finishing later produce no event after the terminal (late events are closed).
        var countAtTerminal = events.Count;
        broker.ReleaseFor("demo.first").TrySetResult("late");
        broker.ReleaseFor("demo.second").TrySetResult("late");
        await Task.Delay(300);
        Assert.Equal(countAtTerminal, events.Count);
    }

    [Fact]
    public async Task Host_cancel_without_grace_in_a_pass_scope_settles_every_ticket_and_the_frame()
    {
        var revision = WorkflowRevision.Create(TwoOutputGraph(), "sequence-pass-cancel");
        var broker = new HeldBroker(honorCancellation: true);
        var events = new ConcurrentQueue<ExecutionEvent>();
        WorkflowRunner.StructuredActivationFrameOwner? frameOwner = null;
        WorkflowDispatchAdmissionOwner? admissionOwner = null;
        using var cancel = new CancellationTokenSource();

        var execution = new WorkflowRunner(broker)
            .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "hello", _ => { },
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancel.Token,
                ownerProbe: owner => admissionOwner = owner, frameOwnerProbe: owner => frameOwner = owner,
                sequencePassScopeId: PassScope);
        await BothStarted(broker);
        Assert.Equal(2, admissionOwner!.OutstandingIn(PassScope).Count);

        cancel.Cancel();
        WorkflowExecutionResult? result = null;
        var outcome = await Record.ExceptionAsync(async () => result = await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(outcome is OperationCanceledException || result is { Cancelled: true },
            $"{outcome} / {Kinds(events)}");

        Assert.Single(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Cancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputAbandoned);
        Assert.Empty(admissionOwner.OutstandingIn(PassScope));
        Assert.Empty(admissionOwner.Outstanding());
        frameOwner!.EnsureFrameSettled(PassScope, null);
    }

    [Fact]
    public async Task Pause_of_a_validated_controlled_sequence_waits_for_both_concurrent_outputs_and_cancel_while_paused_ends_cleanly()
    {
        Assert.Empty(WorkflowValidator.Validate(TwoOutputGraph()));
        var store = new FileRunJournal(directory);
        var revision = WorkflowRevision.Create(TwoOutputGraph(), "sequence-pause-concurrent");
        var header = store.Create(revision, "hello").Header;
        using var control = new ControlledRun(store, header, slot, TimeProvider.System, WorkflowWaitOptions.Default);
        var broker = new HeldBroker(honorCancellation: true);
        var completion = Task.Run(async () =>
        {
            try { return await new WorkflowRunner(broker).ExecuteControlledAsync(header.RunId, revision, "hello",
                control.RecordAsync, control, control.Cancellation.Token); }
            catch (OperationCanceledException) { return "cancelled"; }
        });
        await BothStarted(broker);

        control.RequestPause();
        await Task.Delay(300);
        Assert.DoesNotContain(store.Get(header.RunId)!.Events, evt => evt.Kind == ExecutionEventKinds.Paused);

        // One output finishing is not enough: the sibling is still executing.
        broker.ReleaseFor("demo.first").TrySetResult("first-result");
        await Task.Delay(300);
        Assert.DoesNotContain(store.Get(header.RunId)!.Events, evt => evt.Kind == ExecutionEventKinds.Paused);

        broker.ReleaseFor("demo.second").TrySetResult("second-result");
        await UntilAsync(() => store.Get(header.RunId)!.Events.Any(evt => evt.Kind == ExecutionEventKinds.Paused));
        var paused = store.Get(header.RunId)!;
        Assert.False(paused.IsTerminal);
        var pausedAt = paused.Events.ToList().FindIndex(evt => evt.Kind == ExecutionEventKinds.Paused);
        Assert.All(new[] { "first-tool", "second-tool" }, node =>
            Assert.InRange(paused.Events.ToList().FindIndex(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == node), 0, pausedAt));

        control.Cancel();
        Assert.Equal("cancelled", await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await UntilAsync(() => store.Get(header.RunId)!.IsTerminal);
        var run = store.Get(header.RunId)!;
        Assert.Equal("cancelled", run.Status);
        Assert.Empty(WorkflowWorkEvidence.Read(run).UnresolvedWork);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The awaited condition was not reached.");
            await Task.Delay(10);
        }
    }
}
