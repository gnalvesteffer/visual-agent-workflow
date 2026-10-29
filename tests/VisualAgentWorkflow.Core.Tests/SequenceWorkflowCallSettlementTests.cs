using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceWorkflowCallSettlementTests
{
    [Fact]
    public void Settles_a_child_once_and_ignores_late_outcomes()
    {
        var identity = new SequenceWorkflowCallIdentity("call", "call-node", "child", Guid.NewGuid(), "hash");
        var settlement = new SequenceWorkflowCallSettlement(identity);

        Assert.Equal(SequenceWorkflowCallSettlementState.Declared, settlement.State);
        Assert.True(settlement.TryStart());
        Assert.True(settlement.TrySettleChild(SequenceWorkflowCallSettlementState.Completed));
        Assert.False(settlement.TrySettleChild(SequenceWorkflowCallSettlementState.Failed));
        Assert.True(settlement.TrySettleOutput());
        Assert.False(settlement.TrySettleOutput());
        Assert.Equal(SequenceWorkflowCallSettlementState.OutputSettled, settlement.State);
    }

    [Fact]
    public void Retains_unknown_child_disposition_after_output_settlement()
    {
        var settlement = new SequenceWorkflowCallSettlement(new(
            "call", "call-node", "child", Guid.NewGuid(), "hash"));

        Assert.True(settlement.TryStart());
        Assert.True(settlement.TrySettleChild(SequenceWorkflowCallSettlementState.Unknown));
        Assert.True(settlement.TrySettleOutput());
        Assert.Equal(SequenceWorkflowCallSettlementState.Unknown, settlement.ChildOutcome);
        Assert.False(settlement.TrySettleChild(SequenceWorkflowCallSettlementState.Completed));
    }

    [Fact]
    public void Requires_a_complete_pinned_child_identity()
    {
        Assert.Throws<ArgumentException>(() => new SequenceWorkflowCallSettlement(
            new SequenceWorkflowCallIdentity("call", "node", "", Guid.NewGuid(), "hash")));
    }
}
