using System.Collections.Concurrent;
using VisualAgentWorkflow.AgentFramework;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class WorkflowLoopExecutionBudgetTests
{
    [Fact]
    public void Body_pass_budget_reserves_at_most_the_root_limit_concurrently()
    {
        var budget = new WorkflowLoopPassBudget(maximumBodyPasses: 17);
        var accepted = new ConcurrentBag<bool>();

        Parallel.For(0, 100, _ => accepted.Add(budget.TryReserveBodyPass()));

        Assert.Equal(17, accepted.Count(value => value));
    }

    [Fact]
    public void Evidence_budget_counts_loop_scopes_but_not_unscoped_run_evidence()
    {
        var budget = new WorkflowLoopExecutionBudget(maximumEvidenceEvents: 11);
        budget.RegisterLoopScope("loop/1");
        var accepted = new ConcurrentBag<bool>();

        Parallel.For(0, 100, _ => accepted.Add(budget.TryReserveEvidenceEvent("loop/1/child")));

        Assert.Equal(11, accepted.Count(value => value));
        Assert.True(budget.TryReserveEvidenceEvent(null));
    }
}
