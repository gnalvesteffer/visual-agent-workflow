using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// A Loop pass frame completes, and its successor is admitted, only when work admitted in that frame has settled
/// (Decision 0070: the frame owner and the admission owner are linked).
/// </summary>
public sealed class LoopFrameSettlementTests
{
    private static WorkflowDefinition AgentLoop() => new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "frame-settlement-loop",
        "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["latest-result"] = new(new("agent", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary())
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = new(new("input", "then"), new("loop", "execute"), "execution"),
            ["body"] = new(new("loop", "body"), new("body", "execute"), "execution"),
            ["process"] = new(new("body", "then"), new("agent", "execute"), "execution"),
            ["return"] = new(new("agent", "out"), new("loop", "execute"), "execution"),
            ["complete"] = new(new("loop", "done"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary()
    };

    private sealed class AppendAgent : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default) => Task.FromResult(input + "!");
    }

    private static async Task<(WorkflowExecutionResult Result, List<ExecutionEvent> Events)> RunAsync(
        Action<WorkflowDispatchAdmissionOwner, string>? onPassStarted)
    {
        var revision = WorkflowRevision.Create(AgentLoop(), "loop-frame-settlement");
        WorkflowDispatchAdmissionOwner? owner = null;
        var events = new List<ExecutionEvent>();
        var injected = false;
        var result = await new WorkflowRunner(agents: new AppendAgent())
            .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "continue", _ => { }, evt =>
            {
                lock (events) events.Add(evt);
                if (!injected && onPassStarted is not null && evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
                    evt.ScopeId is { } scope)
                {
                    injected = true;
                    onPassStarted(owner!, scope);
                }
                return ValueTask.CompletedTask;
            }, ownerProbe: created => owner = created).WaitAsync(TimeSpan.FromSeconds(10));
        return (result, events);
    }

    [Fact]
    public async Task A_loop_whose_passes_settle_their_own_work_completes_every_iteration()
    {
        var (result, events) = await RunAsync(null);

        Assert.False(result.Failed);
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
    }

    [Fact]
    public async Task A_pass_with_unsettled_admitted_work_cannot_complete_and_the_next_pass_is_not_admitted()
    {
        var events = new List<ExecutionEvent>();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var revision = WorkflowRevision.Create(AgentLoop(), "loop-frame-unsettled");
            WorkflowDispatchAdmissionOwner? owner = null;
            var injected = false;
            await new WorkflowRunner(agents: new AppendAgent())
                .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "continue", _ => { }, evt =>
                {
                    lock (events) events.Add(evt);
                    if (!injected && evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.ScopeId is { } scope)
                    {
                        injected = true;
                        // Work admitted in this pass's frame that never starts or settles.
                        owner!.Admit(["ghost-work"], scope);
                    }
                    return ValueTask.CompletedTask;
                }, ownerProbe: created => owner = created).WaitAsync(TimeSpan.FromSeconds(10));
        });

        Assert.Contains("ghost-work", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("cannot complete with unsettled admitted work", failure.ToString(), StringComparison.Ordinal);
        lock (events)
        {
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
            Assert.Single(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed);
        }
    }
}
