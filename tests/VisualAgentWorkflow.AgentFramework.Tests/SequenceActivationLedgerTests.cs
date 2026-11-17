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

    [Fact]
    public void Closing_admission_skips_only_outputs_that_have_not_started()
    {
        var ledger = new SequenceActivationLedger(["first", "second", "third"]);
        var first = ledger.DispatchNext(Guid.NewGuid());

        var snapshot = ledger.SkipPending();

        Assert.Equal(SequenceActivationState.Dispatched, snapshot[0].State);
        Assert.Equal([SequenceActivationState.Skipped, SequenceActivationState.Skipped],
            snapshot.Skip(1).Select(output => output.State));
        Assert.Throws<InvalidOperationException>(() => ledger.DispatchNext(Guid.NewGuid()));
        ledger.RecordTerminal(first.ActivationId!.Value, SequenceTerminalState.Completed);
        Assert.True(ledger.IsTerminal);
    }

    [Fact]
    public async Task Admission_and_closing_race_has_one_atomic_winner()
    {
        var ledger = new SequenceActivationLedger(["first", "second", "third"]);
        using var start = new Barrier(3);
        SequenceActivationSnapshot? dispatched = null;
        var dispatch = Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                dispatched = ledger.DispatchNext(Guid.NewGuid());
            }
            catch (InvalidOperationException)
            {
                // Closing admission won the same synchronized boundary.
            }
        });
        var close = Task.Run(() =>
        {
            start.SignalAndWait();
            ledger.SkipPending();
        });

        start.SignalAndWait();
        await Task.WhenAll(dispatch, close);

        var snapshot = ledger.Snapshot;
        Assert.DoesNotContain(snapshot, output => output.State == SequenceActivationState.Declared);
        Assert.Equal(dispatched is null ? 0 : 1,
            snapshot.Count(output => output.State == SequenceActivationState.Dispatched));
        Assert.Equal(dispatched is null ? 3 : 2,
            snapshot.Count(output => output.State == SequenceActivationState.Skipped));
        if (dispatched is not null)
            ledger.RecordTerminal(dispatched.ActivationId!.Value, SequenceTerminalState.Completed);
        Assert.True(ledger.IsTerminal);
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
