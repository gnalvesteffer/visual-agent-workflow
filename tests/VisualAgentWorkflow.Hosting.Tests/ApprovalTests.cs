using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ApprovalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-approval-" + Guid.NewGuid());
    private static WorkflowDefinition Definition() => GraphEditing.InsertApprovalBefore(
        PipelineEditing.ReplaceNode(PipelineTemplates.Create("uppercase"), "step-1", new(NodeTypes.McpTool, "1.0.0", new("fixture.tool", new string('0', 64)))), "step-1");
    private LocalWorkflowRuntime Runtime(CountingBroker broker) => new(new(directory), NullLogger<LocalWorkflowRuntime>.Instance, broker);
    private static async Task<RunSnapshot> WaitFor(LocalWorkflowRuntime runtime, Guid id, Func<RunSnapshot, bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var run = (await runtime.GetRunAsync(id))!;
            if (condition(run)) return run;
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task ApprovalBindsExactInputToolAndInvocationAndDoesNotBlockOtherRuns()
    {
        var broker = new CountingBroker();
        using var runtime = Runtime(broker);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var started = await runtime.StartRunAsync(Definition(), "abc 🌍");
            var waiting = await WaitFor(runtime, started.Header.RunId, r => r.Status == "waiting-approval");
            var request = WorkflowApprovalEvidence.Pending(waiting)!;
            Assert.Equal(0, broker.Calls);
            Assert.Equal("abc 🌍", request.Action.Input);
            Assert.Equal(WorkflowApprovalAction.HashInput("abc 🌍"), request.Action.InputHash);
            Assert.Equal(started.Header.ContentHash, request.Action.WorkflowHash);
            Assert.Equal(started.Header.RevisionId, request.Action.RevisionId);
            Assert.Equal("step-1", request.Action.TargetNodeId);
            Assert.Equal("fixture.tool", request.Action.Tool.BindingId);
            var other = await runtime.StartRunAsync(PipelineTemplates.Create("uppercase"), "other");
            Assert.Equal("completed", (await WaitFor(runtime, other.Header.RunId, r => r.IsTerminal)).Status);
            await runtime.ResumeRunAsync(started.Header.RunId);
            Assert.Equal("waiting-approval", (await runtime.GetRunAsync(started.Header.RunId))!.Status);
            await runtime.DecideApprovalAsync(started.Header.RunId, request.RequestId, request.ActionHash, true, "host-user-alice");
            var finished = await WaitFor(runtime, started.Header.RunId, r => r.IsTerminal);
            Assert.Equal("completed", finished.Status);
            Assert.Equal(1, broker.Calls);
            Assert.Equal("ABC 🌍", finished.Events[^1].Data);
            Assert.Equal(request.Action.TargetInvocationId, broker.LastInvocation);
            var decision = WorkflowApprovalEvidence.ReadDecision(Assert.Single(finished.Events, e => e.Kind == ExecutionEventKinds.ApprovalApproved))!;
            Assert.Equal("host-user-alice", decision.Actor);
            await runtime.DecideApprovalAsync(started.Header.RunId, request.RequestId, request.ActionHash, true, "host-user-bob");
            Assert.Equal(finished.Events.Length, (await runtime.GetRunAsync(started.Header.RunId))!.Events.Length);
            Assert.Equal(1, broker.Calls);
            Assert.Throws<WorkflowApprovalConflictException>(() => { _ = runtime.DecideApprovalAsync(started.Header.RunId, request.RequestId, request.ActionHash, false); });
            Assert.Equal(Enumerable.Range(1, finished.Events.Length).Select(i => (long)i), finished.Events.Select(e => e.Sequence));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StaleOrCrossRunDecisionsCannotReleaseARecordedAction()
    {
        var broker = new CountingBroker();
        using var runtime = Runtime(broker);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var first = await runtime.StartRunAsync(Definition(), "first");
            var second = await runtime.StartRunAsync(Definition(), "second");
            var request = WorkflowApprovalEvidence.Pending(await WaitFor(runtime, first.Header.RunId, r => r.Status == "waiting-approval"))!;
            await WaitFor(runtime, second.Header.RunId, r => r.Status == "waiting-approval");
            Assert.Throws<WorkflowApprovalConflictException>(() => { _ = runtime.DecideApprovalAsync(first.Header.RunId, request.RequestId, new string('0', 64), true); });
            Assert.Throws<WorkflowApprovalConflictException>(() => { _ = runtime.DecideApprovalAsync(second.Header.RunId, request.RequestId, request.ActionHash, true); });
            Assert.Throws<KeyNotFoundException>(() => { _ = runtime.DecideApprovalAsync(Guid.NewGuid(), request.RequestId, request.ActionHash, true); });
            Assert.Equal(0, broker.Calls);
            await runtime.DecideApprovalAsync(first.Header.RunId, request.RequestId, request.ActionHash, false);
            var failed = await WaitFor(runtime, first.Header.RunId, r => r.IsTerminal);
            Assert.Equal("failed", failed.Status);
            Assert.Single(failed.Events, e => e.Kind == ExecutionEventKinds.ApprovalRejected);
            Assert.DoesNotContain(failed.Events, e => e.NodeId == "step-1");
            await runtime.CancelRunAsync(second.Header.RunId);
            await WaitFor(runtime, second.Header.RunId, r => r.IsTerminal);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ApprovingWhilePausedCannotResumeAndHostPolicyStillDeniesTool()
    {
        var broker = new CountingBroker { Allowed = false };
        using var runtime = Runtime(broker);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var started = await runtime.StartRunAsync(Definition(), "abc");
            var request = WorkflowApprovalEvidence.Pending(await WaitFor(runtime, started.Header.RunId, r => r.Status == "waiting-approval"))!;
            await runtime.PauseRunAsync(started.Header.RunId);
            Assert.Equal("paused", (await runtime.GetRunAsync(started.Header.RunId))!.Status);
            await runtime.DecideApprovalAsync(started.Header.RunId, request.RequestId, request.ActionHash, true);
            Assert.Equal("paused", (await runtime.GetRunAsync(started.Header.RunId))!.Status);
            Assert.Equal(0, broker.Calls);
            await runtime.ResumeRunAsync(started.Header.RunId);
            var failed = await WaitFor(runtime, started.Header.RunId, r => r.IsTerminal);
            Assert.Equal("failed", failed.Status);
            Assert.Contains(failed.Events, e => e.Kind == ExecutionEventKinds.ToolDenied);
            Assert.Equal(0, broker.Calls);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancelAndOwnerRestartInvalidatePendingRequestsWithoutToolCalls()
    {
        var broker = new CountingBroker();
        WorkflowApprovalRequest request;
        Guid id;
        using (var runtime = Runtime(broker))
        {
            await runtime.StartAsync(CancellationToken.None);
            var started = await runtime.StartRunAsync(Definition(), "abc");
            id = started.Header.RunId;
            request = WorkflowApprovalEvidence.Pending(await WaitFor(runtime, id, r => r.Status == "waiting-approval"))!;
            await runtime.StopAsync(CancellationToken.None);
        }
        using var restarted = Runtime(broker);
        await restarted.StartAsync(CancellationToken.None);
        try
        {
            Assert.True((await restarted.GetRunAsync(id))!.IsTerminal);
            Assert.Throws<WorkflowApprovalConflictException>(() => { _ = restarted.DecideApprovalAsync(id, request.RequestId, request.ActionHash, true); });
            var run = await restarted.StartRunAsync(Definition(), "cancel");
            var pending = WorkflowApprovalEvidence.Pending(await WaitFor(restarted, run.Header.RunId, r => r.Status == "waiting-approval"))!;
            await restarted.CancelRunAsync(run.Header.RunId);
            Assert.Throws<WorkflowApprovalConflictException>(() => { _ = restarted.DecideApprovalAsync(run.Header.RunId, pending.RequestId, pending.ActionHash, true); });
            var cancelled = await WaitFor(restarted, run.Header.RunId, r => r.IsTerminal);
            Assert.Equal("cancelled", cancelled.Status);
            Assert.Single(cancelled.Events, e => e.Kind == ExecutionEventKinds.ApprovalCancelled);
            Assert.Null(WorkflowApprovalEvidence.Pending(cancelled));
            Assert.Equal(0, broker.Calls);
        }
        finally { await restarted.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ConcurrentMatchingDecisionsRecordAndExecuteOnlyOnce()
    {
        var broker = new CountingBroker();
        using var runtime = Runtime(broker);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(Definition(), "race");
            var pending = WorkflowApprovalEvidence.Pending(await WaitFor(runtime, run.Header.RunId, r => r.Status == "waiting-approval"))!;
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => runtime.DecideApprovalAsync(run.Header.RunId, pending.RequestId, pending.ActionHash, true))));
            var finished = await WaitFor(runtime, run.Header.RunId, r => r.IsTerminal);
            Assert.Equal("completed", finished.Status);
            Assert.Single(finished.Events, e => e.Kind == ExecutionEventKinds.ApprovalApproved);
            Assert.Equal(1, broker.Calls);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CrashSnapshotIsInterruptedAndCannotReleaseAnOldRequest()
    {
        var broker = new CountingBroker();
        using var runtime = Runtime(broker);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(Definition(), "crash");
            var waiting = await WaitFor(runtime, run.Header.RunId, r => r.Status == "waiting-approval");
            var request = WorkflowApprovalEvidence.Pending(waiting)!;
            var cloned = Path.Combine(directory, "crash-copy");
            Directory.CreateDirectory(cloned);
            File.WriteAllText(Path.Combine(cloned, run.Header.RunId + ".run.json"), JsonSerializer.Serialize(waiting.Header, JsonSerializerOptions.Web));
            File.WriteAllLines(Path.Combine(cloned, run.Header.RunId + ".events.jsonl"), waiting.Events.Select(e => JsonSerializer.Serialize(e, JsonSerializerOptions.Web)));
            using var recovered = new LocalWorkflowRuntime(new(cloned), NullLogger<LocalWorkflowRuntime>.Instance, broker);
            await recovered.StartAsync(CancellationToken.None);
            try
            {
                var interrupted = (await recovered.GetRunAsync(run.Header.RunId))!;
                Assert.Equal("interrupted", interrupted.Status);
                Assert.Contains(interrupted.Events, e => e.Kind == ExecutionEventKinds.ApprovalRequested);
                Assert.Null(WorkflowApprovalEvidence.Pending(interrupted));
                Assert.Throws<WorkflowApprovalConflictException>(() => { _ = recovered.DecideApprovalAsync(run.Header.RunId, request.RequestId, request.ActionHash, true); });
                Assert.Equal(0, broker.Calls);
            }
            finally { await recovered.StopAsync(CancellationToken.None); }
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AnOldApprovalCannotReleaseALaterActionInTheSameRun()
    {
        var broker = new CountingBroker();
        var firstGraph = Definition();
        var graph = GraphEditing.InsertNodeAfter(firstGraph, "step-1", "out", new(NodeTypes.McpTool, "1.0.0", new("fixture.tool", new string('0', 64))));
        var secondTool = graph.Nodes.Keys.Except(firstGraph.Nodes.Keys).Single();
        graph = GraphEditing.InsertApprovalBefore(graph, secondTool);
        using var runtime = Runtime(broker);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(graph, "abc");
            var first = WorkflowApprovalEvidence.Pending(await WaitFor(runtime, run.Header.RunId, r => r.Status == "waiting-approval"))!;
            await runtime.DecideApprovalAsync(run.Header.RunId, first.RequestId, first.ActionHash, true);
            var waiting = await WaitFor(runtime, run.Header.RunId, r => r.Status == "waiting-approval" && WorkflowApprovalEvidence.Pending(r)?.RequestId != first.RequestId);
            var second = WorkflowApprovalEvidence.Pending(waiting)!;
            Assert.Equal("ABC", second.Action.Input);
            Assert.Equal(1, broker.Calls);
            await runtime.DecideApprovalAsync(run.Header.RunId, first.RequestId, first.ActionHash, true);
            Assert.Equal(second.RequestId, WorkflowApprovalEvidence.Pending((await runtime.GetRunAsync(run.Header.RunId))!)!.RequestId);
            Assert.Throws<WorkflowApprovalConflictException>(() => { _ = runtime.DecideApprovalAsync(run.Header.RunId, second.RequestId, first.ActionHash, true); });
            Assert.Equal(1, broker.Calls);
            await runtime.DecideApprovalAsync(run.Header.RunId, second.RequestId, second.ActionHash, true);
            var finished = await WaitFor(runtime, run.Header.RunId, r => r.IsTerminal);
            Assert.Equal("completed", finished.Status);
            Assert.Equal(2, broker.Calls);
            Assert.Equal(2, finished.Events.Count(e => e.Kind == ExecutionEventKinds.ApprovalApproved));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class CountingBroker : INodeToolBroker
    {
        public bool Allowed { get; init; } = true;
        public int Calls { get; private set; }
        public Guid LastInvocation { get; private set; }
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this, invocation);
        private sealed class Scope(CountingBroker owner, NodeInvocationContext invocation) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) => owner.ListBindingsAsync(cancellationToken);
            public async Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
            {
                await invocation.Record(ExecutionEventKinds.ToolRequested, bindingId);
                if (!owner.Allowed)
                {
                    await invocation.Record(ExecutionEventKinds.ToolDenied, bindingId);
                    throw new ToolAccessDeniedException();
                }
                owner.Calls++;
                owner.LastInvocation = invocation.InvocationId;
                var output = input.ToUpperInvariant();
                await invocation.Record(ExecutionEventKinds.ToolCompleted, output);
                return output;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
