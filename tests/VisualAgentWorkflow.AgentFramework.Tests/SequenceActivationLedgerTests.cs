using VisualAgentWorkflow.AgentFramework;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class SequenceActivationLedgerTests
{
    [Fact]
    public void Dispatch_is_ordered_but_active_outputs_may_overlap()
    {
        var ledger = new SequenceActivationLedger(["first", "second", "third"]);
        var first = ledger.DispatchNext(Guid.NewGuid());
        var second = ledger.DispatchNext(Guid.NewGuid());
        var third = ledger.DispatchNext(Guid.NewGuid());

        Assert.Equal("first", first.OutputId);
        Assert.Equal("second", second.OutputId);
        Assert.Equal("third", third.OutputId);

        ledger.RecordTerminal(first.ActivationId!.Value, SequenceTerminalState.Completed);
        ledger.RecordTerminal(second.ActivationId!.Value, SequenceTerminalState.Completed);
        ledger.RecordTerminal(third.ActivationId!.Value, SequenceTerminalState.Completed);

        Assert.True(ledger.IsTerminal);
        Assert.Equal(
            [SequenceActivationState.Completed, SequenceActivationState.Completed, SequenceActivationState.Completed],
            ledger.Snapshot.Select(snapshot => snapshot.State));
    }

    [Fact]
    public void Failure_marks_undispatched_outputs_skipped()
    {
        var ledger = new SequenceActivationLedger(["first", "second"]);
        var first = ledger.DispatchNext(Guid.NewGuid());

        ledger.RecordTerminal(first.ActivationId!.Value, SequenceTerminalState.Failed);

        Assert.True(ledger.IsTerminal);
        Assert.Equal(SequenceActivationState.Failed, ledger.Snapshot[0].State);
        Assert.Equal(SequenceActivationState.Skipped, ledger.Snapshot[1].State);
        Assert.Throws<InvalidOperationException>(() => ledger.DispatchNext(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Output_count_is_bounded(int count)
    {
        var outputIds = Enumerable.Range(0, count).Select(index => $"output-{index}").ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => new SequenceActivationLedger(outputIds));
    }
}
