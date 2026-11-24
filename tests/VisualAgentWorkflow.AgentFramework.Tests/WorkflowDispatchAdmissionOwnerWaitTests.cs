using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>Waiting drain primitive: completes when a scope's other admitted work settles (Decision 0070).</summary>
public sealed class WorkflowDispatchAdmissionOwnerWaitTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static Guid Start(WorkflowDispatchAdmissionOwner owner, string node, string? scope)
    {
        var id = owner.Admit(node, scope);
        owner.Begin(id, node);
        return id;
    }

    [Fact]
    public void NothingOutstandingReturnsCompletedTask()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        Assert.True(owner.WhenScopeSettledAsync("loop/0", null).IsCompletedSuccessfully);
        var id = Start(owner, "a", "loop/0");
        owner.Settle(id, "a");
        Assert.True(owner.WhenScopeSettledAsync("loop/0", null).IsCompletedSuccessfully);
        Assert.True(owner.WhenScopeSettledAsync(null, null).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CompletesAfterLastSettle()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var first = Start(owner, "a", "loop/0");
        var second = Start(owner, "b", "loop/0");
        var wait = owner.WhenScopeSettledAsync("loop/0", null);
        Assert.False(wait.IsCompleted);
        owner.Settle(first, "a");
        Assert.False(wait.IsCompleted);
        owner.Settle(second, "b");
        await wait.WaitAsync(Patience);
    }

    [Fact]
    public async Task ExceptedTicketIsIgnored()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var self = Start(owner, "a", "loop/0");
        Assert.True(owner.WhenScopeSettledAsync("loop/0", self).IsCompletedSuccessfully);
        var other = Start(owner, "b", "loop/0");
        var wait = owner.WhenScopeSettledAsync("loop/0", self);
        Assert.False(wait.IsCompleted);
        owner.Settle(other, "b");
        await wait.WaitAsync(Patience);
    }

    [Fact]
    public async Task RevokeCompletesTheWait()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = owner.Admit("a", "loop/0");
        var wait = owner.WhenScopeSettledAsync("loop/0", null);
        Assert.False(wait.IsCompleted);
        owner.Revoke(id);
        await wait.WaitAsync(Patience);
    }

    [Fact]
    public async Task CancellationThrowsOperationCanceled()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        Start(owner, "a", "loop/0");
        using var cts = new CancellationTokenSource();
        var wait = owner.WhenScopeSettledAsync("loop/0", null, cts.Token);
        Assert.False(wait.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Patience));
        using var already = new CancellationTokenSource();
        already.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            owner.WhenScopeSettledAsync("loop/0", null, already.Token).WaitAsync(Patience));
    }

    [Fact]
    public async Task ContinuationRunsOutsideTheLock()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = Start(owner, "a", "loop/0");
        var reentered = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = owner.WhenScopeSettledAsync("loop/0", null);
        var continuation = wait.ContinueWith(_ => reentered.TrySetResult(owner.Outstanding()),
            TaskContinuationOptions.ExecuteSynchronously);
        var settling = Task.Run(() => owner.Settle(id, "a"));
        await settling.WaitAsync(Patience);
        Assert.Empty(await reentered.Task.WaitAsync(Patience));
        await continuation.WaitAsync(Patience);
    }

    [Fact]
    public async Task OtherScopesAreUnaffected()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var inScope = Start(owner, "a", "loop/0");
        var elsewhere = Start(owner, "b", "loop/1");
        Assert.True(owner.WhenScopeSettledAsync("loop/2", null).IsCompletedSuccessfully);
        var wait = owner.WhenScopeSettledAsync("loop/0", null);
        owner.Settle(elsewhere, "b");
        Assert.False(wait.IsCompleted);
        owner.Settle(inScope, "a");
        await wait.WaitAsync(Patience);
    }
}
