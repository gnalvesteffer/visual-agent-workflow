using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ParallelCollaborationExchangeTests
{
    [Fact]
    public void Arrive_ReleasesOnlyAfterEveryDeclaredBranchAndPublishesSameSnapshot()
    {
        var exchange = CreateExchange();
        var firstMessage = exchange.Send("branch_a", "research", "Finding A");
        var secondMessage = exchange.Send("branch_b", "research", "Finding B");

        var firstArrival = exchange.Arrive("branch_a");

        Assert.False(firstArrival.Released);
        Assert.Null(firstArrival.Snapshot);
        Assert.Equal(1, firstArrival.ArrivedCount);
        Assert.Throws<InvalidOperationException>(() => exchange.ReadSnapshot("branch_a", "research"));

        var release = exchange.Arrive("branch_b");

        Assert.True(release.Released);
        Assert.Equal(2, release.ParticipantCount);
        Assert.Equal(new[] { firstMessage, secondMessage }, exchange.ReadSnapshot("branch_a", "research").ToArray());
        Assert.Equal(exchange.ReadSnapshot("branch_a", "research"), exchange.ReadSnapshot("branch_b", "research"));
        Assert.Empty(exchange.ReadSnapshot("branch_a", "summary"));
    }

    [Fact]
    public void Send_RejectsUnknownParticipantsChannelsAndMessagesAfterArrival()
    {
        var exchange = CreateExchange();

        Assert.Throws<InvalidOperationException>(() => exchange.Send("outsider", "research", "hello"));
        Assert.Throws<InvalidOperationException>(() => exchange.Send("branch_a", "unassigned", "hello"));
        Assert.Throws<ArgumentException>(() => exchange.Send("branch_a", "research", " "));
        Assert.Throws<ArgumentException>(() => exchange.Send("branch_a", "research", new string('x', ParallelCollaborationExchange.MaximumMessageCharacters + 1)));

        exchange.Arrive("branch_a");

        Assert.Throws<InvalidOperationException>(() => exchange.Send("branch_a", "research", "too late"));
    }

    [Fact]
    public void Arrive_RejectsDuplicateAndUnknownBranches()
    {
        var exchange = CreateExchange();

        Assert.Throws<InvalidOperationException>(() => exchange.Arrive("outsider"));
        Assert.True(exchange.IsAborted);

        exchange = CreateExchange();
        exchange.Arrive("branch_a");

        Assert.Throws<InvalidOperationException>(() => exchange.Arrive("branch_a"));
        Assert.True(exchange.IsAborted);
    }

    [Fact]
    public async Task Send_ConcurrentAppendsReceiveUniqueMonotonicSequences()
    {
        var exchange = CreateExchange();
        var sends = Enumerable.Range(0, 20)
            .Select(index => Task.Run(() => exchange.Send(index % 2 == 0 ? "branch_a" : "branch_b", "research", $"message-{index}")))
            .ToArray();

        var recorded = await Task.WhenAll(sends);

        Assert.Equal(Enumerable.Range(1, 20).Select(value => (long)value), recorded.Select(message => message.Sequence).Order());
        Assert.Equal(20, recorded.Select(message => message.Sequence).Distinct().Count());
    }

    [Fact]
    public void Arrive_WhenSnapshotLimitIsExceeded_AbortsWithoutExposingPartialSnapshot()
    {
        var exchange = CreateExchange();
        exchange.Send("branch_a", "research", new string('a', ParallelCollaborationExchange.MaximumMessageCharacters));
        exchange.Send("branch_b", "research", new string('b', ParallelCollaborationExchange.MaximumMessageCharacters));
        exchange.Send("branch_a", "research", new string('c', ParallelCollaborationExchange.MaximumMessageCharacters));
        exchange.Send("branch_b", "research", new string('d', ParallelCollaborationExchange.MaximumMessageCharacters));
        exchange.Send("branch_a", "research", new string('e', ParallelCollaborationExchange.MaximumMessageCharacters));
        exchange.Arrive("branch_a");

        Assert.Throws<InvalidOperationException>(() => exchange.Arrive("branch_b"));
        Assert.True(exchange.IsAborted);
        Assert.False(exchange.IsReleased);
        Assert.Throws<InvalidOperationException>(() => exchange.ReadSnapshot("branch_a", "research"));
    }

    [Fact]
    public void Abort_PreventsReleaseAndSnapshotReads()
    {
        var exchange = CreateExchange();
        exchange.Send("branch_a", "research", "Finding A");
        exchange.Arrive("branch_a");
        exchange.Abort("Participant failed.");

        Assert.Equal("Participant failed.", exchange.AbortReason);
        Assert.Throws<InvalidOperationException>(() => exchange.Arrive("branch_b"));
        Assert.Throws<InvalidOperationException>(() => exchange.Send("branch_b", "research", "Finding B"));
    }

    [Fact]
    public void Create_RejectsInvalidBranchAndChannelSets()
    {
        Assert.Throws<ArgumentException>(() => new ParallelCollaborationExchange(Guid.NewGuid(), Guid.NewGuid(), ["bad id", "branch_b"], ["research"]));
        Assert.Throws<ArgumentException>(() => new ParallelCollaborationExchange(Guid.NewGuid(), Guid.NewGuid(), ["branch_a", "branch_a"], ["research"]));
        Assert.Throws<ArgumentException>(() => new ParallelCollaborationExchange(Guid.NewGuid(), Guid.NewGuid(), ["branch_a", "branch_b"], ["Research"]));
        Assert.Throws<ArgumentException>(() => new ParallelCollaborationExchange(Guid.NewGuid(), Guid.NewGuid(), ["branch_a", "branch_b"], []));
    }

    private static ParallelCollaborationExchange CreateExchange() => new(
        Guid.NewGuid(), Guid.NewGuid(), ["branch_a", "branch_b"], ["research", "summary"]);
}
