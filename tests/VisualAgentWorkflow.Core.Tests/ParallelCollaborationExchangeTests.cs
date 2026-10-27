using VisualAgentWorkflow.Core;
using System.Text;
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
    public void Send_WhenCanonicalUtf8SnapshotLimitWouldBeExceeded_RejectsAppendBeforeRecordingIt()
    {
        var exchange = CreateExchange();
        exchange.Send("branch_a", "research", new string('é', ParallelCollaborationExchange.MaximumMessageCharacters));
        exchange.Send("branch_b", "research", new string('界', ParallelCollaborationExchange.MaximumMessageCharacters));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            exchange.Send("branch_a", "research", string.Concat(Enumerable.Repeat("😀", ParallelCollaborationExchange.MaximumMessageCharacters / 2))));

        Assert.Contains("byte limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(exchange.IsAborted);
        Assert.False(exchange.IsReleased);
        var accepted = exchange.Arrive("branch_a");
        Assert.False(accepted.Released);
        var release = exchange.Arrive("branch_b");
        Assert.True(release.Released);
        Assert.NotNull(release.Snapshot);
        Assert.InRange(release.Snapshot.SerializedBytes, 1, ParallelCollaborationExchange.MaximumSnapshotBytes);
        Assert.Equal(release.Snapshot.SerializedBytes, System.Text.Encoding.UTF8.GetByteCount(release.Snapshot.CanonicalJson));
        Assert.Equal(64, release.Snapshot.Sha256.Length);
    }

    [Fact]
    public void Release_UsesStableCanonicalJsonAndSha256AcrossChannelInsertionOrder()
    {
        var first = CreateExchange();
        first.Send("branch_a", "summary", "ready");
        first.Send("branch_b", "research", "café");
        first.Arrive("branch_a");
        var firstSnapshot = first.Arrive("branch_b").Snapshot!;

        var second = CreateExchange();
        second.Send("branch_a", "summary", "ready");
        second.Send("branch_b", "research", "café");
        second.Arrive("branch_a");
        var secondSnapshot = second.Arrive("branch_b").Snapshot!;

        Assert.Equal(firstSnapshot.CanonicalJson, secondSnapshot.CanonicalJson);
        Assert.Equal(firstSnapshot.Sha256, secondSnapshot.Sha256);
        Assert.True(firstSnapshot.CanonicalJson.IndexOf("\"research\"", StringComparison.Ordinal) <
            firstSnapshot.CanonicalJson.IndexOf("\"summary\"", StringComparison.Ordinal));
        Assert.Contains("\"research\"", firstSnapshot.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\\u00E9", firstSnapshot.CanonicalJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PhasedExchanges_ShareCumulativeChannelMessageAndSnapshotByteBudgets()
    {
        var budget = new ParallelCollaborationBudget();
        var first = new ParallelCollaborationExchange(Guid.NewGuid(), Guid.NewGuid(), ["branch_a", "branch_b"], ["research"], budget);
        for (var index = 0; index < ParallelCollaborationLimits.MaximumMessagesPerChannel; index++)
            first.Send(index % 2 == 0 ? "branch_a" : "branch_b", "research", "finding");
        Assert.Throws<InvalidOperationException>(() => first.Send("branch_a", "research", "too many"));
        first.Arrive("branch_a");
        var firstRelease = first.Arrive("branch_b").Snapshot!;

        var second = new ParallelCollaborationExchange(Guid.NewGuid(), Guid.NewGuid(), ["branch_a", "branch_b"], ["research"], budget);
        Assert.Throws<InvalidOperationException>(() => second.Send("branch_a", "research", "phase-two message"));

        budget = new ParallelCollaborationBudget();
        first = new ParallelCollaborationExchange(Guid.NewGuid(), Guid.NewGuid(), ["branch_a", "branch_b"], ["research"], budget);
        for (var index = 0; index < 16; index++) first.Send(index % 2 == 0 ? "branch_a" : "branch_b", "research", new string('x', 3_500));
        first.Arrive("branch_a");
        firstRelease = first.Arrive("branch_b").Snapshot!;
        second = new ParallelCollaborationExchange(Guid.NewGuid(), Guid.NewGuid(), ["branch_a", "branch_b"], ["research"], budget);
        second.Send("branch_a", "research", new string('y', 3_500));
        second.Send("branch_b", "research", new string('z', 3_500));
        Assert.Throws<InvalidOperationException>(() => second.Send("branch_a", "research", new string('z', 3_500)));
        Assert.InRange(firstRelease.SerializedBytes, 1, ParallelCollaborationLimits.MaximumSnapshotBytes);
        Assert.Equal(firstRelease.SerializedBytes, Encoding.UTF8.GetByteCount(firstRelease.CanonicalJson));
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
