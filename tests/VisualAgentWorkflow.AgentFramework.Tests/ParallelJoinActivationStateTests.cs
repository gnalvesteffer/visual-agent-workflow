using VisualAgentWorkflow.AgentFramework;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ParallelJoinActivationStateTests
{
    [Fact]
    public void DuplicateBeforeFinalArrivalPermanentlyRejectsTheActivation()
    {
        var state = new ParallelJoinActivationState(Guid.NewGuid(), Guid.NewGuid());
        state.AddInput("a", "accepted");

        var duplicate = Assert.Throws<InvalidOperationException>(() => state.AddInput("a", "duplicate"));
        Assert.Contains("duplicate branch", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var finalBranch = Assert.Throws<InvalidOperationException>(() => state.AddInput("b", "late"));
        Assert.Contains("already failed", finalBranch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, state.InputCount);
        Assert.Equal(new[] { "a" }, state.InputSnapshot().Keys);
    }

}
